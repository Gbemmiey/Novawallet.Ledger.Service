using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Infrastructure.Data;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace NovaWallet.Api.IntegrationTests.Infrastructure;

public static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> is true, or fails the test with a message.</summary>
    public static async Task UntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, Func<string>? failureMessage = null)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException(failureMessage?.Invoke() ?? $"Condition not met within {timeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }
}

public static class HttpExtensions
{
    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonNode.Parse(text)
            ?? throw new Xunit.Sdk.XunitException($"Response was not JSON (HTTP {(int)response.StatusCode}): '{text}'");
    }

    /// <summary>
    /// The NIP response code of a response, wherever the API put it: failures are ProblemDetails
    /// with a <c>responseCode</c> extension, successes and rate-limit rejections use the envelope.
    /// </summary>
    public static async Task<string?> ReadResponseCodeAsync(this HttpResponseMessage response)
    {
        return (await response.ReadJsonAsync())["responseCode"]?.GetValue<string>();
    }

    /// <summary>The human-readable reason: <c>detail</c> on a failure, <c>responseMessage</c> otherwise.</summary>
    public static async Task<string?> ReadReasonAsync(this HttpResponseMessage response)
    {
        var json = await response.ReadJsonAsync();
        return json["detail"]?.GetValue<string>() ?? json["responseMessage"]?.GetValue<string>();
    }

    public static Task<HttpResponseMessage> TransferAsync(
        this HttpClient client, Guid destinationWalletId, long amountKobo, string? idempotencyKey, string? narration = "test transfer")
    {
        return client.TransferRawAsync(
            new { destinationWalletId = destinationWalletId.ToString(), amountInKobo = amountKobo, narration },
            idempotencyKey);
    }

    /// <summary>Posts an arbitrary body to the transfer endpoint, for malformed-request tests.</summary>
    public static Task<HttpResponseMessage> TransferRawAsync(this HttpClient client, object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/wallets/transfer")
        {
            Content = JsonContent.Create(body)
        };

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> RequeryTransferAsync(this HttpClient client, string idempotencyKey) =>
        client.GetAsync($"/api/v1/wallets/transfer/{idempotencyKey}");
}

public static class LedgerAssertions
{
    /// <summary>
    /// The invariants that must hold at any moment when no request is in flight:
    /// every journal entry balances, every wallet balance equals its ledger (credits minus
    /// debits), no balance is negative, and the WalletTransfers table is internally consistent.
    /// </summary>
    public static async Task AssertConsistentAsync(NovaWalletDbContext db)
    {
        var journalNets = await db.AccountEntries
            .GroupBy(e => e.JournalEntryId)
            .Select(g => new
            {
                JournalEntryId = g.Key,
                Net = g.Sum(e => e.EntryType == EntryType.Credit ? e.AmountKobo : -e.AmountKobo)
            })
            .ToListAsync();

        Assert.All(journalNets, journal => Assert.True(
            journal.Net == 0, $"Journal entry {journal.JournalEntryId} is unbalanced by {journal.Net} kobo."));

        var ledgerByAccount = await db.AccountEntries
            .GroupBy(e => e.AccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                Net = g.Sum(e => e.EntryType == EntryType.Credit ? e.AmountKobo : -e.AmountKobo)
            })
            .ToDictionaryAsync(x => x.AccountId, x => x.Net);

        var wallets = await db.Wallets
            .AsNoTracking()
            .Select(w => new { w.Id, w.AccountId, w.AvailableBalanceKobo })
            .ToListAsync();

        foreach (var wallet in wallets)
        {
            Assert.True(wallet.AvailableBalanceKobo >= 0, $"Wallet {wallet.Id} has a negative balance.");

            var ledgerBalance = ledgerByAccount.GetValueOrDefault(wallet.AccountId);
            Assert.True(
                wallet.AvailableBalanceKobo == ledgerBalance,
                $"Wallet {wallet.Id} balance {wallet.AvailableBalanceKobo} != ledger balance {ledgerBalance}.");
        }

        var transfers = await db.WalletTransfers.AsNoTracking().ToListAsync();

        Assert.Equal(transfers.Count, transfers.Select(t => t.IdempotencyKey).Distinct().Count());

        foreach (var transfer in transfers)
        {
            if (transfer.Status == TransferStatus.Failed)
            {
                Assert.Null(transfer.JournalEntryId);
                Assert.False(string.IsNullOrWhiteSpace(transfer.FailureCode));
            }
            else
            {
                Assert.NotNull(transfer.JournalEntryId);
                Assert.Null(transfer.FailureCode);
            }
        }
    }
}
