// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Live.Transfer.Tests;

/// <summary>The overlap, and every way doc 27 § Testing says it has to be able to fail.</summary>
public class SourceTransferTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly PlayerKey Bruna = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly ShardId Target = ShardId.New();

    [Fact]
    public void The_happy_path_walks_t0_to_t6() {
        var transfer = Start();

        Assert.Equal(TransferPhase.Placing, transfer.Phase);

        Assert.True(transfer.Placed(Target, Prepare(), 5, Noon));
        Assert.Equal(TransferPhase.Preparing, transfer.Phase);

        Assert.True(transfer.TargetReady(Noon.AddSeconds(1)));
        Assert.Equal(TransferPhase.Overlapping, transfer.Phase);

        Assert.True(transfer.ClientReady(Noon.AddSeconds(9), 4_200));
        Assert.Equal(TransferPhase.Committing, transfer.Phase);

        Assert.True(transfer.LeaseTaken(5, Noon.AddSeconds(9)));
        Assert.Equal(TransferPhase.HandingOff, transfer.Phase);

        Assert.True(transfer.HandoffAcknowledged(Noon.AddSeconds(10)));
        Assert.Equal(TransferPhase.Committed, transfer.Phase);
        Assert.Equal(TransferAbort.None, transfer.Abort);
        Assert.True(transfer.Done);
    }

    /// <summary>
    ///     ⚠ The property everything else rests on: the source keeps simulating the player through
    ///     every phase but the last. A realm that stopped at t2 would give them three minutes of
    ///     standing still while their map loaded.
    /// </summary>
    [Fact]
    public void The_player_is_still_ours_until_the_moment_they_are_not() {
        var transfer = Start();

        Assert.True(transfer.StillOurs);

        transfer.Placed(Target, Prepare(), 5, Noon);
        Assert.True(transfer.StillOurs);

        transfer.TargetReady(Noon);
        Assert.True(transfer.StillOurs);

        transfer.ClientReady(Noon, 4_200);
        Assert.True(transfer.StillOurs);

        transfer.LeaseTaken(5, Noon);
        Assert.True(transfer.StillOurs);

        transfer.HandoffAcknowledged(Noon);
        Assert.False(transfer.StillOurs);
    }

    [Fact]
    public void The_overlap_and_the_commit_latency_are_measured() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(), 5, Noon);
        transfer.TargetReady(Noon.AddSeconds(1));
        transfer.ClientReady(Noon.AddSeconds(31), 4_200);
        transfer.LeaseTaken(5, Noon.AddSeconds(31));
        transfer.HandoffAcknowledged(Noon.AddSeconds(32));

        Assert.Equal(TimeSpan.FromSeconds(30), transfer.Overlap);
        Assert.Equal(TimeSpan.FromSeconds(32), transfer.CommitLatency);
    }

    [Fact]
    public void The_commit_names_the_tick_and_the_shard_and_not_before_the_lease_moved() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(), 5, Noon);
        transfer.TargetReady(Noon);
        transfer.ClientReady(Noon, 4_200);

        Assert.Null(transfer.Commit());

        transfer.LeaseTaken(5, Noon);

        var commit = transfer.Commit();

        Assert.NotNull(commit);
        Assert.Equal(4_200, commit.AtTick);
        Assert.Equal(Target, commit.Shard);
    }

    // ── Every abort path, injected ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_orchestrator_never_answers() {
        var transfer = Start();

        Assert.False(transfer.Step(Noon.AddSeconds(9)));
        Assert.True(transfer.Step(Noon.AddSeconds(10)));
        Assert.Equal(TransferAbort.NoShard, transfer.Abort);
        Assert.True(transfer.StillOurs);
    }

    [Fact]
    public void The_target_never_becomes_ready() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(), 5, Noon);

        Assert.True(transfer.Step(Noon.AddSeconds(31)));
        Assert.Equal(TransferAbort.TargetNeverReady, transfer.Abort);
        Assert.True(transfer.StillOurs);
    }

    [Fact]
    public void The_client_never_arrives() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(Noon.AddHours(1)), 5, Noon);
        transfer.TargetReady(Noon);

        Assert.False(transfer.Step(Noon.AddMinutes(2)));
        Assert.True(transfer.Step(Noon.AddMinutes(4)));
        Assert.Equal(TransferAbort.ClientNeverArrived, transfer.Abort);
    }

    /// <summary>
    ///     The ticket's own expiry is checked before the phase deadline, because a client arriving
    ///     with an expired ticket is refused at the door — so continuing to wait for it is waiting
    ///     for something that cannot happen.
    /// </summary>
    [Fact]
    public void The_ticket_expires_before_the_overlap_deadline_would() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(Noon.AddSeconds(30)), 5, Noon);
        transfer.TargetReady(Noon);

        Assert.False(transfer.Step(Noon.AddSeconds(20)));
        Assert.True(transfer.Step(Noon.AddSeconds(31)));
        Assert.Equal(TransferAbort.TicketExpired, transfer.Abort);
    }

    [Fact]
    public void The_target_never_acknowledges_the_handoff() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(), 5, Noon);
        transfer.TargetReady(Noon);
        transfer.ClientReady(Noon, 4_200);
        transfer.LeaseTaken(5, Noon);

        Assert.True(transfer.Step(Noon.AddSeconds(11)));
        Assert.Equal(TransferAbort.HandoffLost, transfer.Abort);
    }

    /// <summary>
    ///     ⚠ A third realm taking the lease means this transfer's epoch is superseded and ADR-021's
    ///     fence would refuse every durable write it made. Aborting is the only thing left that is
    ///     correct.
    /// </summary>
    [Fact]
    public void Somebody_else_took_the_lease() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(), 5, Noon);
        transfer.TargetReady(Noon);
        transfer.ClientReady(Noon, 4_200);

        Assert.False(transfer.LeaseTaken(9, Noon));
        Assert.Equal(TransferPhase.Aborted, transfer.Phase);
        Assert.Equal(TransferAbort.LeaseLost, transfer.Abort);
        Assert.True(transfer.StillOurs);
    }

    [Fact]
    public void The_player_disconnects_mid_flight() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(), 5, Noon);
        transfer.TargetReady(Noon);

        Assert.True(transfer.Stop(TransferAbort.PlayerGone, Noon.AddSeconds(3)));
        Assert.Equal(TransferAbort.PlayerGone, transfer.Abort);
        Assert.Equal(Noon.AddSeconds(3), transfer.Finished);
    }

    /// <summary>
    ///     ⚠ The one refusal that matters: a source that "un-committed" would claim a player two
    ///     realms now believe in, which is the duplication this design has no other way to express.
    /// </summary>
    [Fact]
    public void A_committed_transfer_cannot_be_aborted_afterwards() {
        var transfer = Start();

        transfer.Placed(Target, Prepare(), 5, Noon);
        transfer.TargetReady(Noon);
        transfer.ClientReady(Noon, 4_200);
        transfer.LeaseTaken(5, Noon);
        transfer.HandoffAcknowledged(Noon);

        Assert.False(transfer.Stop(TransferAbort.PlayerGone, Noon.AddSeconds(1)));
        Assert.Equal(TransferPhase.Committed, transfer.Phase);
        Assert.Equal(TransferAbort.None, transfer.Abort);
        Assert.False(transfer.StillOurs);
    }

    [Fact]
    public void An_aborted_transfer_stops_stepping() {
        var transfer = Start();

        transfer.Stop(TransferAbort.Cancelled, Noon);

        Assert.False(transfer.Step(Noon.AddHours(1)));
        Assert.Equal(TransferAbort.Cancelled, transfer.Abort);
    }

    /// <summary>Events arriving in the wrong phase are refused rather than half-applied.</summary>
    [Theory]
    [InlineData(TransferPhase.Placing)]
    [InlineData(TransferPhase.Preparing)]
    [InlineData(TransferPhase.Overlapping)]
    [InlineData(TransferPhase.Committing)]
    public void An_event_for_the_wrong_phase_changes_nothing(TransferPhase reached) {
        var transfer = Start();

        if (reached >= TransferPhase.Preparing) {
            transfer.Placed(Target, Prepare(), 5, Noon);
        }

        if (reached >= TransferPhase.Overlapping) {
            transfer.TargetReady(Noon);
        }

        if (reached >= TransferPhase.Committing) {
            transfer.ClientReady(Noon, 4_200);
        }

        // Every one of these belongs to a phase that is not the current one.
        var before = transfer.Phase;

        if (reached != TransferPhase.Placing) {
            Assert.False(transfer.Placed(Target, Prepare(), 5, Noon));
        }

        if (reached != TransferPhase.Preparing) {
            Assert.False(transfer.TargetReady(Noon));
        }

        if (reached != TransferPhase.Overlapping) {
            Assert.False(transfer.ClientReady(Noon, 1));
        }

        Assert.False(transfer.HandoffAcknowledged(Noon));
        Assert.Equal(before, transfer.Phase);
    }

    static SourceTransfer Start() => new(Bruna, "maps/divinity", Noon, reason: "a portal");

    static TransferPrepare Prepare(DateTimeOffset? expires = null) {
        using var signer = new TransferTicketSigner(Encoding.UTF8.GetBytes("a cluster key that is long enough."));

        var ticket = signer.Sign(
            new() {
                Player = Bruna,
                Target = Target,
                Endpoint = new("realm.example", 30001),
                LeaseEpoch = 5,
                Expires = expires ?? Noon.AddMinutes(2)
            }
        );

        return new(ticket.Encode(), ticket.Endpoint, Target, new("0.1.0", 0xC0FFEE), 900);
    }
}
