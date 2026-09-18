using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;
using NovaWallet.Api.Core.Options;
using NovaWallet.Api.Infrastructure.Data;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry;
using System.Diagnostics;
using UUIDNext;

namespace NovaWallet.Api.Workers
{
    /// <summary>
    /// Continuously sweeping background worker that asserts, for every wallet:
    /// Wallet.AvailableBalanceKobo == Sum(AccountEntries.Credit) - Sum(AccountEntries.Debit)
    /// for the wallet's own Account (README §3), and auto-freezes any <see cref="WalletStatus.Active"/>
    /// wallet found unbalanced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sweep strategy:</b> an in-memory keyset cursor (<c>WHERE "Id" > cursor ORDER BY "Id"
    /// LIMIT BatchSize</c>) selects each batch. When a batch comes back with fewer rows than
    /// <see cref="ReconciliationWorkerOptions.BatchSize"/> (end of table reached), the cursor
    /// resets to <see cref="Guid.Empty"/> so the sweep loops continuously over the whole wallet
    /// population rather than only ever checking the first page. The cursor is in-memory only -
    /// a restart resets it to the start, which is fine since reconciliation is read-only/idempotent
    /// and self-heals every sweep. Running more than one instance of this worker is safe for the
    /// same reason: each instance sweeps independently/redundantly, and the freeze attempt below
    /// is itself a guarded atomic UPDATE, so a race between two instances just means one of them
    /// finds zero rows affected and logs the discrepancy without claiming the freeze.
    /// </para>
    /// <para>
    /// <b>Incremental reconciliation:</b> rather than re-summing an account's entire
    /// <c>AccountEntries</c> history on every tick, each wallet's ledger balance is computed as
    /// <c>LastSnapshot.LedgerBalanceKobo + Sum(new entries posted since LastSnapshot)</c>, using
    /// the wallet's most recent <see cref="LedgerSnapshot"/> (found via <c>LedgerSnapshot</c>'s own
    /// <c>IX_LedgerSnapshot_WalletId_CreatedAt</c> index) as the baseline and
    /// <see cref="LedgerSnapshot.LastAccountEntryId"/> as the "already accounted for" watermark -
    /// <c>AccountEntries.Id</c> is a sequential UUID (<c>Uuid.NewSequential()</c>), so, exactly
    /// like the wallet keyset cursor above, comparing IDs is a valid monotonic ordering. A wallet
    /// with no prior snapshot sums its whole history once (first sweep only); every sweep after
    /// that only touches entries newer than the watermark, so per-tick cost stays roughly constant
    /// as an account's lifetime transaction count grows, instead of scaling with it.
    /// <b>Known limitation:</b> because <c>AccountEntry.Id</c> is generated client-side at
    /// construction time rather than by a DB sequence assigned at commit, two concurrent postings
    /// could - in a narrow window - commit out of ID order (the numerically-lower-ID transaction
    /// commits after the higher-ID one). If a sweep's watermark lands between those two commits,
    /// the lower-ID entry would be permanently skipped by every future incremental sum. This is
    /// not corrected automatically today; a time-based grace-period watermark (only advancing the
    /// cursor past entries older than e.g. a few seconds) would close this gap and is a reasonable
    /// follow-up if it's ever observed in practice.
    /// </para>
    /// <para>
    /// <b>Consistency guarantee behind immediate auto-freeze:</b> despite being incremental, each
    /// batch is still read via a single SQL statement - <c>Wallets</c> LEFT JOIN LATERAL'd to each
    /// wallet's latest <c>LedgerSnapshot</c> baseline and a correlated <c>SUM</c> over only the
    /// AccountEntries newer than that baseline. A single Postgres statement always sees one
    /// consistent MVCC snapshot, so <c>WalletBalanceKobo</c> and the freshly-computed
    /// <c>LedgerBalanceKobo</c> are read at the exact same instant - no read-skew false positives
    /// from an in-flight transfer. And because every write to <c>Wallets.AvailableBalanceKobo</c>
    /// and its paired <c>AccountEntries</c> happens inside one DB transaction
    /// (<c>TransferService</c>/<c>DepositConsumer</c>), Postgres guarantees both become visible
    /// together or not at all. A discrepancy this worker finds therefore reflects an actual bug,
    /// not a race - which is what justifies freezing without a grace period (subject to the one
    /// known limitation immediately above).
    /// </para>
    /// <para>
    /// <b>Freeze behavior:</b> a wallet found unbalanced while still <see cref="WalletStatus.Active"/>
    /// is transitioned to <see cref="WalletStatus.Frozen"/> via the same atomic guarded-UPDATE
    /// pattern used for hot balance mutations (README §4), and an <see cref="AuditLog"/> row
    /// (action <c>"Freeze"</c>, actor <c>"system:reconciliation"</c>) is written alongside it -
    /// all in the same transaction as the <see cref="LedgerSnapshot"/> insert, so a mid-batch DB
    /// failure rolls back the whole tick cleanly. This is gated by
    /// <see cref="ReconciliationWorkerOptions.AutoFreezeOnDiscrepancy"/> as an operational
    /// kill-switch. A wallet that is already not <see cref="WalletStatus.Active"/> (or with the
    /// kill-switch off) is only logged, not frozen again, to avoid repeated freeze/audit spam on
    /// every subsequent tick for the same still-broken wallet. There is currently no automated
    /// unfreeze path - <see cref="Wallet.Reactivate"/> exists but nothing calls it; a wallet
    /// auto-frozen here requires manual/admin intervention.
    /// </para>
    /// <para>
    /// <b>Snapshot volume:</b> every wallet checked in a sweep gets a <see cref="LedgerSnapshot"/>
    /// row, balanced or not, giving a full historical timeline for auditors/graphing drift rather
    /// than only an alert-only log. This means the table grows continuously, bounded by
    /// BatchSize x sweeps/day x wallet count; retention/pruning would be a natural follow-up, not
    /// implemented here.
    /// </para>
    /// </remarks>
    public sealed class ReconciliationWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReconciliationWorker> _logger;
        private readonly ReconciliationWorkerOptions _options;
        private readonly NovaWalletMetrics _metrics;

