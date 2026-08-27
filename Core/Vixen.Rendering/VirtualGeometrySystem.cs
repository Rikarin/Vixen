// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     Every device-side piece of virtualized geometry, joined up.
/// </summary>
/// <remarks>
///     <para>
///         <b>The assembly nobody had written.</b> Six objects have to point at each other before a
///         cluster reaches a pixel — the traversal, the page pool and its residency, the raster, the
///         binning and the resolve — and every one of them was independently constructible and
///         independently tested. What was missing is the only part with no natural owner: which of them
///         holds which, and in what order they are told about a frame. A host doing it by hand gets it
///         right the first time and wrong on the second project.
///     </para>
///     <para>
///         <b>It owns the device objects and not the scene.</b> What goes in is a device, an effect
///         system and a budget; what comes out is a feature to register with a
///         <see cref="RenderSystem" /> and six references a <see cref="CompositorBuilder" /> wants. It
///         has no opinion about which objects draw, which is
///         <see cref="VirtualGeometryRenderFeature.Draws" />' business, or where in the frame the passes
///         go, which is a document's.
///     </para>
///     <para>
///         <b>One page pool for the scene, and that is the whole of the streaming budget.</b> Slots
///         times page size is the resident geometry a project has decided to pay for, and every mesh
///         registered through <see cref="Content(int, VirtualGeometryAsset, System.IO.Stream, MorphIndex)" /> shares it — which is what makes a hundred
///         virtualized meshes cost one budget rather than a hundred.
///     </para>
/// </remarks>
public sealed class VirtualGeometrySystem : IDisposable {
    bool disposed;

    /// <summary>Builds the stack on a device.</summary>
    /// <param name="device">The device every buffer lives on.</param>
    /// <param name="slots">How many pages may be resident at once.</param>
    /// <param name="pageSize">How many bytes one page occupies. Must match what the build produced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <remarks>
    ///     The page size is the caller's because it is the <em>build's</em> — a pool whose slots are a
    ///     different size than the pages a mesh was cut into puts one page's bytes across two slots. The
    ///     default is <c>MeshletPageSettings</c>'s, so a project that changed neither has nothing to say.
    /// </remarks>
    public VirtualGeometrySystem(IGraphicsDevice device, int slots = 512, int pageSize = 128 * 1024) {
        ArgumentNullException.ThrowIfNull(device);

        Pages = new(device, Source, slots, pageSize);
        Residency = new(Pages, (long)slots * pageSize);

        // The traversal reads the slot table the residency keeps, and asks it for the pages a cut wanted
        // and did not have. It is set rather than owned there because improvement 6 has one residency
        // service holding geometry, texture and shadow pages — so the wiring is here and the ownership
        // is nobody's in particular.
        Visibility = new(device) { Residency = Residency };
        Raster = new(device) { Visibility = Visibility, Pages = Pages };
        SoftwareRaster = new(device) { Visibility = Visibility, Pages = Pages };
        Tiles = new(device) { Visibility = Visibility };

        Resolve = new(device) {
            Visibility = Visibility,
            Tiles = Tiles,
            Pages = Pages
        };

        Feature = new() { Visibility = Visibility, Pages = Pages };
    }

    /// <summary>Where every mesh's page blob lives, and what the pool reads out of.</summary>
    /// <remarks>
    ///     Exposed because a project that unloads a level wants its blobs closed —
    ///     <see cref="StreamMeshletPageSource.Remove" /> — and because a test asking whether a mesh's
    ///     bytes are reachable is asking this. Adding to it directly is possible and is not the way in:
    ///     <see cref="Content(int, VirtualGeometryAsset, System.IO.Stream, MorphIndex)" /> adds the blob and registers the mesh under one id, so the two cannot
    ///     be given different ones.
    /// </remarks>
    public StreamMeshletPageSource Source { get; } = new();

    /// <summary>The pool every resident page lives in.</summary>
    public MeshletPagePool Pages { get; }

    /// <summary>What decides which pages are in it.</summary>
    public PageResidency Residency { get; }

