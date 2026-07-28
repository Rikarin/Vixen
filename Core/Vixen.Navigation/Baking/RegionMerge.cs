// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Navigation.Baking;

/// <summary>
///     Absorbs the regions that are too small to be worth their own polygons into the ones beside
///     them, and discards the ones that lead nowhere.
/// </summary>
/// <remarks>
///     <para>
///         A partition is a means, not an end: what matters is the polygons that come out of it, and a
///         sweep that leaves a two-cell sliver beside every doorway produces a mesh whose polygon count
///         is dominated by shapes no agent will ever notice. Merging those into a neighbour costs
///         nothing at run time and takes both the polygon and the search node with it.
///     </para>
///     <para>
///         <b>The single-connection rule is what keeps this safe.</b> Two regions are only merged when
///         each touches the other along <i>one</i> stretch of boundary. Two stretches means the pair
///         encloses something between them, and merging them would produce a region with a hole — which
///         the contour tracer would emit as a second, oppositely-wound outline and the polygoniser
///         would turn into a solid polygon over the very obstacle the hole is. Regions that share a
///         column (one above the other) are refused for the same reason.
///     </para>
///     <para>
///         Recast's <c>mergeAndFilterRegions</c>, re-derived. It is the half of watershed partitioning
///         that the monotone sweep also wants, which is why it is its own stage here rather than part
///         of either.
///     </para>
/// </remarks>
internal static class RegionMerge {
    /// <summary>Merges and filters, and renumbers what survives.</summary>
    /// <param name="field">The partitioned surface. Its spans' regions are read and rewritten.</param>
    /// <param name="regions">One region id per span.</param>
    /// <param name="regionCount">The largest id in use, plus one.</param>
    /// <param name="minRegionArea">The smallest region to keep, in spans.</param>
    /// <param name="mergeRegionArea">
    ///     The size below which a region is absorbed into a neighbour if it can be. Larger than
    ///     <paramref name="minRegionArea" /> in any sensible configuration: the first is "not worth
    ///     keeping at all", the second is "not worth keeping apart".
    /// </param>
    /// <returns>How many regions there are now, counting the unassigned zero.</returns>
    public static int Apply(CompactHeightfield field, ushort[] regions, int regionCount, int minRegionArea, int mergeRegionArea) {
        var pool = new Region[regionCount + 1];

        for (var index = 0; index < pool.Length; index++) {
            pool[index] = new((ushort)index);
        }

        Describe(field, regions, pool);
        Discard(pool, minRegionArea);
        Merge(pool, mergeRegionArea);

        return Renumber(field, regions, pool);
    }

