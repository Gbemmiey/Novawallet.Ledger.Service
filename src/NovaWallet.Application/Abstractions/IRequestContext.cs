namespace NovaWallet.Application.Abstractions
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
        /// Gets the authenticated user's ID (JWT <c>sub</c>) for the current request, if any.
        /// </summary>
        Guid? UserId { get; }
    }
}