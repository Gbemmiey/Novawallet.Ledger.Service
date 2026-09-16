using NovaWallet.Api.Application.Services;
using NovaWallet.Api.Core.Models.Response;
using System.Threading.RateLimiting;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    public static class RateLimiterServiceExtensions
    {
        public static IServiceCollection RegisterPartnerAuthorization(this IServiceCollection services)
        {
            services.AddAuthorization(options =>
            {
                options.AddPolicy(AuthorizationPolicyConstants.PartnerOnly, policy =>
                    policy.RequireAssertion(context =>
                    {
                        var httpContext = context.Resource as HttpContext;
                        var requestContext = httpContext?.RequestServices.GetRequiredService<IRequestContext>();
                        return requestContext?.Partner != null;
                    }));
            });

            return services;
        }

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

                // Inside RateLimiter Policy (Zero blocking / async overhead)
                options.AddPolicy(RateLimitingConstants.PerPartnerPolicy, httpContext =>
                {
                    var requestContext = httpContext.RequestServices.GetRequiredService<IRequestContext>();
                    var partner = requestContext.Partner;

                    var partnerKey = partner?.ApiKeyHash ?? requestContext.RetrieveHashedPartnerClientKey;

                    var partitionKey = !string.IsNullOrWhiteSpace(partnerKey)
                        ? partnerKey
                        : httpContext.Connection.RemoteIpAddress?.ToString() ?? RateLimitingConstants.FallbackPartitionKey;

                    const int defaultFallbackLimit = 60;
                    var permitLimit = partner?.MaximumRequestsPerMinute > 0
                        ? partner.MaximumRequestsPerMinute
                        : defaultFallbackLimit;

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: partitionKey,
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                });

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