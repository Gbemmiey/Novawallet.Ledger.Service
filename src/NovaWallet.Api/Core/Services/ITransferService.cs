using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Core.Services;

public interface ITransferService
{
    Task<ServiceApiResponse<WalletTransferResponse>> Transfer(WalletTransferRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up the outcome (Completed or Failed) of a transfer by its Idempotency-Key. Only the
    /// caller's own transfers are returned; anything else is reported as not found.
    /// </summary>
    Task<ServiceApiResponse<WalletTransferStatusResponse>> RequeryTransfer(string idempotencyKey, CancellationToken cancellationToken);
}
