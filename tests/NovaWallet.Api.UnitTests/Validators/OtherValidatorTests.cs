using NovaWallet.Application.Dto;
using NovaWallet.Application.Dto.Login;
using NovaWallet.Application.Validators;
using NovaWallet.Domain.Enums;

namespace NovaWallet.Api.UnitTests.Validators;

public class NipSingleCreditRequestValidatorTests
{
    private readonly NipSingleCreditRequestValidator _validator = new();

    private static NipSingleCreditRequest ValidRequest() => new()
    {
        SessionId = "000001260919120000000000000001",
        TransactionReference = "REF-0001",
        AmountKobo = 100_000,
        BeneficiaryAccountNumber = "0123456789",
        OriginatingAccountNumber = "9876543210",
        OriginatingBankCode = "058",
        Narration = "salary"
    };

    [Fact]
    public void ValidRequest_Passes()
    {
        Assert.True(_validator.Validate(ValidRequest()).IsValid);
    }

    [Fact]
    public void SessionId_AtLimit30_Passes()
    {
        var request = ValidRequest();
        request.SessionId = new string('1', 30);

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData(nameof(NipSingleCreditRequest.SessionId), 31)]
    [InlineData(nameof(NipSingleCreditRequest.TransactionReference), 65)]
    [InlineData(nameof(NipSingleCreditRequest.BeneficiaryAccountNumber), 33)]
    [InlineData(nameof(NipSingleCreditRequest.OriginatingAccountNumber), 33)]
    [InlineData(nameof(NipSingleCreditRequest.OriginatingBankCode), 11)]
    [InlineData(nameof(NipSingleCreditRequest.Narration), 201)]
    public void Field_OverColumnLimit_Fails(string property, int length)
    {
        var request = ValidRequest();
        typeof(NipSingleCreditRequest).GetProperty(property)!.SetValue(request, new string('x', length));

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == property);
    }

    [Theory]
    [InlineData(nameof(NipSingleCreditRequest.SessionId))]
    [InlineData(nameof(NipSingleCreditRequest.TransactionReference))]
    [InlineData(nameof(NipSingleCreditRequest.BeneficiaryAccountNumber))]
    [InlineData(nameof(NipSingleCreditRequest.OriginatingAccountNumber))]
    [InlineData(nameof(NipSingleCreditRequest.OriginatingBankCode))]
    public void RequiredField_Empty_Fails(string property)
    {
        var request = ValidRequest();
        typeof(NipSingleCreditRequest).GetProperty(property)!.SetValue(request, string.Empty);

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == property);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AmountKobo_NotPositive_Fails(long amount)
    {
        var request = ValidRequest();
        request.AmountKobo = amount;

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(NipSingleCreditRequest.AmountKobo));
    }

    [Fact]
    public void Narration_Null_Passes()
    {
        var request = ValidRequest();
        request.Narration = null!;

        Assert.True(_validator.Validate(request).IsValid);
    }
}

public class MockLoginRequestValidatorTests
{
    private readonly MockLoginRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_Passes()
    {
        var request = new MockLoginRequest { UserId = Guid.NewGuid(), Role = UserRole.Customer };

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void EmptyUserId_Fails()
    {
        var request = new MockLoginRequest { UserId = Guid.Empty };

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MockLoginRequest.UserId));
    }

    [Fact]
    public void UndefinedRole_Fails()
    {
        var request = new MockLoginRequest { UserId = Guid.NewGuid(), Role = (UserRole)99 };

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MockLoginRequest.Role));
    }

    [Fact]
    public void Role_DefaultsToCustomer()
    {
        Assert.Equal(UserRole.Customer, new MockLoginRequest().Role);
    }
}

public class AdminWalletStatusUpdateRequestValidatorTests
{
    private readonly AdminWalletStatusUpdateRequestValidator _validator = new();

    [Theory]
    [InlineData(WalletStatus.Active)]
    [InlineData(WalletStatus.Frozen)]
    [InlineData(WalletStatus.Closed)]
    public void DefinedStatus_Passes(WalletStatus status)
    {
        Assert.True(_validator.Validate(new AdminWalletStatusUpdateRequest { Status = status }).IsValid);
    }

    [Fact]
    public void UndefinedStatus_Fails()
    {
        var result = _validator.Validate(new AdminWalletStatusUpdateRequest { Status = (WalletStatus)42 });

        Assert.False(result.IsValid);
    }
}
