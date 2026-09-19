using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NovaWallet.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API (real pipeline, real EF Core, real background workers) against a test database.
///
/// Configuration is passed as process environment variables, not through
/// <c>ConfigureAppConfiguration</c>: <c>Program.cs</c> reads several values (the connection string,
/// observability options, rate-limit limits) while it is still registering services, which
/// happens before a factory's configuration callbacks are applied. Environment variables are part
/// of <c>WebApplication.CreateBuilder</c>'s own sources, so they are always visible. The previous
/// values are restored on dispose.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string JwtSecret = "integration-test-secret-key-that-is-at-least-32-bytes";
    public const string JwtIssuer = "novawallet-integration-tests";
    public const string JwtAudience = "novawallet-integration-tests";

    private readonly Dictionary<string, string?> _previousValues = new();

    public ApiFactory(string connectionString, IReadOnlyDictionary<string, string> overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            // The deployed API runs as Production (docker-compose.yml). It matters beyond
            // appsettings: in Development, minimal APIs throw on a malformed body, and the global
            // exception middleware would turn what is a plain 400 in production into a 500.
            ["ASPNETCORE_ENVIRONMENT"] = "Production",

            ["NOVAWALLET_LEDGER_CONNECTION_STRING"] = connectionString,

            ["Jwt__SecretKey"] = JwtSecret,
            ["Jwt__Issuer"] = JwtIssuer,
            ["Jwt__Audience"] = JwtAudience,

            // Limits are far above anything a test sends, so bursts are never throttled unless
            // a fixture deliberately lowers them. Credit 0 means unlimited.
            ["RateLimiting__LoginPermitLimit"] = "1000000",
            ["RateLimiting__UserPermitLimit"] = "1000000",
            ["RateLimiting__TransferPermitLimit"] = "1000000",
            ["RateLimiting__CreditPermitLimit"] = "0",

            // The deposit consumer is what settles credits, so it stays on and polls every second.
            ["DepositConsumer__PollingIntervalSeconds"] = "1",

            // Reconciliation only sweeps once at start-up (an empty database) unless a fixture
            // asks for a fast poll - otherwise it could freeze a wallet in the middle of a test.
            ["ReconciliationWorker__PollingIntervalSeconds"] = "86400",

            // No Redis: HybridCache falls back to in-memory.
            ["REDIS_URI"] = null,

            // Quiet telemetry: no log files or console noise. The OTLP exporter still points at
            // the default localhost collector, which is absent, and fails silently.
            ["ObservabilityOptions__EnableTextLog"] = "false",
            ["ObservabilityOptions__EnableJsonLog"] = "false",
            ["ObservabilityOptions__EnableConsoleLog"] = "false",
            ["ObservabilityOptions__EnablePrometheusMetricsEndpoint"] = "false",
            ["Swagger__DisplaySwagger"] = "false"
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        foreach (var (key, value) in settings)
        {
            _previousValues[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // WebApplicationFactory defaults to Development; override it (see ASPNETCORE_ENVIRONMENT above).
        builder.UseEnvironment("Production");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (var (key, value) in _previousValues)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _previousValues.Clear();
    }
}
