using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.Infrastructure.Data
{
    /// <summary>
    /// Represents the Entity Framework Core database context for the application.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="NovaWalletDbContext"/> class.
    /// </remarks>
    /// <param name="options">
    /// The options used to configure the database context.
    /// </param>
    public class NovaWalletDbContext(DbContextOptions<NovaWalletDbContext> options) : DbContext(options)
    {
        public DbSet<Wallet> Wallets => Set<Wallet>();
        public DbSet<WalletDailyUsage> WalletDailyUsages => Set<WalletDailyUsage>();
        public DbSet<Account> Accounts => Set<Account>();
        public DbSet<ExternalCreditRequest> ExternalCreditRequests => Set<ExternalCreditRequest>();
        public DbSet<DepositOutbox> DepositOutboxEntries => Set<DepositOutbox>();
        public DbSet<TransferOutbox> TransferOutboxEntries => Set<TransferOutbox>();
        public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
        public DbSet<AccountEntry> AccountEntries => Set<AccountEntry>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<WalletTransfer> WalletTransfers => Set<WalletTransfer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Each entity's Fluent API configuration lives in its own
            // IEntityTypeConfiguration<T> under Data/Configurations/ — keeps this
            // file from growing into a wall of unrelated config as entities are added.
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(NovaWalletDbContext).Assembly);
        }
    }
}