using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class LedgerSnapshotConfiguration : IEntityTypeConfiguration<LedgerSnapshot>
    {
        public void Configure(EntityTypeBuilder<LedgerSnapshot> builder)
        {
            builder.ToTable("LedgerSnapshot");

            builder.HasKey(s => s.Id);

            builder.Property(s => s.Id)
                .ValueGeneratedNever();

            builder.Property(s => s.RunId)
                .IsRequired();

            builder.Property(s => s.WalletId)
                .IsRequired();

            builder.Property(s => s.AccountId)
                .IsRequired();

            builder.Property(s => s.WalletBalanceKobo)
                .IsRequired();

            builder.Property(s => s.LedgerBalanceKobo)
                .IsRequired();

            builder.Property(s => s.DiscrepancyKobo)
                .IsRequired();

            builder.Property(s => s.IsBalanced)
                .IsRequired();

            builder.Property(s => s.CreatedAt)
                .IsRequired();

            builder.HasOne(s => s.Wallet)
                .WithMany()
                .HasForeignKey(s => s.WalletId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(s => s.Account)
                .WithMany()
                .HasForeignKey(s => s.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // Latest snapshot per wallet - "show current reconciliation status" query.
            builder.HasIndex(s => new { s.WalletId, s.CreatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("IX_LedgerSnapshot_WalletId_CreatedAt");

            // Fast "list current discrepancies, newest first" query - a partial index over
            // mismatches only (the overwhelming majority of rows are expected to be balanced).
            // Indexed on CreatedAt rather than IsBalanced itself, since every row satisfying the
            // filter already has IsBalanced = false - indexing the flag alone would add nothing.
            builder.HasIndex(s => s.CreatedAt)
                .IsDescending()
                .HasFilter("\"IsBalanced\" = false")
                .HasDatabaseName("IX_LedgerSnapshot_IsBalanced_Partial");
        }
    }
}
