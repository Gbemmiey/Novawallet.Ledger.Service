using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
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

        public async Task<ServiceApiResponse<NipSingleCreditResponse>> SubmitDepositRequest(NipSingleCreditRequest nipSingleCreditRequest, CancellationToken cancellationToken)
        {
            // Validate the account number
            var validBeneficiaryAccount = await _novaWalletDbContext.Accounts
                .AnyAsync(a => a.Currency == NovaWalletConstants.CurrencyCode
                && a.AccountType == AccountType.Liability
                && a.AccountNumber == nipSingleCreditRequest.BeneficiaryAccountNumber, cancellationToken);

            if (!validBeneficiaryAccount)
            {
                _logger.LogError("");
                return ServiceApiResponse<NipSingleCreditResponse>.CreateFailure(ResponseCodes.NoRecordReturned);
            }

            // Create a record - db transation wrapped in an execution strategy
            throw new NotImplementedException();
        }
    }
}