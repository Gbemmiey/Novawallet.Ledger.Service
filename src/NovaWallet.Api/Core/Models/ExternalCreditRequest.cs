using UUIDNext;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Raw record of an inbound NIP credit callback, captured synchronously by the
    /// webhook before returning HTTP 202. SessionId's uniqueness is what makes the
    /// DepositConsumer's processing idempotent under at-least-once delivery.
    /// </summary>
    public class ExternalCreditRequest
    {
        public Guid Id { get; }
        public string SessionId { get; }
        public string TransactionReference { get; }
        public long AmountKobo { get; }
        public string BeneficiaryAccountNumber { get; }
        public string OriginatingAccountNumber { get; }
        public string OriginatingBankCode { get; }
        public bool IsProcessed { get; private set; }
        public DateTime CreatedAt { get; }

        private readonly List<DepositOutbox> _outboxEntries = [];
        public IReadOnlyCollection<DepositOutbox> OutboxEntries => _outboxEntries;

        private ExternalCreditRequest(Guid id, string sessionId, string transactionReference, long amountKobo,
            string beneficiaryAccountNumber, string originatingAccountNumber, string originatingBankCode,
            bool isProcessed, DateTime createdAt)
        {
            Id = id;
            SessionId = sessionId;
            TransactionReference = transactionReference;
            AmountKobo = amountKobo;
            BeneficiaryAccountNumber = beneficiaryAccountNumber;
            OriginatingAccountNumber = originatingAccountNumber;
            OriginatingBankCode = originatingBankCode;
            IsProcessed = isProcessed;
            CreatedAt = createdAt;
        }

        public static ExternalCreditRequest Create(string sessionId, string transactionReference, long amountKobo,
            string beneficiaryAccountNumber, string originatingAccountNumber, string originatingBankCode)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("SessionId is required.", nameof(sessionId));
            if (amountKobo <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountKobo), "AmountKobo must be positive.");
            if (string.IsNullOrWhiteSpace(beneficiaryAccountNumber))
                throw new ArgumentException("BeneficiaryAccountNumber is required.", nameof(beneficiaryAccountNumber));

            return new ExternalCreditRequest(
                id: Uuid.NewSequential(),
                sessionId: sessionId,
                transactionReference: transactionReference,
                amountKobo: amountKobo,
                beneficiaryAccountNumber: beneficiaryAccountNumber,
                originatingAccountNumber: originatingAccountNumber,
                originatingBankCode: originatingBankCode,
                isProcessed: false,
                createdAt: DateTime.UtcNow);
        }

        public void MarkProcessed()
        {
            if (IsProcessed) throw new InvalidOperationException($"ExternalCreditRequest {Id} is already processed.");
            IsProcessed = true;
        }
    }
}