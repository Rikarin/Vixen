// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Geometry.Uv;

namespace Vixen.Interchange;

/// <summary>A file's parts laid end to end in one mesh.</summary>
/// <param name="Mesh">The mesh, in world space, with the file's normals and coordinates on its corners.</param>
/// <param name="Parts">Each part's name and how many faces it contributed, in order.</param>
/// <param name="ColouredGroups">Whether its face groups are packed colours, read from vertex colours.</param>
public sealed record FlatModel(
    EditMesh Mesh,
    IReadOnlyList<(string Name, int Faces)> Parts,
    bool ColouredGroups = false
) {
    /// <summary>Each part's material, in the order of <see cref="Parts" />.</summary>
    public IReadOnlyList<int> Materials { get; init; } = [];

    /// <summary>One linear colour per corner, or empty for a file with no vertex colours.</summary>
    public IReadOnlyList<Vector4> Colours { get; init; } = [];
}

/// <summary>The copy between a model file's geometry and the mesh kernel that remeshes and unwraps it.</summary>
/// <remarks>
///     <para>
///         <b>
///             docs/plan/24 § D1 puts this copy in the caller rather than in the kernel, and this is the
///             importer's copy of it.
///         </b>
///         <c>Core/Vixen.Geometry</c> depends on
///         <c>Vixen.Core.Mathematics</c> and nothing else, so it cannot name <see cref="MeshData" />;
///         <c>Vixen.Editor.SceneView</c> makes the same six-line trip for the viewport, and this
///         assembly makes it for the import and for <c>vixen remesh</c>. Two short copies on either
///         side of a layering line are the price of the line, and the line is what lets a content
///         build link the remesher without linking a renderer.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A drawn vertex is a corner and not a position, so the trip out is not the trip in
///             reversed.
///         </b>
///         Going in, a cube's twenty-four entries weld to eight positions. Coming out,
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
    const int White = 0xFFFFFF;

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
            Positions = [..positions],
            Normals = [..normals],
            TexCoords = carry ? [..coordinates] : [],
            Indices = [..indices],
            Bounds = Bounds(positions)
        };
    }

    /// <summary>A whole file as one mesh in world space, with every corner's normal and coordinate.</summary>
    /// <param name="model">What <see cref="ModelReader.Read" /> returned.</param>
    /// <returns>The mesh, and which run of its faces came from which named part.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This is what a bake reads a file with, and <see cref="ToEditMesh" /> is not, because
    ///             that one keeps no normals.
    ///         </b>
    ///         A hard edge is two normals on one point, so a mesh rebuilt
    ///         from positions has no hard edges left to bake. Corners here keep what the file said.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Welded by exact coordinates, not by a tolerance.</b> The two sides of a hard edge are
    ///         the same bits in the file, split only because one drawing vertex cannot have two normals,
    ///         so an exact match restores the connection a curvature bake needs and cannot fold a small
    ///         feature away, which a tolerance chosen for the whole model can.
    ///     </para>
    ///     <para>
    ///         The groups are the file's materials and are marked as assigned, so an id map shows them.
    ///     </para>
    ///     <para>
    ///         With vertex colours in the file, the groups are those colours instead, packed by
    ///         <see cref="PackedColour" />, which is what lets an id map show the colours an artist painted.
    ///         One colour to a face: an id is a label, and <see cref="FaceColour" /> never blends one.
    ///     </para>
    /// </remarks>
    public static FlatModel Flatten(ReadModel model) {
        ArgumentNullException.ThrowIfNull(model);

        return Flatten([model]);
    }

    /// <summary>Several files as one mesh, in order, each in its own world space.</summary>
    /// <param name="models">What <see cref="ModelReader.Read" /> returned for each, in the order they go together.</param>
    /// <returns>The mesh, and which run of its faces came from which named part, the first file's first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="models" /> or one of them is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Each file's materials are numbered after the files before it</b>, so the first
    ///         material of a second file is not the first material of the first: a high mesh split
    ///         across files keeps every material apart in an id map.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Welded within each file and not across them</b>, so two files that touch stay two
    ///         shells.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Vertex colours in any file make every file's groups colours</b>, a file without them
    ///         taking white, because one id map cannot mix packed colours with material numbers.
    ///     </para>
    /// </remarks>
    public static FlatModel Flatten(IReadOnlyList<ReadModel> models) {
        ArgumentNullException.ThrowIfNull(models);

        var built = new EditMesh();
        var normals = new List<Vector3>();
        var coordinates = new List<Vector2>();
        var colours = new List<Vector4>();
        var parts = new List<(string Name, int Faces)>();
        var materials = new List<int>();
        var anyCoordinates = false;
        var anyNormals = false;
        var anyColours = models.Any(static model => model.Meshes.Any(static mesh => mesh.Colours.Length
                == mesh.Positions.Length
                && mesh.Colours.Length > 0
            )
        );
        var offset = 0;
        Span<int> loop = stackalloc int[3];

        foreach (var model in models) {
            ArgumentNullException.ThrowIfNull(model);

            var nodes = model.Model.Nodes;
            var world = new Matrix4x4[nodes.Length];

            for (var at = 0; at < nodes.Length; at++) {
                world[at] = nodes[at].Parent < 0 ? nodes[at].Transform : nodes[at].Transform * world[nodes[at].Parent];
            }

            var meshes = model.Meshes.ToDictionary(static mesh => mesh.Name, StringComparer.Ordinal);

            // ⚠ Welded within a file and never across two. Two files are two objects, and a point they
            // happen to share would join their shells and bend the curvature of both.
            var positions = new Dictionary<Vector3, int>();

            foreach (var part in model.Model.Parts) {
                var mesh = meshes[part.Mesh];
                var transform = world[part.Node];
                var inverse = Matrix4x4.Invert(transform, out var inverted) ? Matrix4x4.Transpose(inverted) : transform;
                var hasNormals = mesh.Normals.Length == mesh.Positions.Length;
                var hasCoordinates = mesh.TexCoords.Length == mesh.Positions.Length;
                var hasColours = mesh.Colours.Length == mesh.Positions.Length;
                var slots = new int[mesh.Positions.Length];
                var turned = new Vector3[hasNormals ? mesh.Positions.Length : 0];
                var made = 0;

                for (var vertex = 0; vertex < slots.Length; vertex++) {
                    var point = Matrix4x4.TransformPosition(mesh.Positions[vertex], transform);

                    if (!positions.TryGetValue(point, out var slot)) {
                        slot = built.AddPosition(point);
                        positions.Add(point, slot);
                    }

                    slots[vertex] = slot;

                    if (hasNormals) {
                        turned[vertex] = Vector3.Normalize(Matrix4x4.TransformDirection(mesh.Normals[vertex], inverse));
                    }
                }

                for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
                    var (a, b, c) = (mesh.Indices[index], mesh.Indices[index + 1], mesh.Indices[index + 2]);

                    (loop[0], loop[1], loop[2]) = (slots[a], slots[b], slots[c]);

                    // A triangle the weld closed has no area, no normal and no edge.
                    if (loop[0] == loop[1] || loop[1] == loop[2] || loop[2] == loop[0]) {
                        continue;
                    }

                    built.AddFace(loop, anyColours ? FaceColour(mesh, a, b, c) : part.Material + offset);
                    made++;

                    normals.Add(hasNormals ? turned[a] : default);
                    normals.Add(hasNormals ? turned[b] : default);
                    normals.Add(hasNormals ? turned[c] : default);
                    coordinates.Add(hasCoordinates ? mesh.TexCoords[a] : default);
                    coordinates.Add(hasCoordinates ? mesh.TexCoords[b] : default);
                    coordinates.Add(hasCoordinates ? mesh.TexCoords[c] : default);

                    if (anyColours) {
                        colours.Add(hasColours ? mesh.Colours[a] : Vector4.One);
                        colours.Add(hasColours ? mesh.Colours[b] : Vector4.One);
                        colours.Add(hasColours ? mesh.Colours[c] : Vector4.One);
                    }
                }

                anyNormals |= hasNormals;
                anyCoordinates |= hasCoordinates;

                var name = nodes[part.Node].Name;

                parts.Add((name.Length > 0 ? name : mesh.Name, made));
                materials.Add(part.Material + offset);
            }

            offset += Math.Max(
                model.Model.Materials.Length,
                model.Model.Parts.Select(static part => part.Material + 1).DefaultIfEmpty(0).Max()
            );
        }

        built.GroupSource = MeshGroupSource.Assigned;

        if (anyNormals) {
            built.SetNormals(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(normals));
        }

        if (anyCoordinates) {
            built.SetTexCoords(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coordinates));
        }

        return new(built, parts, anyColours) { Materials = materials, Colours = colours };
    }

    /// <summary>A flattened file's parts again, each its own mesh, with new coordinates on every corner.</summary>
    /// <param name="model">What <see cref="Flatten(ReadModel)" /> returned.</param>
    /// <param name="uvs">One coordinate per corner.</param>
    /// <returns>One mesh per part, in world space, carrying its normals, colours, name and material.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="uvs" /> is not one coordinate per corner.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The way back that <see cref="ToMeshData" /> is not.</b> That one gives every corner its face's
    ///         normal, which is right for a remesh, whose corners never had one of their own. A new layout changes
    ///         nothing about a surface, so here each corner keeps what the file said about it: an unwrapped ball
    ///         written through <see cref="ToMeshData" /> was lit as a polyhedron.
    ///     </para>
    ///     <para>
    ///         Two corners are one vertex when they agree on everything, so a seam splits a vertex and a smooth
    ///         run shares one, which keeps the file the size it would have been.
    ///     </para>
    /// </remarks>
    public static MeshData[] ToParts(FlatModel model, IReadOnlyList<Vector2> uvs) {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(uvs);

        var mesh = model.Mesh;

        if (uvs.Count != mesh.CornerCount) {
            throw new ArgumentException($"{uvs.Count} coordinates for {mesh.CornerCount} corners.", nameof(uvs));
        }

        var written = new MeshData[model.Parts.Count];
        var face = 0;

        for (var at = 0; at < written.Length; at++) {
            var (name, faces) = model.Parts[at];
            var vertices = new Dictionary<(Vector3, Vector3, Vector2, Vector4), int>();
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var coordinates = new List<Vector2>();
            var colours = new List<Vector4>();
            var indices = new List<int>();

            for (var last = face + faces; face < last; face++) {
                var start = mesh.Faces[face].Start;
                var corners = mesh.CornersOf(face);
                var facing = mesh.Normal(face);
                var indexed = new int[corners.Length];

                if (!facing.IsZero) {
                    facing = Vector3.Normalize(facing);
                }

                for (var corner = 0; corner < corners.Length; corner++) {
                    var slot = start + corner;
                    var normal = mesh.Normals.Length > slot && !mesh.Normals[slot].IsZero ? mesh.Normals[slot] : facing;
                    var colour = model.Colours.Count > slot ? model.Colours[slot] : Vector4.One;
                    var key = (mesh.Positions[corners[corner]], normal, uvs[slot], colour);

                    if (!vertices.TryGetValue(key, out var vertex)) {
                        vertex = positions.Count;
                        vertices.Add(key, vertex);
                        positions.Add(key.Item1);
                        normals.Add(normal);
                        coordinates.Add(key.Item3);
                        colours.Add(colour);
                    }

                    indexed[corner] = vertex;
                }

                for (var corner = 1; corner + 1 < indexed.Length; corner++) {
                    indices.Add(indexed[0]);
                    indices.Add(indexed[corner]);
                    indices.Add(indexed[corner + 1]);
                }
            }

            written[at] = new() {
                Name = name,
                Positions = [..positions],
                Normals = [..normals],
                TexCoords = [..coordinates],
                Colours = model.Colours.Count > 0 ? [..colours] : [],
                Indices = [..indices],
                MaterialIndex = model.Materials.Count > at ? model.Materials[at] : 0,
                Bounds = Bounds(positions)
            };
        }

        return written;
    }

    /// <summary>A colour as one int, each lane sRGB-encoded in eight bits, so that it reads as the artist saw it.</summary>
    /// <param name="colour">The colour, linear, as a file stores it.</param>
    /// <returns>Red in the high byte, blue in the low.</returns>
    public static int PackedColour(Vector4 colour) {
        return (Lane(colour.X) << 16) | (Lane(colour.Y) << 8) | Lane(colour.Z);

        static int Lane(float linear) {
            var value = Math.Clamp(linear, 0f, 1f);
            var encoded = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;

            return (int)MathF.Round(encoded * 255f);
        }
    }

    /// <summary>The colour a face is labelled with: the one two corners agree on, or else its first corner's.</summary>
    /// <remarks>
    ///     ⚠ <b>Picked rather than averaged.</b> An average along a painted border is a colour nobody chose, and
    ///     a selection by colour would then miss the face. A part with no colours is white, as a viewport shows it.
    /// </remarks>
    static int FaceColour(MeshData mesh, int a, int b, int c) {
        if (mesh.Colours.Length != mesh.Positions.Length) {
            return White;
        }

        var (first, second, third) = (PackedColour(mesh.Colours[a]), PackedColour(mesh.Colours[b]),
            PackedColour(mesh.Colours[c]));

        return second == third ? second : first;
    }

    /// <summary>An unwrap's placements applied to its islands, as one coordinate per corner.</summary>
    /// <param name="mesh">The mesh the islands came from, for the corner count.</param>
    /// <param name="islands">What <c>UvUnwrap.Flatten</c> produced.</param>
    /// <param name="placements">What <c>UvUnwrap.Pack</c> decided.</param>
    /// <returns>The atlas coordinates.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The three stages are separable and this is where they stop being separable, which is
    ///         why it is written once here rather than in each caller.
    ///     </b>
    ///     docs/plan/42 § D1: an island
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
    ///         <b>
    ///             docs/plan/42's seventh exit criterion, as the input half of it: "islands unwrapped in
    ///             Blender, imported, repacked: seams untouched, island shapes untouched, better
    ///             efficiency".
    ///         </b>
    ///         An island is a connected component of triangles <i>in the atlas</i>, so
    ///         it is read off the drawing vertices rather than through <see cref="EditMesh" /> — welding
    ///         into the kernel first and then trying to recover the seams would be throwing the answer
    ///         away and looking for it.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Two corners are the same atlas vertex when they agree about <i>both</i> the position
    ///             and the coordinate, and reading a shared index instead is a claim about the file that
    ///             generated meshes do not honour.
    ///         </b>
    ///         A seam is two drawing vertices at one position that
    ///         disagree about the coordinate, and it stays two here — that is what keeps an authored
    ///         seam a seam. But an exporter is free to split a vertex for a normal, for a tangent, or
    ///         for nothing at all: measured on sixteen image-to-3D GLBs, every one of them carries three
    ///         drawing vertices per triangle and shares not a single index, so components over raw
    ///         indices came out at exactly one island per triangle — 25 427 of them on a 25 439-triangle
    ///         mesh whose atlas has 422.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Both tolerances are relative, and an absolute one is a claim about how big the
    ///             model is.
    ///         </b>
    ///         The position tolerance is the bounds' diagonal times
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
                (long)MathF.Floor(position.X / space),
                (long)MathF.Floor(position.Y / space),
                (long)MathF.Floor(position.Z / space)
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
        MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * 0.5f;

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
    ///         <b>
    ///             The correspondence exists because <see cref="EditMesh.FromTriangles" /> is
    ///             order-preserving
    ///         </b>
    ///         : it adds one face per input triangle in order, in the same corner
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
