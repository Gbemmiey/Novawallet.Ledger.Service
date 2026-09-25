using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Enums;

namespace NovaWallet.Infrastructure.Data.EntityConfigurations
{
    public sealed class TransferOutboxConfiguration
        : IEntityTypeConfiguration<TransferOutbox>
    {
        private const string DefaultEventType = "TransferCompleted";

        public void Configure(EntityTypeBuilder<TransferOutbox> builder)
        {
            builder.ToTable("TransferOutbox");

            builder.HasKey(t => t.Id);

            builder.Property(t => t.JournalEntryId)
                .IsRequired();

            builder.Property(t => t.EventType)
                .HasMaxLength(40)
                .HasDefaultValue(DefaultEventType)
                .IsRequired();

            builder.Property(t => t.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(OutboxStatus.Pending)
                .IsRequired();

            builder.Property(t => t.CreatedAt)
                .IsRequired();

            builder.HasOne(t => t.JournalEntry)
                .WithMany()
                .HasForeignKey(t => t.JournalEntryId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}