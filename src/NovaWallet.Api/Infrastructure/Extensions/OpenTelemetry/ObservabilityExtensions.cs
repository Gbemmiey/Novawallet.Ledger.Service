using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NovaWallet.Api.Core.Options;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry.Messaging;
using Serilog;
using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry
{
    /// <summary>
    /// Provides extension methods for configuring observability features (logging with Serilog,
    /// distributed tracing and metrics with OpenTelemetry) in both web applications and worker services.
    /// </summary>
    public static class ObservabilityExtensions
    {
        private static string HealthCheckEndpoint { get; } = "/health";

        // ── Web Applications ────────────────────────────────────────────────────────

        /// <summary>
        /// Configures observability services (Serilog logging and OpenTelemetry tracing/metrics)
        /// for an ASP.NET Core web application using the "Observability" configuration section.
        /// </summary>
        /// <param name="builder">The <see cref="WebApplicationBuilder"/> to configure.</param>
        /// <param name="configure">Optional delegate to override or customize the bound <see cref="ObservabilityOptions"/>.</param>
        /// <returns>The same <see cref="WebApplicationBuilder"/> for fluent chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the "Observability" configuration section is missing or required fields are invalid.
        /// </exception>
        /// <remarks>
        /// Web applications do not require ActivitySources — ASP.NET Core instrumentation
        /// is configured automatically.
        /// <code>
        /// var builder = WebApplication.CreateBuilder(args);
        /// builder.AddObservability();
        /// </code>
        /// </remarks>
        public static WebApplicationBuilder AddObservability(
            this WebApplicationBuilder builder,
            Action<ObservabilityOptions>? configure = null)
        {
            var options = GetAndValidateOptions(builder.Configuration, configure);
            RegisterCoreTraceContextDependencies(builder.Services);
            ConfigureWebObservability(builder, options);
            return builder;
        }

        // ── Middleware Pipeline (Web only) ──────────────────────────────────────────

        /// <summary>
        /// Adds observability-related middleware to the HTTP request pipeline.
        /// Includes the health check endpoint, Prometheus metrics scraping, and Serilog request logging.
        /// </summary>
        /// <param name="app">The <see cref="WebApplication"/> to configure.</param>
        /// <returns>The same <see cref="WebApplication"/> for fluent chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="app"/> is null.</exception>
        /// <remarks>
        /// Call this early in the middleware pipeline, immediately after <c>builder.Build()</c>.
        /// </remarks>
        public static WebApplication UseObservabilityMiddleware(this WebApplication app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            var options = app.Services.GetRequiredService<ObservabilityOptions>();

            if (options.EnableHealthCheckEndpoint)
            {
                app.MapHealthChecks(HealthCheckEndpoint, new HealthCheckOptions
                {
                    ResultStatusCodes =
                    {
                        [HealthStatus.Healthy] = StatusCodes.Status200OK,
                        [HealthStatus.Degraded] = StatusCodes.Status200OK,
                        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
                    }
                }).AllowAnonymous();
            }

            // =========================
            // 📊 Prometheus (conditional)
            // =========================
            if (options.EnablePrometheusMetricsEndpoint)
            {
                app.UseOpenTelemetryPrometheusScrapingEndpoint();
            }

            app.UseSerilogRequestLogging();

            return app;
        }

        #region Private Helpers

        private static void ConfigureWebObservability(WebApplicationBuilder builder, ObservabilityOptions options)
        {
            var env = builder.Environment;
            builder.Services.AddSingleton(options);

            // OTel first — TracerProvider must exist before Serilog enrichers run
            builder.Services.ConfigureDistributedTracingAndMetrics(
                env: env,
                observabilityOptions: options,
                activitySources: new List<string>());

            Log.Logger = new LoggerConfiguration()
                .ConfigureApiLogging(options, env)
                .Enrich.WithProperty("service.name", options.ApplicationName)
                .Enrich.WithProperty("deployment.environment", env.EnvironmentName)
                .CreateLogger();

            builder.Host.UseSerilog();

            if (options.EnableHealthCheckEndpoint)
            {
                builder.Services.AddHealthChecks();
            }
        }

        /// <summary>
        /// Configures trace context propagation and injection.
        /// </summary>
        /// <param name="services"></param>
        private static void RegisterCoreTraceContextDependencies(IServiceCollection services)
        {
            services.AddSingleton<ITraceContextInjector, TraceContextInjector>();

            services.AddSingleton<TraceContextExtractor>();
            services.AddSingleton<ITraceContextExtractor>(sp => sp.GetRequiredService<TraceContextExtractor>());
        }

        private static ObservabilityOptions GetAndValidateOptions(
            IConfiguration configuration,
            Action<ObservabilityOptions>? configure)
        {
            var options = configuration
                .GetSection(nameof(ObservabilityOptions))
                .Get<ObservabilityOptions>()
                ?? throw new InvalidOperationException(
                    "Observability configuration section is missing or invalid.");

            configure?.Invoke(options);

            var validationContext = new ValidationContext(options);
            var validationResults = new List<ValidationResult>();

            if (!Validator.TryValidateObject(options, validationContext, validationResults, validateAllProperties: true))
            {
                var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
                throw new InvalidOperationException($"Observability configuration is invalid: {errors}");
            }

            return options;
        }

        #endregion Private Helpers
    }
}