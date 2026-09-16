using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data.EntityConfigurations
{
    public sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
    {
        public void Configure(EntityTypeBuilder<JournalEntry> builder)
        {
            builder.ToTable("JournalEntries");

            builder.HasKey(j => j.Id);

            builder.Property(j => j.IdempotencyKey)
                .HasMaxLength(128)
                .IsRequired();

            builder.HasIndex(j => j.IdempotencyKey)
                .IsUnique();

            builder.Property(j => j.RequestPayloadHash)
                .HasMaxLength(64)
                .IsRequired();

            // Computed in memory and intentionally not persisted.
            builder.Ignore(j => j.IsBalanced);

            builder.Navigation(j => j.Lines)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        }
    }
}