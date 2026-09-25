using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Validators;
using NovaWallet.Infrastructure.Data;
using NovaWallet.Infrastructure.Http;
using NovaWallet.Infrastructure.Options;
using NovaWallet.Infrastructure.Providers;
using NovaWallet.Infrastructure.Workers;

namespace NovaWallet.Infrastructure.Extensions
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

            // Application-layer services depend on IApplicationDbContext, not the concrete,
            // Postgres-bound NovaWalletDbContext - this is the one place that binds the two.
            services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<NovaWalletDbContext>());

            return services;
        }

        public static IServiceCollection RegisterApplicationOptions(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<DepositConsumerOptions>(configuration.GetSection("DepositConsumer"));
            services.Configure<ReconciliationWorkerOptions>(configuration.GetSection("ReconciliationWorker"));
            services.Configure<StartupOptions>(configuration.GetSection("Startup"));
            return services;
        }

        /// <summary>
        /// Registers the pieces of the Application layer's abstractions that only Infrastructure
        /// can implement (JWT-backed mock auth, the Postgres-specific unique/check-violation
        /// detector), plus the background workers. Distinct from
        /// <c>NovaWallet.Application.ApplicationServiceCollectionExtensions.RegisterApplicationServices</c>,
        /// which registers the Application-layer service implementations themselves.
        /// </summary>
        public static IServiceCollection RegisterInfrastructureServices(this IServiceCollection services)
        {
            services.AddScoped<IMockUserAuthHelper, MockUserAuthHelper>();
            services.AddSingleton<IUniqueConstraintViolationDetector, NpgsqlUniqueConstraintViolationDetector>();
            services.AddHostedService<DepositConsumer>();
            services.AddHostedService<ReconciliationWorker>();
            return services;
        }

        public static IServiceCollection RegisterPayloadValidation(this IServiceCollection services)
        {
            var assemblies = new[]
                {
                    typeof(WalletTransferRequestValidator).Assembly
                };

            services.AddValidatorsFromAssemblies(assemblies);

            services.AddScoped(typeof(ValidationFilter<>));
            services.AddFluentValidationAutoValidation();

            return services;
        }
    }
}
