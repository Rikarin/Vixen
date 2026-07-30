// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;

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
    /// <remarks>
    ///     Two since distance fields joined the artefacts a model produces. A model imported under
    ///     version one has no field sub-asset at all, and nothing downstream could tell that from a
    ///     model whose meshes are all skinned — so the version has to say it rather than the content.
    ///     Three since cluster hierarchies did, for the same reason and with the same consequence:
    ///     every model in every project re-imports, which is what "the artefact this version produces
    ///     is not the one the last version produced" means. Four since the geometry pages joined them —
    ///     a hierarchy without pages is a hierarchy nothing can draw, and the two are produced together
    ///     or not at all. Five since a skinned mesh's pages carry its bone influences: a page vertex
    ///     that did not is one a raster can only draw in its bind pose, and no artefact of version four
    ///     can be told from one of version five by looking at it — the stride is per mesh either way.
    ///     Six since the cluster artefacts became two addressable sub-assets that the mesh points at: a
    ///     version-five model wrote three chunks under one sub-asset id, which a content build refuses
    ///     outright, so those artefacts were never addressable and no runtime could load one.
    /// </remarks>
    public override int Version => 6;

    /// <summary>What the sub-asset holding a mesh's hierarchy and page records is called.</summary>
    /// <remarks>
    ///     The kind and the artefact type are the same word on purpose — one sub-asset, one chunk, one
    ///     name for what is in it — and it is <see cref="VirtualGeometryContent" />'s constant rather
    ///     than a second spelling of it, which the dependency direction happens to allow here: this
    ///     assembly references the runtime and the runtime does not reference this one.
    /// </remarks>
    public const string ClusterKind = VirtualGeometryContent.ClusterArtifact;

    /// <summary>What the sub-asset holding a mesh's page blob is called.</summary>
    public const string ClusterPageKind = VirtualGeometryContent.ClusterPageArtifact;

    /// <summary>What a mesh's cluster sub-asset is named, which is what its address is built from.</summary>
    /// <param name="mesh">The mesh's own name.</param>
    /// <returns>The sub-asset name.</returns>
    /// <remarks>
    ///     ⚠ <b>Distinct from the mesh's name, and that is a build error rather than a tidiness
    ///     point.</b> A sub-asset's address is built from its name alone, so a hierarchy called what its
    ///     mesh is called collides with it exactly as "a mesh and a material both called Body" does —
    ///     which <c>BuildPlanner</c> reports and refuses.
    /// </remarks>
    public static string ClusterName(string mesh) => mesh + " Clusters";

    /// <summary>What a mesh's page-blob sub-asset is named.</summary>
    /// <inheritdoc cref="ClusterName" path="/remarks" />
    public static string PageName(string mesh) => mesh + " Pages";

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

        var fields = 0;
        var skinned = 0;
        var clusters = 0;
        var refused = 0;
        var pageBytes = 0L;

        foreach (var mesh in read.Meshes) {
            if (settings.GenerateMeshlets && mesh.Indices.Length > 0) {
                // Skinned meshes are included, unlike the distance field below. A cluster carries the
                // range of bones its vertices are weighted to, so a traversal can expand its bound by
                // what those bones are doing — which is improvement 1 of docs/plan/22-virtualized-geometry.md
                // and the reason skinning is designed in here rather than retrofitted later.
                var meshlets = ModelCompiler.CompileMeshlets(mesh, settings.ToMeshletSettings(), context.Report);

                if (meshlets is null) {
                    refused++;
                } else {
                    var pages = ModelCompiler.CompilePages(mesh, meshlets, context.Report);

                    if (pages is not null) {
                        // ⚠ Two sub-assets, with names of their own, and both halves of that matter. An
                        // address names exactly one chunk, so the three artefacts this used to write
                        // under one sub-asset id could not be addressed — a content build refuses "two
                        // chunks for one sub-asset". And a sub-asset's address is built from its *name*,
                        // so a hierarchy called the same thing as its mesh collides with it just as a
                        // mesh and a material both called Body would.
                        var records = context.DeclareSubAsset(ClusterKind, ClusterName(mesh.Name));
                        var blob = context.DeclareSubAsset(ClusterPageKind, PageName(mesh.Name));

                        // The records travel together because they are read together, and the blob
                        // travels alone because it is seeked into rather than deserialised — one chunk
                        // carrying both would read every page of every mesh at load, which is the one
                        // thing paging exists to avoid. See MeshletPageSet.WithoutData.
                        context.Write(
                            records,
                            ClusterKind,
                            Serializer.ToBytes(new VirtualGeometryAsset(meshlets, pages.WithoutData()))
                        );

                        context.Write(blob, ClusterPageKind, pages.Data);

                        // Written on the mesh before the mesh is written, which is the whole join: a
                        // frame holding a mesh reference cannot derive a sub-asset id — that needs the
                        // importer's name, the kind and the mesh's name — so the mesh has to say.
                        mesh.Clusters = new(context.Guid, records);
                        mesh.ClusterPages = new(context.Guid, blob);

                        pageBytes += pages.Data.Length;
                    }

                    clusters += meshlets.Meshlets.Length;
                }
            }

            context.Write(context.DeclareSubAsset("Mesh", mesh.Name), "Mesh", Serializer.ToBytes(mesh));

            if (!settings.GenerateDistanceFields || mesh.Indices.Length == 0) {
                continue;
            }

            // A field is baked in one pose and a skinned mesh does not stay in it. Baking the bind
            // pose anyway would put an occluder where the character is standing in the T-pose it was
            // exported in, which is a shadow on the floor next to somebody rather than under them.
            // Unreal excludes skeletal meshes from its global field for the same reason.
            if (mesh.IsSkinned) {
                skinned++;

                continue;
            }

            var field = MeshDistanceFieldBaker.Bake(
                mesh.Positions,
                mesh.Indices,
                settings.ToDistanceFieldSettings()
            );

            context.Write(
                context.DeclareSubAsset("DistanceField", mesh.Name),
                "DistanceField",
                Serializer.ToBytes(field)
            );

            fields++;
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
            + (fields > 0 ? $", {fields} distance field(s)" : string.Empty)
            + (clusters > 0 ? $", {clusters} cluster(s)" : string.Empty)
            + (pageBytes > 0 ? $", {pageBytes / 1024} KB of geometry pages" : string.Empty)
            + "."
        );

        if (refused > 0) {
            // The import as a whole still fails, because CompileMeshlets reported an error and the
            // context counts it. This says how many, which the per-mesh messages do not.
            context.Report(
                ImportSeverity.Error,
                $"{refused} mesh(es) produced a cluster hierarchy that would crack and therefore have "
                + "none. Nothing here is drawable through the virtualized path until that is fixed."
            );
        }

        if (skinned > 0) {
            // Information rather than a warning: this is the correct outcome, not a problem to fix.
            context.Report(
                ImportSeverity.Information,
                $"{skinned} skinned mesh(es) have no distance field. A field is baked in one pose and "
                + "a skinned mesh does not stay in it, so it would occlude where the mesh is not."
            );
        }

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
