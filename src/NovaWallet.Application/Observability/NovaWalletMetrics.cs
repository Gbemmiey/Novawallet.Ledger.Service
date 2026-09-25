using System.Diagnostics.Metrics;

namespace NovaWallet.Application.Observability
{
    /// <summary>
    /// Central home for NovaWallet's custom business/domain metrics, instrumented via the
    /// BCL's <see cref="System.Diagnostics.Metrics"/> API (no additional NuGet package needed -
    /// available since .NET 6). The underlying <see cref="Meter"/> is registered with the
    /// existing OTel metrics pipeline via <c>.AddMeter(NovaWalletMetrics.MeterName)</c> in
    /// <see cref="OpenTelemetryConfigurationExtensions.ConfigureDistributedTracingAndMetrics"/>,
    /// so every instrument here flows through the exact same OTLP exporter (and optional
    /// Prometheus <c>/metrics</c> scrape endpoint) already wired for the built-in ASP.NET
    /// Core/EF Core/runtime/process metrics - nothing new to stand up.
    /// </summary>
    /// <remarks>
    /// Registered as a singleton (see <c>SwitchServiceCollectionExtensions.RegisterApplicationServices</c>)
    /// since a <see cref="Meter"/> and its instruments are inherently thread-safe and meant to be
    /// shared for the lifetime of the process - the same lifetime the underlying OTel
    /// MeterProvider expects to subscribe to.
    ///
    /// <para>
    /// <b>Tagging discipline:</b> every tag value used below is drawn from a small, fixed,
    /// already-existing vocabulary - NIP-style <c>ResponseCode</c> strings (e.g. "00",
    /// "51"), a handful of named outcome strings, or a request path - never anything
    /// caller-supplied (wallet IDs, narrations, session IDs), to avoid unbounded cardinality in
    /// the metrics backend. <c>ResponseMessage</c> is deliberately never used as a tag for this
    /// same reason - it sometimes interpolates dynamic content.
    /// </para>
    /// </remarks>
    public sealed class NovaWalletMetrics : IDisposable
    {
        public const string MeterName = "NovaWallet.Api";

        private readonly Meter _meter;

        private readonly Counter<long> _depositRequests;
        private readonly Histogram<double> _depositRequestDuration;
        private readonly Counter<long> _depositSettlements;
        private readonly Histogram<double> _depositSettlementDuration;

        private readonly Counter<long> _transferRequests;
        private readonly Histogram<double> _transferDuration;
        private readonly Histogram<long> _transferAmountKobo;

        private readonly Counter<long> _reconciliationDiscrepancies;
        private readonly Counter<long> _reconciliationWalletsFrozen;
        private readonly Counter<long> _reconciliationWalletsChecked;
        private readonly Histogram<double> _reconciliationSweepDuration;

        private readonly Counter<long> _rateLimitRejections;

