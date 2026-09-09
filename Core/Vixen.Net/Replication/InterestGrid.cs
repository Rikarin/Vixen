// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Motion;
using Vixen.Net.Sessions;

namespace Vixen.Net.Replication;

/// <summary>What is near enough to each player to be worth telling them about.</summary>
/// <remarks>
///     <para>
///         <b>A source rather than a rule, and that is the entire point.</b> Written as a rule it
///         would be asked "is this within range" once per object per player — ten thousand objects and
///         two hundred players is two million distance tests a tick, which is the cost the feature
///         exists to remove. As a source it buckets the world once and then answers each player from
///         the cells around them.
///     </para>
///     <para>
///         <b>It leaves with hysteresis, and that is not a polish detail.</b> Leaving interest and
///         being destroyed are deliberately the same thing to a client, so an object hovering at the
///         boundary does not flicker — it is destroyed and recreated, on every tick, complete with
///         whatever the game hangs off a spawn. So an object already being observed stays observed
///         until it passes <see cref="Radius" /> <i>plus</i> <see cref="Hysteresis" />, and the band
///         between the two is what a player walking a boundary spends their time in.
///     </para>
///     <para>
///         <b>An object with no position is told to everybody.</b> A match timer, a scoreboard, a
///         team's shared state: a distance rule has nothing to say about a thing that is not
///         anywhere, and the alternative reading — no position, no interest — makes those vanish for
///         reasons nobody can see. Same argument the scene rule makes about an object in no scene.
///     </para>
///     <para>
///         <b>A player whose position nobody has set sees everything</b>, and it is counted. They are
///         loading, or spectating, or the game has not wired <see cref="SetViewpoint" /> up yet — and
///         of the two ways to be wrong, showing too much is the one that gets noticed.
///     </para>
/// </remarks>
public sealed class InterestGrid : IInterestSource {
    static readonly QueryDescription Networked = new QueryDescription().RequireAll([ComponentType<NetworkId>.Id]);

    readonly Dictionary<long, List<Entity>> cells = [];
    readonly List<Entity> unpositioned = [];
    readonly Dictionary<uint, Vector3> viewpoints = [];
    readonly Dictionary<uint, HashSet<uint>> observing = [];
    readonly HashSet<uint> current = [];

    // The layers the last rebuild put anything in — the set rather than its extent, because the
    // extent is what one outlier destroys. `layers` is `occupied` sorted, so a query can walk the
    // part of it inside its window with one binary search.
    readonly HashSet<int> occupied = [];
    readonly List<int> layers = [];

    // The horizontal extent of the cells the last rebuild put anything in, in cell coordinates.
    // Empty until a rebuild, and deliberately inverted when nothing is positioned so a query's loop
    // bounds cross and it walks nothing at all.
    //
    // ⚠ Only x and z. A min/max pair is the right shape for an axis a world is genuinely spread
    // along — it is one number per end and the query is dense in it. It is the wrong shape for the
    // axis a world is *flat* along, which is the one the clamp was bought for: see `layers`.
    (int X, int Z) low = (int.MaxValue, int.MaxValue);
    (int X, int Z) high = (int.MinValue, int.MinValue);

    /// <summary>How large a cell is, in world units.</summary>
    /// <remarks>
    ///     Wants to be about the radius, not much smaller. Too small and a query walks hundreds of
    ///     nearly empty cells; too large and every query drags in a neighbourhood it then has to
    ///     reject one object at a time. A third of the radius means a query touches a handful of cells
    ///     and most of what it finds is genuinely close.
    /// </remarks>
    public float CellSize { get; init; } = 32f;

    /// <summary>How far a player is told about things.</summary>
    public float Radius { get; set; } = 96f;

    /// <summary>How much further something already being watched has to go before it is dropped.</summary>
    /// <remarks>
    ///     Ten percent of the radius or so. It costs a little bandwidth at the edge and it is what
    ///     stops a player standing on a boundary from making everything near them despawn and respawn
    ///     twenty times a second.
    /// </remarks>
    public float Hysteresis { get; set; } = 12f;

    /// <summary>How many cells hold anything.</summary>
    public int CellCount {
        get {
            var total = 0;

            foreach (var cell in cells.Values) {
                if (cell.Count > 0) {
                    total++;
                }
            }

            return total;
        }
    }

    /// <summary>How many objects were bucketed by the last rebuild.</summary>
    public int PositionedCount { get; private set; }

    /// <summary>How many had no position and go to everybody.</summary>
    public int UnpositionedCount => unpositioned.Count;

