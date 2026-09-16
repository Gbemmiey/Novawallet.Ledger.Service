namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// A single immutable debit or credit line within a JournalEntry. Exactly one
    /// of DebitAmountKobo / CreditAmountKobo is positive, never both, never
    /// neither — enforced both here (constructor guard) and by a DB CHECK as a
    /// backstop. This table, keyed by AccountId, is what AutoReconciliationWorker
    /// sums and compares against Wallet.AvailableBalanceKobo, and what backs the
    /// paginated statement endpoint.
    ///
    /// Construction is deliberately internal: an AccountEntry only ever makes
    /// sense as a line within a JournalEntry, so it's created via
    /// JournalEntry.AddDebitLine()/AddCreditLine() rather than directly.
    /// </summary>
    public class AccountEntry
    {
        public Guid Id { get; }
        public Guid JournalEntryId { get; }
        public JournalEntry? JournalEntry { get; private set; }
        public Guid AccountId { get; }
        public Account? Account { get; private set; }
        public long DebitAmountKobo { get; }
        public long CreditAmountKobo { get; }
        public DateTime CreatedAt { get; }

        private AccountEntry(Guid id, Guid journalEntryId, Guid accountId,
            long debitAmountKobo, long creditAmountKobo, DateTime createdAt)
        {
            Id = id;
            JournalEntryId = journalEntryId;
            AccountId = accountId;
            DebitAmountKobo = debitAmountKobo;
            CreditAmountKobo = creditAmountKobo;
            CreatedAt = createdAt;
        }

        internal static AccountEntry CreateDebit(Guid journalEntryId, Guid accountId, long amountKobo)
        {
            if (amountKobo <= 0) throw new ArgumentOutOfRangeException(nameof(amountKobo), "Debit amount must be positive.");
            return new AccountEntry(Guid.NewGuid(), journalEntryId, accountId, amountKobo, 0, DateTime.UtcNow);
        }

        internal static AccountEntry CreateCredit(Guid journalEntryId, Guid accountId, long amountKobo)
        {
            if (amountKobo <= 0) throw new ArgumentOutOfRangeException(nameof(amountKobo), "Credit amount must be positive.");
            return new AccountEntry(Guid.NewGuid(), journalEntryId, accountId, 0, amountKobo, DateTime.UtcNow);
        }
    }
}