// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Live.Transfer;

/// <summary>Where a transfer has got to. Doc 27 § The overlap's seven timestamps, as states.</summary>
/// <remarks>
///     ⚠ <b>The player is playing on the source realm for every state up to and including
///     <see cref="Committing" />.</b> That is the whole design: the second session is opened, the map
///     is loaded and the first snapshot arrives while they are still shooting on the first realm, so
///     the cost of a map change is a preload rather than a reconnect.
/// </remarks>
public enum TransferPhase : byte {
    /// <summary>Not transferring.</summary>
    Idle = 0,

    /// <summary>t0–t1. The orchestrator is choosing a shard.</summary>
    Placing = 1,

    /// <summary>t2. The client has been told where to go and what to carry.</summary>
    Preparing = 2,

    /// <summary>t3. The client has a session to the target and is loading. Still playing here.</summary>
    Overlapping = 3,

    /// <summary>t4. The client is ready and the lease is being moved. The atomic moment.</summary>
    Committing = 4,

    /// <summary>t5. The volatile state has been sent and an acknowledgement is owed.</summary>
    HandingOff = 5,

    /// <summary>t6. Done. The player is on the target and the source has despawned them.</summary>
    Committed = 6,

    /// <summary>It did not happen, and the player is still here. See <see cref="TransferAbort" />.</summary>
    Aborted = 7
}

/// <summary>Why a transfer did not happen.</summary>
/// <remarks>
///     ⚠ <b>Every value here leaves the player where they already were, which is always a valid
///     state.</b> Doc 27 calls that asymmetry deliberate and it is the property the whole failure
///     story rests on: the source never commits until the target has acknowledged, so an abort is a
///     transfer that did not start rather than one that half-finished.
/// </remarks>
public enum TransferAbort : byte {
    /// <summary>Nothing went wrong.</summary>
    None = 0,

    /// <summary>The orchestrator had nowhere to put them.</summary>
    NoShard = 1,

    /// <summary>The target never reported ready inside the deadline.</summary>
    TargetNeverReady = 2,

    /// <summary>The client never opened its second session inside the deadline.</summary>
    ClientNeverArrived = 3,

    /// <summary>The ticket aged out before the client redeemed it.</summary>
    TicketExpired = 4,

    /// <summary>The target never acknowledged the handoff.</summary>
    HandoffLost = 5,

    /// <summary>The player's lease was taken by somebody else mid-transfer.</summary>
    /// <remarks>
    ///     A third realm acquiring the lease means this transfer's epoch is already superseded, and
    ///     ADR-021's fence would refuse every durable write it made. Aborting is not a courtesy — it
    ///     is the only thing left that is correct.
    /// </remarks>
    LeaseLost = 6,

    /// <summary>The player disconnected while it was in flight.</summary>
    PlayerGone = 7,

    /// <summary>Something asked for it to stop. A drain that was cancelled, an operator, a test.</summary>
    Cancelled = 8
}

/// <summary>What the source realm tells the client so it can open its second session.</summary>
/// <param name="Ticket">The encoded <see cref="TransferTicket" />. Opaque; the client is a courier.</param>
/// <param name="Endpoint">Where the target is.</param>
/// <param name="Shard">Which shard, so the client can say which one it reached.</param>
/// <param name="Content">
///     What the target is running. The client checks this before it connects — arriving with the
///     wrong content is a handshake rejection, and finding that out here costs nothing.
/// </param>
/// <param name="TargetTick">
///     The target's tick estimate, so the client can pre-sync during the overlap rather than resync
///     after the switch.
/// </param>
public sealed record TransferPrepare(
    string Ticket,
    RealmEndpoint Endpoint,
    ShardId Shard,
    RealmVersion Content,
    long TargetTick
);

/// <summary>What the source tells the client at t5: switch at this tick.</summary>
/// <param name="AtTick">The source tick the player stops existing here.</param>
/// <param name="Shard">Which shard to switch to, so a stale commit cannot move somebody twice.</param>
public sealed record TransferCommit(long AtTick, ShardId Shard);

/// <summary>
///     The volatile simulation state, moved from one realm to another. <b>Never the inventory.</b>
/// </summary>
/// <remarks>
///     <para>
///         ADR-021: durable state moves by lease and stays in the database. What travels here is
///         position, velocity, buffs with their remaining durations, cooldowns, combat state, the
///         animation graph's position — everything whose loss would be a visible glitch and whose
///         duplication would be worth nothing.
///     </para>
///     <para>
///         ⚠ <b>If this payload is lost, nothing durable is at risk.</b> That is the property that
///         makes an abort cheap, and it is why the split between this and the lease is the single
///         most load-bearing decision in the document.
///     </para>
/// </remarks>
/// <param name="Player">Whose.</param>
/// <param name="LeaseEpoch">The epoch the target has taken. Every durable write it makes names this.</param>
/// <param name="AtTick">The source tick the state was sampled at.</param>
/// <param name="Components">The encoded components, as the replication codec wrote them.</param>
public sealed record RealmHandoff(
    PlayerKey Player,
    long LeaseEpoch,
    long AtTick,
    ImmutableArray<byte> Components
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"handoff of {Player} at epoch {LeaseEpoch}, tick {AtTick}, {(Components.IsDefault ? 0 : Components.Length)} bytes"
        );
}

/// <summary>How long each step of a transfer may take before it is given up on.</summary>
/// <remarks>
///     ⚠ <b>Every one of these is a deadline on the <em>source</em>, and that is the design.</b> The
///     source is the realm that still owns the player, so it is the only one that can decide nothing
///     happened. A deadline on the target would be a decision made by the realm that does not yet
///     have the authority to make it.
/// </remarks>
public sealed record TransferDeadlines {
    /// <summary>How long the orchestrator gets to name a shard.</summary>
    public TimeSpan Placing { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the target gets to become ready and pre-reserve.</summary>
    public TimeSpan Preparing { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the client gets to open its second session, load the map and report ready.
    /// </summary>
    /// <remarks>
    ///     The generous one, and deliberately: this window is a content download on a first visit.
    ///     Doc 27 § The overlap calls t3 "a progress bar that runs while the player is still walking
    ///     around", and cutting it short turns a slow connection into a failed map change.
    /// </remarks>
    public TimeSpan Overlapping { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long the lease move gets.</summary>
    public TimeSpan Committing { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the target gets to acknowledge the payload.</summary>
    public TimeSpan HandingOff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The deadline for a phase, or <see cref="TimeSpan.Zero" /> for one that has none.</summary>
    /// <param name="phase">Which phase.</param>
    /// <returns>How long it may last.</returns>
    public TimeSpan For(TransferPhase phase) =>
        phase switch {
            TransferPhase.Placing => Placing,
            TransferPhase.Preparing => Preparing,
            TransferPhase.Overlapping => Overlapping,
            TransferPhase.Committing => Committing,
            TransferPhase.HandingOff => HandingOff,
            _ => TimeSpan.Zero
        };
}
