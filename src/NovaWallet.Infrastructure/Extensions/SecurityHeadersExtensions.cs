using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Application.Configuration;
using NovaWallet.Infrastructure.Data;

namespace NovaWallet.Infrastructure.Extensions
{
    public static class ApplicationBuilderExtensions
    {
        /// <summary>
        /// Applies all pending Entity Framework Core database migrations.
        /// </summary>
        /// <param name="application">The application builder.</param>
        /// <returns>The application builder for method chaining.</returns>
        public static async Task<IApplicationBuilder> ApplyDatabaseMigrationsAsync(
            this IApplicationBuilder application)
        {
            using var scope = application.ApplicationServices.CreateScope();

            var dbContext = scope.ServiceProvider
                .GetRequiredService<NovaWalletDbContext>();

            await dbContext.Database.MigrateAsync();

            return application;
        }
    }

    /// <summary>
    /// Provides extension methods for configuring custom security headers in an ASP.NET Core application.
    /// </summary>
    /// <remarks>This class includes methods to apply security header policies based on request paths. By
    /// default, a strict security policy is applied, enforcing measures such as denying frame embedding, disallowing
    /// inline scripts, and requiring HTTPS. For specific excluded paths (e.g., Swagger endpoints), a relaxed policy is
    /// applied to allow inline scripts and styles for compatibility. The excluded paths are defined in
    /// <c>OptiverseBusinessConstants.SecurityMiddlewareExcludedPaths</c>.</remarks>
    public static class SecurityHeadersExtensions
    {
        /// <summary>
        /// Configures the application to use custom security headers based on the request path.
        /// </summary>
        /// <remarks>This method applies different security header policies depending on the request path:
        /// <list type="bullet"> <item> <description>A strict policy is applied by default, which enforces strong
        /// security measures such as denying frame embedding, disallowing inline scripts, and requiring
        /// HTTPS.</description> </item> <item> <description>A relaxed policy is applied to specific excluded paths
        /// (e.g., Swagger endpoints), allowing inline scripts and styles for compatibility.</description> </item>
        /// </list> The excluded paths are defined in
        /// <c>OptiverseBusinessConstants.SecurityMiddlewareExcludedPaths</c>.</remarks>
        /// <param name="app">The <see cref="IApplicationBuilder"/> instance to configure.</param>
        /// <returns>The <see cref="IApplicationBuilder"/> instance for further configuration.</returns>
        public static IApplicationBuilder AddCustomSecurityHeaders(this IApplicationBuilder app)
        {
            // 1. THE STRICT POLICY (Default for API)
            var strictPolicy = new HeaderPolicyCollection()
                .AddContentTypeOptionsNoSniff()
                .AddFrameOptionsDeny()
                .AddReferrerPolicyNoReferrer()
                .AddPermissionsPolicyWithDefaultSecureDirectives()
                .RemoveServerHeader()
                .AddCustomHeader("X-Powered-By", "")
                .AddCustomHeader("Server", "")
                .AddContentSecurityPolicy(builder =>
                {
                    builder.AddDefaultSrc().None();
                    builder.AddConnectSrc().Self();
                    builder.AddFrameAncestors().None();
                    builder.AddBaseUri().None();
                    builder.AddFormAction().None();
                    builder.AddUpgradeInsecureRequests();
                })
                .AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds: 31536000);

            // 2. THE RELAXED POLICY (For Swagger/Excluded Paths)
            var relaxedPolicy = new HeaderPolicyCollection()
                .AddDefaultSecurityHeaders()
                .RemoveServerHeader()
                .AddCustomHeader("X-Powered-By", "")
                .AddCustomHeader("Server", "")
                .AddContentSecurityPolicy(builder =>
                {
                    builder.AddDefaultSrc().Self();
                    builder.AddScriptSrc().Self().UnsafeInline();
                    builder.AddStyleSrc().Self().UnsafeInline();
                    builder.AddImgSrc().Self().Data();
                    builder.AddConnectSrc().Self();
                });

            // Helper to check if the current path matches any in your constants list
            bool IsExcluded(HttpContext context)
            {
                var path = context.Request.Path.Value ?? string.Empty;
                return NovaWalletConstants.SecurityMiddlewareExcludedPaths
                    .Any(excludedPath => path.StartsWith(excludedPath, StringComparison.OrdinalIgnoreCase));
            }

            // 3. APPLY CONDITIONALLY
            // Apply relaxed policy if the path is in the Excluded list
            app.UseWhen(IsExcluded, appBuilder =>
                appBuilder.UseSecurityHeaders(relaxedPolicy));

            // Apply strict policy if the path is NOT in the Excluded list
            app.UseWhen(context => !IsExcluded(context), appBuilder =>
                appBuilder.UseSecurityHeaders(strictPolicy));

            return app;
        }
    }
}