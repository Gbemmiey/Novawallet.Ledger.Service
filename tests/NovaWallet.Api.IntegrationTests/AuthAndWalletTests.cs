using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace NovaWallet.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests
{
    private readonly ApiFixture _fixture;

    public AuthTests(ApiFixture fixture) => _fixture = fixture;

    private static string CreateToken(string secret, DateTime notBefore, DateTime expires)
    {
        var token = new JwtSecurityToken(
            issuer: ApiFactory.JwtIssuer,
            audience: ApiFactory.JwtAudience,
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Customer")
            },
            notBefore: notBefore,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Login_ReturnsATokenTheApiAccepts()
    {
        var client = await _fixture.LoginAsync(Guid.NewGuid());

        var response = await client.GetAsync("/api/v1/wallets");

        // Authenticated, but this user has no wallet yet: 404 (25), not 401.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("25", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task Login_WithEmptyUserId_IsRejected()
    {
        var response = await _fixture.CreateAnonymousClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { userId = Guid.Empty, role = "Customer" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownRole_IsRejected()
    {
        var response = await _fixture.CreateAnonymousClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { userId = Guid.NewGuid(), role = "Superuser" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/wallets")]
    [InlineData("POST", "/api/v1/wallets")]
    [InlineData("GET", "/api/v1/wallets/statement")]
    [InlineData("POST", "/api/v1/wallets/transfer")]
    [InlineData("GET", "/api/v1/wallets/transfer/some-key")]
    [InlineData("GET", "/api/v1/admin/audit-logs")]
    public async Task ProtectedEndpoints_WithoutAToken_Return401(string method, string path)
    {
        var response = await _fixture.CreateAnonymousClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GarbageToken_Returns401()
    {
        var response = await ClientWithToken("not.a.jwt").GetAsync("/api/v1/wallets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenSignedWithTheWrongKey_Returns401()
    {
        var token = CreateToken(
            "a-completely-different-signing-key-of-sufficient-length",
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(30));

        var response = await ClientWithToken(token).GetAsync("/api/v1/wallets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Returns401_AndFlagsTheExpiry()
    {
        var token = CreateToken(ApiFactory.JwtSecret, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));

        var response = await ClientWithToken(token).GetAsync("/api/v1/wallets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Token-Expired", out var values));
        Assert.Contains("true", values!);
    }

    [Fact]
    public async Task ValidHandCraftedToken_IsAccepted()
    {
        var token = CreateToken(ApiFactory.JwtSecret, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(30));

        var response = await ClientWithToken(token).GetAsync("/api/v1/wallets");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreditEndpoints_AreAnonymous()
    {
        var response = await _fixture.CreateAnonymousClient().GetAsync("/api/v1/wallets/credit/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_RejectCustomers_AndAllowAdmins()
    {
        var customer = await _fixture.LoginAsync(Guid.NewGuid());
        var admin = await _fixture.LoginAsAdminAsync();

        var asCustomer = await customer.GetAsync("/api/v1/admin/audit-logs");
        var asAdmin = await admin.GetAsync("/api/v1/admin/audit-logs");

        Assert.Equal(HttpStatusCode.Forbidden, asCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_IsAnonymous()
    {
        var response = await _fixture.CreateAnonymousClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

[Collection(ApiCollection.Name)]
public class WalletEndpointTests
{
    private readonly ApiFixture _fixture;

    public WalletEndpointTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateWallet_ReturnsAnActiveEmptyNairaWallet()
    {
        var client = await _fixture.LoginAsync(Guid.NewGuid());

        var response = await client.PostAsync("/api/v1/wallets", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.ReadJsonAsync())["data"]!;
        Assert.Equal("Active", data["status"]!.GetValue<string>());
        Assert.Equal("NGN", data["currencyCode"]!.GetValue<string>());
        Assert.Equal(0, data["availableBalanceKobo"]!.GetValue<long>());
        Assert.Matches("^[0-9]{10}$", data["accountNumber"]!.GetValue<string>());
        Assert.NotEqual(Guid.Empty, Guid.Parse(data["walletId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task CreateWallet_CalledTwice_ReturnsTheSameWallet()
    {
        var userId = Guid.NewGuid();
        var client = await _fixture.LoginAsync(userId);

        var first = await (await client.PostAsync("/api/v1/wallets", null)).ReadJsonAsync();
        var second = await (await client.PostAsync("/api/v1/wallets", null)).ReadJsonAsync();

        Assert.Equal(first["data"]!["walletId"]!.GetValue<string>(), second["data"]!["walletId"]!.GetValue<string>());

        await using var db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.Wallets.CountAsync(w => w.UserId == userId));
    }

    [Fact]
    public async Task CreateWallet_ConcurrentCallsForOneUser_AllSucceedWithTheSameWallet()
    {
        var userId = Guid.NewGuid();
        var client = await _fixture.LoginAsync(userId);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => client.PostAsync("/api/v1/wallets", null)));

        // The unique index on Wallets.UserId makes the losers resolve to the winner's wallet.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var walletIds = new HashSet<string>();
        foreach (var response in responses)
        {
            walletIds.Add((await response.ReadJsonAsync())["data"]!["walletId"]!.GetValue<string>());
        }

        Assert.Single(walletIds);

        await using var db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.Wallets.CountAsync(w => w.UserId == userId));

        var after = await client.GetAsync("/api/v1/wallets");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task RetrieveWallet_ReturnsTheCallersWallet()
    {
        var user = await _fixture.CreateUserAsync();

        var response = await user.Client.GetAsync("/api/v1/wallets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.ReadJsonAsync())["data"]!;
        Assert.Equal(user.WalletId.ToString(), data["walletId"]!.GetValue<string>());
        Assert.Equal(user.AccountNumber, data["accountNumber"]!.GetValue<string>());
    }

    [Fact]
    public async Task RetrieveWallet_WithoutAWallet_Returns404With25()
    {
        var client = await _fixture.LoginAsync(Guid.NewGuid());

        var response = await client.GetAsync("/api/v1/wallets");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("25", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task DifferentUsers_GetDifferentWalletsAndAccountNumbers()
    {
        var a = await _fixture.CreateUserAsync();
        var b = await _fixture.CreateUserAsync();

        Assert.NotEqual(a.WalletId, b.WalletId);
        Assert.NotEqual(a.AccountNumber, b.AccountNumber);
    }

    [Fact]
    public async Task Statement_WithoutAWallet_Returns404()
    {
        var client = await _fixture.LoginAsync(Guid.NewGuid());

        var response = await client.GetAsync("/api/v1/wallets/statement");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Statement_OfANewWallet_IsEmpty()
    {
        var user = await _fixture.CreateUserAsync();

        var response = await user.Client.GetAsync("/api/v1/wallets/statement");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.ReadJsonAsync())["data"]!;
        Assert.Equal(0, data["totalCount"]!.GetValue<int>());
        Assert.Empty(data["items"]!.AsArray());
    }

    [Fact]
    public async Task Statement_ShowsCreditsAndDebits_NewestFirst()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 500_000);
        var receiver = await _fixture.CreateUserAsync();

        var transfer = await sender.Client.TransferAsync(receiver.WalletId, 120_000, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);

        var statement = (await (await sender.Client.GetAsync("/api/v1/wallets/statement")).ReadJsonAsync())["data"]!;
        var items = statement["items"]!.AsArray();

        Assert.Equal(2, statement["totalCount"]!.GetValue<int>());
        Assert.Equal("Debit", items[0]!["entryType"]!.GetValue<string>());
        Assert.Equal(120_000, items[0]!["amountKobo"]!.GetValue<long>());
        Assert.Equal("Credit", items[1]!["entryType"]!.GetValue<string>());
        Assert.Equal(500_000, items[1]!["amountKobo"]!.GetValue<long>());

        var receiverStatement = (await (await receiver.Client.GetAsync("/api/v1/wallets/statement")).ReadJsonAsync())["data"]!;
        Assert.Equal(1, receiverStatement["totalCount"]!.GetValue<int>());
        Assert.Equal("Credit", receiverStatement["items"]![0]!["entryType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Statement_IsPaginatedAndClampsThePageSize()
    {
        var user = await _fixture.CreateUserAsync();
        for (var i = 0; i < 3; i++)
        {
            await _fixture.FundAsync(user.AccountNumber, 1_000);
        }

        var pageOne = (await (await user.Client.GetAsync("/api/v1/wallets/statement?pageNumber=1&pageSize=2")).ReadJsonAsync())["data"]!;
        var pageTwo = (await (await user.Client.GetAsync("/api/v1/wallets/statement?pageNumber=2&pageSize=2")).ReadJsonAsync())["data"]!;
        var oversized = (await (await user.Client.GetAsync("/api/v1/wallets/statement?pageSize=100000")).ReadJsonAsync())["data"]!;

        Assert.Equal(2, pageOne["items"]!.AsArray().Count);
        Assert.Equal(3, pageOne["totalCount"]!.GetValue<int>());
        Assert.Equal(2, pageOne["totalPages"]!.GetValue<int>());
        Assert.True(pageOne["hasNextPage"]!.GetValue<bool>());
        Assert.Single(pageTwo["items"]!.AsArray());
        Assert.False(pageTwo["hasNextPage"]!.GetValue<bool>());
        Assert.Equal(100, oversized["pageSize"]!.GetValue<int>());
    }

    [Fact]
    public async Task Statement_OnlyShowsTheCallersOwnEntries()
    {
        var alice = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var bob = await _fixture.CreateUserAsync(fundKobo: 20_000);

        var aliceStatement = (await (await alice.Client.GetAsync("/api/v1/wallets/statement")).ReadJsonAsync())["data"]!;
        var bobStatement = (await (await bob.Client.GetAsync("/api/v1/wallets/statement")).ReadJsonAsync())["data"]!;

        Assert.Equal(10_000, aliceStatement["items"]![0]!["amountKobo"]!.GetValue<long>());
        Assert.Equal(20_000, bobStatement["items"]![0]!["amountKobo"]!.GetValue<long>());
    }
}
