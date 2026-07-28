// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Baking;

/// <summary>A run of solid voxels in one column.</summary>
internal struct HeightfieldSpan {
    /// <summary>The voxel it starts at, counted from the field's floor.</summary>
    public ushort Min;

    /// <summary>The voxel it ends at.</summary>
    public ushort Max;

    /// <summary>
    ///     How far below <see cref="Max" /> the real surface is, in sixteenths of a voxel.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Alongside <see cref="Max" /> rather than instead of it</b>, and that is the whole
    ///         design. Every decision the bake makes about whether a surface is a step, a ledge or a
    ///         wall is integer arithmetic on <see cref="Max" />, tuned against a voxel grid and tested
    ///         against one. Making those decisions fractional would be a rewrite of the filters for a
    ///         question they are not being asked. This is the residue: where in that voxel the
    ///         triangle actually was, carried past them and used only at the end, where a height is
    ///         reported rather than compared.
    ///     </para>
    ///     <para>
    ///         Sixteenths, because the value it corrects is one whole voxel and the cell height is
    ///         already the finest thing in the bake — a sixteenth of it is a centimetre at the default
    ///         settings, which is below what anything downstream can act on.
    ///     </para>
    /// </remarks>
    public byte Drop;

    /// <summary>The area its top surface carries, or <see cref="NavArea.Null" /> if nothing can stand there.</summary>
    public byte Area;

    /// <summary>The next span up the same column, or -1.</summary>
    public int Next;

    /// <summary>How many parts of a voxel <see cref="Drop" /> counts in.</summary>
    public const int DropScale = 16;

    /// <summary>The span's surface height, in <see cref="DropScale" />ths of a voxel.</summary>
    public readonly int Surface => (Max * DropScale) - Drop;
}

/// <summary>
///     The level's geometry as columns of solid voxels — the first thing a bake builds and the thing
///     that makes everything after it a grid problem instead of a mesh problem.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why voxels at all.</b> Level geometry is an arbitrary triangle soup: overlapping,
///         open-edged, self-intersecting, with a hundred thousand triangles describing a room an
///         agent sees as a floor. Rasterising it into columns throws all of that away and keeps the
///         one thing navigation needs — for each column, which vertical runs are solid — after which
///         "where can something stand" is a question about neighbouring cells rather than about
///         triangles.
///     </para>
///     <para>
///         This is Recast's approach and this is Recast's algorithm; the code is Vixen's. See the
///         project README for what was taken and what was left out.
///     </para>
///     <para>
///         Spans are held in one list and chained by index rather than by reference, with a free list
///         for the ones merging removes. A bake of a large level makes millions of them, and an
///         object per span would be millions of allocations and eight bytes of header each.
///     </para>
/// </remarks>
internal sealed class Heightfield {
    /// <summary>The tallest a span can reach, in voxels. Also the sentinel for "open sky".</summary>
    public const int MaxHeight = 0xffff;

    static readonly int[] OffsetX = [-1, 0, 1, 0];
    static readonly int[] OffsetZ = [0, 1, 0, -1];

    readonly int[] columns;
    readonly List<HeightfieldSpan> spans = [];
    int freeSpan = -1;

    /// <summary>Creates an empty field over a volume.</summary>
    /// <param name="bounds">The volume to voxelise.</param>
    /// <param name="cellSize">The width and depth of a column.</param>
    /// <param name="cellHeight">The height of a voxel.</param>
    public Heightfield(BoundingBox bounds, float cellSize, float cellHeight) {
        Bounds = bounds;
        CellSize = cellSize;
        CellHeight = cellHeight;
        Width = Math.Max(1, (int)MathF.Ceiling((bounds.Maximum.X - bounds.Minimum.X) / cellSize));
        Depth = Math.Max(1, (int)MathF.Ceiling((bounds.Maximum.Z - bounds.Minimum.Z) / cellSize));

        columns = new int[Width * Depth];
        Array.Fill(columns, -1);
    }

    /// <summary>How many columns across X.</summary>
    public int Width { get; }

    /// <summary>How many columns across Z.</summary>
    public int Depth { get; }

    /// <summary>The volume being voxelised.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>The width and depth of a column.</summary>
    public float CellSize { get; }

    /// <summary>The height of a voxel.</summary>
    public float CellHeight { get; }

