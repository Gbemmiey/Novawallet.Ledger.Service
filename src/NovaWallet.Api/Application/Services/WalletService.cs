using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;
using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Api.Application.Services
{
    public sealed class WalletService : IWalletService
    {
        private readonly ILogger<WalletService> _logger;
        private readonly NovaWalletDbContext _novaWalletDbContext;
        private readonly IRequestContext _requestContext;

        public WalletService(ILogger<WalletService> logger, NovaWalletDbContext novaWalletDbContext, IRequestContext requestContext)
        {
            _logger = logger;
            _novaWalletDbContext = novaWalletDbContext;
            _requestContext = requestContext;
        }

        public async Task<ServiceApiResponse<CreateWalletResponse>> CreateWallet(CancellationToken cancellationToken)
        {
            var userId = _requestContext.UserId;

            if (userId is null)
            {
                return ServiceApiResponse<CreateWalletResponse>.CreateFailure(ResponseCodes.AccessDenied);
            }

            // TODO : Low - Distributed lock on UserId for Wallet creation

            var strategy = _novaWalletDbContext.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _novaWalletDbContext.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var existingWallet = await _novaWalletDbContext.Wallets
                        .Include(w => w.Account)
                        .FirstOrDefaultAsync(w => w.UserId == userId.Value, cancellationToken);

                    if (existingWallet is not null)
                    {
                        await transaction.RollbackAsync(cancellationToken);

                        _logger.LogWarning(
                            "Wallet creation requested for UserId {UserId}, but a wallet already exists. WalletId: {WalletId}",
                            userId.Value,
                            existingWallet.Id);

                        return ServiceApiResponse<CreateWalletResponse>.CreateSuccess(
                            new CreateWalletResponse
                            {
                                AccountNumber = existingWallet.Account?.AccountNumber ?? string.Empty,
                                AvailableBalanceKobo = existingWallet.AvailableBalanceKobo,
                                CurrencyCode = existingWallet.Currency,
                                WalletId = existingWallet.Id,
                                Status = existingWallet.Status
                            });
                    }

                    var account = Account.Create(
                        accountNumber: GenerateAccountNumber(userId.Value),
                        accountType: AccountType.Liability);

                    var wallet = Wallet.Create(
                        userId: userId.Value,
                        accountId: account.Id);

                    _novaWalletDbContext.Accounts.Add(account);
                    _novaWalletDbContext.Wallets.Add(wallet);

                    await _novaWalletDbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation(
                        "Wallet created successfully for UserId {UserId}. WalletId: {WalletId}, AccountId: {AccountId}",
                        userId.Value,
                        wallet.Id,
                        account.Id);

                    return ServiceApiResponse<CreateWalletResponse>.CreateSuccess(
                        new CreateWalletResponse
                        {
                            AccountNumber = account.AccountNumber,
                            AvailableBalanceKobo = wallet.AvailableBalanceKobo,
                            CurrencyCode = wallet.Currency,
                            WalletId = wallet.Id,
                            Status = wallet.Status
                        });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(CancellationToken.None);

                    _logger.LogError(ex, "Failed to create wallet for UserId {UserId}", userId.Value);
                    return ServiceApiResponse<CreateWalletResponse>.SystemMalFunctioned();
                }
            });
        }

        public static string GenerateAccountNumber(Guid id)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N")));

            var value = BitConverter.ToUInt64(hash, 0) % 10_000_000_000;

            return value.ToString("D10");
        }
    }
}