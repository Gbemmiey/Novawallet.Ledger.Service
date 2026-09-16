using FluentValidation;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Infrastructure.Http
{
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