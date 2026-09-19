using NovaWallet.Api.Core.Enums;

namespace NovaWallet.Api.Core.Dto.Login
{
    public class CreateWalletResponse
    {
        public Guid WalletId { get; set; }

        public string CurrencyCode { get; set; }
        public long AvailableBalanceKobo { get; set; }

        public string AccountNumber { get; set; }

        public WalletStatus Status { get; set; }
    }
}