using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class WalletTransferConfiguration : IEntityTypeConfiguration<WalletTransfer>
    {
        public void Configure(EntityTypeBuilder<WalletTransfer> builder)
        {
            builder.ToTable(
                "WalletTransfers",
                table => table.HasCheckConstraint(
                    "CHK_WalletTransfers_AmountPositive",
                    "\"AmountKobo\" > 0"));

            builder.HasKey(t => t.Id);

            builder.Property(t => t.Id)
                .ValueGeneratedNever();

            builder.Property(t => t.JournalEntryId)
                .IsRequired();

            builder.Property(t => t.SourceWalletId)
                .IsRequired();

            builder.Property(t => t.DestinationWalletId)
                .IsRequired();

            builder.Property(t => t.AmountKobo)
                .IsRequired();

            builder.Property(t => t.Narration)
                .HasMaxLength(200);

            builder.Property(t => t.PaymentReference)
                .HasMaxLength(64)
                .IsRequired();

            builder.Property(t => t.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(TransferStatus.Completed)
                .IsRequired();

            builder.Property(t => t.TransactionDate)
                .IsRequired();

            builder.Property(t => t.DateModified)
                .IsRequired();

            // One WalletTransfer per JournalEntry - mirrors the 1:1 relationship between
            // a transfer and its ledger posting.
            builder.HasIndex(t => t.JournalEntryId)
                .IsUnique();

            // PaymentReference is the externally quotable reference for a transfer - an
            // independently system-generated UUID v7, decoupled from JournalEntryId -
            // indexed on its own since "look this transfer up by its reference" is a
            // distinct, more likely query pattern than joining through JournalEntryId.
            builder.HasIndex(t => t.PaymentReference)
                .IsUnique();

            builder.HasOne(t => t.JournalEntry)
                .WithMany()
                .HasForeignKey(t => t.JournalEntryId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(t => t.SourceWallet)
                .WithMany()
                .HasForeignKey(t => t.SourceWalletId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(t => t.DestinationWallet)
                .WithMany()
                .HasForeignKey(t => t.DestinationWalletId)
                .OnDelete(DeleteBehavior.Restrict);

            // Supports paginated, newest-first transfer history per wallet - a wallet can
            // appear as either the source or the destination of a transfer, so both
            // directions get their own index (the query pattern a "list my transfers" /
            // statement endpoint needs).
            builder.HasIndex(t => new { t.SourceWalletId, t.TransactionDate })
                .IsDescending(false, true)
                .HasDatabaseName("IX_WalletTransfers_SourceWalletId_TransactionDate");

            builder.HasIndex(t => new { t.DestinationWalletId, t.TransactionDate })
                .IsDescending(false, true)
                .HasDatabaseName("IX_WalletTransfers_DestinationWalletId_TransactionDate");
        }
    }
}
