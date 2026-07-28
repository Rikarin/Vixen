// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Navigation.Baking;

/// <summary>
///     Grows regions out from the ridges of the distance field, one water level at a time.
/// </summary>
/// <remarks>
///     <para>
///         The picture the name comes from is worth keeping in mind, because every decision here
///         follows from it. <see cref="CompactHeightfield.Distances" /> is a terrain whose peaks are
///         the places furthest from a wall — the middle of a room, the centre line of a corridor. The
///         water level starts above the highest peak and drops two half-voxels at a time. At each
///         level, the land that has just emerged is either joined to a region that already reaches it,
///         or, if nothing does, becomes a new region. Two regions growing towards each other stop
///         where they meet, and that meeting line is the boundary.
///     </para>
///     <para>
///         <b>Expand before flood, always.</b> The order is the algorithm. Growing the existing
///         regions first means a newly-emerged strip beside a room joins the room; seeding first would
///         make it a region of its own that then has to be merged away. Getting this backwards
///         produces a partition that is correct and useless — hundreds of small regions where there
///         should be one.
///     </para>
///     <para>
///         <b>A flood refuses to touch a region that is not its own.</b> That is what stops the water
///         from two peaks in the same room running together into one region that reaches round both
///         sides of a pillar — mostly. It does not stop it entirely, which is why
///         <see cref="ContourSet" /> merges holes: a region that grows round an obstacle and meets
///         only itself has broken no rule here.
///     </para>
///     <para>
///         Recast's <c>rcBuildRegions</c>, re-derived. The code is Vixen's.
///     </para>
/// </remarks>
internal static class Watershed {
    /// <summary>How many times a level's regions may be grown before the level is given up on.</summary>
    /// <remarks>
    ///     Growth is one voxel per pass, so this bounds how far a region may reach into newly-emerged
    ///     ground before the rest of it is allowed to seed regions of its own. Eight is Recast's
    ///     number. Too small leaves seeds where a neighbour would have arrived; too large is slower for
    ///     a partition that looks the same, because the water only drops two half-voxels at a time and
    ///     nothing is ever eight voxels from the edge of what just emerged.
    /// </remarks>
    const int ExpandIterations = 8;

    /// <summary>Partitions a field whose distance field has already been built.</summary>
    /// <param name="field">The surface.</param>
    /// <param name="regionCount">The largest region id issued, plus one.</param>
    /// <returns>One region id per span, zero where there is none.</returns>
    public static ushort[] Partition(CompactHeightfield field, out int regionCount) {
        var regions = new ushort[field.Spans.Length];

        // How far each span is from the seed of the region claiming it — not from a wall. The
        // expansion prefers the closest claim, so a span between two regions goes to whichever
        // reached it in fewer steps rather than to whichever the iteration order happened to try.
        var reach = new ushort[field.Spans.Length];

        var stack = new List<int>();
        var flood = new List<int>();
        var pending = new List<(int Index, ushort Region, ushort Reach)>();

        ushort identifier = 1;
        var level = (ushort)((field.MaximumDistance + 1) & ~1);

        while (level > 0) {
            level = level >= 2 ? (ushort)(level - 2) : (ushort)0;

            Expand(field, regions, reach, stack, pending, level, ExpandIterations);

            for (var z = 0; z < field.Depth; z++) {
                for (var x = 0; x < field.Width; x++) {
                    ref var cell = ref field.Cells[x + (z * field.Width)];

                    for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                        if (field.Distances[index] < level || regions[index] != 0 || field.Areas[index] == NavArea.Null) {
                            continue;
                        }

                        if (Flood(field, regions, reach, flood, x, z, index, level, identifier)) {
                            if (identifier == ushort.MaxValue) {
                                // Sixty-five thousand regions in one tile means the settings are
                                // wrong, not that the level is complicated. Stopping here leaves the
                                // rest unpartitioned and walkable-but-empty rather than wrapping the
                                // id round and merging two unrelated parts of the level into one.
                                regionCount = identifier;

                                return regions;
                            }

                            identifier++;
                        }
                    }
                }
            }
        }

        // One last growth with the water fully drained, so that everything the flood refused —
        // anything within a voxel of a wall — still ends up in the region beside it rather than in
        // no region at all. Unbounded, because this is the pass that has to finish.
        Expand(field, regions, reach, stack, pending, 0, ExpandIterations * 8);

        regionCount = identifier;

