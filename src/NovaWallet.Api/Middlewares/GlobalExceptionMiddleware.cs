using Microsoft.AspNetCore.Authorization;
using NovaWallet.Api.Application.Services;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Infrastructure.Http;
using NovaWallet.Api.Infrastructure.Providers;
using System.Diagnostics;
using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

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

public class PartnerAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public PartnerAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context,
        IRequestContext requestContext, IPartnerCacheService partnerCacheService)
    {
        // 1. Skip authentication if endpoint allows anonymous access or isn't mapped (e.g. static files, Scalar UI)
        var endpoint = context.GetEndpoint();
        if (endpoint == null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            await _next(context);
            return;
        }

        // 2. Check if the endpoint explicitly requires the PartnerOnly authorization policy
        var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var requiresPartnerAuth = authorizeData.Any(a => a.Policy == AuthorizationPolicyConstants.PartnerOnly);

        // If the endpoint does NOT require partner auth (e.g., Scalar, Swagger, Health checks), bypass authentication
        if (!requiresPartnerAuth)
        {
            await _next(context);
            return;
        }

        // 1. Extract hashed client key resolved from HttpContext headers
        var hashedApiKey = requestContext.RetrieveHashedPartnerClientKey;

        if (string.IsNullOrWhiteSpace(hashedApiKey))
        {
            await WriteUnauthenticatedResponseAsync(context, "API Key missing or invalid header format.");
            return; // SHORT-CIRCUIT: Request stops here!
        }

        // 2. Resolve cached partner details
        var partner = await partnerCacheService.GetByApiKeyHashAsync(hashedApiKey, context.RequestAborted);

        if (partner == null)
        {
            await WriteUnauthenticatedResponseAsync(context, "Invalid API Key provided.");
            return; // SHORT-CIRCUIT: Request stops here!
        }

        if (!partner.IsActive)
        {
            await WriteUnauthenticatedResponseAsync(context, "Invalid API Key provided.");
            return;
        }
        // 3. Attach partner metadata to RequestContext for downstream use
        requestContext.Partner = partner;

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, partner.UniqueIdentifier.ToString()),
            new Claim(ClaimTypes.Name, partner.Name),
            new Claim("partner_id", partner.Id.ToString())
        };

        var identity = new ClaimsIdentity(claims, "PartnerApiKey");
        context.User = new ClaimsPrincipal(identity);

        // 4. Continue pipeline to Rate Limiter & Endpoints
        await _next(context);
    }

    private static async Task WriteUnauthenticatedResponseAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = MediaTypeNames.Application.Json;

        var response = ServiceApiResponse<object>.CreateFailure(
            code: ResponseCodes.AccessDenied.ResponseCode,
            message: message);

        var json = JsonSerializer.Serialize(response);
        await context.Response.WriteAsync(json);
    }
}

public class RequirePartnerAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var requestContext = httpContext.RequestServices.GetRequiredService<IRequestContext>();

        if (requestContext.Partner == null)
        {
            return Results.Json(
                ServiceApiResponse<object>.CreateFailure(
                    code: ResponseCodes.AccessDenied.ResponseCode,
                    message: "Unauthorized. Valid partner API key required."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}