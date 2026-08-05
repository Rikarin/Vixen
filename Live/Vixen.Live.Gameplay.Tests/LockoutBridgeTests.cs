// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Instances;
using Xunit;
using NetworkPlayerId = Vixen.Net.Sessions.PlayerId;

namespace Vixen.Live.Gameplay.Tests;

public class LockoutBridgeTests {
    static readonly DefId Barrowdeep = DefId.From("instances/barrowdeep");

    readonly PlayerKey ana = new(Guid.NewGuid(), Guid.NewGuid());
    readonly GameplayIdentityMap identity = new();
    readonly LockoutBridge bridge;

    PlayerId Ana { get; }

    public LockoutBridgeTests() {
        Ana = identity.Admit(ana, new NetworkPlayerId(1));
        bridge = new(identity);
    }

    static Lockout Saved(PlayerId player, double expires = 1000d, int completions = 1) =>
        new(player, Barrowdeep, "heroic", expires, completions);

    // ── The cold-read problem ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AskingAboutSomebodyWhoWasNeverLoadedIsCountedAndRaised() {
        // ⚠ The finding this type is arranged around. An unknown balance reads as zero and refuses a
        // purchase, which is safe. An unknown *lockout* reads as null, which ILockoutStore defines as
        // "not locked" — so a cold cache admits somebody to a raid they are already saved to, and the
        // run they get is one the fleet cannot take back.
        var raised = 0;

        bridge.Cold += _ => raised++;

        Assert.Null(bridge.Find(Ana, Barrowdeep, "heroic"));
        Assert.Equal(1, bridge.ColdReads);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void AWarmedPlayerWithNoLockoutsIsADifferentFactFromAnUnloadedOne() {
        // ⚠ "Saved to nothing" and "nobody has asked" are the same absence in the view and must not
        // be the same fact — which is why warming with an empty list is meaningful.
        bridge.Warmed(Ana, []);

        Assert.True(bridge.IsWarm(Ana));
        Assert.Null(bridge.Find(Ana, Barrowdeep, "heroic"));
        Assert.Equal(0, bridge.ColdReads);
    }

    [Fact]
    public void SavingForSomebodyThisRealmDoesNotKnowStaysLocalAndIsCounted() {
        // A durable write against nobody is not a write. It is recorded in the view so the frame is
        // consistent, and the counter is what says the realm has a bug.
        var stranger = new PlayerId(999);

        bridge.Save(Saved(stranger));

        Assert.Equal(1, bridge.ColdReads);
        Assert.Equal(0, bridge.Pending);
        Assert.NotNull(bridge.Find(stranger, Barrowdeep, "heroic"));
    }

    // ── Answering in the frame ────────────────────────────────────────────────────────────────

    [Fact]
    public void AWarmedLockoutIsAnsweredWithoutARoundTrip() {
        bridge.Warmed(Ana, [Saved(Ana, expires: 5000d, completions: 2)]);

        var found = bridge.Find(Ana, Barrowdeep, "heroic");

        Assert.NotNull(found);
        Assert.Equal(2, found.Value.Completions);
        Assert.Equal(0, bridge.ColdReads);
    }

    [Fact]
    public void ADifferentDifficultyIsADifferentLockout() {
        bridge.Warmed(Ana, [Saved(Ana)]);

        Assert.NotNull(bridge.Find(Ana, Barrowdeep, "heroic"));
        Assert.Null(bridge.Find(Ana, Barrowdeep, "normal"));
    }

    [Fact]
    public void SavingAnswersLocallyAndQueuesTheWrite() {
        bridge.Warmed(Ana, []);
        bridge.Save(Saved(Ana));

        Assert.NotNull(bridge.Find(Ana, Barrowdeep, "heroic"));
        Assert.Equal(1, bridge.Pending);

        var write = Assert.Single(bridge.Drain());

        // ⚠ The durable identity, never the gameplay one.
        Assert.Equal(ana, write.Player);
        Assert.Equal(Barrowdeep, write.Instance);
    }

    [Fact]
    public void ExtendingALockoutTwiceIsOneWriteAndTheLastIsTheTruth() {
        bridge.Warmed(Ana, []);
        bridge.Save(Saved(Ana, completions: 1));
        bridge.Save(Saved(Ana, completions: 2));

        var write = Assert.Single(bridge.Drain());

        Assert.Equal(2, write.Completions);
    }

    [Fact]
    public void DrainingDoesNotRemoveAndSettlingDoes() {
        bridge.Warmed(Ana, []);
        bridge.Save(Saved(Ana));

        var write = Assert.Single(bridge.Drain());

        Assert.Single(bridge.Drain());
        Assert.True(bridge.Settle(write));
        Assert.Equal(0, bridge.Pending);
        Assert.False(bridge.Settle(write));
    }

    // ── Purging and leaving ───────────────────────────────────────────────────────────────────

    [Fact]
    public void PurgingDropsWhatHasLiftedAndKeepsWhatHasNot() {
        bridge.Warmed(Ana, [Saved(Ana, expires: 100d), new(Ana, DefId.From("instances/other"), "heroic", 9000d, 1)]);

        Assert.Equal(1, bridge.Purge(500d));
        Assert.Equal(1, bridge.Count);
        Assert.Null(bridge.Find(Ana, Barrowdeep, "heroic"));
    }

    [Fact]
    public void PurgingWritesNothing() {
        // ⚠ Dropping what has lifted from a realm's memory is not releasing a lockout. What decides
        // it has lifted is the reset the cluster holds, and a realm that could write a release would
        // be one that ends a raid lockout by restarting.
        bridge.Warmed(Ana, [Saved(Ana, expires: 100d)]);
        bridge.Purge(500d);

        Assert.Equal(0, bridge.Pending);
    }

    [Fact]
    public void SomebodyWhoLeavesIsForgottenAndTheirWritesAreNot() {
        // ⚠ A lockout recorded a moment before a disconnect is the one most worth not losing — it is
        // the run they just did.
        bridge.Warmed(Ana, []);
        bridge.Save(Saved(Ana));

        Assert.True(bridge.Forget(Ana));
        Assert.False(bridge.IsWarm(Ana));
        Assert.Equal(0, bridge.Count);
        Assert.Equal(1, bridge.Pending);
    }

    [Fact]
    public void ForgettingSomebodyWhoWasNotHereSaysSo() => Assert.False(bridge.Forget(new PlayerId(999)));

    [Fact]
    public void ComingBackNeedsWarmingAgain() {
        bridge.Warmed(Ana, [Saved(Ana)]);
        bridge.Forget(Ana);

        Assert.Null(bridge.Find(Ana, Barrowdeep, "heroic"));
        Assert.Equal(1, bridge.ColdReads);
    }
}