    /// <summary>Counts each region's spans, and finds what it touches.</summary>
    static void Describe(CompactHeightfield field, ushort[] regions, Region[] pool) {
        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                ref var cell = ref field.Cells[x + (z * field.Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    var id = regions[index];

                    if (id == 0 || id >= pool.Length) {
                        continue;
                    }

                    var region = pool[id];
                    region.SpanCount++;

                    // Everything else in this column is a floor above or below this region. A region
                    // that appears twice in one column overlaps itself, and is never merged into
                    // anything: it is already the shape a hole is made of.
                    for (var other = cell.Index; other < cell.Index + cell.Count; other++) {
                        if (other == index) {
                            continue;
                        }

                        var floor = regions[other];

                        if (floor == 0 || floor >= pool.Length) {
                            continue;
                        }

                        if (floor == id) {
                            region.Overlaps = true;
                        } else if (!region.Floors.Contains(floor)) {
                            region.Floors.Add(floor);
                        }
                    }

                    if (region.Connections.Count > 0) {
                        continue;
                    }

                    region.Area = field.Areas[index];

                    for (var direction = 0; direction < 4; direction++) {
                        if (IsBoundary(field, regions, index, x, z, direction)) {
                            WalkBoundary(field, regions, x, z, index, direction, region.Connections);

                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Clears the groups of regions that are too small to be worth reaching.</summary>
    /// <remarks>
    ///     By connected <i>group</i> rather than by region, because three slivers that only touch each
    ///     other are as unreachable as one, and removing them one at a time would keep whichever
    ///     happened to be larger than the threshold.
    /// </remarks>
    static void Discard(Region[] pool, int minRegionArea) {
        if (minRegionArea <= 0) {
            return;
        }

        var stack = new List<ushort>();
        var group = new List<ushort>();

        for (var index = 1; index < pool.Length; index++) {
            if (pool[index].Id == 0 || pool[index].SpanCount == 0 || pool[index].Visited) {
                continue;
            }

            stack.Clear();
            group.Clear();

            pool[index].Visited = true;
            stack.Add((ushort)index);

            var spans = 0;

            while (stack.Count > 0) {
                var current = stack[^1];
                stack.RemoveAt(stack.Count - 1);

                spans += pool[current].SpanCount;
                group.Add(current);

                foreach (var connection in pool[current].Connections) {
                    if (connection == 0 || connection >= pool.Length || pool[connection].Visited || pool[connection].Id == 0) {
                        continue;
                    }

                    pool[connection].Visited = true;
                    stack.Add(connection);
                }
            }

            if (spans >= minRegionArea) {
                continue;
            }

            foreach (var member in group) {
                pool[member].Id = 0;
                pool[member].SpanCount = 0;
            }
        }
    }

    /// <summary>Absorbs each small region into the smallest neighbour that will take it.</summary>
    static void Merge(Region[] pool, int mergeRegionArea) {
        bool merged;

        do {
            merged = false;

            for (var index = 1; index < pool.Length; index++) {
                var region = pool[index];

                if (region.Id == 0 || region.SpanCount == 0 || region.Overlaps) {
                    continue;
                }

                // Big enough, and it touches a wall: it is a piece of the level in its own right.
                if (region.SpanCount > mergeRegionArea && region.Connections.Contains((ushort)0)) {
                    continue;
                }

                var smallest = int.MaxValue;
                var target = region.Id;

                foreach (var connection in region.Connections) {
                    if (connection == 0 || connection >= pool.Length) {
                        continue;
                    }

                    var candidate = pool[connection];

                    if (candidate.Id == 0 || candidate.Overlaps || candidate.SpanCount >= smallest) {
                        continue;
                    }

                    if (!CanMerge(region, candidate) || !CanMerge(candidate, region)) {
                        continue;
                    }

                    smallest = candidate.SpanCount;
                    target = candidate.Id;
                }

                if (target == region.Id || !Absorb(pool[target], region)) {
                    continue;
                }

                var absorbed = region.Id;

                foreach (var other in pool) {
                    if (other.Id == 0) {
                        continue;
                    }

                    if (other.Id == absorbed) {
                        other.Id = target;
                    }

                    Replace(other.Connections, absorbed, target);
                    Replace(other.Floors, absorbed, target);
                }

                merged = true;
            }
        } while (merged);
    }

    /// <summary>Gives the survivors consecutive ids and writes them back onto the spans.</summary>
    static int Renumber(CompactHeightfield field, ushort[] regions, Region[] pool) {
        ushort next = 1;

        foreach (var region in pool) {
            region.Remapped = false;
        }

        for (var index = 1; index < pool.Length; index++) {
            if (pool[index].Id == 0 || pool[index].Remapped) {
                continue;
            }

            var old = pool[index].Id;
            var replacement = next++;

            for (var other = index; other < pool.Length; other++) {
                if (pool[other].Id == old) {
                    pool[other].Id = replacement;
                    pool[other].Remapped = true;
                }
            }
        }

        for (var index = 0; index < regions.Length; index++) {
            var id = regions[index];

            if (id == 0 || id >= pool.Length) {
                regions[index] = 0;

                continue;
            }

            regions[index] = pool[id].Id;

            if (regions[index] == 0) {
                field.Areas[index] = NavArea.Null;
            }
        }

        return next;
    }

    /// <summary>Whether one region may be absorbed into another without enclosing anything.</summary>
    static bool CanMerge(Region region, Region other) {
        var touching = 0;

        foreach (var connection in region.Connections) {
            if (connection == other.Id) {
                touching++;
            }
        }

        // More than one stretch of shared boundary means something is caught between them.
        if (touching > 1) {
            return false;
        }

        // And a region directly above or below is a different storey, not a neighbour.
        return !region.Floors.Contains(other.Id);
    }

    /// <summary>Splices one region's boundary into another's at the point they share.</summary>
    static bool Absorb(Region target, Region region) {
        var insertion = target.Connections.IndexOf(region.Id);
        var source = region.Connections.IndexOf(target.Id);

        if (insertion < 0 || source < 0) {
            return false;
        }

        var merged = new List<ushort>();

        for (var index = 0; index < target.Connections.Count - 1; index++) {
            merged.Add(target.Connections[(insertion + 1 + index) % target.Connections.Count]);
        }

        for (var index = 0; index < region.Connections.Count - 1; index++) {
            merged.Add(region.Connections[(source + 1 + index) % region.Connections.Count]);
        }

        RemoveAdjacentDuplicates(merged);

        target.Connections.Clear();
        target.Connections.AddRange(merged);

        foreach (var floor in region.Floors) {
            if (!target.Floors.Contains(floor)) {
                target.Floors.Add(floor);
            }
        }

        target.SpanCount += region.SpanCount;
        region.SpanCount = 0;
        region.Connections.Clear();

        return true;
    }

    static void Replace(List<ushort> ids, ushort old, ushort replacement) {
        for (var index = 0; index < ids.Count; index++) {
            if (ids[index] == old) {
                ids[index] = replacement;
            }
        }
    }

    /// <summary>Whether the span's neighbour in a direction belongs to a different region.</summary>
    static bool IsBoundary(CompactHeightfield field, ushort[] regions, int index, int x, int z, int direction) {
        var neighbour = field.Neighbour(index, x, z, direction);
        var other = neighbour >= 0 ? regions[neighbour] : (ushort)0;

        return other != regions[index];
    }

    /// <summary>
    ///     Walks the outside of a region, recording which region lies across each stretch of it.
    /// </summary>
    /// <remarks>
    ///     The same walk the contour tracer makes, keeping the ids rather than the corners: what comes
    ///     out is the region's neighbours <i>in order</i>, which is what makes "do we touch this one
    ///     more than once" a question about a list rather than about geometry.
    /// </remarks>
    static void WalkBoundary(CompactHeightfield field, ushort[] regions, int startX, int startZ, int startIndex, int startDirection, List<ushort> connections) {
        var x = startX;
        var z = startZ;
        var index = startIndex;
        var direction = startDirection;

        var neighbour = field.Neighbour(index, x, z, direction);
        var current = neighbour >= 0 ? regions[neighbour] : (ushort)0;

        connections.Add(current);

        // A bound rather than a promise: a malformed field is a bug worth surviving, and a partition
        // that walked for ever would hang a content build with nothing to point at.
        for (var step = 0; step < 40_000; step++) {
            if (IsBoundary(field, regions, index, x, z, direction)) {
                var side = field.Neighbour(index, x, z, direction);
                var across = side >= 0 ? regions[side] : (ushort)0;

                if (across != current) {
                    current = across;
                    connections.Add(current);
                }

                direction = (direction + 1) & 3;
            } else {
                var next = field.Neighbour(index, x, z, direction);

                if (next < 0) {
                    break;
                }

                x += CompactHeightfield.OffsetX[direction];
                z += CompactHeightfield.OffsetZ[direction];
                index = next;
                direction = (direction + 3) & 3;
            }

            if (index == startIndex && direction == startDirection) {
                break;
            }
        }

        RemoveAdjacentDuplicates(connections);
    }

    static void RemoveAdjacentDuplicates(List<ushort> ids) {
        for (var index = 0; index < ids.Count && ids.Count > 1;) {
            var next = (index + 1) % ids.Count;

            if (ids[index] == ids[next]) {
                ids.RemoveAt(index);
            } else {
                index++;
            }
        }
    }

    /// <summary>One region, while the merge is deciding what to do with it.</summary>
    sealed class Region(ushort id) {
        public ushort Id { get; set; } = id;

        public int SpanCount { get; set; }

        public byte Area { get; set; }

        public bool Visited { get; set; }

        public bool Remapped { get; set; }

        /// <summary>Whether the region appears twice in one column, and so cannot be merged safely.</summary>
        public bool Overlaps { get; set; }

        /// <summary>What it touches, in the order its boundary touches them.</summary>
        public List<ushort> Connections { get; } = [];

        /// <summary>What is directly above or below it.</summary>
        public List<ushort> Floors { get; } = [];
    }
}
