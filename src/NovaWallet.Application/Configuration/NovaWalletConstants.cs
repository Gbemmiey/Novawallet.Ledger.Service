using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaWallet.Application.Configuration;

/// <summary>
/// Provides constant values used for payment gateway integrations.
/// </summary>
public static class NovaWalletConstants
{
    public static string CorsPolicyName { get; } = nameof(CorsPolicyName);

    public static string CurrencyCode { get; } = "NGN";

    public static string IdempotencyKey => "Idempotency-Key";

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
        public const string LoginPolicy = "LoginPolicy";
        public const string UserPolicy = "UserPolicy";
        public const string TransferPolicy = "TransferPolicy";
        public const string CreditPolicy = "CreditPolicy";
        public const string InternalAdminPolicy = "InternalAdminPolicy";

        // Header & Anonymous Fallbacks
        public const string FallbackPartitionKey = "anonymous";
    }

    public static class AuthorizationPolicyConstants
    {
        /// <summary>
        /// Requires the caller's JWT to carry a <c>role</c> claim of <c>Admin</c> (see
        /// <c>UserRole</c>/<c>MockUserAuthHelper</c>). Wired up in
        /// <c>AuthenticationExtensions.AddJwtAuthenticationAndAuthorization</c> and applied to
        /// the entire <c>/api/v1/admin</c> route group.
        /// </summary>
        public const string AdminOnly = "AdminOnly";
    }

    /// <summary>
    /// Default/ceiling values for paginated list endpoints (wallet statement, admin audit log).
    /// </summary>
    public static class PaginationConstants
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 100;
    }

    /// <summary>
    /// Well-known chart-of-accounts entries that exist independently of any single
    /// wallet. Unlike per-user liability accounts (created in <c>WalletService.CreateWallet</c>),
    /// these are shared, singleton ledger accounts resolved/created lazily by background
    /// workers on first use (see <c>Workers.DepositConsumer</c>).
    /// </summary>
    public static class SystemAccounts
    {
        /// <summary>
        /// Asset account debited for every settled NIP inbound credit; the offsetting
        /// credit lands on the specific beneficiary wallet's own liability account.
        /// </summary>
        public const string NipSettlementAccountNumber = "1000-NIP-SETTLEMENT";
    }

    /// <summary>
    /// Outbound inter-wallet transfer limits, enforced atomically via the
    /// <c>WalletDailyUsage</c> guarded UPSERT in <c>TransferService</c> (README §6).
    /// The usage bucket is always the WAT (Africa/Lagos) calendar day.
    /// </summary>
    public static class TransferLimits
    {
        /// <summary>₦500,000/day, expressed in kobo.</summary>
        public const long DailyOutboundLimitKobo = 50_000_000;
    }
}