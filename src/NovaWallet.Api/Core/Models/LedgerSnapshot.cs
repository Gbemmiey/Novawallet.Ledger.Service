using UUIDNext;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Insert-only reconciliation record, written once per wallet per sweep tick by
    /// <see cref="NovaWallet.Api.Workers.ReconciliationWorker"/>. Asserts
    /// Wallet.AvailableBalanceKobo == Sum(AccountEntries.Credit) - Sum(AccountEntries.Debit)
    /// for the wallet's own Account (README §3). Every wallet checked in a sweep gets a row -
    /// balanced or not - so this table doubles as a full historical timeline of ledger health,
    /// not just an alert log. Fully immutable: there is no mutation method on this type, and no
    /// UPDATE/DELETE is issued against it anywhere in the codebase.
    /// </summary>
    public class LedgerSnapshot
    {
        public Guid Id { get; }

        /// <summary>Groups every wallet checked within a single sweep tick - all rows sharing a
        /// RunId were computed from the same <see cref="NovaWallet.Api.Workers.ReconciliationWorker"/>
        /// tick.</summary>
        public Guid RunId { get; }
        public Guid WalletId { get; }
        public Wallet? Wallet { get; private set; }
        public Guid AccountId { get; }
        public Account? Account { get; private set; }

        /// <summary>Wallet.AvailableBalanceKobo at the instant this snapshot was taken.</summary>
        public long WalletBalanceKobo { get; }

        /// <summary>Sum(Credit) - Sum(Debit) over AccountEntries for AccountId, read from the
        /// same SQL statement/MVCC snapshot as WalletBalanceKobo.</summary>
        public long LedgerBalanceKobo { get; }

        /// <summary>WalletBalanceKobo - LedgerBalanceKobo. Zero means balanced.</summary>
        public long DiscrepancyKobo { get; }
        public bool IsBalanced { get; }
        public DateTime CreatedAt { get; }

        private LedgerSnapshot(Guid id, Guid runId, Guid walletId, Guid accountId,
            long walletBalanceKobo, long ledgerBalanceKobo, long discrepancyKobo, bool isBalanced, DateTime createdAt)
        {
            Id = id;
            RunId = runId;
            WalletId = walletId;
            AccountId = accountId;
            WalletBalanceKobo = walletBalanceKobo;
            LedgerBalanceKobo = ledgerBalanceKobo;
            DiscrepancyKobo = discrepancyKobo;
            IsBalanced = isBalanced;
            CreatedAt = createdAt;
        }

        public static LedgerSnapshot Create(Guid runId, Guid walletId, Guid accountId,
            long walletBalanceKobo, long ledgerBalanceKobo)
        {
            if (runId == Guid.Empty) throw new ArgumentException("RunId is required.", nameof(runId));
            if (walletId == Guid.Empty) throw new ArgumentException("WalletId is required.", nameof(walletId));
            if (accountId == Guid.Empty) throw new ArgumentException("AccountId is required.", nameof(accountId));

            var discrepancyKobo = walletBalanceKobo - ledgerBalanceKobo;

            return new LedgerSnapshot(
                id: Uuid.NewSequential(),
                runId: runId,
                walletId: walletId,
                accountId: accountId,
                walletBalanceKobo: walletBalanceKobo,
                ledgerBalanceKobo: ledgerBalanceKobo,
                discrepancyKobo: discrepancyKobo,
                isBalanced: discrepancyKobo == 0,
                createdAt: DateTime.UtcNow);
        }
    }
}
