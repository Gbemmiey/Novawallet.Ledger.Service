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
using StackExchange.Redis;
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
    // Best-effort Redis "processing" claim, matching README §5 step 2 literally.
    // Whether the claim succeeds, fails (contention), or Redis is simply unavailable
    // (e.g. local dev without REDIS_URI - see MissingRedisWarningHostedService), the
    // outcome never changes correctness: only a failed/unavailable claim routes to the
    // DB-authoritative IdempotencyKey lookup below, which is what actually decides
    // replay vs. conflict vs. genuinely-new. Redis is a coordination fast-path, never
    // the source of truth (README §1).
    private static readonly TimeSpan RedisClaimTtl = TimeSpan.FromSeconds(30);

    private readonly ILogger<TransferService> _logger;
    private readonly NovaWalletDbContext _dbContext;
    private readonly IRequestContext _requestContext;
    private readonly NovaWalletMetrics _metrics;
    private readonly IConnectionMultiplexer? _redisConnection;

    public TransferService(
        ILogger<TransferService> logger,
        NovaWalletDbContext dbContext,
        IRequestContext requestContext,
        NovaWalletMetrics metrics,
        IConnectionMultiplexer? redisConnection = null)
    {
        _logger = logger;
        _dbContext = dbContext;
        _requestContext = requestContext;
        _metrics = metrics;
        _redisConnection = redisConnection;
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
        if (!Guid.TryParse(request.SourceWalletId, out var sourceWalletId) ||
            !Guid.TryParse(request.DestinationWalletId, out var destinationWalletId))
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "SourceWalletId and DestinationWalletId must be valid identifiers.");
        }

        if (sourceWalletId == destinationWalletId)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "SourceWalletId and DestinationWalletId must differ.");
        }

        if (request.AmountInKobo <= 0)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "AmountInKobo must be strictly positive.");
        }

        var requestPayloadHash = ComputeRequestPayloadHash(request);

        try
        {
            return await ProcessTransferAsync(
                request,
                sourceWalletId,
                destinationWalletId,
                callerUserId.Value,
                idempotencyKey,
                requestPayloadHash,
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
                "Transfer processing failed unexpectedly. IdempotencyKey: {IdempotencyKey}, SourceWalletId: {SourceWalletId}, DestinationWalletId: {DestinationWalletId}",
                idempotencyKey,
                sourceWalletId,
                destinationWalletId);

            return ServiceApiResponse<WalletTransferResponse>.SystemMalFunctioned();
        }
    }

    private async Task<ServiceApiResponse<WalletTransferResponse>> ProcessTransferAsync(
        WalletTransferRequest request,
        Guid sourceWalletId,
        Guid destinationWalletId,
        Guid callerUserId,
        string idempotencyKey,
        string requestPayloadHash,
        CancellationToken cancellationToken)
    {
        // ---- Redis fast-path claim (README §5 step 2) ----
        var claimed = await TryClaimIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        if (!claimed)
        {
            // Claim contention or Redis unavailable: fall through to the DB-authoritative
            // lookup rather than assuming this is a duplicate (README §5 step 2).
            var existingLookup = await FindByIdempotencyKey(idempotencyKey, cancellationToken);

            if (existingLookup is { } existing)
            {
                if (existing.RequestPayloadHash == requestPayloadHash)
                {
                    _logger.LogInformation(
                        "Transfer replay detected for IdempotencyKey {IdempotencyKey}. Returning original result.",
                        idempotencyKey);

                    return ServiceApiResponse<WalletTransferResponse>.CreateSuccess(
                        ToResponse(request, existing));
                }

                _logger.LogWarning(
                    "Transfer rejected - Idempotency-Key {IdempotencyKey} reused with a different request payload.",
                    idempotencyKey);

                return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                    ResponseCodes.Conflict.ResponseCode,
                    "Idempotency-Key has already been used with a different request payload.");
            }
        }

        // ---- Business validation (README §7), ahead of opening a DB transaction ----
        var wallets = await _dbContext.Wallets
            .AsNoTracking()
            .Where(w => w.Id == sourceWalletId || w.Id == destinationWalletId)
            .Select(w => new { w.Id, w.UserId, w.Status, w.Currency, w.AccountId, AccountNumber = w.Account!.AccountNumber })
            .ToListAsync(cancellationToken);

        var sourceWallet = wallets.FirstOrDefault(w => w.Id == sourceWalletId);

        if (sourceWallet is null)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
        }

        // Wallet ownership: a valid token alone does not authorize moving funds out of
        // any wallet, only the caller's own (README §7).
        if (sourceWallet.UserId != callerUserId)
        {
            _logger.LogWarning(
                "Transfer rejected - caller {CallerUserId} does not own source Wallet {SourceWalletId}.",
                callerUserId,
                sourceWalletId);

            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.RequestNotAllowed);
        }

        var destinationWallet = wallets.FirstOrDefault(w => w.Id == destinationWalletId);

        if (destinationWallet is null)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
        }

        if (sourceWallet.Currency != destinationWallet.Currency ||
            sourceWallet.Currency != NovaWalletConstants.CurrencyCode)
        {
            return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                ResponseCodes.InvalidEntryDetected.ResponseCode,
                "Cross-currency transfers are not supported.");
        }

        // ---- Transactional write - db transaction wrapped in an execution strategy ----
        var strategy = _dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
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
                // constraint on JournalEntries.IdempotencyKey is what actually prevents
                // two concurrent requests from both winning the insert race (README §5
                // step 4); this is the app-level reconciliation of that outcome.
                var raceWinner = await FindByIdempotencyKey(idempotencyKey, cancellationToken);

                if (raceWinner is { } winner)
                {
                    if (winner.RequestPayloadHash == requestPayloadHash)
                    {
                        _logger.LogInformation(
                            "Transfer replay detected after unique constraint race for IdempotencyKey {IdempotencyKey}.",
                            idempotencyKey);

                        return ServiceApiResponse<WalletTransferResponse>.CreateSuccess(
                            ToResponse(request, winner));
                    }

                    _logger.LogWarning(
                        ex,
                        "Transfer rejected - Idempotency-Key {IdempotencyKey} lost a unique constraint race with a different payload.",
                        idempotencyKey);

                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                        ResponseCodes.Conflict.ResponseCode,
                        "Idempotency-Key has already been used with a different request payload.");
                }

                _logger.LogError(
                    ex,
                    "Transfer lost a unique constraint race but no JournalEntry was found for IdempotencyKey {IdempotencyKey}.",
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
    /// Looks up a transfer by its IdempotencyKey, left-joining the persisted
    /// WalletTransfer row (written in the same transaction as the JournalEntry, so the
    /// two are always consistent with each other - the join is defensive, not expected
    /// to ever come back null when a JournalEntry is found).
    /// </summary>
    private Task<IdempotencyLookupResult?> FindByIdempotencyKey(string idempotencyKey, CancellationToken cancellationToken)
    {
        return (
            from j in _dbContext.JournalEntries.AsNoTracking()
            where j.IdempotencyKey == idempotencyKey
            join t in _dbContext.WalletTransfers.AsNoTracking() on j.Id equals t.JournalEntryId into transfers
            from t in transfers.DefaultIfEmpty()
            select new IdempotencyLookupResult(j.Id, j.CreatedAt, j.RequestPayloadHash, t))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record IdempotencyLookupResult(
        Guid JournalEntryId, DateTime JournalEntryCreatedAt, string RequestPayloadHash, WalletTransfer? Transfer);

    private async Task<bool> TryClaimIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        if (_redisConnection is null)
        {
            return false;
        }

        try
        {
            var redisDatabase = _redisConnection.GetDatabase();

            return await redisDatabase.StringSetAsync(
                RedisClaimKey(idempotencyKey), "processing", RedisClaimTtl, When.NotExists);
        }
        catch (Exception ex)
        {
            // Redis is a coordination fast-path, never the source of truth (README §1).
            // Any failure here simply routes to the DB-authoritative lookup instead.
            _logger.LogWarning(
                ex,
                "Redis idempotency claim failed for IdempotencyKey {IdempotencyKey}. Falling through to DB lookup.",
                idempotencyKey);

            return false;
        }
    }

    private static string RedisClaimKey(string idempotencyKey) => $"idempotency:{idempotencyKey}";

    /// <summary>
    /// Reconstructs the response for a replayed request from the persisted
    /// WalletTransfer row - the authoritative, queryable record of this transfer.
    /// Falls back to echoing the (hash-verified-identical) incoming request only if no
    /// WalletTransfer is found, which should be unreachable in practice since both rows
    /// are written in the same DB transaction; kept purely as a defensive fallback so a
    /// replay can never fail outright over this.
    /// </summary>
    private WalletTransferResponse ToResponse(WalletTransferRequest request, IdempotencyLookupResult lookup)
    {
        if (lookup.Transfer is { } transfer)
        {
            return ToResponse(transfer);
        }

        _logger.LogWarning(
            "WalletTransfer row missing for JournalEntry {JournalEntryId} despite a matching IdempotencyKey hash - falling back to echoing the request.",
            lookup.JournalEntryId);

        return new WalletTransferResponse
        {
            SourceWalletId = request.SourceWalletId,
            DestinationWalletId = request.DestinationWalletId,
            AmountInKobo = request.AmountInKobo,
            Narration = request.Narration,
            TransactionDate = lookup.JournalEntryCreatedAt,
            PaymentReference = lookup.JournalEntryId.ToString()
        };
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

    private static string ComputeRequestPayloadHash(WalletTransferRequest request)
    {
        // SHA-256 of the sorted request body (README §5 step 1) - sorted by field name
        // so the hash is stable regardless of the caller's JSON property ordering.
        var canonical = string.Join('|', new[]
        {
            $"AmountInKobo={request.AmountInKobo}",
            $"DestinationWalletId={request.DestinationWalletId}",
            $"Narration={request.Narration}",
            $"SourceWalletId={request.SourceWalletId}"
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
