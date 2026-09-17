using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;
using Npgsql;
using StackExchange.Redis;
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
    private readonly IConnectionMultiplexer? _redisConnection;

    public TransferService(
        ILogger<TransferService> logger,
        NovaWalletDbContext dbContext,
        IRequestContext requestContext,
        IConnectionMultiplexer? redisConnection = null)
    {
        _logger = logger;
        _dbContext = dbContext;
        _requestContext = requestContext;
        _redisConnection = redisConnection;
    }

    public async Task<ServiceApiResponse<WalletTransferResponse>> Transfer(WalletTransferRequest request, CancellationToken cancellationToken)
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
            .Select(w => new { w.Id, w.UserId, w.Status, w.Currency, AccountNumber = w.Account!.AccountNumber })
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
                // Deadlock prevention: always lock in ascending Guid order (README §4),
                // regardless of which side is source vs. destination for this request.
                var (firstId, secondId) = sourceWalletId.CompareTo(destinationWalletId) <= 0
                    ? (sourceWalletId, destinationWalletId)
                    : (destinationWalletId, sourceWalletId);

                var firstLocked = await LockWalletAsync(firstId, cancellationToken);
                var secondLocked = await LockWalletAsync(secondId, cancellationToken);

                var lockedSource = firstLocked.Id == sourceWalletId ? firstLocked : secondLocked;
                var lockedDestination = firstLocked.Id == destinationWalletId ? firstLocked : secondLocked;

                // Destination status is checked the same as source - a frozen/closed
                // wallet rejects inbound credits the same way it rejects outbound debits
                // (README §7).
                if (lockedSource.Status != WalletStatus.Active)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                        ResponseCodes.RequestNotAllowed.ResponseCode,
                        $"Source wallet is {lockedSource.Status} and cannot originate transfers.");
                }

                if (lockedDestination.Status != WalletStatus.Active)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(
                        ResponseCodes.RequestNotAllowed.ResponseCode,
                        $"Destination wallet is {lockedDestination.Status} and cannot receive transfers.");
                }

                // Atomic, guarded daily-usage UPSERT (README §6). The INSERT branch is
                // guarded exactly like the UPDATE branch, so a wallet's first transfer of
                // the day is checked too, not just subsequent ones. UsageDate is anchored
                // to WAT explicitly rather than following the DB session's timezone default.
                var dailyUsageRowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""WalletDailyUsage"" (""WalletId"", ""UsageDate"", ""TotalSpentKobo"")
                    SELECT {lockedSource.Id}, (CURRENT_TIMESTAMP AT TIME ZONE 'Africa/Lagos')::DATE, {request.AmountInKobo}
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
                        lockedSource.Id,
                        request.AmountInKobo);

                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.DailyLimitExceeded);
                }

                var sourceBalanceBeforeKobo = lockedSource.AvailableBalanceKobo;
                var destinationBalanceBeforeKobo = lockedDestination.AvailableBalanceKobo;

                try
                {
                    lockedSource.Debit(request.AmountInKobo);
                }
                catch (InvalidOperationException)
                {
                    // Status was already re-validated as Active above under the row
                    // lock, so this can only be an insufficient-balance rejection.
                    await transaction.RollbackAsync(CancellationToken.None);
                    return ServiceApiResponse<WalletTransferResponse>.CreateFailure(ResponseCodes.InsufficientBalance);
                }

                lockedDestination.Credit(request.AmountInKobo);

                var journalEntry = JournalEntry.Create(idempotencyKey, requestPayloadHash);
                journalEntry.AddDebitLine(
                    lockedSource.AccountId,
                    request.AmountInKobo,
                    ComposeParticulars($"Transfer to {destinationWallet.AccountNumber}", request.Narration));
                journalEntry.AddCreditLine(
                    lockedDestination.AccountId,
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
                    sourceWalletId: lockedSource.Id,
                    destinationWalletId: lockedDestination.Id,
                    amountKobo: request.AmountInKobo,
                    narration: request.Narration,
                    transactionDate: journalEntry.CreatedAt);

                _dbContext.JournalEntries.Add(journalEntry);
                _dbContext.TransferOutboxEntries.Add(transferOutbox);
                _dbContext.WalletTransfers.Add(walletTransfer);
                _dbContext.AuditLogs.Add(AuditLog.Create(
                    walletId: lockedSource.Id,
                    actorSubject: actorSubject,
                    action: "Debit",
                    balanceBeforeKobo: sourceBalanceBeforeKobo,
                    balanceAfterKobo: lockedSource.AvailableBalanceKobo,
                    correlationId: journalEntry.Id));
                _dbContext.AuditLogs.Add(AuditLog.Create(
                    walletId: lockedDestination.Id,
                    actorSubject: actorSubject,
                    action: "Credit",
                    balanceBeforeKobo: destinationBalanceBeforeKobo,
                    balanceAfterKobo: lockedDestination.AvailableBalanceKobo,
                    correlationId: journalEntry.Id));

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Transfer settled. IdempotencyKey: {IdempotencyKey}, SourceWalletId: {SourceWalletId}, DestinationWalletId: {DestinationWalletId}, AmountKobo: {AmountKobo}, JournalEntryId: {JournalEntryId}",
                    idempotencyKey,
                    lockedSource.Id,
                    lockedDestination.Id,
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
    /// Takes a real <c>SELECT ... FOR UPDATE</c> lock on the given wallet row. Callers
    /// must invoke this for both sides of a transfer in ascending-Guid order (README §4)
    /// to prevent deadlocks between two transfers moving funds in opposite directions
    /// between the same pair of wallets. Deliberately not <c>AsNoTracking</c>: the
    /// returned entity is mutated in-process via <see cref="Wallet.Debit"/>/<see cref="Wallet.Credit"/>
    /// and persisted by the caller's subsequent <c>SaveChangesAsync</c>.
    /// </summary>
    private async Task<Wallet> LockWalletAsync(Guid walletId, CancellationToken cancellationToken)
    {
        return await _dbContext.Wallets
            .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"Id\" = {walletId} FOR UPDATE")
            .FirstAsync(cancellationToken);
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
}
