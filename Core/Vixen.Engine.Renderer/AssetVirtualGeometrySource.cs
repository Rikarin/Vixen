// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Engine.Renderer;

/// <summary>
///     The cluster hierarchy a scene's mesh references name, loaded and registered.
/// </summary>
/// <remarks>
///     <para>
///         <b>The extraction counterpart <c>VirtualGeometrySystem</c> was missing.</b> Every phase of the
///         virtualized path was finished — the import cuts a mesh into clusters, the traversal walks the
///         DAG, the raster fills a visibility buffer and the resolve shades it — and nothing looked at
///         whether a model had a hierarchy, so the whole system was reachable from code and not from a
///         scene.
///     </para>
///     <para>
///         <b>It asks the mesh where its clusters are.</b> A sub-asset's id is derived from the
///         importer's name, the kind and the mesh's name, and a frame holding a mesh reference knows none
///         of them — so <see cref="MeshData.Clusters" /> is the link and this follows it. That also makes
///         "does this mesh have clusters" a property of the content rather than a guess, which is what
///         lets a project turn the virtualized path off per mesh at import.
///     </para>
///     <para>
///         ⚠ <b>The <see cref="MeshData" /> is loaded either way</b>, and for a clustered mesh that means
///         its fallback geometry is in memory and never uploaded. That is not waste worth removing: the
///         fallback is what WebGL2 draws, what the physics cook reads and what a ray query hits, so a
///         project that has it resident is a project that can answer those questions.
///     </para>
/// </remarks>
public sealed class AssetVirtualGeometrySource : IVirtualGeometrySource, IDisposable {
    readonly AssetManager assets;
    readonly VirtualGeometrySystem geometry;
    readonly Dictionary<AssetReference, Entry> entries = [];

    int nextSource;
    bool disposed;

    /// <summary>Builds a source over a content manager and the stack that draws what it loads.</summary>
    /// <param name="assets">Where the artefacts come from.</param>
    /// <param name="geometry">The stack the meshes are registered with.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AssetVirtualGeometrySource(AssetManager assets, VirtualGeometrySystem geometry) {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(geometry);

