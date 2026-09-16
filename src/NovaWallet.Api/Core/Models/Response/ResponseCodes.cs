namespace NovaWallet.Api.Core.Models.Response;

public static class ResponseCodes
{
    public static Response Success = new()
    {
        ResponseCode = StatusCodes.Status200OK.ToString(),
        ResponseDescription = "Request successful"
    };

    public static Response DuplicateRecord = new()
    {
        ResponseCode = StatusCodes.Status409Conflict.ToString(),
        ResponseDescription = "Duplicate record"
    };

    public static Response Failed = new()
    {
        ResponseCode = StatusCodes.Status500InternalServerError.ToString(),
        ResponseDescription = "Request failed. Kindly try again later."
    };

    public static Response InvalidEntryDetected = new()
    {
        ResponseCode = StatusCodes.Status400BadRequest.ToString(),
        ResponseDescription = "Invalid entry"
    };

    public static Response RequestNotAllowed = new()
    {
        ResponseCode = StatusCodes.Status403Forbidden.ToString(),
        ResponseDescription = "Request not allowed"
    };

    public static Response AccessDenied = new()
    {
        ResponseCode = StatusCodes.Status401Unauthorized.ToString(),
        ResponseDescription = "Access denied. Please liaise with the system administrator."
    };

    public static Response DuplicateTransactionReference = new()
    {
        ResponseCode = StatusCodes.Status409Conflict.ToString(),
        ResponseDescription = "A request with the same transaction reference already exists."
    };

    public static Response NoRecordReturned = new()
    {
        ResponseCode = StatusCodes.Status404NotFound.ToString(),
        ResponseDescription = "No record found."
    };

    public static Response InsufficientBalance = new()
    {
        ResponseCode = StatusCodes.Status422UnprocessableEntity.ToString(),
        ResponseDescription = "Insufficient balance."
    };

    public static Response SystemMalfunction = new()
    {
        ResponseCode = StatusCodes.Status500InternalServerError.ToString(),
        ResponseDescription = "An error occurred. Please try again later."
    };

    public static Response Conflict = new()
    {
        ResponseCode = StatusCodes.Status409Conflict.ToString(),
        ResponseDescription = "A conflicting request already exists."
    };

    public static Response TooManyRequests = new()
    {
        ResponseCode = StatusCodes.Status429TooManyRequests.ToString(),
        ResponseDescription = "Too many requests. Please try again later."
    };
}