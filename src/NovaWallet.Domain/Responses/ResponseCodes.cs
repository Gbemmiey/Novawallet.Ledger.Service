namespace NovaWallet.Domain.Responses;

public static class ResponseCodes
{
    public static NovaResponse Success = new()
    {
        ResponseCode = NipResponseCodes.Approved,
        ResponseDescription = "Request successful"
    };

    public static NovaResponse DuplicateRecord = new()
    {
        ResponseCode = NipResponseCodes.DuplicateRecord,
        ResponseDescription = "Duplicate record"
    };

    public static NovaResponse Failed = new()
    {
        ResponseCode = NipResponseCodes.SystemMalfunction,
        ResponseDescription = "Request failed. Kindly try again later."
    };

    public static NovaResponse InvalidEntryDetected = new()
    {
        ResponseCode = NipResponseCodes.FormatError,
        ResponseDescription = "Invalid entry"
    };

    public static NovaResponse RequestNotAllowed = new()
    {
        ResponseCode = NipResponseCodes.TransactionNotPermitted,
        ResponseDescription = "Request not allowed"
    };

    public static NovaResponse AccessDenied = new()
    {
        ResponseCode = NipResponseCodes.SecurityViolation,
        ResponseDescription = "Access denied. Please liaise with the system administrator."
    };

    public static NovaResponse DuplicateTransactionReference = new()
    {
        ResponseCode = NipResponseCodes.DuplicateTransaction,
        ResponseDescription = "A request with the same transaction reference already exists."
    };

    public static NovaResponse NoRecordReturned = new()
    {
        ResponseCode = NipResponseCodes.UnableToLocateRecord,
        ResponseDescription = "No record found."
    };

    public static NovaResponse InsufficientBalance = new()
    {
        ResponseCode = NipResponseCodes.NoSufficientFunds,
        ResponseDescription = "Insufficient balance."
    };

    public static NovaResponse DailyLimitExceeded = new()
    {
        ResponseCode = NipResponseCodes.TransferLimitExceeded,
        ResponseDescription = "Daily outbound transfer limit exceeded."
    };

    public static NovaResponse SystemMalfunction = new()
    {
        ResponseCode = NipResponseCodes.SystemMalfunction,
        ResponseDescription = "An error occurred. Please try again later."
    };

    public static NovaResponse Conflict = new()
    {
        ResponseCode = NipResponseCodes.DuplicateRecord,
        ResponseDescription = "A conflicting request already exists."
    };

    public static NovaResponse TooManyRequests = new()
    {
        ResponseCode = NipResponseCodes.ExceedsFrequency,
        ResponseDescription = "Too many requests. Please try again later."
    };
}