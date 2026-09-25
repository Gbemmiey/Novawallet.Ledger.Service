using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Enums;

namespace NovaWallet.Infrastructure.Data.EntityConfigurations
{
    public sealed class ExternalCreditRequestConfiguration
        : IEntityTypeConfiguration<ExternalCreditRequest>
    {
        public void Configure(EntityTypeBuilder<ExternalCreditRequest> builder)
        {
            builder.ToTable(
                "ExternalCreditRequests",
                table => table.HasCheckConstraint(
                    "CHK_ExternalCreditRequest_AmountKobo_Positive",
                    "\"AmountKobo\" > 0"));

            builder.HasKey(x => x.Id);

            builder.Property(x => x.SessionId)
                .HasMaxLength(30)
                .IsRequired();

            builder.HasIndex(x => x.SessionId)
                .IsUnique();

            builder.Property(x => x.TransactionReference)
                .HasMaxLength(64)
                .IsRequired();

            builder.HasIndex(x => x.TransactionReference)
                .IsUnique();

            builder.Property(x => x.AmountKobo)
                .IsRequired();

            builder.Property(x => x.BeneficiaryAccountNumber)
                .HasMaxLength(32)
                .IsRequired();

            builder.Property(x => x.OriginatingAccountNumber)
                .HasMaxLength(32)
                .IsRequired();

            builder.Property(x => x.OriginatingBankCode)
                .HasMaxLength(10)
                .IsRequired();

            builder.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(DepositStatus.Pending)
                .IsRequired();

            builder.Property(x => x.CreatedAt)
                .IsRequired();

            builder.Property(x => x.DateModified)
                .IsRequired();

            builder.Property(x => x.CompletedDate);

            builder.Navigation(x => x.OutboxEntries)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        }
    }
}