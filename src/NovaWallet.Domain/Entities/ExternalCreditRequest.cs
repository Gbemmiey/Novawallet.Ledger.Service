using NovaWallet.Domain.Enums;
using UUIDNext;

namespace NovaWallet.Domain.Entities
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
        public DepositStatus Status { get; private set; }
        public DateTime CreatedAt { get; }
        public DateTime DateModified { get; private set; }
        public DateTime? CompletedDate { get; private set; }

        private readonly List<DepositOutbox> _outboxEntries = [];
        public IReadOnlyCollection<DepositOutbox> OutboxEntries => _outboxEntries;

        private ExternalCreditRequest(Guid id, string sessionId, string transactionReference, long amountKobo,
            string beneficiaryAccountNumber, string originatingAccountNumber, string originatingBankCode,
            DepositStatus status, DateTime createdAt, DateTime dateModified, DateTime? completedDate)
        {
            Id = id;
            SessionId = sessionId;
            TransactionReference = transactionReference;
            AmountKobo = amountKobo;
            BeneficiaryAccountNumber = beneficiaryAccountNumber;
            OriginatingAccountNumber = originatingAccountNumber;
            OriginatingBankCode = originatingBankCode;
            Status = status;
            CreatedAt = createdAt;
            DateModified = dateModified;
            CompletedDate = completedDate;
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

            var now = DateTime.UtcNow;

            return new ExternalCreditRequest(
                id: Uuid.NewSequential(),
                sessionId: sessionId,
                transactionReference: transactionReference,
                amountKobo: amountKobo,
                beneficiaryAccountNumber: beneficiaryAccountNumber,
                originatingAccountNumber: originatingAccountNumber,
                originatingBankCode: originatingBankCode,
                status: DepositStatus.Pending,
                createdAt: now,
                dateModified: now,
                completedDate: null);
        }

        public void MarkCompleted()
        {
            if (Status != DepositStatus.Pending)
                throw new InvalidOperationException($"ExternalCreditRequest {Id} is not pending (status: {Status}).");

            var now = DateTime.UtcNow;
            Status = DepositStatus.Completed;
            CompletedDate = now;
            DateModified = now;
        }

        public void MarkFailed()
        {
            if (Status != DepositStatus.Pending)
                throw new InvalidOperationException($"ExternalCreditRequest {Id} is not pending (status: {Status}).");

            Status = DepositStatus.Failed;
            DateModified = DateTime.UtcNow;
        }
    }
}
