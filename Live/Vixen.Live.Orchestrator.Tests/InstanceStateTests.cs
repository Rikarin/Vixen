// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>
///     The second of the three grains doc 27 left undeclared at L1, now that G6 has built the feature
///     it is a contract for. A lockout is fleet-wide, which is the whole reason it is a grain: doc 28
///     says "a lockout one shard knew about is a lockout a player evades by zoning".
/// </summary>
public sealed class InstanceStateTests {
    static readonly PlayerKey Ana = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly PlayerKey Ben = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly PlayerKey Cai = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly PlayerKey Stranger = new(Guid.NewGuid(), Guid.NewGuid());

    static readonly DateTimeOffset Opened = DateTimeOffset.UnixEpoch;
    static readonly DateTimeOffset Reset = DateTimeOffset.UnixEpoch.AddDays(7);

    readonly InstanceState instance = new();

    InstanceOutcome OpenIt(params PlayerKey[] roster) =>
        instance.Open("instances/barrowdeep", "heroic", [.. roster], capacity: 5, Opened, Reset);

    [Fact]
    public void OpeningRecordsTheInstanceAndItsReset() {
        Assert.Equal(InstanceWrite.Applied, OpenIt(Ana, Ben).Write);

        var record = instance.Read();

        Assert.Equal("instances/barrowdeep", record.Instance);
        Assert.Equal("heroic", record.Difficulty);
        Assert.Equal(Reset, record.Expires);
        Assert.Equal(Opened, record.Opened);
    }

    [Fact]
    public void AnInstanceIsOpenedOnce() {
        OpenIt(Ana);

        Assert.Equal(InstanceWrite.Open, OpenIt(Ben).Write);
    }

    [Fact]
    public void NothingWorksBeforeItIsOpened() {
        Assert.Equal(InstanceWrite.NotOpen, instance.Bind(Ana, Opened).Write);
        Assert.Equal(InstanceWrite.NotOpen, instance.Defeat("bosses/gravewarden", Opened).Write);
        Assert.Equal(InstanceWrite.NotOpen, instance.Close().Write);
        Assert.False(instance.Read().Exists);
    }

    // ── Binding is the lockout ────────────────────────────────────────────────────────────────

    [Fact]
    public void BindingSavesSomebodyToIt() {
        OpenIt(Ana, Ben);

        Assert.Equal(InstanceWrite.Applied, instance.Bind(Ana, Opened).Write);
        Assert.True(instance.IsBound(Ana));
        Assert.False(instance.IsBound(Ben));
    }

    [Fact]
    public void ARetriedBindIsUnchangedRatherThanASecondRow() {
        // ⚠ Or the capacity check starts counting one player twice.
        OpenIt(Ana);
        instance.Bind(Ana, Opened);

        Assert.Equal(InstanceWrite.Unchanged, instance.Bind(Ana, Opened).Write);
        Assert.Equal(1, instance.Count);
    }

    [Fact]
    public void AnAccessListIsAnExceptionAndAnEmptyOneAdmitsAnybody() {
        // What a public dungeon finder wants.
        OpenIt();

        Assert.Equal(InstanceWrite.Applied, instance.Bind(Stranger, Opened).Write);
    }

    [Fact]
    public void SomebodyOffTheAccessListIsRefused() {
        OpenIt(Ana, Ben);

        Assert.Equal(InstanceWrite.NotAdmitted, instance.Bind(Stranger, Opened).Write);
        Assert.Equal(0, instance.Count);
    }

    [Fact]
    public void AFullInstanceRefusesTheNext() {
        var roster = new[] { Ana, Ben, Cai, new PlayerKey(Guid.NewGuid(), Guid.NewGuid()), new PlayerKey(Guid.NewGuid(), Guid.NewGuid()), Stranger };

        instance.Open("instances/barrowdeep", "heroic", [.. roster], capacity: 5, Opened, Reset);

        foreach (var player in roster.Take(5)) {
            Assert.Equal(InstanceWrite.Applied, instance.Bind(player, Opened).Write);
        }

        Assert.Equal(InstanceWrite.Full, instance.Bind(Stranger, Opened).Write);
    }

    [Fact]
    public void ThereIsNoWayToUnbind() {
        // ⚠ That is what a lockout *is*: a save you cannot leave. Expressed as an absent method, the
        // same way an achievement has no un-earn.
        Assert.DoesNotContain(
            typeof(InstanceState).GetMethods(),
            method => method.Name.Contains("Unbind", StringComparison.Ordinal)
                || method.Name.Contains("Release", StringComparison.Ordinal)
        );
    }

