using NovaWallet.Domain.Responses;

namespace NovaWallet.Application.Response
{
    public interface IServiceApiResponse
    {
        /// <summary>
        /// Gets or sets the response code indicating the status of the API request.
        /// </summary>
        public string ResponseCode { get; set; }

        /// <summary>
        /// Gets or sets the message describing the outcome of the API request.
        /// </summary>
        public string ResponseMessage { get; set; }

        /// <summary>
        /// Gets or sets the list of errors associated with the API request, if any.
        /// </summary>
        public List<ServiceErrorObject> Errors { get; set; }
    }

    public interface IServiceApiResponse<T> : IServiceApiResponse
    {
        /// <summary>
        /// Gets or sets the data returned by the API request.
        /// </summary>
        public T Data { get; set; }
    }

    /// <summary>
    /// Represents a base API response containing status information and optional error details.
    /// </summary>
    public class ServiceApiResponse : IServiceApiResponse
    {
        /// <summary>
        /// Gets or sets the response code indicating the status of the API request.
        /// </summary>
        public string ResponseCode { get; set; } = ResponseCodes.Failed.ResponseCode;

        /// <summary>
        /// Gets or sets the message describing the outcome of the API request.
        /// </summary>
        public string ResponseMessage { get; set; } = ResponseCodes.Failed.ResponseDescription;

        /// <summary>
        /// Gets or sets the list of errors associated with the API request, if any.
        /// </summary>
        public List<ServiceErrorObject> Errors { get; set; } = [];
    }

    /// <summary>
    /// Represents an individual error object containing details about a specific error in the API request.
    /// </summary>
    public class ServiceErrorObject
    {
        /// <summary>
        /// Gets or sets the name of the property associated with the error.
        /// </summary>
        public string PropertyName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the error message describing the issue.
        /// </summary>
        public string Error { get; set; } = string.Empty;
    }

    public static class ServiceApiResponseExtensions
    {
        /// <summary>
        /// Returns true if the response is successful (shortcut for response?.IsSuccessful == true)
        /// </summary>
        public static bool IsSuccessful<T>(this IServiceApiResponse<T> response) where T : class
        {
            return (response?.ResponseCode == ResponseCodes.Success.ResponseCode);
        }

        /// <summary>
        /// Returns true if the response is successful (shortcut for response?.IsSuccessful == true)
        /// </summary>
        public static bool IsSuccessful<T>(this ServiceApiResponse<T> response) where T : class
        {
            return (response?.ResponseCode == ResponseCodes.Success.ResponseCode);
        }
    }

    /// <summary>
    /// Represents a generic API response containing status information, data, and optional error details.
    /// </summary>
    /// <typeparam name="T">The type of data included in the response.</typeparam>
    public class ServiceApiResponse<T> : ServiceApiResponse, IServiceApiResponse<T?> where T : class
    {
        /// <summary>
        /// Gets or sets the data payload of the API response.
        /// </summary>
        public T? Data { get; set; }

        public static ServiceApiResponse<T> CreateFailure(string message)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = ResponseCodes.Failed.ResponseCode,
                ResponseMessage = string.IsNullOrWhiteSpace(message) ? ResponseCodes.Failed.ResponseDescription : message,
                Data = default
            };
        }

        public static ServiceApiResponse<T> SystemMalFunctioned(string? message = null)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = ResponseCodes.SystemMalfunction.ResponseCode,
                ResponseMessage = string.IsNullOrWhiteSpace(message) ? ResponseCodes.SystemMalfunction.ResponseDescription : message,
                Data = default
            };
        }

        public static ServiceApiResponse<T> NotFound(string? msg = null)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = ResponseCodes.NoRecordReturned.ResponseCode,
                ResponseMessage = !string.IsNullOrWhiteSpace(msg) ? msg : ResponseCodes.NoRecordReturned.ResponseDescription,
                Data = default
            };
        }

        public static ServiceApiResponse<T> CreateFailure(T data, NovaResponse codeInfo)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = codeInfo.ResponseCode,
                ResponseMessage = codeInfo.ResponseDescription,
                Data = data
            };
        }

        public static ServiceApiResponse<T> CreateFailure(NovaResponse codeInfo)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = codeInfo.ResponseCode,
                ResponseMessage = codeInfo.ResponseDescription,
                Data = default(T)
            };
        }

        /// <summary>
        /// Creates a failure response with a specific error message and an optional list of errors.
        /// </summary>
        /// <param name="data">The data to include in the response.</param>
        /// <param name="responseCode">The response code for the failure.</param>
        /// <param name="responseMessage">The response message for the failure.</param>
        /// <returns>A <see cref="ServiceApiResponse{T}"/> representing the failure.</returns>
        public static ServiceApiResponse<T> CreateFailure(T data, string? responseCode, string? responseMessage)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = responseCode ?? ResponseCodes.Failed.ResponseCode,
                ResponseMessage = responseMessage ?? ResponseCodes.Failed.ResponseDescription,
                Data = data ?? default(T)
            };
        }

        public static ServiceApiResponse<T> CreateFailure(string? code, string? message)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = code ?? ResponseCodes.Failed.ResponseCode,
                ResponseMessage = message ?? ResponseCodes.Failed.ResponseDescription,
                Data = default(T)
            };
        }

        public static ServiceApiResponse<T> CreateSuccess(T data)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = ResponseCodes.Success.ResponseCode,
                ResponseMessage = ResponseCodes.Success.ResponseDescription,
                Data = data ?? default(T)
            };
        }

        public static ServiceApiResponse<T> CreateSuccess()
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = ResponseCodes.Success.ResponseCode,
                ResponseMessage = ResponseCodes.Success.ResponseDescription,
                Data = default(T)
            };
        }

        public static ServiceApiResponse<T> CreateSuccess(string? message)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = ResponseCodes.Success.ResponseCode,
                ResponseMessage = message ?? ResponseCodes.Success.ResponseDescription,
                Data = default(T)
            };
        }

        public static ServiceApiResponse<T> CreateSuccess(T data, string? message)
        {
            return new ServiceApiResponse<T>
            {
                ResponseCode = ResponseCodes.Success.ResponseCode,
                ResponseMessage = message ?? ResponseCodes.Success.ResponseDescription,
                Data = data ?? default(T)
            };
        }
    }

    /// <summary>
    /// Represents an API response specifically for error scenarios, inheriting from the generic ApiResponse.
    /// </summary>
    /// <typeparam name="T">The type of data included in the response.</typeparam>
    public class ServiceErrorResponse<T> : ServiceApiResponse<T> where T : class
    {
        /// <summary>
        /// Gets or sets the list of errors associated with the API request, if any.
        /// </summary>
        public new List<ServiceErrorObject>? Errors { get; set; }
    }
}