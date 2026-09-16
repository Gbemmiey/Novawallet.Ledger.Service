using FluentValidation;
using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Models.Response;
using System.Security.Claims;

namespace NovaWallet.Api.Infrastructure.Http
{
    /// <summary>
    /// Provides extension methods for retrieving common request metadata from an
    /// <see cref="HttpContext"/>.
    /// </summary>
    public static class HttpContextExtensions
    {
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
        public static string? RetrieveIdempotencyKey(this HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            if (!httpContext.Request.Headers.TryGetValue(NovaWalletConstants.IdempotencyKey, out var idempotencyKey) ||
                string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return null;
            }

            return idempotencyKey.ToString();
        }

        public static Guid? RetrieveUserId(this HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

            return Guid.TryParse(userId, out var parsedUserId)
                ? parsedUserId
                : null;
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