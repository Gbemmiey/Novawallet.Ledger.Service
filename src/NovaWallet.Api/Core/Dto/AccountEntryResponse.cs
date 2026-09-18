using NovaWallet.Api.Core.Enums;

namespace NovaWallet.Api.Core.Dto
{
    /// <summary>
    /// A single line item in a wallet's paginated statement - a read-facing projection of
    /// <see cref="Models.AccountEntry"/>, scoped server-side to the caller-owned wallet's
    /// underlying Account (see <c>WalletService.GetWalletStatement</c>).
    /// </summary>
    public class AccountEntryResponse
    {
        public Guid Id { get; set; }
        public long AmountKobo { get; set; }
        public EntryType EntryType { get; set; }
        public string TransParticulars { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
