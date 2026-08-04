// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live;

/// <summary>How a realm process comes into existence, whatever is running underneath.</summary>
/// <remarks>
///     <para>
///         ADR-019. Three backends — <c>Kubernetes</c>, <c>Docker</c>, <c>Process</c> — probed in
///         that order at startup, with the first that answers winning and configuration able to
///         override. The interface is small on purpose: everything above it reasons about shards, and
///         the only thing it needs from the world below is that a process with a given
///         <see cref="RealmSpec" /> exists, can be told to stop, and can be watched.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is on a frame path</b>, which is why every method is asynchronous
///         without apology. Starting a pod is seconds; ADR-016's rule about never awaiting the
///         control plane from a tick is what keeps that acceptable.
///     </para>
///     <para>
///         <c>Placement.Process</c> is to this document what <c>Vixen.Net.Transport.Local</c> is to
///         doc 16 — the backend that makes the whole system an ordinary unit test rather than an
///         integration environment. A test that needs eight realms starts eight processes on a
///         laptop, deterministically, and asserts against the same interface production uses.
///     </para>
/// </remarks>
public interface IRealmPlacement {
    /// <summary>Whether this backend can run here, and what it found.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What it decided.</returns>
    /// <remarks>
    ///     Cheap and side-effect free: an in-cluster service-account token, a socket that answers, a
    ///     directory that exists. The orchestrator calls this on every backend at startup and uses
    ///     the first that says yes, so a probe that started something would start it three times.
    /// </remarks>
    ValueTask<PlacementProbe> ProbeAsync(CancellationToken cancellation);

    /// <summary>Brings a realm up.</summary>
    /// <param name="spec">What the realm is to be. An unbound endpoint is bound by the backend.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The instance, as soon as it exists — <em>not</em> as soon as it is ready.</returns>
    /// <remarks>
    ///     ⚠ <b>Returning is <c>Starting</c>, not <c>Ready</c>.</b> The shard becomes a placement
    ///     candidate when the realm itself says it has loaded its map and can accept a session, which
    ///     arrives as a <see cref="PlacementEventKind.Ready" /> on <see cref="WatchAsync" />. A
    ///     backend that blocked until then would make a slow map load look like a failed start.
    /// </remarks>
    ValueTask<RealmInstance> StartAsync(RealmSpec spec, CancellationToken cancellation);

    /// <summary>Takes a realm down.</summary>
    /// <param name="instance">Which one.</param>
    /// <param name="mode">Politely, or now.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When the request has been made — not when the process has gone.</returns>
    /// <remarks>
    ///     Stopping an instance that is already gone is not an error. Every backend races with the
    ///     process it is managing, and a caller that had to distinguish "it was not there" from "it
    ///     would not stop" would write the same retry loop three times.
    /// </remarks>
    ValueTask StopAsync(RealmInstanceId instance, StopMode mode, CancellationToken cancellation);

    /// <summary>What this backend believes is running.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Every instance it knows about.</returns>
    /// <remarks>
    ///     The reconciliation half. An orchestrator that restarts has grain state saying which shards
    ///     should exist and no memory of which processes do; this is the other side of that
    ///     comparison, and it is why a Kubernetes realm is an owner-referenced <c>Pod</c> with a
    ///     label rather than an anonymous one.
    /// </remarks>
    ValueTask<IReadOnlyList<RealmInstance>> ListAsync(CancellationToken cancellation);

    /// <summary>Everything that happens to instances, as it happens.</summary>
    /// <param name="cancellation">Ends the stream.</param>
    /// <returns>Started, ready, stopped and lost, in order.</returns>
    IAsyncEnumerable<PlacementEvent> WatchAsync(CancellationToken cancellation);
}

/// <summary>What a backend found when it looked.</summary>
/// <param name="Available">Whether it can start realms here.</param>
/// <param name="Backend">Which backend answered — <c>kubernetes</c>, <c>docker</c>, <c>process</c>.</param>
/// <param name="Detail">
///     What it saw, for a log: the cluster it reached, the socket it opened, or why it could not.
/// </param>
/// <remarks>
///     ⚠ <b>The detail is not optional even when <paramref name="Available" /> is true.</b> "Docker
///     answered" and "Docker answered on a socket in a devcontainer that is not the host's daemon"
///     are the same boolean and different afternoons.
/// </remarks>
public readonly record struct PlacementProbe(bool Available, string Backend, string Detail) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Backend}: {(Available ? "available" : "unavailable")} — {Detail}"
        );
}

/// <summary>A realm process a backend created.</summary>
/// <remarks>
///     What the backend knows, which is less than what the shard knows: an identity it can act on, a
///     shard the process was told to be, and where clients reach it. Population, tick times and
///     readiness belong to the shard's own heartbeat and never to this.
/// </remarks>
/// <param name="Id">The backend's handle.</param>
/// <param name="Shard">The shard it was started for.</param>
/// <param name="Endpoint">Where clients reach it, bound.</param>
/// <param name="Backend">Which backend owns it.</param>
/// <param name="StartedAt">When it was created.</param>
public readonly record struct RealmInstance(
    RealmInstanceId Id,
    ShardId Shard,
    RealmEndpoint Endpoint,
    string Backend,
    DateTimeOffset StartedAt
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Backend} instance {Id} carrying {Shard} at {Endpoint}");
}

/// <summary>How much of a hurry a stop is in.</summary>
public enum StopMode : byte {
    /// <summary>
    ///     Tell it to drain and wait. Players are moved at safe moments and the process exits when
    ///     the last one has gone — which may be minutes, and is the whole point of § Drain.
    /// </summary>
    Drain = 0,

    /// <summary>
    ///     Kill it. Everything volatile in it is lost, which for a realm means the fight in progress;
    ///     durable state is not at risk because it was never in the process (ADR-021).
    /// </summary>
    Immediate = 1
}

/// <summary>What happened to an instance.</summary>
public enum PlacementEventKind : byte {
    /// <summary>The backend created it. It is not a placement candidate yet.</summary>
    Started = 0,

    /// <summary>The realm reported that it has loaded its map and is accepting sessions.</summary>
    Ready = 1,

    /// <summary>It exited the way it was asked to.</summary>
    Stopped = 2,

    /// <summary>
    ///     It went away without being asked. Doc 27 § Health: recovery is a placement, not a
    ///     resurrection — the shard is gone and its volatile state with it.
    /// </summary>
    Lost = 3
}

/// <summary>One thing that happened to one instance.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Instance">To which instance.</param>
/// <param name="Shard">Which shard it was carrying.</param>
/// <param name="Endpoint">Where it is, on <see cref="PlacementEventKind.Ready" />.</param>
/// <param name="Detail">What the backend saw — an exit code, a signal, a reason.</param>
public readonly record struct PlacementEvent(
    PlacementEventKind Kind,
    RealmInstanceId Instance,
    ShardId Shard,
    RealmEndpoint Endpoint,
    string Detail
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Instance} ({Shard}) {Kind}: {Detail}");
}
