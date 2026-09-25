using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Dto;
using NovaWallet.Application.Dto.Login;
using NovaWallet.Application.Response;
using NovaWallet.Infrastructure.Http;
using static NovaWallet.Application.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Features.Admin
{
    /// <summary>
    /// Admin-only endpoints, gated end-to-end by the AdminOnly authorization policy (see
    /// AuthenticationExtensions) plus the same InternalAdminPolicy rate limit already used for
    /// /login. Deliberately not wallet-ownership-scoped: an AdminOnly caller acts across wallets
    /// by design, unlike WalletEndpoints/TransferEndpoints.
    /// </summary>
    public static class AdminEndpoints
    {
        public static RouteGroupBuilder MapAdminEndpoints(
            this RouteGroupBuilder group)
        {
            var adminApi = group
                .WithTags("Admin")
                .RequireAuthorization(AuthorizationPolicyConstants.AdminOnly)
                .RequireRateLimiting(RateLimitingConstants.InternalAdminPolicy);

            adminApi
                .MapGet("/audit-logs", GetAuditLogs)
                .WithName("GetAuditLogs")
                .Produces<ServiceApiResponse<PagedResponse<AuditLogResponse>>>();

            adminApi
                .MapPatch("/wallets/{walletId:guid}/status", UpdateWalletStatus)
                .WithName("UpdateWalletStatus")
                .Produces<ServiceApiResponse<CreateWalletResponse>>()
                .WithValidation<AdminWalletStatusUpdateRequest>();

            return group;
        }

        /// <summary>
        /// Paginated, newest-first audit trail across all wallets, optionally filtered to a
        /// single walletId query parameter.
        /// </summary>
        private static async Task<IResult> GetAuditLogs(
            HttpContext httpContext,
            IAdminService adminService,
            CancellationToken cancellationToken,
            Guid? walletId = null,
            int pageNumber = 1,
            int pageSize = 20)
        {
            var response = await adminService.GetAuditLogs(walletId, pageNumber, pageSize, cancellationToken);

            return response.ToResult(httpContext);
        }

        /// <summary>
        /// Admin override of a wallet's status (e.g. unfreezing a wallet auto-frozen by
        /// ReconciliationWorker - see AdminService.UpdateWalletStatus for the transition rules).
        /// </summary>
        private static async Task<IResult> UpdateWalletStatus(
            Guid walletId,
            AdminWalletStatusUpdateRequest request,
            HttpContext httpContext,
            IAdminService adminService,
            CancellationToken cancellationToken)
        {
            var response = await adminService.UpdateWalletStatus(walletId, request, cancellationToken);

            return response.ToResult(httpContext);
        }
    }
}
