using Microsoft.Extensions.Hosting;
using NovaWallet.Application.Configuration;
using NovaWallet.Infrastructure.Options;
using Serilog;
using Serilog.Enrichers.OpenTelemetry;
using Serilog.Enrichers.Sensitive;
using Serilog.Enrichers.Span;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Exceptions.Core;
using Serilog.Exceptions.EntityFrameworkCore.Destructurers;
using Serilog.Formatting.Json;
using Serilog.Sinks.OpenTelemetry;

namespace NovaWallet.Infrastructure.Observability
{
    /// <summary>
    /// Provides extension methods for configuring Serilog in a standardized way
    /// for applications using the observability library.
    /// </summary>
    /// <remarks>
    /// This class is internal and intended only for use within the observability extensions.
    /// It handles log level overrides, filtering, enrichment, file sinks, and OTLP log export.
    /// </remarks>
    internal static class SerilogConfigurationExtensions
    {
        /// <summary>
        /// The default output template used for plain text log files.
        /// Includes timestamp, level, request ID, trace ID, source context, Client-ID, message, and exception.
        /// </summary>
        public static string API_LOG_TEMPLATE { get; } =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{RequestId}] TraceId: {TraceId} [{SourceContext}] {Message:lj} {NewLine} {Exception}";

        /// <summary>
        /// The default output template used for plain text log files.
        /// Includes timestamp, level, request ID, trace ID, source context, Client-ID, message, and exception.
        /// </summary>
        public static string WORKER_LOG_TEMPLATE { get; } =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] TraceId: {TraceId} [{SourceContext}] {Message:lj} {NewLine} {Exception}";

        /// <summary>
        /// Configures a Serilog <see cref="LoggerConfiguration"/> with standardized settings
        /// based on the provided observability options.
        /// </summary>
        /// <param name="lc">The <see cref="LoggerConfiguration"/> to configure (fluent interface).</param>
        /// <param name="options">The bound observability configuration options.</param>
        /// <param name="environment">The host environment, used for enrichment (e.g. environment name).</param>
        /// <returns>The configured <see cref="LoggerConfiguration"/> for fluent chaining.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="lc"/> or <paramref name="options"/> is null.
        /// </exception>
        /// <remarks>
        /// This method applies:
        /// <list type="bullet">
        /// <item>Minimum level overrides for Microsoft and ASP.NET Core namespaces</item>
        /// <item>Exclusion of health/metrics endpoint logs</item>
        /// <item>OpenTelemetry trace/span enrichment</item>
        /// <item>Sensitive data masking</item>
        /// <item>File sinks (text and/or JSON) based on options</item>
        /// <item>OTLP log export to the configured collector</item>
        /// </list>
        /// </remarks>
        public static LoggerConfiguration ConfigureApiLogging(
            this LoggerConfiguration lc,
            ObservabilityOptions options,
            IHostEnvironment environment)
        {
            if (lc == null) throw new ArgumentNullException(nameof(lc));
            if (options == null) throw new ArgumentNullException(nameof(options));

            // Minimum level overrides

            lc.MinimumLevel.Override("Microsoft", ParseLogLevel(options.MicrosoftLogLevel))
                .MinimumLevel.Override("Microsoft.AspNetCore", ParseLogLevel(options.MicrosoftLogLevel));

            // Apply custom logger level overrides
            foreach (var loggerOverride in options.LoggerLevelOverrides)
            {
                lc.MinimumLevel.Override(
                    loggerOverride.Key,
                    ParseLogLevel(loggerOverride.Value));
            }

            // Exclude noisy health/metrics endpoints from logging
            lc.Filter.ByExcluding(logEvent =>
            {
                if (!logEvent.Properties.ContainsKey("RequestPath"))
                    return false;

                var path = logEvent.Properties["RequestPath"].ToString();

                // Excluded paths are often quoted (e.g. "\"/metrics\""), so use Contains for simplicity
                // Use the ExcludedPaths list for maintainability and to avoid hardcoding paths in multiple places

                return NovaWalletConstants.SecurityMiddlewareExcludedPaths.Any(excludedPath =>
                    path.Contains(excludedPath, StringComparison.OrdinalIgnoreCase));
            })

                // OpenTelemetry integration enrichers
                .Enrich.WithOpenTelemetryTraceId()
                .Enrich.WithOpenTelemetrySpanId()

                // Detailed exception destructuring (including DbUpdateException support)
                .Enrich.WithExceptionDetails(new DestructuringOptionsBuilder()
                    .WithDefaultDestructurers()
                    .WithDestructurers(new[] { new DbUpdateExceptionDestructurer() }))

                // Standard enrichers
                .Enrich.FromLogContext()
                .Enrich.WithCorrelationId()
                .Enrich.WithClientIp()
                .Enrich.WithMachineName()

                // Span enrichment (requires Serilog.Enrichers.Span)
                .Enrich.WithSpan()

                // Mask sensitive properties (configurable via options)
                .Enrich.WithSensitiveDataMasking(opts =>
                {
                    opts.MaskingOperators.Clear();

                    opts.MaskProperties = options.SensitivePropertyNames
                        .Select(name => new MaskProperty { Name = name })
                        .ToList();
                });

            if (options.EnableConsoleLog)
            {
                lc.WriteTo.Console(outputTemplate: API_LOG_TEMPLATE);
            }

            // Text file sink (daily rolling)
            if (options.EnableTextLog)
            {
                lc.WriteTo.Async(a => a.File(
                    path: Path.Combine(options.LogFilePath, "log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: API_LOG_TEMPLATE,
                    rollOnFileSizeLimit: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: options.LogFileSizeLimitBytes
                ));
            }

            // JSON file sink (daily rolling)
            if (options.EnableJsonLog)
            {
                lc.WriteTo.Async(a => a.File(
                    path: Path.Combine(options.LogFilePath, "log-.json"),
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    formatter: new JsonFormatter(renderMessage: true),
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    // Retention settings
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: options.LogFileSizeLimitBytes
               ));
            }

            // OTLP sink for sending logs to an OpenTelemetry collector
            if (!string.IsNullOrWhiteSpace(options.ExporterUri))
            {
                lc.WriteTo.OpenTelemetry(otel =>
                {
                    otel.Endpoint = new Uri(options.ExporterUri ?? string.Empty).ToString();
                    otel.Protocol = ParseOtlpProtocol(options.ExportProtocol);
                    otel.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = options.ApplicationName,
                        ["deployment.environment"] = environment.EnvironmentName,
                        ["host.name"] = Environment.MachineName
                    };
                });
            }

            return lc;
        }

        /// <summary>
        /// Parses a string representation of a Serilog log level into a <see cref="LogEventLevel"/>.
        /// </summary>
        /// <param name="level">The log level string (case-insensitive, e.g. "Warning", "Information").</param>
        /// <returns>The parsed <see cref="LogEventLevel"/>, or <see cref="LogEventLevel.Warning"/> if parsing fails.</returns>
        private static LogEventLevel ParseLogLevel(string level) =>
            Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var lvl)
                ? lvl
                : LogEventLevel.Warning;

        /// <summary>
        /// Parses the OTLP export protocol string into the corresponding <see cref="OtlpProtocol"/> value.
        /// </summary>
        /// <param name="protocol">The protocol string ("http", "http/protobuf", or other values default to gRPC).</param>
        /// <returns>The matching <see cref="OtlpProtocol"/> (HttpProtobuf or Grpc).</returns>
        private static OtlpProtocol ParseOtlpProtocol(string? protocol) =>
            protocol?.ToLowerInvariant() switch
            {
                "http" or "http/protobuf" => OtlpProtocol.HttpProtobuf,
                _ => OtlpProtocol.Grpc
            };
    }
}