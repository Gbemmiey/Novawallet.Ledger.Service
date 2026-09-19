using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Reflection;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    /// <summary>
    /// Configures Swagger options for each available API version.
    /// This ensures that a Swagger document is created per version
    /// exposed by the <see cref="IApiVersionDescriptionProvider"/>.
    /// </summary>
    public class ConfigureSwaggerOptions : IConfigureOptions<SwaggerGenOptions>
    {
        private readonly IApiVersionDescriptionProvider _provider;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigureSwaggerOptions"/> class.
        /// </summary>
        /// <param name="provider">
        /// The API version description provider used to enumerate
        /// available API versions and their metadata.
        /// </param>
        public ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// Configures Swagger generation options by registering a
        /// Swagger document for each discovered API version.
        /// </summary>
        /// <param name="options">The Swagger generation options.</param>
        public void Configure(SwaggerGenOptions options)
        {
            foreach (var description in _provider.ApiVersionDescriptions)
            {
                options.SwaggerDoc(description.GroupName, CreateInfoForApiVersion(description));
            }
        }

        /// <summary>
        /// Creates OpenAPI metadata for a given API version.
        /// </summary>
        /// <param name="description">The API version description.</param>
        /// <returns>
        /// An <see cref="OpenApiInfo"/> instance containing version-specific metadata.
        /// </returns>
        private OpenApiInfo CreateInfoForApiVersion(ApiVersionDescription description)
        {
            var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Optiverse Business API";
            var version = description.GroupName ?? "v1";

            var info = new OpenApiInfo
            {
                Title = $"{appName} v{description.ApiVersion}",
                Version = description.ApiVersion.ToString(),
            };

            if (description.IsDeprecated)
            {
                info.Description += " This API version has been deprecated.";
            }

            return info;
        }
    }
}