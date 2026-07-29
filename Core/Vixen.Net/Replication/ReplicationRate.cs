// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Motion;
using Vixen.Net.Sessions;

namespace Vixen.Net.Replication;

/// <summary>How often one object's state is worth sending to one connection.</summary>
/// <remarks>
///     <para>
///         <b>Separate from interest, and the separation is a correction to
///         [16](../../../docs/plan/16-networking.md).</b> That document lists the resolvers as "scene
///         scope → explicit visibility overrides → distance grid → LOD rate reduction", which reads
///         as four filters in a chain. The last one is not a filter, and building it as one produces a
///         bug that looks like the feature working.
///     </para>
///     <para>
///         The reason is <c>ReplicationServer</c>'s own design: leaving the observed set means "drop
///         this object", because destruction and walking over the horizon are deliberately the same
///         mechanism to a client. So an object omitted from the set to skip a tick is an object
///         <i>destroyed and recreated</i> on that tick, together with whatever the game hangs off a
///         spawn. Rate has to be decided where the records are written, where skipping one already
///         means "not this tick" — it is the same thing the bandwidth budget does when it sheds, and
///         it takes the same path out: not acknowledged, so it goes in the next snapshot.
///     </para>
/// </remarks>
public interface IReplicationRate {
    /// <summary>Whether this object's state is worth writing to this connection on this tick.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">Who the snapshot is for.</param>
    /// <param name="entity">The object.</param>
    /// <param name="id">Its networked id, which is what a divider should be keyed by.</param>
    /// <param name="tick">The tick being written.</param>
    /// <returns>Whether to write it.</returns>
    bool ShouldSend(World world, PlayerId player, Entity entity, NetworkId id, Tick tick);
}

/// <summary>Sends distant objects less often.</summary>
/// <remarks>
///     <para>
///         What [16](../../../docs/plan/16-networking.md) means by <c>NetworkLOD</c>, and it is the
///         cheapest large saving in the system: a player who can see two hundred objects is usually
///         within arm's reach of three of them. Bands rather than a curve, because the thing being
///         chosen is "one tick in how many" and that is an integer.
///     </para>
///     <para>
///         <b>The phase is the object's id, not a shared counter.</b> A divider that sent every
///         distant object on the same tick would produce a snapshot that is tiny four ticks out of
///         five and enormous on the fifth — the same total bandwidth arriving in a shape that defeats
///         the budget and the path MTU at once. Spreading them by id costs an integer remainder and
///         makes the load flat.
///     </para>
///     <para>
///         <b>It is a rate, not a filter, so nothing here can make an object disappear.</b> An object
///         skipped this tick is one whose newest value the connection has not acknowledged, so it goes
///         in the next snapshot it is due in — which is the same thing that happens to a record the
///         budget shed.
///     </para>
/// </remarks>
public sealed class DistanceReplicationRate : IReplicationRate {
    readonly Dictionary<uint, Vector3> viewpoints = [];

    /// <summary>The bands, as (distance, one tick in how many), nearest first.</summary>
    /// <remarks>
    ///     The default says: everything inside thirty units every tick, out to eighty every other
    ///     tick, out to two hundred one tick in four, and anything further one in eight. Past the
    ///     last band the divider is the last band's — the interest grid is what decides that
    ///     something is too far to send at all, and doing it twice in two places is how the two come
    ///     to disagree.
    /// </remarks>
    public IList<(float Distance, int Divider)> Bands { get; } = [(30f, 1), (80f, 2), (200f, 4), (float.MaxValue, 8)];

    /// <summary>Records skipped because they were not due.</summary>
    public long SkippedCount { get; private set; }

    /// <summary>Records written.</summary>
    public long SentCount { get; private set; }

    /// <summary>Says where a player is looking from.</summary>
    /// <param name="player">Who.</param>
    /// <param name="at">Where.</param>
    public void SetViewpoint(PlayerId player, in Vector3 at) => viewpoints[player.Value] = at;

    /// <summary>Forgets a player who has gone.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were known.</returns>
    public bool Forget(PlayerId player) => viewpoints.Remove(player.Value);

    /// <inheritdoc />
    public bool ShouldSend(World world, PlayerId player, Entity entity, NetworkId id, Tick tick) {
        ArgumentNullException.ThrowIfNull(world);

        var divider = DividerFor(world, player, entity);

        // Phase by id, so the distant objects are spread across the ticks rather than arriving
        // together every eighth one.
        if (divider <= 1 || (tick.Value + id.Value) % (uint)divider == 0) {
            SentCount++;

            return true;
        }

        SkippedCount++;

        return false;
    }

    int DividerFor(World world, PlayerId player, Entity entity) {
        // No viewpoint or no position is full rate. Both are the case where this has nothing to say,
        // and the safe answer to "how often" is "as often as anything else" — a scoreboard that
        // updated one tick in eight because it is not anywhere would be a strange thing to debug.
        if (!viewpoints.TryGetValue(player.Value, out var eye)
            || !world.TryGet<NetworkTransform>(entity, out var transform)) {
            return 1;
        }

        var distance = (transform.Position - eye).LengthSquared();

        foreach (var (limit, divider) in Bands) {
            if (distance <= limit * limit) {
                return divider;
            }
        }

        return Bands.Count > 0 ? Bands[^1].Divider : 1;
    }
}
