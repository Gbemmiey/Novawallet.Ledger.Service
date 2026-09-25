using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaWallet.Application.Configuration;
using NovaWallet.Application.Observability;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Enums;
using NovaWallet.Infrastructure.Data;
using NovaWallet.Infrastructure.Options;
using Npgsql;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Infrastructure.Workers
{
    /// <summary>
    /// Polling background worker that settles pending <c>DepositOutbox</c> rows written by
    /// <see cref="NovaWallet.Application.Services.DepositService.SubmitDepositRequest"/>.
    /// </summary>
    /// <remarks>
    /// This is the consumer half of the inbound NIP deposit flow described in README §2/§8:
    /// the webhook durably records an <see cref="ExternalCreditRequest"/> + <see cref="DepositOutbox"/>
    /// row and returns HTTP 202 immediately; this worker later atomically credits
    /// <see cref="Wallet.AvailableBalanceKobo"/> (no application-held wallet row lock - see the
    /// "Row locking" remarks below), posts the double-entry pair
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
    /// <b>Row locking:</b> the <c>DepositOutbox</c> row itself is still locked via a real
    /// <c>SELECT ... FOR UPDATE</c> - that's what makes it safe to run more than one instance of
    /// this worker (a second instance racing on the same row blocks, then sees it already
    /// <see cref="OutboxStatus.Processed"/> and no-ops). The beneficiary wallet's balance, however,
    /// is credited via a single atomic, guarded SQL UPDATE (see <c>TryCreditWalletAtomicAsync</c>)
    /// with no application-held row lock at all - the same "guarded UPDATE" pattern the hot
    /// outbound-transfer debit path uses (README §4), applied here too since there is no
    /// correctness reason to hold a row lock just to add to a balance. The wallet's own
    /// non-negative-balance CHECK constraint (irrelevant here since this path only credits, never
    /// debits) and its Active-status guard are both enforced atomically by the UPDATE's WHERE
    /// clause, so there is no window in which a concurrent writer could observe or act on a stale
    /// balance.
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
    /// without consuming a retry, since retrying it would never succeed. If the beneficiary
    /// wallet's atomic credit instead affects zero rows despite an Active status on immediate
    /// re-read (an anomaly, not a legitimate business rejection - the wallet vanished between
    /// the beneficiary lookup and the credit statement), that is treated as transient instead,
    /// so it gets retried rather than silently written off as permanently Failed.
    /// </para>
    /// </remarks>
    public sealed class DepositConsumer : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DepositConsumer> _logger;
        private readonly DepositConsumerOptions _options;
        private readonly NovaWalletMetrics _metrics;

        public DepositConsumer(
            IServiceScopeFactory scopeFactory,
            ILogger<DepositConsumer> logger,
            IOptions<DepositConsumerOptions> options,
            NovaWalletMetrics metrics)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options.Value;
            _metrics = metrics;
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
            // TraceParent is read here, alongside the Id, because the settlement span must be
            // started with its parent already known (an Activity's parent can't be changed after
            // it starts) - i.e. before the outbox row is locked and loaded inside the transaction.
            List<(Guid Id, string? TraceParent)> pendingOutboxes;

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

                var rows = await db.DepositOutboxEntries
                    .AsNoTracking()
                    .Where(o => o.Status == OutboxStatus.Pending)
                    .OrderBy(o => o.CreatedAt)
                    .Select(o => new { o.Id, o.TraceParent })
                    .Take(Math.Max(1, _options.BatchSize))
                    .ToListAsync(stoppingToken);

                pendingOutboxes = rows.Select(r => (r.Id, r.TraceParent)).ToList();
            }

            foreach (var (outboxId, traceParent) in pendingOutboxes)
            {
                stoppingToken.ThrowIfCancellationRequested();

                // Fresh scope (and DbContext) per row: keeps each settlement fully isolated
                // so one bad row can't leave stale tracked state for the next, and a failure
                // here never affects the rest of the batch.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

                try
                {
                    await ProcessOutboxEntryAsync(db, outboxId, traceParent, stoppingToken);
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

        private async Task ProcessOutboxEntryAsync(NovaWalletDbContext db, Guid outboxId, string? traceParent, CancellationToken cancellationToken)
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var startTimestamp = Stopwatch.GetTimestamp();

            // Continues the webhook's trace (see NovaWalletTracing): parented on the traceparent
            // stored with the outbox row, so accept -> settle is one trace. Held open across the
            // retry-bookkeeping call at the bottom so that shows up under this span too. Every
            // EF Core / Npgsql span below becomes its child automatically via Activity.Current.
            using var activity = NovaWalletTracing.StartConsumerActivity("deposit.settle", traceParent);
            activity?.SetTag("deposit.outbox_id", outboxId);

            // Set by whichever branch below actually returns/throws, so the single metrics
            // recording call after strategy.ExecuteAsync below can tag the settlement
            // counter/duration with a specific outcome rather than only the coarse
            // Handled/TransientFailure distinction the retry-bookkeeping caller needs.
            var outcomeLabel = "unknown";

            var outcome = await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    // Lock the outbox row first. If another instance of this worker is already
                    // settling the same row, this blocks until that transaction commits, then the
                    // re-check below sees Status == Processed and exits as a safe no-op.
                    DepositOutbox? outbox;

                    using (NovaWalletTracing.Source.StartActivity("deposit.lock_outbox"))
                    {
                        outbox = await db.DepositOutboxEntries
                            .FromSqlInterpolated($"SELECT * FROM \"DepositOutbox\" WHERE \"Id\" = {outboxId} FOR UPDATE")
                            .FirstOrDefaultAsync(cancellationToken);
                    }

                    if (outbox is null || outbox.Status != OutboxStatus.Pending)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        outcomeLabel = "skipped_not_pending";
                        return SettlementOutcome.Handled;
                    }

                    activity?.SetTag("deposit.retry_count", outbox.NumberOfRetries);

                    var externalCreditRequest = await db.ExternalCreditRequests
                        .FirstAsync(x => x.Id == outbox.ExternalCreditRequestId, cancellationToken);

                    activity?.SetTag("deposit.session_id", externalCreditRequest.SessionId);
                    activity?.SetTag("deposit.transaction_reference", externalCreditRequest.TransactionReference);
                    activity?.SetTag("deposit.amount_kobo", externalCreditRequest.AmountKobo);

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
                        outcomeLabel = "already_processed";
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
                        outcomeLabel = "rejected_beneficiary_not_found";
                        return SettlementOutcome.Handled;
                    }

                    activity?.SetTag("wallet.id", beneficiary.Id);

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

                    // Atomic, guarded credit - no application-held row lock (see class remarks).
                    // The WHERE clause enforces the wallet is still Active as part of the same
                    // atomic operation as the balance mutation; a null result means either the
                    // wallet is legitimately not Active (a real business rejection) or it
                    // vanished between the beneficiary lookup above and this statement (an
                    // anomaly, not a business outcome) - the status re-read just below tells
                    // these apart, the same way TransferService.ResolveCreditFailureAsync does.
                    long? newBalanceKobo;

                    using (var creditActivity = NovaWalletTracing.Source.StartActivity("deposit.credit_wallet"))
                    {
                        creditActivity?.SetTag("wallet.id", beneficiary.Id);
                        creditActivity?.SetTag("deposit.amount_kobo", externalCreditRequest.AmountKobo);

                        newBalanceKobo = await TryCreditWalletAtomicAsync(
                            db, beneficiary.Id, externalCreditRequest.AmountKobo, cancellationToken);

                        // Zero rows affected: wallet not Active (or vanished) - see handling below.
                        creditActivity?.SetTag("deposit.credit_applied", newBalanceKobo is not null);
                    }

                    if (newBalanceKobo is null)
                    {
                        var status = await ReadWalletStatusAsync(db, beneficiary.Id, cancellationToken);

                        if (status != WalletStatus.Active)
                        {
                            // Legitimate, permanent business rejection - retrying would never
                            // succeed, so mark both rows Failed immediately without consuming a
                            // retry attempt.
                            outbox.MarkFailed();
                            externalCreditRequest.MarkFailed();
                            await db.SaveChangesAsync(cancellationToken);
                            await transaction.CommitAsync(cancellationToken);

                            _logger.LogWarning(
                                "DepositOutbox {DepositOutboxId} failed permanently - beneficiary Wallet {WalletId} is {Status}, not Active. SessionId: {SessionId}",
                                outbox.Id,
                                beneficiary.Id,
                                status?.ToString() ?? "no longer found",
                                externalCreditRequest.SessionId);
                            outcomeLabel = "rejected_wallet_not_active";
                            return SettlementOutcome.Handled;
                        }

                        // Anomaly: the wallet is Active on this immediate re-read, yet the
                        // guarded UPDATE affected zero rows. Unlike a genuine business
                        // rejection, this should never happen in normal operation, so treat it
                        // as transient rather than silently writing the deposit off as
                        // permanently Failed - this throws, is caught by the generic handler
                        // below, rolls back, and routes through RecordFailedAttemptAsync so it
                        // gets retried (and only becomes permanently Failed after exhausting
                        // DepositOutbox.MaxRetries, same as any other transient failure).
                        throw new InvalidOperationException(
                            $"Credit leg of deposit settlement affected zero rows for Wallet {beneficiary.Id} despite an Active status on immediate re-read. SessionId: {externalCreditRequest.SessionId}");
                    }

                    var balanceBeforeKobo = newBalanceKobo.Value - externalCreditRequest.AmountKobo;
                    var balanceAfterKobo = newBalanceKobo.Value;

                    externalCreditRequest.MarkCompleted();
                    outbox.MarkProcessed();

                    db.JournalEntries.Add(journalEntry);
                    db.AuditLogs.Add(AuditLog.Create(
                        walletId: beneficiary.Id,
                        actorSubject: "system:nip-inbound",
                        action: "Credit",
                        balanceBeforeKobo: balanceBeforeKobo,
                        balanceAfterKobo: balanceAfterKobo,
                        correlationId: journalEntry.Id));

                    using (var postActivity = NovaWalletTracing.Source.StartActivity("deposit.post_journal"))
                    {
                        postActivity?.SetTag("journal_entry.id", journalEntry.Id);

                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }

                    _logger.LogInformation(
                        "Deposit settled. SessionId: {SessionId}, TransactionReference: {TransactionReference}, WalletId: {WalletId}, AmountKobo: {AmountKobo}, JournalEntryId: {JournalEntryId}",
                        externalCreditRequest.SessionId,
                        externalCreditRequest.TransactionReference,
                        beneficiary.Id,
                        externalCreditRequest.AmountKobo,
                        journalEntry.Id);

                    outcomeLabel = "settled";
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

                    outcomeLabel = "unique_violation_race";
                    return SettlementOutcome.Handled;
                }
                catch (Exception ex)
                {
                    // Atomicity: the whole settlement attempt rolls back explicitly rather than
                    // relying on implicit dispose-rollback (mirrors TransferService.ProcessTransferAsync).
                    await transaction.RollbackAsync(CancellationToken.None);

                    NovaWalletTracing.RecordException(activity, ex);

                    _logger.LogError(
                        ex,
                        "Failed to settle DepositOutbox {DepositOutboxId} due to a transient error. Recording a failed attempt.",
                        outboxId);

                    outcomeLabel = "transient_failure";
                    return SettlementOutcome.TransientFailure;
                }
            });

            _metrics.RecordDepositSettlement(outcomeLabel);
            _metrics.RecordDepositSettlementDuration(outcomeLabel, Stopwatch.GetElapsedTime(startTimestamp));

            // Same label the settlement metrics use, so a trace and its counter line up. Business
            // rejections are terminal-but-expected, so only genuine failures flag the span as Error.
            activity?.SetTag("deposit.outcome", outcomeLabel);

            if (outcomeLabel.StartsWith("rejected_", StringComparison.Ordinal))
            {
                activity?.SetStatus(ActivityStatusCode.Error, outcomeLabel);
            }

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

                    // Ambient activity here is the caller's deposit.settle span (this runs inside
                    // ProcessOutboxEntryAsync), so the retry bookkeeping is recorded on it as an
                    // event rather than a separate span.
                    Activity.Current?.AddEvent(new ActivityEvent("retry_recorded", tags: new ActivityTagsCollection
                    {
                        { "deposit.retry_count", outbox.NumberOfRetries }
                    }));
                    Activity.Current?.SetTag("deposit.retries_exhausted", exhausted);

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

                        _metrics.RecordDepositSettlement("retries_exhausted");
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
        /// Credits a wallet via a single atomic, guarded SQL UPDATE - no row lock is taken by
        /// the application. The WHERE clause enforces the wallet is still Active as part of the
        /// same atomic operation (mirrors the guarded UPSERT pattern used for the daily-limit
        /// check in <c>TransferService</c>, and the intent described in <c>Wallet.cs</c>'s own
        /// doc comment). Returns null if zero rows were affected (wallet no longer Active, or no
        /// longer exists) - the caller treats that as a permanent settlement failure.
        /// </summary>
        private static async Task<long?> TryCreditWalletAtomicAsync(
            NovaWalletDbContext db, Guid walletId, long amountKobo, CancellationToken cancellationToken)
        {
            var affectedBalances = await db.Database.SqlQuery<long>($@"
                UPDATE ""Wallets""
                SET ""AvailableBalanceKobo"" = ""AvailableBalanceKobo"" + {amountKobo}
                WHERE ""Id"" = {walletId} AND ""Status"" = {(int)WalletStatus.Active}
                RETURNING ""AvailableBalanceKobo"" AS ""Value""")
                .ToListAsync(cancellationToken);

            return affectedBalances.Count > 0 ? affectedBalances[0] : null;
        }

        /// <summary>
        /// Reads a wallet's current status for error-reporting purposes only. Not authoritative
        /// on its own - it never decides a failure, it only explains one that
        /// <see cref="TryCreditWalletAtomicAsync"/> already reported (zero rows affected) by
        /// distinguishing "not Active" (a real business rejection) from "vanished/never existed"
        /// (an anomaly). Mirrors <c>TransferService.ReadWalletStatusAsync</c>.
        /// </summary>
        private static async Task<WalletStatus?> ReadWalletStatusAsync(
            NovaWalletDbContext db, Guid walletId, CancellationToken cancellationToken)
        {
            return await db.Wallets
                .AsNoTracking()
                .Where(w => w.Id == walletId)
                .Select(w => (WalletStatus?)w.Status)
                .FirstOrDefaultAsync(cancellationToken);
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