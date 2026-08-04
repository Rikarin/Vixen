// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Transfer;

/// <summary>What the client is doing about a transfer.</summary>
public enum ClientTransferState : byte {
    /// <summary>One session, no transfer.</summary>
    Settled = 0,

    /// <summary>Told where to go. Opening the second session.</summary>
    Connecting = 1,

    /// <summary>Two sessions. Playing on the first, loading on the second.</summary>
    Loading = 2,

    /// <summary>Loaded and reported ready. Waiting for the commit.</summary>
    Waiting = 3,

    /// <summary>Switched. The old session is closing.</summary>
    Switched = 4,

    /// <summary>It did not happen. Still on the first session, which never stopped working.</summary>
    Abandoned = 5
}

/// <summary>
///     The client's half of the overlap: hold two sessions, switch at a tick, and rebase the clock.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every state before <see cref="ClientTransferState.Switched" /> is one the player is
///         still playing in.</b> The second session exists so the map can be fetched and the first
///         snapshot can arrive while they walk around on the first; a client that stopped rendering
///         at <see cref="ClientTransferState.Connecting" /> would turn a preload back into a loading
///         screen and give up everything the protocol is for.
///     </para>
///     <para>
///         ⚠ <b>Abandoning is not a failure the player sees.</b> The first session was never closed,
///         so an abort is a second session quietly going away. Doc 27: <i>every abort leaves the
///         player where they already were, which is always a valid state</i>.
///     </para>
/// </remarks>
public sealed class ClientTransfer {
    ClientTransferState state = ClientTransferState.Settled;

    /// <summary>Where it has got to.</summary>
    public ClientTransferState State => state;

    /// <summary>What the source told it, once told.</summary>
    public TransferPrepare? Prepare { get; private set; }

    /// <summary>How many times this client has reset its prediction — doc 27's `PredictionResetCount`.</summary>
    public int PredictionResets { get; private set; }

    /// <summary>The rebase in flight, once there is a second clock to converge on.</summary>
    public TickRebase Rebase { get; private set; } = TickRebase.None;

    /// <summary>Whether the client should still be simulating and rendering the source realm.</summary>
    public bool SourceIsAuthoritative => state is not ClientTransferState.Switched;

    /// <summary>Told where to go. t2.</summary>
    /// <param name="prepare">The endpoint, the ticket and the target's tick estimate.</param>
    /// <param name="ourVersion">What this client is running, for the check below.</param>
    /// <returns>Whether it will go.</returns>
    /// <remarks>
    ///     ⚠ <b>The content check happens here, before the socket is opened.</b> The handshake would
    ///     reject a mismatch anyway (doc 16), but finding out before connecting costs nothing and
    ///     turns "connection refused" into "fetch the catalog" — which is the same distinction
    ///     <c>PlayStatus.UpdateRequired</c> makes one tier up.
    /// </remarks>
    public bool Prepared(TransferPrepare prepare, RealmVersion ourVersion) {
        ArgumentNullException.ThrowIfNull(prepare);

        if (state is not (ClientTransferState.Settled or ClientTransferState.Abandoned)) {
            return false;
        }

        if (prepare.Content.IsValid && !prepare.Content.Admits(ourVersion)) {
            return false;
        }

        Prepare = prepare;
        state = ClientTransferState.Connecting;

        return true;
    }

    /// <summary>The second session handshook and the realm admitted it dormant. t3.</summary>
    /// <param name="ourTick">This client's current tick on the source.</param>
    /// <returns>Whether this was expected.</returns>
    public bool Connected(long ourTick) {
        if (state != ClientTransferState.Connecting || Prepare is null) {
            return false;
        }

        Rebase = new(ourTick, Prepare.TargetTick);
        state = ClientTransferState.Loading;

        return true;
    }

    /// <summary>The map is loaded and the first snapshot has arrived. t4.</summary>
    /// <returns>Whether this was expected.</returns>
    public bool Loaded() {
        if (state != ClientTransferState.Loading) {
            return false;
        }

        state = ClientTransferState.Waiting;

        return true;
    }

