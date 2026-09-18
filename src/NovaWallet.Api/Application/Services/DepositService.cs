using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Cache;
using NovaWallet.Api.Infrastructure.Data;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry;
using Npgsql;
using System.Diagnostics;

namespace NovaWallet.Api.Application.Services
{
    public sealed class DepositService : IDepositService
    {
        // NIP redelivers the same callback under at-least-once semantics; 24h comfortably
        // outlives any realistic redelivery window while the DB unique indexes remain the
        // authoritative guarantee regardless of TTL.
        private static readonly HybridCacheEntryOptions IdempotencyCacheEntryOptions = new()
        {
            Expiration = TimeSpan.FromHours(24)
        };

        private readonly ILogger<DepositService> _logger;
        private readonly NovaWalletDbContext _novaWalletDbContext;
        private readonly HybridCache _hybridCache;
        private readonly NovaWalletMetrics _metrics;

        public DepositService(
            ILogger<DepositService> logger,
            NovaWalletDbContext novaWalletDbContext,
            HybridCache hybridCache,
            NovaWalletMetrics metrics)
        {
            _logger = logger;
            _novaWalletDbContext = novaWalletDbContext;
            _hybridCache = hybridCache;
            _metrics = metrics;
        }

        /// <summary>
        /// Thin, metrics-instrumented wrapper around <see cref="SubmitDepositRequestCoreAsync"/> -
        /// times the whole webhook-accept path and records
        /// <c>novawallet.deposit.requests</c>/<c>.request.duration</c>, tagged by the resulting
        /// <see cref="IServiceApiResponse.ResponseCode"/>, without touching the idempotency/DB
        /// logic itself. Also opens the <c>deposit.accept</c> span: it is a child of the ASP.NET Core
        /// request span, and its <c>traceparent</c> is what gets stored on the outbox row so the
        /// <c>DepositConsumer</c>'s <c>deposit.settle</c> span continues the same trace.
        /// </summary>
        public async Task<ServiceApiResponse<NipSingleCreditResponse>> SubmitDepositRequest(NipSingleCreditRequest nipSingleCreditRequest, CancellationToken cancellationToken)
        {
            var startTimestamp = Stopwatch.GetTimestamp();

            using var activity = NovaWalletTracing.Source.StartActivity("deposit.accept", ActivityKind.Internal);
            activity?.SetTag("deposit.session_id", nipSingleCreditRequest.SessionId);
            activity?.SetTag("deposit.transaction_reference", nipSingleCreditRequest.TransactionReference);
            activity?.SetTag("deposit.amount_kobo", nipSingleCreditRequest.AmountKobo);

            ServiceApiResponse<NipSingleCreditResponse> response;

            try
            {
                response = await SubmitDepositRequestCoreAsync(nipSingleCreditRequest, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                NovaWalletTracing.RecordException(activity, ex);
                throw;
            }

            activity?.SetTag("deposit.outcome", response.ResponseCode);

            if (!response.IsSuccessful())
            {
                activity?.SetStatus(ActivityStatusCode.Error, response.ResponseCode);
            }

            _metrics.RecordDepositRequest(response.ResponseCode, Stopwatch.GetElapsedTime(startTimestamp));

            return response;
        }

        private async Task<ServiceApiResponse<NipSingleCreditResponse>> SubmitDepositRequestCoreAsync(NipSingleCreditRequest nipSingleCreditRequest, CancellationToken cancellationToken)
        {
            try
            {
                // Redis-backed (with automatic in-memory fallback) fast-path: a redelivered
                // callback for a SessionId we've already durably processed is answered here
                // without touching Postgres. HybridCache does not cache a factory that throws,
                // so genuine failures (see DepositProcessingFailedException below) always fall
                // through to a fresh DB-backed attempt on the next delivery - the cache is a
                // fast-path, never the source of truth.
                return await _hybridCache.GetOrCreateAsync(
                    DepositIdempotencyCacheKeys.ForSessionId(nipSingleCreditRequest.SessionId),
                    (Service: this, Request: nipSingleCreditRequest),
                    static (state, ct) => state.Service.ProcessDepositAsync(state.Request, ct),
                    IdempotencyCacheEntryOptions,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DepositProcessingFailedException ex)
            {
                _logger.LogError(
                    ex.InnerException,
                    "Deposit processing failed and was not cached. SessionId: {SessionId}, TransactionReference: {TransactionReference}",
                    nipSingleCreditRequest.SessionId,
                    nipSingleCreditRequest.TransactionReference);

                return ServiceApiResponse<NipSingleCreditResponse>.SystemMalFunctioned();
            }
        }

        private async ValueTask<ServiceApiResponse<NipSingleCreditResponse>> ProcessDepositAsync(
            NipSingleCreditRequest nipSingleCreditRequest, CancellationToken cancellationToken)
        {
            // Validate the account number
            var validBeneficiaryAccount = await _novaWalletDbContext.Accounts
                .AnyAsync(a => a.Currency == NovaWalletConstants.CurrencyCode
                && a.AccountType == AccountType.Liability
                && a.AccountNumber == nipSingleCreditRequest.BeneficiaryAccountNumber, cancellationToken);

            if (!validBeneficiaryAccount)
            {
                _logger.LogWarning(
                    "Deposit rejected - no valid liability account found for BeneficiaryAccountNumber {BeneficiaryAccountNumber}. SessionId: {SessionId}",
                    nipSingleCreditRequest.BeneficiaryAccountNumber,
                    nipSingleCreditRequest.SessionId);

                return ServiceApiResponse<NipSingleCreditResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
            }

            // Idempotent replay check: the NIP switch may redeliver the same callback
            // at-least-once. If we've already durably recorded this SessionId, hand
            // back the original outcome instead of attempting a duplicate insert.
            var existingBySessionId = await FindBySessionId(nipSingleCreditRequest.SessionId, cancellationToken);

            if (existingBySessionId is not null)
            {
                _logger.LogInformation("Deposit replay detected for SessionId {SessionId}. Returning original result.",
                    nipSingleCreditRequest.SessionId);
                Activity.Current?.SetTag("deposit.replay", true);

                return ServiceApiResponse<NipSingleCreditResponse>.CreateSuccess(
                    ToResponse(existingBySessionId, nipSingleCreditRequest.Narration));
            }

            // Genuine conflict: same TransactionReference under a different SessionId
            // is not a replay, so reject it rather than silently recording it twice.
            var duplicateTransactionReference = await _novaWalletDbContext.ExternalCreditRequests
                .AnyAsync(x => x.TransactionReference == nipSingleCreditRequest.TransactionReference, cancellationToken);

            if (duplicateTransactionReference)
            {
                _logger.LogWarning(
                    "Deposit rejected - TransactionReference {TransactionReference} already recorded under a different SessionId. SessionId: {SessionId}",
                    nipSingleCreditRequest.TransactionReference,
                    nipSingleCreditRequest.SessionId);

                return ServiceApiResponse<NipSingleCreditResponse>.CreateFailure(ResponseCodes.DuplicateTransactionReference);
            }

            // Create a record - db transation wrapped in an execution strategy
            var strategy = _novaWalletDbContext.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _novaWalletDbContext.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var externalCreditRequest = ExternalCreditRequest.Create(
                        sessionId: nipSingleCreditRequest.SessionId,
                        transactionReference: nipSingleCreditRequest.TransactionReference,
                        amountKobo: nipSingleCreditRequest.AmountKobo,
                        beneficiaryAccountNumber: nipSingleCreditRequest.BeneficiaryAccountNumber,
                        originatingAccountNumber: nipSingleCreditRequest.OriginatingAccountNumber,
                        originatingBankCode: nipSingleCreditRequest.OriginatingBankCode);

                    // Carry the trace across the outbox: the polling consumer has no HTTP request
                    // to inherit context from, so it parents its settlement span on this value.
                    var depositOutbox = DepositOutbox.Create(
                        externalCreditRequest.Id,
                        NovaWalletTracing.CurrentTraceParent());

                    _novaWalletDbContext.ExternalCreditRequests.Add(externalCreditRequest);
                    _novaWalletDbContext.DepositOutboxEntries.Add(depositOutbox);

                    await _novaWalletDbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation(
                        "Deposit request recorded successfully. SessionId: {SessionId}, TransactionReference: {TransactionReference}, AmountKobo: {AmountKobo}, DepositOutboxId: {DepositOutboxId}",
                        externalCreditRequest.SessionId,
                        externalCreditRequest.TransactionReference,
                        externalCreditRequest.AmountKobo,
                        depositOutbox.Id);

                    return ServiceApiResponse<NipSingleCreditResponse>.CreateSuccess(
                        ToResponse(externalCreditRequest, nipSingleCreditRequest.Narration));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    // Lost a race against a concurrent delivery of the same callback.
                    // Re-resolve by SessionId: if it's there, this was a replay of the
                    // request we just lost the insert race on; otherwise it must have
                    // been the TransactionReference unique index that fired.
                    var raceWinner = await FindBySessionId(nipSingleCreditRequest.SessionId, cancellationToken);

                    if (raceWinner is not null)
                    {
                        _logger.LogInformation(
                            "Deposit replay detected after unique constraint race for SessionId {SessionId}. Returning original result.",
                            nipSingleCreditRequest.SessionId);
                        Activity.Current?.SetTag("deposit.replay", true);

                        return ServiceApiResponse<NipSingleCreditResponse>.CreateSuccess(
                            ToResponse(raceWinner, nipSingleCreditRequest.Narration));
                    }

                    _logger.LogWarning(
                        ex,
                        "Deposit rejected - TransactionReference {TransactionReference} lost a unique constraint race. SessionId: {SessionId}",
                        nipSingleCreditRequest.TransactionReference,
                        nipSingleCreditRequest.SessionId);

                    return ServiceApiResponse<NipSingleCreditResponse>.CreateFailure(ResponseCodes.DuplicateTransactionReference);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    // Thrown, not returned: this is a transient/unexpected failure, not a
                    // final outcome. HybridCache only caches factory results that return
                    // successfully, so throwing here guarantees the next redelivery of this
                    // SessionId gets a genuine retry against the DB instead of a stale,
                    // cached 500 for the rest of the TTL.
                    throw new DepositProcessingFailedException(
                        $"Failed to record deposit request. SessionId: {nipSingleCreditRequest.SessionId}, TransactionReference: {nipSingleCreditRequest.TransactionReference}",
                        ex);
                }
            });
        }

