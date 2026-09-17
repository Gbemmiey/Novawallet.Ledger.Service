using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Core.Services
{
    public interface IDepositService
    {
        Task<ServiceApiResponse<NipSingleCreditResponse>> SubmitDepositRequest(
            NipSingleCreditRequest nipSingleCreditRequest, CancellationToken cancellationToken);
    }
}