using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api.Core.Configuration;

namespace NovaWallet.Api.Infrastructure.Extensions;

/// <summary>
/// Provides extension methods for configuring CORS (Cross-Origin Resource Sharing) in ASP.NET Core applications.
/// </summary>
public static class CorsExtensions
{
    /// <summary>
    /// Retrieves the list of allowed origins from the specified configuration section.
    /// </summary>
    /// <param name="configuration">The configuration instance to read from.</param>
    /// <param name="sectionName">The configuration section path containing the list of allowed domains. Defaults to "CORSConfig:AllowedDomains".</param>
    /// <returns>A list of allowed origin URLs. Returns an empty list if the section is not found or contains no values.</returns>
    /// <remarks>
    /// This method safely handles cases where the configuration section is missing or empty by returning an empty list.
    /// The configuration section should contain a list of valid URLs, e.g., ["http://example.com", "https://example.com"].
    /// </remarks>
    public static List<string> GetAllowedOrigins(this IConfiguration configuration, string sectionName = "CorsConfigs:AllowedDomains")
    {
        return configuration.GetSection(sectionName).Get<List<string>>() ?? new List<string>();
    }

    /// <summary>
    /// Registers a CORS policy with the specified name and allowed origins in the service collection.
    /// </summary>
    /// <param name="services">The service collection to add the CORS policy to.</param>
    /// <param name="policyName">The name of the CORS policy.</param>
    /// <param name="allowedOrigins">The list of allowed origin URLs to configure in the policy.</param>
    /// <returns>The service collection for method chaining.</returns>
    /// <remarks>
    /// The configured CORS policy allows any HTTP method, any header, and credentials for the specified origins.
    /// Ensure that <paramref name="allowedOrigins"/> contains valid URLs to avoid runtime errors.
    /// </remarks>
    public static IServiceCollection AddCustomCors(this IServiceCollection services, List<string> allowedOrigins)
    {
        services.AddCors(options =>
        {
            options.AddPolicy(name: NovaWalletConstants.CorsPolicyName, policy =>
            {
                policy.WithOrigins([.. allowedOrigins])
                      .AllowAnyMethod()
                      .AllowCredentials()
                      .AllowAnyHeader();
            });
        });
        return services;
    }

    /// <summary>
    /// Applies the specified CORS policy to the application pipeline.
    /// </summary>
    /// <param name="app">The application builder to configure the CORS middleware for.</param>
    /// <param name="policyName">The name of the CORS policy to apply.</param>
    /// <returns>The application builder for method chaining.</returns>
    /// <remarks>
    /// This method enables the CORS middleware with the specified policy, allowing cross-origin requests as configured.
    /// Ensure that the CORS policy with <paramref name="policyName"/> is registered using <see cref="AddCustomCors"/> before calling this method.
    /// </remarks>
    public static IApplicationBuilder UseCustomCors(this IApplicationBuilder app)
    {
        app.UseCors(NovaWalletConstants.CorsPolicyName);
        return app;
    }
}