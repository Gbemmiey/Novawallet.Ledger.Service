using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaWallet.Application.Caching
{
    public static class NovaWalletCacheConstants
    {
        /// <summary>
        /// The Key for the Redis URI
        /// </summary>
        public const string RedisUriKey = "REDIS_URI";

        /// <summary>
        /// Determines whether Redis has been configured.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>
        /// <c>true</c> if a non-empty Redis URI is configured; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsRedisConfigured(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return !string.IsNullOrWhiteSpace(configuration[RedisUriKey]);
        }

        /// <summary>
        /// The key for the application on redis
        /// </summary>
        public const string RedisApplicationNameKey = "REDIS_APPLICATION_NAME";

        public static JsonSerializerOptions JsonSerializerOptions { get; } = new JsonSerializerOptions()
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };
    }
}