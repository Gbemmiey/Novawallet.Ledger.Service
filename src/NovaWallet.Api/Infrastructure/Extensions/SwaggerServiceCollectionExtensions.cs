using Microsoft.OpenApi.Models;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    public static class SwaggerServiceCollectionExtensions
    {
        /// <summary>
        /// Adds and configures Swagger generation and API versioning services,
        /// including support for JWT Bearer token authentication.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
        /// <returns>The original <see cref="IServiceCollection"/> to allow chaining.</returns>
        /// <remarks>
        /// This method configures:
        /// <list type="bullet">
        /// <item>Swagger generation with XML comments included from all XML files in the base directory.</item>
        /// <item>Swagger annotations and inline enum definitions.</item>
        /// <item>JWT Bearer token authentication support in Swagger UI.</item>
        /// <item>API versioning with default version assumption and reporting.</item>
        /// <item>API explorer version grouping with URL substitution.</item>
        /// <item>Configuration of custom <see cref="ConfigureSwaggerOptions"/>.</item>
        /// </list>
        /// </remarks>
        public static IServiceCollection AddSwaggerAndApiVersioning(this IServiceCollection services)
        {
            // Registers endpoint API explorer needed for Swagger
            services.AddEndpointsApiExplorer();

            // Adds and configures Swagger generator
            services.AddSwaggerGen(swagger =>
            {
                // Get the base directory of the application
                var baseDirectory = AppContext.BaseDirectory;

                // Find all XML documentation files in the base directory
                var xmlFiles = Directory.GetFiles(baseDirectory, "*.xml", SearchOption.TopDirectoryOnly);

                // Include each XML file for Swagger XML comments
                foreach (var xmlFile in xmlFiles)
                {
                    swagger.IncludeXmlComments(xmlFile);
                }

                swagger.EnableAnnotations();
                swagger.UseInlineDefinitionsForEnums();

                // Add JWT Bearer authentication to Swagger
                var jwtSecurityScheme = new OpenApiSecurityScheme
                {
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.Http,
                    Description = "Enter your JWT token in the format: Bearer {your token}",

                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                };

                swagger.AddSecurityDefinition("Bearer", jwtSecurityScheme);

                swagger.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        jwtSecurityScheme,
                        Array.Empty<string>()
                    }
                });
            });

            // Configure API versioning options
            services.AddApiVersioning(options =>
            {
                options.ReportApiVersions = true;
                options.AssumeDefaultVersionWhenUnspecified = true;
            })
            // Configure API Explorer to understand versioning
            .AddApiExplorer(options =>
            {
                // Format group name like "v1", "v2", etc.
                options.GroupNameFormat = "'v'VVV";
                // Substitute the API version in route URLs
                options.SubstituteApiVersionInUrl = true;
            });

            // Register custom Swagger options configuration
            services.ConfigureOptions<ConfigureSwaggerOptions>();

            return services;
        }
    }
}