    /// <summary>The bottom span of a column, or -1 if it is empty.</summary>
    /// <param name="x">The column's X.</param>
    /// <param name="z">The column's Z.</param>
    /// <returns>The span index.</returns>
    public int First(int x, int z) => columns[x + (z * Width)];

    /// <summary>Reads a span.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The span.</returns>
    public HeightfieldSpan Span(int index) => spans[index];

    /// <summary>How many spans are actually in a column, summed over the field.</summary>
    public int SpanCount {
        get {
            var total = 0;

            for (var column = 0; column < columns.Length; column++) {
                for (var span = columns[column]; span >= 0; span = spans[span].Next) {
                    total++;
                }
            }

            return total;
        }
    }

    /// <summary>Adds a solid run to a column, merging it with whatever it touches.</summary>
    /// <param name="x">The column's X.</param>
    /// <param name="z">The column's Z.</param>
    /// <param name="min">The voxel the run starts at.</param>
    /// <param name="max">The voxel it ends at.</param>
    /// <param name="area">The area its top carries.</param>
    /// <param name="mergeThreshold">
    ///     How close two merged tops have to be, in voxels, for the higher area id to win. Zero means
    ///     only exactly equal tops merge their areas.
    /// </param>
    /// <remarks>
    ///     Merging is what makes the field independent of how the geometry was tessellated: a floor
    ///     built from two triangles and the same floor built from two hundred produce the same spans.
    /// </remarks>
    /// <param name="drop">
    ///     How far below <paramref name="max" /> the real surface is, in
    ///     <see cref="HeightfieldSpan.DropScale" />ths of a voxel.
    /// </param>
    public void AddSpan(int x, int z, ushort min, ushort max, byte area, int mergeThreshold, byte drop = 0) {
        var column = x + (z * Width);
        var added = new HeightfieldSpan { Min = min, Max = max, Area = area, Drop = drop, Next = -1 };

        var previous = -1;
        var current = columns[column];

        while (current >= 0) {
            var span = spans[current];

            if (span.Min > added.Max) {
                break;
            }

            if (span.Max < added.Min) {
                previous = current;
                current = span.Next;

                continue;
            }

            added.Min = Math.Min(added.Min, span.Min);

            // The higher of the two surfaces wins the drop as well as the top, and a tie takes the
            // smaller drop — which is the higher surface again. Decided before Max is overwritten,
            // because afterwards there is no way to tell which of the two it came from.
            if (span.Max > added.Max || (span.Max == added.Max && span.Drop < added.Drop)) {
                added.Drop = span.Drop;
            }

            added.Max = Math.Max(added.Max, span.Max);

            // The area of the merged span is the area of whichever surface is on top. Two surfaces
            // whose tops are within the threshold are the same surface as far as an agent standing on
            // it is concerned, and then the more specific area — the larger id — wins.
            if (Math.Abs(added.Max - span.Max) <= mergeThreshold) {
                added.Area = Math.Max(added.Area, span.Area);
            }

            var next = span.Next;
            Release(current);

            if (previous >= 0) {
                var previousSpan = spans[previous];
                previousSpan.Next = next;
                spans[previous] = previousSpan;
            } else {
                columns[column] = next;
            }

            current = next;
        }

        var index = Allocate(added);

        if (previous >= 0) {
            var previousSpan = spans[previous];
            var inserted = spans[index];
            inserted.Next = previousSpan.Next;
            spans[index] = inserted;
            previousSpan.Next = index;
            spans[previous] = previousSpan;
        } else {
            var inserted = spans[index];
            inserted.Next = columns[column];
            spans[index] = inserted;
            columns[column] = index;
        }
    }

    /// <summary>Marks the triangles an agent could stand on, by their slope.</summary>
    /// <param name="maxSlopeDegrees">The steepest ground the agent can stand on.</param>
    /// <param name="vertices">The geometry's vertices.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="areas">One area id per triangle, written here.</param>
    /// <param name="walkableArea">The area to give the triangles that pass.</param>
    public static void MarkWalkableTriangles(
        float maxSlopeDegrees,
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        Span<byte> areas,
        byte walkableArea
    ) {
        var threshold = MathF.Cos(maxSlopeDegrees * MathF.PI / 180f);

        for (var triangle = 0; triangle < areas.Length; triangle++) {
            var a = vertices[indices[triangle * 3]];
            var b = vertices[indices[(triangle * 3) + 1]];
            var c = vertices[indices[(triangle * 3) + 2]];
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));

