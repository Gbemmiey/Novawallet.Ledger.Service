using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using NovaWallet.Domain.Enums;
using System.Net;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// README "Concurrency Load Test Highlight": many simultaneous requests against one wallet must
/// never double-spend or drive a balance negative.
/// </summary>
[Collection(ApiCollection.Name)]
public class ConcurrentTransferTests
{
    private readonly ApiFixture _fixture;

    public ConcurrentTransferTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Transfer_ConcurrentRequests_GuaranteesNonNegativeBalanceAndNoDoubleSpend()
    {
        // Arrange: 50 requests of 50,000 kobo (2.5m kobo in total) against a 1.0m kobo balance.
        const long balance = 1_000_000;
        const long amount = 50_000;
        const int requests = 50;

        var sender = await _fixture.CreateUserAsync(fundKobo: balance);
        var receiver = await _fixture.CreateUserAsync();

        // Act: all 50 in flight at once, each under its own Idempotency-Key.
        var responses = await Task.WhenAll(Enumerable.Range(0, requests)
            .Select(_ => sender.Client.TransferAsync(receiver.WalletId, amount, Guid.NewGuid().ToString())));

        // Assert
        var succeeded = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ToList();

        Assert.Equal(20, succeeded.Count);   // exactly 20 x 50,000 fit in 1,000,000
        Assert.Equal(30, rejected.Count);    // the rest are rejected as insufficient balance
        Assert.Equal(requests, succeeded.Count + rejected.Count); // and nothing else (no 5xx, no 429)

        foreach (var response in rejected)
        {
            Assert.Equal("51", await response.ReadResponseCodeAsync());
            Assert.Equal("Insufficient balance.", await response.ReadReasonAsync());
        }

        var senderBalance = await _fixture.GetBalanceAsync(sender);
        var receiverBalance = await _fixture.GetBalanceAsync(receiver);

        Assert.True(senderBalance >= 0, "Balance drifted into negative!");
        Assert.Equal(0, senderBalance);                       // exactly 20 succeeded, 30 rejected
        Assert.Equal(balance, receiverBalance);               // every debited kobo arrived
        Assert.Equal(balance, senderBalance + receiverBalance); // money is conserved

        await using var db = _fixture.CreateDbContext();

        var rows = await db.WalletTransfers.AsNoTracking()
            .Where(t => t.SourceWalletId == sender.WalletId)
            .ToListAsync();

        Assert.Equal(20, rows.Count(t => t.Status == TransferStatus.Completed));

        var failedRows = rows.Where(t => t.Status == TransferStatus.Failed).ToList();
        Assert.Equal(30, failedRows.Count);
        Assert.All(failedRows, row =>
        {
            Assert.Equal("51", row.FailureCode);
            Assert.Equal("Insufficient balance.", row.FailureReason);
            Assert.Null(row.JournalEntryId);
        });

        await LedgerAssertions.AssertConsistentAsync(db);
    }

    [Fact]
    public async Task Transfer_ConcurrentTransfersInBothDirections_DoNotDeadlock_AndConserveMoney()
    {
        // A->B and B->A at the same time is the classic lock-ordering deadlock. Both sides are
        // funded well beyond what they send, so every single request must succeed.
        var a = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var b = await _fixture.CreateUserAsync(fundKobo: 1_000_000);

        var aToB = Enumerable.Range(0, 25).Select(_ => a.Client.TransferAsync(b.WalletId, 1_000, Guid.NewGuid().ToString()));
        var bToA = Enumerable.Range(0, 25).Select(_ => b.Client.TransferAsync(a.WalletId, 1_000, Guid.NewGuid().ToString()));

        var responses = await Task.WhenAll(aToB.Concat(bToA));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        // 25 sent and 25 received of the same amount each: both balances are unchanged.
        Assert.Equal(1_000_000, await _fixture.GetBalanceAsync(a));
        Assert.Equal(1_000_000, await _fixture.GetBalanceAsync(b));

        await using var db = _fixture.CreateDbContext();
        await LedgerAssertions.AssertConsistentAsync(db);
    }

    [Fact]
    public async Task Transfer_ManyReceiversAtOnce_FromOneSender_NeverOverspends()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var receivers = new List<TestUser>();
        for (var i = 0; i < 10; i++)
        {
            receivers.Add(await _fixture.CreateUserAsync());
        }

        // 10 receivers x 3 requests x 10,000 = 300,000 attempted against 100,000.
        var responses = await Task.WhenAll(receivers.SelectMany(receiver => Enumerable.Range(0, 3)
            .Select(_ => sender.Client.TransferAsync(receiver.WalletId, 10_000, Guid.NewGuid().ToString()))));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0, await _fixture.GetBalanceAsync(sender));

        long received = 0;
        foreach (var receiver in receivers)
        {
            received += await _fixture.GetBalanceAsync(receiver);
        }

        Assert.Equal(100_000, received);

        await using var db = _fixture.CreateDbContext();
        await LedgerAssertions.AssertConsistentAsync(db);
    }

    [Fact]
    public async Task Transfer_ConcurrentSpendingAndFunding_KeepsTheLedgerConsistent()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 200_000);
        var receiver = await _fixture.CreateUserAsync();

        var transfers = Enumerable.Range(0, 20)
            .Select(_ => sender.Client.TransferAsync(receiver.WalletId, 5_000, Guid.NewGuid().ToString()));

        var fundingSessions = Enumerable.Range(0, 10).Select(_ => ApiFixture.NewSessionId()).ToList();
        var fundings = fundingSessions.Select(session =>
            _fixture.SubmitCreditAsync(session, "REF-" + Guid.NewGuid().ToString("N"), 2_000, sender.AccountNumber));

        var all = await Task.WhenAll(transfers.Concat(fundings));
        Assert.All(all, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, $"Unexpected status {(int)r.StatusCode}."));

        foreach (var session in fundingSessions)
        {
            await _fixture.WaitForCreditStatusAsync(session, "Completed", timeoutSeconds: 60);
        }

        // 200,000 + 10 x 2,000 - 20 x 5,000
        Assert.Equal(120_000, await _fixture.GetBalanceAsync(sender));
        Assert.Equal(100_000, await _fixture.GetBalanceAsync(receiver));

        await using var db = _fixture.CreateDbContext();
        await LedgerAssertions.AssertConsistentAsync(db);
    }
}
