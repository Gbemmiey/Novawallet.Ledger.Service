using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Application.Observability;
using NovaWallet.Application.Response;
using NovaWallet.Domain.Responses;
using NovaWallet.Infrastructure.Http;
using NovaWallet.Infrastructure.Options;
using System.Globalization;
using System.Threading.RateLimiting;
using static NovaWallet.Application.Configuration.NovaWalletConstants;

namespace NovaWallet.Infrastructure.Extensions
{
    public static class RateLimiterServiceExtensions
    {
        public const string ConfigurationSectionName = "RateLimiting";

        /// <summary>
        /// Registers the named rate-limiting policies. Limits and windows come from
        /// <see cref="RateLimitingOptions"/> (section "RateLimiting"), validated on startup.
        /// </summary>
        public static IServiceCollection RegisterRateLimitingPolicies(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(ConfigurationSectionName);

            services
                .AddOptions<RateLimitingOptions>()
                .Bind(section)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The AddRateLimiter callback has no service provider, so read a bound snapshot here.
            // Validation of these same values is enforced at startup by the options registration above.
            var limits = section.Get<RateLimitingOptions>() ?? new RateLimitingOptions();

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.OnRejected = async (context, cancellationToken) =>
                {
                    // Tagged by request path rather than the matched policy name -
                    // OnRejectedContext doesn't expose the policy name directly, and path is a
                    // clean, bounded substitute across today's handful of rate-limited routes.
                    var metrics = context.HttpContext.RequestServices.GetRequiredService<NovaWalletMetrics>();
                    metrics.RecordRateLimitRejection(context.HttpContext.Request.Path.Value ?? "unknown");

                    // RFC 6585 / 9110: tell the client when to retry. The sliding-window limiter
                    // supplies RETRY_AFTER metadata on a rejected lease; fall back to the longest
                    // configured window if it is ever absent.
                    var fallbackSeconds = new[]
                    {
                        limits.LoginWindowSeconds,
                        limits.UserWindowSeconds,
                        limits.TransferWindowSeconds,
                        limits.CreditWindowSeconds
                    }.Max();

                    var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                        ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        : fallbackSeconds;
                    context.HttpContext.Response.Headers.RetryAfter =
                        retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                    context.HttpContext.Response.ContentType = "application/json";

                    var response = ServiceApiResponse<object>.CreateFailure(
                        code: ResponseCodes.TooManyRequests.ResponseCode,
                        message: "Rate limit exceeded. Too many requests sent in a short period.");

                    await context.HttpContext.Response.WriteAsJsonAsync(response, cancellationToken);
                };

                // 1. Internal / Admin Policy (Partitioned by IP). Effectively unlimited today.
                options.AddPolicy(RateLimitingConstants.InternalAdminPolicy, httpContext =>
                    SlidingWindow(
                        $"admin_ip:{ClientIp(httpContext)}",
                        int.MaxValue,
                        TimeSpan.FromMinutes(1)));

                // 2. Login policy - partitioned by client IP ONLY. The request body (and any userId
                // in it) is never read, so a caller cannot get a fresh bucket by inventing user IDs.
                options.AddPolicy(RateLimitingConstants.LoginPolicy, httpContext =>
                    SlidingWindow(
                        $"login_ip:{ClientIp(httpContext)}",
                        limits.LoginPermitLimit,
                        TimeSpan.FromSeconds(limits.LoginWindowSeconds)));

                // 3. General authenticated-user policy (wallet create / fetch / statement).
                // Partitioned by the JWT `sub` claim; UseAuthorization() runs before UseRateLimiter().
                options.AddPolicy(RateLimitingConstants.UserPolicy, httpContext =>
                    SlidingWindow(
                        $"user:{UserKey(httpContext)}",
                        limits.UserPermitLimit,
                        TimeSpan.FromSeconds(limits.UserWindowSeconds)));

                // 4. Transfer policy - its own budget, separate from UserPolicy so transfer
                // limits can be tuned independently. Partitioned by the JWT `sub` claim.
                options.AddPolicy(RateLimitingConstants.TransferPolicy, httpContext =>
                    SlidingWindow(
                        $"transfer:{UserKey(httpContext)}",
                        limits.TransferPermitLimit,
                        TimeSpan.FromSeconds(limits.TransferWindowSeconds)));

                // 5. Credit webhook policy - anonymous route, so partitioned by client IP.
                // CreditPermitLimit == 0 means unlimited.
                options.AddPolicy(RateLimitingConstants.CreditPolicy, httpContext =>
                    limits.CreditPermitLimit == 0
                        ? RateLimitPartition.GetNoLimiter($"credit_ip:{ClientIp(httpContext)}")
                        : SlidingWindow(
                            $"credit_ip:{ClientIp(httpContext)}",
                            limits.CreditPermitLimit,
                            TimeSpan.FromSeconds(limits.CreditWindowSeconds)));
            });

            return services;
        }

        private static string ClientIp(HttpContext httpContext) =>
            httpContext.Connection.RemoteIpAddress?.ToString() ?? RateLimitingConstants.FallbackPartitionKey;

        // The FallbackPartitionKey branch is defensive only; authenticated-only routes are not
        // expected to reach it.
        private static string UserKey(HttpContext httpContext) =>
            httpContext.RetrieveUserId()?.ToString() ?? RateLimitingConstants.FallbackPartitionKey;

        private static RateLimitPartition<string> SlidingWindow(string partitionKey, int permitLimit, TimeSpan window) =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: partitionKey,
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
    }
}