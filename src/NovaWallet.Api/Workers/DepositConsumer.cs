using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Options;
using NovaWallet.Api.Infrastructure.Data;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Api.Workers
{
    /// <summary>
    /// Polling background worker that settles pending <c>DepositOutbox</c> rows written by
    /// <see cref="NovaWallet.Api.Application.Services.DepositService.SubmitDepositRequest"/>.
    /// </summary>
    /// <remarks>
    /// This is the consumer half of the inbound NIP deposit flow described in README §2/§8:
    /// the webhook durably records an <see cref="ExternalCreditRequest"/> + <see cref="DepositOutbox"/>
    /// row and returns HTTP 202 immediately; this worker later locks the beneficiary wallet row,
    /// credits <see cref="Wallet.AvailableBalanceKobo"/>, posts the double-entry pair
    /// (Debit <see cref="NovaWalletConstants.SystemAccounts.NipSettlementAccountNumber"/> Asset,
    /// Credit the beneficiary's own liability account), and marks both the outbox row and the
    /// external credit request as settled — all inside a single DB transaction.
    ///
    /// <para>
    /// <b>Delivery guarantee:</b> at-least-once delivery, exactly-once effect (README §8). A crash
    /// between commit and the next poll simply means the row is revisited; <c>ExternalCreditRequests.Status</c>
    /// and the <c>JournalEntries.IdempotencyKey</c> unique index (keyed off the NIP <c>SessionId</c>) make a
    /// reprocessed row a safe no-op rather than a duplicate credit.
    /// </para>
    /// <para>
    /// <b>Row locking:</b> unlike the hot outbound-transfer debit path (which deliberately avoids
    /// row locks for throughput — see README §4), this worker takes a real <c>SELECT ... FOR UPDATE</c>
    /// lock on both the outbox row and the beneficiary wallet row before mutating them. Inbound
    /// settlement volume is far lower than outbound transfer volume, so correctness/simplicity is
    /// prioritized here, and the lock is what makes this worker safe to run as more than one instance.
    /// </para>
    /// <para>
    /// <b>Failure semantics:</b> settlement runs inside an explicit try/catch around the whole
    /// transaction (mirroring <c>TransferService.ProcessTransferAsync</c>'s atomicity), so a
    /// transient/unexpected failure always rolls back cleanly rather than relying on implicit
    /// dispose-rollback. A transient failure then records an attempt via
    /// <see cref="DepositOutbox.RecordFailedAttempt"/> (in a fresh transaction, since the
    /// settlement transaction itself was rolled back) and leaves the row
    /// <see cref="OutboxStatus.Pending"/> for the next poll cycle to retry — up to
    /// <see cref="DepositOutbox.MaxRetries"/> times, after which the row is marked
    /// <see cref="OutboxStatus.Failed"/> permanently and the cascading failure is also applied to
    /// the linked <see cref="ExternalCreditRequest"/> (via <see cref="ExternalCreditRequest.MarkFailed"/>)
    /// so it's never left stuck at <see cref="DepositStatus.Pending"/> forever. A permanent business
    /// rejection (beneficiary account no longer resolvable, or beneficiary wallet not
    /// <see cref="WalletStatus.Active"/> — see README §7) marks both rows <c>Failed</c> immediately,
    /// without consuming a retry, since retrying it would never succeed.
    /// </para>
    /// </remarks>
    public sealed class DepositConsumer : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DepositConsumer> _logger;
        private readonly DepositConsumerOptions _options;

        public DepositConsumer(
            IServiceScopeFactory scopeFactory,
            ILogger<DepositConsumer> logger,
            IOptions<DepositConsumerOptions> options)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var pollingInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds));

            _logger.LogInformation(
                "DepositConsumer starting. PollingInterval: {PollingInterval}, BatchSize: {BatchSize}",
                pollingInterval,
                _options.BatchSize);

            using var timer = new PeriodicTimer(pollingInterval);

            do
            {
                try
                {
                    await ProcessPendingBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A batch-level failure (e.g. DB unreachable) must never take the whole
                    // hosted service - and with it, the API process - down. Log and retry on
                    // the next tick; individual rows stay Pending and are simply revisited.
                    _logger.LogError(ex, "DepositConsumer batch failed unexpectedly. Will retry next poll.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task ProcessPendingBatchAsync(CancellationToken stoppingToken)
        {
            List<Guid> pendingOutboxIds;

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

                pendingOutboxIds = await db.DepositOutboxEntries
                    .AsNoTracking()
                    .Where(o => o.Status == OutboxStatus.Pending)
                    .OrderBy(o => o.CreatedAt)
                    .Select(o => o.Id)
                    .Take(Math.Max(1, _options.BatchSize))
                    .ToListAsync(stoppingToken);
            }

            foreach (var outboxId in pendingOutboxIds)
            {
                stoppingToken.ThrowIfCancellationRequested();

                // Fresh scope (and DbContext) per row: keeps each settlement fully isolated
                // so one bad row can't leave stale tracked state for the next, and a failure
                // here never affects the rest of the batch.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

                try
                {
                    await ProcessOutboxEntryAsync(db, outboxId, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Left Pending on purpose - see class remarks on failure semantics.
                    _logger.LogError(ex, "Failed to settle DepositOutbox {DepositOutboxId}. Left Pending for retry.", outboxId);
                }
            }
        }

        /// <summary>Outcome of a single settlement attempt, reported back to the caller so it
        /// knows whether to invoke <see cref="RecordFailedAttemptAsync"/>.</summary>
        private enum SettlementOutcome
        {
            Handled,
            TransientFailure
        }

        private async Task ProcessOutboxEntryAsync(NovaWalletDbContext db, Guid outboxId, CancellationToken cancellationToken)
        {
            var strategy = db.Database.CreateExecutionStrategy();

            var outcome = await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    // Lock the outbox row first. If another instance of this worker is already
                    // settling the same row, this blocks until that transaction commits, then the
                    // re-check below sees Status == Processed and exits as a safe no-op.
                    var outbox = await db.DepositOutboxEntries
                        .FromSqlInterpolated($"SELECT * FROM \"DepositOutbox\" WHERE \"Id\" = {outboxId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken);

                    if (outbox is null || outbox.Status != OutboxStatus.Pending)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return SettlementOutcome.Handled;
                    }

                    var externalCreditRequest = await db.ExternalCreditRequests
                        .FirstAsync(x => x.Id == outbox.ExternalCreditRequestId, cancellationToken);

                    if (externalCreditRequest.Status == DepositStatus.Completed)
                    {
                        // Already settled by an earlier run; the outbox row just never got marked.
                        // Self-heal rather than re-post a duplicate journal entry.
                        outbox.MarkProcessed();
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);

                        _logger.LogInformation(
                            "DepositOutbox {DepositOutboxId} pointed at an already-completed ExternalCreditRequest {SessionId}. Marked Processed without reposting.",
                            outbox.Id,
                            externalCreditRequest.SessionId);
                        return SettlementOutcome.Handled;
                    }

                    var beneficiary = await db.Wallets
                        .AsNoTracking()
                        .Where(w => w.Account!.AccountNumber == externalCreditRequest.BeneficiaryAccountNumber
                            && w.Account.AccountType == AccountType.Liability
                            && w.Account.Currency == NovaWalletConstants.CurrencyCode)
                        .Select(w => new { w.Id, w.AccountId })
                        .FirstOrDefaultAsync(cancellationToken);

                    if (beneficiary is null)
                    {
                        outbox.MarkFailed();
                        externalCreditRequest.MarkFailed();
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);

                        _logger.LogError(
                            "DepositOutbox {DepositOutboxId} failed permanently - no liability account found for BeneficiaryAccountNumber {BeneficiaryAccountNumber}. SessionId: {SessionId}",
                            outbox.Id,
                            externalCreditRequest.BeneficiaryAccountNumber,
                            externalCreditRequest.SessionId);
                        return SettlementOutcome.Handled;
                    }

                    // Real row lock on the wallet being credited (see class remarks). Note this is a
                    // brand-new, previously-untracked query, so the materialized values are guaranteed
                    // fresh under the lock - not stale via EF's identity-map reuse of an earlier read.
                    var wallet = await db.Wallets
                        .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"Id\" = {beneficiary.Id} FOR UPDATE")
                        .FirstAsync(cancellationToken);

                    if (wallet.Status != WalletStatus.Active)
                    {
                        outbox.MarkFailed();
                        externalCreditRequest.MarkFailed();
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);

                        _logger.LogWarning(
                            "DepositOutbox {DepositOutboxId} failed permanently - beneficiary Wallet {WalletId} is {Status}, not Active. SessionId: {SessionId}",
                            outbox.Id,
                            wallet.Id,
                            wallet.Status,
                            externalCreditRequest.SessionId);
                        return SettlementOutcome.Handled;
                    }

                    var settlementAccountId = await EnsureSystemAccountAsync(
                        db, NovaWalletConstants.SystemAccounts.NipSettlementAccountNumber, AccountType.Asset, cancellationToken);

                    var journalEntry = JournalEntry.Create(
                        idempotencyKey: BuildDepositIdempotencyKey(externalCreditRequest.SessionId),
                        requestPayloadHash: ComputeRequestPayloadHash(externalCreditRequest));

                    journalEntry.AddDebitLine(
                        settlementAccountId,
                        externalCreditRequest.AmountKobo,
                        $"NIP settlement - SessionId {externalCreditRequest.SessionId}");
                    journalEntry.AddCreditLine(
                        beneficiary.AccountId,
                        externalCreditRequest.AmountKobo,
                        $"NIP credit from {externalCreditRequest.OriginatingAccountNumber} - Ref {externalCreditRequest.TransactionReference}");

                    if (!journalEntry.IsBalanced)
                        throw new InvalidOperationException($"Deposit journal entry for SessionId {externalCreditRequest.SessionId} is not balanced.");

                    var balanceBeforeKobo = wallet.AvailableBalanceKobo;
                    wallet.Credit(externalCreditRequest.AmountKobo);

                    externalCreditRequest.MarkCompleted();
                    outbox.MarkProcessed();

                    db.JournalEntries.Add(journalEntry);
                    db.AuditLogs.Add(AuditLog.Create(
                        walletId: wallet.Id,
                        actorSubject: "system:nip-inbound",
                        action: "Credit",
                        balanceBeforeKobo: balanceBeforeKobo,
                        balanceAfterKobo: wallet.AvailableBalanceKobo,
                        correlationId: journalEntry.Id));

                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation(
                        "Deposit settled. SessionId: {SessionId}, TransactionReference: {TransactionReference}, WalletId: {WalletId}, AmountKobo: {AmountKobo}, JournalEntryId: {JournalEntryId}",
                        externalCreditRequest.SessionId,
                        externalCreditRequest.TransactionReference,
                        wallet.Id,
                        externalCreditRequest.AmountKobo,
                        journalEntry.Id);

                    return SettlementOutcome.Handled;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // Should be unreachable: the FOR UPDATE lock on the outbox row above already
                    // serializes concurrent settlement attempts for this SessionId. Kept as a
                    // defensive backstop matching the same guarantee described in README §8.
                    await transaction.RollbackAsync(CancellationToken.None);

                    _logger.LogWarning(
                        ex,
                        "Deposit settlement lost a unique-constraint race despite row locking for DepositOutbox {DepositOutboxId}. Treating as already-handled.",
                        outboxId);

                    return SettlementOutcome.Handled;
                }
                catch (Exception ex)
                {
                    // Atomicity: the whole settlement attempt rolls back explicitly rather than
                    // relying on implicit dispose-rollback (mirrors TransferService.ProcessTransferAsync).
                    await transaction.RollbackAsync(CancellationToken.None);

                    _logger.LogError(
                        ex,
                        "Failed to settle DepositOutbox {DepositOutboxId} due to a transient error. Recording a failed attempt.",
                        outboxId);

                    return SettlementOutcome.TransientFailure;
                }
            });

            if (outcome == SettlementOutcome.TransientFailure)
            {
                await RecordFailedAttemptAsync(outboxId, cancellationToken);
            }
        }

        /// <summary>
        /// Records a transient-failure attempt against the given outbox row, opening a fresh
        /// scope/DbContext/transaction rather than reusing the (already rolled-back) one from the
        /// settlement attempt itself - so the retry count is durably persisted even though the
        /// failed attempt's own changes were not. If this exhausts <see cref="DepositOutbox.MaxRetries"/>,
        /// the row transitions to <see cref="OutboxStatus.Failed"/> (see <see cref="DepositOutbox.RecordFailedAttempt"/>)
        /// and the failure cascades to the linked <see cref="ExternalCreditRequest"/> too, in the
        /// same transaction, so it's never left stuck at <see cref="DepositStatus.Pending"/> forever.
        /// </summary>
        private async Task RecordFailedAttemptAsync(Guid outboxId, CancellationToken cancellationToken)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

            var strategy = db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var outbox = await db.DepositOutboxEntries
                        .FromSqlInterpolated($"SELECT * FROM \"DepositOutbox\" WHERE \"Id\" = {outboxId} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken);

                    if (outbox is null || outbox.Status != OutboxStatus.Pending)
                    {
                        // Already resolved (e.g. by another worker instance, or self-healed) since
                        // the transient failure occurred - nothing left to record.
                        await transaction.RollbackAsync(CancellationToken.None);
                        return;
                    }

                    var exhausted = outbox.RecordFailedAttempt();

                    if (exhausted)
                    {
                        var externalCreditRequest = await db.ExternalCreditRequests
                            .FirstAsync(x => x.Id == outbox.ExternalCreditRequestId, cancellationToken);

                        if (externalCreditRequest.Status == DepositStatus.Pending)
                        {
                            externalCreditRequest.MarkFailed();
                        }

                        _logger.LogError(
                            "DepositOutbox {DepositOutboxId} exhausted {MaxRetries} retries and is now permanently Failed. SessionId: {SessionId}",
                            outbox.Id,
                            DepositOutbox.MaxRetries,
                            externalCreditRequest.SessionId);
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    _logger.LogError(
                        ex,
                        "Failed to record a failed attempt for DepositOutbox {DepositOutboxId}. Row remains Pending with a stale retry count; will be revisited next poll.",
                        outboxId);
                }
            });
        }

        /// <summary>
        /// Resolves the singleton system account identified by <paramref name="accountNumber"/>,
        /// creating it on first use. Concurrent creation from another worker/instance is resolved
        /// via the unique index on <c>Accounts.AccountNumber</c> - the loser of the race simply
        /// re-reads the winner's row rather than failing.
        /// </summary>
        private static async Task<Guid> EnsureSystemAccountAsync(
            NovaWalletDbContext db, string accountNumber, AccountType accountType, CancellationToken cancellationToken)
        {
            var existing = await db.Accounts
                .AsNoTracking()
                .Where(a => a.AccountNumber == accountNumber)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != Guid.Empty)
                return existing;

            var account = Account.Create(accountNumber, accountType);
            db.Accounts.Add(account);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return account.Id;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                db.Entry(account).State = EntityState.Detached;

                return await db.Accounts
                    .AsNoTracking()
                    .Where(a => a.AccountNumber == accountNumber)
                    .Select(a => a.Id)
                    .FirstAsync(cancellationToken);
            }
        }

        private static string BuildDepositIdempotencyKey(string sessionId) => $"deposit:{sessionId}";

        private static string ComputeRequestPayloadHash(ExternalCreditRequest externalCreditRequest)
        {
            var payload = $"{externalCreditRequest.SessionId}|{externalCreditRequest.TransactionReference}|{externalCreditRequest.AmountKobo}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        }

        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            return ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
        }
    }
}
