// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Geometry;

namespace Vixen.Interchange;

/// <summary>Writes meshes back out as a file a DCC opens: OBJ, glTF or GLB.</summary>
/// <remarks>
///     <para>
///         <b>
///             <see cref="ModelReader" />'s other half, and it is here for the same reason that one
///             is.
///         </b>
///         docs/plan/41 § D16 and docs/plan/42 § D13 both give the batch surface as
///         <c>vixen remesh in.glb out.glb</c>, which is a read through Assimp and a write that Assimp
///         does not do for us. Putting the write beside the read means the CLI links one assembly to
///         do a round trip rather than two that disagree about what a mesh is.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <c>MeshExport</c> in <c>Vixen.Editor.SceneView</c> writes glTF too, and this is not
///             it.
///         </b>
///         That one takes doc 24's <c>ExportPiece</c> — a kernel mesh, a world transform and a
///         name, straight out of a scene document — and lives in the viewport assembly, which carries
///         the whole UI stack. A batch tool that took that reference to write a file would link a
///         renderer and a control library to run on a build server. The overlap is the glTF envelope
///         and it is about eighty lines; the alternative was moving the exporter down into an assembly
///         the viewport must not depend on, and doc 24's <c>IMeshBaker</c> exists precisely because
///         <c>Vixen.Editor.SceneView</c> does not reference the asset database.
///     </para>
///     <para>
///         ⚠ <b>The format is chosen by extension and an unknown one is refused rather than guessed.</b>
///         Writing glTF into a file called <c>.fbx</c> produces something no tool opens and every tool
///         blames on the mesh.
///     </para>
/// </remarks>
public static class ModelWriter {
    /// <summary>Every extension this writes, with their dots, in the order a help string wants them.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".obj", ".gltf", ".glb"];

    /// <summary>UTF-8 with no byte-order mark, which is the only kind a mesh file may have.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         <see cref="Encoding.UTF8" /> emits a BOM and that is enough to make a valid glTF
    ///         unreadable.
    ///     </b>
    ///     Assimp finds its importer by searching the first couple of hundred bytes for
    ///     <c>"asset"</c> and <c>"scenes"</c>, and three bytes of preamble in front of the opening brace
    ///     take the file past every check it has — the error it gives back is "no suitable reader found
    ///     for the file format", which reads as a bug in the writer's JSON and is a bug in its
    ///     encoding. Measured: the same document round-trips with this encoder and does not with the
    ///     framework's default.
    /// </remarks>
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Whether a path names a format this can write.</summary>
    /// <param name="path">The file name or path.</param>
    /// <returns>Whether the extension is one of <see cref="Extensions" />.</returns>
    public static bool CanWrite(string? path) =>
        path is not null
        && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes meshes in whichever format the path's extension names.</summary>
    /// <param name="path">Where to write. Its extension chooses the format.</param>
    /// <param name="meshes">What to write.</param>
    /// <param name="name">What to call the scene.</param>
    /// <param name="materials">
    ///     What <see cref="MeshData.MaterialIndex" /> indexes, or null for a file with no materials. Only glTF and GLB
    ///     keep them.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="NotSupportedException">The extension is not one of <see cref="Extensions" />.</exception>
    public static void Write(
        string path,
        IReadOnlyList<MeshData> meshes,
        string name = "Mesh",
        IReadOnlyList<ModelWriterMaterial>? materials = null
    ) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(meshes);

