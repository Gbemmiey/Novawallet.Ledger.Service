using NovaWallet.Api.Core.Dto.FintechPartners;

namespace NovaWallet.Api.Application.Services
{
    /// <summary>
    /// Provides access to request-specific metadata.
    /// </summary>
    public interface IRequestContext
    {
        /// <summary>
        /// Gets the distributed trace identifier for the current request.
        /// </summary>
        string TraceId { get; }

        /// <summary>
        /// Gets the application client identifier, if supplied.
        /// </summary>
        string? RetrieveHashedPartnerClientKey { get; }

        /// <summary>
        /// Gets or sets the authenticated partner context for the current request.
        /// </summary>
        FintechPartnerCacheDto? Partner { get; set; }
    }
}