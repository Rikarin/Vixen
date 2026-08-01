// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Geometry;

namespace Vixen.Editor.SceneView;

/// <summary>One piece of geometry on its way out of the editor: a mesh, where it is, and its name.</summary>
/// <param name="Mesh">The geometry, in its own space.</param>
/// <param name="Transform">Where it goes, relative to whatever the export is centred on.</param>
/// <param name="Name">What to call it in the file.</param>
public readonly record struct ExportPiece(EditMesh Mesh, Matrix4x4 Transform, string Name);

/// <summary>Writes block-out geometry as a file a DCC opens.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P7, and its own words for why it is in the plan: "this is what makes 'import
///         from a DCC' and this document the same workflow rather than competing ones".</b> A
///         designer blocks out a level, an artist opens the block-out in their tool and models to it,
///         and the level does not change shape.
///     </para>
///     <para>
///         ⚠ <b>The export and the bake are one piece of code, and that is the decision this phase
///         turns on.</b> The plan says "bake to a <c>.vxmesh</c> asset through the existing importer
///         machinery" — and the existing importer machinery is <c>ModelImporter</c>, which reads OBJ,
///         FBX and glTF through Assimp and compiles meshlets, bounds and distance fields out of them.
///         Inventing a second mesh format would mean a second compiler to keep in step with the first,
///         for geometry whose whole purpose is to be replaced. Writing the file the artist will open
///         and pointing the entity at <i>that</i> is one artefact instead of two, and it makes the
///         round trip literally the same file.
///     </para>
///     <para>
///         ⚠ <b>OBJ for the bake and glTF for the handoff, and they are not interchangeable.</b> OBJ
///         is text, is opened by everything, and carries positions, normals, coordinates and face
///         groups — which is all a block-out has. glTF carries the same and a material per group, in
///         one self-contained file, which is what an artist wants and what a viewer on a phone can
///         open. Neither carries an n-gon, so both triangulate.
///     </para>
/// </remarks>
public static class MeshExport {
    /// <summary>Writes pieces as a Wavefront OBJ.</summary>
    /// <param name="pieces">What to write.</param>
    /// <param name="name">What to call the object when there is only one.</param>
    /// <returns>The file's text, ending in a newline.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One <c>o</c> per piece and one <c>g</c> per face group.</b> Face groups are what a
    ///         block-out's materials are assigned to — doc 24's D2 — and OBJ's group is the only place
    ///         to put them where an importer will give them back. Losing them would make "the reveal
    ///         is trim and the wall is brick" something the artist has to reconstruct by eye.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Indices are one-based and per file rather than per object.</b> OBJ counts vertices
    ///         from one across the whole document, which is the single most common way a hand-written
    ///         exporter is wrong — and it is wrong in a way that produces a file that opens, with the
    ///         second object's faces made of the first object's vertices.
    ///     </para>
    ///     <para>
    ///         Numbers are written with <see cref="CultureInfo.InvariantCulture" />, because a decimal
    ///         comma is a file no importer anywhere reads.
    ///     </para>
    /// </remarks>
    public static string Obj(IReadOnlyList<ExportPiece> pieces, string name = "Blockout") {
        ArgumentNullException.ThrowIfNull(pieces);

        var text = new StringBuilder();

        text.Append("# Exported by Vixen\n");

        var vertices = 1;
        var normals = 1;

        foreach (var piece in pieces) {
            var mesh = piece.Mesh;

            if (mesh.IsEmpty) {
                continue;
            }

            text.Append("o ").Append(pieces.Count == 1 ? name : piece.Name).Append('\n');

            foreach (var position in mesh.Positions) {
                var point = Matrix4x4.TransformPosition(position, piece.Transform);

                text.Append("v ").Append(Number(point.X)).Append(' ')
                    .Append(Number(point.Y)).Append(' ')
                    .Append(Number(point.Z)).Append('\n');
            }

            for (var face = 0; face < mesh.FaceCount; face++) {
                var facing = Matrix4x4.TransformDirection(mesh.Normal(face), piece.Transform);

                if (!facing.IsZero) {
                    facing = Vector3.Normalize(facing);
                }

                text.Append("vn ").Append(Number(facing.X)).Append(' ')
                    .Append(Number(facing.Y)).Append(' ')
                    .Append(Number(facing.Z)).Append('\n');
            }

            var group = int.MinValue;

            for (var face = 0; face < mesh.FaceCount; face++) {
                var entry = mesh.Faces[face];

                if (entry.Group != group) {
                    group = entry.Group;

                    text.Append("g ").Append(piece.Name).Append('_').Append(Number(group)).Append('\n');
                }

                text.Append('f');

                for (var corner = 0; corner < entry.Count; corner++) {
                    text.Append(' ')
                        .Append(vertices + mesh.Corners[entry.Start + corner])
                        .Append("//")
                        .Append(normals + face);
                }

                text.Append('\n');
            }

            vertices += mesh.PositionCount;
            normals += mesh.FaceCount;
        }

        return text.ToString();
    }

