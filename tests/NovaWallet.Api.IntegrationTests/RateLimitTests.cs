using Microsoft.IdentityModel.Tokens;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// The named rate-limit policies, run against tiny limits (login 3, transfer 2, credit 2 per
/// minute). Each test targets a different policy - and so a different limiter partition - so they
/// cannot exhaust one another's budget. Tokens are minted locally rather than through
/// <c>/auth/login</c>, since login is itself one of the limited endpoints.
/// </summary>
[Collection(RateLimitCollection.Name)]
public class RateLimitTests
{
    private readonly RateLimitFixture _fixture;

    public RateLimitTests(RateLimitFixture fixture) => _fixture = fixture;

    private HttpClient ClientFor(Guid userId)
    {
        var token = new JwtSecurityToken(
            issuer: ApiFactory.JwtIssuer,
            audience: ApiFactory.JwtAudience,
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, "Customer")
            },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.JwtSecret)), SecurityAlgorithms.HmacSha256));

        var client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Login_IsLimitedPerClientIp_AndTheRejectionCarriesRetryAfter()
    {
        var anonymous = _fixture.CreateAnonymousClient();

        // The limit is 3 per window. The body's userId is irrelevant: the key is the IP alone,
        // so inventing a fresh user id per attempt must not buy a fresh budget.
        var allowed = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
        {
            allowed.Add(await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { userId = Guid.NewGuid(), role = "Customer" }));
        }

        var rejected = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { userId = Guid.NewGuid(), role = "Customer" });

        Assert.All(allowed, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("65", await rejected.ReadResponseCodeAsync());

        Assert.True(rejected.Headers.TryGetValues("Retry-After", out var retryAfter), "429 is missing Retry-After.");
        Assert.InRange(int.Parse(retryAfter!.Single()), 1, 60);
    }

    [Fact]
    public async Task Transfer_IsLimitedPerUser_NotGlobally()
    {
        var heavyUser = ClientFor(Guid.NewGuid());
        var otherUser = ClientFor(Guid.NewGuid());

        // Whatever the transfer itself answers (these users have no wallets), the first two
        // requests are within budget and the third is throttled before the handler runs.
        var first = await heavyUser.TransferAsync(Guid.NewGuid(), 1_000, Guid.NewGuid().ToString());
        var second = await heavyUser.TransferAsync(Guid.NewGuid(), 1_000, Guid.NewGuid().ToString());
        var third = await heavyUser.TransferAsync(Guid.NewGuid(), 1_000, Guid.NewGuid().ToString());

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));

        // A different user has their own bucket.
        var otherUsersFirst = await otherUser.TransferAsync(Guid.NewGuid(), 1_000, Guid.NewGuid().ToString());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherUsersFirst.StatusCode);
    }

    [Fact]
    public async Task Transfer_RateLimit_DoesNotThrottleOtherEndpointsOfTheSameUser()
    {
        var user = ClientFor(Guid.NewGuid());

        for (var i = 0; i < 3; i++)
        {
            await user.TransferAsync(Guid.NewGuid(), 1_000, Guid.NewGuid().ToString());
        }

        // Wallet reads use the separate, much larger UserPolicy budget.
        var wallet = await user.GetAsync("/api/v1/wallets");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, wallet.StatusCode);
    }

    [Fact]
    public async Task Credit_IsLimitedPerClientIp()
    {
        // Unknown accounts: each accepted request answers 404, the throttled one 429.
        async Task<HttpResponseMessage> Credit() => await _fixture.SubmitCreditAsync(
            ApiFixture.NewSessionId(), "REF-" + Guid.NewGuid().ToString("N"), 1_000, "0000000000");

        var first = await Credit();
        var second = await Credit();
        var third = await Credit();

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("65", await third.ReadResponseCodeAsync());
    }
}
