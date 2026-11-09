// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Which of the source's edges a rounded-edge bake rounds.</summary>
public enum RoundedEdges : byte {
    /// <summary>Every edge whose two faces meet at <see cref="BakeRounding.Angle" /> or more.</summary>
    Angle,

    /// <summary>The edges the source's own normals split, which is where its author made it hard.</summary>
    /// <remarks>
    ///     ⚠ <b>The nearest thing an interchange file has to an edge weight.</b> glTF and OBJ carry no
    ///     per-edge bevel weight, and a split normal or a smoothing-group border is the hard edge an
    ///     artist marked. A source with no normals has its smoothing groups read instead.
    /// </remarks>
    Hard
}

/// <summary>Edges baked round into the normal, with no high poly modelled to give them a bevel.</summary>
/// <param name="Width">How far either side of an edge the bevel reaches, in <see cref="BakeSettings.Units" />.</param>
/// <param name="Edges">Which edges are rounded.</param>
/// <param name="Angle">For <see cref="RoundedEdges.Angle" />: the least angle between two faces that is an edge, in degrees.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The normal turns, the surface does not move.</b> Within <paramref name="Width" /> of a
///         rounded edge the source's normal leans towards the direction halfway between the edge's two
///         faces, evenly with distance, and reaches it on the edge — so both sides arrive at the same
///         normal and the edge shades as a continuous turn. The displacement is untouched: a rounded-edge
///         bake is a shading answer, the one a hard-surface artist wants instead of modelling a bevel.
///     </para>
///     <para>
///         ⚠ <b>The lean is added to the source's own normal, not put in its place.</b> A smooth-shaded
///         source keeps its interpolated normal everywhere a bevel does not reach, and the lean starts at
///         nothing at the bevel's edge, so the map has no step where the band begins.
///     </para>
/// </remarks>
public sealed record BakeRounding(float Width, RoundedEdges Edges = RoundedEdges.Angle, float Angle = 30f);

/// <summary>The rounded edges of a source, found once and asked about per texel from any thread.</summary>
/// <remarks>
///     ⚠ <b>A grid of cells as wide as the bevel</b>, so a point asks only the edges in its own cell and
///     every edge it could be within reach of is there. A long edge is in every cell along it, which is
///     the price of asking one cell; a grid coarser than the width would have the point walk neighbours.
/// </remarks>
sealed class EdgeRounding {
    /// <summary>How far from a face's plane a rounded edge's face may be and still be the face a point is on.</summary>
    /// <remarks>
    ///     ⚠ <b>Coplanar, not adjacent.</b> A quad split into two triangles puts a point on a triangle that
    ///     does not touch the edge, and it is still the face the bevel runs across. The cosine of about
    ///     two and a half degrees: a facet of a curved surface further round than that is another face.
    /// </remarks>
    const float SameFace = 0.999f;

    /// <summary>About how many grid entries the edges may make before the grid is coarsened.</summary>
    const int MostCells = 1 << 20;

    readonly Segment[] segments;
    readonly Dictionary<(int, int, int), List<int>> cells;
    readonly float width;
    readonly float cell;

    EdgeRounding(Segment[] segments, Dictionary<(int, int, int), List<int>> cells, float width, float cell) {
        this.segments = segments;
        this.cells = cells;
        this.width = width;
        this.cell = cell;
    }

    /// <summary>How many edges are rounded.</summary>
    public int Count => segments.Length;

    /// <summary>Finds the edges to round.</summary>
    /// <param name="source">The source mesh.</param>
    /// <param name="rounding">What to round.</param>
    /// <param name="width">The bevel's width as a length in the model's units.</param>
    /// <returns>The edges, or null when there is nothing to round.</returns>
    public static EdgeRounding? Build(EditMesh source, BakeRounding rounding, float width) {
        if (!(width > 0f) || source.FaceCount == 0) {
            return null;
        }

        var normals = source.Normals.Length == source.CornerCount && source.CornerCount > 0;
        var least = MathF.Cos(Math.Clamp(rounding.Angle, 0f, 180f) * (MathF.PI / 180f));
        var edges = source.Edges;
        List<Segment> found = [];

        for (var edge = 0; edge < edges.Count; edge++) {
            var faces = source.FacesOf(edge);

            // An open rim has one face and nothing to turn towards; three or more faces is not a surface.
            if (faces.Length != 2) {
                continue;
            }

            var (one, two) = (faces[0], faces[1]);
            var left = ScaleSafe.Unit(source.Normal(one));
            var right = ScaleSafe.Unit(source.Normal(two));

            if (left.LengthSquared() <= 0f || right.LengthSquared() <= 0f) {
                continue;
            }

            var (a, b) = (edges[edge].A, edges[edge].B);
            var round = rounding.Edges is RoundedEdges.Hard
                ? Split(source, normals, one, two, a, b)
                : Vector3.Dot(left, right) <= least;

            // Two faces that agree have no edge to turn round, whatever the normals say.
            if (round && Vector3.Dot(left, right) < 0.99999f) {
                found.Add(new(source.Positions[a], source.Positions[b], left, right));
            }
        }

        if (found.Count == 0) {
            return null;
        }

        var size = width;
        var total = found.Sum(static segment => Vector3.Distance(segment.A, segment.B));

        // ⚠ A bevel far narrower than the model's edges puts each in a great many cells, about nine for
        // every width of its length; past the limit the cells grow, and a point walks a few more edges
        // instead of the grid taking the memory.
        while (((total / size) + (3f * found.Count)) * 9f > MostCells) {
            size *= 2f;
        }

        Dictionary<(int, int, int), List<int>> cells = [];

        for (var at = 0; at < found.Count; at++) {
            var segment = found[at];
            var low = Cell(Vector3.Min(segment.A, segment.B) - new Vector3(width), size);
            var high = Cell(Vector3.Max(segment.A, segment.B) + new Vector3(width), size);

            for (var x = low.X; x <= high.X; x++) {
                for (var y = low.Y; y <= high.Y; y++) {
                    for (var z = low.Z; z <= high.Z; z++) {
                        // Only cells the segment's reach can touch: a diagonal edge's box is mostly empty.
                        var centre = (new Vector3(x, y, z) + new Vector3(0.5f)) * size;

                        if (Distance(segment, centre) > width + (size * 0.8661f)) {
                            continue;
                        }

                        if (!cells.TryGetValue((x, y, z), out var list)) {
                            list = [];
                            cells.Add((x, y, z), list);
                        }

                        list.Add(at);
                    }
                }
            }
        }

        return new([.. found], cells, width, size);
    }