    /// <summary>A better estimate of the target's clock, from a snapshot during the overlap.</summary>
    /// <param name="sourceTick">Where the source is now.</param>
    /// <param name="targetTick">Where the target says it is.</param>
    /// <remarks>
    ///     This is what makes the switch a pointer change rather than a resync: by t6 the estimate
    ///     has converged over the whole overlap, so nothing has to be measured after the player is
    ///     already being simulated somewhere new.
    /// </remarks>
    public void Observed(long sourceTick, long targetTick) {
        if (state is ClientTransferState.Loading or ClientTransferState.Waiting) {
            Rebase = new(sourceTick, targetTick);
        }
    }

    /// <summary>The source said to switch. t6.</summary>
    /// <param name="commit">Which tick, and which shard.</param>
    /// <returns>Whether it switched.</returns>
    /// <remarks>
    ///     ⚠ <b>The shard is checked, so a stale commit cannot move somebody twice.</b> A commit for
    ///     a shard this client is not transferring to is a message from a transfer that already
    ///     aborted, and obeying it would send the player to a realm holding nothing for them.
    /// </remarks>
    public bool Committed(TransferCommit commit) {
        ArgumentNullException.ThrowIfNull(commit);

        if (state != ClientTransferState.Waiting || Prepare is null || commit.Shard != Prepare.Shard) {
            return false;
        }

        // ⚠ Exactly one reset per transfer, and it happens here rather than at t2. Rolling back
        // across a realm boundary is meaningless — the state to replay from belongs to a simulation
        // that no longer owns this player — but resetting any earlier would throw away prediction
        // the player is still using on the source.
        PredictionResets++;
        state = ClientTransferState.Switched;

        return true;
    }

    /// <summary>It did not happen. Close the second session and carry on.</summary>
    /// <returns>Whether there was one to abandon.</returns>
    public bool Abandon() {
        if (state is ClientTransferState.Settled or ClientTransferState.Switched) {
            return false;
        }

        Prepare = null;
        Rebase = TickRebase.None;
        state = ClientTransferState.Abandoned;

        return true;
    }

    /// <summary>The switch is complete and this client is settled on the new realm.</summary>
    public void Settle() {
        if (state == ClientTransferState.Switched) {
            Prepare = null;
            Rebase = TickRebase.None;
            state = ClientTransferState.Settled;
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"client transfer: {state}, {PredictionResets} reset(s)");
}

/// <summary>Two realms' clocks, and the offset between them.</summary>
/// <remarks>
///     <para>
///         Doc 27 § Tick rebasing: A and B run independent clocks and the two are not related. This
///         is the whole of the relationship — an offset, measured during the overlap, applied at the
///         switch.
///     </para>
///     <para>
///         ⚠ <b>What cannot be carried over is stated rather than hidden:</b> the prediction history
///         is cleared, the input log is cleared and re-armed from the target's first snapshot, and
///         the snapshot buffers are dropped. So the visible cost is one interpolation delay of extra
///         smoothing and one prediction reset — roughly 100–150 ms of softer local response, once, at
///         a moment the player initiated.
///     </para>
/// </remarks>
/// <param name="SourceTick">Where the source was.</param>
/// <param name="TargetTick">Where the target was at the same moment.</param>
public readonly record struct TickRebase(long SourceTick, long TargetTick) {
    /// <summary>No rebase.</summary>
    public static TickRebase None => default;

    /// <summary>What to add to a source tick to get a target tick.</summary>
    public long Offset => TargetTick - SourceTick;

    /// <summary>Whether there is one.</summary>
    public bool IsValid => SourceTick != 0 || TargetTick != 0;

    /// <summary>Converts a tick on the source's clock to the target's.</summary>
    /// <param name="tick">The source tick.</param>
    /// <returns>The same moment, as the target counts.</returns>
    public long ToTarget(long tick) => tick + Offset;

    /// <inheritdoc />
    public override string ToString() =>
        IsValid
            ? string.Create(CultureInfo.InvariantCulture, $"tick {SourceTick} → {TargetTick} (offset {Offset:+#;-#;0})")
            : "no rebase";
}
