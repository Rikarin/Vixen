// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;

namespace Vixen.Editor.Assets.Models;

/// <summary>Turns a model an artist exported into the chunks a build packs.</summary>
/// <remarks>
///     <para>
///         The first importer that produces more than one thing. A model is a model, and also a mesh
///         per material, a skeleton and a clip per animation — and each of those is separately
///         addressable, separately deduplicated by the object database, and separately loadable.
///         <c>BuildPlanner</c> gives each an address under its owner's (<c>characters/hero</c>,
///         <c>characters/hero#Hero_Body</c>), which is the machinery this is the first real consumer
///         of.
///     </para>
///     <para>
///         <b>The parts are named, not numbered.</b> An exporter that reorders its meshes — which
///         happens whenever an artist re-exports after adding a material — would break every
///         reference that had been stored by position. A sub-asset id is derived from the name, so
///         renaming a mesh breaks a reference and reordering does not, which is the trade worth
///         making.
///     </para>
///     <para>
///         <b>Reading is <see cref="ModelReader" />'s job and this is the plumbing.</b> The split is
///         so that the conversion — where every decision and every way to be wrong lives — is
///         testable against a file with no import context in the way.
///     </para>
/// </remarks>
[Importer(".fbx", ".gltf", ".glb", ".obj", ".dae", ".3ds", ".ply", ".stl", ".blend")]
public sealed class ModelImporter : AssetImporter<ModelImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        ModelImportSettings settings,
        CancellationToken cancellationToken
    ) {
        var path = context.SourcePath.ToString();
        var extension = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);

        byte[] bytes;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        ReadModel read;

        try {
            read = ModelReader.Read(bytes, extension, name, settings, context.Report);
        } catch (ModelFormatException failure) {
            // A file the artist has to fix, reported against the asset. Anything else — an access
            // violation from a corrupt file that got past Assimp's own checks — is deliberately not
            // caught, because that is what the out-of-process worker exists to survive.
            context.Report(ImportSeverity.Error, failure.Message);
            return context.Finish();
        }

        foreach (var mesh in read.Meshes) {
            context.Write(context.DeclareSubAsset("Mesh", mesh.Name), "Mesh", Serializer.ToBytes(mesh));
        }

        if (read.Skeleton is { } skeleton) {
            context.Write(
                context.DeclareSubAsset("Skeleton", skeleton.Name),
                "Skeleton",
                Serializer.ToBytes(skeleton)
            );
        }

        foreach (var clip in read.Animations) {
            context.Write(
                context.DeclareSubAsset("AnimationClip", clip.Name),
                "AnimationClip",
                Serializer.ToBytes(clip)
            );
        }

        // Last, and as the main object. The order does not matter to the pipeline, which sorts
        // artefacts by sub-asset; it matters to a person reading the list, where the thing the file
        // is comes after the things it is made of.
        context.Write(SubAssetId.Main, "Model", Serializer.ToBytes(read.Model));

        context.Report(
            ImportSeverity.Information,
            $"{read.Meshes.Length} mesh(es), {read.Model.Materials.Length} material(s), "
            + $"{read.Model.Nodes.Length} node(s)"
            + (read.Skeleton is { } bones ? $", {bones.Joints.Length} joint(s)" : string.Empty)
            + "."
        );

        if (read.Meshes.Length == 0) {
            context.Report(
                ImportSeverity.Warning,
                "It has no meshes. A file that holds only a camera, a light or an armature imports to "
                + "nothing drawable, which is worth knowing before something tries to render it."
            );
        }

        return context.Finish();
    }
}
