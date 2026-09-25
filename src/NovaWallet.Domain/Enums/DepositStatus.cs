namespace NovaWallet.Domain.Enums
{
    /// <summary>
    /// Lifecycle status of an <see cref="Models.ExternalCreditRequest"/> - distinct from
    /// <see cref="OutboxStatus"/>, which tracks the outbox row's own delivery state.
    /// Stored as a string (see ExternalCreditRequestConfiguration), matching
    /// OutboxStatus/EntryType's convention.
    /// </summary>
    public enum DepositStatus
    {
        Pending = 1,
        Completed = 2,
        Failed = 3
    }
}
