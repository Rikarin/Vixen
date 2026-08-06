// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Geometry.Uv;
using Vixen.Rendering;

namespace Vixen.Editor.Assets.Models;

/// <summary>The copy between a model file's geometry and the mesh kernel that remeshes and unwraps it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/24 § D1 puts this copy in the caller rather than in the kernel, and this is the
///         importer's copy of it.</b> <c>Core/Vixen.Geometry</c> depends on
///         <c>Vixen.Core.Mathematics</c> and nothing else, so it cannot name <see cref="MeshData" />;
///         <c>Vixen.Editor.SceneView</c> makes the same six-line trip for the viewport, and this
///         assembly makes it for the import and for <c>vixen remesh</c>. Two short copies on either
///         side of a layering line are the price of the line, and the line is what lets a content
///         build link the remesher without linking a renderer.
///     </para>
///     <para>
///         ⚠ <b>A drawn vertex is a corner and not a position, so the trip out is not the trip in
///         reversed.</b> Going in, a cube's twenty-four entries weld to eight positions. Coming out,
///         eight positions expand to one vertex per corner again, because a normal and a texture
///         coordinate belong to a corner — a cube drawn from eight shared vertices is a cube lit as a
///         very lumpy sphere, and one atlased from eight shared vertices has no seams and therefore no
///         atlas.
///     </para>
///     <para>
///         ⚠ <b>The bounds are recomputed rather than carried across.</b> A remesh moves every vertex,
///         and a <see cref="MeshData.Bounds" /> left over from the source is a culling volume that does
///         not contain its mesh — which shows up as an object that disappears when you look at it from
///         the wrong side and never as a geometry bug.
///     </para>
/// </remarks>
public static class ModelGeometry {
    /// <summary>Reads a model file's mesh into the kernel.</summary>
    /// <param name="mesh">The geometry, as the reader produced it.</param>
    /// <param name="weld">How near two positions may be and still be one, as a fraction of the bounds.</param>
    /// <returns>The mesh.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public static EditMesh ToEditMesh(MeshData mesh, float weld = EditMesh.DefaultWeldTolerance) {
        ArgumentNullException.ThrowIfNull(mesh);

        var built = EditMesh.FromTriangles(mesh.Positions, mesh.Indices, weld);

        // Coordinates are carried in when the source had a full set, because docs/plan/41 § D4's
        // KeepUvSeams reads them off the *source* to decide where the old atlas was cut. Assimp gives
        // one coordinate per drawing vertex and the kernel wants one per corner, which after the weld
        // is a lookup through the triangle the corner came from.
        if (mesh.TexCoords.Length == mesh.Positions.Length && mesh.TexCoords.Length > 0) {
            Carry(built, mesh);
        }

