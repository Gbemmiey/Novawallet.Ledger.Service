using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.UnitTests.Domain;

public class WalletTransferTests
{
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Destination = Guid.NewGuid();

    private static WalletTransfer Completed(long amount = 50_000) => WalletTransfer.Create(
        journalEntryId: Guid.NewGuid(),
        sourceWalletId: Source,
        destinationWalletId: Destination,
        amountKobo: amount,
        narration: "rent",
        idempotencyKey: "key-1",
        requestPayloadHash: "HASH",
        transactionDate: DateTime.UtcNow);

    private static WalletTransfer Failed(string code = "51", string reason = "Insufficient balance.") => WalletTransfer.CreateFailed(
        sourceWalletId: Source,
        destinationWalletId: Destination,
        amountKobo: 50_000,
        narration: "rent",
        idempotencyKey: "key-2",
        requestPayloadHash: "HASH",
        failureCode: code,
        failureReason: reason);

    [Fact]
    public void Create_ProducesCompletedTransferWithJournalEntryAndNoFailure()
    {
        var journalEntryId = Guid.NewGuid();
        var when = DateTime.UtcNow.AddMinutes(-1);

        var transfer = WalletTransfer.Create(journalEntryId, Source, Destination, 100, "n", "k", "H", when);

        Assert.Equal(TransferStatus.Completed, transfer.Status);
        Assert.Equal(journalEntryId, transfer.JournalEntryId);
        Assert.Null(transfer.FailureCode);
        Assert.Null(transfer.FailureReason);
        Assert.Equal(when.Ticks - (when.Ticks % 10), transfer.TransactionDate.Ticks);
        Assert.False(string.IsNullOrWhiteSpace(transfer.PaymentReference));
    }

    [Fact]
    public void Create_TruncatesTheTransactionDateToMicroseconds_SoItSurvivesAPostgresRoundTrip()
    {
        // 100 ns ticks that Postgres (microsecond precision) cannot store.
        var withSubMicrosecondTicks = new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

        var transfer = WalletTransfer.Create(
            Guid.NewGuid(), Source, Destination, 100, null, "k", "H", withSubMicrosecondTicks);

        Assert.Equal(0, transfer.TransactionDate.Ticks % 10);
        Assert.Equal(DateTimeKind.Utc, transfer.TransactionDate.Kind);
        Assert.Equal(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560), transfer.TransactionDate);
    }

    [Fact]
    public void CreateFailed_TransactionDate_IsAlsoMicrosecondPrecision()
    {
        Assert.Equal(0, Failed().TransactionDate.Ticks % 10);
    }

    [Fact]
    public void Create_GeneratesPaymentReferenceIndependentOfJournalEntry()
    {
        var journalEntryId = Guid.NewGuid();

        var transfer = WalletTransfer.Create(journalEntryId, Source, Destination, 100, null, "k", "H", DateTime.UtcNow);

        Assert.NotEqual(journalEntryId.ToString(), transfer.PaymentReference);
        Assert.NotEqual(transfer.Id.ToString(), transfer.PaymentReference);
    }

    [Fact]
    public void Create_EmptyJournalEntryId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            WalletTransfer.Create(Guid.Empty, Source, Destination, 100, null, "k", "H", DateTime.UtcNow));
    }

    [Fact]
    public void Create_SameSourceAndDestination_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            WalletTransfer.Create(Guid.NewGuid(), Source, Source, 100, null, "k", "H", DateTime.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Create_NotPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Completed(amount));
    }

    [Theory]
    [InlineData("", "H")]
    [InlineData("  ", "H")]
    [InlineData("k", "")]
    [InlineData("k", " ")]
    public void Create_MissingKeyOrHash_Throws(string key, string hash)
    {
        Assert.Throws<ArgumentException>(() =>
            WalletTransfer.Create(Guid.NewGuid(), Source, Destination, 100, null, key, hash, DateTime.UtcNow));
    }

    [Fact]
    public void CreateFailed_ProducesFailedTransferWithNoJournalEntry()
    {
        var transfer = Failed();

        Assert.Equal(TransferStatus.Failed, transfer.Status);
        Assert.Null(transfer.JournalEntryId);
        Assert.Equal("51", transfer.FailureCode);
        Assert.Equal("Insufficient balance.", transfer.FailureReason);
        Assert.Equal("key-2", transfer.IdempotencyKey);
        Assert.Equal("HASH", transfer.RequestPayloadHash);
    }

    [Fact]
    public void CreateFailed_TruncatesReasonTo500Characters()
    {
        var transfer = Failed(reason: new string('r', 900));

        Assert.Equal(500, transfer.FailureReason!.Length);
    }

    [Fact]
    public void CreateFailed_BlankReason_IsStoredAsNull()
    {
        Assert.Null(Failed(reason: "   ").FailureReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CreateFailed_MissingCode_Throws(string code)
    {
        Assert.Throws<ArgumentException>(() => Failed(code: code));
    }

    [Fact]
    public void CreateFailed_SameSourceAndDestination_Throws()
    {
        Assert.Throws<ArgumentException>(() => WalletTransfer.CreateFailed(
            Source, Source, 100, null, "k", "H", "51", "x"));
    }

    [Fact]
    public void MarkReversed_FromCompleted_Succeeds()
    {
        var transfer = Completed();

        transfer.MarkReversed();

        Assert.Equal(TransferStatus.Reversed, transfer.Status);
    }

    [Fact]
    public void MarkReversed_FromFailed_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Failed().MarkReversed());
    }

    [Fact]
    public void MarkReversed_Twice_Throws()
    {
        var transfer = Completed();
        transfer.MarkReversed();

        Assert.Throws<InvalidOperationException>(() => transfer.MarkReversed());
    }
}
