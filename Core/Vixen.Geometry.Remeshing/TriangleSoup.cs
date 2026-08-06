// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Positions, triangles and a group per triangle, in flat lists the conditioning steps rewrite.</summary>
/// <remarks>
///     <para>
///         <b>The scratch form the seven steps of docs/plan/41 § D3 pass between one another.</b> A
///         weld renumbers every index, a de-speck deletes triangles, a cut adds positions and an
///         isotropic round does all three — none of which an <see cref="EditMesh" /> is a convenient
///         shape for, and none of which the read-only <see cref="ManifoldMesh" /> permits at all.
///     </para>
///     <para>
///         ⚠ <b>Nothing is built from an <see cref="EditMesh" /> a face at a time.</b>
///         <c>EditMesh</c> rebuilds its whole adjacency behind a dirty flag and every accessor
///         triggers it, so a loop that alternates <c>AddFace</c> and <c>EdgesAt</c> pays a full
///         rebuild per face. Conditioning reads the source's arrays once, works here, and builds the
///         edge tables exactly twice — once for the view it hands on, once for the report.
///     </para>
/// </remarks>
sealed class TriangleSoup {
    /// <summary>The positions, shared between triangles.</summary>
    public List<Vector3> Positions { get; init; } = [];

    /// <summary>Three position indices per triangle, in winding order.</summary>
    public List<int> Triangles { get; init; } = [];

    /// <summary>One group per triangle, carried from the source so § D4 can still read it.</summary>
    /// <remarks>
    ///     Face groups are one of the five feature sources, so throwing them away in stage one would
    ///     silently remove a feature source from stage two. A split inherits its parent's group; a
    ///     collapse keeps the survivor's; a shrinkwrap has none and says so by writing zero.
    /// </remarks>
    public List<int> Groups { get; init; } = [];

    /// <summary>How many triangles there are.</summary>
    public int TriangleCount => Triangles.Count / 3;

    /// <summary>The box round every position, or an empty one when there are none.</summary>
    public BoundingBox Bounds {
        get {
            if (Positions.Count == 0) {
                return default;
            }

            var low = Positions[0];
            var high = Positions[0];

            foreach (var position in Positions) {
                low = Vector3.Min(low, position);
                high = Vector3.Max(high, position);
            }

            return new(low, high);
        }
    }

    /// <summary>The bounding box's diagonal, which every tolerance in conditioning is a fraction of.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the number that makes conditioning scale-free, and it is the single most
    ///     likely place for the bug docs/plan/41 § D3 names.</b> Doc 24 records the same lesson twice
    ///     — <c>EditMesh.DefaultWeldTolerance</c> and the capsule's poles, where an absolute epsilon
    ///     declared sixty-four real triangles degenerate. A fixed epsilon is a claim about how big a
    ///     model is.
    /// </remarks>
    public float Diagonal {
        get {
            var extent = Bounds;

            return (extent.Maximum - extent.Minimum).Length();
        }
    }

    /// <summary>A copy of the source's positions and its triangulation, with the groups beside it.</summary>
    /// <param name="source">The mesh to read. Read once, and no adjacency accessor is touched.</param>
    /// <returns>The soup.</returns>
    public static TriangleSoup From(EditMesh source) {
        ArgumentNullException.ThrowIfNull(source);

        var soup = new TriangleSoup();

        soup.Positions.AddRange(source.Positions);

        var indices = source.Triangulate();

        soup.Triangles.AddRange(indices);

        // `Triangulate` emits exactly `Count - 2` triangles per face in face order, which is the
        // one-to-one walk `EditMesh.Triangulate` documents and the only reason a group per triangle
        // can be recovered without a second table.
        for (var face = 0; face < source.FaceCount; face++) {
            var count = Math.Max(source.Faces[face].Count - 2, 0);

            for (var at = 0; at < count; at++) {
                soup.Groups.Add(source.Faces[face].Group);
            }
        }

        return soup;
    }

    /// <summary>Adds one triangle and its group.</summary>
    /// <param name="a">Its first corner.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="group">Which group it belongs to.</param>
    public void Add(int a, int b, int c, int group) {
        Triangles.Add(a);
        Triangles.Add(b);
        Triangles.Add(c);
        Groups.Add(group);
    }

    /// <summary>Drops every position no triangle names, and renumbers what is left.</summary>
    /// <returns>How many positions went away.</returns>
    /// <remarks>
    ///     An orphan is not wrong, but it is a position the field solve would allocate a tangent frame
    ///     and a cross for and never visit — so conditioning removes them rather than handing them on.
    /// </remarks>
    public int Compact() {
        var remap = new int[Positions.Count];

        Array.Fill(remap, -1);

        var kept = new List<Vector3>(Positions.Count);

        foreach (var index in Triangles) {
            if (remap[index] < 0) {
                remap[index] = kept.Count;
                kept.Add(Positions[index]);
            }
        }

        var dropped = Positions.Count - kept.Count;

        for (var at = 0; at < Triangles.Count; at++) {
            Triangles[at] = remap[Triangles[at]];
        }

        Positions.Clear();
        Positions.AddRange(kept);

        return dropped;
    }

    /// <summary>Twice a triangle's area, as a vector, which is the sum a normal is derived from.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The cross product of two of its edges.</returns>
    public Vector3 Cross(int triangle) {
        var a = Positions[Triangles[(triangle * 3) + 0]];
        var b = Positions[Triangles[(triangle * 3) + 1]];
        var c = Positions[Triangles[(triangle * 3) + 2]];

        return Vector3.Cross(b - a, c - a);
    }

    /// <summary>A triangle's area.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The area, in world units squared.</returns>
    public float Area(int triangle) => Cross(triangle).Length() * 0.5f;
}
