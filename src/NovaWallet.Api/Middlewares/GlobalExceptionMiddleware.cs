using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Infrastructure.Http;
using System.Diagnostics;

namespace NovaWallet.Api.Middlewares;

/// <summary>
/// Catch-all exception handler placed first in the middleware pipeline.
/// Mitigation for OWASP A10:2025 – Mishandling of Exceptional Conditions: converts unhandled
/// exceptions to structured error responses without leaking stack traces to callers.
/// Mitigation for OWASP A09:2025 – Logging & Monitoring: logs full exception with TraceId so
/// internal teams can correlate issues without exposing details externally.
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Extract active W3C trace parent ID or fall back to HTTP context identifier
        string traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        // OWASP A09:2025 - Log full exception details correlated with TraceId
        _logger.LogError(
            exception,
            "Unhandled exception occurred. TraceId={TraceId}, Path={Path}, Method={Method}",
            traceId,
            context.Request.Path,
            context.Request.Method);

        if (context.Response.HasStarted)
        {
            _logger.LogWarning(
                "The response has already started, cannot write exception response. TraceId={TraceId}",
                traceId);
            return;
        }

        // Standardize output payload using your system malfunction helper
        ServiceApiResponse<object> responsePayload = ServiceApiResponse<object>.SystemMalFunctioned();

        // Delegate rendering to custom IResult (sets X-Trace-Id header and streams response)
        var result = new ApiResponseResult<object?>(
            response: responsePayload,
            statusCode: StatusCodes.Status500InternalServerError);

        await result.ExecuteAsync(context);
    }
}