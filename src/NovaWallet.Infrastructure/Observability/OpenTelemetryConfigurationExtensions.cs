using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaWallet.Application.Configuration;
using NovaWallet.Application.Observability;
using NovaWallet.Infrastructure.Options;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NovaWallet.Infrastructure.Observability
{
    /// <summary>
    /// Provides extension methods for configuring OpenTelemetry distributed tracing and metrics
    /// in a consistent way across web applications and worker services.
    /// </summary>
    /// <remarks>
    /// This class is internal and intended for use only within the observability extensions library.
    /// It registers resource attributes, instrumentation libraries, OTLP export, and filtering logic.
    /// </remarks>
    internal static class OpenTelemetryConfigurationExtensions
    {
        /// <summary>
        /// Configures OpenTelemetry tracing and metrics with OTLP export and common instrumentation.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add OpenTelemetry services to.</param>
        /// <param name="env">The host environment, used for resource attributes (e.g. deployment.environment).</param>
        /// <param name="observabilityOptions"></param>
        /// <param name="activitySources"></param>
        /// <returns>The same <see cref="IServiceCollection"/> instance for fluent chaining.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="services"/>, <paramref name="env"/> is null or empty.
        /// </exception>
        /// <remarks>
        /// This method:
        /// <list type="bullet">
        /// <item>Creates a resource builder with service name, version, environment, and host name</item>
        /// <item>Configures tracing with ASP.NET Core, HttpClient, EF Core, SQL, gRPC, RabbitMQ, and Redis instrumentation</item>
        /// <item>Filters out health checks, metrics, and Swagger endpoints from traces</item>
        /// <item>Configures metrics with runtime, process, ASP.NET Core, HttpClient, and SQL instrumentation + Prometheus exporter</item>
        /// <item>Exports both signals via OTLP to the specified collector</item>
        /// </list>
        ///
        /// Call this method during service registration (typically in <c>AddObservability</c> extensions).
        /// </remarks>
        public static IServiceCollection ConfigureDistributedTracingAndMetrics(this IServiceCollection services, IHostEnvironment env, ObservabilityOptions observabilityOptions, List<string>? activitySources = null)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (env == null) throw new ArgumentNullException(nameof(env));
            if (string.IsNullOrWhiteSpace(observabilityOptions.ApplicationName))
                throw new ArgumentException("Application name cannot be null or empty", nameof(observabilityOptions.ApplicationName));
            if (string.IsNullOrWhiteSpace(observabilityOptions.ExporterUri))
                throw new ArgumentException("OTLP exporter URI cannot be null or empty", nameof(observabilityOptions.ExporterUri));

            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(observabilityOptions.ApplicationName, serviceVersion: "1.0.0")
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment", env.EnvironmentName),
                    new KeyValuePair<string, object>("host.name", Environment.MachineName)
                });

            var openTelemetry = services.AddOpenTelemetry();

            // =========================
            // 🔭 TRACING
            // =========================
            openTelemetry.WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder
                    .SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = ctx => !IsExcludedPath(ctx.Request.Path);
                    })
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddSqlClientInstrumentation()
                    .AddGrpcClientInstrumentation()
                    .AddRabbitMQInstrumentation()
                    .AddNpgsql()
                    .AddRedisInstrumentation();
                //.AddRedisInstrumentation(
                //    services.BuildServiceProvider().GetRequiredService<IConnectionMultiplexer>()
                //);

                if (activitySources is { Count: > 0 })
                {
                    tracerProviderBuilder.AddSource(activitySources.ToArray());
                }

                tracerProviderBuilder.AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(observabilityOptions.ExporterUri);
                    options.Protocol = OtlpExportProtocol.Grpc;
                });
            });

            // =========================
            // 📊 METRICS
            // =========================
            openTelemetry.WithMetrics(metricProviderBuilder =>
            {
                metricProviderBuilder
                    .SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddSqlClientInstrumentation()
                    .AddMeter("Npgsql")
                    .AddMeter(NovaWalletMetrics.MeterName)
                    .AddProcessInstrumentation();

                // ✅ ALWAYS export metrics to OTEL Collector
                metricProviderBuilder.AddOtlpExporter(options =>
                        {
                            options.Endpoint = new Uri(observabilityOptions.ExporterUri);
                            options.Protocol = OtlpExportProtocol.Grpc;
                        });

                // ✅ ONLY expose Prometheus scrape endpoint if enabled
                if (observabilityOptions.EnablePrometheusMetricsEndpoint)
                {
                    metricProviderBuilder.AddPrometheusExporter();
                }
            });

            return services;
        }

        /// <summary>
        /// Determines whether a request path should be excluded from distributed tracing.
        /// </summary>
        /// <param name="path">The request path to check (e.g. "/health", "/metrics").</param>
        /// <returns><c>true</c> if the path matches an excluded endpoint; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Excluded paths include health checks, metrics endpoints, and Swagger UI to reduce noise in traces.
        /// Comparison is case-insensitive and uses StartsWith for flexibility.
        /// </remarks>
        private static bool IsExcludedPath(string path)
        {
            var excluded = NovaWalletConstants.SecurityMiddlewareExcludedPaths;
            return excluded.Any(e => path.StartsWith(e, StringComparison.OrdinalIgnoreCase));
        }
    }
}