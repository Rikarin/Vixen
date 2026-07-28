// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Baking;

/// <summary>The outline of one region, in voxel coordinates.</summary>
/// <remarks>
///     Four numbers per vertex: where it is, and which region lies across the edge that starts there.
///     That last one is what tells the simplifier which parts of the outline are a wall — free to
///     move, within the error tolerance — and which are a border with another region, where moving a
///     vertex would tear the two apart.
/// </remarks>
internal sealed class Contour {
    /// <summary>Four ints per vertex: X, Y, Z in voxels, then the region across the edge leaving it.</summary>
    public int[] Vertices { get; init; } = [];

    /// <summary>How many vertices there are.</summary>
    public int VertexCount => Vertices.Length / 4;

    /// <summary>The region this outlines.</summary>
    public ushort Region { get; init; }

    /// <summary>The area of the surface inside it.</summary>
    public byte Area { get; init; }
}

/// <summary>
///     Every region's outline, simplified from the voxel staircase into something worth making
///     polygons out of.
/// </summary>
/// <remarks>
///     <para>
///         Tracing is exact and produces a vertex per voxel corner; simplification is where the
///         decisions are. A wall's outline may be moved by up to the error tolerance, because nothing
///         depends on exactly which voxel it ran through. A border with another region may not be
///         moved at all: both regions traced it, and if they simplified it differently the polygons
///         either side would no longer meet, leaving a crack an agent's path can fall through.
///     </para>
///     <para>
///         Recast's algorithm — trace, then keep the region-border vertices and add back whichever
///         wall vertex deviates most, until none deviates by more than the tolerance. The code is
///         Vixen's.
///     </para>
/// </remarks>
internal sealed class ContourSet {
    ContourSet(CompactHeightfield field) {
        Bounds = field.Bounds;
        CellSize = field.CellSize;
        CellHeight = field.CellHeight;
        Width = field.Width;
        Depth = field.Depth;
    }

    /// <summary>The outlines.</summary>
    public List<Contour> Contours { get; } = [];

    /// <summary>The volume the voxel coordinates are relative to.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>The width and depth of a column.</summary>
    public float CellSize { get; }

    /// <summary>The height of a voxel.</summary>
    public float CellHeight { get; }

    /// <summary>How many columns across X.</summary>
    public int Width { get; }

    /// <summary>How many columns across Z.</summary>
    public int Depth { get; }

    /// <summary>Traces and simplifies the outline of every region.</summary>
    /// <param name="field">The partitioned surface.</param>
    /// <param name="maxError">How far a wall vertex may be moved, in voxels.</param>
    /// <param name="maxEdgeLength">The longest wall edge to leave unsplit, in voxels. Zero disables splitting.</param>
    /// <returns>The contours.</returns>
    public static ContourSet Build(CompactHeightfield field, float maxError, float maxEdgeLength) {
        var set = new ContourSet(field);
        var flags = new byte[field.Spans.Length];

        // An edge is part of an outline when the span across it belongs to a different region — or to
        // nothing at all, which is the same answer from the point of view of the region being traced.
        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                ref var cell = ref field.Cells[x + (z * field.Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    if (field.Spans[index].Region == 0 || field.Areas[index] == NavArea.Null) {
                        flags[index] = 0;

                        continue;
                    }

                    var shared = 0;

                    for (var direction = 0; direction < 4; direction++) {
                        var neighbour = field.Neighbour(index, x, z, direction);
                        var region = neighbour >= 0 ? field.Spans[neighbour].Region : 0;

                        if (region == field.Spans[index].Region) {
                            shared |= 1 << direction;
                        }
                    }

                    flags[index] = (byte)(shared ^ 0xf);
                }
            }
        }

