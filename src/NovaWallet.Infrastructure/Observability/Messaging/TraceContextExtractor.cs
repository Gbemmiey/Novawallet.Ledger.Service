using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;

namespace NovaWallet.Infrastructure.Observability.Messaging
{
    /// <summary>
    /// Extracts distributed tracing context from inbound message headers and starts
    /// a linked consumer <see cref="Activity"/> that continues the upstream trace.
    /// </summary>
    public interface ITraceContextExtractor
    {
        Activity? StartConsumerActivity(IBasicProperties properties, string activityName, string sourceName);

        Activity? StartConsumerActivity(IReadOnlyBasicProperties properties, string activityName, string sourceName);

        Activity? StartConsumerActivity(IReadOnlyDictionary<string, object?>? headers, string activityName, string sourceName);

        Activity? StartConsumerActivity<TCarrier>(
            TCarrier carrier,
            Func<TCarrier, string, IEnumerable<string>> getter,
            string activityName,
            string sourceName);
    }

    /// <summary>
    /// Extracts distributed tracing context from inbound message headers and starts
    /// a linked consumer <see cref="Activity"/> that continues the upstream trace.
    /// </summary>
    /// <remarks>
    /// This class is designed for use in a shared class library consumed across multiple services
    /// and messaging workloads (e.g., RabbitMQ consumers).
    ///
    /// <para>
    /// It is intentionally optimized for high-throughput scenarios while preserving correctness
    /// and compatibility with OpenTelemetry propagation standards.
    /// </para>
    ///
    /// <para><b>Design goals:</b></para>
    /// <list type="bullet">
    /// <item>Maintain compatibility with OpenTelemetry <see cref="TextMapPropagator"/> standards</item>
    /// <item>Support multiple transport abstractions (RabbitMQ + generic carriers)</item>
    /// <item>Minimize allocations in message-processing hot paths</item>
    /// <item>Ensure consistent behavior across all consuming services</item>
    /// </list>
    /// </remarks>
    public sealed class TraceContextExtractor : ITraceContextExtractor
    {
        private static readonly TextMapPropagator Propagator =
            Propagators.DefaultTextMapPropagator;

        private readonly Dictionary<string, ActivitySource> _sources = new();

        /// <summary>
        /// Cached empty string sequence used to avoid repeated allocations in hot paths.
        /// </summary>
        private static readonly string[] Empty = Array.Empty<string>();

        /// <summary>
        /// Registers an <see cref="ActivitySource"/> for a given source name.
        /// </summary>
        /// <param name="sourceName">
        /// The name of the activity source. This must match the source used when starting activities.
        /// </param>
        /// <remarks>
        /// This method is typically invoked during application startup via the observability configuration
        /// (e.g., <c>ObservabilityBuilder.AddActivitySources</c>).
        /// </remarks>
        internal void RegisterSource(string sourceName)
        {
            if (!_sources.ContainsKey(sourceName))
                _sources[sourceName] = new ActivitySource(sourceName);
        }

        /// <summary>
        /// Starts a consumer <see cref="Activity"/> from RabbitMQ message properties.
        /// </summary>
        /// <param name="properties">The RabbitMQ message properties containing headers.</param>
        /// <param name="activityName">The logical name of the consumer operation (span name).</param>
        /// <param name="sourceName">The registered <see cref="ActivitySource"/> name.</param>
        /// <returns>
        /// A started <see cref="Activity"/> linked to the upstream trace if available,
        /// or <c>null</c> if sampling is disabled or no trace exists.
        /// </returns>
        public Activity? StartConsumerActivity(
            IBasicProperties properties,
            string activityName,
            string sourceName)
        {
            return StartConsumerActivity(
                ToReadOnly(properties?.Headers),
                activityName,
                sourceName);
        }

        /// <summary>
        /// Starts a consumer <see cref="Activity"/> from RabbitMQ message properties.
        /// </summary>
        /// <param name="properties">The RabbitMQ message properties containing headers.</param>
        /// <param name="activityName">The logical name of the consumer operation (span name).</param>
        /// <param name="sourceName">The registered <see cref="ActivitySource"/> name.</param>
        /// <returns>
        /// A started <see cref="Activity"/> linked to the upstream trace if available,
        /// or <c>null</c> if sampling is disabled or no trace exists.
        /// </returns>
        public Activity? StartConsumerActivity(
            IReadOnlyBasicProperties properties,
            string activityName,
            string sourceName)
        {
            return StartConsumerActivity(
                ToReadOnly(properties?.Headers),
                activityName,
                sourceName);
        }

        private static IReadOnlyDictionary<string, object?>? ToReadOnly(IDictionary<string, object?>? headers)
        {
            if (headers is null)
                return null;

            return headers as IReadOnlyDictionary<string, object?>
                   ?? new ReadOnlyDictionary<string, object?>(headers);
        }

