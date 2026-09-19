namespace NovaWallet.Api.Core.Options
{
    /// <summary>
    /// Strongly-typed configuration for <see cref="NovaWallet.Api.Workers.DepositConsumer"/>,
    /// the polling background worker that settles pending <c>DepositOutbox</c> rows.
    /// </summary>
    /// <remarks>
    /// Bound from the "DepositConsumer" section in configuration files (e.g. appsettings.json):
    /// <code>
    /// {
    ///   "DepositConsumer": {
    ///     "PollingIntervalSeconds": 2,
    ///     "BatchSize": 50
    ///   }
    /// }
    /// </code>
    /// </remarks>
    public class DepositConsumerOptions
    {
        /// <summary>
        /// Gets or sets how often, in seconds, the worker polls <c>DepositOutbox</c> for
        /// rows with <see cref="Enums.OutboxStatus.Pending"/>. Default: 2 seconds.
        /// </summary>
        public int PollingIntervalSeconds { get; set; } = 2;

        /// <summary>
        /// Gets or sets the maximum number of pending outbox rows fetched per poll cycle.
        /// Each row is still settled in its own DB transaction, so this only bounds how
        /// much work one cycle picks up. Default: 50.
        /// </summary>
        public int BatchSize { get; set; } = 50;
    }
}