    /// <summary>Whether the source's normals or smoothing groups split along an edge.</summary>
    static bool Split(EditMesh source, bool normals, int one, int two, int a, int b) {
        if (!normals) {
            return source.Faces[one].Smoothing != source.Faces[two].Smoothing;
        }

        foreach (var position in (ReadOnlySpan<int>) [a, b]) {
            var first = CornerNormal(source, one, position);
            var second = CornerNormal(source, two, position);

            if (Vector3.Dot(ScaleSafe.Unit(first), ScaleSafe.Unit(second)) < 0.9999f) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The normal a face's corner on a position carries.</summary>
    static Vector3 CornerNormal(EditMesh source, int face, int position) {
        var loop = source.CornersOf(face);

        for (var index = 0; index < loop.Length; index++) {
            if (loop[index] == position) {
                return source.Normals[source.Faces[face].Start + index];
            }
        }

        return source.Normal(face);
    }

    /// <summary>The normal at a point of the source once the bevels within reach have turned it.</summary>
    /// <param name="point">The point, on the source.</param>
    /// <param name="face">The geometric normal of the face it is on.</param>
    /// <param name="normal">The source's own normal there.</param>
    /// <returns>The normal, turned towards every rounded edge within <see cref="BakeRounding.Width" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Turns add.</b> At a corner two or three bevels reach the same point and each leans the
    ///     normal its own way, so the corner rounds towards all of them, as a ball-ended fillet does.
    /// </remarks>
    public Vector3 Apply(Vector3 point, Vector3 face, Vector3 normal) {
        if (!cells.TryGetValue(Cell(point, cell), out var near)) {
            return normal;
        }

        var lean = Vector3.Zero;

        foreach (var at in near) {
            var segment = segments[at];
            var distance = Distance(segment, point);

            if (distance >= width) {
                continue;
            }

            var leftward = Vector3.Dot(face, segment.Left);
            var rightward = Vector3.Dot(face, segment.Right);
            var (mine, other) = leftward >= rightward ? (segment.Left, segment.Right) : (segment.Right, segment.Left);

            // The point must be on one of the edge's two faces, or a face in the same plane: a bevel on the
            // far side of a thin plate is within reach and is not this surface's edge.
            if (MathF.Max(leftward, rightward) < SameFace) {
                continue;
            }

            var half = ScaleSafe.Unit(mine + other);

            if (half.LengthSquared() <= 0f) {
                continue;
            }

            // Evenly with distance: nothing at the band's edge, halfway between the faces on the edge itself.
            var turned = Slerp(mine, half, 1f - (distance / width));

            lean += turned - mine;
        }

        if (lean.LengthSquared() <= 0f) {
            return normal;
        }

        var leaned = ScaleSafe.Unit(normal + lean);

        return leaned.LengthSquared() > 0f ? leaned : normal;
    }

    static (int X, int Y, int Z) Cell(Vector3 point, float size) =>
        ((int) MathF.Floor(point.X / size), (int) MathF.Floor(point.Y / size), (int) MathF.Floor(point.Z / size));

    /// <summary>How far a point is from a segment.</summary>
    static float Distance(Segment segment, Vector3 point) {
        var along = segment.B - segment.A;
        var length = along.LengthSquared();
        var t = length > 0f ? Math.Clamp(Vector3.Dot(point - segment.A, along) / length, 0f, 1f) : 0f;

        return Vector3.Distance(point, segment.A + (along * t));
    }

    /// <summary>Turns one unit direction a share of the way to another, at an even rate.</summary>
    static Vector3 Slerp(Vector3 from, Vector3 to, float share) {
        var cosine = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        var angle = MathF.Acos(cosine);

        if (angle < 1e-5f) {
            return to;
        }

        var sine = MathF.Sin(angle);

        return (from * (MathF.Sin((1f - share) * angle) / sine)) + (to * (MathF.Sin(share * angle) / sine));
    }

    /// <summary>A rounded edge: its ends and the normals of the faces either side.</summary>
    readonly record struct Segment(Vector3 A, Vector3 B, Vector3 Left, Vector3 Right);
}
