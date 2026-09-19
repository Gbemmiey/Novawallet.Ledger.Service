using NovaWallet.Api.Core.Enums;

namespace NovaWallet.Api.Core.Dto
{
    /// <summary>
    /// Body for <c>PATCH /api/v1/admin/wallets/{walletId}/status</c>. See
    /// <c>AdminService.UpdateWalletStatus</c> for the transition rules (Closed is terminal;
    /// same-status is treated as an idempotent no-op).
    /// </summary>
    public class AdminWalletStatusUpdateRequest
    {
        public WalletStatus Status { get; set; }
    }
}
