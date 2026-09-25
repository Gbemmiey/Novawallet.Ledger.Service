using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NovaWallet.Application.Response;
using NovaWallet.Domain.Responses;
using NovaWallet.Infrastructure.Http;
using System.Text.Json;

namespace NovaWallet.Api.UnitTests.Http;

public class ApiResponseTransformerTests
{
    // Every NIP code the API can return, and the HTTP status the wire carries for it (README §10).
    [Theory]
    [InlineData("26", StatusCodes.Status409Conflict)]
    [InlineData("94", StatusCodes.Status409Conflict)]
    [InlineData("25", StatusCodes.Status404NotFound)]
    [InlineData("63", StatusCodes.Status401Unauthorized)]
    [InlineData("57", StatusCodes.Status403Forbidden)]
    [InlineData("30", StatusCodes.Status400BadRequest)]
    [InlineData("51", StatusCodes.Status422UnprocessableEntity)]
    [InlineData("61", StatusCodes.Status422UnprocessableEntity)]
    [InlineData("65", StatusCodes.Status429TooManyRequests)]
    [InlineData("96", StatusCodes.Status500InternalServerError)]
    [InlineData("99", StatusCodes.Status400BadRequest)] // unmapped codes fall back to 400
    public void ToResult_MapsResponseCodeToHttpStatus(string responseCode, int expectedStatus)
    {
        var response = ServiceApiResponse<string>.CreateFailure(responseCode, "boom");

        var result = response.ToResult(new DefaultHttpContext());

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(expectedStatus, problem.StatusCode);
    }

    [Fact]
    public void ToResult_Failure_CarriesResponseCodeAndDetail()
    {
        var response = ServiceApiResponse<string>.CreateFailure(ResponseCodes.InsufficientBalance);

        var problem = Assert.IsType<ProblemHttpResult>(response.ToResult(new DefaultHttpContext()));

        Assert.Equal("51", problem.ProblemDetails.Extensions["responseCode"]?.ToString());
        Assert.Equal("Insufficient balance.", problem.ProblemDetails.Detail);
        Assert.True(problem.ProblemDetails.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public void ToResult_Success_IsNotAProblem()
    {
        var response = ServiceApiResponse<string>.CreateSuccess(data: "payload");

        var result = response.ToResult(new DefaultHttpContext());

        Assert.IsType<ApiResponseResult<string?>>(result);
    }

    [Fact]
    public async Task ToResult_Success_WritesCamelCaseEnvelopeWith200()
    {
        var response = ServiceApiResponse<string>.CreateSuccess(data: "payload");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await response.ToResult(context).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("00", json.RootElement.GetProperty("responseCode").GetString());
        Assert.Equal("payload", json.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task ToAcceptedResult_Success_Returns202()
    {
        var response = ServiceApiResponse<string>.CreateSuccess(data: "payload");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await response.ToAcceptedResult(context).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
    }

    [Fact]
    public void ToAcceptedResult_Failure_FallsBackToStatusMapping()
    {
        var response = ServiceApiResponse<string>.CreateFailure(ResponseCodes.DuplicateTransactionReference);

        var problem = Assert.IsType<ProblemHttpResult>(response.ToAcceptedResult(new DefaultHttpContext()));

        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
    }
}