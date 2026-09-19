using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Api.Core.Options
{
    /// <summary>
    /// Strongly-typed configuration for the named rate-limiting policies registered in
    /// <c>RateLimiterServiceExtensions.RegisterRateLimitingPolicies</c>.
    /// </summary>
    /// <remarks>
    /// Bound from the "RateLimiting" section (env override form: <c>RateLimiting__LoginPermitLimit</c>).
    /// Class defaults are deliberately conservative so a missing config value can never silently
    /// loosen production; load-test/dev environments raise them via configuration.
    /// <code>
    /// {
    ///   "RateLimiting": {
    ///     "LoginPermitLimit": 30,     "LoginWindowSeconds": 60,
    ///     "UserPermitLimit": 120,     "UserWindowSeconds": 60,
    ///     "TransferPermitLimit": 20,  "TransferWindowSeconds": 60,
    ///     "CreditPermitLimit": 600,   "CreditWindowSeconds": 60
    ///   }
    /// }
    /// </code>
    /// </remarks>
    public class RateLimitingOptions
    {
        /// <summary>Requests allowed per window, per client IP, on <c>POST /auth/login</c>. Keyed on IP only, never on the userId in the body.</summary>
        [Range(1, int.MaxValue)]
        public int LoginPermitLimit { get; set; } = 30;

        [Range(1, 86_400)]
        public int LoginWindowSeconds { get; set; } = 60;

        /// <summary>Requests allowed per window, per authenticated user (JWT <c>sub</c>), on wallet create / fetch / statement.</summary>
        [Range(1, int.MaxValue)]
        public int UserPermitLimit { get; set; } = 120;

        [Range(1, 86_400)]
        public int UserWindowSeconds { get; set; } = 60;

        /// <summary>Requests allowed per window, per authenticated user (JWT <c>sub</c>), on <c>POST /wallets/transfer</c>.</summary>
        [Range(1, int.MaxValue)]
        public int TransferPermitLimit { get; set; } = 20;

        [Range(1, 86_400)]
        public int TransferWindowSeconds { get; set; } = 60;

        /// <summary>
        /// Requests allowed per window, per client IP, on the anonymous <c>POST /wallets/credit</c>
        /// webhook. <c>0</c> disables the limit entirely.
        /// </summary>
        [Range(0, int.MaxValue)]
        public int CreditPermitLimit { get; set; } = 600;

        [Range(1, 86_400)]
        public int CreditWindowSeconds { get; set; } = 60;
    }
}
