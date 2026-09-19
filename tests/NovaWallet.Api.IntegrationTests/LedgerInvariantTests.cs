using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// Database-level guarantees that sit underneath the application code: the CHECK constraints and
/// unique indexes that are the "non-bypassable backstop" the README describes, plus the ledger
/// invariants over everything the rest of the suite has posted so far.
/// </summary>
[Collection(ApiCollection.Name)]
public class LedgerInvariantTests
{
    private readonly ApiFixture _fixture;

    public LedgerInvariantTests(ApiFixture fixture) => _fixture = fixture;

    /// <summary>EF may or may not wrap the provider exception; find it either way.</summary>
    private static PostgresException? FindPostgresException(Exception exception)
    {
        Exception? current = exception;

        while (current is not null)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }

            current = current.InnerException;
        }

        return null;
    }

    [Fact]
    public async Task EveryJournalBalances_AndEveryWalletMatchesItsLedger()
    {
        // Give the check something to chew on even when this test runs first.
        var sender = await _fixture.CreateUserAsync(fundKobo: 300_000);
        var receiver = await _fixture.CreateUserAsync(fundKobo: 50_000);
        await sender.Client.TransferAsync(receiver.WalletId, 120_000, Guid.NewGuid().ToString());
        await receiver.Client.TransferAsync(sender.WalletId, 999_999_999, Guid.NewGuid().ToString()); // rejected

        await using var db = _fixture.CreateDbContext();

        await LedgerAssertions.AssertConsistentAsync(db);
    }

    [Fact]
    public async Task TheDatabase_RefusesANegativeBalance_EvenWhenTheApplicationWouldNot()
    {
        var user = await _fixture.CreateUserAsync(fundKobo: 1_000);

        await using var db = _fixture.CreateDbContext();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Wallets\" SET \"AvailableBalanceKobo\" = {-1L} WHERE \"Id\" = {user.WalletId}"));

        var postgres = FindPostgresException(exception);
        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres!.SqlState);
        Assert.Equal(1_000, await _fixture.GetBalanceAsync(user));
    }

    [Fact]
    public async Task TheDatabase_RefusesASecondWalletForTheSameUser()
    {
        var user = await _fixture.CreateUserAsync();

        await using var db = _fixture.CreateDbContext();

        var account = Account.Create(
            accountNumber: Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(),
            accountType: AccountType.Liability);
        db.Accounts.Add(account);

        var userId = await db.Wallets
            .Where(w => w.Id == user.WalletId)
            .Select(w => w.UserId)
            .SingleAsync();
        db.Wallets.Add(Wallet.Create(userId, account.Id));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, FindPostgresException(exception)?.SqlState);
    }

    [Fact]
    public async Task TheDatabase_RefusesADuplicateIdempotencyKey()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();
        var key = Guid.NewGuid().ToString();

        // Two failed rows with the same key: only the unique index stands between them.
        await using (var first = _fixture.CreateDbContext())
        {
            first.WalletTransfers.Add(WalletTransfer.CreateFailed(
                sender.WalletId, receiver.WalletId, 1_000, null, key, "HASH-A", "51", "Insufficient balance."));
            await first.SaveChangesAsync();
        }

        await using var second = _fixture.CreateDbContext();
        second.WalletTransfers.Add(WalletTransfer.CreateFailed(
            sender.WalletId, receiver.WalletId, 1_000, null, key, "HASH-B", "51", "Insufficient balance."));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, FindPostgresException(exception)?.SqlState);
    }

    [Fact]
    public async Task TheDatabase_AllowsManyFailedRowsWithoutAJournalEntry()
    {
        // JournalEntryId is nullable and uniquely indexed: Postgres must treat NULLs as distinct,
        // or a second failed transfer could never be stored.
        var sender = await _fixture.CreateUserAsync(fundKobo: 10_000);
        var receiver = await _fixture.CreateUserAsync();

        await using var db = _fixture.CreateDbContext();
        for (var i = 0; i < 3; i++)
        {
            db.WalletTransfers.Add(WalletTransfer.CreateFailed(
                sender.WalletId, receiver.WalletId, 1_000, null, Guid.NewGuid().ToString(), "HASH", "51", "Insufficient balance."));
        }

        await db.SaveChangesAsync();

        Assert.Equal(3, await db.WalletTransfers.CountAsync(t => t.SourceWalletId == sender.WalletId));
    }
}
