using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Infrastructure.Data.EntityConfigurations
{
    public sealed class AccountEntryConfiguration : IEntityTypeConfiguration<AccountEntry>
    {
        public void Configure(EntityTypeBuilder<AccountEntry> builder)
        {
            builder.ToTable(
                "AccountEntries",
                table => table.HasCheckConstraint(
                    "CHK_AccountEntries_PositiveAmount",
                    "\"AmountKobo\" > 0"));

            builder.HasKey(a => a.Id);

            builder.Property(a => a.Id)
                .ValueGeneratedNever();

            builder.Property(a => a.JournalEntryId)
                .IsRequired();

            builder.Property(a => a.AccountId)
                .IsRequired();

            builder.Property(a => a.AmountKobo)
                .IsRequired();

            builder.Property(a => a.EntryType)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            builder.Property(a => a.TransParticulars)
                .HasMaxLength(100)
                .IsRequired();

            builder.Property(a => a.CreatedAt)
                .IsRequired();

            builder.HasOne(a => a.JournalEntry)
                .WithMany(j => j.Lines)
                .HasForeignKey(a => a.JournalEntryId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(a => a.Account)
                .WithMany(acc => acc.Entries)
                .HasForeignKey(a => a.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // Supports paginated, newest-first account statement queries.
            builder.HasIndex(a => new
            {
                a.AccountId,
                a.CreatedAt
            })
            .IsDescending(false, true)
            .HasDatabaseName("IX_AccountEntries_AccountId_CreatedAt");
        }
    }
}