using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
    {
        public void Configure(EntityTypeBuilder<Account> builder)
        {
            builder.ToTable("Accounts");

            builder.HasKey(a => a.Id);

            builder.Property(a => a.AccountNumber)
                .HasMaxLength(32)
                .IsRequired();

            builder.HasIndex(a => a.AccountNumber)
                .IsUnique();

            builder.Property(a => a.AccountType)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(a => a.Currency)
                .HasMaxLength(3)
                .HasDefaultValue("NGN")
                .IsRequired();

            builder.Navigation(a => a.Entries)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        }
    }
}