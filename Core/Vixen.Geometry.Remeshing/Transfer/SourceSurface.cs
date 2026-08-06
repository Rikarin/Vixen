// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The surface a transfer queries: a triangle tree, and the way back to the mesh's corners.</summary>
/// <remarks>
///     <para>
///         <b><see cref="TriangleTree" /> answers in triangle indices and every attribute lives on a
///         corner</b>, so something has to hold the map between them.
///         <see cref="EditMesh.Triangulate" /> hands back <i>position</i> indices, which is what the
///         geometry needs and not what a per-corner normal is indexed by — and the two cannot be the
///         same array, because a seam is exactly the case where one position carries two different
///         values.
///     </para>
///     <para>
///         ⚠ <b>Every face produces exactly <c>Count − 2</c> triangles whatever route the ear
///         clipping took</b>, which is the invariant <see cref="EditMesh.Triangulate" />'s own
///         remarks call out, and it is what lets the faces and the triangles be walked together here
///         with no second table from the kernel.
///     </para>
///     <para>
///         ⚠ <b>Barycentric weights arrive summing to one, including on an edge, on a vertex and on
///         a triangle with no area</b> — <see cref="ClosestTriangle" />'s guarantee — so nothing
///         here renormalises them. A transfer that divided by their sum would divide by zero in
///         exactly the degenerate cases the guarantee exists to cover.
///     </para>
/// </remarks>
sealed class SourceSurface {
    readonly EditMesh mesh;
    readonly int[] triangles;
    readonly int[] faceOf;
    readonly int[] cornerOf;

    /// <summary>The tree the queries go through.</summary>
    public TriangleTree Tree { get; }

    /// <summary>How many triangles the surface has.</summary>
    public int TriangleCount => faceOf.Length;

    /// <summary>Whether the source carried a per-corner normal layer at all.</summary>
    public bool HasNormals { get; }

    /// <summary>Whether it carried texture coordinates.</summary>
    public bool HasTexCoords { get; }

    /// <summary>The bounding-box diagonal, which every relative tolerance here is a fraction of.</summary>
    public float Diagonal { get; }

    SourceSurface(EditMesh mesh, int[] triangles, int[] faceOf, int[] cornerOf) {
        this.mesh = mesh;
        this.triangles = triangles;
        this.faceOf = faceOf;
        this.cornerOf = cornerOf;

        Tree = new(mesh.Positions, triangles);
        HasNormals = mesh.Normals.Length == mesh.CornerCount && mesh.CornerCount > 0;
        HasTexCoords = mesh.TexCoords.Length == mesh.CornerCount && mesh.CornerCount > 0;

        var bounds = mesh.Bounds;
        Diagonal = mesh.PositionCount > 0 ? Vector3.Distance(bounds.Minimum, bounds.Maximum) : 0f;
    }

    /// <summary>Builds the surface over a mesh, triangulating it and keeping the way back.</summary>
    /// <param name="mesh">The mesh. Read, never modified.</param>
    /// <returns>The surface.</returns>
    public static SourceSurface From(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var triangles = mesh.Triangulate();
        var count = triangles.Length / 3;
        var faceOf = new int[count];
        var cornerOf = new int[count * 3];

        var at = 0;

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);
            var owned = Math.Max(entry.Count - 2, 0);

