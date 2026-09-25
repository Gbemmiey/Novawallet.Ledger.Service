using NovaWallet.Application.Dto.Login;
using NovaWallet.Application.Response;

namespace NovaWallet.Application.Abstractions
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
