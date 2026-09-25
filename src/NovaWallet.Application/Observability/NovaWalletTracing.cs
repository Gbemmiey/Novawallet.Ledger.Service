using System.Diagnostics;

namespace NovaWallet.Application.Observability
{
    /// <summary>
    /// Central home for NovaWallet's custom tracing, built on the BCL's <see cref="ActivitySource"/>
    /// (no extra NuGet package). The source is registered with the OTel tracer provider via
    /// <see cref="SourceName"/> in <see cref="ObservabilityExtensions.AddObservability"/>; spans
    /// started here flow through the same OTLP exporter as the ASP.NET Core/EF Core/Npgsql spans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists - the outbox gap:</b> an inbound credit is accepted by
    /// <c>DepositService</c> on an HTTP request, then settled later by the polling
    /// <c>DepositConsumer</c>. Nothing carries trace context across the database, so without help
    /// the settlement would start an unrelated trace. <see cref="CurrentTraceParent"/> captures the
    /// W3C <c>traceparent</c> at accept time (stored on <c>DepositOutbox.TraceParent</c>), and
    /// <see cref="StartConsumerActivity"/> uses it as the parent of the settlement span, giving one
    /// continuous trace: <c>POST /credit</c> -> <c>deposit.accept</c> -> <c>deposit.settle</c> ->
    /// its child steps.
    /// </para>
    /// <para>
    /// <b>Attributes:</b> unlike metrics, spans have no cardinality constraint, so identifiers such
    /// as <c>deposit.session_id</c>, <c>deposit.transaction_reference</c> and <c>wallet.id</c> are
    /// fine here (search by them in OpenObserve). Account numbers are still deliberately never
    /// attached.
    /// </para>
    /// </remarks>
    public static class NovaWalletTracing
    {
        public const string SourceName = "NovaWallet.Deposits";

        public static readonly ActivitySource Source = new(SourceName, "1.0.0");

        /// <summary>
        /// Returns the ambient activity's W3C <c>traceparent</c> string for storage alongside a
        /// row that will be processed later, or <see langword="null"/> if there is no ambient
        /// activity or it isn't W3C-formatted.
        /// </summary>
        public static string? CurrentTraceParent()
        {
            var current = Activity.Current;

            return current is { IdFormat: ActivityIdFormat.W3C } ? current.Id : null;
        }

        /// <summary>
        /// Starts a <see cref="ActivityKind.Consumer"/> span parented on a stored
        /// <c>traceparent</c>. If the value is missing or unparseable (e.g. rows written before the
        /// column existed) the span simply starts a new trace instead. Returns
        /// <see langword="null"/> when nothing is listening to <see cref="SourceName"/>.
        /// </summary>
        public static Activity? StartConsumerActivity(string name, string? traceParent)
        {
            if (!string.IsNullOrWhiteSpace(traceParent)
                && ActivityContext.TryParse(traceParent, null, out var parentContext))
            {
                return Source.StartActivity(name, ActivityKind.Consumer, parentContext);
            }

            return Source.StartActivity(name, ActivityKind.Consumer);
        }

        /// <summary>
        /// Marks the span as failed and attaches the exception using the OTel semantic-convention
        /// <c>exception</c> event (<c>Activity.AddException</c> is .NET 9+ only, so it's done by hand).
        /// </summary>
        public static void RecordException(Activity? activity, Exception exception)
        {
            if (activity is null)
                return;

            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", exception.GetType().FullName },
                { "exception.message", exception.Message },
                { "exception.stacktrace", exception.ToString() }
            }));
        }
    }
}
