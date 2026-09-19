using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NovaWallet.Api.Application.Services;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Data;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry;

namespace NovaWallet.Api.UnitTests.Services;

/// <summary>
/// Covers the request-validation guards that run before any database work. The DbContext is built
/// against an unreachable connection string on purpose: if a guard ever let a request through to
/// the database, the test would fail with a connection error instead of passing silently.
/// Everything that touches SQL is covered by the integration tests.
/// </summary>
public sealed class TransferServiceGuardTests : IDisposable
{
    private readonly NovaWalletDbContext _dbContext = new(
        new DbContextOptionsBuilder<NovaWalletDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Timeout=1")
            .Options);

    private readonly NovaWalletMetrics _metrics = new();
    private readonly Mock<IRequestContext> _requestContext = new();

    private TransferService NewService() =>
        new(NullLogger<TransferService>.Instance, _dbContext, _requestContext.Object, _metrics);

    private void SignedInAs(Guid? userId, string? idempotencyKey)
    {
        _requestContext.SetupGet(c => c.UserId).Returns(userId);
        _requestContext.SetupGet(c => c.RetrieveIdempotencyKey).Returns(idempotencyKey);
    }

    private static WalletTransferRequest ValidRequest() => new()
    {
        DestinationWalletId = Guid.NewGuid().ToString(),
        AmountInKobo = 1_000,
        Narration = "n"
    };

    public void Dispose()
    {
        _dbContext.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task Transfer_WithoutAuthenticatedUser_IsAccessDenied()
    {
        SignedInAs(null, "key");

        var response = await NewService().Transfer(ValidRequest(), CancellationToken.None);

        Assert.Equal("63", response.ResponseCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Transfer_WithoutIdempotencyKey_IsFormatError(string? key)
    {
        SignedInAs(Guid.NewGuid(), key);

        var response = await NewService().Transfer(ValidRequest(), CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
        Assert.Contains("Idempotency-Key header is required", response.ResponseMessage);
    }

    [Fact]
    public async Task Transfer_IdempotencyKeyOver128Chars_IsFormatError()
    {
        SignedInAs(Guid.NewGuid(), new string('k', 129));

        var response = await NewService().Transfer(ValidRequest(), CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
        Assert.Contains("128", response.ResponseMessage);
    }

    [Fact]
    public async Task Transfer_IdempotencyKeyAt128Chars_PassesTheKeyGuard()
    {
        // 128 is the inclusive limit: it must get past the guards and reach the DB (which is
        // unreachable here, so the service reports a system malfunction rather than a format error).
        SignedInAs(Guid.NewGuid(), new string('k', 128));

        var response = await NewService().Transfer(ValidRequest(), CancellationToken.None);

        Assert.Equal("96", response.ResponseCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task Transfer_InvalidDestination_IsFormatError(string destination)
    {
        SignedInAs(Guid.NewGuid(), "key");
        var request = ValidRequest();
        request.DestinationWalletId = destination;

        var response = await NewService().Transfer(request, CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Transfer_NonPositiveAmount_IsFormatError(long amount)
    {
        SignedInAs(Guid.NewGuid(), "key");
        var request = ValidRequest();
        request.AmountInKobo = amount;

        var response = await NewService().Transfer(request, CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
    }

    [Fact]
    public async Task RequeryTransfer_WithoutAuthenticatedUser_IsAccessDenied()
    {
        SignedInAs(null, null);

        var response = await NewService().RequeryTransfer("key", CancellationToken.None);

        Assert.Equal("63", response.ResponseCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequeryTransfer_BlankKey_IsFormatError(string key)
    {
        SignedInAs(Guid.NewGuid(), null);

        var response = await NewService().RequeryTransfer(key, CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
    }

    [Fact]
    public async Task RequeryTransfer_KeyOver128Chars_IsFormatError()
    {
        SignedInAs(Guid.NewGuid(), null);

        var response = await NewService().RequeryTransfer(new string('k', 129), CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
    }
}

public sealed class DepositServiceGuardTests : IDisposable
{
    private readonly NovaWalletDbContext _dbContext = new(
        new DbContextOptionsBuilder<NovaWalletDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Timeout=1")
            .Options);

    private readonly NovaWalletMetrics _metrics = new();

    private DepositService NewService() =>
        new(NullLogger<DepositService>.Instance, _dbContext, Mock.Of<HybridCache>(), _metrics);

    public void Dispose()
    {
        _dbContext.Dispose();
        _metrics.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequeryDeposit_BlankSessionId_IsFormatError(string sessionId)
    {
        var response = await NewService().RequeryDeposit(sessionId, CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
    }

    [Fact]
    public async Task RequeryDeposit_SessionIdOver128Chars_IsFormatError()
    {
        var response = await NewService().RequeryDeposit(new string('s', 129), CancellationToken.None);

        Assert.Equal("30", response.ResponseCode);
    }
}

public class WalletServiceTests
{
    [Fact]
    public void GenerateAccountNumber_IsAlwaysTenDigits()
    {
        for (var i = 0; i < 200; i++)
        {
            var number = WalletService.GenerateAccountNumber(Guid.NewGuid());

            Assert.Equal(10, number.Length);
            Assert.All(number, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }

    [Fact]
    public void GenerateAccountNumber_IsDeterministicPerUser()
    {
        var id = Guid.NewGuid();

        Assert.Equal(WalletService.GenerateAccountNumber(id), WalletService.GenerateAccountNumber(id));
    }

    [Fact]
    public void GenerateAccountNumber_DiffersAcrossUsers()
    {
        var numbers = Enumerable.Range(0, 500)
            .Select(_ => WalletService.GenerateAccountNumber(Guid.NewGuid()))
            .ToHashSet();

        // 10-digit space: a collision in 500 draws is astronomically unlikely, so any repeat
        // would mean the generator is not really depending on its input.
        Assert.Equal(500, numbers.Count);
    }
}
