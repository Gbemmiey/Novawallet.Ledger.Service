using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Infrastructure.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace NovaWallet.Api.IntegrationTests.Infrastructure;

/// <summary>A signed-in customer with a wallet, plus a client that already carries their token.</summary>
public sealed record TestUser(Guid UserId, HttpClient Client, Guid WalletId, string AccountNumber);

/// <summary>
/// Shared by every test in a collection: a fresh database with the real EF migrations applied and
/// the real API running on top of it. Tests never share users or wallets, so they stay independent
/// even though they share the database.
/// </summary>
public class ApiFixture : IAsyncLifetime
{
    public ApiFactory Factory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    /// <summary>Environment-variable style overrides (e.g. <c>RateLimiting__LoginPermitLimit</c>).</summary>
    protected virtual IReadOnlyDictionary<string, string> Settings => NoOverrides;

    private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

    public async Task InitializeAsync()
    {
        ConnectionString = await PostgresHost.CreateDatabaseAsync();

        // Applying the migrations here (rather than trusting the API to) means the real migration
        // chain is exercised, and a broken migration fails every test loudly and immediately.
        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        Factory = new ApiFactory(ConnectionString, Settings);

        // Forces the host to build and start, so start-up problems surface here, not mid-test.
        _ = Factory.Server;
    }

    public Task DisposeAsync()
    {
        Factory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A context for direct database assertions, independent of the API's own scopes.</summary>
    public NovaWalletDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<NovaWalletDbContext>().UseNpgsql(ConnectionString).Options);

    public HttpClient CreateAnonymousClient() => Factory.CreateClient();

    // -------------------------------------------------------------------------------------
    // Sign-in, wallets, funding
    // -------------------------------------------------------------------------------------

    /// <summary>Logs in through the real endpoint and returns a client carrying the bearer token.</summary>
    public async Task<HttpClient> LoginAsync(Guid userId, UserRole role = UserRole.Customer)
    {
        var anonymous = CreateAnonymousClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { userId, role = role.ToString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var token = (await response.ReadJsonAsync())["data"]!["accessToken"]!.GetValue<string>();

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public Task<HttpClient> LoginAsAdminAsync() => LoginAsync(Guid.NewGuid(), UserRole.Admin);

    /// <summary>Signs in a brand-new user, creates their wallet and optionally funds it.</summary>
    public async Task<TestUser> CreateUserAsync(long fundKobo = 0)
    {
        var userId = Guid.NewGuid();
        var client = await LoginAsync(userId);

        var response = await client.PostAsync("/api/v1/wallets", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var data = (await response.ReadJsonAsync())["data"]!;
        var user = new TestUser(
            userId,
            client,
            Guid.Parse(data["walletId"]!.GetValue<string>()),
            data["accountNumber"]!.GetValue<string>());

        if (fundKobo > 0)
        {
            await FundAsync(user.AccountNumber, fundKobo);
        }

        return user;
    }

    /// <summary>
    /// Sends an inbound NIP credit through the anonymous webhook, then waits for the deposit
    /// consumer to settle it (the endpoint itself only accepts and returns 202).
    /// </summary>
    public async Task<string> FundAsync(string accountNumber, long amountKobo)
    {
        var sessionId = NewSessionId();

        var response = await SubmitCreditAsync(sessionId, "REF-" + Guid.NewGuid().ToString("N"), amountKobo, accountNumber);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await WaitForCreditStatusAsync(sessionId, "Completed");
        return sessionId;
    }

    public Task<HttpResponseMessage> SubmitCreditAsync(
        string sessionId, string transactionReference, long amountKobo, string beneficiaryAccountNumber)
    {
        return CreateAnonymousClient().PostAsJsonAsync("/api/v1/wallets/credit", new
        {
            sessionId,
            transactionReference,
            amountKobo,
            beneficiaryAccountNumber,
            originatingAccountNumber = "9876543210",
            originatingBankCode = "058",
            narration = "integration test"
        });
    }

    /// <summary>SessionId is limited to 30 characters.</summary>
    public static string NewSessionId() => Guid.NewGuid().ToString("N")[..30];

    public async Task<JsonNode> WaitForCreditStatusAsync(string sessionId, string expectedStatus, int timeoutSeconds = 30)
    {
        var client = CreateAnonymousClient();
        JsonNode? last = null;

        await Wait.UntilAsync(async () =>
        {
            var response = await client.GetAsync($"/api/v1/wallets/credit/{sessionId}");
            last = await response.ReadJsonAsync();
            return response.StatusCode == HttpStatusCode.OK
                && last["data"]!["status"]!.GetValue<string>() == expectedStatus;
        },
        TimeSpan.FromSeconds(timeoutSeconds),
        () => $"Credit {sessionId} never reached '{expectedStatus}'. Last response: {last}");

        return last!;
    }

    public async Task<long> GetBalanceAsync(TestUser user)
    {
        var response = await user.Client.GetAsync("/api/v1/wallets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.ReadJsonAsync())["data"]!["availableBalanceKobo"]!.GetValue<long>();
    }

    /// <summary>Admin override of a wallet's status, through the real admin endpoint.</summary>
    public async Task SetWalletStatusAsync(Guid walletId, WalletStatus status)
    {
        var admin = await LoginAsAdminAsync();

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/wallets/{walletId}/status")
        {
            Content = JsonContent.Create(new { status = status.ToString() })
        };

        var response = await admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>Fixture for the rate-limit tests: tiny limits so they can be hit with a few requests.</summary>
public sealed class RateLimitFixture : ApiFixture
{
    private static readonly IReadOnlyDictionary<string, string> Limits = new Dictionary<string, string>
    {
        ["RateLimiting__LoginPermitLimit"] = "3",
        ["RateLimiting__LoginWindowSeconds"] = "60",
        ["RateLimiting__TransferPermitLimit"] = "2",
        ["RateLimiting__TransferWindowSeconds"] = "60",
        ["RateLimiting__CreditPermitLimit"] = "2",
        ["RateLimiting__CreditWindowSeconds"] = "60"
    };

    protected override IReadOnlyDictionary<string, string> Settings => Limits;
}

/// <summary>Fixture for the reconciliation tests: the worker sweeps every second and freezes at once.</summary>
public sealed class ReconciliationFixture : ApiFixture
{
    private static readonly IReadOnlyDictionary<string, string> Worker = new Dictionary<string, string>
    {
        ["ReconciliationWorker__PollingIntervalSeconds"] = "1",
        ["ReconciliationWorker__BatchSize"] = "200",
        ["ReconciliationWorker__AutoFreezeOnDiscrepancy"] = "true",
        // Zero grace: entries are folded into the watermark straight away, so a test does not
        // have to wait a minute for its own postings to count.
        ["ReconciliationWorker__WatermarkGracePeriodSeconds"] = "0",
        ["ReconciliationWorker__SnapshotHeartbeatMinutes"] = "0"
    };

    protected override IReadOnlyDictionary<string, string> Settings => Worker;
}

[CollectionDefinition(ApiCollection.Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}

[CollectionDefinition(RateLimitCollection.Name)]
public sealed class RateLimitCollection : ICollectionFixture<RateLimitFixture>
{
    public const string Name = "RateLimit";
}

[CollectionDefinition(ReconciliationCollection.Name)]
public sealed class ReconciliationCollection : ICollectionFixture<ReconciliationFixture>
{
    public const string Name = "Reconciliation";
}
