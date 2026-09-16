using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
    {
        public void Configure(EntityTypeBuilder<AuditLog> builder)
        {
            builder.ToTable("AuditLog");

            builder.HasKey(a => a.Id);

            builder.Property(a => a.WalletId)
                .IsRequired();

            builder.Property(a => a.ActorSubject)
                .HasMaxLength(128)
                .IsRequired();

            builder.Property(a => a.Action)
                .HasMaxLength(40)
                .IsRequired();

            builder.Property(a => a.BalanceBeforeKobo)
                .IsRequired();

            builder.Property(a => a.BalanceAfterKobo)
                .IsRequired();

            builder.Property(a => a.CorrelationId)
                .IsRequired();

            builder.Property(a => a.CreatedAt)
                .IsRequired();

            builder.HasOne(a => a.Wallet)
                .WithMany()
                .HasForeignKey(a => a.WalletId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}