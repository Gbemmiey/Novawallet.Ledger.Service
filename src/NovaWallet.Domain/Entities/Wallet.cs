using NovaWallet.Domain.Enums;
using UUIDNext;

namespace NovaWallet.Domain.Entities
{
    /// <summary>
    /// Product-domain entity. Holds the fast-access, mutable balance used for the
    /// O(1) overdraft guard on the debit path. The authoritative financial record
    /// lives in AccountEntries via the linked Account.
    ///
    /// NOTE: the hot debit path (see Data/Sql/DebitWalletAtomic.sql) mutates
    /// AvailableBalanceKobo directly with an atomic conditional UPDATE, bypassing
    /// this entity entirely — that's intentional (see README §4). Credit()/Debit()
    /// below exist so the same invariants are enforced in-process for the
    /// non-contended paths (deposit settlement, unit tests) rather than only
    /// living in raw SQL.
    /// </summary>
    public class Wallet
    {
        public Guid Id { get; }
        public Guid UserId { get; }
        public string Currency { get; }
        public long AvailableBalanceKobo { get; private set; }
        public WalletStatus Status { get; private set; }
        public Guid AccountId { get; }
        public Account? Account { get; private set; }
        public DateTime CreatedAt { get; }

        private readonly List<WalletDailyUsage> _dailyUsages = [];
        public IReadOnlyCollection<WalletDailyUsage> DailyUsages => _dailyUsages;

        // Private constructor used by EF Core constructor binding for materialization,
        // and internally by the Create factory. Never called directly outside this class.
        private Wallet(Guid id, Guid userId, string currency, long availableBalanceKobo,
            WalletStatus status, Guid accountId, DateTime createdAt)
        {
            Id = id;
            UserId = userId;
            Currency = currency;
            AvailableBalanceKobo = availableBalanceKobo;
            Status = status;
            AccountId = accountId;
            CreatedAt = createdAt;
        }

        public static Wallet Create(Guid userId, Guid accountId, string currency = "NGN")
        {
            if (userId == Guid.Empty) throw new ArgumentException("UserId is required.", nameof(userId));
            if (accountId == Guid.Empty) throw new ArgumentException("AccountId is required.", nameof(accountId));

            return new Wallet(
                id: Uuid.NewSequential(),
                userId: userId,
                currency: currency,
                availableBalanceKobo: 0,
                status: WalletStatus.Active,
                accountId: accountId,
                createdAt: DateTime.UtcNow);
        }

        public void Credit(long amountKobo)
        {
            if (amountKobo <= 0) throw new ArgumentOutOfRangeException(nameof(amountKobo), "Credit amount must be positive.");
            EnsureActive();
            AvailableBalanceKobo += amountKobo;
        }

        public void Debit(long amountKobo)
        {
            if (amountKobo <= 0) throw new ArgumentOutOfRangeException(nameof(amountKobo), "Debit amount must be positive.");
            EnsureActive();
            if (AvailableBalanceKobo < amountKobo)
                throw new InvalidOperationException($"Insufficient funds on wallet {Id}.");

            AvailableBalanceKobo -= amountKobo;
        }

        public void Freeze() => Status = Status == WalletStatus.Closed
            ? throw new InvalidOperationException("Cannot freeze a closed wallet.")
            : WalletStatus.Frozen;

        public void Reactivate() => Status = Status == WalletStatus.Closed
            ? throw new InvalidOperationException("Cannot reactivate a closed wallet.")
            : WalletStatus.Active;

        public void Close() => Status = WalletStatus.Closed;

        private void EnsureActive()
        {
            if (Status != WalletStatus.Active)
                throw new InvalidOperationException($"Wallet {Id} is not active (status: {Status}).");
        }
    }
}