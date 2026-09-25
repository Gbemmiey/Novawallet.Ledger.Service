using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaWallet.Application.Observability;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Enums;
using NovaWallet.Infrastructure.Data;
using NovaWallet.Infrastructure.Options;
using System.Diagnostics;
using UUIDNext;

namespace NovaWallet.Infrastructure.Workers
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
    /// <c>LastSnapshot.WatermarkBalanceKobo + Sum(entries posted after the last watermark)</c>, using
    /// the wallet's most recent <see cref="LedgerSnapshot"/> (found via <c>LedgerSnapshot</c>'s own
    /// <c>IX_LedgerSnapshot_WalletId_CreatedAt</c> index) as the baseline and
    /// <see cref="LedgerSnapshot.LastAccountEntryId"/> as the "already accounted for" watermark -
    /// <c>AccountEntries.Id</c> is a sequential UUID (<c>Uuid.NewSequential()</c>), so, exactly
    /// like the wallet keyset cursor above, comparing IDs is a valid monotonic ordering. A wallet
    /// with no prior snapshot sums its whole history once (first sweep only); every sweep after
    /// that only touches entries newer than the watermark, so per-tick cost stays roughly constant
    /// as an account's lifetime transaction count grows, instead of scaling with it.
    /// </para>
    /// <para>
    /// <b>Grace-period watermark:</b> <c>AccountEntry.Id</c> is assigned before commit (any ID
    /// scheme is - client UUID, DB default, or sequence), so two concurrent postings can commit out
    /// of ID order. If the watermark advanced straight to the highest committed ID, a slower,
    /// lower-ID transaction committing afterwards would be skipped forever. To prevent that, the
    /// balance compared against the wallet (<c>LedgerBalanceKobo</c>) always includes every
    /// committed entry past the previous watermark, but the <i>stored</i> watermark
    /// (<c>LastAccountEntryId</c> plus its matching <c>WatermarkBalanceKobo</c>) only advances to
    /// the highest entry older than <see cref="ReconciliationWorkerOptions.WatermarkGracePeriodSeconds"/>.
    /// Younger entries are simply re-summed on the next sweep(s) until they age past the grace
    /// period, by which point any in-flight lower-ID transaction has long since committed. The
    /// residual assumption is that no posting transaction outlives the grace period (and that
    /// clocks across API instances don't skew by more than it).
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
    /// not a race - which is what justifies freezing without a grace period on the comparison
    /// itself (the grace period above only governs how far the stored watermark advances).
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
    /// <b>Snapshot volume:</b> every wallet is checked every sweep, but a <see cref="LedgerSnapshot"/>
    /// row is only written when it carries information: the wallet has no prior snapshot, its
    /// watermark advanced, it is unbalanced (so discrepancy history and the freeze audit link are
    /// always recorded), or <see cref="ReconciliationWorkerOptions.SnapshotHeartbeatMinutes"/> has
    /// elapsed since its last row (a proof-of-life for idle wallets). Skipping a write never skips
    /// the comparison, and the next sweep's baseline is the last row actually written, so the
    /// incremental maths is unaffected. Growth is therefore per state change, not per tick;
    /// retention/pruning is still a natural follow-up, not implemented here.
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
            Guid? LastAccountEntryId, long WatermarkBalanceKobo,
            bool HasPriorSnapshot, Guid? PriorLastAccountEntryId, DateTime? PriorCreatedAt);

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
            var now = DateTime.UtcNow;
            var graceCutoff = now - TimeSpan.FromSeconds(Math.Max(0, _options.WatermarkGracePeriodSeconds));
            DateTime? heartbeatCutoff = _options.SnapshotHeartbeatMinutes > 0
                ? now - TimeSpan.FromMinutes(_options.SnapshotHeartbeatMinutes)
                : null;

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
                    (COALESCE(prior.""WatermarkBalanceKobo"", 0) + COALESCE(delta.""DeltaKobo"", 0))::bigint AS ""LedgerBalanceKobo"",
                    COALESCE(safe.""SafeMaxId"", prior.""LastAccountEntryId"") AS ""LastAccountEntryId"",
                    (COALESCE(prior.""WatermarkBalanceKobo"", 0) + COALESCE(delta.""SafeDeltaKobo"", 0))::bigint AS ""WatermarkBalanceKobo"",
                    (prior.""PriorId"" IS NOT NULL) AS ""HasPriorSnapshot"",
                    prior.""LastAccountEntryId"" AS ""PriorLastAccountEntryId"",
                    prior.""PriorCreatedAt"" AS ""PriorCreatedAt""
                FROM ""Wallets"" w
                LEFT JOIN LATERAL (
                    SELECT ls.""Id"" AS ""PriorId"", ls.""CreatedAt"" AS ""PriorCreatedAt"",
                           ls.""WatermarkBalanceKobo"", ls.""LastAccountEntryId""
                    FROM ""LedgerSnapshot"" ls
                    WHERE ls.""WalletId"" = w.""Id""
                    ORDER BY ls.""CreatedAt"" DESC, ls.""Id"" DESC
                    LIMIT 1
                ) prior ON TRUE
                LEFT JOIN LATERAL (
                    -- Safe watermark: highest entry ID (after the prior watermark) that is already
                    -- older than the grace period. Postgres has no MAX(uuid), hence ORDER BY/LIMIT.
                    SELECT ae.""Id"" AS ""SafeMaxId""
                    FROM ""AccountEntries"" ae
                    WHERE ae.""AccountId"" = w.""AccountId""
                      AND (prior.""LastAccountEntryId"" IS NULL OR ae.""Id"" > prior.""LastAccountEntryId"")
                      AND ae.""CreatedAt"" < {graceCutoff}
                    ORDER BY ae.""Id"" DESC
                    LIMIT 1
                ) safe ON TRUE
                LEFT JOIN LATERAL (
                    -- DeltaKobo: every entry past the prior watermark, including those still inside
                    -- the grace period (this is what gets compared to the wallet balance).
                    -- SafeDeltaKobo: only the ID-prefix up to SafeMaxId (what the new watermark covers).
                    SELECT
                        SUM(CASE WHEN ae.""EntryType"" = 'Credit' THEN ae.""AmountKobo"" ELSE -ae.""AmountKobo"" END) AS ""DeltaKobo"",
                        SUM(CASE WHEN ae.""EntryType"" = 'Credit' THEN ae.""AmountKobo"" ELSE -ae.""AmountKobo"" END)
                            FILTER (WHERE safe.""SafeMaxId"" IS NOT NULL AND ae.""Id"" <= safe.""SafeMaxId"") AS ""SafeDeltaKobo""
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
                            row.LastAccountEntryId, row.WatermarkBalanceKobo);

                        // Every wallet is still checked, but a row is only persisted when it
                        // carries information (see "Snapshot volume" in the class remarks).
                        // The next sweep's baseline is simply the last row that was written.
                        if (!snapshot.IsBalanced || ShouldPersistSnapshot(row, heartbeatCutoff))
                        {
                            db.LedgerSnapshots.Add(snapshot);
                        }

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
        /// Decides whether a <i>balanced</i> wallet's snapshot is worth persisting: yes if it has
        /// never been snapshotted, if its watermark moved, or if the heartbeat interval has elapsed
        /// since its last row. Unbalanced wallets are always persisted by the caller.
        /// </summary>
        private static bool ShouldPersistSnapshot(WalletReconciliationRow row, DateTime? heartbeatCutoff)
        {
            if (!row.HasPriorSnapshot || row.LastAccountEntryId != row.PriorLastAccountEntryId)
            {
                return true;
            }

            return heartbeatCutoff.HasValue
                && row.PriorCreatedAt.HasValue
                && row.PriorCreatedAt.Value <= heartbeatCutoff.Value;
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