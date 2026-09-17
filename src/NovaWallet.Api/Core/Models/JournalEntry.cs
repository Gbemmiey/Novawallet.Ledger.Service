using NovaWallet.Api.Core.Enums;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Header record for one double-entry posting (a transfer or a deposit
    /// settlement). Acts as the aggregate root for its AccountEntry lines — lines
    /// are only ever created through AddDebitLine()/AddCreditLine() so the entry
    /// can never exist without its owning journal.
    ///
    /// IdempotencyKey is the client-supplied Idempotency-Key header for
    /// transfers; RequestPayloadHash distinguishes a legitimate replay (same key,
    /// same hash -> return cached result) from a reused key with a different
    /// payload (same key, different hash -> 409 Conflict).
    /// </summary>
    public class JournalEntry
    {
        public Guid Id { get; }
        public string IdempotencyKey { get; }
        public string RequestPayloadHash { get; }
        public DateTime CreatedAt { get; }

        private readonly List<AccountEntry> _lines = [];
        public IReadOnlyCollection<AccountEntry> Lines => _lines;

        /// <summary>True once SUM(Debit) == SUM(Credit) across all lines posted so far.
        /// Callers should check this before committing the surrounding DB transaction.</summary>
        public bool IsBalanced =>
            _lines.Where(l => l.EntryType == EntryType.Debit).Sum(l => l.AmountKobo)
            == _lines.Where(l => l.EntryType == EntryType.Credit).Sum(l => l.AmountKobo);

        private JournalEntry(Guid id, string idempotencyKey, string requestPayloadHash, DateTime createdAt)
        {
            Id = id;
            IdempotencyKey = idempotencyKey;
            RequestPayloadHash = requestPayloadHash;
            CreatedAt = createdAt;
        }

        public static JournalEntry Create(string idempotencyKey, string requestPayloadHash)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));
            if (string.IsNullOrWhiteSpace(requestPayloadHash))
                throw new ArgumentException("RequestPayloadHash is required.", nameof(requestPayloadHash));

            return new JournalEntry(Guid.NewGuid(), idempotencyKey, requestPayloadHash, DateTime.UtcNow);
        }

        public AccountEntry AddDebitLine(Guid accountId, long amountKobo, string transParticulars)
        {
            var line = AccountEntry.CreateDebit(Id, accountId, amountKobo, transParticulars);
            _lines.Add(line);
            return line;
        }

        public AccountEntry AddCreditLine(Guid accountId, long amountKobo, string transParticulars)
        {
            var line = AccountEntry.CreateCredit(Id, accountId, amountKobo, transParticulars);
            _lines.Add(line);
            return line;
        }
    }
}