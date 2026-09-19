using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry;
using Npgsql;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Api.Application.Services;

/// <summary>
/// Handles synchronous, concurrency-safe inter-wallet transfers (README §2 workflow
/// diagram "INTER-WALLET TRANSFER FLOW"). Unlike inbound NIP deposits, a transfer is
/// fully settled within the request/response cycle - there is no outbox consumer on
/// the debit side, only a <see cref="TransferOutbox"/> row for downstream notification.
/// </summary>
public sealed class TransferService : ITransferService
{
    // Idempotency is decided by the DB alone: WalletTransfers.IdempotencyKey is unique, and every
    // request looks it up before doing any business work. The earlier best-effort Redis
    // "processing" claim never changed that outcome (a lost or unavailable claim only fell
    // through to this same lookup), so it was removed rather than left as a wasted round trip.

    private readonly ILogger<TransferService> _logger;
    private readonly NovaWalletDbContext _dbContext;
    private readonly IRequestContext _requestContext;
    private readonly NovaWalletMetrics _metrics;

    public TransferService(
        ILogger<TransferService> logger,
        NovaWalletDbContext dbContext,
        IRequestContext requestContext,
        NovaWalletMetrics metrics)
    {
        _logger = logger;
        _dbContext = dbContext;
        _requestContext = requestContext;
        _metrics = metrics;
    }

    /// <summary>
    /// Looks up the stored outcome (Completed or Failed) of a transfer by Idempotency-Key.
    /// Scoped to the caller: a key whose source wallet is not the caller's is reported as not
    /// found, so one user can never learn another user's transfer outcome.
    /// </summary>
    public async Task<ServiceApiResponse<WalletTransferStatusResponse>> RequeryTransfer(string idempotencyKey, CancellationToken cancellationToken)
    {
        var callerUserId = _requestContext.UserId;

        if (callerUserId is null)
        {
            return ServiceApiResponse<WalletTransferStatusResponse>.CreateFailure(ResponseCodes.AccessDenied);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            return ServiceApiResponse<WalletTransferStatusResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "Idempotency key must be between 1 and 128 characters.");
        }

        var transfer = await _dbContext.WalletTransfers
            .AsNoTracking()
            .Where(t => t.IdempotencyKey == idempotencyKey && t.SourceWallet!.UserId == callerUserId.Value)
            .FirstOrDefaultAsync(cancellationToken);

        if (transfer is null)
        {
            return ServiceApiResponse<WalletTransferStatusResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
        }

        var isFailed = transfer.Status == TransferStatus.Failed;

