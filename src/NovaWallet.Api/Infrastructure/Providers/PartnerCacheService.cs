using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using NovaWallet.Api.Infrastructure.Data;

namespace NovaWallet.Api.Infrastructure.Providers
{
    public interface IPartnerCacheService
    {
        /// <summary>
        /// Retrieves a partner by hashed API key from cache or database.
        /// </summary>
        Task<FintechPartnerCacheDto?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken ct = default);

        /// <summary>
        /// Purges/invalidates the cached partner record across distributed cache.
        /// </summary>
        Task InvalidatePartnerCacheAsync(string apiKeyHash, CancellationToken ct = default);
    }

    public class PartnerCacheService : IPartnerCacheService
    {
        private readonly NovaWalletDbContext _dbContext;
        private readonly HybridCache _hybridCache;

        private static readonly HybridCacheEntryOptions CacheOptions = new()
        {
            Flags = HybridCacheEntryFlags.DisableLocalCache // Recommended for multi-node consistency
        };

        public PartnerCacheService(NovaWalletDbContext dbContext, HybridCache hybridCache)
        {
            _dbContext = dbContext;
            _hybridCache = hybridCache;
        }

        public async Task<FintechPartnerCacheDto?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKeyHash))
                return null;

            var cacheKey = GetPartnerCacheKey(apiKeyHash);

            return await _hybridCache.GetOrCreateAsync(
                cacheKey,
                async token => await _dbContext.FintechPartners
                    .AsNoTracking()
                    .Where(fp => fp.ApiKeyHash == apiKeyHash)
                    .Select(fp => new FintechPartnerCacheDto
                    {
                        Id = fp.Id,
                        UniqueIdentifier = fp.UniqueIdentifier,
                        Name = fp.Name,
                        ApiKeyHash = fp.ApiKeyHash,
                        MaximumRequestsPerMinute = fp.MaximumRequestsPerMinute,
                        IsActive = fp.IsActive,
                    })
                    .FirstOrDefaultAsync(token),
                CacheOptions,
                cancellationToken: ct);
        }

        public async Task InvalidatePartnerCacheAsync(string apiKeyHash, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKeyHash))
                return;

            var cacheKey = GetPartnerCacheKey(apiKeyHash);
            await _hybridCache.RemoveAsync(cacheKey, ct);
        }

        private static string GetPartnerCacheKey(string apiKeyHash) => $"partner:apikey:{apiKeyHash}";
    }
}