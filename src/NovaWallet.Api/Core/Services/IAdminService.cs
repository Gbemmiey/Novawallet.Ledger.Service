using NovaWallet.Api.Core.Dto;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Core.Services
{
    /// <summary>
    /// Backs the AdminOnly-policy endpoints under /api/v1/admin. Deliberately not
    /// ownership-scoped, unlike IWalletService/ITransferService - an admin caller acts across
    /// wallets by design.
    /// </summary>
    public interface IAdminService
    {
        /// <summary>
        /// Paginated, newest-first audit trail, optionally filtered to a single wallet.
        /// </summary>
        Task<ServiceApiResponse<PagedResponse<AuditLogResponse>>> GetAuditLogs(
            Guid? walletId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken);

        /// <summary>
        /// Admin override of a wallet's status. Closed is terminal (no transition out); setting
        /// the same status it's already at is an idempotent no-op (no AuditLog spam). Any other
        /// genuine transition is an atomic, guarded compare-and-swap UPDATE plus an AuditLog row
        /// written in the same transaction - see AdminService for the full rationale.
        /// </summary>
        Task<ServiceApiResponse<CreateWalletResponse>> UpdateWalletStatus(
            Guid walletId,
            AdminWalletStatusUpdateRequest request,
            CancellationToken cancellationToken);
    }
}
