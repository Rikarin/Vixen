// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Geometry.Remeshing;
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
    ///     Eight since <c>MeshData</c> stopped carrying a colour channel and a second coordinate set:
    ///     two members left the middle of a positional record, so a version-seven mesh chunk read by
    ///     this build would give <c>Indices</c> a coordinate array. The generated reader's member
    ///     count refuses it outright, and this bump is what regenerates it before anything asks.
    ///     Nine since a mesh carries its blend shapes: <c>MorphTargets</c> is a member a version-eight
    ///     chunk has no bytes for, and a reader that reached the end of one would read the next
    ///     chunk's. It is appended last on purpose — that is the position at which the members before
    ///     it keep their offsets — but appended is still a change to the member count, and the member
    ///     count is what the generated reader checks.
    ///     Ten since <c>AnimationChannel</c> grew a scalar weight track — <c>Shape</c>,
    ///     <c>WeightTimes</c> and <c>Weights</c>, appended last — which is what lets a clip drive a
    ///     blend shape. ⚠ <b>This bump is a re-import trigger and not a compatibility fence, which
    ///     makes it the odd one out above.</b> The generated reader writes its member count and
    ///     refuses only <c>count &gt; MemberCount</c>, so <em>appended</em> members are read back by
    ///     an older payload as their defaults — a version-nine clip chunk answers "no weight track",
    ///     which is true. Nothing would break without this. What would happen instead is nothing at
    ///     all: the curves were being dropped at import, and only a re-import can go back to the
    ///     source file for them. So the cost is one content build over the project, and the benefit
    ///     is that a face that was silently still starts moving.
    /// </remarks>
    public override int Version => 10;

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

    /// <summary>The chunk type a mesh artefact is recorded as.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>[DataContract]</c> alias, not a friendly name, and the two are not the
    ///         same thing.</b> The sub-asset <i>kind</i> beside it is <c>"Mesh"</c> — that is what a
    ///         <c>.meta</c> lists and what a person reads. This is what goes in the chunk header, and
    ///         <c>ImportPipeline.TypeIdOf</c> resolves it through the type registry, falling back to
    ///         <c>ImportedArtifact</c> when it does not resolve.
    ///     </para>
    ///     <para>
    ///         That fallback is silent, and it was wrong here for as long as this importer existed:
    ///         every mesh chunk in every content build carried an editor type's id, so a game loading
    ///         one got "nothing registered in this process claims it" about content the build had just
    ///         declared good. A constant rather than a literal, so the alias and the writer cannot
    ///         drift apart again without the compiler noticing.
    ///     </para>
    /// </remarks>
    public const string MeshType = "MeshData";

    /// <summary>The chunk type a signed-distance-field artefact is recorded as.</summary>
    /// <inheritdoc cref="MeshType" path="/remarks" />
    public const string DistanceFieldType = "MeshDistanceField";

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

    /// <summary>What a mesh's signed-distance-field sub-asset is named.</summary>
    /// <inheritdoc cref="ClusterName" path="/remarks" />
    /// <remarks>
    ///     ⚠ <b>This was the mesh's own name, and the consequence was every model with a field
    ///     silently leaving the build.</b> Two sub-assets with one name are two claims on one address,
    ///     which <c>BuildPlanner</c> refuses — and refusing an address means the model has none, which
    ///     means every scene referencing it fails with "depends on asset …, which has no address".
    ///     The clusters and the pages were suffixed for exactly this reason from the start; the field
    ///     was not, and nothing noticed until a project shipped a mesh, a scene and a distance field
    ///     together.
    /// </remarks>
    public static string FieldName(string mesh) => mesh + " Field";

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

        // ⚠ Before anything else reads a mesh, and that ordering is the whole of the wiring.
        // docs/plan/41 § D16 and docs/plan/42 § D13 both put these ahead of the compilers: clusters,
        // pages and a distance field built from the source triangles and then thrown away when the
        // retopology replaced them would be the most expensive no-op in the pipeline.
        await RetopologiseAsync(context, settings, read, cancellationToken).ConfigureAwait(false);

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

            // The sub-asset kind is "Mesh" and the chunk type is the contract alias. See MeshType.
            context.Write(context.DeclareSubAsset("Mesh", mesh.Name), MeshType, Serializer.ToBytes(mesh));

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
                context.DeclareSubAsset("DistanceField", FieldName(mesh.Name)),
                DistanceFieldType,
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

    /// <summary>docs/plan/41 § D16 and docs/plan/42 § D13, over every mesh the file carried.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The meshes are replaced in place rather than added beside the originals.</b> A
    ///         retopologised mesh is not a second level of detail — doc 22's cluster hierarchy is what
    ///         provides those, and it is built from whatever this leaves behind. Keeping both would
    ///         double every model in the bundle and leave two sub-assets with a claim on one address.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A guide's file is declared as a dependency, which is what puts it in the cache
    ///         key.</b> Otherwise editing the curve would leave every model that follows it importing
    ///         from cache, which is the class of staleness that looks like the setting doing nothing.
    ///     </para>
    /// </remarks>
    static async ValueTask RetopologiseAsync(
        ImportContext context,
        ModelImportSettings settings,
        ReadModel read,
        CancellationToken cancellationToken
    ) {
        if (!settings.Retopologize && settings.Unwrap == UnwrapMode.Never) {
            return;
        }

        var guides = await GuidesAsync(context, settings, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < read.Meshes.Length; index++) {
            cancellationToken.ThrowIfCancellationRequested();

            var result = ModelRetopology.Run(read.Meshes[index], settings, guides);

            foreach (var message in result.Messages) {
                context.Report(ImportSeverity.Information, message);
            }

            if (!result.Remeshed && !result.Unwrapped) {
                continue;
            }

            // The name, the material and the cluster references are the mesh's identity and the
            // geometry is not — so what comes back keeps the record it replaced apart from its arrays.
            result.Mesh.MaterialIndex = read.Meshes[index].MaterialIndex;
            read.Meshes[index] = result.Mesh;
        }
    }

    /// <summary>The guide curves the settings name, read from their assets.</summary>
    static async ValueTask<IReadOnlyList<RemeshGuide>> GuidesAsync(
        ImportContext context,
        ModelImportSettings settings,
        CancellationToken cancellationToken
    ) {
        if (settings.RetopologyGuides.Count == 0) {
            return [];
        }

        var guides = new List<RemeshGuide>();

        foreach (var reference in settings.RetopologyGuides) {
            if (string.IsNullOrWhiteSpace(reference.Spline)) {
                continue;
            }

            var path = new VirtualPath(reference.Spline.StartsWith('/') ? reference.Spline : "/" + reference.Spline);

            context.DependsOnFile(path);

            try {
                await using var stream = await context.Files.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var asset = YamlSerializer.Deserialize<SplineAsset>(YamlReader.Read(text));

                if (!asset.CanBuild) {
                    context.Report(
                        ImportSeverity.Warning,
                        $"The guide '{reference.Spline}' has fewer than two control points, so it is not a curve."
                    );

                    continue;
                }

                guides.Add(ModelRetopology.ToGuide(asset.Build(), reference.Strength));
            } catch (Exception failure) when (failure is not OperationCanceledException) {
                // A warning rather than an error, and deliberately: a guide is a hint about edge flow,
                // so a missing one costs topology quality and never validity. Refusing the import
                // would make a renamed curve break every model that ever mentioned it.
                context.Report(
                    ImportSeverity.Warning,
                    $"The guide '{reference.Spline}' could not be read and was skipped: {failure.Message}"
                );
            }
        }

        return guides;
    }
}
