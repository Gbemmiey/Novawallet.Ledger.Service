using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;

namespace NovaWallet.Api.Application.Services;

public class TransferService : ITransferService
{
    private readonly ILogger<TransferService> _logger;
    private readonly NovaWalletDbContext  _dbContext;
    private readonly IRequestContext _requestContext;

    public TransferService(ILogger<TransferService> logger, NovaWalletDbContext dbContext, IRequestContext requestContext)
    {
        _logger = logger;
        _dbContext = dbContext;
        _requestContext = requestContext;
    }

    public async Task Transfer(WalletTransferRequest request, CancellationToken cancellationToken)
    {
        // IdempotencyKey ::         _requestContext.RetrieveIdempotencyKey

        throw new NotImplementedException();
    }
}