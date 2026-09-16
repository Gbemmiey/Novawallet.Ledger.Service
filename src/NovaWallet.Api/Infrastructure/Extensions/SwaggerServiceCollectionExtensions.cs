using Asp.Versioning;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    public static class OpenApiServiceExtensions
    {
        public static IServiceCollection RegisterOpenApiSpecifications(this IServiceCollection services)
        {
            services.AddSwaggerGen();

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