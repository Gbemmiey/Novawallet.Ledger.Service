using Microsoft.EntityFrameworkCore;
using NovaWallet.Application.Abstractions;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Infrastructure.Data
{
    /// <summary>
    /// Represents the Entity Framework Core database context for the application. Implements
    /// <see cref="IApplicationDbContext"/> so Application-layer services depend on that
    /// interface (defined in NovaWallet.Application, with no Npgsql reference) rather than on
    /// this concrete, Postgres-bound type.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="NovaWalletDbContext"/> class.
    /// </remarks>
    /// <param name="options">
    /// The options used to configure the database context.
    /// </param>
    public class NovaWalletDbContext(DbContextOptions<NovaWalletDbContext> options) : DbContext(options), IApplicationDbContext
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
        public DbSet<LedgerSnapshot> LedgerSnapshots => Set<LedgerSnapshot>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Each entity's Fluent API configuration lives in its own
            // IEntityTypeConfiguration<T> under Data/Configurations/ — keeps this
            // file from growing into a wall of unrelated config as entities are added.
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(NovaWalletDbContext).Assembly);
        }
    }
}