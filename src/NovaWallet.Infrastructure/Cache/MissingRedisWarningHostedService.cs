using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NovaWallet.Infrastructure.Cache
{
    /// <summary>
    /// Hosted service that emits a one-time startup warning when the shared keyed
    /// <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/> has been registered without a
    /// Redis-backed <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>.
    /// </summary>
    /// <remarks>
    /// This service exists to surface a configuration gap that would otherwise fail silently: when
    /// <c>RedisUri</c> is not configured, the keyed shared <c>HybridCache</c> is still registered (so
    /// dependency injection can resolve it), but it has no distributed backing store. Consumers such as
    /// <c>CustomerSessionHelper.IsIpAddressValidAsync</c> that rely on the cache being shared across
    /// services will instead observe local, per-instance-only state, with no error or exception to
    /// indicate the degradation.
    /// <para>
    /// Registered as an <see cref="IHostedService"/> rather than an <c>IStartupFilter</c> so that the
    /// warning fires consistently in both ASP.NET Core web hosts and generic hosts (e.g. RabbitMQ
    /// consumer hosts) that do not build an <see cref="Microsoft.AspNetCore.Builder.IApplicationBuilder"/>
    /// pipeline.
    /// </para>
    /// </remarks>
    /// <param name="logger">The logger used to emit the missing-Redis warning at startup.</param>
    public sealed class MissingRedisWarningHostedService(ILogger<MissingRedisWarningHostedService> logger)
        : IHostedService
    {
        /// <summary>
        /// Logs a warning indicating that the shared keyed <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/>
        /// is running without a distributed backing store.
        /// </summary>
        /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
        /// <returns>A completed task, since this method performs no asynchronous work.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning(
                "RedisUri is not configured. The shared HybridCache " +
                "is running WITHOUT a distributed backing store. Cross-service shared-cache semantics " +
                "will not work as intended in this environment.");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Performs no action on shutdown. Included to satisfy the <see cref="IHostedService"/> contract.
        /// </summary>
        /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
        /// <returns>A completed task, since this method performs no work.</returns>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}