        return built;
    }

    /// <summary>Writes a kernel mesh back out as geometry a renderer or a file can take.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="name">What to call it.</param>
    /// <param name="uvs">One coordinate per corner, or empty for none.</param>
    /// <returns>The geometry, triangulated, one vertex per corner.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public static MeshData ToMeshData(EditMesh mesh, string name, IReadOnlyList<Vector2>? uvs = null) {
        ArgumentNullException.ThrowIfNull(mesh);

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var coordinates = new List<Vector2>();
        var indices = new List<int>();
        var carry = uvs is { Count: > 0 } && uvs.Count == mesh.CornerCount;

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var corners = mesh.CornersOf(face);
            var facing = mesh.Normal(face);

            if (!facing.IsZero) {
                facing = Vector3.Normalize(facing);
            }

            var first = positions.Count;

            for (var corner = 0; corner < corners.Length; corner++) {
                positions.Add(mesh.Positions[corners[corner]]);
                normals.Add(facing);
                coordinates.Add(carry ? uvs![entry.Start + corner] : Vector2.Zero);
            }

            // A fan, which is right for the convex faces a remesh and a block-out both produce and is
            // what EditMesh.Triangulate does for the same reason.
            for (var corner = 1; corner + 1 < corners.Length; corner++) {
                indices.Add(first);
                indices.Add(first + corner);
                indices.Add(first + corner + 1);
            }
        }

        return new() {
            Name = name,
            Positions = [.. positions],
            Normals = [.. normals],
            TexCoords = carry ? [.. coordinates] : [],
            Indices = [.. indices],
            Bounds = Bounds(positions)
        };
    }

    /// <summary>An unwrap's placements applied to its islands, as one coordinate per corner.</summary>
    /// <param name="mesh">The mesh the islands came from, for the corner count.</param>
    /// <param name="islands">What <c>UvUnwrap.Flatten</c> produced.</param>
    /// <param name="placements">What <c>UvUnwrap.Pack</c> decided.</param>
    /// <returns>The atlas coordinates.</returns>
    /// <remarks>
    ///     ⚠ <b>The three stages are separable and this is where they stop being separable, which is
    ///     why it is written once here rather than in each caller.</b> docs/plan/42 § D1: an island
    ///     carries its own coordinates and a placement carries the offset, scale, quarter-turn and tile
    ///     that put it in the atlas — a caller that applied only the offset would produce an atlas that
    ///     looks right until something is rotated.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static Vector2[] Atlas(
        EditMesh mesh,
        IReadOnlyList<UvIsland> islands,
        IReadOnlyList<UvPlacement> placements
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(placements);

        var coordinates = new Vector2[mesh.CornerCount];

        foreach (var placement in placements) {
            if (placement.Island < 0 || placement.Island >= islands.Count) {
                continue;
            }

            var island = islands[placement.Island];

            for (var corner = 0; corner < island.Corners.Count; corner++) {
                var into = island.Corners[corner];

                if (into >= 0 && into < coordinates.Length) {
                    coordinates[into] = placement.Apply(island, island.Coordinates[corner]);
                }
            }
        }

        return coordinates;
    }

    /// <summary>Copies per-vertex coordinates onto per-corner ones through the welded triangles.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A lookup keyed by position is the wrong answer here and it is the obvious one.</b>
    ///         A seam <i>is</i> two drawing vertices at one position that disagree about the
    ///         coordinate, so a position-keyed table keeps one of the two and erases every seam in the
    ///         file — which makes <c>KeepUvSeams</c> a setting that runs, reports nothing, and
    ///         preserves nothing. The coordinate has to arrive per corner, and a corner is a
    ///         (face, slot) pair rather than a position.
    ///     </para>
    ///     <para>
    ///         <b>The correspondence exists because <see cref="EditMesh.FromTriangles" /> is
    ///         order-preserving</b>: it adds one face per input triangle in order, in the same corner
    ///         order, and drops only the triangles whose corners collapsed onto one another during the
    ///         weld. So a forward cursor over the source triangles, advanced past the dropped ones by
    ///         comparing positions against the weld tolerance, lands each built corner on the drawing
    ///         vertex it came from.
    ///     </para>
    /// </remarks>
    static void Carry(EditMesh built, MeshData mesh) {
        var coordinates = new Vector2[built.CornerCount];
        var bounds = built.Bounds.Size.Length();
        var tolerance = MathF.Max(bounds * EditMesh.DefaultWeldTolerance, float.Epsilon) * 4f;
        var triangle = 0;
        var carried = 0;

        for (var face = 0; face < built.FaceCount; face++) {
            var entry = built.Faces[face];
            var corners = built.CornersOf(face);

            if (corners.Length != 3) {
                continue;
            }

            while (triangle * 3 + 2 < mesh.Indices.Length && !Matches(built, corners, mesh, triangle, tolerance)) {
                triangle++;
            }

            if (triangle * 3 + 2 >= mesh.Indices.Length) {
                break;
            }

            for (var corner = 0; corner < 3; corner++) {
                coordinates[entry.Start + corner] = mesh.TexCoords[mesh.Indices[triangle * 3 + corner]];
            }

            triangle++;
            carried++;
        }

        // Nothing rather than half: a coordinate layer that covers the first n faces and is zero
        // afterwards reads to FeatureCurves.FromUvSeams as one enormous seam around whatever face the
        // walk gave up on, which is a worse answer than having no coordinates at all.
        if (carried == built.FaceCount) {
            built.SetTexCoords(coordinates);
        }
    }

    /// <summary>Whether a built face's corners are the source triangle's, to the weld tolerance.</summary>
    static bool Matches(EditMesh built, ReadOnlySpan<int> corners, MeshData mesh, int triangle, float tolerance) {
        for (var corner = 0; corner < 3; corner++) {
            var source = mesh.Positions[mesh.Indices[triangle * 3 + corner]];

            if ((built.Positions[corners[corner]] - source).LengthSquared() > tolerance * tolerance) {
                return false;
            }
        }

        return true;
    }

    static BoundingBox Bounds(List<Vector3> positions) {
        if (positions.Count == 0) {
            return new(Vector3.Zero, Vector3.Zero);
        }

        var low = positions[0];
        var high = positions[0];

        foreach (var position in positions) {
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }

        return new(low, high);
    }
}