    /// <summary>The cluster traversal.</summary>
    public GpuClusterVisibility Visibility { get; }

    /// <summary>The draw that fills the visibility buffer.</summary>
    public GpuClusterRaster Raster { get; }

    /// <summary>The compute raster phase 6 routes the sub-pixel clusters to.</summary>
    /// <remarks>
    ///     Built unconditionally and dormant unless a host sets
    ///     <see cref="Features.VirtualGeometryRenderFeature.SoftwareThreshold" /> — and dormant whatever
    ///     it is set to on a device without <see cref="GraphicsDeviceFeatures.HasInt64Atomics" />. See
    ///     <see cref="GpuClusterSoftwareRaster.Supported" />, which is what says which of the two it is.
    /// </remarks>
    public GpuClusterSoftwareRaster SoftwareRaster { get; }

    /// <summary>The binning that sorts its tiles by material.</summary>
    public GpuVisibilityTiles Tiles { get; }

    /// <summary>The per-material shading that consumes those bins.</summary>
    public GpuClusterResolve Resolve { get; }

    /// <summary>The feature a scene's objects draw through.</summary>
    public VirtualGeometryRenderFeature Feature { get; }

    /// <summary>Which material each bin holds, as the resolve dispatches them.</summary>
    /// <remarks>
    ///     Forwarded rather than mirrored, because a second list is a second thing to keep in step. The
    ///     bins are numbered by <c>Meshlet.MaterialIndex</c> — a model's own submesh slots — and a
    ///     project binds an asset to each.
    /// </remarks>
    public IReadOnlyList<ResolveMaterial> Materials {
        get => Resolve.Materials;
        set => Resolve.Materials = value;
    }

    /// <summary>How many meshes have been loaded into it.</summary>
    public int MeshCount => Visibility.MeshCount;

    /// <summary>
    ///     Where every variant is compiled, which every one of the four passes needs.
    /// </summary>
    /// <remarks>
    ///     Set once here rather than four times by a caller. Three of the four resolve a variant and do
    ///     nothing quietly without one, which is a frame that draws no virtualized geometry and reports
    ///     no reason — the failure this property exists to make impossible to have partially.
    /// </remarks>
    public EffectSystem? Effects {
        get => Visibility.Effects;
        set {
            Visibility.Effects = value;
            Raster.Effects = value;
            SoftwareRaster.Effects = value;
            Tiles.Effects = value;
            Resolve.Effects = value;
        }
    }

    /// <summary>Where the compute pipelines come from, on the same terms.</summary>
    public ComputePipelineCache? Pipelines {
        get => Visibility.Pipelines;
        set {
            Visibility.Pipelines = value;
            SoftwareRaster.Pipelines = value;
            Tiles.Pipelines = value;
            Resolve.Pipelines = value;
        }
    }

    /// <summary>Where the raster's shader modules come from.</summary>
    /// <remarks>
    ///     The raster builds a graphics pipeline rather than a compute one, so it needs the describer
    ///     that turns an effect's stages into modules — the same one <c>MeshRenderFeature</c> takes.
    /// </remarks>
    public EffectPipelineDescriber? Modules {
        get => Raster.Modules;
        set => Raster.Modules = value;
    }

    /// <summary>The frame's per-frame constants, which the resolve's lighting reads.</summary>
    public SceneConstants? Scene {
        get => Resolve.Scene;
        set => Resolve.Scene = value;
    }

    /// <summary>The frame's per-view constants.</summary>
    public ViewConstants? ViewConstants {
        get => Resolve.ViewConstants;
        set => Resolve.ViewConstants = value;
    }

