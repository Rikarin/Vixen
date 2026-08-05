// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Economy.Tests;

/// <summary>The horizon's arithmetic, and the one property the whole design is for.</summary>
public class KeyHorizonTests {
    static readonly DateTimeOffset Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    static readonly EconomyAccount Bank = EconomyAccount.Of("world/bank");

    static readonly EconomyAccount Player = EconomyAccount.Of(new PlayerId(1));

    static readonly DefId Gold = DefId.From("currency/gold");

    /// <summary>
    ///     ⚠ The safety property, over every window a game might plausibly state. A horizon whose
    ///     <em>worst</em> retention is shorter than the window it was built from would forget a key
    ///     that a retry still carries, and the failure of that is a duplicated item rather than an
    ///     exception.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(3_600)]
    [InlineData(86_400)]
    public void EveryBoundedHorizonOutlivesTheWindowItWasBuiltFrom(int seconds) {
        var window = TimeSpan.FromSeconds(seconds);
        var horizon = KeyHorizon.Outliving(window);

        Assert.True(horizon.Guaranteed > window, $"{horizon.Guaranteed} must exceed {window}");
    }

    [Fact]
    public void NeverIsUnbounded() {
        Assert.False(KeyHorizon.Never.IsBounded);
        Assert.Equal(TimeSpan.Zero, KeyHorizon.Never.RetryWindow);
    }

    [Fact]
    public void AWindowThatIsNotPositiveIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyHorizon.Outliving(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyHorizon.Outliving(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AWindowTooLargeToScaleIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyHorizon.Outliving(TimeSpan.MaxValue));

    [Fact]
    public void AnUnboundedLedgerForgetsNothing() {
        var ledger = Seeded(KeyHorizon.Never);

        Post(ledger, "a");

        Assert.Equal(0, ledger.Forget(Noon));
        Assert.Equal(0, ledger.Forget(Noon.AddDays(365)));
        Assert.Equal(1, ledger.Keys);
        Assert.Equal(0, ledger.Forgotten);
    }

    /// <summary>⚠ The first sweep starts the clock rather than dropping the first generation.</summary>
    [Fact]
    public void TheFirstSweepForgetsNothingHoweverLateItIs() {
        var ledger = Seeded(KeyHorizon.Outliving(TimeSpan.FromMinutes(1)));

        Post(ledger, "a");

        Assert.Equal(0, ledger.Forget(Noon.AddDays(1)));
        Assert.Equal(1, ledger.Keys);
    }

    /// <summary>
    ///     ⚠ Posted at the <em>worst</em> moment there is — a hair before a rotation, so it spends the
    ///     shortest possible time in the generation it lands in. Posting at a rotation instead tests
    ///     the best case, which is a whole <c>Interval</c> longer and passes with the guarantee stated
    ///     wrongly.
    /// </summary>
    [Fact]
    public void AKeyPostedAtTheWorstMomentStillSurvivesTheGuaranteedWindow() {
        var horizon = KeyHorizon.Outliving(TimeSpan.FromMinutes(1));
        var ledger = Seeded(horizon);
        var posted = Noon + horizon.Interval - TimeSpan.FromMilliseconds(1);

        ledger.Forget(Noon);
        ledger.Forget(posted);
        Post(ledger, "a");

        // Swept the whole way, one interval at a time, which is what a realm actually does.
        for (var at = posted; at <= posted + horizon.Guaranteed; at += horizon.Interval / 2) {
            ledger.Forget(at);

            Assert.Equal(EconomyVerdict.Replayed, Post(ledger, "a").Verdict);
        }
    }

    [Fact]
    public void AKeyIsForgottenOnceItIsPastTheHorizon() {
        var horizon = KeyHorizon.Outliving(TimeSpan.FromMinutes(1));
        var ledger = Seeded(horizon);

        ledger.Forget(Noon);
        Post(ledger, "a");

        for (var at = Noon; at <= Noon + horizon.Length + horizon.Interval; at += horizon.Interval) {
            ledger.Forget(at);
        }

        Assert.Equal(0, ledger.Keys);
        Assert.Equal(1, ledger.Forgotten);
        Assert.Equal(EconomyVerdict.Applied, Post(ledger, "a").Verdict);
    }

    /// <summary>⚠ A host that paused the process must not cost the next eight sweeps.</summary>
    [Fact]
    public void AClockThatJumpsPastTheWholeHorizonEmptiesTheSetInOneSweep() {
        var horizon = KeyHorizon.Outliving(TimeSpan.FromMinutes(1));
        var ledger = Seeded(horizon);

        ledger.Forget(Noon);

        for (var index = 0; index < 20; index++) {
            Post(ledger, $"key-{index}");
        }

        Assert.Equal(20, ledger.Forget(Noon.AddHours(1)));
        Assert.Equal(0, ledger.Keys);
    }

    /// <summary>⚠ Backwards is the safe direction: nothing ages out until the clock has caught up.</summary>
    [Fact]
    public void AClockThatGoesBackwardsForgetsNothing() {
        var ledger = Seeded(KeyHorizon.Outliving(TimeSpan.FromMinutes(1)));

        ledger.Forget(Noon);
        Post(ledger, "a");

        Assert.Equal(0, ledger.Forget(Noon.AddHours(-1)));
        Assert.Equal(1, ledger.Keys);
    }

    [Fact]
    public void ForgettingDoesNotTouchBalances() {
        var horizon = KeyHorizon.Outliving(TimeSpan.FromMinutes(1));
        var ledger = Seeded(horizon);

        ledger.Forget(Noon);
        Post(ledger, "a", 40);

        for (var at = Noon; at <= Noon + horizon.Length + horizon.Interval; at += horizon.Interval) {
            ledger.Forget(at);
        }

        Assert.Equal(40, ledger.Balance(Player, Gold));
    }

    static MemoryEconomyLedger Seeded(KeyHorizon horizon) => new(horizon);

    static EconomyResult Post(MemoryEconomyLedger ledger, string key, long amount = 10) =>
        ledger.Post(EconomyIntent.Transfer(key, Bank, Player, Gold, amount));
}

/// <summary>Letting a player go, which is the other half of seeding them when they arrive.</summary>
public class LedgerReleaseTests {
    static readonly EconomyAccount Bank = EconomyAccount.Of("world/bank");

