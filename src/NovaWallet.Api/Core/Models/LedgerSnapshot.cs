using UUIDNext;

namespace NovaWallet.Api.Core.Models
{
    /// <summary>
    /// Insert-only reconciliation record, written once per wallet per sweep tick by
    /// <see cref="NovaWallet.Api.Workers.ReconciliationWorker"/>. Asserts
    /// Wallet.AvailableBalanceKobo == Sum(AccountEntries.Credit) - Sum(AccountEntries.Debit)
    /// for the wallet's own Account (README §3). Every wallet checked in a sweep gets a row -
    /// balanced or not - so this table doubles as a full historical timeline of ledger health,
    /// not just an alert log. Each row also carries <see cref="LastAccountEntryId"/>, the
    /// watermark the next sweep uses to reconcile only the AccountEntries posted after this
    /// snapshot rather than the account's entire history. Fully immutable: there is no mutation
    /// method on this type, and no UPDATE/DELETE is issued against it anywhere in the codebase.
    /// </summary>
    public class LedgerSnapshot
    {
        public Guid Id { get; }

        /// <summary>Groups every wallet checked within a single sweep tick - all rows sharing a
        /// RunId were computed from the same <see cref="NovaWallet.Api.Workers.ReconciliationWorker"/>
        /// tick.</summary>
        public Guid RunId { get; }
        public Guid WalletId { get; }
        public Wallet? Wallet { get; private set; }
        public Guid AccountId { get; }
        public Account? Account { get; private set; }

        /// <summary>Wallet.AvailableBalanceKobo at the instant this snapshot was taken.</summary>
        public long WalletBalanceKobo { get; }

        /// <summary>Sum(Credit) - Sum(Debit) over all AccountEntries for AccountId, as of this
        /// snapshot. Computed incrementally as LastSnapshot.LedgerBalanceKobo + Sum(entries newer
        /// than LastAccountEntryId) rather than re-summing the full history every time (see
        /// ReconciliationWorker remarks) - the resulting value is exactly the same total either
        /// way, and is still read from the same SQL statement/MVCC snapshot as WalletBalanceKobo.</summary>
        public long LedgerBalanceKobo { get; }

        /// <summary>WalletBalanceKobo - LedgerBalanceKobo. Zero means balanced.</summary>
        public long DiscrepancyKobo { get; }
        public bool IsBalanced { get; }
        public DateTime CreatedAt { get; }

        /// <summary>The highest <see cref="AccountEntry.Id"/> folded into this snapshot's
        /// <see cref="LedgerBalanceKobo"/> - the incremental-reconciliation watermark.
        /// <see langword="null"/> means the account had zero <see cref="AccountEntry"/> rows as of
        /// this snapshot. <see cref="NovaWallet.Api.Workers.ReconciliationWorker"/> uses this to
        /// only sum entries newer than the wallet's last snapshot on the next sweep, instead of
        /// re-summing the account's entire history every tick - see its class remarks for the
        /// full rationale and the one known race-window caveat.</summary>
        public Guid? LastAccountEntryId { get; }

        /// <summary>Sum(Credit) - Sum(Debit) over only the AccountEntries up to and including
        /// <see cref="LastAccountEntryId"/> - the baseline the next sweep adds newer entries onto.
        /// Distinct from <see cref="LedgerBalanceKobo"/> (the full total compared against the
        /// wallet): the watermark deliberately lags behind the newest entries by the worker's
        /// grace period, so an entry whose transaction commits slightly out of ID order is still
        /// picked up by a later sweep rather than permanently skipped.</summary>
        public long WatermarkBalanceKobo { get; }

        private LedgerSnapshot(Guid id, Guid runId, Guid walletId, Guid accountId,
            long walletBalanceKobo, long ledgerBalanceKobo, long discrepancyKobo, bool isBalanced,
            DateTime createdAt, Guid? lastAccountEntryId, long watermarkBalanceKobo)
        {
            WatermarkBalanceKobo = watermarkBalanceKobo;
            Id = id;
            RunId = runId;
            WalletId = walletId;
            AccountId = accountId;
            WalletBalanceKobo = walletBalanceKobo;
            LedgerBalanceKobo = ledgerBalanceKobo;
            DiscrepancyKobo = discrepancyKobo;
            IsBalanced = isBalanced;
            CreatedAt = createdAt;
            LastAccountEntryId = lastAccountEntryId;
        }

        public static LedgerSnapshot Create(Guid runId, Guid walletId, Guid accountId,
            long walletBalanceKobo, long ledgerBalanceKobo, Guid? lastAccountEntryId, long watermarkBalanceKobo)
        {
            if (runId == Guid.Empty) throw new ArgumentException("RunId is required.", nameof(runId));
            if (walletId == Guid.Empty) throw new ArgumentException("WalletId is required.", nameof(walletId));
            if (accountId == Guid.Empty) throw new ArgumentException("AccountId is required.", nameof(accountId));

            var discrepancyKobo = walletBalanceKobo - ledgerBalanceKobo;

            return new LedgerSnapshot(
                id: Uuid.NewSequential(),
                runId: runId,
                walletId: walletId,
                accountId: accountId,
                walletBalanceKobo: walletBalanceKobo,
                ledgerBalanceKobo: ledgerBalanceKobo,
                discrepancyKobo: discrepancyKobo,
                isBalanced: discrepancyKobo == 0,
                createdAt: DateTime.UtcNow,
                lastAccountEntryId: lastAccountEntryId,
                watermarkBalanceKobo: watermarkBalanceKobo);
        }
    }
}
