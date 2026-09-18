using NovaWallet.Api.Core.Dto;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Http;

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
                .Produces<ServiceApiResponse<CreateWalletResponse>>();

            walletApi
                .MapGet("/", RetrieveWalletDetails)
                .WithName("RetrieveWalletDetails")
                .Produces<ServiceApiResponse<CreateWalletResponse>>();

            walletApi
                .MapGet("/{walletId:guid}/statement", GetWalletStatement)
                .WithName("GetWalletStatement")
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
        /// Paginated, newest-first statement of a wallet's AccountEntries. Ownership-enforced
        /// in IWalletService.GetWalletStatement - 404 if the wallet doesn't exist, 403 if the
        /// caller isn't its owner. pageSize is clamped server-side (1-100, default 20).
        /// </summary>
        private static async Task<IResult> GetWalletStatement(
            Guid walletId,
            HttpContext httpContext,
            IWalletService walletService,
            CancellationToken cancellationToken,
            int pageNumber = 1,
            int pageSize = 20,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            var response = await walletService.GetWalletStatement(
                walletId, pageNumber, pageSize, fromDate, toDate, cancellationToken);

            return response.ToResult(httpContext);
        }
    }
}