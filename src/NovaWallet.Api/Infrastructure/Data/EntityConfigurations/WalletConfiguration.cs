using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class WalletConfiguration
        : IEntityTypeConfiguration<Wallet>
    {
        public void Configure(EntityTypeBuilder<Wallet> builder)
        {
            builder.ToTable(
                "Wallets",
                table => table.HasCheckConstraint(
                    "CHK_Wallet_AvailableBalanceKobo_NonNegative",
                    "\"AvailableBalanceKobo\" >= 0"));

            builder.HasKey(w => w.Id);

            builder.Property(w => w.UserId)
                .IsRequired();

            builder.Property(w => w.Currency)
                .HasMaxLength(3)
                .HasDefaultValue("NGN")
                .IsRequired();

            builder.Property(w => w.AvailableBalanceKobo)
                .IsRequired();

            builder.Property(w => w.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(w => w.AccountId)
                .IsRequired();

            builder.Property(w => w.CreatedAt)
                .IsRequired();

            builder.HasIndex(w => w.AccountId)
                .IsUnique();

            builder.HasOne(w => w.Account)
                .WithOne()
                .HasForeignKey<Wallet>(w => w.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Navigation(w => w.DailyUsages)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        }
    }
}