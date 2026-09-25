using NovaWallet.Domain.Enums;
using UUIDNext;

namespace NovaWallet.Domain.Entities
{
    /// <summary>
    /// Outbox row written in the same transaction as ExternalCreditRequest so the
    /// webhook can return 202 without waiting on the DepositConsumer. Picked up by
    /// Workers/DepositConsumer.cs.
    /// </summary>
    public class DepositOutbox
    {
        /// <summary>Hard ceiling on transient-failure retries before a row is given up on
        /// and marked permanently <see cref="OutboxStatus.Failed"/> - see
        /// <see cref="RecordFailedAttempt"/>.</summary>
        public const int MaxRetries = 3;

        public Guid Id { get; }
        public Guid ExternalCreditRequestId { get; }
        public ExternalCreditRequest? ExternalCreditRequest { get; private set; }
        public OutboxStatus Status { get; private set; }
        public int NumberOfRetries { get; private set; }
        public DateTime CreatedAt { get; }
        public DateTime? DateProcessed { get; private set; }

        /// <summary>
        /// W3C <c>traceparent</c> of the webhook request that wrote this row, so the polling
        /// <c>DepositConsumer</c> can continue the same distributed trace across the outbox.
        /// Null for rows written before tracing was added, or when no activity was ambient.
        /// </summary>
        public string? TraceParent { get; }

        private DepositOutbox(Guid id, Guid externalCreditRequestId, OutboxStatus status, int numberOfRetries,
            DateTime createdAt, DateTime? dateProcessed, string? traceParent)
        {
            Id = id;
            ExternalCreditRequestId = externalCreditRequestId;
            Status = status;
            NumberOfRetries = numberOfRetries;
            CreatedAt = createdAt;
            DateProcessed = dateProcessed;
            TraceParent = traceParent;
        }

        public static DepositOutbox Create(Guid externalCreditRequestId, string? traceParent = null)
        {
            if (externalCreditRequestId == Guid.Empty)
                throw new ArgumentException("ExternalCreditRequestId is required.", nameof(externalCreditRequestId));

            return new DepositOutbox(
                id: Uuid.NewSequential(),
                externalCreditRequestId: externalCreditRequestId,
                status: OutboxStatus.Pending,
                numberOfRetries: 0,
                createdAt: DateTime.UtcNow,
                dateProcessed: null,
                traceParent: traceParent);
        }

        public void MarkProcessed()
        {
            if (Status != OutboxStatus.Pending)
                throw new InvalidOperationException($"DepositOutbox {Id} is not pending (status: {Status}).");

            Status = OutboxStatus.Processed;
            DateProcessed = DateTime.UtcNow;
        }

        public void MarkFailed()
        {
            Status = OutboxStatus.Failed;
            DateProcessed = DateTime.UtcNow;
        }

        /// <summary>
        /// Records a transient-failure attempt on this row. Returns <see langword="true"/>
        /// once <see cref="MaxRetries"/> has been reached, at which point the row is also
        /// transitioned to <see cref="OutboxStatus.Failed"/> internally (i.e. "exhausted,
        /// stop retrying, cascade the failure") - otherwise the row is left
        /// <see cref="OutboxStatus.Pending"/> so the next poll cycle retries it.
        /// </summary>
        public bool RecordFailedAttempt()
        {
            NumberOfRetries++;

            if (NumberOfRetries >= MaxRetries)
            {
                MarkFailed();
                return true;
            }

            return false;
        }
    }
}
