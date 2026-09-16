using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaWallet.Api.Core.Configuration;

/// <summary>
/// Provides constant values used for payment gateway integrations.
/// </summary>
public static class NovaWalletConstants
{
    public static string CorsPolicyName { get; } = nameof(CorsPolicyName);

    public static string IdempotencyKey => "X-Idempotency-Key";

    /// <summary>
    /// Gets the JSON serializer options configured for the payment switch.
    /// </summary>
    public static JsonSerializerOptions JsonSerializerOptions { get; } = new JsonSerializerOptions()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    /// <summary>
    /// Gets the list of request paths that are excluded from security middleware processing. Requests to these paths will bypass authentication and authorization checks, allowing for public access to endpoints such as health checks, metrics, and API documentation.
    /// </summary>
    public static readonly string[] SecurityMiddlewareExcludedPaths =
    {
                "/swagger",
                "/health",
                "/metrics",
                "/scalar"
    };

    public static class RateLimitingConstants
    {
        public const string PerPartnerPolicy = "PerPartnerPolicy";
        public const string InternalAdminPolicy = "InternalAdminPolicy";

        // Header & Anonymous Fallbacks
        public const string FallbackPartitionKey = "anonymous";
    }

    public static class AuthorizationPolicyConstants
    {
        public const string PartnerOnly = "PartnerOnly";
    }
}