            // Only the upward-facing side counts. A wall's inner face is not ground at any slope, and
            // the ceiling above a walkable floor would otherwise be walkable from underneath.
            areas[triangle] = normal.Y > threshold ? walkableArea : NavArea.Null;
        }
    }

    /// <summary>Rasterises a triangle soup into the field.</summary>
    /// <param name="vertices">The geometry's vertices.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="areas">One area id per triangle.</param>
    /// <param name="mergeThreshold">Passed through to <see cref="AddSpan" />.</param>
    public void RasterizeTriangles(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, ReadOnlySpan<byte> areas, int mergeThreshold) {
        for (var triangle = 0; triangle < areas.Length; triangle++) {
            RasterizeTriangle(
                vertices[indices[triangle * 3]],
                vertices[indices[(triangle * 3) + 1]],
                vertices[indices[(triangle * 3) + 2]],
                areas[triangle],
                mergeThreshold
            );
        }
    }

    /// <summary>Rasterises one triangle.</summary>
    /// <param name="a">Its first vertex.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="area">The area its surface carries.</param>
    /// <param name="mergeThreshold">Passed through to <see cref="AddSpan" />.</param>
    /// <remarks>
    ///     <para>
    ///         The triangle is clipped to one row of columns, then that row's piece is clipped to one
    ///         column, and the piece left over from each clip is what the next row and the next column
    ///         start from. Sampling the triangle at cell centres instead would miss a thin diagonal
    ///         wall entirely — the hole in the navmesh that lets an agent walk through it.
    ///     </para>
    ///     <para>
    ///         Seven vertices is the bound on a clipped triangle: three cuts against the four sides of
    ///         a cell can add at most four.
    ///     </para>
    /// </remarks>
    public void RasterizeTriangle(Vector3 a, Vector3 b, Vector3 c, byte area, int mergeThreshold) {
        var minimum = Vector3.Min(a, Vector3.Min(b, c));
        var maximum = Vector3.Max(a, Vector3.Max(b, c));

        if (minimum.X > Bounds.Maximum.X || maximum.X < Bounds.Minimum.X ||
            minimum.Z > Bounds.Maximum.Z || maximum.Z < Bounds.Minimum.Z ||
            maximum.Y < Bounds.Minimum.Y) {
            return;
        }

        var height = Bounds.Maximum.Y - Bounds.Minimum.Y;
        var inverseCellSize = 1f / CellSize;
        var inverseCellHeight = 1f / CellHeight;

        var firstRow = Math.Clamp((int)((minimum.Z - Bounds.Minimum.Z) * inverseCellSize), 0, Depth - 1);
        var lastRow = Math.Clamp((int)((maximum.Z - Bounds.Minimum.Z) * inverseCellSize), 0, Depth - 1);

        Span<Vector3> remaining = stackalloc Vector3[7];
        Span<Vector3> row = stackalloc Vector3[7];
        Span<Vector3> cell = stackalloc Vector3[7];
        Span<Vector3> scratch = stackalloc Vector3[7];

        remaining[0] = a;
        remaining[1] = b;
        remaining[2] = c;
        var remainingCount = 3;

        for (var z = firstRow; z <= lastRow && remainingCount >= 3; z++) {
            var rowMaximum = Bounds.Minimum.Z + ((z + 1) * CellSize);

            Divide(remaining[..remainingCount], row, out var rowCount, scratch, out var leftoverCount, rowMaximum, 2);
            scratch[..leftoverCount].CopyTo(remaining);
            remainingCount = leftoverCount;

            if (rowCount < 3) {
                continue;
            }

            var rowMinX = row[0].X;
            var rowMaxX = row[0].X;

            for (var index = 1; index < rowCount; index++) {
                rowMinX = MathF.Min(rowMinX, row[index].X);
                rowMaxX = MathF.Max(rowMaxX, row[index].X);
            }

            var firstColumn = Math.Clamp((int)((rowMinX - Bounds.Minimum.X) * inverseCellSize), 0, Width - 1);
            var lastColumn = Math.Clamp((int)((rowMaxX - Bounds.Minimum.X) * inverseCellSize), 0, Width - 1);

            for (var x = firstColumn; x <= lastColumn && rowCount >= 3; x++) {
                var columnMaximum = Bounds.Minimum.X + ((x + 1) * CellSize);

                Divide(row[..rowCount], cell, out var cellCount, scratch, out var rowLeftover, columnMaximum, 0);
                scratch[..rowLeftover].CopyTo(row);
                rowCount = rowLeftover;

                if (cellCount < 3) {
                    continue;
                }

                var low = cell[0].Y;
                var high = cell[0].Y;

                for (var index = 1; index < cellCount; index++) {
                    low = MathF.Min(low, cell[index].Y);
                    high = MathF.Max(high, cell[index].Y);
                }

                low -= Bounds.Minimum.Y;
                high -= Bounds.Minimum.Y;

                if (high < 0f || low > height) {
                    continue;
                }

                low = MathF.Max(low, 0f);
                high = MathF.Min(high, height);

                var spanMin = (ushort)Math.Clamp((int)MathF.Floor(low * inverseCellHeight), 0, MaxHeight - 1);
                var spanMax = (ushort)Math.Clamp((int)MathF.Ceiling(high * inverseCellHeight), spanMin + 1, MaxHeight);

                // Where in that top voxel the triangle actually was. A flat floor rounds up to the
                // voxel above it and then drops a whole voxel back down to itself, which is the one
                // cell of height every navmesh over a flat floor used to be reported at.
                var drop = (byte)Math.Clamp(
                    (int)MathF.Round((spanMax - (high * inverseCellHeight)) * HeightfieldSpan.DropScale),
                    0,
                    HeightfieldSpan.DropScale
                );

                AddSpan(x, z, spanMin, spanMax, area, mergeThreshold, drop);
            }
        }
    }

    /// <summary>Makes a small step onto an obstacle walkable, so an agent can climb onto it.</summary>
    /// <param name="walkableClimb">The agent's step height, in voxels.</param>
    /// <remarks>
    ///     The obstacle's own top is not walkable — it is a crate, and nothing marked it as ground —
    ///     but if it is within a step of the walkable floor beneath it then an agent can be on it, and
    ///     leaving it out puts a hole in the mesh in the shape of every kerb in the level.
    /// </remarks>
    public void FilterLowHangingWalkableObstacles(int walkableClimb) {
        for (var column = 0; column < columns.Length; column++) {
            var previousWalkable = false;
            var previousArea = NavArea.Null;
            var previousMax = 0;

            for (var index = columns[column]; index >= 0; index = spans[index].Next) {
                var span = spans[index];
                var walkable = span.Area != NavArea.Null;

                if (!walkable && previousWalkable && Math.Abs(span.Max - previousMax) <= walkableClimb) {
                    span.Area = previousArea;
                    spans[index] = span;
                }

                previousWalkable = walkable;
                previousArea = span.Area;
                previousMax = span.Max;
            }
        }
    }

    /// <summary>Unmarks the surface at the top of a drop, so an agent does not walk off it.</summary>
    /// <param name="walkableHeight">The agent's height, in voxels.</param>
    /// <param name="walkableClimb">The agent's step height, in voxels.</param>
    /// <remarks>
    ///     <para>
    ///         A span is a ledge when one of its neighbours is more than a step below it — the edge of
    ///         a roof, the lip of a pit. The polygons that would be built there are exactly the ones a
    ///         path would cut a corner across, and an agent following it would walk into the drop.
    ///     </para>
    ///     <para>
    ///         The second test catches the other shape: a span whose reachable neighbours disagree by
    ///         more than a step is the top of a staircase seen sideways, and standing on it is not the
    ///         same as being able to leave it in every direction.
    ///     </para>
    /// </remarks>
    public void FilterLedgeSpans(int walkableHeight, int walkableClimb) {
        for (var z = 0; z < Depth; z++) {
            for (var x = 0; x < Width; x++) {
                for (var index = First(x, z); index >= 0; index = spans[index].Next) {
                    var span = spans[index];

                    if (span.Area == NavArea.Null) {
                        continue;
                    }

                    var bottom = (int)span.Max;
                    var top = span.Next >= 0 ? spans[span.Next].Min : MaxHeight;

                    var lowestNeighbour = MaxHeight;
                    var lowestReachable = (int)span.Max;
                    var highestReachable = (int)span.Max;

                    for (var direction = 0; direction < 4; direction++) {
                        var neighbourX = x + OffsetX[direction];
                        var neighbourZ = z + OffsetZ[direction];

                        if (neighbourX < 0 || neighbourZ < 0 || neighbourX >= Width || neighbourZ >= Depth) {
                            lowestNeighbour = Math.Min(lowestNeighbour, -walkableClimb - bottom);

                            continue;
                        }

                        // The gap under the neighbour column's first span counts too: a column with
                        // nothing in it at all is a hole in the floor, not an absence of information.
                        var neighbourBottom = -walkableClimb;
                        var first = First(neighbourX, neighbourZ);
                        var neighbourTop = first >= 0 ? spans[first].Min : MaxHeight;

                        if (Math.Min(top, neighbourTop) - Math.Max(bottom, neighbourBottom) > walkableHeight) {
                            lowestNeighbour = Math.Min(lowestNeighbour, neighbourBottom - bottom);
                        }

                        for (var other = first; other >= 0; other = spans[other].Next) {
                            var neighbour = spans[other];
                            neighbourBottom = neighbour.Max;
                            neighbourTop = neighbour.Next >= 0 ? spans[neighbour.Next].Min : MaxHeight;

                            if (Math.Min(top, neighbourTop) - Math.Max(bottom, neighbourBottom) <= walkableHeight) {
                                continue;
                            }

                            var step = neighbourBottom - bottom;
                            lowestNeighbour = Math.Min(lowestNeighbour, step);

                            if (Math.Abs(step) <= walkableClimb) {
                                lowestReachable = Math.Min(lowestReachable, neighbourBottom);
                                highestReachable = Math.Max(highestReachable, neighbourBottom);
                            }
                        }
                    }

                    if (lowestNeighbour < -walkableClimb || highestReachable - lowestReachable > walkableClimb) {
                        span.Area = NavArea.Null;
                        spans[index] = span;
                    }
                }
            }
        }
    }

    /// <summary>Unmarks surfaces an agent cannot stand up on.</summary>
    /// <param name="walkableHeight">The agent's height, in voxels.</param>
    public void FilterWalkableLowHeightSpans(int walkableHeight) {
        for (var column = 0; column < columns.Length; column++) {
            for (var index = columns[column]; index >= 0; index = spans[index].Next) {
                var span = spans[index];
                var top = span.Next >= 0 ? spans[span.Next].Min : MaxHeight;

                if (top - span.Max < walkableHeight) {
                    span.Area = NavArea.Null;
                    spans[index] = span;
                }
            }
        }
    }

    int Allocate(HeightfieldSpan span) {
        if (freeSpan < 0) {
            spans.Add(span);

            return spans.Count - 1;
        }

        var index = freeSpan;
        freeSpan = spans[index].Next;
        spans[index] = span;

        return index;
    }

    void Release(int index) {
        var span = spans[index];
        span.Next = freeSpan;
        spans[index] = span;
        freeSpan = index;
    }

    /// <summary>Splits a convex polygon by an axis-aligned plane.</summary>
    /// <param name="input">The polygon.</param>
    /// <param name="below">Where to write the part on the low side of the plane.</param>
    /// <param name="belowCount">How much of it there was.</param>
    /// <param name="above">Where to write the part on the high side.</param>
    /// <param name="aboveCount">How much of that there was.</param>
    /// <param name="value">Where the plane is.</param>
    /// <param name="axis">Which axis it is perpendicular to: 0 for X, 2 for Z.</param>
    static void Divide(
        ReadOnlySpan<Vector3> input,
        Span<Vector3> below,
        out int belowCount,
        Span<Vector3> above,
        out int aboveCount,
        float value,
        int axis
    ) {
        Span<float> distances = stackalloc float[8];

        for (var index = 0; index < input.Length; index++) {
            distances[index] = value - input[index][axis];
        }

        belowCount = 0;
        aboveCount = 0;

        for (int index = 0, previous = input.Length - 1; index < input.Length; previous = index++) {
            var previousInside = distances[previous] >= 0;
            var currentInside = distances[index] >= 0;

            if (previousInside != currentInside) {
                var fraction = distances[previous] / (distances[previous] - distances[index]);
                var crossing = input[previous] + ((input[index] - input[previous]) * fraction);

                below[belowCount++] = crossing;
                above[aboveCount++] = crossing;

                if (distances[index] > 0) {
                    below[belowCount++] = input[index];
                } else if (distances[index] < 0) {
                    above[aboveCount++] = input[index];
                }

                continue;
            }

            if (distances[index] >= 0) {
                below[belowCount++] = input[index];

                if (distances[index] != 0) {
                    continue;
                }
            }

            above[aboveCount++] = input[index];
        }
    }
}