    // ── Progress is the instance's ────────────────────────────────────────────────────────────

    [Fact]
    public void SomebodyBoundLateInheritsWhatIsAlreadyDead() {
        // ⚠ The rule the mechanic exists for. Per-player progress would mean a raid re-killing its
        // first boss for every latecomer, which is both the exploit and the tedium.
        OpenIt(Ana, Ben);
        instance.Bind(Ana, Opened);
        instance.Defeat("bosses/gravewarden", Opened);

        Assert.Equal(InstanceWrite.Applied, instance.Bind(Ben, Opened).Write);
        Assert.True(instance.IsDefeated("bosses/gravewarden"));
        Assert.Equal(["bosses/gravewarden"], instance.Read().Defeated);
    }

    [Fact]
    public void ABossReportedTwiceIsUnchangedRatherThanASecondKill() {
        // ⚠ A realm whose grain call was lost retries, and for a loot-bearing encounter a second kill
        // is the duplication this whole layer exists to prevent.
        OpenIt(Ana);

        Assert.Equal(InstanceWrite.Applied, instance.Defeat("bosses/gravewarden", Opened).Write);
        Assert.Equal(InstanceWrite.Unchanged, instance.Defeat("bosses/gravewarden", Opened).Write);
        Assert.Single(instance.Read().Defeated);
    }

    // ── Expiry and closing ────────────────────────────────────────────────────────────────────

    [Fact]
    public void NothingLandsAfterTheReset() {
        OpenIt(Ana, Ben);
        instance.Bind(Ana, Opened);

        Assert.Equal(InstanceWrite.Expired, instance.Bind(Ben, Reset).Write);
        Assert.Equal(InstanceWrite.Expired, instance.Defeat("bosses/gravewarden", Reset).Write);
    }

    [Fact]
    public void ClosingDoesNotReleaseAnybodysLockout() {
        // ⚠ The shard goes away; the save does not, until its reset. Otherwise disbanding is how a
        // group runs a raid twice.
        OpenIt(Ana);
        instance.Bind(Ana, Opened);

        Assert.Equal(InstanceWrite.Applied, instance.Close().Write);
        Assert.True(instance.IsBound(Ana));
        Assert.True(instance.Read().Closed);
    }

    [Fact]
    public void AClosedInstanceTakesNothingMore() {
        OpenIt(Ana, Ben);
        instance.Close();

        Assert.Equal(InstanceWrite.Expired, instance.Bind(Ben, Opened).Write);
        Assert.Equal(InstanceWrite.Expired, instance.Defeat("bosses/gravewarden", Opened).Write);
        Assert.Equal(InstanceWrite.Unchanged, instance.Close().Write);
    }

    // ── Revisions and loading ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryChangeMovesTheRevisionAndARefusalDoesNot() {
        OpenIt(Ana);

        var before = instance.Revision;

        Assert.Equal(InstanceWrite.NotAdmitted, instance.Bind(Stranger, Opened).Write);
        Assert.Equal(before, instance.Revision);

        Assert.Equal(before + 1, instance.Bind(Ana, Opened).Revision);
    }

    [Fact]
    public void ASavedInstanceComesBackAsItWas() {
        OpenIt(Ana, Ben);
        instance.Bind(Ana, Opened);
        instance.Defeat("bosses/gravewarden", Opened);

        var saved = instance.Read();
        var loaded = new InstanceState();

        loaded.Restore(saved, capacity: 5, [Ana, Ben]);

        Assert.Equal(saved, loaded.Read());
        Assert.True(loaded.IsBound(Ana));
        Assert.True(loaded.IsDefeated("bosses/gravewarden"));
    }

    [Fact]
    public void ARestoredInstanceStillRefusesSomebodyOffItsList() {
        // The access list is the group's rather than the instance's, so it is handed back on restore
        // rather than stored on the record — and the refusal has to survive a reactivation.
        OpenIt(Ana, Ben);

        var loaded = new InstanceState();

        loaded.Restore(instance.Read(), capacity: 5, [Ana, Ben]);

        Assert.Equal(InstanceWrite.NotAdmitted, loaded.Bind(Stranger, Opened).Write);
    }
}
