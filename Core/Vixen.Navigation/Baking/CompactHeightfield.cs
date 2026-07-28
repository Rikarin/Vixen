// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Baking;

/// <summary>Where one column's walkable spans start in the flat span array, and how many there are.</summary>
internal struct CompactCell {
    /// <summary>The first span's index.</summary>
    public int Index;

    /// <summary>How many there are.</summary>
    public int Count;
}

/// <summary>One walkable surface: the top of a solid span, with the open space above it.</summary>
internal struct CompactSpan {
    /// <summary>The voxel the surface is at.</summary>
    public ushort Y;

    /// <summary>Which region it was partitioned into, or zero.</summary>
    public ushort Region;

    /// <summary>How much open space is above it, in voxels, saturating at 255.</summary>
    public byte Height;

    /// <summary>
    ///     Four six-bit fields, one per direction: the offset of the neighbouring span within its own
    ///     column, or <see cref="CompactHeightfield.NotConnected" />.
    /// </summary>
    public uint Connections;
}

/// <summary>
///     The walkable surface, one entry per place something can stand, with its neighbours already
///     worked out.
/// </summary>
/// <remarks>
///     <para>
///         The solid heightfield answers "what is solid"; this answers "where can an agent be, and
///         where can it go from there". Everything after it — erosion by the agent radius,
///         partitioning into regions, tracing their outlines — is a graph walk over these spans, and
///         none of it looks at a triangle or a voxel again.
///     </para>
///     <para>
///         Connections are stored rather than searched because they are asked for constantly and the
///         answer is not obvious: two columns are neighbours when their surfaces are within a step of
///         each other <i>and</i> the agent fits in the overlap of the space above them. A doorway an
///         agent cannot fit through is not a connection, and this is where that is decided once.
///     </para>
///     <para>
///         Recast's structure and Recast's algorithms; the code is Vixen's.
///     </para>
/// </remarks>
internal sealed class CompactHeightfield {
    /// <summary>The value a connection field holds when there is no neighbour that way.</summary>
    public const int NotConnected = 0x3f;

    /// <summary>Direction 0 is -X, 1 is +Z, 2 is +X, 3 is -Z. Every walk in the bake uses this order.</summary>
    public static readonly int[] OffsetX = [-1, 0, 1, 0];

    /// <summary>The Z half of <see cref="OffsetX" />.</summary>
    public static readonly int[] OffsetZ = [0, 1, 0, -1];

    CompactHeightfield(int width, int depth, BoundingBox bounds, float cellSize, float cellHeight, int spanCount) {
        Width = width;
        Depth = depth;
        Bounds = bounds;
        CellSize = cellSize;
        CellHeight = cellHeight;
        Cells = new CompactCell[width * depth];
        Spans = new CompactSpan[spanCount];
        Areas = new byte[spanCount];
    }

    /// <summary>How many columns across X.</summary>
    public int Width { get; }

    /// <summary>How many columns across Z.</summary>
    public int Depth { get; }

    /// <summary>The volume the field covers.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>The width and depth of a column.</summary>
    public float CellSize { get; }

    /// <summary>The height of a voxel.</summary>
    public float CellHeight { get; }

    /// <summary>Where each column's spans are.</summary>
    public CompactCell[] Cells { get; }

    /// <summary>Every walkable span in the field, grouped by column.</summary>
    public CompactSpan[] Spans { get; }

    /// <summary>One area id per span, parallel to <see cref="Spans" />.</summary>
    public byte[] Areas { get; }

    /// <summary>How many regions the partition produced, counting the unassigned zero.</summary>
    public int RegionCount { get; private set; }

    /// <summary>Reads one direction of a span's connections.</summary>
    /// <param name="span">The span.</param>
    /// <param name="direction">The direction, 0 to 3.</param>
    /// <returns>The neighbour's offset within its column, or <see cref="NotConnected" />.</returns>
    public static int GetConnection(in CompactSpan span, int direction) => (int)((span.Connections >> (direction * 6)) & 0x3f);

    /// <summary>Writes one direction of a span's connections.</summary>
    /// <param name="span">The span.</param>
    /// <param name="direction">The direction, 0 to 3.</param>
    /// <param name="value">The neighbour's offset within its column, or <see cref="NotConnected" />.</param>
    public static void SetConnection(ref CompactSpan span, int direction, int value) {
        var shift = direction * 6;
        span.Connections = (span.Connections & ~(0x3fu << shift)) | (((uint)value & 0x3f) << shift);
    }

