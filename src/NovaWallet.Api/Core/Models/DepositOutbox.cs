using NovaWallet.Api.Core.Enums;
using UUIDNext;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Outbox row written in the same transaction as ExternalCreditRequest so the
    /// webhook can return 202 without waiting on the DepositConsumer. Picked up by
    /// Workers/DepositConsumer.cs.
    /// </summary>
    public class DepositOutbox
    {
        public Guid Id { get; }
        public Guid ExternalCreditRequestId { get; }
        public ExternalCreditRequest? ExternalCreditRequest { get; private set; }
        public OutboxStatus Status { get; private set; }
        public DateTime CreatedAt { get; }

        private DepositOutbox(Guid id, Guid externalCreditRequestId, OutboxStatus status, DateTime createdAt)
        {
            Id = id;
            ExternalCreditRequestId = externalCreditRequestId;
            Status = status;
            CreatedAt = createdAt;
        }

        public static DepositOutbox Create(Guid externalCreditRequestId)
        {
            if (externalCreditRequestId == Guid.Empty)
                throw new ArgumentException("ExternalCreditRequestId is required.", nameof(externalCreditRequestId));

            return new DepositOutbox(
                id: Uuid.NewSequential(),
                externalCreditRequestId: externalCreditRequestId,
                status: OutboxStatus.Pending,
                createdAt: DateTime.UtcNow);
        }

        public void MarkProcessed()
        {
            if (Status != OutboxStatus.Pending)
                throw new InvalidOperationException($"DepositOutbox {Id} is not pending (status: {Status}).");
            Status = OutboxStatus.Processed;
        }

        public void MarkFailed() => Status = OutboxStatus.Failed;
    }
}