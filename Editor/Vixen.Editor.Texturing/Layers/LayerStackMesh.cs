// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Painting;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

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
///         ⚠ <b>The imported artefacts are read where there are any, and the source file only where
///         there are not</b> — <a href="https://github.com/Rikarin/Vixen/issues/934">#934</a>. The
///         source file's atlas is the one the <em>file</em> carries, and that is not what the project
///         has: <c>ModelImportSettings.Unwrap</c> generates coordinates inside
///         <c>ModelRetopology.Run</c>, which the <em>importer</em> calls and <c>ModelReader</c> does
///         not — so a model imported with <c>UnwrapMode.Always</c> resolved here to "no texture
///         coordinates", a refusal about a state the artist had already fixed. Reading the mesh chunk
///         that import wrote gets the post-unwrap coordinates, and reading the sidecar's declared
///         sub-assets gets the post-<c>SubAssetNames</c> name, which is the one
///         <see cref="TextureSetAsset.Mesh" /> is matched against.
///     </para>
///     <para>
///         ⚠ <b>The fallback is the honest half and it is not the same answer.</b> A model dropped
///         into <c>Assets/</c> a minute ago has no import record at all, and that is the commonest
///         moment to bind one — so it is read through <c>ModelReader</c> exactly as before, and a
///         mesh with no atlas is told that its import has not run rather than that it needs
///         unwrapping. The two sentences point at different actions.
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

    /// <summary>What the project calls the meshes of the model a stack names, or an empty list.</summary>
    /// <param name="project">The project the path is resolved against.</param>
    /// <param name="stack">The stack, whose <see cref="LayerStackAsset.Model" /> says which model.</param>
    /// <returns>The names, in the order the import declared them.</returns>
    /// <exception cref="ArgumentNullException">The project or the stack is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Cheap enough for a panel build, which is what
    ///     <a href="https://github.com/Rikarin/Vixen/issues/941">#941</a> said it could not be.</b>
    ///     That issue declined a picker for <see cref="TextureSetAsset.Mesh" /> because offering the
    ///     names means knowing them and knowing them means an Assimp parse — seconds on a hero asset.
    ///     It is not: an import writes the names it declared back into the <c>.meta</c>, so this is
    ///     one small YAML file and no geometry at all. The objection was true of the source file and
    ///     false of the project.
    ///     ⚠ <b>Empty is "not imported yet" and it is the one case that still has no answer</b> — a
    ///     model whose import has never run declares no sub-assets, and there is nowhere but the file
    ///     to read a name from. A caller offering a picker shows the field as it is and says so.
    /// </remarks>
    public static IReadOnlyList<string> Names(EditorProject project, LayerStackAsset stack) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(stack);

        var reference = stack.Model.Trim();

        if (reference.Length == 0 || !project.Assets.TryGetByPath(reference, out var asset)) {
            return [];
        }

        return [.. ProjectMeshSource.Declared(project.Paths.Absolute(asset.Path)).Select(entry => entry.Name)];
    }

    /// <summary>Resolves the model a stack names, narrowed to one set's mesh.</summary>
    /// <param name="project">The project the path is resolved against.</param>
    /// <param name="stack">The stack, whose <see cref="LayerStackAsset.Model" /> says which model.</param>
    /// <param name="set">The set, whose <see cref="TextureSetAsset.Mesh" /> narrows it, or null.</param>
    /// <param name="geometry">
    ///     Where the imported mesh chunks are read from — the host's <see cref="IMeshSource" />, or
    ///     null in a host that publishes none, which takes the source-file path below.
    /// </param>
    /// <param name="refusal">Why there is none, or empty.</param>
    /// <returns>The mesh, or <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">The project or the stack is null.</exception>
    public static LayerStackMesh? Open(
        EditorProject project,
        LayerStackAsset stack,
        TextureSetAsset? set,
        IMeshSource? geometry,
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

        var wanted = (set?.Mesh ?? "").Trim();
        IReadOnlyList<SubAssetEntry> declared = geometry is null ? [] : ProjectMeshSource.Declared(file);

        return declared.Count > 0
            ? Imported(reference, asset.Guid, declared, wanted, geometry!, out refusal)
            : FromSource(reference, file, extension, wanted, out refusal);
    }

    /// <summary>Reads the mesh chunks the last import wrote, which is what the project has.</summary>
    /// <param name="reference">The stack's own path to the model, for the sentences.</param>
    /// <param name="model">The model asset.</param>
    /// <param name="declared">Its declared mesh sub-assets, which is what the names are matched on.</param>
    /// <param name="wanted">The set's mesh name, or empty for every mesh.</param>
    /// <param name="geometry">Where a chunk is read from.</param>
    /// <param name="refusal">Why there is none, or empty.</param>
    /// <returns>The mesh, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>A declared mesh whose chunk will not read is skipped rather than refused</b>, and the
    ///     two are told apart at the end: no name matched is "there is no mesh called that", and names
    ///     that matched with nothing behind them is a <c>Library/</c> the sidecar disagrees with —
    ///     which is a re-import and not something an artist can fix by renaming anything.
    /// </remarks>
    static LayerStackMesh? Imported(
        string reference,
        AssetId model,
        IReadOnlyList<SubAssetEntry> declared,
        string wanted,
        IMeshSource geometry,
        out string refusal
    ) {
        refusal = "";

        List<Vector2> coordinates = [];
        List<string> named = [];
        var triangles = 0;
        var matched = 0;
        var read = 0;

        foreach (var entry in declared) {
            if (wanted.Length > 0 && !string.Equals(entry.Name, wanted, StringComparison.Ordinal)) {
                continue;
            }

            matched++;

            if (!geometry.TryGet(new(model, entry.Id), out var mesh)) {
                continue;
            }

            read++;
            named.Add(entry.Name);
            triangles += Triangulate(mesh, coordinates);
        }

        if (matched == 0) {
            refusal = $"'{reference}' has no mesh called '{wanted}'. This project's import of it produced "
                + $"{string.Join(", ", declared.Select(entry => $"'{entry.Name}'"))}.";

            return null;
        }

        if (read == 0) {
            refusal = $"'{reference}' declares {matched} mesh(es) that this project has no imported geometry "
                + "for. Import the project again — the sidecar and the artefact store disagree.";

            return null;
        }

        if (triangles == 0) {
            refusal = $"'{reference}' has no texture coordinates on "
                + $"{(wanted.Length > 0 ? $"'{wanted}'" : "any mesh")}, so it has no UV islands. Unwrap it "
                + "before painting on it — the model importer's Unwrap setting will.";

            return null;
        }

        return new(reference, wanted, string.Join(", ", named), triangles, [.. coordinates]);
    }

    /// <summary>Reads the model file itself, for a model this project has never imported.</summary>
    /// <param name="reference">The stack's own path to the model, for the sentences.</param>
    /// <param name="file">Where it is.</param>
    /// <param name="extension">Its extension, lowercased, which is what tells Assimp the format.</param>
    /// <param name="wanted">The set's mesh name, or empty for every mesh.</param>
    /// <param name="refusal">Why there is none, or empty.</param>
    /// <returns>The mesh, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The atlas here is the one the file carries, and the "no coordinates" sentence says
    ///     which action is owed.</b> An unwrap is an <em>import</em> step — <c>ModelRetopology.Run</c>
    ///     is called by <c>ModelImporter</c> and not by <c>ModelReader</c> — so a model whose settings
    ///     ask for one and whose import has not run has no atlas here and a perfectly good one waiting.
    ///     Telling that artist to unwrap the model would be advice about something already done.
    /// </remarks>
    static LayerStackMesh? FromSource(
        string reference,
        string file,
        string extension,
        string wanted,
        out string refusal
    ) {
        refusal = "";

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
                + $"{(wanted.Length > 0 ? $"'{wanted}'" : "any mesh")}, so it has no UV islands, and this "
                + "project has never imported it. Import it — its Unwrap setting generates the atlas — or "
                + "unwrap it before painting on it.";

            return null;
        }

        return new(reference, wanted, string.Join(", ", named), triangles, [.. coordinates]);
    }
}
