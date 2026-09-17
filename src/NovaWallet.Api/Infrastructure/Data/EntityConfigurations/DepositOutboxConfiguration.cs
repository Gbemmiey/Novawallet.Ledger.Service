using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class DepositOutboxConfiguration : IEntityTypeConfiguration<DepositOutbox>
    {
        public void Configure(EntityTypeBuilder<DepositOutbox> builder)
        {
            builder.ToTable("DepositOutbox");

            builder.HasKey(d => d.Id);

            builder.Property(d => d.ExternalCreditRequestId)
                .IsRequired();

            builder.Property(d => d.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(OutboxStatus.Pending)
                .IsRequired();

            builder.Property(d => d.NumberOfRetries)
                .HasDefaultValue(0)
                .IsRequired();

            builder.Property(d => d.CreatedAt)
                .IsRequired();

            builder.Property(d => d.DateProcessed);

            builder.HasOne(d => d.ExternalCreditRequest)
                .WithMany(x => x.OutboxEntries)
                .HasForeignKey(d => d.ExternalCreditRequestId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}