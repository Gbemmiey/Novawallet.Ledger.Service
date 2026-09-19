using NovaWallet.Api.Core.Enums;
using UUIDNext;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Outbox row written in the same transaction as a transfer's JournalEntry,
    /// so publishing the TransferCompleted event can never race ahead of the
    /// commit. Picked up by Workers/TransferOutboxWorker.cs. Never deleted after
    /// publishing — it doubles as a record of what was announced and when.
    /// </summary>
    public class TransferOutbox
    {
        public Guid Id { get; }
        public Guid JournalEntryId { get; }
        public JournalEntry? JournalEntry { get; private set; }
        public string EventType { get; }
        public OutboxStatus Status { get; private set; }
        public DateTime CreatedAt { get; }

        private TransferOutbox(Guid id, Guid journalEntryId, string eventType, OutboxStatus status, DateTime createdAt)
        {
            Id = id;
            JournalEntryId = journalEntryId;
            EventType = eventType;
            Status = status;
            CreatedAt = createdAt;
        }

        public static TransferOutbox Create(Guid journalEntryId, string eventType = "TransferCompleted")
        {
            if (journalEntryId == Guid.Empty)
                throw new ArgumentException("JournalEntryId is required.", nameof(journalEntryId));

            return new TransferOutbox(
                id: Uuid.NewSequential(),
                journalEntryId: journalEntryId,
                eventType: eventType,
                status: OutboxStatus.Pending,
                createdAt: DateTime.UtcNow);
        }

        public void MarkPublished()
        {
            if (Status != OutboxStatus.Pending)
                throw new InvalidOperationException($"TransferOutbox {Id} is not pending (status: {Status}).");
            Status = OutboxStatus.Processed;
        }

        public void MarkFailed() => Status = OutboxStatus.Failed;
    }
}