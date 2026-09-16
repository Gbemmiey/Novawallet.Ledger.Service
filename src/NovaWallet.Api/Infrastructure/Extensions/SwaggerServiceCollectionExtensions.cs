using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using NovaWallet.Api.Core.Configuration;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    public static class OpenApiServiceExtensions
    {
        public static IServiceCollection RegisterOpenApiSpecifications(this IServiceCollection services)
        {
            var apiKeyHeader = NovaWalletConstants.PartnerApiKeyHeader ?? "X-API-Key";
            const string apiKeySchemeName = "ApiKey";

            services.AddOpenApi(options =>
            {
                // 1. Declare the Security Scheme component globally
                options.AddDocumentTransformer((document, context, cancellationToken) =>
                {
                    document.Components ??= new OpenApiComponents();
                    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

                    document.Components.SecuritySchemes[apiKeySchemeName] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        Name = apiKeyHeader,
                        In = ParameterLocation.Header,
                        Description = "Enter your partner API key below:"
                    };

                    return Task.CompletedTask;
                });

                // 2. Operation Transformer: Inspect metadata per-endpoint directly!
                options.AddOperationTransformer((operation, context, cancellationToken) =>
                {
                    // Inspect metadata directly on the ActionDescriptor
                    var metadata = context.Description.ActionDescriptor.EndpointMetadata;

                    var isPartnerEndpoint = metadata.OfType<IAuthorizeData>()
                        .Any(a => string.Equals(a.Policy, AuthorizationPolicyConstants.PartnerOnly, StringComparison.OrdinalIgnoreCase));

                    if (isPartnerEndpoint)
                    {
                        operation.Security ??= new List<OpenApiSecurityRequirement>();

                        // Build reference using document context from transformer context
                        var schemeReference = new OpenApiSecuritySchemeReference(apiKeySchemeName, context.Document);

                        operation.Security.Add(new OpenApiSecurityRequirement
                        {
                            [schemeReference] = new List<string>()
                        });
                    }

                    return Task.CompletedTask;
                });
            });

            // Configure API Versioning
            services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });

            return services;
        }
    }
}