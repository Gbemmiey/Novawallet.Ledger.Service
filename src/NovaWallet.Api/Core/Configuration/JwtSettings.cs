using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Api.Core.Configuration;

/// <summary>
/// Represents the configuration settings required for generating and validating JSON Web Tokens (JWT).
/// </summary>
public class JwtSettings
{
    public static readonly string SectionName = "Jwt";

    /// <summary>
    /// Gets or sets the secret key used to sign and validate JWT tokens.
    /// </summary>
    /// <remarks>
    /// This key should be a long, random string and kept secure.
    /// It is typically stored in a secure configuration source (e.g., environment variable, key vault).
    /// </remarks>
    [Required(ErrorMessage = "The JWT key is required.")]
    public string SecretKey { get; set; }

    /// <summary>
    /// Gets or sets the issuer of the JWT token.
    /// </summary>
    /// <remarks>
    /// The issuer usually represents the authentication server or identity provider
    /// responsible for issuing the token.
    /// </remarks>
    [Required(ErrorMessage = "The JWT issuer is required.")]
    public string Issuer { get; set; }

    /// <summary>
    /// Gets or sets the intended audience for the JWT token.
    /// </summary>
    /// <remarks>
    /// The audience defines the recipients that the token is intended for.
    /// The application validating the token should verify this value.
    /// </remarks>
    [Required(ErrorMessage = "The JWT audience is required.")]
    public string Audience { get; set; }

    /// <summary>
    /// Gets or sets the token expiration time in minutes.
    /// </summary>
    /// <remarks>
    /// This value determines how long a generated JWT token remains valid before expiring.
    /// The default value is 5 minutes.
    /// </remarks>
    [Range(1, int.MaxValue, ErrorMessage = "TokenExpirationTimeInMinutes must be greater than zero.")]
    [Required] public int TokenExpirationTimeInMinutes { get; set; } = 5;
}