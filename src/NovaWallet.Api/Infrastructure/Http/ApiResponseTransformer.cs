using NovaWallet.Api.Core.Models.Response;
using Serilog;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaWallet.Api.Infrastructure.Http
{
    /// <summary>
    /// A high-performance custom <see cref="IResult"/> for serializing <see cref="IServiceApiResponse{T}"/> outputs.
    /// </summary>
    public sealed class ApiResponseResult<T> : IResult
    {
        private readonly IServiceApiResponse<T> _response;
        private readonly int _statusCode;
        private readonly string? _locationHeader;

        private static readonly JsonSerializerOptions DefaultSerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        public ApiResponseResult(IServiceApiResponse<T> response, int statusCode, string? locationHeader = null)
        {
            _response = response ?? throw new ArgumentNullException(nameof(response));
            _statusCode = statusCode;
            _locationHeader = locationHeader;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var response = httpContext.Response;

            response.StatusCode = _statusCode;
            response.ContentType = "application/json; charset=utf-8";

            if (!string.IsNullOrEmpty(_locationHeader))
            {
                response.Headers.Location = _locationHeader;
            }

            // Stream JSON directly to the response body pipe to minimize memory overhead
            await JsonSerializer.SerializeAsync(
                response.Body,
                _response,
                DefaultSerializerOptions,
                httpContext.RequestAborted);
        }
    }

    public static class ApiResponseTransformer
    {
        /// <summary>
        /// Transforms a service response into a custom Minimal API <see cref="IResult"/>.
        /// </summary>
        public static IResult ToResult<T>(this IServiceApiResponse<T> response, HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(response);
            Log.Information("Returning responseCode: {Code}; Message: {Message}", response.ResponseCode, response.ResponseMessage);

            if (response.ResponseCode == ResponseCodes.Success.ResponseCode)
            {
                return new ApiResponseResult<T>(response, StatusCodes.Status200OK);
            }

            int statusCode = response.ResponseCode switch
            {
                var code when code == ResponseCodes.Success.ResponseCode
                    => StatusCodes.Status200OK,

                var code when code == ResponseCodes.DuplicateTransactionReference.ResponseCode
                          || code == ResponseCodes.DuplicateRecord.ResponseCode
                          || code == ResponseCodes.Conflict.ResponseCode
                    => StatusCodes.Status409Conflict,

                var code when code == ResponseCodes.NoRecordReturned.ResponseCode
                    => StatusCodes.Status404NotFound,

                var code when code == ResponseCodes.AccessDenied.ResponseCode
                    => StatusCodes.Status401Unauthorized,

                var code when code == ResponseCodes.RequestNotAllowed.ResponseCode
                    => StatusCodes.Status403Forbidden,

                var code when code == ResponseCodes.InvalidEntryDetected.ResponseCode
                    => StatusCodes.Status400BadRequest,

                var code when code == ResponseCodes.InsufficientBalance.ResponseCode
                    => StatusCodes.Status422UnprocessableEntity,

                var code when code == ResponseCodes.Failed.ResponseCode
                          || code == ResponseCodes.SystemMalfunction.ResponseCode
                    => StatusCodes.Status500InternalServerError,

                _ => StatusCodes.Status400BadRequest
            };
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            return Results.Problem(
                detail: response.ResponseMessage,
                statusCode: statusCode,
                title: GetTitleForStatusCode(statusCode),
                extensions: new Dictionary<string, object?>
                {
                    ["responseCode"] = response.ResponseCode,
                    ["traceId"] = traceId
                });
        }

        private static string GetTitleForStatusCode(int statusCode) => statusCode switch
        {
            400 => "Bad Request",
            404 => "Resource Not Found",
            409 => "Conflict",
            500 => "Internal Server Error",
            _ => "An error occurred processing your request"
        };

        /// <summary>
        /// Transforms a service response into a 201 Created custom <see cref="IResult"/> with location routing.
        /// </summary>
        public static IResult ToCreatedResult<T>(
            this IServiceApiResponse<T> response,
            HttpContext httpContext,
            LinkGenerator linkGenerator,
            string routeName,
            object? routeValues = null)
        {
            ArgumentNullException.ThrowIfNull(response);

            if (response.ResponseCode == ResponseCodes.Success.ResponseCode)
            {
                Log.Information("Returning Created responseCode: {Code}; Message: {Message}", response.ResponseCode, response.ResponseMessage);

                string? location = linkGenerator.GetPathByName(httpContext, routeName, routeValues);

                return new ApiResponseResult<T>(response, StatusCodes.Status201Created, location);
            }

            // Fallback to standard status code mapping if domain execution failed
            return response.ToResult(httpContext);
        }

        /// <summary>
        /// Transforms a service response into a 202 Accepted custom <see cref="IResult"/>,
        /// for operations that have been durably recorded but are completed asynchronously
        /// (e.g. inbound NIP deposit acknowledgment ahead of outbox-driven crediting).
        /// </summary>
        public static IResult ToAcceptedResult<T>(this IServiceApiResponse<T> response, HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(response);

            if (response.ResponseCode == ResponseCodes.Success.ResponseCode)
            {
                Log.Information("Returning Accepted responseCode: {Code}; Message: {Message}", response.ResponseCode, response.ResponseMessage);

                return new ApiResponseResult<T>(response, StatusCodes.Status202Accepted);
            }

            // Fallback to standard status code mapping if domain execution failed
            return response.ToResult(httpContext);
        }
    }
}