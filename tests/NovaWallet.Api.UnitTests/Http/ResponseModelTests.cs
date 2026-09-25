using NovaWallet.Application.Response;
using NovaWallet.Domain.Responses;
using NovaWallet.Infrastructure.Options;
using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Api.UnitTests.Http;

public class ResponseModelTests
{
    // README §10: the NIP-style code table. A change to any of these is a wire-contract change.
    [Theory]
    [InlineData(nameof(NipResponseCodes.Approved), "00")]
    [InlineData(nameof(NipResponseCodes.InProgress), "09")]
    [InlineData(nameof(NipResponseCodes.FormatError), "30")]
    [InlineData(nameof(NipResponseCodes.DuplicateRecord), "26")]
    [InlineData(nameof(NipResponseCodes.UnableToLocateRecord), "25")]
    [InlineData(nameof(NipResponseCodes.NoSufficientFunds), "51")]
    [InlineData(nameof(NipResponseCodes.TransactionNotPermitted), "57")]
    [InlineData(nameof(NipResponseCodes.TransferLimitExceeded), "61")]
    [InlineData(nameof(NipResponseCodes.SecurityViolation), "63")]
    [InlineData(nameof(NipResponseCodes.ExceedsFrequency), "65")]
    [InlineData(nameof(NipResponseCodes.DuplicateTransaction), "94")]
    [InlineData(nameof(NipResponseCodes.SystemMalfunction), "96")]
    public void NipResponseCodes_MatchTheDocumentedTable(string name, string expected)
    {
        var actual = (string)typeof(NipResponseCodes).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResponseCodes_UseTheExpectedNipCodes()
    {
        Assert.Equal("00", ResponseCodes.Success.ResponseCode);
        Assert.Equal("30", ResponseCodes.InvalidEntryDetected.ResponseCode);
        Assert.Equal("57", ResponseCodes.RequestNotAllowed.ResponseCode);
        Assert.Equal("63", ResponseCodes.AccessDenied.ResponseCode);
        Assert.Equal("94", ResponseCodes.DuplicateTransactionReference.ResponseCode);
        Assert.Equal("25", ResponseCodes.NoRecordReturned.ResponseCode);
        Assert.Equal("51", ResponseCodes.InsufficientBalance.ResponseCode);
        Assert.Equal("61", ResponseCodes.DailyLimitExceeded.ResponseCode);
        Assert.Equal("96", ResponseCodes.SystemMalfunction.ResponseCode);
        Assert.Equal("26", ResponseCodes.Conflict.ResponseCode);
        Assert.Equal("65", ResponseCodes.TooManyRequests.ResponseCode);
    }

    [Fact]
    public void CreateSuccess_CarriesDataAndApprovedCode()
    {
        var response = ServiceApiResponse<string>.CreateSuccess(data: "x");

        Assert.Equal("00", response.ResponseCode);
        Assert.Equal("x", response.Data);
        Assert.True(response.IsSuccessful());
    }

    [Fact]
    public void CreateFailure_WithCodeAndMessage_HasNoData()
    {
        var response = ServiceApiResponse<string>.CreateFailure("51", "Insufficient balance.");

        Assert.Equal("51", response.ResponseCode);
        Assert.Equal("Insufficient balance.", response.ResponseMessage);
        Assert.Null(response.Data);
        Assert.False(response.IsSuccessful());
    }

    [Fact]
    public void CreateFailure_NullCodeAndMessage_FallBackToGenericFailure()
    {
        var response = ServiceApiResponse<string>.CreateFailure((string?)null, (string?)null);

        Assert.Equal("96", response.ResponseCode);
        Assert.Equal(ResponseCodes.Failed.ResponseDescription, response.ResponseMessage);
    }

    [Fact]
    public void NotFound_UsesUnableToLocateRecord()
    {
        var response = ServiceApiResponse<string>.NotFound();

        Assert.Equal("25", response.ResponseCode);
    }

    [Fact]
    public void SystemMalFunctioned_Uses96()
    {
        Assert.Equal("96", ServiceApiResponse<string>.SystemMalFunctioned().ResponseCode);
    }
}

public class RateLimitingOptionsTests
{
    private static bool IsValid(RateLimitingOptions options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), new List<ValidationResult>(), validateAllProperties: true);

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.True(IsValid(new RateLimitingOptions()));
    }

    [Fact]
    public void CreditPermitLimit_Zero_MeansUnlimitedAndIsValid()
    {
        Assert.True(IsValid(new RateLimitingOptions { CreditPermitLimit = 0 }));
    }

    [Theory]
    [InlineData(nameof(RateLimitingOptions.LoginPermitLimit), 0)]
    [InlineData(nameof(RateLimitingOptions.UserPermitLimit), 0)]
    [InlineData(nameof(RateLimitingOptions.TransferPermitLimit), 0)]
    [InlineData(nameof(RateLimitingOptions.CreditPermitLimit), -1)]
    [InlineData(nameof(RateLimitingOptions.LoginWindowSeconds), 0)]
    [InlineData(nameof(RateLimitingOptions.TransferWindowSeconds), 86_401)]
    public void OutOfRangeValues_AreInvalid(string property, int value)
    {
        var options = new RateLimitingOptions();
        typeof(RateLimitingOptions).GetProperty(property)!.SetValue(options, value);

        Assert.False(IsValid(options));
    }
}