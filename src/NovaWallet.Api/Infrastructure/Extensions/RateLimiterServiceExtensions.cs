using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Infrastructure.Http;
using System.Threading.RateLimiting;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    public static class RateLimiterServiceExtensions
    {
        public static IServiceCollection RegisterRateLimitingPolicies(this IServiceCollection services)
        {
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.OnRejected = async (context, cancellationToken) =>
                {
                    context.HttpContext.Response.ContentType = "application/json";

                    var response = ServiceApiResponse<object>.CreateFailure(
                        code: ResponseCodes.TooManyRequests.ResponseCode,
                        message: "Rate limit exceeded. Too many requests sent in a short period.");

                    await context.HttpContext.Response.WriteAsJsonAsync(response, cancellationToken);
                };

                // 1. Internal / Admin Policy (Partitioned by User Identity or IP)
                options.AddPolicy(RateLimitingConstants.InternalAdminPolicy, httpContext =>
                {
                    var partitionKey = $"admin_ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? RateLimitingConstants.FallbackPartitionKey}";

                    // Higher limit or sliding window suited for internal admin management dashboards
                    return RateLimitPartition.GetSlidingWindowLimiter(
                        partitionKey: partitionKey,
                        factory: _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = 300,
                            Window = TimeSpan.FromMinutes(1),
                            SegmentsPerWindow = 6,
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                });

                // 2. Per-Partner (authenticated caller) Policy — applied to POST /api/v1/wallets/transfer.
                // Partitioned by the caller's JWT `sub` claim (UseAuthorization() runs before
                // UseRateLimiter() in the pipeline, so the caller is already authenticated by the
                // time this partition function runs); the FallbackPartitionKey branch is defensive
                // only, mirroring InternalAdminPolicy's style, and is not expected to trigger on an
                // authenticated-only route.
                options.AddPolicy(RateLimitingConstants.PerPartnerPolicy, httpContext =>
                {
                    var partitionKey = httpContext.RetrieveUserId()?.ToString()
                        ?? RateLimitingConstants.FallbackPartitionKey;

                    return RateLimitPartition.GetSlidingWindowLimiter(
                        partitionKey: partitionKey,
                        factory: _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            SegmentsPerWindow = 6,
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                });
            });

            return services;
        }
    }
}