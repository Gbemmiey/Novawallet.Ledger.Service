using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Application.Services;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;
using NovaWallet.Api.Infrastructure.Http;
using NovaWallet.Api.Infrastructure.Providers;
using System.Reflection;

namespace NovaWallet.Api.Extensions
{
    public static class SwitchServiceCollectionExtensions
    {
        public static IServiceCollection RegisterApplicationDatabase(this IServiceCollection services, IConfiguration configuration)
        {
            var databaseConnectionString = configuration.ResolveDatabaseConnectionString();

            services.AddDbContext<NovaWalletDbContext>(options =>
                options.UseNpgsql(databaseConnectionString, sqlOptions =>
                {
                    sqlOptions.EnableRetryOnFailure(3);
                    sqlOptions.CommandTimeout(30);
                    sqlOptions.MigrationsAssembly(typeof(NovaWalletDbContext).Assembly.GetName().Name);
                }));

            return services;
        }

        public static IServiceCollection RegisterApplicationOptions(this IServiceCollection services, IConfiguration configuration)
        {
            return services;
        }

        public static IServiceCollection RegisterApplicationServices(this IServiceCollection services)
        {
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IMockUserAuthHelper, MockUserAuthHelper>();
            services.AddScoped<IWalletService, WalletService>();
            services.AddScoped<IDepositService, DepositService>();
            return services;
        }

        public static IServiceCollection RegisterPayloadValidation(this IServiceCollection services)
        {
            var assemblies = new[]
                {
                    Assembly.GetExecutingAssembly()
                };

            services.AddValidatorsFromAssemblies(assemblies);

            services.AddScoped(typeof(ValidationFilter<>));
            services.AddFluentValidationAutoValidation();

            return services;
        }
    }
}