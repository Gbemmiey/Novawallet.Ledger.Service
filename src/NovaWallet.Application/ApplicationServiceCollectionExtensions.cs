using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Observability;
using NovaWallet.Application.Services;

namespace NovaWallet.Application
{
    public static class ApplicationServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the Application-layer service implementations and the custom metrics
        /// singleton they share. Distinct from
        /// <c>NovaWallet.Infrastructure.Extensions.SwitchServiceCollectionExtensions.RegisterInfrastructureServices</c>,
        /// which registers the Infrastructure-side implementations of the abstractions these
        /// services depend on (IApplicationDbContext, IMockUserAuthHelper,
        /// IUniqueConstraintViolationDetector).
        /// </summary>
        public static IServiceCollection RegisterApplicationServices(this IServiceCollection services)
        {
            services.AddSingleton<NovaWalletMetrics>();
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IWalletService, WalletService>();
            services.AddScoped<IDepositService, DepositService>();
            services.AddScoped<ITransferService, TransferService>();
            services.AddScoped<IAdminService, AdminService>();
            return services;
        }
    }
}