    /// <summary>The index of a span's neighbour, or -1.</summary>
    /// <param name="index">The span's index.</param>
    /// <param name="x">Its column's X.</param>
    /// <param name="z">Its column's Z.</param>
    /// <param name="direction">The direction, 0 to 3.</param>
    /// <returns>The neighbour's index, or -1.</returns>
    public int Neighbour(int index, int x, int z, int direction) {
        var connection = GetConnection(in Spans[index], direction);

        if (connection == NotConnected) {
            return -1;
        }

        return Cells[x + OffsetX[direction] + ((z + OffsetZ[direction]) * Width)].Index + connection;
    }

    /// <summary>Builds the walkable surface from a solid heightfield.</summary>
    /// <param name="field">The rasterised geometry, already filtered.</param>
    /// <param name="walkableHeight">The agent's height, in voxels.</param>
    /// <param name="walkableClimb">The agent's step height, in voxels.</param>
    /// <returns>The compact field.</returns>
    public static CompactHeightfield Build(Heightfield field, int walkableHeight, int walkableClimb) {
        var count = 0;

        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                for (var index = field.First(x, z); index >= 0; index = field.Span(index).Next) {
                    if (field.Span(index).Area != NavArea.Null) {
                        count++;
                    }
                }
            }
        }

        var compact = new CompactHeightfield(field.Width, field.Depth, field.Bounds, field.CellSize, field.CellHeight, count);
        var next = 0;

        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                var column = x + (z * field.Width);
                compact.Cells[column].Index = next;

                for (var index = field.First(x, z); index >= 0; index = field.Span(index).Next) {
                    var span = field.Span(index);

                    if (span.Area == NavArea.Null) {
                        continue;
                    }

                    var top = span.Next >= 0 ? field.Span(span.Next).Min : Heightfield.MaxHeight;

                    compact.Spans[next] = new() {
                        Y = span.Max,
                        Height = (byte)Math.Clamp(top - span.Max, 0, byte.MaxValue),
                        Connections = 0xffffffff
                    };

                    compact.Areas[next] = span.Area;
                    next++;
                    compact.Cells[column].Count++;
                }
            }
        }

        compact.Connect(walkableHeight, walkableClimb);

        return compact;
    }

    /// <summary>Shrinks the walkable surface by the agent's radius.</summary>
    /// <param name="radius">The radius, in voxels.</param>
    /// <remarks>
    ///     <para>
    ///         This is why an agent can be treated as a point for the rest of its life. Every polygon
    ///         the bake produces is at least a radius away from anything solid, so a path that stays
    ///         on the mesh is a path a body of that width fits along, and nothing downstream has to
    ///         think about the agent's size at all.
    ///     </para>
    ///     <para>
    ///         The distance is a two-pass chamfer with weights 2 and 3 for orthogonal and diagonal
    ///         steps — an integer approximation of Euclidean distance, which is why the threshold is
    ///         twice the radius.
    ///     </para>
    /// </remarks>
    public void ErodeWalkableArea(int radius) {
        if (radius <= 0) {
            return;
        }

        var distance = BorderDistance();
        var threshold = radius * 2;

        for (var index = 0; index < Spans.Length; index++) {
            if (distance[index] < threshold) {
                Areas[index] = NavArea.Null;
            }
        }
    }

    /// <summary>Stamps an authored area onto the surface inside a volume.</summary>
    /// <param name="volume">The volume.</param>
    /// <remarks>
    ///     Called after erosion and before partitioning: after, because a volume over ground the agent
    ///     cannot reach should not resurrect it, and before, because the region sweep will not run a
    ///     region across an area boundary and that is what keeps a polygon from being half road.
    /// </remarks>
    public void MarkArea(NavAreaVolume volume) {
        ArgumentNullException.ThrowIfNull(volume);

        var minimumX = Math.Max(0, (int)MathF.Floor((volume.Bounds.Minimum.X - Bounds.Minimum.X) / CellSize));
        var maximumX = Math.Min(Width - 1, (int)MathF.Ceiling((volume.Bounds.Maximum.X - Bounds.Minimum.X) / CellSize));
        var minimumZ = Math.Max(0, (int)MathF.Floor((volume.Bounds.Minimum.Z - Bounds.Minimum.Z) / CellSize));
        var maximumZ = Math.Min(Depth - 1, (int)MathF.Ceiling((volume.Bounds.Maximum.Z - Bounds.Minimum.Z) / CellSize));

        for (var z = minimumZ; z <= maximumZ; z++) {
            for (var x = minimumX; x <= maximumX; x++) {
                ref var cell = ref Cells[x + (z * Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    if (Areas[index] == NavArea.Null) {
                        continue;
                    }

                    // The column's centre rather than its corner, so a volume drawn along a cell
                    // boundary does not depend on which side of it the arithmetic lands.
                    var point = new Vector3(
                        Bounds.Minimum.X + ((x + 0.5f) * CellSize),
                        Bounds.Minimum.Y + (Spans[index].Y * CellHeight),
                        Bounds.Minimum.Z + ((z + 0.5f) * CellSize)
                    );

                    if (volume.Contains(point)) {
                        Areas[index] = volume.Area;
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Partitions the walkable surface into regions by sweeping it row by row.
    /// </summary>
    /// <param name="minRegionArea">The smallest region to keep, in spans.</param>
    /// <remarks>
    ///     <para>
    ///         Monotone partitioning, which is one of the two Recast offers. A region is grown from
    ///         runs of connected spans in a row, and a run joins the region above it only when it is
    ///         the <i>only</i> run in its row touching that region. That last condition is the whole
    ///         algorithm: without it, the two sides of a pillar would both join the region in front of
    ///         it and the result would be a region with a hole in it, which the contour tracer cannot
    ///         express and the polygoniser cannot triangulate.
    ///     </para>
    ///     <para>
    ///         The cost is shape. Watershed partitioning — Recast's other option, and its default —
    ///         produces rounder regions and therefore fewer, fatter polygons; monotone produces long
    ///         thin strips wherever the sweep direction disagrees with the geometry. Both give correct
    ///         paths. Watershed is the natural thing to add here, and the README says so.
    ///     </para>
    /// </remarks>
    public void BuildRegionsMonotone(int minRegionArea) {
        var regions = new ushort[Spans.Length];
        var sweeps = new SweepSpan[Math.Max(Width, Depth) + 1];
        ushort identifier = 1;

        var previousCounts = new int[Spans.Length + 2];

        for (var z = 0; z < Depth; z++) {
            Array.Clear(previousCounts, 0, Math.Min(previousCounts.Length, identifier + 1));
            ushort sweepCount = 1;

            for (var x = 0; x < Width; x++) {
                ref var cell = ref Cells[x + (z * Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    if (Areas[index] == NavArea.Null) {
                        continue;
                    }

                    // Carry on the run coming from -X, or start a new one.
                    ushort sweep = 0;
                    var left = Neighbour(index, x, z, 0);

                    if (left >= 0 && Areas[left] == Areas[index]) {
                        sweep = regions[left];
                    }

                    if (sweep == 0) {
                        sweep = sweepCount++;

                        // A row holds one run per column at least, and one more for every extra
                        // surface stacked in a column — a walkway over a floor is two.
                        if (sweep >= sweeps.Length) {
                            Array.Resize(ref sweeps, sweeps.Length * 2);
                        }

                        sweeps[sweep] = new() { Region = sweep, Count = 0, Neighbour = 0 };
                    }

                    // Remember which region the row below offers, and give up on inheriting it the
                    // moment this run touches two different ones.
                    var below = Neighbour(index, x, z, 3);

                    if (below >= 0 && regions[below] != 0 && Areas[below] == Areas[index]) {
                        var candidate = regions[below];

                        if (sweeps[sweep].Neighbour == 0 || sweeps[sweep].Neighbour == candidate) {
                            sweeps[sweep].Neighbour = candidate;
                            sweeps[sweep].Count++;
                            previousCounts[candidate]++;
                        } else {
                            sweeps[sweep].Neighbour = ushort.MaxValue;
                        }
                    }

                    regions[index] = sweep;
                }
            }

            for (var sweep = 1; sweep < sweepCount; sweep++) {
                var neighbour = sweeps[sweep].Neighbour;

                if (neighbour is not 0 and not ushort.MaxValue && previousCounts[neighbour] == sweeps[sweep].Count) {
                    sweeps[sweep].Region = neighbour;
                } else {
                    sweeps[sweep].Region = identifier++;

                    if (identifier + 1 >= previousCounts.Length) {
                        Array.Resize(ref previousCounts, previousCounts.Length * 2);
                    }
                }
            }

            for (var x = 0; x < Width; x++) {
                ref var cell = ref Cells[x + (z * Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    if (regions[index] > 0 && regions[index] < sweepCount) {
                        regions[index] = sweeps[regions[index]].Region;
                    }
                }
            }
        }

        RemoveSmallRegions(regions, identifier, minRegionArea);

        for (var index = 0; index < Spans.Length; index++) {
            Spans[index].Region = regions[index];
        }

        RegionCount = identifier;
    }

    /// <summary>Discards regions too small to be worth walking on.</summary>
    /// <remarks>
    ///     Discarded rather than merged into a neighbour, which is what Recast does. Merging keeps
    ///     more surface, at the cost of regions that are no longer monotone and can therefore have
    ///     holes — the thing the partitioning was chosen to avoid. Discarding loses the ledge behind
    ///     the pillar, which is the surface nothing could reach anyway.
    /// </remarks>
    void RemoveSmallRegions(ushort[] regions, int regionCount, int minRegionArea) {
        if (minRegionArea <= 0) {
            return;
        }

        var counts = new int[regionCount + 1];

        for (var index = 0; index < regions.Length; index++) {
            if (regions[index] > 0 && regions[index] < counts.Length) {
                counts[regions[index]]++;
            }
        }

        for (var index = 0; index < regions.Length; index++) {
            if (regions[index] > 0 && regions[index] < counts.Length && counts[regions[index]] < minRegionArea) {
                regions[index] = 0;
                Areas[index] = NavArea.Null;
            }
        }
    }

    /// <summary>Works out which spans an agent can step between.</summary>
    void Connect(int walkableHeight, int walkableClimb) {
        for (var z = 0; z < Depth; z++) {
            for (var x = 0; x < Width; x++) {
                ref var cell = ref Cells[x + (z * Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    ref var span = ref Spans[index];

                    for (var direction = 0; direction < 4; direction++) {
                        SetConnection(ref span, direction, NotConnected);

                        var neighbourX = x + OffsetX[direction];
                        var neighbourZ = z + OffsetZ[direction];

                        if (neighbourX < 0 || neighbourZ < 0 || neighbourX >= Width || neighbourZ >= Depth) {
                            continue;
                        }

                        ref var neighbourCell = ref Cells[neighbourX + (neighbourZ * Width)];

                        for (var other = neighbourCell.Index; other < neighbourCell.Index + neighbourCell.Count; other++) {
                            ref var neighbour = ref Spans[other];

                            var bottom = Math.Max(span.Y, neighbour.Y);
                            var top = Math.Min(span.Y + span.Height, neighbour.Y + neighbour.Height);

                            if (top - bottom < walkableHeight || Math.Abs(neighbour.Y - span.Y) > walkableClimb) {
                                continue;
                            }

                            var offset = other - neighbourCell.Index;

                            // Six bits is Recast's budget and it is generous: it is the number of
                            // surfaces in one column, not in the level. A column with sixty-three
                            // floors in it loses the last of them rather than corrupting the field.
                            if (offset is >= 0 and < NotConnected) {
                                SetConnection(ref span, direction, offset);
                            }

                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>The distance from every span to the nearest edge of the walkable surface, in half-voxels.</summary>
    byte[] BorderDistance() {
        var distance = new byte[Spans.Length];
        Array.Fill(distance, byte.MaxValue);

        for (var z = 0; z < Depth; z++) {
            for (var x = 0; x < Width; x++) {
                ref var cell = ref Cells[x + (z * Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    if (Areas[index] == NavArea.Null) {
                        distance[index] = 0;

                        continue;
                    }

                    var open = 0;

                    for (var direction = 0; direction < 4; direction++) {
                        var neighbour = Neighbour(index, x, z, direction);

                        if (neighbour >= 0 && Areas[neighbour] != NavArea.Null) {
                            open++;
                        }
                    }

                    if (open != 4) {
                        distance[index] = 0;
                    }
                }
            }
        }

        Sweep(distance, forward: true);
        Sweep(distance, forward: false);

        return distance;
    }

    /// <summary>One pass of the chamfer distance transform.</summary>
    void Sweep(byte[] distance, bool forward) {
        // Each pass relaxes against the two directions it has already visited, plus the diagonal
        // reached through each of them — which is what makes two passes enough.
        var first = forward ? 0 : 2;
        var firstDiagonal = forward ? 3 : 1;
        var second = forward ? 3 : 1;
        var secondDiagonal = forward ? 2 : 0;

        for (var step = 0; step < Width * Depth; step++) {
            var column = forward ? step : (Width * Depth) - 1 - step;
            var x = column % Width;
            var z = column / Width;

            ref var cell = ref Cells[column];

            for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                Relax(distance, index, x, z, first, firstDiagonal);
                Relax(distance, index, x, z, second, secondDiagonal);
            }
        }
    }

    void Relax(byte[] distance, int index, int x, int z, int direction, int diagonal) {
        var neighbour = Neighbour(index, x, z, direction);

        if (neighbour < 0) {
            return;
        }

        if (distance[neighbour] + 2 < distance[index]) {
            distance[index] = (byte)(distance[neighbour] + 2);
        }

        var corner = Neighbour(neighbour, x + OffsetX[direction], z + OffsetZ[direction], diagonal);

        if (corner >= 0 && distance[corner] + 3 < distance[index]) {
            distance[index] = (byte)(distance[corner] + 3);
        }
    }

    /// <summary>One run of connected spans in a row, while the sweep is deciding what it belongs to.</summary>
    struct SweepSpan {
        /// <summary>The region it ends up in.</summary>
        public ushort Region;

        /// <summary>The region below that it might join.</summary>
        public ushort Neighbour;

        /// <summary>How many of its spans touch that region.</summary>
        public int Count;
    }
}
