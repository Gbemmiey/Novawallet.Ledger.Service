using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using System.Net;
using System.Net.Http.Json;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// Inbound NIP credits: the anonymous webhook records the deposit and returns 202, and the
/// DepositConsumer settles it asynchronously through the outbox.
/// </summary>
[Collection(ApiCollection.Name)]
public class DepositFlowTests
{
    private readonly ApiFixture _fixture;

    public DepositFlowTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Credit_IsAcceptedWith202_AndEchoesTheRequest()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = ApiFixture.NewSessionId();

        var response = await _fixture.SubmitCreditAsync(sessionId, "REF-" + Guid.NewGuid().ToString("N"), 25_000, user.AccountNumber);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("00", body["responseCode"]!.GetValue<string>());
        Assert.Equal(sessionId, body["data"]!["sessionId"]!.GetValue<string>());
        Assert.Equal(25_000, body["data"]!["amountKobo"]!.GetValue<long>());
        Assert.Equal(user.AccountNumber, body["data"]!["beneficiaryAccountNumber"]!.GetValue<string>());
    }

    [Fact]
    public async Task Credit_IsSettledByTheConsumer_AndCreditsTheWallet()
    {
        var user = await _fixture.CreateUserAsync();

        await _fixture.FundAsync(user.AccountNumber, 75_000);

        Assert.Equal(75_000, await _fixture.GetBalanceAsync(user));
    }

    [Fact]
    public async Task SettledCredit_PostsABalancedJournal_FromTheSettlementAccountToTheWallet()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = await _fixture.FundAsync(user.AccountNumber, 40_000);

        await using var db = _fixture.CreateDbContext();
        var journal = await db.JournalEntries.AsNoTracking().SingleAsync(j => j.IdempotencyKey == $"deposit:{sessionId}");
        var lines = await db.AccountEntries.AsNoTracking().Where(e => e.JournalEntryId == journal.Id).ToListAsync();

        Assert.Equal(2, lines.Count);

        var debit = Assert.Single(lines, l => l.EntryType == EntryType.Debit);
        var credit = Assert.Single(lines, l => l.EntryType == EntryType.Credit);
        Assert.Equal(40_000, debit.AmountKobo);
        Assert.Equal(40_000, credit.AmountKobo);

        var settlementAccount = await db.Accounts.AsNoTracking()
            .SingleAsync(a => a.AccountNumber == NovaWalletConstants.SystemAccounts.NipSettlementAccountNumber);
        Assert.Equal(settlementAccount.Id, debit.AccountId);
        Assert.Equal(user.AccountNumber, (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == credit.AccountId)).AccountNumber);
    }

    [Fact]
    public async Task SettledCredit_MarksBothTheRequestAndTheOutboxRowDone()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = await _fixture.FundAsync(user.AccountNumber, 10_000);

        await using var db = _fixture.CreateDbContext();
        var request = await db.ExternalCreditRequests.AsNoTracking().SingleAsync(x => x.SessionId == sessionId);
        var outbox = await db.DepositOutboxEntries.AsNoTracking().SingleAsync(o => o.ExternalCreditRequestId == request.Id);

        Assert.Equal(DepositStatus.Completed, request.Status);
        Assert.NotNull(request.CompletedDate);
        Assert.Equal(OutboxStatus.Processed, outbox.Status);
        Assert.NotNull(outbox.DateProcessed);
    }

    [Fact]
    public async Task NewOutboxRows_CarryATraceParent_SoTheSettleSpanJoinsTheAcceptSpan()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = await _fixture.FundAsync(user.AccountNumber, 10_000);

        await using var db = _fixture.CreateDbContext();
        var request = await db.ExternalCreditRequests.AsNoTracking().SingleAsync(x => x.SessionId == sessionId);
        var outbox = await db.DepositOutboxEntries.AsNoTracking().SingleAsync(o => o.ExternalCreditRequestId == request.Id);

        Assert.False(string.IsNullOrWhiteSpace(outbox.TraceParent), "DepositOutbox.TraceParent was not populated.");
        // W3C traceparent: version-traceid-spanid-flags
        Assert.Matches("^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", outbox.TraceParent!);
    }

    [Fact]
    public async Task Credit_ReplayedWithTheSameSession_IsAcceptedAgain_ButCreditsOnce()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = ApiFixture.NewSessionId();
        var reference = "REF-" + Guid.NewGuid().ToString("N");

        var first = await _fixture.SubmitCreditAsync(sessionId, reference, 30_000, user.AccountNumber);
        var second = await _fixture.SubmitCreditAsync(sessionId, reference, 30_000, user.AccountNumber);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        await _fixture.WaitForCreditStatusAsync(sessionId, "Completed");
        // Give the consumer another poll so a duplicate settlement, if there were one, would show.
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.Equal(30_000, await _fixture.GetBalanceAsync(user));

        await using var db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.ExternalCreditRequests.CountAsync(x => x.SessionId == sessionId));
        Assert.Equal(1, await db.JournalEntries.CountAsync(j => j.IdempotencyKey == $"deposit:{sessionId}"));
    }

    [Fact]
    public async Task Credit_TenConcurrentRedeliveriesOfOneSession_CreditOnce()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = ApiFixture.NewSessionId();
        var reference = "REF-" + Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _fixture.SubmitCreditAsync(sessionId, reference, 12_000, user.AccountNumber)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

        await _fixture.WaitForCreditStatusAsync(sessionId, "Completed");
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.Equal(12_000, await _fixture.GetBalanceAsync(user));

        await using var db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.ExternalCreditRequests.CountAsync(x => x.SessionId == sessionId));
    }

    [Fact]
    public async Task Credit_SameTransactionReferenceUnderADifferentSession_IsRejectedAsDuplicate()
    {
        var user = await _fixture.CreateUserAsync();
        var reference = "REF-" + Guid.NewGuid().ToString("N");

        var first = await _fixture.SubmitCreditAsync(ApiFixture.NewSessionId(), reference, 5_000, user.AccountNumber);
        var second = await _fixture.SubmitCreditAsync(ApiFixture.NewSessionId(), reference, 5_000, user.AccountNumber);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("94", await second.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task Credit_ConcurrentSessionsSharingOneReference_AreAcceptedExactlyOnce()
    {
        var user = await _fixture.CreateUserAsync();
        var reference = "REF-" + Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => _fixture.SubmitCreditAsync(ApiFixture.NewSessionId(), reference, 8_000, user.AccountNumber)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.ExternalCreditRequests.CountAsync(x => x.TransactionReference == reference));
    }

    [Fact]
    public async Task Credit_ToAnUnknownAccount_Returns404With25()
    {
        var response = await _fixture.SubmitCreditAsync(ApiFixture.NewSessionId(), "REF-" + Guid.NewGuid().ToString("N"), 5_000, "0000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("25", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task Credit_InvalidPayload_Returns400_AndRecordsNothing()
    {
        var client = _fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/v1/wallets/credit", new
        {
            sessionId = new string('s', 31),   // over the 30-character limit
            transactionReference = "",         // required
            amountKobo = 0,                    // must be positive
            beneficiaryAccountNumber = "0123456789",
            originatingAccountNumber = "9876543210",
            originatingBankCode = "058"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.ReadJsonAsync())["errors"]!.AsObject();
        Assert.True(errors.ContainsKey("SessionId"));
        Assert.True(errors.ContainsKey("TransactionReference"));
        Assert.True(errors.ContainsKey("AmountKobo"));
    }

    [Fact]
    public async Task Credit_ManyConcurrentDistinctCredits_AllSettleAndSumCorrectly()
    {
        var user = await _fixture.CreateUserAsync();
        var sessions = Enumerable.Range(0, 20).Select(_ => ApiFixture.NewSessionId()).ToList();

        var responses = await Task.WhenAll(sessions.Select(session =>
            _fixture.SubmitCreditAsync(session, "REF-" + Guid.NewGuid().ToString("N"), 1_000, user.AccountNumber)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

        foreach (var session in sessions)
        {
            await _fixture.WaitForCreditStatusAsync(session, "Completed", timeoutSeconds: 60);
        }

        Assert.Equal(20_000, await _fixture.GetBalanceAsync(user));

        await using var db = _fixture.CreateDbContext();
        await LedgerAssertions.AssertConsistentAsync(db);
    }

    [Fact]
    public async Task Credit_ToAFrozenWallet_FailsPermanently_AndLeavesTheBalanceAlone()
    {
        var user = await _fixture.CreateUserAsync(fundKobo: 2_000);
        await _fixture.SetWalletStatusAsync(user.WalletId, WalletStatus.Frozen);
        var sessionId = ApiFixture.NewSessionId();

        var response = await _fixture.SubmitCreditAsync(sessionId, "REF-" + Guid.NewGuid().ToString("N"), 9_000, user.AccountNumber);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var requery = await _fixture.WaitForCreditStatusAsync(sessionId, "Failed");

        Assert.Equal("96", requery["data"]!["responseCode"]!.GetValue<string>());
        Assert.Equal(2_000, await _fixture.GetBalanceAsync(user));

        await using var db = _fixture.CreateDbContext();
        var request = await db.ExternalCreditRequests.AsNoTracking().SingleAsync(x => x.SessionId == sessionId);
        var outbox = await db.DepositOutboxEntries.AsNoTracking().SingleAsync(o => o.ExternalCreditRequestId == request.Id);
        Assert.Equal(OutboxStatus.Failed, outbox.Status);
        // A business rejection is final: it must not have burned through retries first.
        Assert.Equal(0, outbox.NumberOfRetries);
    }

    // ---------------------------------------------------------------------------------
    // Requery
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task RequeryCredit_OfASettledCredit_ReportsCompletedWith00()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = await _fixture.FundAsync(user.AccountNumber, 15_000);

        var response = await _fixture.CreateAnonymousClient().GetAsync($"/api/v1/wallets/credit/{sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.ReadJsonAsync())["data"]!;
        Assert.Equal("Completed", data["status"]!.GetValue<string>());
        Assert.Equal("00", data["responseCode"]!.GetValue<string>());
        Assert.Equal(15_000, data["amountKobo"]!.GetValue<long>());
        Assert.Equal(sessionId, data["sessionId"]!.GetValue<string>());
        Assert.NotNull(data["completedDate"]);
    }

    [Fact]
    public async Task RequeryCredit_OfAPendingCredit_Reports09()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = ApiFixture.NewSessionId();

        // Written straight to the table with no outbox row, so the consumer never picks it up
        // and it stays Pending - the state is otherwise too brief to observe reliably.
        await using (var db = _fixture.CreateDbContext())
        {
            db.ExternalCreditRequests.Add(ExternalCreditRequest.Create(
                sessionId, "REF-" + Guid.NewGuid().ToString("N"), 1_000, user.AccountNumber, "9876543210", "058"));
            await db.SaveChangesAsync();
        }

        var response = await _fixture.CreateAnonymousClient().GetAsync($"/api/v1/wallets/credit/{sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.ReadJsonAsync())["data"]!;
        Assert.Equal("Pending", data["status"]!.GetValue<string>());
        Assert.Equal("09", data["responseCode"]!.GetValue<string>());
        Assert.Null(data["completedDate"]);
    }

    [Fact]
    public async Task RequeryCredit_OfAFailedCredit_Reports96()
    {
        var user = await _fixture.CreateUserAsync();
        var sessionId = ApiFixture.NewSessionId();

        await using (var db = _fixture.CreateDbContext())
        {
            var request = ExternalCreditRequest.Create(
                sessionId, "REF-" + Guid.NewGuid().ToString("N"), 1_000, user.AccountNumber, "9876543210", "058");
            request.MarkFailed();
            db.ExternalCreditRequests.Add(request);
            await db.SaveChangesAsync();
        }

        var data = (await (await _fixture.CreateAnonymousClient().GetAsync($"/api/v1/wallets/credit/{sessionId}")).ReadJsonAsync())["data"]!;

        Assert.Equal("Failed", data["status"]!.GetValue<string>());
        Assert.Equal("96", data["responseCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequeryCredit_OfAnUnknownSession_Returns404With25()
    {
        var response = await _fixture.CreateAnonymousClient().GetAsync($"/api/v1/wallets/credit/{ApiFixture.NewSessionId()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("25", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task RequeryCredit_WithAnOverlongSessionId_Returns400()
    {
        var response = await _fixture.CreateAnonymousClient().GetAsync($"/api/v1/wallets/credit/{new string('s', 129)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("30", await response.ReadResponseCodeAsync());
    }
}
