using NovaWallet.Api.IntegrationTests.Infrastructure;
using NovaWallet.Domain.Enums;
using System.Net;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// <c>GET /api/v1/wallets/transfer/{idempotencyKey}</c>: the stored outcome of a transfer, scoped
/// to the caller.
/// </summary>
[Collection(ApiCollection.Name)]
public class TransferRequeryTests
{
    private readonly ApiFixture _fixture;

    public TransferRequeryTests(ApiFixture fixture) => _fixture = fixture;

    private static string NewKey() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Requery_OfACompletedTransfer_ReportsCompletedWith00_AndTheSameReceipt()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var transfer = await sender.Client.TransferAsync(receiver.WalletId, 40_000, key, "rent");
        var receipt = (await transfer.ReadJsonAsync())["data"]!;

        var response = await sender.Client.RequeryTransferAsync(key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("00", body["responseCode"]!.GetValue<string>());

        var data = body["data"]!;
        Assert.Equal(key, data["idempotencyKey"]!.GetValue<string>());
        Assert.Equal("Completed", data["status"]!.GetValue<string>());
        Assert.Equal("00", data["responseCode"]!.GetValue<string>());
        Assert.Null(data["failureReason"]);
        Assert.Equal(40_000, data["amountInKobo"]!.GetValue<long>());
        Assert.Equal("rent", data["narration"]!.GetValue<string>());
        Assert.Equal(sender.WalletId.ToString(), data["sourceWalletId"]!.GetValue<string>());
        Assert.Equal(receiver.WalletId.ToString(), data["destinationWalletId"]!.GetValue<string>());
        Assert.Equal(receipt["paymentReference"]!.GetValue<string>(), data["paymentReference"]!.GetValue<string>());
        Assert.Equal(
            receipt["transactionDate"]!.GetValue<DateTime>(),
            data["transactionDate"]!.GetValue<DateTime>());
    }

    [Fact]
    public async Task Requery_OfAFailedTransfer_ReportsTheFailureCodeAndReason()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        await sender.Client.TransferAsync(receiver.WalletId, 50_000, key);

        var response = await sender.Client.RequeryTransferAsync(key);

        // The requery itself succeeded; the transfer it reports on is the thing that failed.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.ReadJsonAsync())["data"]!;
        Assert.Equal("Failed", data["status"]!.GetValue<string>());
        Assert.Equal("51", data["responseCode"]!.GetValue<string>());
        Assert.Equal("Insufficient balance.", data["failureReason"]!.GetValue<string>());
        Assert.Equal(50_000, data["amountInKobo"]!.GetValue<long>());
    }

    [Fact]
    public async Task Requery_OfAFrozenWalletRejection_Reports57()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        await _fixture.SetWalletStatusAsync(receiver.WalletId, WalletStatus.Frozen);
        var key = NewKey();

        await sender.Client.TransferAsync(receiver.WalletId, 1_000, key);

        var data = (await (await sender.Client.RequeryTransferAsync(key)).ReadJsonAsync())["data"]!;

        Assert.Equal("Failed", data["status"]!.GetValue<string>());
        Assert.Equal("57", data["responseCode"]!.GetValue<string>());
        Assert.Contains("Destination wallet is Frozen", data["failureReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requery_OfAnUnknownKey_Returns404With25()
    {
        var user = await _fixture.CreateUserAsync();

        var response = await user.Client.RequeryTransferAsync(NewKey());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("25", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task Requery_OfATransferThatWasNeverRecorded_Returns404()
    {
        // Validation errors and missing wallets are deliberately not stored, so there is nothing to find.
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var key = NewKey();

        await sender.Client.TransferAsync(Guid.NewGuid(), 1_000, key); // unknown destination

        var response = await sender.Client.RequeryTransferAsync(key);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Requery_OfSomeoneElsesTransfer_Returns404_NotTheirReceipt()
    {
        var alice = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var receiver = await _fixture.CreateUserAsync();
        var mallory = await _fixture.CreateUserAsync();
        var key = NewKey();

        await alice.Client.TransferAsync(receiver.WalletId, 10_000, key);

        var asMallory = await mallory.Client.RequeryTransferAsync(key);
        var asReceiver = await receiver.Client.RequeryTransferAsync(key);

        // Scoped to the source wallet's owner: not even the recipient can read it.
        Assert.Equal(HttpStatusCode.NotFound, asMallory.StatusCode);
        Assert.Equal("25", await asMallory.ReadResponseCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, asReceiver.StatusCode);
    }

    [Fact]
    public async Task Requery_WithoutAToken_Returns401()
    {
        var response = await _fixture.CreateAnonymousClient().GetAsync($"/api/v1/wallets/transfer/{NewKey()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requery_WithAKeyOver128Characters_Returns400With30()
    {
        var user = await _fixture.CreateUserAsync();

        var response = await user.Client.RequeryTransferAsync(new string('k', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("30", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task Requery_IsReadOnly_AndRepeatable()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();
        await sender.Client.TransferAsync(receiver.WalletId, 10_000, key);

        var first = await (await sender.Client.RequeryTransferAsync(key)).Content.ReadAsStringAsync();
        var second = await (await sender.Client.RequeryTransferAsync(key)).Content.ReadAsStringAsync();

        Assert.Equal(first, second);
        Assert.Equal(90_000, await _fixture.GetBalanceAsync(sender));
    }
}
