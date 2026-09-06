// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Painting;
using Vixen.Rendering;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>The geometry a stack is painted on, resolved: its UV triangles, and its coverage map.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>'s host half.</b>
///         <see cref="LayerStackAsset.Model" /> is a path in a file and this is what turns one into
///         the two things painting needs: the coordinates the 2D view draws its islands from, and the
///         <see cref="PaintCoverage" /> a stroke dilates across. Both come out of the same array, so
///         the outlines an artist aims with and the texels the brush will accept cannot disagree.
///     </para>
///     <para>
///         ⚠ <b>The source file is read, not the imported artefacts, and that is a limit worth
///         stating rather than a shortcut.</b> <c>ModelReader.Read</c> is the same call the importer
///         makes and it returns the atlas the file already carries — so a model imported with
///         <c>ModelImportSettings.Unwrap</c> set, whose coordinates were <em>generated</em> during
///         the import, resolves here to the coordinates it had before that ran, which for a mesh with
///         no UVs at all is none. That case is reported as "no texture coordinates" rather than
///         silently painted over.
///     </para>
///     <para>
///         ⚠ <b>Every failure is a returned sentence and none is an exception.</b> This is asked from
///         a panel build and from a pointer-down, and <c>LayerStackPreview.Evaluate</c>'s reason
///         holds unchanged: a throw out of one takes the editor's frame with it. A model that has
///         been deleted, a file Assimp will not parse, and a set naming a mesh the model does not
///         have are all sentences.
///     </para>
///     <para>
///         ⚠ <b>The coverage map is built once per size and kept</b>, because the caller is a
///         pointer-down. Rasterising a 25 000-triangle atlas at 4K on every stroke would put the
///         mesh's triangle count into the per-stamp path, which is precisely the property doc 48's
///         exit criterion 8 is about.
///     </para>
/// </remarks>
sealed class LayerStackMesh {
    /// <summary>Which extensions name a model, asked of the importer rather than listed here.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived, because a list here would be a second opinion about <c>[Importer]</c>.</b>
    ///     The shape five exact-equality roll calls in this workstream have gone red on is a second
    ///     copy of a set somebody else declares; <c>ModelImporter</c> declares this one, and reading
    ///     it back off the attribute means a format added there is a format the mesh picker offers
    ///     with no edit here.
    /// </remarks>
    public static IReadOnlyList<string> Extensions { get; } = new ModelImporter().Extensions;

    readonly Vector2[] coordinates;

    PaintCoverage? coverage;

    LayerStackMesh(string model, string mesh, string named, int triangles, Vector2[] coordinates) {
        Model = model;
        Mesh = mesh;
        Named = named;
        Triangles = triangles;
        this.coordinates = coordinates;
    }

    /// <summary>The model's path, relative to the project root.</summary>
    public string Model { get; }

    /// <summary>What the set asked for, or empty for every mesh in the model.</summary>
    public string Mesh { get; }

    /// <summary>What the meshes this resolved to are called, joined for a status line.</summary>
    public string Named { get; }

    /// <summary>How many triangles carry the coordinates.</summary>
    public int Triangles { get; }

    /// <summary>Three UV coordinates per triangle, in the unit square.</summary>
    /// <remarks>
    ///     What <c>PaintUvView.ShowIslands</c> takes and what <see cref="Coverage" /> rasterises —
    ///     one array behind both, so the outlines and the paintable texels are the same claim.
    /// </remarks>
    public IReadOnlyList<Vector2> Coordinates => coordinates;

    /// <summary>Which texels of an atlas this size the mesh's islands cover.</summary>
    /// <param name="width">The atlas width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The map.</returns>
    /// <remarks>
    ///     ⚠ <b>Kept for the size it was built at and rebuilt when that changes.</b> A stack's
    ///     resolution is an edit an artist makes with the pointer up, and a map held for the old size
    ///     would be refused by <c>PaintStroke</c>'s own dimension check — correctly, and with a
    ///     message about a coverage map rather than about the resolution somebody just changed.
    /// </remarks>
    public PaintCoverage Coverage(int width, int height) {
        if (coverage is { } held && held.Width == width && held.Height == height) {
            return held;
        }

        coverage = PaintCoverage.FromTriangles(width, height, coordinates);

        return coverage;
    }

