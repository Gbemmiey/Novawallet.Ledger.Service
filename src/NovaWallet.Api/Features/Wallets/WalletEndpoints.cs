using NovaWallet.Api.Core.Dto;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Http;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Features.Wallets
{
    public static class WalletEndpoints
    {
        public static RouteGroupBuilder MapWalletEndpoints(
            this RouteGroupBuilder group)
        {
            var walletApi = group
                .WithTags("Wallets")
                .RequireAuthorization();

            walletApi
                .MapPost("/", CreateWallet)
                .WithName("CreateWallet")
                .RequireRateLimiting(RateLimitingConstants.UserPolicy)
                .Produces<ServiceApiResponse<CreateWalletResponse>>();

            walletApi
                .MapGet("/", RetrieveWalletDetails)
                .WithName("RetrieveWalletDetails")
                .RequireRateLimiting(RateLimitingConstants.UserPolicy)
                .Produces<ServiceApiResponse<CreateWalletResponse>>();

            walletApi
                .MapGet("/statement", GetWalletStatement)
                .WithName("GetWalletStatement")
                .RequireRateLimiting(RateLimitingConstants.UserPolicy)
                .Produces<ServiceApiResponse<PagedResponse<AccountEntryResponse>>>();

            return group;
        }

        private static async Task<IResult> CreateWallet(
            HttpContext httpContext,
            IWalletService walletService,
            CancellationToken cancellationToken)
        {
            var response = await walletService.CreateWallet(cancellationToken);

            return response.ToResult(httpContext);
        }

        private static async Task<IResult> RetrieveWalletDetails(
            HttpContext httpContext,
            IWalletService walletService,
            CancellationToken cancellationToken)
        {
            var response = await walletService.RetrieveWalletDetails(cancellationToken);

            return response.ToResult(httpContext);
        }

        /// <summary>
        /// Paginated, newest-first statement of the caller's own wallet's AccountEntries.
        /// Since a user has exactly one wallet, the wallet is resolved from the caller's
        /// identity (IWalletService.GetWalletStatement) - no walletId is accepted, so there is
        /// no other-wallet access to guard against. 404 if the caller has no wallet yet.
        /// pageSize is clamped server-side (1-100, default 20).
        /// </summary>
        private static async Task<IResult> GetWalletStatement(
            HttpContext httpContext,
            IWalletService walletService,
            CancellationToken cancellationToken,
            int pageNumber = 1,
            int pageSize = 20,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            var response = await walletService.GetWalletStatement(
                pageNumber, pageSize, fromDate, toDate, cancellationToken);

            return response.ToResult(httpContext);
        }
    }
}