// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;

namespace Vixen.Editor.Assets.Content;

/// <summary>
///     The look of the materials a scene names, read out of the project's own import cache.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="ProjectMeshSource" />'s twin, down to the reason it exists.</b> A game resolves
///         a material through a catalog and a bundle; the editor has neither and does not want them —
///         it has the chunks the last import wrote, in a store on disk beside the project. Waiting for
///         a content build to see what colour a wall is would make the viewport a function of the build
///         rather than of the files.
///     </para>
///     <para>
///         <b>It reads the compiled chunk and not the <c>.vxmat</c>.</b> <c>MaterialImporter</c> already
///         parses the YAML and writes a <see cref="MaterialContent" />, so reading the text again here
///         would be a second parser for one format — and the editor's <c>MaterialAsset</c> binding it
///         separately is exactly the arrangement <see cref="MaterialContent" />'s own remarks call the
///         part worth watching. One reader, and it is the one the build uses.
///     </para>
///     <para>
///         ⚠ <b>A material that has not been imported yet is a miss, and a miss is grey rather than
///         invisible.</b> That is the asymmetry with the mesh source and it is deliberate — see
///         <see cref="ISurfaceSource" />. An entity whose geometry has not arrived draws nothing,
///         because the alternative is a scene whose shape depends on disk speed; an entity whose
///         material has not arrived draws in the neutral surface, because the alternative is a level
///         that disappears while its materials are read.
///     </para>
/// </remarks>
public sealed class ProjectSurfaceSource : ISurfaceSource {
    readonly ProjectWorkspace workspace;
    readonly Dictionary<AssetReference, MaterialSurface?> surfaces = [];

    /// <summary>Builds a source over a project.</summary>
    /// <param name="workspace">The project, for its import cache and its chunk store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workspace" /> is null.</exception>
    public ProjectSurfaceSource(ProjectWorkspace workspace) {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
    }

    /// <summary>How many distinct materials have been asked for.</summary>
    public int Requested => surfaces.Count;

    /// <summary>How many of them were read.</summary>
    public int Loaded {
        get {
            var count = 0;

            foreach (var surface in surfaces.Values) {
                if (surface is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Forgets what has been read, so a re-import is picked up.</summary>
    /// <inheritdoc cref="ProjectMeshSource.Invalidate" path="/remarks" />
    public void Invalidate() => surfaces.Clear();

    /// <inheritdoc />
    public bool TryGet(AssetReference reference, out MaterialSurface surface) {
        surface = MaterialSurface.Default;

        if (reference.IsNull) {
            return false;
        }

        if (!surfaces.TryGetValue(reference, out var found)) {
            surfaces[reference] = found = Read(reference);
        }

        if (found is null) {
            return false;
        }

        surface = found.Value;
        return true;
    }

    /// <summary>Reads one material's chunk and reduces it, or null if this project has none for it.</summary>
    MaterialSurface? Read(AssetReference reference) {
        if (!workspace.Cache.TryGet(reference.Asset, out var record) || record is null) {
            return null;
        }

        foreach (var artifact in record.Artifacts) {
            if (artifact.SubAsset != reference.SubAsset) {
                continue;
            }

            try {
                return MaterialSurface.Of(workspace.Artifacts.Read<MaterialContent>(artifact.Id));
            } catch (SerializationException) {
                // A chunk written by a different type: the reference names a texture or a mesh rather
                // than a material. That is a scene pointing at the wrong asset, which is an entity
                // drawn neutrally rather than an editor that will not open.
                return null;
            }
        }

        return null;
    }
}