        return regions;
    }

    /// <summary>Grows every region outwards into the ground that has emerged at this level.</summary>
    /// <remarks>
    ///     Collected once and then worked over repeatedly: a span is taken out of the list the moment
    ///     it is claimed, so each pass is over what is left rather than over the whole level, and the
    ///     loop ends when a pass claims nothing.
    /// </remarks>
    static void Expand(
        CompactHeightfield field,
        ushort[] regions,
        ushort[] reach,
        List<int> stack,
        List<(int Index, ushort Region, ushort Reach)> pending,
        ushort level,
        int maxIterations
    ) {
        stack.Clear();

        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                ref var cell = ref field.Cells[x + (z * field.Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    if (field.Distances[index] >= level && regions[index] == 0 && field.Areas[index] != NavArea.Null) {
                        stack.Add(x);
                        stack.Add(z);
                        stack.Add(index);
                    }
                }
            }
        }

        var iteration = 0;

        while (stack.Count > 0) {
            var failed = 0;
            pending.Clear();

            for (var entry = 0; entry < stack.Count; entry += 3) {
                var index = stack[entry + 2];

                if (index < 0) {
                    failed++;

                    continue;
                }

                var x = stack[entry];
                var z = stack[entry + 1];
                var area = field.Areas[index];

                var claim = regions[index];
                var claimReach = ushort.MaxValue;

                for (var direction = 0; direction < 4; direction++) {
                    var neighbour = field.Neighbour(index, x, z, direction);

                    if (neighbour < 0 || field.Areas[neighbour] != area || regions[neighbour] == 0) {
                        continue;
                    }

                    if (reach[neighbour] + 2 < claimReach) {
                        claim = regions[neighbour];
                        claimReach = (ushort)(reach[neighbour] + 2);
                    }
                }

                if (claim == 0) {
                    failed++;

                    continue;
                }

                // Written after the whole pass, not during it, so that a region cannot grow two
                // voxels in one pass by claiming a span and then being read back through it. That
                // would make the result depend on the order the spans happen to be visited in.
                stack[entry + 2] = -1;
                pending.Add((index, claim, claimReach));
            }

            foreach (var (index, region, claimed) in pending) {
                regions[index] = region;
                reach[index] = claimed;
            }

            if (failed * 3 == stack.Count) {
                break;
            }

            if (level > 0 && ++iteration >= maxIterations) {
                break;
            }
        }
    }

    /// <summary>Seeds a region at one span and lets it fill whatever is above the water line.</summary>
    /// <returns><see langword="false" /> if the seed touched another region and claimed nothing.</returns>
    /// <remarks>
    ///     <para>
    ///         The fill stops at anything belonging to another region, and it looks diagonally as well
    ///         as orthogonally to do it. The diagonal check is what keeps two regions from meeting at a
    ///         corner and leaving a span that touches both but is adjacent to neither — a corner like
    ///         that becomes a contour the tracer walks twice.
    ///     </para>
    ///     <para>
    ///         A span that fails the check is <i>unclaimed</i> rather than skipped, because it was
    ///         claimed optimistically on the way in: the fill marks a span as it pushes it, so that
    ///         nothing is pushed twice, and undoes that when it turns out to be next to somebody else.
    ///     </para>
    /// </remarks>
    static bool Flood(
        CompactHeightfield field,
        ushort[] regions,
        ushort[] reach,
        List<int> stack,
        int startX,
        int startZ,
        int startIndex,
        ushort level,
        ushort identifier
    ) {
        var area = field.Areas[startIndex];
        var floor = level >= 2 ? (ushort)(level - 2) : (ushort)0;
        var claimed = 0;

        stack.Clear();
        stack.Add(startX);
        stack.Add(startZ);
        stack.Add(startIndex);

        regions[startIndex] = identifier;
        reach[startIndex] = 0;

        while (stack.Count > 0) {
            var index = stack[^1];
            var z = stack[^2];
            var x = stack[^3];
            stack.RemoveRange(stack.Count - 3, 3);

            var foreign = false;

            for (var direction = 0; direction < 4 && !foreign; direction++) {
                var neighbour = field.Neighbour(index, x, z, direction);

                if (neighbour < 0 || field.Areas[neighbour] != area) {
                    continue;
                }

                if (regions[neighbour] != 0 && regions[neighbour] != identifier) {
                    foreign = true;

                    break;
                }

                var next = (direction + 1) & 3;

                var diagonal = field.Neighbour(
                    neighbour,
                    x + CompactHeightfield.OffsetX[direction],
                    z + CompactHeightfield.OffsetZ[direction],
                    next
                );

                if (diagonal < 0 || field.Areas[diagonal] != area) {
                    continue;
                }

                if (regions[diagonal] != 0 && regions[diagonal] != identifier) {
                    foreign = true;
                }
            }

            if (foreign) {
                regions[index] = 0;

                continue;
            }

            claimed++;

            for (var direction = 0; direction < 4; direction++) {
                var neighbour = field.Neighbour(index, x, z, direction);

                if (neighbour < 0 || field.Areas[neighbour] != area) {
                    continue;
                }

                if (field.Distances[neighbour] < floor || regions[neighbour] != 0) {
                    continue;
                }

                regions[neighbour] = identifier;
                reach[neighbour] = 0;

                stack.Add(x + CompactHeightfield.OffsetX[direction]);
                stack.Add(z + CompactHeightfield.OffsetZ[direction]);
                stack.Add(neighbour);
            }
        }

        return claimed > 0;
    }
}
