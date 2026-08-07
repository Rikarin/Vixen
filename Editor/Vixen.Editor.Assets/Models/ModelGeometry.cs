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

        // ⚠ The file's own material assignment replaces the coplanarity guess `FromTriangles` made,
        // and this is the whole of what a reader knows that the kernel cannot. `MeshData.MaterialIndex`
        // is one number for the whole mesh — Assimp splits a multi-material mesh into one MeshData per
        // material before this sees it — so what the file declared is "every face here is the same
        // material", which is exactly one group and no boundaries. Left as `Regroup` computed it, the
        // group ids say the opposite on any faceted surface: one group per triangle, which a remesh
        // reads as every edge a crease and an unwrap reads as every triangle its own chart. See
        // MeshGroupSource, docs/plan/41 § D4 and docs/plan/42 § D3.
        for (var face = 0; face < built.FaceCount; face++) {
            built.SetGroup(face, mesh.MaterialIndex);
        }

        built.GroupSource = MeshGroupSource.Assigned;

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

    /// <summary>The islands a file's existing texture coordinates already describe.</summary>
    /// <param name="mesh">The geometry, as the reader produced it. It must carry a full coordinate set.</param>
    /// <returns>One island per connected run of triangles in the atlas, ordered so two runs agree.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/42's seventh exit criterion, as the input half of it: "islands unwrapped in
    ///         Blender, imported, repacked: seams untouched, island shapes untouched, better
    ///         efficiency".</b> An island is a connected component of triangles <i>in the atlas</i>, so
    ///         it is read off the drawing vertices rather than through <see cref="EditMesh" /> — welding
    ///         into the kernel first and then trying to recover the seams would be throwing the answer
    ///         away and looking for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two corners are the same atlas vertex when they agree about <i>both</i> the position
    ///         and the coordinate, and reading a shared index instead is a claim about the file that
    ///         generated meshes do not honour.</b> A seam is two drawing vertices at one position that
    ///         disagree about the coordinate, and it stays two here — that is what keeps an authored
    ///         seam a seam. But an exporter is free to split a vertex for a normal, for a tangent, or
    ///         for nothing at all: measured on sixteen image-to-3D GLBs, every one of them carries three
    ///         drawing vertices per triangle and shares not a single index, so components over raw
    ///         indices came out at exactly one island per triangle — 25 427 of them on a 25 439-triangle
    ///         mesh whose atlas has 422.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both tolerances are relative, and an absolute one is a claim about how big the
    ///         model is.</b> The position tolerance is the bounds' diagonal times
    ///         <see cref="EditMesh.DefaultWeldTolerance" />, as everywhere else in the kernel; the
    ///         coordinate tolerance is the same fraction of the atlas's own extent, so a mesh laid out
    ///         in one unit square and the same mesh laid out across eight tiles weld identically.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public static IReadOnlyList<UvIsland> Islands(MeshData mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var parent = new int[mesh.Positions.Length];

        for (var vertex = 0; vertex < parent.Length; vertex++) {
            parent[vertex] = vertex;
        }

        int Find(int vertex) {
            while (parent[vertex] != vertex) {
                parent[vertex] = parent[parent[vertex]];
                vertex = parent[vertex];
            }

            return vertex;
        }

        void Union(int left, int right) {
            var a = Find(left);
            var b = Find(right);

            if (a != b) {
                parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }

        var atlas = Atlas(mesh);

        for (var vertex = 0; vertex < atlas.Length; vertex++) {
            if (atlas[vertex] != vertex) {
                Union(atlas[vertex], vertex);
            }
        }

        for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
            Union(mesh.Indices[index], mesh.Indices[index + 1]);
            Union(mesh.Indices[index + 1], mesh.Indices[index + 2]);
        }

        var corners = new Dictionary<int, List<int>>();

        for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
            var root = Find(mesh.Indices[index]);

            if (!corners.TryGetValue(root, out var into)) {
                corners[root] = into = [];
            }

            into.Add(index);
            into.Add(index + 1);
            into.Add(index + 2);
        }

        var islands = new List<UvIsland>();

        // Ordered by root, because docs/plan/42 § D12 makes the packing order part of the determinism
        // gate and a dictionary's enumeration order is not a promise.
        foreach (var root in corners.Keys.Order()) {
            var slots = corners[root];
            var coordinates = new Vector2[slots.Count];
            var low = new Vector2(float.MaxValue, float.MaxValue);
            var high = new Vector2(float.MinValue, float.MinValue);
            var area = 0f;
            var world = 0f;

            for (var slot = 0; slot < slots.Count; slot++) {
                coordinates[slot] = mesh.TexCoords[mesh.Indices[slots[slot]]];
                low = Vector2.Min(low, coordinates[slot]);
                high = Vector2.Max(high, coordinates[slot]);
            }

            for (var slot = 0; slot + 2 < slots.Count; slot += 3) {
                area += Area(coordinates[slot], coordinates[slot + 1], coordinates[slot + 2]);

                world += Area(
                    mesh.Positions[mesh.Indices[slots[slot]]],
                    mesh.Positions[mesh.Indices[slots[slot + 1]]],
                    mesh.Positions[mesh.Indices[slots[slot + 2]]]
                );
            }

            // Coordinates per world unit, which is what the island's Scale means: the packer is
            // allowed to move an island and has to be able to say when it resized one.
            islands.Add(new(coordinates, slots, low, high, world > 0f ? MathF.Sqrt(area / world) : 1f));
        }

        return islands;
    }

    /// <summary>Which drawing vertex each drawing vertex is the same atlas vertex as.</summary>
    /// <remarks>
    ///     <para>
    ///         Two stages, because the two conditions have very different fan-out. Positions go through
    ///         a hashed grid looked up across its own cell and the twenty-six around it — the same
    ///         arrangement <see cref="EditMesh.FromTriangles" /> welds with, so that a corner either side
    ///         of a cell boundary still meets its twin. Coordinates are then compared pairwise
    ///         <i>within</i> one welded position, where the count is the number of triangles meeting
    ///         there and a grid would cost more than it saved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The answer is the lowest index of the run and never a fresh id</b>, so the caller
    ///         can union straight into its own forest and the order two runs come out in does not depend
    ///         on a dictionary's.
    ///     </para>
    /// </remarks>
    static int[] Atlas(MeshData mesh) {
        var count = mesh.Positions.Length;
        var same = new int[count];
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        var flat = new Vector2(float.MaxValue);
        var tall = new Vector2(float.MinValue);

        for (var vertex = 0; vertex < count; vertex++) {
            same[vertex] = vertex;
            low = Vector3.Min(low, mesh.Positions[vertex]);
            high = Vector3.Max(high, mesh.Positions[vertex]);
            flat = Vector2.Min(flat, mesh.TexCoords[vertex]);
            tall = Vector2.Max(tall, mesh.TexCoords[vertex]);
        }

        var space = MathF.Max((high - low).Length() * EditMesh.DefaultWeldTolerance, float.Epsilon);
        var atlas = MathF.Max((tall - flat).Length() * EditMesh.DefaultWeldTolerance, float.Epsilon);
        var cells = new Dictionary<(long X, long Y, long Z), List<int>>();
        var runs = new List<List<int>>();

        for (var vertex = 0; vertex < count; vertex++) {
            var position = mesh.Positions[vertex];

            var cell = (
                (long) MathF.Floor(position.X / space),
                (long) MathF.Floor(position.Y / space),
                (long) MathF.Floor(position.Z / space)
            );

            var at = -1;

            for (var x = -1; at < 0 && x <= 1; x++) {
                for (var y = -1; at < 0 && y <= 1; y++) {
                    for (var z = -1; at < 0 && z <= 1; z++) {
                        if (!cells.TryGetValue((cell.Item1 + x, cell.Item2 + y, cell.Item3 + z), out var bucket)) {
                            continue;
                        }

                        foreach (var run in bucket) {
                            if (Vector3.DistanceSquared(mesh.Positions[runs[run][0]], position) <= space * space) {
                                at = run;

                                break;
                            }
                        }
                    }
                }
            }

            if (at < 0) {
                at = runs.Count;
                runs.Add([]);

                if (!cells.TryGetValue(cell, out var bucket)) {
                    cells[cell] = bucket = [];
                }

                bucket.Add(at);
            }

            runs[at].Add(vertex);
        }

        foreach (var run in runs) {
            foreach (var vertex in run) {
                foreach (var candidate in run) {
                    if (candidate >= vertex) {
                        break;
                    }

                    if (Vector2.DistanceSquared(mesh.TexCoords[candidate], mesh.TexCoords[vertex]) <= atlas * atlas) {
                        same[vertex] = same[candidate];

                        break;
                    }
                }
            }
        }

        return same;
    }

    static float Area(Vector2 a, Vector2 b, Vector2 c) =>
        MathF.Abs(((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X))) * 0.5f;

    static float Area(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).Length() * 0.5f;

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
