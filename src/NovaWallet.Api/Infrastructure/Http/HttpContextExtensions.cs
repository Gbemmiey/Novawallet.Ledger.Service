using FluentValidation;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Security;

namespace NovaWallet.Api.Infrastructure.Http
{
    /// <summary>
    /// Provides extension methods for retrieving common request metadata from an
    /// <see cref="HttpContext"/>.
    /// </summary>
    public static class HttpContextExtensions
    {
        /// <summary>
        /// Retrieves the trace identifier associated with the current request.
        /// </summary>
        /// <param name="httpContext">
        /// The current HTTP context.
        /// </param>
        /// <returns>
        /// The current activity trace identifier when available; otherwise,
        /// the ASP.NET Core request trace identifier.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="httpContext"/> is <see langword="null"/>.
        /// </exception>
        public static string RetrieveTraceId(this HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            return System.Diagnostics.Activity.Current?.TraceId.ToString()
                   ?? httpContext.TraceIdentifier;
        }

        /// <summary>
        /// Retrieves the application client identifier from the current request.
        /// </summary>
        /// <param name="httpContext">
        /// The current HTTP context.
        /// </param>
        /// <returns>
        /// The value of the
        /// <see cref="PaymentGatewayConstants.ApplicationClientKeyHeader"/>
        /// request header, or <see langword="null"/> if the header is missing or empty.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="httpContext"/> is <see langword="null"/>.
        /// </exception>
        public static string? RetrieveHashedPartnerApiKey(this HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            if (!httpContext.Request.Headers.TryGetValue(NovaWalletConstants.PartnerApiKeyHeader, out var apiKey) ||
                string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            return ApiKeyHasher.HashApiKey(apiKey.ToString());
        }
    }

    public static class EndpointValidationExtensions
    {
        public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) where T : class
        {
            return builder
                .AddEndpointFilter<ValidationFilter<T>>()
                .ProducesValidationProblem(StatusCodes.Status400BadRequest);
        }
    }

    public sealed class ValidationFilter<T> : IEndpointFilter where T : class
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            // 1. Resolve the validator from DI
            var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>();

            // If no validator is registered for this DTO, skip validation
            if (validator is null)
            {
                return await next(context);
            }

            // 2. Extract the DTO argument from the incoming request parameters
            var argument = context.Arguments.OfType<T>().FirstOrDefault();

            if (argument is null)
            {
                return Results.BadRequest(ServiceApiResponse<object>.CreateFailure("Request payload cannot be null."));
            }

            // 3. Perform asynchronous validation
            var validationResult = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);

            if (!validationResult.IsValid)
            {
                // Format validation errors into a dictionary compatible with standard ProblemDetails
                var errors = validationResult.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).ToArray()
                    );

                return Results.ValidationProblem(errors);
            }

            return await next(context);
        }
    }
}