        // In-memory keyset cursor - see class remarks. Guid.Empty is the numeric minimum UUID,
        // so "Id > cursor" starting from Guid.Empty matches every wallet on the first sweep.
        private Guid _cursor = Guid.Empty;

        public ReconciliationWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<ReconciliationWorker> logger,
            IOptions<ReconciliationWorkerOptions> options,
            NovaWalletMetrics metrics)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options.Value;
            _metrics = metrics;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var pollingInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds));

            _logger.LogInformation(
                "ReconciliationWorker starting. PollingInterval: {PollingInterval}, BatchSize: {BatchSize}, AutoFreezeOnDiscrepancy: {AutoFreezeOnDiscrepancy}",
                pollingInterval,
                _options.BatchSize,
                _options.AutoFreezeOnDiscrepancy);

            using var timer = new PeriodicTimer(pollingInterval);

            do
            {
                try
                {
                    await ProcessSweepTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A batch-level failure (e.g. DB unreachable) must never take the whole
                    // hosted service - and with it, the API process - down. Log and retry on
                    // the next tick against the same cursor position (it is only advanced after
                    // a successful commit below).
                    _logger.LogError(ex, "ReconciliationWorker sweep tick failed unexpectedly. Will retry next poll.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>
        /// Per-wallet row read by the single grouped batch query below. Status is kept as the
        /// raw stored <c>int</c> (see <c>WalletConfiguration</c>'s <c>HasConversion&lt;int&gt;()</c>)
        /// rather than the <see cref="WalletStatus"/> enum, since this row is materialized from a
        /// raw SQL projection rather than an entity-mapped query - callers cast explicitly.
        /// </summary>
        private sealed record WalletReconciliationRow(
            Guid WalletId, Guid AccountId, int Status, long WalletBalanceKobo, long LedgerBalanceKobo,
            Guid? LastAccountEntryId);

        /// <summary>
        /// Thin, metrics-instrumented wrapper around <see cref="ProcessSweepTickCoreAsync"/> -
        /// times the whole tick and records <c>novawallet.reconciliation.sweep.duration</c>,
        /// without touching the sweep/freeze logic itself.
        /// </summary>
        private async Task ProcessSweepTickAsync(CancellationToken cancellationToken)
        {
            var startTimestamp = Stopwatch.GetTimestamp();

            await ProcessSweepTickCoreAsync(cancellationToken);

            _metrics.RecordReconciliationSweepDuration(Stopwatch.GetElapsedTime(startTimestamp));
        }

        private async Task ProcessSweepTickCoreAsync(CancellationToken cancellationToken)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();

            var batchSize = Math.Max(1, _options.BatchSize);
            var cursor = _cursor;

            // Single set-based raw SQL query for the whole batch (EF Core 8's Database.SqlQuery<T>
            // support for non-scalar types - the same mechanism already used elsewhere in this
            // codebase via SqlQuery<long> for the atomic guarded balance UPDATEs). Incremental
            // reconciliation (see class remarks): a LATERAL join finds each wallet's latest
            // LedgerSnapshot as a baseline (LedgerBalanceKobo + the LastAccountEntryId watermark),
            // and a second LATERAL join sums only the AccountEntries posted *after* that watermark
            // - never the account's entire history once a wallet has been snapshotted once. A
            // wallet with no prior snapshot (prior.LastAccountEntryId IS NULL) falls back to a
            // full-history sum, which only ever happens once per wallet, on its first sweep. The
            // whole read - current wallet balance, prior baseline, and the new delta - is still one
            // Postgres statement, so it's still one consistent MVCC snapshot (see class remarks on
            // why that justifies immediate auto-freeze). No entity tracking applies here since
            // nothing in this query is mutated via EF - the freeze below is a separate raw guarded
            // UPDATE, same pattern as the hot balance paths.
            var batch = await db.Database.SqlQuery<WalletReconciliationRow>($@"
                SELECT
                    w.""Id"" AS ""WalletId"",
                    w.""AccountId"" AS ""AccountId"",
                    w.""Status"" AS ""Status"",
                    w.""AvailableBalanceKobo"" AS ""WalletBalanceKobo"",
                    COALESCE(prior.""LedgerBalanceKobo"", 0) + COALESCE(delta.""DeltaKobo"", 0) AS ""LedgerBalanceKobo"",
                    COALESCE(delta.""MaxEntryId"", prior.""LastAccountEntryId"") AS ""LastAccountEntryId""
                FROM ""Wallets"" w
                LEFT JOIN LATERAL (
                    SELECT ls.""LedgerBalanceKobo"", ls.""LastAccountEntryId""
                    FROM ""LedgerSnapshot"" ls
                    WHERE ls.""WalletId"" = w.""Id""
                    ORDER BY ls.""CreatedAt"" DESC, ls.""Id"" DESC
                    LIMIT 1
                ) prior ON TRUE
                LEFT JOIN LATERAL (
                    SELECT
                        SUM(CASE WHEN ae.""EntryType"" = 'Credit' THEN ae.""AmountKobo"" ELSE -ae.""AmountKobo"" END) AS ""DeltaKobo"",
                        MAX(ae.""Id"") AS ""MaxEntryId""
                    FROM ""AccountEntries"" ae
                    WHERE ae.""AccountId"" = w.""AccountId""
                      AND (prior.""LastAccountEntryId"" IS NULL OR ae.""Id"" > prior.""LastAccountEntryId"")
                ) delta ON TRUE
                WHERE w.""Id"" > {cursor}
                ORDER BY w.""Id""
                LIMIT {batchSize}")
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                // Empty wallet population, or the cursor ran off the end of the table exactly on
                // the previous tick's boundary - reset so the next tick starts a fresh sweep.
                _cursor = Guid.Empty;
                return;
            }

            _metrics.RecordWalletsChecked(batch.Count);

            var runId = Uuid.NewSequential();
            var strategy = db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    foreach (var row in batch)
                    {
                        var snapshot = LedgerSnapshot.Create(
                            runId, row.WalletId, row.AccountId, row.WalletBalanceKobo, row.LedgerBalanceKobo,
                            row.LastAccountEntryId);

                        db.LedgerSnapshots.Add(snapshot);

                        if (snapshot.IsBalanced)
                        {
                            continue;
                        }

                        _metrics.RecordReconciliationDiscrepancy();

                        if (row.Status == (int)WalletStatus.Active && _options.AutoFreezeOnDiscrepancy)
                        {
                            var frozen = await TryFreezeWalletAtomicAsync(db, row.WalletId, cancellationToken);

                            if (frozen)
                            {
                                _metrics.RecordWalletFrozen();

                                db.AuditLogs.Add(AuditLog.Create(
                                    walletId: row.WalletId,
                                    actorSubject: "system:reconciliation",
                                    action: "Freeze",
                                    balanceBeforeKobo: row.WalletBalanceKobo,
                                    balanceAfterKobo: row.WalletBalanceKobo,
                                    correlationId: snapshot.Id));

                                _logger.LogCritical(
                                    "Wallet {WalletId} auto-frozen due to a ledger discrepancy. RunId: {RunId}, DiscrepancyKobo: {DiscrepancyKobo}, WalletBalanceKobo: {WalletBalanceKobo}, LedgerBalanceKobo: {LedgerBalanceKobo}",
                                    row.WalletId,
                                    runId,
                                    snapshot.DiscrepancyKobo,
                                    row.WalletBalanceKobo,
                                    row.LedgerBalanceKobo);
                            }
                            else
                            {
                                // Lost a race - the wallet's status changed between the read
                                // above and this guarded UPDATE (e.g. another process already
                                // froze/closed it, or another ReconciliationWorker instance beat
                                // us to it). Not our freeze to claim; the discrepancy is still
                                // logged below like any other non-Active mismatch.
                                _logger.LogError(
                                    "Ledger discrepancy detected but Wallet {WalletId} was no longer Active by the time of the freeze attempt. RunId: {RunId}, DiscrepancyKobo: {DiscrepancyKobo}",
                                    row.WalletId,
                                    runId,
                                    snapshot.DiscrepancyKobo);
                            }
                        }
                        else
                        {
                            _logger.LogError(
                                "Ledger discrepancy detected for Wallet {WalletId}. RunId: {RunId}, DiscrepancyKobo: {DiscrepancyKobo}, WalletBalanceKobo: {WalletBalanceKobo}, LedgerBalanceKobo: {LedgerBalanceKobo}, Status: {Status}",
                                row.WalletId,
                                runId,
                                snapshot.DiscrepancyKobo,
                                row.WalletBalanceKobo,
                                row.LedgerBalanceKobo,
                                (WalletStatus)row.Status);
                        }
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            });

            // Advance the cursor only after a successful commit, so a failed tick is retried
            // against the same batch next time rather than silently skipping it. Wrap back to
            // the start once a short batch signals the end of the table was reached.
            _cursor = batch.Count < batchSize ? Guid.Empty : batch[^1].WalletId;
        }

        /// <summary>
        /// Freezes a wallet via a single atomic, guarded SQL UPDATE - no row lock is taken by the
        /// application. The WHERE clause enforces the wallet is still Active as part of the same
        /// atomic operation as the status mutation, so this is a no-op (affects zero rows) if the
        /// wallet's status has already moved on for any reason. Mirrors the guarded UPDATE pattern
        /// used for hot balance mutations in TransferService/DepositConsumer (README §4).
        /// </summary>
        private static async Task<bool> TryFreezeWalletAtomicAsync(
            NovaWalletDbContext db, Guid walletId, CancellationToken cancellationToken)
        {
            var affectedRows = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ""Wallets""
                SET ""Status"" = {(int)WalletStatus.Frozen}
                WHERE ""Id"" = {walletId} AND ""Status"" = {(int)WalletStatus.Active}",
                cancellationToken);

            return affectedRows > 0;
        }
    }
}