    /// <summary>Writes pieces as a self-contained glTF 2.0 document.</summary>
    /// <param name="pieces">What to write.</param>
    /// <param name="name">What to call the scene.</param>
    /// <returns>The file's JSON text.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One file with its buffer inlined as a data URI, rather than a <c>.gltf</c> beside a
    ///         <c>.bin</c> or a binary <c>.glb</c>.</b> A block-out export is something a designer
    ///         attaches to a message; two files is one file somebody forgets, and a <c>.glb</c>'s
    ///         chunk alignment is a second thing to get right for a format nobody is going to diff.
    ///         Every glTF reader takes the data URI.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Triangulated, with a vertex per corner and the face's own normal.</b> glTF has no
    ///         n-gon and no per-face attribute, so the trip out is the same expansion
    ///         <c>EditMeshes.ToMeshData</c> makes for a renderer — which is also what makes the
    ///         smoothing groups survive it, because by then they are already corner normals.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The Y-up, −Z-forward convention is glTF's and is also Vixen's</b>, so nothing is
    ///         flipped on the way out. An exporter that converted would be one whose round trip moves
    ///         the level, which is the one thing P7's exit criterion forbids.
    ///     </para>
    /// </remarks>
    public static string Gltf(IReadOnlyList<ExportPiece> pieces, string name = "Blockout") {
        ArgumentNullException.ThrowIfNull(pieces);

        List<byte> buffer = [];
        List<string> meshes = [];
        List<string> nodes = [];
        List<string> accessors = [];
        List<string> views = [];

        foreach (var piece in pieces) {
            if (piece.Mesh.IsEmpty) {
                continue;
            }

            var data = piece.Mesh.ToMeshData(piece.Name);

            if (data.Indices.Length == 0) {
                continue;
            }

            var positions = Write(buffer, views, accessors, data.Positions, piece.Transform);
            var shading = WriteNormals(buffer, views, accessors, data.Normals, piece.Transform);
            var indices = Write(buffer, views, accessors, data.Indices);

            meshes.Add(
                $$"""{"primitives":[{"attributes":{"POSITION":{{positions}},"NORMAL":{{shading}}},"indices":{{indices}},"mode":4}],"name":{{Text(piece.Name)}}}"""
            );

            nodes.Add($$"""{"mesh":{{meshes.Count - 1}},"name":{{Text(piece.Name)}}}""");
        }

        if (meshes.Count == 0) {
            return $$"""{"asset":{"version":"2.0","generator":"Vixen"},"scenes":[{"name":{{Text(name)}},"nodes":[]}],"scene":0}""";
        }

        var bytes = Convert.ToBase64String([.. buffer]);
        var children = string.Join(",", Enumerable.Range(0, nodes.Count));

        return $$"""
            {"asset":{"version":"2.0","generator":"Vixen"},
            "scene":0,
            "scenes":[{"name":{{Text(name)}},"nodes":[{{children}}]}],
            "nodes":[{{string.Join(",", nodes)}}],
            "meshes":[{{string.Join(",", meshes)}}],
            "accessors":[{{string.Join(",", accessors)}}],
            "bufferViews":[{{string.Join(",", views)}}],
            "buffers":[{"byteLength":{{buffer.Count}},"uri":"data:application/octet-stream;base64,{{bytes}}"}]}
            """.Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Every entity in a document that carries geometry, ready to be written.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entities">Which entities, or null for every one that has a mesh.</param>
    /// <param name="centre">
    ///     Whose space the export is in, or <see cref="Entity.Null" /> for the world's.
    /// </param>
    /// <returns>The pieces.</returns>
    /// <remarks>
    ///     ⚠ <b>Centred on one entity rather than on the world, when the caller says so.</b> A bake
    ///     writes an asset the entity will then be an instance of, so its geometry has to be in *that
    ///     entity's* space or the mesh arrives offset by wherever in the level it happened to be
    ///     standing. An export for an artist wants world space, so the two share the routine and
    ///     differ in one argument.
    /// </remarks>
    public static IReadOnlyList<ExportPiece> Pieces(
        SceneDocument document,
        IEnumerable<Entity>? entities = null,
        Entity centre = default
    ) {
        ArgumentNullException.ThrowIfNull(document);

        var world = document.World;
        var chosen = entities ?? document.Meshes.Keys;

        var inverse = Matrix4x4.Identity;

        if (!centre.IsNull && world.Has<Vixen.Engine.Transforms.WorldTransform>(centre)) {
            Matrix4x4.Invert(world.Read<Vixen.Engine.Transforms.WorldTransform>(centre).Value, out inverse);
        }

        List<ExportPiece> pieces = [];

        foreach (var entity in chosen) {
            if (!world.IsAlive(entity) || document.MeshOf(entity) is not { } mesh) {
                continue;
            }

            var transform = world.Has<Vixen.Engine.Transforms.WorldTransform>(entity)
                ? world.Read<Vixen.Engine.Transforms.WorldTransform>(entity).Value * inverse
                : Matrix4x4.Identity;

            pieces.Add(new(mesh, transform, document.NameOf(entity)));
        }

        return pieces;
    }

    static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    static string Text(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    static int Write(List<byte> buffer, List<string> views, List<string> accessors, Vector3[] points, Matrix4x4 transform) {
        var offset = Align(buffer);
        var low = Vector3.Zero;
        var high = Vector3.Zero;

        for (var index = 0; index < points.Length; index++) {
            var point = Matrix4x4.TransformPosition(points[index], transform);

            low = index == 0 ? point : Vector3.Min(low, point);
            high = index == 0 ? point : Vector3.Max(high, point);

            Add(buffer, point);
        }

        views.Add($$"""{"buffer":0,"byteOffset":{{offset}},"byteLength":{{points.Length * 12}}}""");

        // ⚠ A POSITION accessor is the one glTF *requires* min and max on, because a reader uses them
        // for the scene bounds before it has looked at a single vertex. Omitting them produces a file
        // that most viewers open and some frame at the origin.
        accessors.Add(
            $$"""{"bufferView":{{views.Count - 1}},"componentType":5126,"count":{{points.Length}},"type":"VEC3","min":[{{Number(low.X)}},{{Number(low.Y)}},{{Number(low.Z)}}],"max":[{{Number(high.X)}},{{Number(high.Y)}},{{Number(high.Z)}}]}"""
        );

        return accessors.Count - 1;
    }

    static int WriteNormals(List<byte> buffer, List<string> views, List<string> accessors, Vector3[] normals, Matrix4x4 transform) {
        var offset = Align(buffer);

        foreach (var normal in normals) {
            var facing = Matrix4x4.TransformDirection(normal, transform);

            Add(buffer, facing.IsZero ? Vector3.UnitY : Vector3.Normalize(facing));
        }

        views.Add($$"""{"buffer":0,"byteOffset":{{offset}},"byteLength":{{normals.Length * 12}}}""");

        accessors.Add(
            $$"""{"bufferView":{{views.Count - 1}},"componentType":5126,"count":{{normals.Length}},"type":"VEC3"}"""
        );

        return accessors.Count - 1;
    }

    static int Write(List<byte> buffer, List<string> views, List<string> accessors, int[] indices) {
        var offset = Align(buffer);

        foreach (var index in indices) {
            buffer.AddRange(BitConverter.GetBytes((uint) index));
        }

        views.Add($$"""{"buffer":0,"byteOffset":{{offset}},"byteLength":{{indices.Length * 4}}}""");

        accessors.Add(
            $$"""{"bufferView":{{views.Count - 1}},"componentType":5125,"count":{{indices.Length}},"type":"SCALAR"}"""
        );

        return accessors.Count - 1;
    }

    /// <summary>Pads the buffer so the next view starts on a four-byte boundary, which glTF requires.</summary>
    static int Align(List<byte> buffer) {
        while (buffer.Count % 4 != 0) {
            buffer.Add(0);
        }

        return buffer.Count;
    }

    static void Add(List<byte> buffer, Vector3 value) {
        buffer.AddRange(BitConverter.GetBytes(value.X));
        buffer.AddRange(BitConverter.GetBytes(value.Y));
        buffer.AddRange(BitConverter.GetBytes(value.Z));
    }
}

/// <summary>Something that can put a file into the project and say what asset it became.</summary>
/// <remarks>
///     <para>
///         <b>The seam doc 24's P7 bake needs, and it is the shape every other one in this assembly
///         has.</b> <c>IMeshSource</c>, <c>ISurfaceSource</c> and <c>ISceneWriter</c> are all the same
///         bargain: the viewport says what it wants and the application, which owns the asset
///         database, answers. A scene view that knew how to import an asset would be the coupling doc
///         11's layering exists to prevent.
///     </para>
///     <para>
///         ⚠ <b>It returns a reference rather than a path, because a path is not an identity.</b> What
///         goes on the entity is an <c>AssetReference</c>, which survives the file being renamed —
///         doc 08's whole argument — and only the thing that ran the import knows what it minted.
///     </para>
/// </remarks>
public interface IMeshBaker {
    /// <summary>Writes a mesh into the project and imports it.</summary>
    /// <param name="name">What to call it, without an extension.</param>
    /// <param name="extension">Which format it is in, with its dot.</param>
    /// <param name="content">The file.</param>
    /// <returns>What it became, or <see cref="AssetReference.Null" /> if it could not be imported.</returns>
    AssetReference Bake(string name, string extension, string content);
}
