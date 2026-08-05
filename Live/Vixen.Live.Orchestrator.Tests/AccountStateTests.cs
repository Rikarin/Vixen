// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>
///     The account-wide half of doc 28's collections, which doc 27 § Grains does not have a grain for
///     and which G8 is what showed was missing: <c>IPlayerGrain</c> is keyed by account *and*
///     character, and a mount earned on one character is owned by all of them.
/// </summary>
public sealed class AccountStateTests {
    readonly AccountState account = new();

    static AccountUnlock Mount(string address = "collect/mount/gryphon") =>
        new(address, "Loot", "boss/skarr", 0);

    [Fact]
    public void AnUnlockIsRecordedWithWhereItCameFrom() {
        Assert.True(account.Unlock(Mount()));

        var held = Assert.Single(account.Holdings().Unlocks);

        Assert.Equal("collect/mount/gryphon", held.Address);
        Assert.Equal("Loot", held.Source);
        Assert.Equal("boss/skarr", held.From);
    }

    [Fact]
    public void TheSameThingTwiceIsANoOpRatherThanASecondRow() {
        // ⚠ Two realms racing to grant one mount to two characters of one account is ordinary, not
        // exceptional — that is what "account-wide" means. Idempotent on the address, so no key.
        Assert.True(account.Unlock(Mount()));
        Assert.False(account.Unlock(Mount()));
        Assert.Equal(1, account.Count);
    }

    [Fact]
    public void TheOrderIsAssignedHereRatherThanTrustedFromTheCaller() {
        // Two realms cannot agree on a counter without asking, and asking is this call.
        account.Unlock(Mount("a") with { Order = 500 });
        account.Unlock(Mount("b") with { Order = 500 });

        Assert.Equal([1, 2], account.Holdings().Unlocks.Select(unlock => unlock.Order));
    }

    [Fact]
    public void ThingsComeBackInTheOrderTheyWereGot() {
        account.Unlock(Mount("c"));
        account.Unlock(Mount("a"));
        account.Unlock(Mount("b"));

        Assert.Equal(["c", "a", "b"], account.Holdings().Unlocks.Select(unlock => unlock.Address));
    }

    [Fact]
    public void AnAchievementIsWorthItsPointsOnceOnly() {
        Assert.True(account.Earn("achieve/slayer", 20));
        Assert.False(account.Earn("achieve/slayer", 20));

        Assert.Equal(1, account.Earned);
        Assert.Equal(20, account.Points);
    }

    [Fact]
    public void ThereIsNoWayToUnEarnAnAchievement() {
        // ⚠ Doc 28's rule, expressed as an absent method: a refund, a sale or a patch that raises a
        // threshold must not take back something somebody already did. Revoke is unlocks only.
        account.Earn("achieve/slayer", 20);
        account.Unlock(Mount());

        Assert.True(account.Revoke("collect/mount/gryphon"));
        Assert.False(account.Revoke("achieve/slayer"));

        Assert.Equal(0, account.Count);
        Assert.Equal(1, account.Earned);
        Assert.Equal(20, account.Points);
    }

    [Fact]
    public void ARevokedThingCanBeGivenBack() {
        account.Unlock(Mount());
        account.Revoke("collect/mount/gryphon");

        Assert.True(account.Unlock(Mount()));
        Assert.Equal(1, account.Count);
    }

    [Fact]
    public void NegativePointsCountAsNone() {
        account.Earn("achieve/odd", -50);

        Assert.Equal(0, account.Points);
    }

    [Fact]
    public void EveryChangeMovesTheRevisionAndANoOpDoesNot() {
        // What an optimistic write checks, and the same signal HousePlot uses for the same reason.
        Assert.Equal(0u, account.Revision);

        account.Unlock(Mount());
        Assert.Equal(1u, account.Revision);

        account.Unlock(Mount());
        Assert.Equal(1u, account.Revision);

        account.Earn("achieve/slayer", 5);
        Assert.Equal(2u, account.Revision);

        account.Revoke("nothing-here");
        Assert.Equal(2u, account.Revision);
    }

    [Fact]
    public void AnEmptyAddressIsRefusedRatherThanStored() {
        Assert.False(account.Unlock(Mount("")));
        Assert.False(account.Earn("", 10));
        Assert.False(account.Revoke(""));
        Assert.Equal(0u, account.Revision);
    }

    // ── Loading ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASavedAccountComesBackAsItWas() {
        account.Unlock(Mount("collect/mount/gryphon"));
        account.Unlock(Mount("collect/pet/cat"));
        account.Earn("achieve/stabled", 10);

        var saved = account.Holdings();
        var loaded = new AccountState();

        loaded.Restore(saved);

        Assert.Equal(saved.Unlocks.Select(unlock => unlock.Address), loaded.Holdings().Unlocks.Select(unlock => unlock.Address));
        Assert.Equal(saved.Achievements, loaded.Holdings().Achievements);
        Assert.Equal(saved.Points, loaded.Holdings().Points);
        Assert.Equal(saved.Revision, loaded.Holdings().Revision);
    }

    [Fact]
    public void ARestoredAccountDoesNotRenumberWhatItAlreadyHad() {
        // ⚠ Not a replay: re-running the grants would re-derive them against today's content, so a
        // patch that removed a promotion would take back what somebody was given.
        var loaded = new AccountState();

        loaded.Restore(new([new("collect/mount/gryphon", "Promotion", "", 40)], [], 0, 3));

        Assert.Equal(40, loaded.Holdings().Unlocks[0].Order);

        loaded.Unlock(Mount("collect/pet/cat"));

        Assert.Equal(41, loaded.Holdings().Unlocks[1].Order);
    }

    [Fact]
    public void AnEmptyAccountIsWhatAFreshOneLooksLike() {
        Assert.Equal(AccountHoldings.Empty.Points, account.Holdings().Points);
        Assert.Empty(account.Holdings().Unlocks);
        Assert.Empty(account.Holdings().Achievements);
    }
}