        var raw = new List<int>();
        var simplified = new List<int>();

        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                ref var cell = ref field.Cells[x + (z * field.Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    // Nothing to trace, or a span whose every side is an outline — a single isolated
                    // voxel, which cannot make a polygon and would leave a degenerate contour behind.
                    if (flags[index] is 0 or 0xf) {
                        flags[index] = 0;

                        continue;
                    }

                    var region = field.Spans[index].Region;
                    var area = field.Areas[index];

                    raw.Clear();
                    simplified.Clear();

                    var direction = 0;

                    while ((flags[index] & (1 << direction)) == 0) {
                        direction++;
                    }

                    Walk(field, flags, x, z, index, direction, raw);
                    Simplify(raw, simplified, maxError, maxEdgeLength);

                    if (simplified.Count / 4 < 3) {
                        continue;
                    }

                    set.Contours.Add(new() { Vertices = [.. simplified], Region = region, Area = area });
                }
            }
        }

        return set;
    }

    /// <summary>Walks one region's boundary, turning at every outline edge.</summary>
    /// <remarks>
    ///     The walk keeps the region on one side: it emits a vertex and turns one way whenever the
    ///     edge it faces is an outline edge, and steps into the neighbouring span and turns the other
    ///     way when it is not. Doing that until it arrives back where it started, facing the way it
    ///     started, traces the boundary exactly once — including the parts of it that touch at a
    ///     corner, which is why the loop terminates on the direction as well as the position.
    /// </remarks>
    static void Walk(CompactHeightfield field, byte[] flags, int startX, int startZ, int startIndex, int startDirection, List<int> points) {
        var x = startX;
        var z = startZ;
        var index = startIndex;
        var direction = startDirection;
        var area = field.Areas[startIndex];

        while (true) {
            if ((flags[index] & (1 << direction)) != 0) {
                var cornerX = x;
                var cornerZ = z;
                var cornerY = CornerHeight(field, x, z, index, direction);

                switch (direction) {
                    case 0:
                        cornerZ++;

                        break;
                    case 1:
                        cornerX++;
                        cornerZ++;

                        break;
                    case 2:
                        cornerX++;

                        break;
                }

                var neighbour = field.Neighbour(index, x, z, direction);
                var region = 0;

                if (neighbour >= 0) {
                    region = field.Spans[neighbour].Region;

                    if (field.Areas[neighbour] != area) {
                        region |= AreaBorder;
                    }
                }

                points.Add(cornerX);
                points.Add(cornerY);
                points.Add(cornerZ);
                points.Add(region);

                flags[index] &= (byte)~(1 << direction);
                direction = (direction + 1) & 3;
            } else {
                var neighbour = field.Neighbour(index, x, z, direction);

                if (neighbour < 0) {
                    break;
                }

                x += CompactHeightfield.OffsetX[direction];
                z += CompactHeightfield.OffsetZ[direction];
                index = neighbour;
                direction = (direction + 3) & 3;
            }

            if (index == startIndex && direction == startDirection) {
                break;
            }
        }
    }

    /// <summary>The height of a corner shared by up to four spans: the highest of them.</summary>
    /// <remarks>
    ///     The highest, because the contour is the top of a surface and a vertex that took the lowest
    ///     of the four would sink the polygon's corner below the floor it belongs to. Where the four
    ///     disagree by more than a step they are not one surface, and the region boundary is between
    ///     them anyway.
    /// </remarks>
    static int CornerHeight(CompactHeightfield field, int x, int z, int index, int direction) {
        var height = (int)field.Spans[index].Y;
        var next = (direction + 1) & 3;

        var alongDirection = field.Neighbour(index, x, z, direction);

        if (alongDirection >= 0) {
            height = Math.Max(height, field.Spans[alongDirection].Y);

            var diagonal = field.Neighbour(
                alongDirection,
                x + CompactHeightfield.OffsetX[direction],
                z + CompactHeightfield.OffsetZ[direction],
                next
            );

            if (diagonal >= 0) {
                height = Math.Max(height, field.Spans[diagonal].Y);
            }
        }

        var alongNext = field.Neighbour(index, x, z, next);

        if (alongNext >= 0) {
            height = Math.Max(height, field.Spans[alongNext].Y);

            var diagonal = field.Neighbour(
                alongNext,
                x + CompactHeightfield.OffsetX[next],
                z + CompactHeightfield.OffsetZ[next],
                direction
            );

            if (diagonal >= 0) {
                height = Math.Max(height, field.Spans[diagonal].Y);
            }
        }

        return height;
    }

    /// <summary>The bit that marks an edge as the boundary between two areas rather than two regions.</summary>
    const int AreaBorder = 0x20000;

    /// <summary>Everything below that is the region id.</summary>
    const int RegionMask = 0xffff;

    /// <summary>Reduces a traced outline to the vertices that matter.</summary>
    static void Simplify(List<int> raw, List<int> simplified, float maxError, float maxEdgeLength) {
        var count = raw.Count / 4;

        if (count < 3) {
            return;
        }

        var connected = false;

        for (var index = 0; index < count; index++) {
            if ((raw[(index * 4) + 3] & RegionMask) != 0) {
                connected = true;

                break;
            }
        }

        if (connected) {
            // Every point where the neighbour changes has to survive, because the region on the other
            // side has traced the same corner and will keep it too.
            for (var index = 0; index < count; index++) {
                var next = (index + 1) % count;
                var here = raw[(index * 4) + 3];
                var there = raw[(next * 4) + 3];

                if ((here & RegionMask) != (there & RegionMask) || (here & AreaBorder) != (there & AreaBorder)) {
                    Add(simplified, raw, index);
                }
            }
        }

        if (simplified.Count == 0) {
            // A contour with no neighbours at all — an island. Any two opposite points will do to
            // start from, and the extremes are the two that cannot be simplified away.
            var lowerLeft = 0;
            var upperRight = 0;

            for (var index = 1; index < count; index++) {
                var x = raw[index * 4];
                var z = raw[(index * 4) + 2];

                if (x < raw[lowerLeft * 4] || (x == raw[lowerLeft * 4] && z < raw[(lowerLeft * 4) + 2])) {
                    lowerLeft = index;
                }

                if (x > raw[upperRight * 4] || (x == raw[upperRight * 4] && z > raw[(upperRight * 4) + 2])) {
                    upperRight = index;
                }
            }

            Add(simplified, raw, lowerLeft);
            Add(simplified, raw, upperRight);
        }

        AddDeviations(raw, simplified, maxError);
        SplitLongEdges(raw, simplified, maxEdgeLength);

        // The region recorded against a simplified vertex is the one across the edge that leaves it,
        // which is the traced point's own — the vertices between them were dropped, and they all
        // faced the same neighbour or they would not have been.
        for (var index = 0; index < simplified.Count / 4; index++) {
            simplified[(index * 4) + 3] = raw[(simplified[(index * 4) + 3] * 4) + 3];
        }
    }

    /// <summary>Puts back whichever dropped vertex strays furthest, until none strays too far.</summary>
    static void AddDeviations(List<int> raw, List<int> simplified, float maxError) {
        var count = raw.Count / 4;

        for (var index = 0; index < simplified.Count / 4;) {
            var next = (index + 1) % (simplified.Count / 4);

            var startX = simplified[index * 4];
            var startZ = simplified[(index * 4) + 2];
            var startRaw = simplified[(index * 4) + 3];

            var endX = simplified[next * 4];
            var endZ = simplified[(next * 4) + 2];
            var endRaw = simplified[(next * 4) + 3];

            int step;
            int current;
            int last;

            // Walked in a fixed direction in world terms rather than in contour terms, so that the
            // two regions sharing a border walk it the same way and simplify it identically.
            if (endX > startX || (endX == startX && endZ > startZ)) {
                step = 1;
                current = (startRaw + 1) % count;
                last = endRaw;
            } else {
                step = count - 1;
                current = (endRaw + step) % count;
                last = startRaw;
                (startX, endX) = (endX, startX);
                (startZ, endZ) = (endZ, startZ);
            }

            var furthest = -1;
            var furthestDistance = 0f;

            if ((raw[(current * 4) + 3] & RegionMask) == 0 || (raw[(current * 4) + 3] & AreaBorder) != 0) {
                while (current != last) {
                    var distance = DistanceToSegmentSquared(raw[current * 4], raw[(current * 4) + 2], startX, startZ, endX, endZ);

                    if (distance > furthestDistance) {
                        furthestDistance = distance;
                        furthest = current;
                    }

                    current = (current + step) % count;
                }
            }

            if (furthest >= 0 && furthestDistance > maxError * maxError) {
                Insert(simplified, raw, index + 1, furthest);
            } else {
                index++;
            }
        }
    }

    /// <summary>Splits wall edges that got too long to describe the wall well.</summary>
    static void SplitLongEdges(List<int> raw, List<int> simplified, float maxEdgeLength) {
        if (maxEdgeLength <= 0) {
            return;
        }

        var count = raw.Count / 4;

        for (var index = 0; index < simplified.Count / 4;) {
            var next = (index + 1) % (simplified.Count / 4);

            var startX = simplified[index * 4];
            var startZ = simplified[(index * 4) + 2];
            var startRaw = simplified[(index * 4) + 3];

            var endX = simplified[next * 4];
            var endZ = simplified[(next * 4) + 2];
            var endRaw = simplified[(next * 4) + 3];

            var middle = -1;
            var following = (startRaw + 1) % count;

            // Only walls. A border with another region has to stay exactly as the other region drew
            // it, and splitting it here would be a disagreement about where the two meet.
            if ((raw[(following * 4) + 3] & RegionMask) == 0) {
                var dx = endX - startX;
                var dz = endZ - startZ;

                if ((dx * dx) + (dz * dz) > maxEdgeLength * maxEdgeLength) {
                    var between = endRaw < startRaw ? endRaw + count - startRaw : endRaw - startRaw;

                    if (between > 1) {
                        middle = endX > startX || (endX == startX && endZ > startZ)
                            ? (startRaw + (between / 2)) % count
                            : (startRaw + ((between + 1) / 2)) % count;
                    }
                }
            }

            if (middle >= 0) {
                Insert(simplified, raw, index + 1, middle);
            } else {
                index++;
            }
        }
    }

    static void Add(List<int> simplified, List<int> raw, int rawIndex) {
        simplified.Add(raw[rawIndex * 4]);
        simplified.Add(raw[(rawIndex * 4) + 1]);
        simplified.Add(raw[(rawIndex * 4) + 2]);
        simplified.Add(rawIndex);
    }

    static void Insert(List<int> simplified, List<int> raw, int position, int rawIndex) {
        simplified.Insert(position * 4, rawIndex);
        simplified.Insert(position * 4, raw[(rawIndex * 4) + 2]);
        simplified.Insert(position * 4, raw[(rawIndex * 4) + 1]);
        simplified.Insert(position * 4, raw[rawIndex * 4]);
    }

    static float DistanceToSegmentSquared(int pointX, int pointZ, int startX, int startZ, int endX, int endZ) {
        float dx = endX - startX;
        float dz = endZ - startZ;
        float px = pointX - startX;
        float pz = pointZ - startZ;

        var lengthSquared = (dx * dx) + (dz * dz);
        var t = lengthSquared > 0 ? Math.Clamp(((px * dx) + (pz * dz)) / lengthSquared, 0f, 1f) : 0f;

        var offsetX = px - (dx * t);
        var offsetZ = pz - (dz * t);

        return (offsetX * offsetX) + (offsetZ * offsetZ);
    }
}
