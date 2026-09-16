using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Core.Services
{
    public interface IWalletService
    {
        Task<ServiceApiResponse<CreateWalletResponse>> CreateWallet(CancellationToken cancellationToken);

        Task<ServiceApiResponse<CreateWalletResponse>> RetrieveWalletDetails(CancellationToken cancellationToken);
    }
}