    /// <summary>The UV triangles of one mesh, three coordinates at a time.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="into">Where the coordinates go.</param>
    /// <returns>How many triangles it contributed.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A triangle whose corners are not all in the coordinate array is dropped rather than
    ///     clamped.</b> <c>MeshData.TexCoords</c> is empty for a mesh with no atlas and may be short
    ///     for a file that lied about its vertex count; a clamp would put every such triangle at
    ///     <c>(0, 0)</c>, which rasterises as a covered texel in the corner of the atlas — a coverage
    ///     map that is wrong in a place an artist would paint.
    /// </remarks>
    public static int Triangulate(MeshData mesh, List<Vector2> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        var uvs = mesh.TexCoords;
        var indices = mesh.Indices;
        var made = 0;

        for (var triangle = 0; triangle + 2 < indices.Length; triangle += 3) {
            var a = indices[triangle];
            var b = indices[triangle + 1];
            var c = indices[triangle + 2];

            if ((uint)a >= (uint)uvs.Length || (uint)b >= (uint)uvs.Length || (uint)c >= (uint)uvs.Length) {
                continue;
            }

            into.Add(uvs[a]);
            into.Add(uvs[b]);
            into.Add(uvs[c]);
            made++;
        }

        return made;
    }

    /// <summary>Resolves the model a stack names, narrowed to one set's mesh.</summary>
    /// <param name="project">The project the path is resolved against.</param>
    /// <param name="stack">The stack, whose <see cref="LayerStackAsset.Model" /> says which model.</param>
    /// <param name="set">The set, whose <see cref="TextureSetAsset.Mesh" /> narrows it, or null.</param>
    /// <param name="refusal">Why there is none, or empty.</param>
    /// <returns>The mesh, or <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">The project or the stack is null.</exception>
    public static LayerStackMesh? Open(
        EditorProject project,
        LayerStackAsset stack,
        TextureSetAsset? set,
        out string refusal
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(stack);

        refusal = "";

        var reference = stack.Model.Trim();

        if (reference.Length == 0) {
            refusal = "This stack names no model, so it has no UV islands and every texel of the atlas is "
                + "paintable. Bind one in the Layer Stack panel.";

            return null;
        }

        if (!project.Assets.TryGetByPath(reference, out var asset)) {
            refusal = $"'{reference}' is not in this project's assets, so there is no mesh to read.";

            return null;
        }

        var file = project.Paths.Absolute(asset.Path);
        var extension = Path.GetExtension(file).ToLowerInvariant();

        // ⚠ Asked before the file is opened, because Assimp's answer to a PNG is a parse failure
        // naming the format rather than the binding. A sentence about which files are models is one
        // an artist can act on.
        if (!Extensions.Contains(extension)) {
            refusal = $"'{reference}' is not a model this build reads. Models are {string.Join(", ", Extensions)}.";

            return null;
        }

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(file);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            refusal = $"'{reference}' would not read: {failure.Message}";

            return null;
        }

        ReadModel read;

        try {
            read = ModelReader.Read(bytes, extension, Path.GetFileNameWithoutExtension(file), new ModelImportSettings());
        } catch (ModelFormatException failure) {
            refusal = $"'{reference}' is not readable as a model: {failure.Message}";

            return null;
        }

        var wanted = (set?.Mesh ?? "").Trim();
        List<Vector2> coordinates = [];
        List<string> named = [];
        var triangles = 0;
        var matched = 0;

        foreach (var mesh in read.Meshes) {
            if (wanted.Length > 0 && !string.Equals(mesh.Name, wanted, StringComparison.Ordinal)) {
                continue;
            }

            matched++;
            named.Add(mesh.Name);
            triangles += Triangulate(mesh, coordinates);
        }

        if (matched == 0) {
            refusal = $"'{reference}' has no mesh called '{wanted}'. It has "
                + $"{string.Join(", ", read.Meshes.Select(mesh => $"'{mesh.Name}'"))}.";

            return null;
        }

        // ⚠ Zero triangles from meshes that exist is the "no atlas" case and not the "no mesh" one,
        // and it needs its own sentence: `PaintCoverage.FromTriangles` over an empty array is a map
        // covering nothing at all, so a brush over it would refuse every texel and the pane would
        // look exactly like a brush that is broken.
        if (triangles == 0) {
            refusal = $"'{reference}' has no texture coordinates on "
                + $"{(wanted.Length > 0 ? $"'{wanted}'" : "any mesh")}, so it has no UV islands. Unwrap it "
                + "before painting on it.";

            return null;
        }

        return new(reference, wanted, string.Join(", ", named), triangles, [.. coordinates]);
    }
}
