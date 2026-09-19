using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Core.Services
{
    public interface IDepositService
    {
        Task<ServiceApiResponse<NipSingleCreditResponse>> SubmitDepositRequest(
            NipSingleCreditRequest nipSingleCreditRequest, CancellationToken cancellationToken);

        /// <summary>Looks up the processing state of an inbound credit by its SessionId.</summary>
        Task<ServiceApiResponse<NipSingleCreditStatusResponse>> RequeryDeposit(
            string sessionId, CancellationToken cancellationToken);
    }
}