        private Task<ExternalCreditRequest?> FindBySessionId(string sessionId, CancellationToken cancellationToken)
        {
            return _novaWalletDbContext.ExternalCreditRequests
                .FirstOrDefaultAsync(x => x.SessionId == sessionId, cancellationToken);
        }

        private static NipSingleCreditResponse ToResponse(ExternalCreditRequest externalCreditRequest, string narration)
        {
            return new NipSingleCreditResponse
            {
                SessionId = externalCreditRequest.SessionId,
                TransactionReference = externalCreditRequest.TransactionReference,
                AmountKobo = externalCreditRequest.AmountKobo,
                BeneficiaryAccountNumber = externalCreditRequest.BeneficiaryAccountNumber,
                OriginatingAccountNumber = externalCreditRequest.OriginatingAccountNumber,
                OriginatingBankCode = externalCreditRequest.OriginatingBankCode,
                Narration = narration
            };
        }

        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            return ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
        }

        /// <summary>
        /// Marks a deposit-processing failure that must NOT be treated as a cacheable
        /// final outcome by the HybridCache idempotency fast-path in
        /// <see cref="SubmitDepositRequest"/>. Caught and translated to
        /// <see cref="ServiceApiResponse{T}.SystemMalFunctioned"/> outside the cache call.
        /// </summary>
        private sealed class DepositProcessingFailedException : Exception
        {
            public DepositProcessingFailedException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }
    }
}