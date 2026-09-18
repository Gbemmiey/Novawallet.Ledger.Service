using NovaWallet.Api.Core.Dto;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Core.Services
{
    public interface IWalletService
    {
        Task<ServiceApiResponse<CreateWalletResponse>> CreateWallet(CancellationToken cancellationToken);

        Task<ServiceApiResponse<CreateWalletResponse>> RetrieveWalletDetails(CancellationToken cancellationToken);

        /// <summary>
        /// Returns a paginated, newest-first statement of AccountEntries for the caller's own
        /// wallet's underlying Account. A user has exactly one wallet, so the wallet is
        /// resolved from the caller's identity rather than a client-supplied walletId - there
        /// is no other-wallet access to guard against. 404 if the caller has no wallet yet.
        /// </summary>
        Task<ServiceApiResponse<PagedResponse<AccountEntryResponse>>> GetWalletStatement(
            int pageNumber,
            int pageSize,
            DateTime? fromDate,
            DateTime? toDate,
            CancellationToken cancellationToken);
    }
}