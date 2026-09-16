namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Insert-only audit trail, deliberately separate from AccountEntries.
    /// AccountEntries answers "what moved, in double-entry terms"; AuditLog
    /// answers "who did what, when, and what did the balance look like
    /// before/after" — the compliance-facing view. Fully immutable: there is no
    /// mutation method on this type, and no UPDATE/DELETE is issued against it
    /// anywhere in the codebase.
    /// </summary>
    public class AuditLog
    {
        public Guid Id { get; }
        public Guid WalletId { get; }
        public Wallet? Wallet { get; private set; }
        public string ActorSubject { get; }
        public string Action { get; }
        public long BalanceBeforeKobo { get; }
        public long BalanceAfterKobo { get; }
        public Guid CorrelationId { get; }
        public DateTimeOffset CreatedAt { get; }

        private AuditLog(Guid id, Guid walletId, string actorSubject, string action,
            long balanceBeforeKobo, long balanceAfterKobo, Guid correlationId, DateTimeOffset createdAt)
        {
            Id = id;
            WalletId = walletId;
            ActorSubject = actorSubject;
            Action = action;
            BalanceBeforeKobo = balanceBeforeKobo;
            BalanceAfterKobo = balanceAfterKobo;
            CorrelationId = correlationId;
            CreatedAt = createdAt;
        }

        public static AuditLog Create(Guid walletId, string actorSubject, string action,
            long balanceBeforeKobo, long balanceAfterKobo, Guid correlationId)
        {
            if (walletId == Guid.Empty) throw new ArgumentException("WalletId is required.", nameof(walletId));
            if (string.IsNullOrWhiteSpace(actorSubject)) throw new ArgumentException("ActorSubject is required.", nameof(actorSubject));
            if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("Action is required.", nameof(action));

            return new AuditLog(
                id: Guid.NewGuid(),
                walletId: walletId,
                actorSubject: actorSubject,
                action: action,
                balanceBeforeKobo: balanceBeforeKobo,
                balanceAfterKobo: balanceAfterKobo,
                correlationId: correlationId,
                createdAt: DateTimeOffset.UtcNow);
        }
    }
}