    /// <summary>How many distinct layers hold anything, after the last rebuild.</summary>
    /// <remarks>
    ///     <para>
    ///         The bound on the vertical half of a query's price, and the number that says why a tick
    ///         got slower. A query walks at most <c>min(OccupiedLayerCount, 2 · span + 1)</c> layers,
    ///         so a world standing on the ground reads one here whatever else is in it, and a world
    ///         with real floors reads the number of floors.
    ///     </para>
    ///     <para>
    ///         ⚠ It counts layers, not their <i>extent</i>. That distinction is the whole of #1144:
    ///         an earlier version of this grid clamped a query to the min/max band of occupied
    ///         layers, and one entity parked at <c>y = 1000</c> — a flying camera, a projectile, a
    ///         transform left on a sentinel — made that band thirty-two cells deep and handed every
    ///         connection the whole cube back. A set has no such outlier: the stray layer is one
    ///         entry, it is outside every window that does not contain it, and it costs one probe to
    ///         the queries it does reach.
    ///     </para>
    /// </remarks>
    public int OccupiedLayerCount => layers.Count;

    /// <summary>Cells looked up by every query since this grid was made.</summary>
    /// <remarks>
    ///     <para>
    ///         The grid's cost, expressed as work rather than as milliseconds — a query's price is one
    ///         dictionary probe per cell it walks plus a distance test per object it finds, and this is
    ///         the first half. Divide by ticks and connections to get the per-query figure, which is
    ///         what a soak wants to print.
    ///     </para>
    ///     <para>
    ///         ⚠ It is <b>not</b> <c>(2 · span + 1)³</c>: a query walks only the part of its window
    ///         that the last rebuild put something in, so a flat world costs one layer rather than
    ///         nine. The bound is
    ///         <c>(2 · span + 1)² · min(<see cref="OccupiedLayerCount" />, 2 · span + 1)</c>, and it
    ///         holds however far apart the occupied layers are.
    ///     </para>
    /// </remarks>
    public long ProbedCellCount { get; private set; }

    /// <summary>Queries answered for a player whose viewpoint nobody had set.</summary>
    public long ViewpointlessCount { get; private set; }

    /// <summary>Says where a player is looking from.</summary>
    /// <param name="player">Who.</param>
    /// <param name="at">Where. Their avatar, or their camera for a spectator.</param>
    public void SetViewpoint(PlayerId player, in Vector3 at) => viewpoints[player.Value] = at;

    /// <summary>Forgets a player who has gone.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they were known.</returns>
    public bool Forget(PlayerId player) {
        observing.Remove(player.Value);

        return viewpoints.Remove(player.Value);
    }

