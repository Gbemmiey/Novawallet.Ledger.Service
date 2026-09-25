using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Infrastructure.Data.EntityConfigurations
{
    public sealed class WalletDailyUsageConfiguration
        : IEntityTypeConfiguration<WalletDailyUsage>
    {
        public void Configure(EntityTypeBuilder<WalletDailyUsage> builder)
        {
            builder.ToTable("WalletDailyUsage");

            builder.HasKey(u => new
            {
                u.WalletId,
                u.UsageDate
            });

            builder.Property(u => u.WalletId)
                .IsRequired();

            builder.Property(u => u.UsageDate)
                .IsRequired();

            builder.Property(u => u.TotalSpentKobo)
                .IsRequired();

            builder.HasOne(u => u.Wallet)
                .WithMany(w => w.DailyUsages)
                .HasForeignKey(u => u.WalletId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}