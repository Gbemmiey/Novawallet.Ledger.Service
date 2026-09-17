namespace NovaWallet.Api.Core.Dto.Login
{
    public class MockLoginRequest
    {
        public Guid UserId { get; set; }
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
        public string SourceWalletId { get; set; }
        public string DestinationWalletId { get; set; }
        
        public long AmountInKobo { get; set; }
        
        public string Narration { get; set; }
    }
    
}