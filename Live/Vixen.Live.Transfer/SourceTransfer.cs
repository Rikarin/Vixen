// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Transfer;

/// <summary>One player's transfer, from the realm that still owns them.</summary>
/// <remarks>
///     <para>
///         Doc 27 § The overlap as a state machine a test constructs and drives — the same shape
///         <c>ShardLifecycle</c>, <c>PlayerLeaseState</c> and <c>GateService</c> took, and for the
///         same reason: § Testing asks for <i>every abort path, injected</i>, and nobody injects a
///         source realm dying at t5 against three live processes.
///     </para>
///     <para>
///         ⚠ <b>Nothing here talks to anything.</b> It is fed events — the orchestrator answered,
///         the client arrived, the target acked — and asked what to do next. The realm supplies the
///         I/O and <c>RealmDirectory</c> supplies the grain calls, which is what keeps ADR-016's rule
///         a property of where this is driven from rather than a promise made inside it.
///     </para>
///     <para>
///         ⚠ <b>The player is authoritative here until <see cref="Phase" /> reaches
///         <see cref="TransferPhase.Committed" />.</b> Every abort leaves them playing, and there is
///         no state in which two realms both believe they own them — the lease epoch is the boundary
///         and <see cref="LeaseTaken" /> is the only edge that crosses it.
///     </para>
/// </remarks>
public sealed class SourceTransfer {
    readonly TransferDeadlines deadlines;

    DateTimeOffset entered;

    /// <summary>Starts one.</summary>
    /// <param name="player">Who is moving.</param>
    /// <param name="destination">Which map they asked for.</param>
    /// <param name="now">The realm's clock.</param>
    /// <param name="deadlines">How long each step may take.</param>
    /// <param name="reason">Why they are moving, for the trace and the fleet view.</param>
    public SourceTransfer(
        PlayerKey player,
        string destination,
        DateTimeOffset now,
        TransferDeadlines? deadlines = null,
        string reason = ""
    ) {
        Player = player;
        Destination = destination ?? "";
        Reason = reason ?? "";
        Started = now;
        entered = now;

        this.deadlines = deadlines ?? new();
    }

    /// <summary>Who is moving.</summary>
    public PlayerKey Player { get; }

    /// <summary>Which map they asked for.</summary>
    public string Destination { get; }

    /// <summary>Why — a portal, a party join, a drain.</summary>
    public string Reason { get; }

    /// <summary>When it began.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>Where it has got to.</summary>
    public TransferPhase Phase { get; private set; } = TransferPhase.Placing;

    /// <summary>Why it did not happen, once it has not.</summary>
    public TransferAbort Abort { get; private set; }

    /// <summary>Which shard, once the orchestrator has said.</summary>
    public ShardId Target { get; private set; } = ShardId.None;

    /// <summary>The epoch the target will take.</summary>
    public long LeaseEpoch { get; private set; }

    /// <summary>What the client was told, once it has been.</summary>
    public TransferPrepare? Prepare { get; private set; }

    /// <summary>The tick the player stops existing here, once chosen.</summary>
    public long CommitTick { get; private set; }

    /// <summary>When it finished, either way.</summary>
    public DateTimeOffset? Finished { get; private set; }

    /// <summary>Whether it is over.</summary>
    public bool Done => Phase is TransferPhase.Committed or TransferPhase.Aborted;

    /// <summary>Whether the player is still this realm's to simulate.</summary>
    /// <remarks>
    ///     ⚠ <b>True in every phase except <see cref="TransferPhase.Committed" />.</b> A realm that
    ///     stopped simulating at t2 would give the player three minutes of standing still while their
    ///     map loaded, which is the failure the overlap exists to avoid.
    /// </remarks>
    public bool StillOurs => Phase != TransferPhase.Committed;

    /// <summary>How long the overlap lasted — doc 27 § Tick rebasing's <c>OverlapDuration</c>.</summary>
    public TimeSpan Overlap { get; private set; }

    /// <summary>How long the commit took, once it has.</summary>
    public TimeSpan CommitLatency { get; private set; }

    /// <summary>The orchestrator named a shard. t1.</summary>
    /// <param name="shard">Which.</param>
    /// <param name="prepare">What the client needs to reach it.</param>
    /// <param name="epoch">The epoch the target will acquire.</param>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Whether this was expected here.</returns>
    public bool Placed(ShardId shard, TransferPrepare prepare, long epoch, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(prepare);

        if (Phase != TransferPhase.Placing) {
            return false;
        }

        Target = shard;
        Prepare = prepare;
        LeaseEpoch = epoch;

        Enter(TransferPhase.Preparing, now);

        return true;
    }

    /// <summary>The target pre-reserved a slot and is expecting them. t2.</summary>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Whether this was expected here.</returns>
    public bool TargetReady(DateTimeOffset now) {
        if (Phase != TransferPhase.Preparing) {
            return false;
        }

        Enter(TransferPhase.Overlapping, now);

        return true;
    }

