using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Validators;

namespace NovaWallet.Api.UnitTests.Validators;

public class WalletTransferRequestValidatorTests
{
    private readonly WalletTransferRequestValidator _validator = new();

    private static WalletTransferRequest ValidRequest() => new()
    {
        DestinationWalletId = Guid.NewGuid().ToString(),
        AmountInKobo = 50_000,
        Narration = "rent"
    };

    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(ValidRequest());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void SourceWalletId_WhenPresent_IsRejected(string sourceWalletId)
    {
        var request = ValidRequest();
#pragma warning disable CS0618 // deliberately setting the deprecated property to prove it is rejected
        request.SourceWalletId = sourceWalletId;
#pragma warning restore CS0618

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("SourceWalletId", error.PropertyName);
        Assert.Contains("inferred from your session", error.ErrorMessage);
    }

    [Fact]
    public void SourceWalletId_WhenNull_IsAccepted()
    {
        var request = ValidRequest();
#pragma warning disable CS0618
        request.SourceWalletId = null;
#pragma warning restore CS0618

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    public void DestinationWalletId_InvalidValues_Fail(string destination)
    {
        var request = ValidRequest();
        request.DestinationWalletId = destination;

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(WalletTransferRequest.DestinationWalletId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void AmountInKobo_NotPositive_Fails(long amount)
    {
        var request = ValidRequest();
        request.AmountInKobo = amount;

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(WalletTransferRequest.AmountInKobo));
    }

    [Fact]
    public void Narration_AtMaximumLength_Passes()
    {
        var request = ValidRequest();
        request.Narration = new string('x', 200);

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Narration_OverMaximumLength_Fails()
    {
        var request = ValidRequest();
        request.Narration = new string('x', 201);

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(WalletTransferRequest.Narration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Narration_NullOrEmpty_Passes(string? narration)
    {
        var request = ValidRequest();
        request.Narration = narration!;

        Assert.True(_validator.Validate(request).IsValid);
    }
}
