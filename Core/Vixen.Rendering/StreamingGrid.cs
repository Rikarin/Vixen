// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>Somewhere the world has to be loaded around.</summary>
/// <param name="Position">Where it is.</param>
/// <param name="Radius">How far around it the world must be resident, in metres.</param>
/// <remarks>
///     A camera is one; so is a player who is not the camera, a remote player on a listen server, a
///     spawn point being prepared, and a cinematic about to cut somewhere. Several rather than one,
///     because every engine that assumed one camera had to retrofit the rest, and the retrofit is
///     always the same shape as this.
/// </remarks>
public readonly record struct StreamingSource(Vector3 Position, float Radius);

/// <summary>
///     A regular grid of pages over the XZ plane, and the policy that decides which are resident.
/// </summary>
/// <remarks>
///     <para>
///         <b>The policy half of [docs/plan/31 § D13], and no more than that.</b>
///         <see cref="PageResidency" /> already knows how to load, place, refresh and evict a page,
///         and it deliberately does not interpret <see cref="PageKey.Source" /> — its own remarks say
///         the source is "assigned by whoever registered it" and "never interpreted". What was missing
///         was anything that decides <em>which</em> pages a frame wants. This is that decision for the
///         one shape a terrain has: a grid.
///     </para>
///     <para>
///         ⚠ <b>This is not world streaming, and the difference is worth being exact about.</b>
///         [docs/plan/31 § B6] records that there is no distance-based level streaming — no cells of
///         entities, no data layers, no HLOD — and declines to invent one. What is here makes the
///         <em>bytes</em> of a tiled thing stream: terrain heights, terrain weights, and anything else
///         that is a grid of blobs. A scene with ten thousand tree instances in it still loads all ten
///         thousand, because they are entities and this does not know what an entity is.
///     </para>
///     <para>
///         <b>A lead ring, because a page that arrives when it is needed arrives late.</b> Pages
///         within a source's radius are touched — they are being used, so they must not be the first
///         evicted — and pages within the radius plus <see cref="Lead" /> are requested. Without the
///         lead, a source moving at any speed crosses into a cell at the same moment the cell is asked
///         for, and the first frames of it are drawn from whatever coarser thing was resident.
///     </para>
///     <para>
///         <b>Distance to the cell, not to its centre.</b> A source standing in the corner of a large
///         cell is zero metres from that cell and one cell-width from its centre; measuring to the
///         centre makes the radius mean different things depending on where in a cell somebody
///         stands, which shows up as a load boundary that moves when the player strafes.
///     </para>
/// </remarks>
public sealed class StreamingGrid {
    readonly int source;
    readonly Vector2 origin;
    readonly float cellSize;
    readonly int countX;
    readonly int countZ;

    /// <summary>Describes a grid of pages.</summary>
    /// <param name="source">
    ///     Which <see cref="PageKey.Source" /> these pages belong to. One residency service can hold
    ///     several grids, and several other things besides.
    /// </param>
    /// <param name="origin">The low corner of cell (0, 0), in world XZ.</param>
    /// <param name="cellSize">How wide a cell is, in metres. Positive.</param>
    /// <param name="countX">How many cells along X. Positive.</param>
    /// <param name="countZ">How many cells along Z. Positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">The grid has no extent.</exception>
    public StreamingGrid(int source, Vector2 origin, float cellSize, int countX, int countZ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countZ);

