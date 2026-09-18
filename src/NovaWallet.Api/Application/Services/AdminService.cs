using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;
using UUIDNext;

namespace NovaWallet.Api.Application.Services
{
    /// <summary>
    /// Backs the Admin-only endpoints (audit-trail lookup, wallet status override). Distinct
    /// from WalletService: every query/mutation here is deliberately NOT scoped to the caller's
    /// own wallet - an AdminOnly-policy caller can see and act across wallets by design.
    /// </summary>
    public sealed class AdminService : IAdminService
    {
        private readonly ILogger<AdminService> _logger;
        private readonly NovaWalletDbContext _novaWalletDbContext;
        private readonly IRequestContext _requestContext;

        public AdminService(ILogger<AdminService> logger, NovaWalletDbContext novaWalletDbContext, IRequestContext requestContext)
        {
            _logger = logger;
            _novaWalletDbContext = novaWalletDbContext;
            _requestContext = requestContext;
        }

        public async Task<ServiceApiResponse<PagedResponse<AuditLogResponse>>> GetAuditLogs(
            Guid? walletId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var query = _novaWalletDbContext.AuditLogs.AsNoTracking();

            if (walletId is not null)
            {
                query = query.Where(a => a.WalletId == walletId.Value);
            }

            var clampedPageNumber = pageNumber < 1 ? 1 : pageNumber;
            var clampedPageSize = pageSize < 1
                ? NovaWalletConstants.PaginationConstants.DefaultPageSize
                : Math.Min(pageSize, NovaWalletConstants.PaginationConstants.MaxPageSize);

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((clampedPageNumber - 1) * clampedPageSize)
                .Take(clampedPageSize)
                .Select(a => new AuditLogResponse
                {
                    Id = a.Id,
                    WalletId = a.WalletId,
                    ActorSubject = a.ActorSubject,
                    Action = a.Action,
                    BalanceBeforeKobo = a.BalanceBeforeKobo,
                    BalanceAfterKobo = a.BalanceAfterKobo,
                    CorrelationId = a.CorrelationId,
                    CreatedAt = a.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return ServiceApiResponse<PagedResponse<AuditLogResponse>>.CreateSuccess(
                new PagedResponse<AuditLogResponse>(items, totalCount, clampedPageNumber, clampedPageSize));
        }

        public async Task<ServiceApiResponse<CreateWalletResponse>> UpdateWalletStatus(
            Guid walletId,
            AdminWalletStatusUpdateRequest request,
            CancellationToken cancellationToken)
        {
            var adminUserId = _requestContext.UserId;

            if (adminUserId is null)
            {
                return ServiceApiResponse<CreateWalletResponse>.CreateFailure(ResponseCodes.AccessDenied);
            }

            var wallet = await _novaWalletDbContext.Wallets
                .AsNoTracking()
                .Where(w => w.Id == walletId)
                .Select(w => new
                {
                    w.Id,
                    w.Status,
                    w.AvailableBalanceKobo,
                    w.Currency,
                    AccountNumber = w.Account == null ? string.Empty : w.Account.AccountNumber
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (wallet is null)
            {
                return ServiceApiResponse<CreateWalletResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
            }

            // Closed is terminal - mirrors Wallet.Close()'s domain intent. No transition out of
            // Closed, including via this admin override.
            if (wallet.Status == WalletStatus.Closed)
            {
                return ServiceApiResponse<CreateWalletResponse>.CreateFailure(
                    ResponseCodes.RequestNotAllowed.ResponseCode,
                    "Wallet is Closed; status cannot be changed further.");
            }

            // Idempotent no-op - matches ReconciliationWorker's stated philosophy of not
            // spamming an AuditLog row for a status that hasn't actually changed.
            if (wallet.Status == request.Status)
            {
                return ServiceApiResponse<CreateWalletResponse>.CreateSuccess(new CreateWalletResponse
                {
                    WalletId = wallet.Id,
                    CurrencyCode = wallet.Currency,
                    AvailableBalanceKobo = wallet.AvailableBalanceKobo,
                    AccountNumber = wallet.AccountNumber,
                    Status = wallet.Status
                });
            }

            var strategy = _novaWalletDbContext.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _novaWalletDbContext.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    // Atomic, guarded compare-and-swap UPDATE - same philosophy as the hot
                    // balance-mutation paths (README §4): the WHERE clause re-checks the status
                    // just read above as part of the same atomic operation as the mutation, so a
                    // concurrent status change (another admin call, or ReconciliationWorker's
                    // auto-freeze) can never be silently clobbered. No application-held row lock
                    // is taken ahead of time.
                    var affectedRows = await _novaWalletDbContext.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE ""Wallets""
                        SET ""Status"" = {(int)request.Status}
                        WHERE ""Id"" = {walletId} AND ""Status"" = {(int)wallet.Status}",
                        cancellationToken);

                    if (affectedRows == 0)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);

                        _logger.LogWarning(
                            "Admin wallet-status update lost a race - Wallet {WalletId} status changed concurrently since it was read as {ExpectedStatus}.",
                            walletId,
                            wallet.Status);

                        return ServiceApiResponse<CreateWalletResponse>.CreateFailure(
                            ResponseCodes.Conflict.ResponseCode,
                            "Wallet status changed concurrently; please retry.");
                    }

                    _novaWalletDbContext.AuditLogs.Add(AuditLog.Create(
                        walletId: walletId,
                        actorSubject: adminUserId.Value.ToString(),
                        action: "AdminStatusChange",
                        balanceBeforeKobo: wallet.AvailableBalanceKobo,
                        balanceAfterKobo: wallet.AvailableBalanceKobo,
                        correlationId: Uuid.NewSequential()));

                    await _novaWalletDbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation(
                        "Admin {AdminUserId} changed Wallet {WalletId} status from {PreviousStatus} to {NewStatus}.",
                        adminUserId.Value,
                        walletId,
                        wallet.Status,
                        request.Status);

                    return ServiceApiResponse<CreateWalletResponse>.CreateSuccess(new CreateWalletResponse
                    {
                        WalletId = wallet.Id,
                        CurrencyCode = wallet.Currency,
                        AvailableBalanceKobo = wallet.AvailableBalanceKobo,
                        AccountNumber = wallet.AccountNumber,
                        Status = request.Status
                    });
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
                        "Failed to update Wallet {WalletId} status via admin override.",
                        walletId);

                    return ServiceApiResponse<CreateWalletResponse>.SystemMalFunctioned();
                }
            });
        }
    }
}