    /// <summary>The client has the map, the session and the first snapshot. t4.</summary>
    /// <param name="now">The realm's clock.</param>
    /// <param name="atTick">The tick the source will hand over at.</param>
    /// <returns>Whether this was expected here.</returns>
    /// <remarks>
    ///     ⚠ <b>Reported by the <em>client</em>, not by the target.</b> The target knows it admitted
    ///     somebody; only the client knows whether its own map finished loading and its first
    ///     snapshot arrived — and moving a player whose target is still a loading screen is the one
    ///     thing the overlap exists to make impossible.
    /// </remarks>
    public bool ClientReady(DateTimeOffset now, long atTick) {
        if (Phase != TransferPhase.Overlapping) {
            return false;
        }

        Overlap = now - entered;
        CommitTick = atTick;

        Enter(TransferPhase.Committing, now);

        return true;
    }

    /// <summary>The lease moved to the target. <b>The atomic moment.</b> t4.</summary>
    /// <param name="epoch">The epoch that was granted.</param>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Whether this was expected here.</returns>
    /// <remarks>
    ///     ⚠ <b>An epoch other than the one this transfer asked for means somebody else took the
    ///     lease</b> — a third realm, a reconnect elsewhere, an operator. This transfer's writes
    ///     would be refused by the fence from here on, so it aborts rather than continuing into a
    ///     handoff whose durable half can never land.
    /// </remarks>
    public bool LeaseTaken(long epoch, DateTimeOffset now) {
        if (Phase != TransferPhase.Committing) {
            return false;
        }

        if (epoch != LeaseEpoch) {
            Stop(TransferAbort.LeaseLost, now);

            return false;
        }

        Enter(TransferPhase.HandingOff, now);

        return true;
    }

    /// <summary>The target applied the payload and said so. t5 → t6.</summary>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Whether this was expected here.</returns>
    public bool HandoffAcknowledged(DateTimeOffset now) {
        if (Phase != TransferPhase.HandingOff) {
            return false;
        }

        CommitLatency = now - Started;

        Enter(TransferPhase.Committed, now);
        Finished = now;

        return true;
    }

    /// <summary>Gives up.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Whether it was still in flight.</returns>
    /// <remarks>
    ///     ⚠ <b>Aborting after <see cref="TransferPhase.Committed" /> is refused rather than
    ///     tolerated.</b> The player is somebody else's by then, and a source that "un-committed"
    ///     would be a source claiming a player two realms now believe in — which is the duplication
    ///     this design has no other way to express.
    /// </remarks>
    public bool Stop(TransferAbort reason, DateTimeOffset now) {
        if (Done) {
            return false;
        }

        Abort = reason == TransferAbort.None ? TransferAbort.Cancelled : reason;
        Finished = now;

        Enter(TransferPhase.Aborted, now);

        return true;
    }

    /// <summary>One turn of the clock. Gives up on whatever has run out of time.</summary>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Whether this call ended it.</returns>
    /// <remarks>
    ///     Driven from the realm's update rather than from a timer, because a transfer that expired
    ///     while the realm was stalled should expire when the realm notices — not on a thread-pool
    ///     thread that would then be mutating a player the frame is reading.
    /// </remarks>
    public bool Step(DateTimeOffset now) {
        if (Done) {
            return false;
        }

        // The ticket's own expiry is checked before the phase deadline, because it is the one that
        // makes the rest pointless: a client arriving with an expired ticket is refused at the door,
        // so continuing to wait for it is waiting for something that cannot happen.
        if (Prepare is not null
            && Phase is TransferPhase.Preparing or TransferPhase.Overlapping
            && TransferTicket.TryDecode(Prepare.Ticket, out var ticket, out _)
            && ticket!.Expires <= now) {
            return Stop(TransferAbort.TicketExpired, now);
        }

        var allowed = deadlines.For(Phase);

        if (allowed <= TimeSpan.Zero || now - entered < allowed) {
            return false;
        }

        return Stop(
            Phase switch {
                TransferPhase.Placing => TransferAbort.NoShard,
                TransferPhase.Preparing => TransferAbort.TargetNeverReady,
                TransferPhase.Overlapping => TransferAbort.ClientNeverArrived,
                TransferPhase.Committing => TransferAbort.LeaseLost,
                _ => TransferAbort.HandoffLost
            },
            now
        );
    }

    /// <summary>What to send the client at t5, once there is something to send.</summary>
    /// <returns>The commit, or null if it is not time.</returns>
    public TransferCommit? Commit() =>
        Phase is TransferPhase.HandingOff or TransferPhase.Committed ? new(CommitTick, Target) : null;

    /// <inheritdoc />
    public override string ToString() =>
        Abort == TransferAbort.None
            ? string.Create(CultureInfo.InvariantCulture, $"{Player} → {Destination}: {Phase}")
            : string.Create(CultureInfo.InvariantCulture, $"{Player} → {Destination}: {Phase} ({Abort})");

    void Enter(TransferPhase phase, DateTimeOffset now) {
        Phase = phase;
        entered = now;
    }
}
