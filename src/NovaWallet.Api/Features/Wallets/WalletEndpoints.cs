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
    }
}