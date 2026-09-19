using Microsoft.Extensions.Options;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Infrastructure.Providers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace NovaWallet.Api.UnitTests.Http;

public class MockUserAuthHelperTests
{
    private static MockUserAuthHelper NewHelper(int minutes = 60) => new(Options.Create(new JwtSettings
    {
        SecretKey = "unit-test-secret-key-that-is-long-enough-for-hmac-sha256",
        Issuer = "unit-test-issuer",
        Audience = "unit-test-audience",
        TokenExpirationTimeInMinutes = minutes
    }));

    [Fact]
    public void GenerateToken_EmbedsUserIdAndRoleClaims()
    {
        var userId = Guid.NewGuid();

        var token = NewHelper().GenerateToken(new MockLoginRequest { UserId = userId, Role = UserRole.Admin });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.NameIdentifier && c.Value == userId.ToString());
        Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.Role && c.Value == "Admin");
    }

    [Fact]
    public void GenerateToken_SetsIssuerAudienceAndExpiry()
    {
        var before = DateTime.UtcNow;

        var token = NewHelper(minutes: 5).GenerateToken(new MockLoginRequest { UserId = Guid.NewGuid() });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("unit-test-issuer", jwt.Issuer);
        Assert.Contains("unit-test-audience", jwt.Audiences);
        Assert.InRange(jwt.ValidTo, before.AddMinutes(4).AddSeconds(50), before.AddMinutes(5).AddSeconds(10));
    }

    [Fact]
    public void GenerateToken_DefaultRoleIsCustomer()
    {
        var token = NewHelper().GenerateToken(new MockLoginRequest { UserId = Guid.NewGuid() });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.Role && c.Value == "Customer");
    }

    [Fact]
    public void GenerateToken_NullRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NewHelper().GenerateToken(null!));
    }
}
