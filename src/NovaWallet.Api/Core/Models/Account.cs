using NovaWallet.Api.Core.Enums;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Ledger-domain entity — a line in the chart of accounts. A wallet-backed
    /// account (Type = Liability) has exactly one owning Wallet via Wallet.AccountId.
    /// System accounts (e.g. "1000-NIP-SETTLEMENT" Asset, "4000-TRANSFER-FEE-INCOME"
    /// Revenue) have no Wallet and exist purely for double-entry postings. Fully
    /// immutable after creation — nothing about a chart-of-accounts entry changes
    /// post-creation in this scope.
    /// </summary>
    public class Account
    {
        public Guid Id { get; }
        public string AccountNumber { get; }
        public AccountType AccountType { get; }
        public string Currency { get; }
        public DateTimeOffset CreatedAt { get; }

        private readonly List<AccountEntry> _entries = [];
        public IReadOnlyCollection<AccountEntry> Entries => _entries;

        private Account(Guid id, string accountNumber, AccountType accountType, string currency, DateTimeOffset createdAt)
        {
            Id = id;
            AccountNumber = accountNumber;
            AccountType = accountType;
            Currency = currency;
            CreatedAt = createdAt;
        }

        public static Account Create(string accountNumber, AccountType accountType, string currency = "NGN")
        {
            if (string.IsNullOrWhiteSpace(accountNumber))
                throw new ArgumentException("AccountNumber is required.", nameof(accountNumber));

            return new Account(
                id: Guid.NewGuid(),
                accountNumber: accountNumber,
                accountType: accountType,
                currency: currency,
                createdAt: DateTimeOffset.UtcNow);
        }
    }
}