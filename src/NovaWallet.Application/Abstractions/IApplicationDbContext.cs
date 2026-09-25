using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Application.Abstractions;

/// <summary>
/// The shape of the database that Application-layer services depend on. Deliberately EF-Core-
/// shaped (DbSet&lt;T&gt;, DatabaseFacade, ChangeTracker) rather than a narrower repository
/// abstraction - the services here use EF's own execution-strategy/transaction/raw-SQL surface
/// directly (see e.g. TransferService's guarded atomic UPDATEs), and re-abstracting that on top
/// would just be a second, thinner copy of the same EF API. What this interface *does* buy: no
/// Application file references <c>NovaWallet.Infrastructure</c> or Npgsql - only
/// <c>Microsoft.EntityFrameworkCore</c>/<c>.Relational</c>, which are provider-neutral. The
/// concrete <c>NovaWalletDbContext</c> (NovaWallet.Infrastructure.Data) is the only place that
/// binds this shape to Postgres.
/// </summary>
public interface IApplicationDbContext
{
    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    DbSet<Wallet> Wallets { get; }
    DbSet<WalletDailyUsage> WalletDailyUsages { get; }
    DbSet<Account> Accounts { get; }
    DbSet<ExternalCreditRequest> ExternalCreditRequests { get; }
    DbSet<DepositOutbox> DepositOutboxEntries { get; }
    DbSet<TransferOutbox> TransferOutboxEntries { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<AccountEntry> AccountEntries { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<WalletTransfer> WalletTransfers { get; }
    DbSet<LedgerSnapshot> LedgerSnapshots { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
}
