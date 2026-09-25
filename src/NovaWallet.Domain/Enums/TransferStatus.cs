namespace NovaWallet.Domain.Enums
{
    /// <summary>
    /// Lifecycle status of a <see cref="Models.WalletTransfer"/>. A row is only ever
    /// inserted on the successful-settlement path today (see TransferService), so
    /// Completed is the only value written in practice - Reversed/Failed exist for
    /// schema completeness ahead of a future reversal feature. Stored as a string
    /// (see WalletTransferConfiguration), matching OutboxStatus/EntryType's convention.
    /// </summary>
    public enum TransferStatus
    {
        Completed = 1,
        Reversed = 2,
        Failed = 3
    }
}
