namespace NovaWallet.Domain.Responses;

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