            for (var step = 0; step < owned; step++, at++) {
                faceOf[at] = face;

                for (var slot = 0; slot < 3; slot++) {
                    var position = triangles[(at * 3) + slot];
                    var corner = entry.Start;

                    // The first corner of this face standing on that position. A face that walks one
                    // position twice is degenerate and either answer is as good as the other; taking
                    // the first makes it the same answer on every run, which § D14 is about.
                    for (var index = 0; index < loop.Length; index++) {
                        if (loop[index] == position) {
                            corner = entry.Start + index;

                            break;
                        }
                    }

                    cornerOf[(at * 3) + slot] = corner;
                }
            }
        }

        return new(mesh, triangles, faceOf, cornerOf);
    }

    /// <summary>Which face a triangle came off.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The face index.</returns>
    public int FaceOf(int triangle) => faceOf[triangle];

    /// <summary>Which face group a triangle carries.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The group.</returns>
    public int GroupOf(int triangle) => mesh.Faces[faceOf[triangle]].Group;

    /// <summary>Which smoothing group a triangle carries.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The smoothing group.</returns>
    public int SmoothingOf(int triangle) => mesh.Faces[faceOf[triangle]].Smoothing;

    /// <summary>A triangle's three source corners, in the order its barycentric weights are in.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>Three corner indices.</returns>
    public ReadOnlySpan<int> CornersOf(int triangle) => cornerOf.AsSpan(triangle * 3, 3);

    /// <summary>A triangle's three positions, in the same order.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>Three position indices.</returns>
    public ReadOnlySpan<int> PositionsOf(int triangle) => triangles.AsSpan(triangle * 3, 3);

    /// <summary>A triangle's geometric normal, computed scale-safely.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The unit normal, or zero for a triangle with no area at all.</returns>
    /// <remarks>
    ///     ⚠ Edges are normalised <i>before</i> the cross product, which is
    ///     <see cref="ScaleSafe" />'s whole point: a cross product carries the model's units squared,
    ///     so a millimetre-scale mesh has cross products of <c>1e-9</c> and an absolute floor turns
    ///     every one of its normals into the zero vector.
    /// </remarks>
    public Vector3 NormalOf(int triangle) {
        var slots = PositionsOf(triangle);
        var origin = mesh.Positions[slots[0]];
        var one = ScaleSafe.Unit(mesh.Positions[slots[1]] - origin);
        var two = ScaleSafe.Unit(mesh.Positions[slots[2]] - origin);

        return one.LengthSquared() <= 0f || two.LengthSquared() <= 0f
            ? Vector3.Zero
            : ScaleSafe.Unit(Vector3.Cross(one, two));
    }

    /// <summary>The normal at a point on a triangle, interpolated from the source's corners.</summary>
    /// <param name="triangle">Which triangle.</param>
    /// <param name="barycentric">Where on it, as weights summing to one.</param>
    /// <returns>The unit normal.</returns>
    /// <remarks>
    ///     ⚠ <b>The geometric normal when the source has no normal layer, and that is the honest
    ///     answer rather than a zero.</b> A source with no normals is a real input — a boolean
    ///     result, a marching-cubes surface, an <c>.obj</c> with no <c>vn</c> lines — and handing
    ///     back <see cref="Vector3.Zero" /> would write a mesh whose every shading normal is
    ///     degenerate.
    /// </remarks>
    public Vector3 NormalAt(int triangle, Vector3 barycentric) {
        if (!HasNormals) {
            return NormalOf(triangle);
        }

        var slots = CornersOf(triangle);
        var normals = mesh.Normals;

        var sum = (normals[slots[0]] * barycentric.X)
            + (normals[slots[1]] * barycentric.Y)
            + (normals[slots[2]] * barycentric.Z);

        var unit = ScaleSafe.Unit(sum);

        // Three corner normals that cancel — a source whose normals were authored inconsistently, or
        // a fold — leave nothing to normalise. The face it sits on still has a direction.
        return unit.LengthSquared() > 0f ? unit : NormalOf(triangle);
    }

    /// <summary>The texture coordinate at a point on a triangle.</summary>
    /// <param name="triangle">Which triangle.</param>
    /// <param name="barycentric">Where on it.</param>
    /// <returns>The coordinate, or zero when the source had none.</returns>
    public Vector2 TexCoordAt(int triangle, Vector3 barycentric) {
        if (!HasTexCoords) {
            return Vector2.Zero;
        }

        var slots = CornersOf(triangle);
        var coordinates = mesh.TexCoords;

        return (coordinates[slots[0]] * barycentric.X)
            + (coordinates[slots[1]] * barycentric.Y)
            + (coordinates[slots[2]] * barycentric.Z);
    }

    /// <summary>A triangle's area.</summary>
    /// <param name="triangle">Its index.</param>
    /// <returns>The area, in the mesh's own units squared.</returns>
    public float Area(int triangle) {
        var slots = PositionsOf(triangle);
        var origin = mesh.Positions[slots[0]];

        return Vector3.Cross(mesh.Positions[slots[1]] - origin, mesh.Positions[slots[2]] - origin).Length() * 0.5f;
    }
}