    static readonly EconomyAccount Restore = EconomyAccount.Of("world/restore");

    static readonly DefId Gold = DefId.From("currency/gold");

    static readonly DefId Ore = DefId.From("items/ore");

    [Fact]
    public void EverythingAnAccountHoldsGoesToTheDestination() {
        var ledger = new MemoryEconomyLedger();
        var player = EconomyAccount.Of(new PlayerId(7));

        ledger.Post(EconomyIntent.Transfer("a", Bank, player, Gold, 120));
        ledger.Post(EconomyIntent.Transfer("b", Bank, player, Ore, 3));

        Assert.Equal(2, ledger.Release(player, Restore));
        Assert.Equal(0, ledger.Balance(player, Gold));
        Assert.Equal(120, ledger.Balance(Restore, Gold));
        Assert.Equal(3, ledger.Balance(Restore, Ore));
    }

    /// <summary>⚠ The invariant that finds duplication bugs must survive the leak being fixed.</summary>
    [Fact]
    public void TotalIsUnchangedByReleasing() {
        var ledger = new MemoryEconomyLedger();
        var player = EconomyAccount.Of(new PlayerId(7));

        ledger.Post(EconomyIntent.Transfer("a", Bank, player, Gold, 120));

        Assert.Equal(0, ledger.Total(Gold));

        ledger.Release(player, Restore);

        Assert.Equal(0, ledger.Total(Gold));
    }

    /// <summary>⚠ A player who left with an empty purse still had a row, and it still has to go.</summary>
    [Fact]
    public void ARowSpentToNothingIsDroppedToo() {
        var ledger = new MemoryEconomyLedger();
        var player = EconomyAccount.Of(new PlayerId(7));

        ledger.Post(EconomyIntent.Transfer("a", Bank, player, Gold, 50));
        ledger.Post(EconomyIntent.Transfer("b", player, Bank, Gold, 50));

        Assert.Equal(1, ledger.Release(player, Restore));
        Assert.Empty(ledger.Holdings(player));
    }

    [Fact]
    public void ReleasingIntoNowhereOrIntoItselfDoesNothing() {
        var ledger = new MemoryEconomyLedger();
        var player = EconomyAccount.Of(new PlayerId(7));

        ledger.Post(EconomyIntent.Transfer("a", Bank, player, Gold, 50));

        Assert.Equal(0, ledger.Release(player, EconomyAccount.Nowhere));
        Assert.Equal(0, ledger.Release(player, player));
        Assert.Equal(50, ledger.Balance(player, Gold));
    }

    /// <summary>⚠ Releasing is not a movement, so it puts no key in the guard.</summary>
    [Fact]
    public void ReleasingAddsNoKey() {
        var ledger = new MemoryEconomyLedger();
        var player = EconomyAccount.Of(new PlayerId(7));

        ledger.Post(EconomyIntent.Transfer("a", Bank, player, Gold, 50));

        var before = ledger.Keys;

        ledger.Release(player, Restore);

        Assert.Equal(before, ledger.Keys);
    }
}
