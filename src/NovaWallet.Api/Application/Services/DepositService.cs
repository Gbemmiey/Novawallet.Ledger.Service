using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;

namespace NovaWallet.Api.Application.Services
{
    public sealed class DepositService : IDepositService
    {
        private readonly ILogger<DepositService> _logger;
        private readonly NovaWalletDbContext _novaWalletDbContext;

        public DepositService(ILogger<DepositService> logger, NovaWalletDbContext novaWalletDbContext)
        {
            _logger = logger;
            _novaWalletDbContext = novaWalletDbContext;
        }

        public Task<ServiceApiResponse<NipSingleCreditResponse>> SubmitDepositRequest(NipSingleCreditRequest nipSingleCreditRequest, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}