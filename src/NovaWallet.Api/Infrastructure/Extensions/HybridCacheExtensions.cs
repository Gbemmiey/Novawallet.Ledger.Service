using Microsoft.Extensions.Caching.Hybrid;
using NovaWallet.Api.Infrastructure.Cache;
using StackExchange.Redis;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    /// <summary>
    /// Adds Caching with:
    /// • Redis L2 (distributed) + automatic in-memory L1 fallback
    /// • Automatic in-memory-only mode when Redis is unavailable (perfect for local dev)
    /// </summary>
    public static partial class HybridCacheExtensions
    {
        /// <summary>
        /// Configures Microsoft.Extensions.Caching.Hybrid (the official successor to FusionCache)
        /// with:
        /// • L1 In-Memory + L2 Redis (automatic hybrid)
        /// • Redis Pub/Sub backplane for real-time invalidation
        /// • Full fail-safe, stampede protection, and resilience
        /// • Works perfectly on .NET 8, 9, and 10+
        /// • A shared keyed HybridCache instance backed by a fixed Redis namespace
        ///   for cross-service cache entries (e.g. session IP history)
        /// </summary>
        public static IServiceCollection AddAppHybridCache(
         this IServiceCollection services,
         IConfiguration configuration)
        {
            var redisConfigured = NovaWalletCacheConstants.IsRedisConfigured(configuration);

            if (redisConfigured)
            {
                var redisUri = configuration[NovaWalletCacheConstants.RedisUriKey]!;

                var instanceName = configuration[NovaWalletCacheConstants.RedisApplicationNameKey] ?? "NovaWallet:Shared";

                var config = ConfigurationOptions.Parse(redisUri);

                // Connection establishment
                config.AbortOnConnectFail = false;
                config.ConnectRetry = 1;
                config.ConnectTimeout = 200;

                // Redis operation timeout
                config.AsyncTimeout = 500;

                // Don't keep application cache commands waiting for a
                // Redis connection to become available.
                config.BacklogPolicy = BacklogPolicy.FailFast;

                config.ReconnectRetryPolicy = new ExponentialRetry(200);
                config.Ssl = redisUri.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase);

                // 3. Single shared IConnectionMultiplexer for the shared cache
                services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(config));

                // 4. Keyed IDistributedCache with a fixed namespace - no service prefix
                services.AddStackExchangeRedisCache(options =>
                {
                    options.ConfigurationOptions = config;

                    // Shared namespace - no application prefix
                    options.InstanceName = $"{instanceName}:";
                });
            }

            // Default HybridCache registration
            services.AddHybridCache(options =>
            {
                options.DisableCompression = false;
                options.MaximumPayloadBytes = 10 * 1024 * 1024;
                options.MaximumKeyLength = 1024;

                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromMinutes(10),
                    Flags = HybridCacheEntryFlags.DisableLocalCache,
                };
            });

            if (!redisConfigured)
            {
                services.AddHostedService<MissingRedisWarningHostedService>();
            }

            return services;
        }
    }
}