using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaWallet.Api.Core.Options;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry;

namespace NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry.Messaging
{
    /// <summary>
    /// Fluent builder returned by worker/hosted service AddObservability overloads.
    /// Allows registering ActivitySources after initial service registration,
    /// before the OTel TracerProvider is built.
    /// </summary>
    public sealed class ObservabilityBuilder
    {
        private readonly IServiceCollection _services;
        private readonly ObservabilityOptions _options;
        private readonly IHostEnvironment _env;
        internal readonly List<string> ActivitySources = new();

        internal ObservabilityBuilder(
            IServiceCollection services,
            ObservabilityOptions options,
            IHostEnvironment env)
        {
            _services = services;
            _options = options;
            _env = env;
        }

        /// <summary>
        /// Registers one or more ActivitySource names with the OpenTelemetry tracer provider
        /// and the TraceContextExtractor. Each name must match the sourceName argument
        /// passed to TraceContextExtractor.StartConsumerActivity() in your consumers.
        /// </summary>
        public ObservabilityBuilder AddActivitySources(params string[] sourceNames)
        {
            ActivitySources.AddRange(sourceNames);

            // Register sources on the extractor
            _services.AddSingleton<IHostedService>(sp =>
            {
                var extractor = sp.GetRequiredService<TraceContextExtractor>();
                foreach (var name in sourceNames)
                    extractor.RegisterSource(name);

                return new ObservabilityStartup(_services, _options, _env, ActivitySources);
            });

            return this;
        }

        /// <summary>
        /// Finalizes OTel registration. Called internally if AddActivitySources()
        /// is never called (e.g. worker with no custom sources).
        /// </summary>
        internal void Build()
        {
            _services.AddSingleton(_options);
            _services.ConfigureDistributedTracingAndMetrics(_env, _options, ActivitySources);
        }
    }

    /// <summary>
    /// Deferred hosted service that finalizes OTel TracerProvider configuration
    /// after all AddActivitySources() calls have been made.
    /// </summary>
    internal sealed class ObservabilityStartup : IHostedService
    {
        private readonly IServiceCollection _services;
        private readonly ObservabilityOptions _options;
        private readonly IHostEnvironment _env;
        private readonly List<string> _activitySources;

        public ObservabilityStartup(
            IServiceCollection services,
            ObservabilityOptions options,
            IHostEnvironment env,
            List<string>? activitySources)
        {
            _services = services;
            _options = options;
            _env = env;
            _activitySources = activitySources ?? new List<string>();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // No-op: OpenTelemetry is configured during service registration by the
            // AddObservability/IHostBuilder flow. The hosted startup exists only to
            // ensure ActivitySource names have been registered on the extractor (the
            // extractor registration happens in the factory that creates this hosted
            // service). Avoid re-configuring OpenTelemetry here to prevent double
            // registration.
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}