        return ServiceApiResponse<WalletTransferStatusResponse>.CreateSuccess(new WalletTransferStatusResponse
        {
            IdempotencyKey = transfer.IdempotencyKey,
            Status = transfer.Status.ToString(),
            ResponseCode = isFailed
                ? transfer.FailureCode ?? ResponseCodes.Failed.ResponseCode
                : ResponseCodes.Success.ResponseCode,
            FailureReason = isFailed ? transfer.FailureReason : null,
            PaymentReference = transfer.PaymentReference,
            SourceWalletId = transfer.SourceWalletId.ToString(),
            DestinationWalletId = transfer.DestinationWalletId.ToString(),
            AmountInKobo = transfer.AmountKobo,
            Narration = transfer.Narration,
            TransactionDate = transfer.TransactionDate
        });
    }

    /// <summary>
    /// Thin, metrics-instrumented wrapper around <see cref="TransferCoreAsync"/> - times the
    /// whole call and records <c>novawallet.transfer.requests</c>/<c>.duration</c> (tagged by
    /// the resulting <see cref="IServiceApiResponse.ResponseCode"/>) plus
    /// <c>novawallet.transfer.amount</c> on success, without touching the money-movement logic
    /// itself.
    /// </summary>
    public async Task<ServiceApiResponse<WalletTransferResponse>> Transfer(WalletTransferRequest request, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        var response = await TransferCoreAsync(request, cancellationToken);

        _metrics.RecordTransfer(response.ResponseCode, Stopwatch.GetElapsedTime(startTimestamp));

        if (response.ResponseCode == ResponseCodes.Success.ResponseCode && response.Data is not null)
        {
            _metrics.RecordTransferAmount(response.Data.AmountInKobo);
        }

        return response;
    }

    private async Task<ServiceApiResponse<WalletTransferResponse>> TransferCoreAsync(WalletTransferRequest request, CancellationToken cancellationToken)
    {
        // ---- Authentication ----
        var callerUserId = _requestContext.UserId;

        if (callerUserId is null)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.AccessDenied);
        }

        // ---- Idempotency-Key header is mandatory for this endpoint (README §5) ----
        var idempotencyKey = _requestContext.RetrieveIdempotencyKey;

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "Idempotency-Key header is required.");
        }

        if (idempotencyKey.Length > 128)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "Idempotency-Key must not exceed 128 characters.");
        }

        // ---- Structural / scope validation (README §7), ahead of any DB work ----
        // The source wallet is never client-supplied; it is resolved from the session below.
        // (A stale sourceWalletId in the body is rejected by WalletTransferRequestValidator.)
        if (!Guid.TryParse(request.DestinationWalletId, out var destinationWalletId))
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "DestinationWalletId must be a valid identifier.");
        }

        if (request.AmountInKobo <= 0)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "AmountInKobo must be strictly positive.");
        }

        try
        {
            return await ProcessTransferAsync(
                request,
                destinationWalletId,
                callerUserId.Value,
                idempotencyKey,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Transfer processing failed unexpectedly. IdempotencyKey: {IdempotencyKey}, CallerUserId: {CallerUserId}, DestinationWalletId: {DestinationWalletId}",
                idempotencyKey,
                callerUserId,
                destinationWalletId);

            return ServiceApiResponse<WalletTransferResponse>.SystemMalFunctioned();
        }
    }

    private async Task<ServiceApiResponse<WalletTransferResponse>> ProcessTransferAsync(
        WalletTransferRequest request,
        Guid destinationWalletId,
        Guid callerUserId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        // ---- Resolve the source wallet from the session, plus the destination ----
        var wallets = await _dbContext.Wallets
            .AsNoTracking()
            .Where(w => w.UserId == callerUserId || w.Id == destinationWalletId)
            .Select(w => new { w.Id, w.UserId, w.Status, w.Currency, w.AccountId, AccountNumber = w.Account!.AccountNumber })
            .ToListAsync(cancellationToken);

        var sourceWallet = wallets.FirstOrDefault(w => w.UserId == callerUserId);

        if (sourceWallet is null)
        {
            // The caller has no wallet to send from.
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
        }

        var sourceWalletId = sourceWallet.Id;

        if (sourceWalletId == destinationWalletId)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "The destination wallet must differ from your own wallet.");
        }

        // The hash covers the resolved source wallet, so the same key sent by a different user
        // can never be mistaken for a replay of this one.
        var requestPayloadHash = ComputeRequestPayloadHash(request, sourceWalletId, destinationWalletId);

        // ---- Idempotency (README §5): the DB is the only authority ----
        // Runs for every request, ahead of any business check. Same key + same payload replays
        // the stored outcome (success or failure); same key + different payload is rejected.
        var existing = await FindByIdempotencyKey(idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            return ResolveExisting(existing, requestPayloadHash, idempotencyKey);
        }

        var destinationWallet = wallets.FirstOrDefault(w => w.Id == destinationWalletId);

        if (destinationWallet is null)
        {
            // Not recorded: a missing wallet cannot be stored (WalletTransfers has FKs to Wallets).
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
        }

        // From here both wallets exist, so business rejections are recorded as Failed rows.
        if (sourceWallet.Currency != destinationWallet.Currency ||
            sourceWallet.Currency != NovaWalletConstants.CurrencyCode)
        {
            var currencyFailure = ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "Cross-currency transfers are not supported.");

            await RecordFailedTransferAsync(
                sourceWalletId, destinationWalletId, request, idempotencyKey, requestPayloadHash, currencyFailure);

            return currencyFailure;
        }

        // ---- Transactional write - db transaction wrapped in an execution strategy ----
        var strategy = _dbContext.Database.CreateExecutionStrategy();

        // Cleared when the lambda returns a replay/conflict resolved from the DB, which must not
        // be recorded again.
        var recordFailure = true;

        var result = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // Fast preliminary check using the pre-transaction, unlocked snapshot taken
                // above - not authoritative under concurrency, just fails fast so an obviously
                // frozen/closed wallet doesn't waste a write on the daily-usage UPSERT below.
                // The atomic, guarded UPDATEs further down are what actually enforce this
                // (README §7 - destination status is checked the same as source, since a
                // frozen/closed wallet rejects inbound credits the same way it rejects
                // outbound debits).
                if (sourceWallet.Status != WalletStatus.Active)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                        ResponseCodes.RequestNotAllowed.ResponseCode,
                        $"Source wallet is {sourceWallet.Status} and cannot originate transfers.");
                }

                if (destinationWallet.Status != WalletStatus.Active)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                        ResponseCodes.RequestNotAllowed.ResponseCode,
                        $"Destination wallet is {destinationWallet.Status} and cannot receive transfers.");
                }

                // Atomic, guarded daily-usage UPSERT (README §6). The INSERT branch is
                // guarded exactly like the UPDATE branch, so a wallet's first transfer of
                // the day is checked too, not just subsequent ones. UsageDate is anchored
                // to WAT explicitly rather than following the DB session's timezone default.
                var dailyUsageRowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""WalletDailyUsage"" (""WalletId"", ""UsageDate"", ""TotalSpentKobo"")
                    SELECT {sourceWalletId}, (CURRENT_TIMESTAMP AT TIME ZONE 'Africa/Lagos')::DATE, {request.AmountInKobo}
                    WHERE {request.AmountInKobo} <= {NovaWalletConstants.TransferLimits.DailyOutboundLimitKobo}
                    ON CONFLICT (""WalletId"", ""UsageDate"")
                    DO UPDATE SET
                        ""TotalSpentKobo"" = ""WalletDailyUsage"".""TotalSpentKobo"" + EXCLUDED.""TotalSpentKobo""
                    WHERE ""WalletDailyUsage"".""TotalSpentKobo"" + EXCLUDED.""TotalSpentKobo"" <= {NovaWalletConstants.TransferLimits.DailyOutboundLimitKobo}",
                    cancellationToken);

                if (dailyUsageRowsAffected == 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    _logger.LogWarning(
                        "Transfer rejected - daily outbound limit exceeded for Wallet {SourceWalletId}. AmountKobo: {AmountKobo}",
                        sourceWalletId,
                        request.AmountInKobo);

                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.DailyLimitExceeded);
                }

                // Atomic, guarded wallet mutations - no application-held row lock is taken for
                // either wallet (README §4's documented intent for hot balance mutations,
                // applied to both sides of a transfer here). Each UPDATE's WHERE clause
                // enforces Active status - and, for the debit, sufficient balance too - as part
                // of the same atomic operation as the mutation itself, so there is no window in
                // which a concurrent transaction could observe or act on a stale balance. The
                // DB's own CHK_Wallet_AvailableBalanceKobo_NonNegative constraint remains the
                // absolute, non-bypassable backstop against a negative balance (see the
                // check-violation catch below) regardless of this guard's correctness.
                //
                // Deadlock prevention: the two UPDATEs still run in ascending-Guid order
                // (README §4) even though there is no explicit FOR UPDATE anymore - a plain
                // UPDATE still takes an implicit row lock for the rest of the transaction, so
                // two transfers moving funds in opposite directions between the same wallet
                // pair could otherwise deadlock against each other.
                var sourceRunsFirst = sourceWalletId.CompareTo(destinationWalletId) <= 0;

                long? sourceNewBalanceKobo;
                long? destinationNewBalanceKobo;

                if (sourceRunsFirst)
                {
                    sourceNewBalanceKobo = await TryDebitWalletAtomicAsync(sourceWalletId, request.AmountInKobo, cancellationToken);

                    if (sourceNewBalanceKobo is null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return await ResolveDebitFailureAsync(sourceWalletId, cancellationToken);
                    }

                    destinationNewBalanceKobo = await TryCreditWalletAtomicAsync(destinationWalletId, request.AmountInKobo, cancellationToken);

                    if (destinationNewBalanceKobo is null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return await ResolveCreditFailureAsync(destinationWalletId, cancellationToken);
                    }
                }
                else
                {
                    destinationNewBalanceKobo = await TryCreditWalletAtomicAsync(destinationWalletId, request.AmountInKobo, cancellationToken);

                    if (destinationNewBalanceKobo is null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return await ResolveCreditFailureAsync(destinationWalletId, cancellationToken);
                    }

                    sourceNewBalanceKobo = await TryDebitWalletAtomicAsync(sourceWalletId, request.AmountInKobo, cancellationToken);

                    if (sourceNewBalanceKobo is null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return await ResolveDebitFailureAsync(sourceWalletId, cancellationToken);
                    }
                }

                var sourceBalanceBeforeKobo = sourceNewBalanceKobo.Value + request.AmountInKobo;
                var sourceBalanceAfterKobo = sourceNewBalanceKobo.Value;
                var destinationBalanceBeforeKobo = destinationNewBalanceKobo.Value - request.AmountInKobo;
                var destinationBalanceAfterKobo = destinationNewBalanceKobo.Value;

                var journalEntry = JournalEntry.Create(idempotencyKey, requestPayloadHash);
                journalEntry.AddDebitLine(
                    sourceWallet.AccountId,
                    request.AmountInKobo,
                    ComposeParticulars($"Transfer to {destinationWallet.AccountNumber}", request.Narration));
                journalEntry.AddCreditLine(
                    destinationWallet.AccountId,
                    request.AmountInKobo,
                    ComposeParticulars($"Transfer from {sourceWallet.AccountNumber}", request.Narration));

                if (!journalEntry.IsBalanced)
                {
                    throw new InvalidOperationException(
                        $"Transfer journal entry for IdempotencyKey {idempotencyKey} is not balanced.");
                }

                var transferOutbox = TransferOutbox.Create(journalEntry.Id);
                var actorSubject = callerUserId.ToString();

                // Product-domain record of this transfer, distinct from the ledger's
                // JournalEntry/AccountEntry pair - see WalletTransfer's doc comment.
                // PaymentReference is generated internally by WalletTransfer.Create as its
                // own independent UUID v7, decoupled from JournalEntryId (ToResponse below
                // surfaces it to callers).
                var walletTransfer = WalletTransfer.Create(
                    journalEntryId: journalEntry.Id,
                    sourceWalletId: sourceWalletId,
                    destinationWalletId: destinationWalletId,
                    amountKobo: request.AmountInKobo,
                    narration: request.Narration,
                    idempotencyKey: idempotencyKey,
                    requestPayloadHash: requestPayloadHash,
                    transactionDate: journalEntry.CreatedAt);

                _dbContext.JournalEntries.Add(journalEntry);
                _dbContext.TransferOutboxEntries.Add(transferOutbox);
                _dbContext.WalletTransfers.Add(walletTransfer);
                _dbContext.AuditLogs.Add(AuditLog.Create(
                    walletId: sourceWalletId,
                    actorSubject: actorSubject,
                    action: "Debit",
                    balanceBeforeKobo: sourceBalanceBeforeKobo,
                    balanceAfterKobo: sourceBalanceAfterKobo,
                    correlationId: journalEntry.Id));
                _dbContext.AuditLogs.Add(AuditLog.Create(
                    walletId: destinationWalletId,
                    actorSubject: actorSubject,
                    action: "Credit",
                    balanceBeforeKobo: destinationBalanceBeforeKobo,
                    balanceAfterKobo: destinationBalanceAfterKobo,
                    correlationId: journalEntry.Id));

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Transfer settled. IdempotencyKey: {IdempotencyKey}, SourceWalletId: {SourceWalletId}, DestinationWalletId: {DestinationWalletId}, AmountKobo: {AmountKobo}, JournalEntryId: {JournalEntryId}",
                    idempotencyKey,
                    sourceWalletId,
                    destinationWalletId,
                    request.AmountInKobo,
                    journalEntry.Id);

                return ServiceApiResponse<WalletTransferResponse>.CreateSuccess(ToResponse(walletTransfer));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                await transaction.RollbackAsync(CancellationToken.None);

                // Lost a race against a concurrent request carrying the same
                // Idempotency-Key. Re-resolve authoritatively via the DB - the unique
                // constraints on WalletTransfers/JournalEntries.IdempotencyKey are what
                // actually prevent two concurrent requests from both winning the insert race
                // (README §5 step 4); this is the app-level reconciliation of that outcome.
                // Whatever the winner stored is returned as-is, so it is never re-recorded.
                recordFailure = false;

                var raceWinner = await FindByIdempotencyKey(idempotencyKey, cancellationToken);

                if (raceWinner is not null)
                {
                    _logger.LogInformation(
                        ex,
                        "Transfer lost a unique constraint race for IdempotencyKey {IdempotencyKey}; resolving against the winner.",
                        idempotencyKey);

                    return ResolveExisting(raceWinner, requestPayloadHash, idempotencyKey);
                }

                _logger.LogError(
                    ex,
                    "Transfer lost a unique constraint race but no WalletTransfer was found for IdempotencyKey {IdempotencyKey}.",
                    idempotencyKey);

                return ServiceApiResponse<WalletTransferResponse>.SystemMalFunctioned();
            }
            catch (Exception ex) when (IsCheckViolation(ex))
            {
                // Defense-in-depth: the WHERE-clause guards in TryDebitWalletAtomicAsync/
                // TryCreditWalletAtomicAsync should make this unreachable, but the DB CHECK
                // constraint (CHK_Wallet_AvailableBalanceKobo_NonNegative) is the
                // non-negotiable backstop that can never be bypassed regardless of
                // application-code correctness. If it ever fires, report it as
                // InsufficientBalance rather than an opaque SystemMalFunctioned.
                await transaction.RollbackAsync(CancellationToken.None);

                _logger.LogError(
                    ex,
                    "Wallet balance mutation tripped the DB-level non-negative-balance CHECK constraint despite the WHERE guard. Investigate the guard logic. SourceWalletId: {SourceWalletId}, DestinationWalletId: {DestinationWalletId}",
                    sourceWalletId,
                    destinationWalletId);

                return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.InsufficientBalance);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                _logger.LogError(
                    ex,
                    "Failed to settle transfer. IdempotencyKey: {IdempotencyKey}, SourceWalletId: {SourceWalletId}, DestinationWalletId: {DestinationWalletId}",
                    idempotencyKey,
                    sourceWalletId,
                    destinationWalletId);

                return ServiceApiResponse<WalletTransferResponse>.SystemMalFunctioned();
            }
        });

        // Recorded only now, after the transaction has been rolled back - saving inside it
        // would let the rollback erase the row.
        if (recordFailure && IsRecordableFailure(result.ResponseCode))
        {
            await RecordFailedTransferAsync(
                sourceWalletId, destinationWalletId, request, idempotencyKey, requestPayloadHash, result);
        }

        return result;
    }

    /// <summary>
    /// Business rejections that are final for a given Idempotency-Key and therefore stored:
    /// insufficient balance (51), daily limit (61) and a non-Active wallet (57). Everything else
    /// is not stored - 96 is transient and must stay retryable under the same key, and 26/00 are
    /// replay/success outcomes.
    /// </summary>
    private static bool IsRecordableFailure(string? responseCode)
    {
        return responseCode == NipResponseCodes.NoSufficientFunds
            || responseCode == NipResponseCodes.TransferLimitExceeded
            || responseCode == NipResponseCodes.TransactionNotPermitted;
    }

    /// <summary>
    /// Stores a rejected transfer as a Failed WalletTransfer so it can be inspected in the table
    /// and requeried by key. Best-effort: it never changes the response returned to the caller.
    /// A unique violation means a concurrent request already stored this key, which is fine.
    /// </summary>
    private async Task RecordFailedTransferAsync(
        Guid sourceWalletId,
        Guid destinationWalletId,
        WalletTransferRequest request,
        string idempotencyKey,
        string requestPayloadHash,
        ServiceApiResponse<WalletTransferResponse> failure)
    {
        try
        {
            // Drop anything left tracked by the rolled-back attempt.
            _dbContext.ChangeTracker.Clear();

            _dbContext.WalletTransfers.Add(WalletTransfer.CreateFailed(
                sourceWalletId: sourceWalletId,
                destinationWalletId: destinationWalletId,
                amountKobo: request.AmountInKobo,
                narration: request.Narration,
                idempotencyKey: idempotencyKey,
                requestPayloadHash: requestPayloadHash,
                failureCode: failure.ResponseCode,
                failureReason: failure.ResponseMessage ?? string.Empty));

            await _dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _logger.LogInformation(
                "Failed transfer for IdempotencyKey {IdempotencyKey} was already recorded by a concurrent request.",
                idempotencyKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not record failed transfer for IdempotencyKey {IdempotencyKey}. The response is unaffected.",
                idempotencyKey);
        }
    }

    /// <summary>
    /// Applies the idempotency rule to a stored transfer: same payload replays the stored
    /// outcome (success data, or the stored failure code and reason); a different payload is
    /// rejected as a conflict.
    /// </summary>
    private ServiceApiResponse<WalletTransferResponse> ResolveExisting(
        WalletTransfer existing, string requestPayloadHash, string idempotencyKey)
    {
        if (!string.Equals(existing.RequestPayloadHash, requestPayloadHash, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Transfer rejected - Idempotency-Key {IdempotencyKey} reused with a different request payload.",
                idempotencyKey);

            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.Conflict.ResponseCode,
                "Idempotency-Key has already been used with a different request payload.");
        }

        _logger.LogInformation(
            "Transfer replay detected for IdempotencyKey {IdempotencyKey}. Returning original result.",
            idempotencyKey);

        if (existing.Status == TransferStatus.Failed)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                existing.FailureCode ?? ResponseCodes.Failed.ResponseCode,
                existing.FailureReason);
        }

        return ServiceApiResponse<WalletTransferResponse>.CreateSuccess(ToResponse(existing));
    }

    /// <summary>
    /// Debits a wallet via a single atomic, guarded SQL UPDATE - no row lock is taken
    /// ahead of time by the application. The WHERE clause enforces both Active status and
    /// sufficient balance as part of the same atomic operation as the mutation itself.
    /// Returns null if zero rows were affected (status/balance guard failed, or the wallet
    /// no longer exists); the caller distinguishes which via <see cref="ResolveDebitFailureAsync"/>.
    /// </summary>
    private async Task<long?> TryDebitWalletAtomicAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken)
    {
        var affectedBalances = await _dbContext.Database.SqlQuery<long>($@"
            UPDATE ""Wallets""
            SET ""AvailableBalanceKobo"" = ""AvailableBalanceKobo"" - {amountKobo}
            WHERE ""Id"" = {walletId} AND ""Status"" = {(int)WalletStatus.Active} AND ""AvailableBalanceKobo"" >= {amountKobo}
            RETURNING ""AvailableBalanceKobo"" AS ""Value""")
            .ToListAsync(cancellationToken);

        return affectedBalances.Count > 0 ? affectedBalances[0] : null;
    }

    /// <summary>
    /// Credits a wallet via a single atomic, guarded SQL UPDATE - no row lock is taken
    /// ahead of time by the application. The WHERE clause enforces Active status as part
    /// of the same atomic operation as the mutation itself. Returns null if zero rows were
    /// affected (status guard failed, or the wallet no longer exists); the caller
    /// distinguishes which via <see cref="ResolveCreditFailureAsync"/>.
    /// </summary>
    private async Task<long?> TryCreditWalletAtomicAsync(Guid walletId, long amountKobo, CancellationToken cancellationToken)
    {
        var affectedBalances = await _dbContext.Database.SqlQuery<long>($@"
            UPDATE ""Wallets""
            SET ""AvailableBalanceKobo"" = ""AvailableBalanceKobo"" + {amountKobo}
            WHERE ""Id"" = {walletId} AND ""Status"" = {(int)WalletStatus.Active}
            RETURNING ""AvailableBalanceKobo"" AS ""Value""")
            .ToListAsync(cancellationToken);

        return affectedBalances.Count > 0 ? affectedBalances[0] : null;
    }

    /// <summary>
    /// Reads a wallet's current status for error-reporting purposes only. Not authoritative
    /// on its own - it never decides a failure, it only explains one that a guarded atomic
    /// UPDATE already reported (zero rows affected) by distinguishing "not Active" from
    /// "vanished/never existed".
    /// </summary>
    private async Task<WalletStatus?> ReadWalletStatusAsync(Guid walletId, CancellationToken cancellationToken)
    {
        return await _dbContext.Wallets
            .AsNoTracking()
            .Where(w => w.Id == walletId)
            .Select(w => (WalletStatus?)w.Status)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Explains a zero-row result from <see cref="TryDebitWalletAtomicAsync"/>: distinguishes
    /// "source wallet is no longer Active" from "insufficient balance" (status was Active on
    /// this immediate re-read, so the guard's balance condition must be what failed).
    /// </summary>
    private async Task<ServiceApiResponse<WalletTransferResponse>> ResolveDebitFailureAsync(Guid sourceWalletId, CancellationToken cancellationToken)
    {
        var status = await ReadWalletStatusAsync(sourceWalletId, cancellationToken);

        if (status != WalletStatus.Active)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.RequestNotAllowed.ResponseCode,
                $"Source wallet is {(status?.ToString() ?? "no longer found")} and cannot originate transfers.");
        }

        return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.InsufficientBalance);
    }

    /// <summary>
    /// Explains a zero-row result from <see cref="TryCreditWalletAtomicAsync"/>: the guard is
    /// Active-only (no balance condition), so a zero-row result with an Active status on this
    /// immediate re-read means the wallet vanished between the pre-transaction snapshot and
    /// this point - a race outside normal operation, logged and reported as a system failure.
    /// </summary>
    private async Task<ServiceApiResponse<WalletTransferResponse>> ResolveCreditFailureAsync(Guid destinationWalletId, CancellationToken cancellationToken)
    {
        var status = await ReadWalletStatusAsync(destinationWalletId, cancellationToken);

        if (status != WalletStatus.Active)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.RequestNotAllowed.ResponseCode,
                $"Destination wallet is {(status?.ToString() ?? "no longer found")} and cannot receive transfers.");
        }

        _logger.LogError(
            "Credit leg of a transfer affected zero rows for Wallet {DestinationWalletId} despite an Active status on immediate re-read.",
            destinationWalletId);

        return ServiceApiResponse<WalletTransferResponse>.SystemMalFunctioned();
    }

    /// <summary>
    /// Looks up the stored transfer (Completed or Failed) for an Idempotency-Key. The unique
    /// index on WalletTransfers.IdempotencyKey guarantees at most one row.
    /// </summary>
    private Task<WalletTransfer?> FindByIdempotencyKey(string idempotencyKey, CancellationToken cancellationToken)
    {
        return _dbContext.WalletTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    /// <summary>
    /// Builds the API response directly from a persisted WalletTransfer - used both for
    /// a brand-new transfer's success response and for a DB-authoritative replay.
    /// </summary>
    private static WalletTransferResponse ToResponse(WalletTransfer transfer)
    {
        return new WalletTransferResponse
        {
            SourceWalletId = transfer.SourceWalletId.ToString(),
            DestinationWalletId = transfer.DestinationWalletId.ToString(),
            AmountInKobo = transfer.AmountKobo,
            Narration = transfer.Narration,
            TransactionDate = transfer.TransactionDate,
            PaymentReference = transfer.PaymentReference
        };
    }

    /// <summary>
    /// Composes an AccountEntry line's TransParticulars from a fixed prefix describing the
    /// counterparty side plus the caller-supplied Narration, if any. Truncation to 100 chars
    /// happens inside AccountEntry itself, not here.
    /// </summary>
    private static string ComposeParticulars(string prefix, string? narration)
    {
        return string.IsNullOrWhiteSpace(narration)
            ? prefix
            : $"{prefix} - {narration}";
    }

    private static string ComputeRequestPayloadHash(WalletTransferRequest request, Guid sourceWalletId, Guid destinationWalletId)
    {
        // SHA-256 of the sorted request body (README §5 step 1) - sorted by field name
        // so the hash is stable regardless of the caller's JSON property ordering. The source
        // is the session-resolved wallet, not a body field, and both ids use the canonical Guid
        // format so casing differences in the body do not change the hash. (Rows written before
        // this change hashed the raw strings, so a replay of one of those whose ids were not
        // lower-case is reported as a payload mismatch.)
        var canonical = string.Join('|', new[]
        {
            $"AmountInKobo={request.AmountInKobo}",
            $"DestinationWalletId={destinationWalletId}",
            $"Narration={request.Narration}",
            $"SourceWalletId={sourceWalletId}"
        }.OrderBy(x => x, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    /// <summary>
    /// True if the given exception is (or wraps) a Postgres CHECK-constraint violation - e.g.
    /// <c>CHK_Wallet_AvailableBalanceKobo_NonNegative</c> firing as the last-resort backstop
    /// against a negative balance, should the guarded atomic UPDATE's WHERE clause ever fail
    /// to catch it first. Unlike <see cref="IsUniqueViolation"/>, this isn't limited to
    /// <see cref="DbUpdateException"/> since <c>Database.SqlQuery</c> raw-SQL calls (used by
    /// <see cref="TryDebitWalletAtomicAsync"/>/<see cref="TryCreditWalletAtomicAsync"/>) surface
    /// Postgres errors directly rather than wrapped in a SaveChanges-specific exception type.
    /// </summary>
    private static bool IsCheckViolation(Exception ex)
    {
        return ex is PostgresException { SqlState: PostgresErrorCodes.CheckViolation }
            || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation };
    }
}
