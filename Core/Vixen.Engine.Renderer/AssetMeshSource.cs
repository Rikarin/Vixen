// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Engine.Renderer;

/// <summary>
///     The geometry a scene's mesh references name, loaded through the content manager.
/// </summary>
/// <remarks>
///     <para>
///         <b>The join that made <c>MeshRenderable</c> mean something.</b> Everything either side of it
///         was finished — the catalog resolves a reference, the manager loads and shares the bytes, and
///         <see cref="MeshExtractionSystem" /> puts a render object in the store — and between them
///         stood one function returning an empty mesh, so an entity carrying a mesh reference was
///         authored, saved, compiled, loaded and invisible.
///     </para>
///     <para>
///         <b>Nothing here waits.</b> A load is started on the first ask and the answer is "not yet"
///         until it lands; the extraction system leaves the entity alone and asks again next frame. The
///         alternative is a synchronous load inside extraction, which stalls the frame a level starts
///         on every mesh in it — the one frame in the run where the I/O is worst placed.
///     </para>
///     <para>
///         <b>A handle per reference, kept.</b> An <see cref="AssetHandle{T}" /> is the claim that keeps
///         the mesh and everything it depends on loaded, so dropping it would unload the geometry a
///         residency slice was built from. Keeping the failed ones matters just as much: without that a
///         reference nothing can resolve is retried every frame for the life of the level.
///     </para>
/// </remarks>
public sealed class AssetMeshSource : IMeshSource {
    readonly AssetManager assets;
    readonly Dictionary<AssetReference, AssetHandle<MeshData>> handles = [];

    /// <summary>Builds a source over a content manager.</summary>
    /// <param name="assets">Where the meshes come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public AssetMeshSource(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);
        this.assets = assets;
    }

    /// <summary>How many distinct meshes have been asked for.</summary>
    public int Requested => handles.Count;

    /// <summary>How many of them have arrived.</summary>
    public int Loaded {
        get {
            var count = 0;

            foreach (var handle in handles.Values) {
                if (handle is { Status: AssetStatus.Loaded }) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many will not arrive.</summary>
    /// <remarks>
    ///     A reference that resolves to no address, or an address whose bytes will not deserialise. Both
    ///     are content problems rather than frame problems, and both are invisible without a number:
    ///     what they look like is a mesh that is simply not in the level.
    /// </remarks>
    public int Failed {
        get {
            var count = 0;

            foreach (var handle in handles.Values) {
                // A null handle is a reference the catalog had no address for, which failed before a
                // load could be started and is the same kind of content problem.
                if (handle is null || handle.Status == AssetStatus.Failed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public bool TryGet(AssetReference reference, out MeshData mesh) {
        mesh = null!;

        if (!handles.TryGetValue(reference, out var handle)) {
            try {
                handles[reference] = handle = assets.LoadAsync<MeshData>(reference);
            } catch (ReferenceNotFoundException) {
                // Recorded as a null handle rather than rethrown: a reference the catalog does not know
                // is one entity in a level, and a frame that threw would take the level with it. The
                // null is also what stops it being retried every frame for the rest of the run.
                handles[reference] = handle = null!;
                return false;
            }
        }

        if (handle is null || handle.Status != AssetStatus.Loaded) {
            return false;
        }

        mesh = handle.Result;

        return true;
    }
}
