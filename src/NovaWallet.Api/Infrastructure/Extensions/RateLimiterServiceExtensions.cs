using NovaWallet.Api.Core.Models.Response;
using System.Threading.RateLimiting;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    public static class RateLimiterServiceExtensions
    {
        public static IServiceCollection RegisterCardIssuanceLimiting(this IServiceCollection services)
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

                // 2. Internal / Admin Policy (Partitioned by User Identity or IP)
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
            });

            return services;
        }
    }
}