using NovaWallet.Api.Core.Enums;
using NovaWallet.Api.Core.Models;

namespace NovaWallet.Api.UnitTests.Domain;

public class WalletTests
{
    private static Wallet NewWallet() => Wallet.Create(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Create_StartsActiveWithZeroBalanceAndNgn()
    {
        var wallet = NewWallet();

        Assert.NotEqual(Guid.Empty, wallet.Id);
        Assert.Equal(WalletStatus.Active, wallet.Status);
        Assert.Equal(0, wallet.AvailableBalanceKobo);
        Assert.Equal("NGN", wallet.Currency);
    }

    [Fact]
    public void Create_EmptyUserId_Throws()
    {
        Assert.Throws<ArgumentException>(() => Wallet.Create(Guid.Empty, Guid.NewGuid()));
    }

    [Fact]
    public void Create_EmptyAccountId_Throws()
    {
        Assert.Throws<ArgumentException>(() => Wallet.Create(Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public void Credit_IncreasesBalance()
    {
        var wallet = NewWallet();

        wallet.Credit(1_000);
        wallet.Credit(250);

        Assert.Equal(1_250, wallet.AvailableBalanceKobo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Credit_NotPositive_Throws(long amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewWallet().Credit(amount));
    }

    [Fact]
    public void Debit_DecreasesBalance()
    {
        var wallet = NewWallet();
        wallet.Credit(1_000);

        wallet.Debit(400);

        Assert.Equal(600, wallet.AvailableBalanceKobo);
    }

    [Fact]
    public void Debit_ExactBalance_LeavesZero()
    {
        var wallet = NewWallet();
        wallet.Credit(500);

        wallet.Debit(500);

        Assert.Equal(0, wallet.AvailableBalanceKobo);
    }

    [Fact]
    public void Debit_MoreThanBalance_ThrowsAndLeavesBalanceUnchanged()
    {
        var wallet = NewWallet();
        wallet.Credit(500);

        Assert.Throws<InvalidOperationException>(() => wallet.Debit(501));
        Assert.Equal(500, wallet.AvailableBalanceKobo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Debit_NotPositive_Throws(long amount)
    {
        var wallet = NewWallet();
        wallet.Credit(500);

        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.Debit(amount));
    }

    [Fact]
    public void FrozenWallet_RejectsCreditAndDebit()
    {
        var wallet = NewWallet();
        wallet.Credit(500);
        wallet.Freeze();

        Assert.Equal(WalletStatus.Frozen, wallet.Status);
        Assert.Throws<InvalidOperationException>(() => wallet.Credit(1));
        Assert.Throws<InvalidOperationException>(() => wallet.Debit(1));
        Assert.Equal(500, wallet.AvailableBalanceKobo);
    }

    [Fact]
    public void Reactivate_RestoresFrozenWallet()
    {
        var wallet = NewWallet();
        wallet.Freeze();

        wallet.Reactivate();

        Assert.Equal(WalletStatus.Active, wallet.Status);
        wallet.Credit(10);
        Assert.Equal(10, wallet.AvailableBalanceKobo);
    }

    [Fact]
    public void ClosedWallet_IsTerminal()
    {
        var wallet = NewWallet();
        wallet.Close();

        Assert.Equal(WalletStatus.Closed, wallet.Status);
        Assert.Throws<InvalidOperationException>(() => wallet.Freeze());
        Assert.Throws<InvalidOperationException>(() => wallet.Reactivate());
        Assert.Throws<InvalidOperationException>(() => wallet.Credit(1));
    }
}