    /// <summary>Buckets the world. Once a tick, before any player is resolved.</summary>
    /// <param name="world">The server's world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     <b>Rebuilt rather than maintained.</b> An incremental grid has to be told when anything
    ///     moves, which means either every mover knowing about the grid or a change-detection pass
    ///     that costs what the rebuild costs. This is one sweep of the networked entities per tick,
    ///     shared by every player, and it is the pass whose whole purpose is to not be done per
    ///     player.
    /// </remarks>
    public void Rebuild(World world) {
        ArgumentNullException.ThrowIfNull(world);

        // Cleared rather than dropped: a cell that emptied this tick is very likely to fill again
        // next tick, and keeping the list keeps the steady state free of allocation.
        foreach (var cell in cells.Values) {
            cell.Clear();
        }

        unpositioned.Clear();
        occupied.Clear();
        layers.Clear();
        PositionedCount = 0;
        low = (int.MaxValue, int.MaxValue);
        high = (int.MinValue, int.MinValue);

        foreach (var chunk in world.Chunks(Networked)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!world.TryGet<NetworkTransform>(entities[index], out var transform)) {
                    unpositioned.Add(entities[index]);

                    continue;
                }

                var at = Coordinates(transform.Position);

                Cell(Pack(at.X, at.Y, at.Z)).Add(entities[index]);
                PositionedCount++;
                occupied.Add(at.Y);

                low = (Math.Min(low.X, at.X), Math.Min(low.Z, at.Z));
                high = (Math.Max(high.X, at.X), Math.Max(high.Z, at.Z));
            }
        }

        // Sorted once a tick and then read by every connection, which is the trade the whole grid is
        // built on. A world has as many layers as it has floors, so this sorts single digits.
        layers.AddRange(occupied);
        layers.Sort();
    }

    /// <inheritdoc />
    public void Candidates(World world, PlayerId player, List<Entity> into) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(into);

        into.AddRange(unpositioned);

        if (!viewpoints.TryGetValue(player.Value, out var eye)) {
            ViewpointlessCount++;

            foreach (var cell in cells.Values) {
                into.AddRange(cell);
            }

            return;
        }

        if (!observing.TryGetValue(player.Value, out var held)) {
            held = [];
            observing[player.Value] = held;
        }

        current.Clear();

        // The band an object already being watched is allowed to stray into before it is dropped, so
        // the query has to reach that far even though most of what it finds there will be refused.
        var reach = Radius + Hysteresis;
        var span = (int)MathF.Ceiling(reach / CellSize);
        var centre = Coordinates(eye);
        var near = Radius * Radius;
        var far = reach * reach;

        // ⚠ The window is intersected with what the rebuild actually filled, because a world is wide
        // and flat and the window is a cube. At the cell size these remarks recommend the span is
        // four, so walking the whole window is 729 probes per connection per tick and every object
        // standing on the ground puts 648 of them in layers nothing is in. The clamp is not a
        // decision to drop the third dimension — a world with stacked floors still pays for the
        // floors it has, and only for those.
        var lowX = Math.Max(centre.X - span, low.X);
        var highX = Math.Min(centre.X + span, high.X);
        var lowZ = Math.Max(centre.Z - span, low.Z);
        var highZ = Math.Min(centre.Z + span, high.Z);

        // ⚠ And the vertical axis is intersected against the *set* of occupied layers, not against
        // their extent (#1144). An extent is one outlier away from being the whole window again: a
        // single entity at y = 1000 in an otherwise flat world made the band thirty-two cells deep
        // and gave every connection back the cube the clamp had just removed, with nothing in the
        // grid reporting why the tick had trebled. Over the set, that entity is one layer, it is
        // outside this window unless the eye is up there with it, and the price of finding that out
        // is one binary search.
        var first = FirstLayerFrom(centre.Y - span);
        var lastY = centre.Y + span;

        for (var x = lowX; x <= highX; x++) {
            for (var index = first; index < layers.Count && layers[index] <= lastY; index++) {
                var y = layers[index];

                for (var z = lowZ; z <= highZ; z++) {
                    ProbedCellCount++;

                    if (!cells.TryGetValue(Pack(x, y, z), out var cell) || cell.Count == 0) {
                        continue;
                    }

                    foreach (var entity in cell) {
                        Consider(world, entity, eye, near, far, held, into);
                    }
                }
            }
        }

        held.Clear();

        foreach (var id in current) {
            held.Add(id);
        }
    }

    void Consider(
        World world,
        Entity entity,
        in Vector3 eye,
        float near,
        float far,
        HashSet<uint> held,
        List<Entity> into
    ) {
        if (!world.TryGet<NetworkId>(entity, out var id)) {
            return;
        }

        var distance = (world.Read<NetworkTransform>(entity).Position - eye).LengthSquared();

        // Two thresholds, and which one applies depends on whether they were already watching it.
        // One threshold is a boundary an object sits on, and sitting on this one costs a despawn and
        // a respawn every tick it wavers.
        var limit = held.Contains(id.Value) ? far : near;

        if (distance > limit) {
            return;
        }

        current.Add(id.Value);
        into.Add(entity);
    }

    // The index of the first occupied layer at or above `from`, which is `layers.Count` when there
    // is none — so a window entirely above everything walks nothing rather than clamping onto the
    // top layer and walking it.
    int FirstLayerFrom(int from) {
        var found = layers.BinarySearch(from);

        return found < 0 ? ~found : found;
    }

    List<Entity> Cell(long key) {
        if (!cells.TryGetValue(key, out var cell)) {
            cell = [];
            cells[key] = cell;
        }

        return cell;
    }

    (int X, int Y, int Z) Coordinates(in Vector3 position) =>
        ((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Y / CellSize),
            (int)MathF.Floor(position.Z / CellSize));

    /// <summary>Three cell coordinates in one key.</summary>
    /// <remarks>
    ///     Twenty-one bits each, which at a cell of thirty-two units is a world about sixty-seven
    ///     million units across in every direction. A coordinate outside that wraps into a
    ///     neighbouring key rather than failing, which is the right failure for a number no game
    ///     reaches: an object at the edge of a world nobody has built is told to somebody it should
    ///     not be, rather than throwing in the middle of a tick.
    /// </remarks>
    static long Pack(int x, int y, int z) =>
        ((long)(x & 0x1F_FFFF) << 42) | ((long)(y & 0x1F_FFFF) << 21) | (uint)(z & 0x1F_FFFF);
}
