using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.UnitTests.Domain;

public class JournalEntryTests
{
    [Fact]
    public void Create_MissingIdempotencyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => JournalEntry.Create("", "HASH"));
    }

    [Fact]
    public void Create_MissingHash_Throws()
    {
        Assert.Throws<ArgumentException>(() => JournalEntry.Create("key", " "));
    }

    [Fact]
    public void NewEntry_WithNoLines_IsTriviallyBalanced()
    {
        Assert.True(JournalEntry.Create("key", "HASH").IsBalanced);
    }

    [Fact]
    public void MatchingDebitAndCredit_IsBalanced()
    {
        var entry = JournalEntry.Create("key", "HASH");
        entry.AddDebitLine(Guid.NewGuid(), 5_000, "Transfer to 0123456789");
        entry.AddCreditLine(Guid.NewGuid(), 5_000, "Transfer from 9876543210");

        Assert.True(entry.IsBalanced);
        Assert.Equal(2, entry.Lines.Count);
    }

    [Fact]
    public void MismatchedLines_AreNotBalanced()
    {
        var entry = JournalEntry.Create("key", "HASH");
        entry.AddDebitLine(Guid.NewGuid(), 5_000, "debit");
        entry.AddCreditLine(Guid.NewGuid(), 4_999, "credit");

        Assert.False(entry.IsBalanced);
    }

    [Fact]
    public void SplitCreditLines_SummingToDebit_AreBalanced()
    {
        var entry = JournalEntry.Create("key", "HASH");
        entry.AddDebitLine(Guid.NewGuid(), 1_000, "debit");
        entry.AddCreditLine(Guid.NewGuid(), 600, "credit a");
        entry.AddCreditLine(Guid.NewGuid(), 400, "credit b");

        Assert.True(entry.IsBalanced);
    }

    [Fact]
    public void Lines_CarryEntryTypeAndJournalEntryId()
    {
        var entry = JournalEntry.Create("key", "HASH");
        var debit = entry.AddDebitLine(Guid.NewGuid(), 10, "d");
        var credit = entry.AddCreditLine(Guid.NewGuid(), 10, "c");

        Assert.Equal(EntryType.Debit, debit.EntryType);
        Assert.Equal(EntryType.Credit, credit.EntryType);
        Assert.Equal(entry.Id, debit.JournalEntryId);
        Assert.Equal(entry.Id, credit.JournalEntryId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Lines_NotPositive_Throw(long amount)
    {
        var entry = JournalEntry.Create("key", "HASH");

        Assert.Throws<ArgumentOutOfRangeException>(() => entry.AddDebitLine(Guid.NewGuid(), amount, "d"));
        Assert.Throws<ArgumentOutOfRangeException>(() => entry.AddCreditLine(Guid.NewGuid(), amount, "c"));
    }

    [Fact]
    public void Line_BlankParticulars_Throws()
    {
        var entry = JournalEntry.Create("key", "HASH");

        Assert.Throws<ArgumentException>(() => entry.AddDebitLine(Guid.NewGuid(), 10, "  "));
    }

    [Fact]
    public void Line_Particulars_AreTrimmedAndTruncatedTo100()
    {
        var entry = JournalEntry.Create("key", "HASH");

        var line = entry.AddDebitLine(Guid.NewGuid(), 10, "  " + new string('p', 250) + "  ");

        Assert.Equal(100, line.TransParticulars.Length);
        Assert.DoesNotContain(' ', line.TransParticulars);
    }
}

public class DepositOutboxTests
{
    [Fact]
    public void Create_StartsPendingWithNoRetries_AndCarriesTraceParent()
    {
        var outbox = DepositOutbox.Create(Guid.NewGuid(), "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        Assert.Equal(OutboxStatus.Pending, outbox.Status);
        Assert.Equal(0, outbox.NumberOfRetries);
        Assert.Null(outbox.DateProcessed);
        Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", outbox.TraceParent);
    }

    [Fact]
    public void Create_WithoutTraceParent_LeavesItNull()
    {
        Assert.Null(DepositOutbox.Create(Guid.NewGuid()).TraceParent);
    }

    [Fact]
    public void Create_EmptyExternalCreditRequestId_Throws()
    {
        Assert.Throws<ArgumentException>(() => DepositOutbox.Create(Guid.Empty));
    }

    [Fact]
    public void MarkProcessed_SetsStatusAndDate()
    {
        var outbox = DepositOutbox.Create(Guid.NewGuid());

        outbox.MarkProcessed();

        Assert.Equal(OutboxStatus.Processed, outbox.Status);
        Assert.NotNull(outbox.DateProcessed);
    }

    [Fact]
    public void MarkProcessed_WhenNotPending_Throws()
    {
        var outbox = DepositOutbox.Create(Guid.NewGuid());
        outbox.MarkProcessed();

        Assert.Throws<InvalidOperationException>(() => outbox.MarkProcessed());
    }

    [Fact]
    public void RecordFailedAttempt_StaysPendingUntilMaxRetries()
    {
        var outbox = DepositOutbox.Create(Guid.NewGuid());

        for (var attempt = 1; attempt < DepositOutbox.MaxRetries; attempt++)
        {
            Assert.False(outbox.RecordFailedAttempt());
            Assert.Equal(OutboxStatus.Pending, outbox.Status);
            Assert.Equal(attempt, outbox.NumberOfRetries);
        }
    }

    [Fact]
    public void RecordFailedAttempt_AtMaxRetries_FailsPermanently()
    {
        var outbox = DepositOutbox.Create(Guid.NewGuid());

        var exhausted = false;
        for (var attempt = 0; attempt < DepositOutbox.MaxRetries; attempt++)
        {
            exhausted = outbox.RecordFailedAttempt();
        }

        Assert.True(exhausted);
        Assert.Equal(OutboxStatus.Failed, outbox.Status);
        Assert.NotNull(outbox.DateProcessed);
    }
}

public class ExternalCreditRequestTests
{
    private static ExternalCreditRequest NewRequest() => ExternalCreditRequest.Create(
        "SESSION-1", "REF-1", 10_000, "0123456789", "9876543210", "058");

    [Fact]
    public void Create_StartsPending()
    {
        var request = NewRequest();

        Assert.Equal(DepositStatus.Pending, request.Status);
        Assert.Null(request.CompletedDate);
    }

    [Theory]
    [InlineData("", 10, "0123456789")]
    [InlineData("S", 0, "0123456789")]
    [InlineData("S", -1, "0123456789")]
    [InlineData("S", 10, "")]
    public void Create_InvalidArguments_Throw(string sessionId, long amount, string beneficiary)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ExternalCreditRequest.Create(sessionId, "REF", amount, beneficiary, "1", "058"));
    }

    [Fact]
    public void MarkCompleted_SetsStatusAndCompletedDate()
    {
        var request = NewRequest();

        request.MarkCompleted();

        Assert.Equal(DepositStatus.Completed, request.Status);
        Assert.NotNull(request.CompletedDate);
    }

    [Fact]
    public void MarkFailed_LeavesNoCompletedDate()
    {
        var request = NewRequest();

        request.MarkFailed();

        Assert.Equal(DepositStatus.Failed, request.Status);
        Assert.Null(request.CompletedDate);
    }

    [Fact]
    public void FinalStates_CannotBeChanged()
    {
        var completed = NewRequest();
        completed.MarkCompleted();
        Assert.Throws<InvalidOperationException>(() => completed.MarkFailed());
        Assert.Throws<InvalidOperationException>(() => completed.MarkCompleted());

        var failed = NewRequest();
        failed.MarkFailed();
        Assert.Throws<InvalidOperationException>(() => failed.MarkCompleted());
    }
}

public class AccountTests
{
    [Fact]
    public void Create_SetsFields()
    {
        var account = Account.Create("0123456789", AccountType.Liability);

        Assert.Equal("0123456789", account.AccountNumber);
        Assert.Equal(AccountType.Liability, account.AccountType);
        Assert.Equal("NGN", account.Currency);
    }

    [Fact]
    public void Create_BlankNumber_Throws()
    {
        Assert.Throws<ArgumentException>(() => Account.Create(" ", AccountType.Asset));
    }
}
