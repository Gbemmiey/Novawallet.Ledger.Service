namespace NovaWallet.Api.Core.Models.Response;

/// <summary>
/// NIBSS Instant Payment (NIP) style response codes. These are the values carried in the body's
/// <c>responseCode</c> field; the HTTP status on the wire is chosen separately by
/// <c>ApiResponseTransformer</c>.
/// </summary>
public static class NipResponseCodes
{
    public const string Approved = "00";
    // Request received and still being processed (used by the credit requery while pending).
    public const string InProgress = "09";
    public const string FormatError = "30";
    public const string DuplicateRecord = "26";
    public const string UnableToLocateRecord = "25";
    public const string NoSufficientFunds = "51";
    public const string TransactionNotPermitted = "57";
    public const string TransferLimitExceeded = "61";
    public const string SecurityViolation = "63";
    // NIP has no dedicated rate-limit code; 65 (exceeds withdrawal frequency) is the closest.
    public const string ExceedsFrequency = "65";
    public const string DuplicateTransaction = "94";
    public const string SystemMalfunction = "96";
}

public static class ResponseCodes
{
    public static Response Success = new()
    {
        ResponseCode = NipResponseCodes.Approved,
        ResponseDescription = "Request successful"
    };

    public static Response DuplicateRecord = new()
    {
        ResponseCode = NipResponseCodes.DuplicateRecord,
        ResponseDescription = "Duplicate record"
    };

    public static Response Failed = new()
    {
        ResponseCode = NipResponseCodes.SystemMalfunction,
        ResponseDescription = "Request failed. Kindly try again later."
    };

    public static Response InvalidEntryDetected = new()
    {
        ResponseCode = NipResponseCodes.FormatError,
        ResponseDescription = "Invalid entry"
    };

    public static Response RequestNotAllowed = new()
    {
        ResponseCode = NipResponseCodes.TransactionNotPermitted,
        ResponseDescription = "Request not allowed"
    };

    public static Response AccessDenied = new()
    {
        ResponseCode = NipResponseCodes.SecurityViolation,
        ResponseDescription = "Access denied. Please liaise with the system administrator."
    };

    public static Response DuplicateTransactionReference = new()
    {
        ResponseCode = NipResponseCodes.DuplicateTransaction,
        ResponseDescription = "A request with the same transaction reference already exists."
    };

    public static Response NoRecordReturned = new()
    {
        ResponseCode = NipResponseCodes.UnableToLocateRecord,
        ResponseDescription = "No record found."
    };

    public static Response InsufficientBalance = new()
    {
        ResponseCode = NipResponseCodes.NoSufficientFunds,
        ResponseDescription = "Insufficient balance."
    };

    public static Response DailyLimitExceeded = new()
    {
        ResponseCode = NipResponseCodes.TransferLimitExceeded,
        ResponseDescription = "Daily outbound transfer limit exceeded."
    };

    public static Response SystemMalfunction = new()
    {
        ResponseCode = NipResponseCodes.SystemMalfunction,
        ResponseDescription = "An error occurred. Please try again later."
    };

    public static Response Conflict = new()
    {
        ResponseCode = NipResponseCodes.DuplicateRecord,
        ResponseDescription = "A conflicting request already exists."
    };

    public static Response TooManyRequests = new()
    {
        ResponseCode = NipResponseCodes.ExceedsFrequency,
        ResponseDescription = "Too many requests. Please try again later."
    };
}
