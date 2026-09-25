using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.IntegrationTests.Infrastructure;
using NovaWallet.Domain.Enums;
using System.Net;

namespace NovaWallet.Api.IntegrationTests;

/// <summary>
/// The ReconciliationWorker, sweeping every second with zero watermark grace. Its one automated
/// action - freezing a customer's wallet - must fire on a real discrepancy and never on a healthy
/// wallet, so both directions are covered.
/// </summary>
[Collection(ReconciliationCollection.Name)]
public class ReconciliationTests
{
    private static readonly TimeSpan SweepTimeout = TimeSpan.FromSeconds(45);

    private readonly ReconciliationFixture _fixture;

    public ReconciliationTests(ReconciliationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ABalancedWallet_IsSnapshotted_AndStaysActive()
    {
        var user = await _fixture.CreateUserAsync(fundKobo: 50_000);

        await Wait.UntilAsync(async () =>
        {
            await using var db = _fixture.CreateDbContext();
            return await db.LedgerSnapshots.AnyAsync(s =>
                s.WalletId == user.WalletId && s.IsBalanced && s.WalletBalanceKobo == 50_000 && s.LedgerBalanceKobo == 50_000);
        },
        SweepTimeout,
        () => "No balanced LedgerSnapshot for the funded wallet appeared - the worker may not be sweeping.");

        await using var check = _fixture.CreateDbContext();
        Assert.Equal(WalletStatus.Active, (await check.Wallets.AsNoTracking().SingleAsync(w => w.Id == user.WalletId)).Status);
        Assert.False(await check.LedgerSnapshots.AnyAsync(s => s.WalletId == user.WalletId && !s.IsBalanced));
    }

    [Fact]
    public async Task ADiscrepancy_IsRecorded_AndFreezesTheWallet_WithAnAuditTrail()
    {
        var user = await _fixture.CreateUserAsync(fundKobo: 100_000);

        // Corrupt the wallet behind the ledger's back: +777 kobo that no journal entry explains.
        await using (var db = _fixture.CreateDbContext())
        {
            var rows = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Wallets\" SET \"AvailableBalanceKobo\" = \"AvailableBalanceKobo\" + {777L} WHERE \"Id\" = {user.WalletId}");
            Assert.Equal(1, rows);
        }

        await Wait.UntilAsync(async () =>
        {
            await using var db = _fixture.CreateDbContext();
            return (await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == user.WalletId)).Status == WalletStatus.Frozen;
        },
        SweepTimeout,
        () => "The unbalanced wallet was never auto-frozen.");

        await using var check = _fixture.CreateDbContext();

        var snapshot = await check.LedgerSnapshots.AsNoTracking()
            .Where(s => s.WalletId == user.WalletId && !s.IsBalanced)
            .OrderBy(s => s.CreatedAt)
            .FirstAsync();
        Assert.Equal(777, snapshot.DiscrepancyKobo);
        Assert.Equal(100_777, snapshot.WalletBalanceKobo);
        Assert.Equal(100_000, snapshot.LedgerBalanceKobo);

        var freeze = await check.AuditLogs.AsNoTracking().SingleAsync(a => a.WalletId == user.WalletId && a.Action == "Freeze");
        Assert.Equal("system:reconciliation", freeze.ActorSubject);
        Assert.Equal(snapshot.Id, freeze.CorrelationId);
    }

    [Fact]
    public async Task AFrozenWallet_CanNoLongerSendMoney()
    {
        var sender = await _fixture.CreateUserAsync(fundKobo: 100_000);
        var receiver = await _fixture.CreateUserAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Wallets\" SET \"AvailableBalanceKobo\" = \"AvailableBalanceKobo\" + {1L} WHERE \"Id\" = {sender.WalletId}");
        }

        await Wait.UntilAsync(async () =>
        {
            await using var db = _fixture.CreateDbContext();
            return (await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == sender.WalletId)).Status == WalletStatus.Frozen;
        },
        SweepTimeout,
        () => "The unbalanced wallet was never auto-frozen.");

        var response = await sender.Client.TransferAsync(receiver.WalletId, 1_000, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("57", await response.ReadResponseCodeAsync());
    }

    [Fact]
    public async Task HealthyWallets_ActiveDuringConcurrentTransfers_AreNeverFlaggedOrFrozen()
    {
        // The false-positive check: the sweep reads wallet balance and ledger in one statement, so
        // transfers committing mid-sweep must never look like a discrepancy.
        var a = await _fixture.CreateUserAsync(fundKobo: 1_000_000);
        var b = await _fixture.CreateUserAsync(fundKobo: 1_000_000);

        for (var round = 0; round < 4; round++)
        {
            var burst = Enumerable.Range(0, 15).SelectMany(_ => new[]
            {
                a.Client.TransferAsync(b.WalletId, 700, Guid.NewGuid().ToString()),
                b.Client.TransferAsync(a.WalletId, 300, Guid.NewGuid().ToString())
            });

            Assert.All(await Task.WhenAll(burst), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            await Task.Delay(TimeSpan.FromSeconds(1.2)); // let a few sweeps land between bursts
        }

        // A sweep that has seen the final state.
        await Wait.UntilAsync(async () =>
        {
            await using var db = _fixture.CreateDbContext();
            var expectedA = 1_000_000 - (60 * 700) + (60 * 300);
            return await db.LedgerSnapshots.AnyAsync(s => s.WalletId == a.WalletId && s.WalletBalanceKobo == expectedA);
        },
        SweepTimeout,
        () => "The worker never snapshotted the final balance.");

        await using var check = _fixture.CreateDbContext();

        Assert.False(await check.LedgerSnapshots.AnyAsync(s =>
            (s.WalletId == a.WalletId || s.WalletId == b.WalletId) && !s.IsBalanced),
            "A healthy wallet was reported as unbalanced.");

        var statuses = await check.Wallets.AsNoTracking()
            .Where(w => w.Id == a.WalletId || w.Id == b.WalletId)
            .Select(w => w.Status)
            .ToListAsync();
        Assert.All(statuses, s => Assert.Equal(WalletStatus.Active, s));

        Assert.False(await check.AuditLogs.AnyAsync(l =>
            (l.WalletId == a.WalletId || l.WalletId == b.WalletId) && l.Action == "Freeze"));
    }
}
