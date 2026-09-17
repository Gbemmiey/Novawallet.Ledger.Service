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
            var existingJournalEntry = await FindByIdempotencyKey(idempotencyKey, cancellationToken);

            if (existingJournalEntry is not null)
            {
                if (existingJournalEntry.RequestPayloadHash == requestPayloadHash)
                {
                    _logger.LogInformation(
                        "Transfer replay detected for IdempotencyKey {IdempotencyKey}. Returning original result.",
                        idempotencyKey);

                    return ServiceApiResponse<WalletTransferResponse>.CreateSuccess(
                        ToResponse(request, existingJournalEntry));
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
            .Select(w => new { w.Id, w.UserId, w.Status, w.Currency })
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
                journalEntry.AddDebitLine(lockedSource.AccountId, request.AmountInKobo);
                journalEntry.AddCreditLine(lockedDestination.AccountId, request.AmountInKobo);

                if (!journalEntry.IsBalanced)
                {
                    throw new InvalidOperationException(
                        $"Transfer journal entry for IdempotencyKey {idempotencyKey} is not balanced.");
                }

                var transferOutbox = TransferOutbox.Create(journalEntry.Id);
                var actorSubject = callerUserId.ToString();

                _dbContext.JournalEntries.Add(journalEntry);
                _dbContext.TransferOutboxEntries.Add(transferOutbox);
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

                return ServiceApiResponse<WalletTransferResponse>.CreateSuccess(new WalletTransferResponse
                {
                    SourceWalletId = lockedSource.Id.ToString(),
                    DestinationWalletId = lockedDestination.Id.ToString(),
                    AmountInKobo = request.AmountInKobo,
                    Narration = request.Narration,
                    TransactionDate = journalEntry.CreatedAt,
                    PaymentReference = journalEntry.Id.ToString()
                });
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

                if (raceWinner is not null)
                {
                    if (raceWinner.RequestPayloadHash == requestPayloadHash)
                    {
                        _logger.LogInformation(
                            "Transfer replay detected after unique constraint race for IdempotencyKey {IdempotencyKey}.",
                            idempotencyKey);

                        return ServiceApiResponse<WalletTransferResponse>.CreateSuccess(
                            ToResponse(request, raceWinner));
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

    private Task<JournalEntry?> FindByIdempotencyKey(string idempotencyKey, CancellationToken cancellationToken)
    {
        return _dbContext.JournalEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.IdempotencyKey == idempotencyKey, cancellationToken);
    }

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
    /// Reconstructs the response for a replayed request. Narration/AmountInKobo/wallet
    /// IDs are echoed from the (hash-verified-identical) incoming request rather than
    /// stored redundantly on JournalEntry/AccountEntry, matching how DepositService.ToResponse
    /// echoes Narration back from the request on a deposit replay.
    /// </summary>
    private static WalletTransferResponse ToResponse(WalletTransferRequest request, JournalEntry journalEntry)
    {
        return new WalletTransferResponse
        {
            SourceWalletId = request.SourceWalletId,
            DestinationWalletId = request.DestinationWalletId,
            AmountInKobo = request.AmountInKobo,
            Narration = request.Narration,
            TransactionDate = journalEntry.CreatedAt,
            PaymentReference = journalEntry.Id.ToString()
        };
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
