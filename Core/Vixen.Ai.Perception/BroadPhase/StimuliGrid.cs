// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ai.Perception;

/// <summary>Which stimuli sources are near a point, without asking all of them.</summary>
/// <remarks>
///     <para>
///         The first of doc 37 § D15's three bounds. Sight is O(listeners × sources) and the schedule
///         is the whole design: five hundred listeners against five hundred sources is a quarter of a
///         million radius tests a pass, and the numbers a game ships are larger than that in both
///         directions at once.
///     </para>
///     <para>
///         <b>A uniform grid rather than a tree, and rebuilt rather than updated.</b> Every source
///         moves every frame — they are characters — so the incremental update a tree needs to be
///         worth its structure is the case that never happens. Rebuilding is one pass over an array
///         that is already in hand, and it costs a bucket write per source.
///     </para>
///     <para>
///         ⚠ <b>Two-dimensional, over X and Z.</b> A level is mostly a floor: cells over the vertical
///         axis as well would triple the number of cells a query walks — a 25-metre radius spans three
///         cells in Y that almost always hold the same one occupant — for a level where every agent is
///         within a few metres of the same height. Height still counts, because the distance test
///         below is in three dimensions; what a tall level costs is a longer chain in one cell, not a
///         wrong answer. Crowd systems make the same trade and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>Not the physics broad phase, and that is a deliberate difference from what doc 37
///         § D15 says.</b> A stimuli source is not necessarily a body: a noise, a security camera, a
///         scripted marker and a corpse are all perceivable and none of them has a collider. Querying
///         Jolt would find bodies, which then have to be mapped back to entities and filtered down to
///         the ones that are actually sources — a broad phase over the wrong set, whose cost is the
///         level's collision geometry rather than the handful of things worth looking for. The
///         physics world is still where the <i>occlusion trace</i> goes, which is the expensive half.
///     </para>
/// </remarks>
public sealed class StimuliGrid {
    // A chain per cell: heads[cell] is an index into the caller's array and next[i] is the one after
    // it. Two integers per source and no per-cell list, which is what keeps a rebuild from allocating
    // once the arrays have stopped growing.
    readonly Dictionary<long, int> heads = [];
    int[] next = [];
    Vector3[] points = [];

    /// <summary>How many sources are in it.</summary>
    public int Count { get; private set; }

    /// <summary>How wide a cell is, in metres.</summary>
    public float CellSize { get; private set; } = 8f;

    /// <summary>How many cells have anything in them.</summary>
    public int OccupiedCells => heads.Count;

    /// <summary>Puts a set of positions in.</summary>
    /// <param name="positions">Where the sources are. Indices into this are what a query returns.</param>
    /// <param name="cellSize">How wide a cell should be, in metres. Ignored below a centimetre.</param>
    /// <remarks>
    ///     ⚠ <b>The cell size wants to be about the query radius, not about the level.</b> Much
    ///     smaller and a query walks hundreds of empty cells; much larger and every query returns most
    ///     of the level. <see cref="Ecs.PerceptionSystem" /> picks it from the widest sense in play.
    /// </remarks>
    public void Build(ReadOnlySpan<Vector3> positions, float cellSize) {
        heads.Clear();
        Count = positions.Length;

        if (cellSize > 0.01f) {
            CellSize = cellSize;
        }

        if (next.Length < positions.Length) {
            var size = Math.Max(64, positions.Length);

            next = new int[size];
            points = new Vector3[size];
        }

        for (var index = 0; index < positions.Length; index++) {
            var key = KeyOf(positions[index]);

            points[index] = positions[index];
            next[index] = heads.TryGetValue(key, out var head) ? head : -1;
            heads[key] = index;
        }
    }

    /// <summary>Everything within a radius of a point.</summary>
    /// <param name="centre">Where to look.</param>
    /// <param name="radius">How far, in metres.</param>
    /// <param name="results">Where to put the indices. Cleared first.</param>
    /// <param name="cells">How many cells were walked.</param>
    /// <returns>
    ///     How many sources were distance-tested. ⚠ <b>This is the number the broad phase exists to
    ///     make small</b>, and it is what P3's exit criterion compares against the scan it replaces —
    ///     so it is returned rather than logged.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="results" /> is null.</exception>
    /// <remarks>
    ///     The distance test is done here rather than left to the caller, so what comes back is inside
    ///     the sphere and not merely inside its cells. A caller that got the cell contents would
    ///     repeat the test anyway and would be handed the corners of the box for free.
    /// </remarks>
    public int Query(Vector3 centre, float radius, List<int> results, out int cells) {
        ArgumentNullException.ThrowIfNull(results);

        results.Clear();
        cells = 0;

        if (Count == 0 || radius <= 0f) {
            return 0;
        }

        var inverse = 1f / CellSize;
        var minimum = Cell(centre - new Vector3(radius, radius, radius), inverse);
        var maximum = Cell(centre + new Vector3(radius, radius, radius), inverse);
        var squared = radius * radius;
        var examined = 0;

        for (var x = minimum.X; x <= maximum.X; x++) {
            for (var z = minimum.Z; z <= maximum.Z; z++) {
                cells++;

                if (!heads.TryGetValue(Key(x, z), out var index)) {
                    continue;
                }

                while (index >= 0) {
                    examined++;

                    // In three dimensions, even though the cells are in two: a source on the floor
                    // above is in the same cell and is not within twenty metres of anybody.
                    if ((points[index] - centre).LengthSquared() <= squared) {
                        results.Add(index);
                    }

                    index = next[index];
                }
            }
        }

        return examined;
    }

    /// <summary>Empties it.</summary>
    public void Clear() {
        heads.Clear();
        Count = 0;
    }

    long KeyOf(Vector3 position) {
        var cell = Cell(position, 1f / CellSize);

        return Key(cell.X, cell.Z);
    }

    static (int X, int Z) Cell(Vector3 position, float inverse) => (
        (int)MathF.Floor(position.X * inverse),
        (int)MathF.Floor(position.Z * inverse)
    );

    // Thirty-two bits an axis, so no level anybody builds can make two cells share a key. Aliasing
    // would be a query that returns something too far away, which the distance test above throws out
    // — the failure mode would be cost rather than a wrong answer, but there is no reason to have one.
    static long Key(int x, int z) => ((long)(uint)x << 32) | (uint)z;
}
