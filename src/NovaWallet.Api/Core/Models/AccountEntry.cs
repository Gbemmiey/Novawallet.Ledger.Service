using NovaWallet.Api.Core.Enums;
using UUIDNext;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// A single immutable debit or credit line within a JournalEntry. AmountKobo
    /// is always positive; EntryType discriminates which side of the posting it
    /// represents — enforced both here (constructor guard) and by a DB CHECK as a
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
        /// <summary>Hard ceiling for <see cref="TransParticulars"/>, matching the DB column's
        /// <c>HasMaxLength(100)</c>. Overlong text is trimmed and truncated, never rejected -
        /// this is a system-composed narration, not user-supplied input worth failing a
        /// posting over.</summary>
        internal const int MaxTransParticularsLength = 100;

        public Guid Id { get; }
        public Guid JournalEntryId { get; }
        public JournalEntry? JournalEntry { get; private set; }
        public Guid AccountId { get; }
        public Account? Account { get; private set; }
        public long AmountKobo { get; }
        public EntryType EntryType { get; }

        /// <summary>System-composed, per-leg narration describing this specific line (e.g.
        /// "Transfer to 0123456789" on the debit leg vs. "Transfer from 0123456789" on the
        /// credit leg of the same JournalEntry) - distinct from WalletTransfers.Narration,
        /// which is a single free-text value shared by both legs.</summary>
        public string TransParticulars { get; }
        public DateTime CreatedAt { get; }

        private AccountEntry(Guid id, Guid journalEntryId, Guid accountId,
            long amountKobo, EntryType entryType, string transParticulars, DateTime createdAt)
        {
            Id = id;
            JournalEntryId = journalEntryId;
            AccountId = accountId;
            AmountKobo = amountKobo;
            EntryType = entryType;
            TransParticulars = transParticulars;
            CreatedAt = createdAt;
        }

        internal static AccountEntry CreateDebit(Guid journalEntryId, Guid accountId, long amountKobo, string transParticulars)
        {
            if (amountKobo <= 0) throw new ArgumentOutOfRangeException(nameof(amountKobo), "Debit amount must be positive.");
            return new AccountEntry(Uuid.NewSequential(), journalEntryId, accountId, amountKobo, EntryType.Debit,
                NormalizeTransParticulars(transParticulars), DateTime.UtcNow);
        }

        internal static AccountEntry CreateCredit(Guid journalEntryId, Guid accountId, long amountKobo, string transParticulars)
        {
            if (amountKobo <= 0) throw new ArgumentOutOfRangeException(nameof(amountKobo), "Credit amount must be positive.");
            return new AccountEntry(Uuid.NewSequential(), journalEntryId, accountId, amountKobo, EntryType.Credit,
                NormalizeTransParticulars(transParticulars), DateTime.UtcNow);
        }

        private static string NormalizeTransParticulars(string transParticulars)
        {
            if (string.IsNullOrWhiteSpace(transParticulars))
                throw new ArgumentException("TransParticulars is required.", nameof(transParticulars));

            var trimmed = transParticulars.Trim();

            return trimmed.Length > MaxTransParticularsLength
                ? trimmed[..MaxTransParticularsLength]
                : trimmed;
        }
    }
}