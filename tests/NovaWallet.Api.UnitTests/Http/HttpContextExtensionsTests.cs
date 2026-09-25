using Microsoft.AspNetCore.Http;
using NovaWallet.Infrastructure.Http;
using System.Security.Claims;

namespace NovaWallet.Api.UnitTests.Http;

public class HttpContextExtensionsTests
{
    private static DefaultHttpContext ContextWithUser(string? nameIdentifier)
    {
        var context = new DefaultHttpContext();
        var claims = nameIdentifier is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, nameIdentifier) };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return context;
    }

    [Fact]
    public void RetrieveUserId_ValidGuidClaim_ReturnsIt()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, ContextWithUser(id.ToString()).RetrieveUserId());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void RetrieveUserId_UnparseableClaim_ReturnsNull(string claim)
    {
        Assert.Null(ContextWithUser(claim).RetrieveUserId());
    }

    [Fact]
    public void RetrieveUserId_NoClaim_ReturnsNull()
    {
        Assert.Null(ContextWithUser(null).RetrieveUserId());
    }

    [Fact]
    public void RetrieveIdempotencyKey_HeaderPresent_ReturnsValue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "abc-123";

        Assert.Equal("abc-123", context.RetrieveIdempotencyKey());
    }

    [Fact]
    public void RetrieveIdempotencyKey_HeaderMissing_ReturnsNull()
    {
        Assert.Null(new DefaultHttpContext().RetrieveIdempotencyKey());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RetrieveIdempotencyKey_BlankHeader_ReturnsNull(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = value;

        Assert.Null(context.RetrieveIdempotencyKey());
    }

    [Fact]
    public void RetrieveIdempotencyKey_NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((HttpContext)null!).RetrieveIdempotencyKey());
    }
}
