// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Agents;

/// <summary>
///     A flat spatial hash over the XZ plane, rebuilt every frame, for "which agents are near this
///     one".
/// </summary>
/// <remarks>
///     <para>
///         Avoidance is the only thing that asks, and it asks once per agent per frame about a circle
///         a few radii wide. Comparing every agent to every other is fine at ten agents and quadratic
///         at two hundred; a grid whose cell is the query range turns it into a walk over nine cells.
///     </para>
///     <para>
///         Rebuilt rather than maintained, because every agent moves every frame and an incremental
///         structure would be re-inserting all of them anyway. The buckets keep their capacity across
///         frames, so a steady-state crowd allocates nothing.
///     </para>
/// </remarks>
public sealed class ProximityGrid {
    readonly Dictionary<long, List<int>> cells = [];
    readonly Stack<List<int>> spare = [];
    readonly float inverseCellSize;

    /// <summary>Creates a grid.</summary>
    /// <param name="cellSize">How wide a cell is. The query range is the size to use.</param>
    public ProximityGrid(float cellSize) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);

        CellSize = cellSize;
        inverseCellSize = 1f / cellSize;
    }

    /// <summary>How wide a cell is.</summary>
    public float CellSize { get; }

    /// <summary>Empties the grid, keeping the buckets for the next frame.</summary>
    /// <remarks>
    ///     The buckets go back to a pool rather than staying keyed to the cells they were in. A crowd
    ///     walking across a level occupies a roughly constant <i>number</i> of cells and a constantly
    ///     changing <i>set</i> of them, so keeping a bucket per visited cell allocates a list every
    ///     time somebody walks somewhere new — a slow drip that never quite stops, which is exactly
    ///     what a steady-state allocation budget is about.
    /// </remarks>
    public void Clear() {
        foreach (var bucket in cells.Values) {
            bucket.Clear();
            spare.Push(bucket);
        }

        cells.Clear();
    }

    /// <summary>Puts an item in the grid.</summary>
    /// <param name="item">Whatever the caller wants back — an agent index.</param>
    /// <param name="position">Where it is.</param>
    public void Add(int item, Vector3 position) {
        var key = Key(position);

        if (!cells.TryGetValue(key, out var bucket)) {
            bucket = spare.Count > 0 ? spare.Pop() : [];
            cells[key] = bucket;
        }

        bucket.Add(item);
    }

    /// <summary>Finds everything within a range of a point.</summary>
    /// <param name="position">The centre.</param>
    /// <param name="range">The radius. Anything beyond one cell of it may be missed.</param>
    /// <param name="results">Where to write the items found.</param>
    /// <returns>How many were written.</returns>
    public int Query(Vector3 position, float range, Span<int> results) {
        var count = 0;
        var minX = (int)MathF.Floor((position.X - range) * inverseCellSize);
        var maxX = (int)MathF.Floor((position.X + range) * inverseCellSize);
        var minZ = (int)MathF.Floor((position.Z - range) * inverseCellSize);
        var maxZ = (int)MathF.Floor((position.Z + range) * inverseCellSize);

        for (var z = minZ; z <= maxZ; z++) {
            for (var x = minX; x <= maxX; x++) {
                if (!cells.TryGetValue(((long)x << 32) | (uint)z, out var bucket)) {
                    continue;
                }

                foreach (var item in bucket) {
                    if (count == results.Length) {
                        return count;
                    }

                    results[count++] = item;
                }
            }
        }

        return count;
    }

    long Key(Vector3 position) {
        var x = (int)MathF.Floor(position.X * inverseCellSize);
        var z = (int)MathF.Floor(position.Z * inverseCellSize);

        return ((long)x << 32) | (uint)z;
    }
}
