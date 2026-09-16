using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class AccountEntryConfiguration : IEntityTypeConfiguration<AccountEntry>
    {
        public void Configure(EntityTypeBuilder<AccountEntry> builder)
        {
            builder.ToTable(
                "AccountEntries",
                table => table.HasCheckConstraint(
                    "CHK_AccountEntries_ExclusiveSign",
                    """
                ("DebitAmountKobo" > 0 AND "CreditAmountKobo" = 0)
                OR
                ("DebitAmountKobo" = 0 AND "CreditAmountKobo" > 0)
                """));

            builder.HasKey(a => a.Id);

            builder.Property(a => a.Id)
                .ValueGeneratedNever();

            builder.Property(a => a.JournalEntryId)
                .IsRequired();

            builder.Property(a => a.AccountId)
                .IsRequired();

            builder.Property(a => a.DebitAmountKobo)
                .IsRequired();

            builder.Property(a => a.CreditAmountKobo)
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