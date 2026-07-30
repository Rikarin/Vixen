// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
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
///         <b>Why the editor cannot simply use the game's.</b> A <c>ContentCatalog</c> is built by a
///         content build, which is something an author runs when they ship rather than every time they
///         open a scene. Waiting for one to look at a level would make the viewport a function of the
///         build rather than of the files.
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
