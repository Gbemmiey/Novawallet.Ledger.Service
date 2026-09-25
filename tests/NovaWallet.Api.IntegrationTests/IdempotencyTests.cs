using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using NovaWallet.Domain.Enums;
using System.Net;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// README "Idempotency Regression Test Highlight": a replayed key with an identical payload
/// returns the original result without a second debit, and a reused key with a different payload
/// is rejected outright - for failed transfers as well as successful ones.
/// </summary>
[Collection(ApiCollection.Name)]
public class IdempotencyTests
{
    private readonly ApiFixture _fixture;

    public IdempotencyTests(ApiFixture fixture) => _fixture = fixture;

    private static string NewKey() => Guid.NewGuid().ToString();

    private async Task<int> RowCountAsync(string key)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.WalletTransfers.CountAsync(t => t.IdempotencyKey == key);
    }

    [Fact]
    public async Task Transfer_ReplayedIdempotencyKey_ReturnsSameResultWithoutDoubleDebit()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var first = await sender.Client.TransferAsync(receiver.WalletId, 50_000, key);
        var second = await sender.Client.TransferAsync(receiver.WalletId, 50_000, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // Byte-for-byte identical, including the transaction timestamp and payment reference.
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        Assert.Equal(1_000_000 - 50_000, await _fixture.GetBalanceAsync(sender)); // debited exactly once
        Assert.Equal(50_000, await _fixture.GetBalanceAsync(receiver));
        Assert.Equal(1, await RowCountAsync(key));
    }

    [Fact]
    public async Task Transfer_ReusedIdempotencyKeyWithADifferentAmount_ReturnsConflict()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        await sender.Client.TransferAsync(receiver.WalletId, 50_000, key);
        var response = await sender.Client.TransferAsync(receiver.WalletId, 75_000, key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("26", await response.ReadResponseCodeAsync());
        Assert.Contains("different request payload", (await response.ReadReasonAsync())!);

        // Only the first request took effect.
        Assert.Equal(950_000, await _fixture.GetBalanceAsync(sender));
        Assert.Equal(50_000, await _fixture.GetBalanceAsync(receiver));
        Assert.Equal(1, await RowCountAsync(key));
    }

    [Fact]
    public async Task Transfer_ReusedIdempotencyKeyWithADifferentDestination_ReturnsConflict()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var first = await _fixture.CreateUserAsync();
        var second = await _fixture.CreateUserAsync();
        var key = NewKey();

        await sender.Client.TransferAsync(first.WalletId, 10_000, key);
        var response = await sender.Client.TransferAsync(second.WalletId, 10_000, key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await _fixture.GetBalanceAsync(second));
    }

    [Fact]
    public async Task Transfer_ReusedIdempotencyKeyWithADifferentNarration_ReturnsConflict()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        await sender.Client.TransferAsync(receiver.WalletId, 10_000, key, narration: "one");
        var response = await sender.Client.TransferAsync(receiver.WalletId, 10_000, key, narration: "two");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ReplayIsCaseInsensitiveAboutTheDestinationGuid()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var lower = await sender.Client.TransferRawAsync(
            new { destinationWalletId = receiver.WalletId.ToString().ToLowerInvariant(), amountInKobo = 5_000, narration = "n" }, key);
        var upper = await sender.Client.TransferRawAsync(
            new { destinationWalletId = receiver.WalletId.ToString().ToUpperInvariant(), amountInKobo = 5_000, narration = "n" }, key);

        // Same wallet, different spelling: a replay, not a conflict, and not a second debit.
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(995_000, await _fixture.GetBalanceAsync(sender));
    }

    [Fact]
    public async Task Transfer_ReplayOfAFailedTransfer_ReturnsTheStoredFailure_AndStoresNoSecondRow()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var first = await sender.Client.TransferAsync(receiver.WalletId, 20_000, key);
        var second = await sender.Client.TransferAsync(receiver.WalletId, 20_000, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("51", await second.ReadResponseCodeAsync());
        Assert.Equal("Insufficient balance.", await second.ReadReasonAsync());
        Assert.Equal(1, await RowCountAsync(key));
    }

    [Fact]
    public async Task Transfer_AFailedKeyStaysFailed_EvenAfterTheSenderIsToppedUp()
    {
        // One row per key: a stored failure is final for that key, so the client needs a new key.
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var failed = await sender.Client.TransferAsync(receiver.WalletId, 20_000, key);
        await _fixture.FundAsync(sender.AccountNumber, 50_000);
        var retriedSameKey = await sender.Client.TransferAsync(receiver.WalletId, 20_000, key);
        var retriedNewKey = await sender.Client.TransferAsync(receiver.WalletId, 20_000, NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retriedSameKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retriedNewKey.StatusCode);
        Assert.Equal(40_000, await _fixture.GetBalanceAsync(sender));
    }

    [Fact]
    public async Task Transfer_ReusingAFailedKeyWithADifferentPayload_ReturnsConflict()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        await sender.Client.TransferAsync(receiver.WalletId, 20_000, key); // fails: insufficient
        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, key); // would succeed, different payload

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("26", await response.ReadResponseCodeAsync());
        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));
    }

    [Fact]
    public async Task Transfer_AnotherUsersKey_IsRejected_AndLeaksNothingAboutTheOriginal()
    {
        var alice = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var bob = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var alicesTransfer = await alice.Client.TransferAsync(receiver.WalletId, 25_000, key);
        var alicesPaymentReference = (await alicesTransfer.ReadJsonAsync())["data"]!["paymentReference"]!.GetValue<string>();

        // Bob replays Alice's key with a byte-identical body. He must not receive her receipt.
        var bobsAttempt = await bob.Client.TransferAsync(receiver.WalletId, 25_000, key);

        Assert.Equal(HttpStatusCode.Conflict, bobsAttempt.StatusCode);
        Assert.Equal("26", await bobsAttempt.ReadResponseCodeAsync());

        var bobsBody = await bobsAttempt.Content.ReadAsStringAsync();
        Assert.DoesNotContain(alicesPaymentReference, bobsBody);
        Assert.DoesNotContain(alice.WalletId.ToString(), bobsBody, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(100_000, await _fixture.GetBalanceAsync(bob));
        Assert.Equal(75_000, await _fixture.GetBalanceAsync(alice));
        Assert.Equal(1, await RowCountAsync(key));
    }

    [Fact]
    public async Task Transfer_TenConcurrentRequestsWithTheSameKey_ProduceOneTransferAndOneDebit()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => sender.Client.TransferAsync(receiver.WalletId, 50_000, key)));

        // Losers of the insert race must resolve to the winner's stored result - never an error.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var references = new HashSet<string>();
        foreach (var response in responses)
        {
            references.Add((await response.ReadJsonAsync())["data"]!["paymentReference"]!.GetValue<string>());
        }

        Assert.Single(references);

        Assert.Equal(950_000, await _fixture.GetBalanceAsync(sender));
        Assert.Equal(50_000, await _fixture.GetBalanceAsync(receiver));
        Assert.Equal(1, await RowCountAsync(key));

        await using var db = _fixture.CreateDbContext();
        await LedgerAssertions.AssertConsistentAsync(db);
    }

    [Fact]
    public async Task Transfer_ConcurrentRequestsWithTheSameKeyButDifferentAmounts_OnlyOneWins()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(1, 8)
            .Select(i => sender.Client.TransferAsync(receiver.WalletId, i * 1_000, key)));

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflicts = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, succeeded);
        Assert.Equal(7, conflicts);
        Assert.Equal(1, await RowCountAsync(key));

        await using var db = _fixture.CreateDbContext();
        var row = await db.WalletTransfers.AsNoTracking().SingleAsync(t => t.IdempotencyKey == key);
        Assert.Equal(TransferStatus.Completed, row.Status);
        Assert.Equal(1_000_000 - row.AmountKobo, await _fixture.GetBalanceAsync(sender));
        await LedgerAssertions.AssertConsistentAsync(db);
    }
}
