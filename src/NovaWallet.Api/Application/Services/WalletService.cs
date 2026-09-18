using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;
using System.Linq.Expressions;
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
                return ServiceApiResponse<CreateWalletResponse>.CreateFailure(
                    ResponseCodes.AccessDenied);
            }

            // TODO : Low - Distributed lock on UserId for Wallet creation

            var strategy = _novaWalletDbContext.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _novaWalletDbContext.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var existingWallet = await _novaWalletDbContext.Wallets
                        .Where(w => w.UserId == userId.Value)
                        .Select(WalletResponseProjection)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (existingWallet is not null)
                    {
                        await transaction.RollbackAsync(cancellationToken);

                        _logger.LogWarning(
                            "Wallet creation requested for UserId {UserId}, but a wallet already exists. WalletId: {WalletId}",
                            userId.Value,
                            existingWallet.WalletId);

                        return ServiceApiResponse<CreateWalletResponse>.CreateSuccess(
                            existingWallet);
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

                    _logger.LogError(
                        ex,
                        "Failed to create wallet for UserId {UserId}",
                        userId.Value);

                    return ServiceApiResponse<CreateWalletResponse>.SystemMalFunctioned();
                }
            });
        }

        public async Task<ServiceApiResponse<CreateWalletResponse>> RetrieveWalletDetails(
            CancellationToken cancellationToken)
        {
            var userId = _requestContext.UserId;

            if (userId is null)
            {
                return ServiceApiResponse<CreateWalletResponse>.CreateFailure(
                    ResponseCodes.AccessDenied);
            }

            var wallet = await _novaWalletDbContext.Wallets
                .Where(w => w.UserId == userId.Value)
                .Select(WalletResponseProjection)
                .FirstOrDefaultAsync(cancellationToken);

            if (wallet is null)
            {
                _logger.LogWarning(
                    "Wallet not found for UserId {UserId}",
                    userId.Value);

                return ServiceApiResponse<CreateWalletResponse>.CreateFailure(
                    ResponseCodes.NoRecordReturned);
            }

            return ServiceApiResponse<CreateWalletResponse>.CreateSuccess(wallet);
        }

        public async Task<ServiceApiResponse<PagedResponse<AccountEntryResponse>>> GetWalletStatement(
            Guid walletId,
            int pageNumber,
            int pageSize,
            DateTime? fromDate,
            DateTime? toDate,
            CancellationToken cancellationToken)
        {
            var userId = _requestContext.UserId;

            if (userId is null)
            {
                return ServiceApiResponse<PagedResponse<AccountEntryResponse>>.CreateFailure(ResponseCodes.AccessDenied);
            }

            var wallet = await _novaWalletDbContext.Wallets
                .AsNoTracking()
                .Where(w => w.Id == walletId)
                .Select(w => new { w.UserId, w.AccountId })
                .FirstOrDefaultAsync(cancellationToken);

            if (wallet is null)
            {
                return ServiceApiResponse<PagedResponse<AccountEntryResponse>>.CreateFailure(ResponseCodes.NoRecordReturned);
            }

            // Ownership: a valid token alone does not authorize reading any wallet's statement,
            // only the caller's own (mirrors TransferService's source-wallet ownership guard).
            if (wallet.UserId != userId.Value)
            {
                _logger.LogWarning(
                    "Wallet statement rejected - caller {CallerUserId} does not own Wallet {WalletId}.",
                    userId.Value,
                    walletId);

                return ServiceApiResponse<PagedResponse<AccountEntryResponse>>.CreateFailure(ResponseCodes.RequestNotAllowed);
            }

            var clampedPageNumber = pageNumber < 1 ? 1 : pageNumber;
            var clampedPageSize = pageSize < 1
                ? NovaWalletConstants.PaginationConstants.DefaultPageSize
                : Math.Min(pageSize, NovaWalletConstants.PaginationConstants.MaxPageSize);

            var query = _novaWalletDbContext.AccountEntries
                .AsNoTracking()
                .Where(e => e.AccountId == wallet.AccountId);

            if (fromDate is not null)
            {
                query = query.Where(e => e.CreatedAt >= fromDate.Value);
            }

            if (toDate is not null)
            {
                query = query.Where(e => e.CreatedAt <= toDate.Value);
            }

            // Uses IX_AccountEntries_AccountId_CreatedAt (AccountEntryConfiguration) - built
            // for exactly this newest-first, per-account paginated query.
            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(e => e.CreatedAt)
                .Skip((clampedPageNumber - 1) * clampedPageSize)
                .Take(clampedPageSize)
                .Select(e => new AccountEntryResponse
                {
                    Id = e.Id,
                    AmountKobo = e.AmountKobo,
                    EntryType = e.EntryType,
                    TransParticulars = e.TransParticulars,
                    CreatedAt = e.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return ServiceApiResponse<PagedResponse<AccountEntryResponse>>.CreateSuccess(
                new PagedResponse<AccountEntryResponse>(items, totalCount, clampedPageNumber, clampedPageSize));
        }

        private static readonly Expression<Func<Wallet, CreateWalletResponse>> WalletResponseProjection =
            wallet => new CreateWalletResponse
            {
                AccountNumber = wallet.Account == null
                    ? string.Empty
                    : wallet.Account.AccountNumber,
                AvailableBalanceKobo = wallet.AvailableBalanceKobo,
                CurrencyCode = wallet.Currency,
                WalletId = wallet.Id,
                Status = wallet.Status
            };

        public static string GenerateAccountNumber(Guid id)
        {
            var hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(id.ToString("N")));

            var value = BitConverter.ToUInt64(hash, 0) % 10_000_000_000;

            return value.ToString("D10");
        }
    }
}