        this.assets = assets;
        this.geometry = geometry;
    }

    /// <summary>How many distinct meshes have been asked about.</summary>
    public int Requested => entries.Count;

    /// <summary>How many turned out to have a hierarchy and were registered.</summary>
    public int Registered {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.State == ClusterState.Ready) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public ClusterState TryGet(AssetReference reference, out int mesh, out BoundingSphere bounds) {
        ObjectDisposedException.ThrowIf(disposed, this);

        mesh = -1;
        bounds = default;

        if (!entries.TryGetValue(reference, out var entry)) {
            entries[reference] = entry = new();

            try {
                entry.Mesh = assets.LoadAsync<MeshData>(reference);
            } catch (ReferenceNotFoundException) {
                // Not this class's problem to report: AssetMeshSource is asked next and records the same
                // reference as failed. Saying "no clusters" sends it there.
                entry.State = ClusterState.None;
            }
        }

        if (entry.State != ClusterState.Waiting) {
            mesh = entry.Index;
            bounds = entry.Bounds;

            return entry.State;
        }

        if (entry.Mesh is not { Status: AssetStatus.Loaded } handle) {
            // A failed mesh is "no clusters" for the same reason a missing one is — one reporter per
            // reference, and it is the ordinary source.
            return entry.Mesh is { Status: AssetStatus.Failed }
                ? entry.State = ClusterState.None
                : ClusterState.Waiting;
        }

        if (!handle.Result.IsClustered) {
            return entry.State = ClusterState.None;
        }

        return Register(entry, handle.Result, out mesh, out bounds);
    }

    /// <summary>Reads the two chunks and registers the mesh, once its records have loaded.</summary>
    /// <remarks>
    ///     ⚠ <b>The blob is opened as a stream and not loaded as an object.</b>
    ///     <see cref="StreamMeshletPageSource" /> seeks into it per page, which is what makes residency
    ///     demand-driven; loading it would put every page of every mesh in memory and leave the streaming
    ///     machinery reading out of memory somebody had already paid for.
    /// </remarks>
    ClusterState Register(Entry entry, MeshData mesh, out int index, out BoundingSphere bounds) {
        index = -1;
        bounds = default;

        entry.Records ??= Load(entry, mesh.Clusters);

        if (entry.State != ClusterState.Waiting) {
            return entry.State;
        }

        if (entry.Records is not { Status: AssetStatus.Loaded } records) {
            return entry.Records is { Status: AssetStatus.Failed }
                ? entry.State = ClusterState.None
                : ClusterState.Waiting;
        }

        Stream blob;

        try {
            blob = assets.Open(assets.AddressOf(mesh.ClusterPages));
        } catch (Exception failure) when (failure is ReferenceNotFoundException or AddressNotFoundException) {
            // A mesh that says it has pages and has none is content built by a version that wrote the
            // records and not the blob. Nothing can draw it virtualized, and its fallback can.
            return entry.State = ClusterState.None;
        }

        entry.Index = index = geometry.Content(nextSource++, records.Result, blob);
        entry.Bounds = bounds = Bound(entry.Index);

        return entry.State = ClusterState.Ready;
    }

    AssetHandle<VirtualGeometryAsset>? Load(Entry entry, AssetReference records) {
        try {
            return assets.LoadAsync<VirtualGeometryAsset>(records);
        } catch (ReferenceNotFoundException) {
            entry.State = ClusterState.None;
            return null;
        }
    }

    /// <summary>The bind-pose bound the traversal registered, which is what an instance is scaled from.</summary>
    /// <remarks>
    ///     Read back from the traversal rather than recomputed, because they have to be the same number:
    ///     the culling loop tests an instance's world-space sphere against a radius the shader derives
    ///     from this one, and two answers would cull geometry that is on screen.
    /// </remarks>
    BoundingSphere Bound(int index) {
        var registered = geometry.Visibility.MeshAt(index);
        return new(registered.Center, registered.Radius);
    }

    /// <summary>
    ///     Releases a mesh: its pages go back to the pool, its blob is closed and its claims are dropped.
    /// </summary>
    /// <param name="reference">The mesh reference it was asked about under.</param>
    /// <returns>Whether anything was registered to release.</returns>
    /// <remarks>
    ///     ⚠ <b>Nothing calls this per entity, and it must not.</b> A reference is shared — a forest of
    ///     one tree asset is one registration — so releasing it because one entity went away would take
    ///     the geometry out from under every other entity drawing the same mesh. What may call it is
    ///     whatever knows the content itself has gone, which today is <see cref="Dispose" />.
    /// </remarks>
    public bool Release(AssetReference reference) {
        ObjectDisposedException.ThrowIf(disposed, this);

        return entries.Remove(reference, out var entry) && Retire(entry);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Every registration is released, not merely forgotten.</b> Clearing the dictionary leaves
    ///     the pool holding a pinned root page per mesh and <c>StreamMeshletPageSource</c> holding an
    ///     open stream per mesh, both for content this object was the only reference to — so a host that
    ///     rebuilt its renderer would find the pool smaller every time and nothing would say why.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var entry in entries.Values) {
            Retire(entry);
        }

        entries.Clear();
    }

    /// <summary>Gives back everything one entry claimed, in the order it claimed it.</summary>
    bool Retire(Entry entry) {
        var released = entry.State == ClusterState.Ready && entry.Index >= 0 && geometry.Release(entry.Index);

        entry.Records?.Release();
        entry.Mesh?.Release();

        entry.Records = null;
        entry.Mesh = null;
        entry.State = ClusterState.None;
        entry.Index = -1;

        return released;
    }

    /// <summary>One mesh, somewhere between named and traversed.</summary>
    sealed class Entry {
        public AssetHandle<MeshData>? Mesh;
        public AssetHandle<VirtualGeometryAsset>? Records;
        public ClusterState State = ClusterState.Waiting;
        public int Index = -1;
        public BoundingSphere Bounds;
    }
}
