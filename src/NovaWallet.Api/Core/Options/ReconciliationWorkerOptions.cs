namespace NovaWallet.Api.Core.Options
{
    /// <summary>
    /// Strongly-typed configuration for <see cref="NovaWallet.Api.Workers.ReconciliationWorker"/>,
    /// the polling background worker that continuously sweeps every wallet and asserts
    /// Wallet.AvailableBalanceKobo against its ledger-derived balance (README §3).
    /// </summary>
    /// <remarks>
    /// Bound from the "ReconciliationWorker" section in configuration files (e.g. appsettings.json):
    /// <code>
    /// {
    ///   "ReconciliationWorker": {
    ///     "PollingIntervalSeconds": 60,
    ///     "BatchSize": 200,
    ///     "AutoFreezeOnDiscrepancy": true
    ///   }
    /// }
    /// </code>
    /// </remarks>
    public class ReconciliationWorkerOptions
    {
        /// <summary>
        /// Gets or sets how often, in seconds, the worker sweeps the next batch of wallets.
        /// Default: 60 seconds.
        /// </summary>
        public int PollingIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Gets or sets the number of wallets checked per sweep tick, via an in-memory keyset
        /// cursor over Wallets.Id that wraps back to the start once it reaches the end of the
        /// table - so the sweep continuously loops over the whole wallet population rather than
        /// only ever checking the first page. Default: 200.
        /// </summary>
        public int BatchSize { get; set; } = 200;

        /// <summary>
        /// Gets or sets whether an <see cref="Enums.WalletStatus.Active"/> wallet found unbalanced
        /// is automatically transitioned to <see cref="Enums.WalletStatus.Frozen"/>. This is the
        /// one automated action in the system that unilaterally locks a customer out of their
        /// funds, so it is gated behind this operational kill-switch - if the snapshot computation
        /// itself is ever suspected of producing false positives, ops can disable auto-freeze via
        /// config without a redeploy while discrepancies keep being recorded and logged. Default:
        /// <see langword="true"/>.
        /// </summary>
        public bool AutoFreezeOnDiscrepancy { get; set; } = true;
    }
}
