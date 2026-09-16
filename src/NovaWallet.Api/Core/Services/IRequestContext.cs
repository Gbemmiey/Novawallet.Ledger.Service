namespace NovaWallet.Api.Core.Services
{
    /// <summary>
    /// Provides access to request-specific metadata.
    /// </summary>
    public interface IRequestContext
    {
        /// <summary>
        /// Gets the application client identifier, if supplied.
        /// </summary>
        string? RetrieveIdempotencyKey { get; }

        /// <summary>
        /// Gets or sets the authenticated partner context for the current request.
        /// </summary>
        Guid? UserId { get; }
    }
}