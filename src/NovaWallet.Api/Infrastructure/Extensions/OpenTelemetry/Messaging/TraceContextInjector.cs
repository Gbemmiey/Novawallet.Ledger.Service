using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text;

namespace NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry.Messaging
{
    /// <summary>
    /// Injects the current OpenTelemetry trace context into outbound message headers,
    /// enabling distributed trace propagation across message broker boundaries.
    /// </summary>
    public interface ITraceContextInjector
    {
        void Inject(IBasicProperties properties);

        void Inject<TCarrier>(TCarrier carrier, Action<TCarrier, string, string> setter);
    }

    /// <summary>
    /// Injects the current OpenTelemetry trace context into outbound message headers,
    /// enabling distributed trace propagation across message broker boundaries.
    /// </summary>
    public sealed class TraceContextInjector : ITraceContextInjector
    {
        private static readonly TextMapPropagator Propagator =
            Propagators.DefaultTextMapPropagator;

        /// <summary>
        /// Injects the current trace context into RabbitMQ message headers.
        /// Call immediately before <c>BasicPublish</c> on the producer side.
        /// </summary>
        /// <param name="properties">The RabbitMQ message properties to inject headers into.</param>
        public void Inject(IBasicProperties properties)
        {
            properties.Headers ??= new Dictionary<string, object?>();

            Propagator.Inject(
                new PropagationContext(Activity.Current?.Context ?? default, Baggage.Current),
                properties.Headers,
                static (headers, key, value) => headers[key] = Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        /// Generic overload for non-RabbitMQ transports (e.g. Kafka, Azure Service Bus).
        /// </summary>
        /// <typeparam name="TCarrier">The type of the message carrier.</typeparam>
        /// <param name="carrier">The carrier to inject headers into.</param>
        /// <param name="setter">A delegate that sets a header key/value on the carrier.</param>
        public void Inject<TCarrier>(TCarrier carrier, Action<TCarrier, string, string> setter)
        {
            Propagator.Inject(
                new PropagationContext(Activity.Current?.Context ?? default, Baggage.Current),
                carrier,
                setter);
        }
    }
}