        /// <summary>
        /// Starts a consumer <see cref="Activity"/> from a read-only header dictionary.
        /// </summary>
        /// <param name="headers">Message headers containing distributed trace context.</param>
        /// <param name="activityName">The logical name of the consumer operation (span name).</param>
        /// <param name="sourceName">The registered <see cref="ActivitySource"/> name.</param>
        /// <returns>
        /// A started <see cref="Activity"/> linked to the upstream trace if present,
        /// or <c>null</c> if sampling drops the span.
        /// </returns>
        /// <remarks>
        /// This is the primary high-performance overload used in message-processing pipelines.
        /// It avoids unnecessary allocations and works with modern RabbitMQ client APIs
        /// (<see cref="RabbitMQ.Client.ReadOnlyBasicProperties"/>).
        /// </remarks>
        public Activity? StartConsumerActivity(
            IReadOnlyDictionary<string, object?>? headers,
            string activityName,
            string sourceName)
        {
            var context = ExtractContext(headers);
            Baggage.Current = context.Baggage;

            if (!_sources.TryGetValue(sourceName, out var source))
                throw new InvalidOperationException(
                    $"ActivitySource '{sourceName}' is not registered.");

            return source.StartActivity(
                activityName,
                ActivityKind.Consumer,
                context.ActivityContext);
        }

        /// <summary>
        /// Starts a consumer <see cref="Activity"/> using a generic carrier abstraction.
        /// </summary>
        /// <typeparam name="TCarrier">The transport-specific message carrier type.</typeparam>
        /// <param name="carrier">The message or request containing trace headers.</param>
        /// <param name="getter">
        /// Function used to extract header values from the carrier.
        /// </param>
        /// <param name="activityName">The logical operation name (span name).</param>
        /// <param name="sourceName">The registered <see cref="ActivitySource"/> name.</param>
        /// <returns>
        /// A started <see cref="Activity"/> linked to the upstream trace if available.
        /// </returns>
        public Activity? StartConsumerActivity<TCarrier>(
            TCarrier carrier,
            Func<TCarrier, string, IEnumerable<string>> getter,
            string activityName,
            string sourceName)
        {
            var context = Propagator.Extract(default, carrier, getter);
            Baggage.Current = context.Baggage;

            if (!_sources.TryGetValue(sourceName, out var source))
                throw new InvalidOperationException(
                    $"ActivitySource '{sourceName}' is not registered.");

            return source.StartActivity(
                activityName,
                ActivityKind.Consumer,
                context.ActivityContext);
        }

        /// <summary>
        /// Extracts distributed tracing context from message headers using OpenTelemetry propagation.
        /// </summary>
        /// <param name="headers">The inbound message headers.</param>
        /// <returns>
        /// A <see cref="PropagationContext"/> containing the extracted <see cref="ActivityContext"/>
        /// and baggage information.
        /// </returns>
        /// <remarks>
        /// This method is optimized for high-throughput workloads:
        /// <list type="bullet">
        /// <item>avoids LINQ allocations</item>
        /// <item>uses cached empty arrays</item>
        /// <item>avoids unnecessary enumerator allocations</item>
        /// </list>
        /// </remarks>
        private static PropagationContext ExtractContext(
            IReadOnlyDictionary<string, object?>? headers)
        {
            if (headers is null || headers.Count == 0)
                return default;

            return Propagator.Extract(
                default,
                headers,
                HeaderGetter);
        }

        /// <summary>
        /// Efficient header value extractor used by the OpenTelemetry propagator.
        /// </summary>
        /// <param name="headers">The message headers dictionary.</param>
        /// <param name="key">The header key to retrieve.</param>
        /// <returns>
        /// A sequence of header values without unnecessary allocations.
        /// </returns>
        private static IEnumerable<string> HeaderGetter(
            IReadOnlyDictionary<string, object?> headers,
            string key)
        {
            if (!headers.TryGetValue(key, out var value) || value is null)
                return Empty;

            return value switch
            {
                byte[] bytes => DecodeSingle(bytes),
                string s => ReturnSingle(s),
                string[] arr => arr,
                IEnumerable<string> list => list,
                _ => Empty
            };
        }

        /// <summary>
        /// Returns a single string value as a deferred enumerable.
        /// </summary>
        private static IEnumerable<string> ReturnSingle(string value)
        {
            yield return value;
        }

        /// <summary>
        /// Decodes a UTF-8 byte array into a single string value.
        /// </summary>
        private static IEnumerable<string> DecodeSingle(byte[] bytes)
        {
            yield return Encoding.UTF8.GetString(bytes);
        }
    }
}