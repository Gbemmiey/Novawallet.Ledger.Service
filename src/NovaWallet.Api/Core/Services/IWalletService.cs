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
        /// Returns a paginated, newest-first statement of AccountEntries for the caller-owned
        /// wallet's underlying Account. Ownership-enforced: 404 if the wallet doesn't exist,
        /// 403 if the caller doesn't own it - a valid token alone does not authorize reading
        /// any wallet's statement, only the caller's own (mirrors TransferService's ownership
        /// guard).
        /// </summary>
        Task<ServiceApiResponse<PagedResponse<AccountEntryResponse>>> GetWalletStatement(
            Guid walletId,
            int pageNumber,
            int pageSize,
            DateTime? fromDate,
            DateTime? toDate,
            CancellationToken cancellationToken);
    }
}