        public NovaWalletMetrics()
        {
            _meter = new Meter(MeterName, "1.0.0");

            _depositRequests = _meter.CreateCounter<long>(
                "novawallet.deposit.requests",
                unit: "{request}",
                description: "Inbound NIP deposit-webhook requests (POST /wallets/credit), by response code.");

            _depositRequestDuration = _meter.CreateHistogram<double>(
                "novawallet.deposit.request.duration",
                unit: "ms",
                description: "Latency of the synchronous webhook-accept path for inbound NIP deposits.");

            _depositSettlements = _meter.CreateCounter<long>(
                "novawallet.deposit.settlements",
                unit: "{settlement}",
                description: "DepositConsumer settlement attempts, by outcome.");

            _depositSettlementDuration = _meter.CreateHistogram<double>(
                "novawallet.deposit.settlement.duration",
                unit: "ms",
                description: "Latency of a single DepositConsumer settlement attempt, by outcome.");

            _transferRequests = _meter.CreateCounter<long>(
                "novawallet.transfer.requests",
                unit: "{request}",
                description: "POST /wallets/transfer requests, by response code.");

            _transferDuration = _meter.CreateHistogram<double>(
                "novawallet.transfer.duration",
                unit: "ms",
                description: "End-to-end latency of POST /wallets/transfer, by response code.");

            _transferAmountKobo = _meter.CreateHistogram<long>(
                "novawallet.transfer.amount",
                unit: "kobo",
                description: "Distribution of successfully-settled transfer amounts.");

            _reconciliationDiscrepancies = _meter.CreateCounter<long>(
                "novawallet.reconciliation.discrepancies",
                unit: "{wallet}",
                description: "Wallets found with a ledger/wallet-balance mismatch during a reconciliation sweep.");

            _reconciliationWalletsFrozen = _meter.CreateCounter<long>(
                "novawallet.reconciliation.wallets_frozen",
                unit: "{wallet}",
                description: "Wallets successfully auto-frozen by ReconciliationWorker due to a discrepancy.");

            _reconciliationWalletsChecked = _meter.CreateCounter<long>(
                "novawallet.reconciliation.wallets_checked",
                unit: "{wallet}",
                description: "Wallets checked per reconciliation sweep tick (throughput signal).");

            _reconciliationSweepDuration = _meter.CreateHistogram<double>(
                "novawallet.reconciliation.sweep.duration",
                unit: "ms",
                description: "Latency of a single ReconciliationWorker sweep tick.");

            _rateLimitRejections = _meter.CreateCounter<long>(
                "novawallet.ratelimit.rejections",
                unit: "{rejection}",
                description: "HTTP 429 rate-limit rejections, by request path.");
        }

        /// <summary>Records one POST /wallets/credit request outcome + its accept-path latency.</summary>
        public void RecordDepositRequest(string responseCode, TimeSpan elapsed)
        {
            var tag = new KeyValuePair<string, object?>("response_code", responseCode);
            _depositRequests.Add(1, tag);
            _depositRequestDuration.Record(elapsed.TotalMilliseconds, tag);
        }

        /// <summary>Increments the settlement-outcome counter for a DepositConsumer attempt.</summary>
        public void RecordDepositSettlement(string outcome)
        {
            _depositSettlements.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }

        /// <summary>Records the latency of a single DepositConsumer settlement attempt.</summary>
        public void RecordDepositSettlementDuration(string outcome, TimeSpan elapsed)
        {
            _depositSettlementDuration.Record(
                elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
        }

        /// <summary>Records one POST /wallets/transfer request outcome + its end-to-end latency.</summary>
        public void RecordTransfer(string responseCode, TimeSpan elapsed)
        {
            var tag = new KeyValuePair<string, object?>("response_code", responseCode);
            _transferRequests.Add(1, tag);
            _transferDuration.Record(elapsed.TotalMilliseconds, tag);
        }

        /// <summary>Records the settled amount (kobo) of a successful transfer.</summary>
        public void RecordTransferAmount(long amountKobo)
        {
            _transferAmountKobo.Record(amountKobo);
        }

        /// <summary>Records how many wallets a single reconciliation sweep batch checked.</summary>
        public void RecordWalletsChecked(int count)
        {
            if (count > 0)
            {
                _reconciliationWalletsChecked.Add(count);
            }
        }

        /// <summary>Records one wallet found unbalanced during a reconciliation sweep.</summary>
        public void RecordReconciliationDiscrepancy()
        {
            _reconciliationDiscrepancies.Add(1);
        }

        /// <summary>Records one wallet successfully auto-frozen due to a discrepancy.</summary>
        public void RecordWalletFrozen()
        {
            _reconciliationWalletsFrozen.Add(1);
        }

        /// <summary>Records the latency of a single ReconciliationWorker sweep tick.</summary>
        public void RecordReconciliationSweepDuration(TimeSpan elapsed)
        {
            _reconciliationSweepDuration.Record(elapsed.TotalMilliseconds);
        }

        /// <summary>Records one HTTP 429 rate-limit rejection for the given request path.</summary>
        public void RecordRateLimitRejection(string path)
        {
            _rateLimitRejections.Add(1, new KeyValuePair<string, object?>("path", path));
        }

        public void Dispose()
        {
            _meter.Dispose();
        }
    }
}