        switch (Path.GetExtension(path).ToLowerInvariant()) {
            case ".obj":
                File.WriteAllText(path, Obj(meshes), Utf8);

                break;

            case ".gltf":
                File.WriteAllText(path, Gltf(meshes, name, materials), Utf8);

                break;

            case ".glb":
                File.WriteAllBytes(path, Glb(meshes, name, materials));

                break;

            default:
                throw new NotSupportedException(
                    $"'{Path.GetExtension(path)}' is not a format this writes. It writes "
                    + string.Join(", ", Extensions)
                    + "."
                );
        }
    }

    /// <summary>Writes meshes whose faces have however many sides they have.</summary>
    /// <param name="path">Where to write. Its extension chooses the format.</param>
    /// <param name="meshes">What to write.</param>
    /// <param name="name">What to call the scene.</param>
    /// <param name="warnings">Where to say that the format could not take the faces, or null to say nothing.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The path docs/plan/41 § Part 4's quads take to a file.</b> <c>.obj</c> writes them as
    ///         quads, because <c>f a b c d</c> is a quad.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             glTF and GLB are triangles-only by specification, so <c>.glb</c> genuinely cannot
    ///             carry a quad.
    ///         </b>
    ///         Both write a triangulated copy and say so on
    ///         <paramref name="warnings" />. Silently triangulating is what produced a tool whose output
    ///         nobody could tell had been triangulated; refusing the format outright would take away the
    ///         one container the rest of the pipeline reads.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> or <paramref name="meshes" /> is null.</exception>
    /// <exception cref="NotSupportedException">The extension is not one of <see cref="Extensions" />.</exception>
    public static void Write(
        string path,
        IReadOnlyList<PolygonMesh> meshes,
        string name = "Mesh",
        TextWriter? warnings = null
    ) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(meshes);

        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".obj") {
            File.WriteAllText(path, Obj(meshes), Utf8);

            return;
        }

        if (!CanWrite(path)) {
            throw new NotSupportedException(
                $"'{Path.GetExtension(path)}' is not a format this writes. It writes "
                + string.Join(", ", Extensions)
                + "."
            );
        }

        var sided = meshes.Count(static entry => Sided(entry.Mesh));

        if (sided > 0 && warnings is not null) {
            warnings.WriteLine(
                $"note    {extension} stores triangles only — glTF 2.0 has no n-gon primitive — so the "
                + $"faces of {sided} mesh(es) were triangulated on the way out. Write .obj to keep them."
            );
        }

        Write(
            path,
            [..meshes.Select(static entry => ModelGeometry.ToMeshData(entry.Mesh, entry.Name, entry.TexCoords))],
            name
        );
    }

    /// <summary>Whether a mesh has a face that a triangle list would have to cut up.</summary>
    static bool Sided(EditMesh mesh) {
        for (var face = 0; face < mesh.FaceCount; face++) {
            if (mesh.Faces[face].Count != 3) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes meshes as a Wavefront OBJ.</summary>
    /// <param name="meshes">What to write.</param>
    /// <returns>The file's text, ending in a newline.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Indices are one-based and counted per file rather than per object</b>, which is the
    ///         single most common way a hand-written OBJ exporter is wrong — and it is wrong in a way
    ///         that produces a file that opens, with the second object's faces made of the first
    ///         object's vertices.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Positions are shared between the corners that sit on one, and the coordinates and
    ///             normals are not.
    ///         </b>
    ///         A <see cref="MeshData" /> is a vertex buffer — one entry per corner
    ///         — so writing its entries out as <c>v</c> lines one for one produces a file in which no
    ///         two triangles share a vertex and every edge is a boundary: 76 293 positions and 76 293
    ///         boundary edges for a 25 439-triangle mesh, measured. OBJ has a separate index per
    ///         attribute for exactly this reason, so <c>v</c> is welded on an exact match and
    ///         <c>vt</c>/<c>vn</c> stay per corner — which is also what keeps a seam a seam.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="meshes" /> is null.</exception>
    public static string Obj(IReadOnlyList<MeshData> meshes) {
        ArgumentNullException.ThrowIfNull(meshes);

        var text = new StringBuilder("# Written by Vixen\n");
        var positionBase = 1;
        var attributeBase = 1;

        foreach (var mesh in meshes) {
            if (mesh.Indices.Length == 0) {
                continue;
            }

            text.Append("o ").Append(mesh.Name.Length > 0 ? mesh.Name : "Mesh").Append('\n');

            var welded = Weld(mesh.Positions, out var distinct);

            foreach (var position in distinct) {
                text.Append("v ")
                    .Append(Number(position.X))
                    .Append(' ')
                    .Append(Number(position.Y))
                    .Append(' ')
                    .Append(Number(position.Z))
                    .Append('\n');
            }

            foreach (var normal in mesh.Normals) {
                text.Append("vn ")
                    .Append(Number(normal.X))
                    .Append(' ')
                    .Append(Number(normal.Y))
                    .Append(' ')
                    .Append(Number(normal.Z))
                    .Append('\n');
            }

            foreach (var coordinate in mesh.TexCoords) {
                // ⚠ V is flipped back on the way out. ModelReader asks Assimp for FlipUVs so that a
                // coordinate arrives in the convention the renderer samples in; a file written without
                // undoing that is a file whose textures are upside down in every other tool.
                text.Append("vt ")
                    .Append(Number(coordinate.X))
                    .Append(' ')
                    .Append(Number(1f - coordinate.Y))
                    .Append('\n');
            }

            var hasNormals = mesh.Normals.Length == mesh.Positions.Length;
            var hasCoordinates = mesh.TexCoords.Length == mesh.Positions.Length;

            for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
                text.Append('f');

                for (var corner = 0; corner < 3; corner++) {
                    var entry = mesh.Indices[index + corner];

                    text.Append(' ').Append(Number(positionBase + welded[entry]));

                    if (hasCoordinates) {
                        text.Append('/').Append(Number(attributeBase + entry));
                    } else if (hasNormals) {
                        text.Append('/');
                    }

                    if (hasNormals) {
                        text.Append('/').Append(Number(attributeBase + entry));
                    }
                }

                text.Append('\n');
            }

            positionBase += distinct.Count;
            attributeBase += mesh.Positions.Length;
        }

        return text.ToString();
    }

    /// <summary>Writes meshes as a Wavefront OBJ, keeping every face at the number of sides it has.</summary>
    /// <param name="meshes">What to write.</param>
    /// <returns>The file's text, ending in a newline.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § Part 4's quads, actually reaching the file.</b> OBJ's <c>f</c> takes
    ///         any number of corners, so a quad is written as a quad and the positions it shares with
    ///         its neighbours are written once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One <c>vn</c> per face, not per corner.</b> A retopology's faces are flat by
    ///         construction and the normal belongs to the face; a reader that wants smooth shading
    ///         recomputes it anyway.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="meshes" /> is null.</exception>
    public static string Obj(IReadOnlyList<PolygonMesh> meshes) {
        ArgumentNullException.ThrowIfNull(meshes);

        var text = new StringBuilder("# Written by Vixen\n");
        var positionBase = 1;
        var coordinateBase = 1;
        var normalBase = 1;

        foreach (var entry in meshes) {
            var mesh = entry.Mesh;

            if (mesh.FaceCount == 0) {
                continue;
            }

            text.Append("o ").Append(entry.Name.Length > 0 ? entry.Name : "Mesh").Append('\n');

            foreach (var position in mesh.Positions) {
                text.Append("v ")
                    .Append(Number(position.X))
                    .Append(' ')
                    .Append(Number(position.Y))
                    .Append(' ')
                    .Append(Number(position.Z))
                    .Append('\n');
            }

            var hasCoordinates = entry.TexCoords.Count == mesh.CornerCount && mesh.CornerCount > 0;

            if (hasCoordinates) {
                foreach (var coordinate in entry.TexCoords) {
                    text.Append("vt ")
                        .Append(Number(coordinate.X))
                        .Append(' ')
                        .Append(Number(1f - coordinate.Y))
                        .Append('\n');
                }
            }

            for (var face = 0; face < mesh.FaceCount; face++) {
                var facing = mesh.Normal(face);

                if (!facing.IsZero) {
                    facing = Vector3.Normalize(facing);
                }

                text.Append("vn ")
                    .Append(Number(facing.X))
                    .Append(' ')
                    .Append(Number(facing.Y))
                    .Append(' ')
                    .Append(Number(facing.Z))
                    .Append('\n');
            }

            for (var face = 0; face < mesh.FaceCount; face++) {
                var start = mesh.Faces[face].Start;
                var corners = mesh.CornersOf(face);

                text.Append('f');

                for (var corner = 0; corner < corners.Length; corner++) {
                    text.Append(' ').Append(Number(positionBase + corners[corner]));

                    if (hasCoordinates) {
                        text.Append('/').Append(Number(coordinateBase + start + corner));
                    } else {
                        text.Append('/');
                    }

                    text.Append('/').Append(Number(normalBase + face));
                }

                text.Append('\n');
            }

            positionBase += mesh.PositionCount;
            coordinateBase += hasCoordinates ? mesh.CornerCount : 0;
            normalBase += mesh.FaceCount;
        }

        return text.ToString();
    }

    /// <summary>Which distinct position each entry of a vertex buffer is, and those positions in order.</summary>
    /// <remarks>
    ///     ⚠ <b>An exact match rather than a tolerance.</b> The expansion this undoes copies a position
    ///     bit for bit into every corner that sits on it, so exact recovers all of it; a tolerance would
    ///     additionally weld positions the source file deliberately kept apart, which is a change to the
    ///     geometry and not to how it is written down.
    /// </remarks>
    static int[] Weld(Vector3[] positions, out List<Vector3> distinct) {
        var index = new Dictionary<(int X, int Y, int Z), int>();
        var welded = new int[positions.Length];

        distinct = [];

        for (var vertex = 0; vertex < positions.Length; vertex++) {
            var position = positions[vertex];

            // Through the bit pattern, so that the key is exactly what the `v` line will say. Negative
            // zero is folded onto zero first: the two are equal as floats and print the same, and a
            // dictionary that told them apart would emit two identical `v` lines.
            var key = (
                BitConverter.SingleToInt32Bits(position.X + 0f),
                BitConverter.SingleToInt32Bits(position.Y + 0f),
                BitConverter.SingleToInt32Bits(position.Z + 0f)
            );

            if (!index.TryGetValue(key, out var at)) {
                index[key] = at = distinct.Count;
                distinct.Add(position);
            }

            welded[vertex] = at;
        }

        return welded;
    }

    /// <summary>Writes meshes as a self-contained glTF 2.0 document with its buffer inlined.</summary>
    /// <param name="meshes">What to write.</param>
    /// <param name="name">What to call the scene.</param>
    /// <param name="materials">What <see cref="MeshData.MaterialIndex" /> indexes, or null for none.</param>
    /// <returns>The file's JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="meshes" /> is null.</exception>
    public static string Gltf(
        IReadOnlyList<MeshData> meshes,
        string name = "Mesh",
        IReadOnlyList<ModelWriterMaterial>? materials = null
    ) {
        ArgumentNullException.ThrowIfNull(meshes);

        return Document(meshes, name, true, materials ?? []).Json;
    }

    /// <summary>Writes meshes as a binary glTF container.</summary>
    /// <param name="meshes">What to write.</param>
    /// <param name="name">What to call the scene.</param>
    /// <param name="materials">What <see cref="MeshData.MaterialIndex" /> indexes, or null for none.</param>
    /// <returns>The file's bytes.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Both chunks are padded to four bytes, the JSON with spaces and the binary with
    ///         zeroes.
    ///     </b>
    ///     The specification says so and says which filler each takes, and a reader that
    ///     trusts the chunk length — most of them — reads the padding as content otherwise. A GLB that
    ///     is wrong this way opens in exactly one viewer and in none of the others.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="meshes" /> is null.</exception>
    public static byte[] Glb(
        IReadOnlyList<MeshData> meshes,
        string name = "Mesh",
        IReadOnlyList<ModelWriterMaterial>? materials = null
    ) {
        ArgumentNullException.ThrowIfNull(meshes);

        var (json, buffer) = Document(meshes, name, false, materials ?? []);
        var text = Utf8.GetBytes(json);
        var textPadding = (4 - text.Length % 4) % 4;
        var binaryPadding = (4 - buffer.Count % 4) % 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);

        var total = 12 + 8 + text.Length + textPadding + (buffer.Count > 0 ? 8 + buffer.Count + binaryPadding : 0);

        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)total);

        writer.Write((uint)(text.Length + textPadding));
        writer.Write(0x4E4F534Au);
        writer.Write(text);

        for (var pad = 0; pad < textPadding; pad++) {
            writer.Write((byte)0x20);
        }

        if (buffer.Count > 0) {
            writer.Write((uint)(buffer.Count + binaryPadding));
            writer.Write(0x004E4942u);
            writer.Write(buffer.ToArray());

            for (var pad = 0; pad < binaryPadding; pad++) {
                writer.Write((byte)0);
            }
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>The JSON and its buffer, shared by the embedded and the binary form.</summary>
    static (string Json, List<byte> Buffer) Document(
        IReadOnlyList<MeshData> meshes,
        string name,
        bool embedded,
        IReadOnlyList<ModelWriterMaterial> materials
    ) {
        List<byte> buffer = [];
        List<string> primitives = [];
        List<string> nodes = [];
        List<string> accessors = [];
        List<string> views = [];

        foreach (var mesh in meshes) {
            if (mesh.Indices.Length == 0 || mesh.Positions.Length == 0) {
                continue;
            }

            var attributes = new List<string> { $"\"POSITION\":{Positions(buffer, views, accessors, mesh.Positions)}" };

            if (mesh.Normals.Length == mesh.Positions.Length) {
                attributes.Add($"\"NORMAL\":{Vectors(buffer, views, accessors, mesh.Normals)}");
            }

            if (mesh.TexCoords.Length == mesh.Positions.Length) {
                attributes.Add($"\"TEXCOORD_0\":{Coordinates(buffer, views, accessors, mesh.TexCoords)}");
            }

            if (mesh.Colours.Length == mesh.Positions.Length) {
                attributes.Add($"\"COLOR_0\":{Colours(buffer, views, accessors, mesh.Colours)}");
            }

            var indices = Indices(buffer, views, accessors, mesh.Indices);
            var label = Text(mesh.Name.Length > 0 ? mesh.Name : "Mesh");
            var material = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < materials.Count
                ? $"\"material\":{Number(mesh.MaterialIndex)},"
                : string.Empty;

            primitives.Add(
                $$"""{"primitives":[{"attributes":{{{string.Join(",", attributes)}}},"indices":{{indices}},{{material}}"mode":4}],"name":{{label}}}"""
            );

            nodes.Add($$"""{"mesh":{{primitives.Count - 1}},"name":{{label}}}""");
        }

        if (primitives.Count == 0) {
            return (
                $$"""{"asset":{"version":"2.0","generator":"Vixen"},"scene":0,"scenes":[{"name":{{Text(name)}},"nodes":[]}]}""",
                buffer
            );
        }

        var declaration = embedded
            ? $$"""{"byteLength":{{buffer.Count}},"uri":"data:application/octet-stream;base64,{{Convert.ToBase64String([..buffer])}}"}"""
            : $$"""{"byteLength":{{buffer.Count}}}""";

        var json = $$"""
                     {"asset":{"version":"2.0","generator":"Vixen"},
                     "scene":0,
                     "scenes":[{"name":{{Text(name)}},"nodes":[{{string.Join(",", Enumerable.Range(0, nodes.Count))}}]}],
                     "nodes":[{{string.Join(",", nodes)}}],
                     {{Materials(materials)}}"meshes":[{{string.Join(",", primitives)}}],
                     "accessors":[{{string.Join(",", accessors)}}],
                     "bufferViews":[{{string.Join(",", views)}}],
                     "buffers":[{{declaration}}]}
                     """.Replace("\n", string.Empty, StringComparison.Ordinal);

        return (json, buffer);
    }

    /// <summary>A POSITION accessor, which is the one glTF requires bounds on.</summary>
    /// <remarks>
    ///     ⚠ A reader uses <c>min</c> and <c>max</c> for the scene bounds before it has looked at a
    ///     single vertex. Omitting them produces a file most viewers open and some frame at the origin.
    /// </remarks>
    static int Positions(List<byte> buffer, List<string> views, List<string> accessors, Vector3[] points) {
        var offset = Align(buffer);
        var low = points.Length > 0 ? points[0] : Vector3.Zero;
        var high = low;

        foreach (var point in points) {
            low = Vector3.Min(low, point);
            high = Vector3.Max(high, point);

            Add(buffer, point);
        }

        views.Add($$"""{"buffer":0,"byteOffset":{{offset}},"byteLength":{{points.Length * 12}}}""");

        accessors.Add(
            $$"""{"bufferView":{{views.Count - 1}},"componentType":5126,"count":{{points.Length}},"type":"VEC3","min":[{{Number(low.X)}},{{Number(low.Y)}},{{Number(low.Z)}}],"max":[{{Number(high.X)}},{{Number(high.Y)}},{{Number(high.Z)}}]}"""
        );

        return accessors.Count - 1;
    }

    static int Vectors(List<byte> buffer, List<string> views, List<string> accessors, Vector3[] values) {
        var offset = Align(buffer);

        foreach (var value in values) {
            Add(buffer, value.IsZero ? Vector3.UnitY : Vector3.Normalize(value));
        }

        views.Add($$"""{"buffer":0,"byteOffset":{{offset}},"byteLength":{{values.Length * 12}}}""");
        accessors.Add(
            $$"""{"bufferView":{{views.Count - 1}},"componentType":5126,"count":{{values.Length}},"type":"VEC3"}"""
        );

        return accessors.Count - 1;
    }

    static int Coordinates(List<byte> buffer, List<string> views, List<string> accessors, Vector2[] values) {
        var offset = Align(buffer);

        foreach (var value in values) {
            // ⚠ glTF's v already runs down the image, as a coordinate here does, and that is why this
            // one format is written as it stands: Assimp turns it over on the way in and FlipUVs turns
            // it back. OBJ and PLY are the formats whose v runs up, and those are flipped above.
            buffer.AddRange(BitConverter.GetBytes(value.X));
            buffer.AddRange(BitConverter.GetBytes(value.Y));
        }

        views.Add($$"""{"buffer":0,"byteOffset":{{offset}},"byteLength":{{values.Length * 8}}}""");
        accessors.Add(
            $$"""{"bufferView":{{views.Count - 1}},"componentType":5126,"count":{{values.Length}},"type":"VEC2"}"""
        );

        return accessors.Count - 1;
    }

    /// <summary>A COLOR_0 accessor, linear as glTF has it and as a <see cref="MeshData" /> does.</summary>
    static int Colours(List<byte> buffer, List<string> views, List<string> accessors, Vector4[] values) {
        var offset = Align(buffer);

        foreach (var value in values) {
            buffer.AddRange(BitConverter.GetBytes(value.X));
            buffer.AddRange(BitConverter.GetBytes(value.Y));
            buffer.AddRange(BitConverter.GetBytes(value.Z));
            buffer.AddRange(BitConverter.GetBytes(value.W));
        }

        views.Add($$"""{"buffer":0,"byteOffset":{{offset}},"byteLength":{{values.Length * 16}}}""");
        accessors.Add(
            $$"""{"bufferView":{{views.Count - 1}},"componentType":5126,"count":{{values.Length}},"type":"VEC4"}"""
        );

        return accessors.Count - 1;
    }

    /// <summary>Writes materials alone as a glTF 2.0 document: no mesh, no buffer, only what a material reads.</summary>
    /// <param name="materials">The materials, whose images are URIs relative to the file.</param>
    /// <param name="name">What to call the scene, which is empty.</param>
    /// <returns>The file's JSON text.</returns>
    /// <remarks>
    ///     <b>A library of materials, which is what a texturing tool hands a DCC beside its images.</b> glTF requires
    ///     no mesh, so a reader that imports materials takes these and a scene with nothing in it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="materials" /> is null.</exception>
    public static string GltfMaterials(IReadOnlyList<ModelWriterMaterial> materials, string name = "Materials") {
        ArgumentNullException.ThrowIfNull(materials);

        return $$"""{"asset":{"version":"2.0","generator":"Vixen"},{{Materials(materials)}}"scene":0,"scenes":[{"name":{{Text(name)}},"nodes":[]}]}""";
    }

    /// <summary>
    ///     The <c>materials</c>, <c>textures</c>, <c>images</c> and <c>samplers</c> members with their comma, or
    ///     nothing for a file with no material.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One texture per image, in the order the images are first named</b>, so a texture's index is its
    ///     image's and an image two slots share, such as an occlusion-roughness-metallic file, is written once. A WebP
    ///     image is reachable only through <c>EXT_texture_webp</c>, which is then required: a reader without it has no
    ///     image to fall back to.
    /// </remarks>
    static string Materials(IReadOnlyList<ModelWriterMaterial> materials) {
        if (materials.Count == 0) {
            return string.Empty;
        }

        List<string> images = [];
        SortedSet<string> used = new(StringComparer.Ordinal);
        SortedSet<string> required = new(StringComparer.Ordinal);

        int Image(string uri) {
            var at = images.IndexOf(uri);

            if (at < 0) {
                at = images.Count;
                images.Add(uri);
            }

            return at;
        }

        var written = materials.Select(material => Material(material, Image, used)).ToList();

        var textures = images.Select(uri => {
                    if (!uri.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) {
                        return $$"""{"sampler":0,"source":{{Number(images.IndexOf(uri))}}}""";
                    }

                    used.Add("EXT_texture_webp");
                    required.Add("EXT_texture_webp");

                    return """{"sampler":0,"extensions":{"EXT_texture_webp":{"source":"""
                        + Number(images.IndexOf(uri))
                        + "}}}";
                }
            )
            .ToList();

        var text = new StringBuilder();
        text.Append($"\"materials\":[{string.Join(",", written)}],");

        if (images.Count > 0) {
            text.Append($"\"textures\":[{string.Join(",", textures)}],");
            text.Append(
                $"\"images\":[{string.Join(",", images.Select(static uri => $$"""{"uri":{{Text(Escape(uri))}}}"""))}],"
            );
            // Trilinear and repeat, which is what a texture that tiles over a UV layout wants.
            text.Append("\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}],");
        }

        if (used.Count > 0) {
            text.Append($"\"extensionsUsed\":[{string.Join(",", used.Select(Text))}],");
        }

        if (required.Count > 0) {
            text.Append($"\"extensionsRequired\":[{string.Join(",", required.Select(Text))}],");
        }

        return text.ToString();
    }

    /// <summary>One material, with each image it reads by the index <paramref name="image" /> gives it.</summary>
    static string Material(ModelWriterMaterial material, Func<string, int> image, SortedSet<string> used) {
        var colour = material.Factors.BaseColor;
        var textures = material.Textures;
        var pbr = new List<string> {
            $"\"baseColorFactor\":[{Number(colour.X)},{Number(colour.Y)},{Number(colour.Z)},{Number(colour.W)}]",
            $"\"metallicFactor\":{Number(material.Factors.Metallic)}",
            $"\"roughnessFactor\":{Number(material.Factors.Roughness)}"
        };
        var members = new List<string> { $"\"name\":{Text(material.Name)}" };

        if (textures is null) {
            members.Add($"\"pbrMetallicRoughness\":{{{string.Join(",", pbr)}}}");

            return $"{{{string.Join(",", members)}}}";
        }

        if (textures.BaseColor is { } baseColor) {
            pbr.Add($"\"baseColorTexture\":{{\"index\":{Number(image(baseColor))}}}");
        }

        if (textures.MetallicRoughness is { } metallicRoughness) {
            pbr.Add($"\"metallicRoughnessTexture\":{{\"index\":{Number(image(metallicRoughness))}}}");
        }

        members.Add($"\"pbrMetallicRoughness\":{{{string.Join(",", pbr)}}}");

        if (textures.Normal is { } normal) {
            members.Add(
                $"\"normalTexture\":{{\"index\":{Number(image(normal))},\"scale\":{Number(textures.NormalScale)}}}"
            );
        }

        if (textures.Occlusion is { } occlusion) {
            members.Add(
                $"\"occlusionTexture\":{{\"index\":{Number(image(occlusion))},\"strength\":{Number(textures.OcclusionStrength)}}}"
            );
        }

        if (textures.Emissive is { } emissive) {
            members.Add($"\"emissiveTexture\":{{\"index\":{Number(image(emissive))}}}");
        }

        var glow = textures.EmissiveFactor ?? (textures.Emissive is null ? Vector3.Zero : Vector3.One);

        if (glow != Vector3.Zero) {
            members.Add($"\"emissiveFactor\":[{Number(glow.X)},{Number(glow.Y)},{Number(glow.Z)}]");
        }

        if (textures.AlphaMode != ModelAlphaMode.Opaque) {
            members.Add($"\"alphaMode\":{Text(textures.AlphaMode == ModelAlphaMode.Mask ? "MASK" : "BLEND")}");
        }

        if (textures.AlphaMode == ModelAlphaMode.Mask) {
            members.Add($"\"alphaCutoff\":{Number(textures.AlphaCutoff)}");
        }

        if (textures.DoubleSided) {
            members.Add("\"doubleSided\":true");
        }

        if (textures.EmissiveStrength > 1f) {
            used.Add("KHR_materials_emissive_strength");
            members.Add(
                $"\"extensions\":{{\"KHR_materials_emissive_strength\":{{\"emissiveStrength\":{Number(textures.EmissiveStrength)}}}}}"
            );
        }

        return $"{{{string.Join(",", members)}}}";
    }

    /// <summary>A relative path as a URI: each part percent-encoded, the slashes kept.</summary>
    static string Escape(string path) =>
        string.Join("/", path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    static int Indices(List<byte> buffer, List<string> views, List<string> accessors, int[] indices) {
        var offset = Align(buffer);

        foreach (var index in indices) {
            buffer.AddRange(BitConverter.GetBytes((uint)index));
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

    static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    static string Text(string value) =>
        "\""
        + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        + "\"";
}
