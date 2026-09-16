namespace NovaWallet.Api.Core.Enums
{
    public enum AccountType
    {
        Asset = 1,      // e.g. Settlement / Bank accounts (1000-NIP-SETTLEMENT)
        Liability = 2,  // e.g. per-wallet user liability accounts
        Equity = 3,
        Revenue = 4,    // e.g. Transfer fee income
        Expense = 5
    }
}