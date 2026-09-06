// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Models;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.Assets.Content;

/// <summary>
///     The geometry a scene's mesh references name, read out of the project's own import cache.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>AssetMeshSource</c>'s editor twin, and the reason <see cref="IMeshSource" /> is an
///         interface.</b> A game resolves a reference through a catalog and a bundle; the editor has
///         neither and does not want them — it has the chunks the last import wrote, in a store on disk
///         beside the project. Same question, two places to look, and everything above the interface is
///         identical.
///     </para>
///     <para>
///         ⚠ <b>Why the editor cannot simply use the game's, and it is not the reason this paragraph
///         used to give.</b> It said a <c>ContentCatalog</c> is built by a content build and that
///         waiting for one would make the viewport a function of the build rather than of the files.
///         That is not true of the catalog the editor would actually use: <c>LooseContent.Write</c>
///         needs no build — no packing, no copying — and it reads this very import cache. It is
///         sub-asset granular too, because <c>BuildPlanner</c> emits one entry per sub-asset.
///     </para>
///     <para>
///         ⚠ <b>The real reason is that a catalog is what <em>ships</em> and a viewport has to draw
///         what is in the project.</b> <c>BuildPlanner.AddressOf</c> gives an excluded asset no
///         address — <c>AddressableInfo.Excluded</c> being the designed way to keep "a reference FBX
///         kept beside the one that ships" out of a build — so the catalog has no entry for it and
///         <c>AssetMeshSource</c> throws <c>ReferenceNotFoundException</c>, while the lookup below
///         finds it by id and reads it. The same goes for a sub-asset the <c>.meta</c> does not name
///         and for two sub-assets whose names collide, both of which refuse the whole asset. Every
///         one of those is silent — the catalog is written successfully and the asset is simply not
///         in it — so a viewport moved onto that path would stop drawing part of a project with
///         nothing anywhere saying which part.
///     </para>
///     <para>
///         ⚠ <b>Synchronous, unlike every other source.</b> The chunk is on local disk and already
///         decompressed by the object database, so there is no load to be in flight — and an editor
///         frame that skipped a mesh would have to re-ask, which for a viewport redrawn on demand means
///         a mesh that appears when something else happens to cause a repaint. The protocol still allows
///         it to answer false, which is what a missing or unreadable chunk is.
///     </para>
/// </remarks>
public sealed class ProjectMeshSource : IMeshSource {
    readonly ProjectWorkspace workspace;
    readonly Dictionary<AssetReference, MeshData?> meshes = [];

    /// <summary>Builds a source over a project.</summary>
    /// <param name="workspace">The project, for its import cache and its chunk store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workspace" /> is null.</exception>
    public ProjectMeshSource(ProjectWorkspace workspace) {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
    }

    /// <summary>How many distinct meshes have been asked for.</summary>
    public int Requested => meshes.Count;

    /// <summary>How many of them were read.</summary>
    public int Loaded {
        get {
            var count = 0;

            foreach (var mesh in meshes.Values) {
                if (mesh is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Forgets what has been read, so a re-import is picked up.</summary>
    /// <remarks>
    ///     ⚠ <b>Called when an import finishes, or the viewport keeps drawing the old mesh for ever.</b>
    ///     A chunk is content-addressed, so a re-imported mesh is a <em>different</em> id under the same
    ///     reference — nothing about the cached <see cref="MeshData" /> would ever say it is stale.
    /// </remarks>
    public void Invalidate() => meshes.Clear();

    /// <inheritdoc />
    public bool TryGet(AssetReference reference, out MeshData mesh) {
        mesh = null!;

        if (reference.IsNull) {
            return false;
        }

        if (!meshes.TryGetValue(reference, out var found)) {
            meshes[reference] = found = Read(reference);
        }

        if (found is null) {
            return false;
        }

        mesh = found;
        return true;
    }

    /// <summary>What a model asset's last import declared its meshes to be, in declaration order.</summary>
    /// <param name="assetFile">The model's own file, absolute or project-relative. Not its sidecar.</param>
    /// <returns>
    ///     One entry per mesh, whose <see cref="SubAssetEntry.Name" /> is what the project calls it and
    ///     whose <see cref="SubAssetEntry.Id" /> completes an <see cref="AssetReference" /> for
    ///     <see cref="TryGet" />. Empty where the asset has never been imported.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="assetFile" /> is null or empty.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The half of <see cref="TryGet" /> that answers "which meshes are there", which
    ///         nothing could answer at all.</b> A reference names one sub-asset and every editor
    ///         surface that wants a model's geometry — a mesh-map bake, a layer stack's binding, a
    ///         mesh picker — starts from the asset and not from the sub-asset. The sidecar is where
    ///         that list already is: <c>ImportPipeline</c> writes what an import declared back into
    ///         it, so this needs no model parse, no Assimp and no artefact store.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The name here is the <em>project's</em> name for the mesh and not the file's</b> —
    ///         <c>ImportContext.DeclareSubAsset</c> applies <c>SubAssetNames</c> before deriving the
    ///         id, and suffixes a second <c>Cube</c>. So a caller matching on <c>MeshData.Name</c>
    ///         read out of the source file is matching on the one name a renamed mesh does not have,
    ///         which is <a href="https://github.com/Rikarin/Vixen/issues/934">#934</a>'s second bullet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty is "not imported yet" and never an exception.</b> A model dropped into
    ///         <c>Assets/</c> a moment ago has a sidecar with a GUID and no sub-assets, which is the
    ///         commonest moment to want a bake; a sidecar that will not parse is the same answer,
    ///         because every caller of this has a fallback that reads the source file and none of
    ///         them can usefully be stopped by a YAML error in a file they did not write.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<SubAssetEntry> Declared(string assetFile) {
        ArgumentException.ThrowIfNullOrEmpty(assetFile);

        var sidecar = AssetMetaFile.PathFor(assetFile);

        if (!File.Exists(sidecar)) {
            return [];
        }

        AssetMeta meta;

        try {
            meta = AssetMetaFile.ReadFile(sidecar);
        } catch (Exception failure) when (failure
            is IOException
            or UnauthorizedAccessException
            or YamlParseException
            or YamlBindingException
            or MetaVersionException) {
            return [];
        }

        var meshes = new List<SubAssetEntry>();

        foreach (var entry in meta.SubAssets) {
            if (string.Equals(entry.Type, ModelImporter.MeshKind, StringComparison.Ordinal)) {
                meshes.Add(entry);
            }
        }

        return meshes;
    }

    /// <summary>Reads one mesh's chunk, or null if this project has none for it.</summary>
    /// <remarks>
    ///     The sub-asset id in the reference is the one the importer declared, and the import record
    ///     lists what each one was written as — so this is a lookup rather than a search, and a reference
    ///     to a mesh that has since been removed from the model finds nothing rather than the wrong part.
    /// </remarks>
    MeshData? Read(AssetReference reference) {
        if (!workspace.Cache.TryGet(reference.Asset, out var record) || record is null) {
            return null;
        }

        foreach (var artifact in record.Artifacts) {
            if (artifact.SubAsset != reference.SubAsset) {
                continue;
            }

            try {
                return workspace.Artifacts.Read<MeshData>(artifact.Id);
            } catch (SerializationException) {
                // A chunk written by a different type: the reference names a skeleton or a clip rather
                // than a mesh. That is a scene pointing at the wrong sub-asset, which is an entity that
                // draws nothing rather than an editor that will not open.
                return null;
            }
        }

        return null;
    }
}
