namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Materialized per-day, per-wallet outbound-transfer usage. The hot path
    /// updates TotalSpentKobo via the atomic guarded UPSERT in
    /// Data/Sql/DailyUsageUpsert.sql; IncrementUsage() mirrors that guard for
    /// in-process/test use. UsageDate is always the WAT (Africa/Lagos) calendar
    /// date, never the raw session timezone default.
    /// </summary>
    public class WalletDailyUsage
    {
        public Guid WalletId { get; }
        public Wallet? Wallet { get; private set; }
        public DateOnly UsageDate { get; }
        public long TotalSpentKobo { get; private set; }

        private WalletDailyUsage(Guid walletId, DateOnly usageDate, long totalSpentKobo)
        {
            WalletId = walletId;
            UsageDate = usageDate;
            TotalSpentKobo = totalSpentKobo;
        }

        public static WalletDailyUsage Create(Guid walletId, DateOnly usageDate)
        {
            if (walletId == Guid.Empty) throw new ArgumentException("WalletId is required.", nameof(walletId));
            return new WalletDailyUsage(walletId, usageDate, totalSpentKobo: 0);
        }

        public void IncrementUsage(long amountKobo, long dailyLimitKobo)
        {
            if (amountKobo <= 0) throw new ArgumentOutOfRangeException(nameof(amountKobo), "Amount must be positive.");
            if (TotalSpentKobo + amountKobo > dailyLimitKobo)
                throw new InvalidOperationException($"Daily limit exceeded for wallet {WalletId} on {UsageDate}.");

            TotalSpentKobo += amountKobo;
        }
    }
}