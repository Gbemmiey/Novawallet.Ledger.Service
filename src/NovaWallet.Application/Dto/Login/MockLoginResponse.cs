using NovaWallet.Domain.Enums;

namespace NovaWallet.Application.Dto.Login
{
    public class MockLoginRequest
    {
        public Guid UserId { get; set; }

        /// <summary>
        /// Mock-auth role, embedded verbatim into the JWT's role claim (see
        /// <see cref="Infrastructure.Providers.MockUserAuthHelper"/>). Defaults to
        /// <see cref="UserRole.Customer"/> - callers must explicitly opt in to
        /// <see cref="UserRole.Admin"/> to reach the <c>/api/v1/admin</c> endpoints.
        /// </summary>
        public UserRole Role { get; set; } = UserRole.Customer;
    }

    /// <summary>
    /// Represents the response returned upon a successful user login.
    /// </summary>
    public class MockLoginResponse
    {
        /// <summary>
        /// Gets or sets the access token used to authenticate the user's requests.
        /// </summary>
        public string AccessToken { get; set; } = string.Empty;
    }

    public class NipSingleCreditRequest
    {
        public string SessionId { get; set; }
        public string TransactionReference { get; set; }
        public long AmountKobo { get; set; }
        public string BeneficiaryAccountNumber { get; set; }
        public string OriginatingAccountNumber { get; set; }
        public string OriginatingBankCode { get; set; }

        public string Narration { get; set; }
    }

    public class NipSingleCreditResponse
    {
        public string SessionId { get; set; }
        public string TransactionReference { get; set; }
        public long AmountKobo { get; set; }
        public string BeneficiaryAccountNumber { get; set; }
        public string OriginatingAccountNumber { get; set; }

        public string Narration { get; set; }
        public string OriginatingBankCode { get; set; }
    }


    public class WalletTransferResponse
    {
        public string SourceWalletId { get; set; }
        public string DestinationWalletId { get; set; }
        
        public long AmountInKobo { get; set; }
        
        public string Narration { get; set; }
        
        public DateTime TransactionDate { get; set; }
        
        public string PaymentReference { get; set; }
    }

    public class WalletTransferRequest
    {
        /// <summary>
        /// Deprecated and rejected. The source wallet is inferred from the caller's session
        /// (JWT), so it must be omitted. This property exists only so a stale client that still
        /// sends it gets a <c>30</c> validation error instead of the value being silently dropped.
        /// </summary>
        [Obsolete("The source wallet is inferred from the session. Omit this field; it is rejected when present.")]
        public string? SourceWalletId { get; set; }

        public string DestinationWalletId { get; set; }

        public long AmountInKobo { get; set; }

        public string Narration { get; set; }
    }

    /// <summary>
    /// Outcome of a previously submitted transfer, looked up by its Idempotency-Key.
    /// </summary>
    public class WalletTransferStatusResponse
    {
        public string IdempotencyKey { get; set; }

        /// <summary>Completed, Failed or Reversed.</summary>
        public string Status { get; set; }

        /// <summary>NIP response code: "00" for a completed transfer, the failure code otherwise.</summary>
        public string ResponseCode { get; set; }

        /// <summary>Reason the transfer failed. Null unless <see cref="Status"/> is Failed.</summary>
        public string? FailureReason { get; set; }

        public string PaymentReference { get; set; }
        public string SourceWalletId { get; set; }
        public string DestinationWalletId { get; set; }
        public long AmountInKobo { get; set; }
        public string? Narration { get; set; }
        public DateTime TransactionDate { get; set; }
    }

    /// <summary>
    /// State of a previously submitted inbound NIP credit, looked up by its SessionId.
    /// </summary>
    public class NipSingleCreditStatusResponse
    {
        public string SessionId { get; set; }
        public string TransactionReference { get; set; }
        public long AmountKobo { get; set; }
        public string BeneficiaryAccountNumber { get; set; }

        /// <summary>Pending, Completed or Failed.</summary>
        public string Status { get; set; }

        /// <summary>"09" while pending, "00" once completed, "96" if it failed.</summary>
        public string ResponseCode { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedDate { get; set; }
    }

}