    /// <summary>
    ///     Loads a mesh out of the artefacts a build wrote, and makes it drawable.
    /// </summary>
    /// <param name="id">The id its pages carry. Unique across the meshes in this system.</param>
    /// <param name="hierarchy">The <c>Meshlets</c> artefact's bytes.</param>
    /// <param name="pages">The <c>MeshletPages</c> artefact's bytes.</param>
    /// <param name="data">The <c>MeshletPageData</c> blob, seekable. Owned from here.</param>
    /// <param name="morphIndex">
    ///     The mesh's blend shapes re-indexed by vertex, or null for a mesh with none.
    /// </param>
    /// <returns>The index to put in <see cref="VirtualGeometryDraw.Mesh" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is null.</exception>
    public int Content(
        int id,
        ReadOnlySpan<byte> hierarchy,
        ReadOnlySpan<byte> pages,
        Stream data,
        MorphIndex? morphIndex = null
    ) {
        ObjectDisposedException.ThrowIf(disposed, this);

        return VirtualGeometryContent.Load(Feature, Source, id, hierarchy, pages, data, morphIndex);
    }

    /// <summary>Loads a mesh out of the one chunk a build writes the records as.</summary>
    /// <param name="id">The id its pages carry. Unique across the meshes in this system.</param>
    /// <param name="asset">The records, as <c>MeshData.Clusters</c> resolves to.</param>
    /// <param name="data">The page blob, seekable. Owned from here.</param>
    /// <param name="morphIndex">
    ///     The mesh's blend shapes re-indexed by vertex, or null for a mesh with none.
    /// </param>
    /// <returns>The index to put in <see cref="VirtualGeometryDraw.Mesh" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is null.</exception>
    public int Content(int id, VirtualGeometryAsset asset, Stream data, MorphIndex? morphIndex = null) {
        ObjectDisposedException.ThrowIf(disposed, this);

        return VirtualGeometryContent.Load(Feature, Source, id, asset, data, morphIndex);
    }

    /// <summary>
    ///     Releases a mesh: its pages go back to the pool and its blob is closed.
    /// </summary>
    /// <param name="mesh">The index <see cref="Content(int, VirtualGeometryAsset, System.IO.Stream, MorphIndex)" /> returned.</param>
    /// <returns>Whether there was a live registration to release.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such registration.</exception>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="Content(int, VirtualGeometryAsset, System.IO.Stream, MorphIndex)" />'s counterpart, and
    ///         it undoes both halves for the reason that one does both.</b> A registration pins a root
    ///         page and opens a blob; releasing only the pin leaves a file handle, and releasing only the
    ///         blob leaves a slot of the pool pinned to content nothing draws — for ever, because nothing
    ///         re-pins and nothing unpins. A project that loads and unloads levels loses a slot per mesh
    ///         per level until <see cref="PageResidency.Pin" /> refuses.
    ///     </para>
    ///     <para>
    ///         ⚠ The index stays valid and keeps its number: an object still holding it in
    ///         <c>VirtualGeometryDraw.Mesh</c> draws nothing rather than drawing whatever was registered
    ///         next. See <c>GpuClusterVisibility.Unregister</c> for why the record is retired in place.
    ///     </para>
    /// </remarks>
    public bool Release(int mesh) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var id = Feature.Unregister(mesh);

        if (id < 0) {
            return false;
        }

        Source.Remove(id);

        return true;
    }

    /// <summary>Registers the feature with a render system.</summary>
    /// <param name="system">The system.</param>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> is null.</exception>
    public void Register(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);
        ObjectDisposedException.ThrowIf(disposed, this);

        system.AddFeature(Feature);
    }

    /// <summary>Hands a compositor builder everything its two nodes need.</summary>
    /// <param name="builder">The builder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <remarks>
    ///     Six assignments, and a node built without any one of them is a node that silently does
    ///     nothing — which is right for a project with no virtualized geometry and is a bug in a project
    ///     that has some. Doing them here is what makes "the document names the passes" true rather than
    ///     "the document names the passes and the host remembers six properties".
    /// </remarks>
    public void Supply(CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);
        ObjectDisposedException.ThrowIf(disposed, this);

        builder.Clusters = Visibility;
        builder.Pages = Pages;
        builder.Raster = Raster;
        builder.SoftwareRaster = SoftwareRaster;
        builder.Tiles = Tiles;
        builder.Resolve = Resolve;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Resolve.Dispose();
        Tiles.Dispose();
        SoftwareRaster.Dispose();
        Raster.Dispose();
        Visibility.Dispose();
        Pages.Dispose();
        Source.Dispose();
    }
}