        this.source = source;
        this.origin = origin;
        this.cellSize = cellSize;
        this.countX = countX;
        this.countZ = countZ;
    }

    /// <summary>Which page source these cells belong to.</summary>
    public int Source => source;

    /// <summary>How wide a cell is, in metres.</summary>
    public float CellSize => cellSize;

    /// <summary>How many cells along X.</summary>
    public int CountX => countX;

    /// <summary>How many cells along Z.</summary>
    public int CountZ => countZ;

    /// <summary>How many cells there are.</summary>
    public int CellCount => countX * countZ;

    /// <summary>
    ///     How far beyond a source's radius pages are requested but not yet used, in metres.
    /// </summary>
    /// <remarks>
    ///     One cell by default, which is the smallest lead that can ever help. A project whose sources
    ///     move quickly wants more, and the number that matters is <c>speed × time-to-load</c>.
    /// </remarks>
    public float Lead { get; set; } = -1f;

    /// <summary>How many cells the last <see cref="Update" /> touched.</summary>
    public int TouchedCells { get; private set; }

    /// <summary>How many cells the last <see cref="Update" /> requested.</summary>
    public int RequestedCells { get; private set; }

    /// <summary>The page a cell is.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>The key.</returns>
    public PageKey KeyOf(int x, int z) => new(source, (z * countX) + x);

    /// <summary>Which cell a page index is.</summary>
    /// <param name="index">The page's index within this grid.</param>
    /// <returns>The cell's X and Z indices.</returns>
    public (int X, int Z) CellOf(int index) => (index % countX, index / countX);

    /// <summary>Where a cell is, in world XZ.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>Its low and high corners.</returns>
    public (Vector2 Minimum, Vector2 Maximum) BoundsOf(int x, int z) {
        var low = origin + new Vector2(x * cellSize, z * cellSize);
        return (low, low + new Vector2(cellSize, cellSize));
    }

    /// <summary>The shortest distance from a place to a cell, in world XZ. Zero inside it.</summary>
    /// <param name="x">The cell's X index.</param>
    /// <param name="z">The cell's Z index.</param>
    /// <param name="position">The place.</param>
    /// <returns>The distance.</returns>
    public float DistanceTo(int x, int z, Vector3 position) {
        var (low, high) = BoundsOf(x, z);
        var point = new Vector2(position.X, position.Z);

        var dx = MathF.Max(0f, MathF.Max(low.X - point.X, point.X - high.X));
        var dz = MathF.Max(0f, MathF.Max(low.Y - point.Y, point.Y - high.Y));

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    ///     Tells a residency service what this frame's sources want.
    /// </summary>
    /// <param name="sources">Where the world has to be loaded around.</param>
    /// <param name="residency">The service. Nothing is loaded until it is serviced.</param>
    /// <returns>How many cells were touched, which is how many are in use.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>It never evicts anything, and that is the design rather than an omission.</b> A cell
    ///         that stops being wanted is simply not touched, and <see cref="PageResidency" />'s LRU
    ///         reclaims it when something else needs the room. Evicting on the way out would make the
    ///         pool empty itself whenever a source turned around, and refill it on the way back.
    ///     </para>
    ///     <para>
    ///         <b>Only the cells a source can reach are visited, not all of them.</b> The window is
    ///         derived from the source's radius, so a 4 km² terrain with a 200 m radius examines a few
    ///         dozen cells rather than sixteen thousand — which matters because this runs every frame
    ///         for every source.
    ///     </para>
    /// </remarks>
    public int Update(ReadOnlySpan<StreamingSource> sources, PageResidency residency) {
        ArgumentNullException.ThrowIfNull(residency);

        TouchedCells = 0;
        RequestedCells = 0;

        var lead = Lead < 0f ? cellSize : Lead;

        // Two sets rather than one pass, because a cell reached by two sources must be counted once
        // and because a cell inside one source's radius and merely near another's must be touched,
        // not just requested.
        var touched = new HashSet<int>();
        var requested = new HashSet<int>();

        foreach (var streamingSource in sources) {
            var radius = MathF.Max(0f, streamingSource.Radius);
            Visit(streamingSource, radius + lead, requested, touched, radius);
        }

        foreach (var index in requested) {
            residency.Request(new(source, index));
        }

        foreach (var index in touched) {
            var key = new PageKey(source, index);

            // Requested as well as touched: a page in use that has not arrived is still wanted, and
            // Request is idempotent for one that has.
            residency.Request(key);
            residency.Touch(key);
        }

        TouchedCells = touched.Count;
        RequestedCells = requested.Count;

        return TouchedCells;
    }

    void Visit(
        in StreamingSource streamingSource,
        float reach,
        HashSet<int> requested,
        HashSet<int> touched,
        float radius
    ) {
        var point = new Vector2(streamingSource.Position.X, streamingSource.Position.Z);

        var minX = Math.Max(0, (int)MathF.Floor((point.X - reach - origin.X) / cellSize));
        var maxX = Math.Min(countX - 1, (int)MathF.Floor((point.X + reach - origin.X) / cellSize));
        var minZ = Math.Max(0, (int)MathF.Floor((point.Y - reach - origin.Y) / cellSize));
        var maxZ = Math.Min(countZ - 1, (int)MathF.Floor((point.Y + reach - origin.Y) / cellSize));

        for (var z = minZ; z <= maxZ; z++) {
            for (var x = minX; x <= maxX; x++) {
                var distance = DistanceTo(x, z, streamingSource.Position);

                if (distance > reach) {
                    continue;
                }

                var index = (z * countX) + x;

                if (distance <= radius) {
                    touched.Add(index);
                    requested.Remove(index);
                } else if (!touched.Contains(index)) {
                    requested.Add(index);
                }
            }
        }
    }
}
