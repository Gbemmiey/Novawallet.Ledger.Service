using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using System.Net;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// Single-request behaviour of <c>POST /api/v1/wallets/transfer</c>: the happy path, how the
/// source wallet is inferred from the session, request validation, and how each kind of
/// rejection is (or is not) recorded in WalletTransfers.
/// </summary>
[Collection(ApiCollection.Name)]
public class TransferTests
{
    private readonly ApiFixture _fixture;

    public TransferTests(ApiFixture fixture) => _fixture = fixture;

    private static string NewKey() => Guid.NewGuid().ToString();

    private async Task<List<WalletTransfer>> RowsForAsync(string key)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.WalletTransfers.AsNoTracking().Where(t => t.IdempotencyKey == key).ToListAsync();
    }

    // ---------------------------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_MovesTheMoney_AndReturnsAReceipt()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 500_000);
        var receiver = await _fixture.CreateUserAsync();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 120_000, NewKey(), "rent");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("00", body["responseCode"]!.GetValue<string>());

        var data = body["data"]!;
        Assert.Equal(sender.WalletId.ToString(), data["sourceWalletId"]!.GetValue<string>());
        Assert.Equal(receiver.WalletId.ToString(), data["destinationWalletId"]!.GetValue<string>());
        Assert.Equal(120_000, data["amountInKobo"]!.GetValue<long>());
        Assert.Equal("rent", data["narration"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(data["paymentReference"]!.GetValue<string>()));
        Assert.NotNull(data["transactionDate"]);

        Assert.Equal(380_000, await _fixture.GetBalanceAsync(sender));
        Assert.Equal(120_000, await _fixture.GetBalanceAsync(receiver));
    }

    [Fact]
    public async Task Transfer_PostsABalancedJournal_AuditsBothWallets_AndStoresACompletedRow()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 500_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 200_000, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await RowsForAsync(key));
        Assert.Equal(TransferStatus.Completed, row.Status);
        Assert.NotNull(row.JournalEntryId);
        Assert.Null(row.FailureCode);
        Assert.Equal(sender.WalletId, row.SourceWalletId);
        Assert.Equal(receiver.WalletId, row.DestinationWalletId);
        Assert.Equal(200_000, row.AmountKobo);

        await using var db = _fixture.CreateDbContext();
        var lines = await db.AccountEntries.AsNoTracking().Where(e => e.JournalEntryId == row.JournalEntryId).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.Equal(200_000, lines.Single(l => l.EntryType == EntryType.Debit).AmountKobo);
        Assert.Equal(200_000, lines.Single(l => l.EntryType == EntryType.Credit).AmountKobo);

        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.CorrelationId == row.JournalEntryId).ToListAsync();
        Assert.Equal(2, audits.Count);

        var debitAudit = audits.Single(a => a.WalletId == sender.WalletId);
        Assert.Equal("Debit", debitAudit.Action);
        Assert.Equal(500_000, debitAudit.BalanceBeforeKobo);
        Assert.Equal(300_000, debitAudit.BalanceAfterKobo);

        var creditAudit = audits.Single(a => a.WalletId == receiver.WalletId);
        Assert.Equal("Credit", creditAudit.Action);
        Assert.Equal(0, creditAudit.BalanceBeforeKobo);
        Assert.Equal(200_000, creditAudit.BalanceAfterKobo);

        await LedgerAssertions.AssertConsistentAsync(db);
    }

    [Fact]
    public async Task Transfer_OfTheExactBalance_LeavesZero()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 75_000);
        var receiver = await _fixture.CreateUserAsync();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 75_000, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await _fixture.GetBalanceAsync(sender));
        Assert.Equal(75_000, await _fixture.GetBalanceAsync(receiver));
    }

    [Fact]
    public async Task Transfer_WithoutANarration_Succeeds()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, NewKey(), narration: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------
    // The source wallet comes from the session
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_DebitsTheCallersOwnWallet_NotAnyoneElses()
    {
        var alice = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var bob = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var carol = await _fixture.CreateUserAsync();

        var response = await bob.Client.TransferAsync(carol.WalletId, 30_000, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100_000, await _fixture.GetBalanceAsync(alice));
        Assert.Equal(70_000, await _fixture.GetBalanceAsync(bob));
        Assert.Equal(30_000, await _fixture.GetBalanceAsync(carol));
    }

    [Theory]
    [InlineData("other-wallet")]
    [InlineData("")]
    public async Task Transfer_ThatStillSendsASourceWalletId_IsRejected(string mode)
    {
        var victim = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var attacker = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        // Trying to spend from somebody else's wallet, or just sending the field at all.
        var sourceWalletId = mode == "" ? "" : victim.WalletId.ToString();

        var response = await attacker.Client.TransferRawAsync(
            new
            {
                sourceWalletId,
                destinationWalletId = receiver.WalletId.ToString(),
                amountInKobo = 10_000,
                narration = "x"
            },
            key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.ReadJsonAsync())["errors"]!.AsObject();
        Assert.True(errors.ContainsKey("SourceWalletId"));
        Assert.Contains("inferred from your session", errors["SourceWalletId"]![0]!.GetValue<string>());

        Assert.Equal(100_000, await _fixture.GetBalanceAsync(victim));
        Assert.Equal(100_000, await _fixture.GetBalanceAsync(attacker));
        Assert.Empty(await RowsForAsync(key));
    }

    [Fact]
    public async Task Transfer_FromACallerWithNoWallet_Returns404With25()
    {
        var receiver = await _fixture.CreateUserAsync();
        var walletless = await _fixture.LoginAsync(Guid.NewGuid());
        var key = NewKey();

        var response = await walletless.TransferAsync(receiver.WalletId, 1_000, key);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("25", await response.ReadResponseCodeAsync());
        Assert.Empty(await RowsForAsync(key));
    }

    // ---------------------------------------------------------------------------------
    // Validation - none of these are recorded
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_ToAnUnknownWallet_Returns404_AndIsNotRecorded()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var key = NewKey();

        var response = await sender.Client.TransferAsync(Guid.NewGuid(), 1_000, key);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("25", await response.ReadResponseCodeAsync());
        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));
        Assert.Empty(await RowsForAsync(key));
    }

    [Fact]
    public async Task Transfer_ToYourOwnWallet_Returns400With30_AndIsNotRecorded()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var key = NewKey();

        var response = await sender.Client.TransferAsync(sender.WalletId, 1_000, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("30", await response.ReadResponseCodeAsync());
        Assert.Contains("must differ", (await response.ReadReasonAsync())!);
        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));
        Assert.Empty(await RowsForAsync(key));
    }

    [Fact]
    public async Task Transfer_WithoutAnIdempotencyKey_Returns400With30()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("30", await response.ReadResponseCodeAsync());
        Assert.Contains("Idempotency-Key", (await response.ReadReasonAsync())!);
        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));
    }

    [Fact]
    public async Task Transfer_WithAnIdempotencyKeyOver128Characters_Returns400With30()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, new string('k', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("30", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task Transfer_WithAnIdempotencyKeyOfExactly128Characters_IsAccepted()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, new string('k', 128));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public async Task Transfer_OfANonPositiveAmount_Returns400(long amount)
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var response = await sender.Client.TransferAsync(receiver.WalletId, amount, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadJsonAsync())["errors"]!.AsObject().ContainsKey("AmountInKobo"));
        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));
        Assert.Empty(await RowsForAsync(key));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task Transfer_ToAMalformedDestination_Returns400(string destination)
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var key = NewKey();

        var response = await sender.Client.TransferRawAsync(
            new { destinationWalletId = destination, amountInKobo = 1_000, narration = "x" }, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadJsonAsync())["errors"]!.AsObject().ContainsKey("DestinationWalletId"));
        Assert.Empty(await RowsForAsync(key));
    }

    [Fact]
    public async Task Transfer_WithANarrationOver200Characters_Returns400()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, NewKey(), narration: new string('n', 201));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadJsonAsync())["errors"]!.AsObject().ContainsKey("Narration"));
    }

    // ---------------------------------------------------------------------------------
    // Business rejections - recorded as Failed rows
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_WithInsufficientBalance_Returns422With51_AndRecordsTheFailure()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 10_001, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("51", await response.ReadResponseCodeAsync());
        Assert.Equal("Insufficient balance.", await response.ReadReasonAsync());

        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));
        Assert.Equal(0, await _fixture.GetBalanceAsync(receiver));

        var row = Assert.Single(await RowsForAsync(key));
        Assert.Equal(TransferStatus.Failed, row.Status);
        Assert.Equal("51", row.FailureCode);
        Assert.Equal("Insufficient balance.", row.FailureReason);
        Assert.Null(row.JournalEntryId);
        Assert.Equal(10_001, row.AmountKobo);
        Assert.Equal(sender.WalletId, row.SourceWalletId);
        Assert.Equal(receiver.WalletId, row.DestinationWalletId);
    }

    [Fact]
    public async Task Transfer_ThatFails_PostsNothingToTheLedger()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();

        await sender.Client.TransferAsync(receiver.WalletId, 99_999, NewKey());

        await using var db = _fixture.CreateDbContext();
        var senderAccountId = (await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == sender.WalletId)).AccountId;
        var receiverAccountId = (await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == receiver.WalletId)).AccountId;

        // Sender has only the funding credit; receiver has nothing at all.
        Assert.Equal(1, await db.AccountEntries.CountAsync(e => e.AccountId == senderAccountId));
        Assert.Equal(0, await db.AccountEntries.CountAsync(e => e.AccountId == receiverAccountId));
        Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.WalletId == receiver.WalletId));
    }

    [Fact]
    public async Task Transfer_ExceedingTheDailyLimitInOneGo_Returns422With61_AndRecordsIt()
    {
        // Daily outbound limit is 50,000,000 kobo (N500,000).
        var sender = await _fixture.CreateUserAsync(fundKobo: 100_000_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = NewKey();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 50_000_001, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("61", await response.ReadResponseCodeAsync());
        Assert.Equal(100_000_000, await _fixture.GetBalanceAsync(sender));

        var row = Assert.Single(await RowsForAsync(key));
        Assert.Equal(TransferStatus.Failed, row.Status);
        Assert.Equal("61", row.FailureCode);
    }

    [Fact]
    public async Task Transfer_ThatPushesTheDayTotalOverTheLimit_IsRejected_ButUpToTheLimitIsAllowed()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 100_000_000);
        var receiver = await _fixture.CreateUserAsync();

        var first = await sender.Client.TransferAsync(receiver.WalletId, 30_000_000, NewKey());
        var overLimit = await sender.Client.TransferAsync(receiver.WalletId, 30_000_000, NewKey());
        var upToLimit = await sender.Client.TransferAsync(receiver.WalletId, 20_000_000, NewKey());
        var oneKoboMore = await sender.Client.TransferAsync(receiver.WalletId, 1, NewKey());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overLimit.StatusCode);
        Assert.Equal("61", await overLimit.ReadResponseCodeAsync());
        // 30,000,000 + 20,000,000 == the limit exactly, which is still allowed...
        Assert.Equal(HttpStatusCode.OK, upToLimit.StatusCode);
        // ...but not one kobo more.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oneKoboMore.StatusCode);

        Assert.Equal(50_000_000, await _fixture.GetBalanceAsync(sender));
        Assert.Equal(50_000_000, await _fixture.GetBalanceAsync(receiver));
    }

    [Fact]
    public async Task Transfer_ToAFrozenWallet_Returns403With57_AndRecordsIt()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        await _fixture.SetWalletStatusAsync(receiver.WalletId, WalletStatus.Frozen);
        var key = NewKey();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("57", await response.ReadResponseCodeAsync());
        Assert.Contains("Destination wallet is Frozen", (await response.ReadReasonAsync())!);
        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));

        var row = Assert.Single(await RowsForAsync(key));
        Assert.Equal(TransferStatus.Failed, row.Status);
        Assert.Equal("57", row.FailureCode);
    }

    [Fact]
    public async Task Transfer_FromAFrozenWallet_Returns403With57()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        await _fixture.SetWalletStatusAsync(sender.WalletId, WalletStatus.Frozen);
        var key = NewKey();

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("57", await response.ReadResponseCodeAsync());
        Assert.Contains("Source wallet is Frozen", (await response.ReadReasonAsync())!);
        Assert.Equal(10_000, await _fixture.GetBalanceAsync(sender));
        Assert.Equal("57", Assert.Single(await RowsForAsync(key)).FailureCode);
    }

    [Fact]
    public async Task Transfer_AfterAFrozenWalletIsReactivated_Succeeds()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        await _fixture.SetWalletStatusAsync(receiver.WalletId, WalletStatus.Frozen);
        await _fixture.SetWalletStatusAsync(receiver.WalletId, WalletStatus.Active);

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
