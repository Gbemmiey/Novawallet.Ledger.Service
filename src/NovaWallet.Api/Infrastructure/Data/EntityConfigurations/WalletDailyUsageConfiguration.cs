using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class WalletDailyUsageConfiguration : IEntityTypeConfiguration<WalletDailyUsage>
    {
        public void Configure(EntityTypeBuilder<WalletDailyUsage> builder)
        {
            builder.ToTable("WalletDailyUsage");

            builder.HasKey(u => new
            {
                u.WalletId,
                u.UsageDate
            });

            builder.HasOne(u => u.Wallet)
                .WithMany(w => w.DailyUsages)
                .HasForeignKey(u => u.WalletId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}