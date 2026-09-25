using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Infrastructure.Options
{
    /// <summary>
    /// Strongly-typed configuration options for the observability setup.
    /// These options control Serilog logging behavior, OpenTelemetry OTLP export,
    /// and related observability features.
    /// </summary>
    /// <remarks>
    /// This class is bound directly from the "Observability" section in configuration files
    /// (e.g. appsettings.json). Required fields are validated using data annotations
    /// and runtime checks in the extension methods.
    ///
    /// Typical configuration example:
    /// <code>
    /// {
    ///   "Observability": {
    ///     "ApplicationName": "MyService",
    ///     "ExporterUri": "http://otel-collector:4317",
    ///     "ExportProtocol": "grpc",
    ///     "LogFilePath": "./logs",
    ///     "EnableTextLog": true,
    ///     "EnableJsonLog": false,
    ///     "MicrosoftLogLevel": "Warning"
    ///   }
    /// }
    /// </code>
    /// </remarks>
    public class ObservabilityOptions
    {
        /// <summary>
        /// Gets or sets the name of the application or service.
        /// This value is used as the <c>service.name</c> resource attribute in OpenTelemetry
        /// and as a property in Serilog logs.
        /// </summary>
        /// <remarks>
        /// This field is **required**. It should be unique and stable across deployments
        /// (e.g. "OrderService", "PaymentProcessor").
        /// </remarks>
        [Required(ErrorMessage = "ApplicationName is required")]
        public string ApplicationName { get; set; } = null!;

        /// <summary>
        /// Gets or sets the OTLP exporter endpoint URL where traces, metrics, and logs are sent.
        /// Examples: "http://localhost:4317" (gRPC), "http://localhost:4318/v1/traces" (HTTP).
        /// </summary>
        /// <remarks>
        /// This field is **required**. It must be a valid absolute URI pointing to an
        /// OpenTelemetry collector or compatible backend (e.g. Grafana Tempo, Jaeger, Loki).
        /// </remarks>
        public string? ExporterUri { get; set; } = null!;

        /// <summary>
        /// Gets or sets the OTLP export protocol to use.
        /// Supported values: "grpc" (default), "http", "http/protobuf".
        /// </summary>
        /// <remarks>
        /// Most collectors support both protocols. gRPC is generally more efficient,
        /// while HTTP/Protobuf may be preferred in environments with strict proxy/firewall rules.
        /// </remarks>
        public string ExportProtocol { get; set; } = "grpc";

        /// <summary>
        /// Gets or sets the base directory path where log files are written.
        /// </summary>
        /// <remarks>
        /// Default value is a "logs" folder in the application's current working directory.
        /// Use relative paths (e.g. "./logs") or absolute paths depending on your hosting environment
        /// (containers, VMs, etc.). Ensure the application has write permissions to this location.
        /// </remarks>
        public string LogFilePath { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs");

        /// <summary>
        /// Gets or sets a value indicating whether plain text rolling log files should be written.
        /// Files are named "log-.txt" and roll daily.
        /// </summary>
        /// <remarks>
        /// Default: <c>true</c>.
        /// Use this for human-readable logs during development or when a log shipper prefers text format.
        /// </remarks>
        public bool EnableTextLog { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether structured JSON rolling log files should be written.
        /// Files are named "log-.json" and roll daily.
        /// </summary>
        /// <remarks>
        /// Default: <c>false</c>.
        /// JSON logs are useful for structured log ingestion (Loki, ELK, Splunk, etc.).
        /// </remarks>
        public bool EnableJsonLog { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether logs should be displayed to the console.
        /// </summary>
        /// <remarks>
        /// Honored regardless of <c>ASPNETCORE_ENVIRONMENT</c> — this is the sole on/off switch
        /// for the console sink (it previously also required
        /// <c>IHostEnvironment.IsDevelopment()</c>, which meant container log visibility was
        /// accidentally coupled to running in "Development" mode; see
        /// <see cref="StartupOptions.ApplyMigrationsOnStartup"/> for the same
        /// decouple-from-environment fix applied to migrations).
        /// </remarks>
        public bool EnableConsoleLog { get; set; } = false;

        /// <summary>
        /// Gets or sets the minimum log level for Microsoft.* and ASP.NET Core categories.
        /// </summary>
        /// <remarks>
        /// Allowed values (case-insensitive): Verbose, Debug, Information, Warning, Error, Fatal.
        /// Default: "Warning" (recommended to reduce noise from framework logs).
        /// </remarks>
        public string MicrosoftLogLevel { get; set; } = "Warning";

        /// <summary>
        /// Gets or sets a value indicating whether a prometheus endpoint should be enabled.
        /// </summary>
        public bool EnablePrometheusMetricsEndpoint { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether the application should expose a health check endpoint.
        /// </summary>
        public bool EnableHealthCheckEndpoint { get; set; } = true;

        /// <summary>
        /// Gets or sets the list of property names that should be masked in logs
        /// to prevent sensitive data leakage.
        /// </summary>
        /// <remarks>
        /// Property matching is case-insensitive.
        /// Default list includes common sensitive fields.
        /// Add more entries (e.g. "CreditCard", "SSN") for your domain-specific secrets.
        /// </remarks>
        public List<string> SensitivePropertyNames { get; set; } = new()
        {
            "Password", "Token", "ApiKey", "Secret", "Authorization",
            "AccessToken", "RefreshToken", "ConnectionString"
        };

        /// <summary>
        /// Gets or sets the maximum size (in bytes) of each log file before rolling.
        /// </summary>
        /// <remarks>
        /// When set, files roll on size limit even within the same day.
        /// <c>null</c> = no size-based rolling (default behavior).
        /// </remarks>
        public long? LogFileSizeLimitBytes { get; set; } = 1073741824L;

        /// <summary>
        /// Gets or sets the maximum number of days to retain log files.
        /// </summary>
        /// <remarks>
        /// Default: 31 days.
        /// <c>null</c> = retain files indefinitely (not recommended in production).
        /// </remarks>
        public int? RetainedFileCountLimit { get; set; } = null;

        /// <summary>
        /// Gets or sets the logging level overrides for specific logger categories or sources.
        /// </summary>
        /// <remarks>
        /// The dictionary key represents the logger category/source name, and the value represents
        /// the minimum log level to apply for that category.
        /// </remarks>
        public Dictionary<string, string> LoggerLevelOverrides { get; set; } = new();
    }
}