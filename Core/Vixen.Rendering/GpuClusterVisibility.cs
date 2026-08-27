// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.VirtualGeometry;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>Where one registered mesh's records ended up in the scene-wide buffers.</summary>
/// <param name="Source">The id its pages carry in a <see cref="PageKey" />.</param>
/// <param name="FirstCluster">Where its clusters start in the merged cluster buffer.</param>
/// <param name="ClusterCount">How many it has.</param>
/// <param name="FirstRoot">Where its roots start in the merged root buffer.</param>
/// <param name="RootCount">How many it has.</param>
/// <param name="FirstPage">The global page index its page zero became.</param>
/// <param name="PageCount">How many pages it has.</param>
/// <param name="Center">The centre of its bind-pose bound, in object space.</param>
/// <param name="Radius">
///     The radius of that bound — what a pose is measured against to say how far the mesh can move.
/// </param>
/// <remarks>
///     What an instance needs to point at a DAG, and nothing about where the instance is — a mesh is
///     registered once and drawn many times, which is the whole reason the records and the instances
///     are two buffers.
/// </remarks>
public readonly record struct ClusterMesh(
    int Source,
    int FirstCluster,
    int ClusterCount,
    int FirstRoot,
    int RootCount,
    int FirstPage,
    int PageCount,
    Vector3 Center = default,
    float Radius = 0f
);

/// <summary>Where one cluster's bytes are inside its page, as <c>RasterCluster</c> declares it.</summary>
/// <remarks>
///     <see cref="CullCluster" />'s other half, and split from it for the reason the whole phase is
///     split that way: a traversal reads bounds and errors for every cluster it <em>tests</em>, and a
///     raster reads this for only the ones it <em>draws</em>. One buffer per question keeps the
///     traversal's reads dense, which is what a walk over a hundred thousand clusters is bound by.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct RasterCluster {
    /// <summary>The cluster's origin on the mesh's quantization grid, in whole grid steps.</summary>
    public Int3 Origin;

    /// <summary>Which page holds its bytes, in the pool's global page numbering.</summary>
    public uint Page;

    /// <summary>Where its vertices start, in bytes from the page's own start.</summary>
    public uint VertexOffset;

    /// <summary>Where its triangle corners start, in bytes from the page's own start.</summary>
    public uint TriangleOffset;

    /// <summary>How many triangles it has.</summary>
    public uint TriangleCount;

    /// <summary>How many bytes one of its vertices occupies.</summary>
    public uint VertexStride;
}

/// <summary>One mesh's quantization grid, as <c>RasterMesh</c> declares it.</summary>
/// <remarks>
///     Per mesh and not per cluster, which is the property that keeps a cut crack-free: a vertex on a
///     locked boundary is referenced by a cluster on each side, and only a grid they share decodes it to
///     the same bits from both. See <see cref="MeshletPageSet.QuantizationStep" />, which is where that
///     argument is made at length.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct RasterMesh {
    /// <summary>What <see cref="InfluenceOffset" /> holds for a mesh with no skeleton.</summary>
    public const uint NoInfluences = 0xFFFFFFFFu;

    /// <summary>What <see cref="MorphRunBase" /> holds for a mesh with no blend shapes.</summary>
    /// <remarks>
    ///     ⚠ <b>The record's three morph words are the twelve bytes of padding
    ///     <c>RasterMesh</c> was carrying</b>, so the size and the stride are unchanged and every
    ///     mesh registered before blend shapes existed reads <see cref="NoMorphs" /> out of the zeros
    ///     it was padded with — which is not true, and is why the writer sets it explicitly rather
    ///     than relying on a default.
    /// </remarks>
    public const uint NoMorphs = 0xFFFFFFFFu;

    /// <summary>The corner of the grid.</summary>
    public Vector3 QuantizationOrigin;

    /// <summary>How far apart its points are, in object space.</summary>
    public float QuantizationStep;

    /// <summary>
    ///     Where a vertex's bone influences sit inside it, in bytes, or <see cref="NoInfluences" />.
    /// </summary>
    /// <remarks>
    ///     <see cref="MeshletPageSet.InfluenceOffset" /> as the device reads it, and per mesh for the
    ///     reason given there: a static mesh's page vertex is sixteen bytes and must stay sixteen. That
    ///     makes "is this vertex skinned" a value rather than a permutation — one draw covers every mesh
    ///     in the scene, so there is no per-draw choice of variant to make.
    /// </remarks>
    public uint InfluenceOffset;

    /// <summary>
    ///     Where this mesh's per-vertex run table starts in the morph buffer, in words, or
    ///     <see cref="NoMorphs" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The word the padding was, and it pays for itself the way
    ///         <see cref="CullInstance.Mesh" /> did.</b> A page holds a vertex's rest position and the
    ///         weights that move it are the <em>instance's</em>, so a paged mesh is morphed where it
    ///         is decoded rather than by a pre-pass — see <see cref="MorphIndex" /> for why a scatter
    ///         has nowhere to write.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="NoMorphs" /> and not zero, because zero is a real base</b> — the first
    ///         morphed mesh registered gets it. This is the field whose wrong default is a rock with a
    ///         face's deltas applied to it.
    ///     </para>
    /// </remarks>
    public uint MorphRunBase;

    /// <summary>Where its entries start in the morph buffer, in words.</summary>
    /// <remarks>
    ///     Four words an entry: the shape's slot, then the six quantised components
    ///     <c>MorphKernel.Pack</c> writes. Meaningless when <see cref="MorphRunBase" /> is
    ///     <see cref="NoMorphs" />.
    /// </remarks>
    public uint MorphEntryBase;

    /// <summary>
    ///     Where its per-cluster table of source-vertex runs starts in the morph buffer, in words.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Indexed by the cluster's index <em>within this mesh</em> — which is what the raster
    ///         already has, because <c>geometry</c> is reached as
    ///         <c>instance.FirstCluster + clusterIndex</c>. A table over every cluster in the scene
    ///         would need rebuilding whenever any mesh registered; this one is written once with its
    ///         mesh and never moves.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A page vertex does not know which mesh vertex it is</b>, which is the whole reason
    ///         this indirection exists. A cluster's vertex list belongs to the DAG —
    ///         <c>MeshletMesh.Vertices</c> — and the page carries only the position and attributes it
    ///         copied out. A vertex on a locked boundary appears in a cluster on each side and at
    ///         several levels, and every copy has to pick up the same delta or the cut cracks.
    ///     </para>
    /// </remarks>
    public uint MorphClusterBase;
}


/// <summary>
///     The device side of the cluster traversal: the DAG in buffers, the dispatch, and the page
///     requests coming back.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="GpuVisibilityGroup" />'s sibling, and the same three-part arrangement.</b>
///         That one owns the object cull's buffers and dispatch, <see cref="Features.MeshRenderFeature" />
///         extracts what it culls, and <see cref="Compositor.GpuCullingRenderer" /> puts the dispatch in
///         the frame. This one owns the traversal's buffers and dispatch,
///         <see cref="Features.VirtualGeometryRenderFeature" /> extracts what it traverses, and
///         <see cref="Compositor.ClusterCullingRenderer" /> puts it in the frame. Deliberately the same
///         shape, because it is the same decision at a different depth of the same hierarchy — which is
///         improvement 3 of <c>docs/plan/22-virtualized-geometry.md</c>, and is why the shader is one shader.
///     </para>
///     <para>
///         <b>Two kinds of buffer, and the difference is what changes.</b> The cluster records, the
///         child runs and the roots are the <em>meshes</em> — they change when a mesh is registered or
///         forgotten, which is a load, not a frame. The instances are the <em>scene</em> — they change
///         when something moves. So the first three are uploaded on a dirty flag and the last through a
///         <see cref="PersistentUploadBuffer{T}" />, which is phase 0's answer to the same question
///         about objects.
///     </para>
///     <para>
///         <b>The answer never comes back to the host, and that is the point.</b> What the dispatch
///         writes is a visible-cluster list an indirect draw reads and a request list the residency
///         service reads. Only the second is read back, it is small, and it is read a frame late on
///         purpose — see <see cref="ServiceRequests" />.
///     </para>
/// </remarks>
public sealed class GpuClusterVisibility : IDisposable {
    /// <summary>How many bytes of visible-cluster list are allocated per instance per view.</summary>
    /// <remarks>
    ///     <para>
    ///         A ceiling rather than a measurement, because the list is written by atomics on the device
    ///         and its length is not known until after the dispatch that fills it. Sized from the cut a
    ///         view could ask for: a cut is at most as wide as the DAG's leaf level, and the traversal's
    ///         queue caps a single group's front at <see cref="GpuClusterCulling.QueueCapacity" /> — so
    ///         this many clusters per instance per view is enough for any cut a bounded queue can
    ///         produce.
    ///     </para>
    ///     <para>
    ///         Overflow is not a hole. <c>Accept</c> increments the counter and then checks the bound, so
    ///         a list that fills stops appending and the count reports what was wanted — which is what
    ///         <see cref="VisibleOverflowed" /> is derived from.
    ///     </para>
    /// </remarks>
    public const int VisiblePerInstance = GpuClusterCulling.QueueCapacity;

    /// <summary>How many counter words the visible list carries before its first entry.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>Cull.VisibleBase</c>, and three rather than one since phase 6: the hardware raster's
    ///         count, the software raster's, and a shared reservation. The two rasters fill the list
    ///         from opposite ends — a single instanced draw covers a <em>prefix</em>, so the clusters it
    ///         does not draw have to be somewhere that is not in the middle of it — and the reservation
    ///         is what makes the two ends provably unable to meet.
    ///     </para>
    ///     <para>
    ///         A pixel's identity names an entry minus this, whichever raster wrote it, so the resolve
    ///         reads one buffer and asks one question. See <see cref="GpuClusterRaster.Pack" />.
    ///     </para>
    /// </remarks>
    public const int VisibleBase = 3;

    /// <summary>What the slot table holds for a page whose bytes are not in the pool.</summary>
    /// <remarks>
    ///     <c>Cull.PageAbsent</c>, and all ones rather than zero because zero is a real slot. A table
    ///     cleared to zero would say every page of every mesh was in slot zero, which draws the whole
    ///     scene out of one page's bytes at all the right places.
    /// </remarks>
    public const uint PageAbsent = 0xFFFFFFFFu;

    /// <summary>How many page requests one frame's dispatch may record.</summary>
    /// <remarks>
    ///     A frame cannot usefully act on more than a few dozen — <see cref="PageResidency.Service" />
    ///     takes a handful and drops the rest, because a request is a statement about a camera and the
    ///     next frame will ask again. This is generous by two orders of magnitude and still four
    ///     kilobytes.
    /// </remarks>
    public const int RequestCapacity = 1024;

    readonly IGraphicsDevice device;
    readonly List<ClusterMesh> meshes = [];
    readonly List<CullCluster> clusters = [];
    readonly List<uint> children = [];
    readonly List<uint> roots = [];
    readonly List<RasterCluster> geometry = [];
    readonly List<RasterMesh> grids = [];
    readonly List<uint> materials = [];

    // Three tables in one list, per morphed mesh: where each of its clusters' source-vertex run starts,
    // those runs themselves, its per-vertex prefix and its entries. One buffer because one invocation
    // reads all four in sequence, and a binding costs more than an offset. A mesh with no shapes
    // contributes nothing at all — which is every rock in the project.
    readonly List<uint> morphs = [];

    // The instances, incrementally: a scene of a hundred thousand virtualized objects of which one
    // moved is one record uploaded, for PersistentUploadBuffer's reason and measured the same way.
    readonly PersistentUploadBuffer<CullInstance> instances = new("ClusterCulling.Instances");
    readonly UploadBuffer<CullView> views = new("ClusterCulling.Views");

    // Every skinned instance's matrices, back to back, rebuilt whole each frame — a palette is a pose
    // and a pose is different every frame, so there is nothing here for the incremental compare that
    // the instance records earn their keep with.
    readonly UploadBuffer<Matrix4x4> bones = new("ClusterCulling.Bones");

    // Per instance per shape, and per frame, exactly as the palette is — see BeginMorphs.
    readonly UploadBuffer<Vector2> morphWeights = new("ClusterCulling.MorphWeights");
    readonly UploadBuffer<uint> residencyBits = new("ClusterCulling.Residency");
    readonly DescriptorWrite[] writes = new DescriptorWrite[GpuCulling.SetBindings.Length + 1];

    // One set per frame in flight. Nothing here submits or waits, so the previous frame's dispatch may
    // still be reading the set this frame is rewriting — DescriptorAllocator's race exactly.
    DescriptorSetHandle[] descriptors = [];
    int ring;

    readonly OccluderProjections projections = new();

    CullView[] packedViews = [];
    uint[] residency = [];
    uint[] requestReadback = [];

    BufferHandle clusterBuffer;
    BufferHandle childBuffer;
    BufferHandle rootBuffer;
    BufferHandle geometryBuffer;
    BufferHandle gridBuffer;
    BufferHandle materialBuffer;
    BufferHandle morphBuffer;
    BufferHandle visibleBuffer;
    BufferHandle requestBuffer;
    BufferHandle requestReadbackBuffer;
    BufferHandle zeros;
    BufferHandle staging;

    readonly List<(long From, BufferHandle To, long Size)> pendingMeshCopies = [];
    long stagedMeshBytes;

    // One texel, for the frames with no pyramid and a variant that declares one anyway — the same
    // stand-in GpuVisibilityGroup keeps, and needed here for exactly the same reason: a permutation
    // folds away the sampling and leaves the declaration, so the set still has a binding to fill.
    TextureHandle placeholder;
    TextureViewHandle placeholderView;
    bool placeholderReady;

    long visibleCapacity;
    bool meshesDirty = true;
    bool morphsDirty = true;
    long morphCapacity;

    ResourceState visibleState = ResourceState.Undefined;
    ResourceState requestState = ResourceState.Undefined;

    Effect? allocatedFor;
    PendingTraversal? pending;
    bool disposed;

    const DescriptorSetSlot Slot = (DescriptorSetSlot)CullingKeys.ObjectsSet;

    /// <summary>Creates a traversal that runs on a device.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public GpuClusterVisibility(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        instances.Device = device;
        views.Device = device;
        residencyBits.Device = device;
        bones.Device = device;
        morphWeights.Device = device;
    }

    /// <summary>Where the traversal variant is resolved from. Null does nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null does nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Last frame's depth, min-reduced, or null to test the frustum and the cone alone.</summary>
    /// <remarks>
    ///     The same pyramid the object cull tests against, and the same question asked of it — see
    ///     <c>Culling.rvn</c>'s <c>Occluded</c>, which takes a sphere precisely so both callers can hand
    ///     it one. What differs is which sphere: an object's bound there, a cluster's here.
    /// </remarks>
    public HiZPyramid? Occluders { get; set; }

    /// <summary>Which pages are in the pool. Null traverses as though none are.</summary>
    /// <remarks>
    ///     Set rather than owned, because it is <see cref="Features.VirtualGeometryRenderFeature" />'s —
    ///     and because improvement 6 of <c>docs/plan/22-virtualized-geometry.md</c> means the same service will
    ///     one day hold texture and shadow pages, which have nothing to do with this dispatch.
    /// </remarks>
    public PageResidency? Residency { get; set; }

    /// <summary>How many meshes are registered.</summary>
    public int MeshCount => meshes.Count;

    /// <summary>How many clusters they have between them.</summary>
    public int ClusterCount => clusters.Count;

    /// <summary>How many pages, which is what the slot table has to cover.</summary>
    public int PageCount { get; private set; }

    /// <summary>How many bytes one page occupies in the pool.</summary>
    /// <remarks>
    ///     From the registered meshes rather than configured, because it is a property of the artefacts:
    ///     the raster multiplies a slot by it to find a page's bytes, and a number that disagreed with
    ///     the packer's would read a page at a fraction of an offset into the pool.
    /// </remarks>
    public int PageSize { get; private set; }

    /// <summary>The most triangles any registered cluster holds.</summary>
    /// <remarks>
    ///     What the single instanced draw's index count is three times. A cluster with fewer than this
    ///     has corners left over, and they collapse to a degenerate triangle — see
    ///     <c>ClusterRaster.rvn</c>'s vertex stage. Taken from the meshes rather than from
    ///     <c>MeshletBuildSettings.MaxTriangles</c>, because a build may have used a different setting
    ///     and the records are what actually shipped.
    /// </remarks>
    public int MaximumTriangles { get; private set; }

    /// <summary>How many instances the last <see cref="Prepare" /> uploaded.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>How many bytes of instance records it handed to the device.</summary>
    /// <remarks>
    ///     <see cref="GpuVisibilityGroup.ObjectBytesUploaded" />'s counterpart, and exposed for the same
    ///     reason: a frame that quietly went back to uploading every instance draws the same picture,
    ///     so the only way to keep the incremental upload honest is to be able to assert on it.
    /// </remarks>
    public long InstanceBytesUploaded => instances.LastUploadBytes;

    /// <summary>How many separate writes those bytes took.</summary>
    public int InstanceUploadRegions => instances.LastUploadRegions;

    /// <summary>Whether the last frame's traversal ran on the device.</summary>
    public bool TraversedOnDevice { get; private set; }

    /// <summary>How many clusters the last serviced frame's traversal wanted to draw.</summary>
    /// <remarks>
    ///     From the count word, read back beside the requests — so it is one frame stale, like them.
    ///     Worth having because it is the only number that says the traversal did anything at all: a
    ///     visible list of zero with no requests is a scene nothing can see, and one with requests is a
    ///     scene still streaming in.
    /// </remarks>
    public int VisibleClusters { get; private set; }

    /// <summary>How many of them the software raster was asked to draw.</summary>
    /// <remarks>
    ///     <para>
    ///         Phase 6's routing, reported rather than assumed. Zero on every frame whose views have no
    ///         <see cref="CullView.SoftwareThreshold" /> — which is every frame by default, and every
    ///         frame on a device without <see cref="GraphicsDeviceFeatures.HasInt64Atomics" /> whatever
    ///         a host asked for.
    ///     </para>
    ///     <para>
    ///         Worth a counter of its own because the two rasters draw the same picture when the routing
    ///         works: a threshold that quietly routed nothing and one that routed everything are
    ///         indistinguishable from the image, which is exactly why the phase's own test sweeps this.
    ///     </para>
    /// </remarks>
    public int SoftwareClusters { get; private set; }

    /// <summary>Whether that count exceeded what the list can hold.</summary>
    /// <remarks>
    ///     A frame that drew a prefix of its cut rather than all of it, which is a visible hole and not
    ///     a slow frame. Distinct from a residency rejection, which draws something coarser and whole.
    /// </remarks>
    public bool VisibleOverflowed { get; private set; }

    /// <summary>How many page requests came back from it.</summary>
    public int RequestedPages { get; private set; }

    /// <summary>The buffer the accepted clusters are appended to: element zero is the count.</summary>
    /// <remarks>
    ///     What phase 4 draws from. Element zero is the count precisely so that
    ///     <see cref="ICommandList.DrawIndexedIndirectCount" /> can read it without the host ever
    ///     learning it — which is the call the whole arrangement exists to make.
    /// </remarks>
    public BufferHandle Visible => visibleBuffer;

    /// <summary>The pages the traversal wanted and did not have. Element zero is the count.</summary>
    public BufferHandle Requests => requestBuffer;

    /// <summary>Where each cluster's bytes are, for the raster.</summary>
    public BufferHandle Geometry => geometryBuffer;

    /// <summary>One quantization grid per registered mesh.</summary>
    public BufferHandle Grids => gridBuffer;

    /// <summary>Every morphed mesh's shapes re-indexed by vertex, as the three rasters read them.</summary>
    /// <remarks>
    ///     Always valid once the buffers exist, even in a scene with no blend shapes in it: the three
    ///     shaders declare the binding whether or not any branch reaches it, and a descriptor write
    ///     naming nothing is something a recording backend accepts and a real device refuses. That is
    ///     the same argument <see cref="BeginBones" /> makes for its identity.
    /// </remarks>
    public BufferHandle Morphs => morphBuffer;

    /// <summary>How many words the morph tables occupy between them.</summary>
    public int MorphWords => morphs.Count;

    /// <summary>This frame's blend-shape weights, one run per morphed instance.</summary>
    public BufferHandle MorphWeights => morphWeights.Buffer;

    /// <summary>Where this frame's weights start in it, in entries — <see cref="BoneBase" />'s twin.</summary>
    public int MorphWeightBase => (int)(morphWeights.Offset / Unsafe.SizeOf<Vector2>());

    /// <summary>How many weight entries this frame has, the seed included.</summary>
    public int MorphWeightCount => morphWeights.Count;

    /// <summary>Which material each cluster uses, for the resolve's binning.</summary>
    public BufferHandle Materials => materialBuffer;

    /// <summary>The highest material index any registered cluster names, plus one.</summary>
    /// <remarks>
    ///     How many bins <c>VisibilityTiles.rvn</c> has to cover. Derived from the records rather than
    ///     configured, because a bin nothing writes to is a dispatch of no workgroups and a material past
    ///     the ceiling is a material nothing resolves — the second is worth reporting and the first costs
    ///     nothing.
    /// </remarks>
    public int MaterialCount { get; private set; }

    /// <summary>Where each page sits in the pool, or <see cref="PageAbsent" />.</summary>
    public BufferHandle Slots => residencyBits.Buffer;

    /// <summary>Where this frame's slot table starts in that buffer.</summary>
    public long SlotsOffset => residencyBits.Offset;

    /// <summary>Where it starts in entries rather than bytes.</summary>
    /// <inheritdoc cref="BoneBase" select="/remarks" />
    public int SlotBase => (int)(residencyBits.Offset / sizeof(uint));

    /// <summary>Where this frame's instance records start.</summary>
    public BufferHandle Instances => instances.Buffer;

    /// <inheritdoc cref="SlotsOffset" />
    public long InstancesOffset => instances.Offset;

    /// <summary>Where this frame's instance records start, in records rather than bytes.</summary>
    /// <inheritdoc cref="BoneBase" select="/remarks" />
    public int InstanceBase =>
        (int)(instances.Offset / System.Runtime.CompilerServices.Unsafe.SizeOf<CullInstance>());

    /// <summary>Every skinned instance's matrices, for the raster and the resolve to read.</summary>
    public BufferHandle Bones => bones.Buffer;

    /// <summary>
    ///     Where this frame's palettes start, in matrices rather than bytes.
    /// </summary>
    /// <remarks>
    ///     <b>An index and not a descriptor offset</b>, which is
    ///     <see cref="Features.TransformRenderFeature.BaseIndex" />'s arrangement and is here for a
    ///     harder reason than symmetry: the resolve fills its set through
    ///     <see cref="EffectSetWriter" />, which binds a storage buffer whole and has nowhere to put an
    ///     offset. A shader that adds a base to its index reads the right region on both paths; a
    ///     descriptor offset would be right in the raster and silently a frame stale in the resolve.
    /// </remarks>
    public int BoneBase => (int)(bones.Offset / System.Runtime.CompilerServices.Unsafe.SizeOf<Matrix4x4>());

    /// <summary>How many matrices this frame's palettes hold, including the leading identity.</summary>
    public int BoneCount => bones.Count;

    /// <summary>Registers a mesh's DAG, and pins its root page.</summary>
    /// <param name="mesh">The DAG, from <c>MeshletBuilder</c>.</param>
    /// <param name="pages">Where its geometry is, from <c>MeshletPageBuilder</c>.</param>
    /// <param name="source">The id its pages will carry in a <see cref="PageKey" />.</param>
    /// <param name="morphIndex">
    ///     Its blend shapes re-indexed by vertex, or null for a mesh with none. Null is the ordinary
    ///     answer and leaves <see cref="RasterMesh.MorphRunBase" /> at
    ///     <see cref="RasterMesh.NoMorphs" />, which is the branch every rock in the project takes.
    /// </param>
    /// <returns>Where its records landed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Page indices become global here, and cluster indices do not.</b> The residency bitset
    ///         is one bitset for the pool, so a page's identity has to be unique across every mesh in
    ///         it — hence <see cref="ClusterMesh.FirstPage" /> added to every record's page. A
    ///         cluster index stays mesh-local, because the shader reads
    ///         <c>clusterRecords[instance.firstCluster + cluster]</c> and the roots and the child runs
    ///         are indices of that same kind; making those global too would be adding the base twice.
    ///     </para>
    ///     <para>
    ///         <b>Page zero is pinned, and that is the floor of the whole degradation story.</b> It
    ///         holds the roots — <c>MeshletPageBuilder</c> packs coarsest first so that it does — so an
    ///         object whose other pages have not arrived draws at its coarsest level rather than not at
    ///         all.
    ///     </para>
    /// </remarks>
    public ClusterMesh Register(MeshletMesh mesh, MeshletPageSet pages, int source, MorphIndex? morphIndex = null) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pages);
        ObjectDisposedException.ThrowIf(disposed, this);

        var scene = GpuClusterCulling.Flatten(mesh, pages);
        var (center, radius) = Bound(scene);

        var entry = new ClusterMesh(
            source,
            clusters.Count,
            scene.Clusters.Length,
            roots.Count,
            scene.Roots.Length,
            PageCount,
            pages.Pages.Length,
            center,
            radius
        );

        var childBase = (uint)children.Count;

        foreach (var record in scene.Clusters) {
            clusters.Add(
                record with {
                    FirstChild = record.FirstChild + childBase,
                    Page = record.Page + (uint)entry.FirstPage
                }
            );
        }

        children.AddRange(scene.Children);
        roots.AddRange(scene.Roots);

        // The raster's half of the same registration, built here rather than by whatever records the
        // draw, so the page offset is applied by one authority. Two places that each add FirstPage is
        // how the traversal comes to be testing one page while the raster reads another.
        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            var placement = pages.Clusters[i];

            geometry.Add(
                new() {
                    Origin = placement.Origin,
                    Page = (uint)(placement.Page + entry.FirstPage),
                    VertexOffset = (uint)placement.VertexOffset,
                    TriangleOffset = (uint)placement.TriangleOffset,
                    TriangleCount = (uint)mesh.Meshlets[i].TriangleCount,
                    VertexStride = (uint)pages.VertexStride
                }
            );
        }

        grids.Add(
            new() {
                QuantizationOrigin = pages.QuantizationOrigin,
                QuantizationStep = pages.QuantizationStep,
                InfluenceOffset = pages.IsSkinned ? (uint)pages.InfluenceOffset : RasterMesh.NoInfluences,

                // ⚠ Written explicitly on both branches. These three words were RasterMesh's padding,
                // so a record that left them alone would read zero — and zero is a real base that the
                // first morphed mesh registered gets, which is a rock wearing a face's deltas.
                MorphRunBase = RasterMesh.NoMorphs,
                MorphEntryBase = RasterMesh.NoMorphs,
                MorphClusterBase = RasterMesh.NoMorphs
            }
        );

        if (morphIndex is not null) {
            grids[^1] = Morphed(mesh, morphIndex, grids[^1]);
        }

        // Which material each cluster uses. Per cluster rather than per mesh, and every cluster of a mesh
        // shares one value today — but phase 8 of the plan routes clusters whose material discards or
        // perturbs position to a distinct raster permutation, and that needs the material to be a
        // cluster's property rather than a mesh's. Nothing here depends on them being equal.
        foreach (var meshlet in mesh.Meshlets) {
            materials.Add((uint)Math.Max(meshlet.MaterialIndex, 0));
        }

        PageSize = Math.Max(PageSize, pages.PageSize);
        MaterialCount = Math.Max(MaterialCount, mesh.Meshlets.Max(meshlet => meshlet.MaterialIndex) + 1);
        MaximumTriangles = Math.Max(MaximumTriangles, mesh.Meshlets.Max(meshlet => meshlet.TriangleCount));
        PageCount += pages.Pages.Length;
        meshes.Add(entry);
        meshesDirty = true;

        if (pages.Pages.Length > 0) {
            Residency?.Pin(new(source, 0));
        }

        return entry;
    }

    /// <summary>
    ///     Retires a registration and gives its pages back to the pool.
    /// </summary>
    /// <param name="index">Which registration, as <see cref="Register" /> numbered it.</param>
    /// <returns>Whether there was a live registration to retire.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such registration.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What a level unload calls, and the reason it has to exist.</b> A registration pins its
    ///         root page, and a pinned page is never evicted — so a project that loads a level, unloads
    ///         it and loads another has permanently spent one slot per mesh of the level it is no longer
    ///         drawing. Enough of those and the pool is entirely pinned to content nothing references,
    ///         which <see cref="PageResidency.Pin" /> then refuses by name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The record is retired in place, not removed.</b> Every list here is addressed by a
    ///         base and a count recorded at registration — <see cref="ClusterMesh.FirstCluster" />,
    ///         <see cref="ClusterMesh.FirstPage" /> — and those bases are baked into the cluster and
    ///         raster records themselves, so removing a mesh's slice would renumber every mesh after it
    ///         and every <c>VirtualGeometryDraw.Mesh</c> a scene is holding. Zeroing the counts costs the
    ///         slice's memory until the whole system is disposed and costs nothing per frame: an
    ///         instance pointing at it has no roots to walk, and its page window uploads as absent.
    ///     </para>
    /// </remarks>
    public bool Unregister(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, meshes.Count);
        ObjectDisposedException.ThrowIf(disposed, this);

        var entry = meshes[index];

        if (entry.ClusterCount == 0 && entry.RootCount == 0 && entry.PageCount == 0) {
            return false;
        }

        // Dropped rather than unpinned: unpinning hands the page to the eviction order, where it waits
        // to be the least recently used of a pool it no longer belongs to. The content is gone, so the
        // next thing loaded should be given the room rather than have to earn it.
        if (Residency is not null) {
            for (var page = 0; page < entry.PageCount; page++) {
                Residency.Drop(new(entry.Source, page));
            }
        }

        meshes[index] = entry with { ClusterCount = 0, RootCount = 0, PageCount = 0 };
        meshesDirty = true;

        return true;
    }

    /// <summary>
    ///     The merged cluster records, as the device will read them.
    /// </summary>
    /// <remarks>
    ///     Named as the binding is — <c>clusterRecords</c> — because that is what this is: the buffer's
    ///     contents before they are a buffer. Exposed because the page offset applied during
    ///     <see cref="Register" /> is invisible from everywhere else:
    ///     <see cref="ClusterMesh.FirstPage" /> says what the offset <em>should</em> be and these say
    ///     what it was, and a check of only the first would pass with the second never applied.
    /// </remarks>
    public ReadOnlySpan<CullCluster> Records => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(clusters);

    /// <summary>
    ///     The merged geometry records, as the raster will read them.
    /// </summary>
    /// <remarks>
    ///     <see cref="Records" />' counterpart, exposed for the same reason: the page offset and the
    ///     triangle counts are applied here and are invisible from anywhere else, so a check of the
    ///     registration alone would pass with the records never written.
    /// </remarks>
    public ReadOnlySpan<RasterCluster> GeometryRecords =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(geometry);

    /// <summary>One grid per registered mesh, as the raster and the resolve read them.</summary>
    /// <remarks>
    ///     On <see cref="GeometryRecords" />' terms. What is only visible here is whether a mesh's
    ///     vertices carry bone influences, which the registration derives from the page set — so a check
    ///     of the page set alone passes with every mesh registered as rigid.
    /// </remarks>
    public ReadOnlySpan<RasterMesh> MeshRecords =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(grids);

    /// <summary>This frame's instance records, before they reach the device.</summary>
    public ReadOnlySpan<CullInstance> InstanceRecords => instances.Items;

    /// <summary>
    ///     A sphere around every root of a flattened DAG, which is a sphere around the whole mesh.
    /// </summary>
    /// <remarks>
    ///     The roots cover the mesh by construction — a root is a cluster nothing simplifies further, and
    ///     the coarsest cut is all of them — so bounding those bounds the mesh without touching a vertex.
    ///     Around the box rather than a minimal sphere: what it is for is scaling a pose's displacement,
    ///     where a loose bound refines a little sooner and a wrong one culls something on screen.
    /// </remarks>
    static (Vector3 Center, float Radius) Bound(ClusterScene scene) {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);

        foreach (var root in scene.Roots) {
            var record = scene.Clusters[(int)root];
            var extent = new Vector3(record.Radius);

            minimum = Vector3.Min(minimum, record.Center - extent);
            maximum = Vector3.Max(maximum, record.Center + extent);
        }

        if (scene.Roots.Length == 0) {
            return (Vector3.Zero, 0f);
        }

        var center = (minimum + maximum) * 0.5f;

        return (center, (maximum - center).Length());
    }

    /// <summary>What a registered mesh's records are, by registration order.</summary>
    /// <param name="index">Which registration.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such one.</exception>
    public ClusterMesh MeshAt(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, meshes.Count);

        return meshes[index];
    }

    /// <summary>Begins a frame's instance records, keeping what did not change.</summary>
    /// <param name="count">How many instances this frame has.</param>
    public void Begin(int count) {
        ObjectDisposedException.ThrowIf(disposed, this);
        instances.Begin(count);
        InstanceCount = count;
    }

    /// <summary>
    ///     Begins a frame's bone palettes, discarding the last frame's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="Begin" /> and explicitly called, for the reason
    ///         <see cref="Features.SkinningRenderFeature.Begin" /> is: whoever fills a palette is the
    ///         animation system, which runs before the render system's phases — so there is no callback
    ///         of ours between "the pose is final" and "the first palette is written". Folding it into
    ///         the instance frame would wipe every palette the frame had already been given.
    ///     </para>
    ///     <para>
    ///         The first matrix is always an identity, so that the buffer exists even in a frame with
    ///         nothing skinned in it. A binding a variant declares has to be filled whether or not the
    ///         shader's branch reaches it, which is the same reason this class keeps a placeholder
    ///         texture — and an identity is the value that would be harmless if it ever were read.
    ///     </para>
    /// </remarks>
    public void BeginBones() {
        ObjectDisposedException.ThrowIf(disposed, this);

        ReadOnlySpan<Matrix4x4> identity = [Matrix4x4.Identity];

        bones.Begin();
        bones.Add(identity);
    }

    /// <summary>Adds one instance's palette to this frame's.</summary>
    /// <param name="palette">
    ///     Its skinning matrices — <c>inverseBindPose * boneWorld</c>, one per bone, in the order the
    ///     mesh's page vertices index them.
    /// </param>
    /// <returns>Where they landed, for <see cref="CullInstance.FirstBone" />.</returns>
    /// <exception cref="InvalidOperationException"><see cref="BeginBones" /> has not been called.</exception>
    /// <remarks>
    ///     A palette per instance rather than per skeleton, which is one pose's worth of duplication for
    ///     two characters in the same animation on the same frame — and the alternative is an identity
    ///     for a pose, which is a cache nothing here can invalidate.
    /// </remarks>
    public int AddBones(ReadOnlySpan<Matrix4x4> palette) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (bones.Count == 0) {
            throw new InvalidOperationException(
                "Call BeginBones before adding a palette. A frame that added palettes without beginning "
                + "one would keep growing the buffer with poses no instance points at."
            );
        }

        return bones.Add(palette);
    }

    /// <summary>Begins a frame's blend-shape weights, discarding the last frame's.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="BeginBones" />'s twin, called for its reason: whoever fills the weights is an
    ///         animation system running before the render system's phases, so there is no callback of
    ///         ours between "the expression is final" and "the first weight is written".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first entry is always zero and no instance ever points at it</b>, which is what
    ///         makes <see cref="GpuCulling.NoWeights" /> able to be zero — and zero is what a record
    ///         nobody filled holds. A dead slot, a draw a test forgot a field of, an instance extracted
    ///         before any weight reached it: every one of them is at rest rather than wearing whichever
    ///         expression happened to be first. It is also what makes the buffer exist at all in a
    ///         frame with nothing morphed, which the descriptor write needs.
    ///     </para>
    /// </remarks>
    public void BeginMorphs() {
        ObjectDisposedException.ThrowIf(disposed, this);

        ReadOnlySpan<Vector2> rest = [Vector2.Zero];

        morphWeights.Begin();
        morphWeights.Add(rest);
    }

    /// <summary>Adds one instance's weights to this frame's.</summary>
    /// <param name="weights">
    ///     One entry per shape of its mesh: the weight already multiplied by that shape's quantisation
    ///     step, position in <c>X</c> and normal in <c>Y</c>.
    /// </param>
    /// <returns>Where they landed, for <see cref="CullInstance.FirstWeight" />.</returns>
    /// <exception cref="InvalidOperationException"><see cref="BeginMorphs" /> has not been called.</exception>
    /// <remarks>
    ///     ⚠ <b>Pre-multiplied on the host, rather than the step being uploaded beside the weight.</b>
    ///     <c>MorphKernel.Step</c> makes the argument: one division here is one float both processors
    ///     then agree about exactly, and two is two chances for them not to.
    /// </remarks>
    public int AddMorphWeights(ReadOnlySpan<Vector2> weights) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (morphWeights.Count == 0) {
            throw new InvalidOperationException(
                "Call BeginMorphs before adding weights. A frame that added weights without beginning "
                + "one would keep growing the buffer with expressions no instance points at."
            );
        }

        return morphWeights.Add(weights);
    }

    /// <summary>Sets one instance's record, uploading it only if it differs.</summary>
    /// <param name="index">Which instance slot.</param>
    /// <param name="instance">Its record.</param>
    public void Set(int index, in CullInstance instance) {
        ObjectDisposedException.ThrowIf(disposed, this);
        instances.Set(index, instance);
    }

    /// <summary>Hands whatever changed to the device.</summary>
    /// <remarks>
    ///     Called by <see cref="Prepare" />, and separately callable so that the incremental upload can
    ///     be measured without standing up an effect system to resolve a variant with. What
    ///     <see cref="InstanceBytesUploaded" /> reports is the result of this.
    /// </remarks>
    public void UploadInstances() {
        ObjectDisposedException.ThrowIf(disposed, this);

        // A frame with nothing skinned in it still has to have a palette, because the raster and the
        // resolve declare the binding whether or not any instance reaches it — a permutation folds code
        // and not bindings, and this one is not even a permutation. Without the seed the buffer is never
        // created and the descriptor write refers to nothing, which a recording backend accepts and a
        // real device refuses outright.
        if (bones.Count == 0) {
            BeginBones();
        }

        // And the same for the weights, for the same reason and with the same consequence: three
        // shaders declare the binding whether or not any instance is morphed.
        if (morphWeights.Count == 0) {
            BeginMorphs();
        }

        instances.Upload();
        bones.Upload();
        morphWeights.Upload();
    }

    /// <summary>
    ///     Resolves the variant, uploads what changed, and leaves a dispatch for the frame to record.
    /// </summary>
    /// <param name="frameViews">The views to traverse for.</param>
    /// <param name="errorScale">
    ///     What an object-space error is multiplied by before the distance divide, per view, from
    ///     <see cref="GpuClusterCulling.ErrorScaleFor(float, int)" />.
    /// </param>
    /// <param name="errorThreshold">How many pixels of deviation a view tolerates, per view.</param>
    /// <param name="softwareThreshold">
    ///     How wide a cluster may be on screen before it goes to the hardware raster, per view. Empty —
    ///     which is the default — routes every cluster to hardware, and so does a device without
    ///     <see cref="GraphicsDeviceFeatures.HasInt64Atomics" /> whatever this says.
    /// </param>
    /// <returns>Whether there is a dispatch to record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frameViews" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         Nothing is submitted and nothing is waited on — <see cref="Record" /> puts the dispatch in
    ///         the frame's own list, for <see cref="GpuVisibilityGroup.Record" />'s reason: the only
    ///         ordering this RHI can express without a fence is a barrier between two things in the same
    ///         queue, and what consumes this answer is a draw.
    ///     </para>
    ///     <para>
    ///         The error scale and threshold are per view and arrive here rather than being computed
    ///         here, because they are a camera's field of view and a project's quality setting — neither
    ///         of which a buffer of clusters knows anything about.
    ///     </para>
    /// </remarks>
    public bool Prepare(
        IReadOnlyList<RenderView> frameViews,
        ReadOnlySpan<float> errorScale,
        ReadOnlySpan<float> errorThreshold,
        ReadOnlySpan<float> softwareThreshold = default
    ) {
        ArgumentNullException.ThrowIfNull(frameViews);
        ObjectDisposedException.ThrowIf(disposed, this);

        TraversedOnDevice = false;
        pending = null;

        if (Effects is null || Pipelines is null || meshes.Count == 0 || InstanceCount == 0 || frameViews.Count == 0) {
            return false;
        }

        var occlusion = Occluders is { IsBuilt: true };

        if (Effects.Resolve(Key(occlusion)) is not { IsPlaceholder: false } effect) {
            return false;
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid || !EnsureBuffers() || !EnsureDescriptors(effect)) {
            return false;
        }

        UploadMeshes();
        UploadInstances();
        UploadViews(frameViews, errorScale, errorThreshold, softwareThreshold);
        UploadResidency();

        ring = (ring + 1) % Math.Max(descriptors.Length, 1);
        Bind(frameViews.Count);

        pending = new(pipeline, InstanceCount, frameViews.Count);
        TraversedOnDevice = true;

        return true;
    }

    /// <summary>Records the traversal into the frame's own list.</summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>False when there was nothing prepared, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The counters are cleared by a copy rather than by a host write.</b> Both buffers are
    ///         device-local, because every append to them is an atomic and an atomic on host-visible
    ///         memory is a round trip. Zeroing element zero is therefore a four-byte copy out of a
    ///         buffer of zeros — <see cref="GpuDrawArguments" />'s device-side clear, for its reason.
    ///     </para>
    ///     <para>
    ///         <b>The request copy at the end is what closes the streaming loop</b>, and it is read a
    ///         frame later rather than waited for — see <see cref="ServiceRequests" />.
    ///     </para>
    /// </remarks>
    public bool Record(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (pending is not { } work) {
            return false;
        }

        pending = null;

        // The mesh records, if a registration staged any. Before the barrier below and before the
        // dispatch, because the traversal is about to read exactly these.
        if (pendingMeshCopies.Count > 0) {
            foreach (var (from, to, size) in pendingMeshCopies) {
                list.Barrier(new([new(to, ResourceState.Undefined, ResourceState.CopyDestination)], []));
                list.CopyBuffer(staging, from, to, 0, size);
                list.Barrier(new([new(to, ResourceState.CopyDestination, ResourceState.ShaderRead)], []));
            }

            pendingMeshCopies.Clear();
        }

        // A texture is created in no layout at all and a descriptor promises the one it was written
        // with, so the stand-in has to be transitioned even though nothing ever samples it. Once, on
        // the first dispatch that binds it; the pyramid transitions itself.
        if (placeholder.IsValid && !placeholderReady) {
            list.Barrier(new([], [new(placeholder, ResourceState.Undefined, ResourceState.ShaderRead)]));
            placeholderReady = true;
        }

        // The counts, and only the counts: the rest of both buffers is the previous frame's answer and
        // is about to be overwritten by however much this frame's answer takes. Clearing all of it
        // would be megabytes of copy to hide a tail nothing reads, because a reader reads the count.
        list.Barrier(
            new(
                [
                    new(visibleBuffer, visibleState, ResourceState.CopyDestination),
                    new(requestBuffer, requestState, ResourceState.CopyDestination)
                ],
                []
            )
        );

        // Three words of the visible list and one of the requests: the counters, and only the counters.
        // The visible list's three are the two rasters' counts and the reservation that keeps their
        // regions apart — see VisibleBase — and all three have to reach zero together, or a frame's
        // hardware entries land on top of the previous frame's software ones.
        list.CopyBuffer(zeros, 0, visibleBuffer, 0, (long)VisibleBase * sizeof(uint));
        list.CopyBuffer(zeros, 0, requestBuffer, 0, sizeof(uint));

        list.Barrier(
            new(
                [
                    new(visibleBuffer, ResourceState.CopyDestination, ResourceState.ShaderWrite),
                    new(requestBuffer, ResourceState.CopyDestination, ResourceState.ShaderWrite)
                ],
                []
            )
        );

        list.BindPipeline(work.Pipeline);
        list.BindDescriptorSet(Slot, descriptors[ring]);

        // One workgroup per instance per view, which is the traversal's shape: a group owns an
        // instance's front and its lanes share the queue. Not a word count — that is the object cull's
        // shape, and the same entry point serves both because the branch folds away.
        list.Dispatch(work.InstanceCount, work.ViewCount);

        // ShaderRead rather than IndirectArgument: what reads the visible list is a shader indexing it,
        // and what reads the count is the indirect draw's count buffer — which phase 4 transitions when
        // it records the draw, because it is the side that knows.
        list.Barrier(
            new(
                [
                    new(visibleBuffer, ResourceState.ShaderWrite, ResourceState.ShaderRead),
                    new(requestBuffer, ResourceState.ShaderWrite, ResourceState.CopySource)
                ],
                []
            )
        );

        list.CopyBuffer(requestBuffer, 0, requestReadbackBuffer, 0, RequestBytes);
        list.CopyBuffer(visibleBuffer, 0, requestReadbackBuffer, RequestBytes, (long)VisibleBase * sizeof(uint));

        visibleState = ResourceState.ShaderRead;
        requestState = ResourceState.CopySource;

        return true;
    }

    /// <summary>
    ///     Feeds the previous frame's requests to the residency service and lets it work.
    /// </summary>
    /// <param name="maxLoads">How many loads may be started, as <see cref="PageResidency.Service" />'s.</param>
    /// <returns>How many pages became resident.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A frame late, and deliberately.</b> The requests are written by a dispatch this host
    ///         has already submitted and not waited for, so reading them at the point they were written
    ///         would be a stall — the one thing a GPU-driven pipeline is arranged to avoid. Reading them
    ///         at the head of the next frame reads whatever the queue has got to, which is at worst the
    ///         frame before.
    ///     </para>
    ///     <para>
    ///         <b>Nothing is lost by being late, because a request is not a promise.</b> It says a cut
    ///         wanted a cluster this view could not draw, and the traversal draws the parent instead
    ///         and asks again next frame — which is why <c>MeshletCut</c>'s residency-aware overload
    ///         raises the threshold rather than dropping what is missing. So the cost of the latency is
    ///         a frame or two at a coarser level, not a hole.
    ///     </para>
    /// </remarks>
    public int ServiceRequests(int maxLoads = 8) {
        ObjectDisposedException.ThrowIf(disposed, this);

        RequestedPages = 0;
        VisibleClusters = 0;
        SoftwareClusters = 0;
        VisibleOverflowed = false;

        if (Residency is null) {
            return 0;
        }

        if (requestReadbackBuffer.IsValid) {
            device.Read(requestReadbackBuffer, 0, MemoryMarshal.AsBytes(requestReadback.AsSpan()));

            var wanted = (int)requestReadback[0];
            RequestedPages = wanted;

            // The three counter words, in the visible list's own order. The reservation is what the
            // overflow is judged on rather than either raster's count: a frame whose cut did not fit
            // stopped appending somewhere, and which end it stopped at is not the interesting part.
            var counters = requestReadback.AsSpan(RequestCapacity + 1, VisibleBase);

            SoftwareClusters = (int)counters[1];
            VisibleClusters = (int)counters[2];
            VisibleOverflowed = VisibleClusters > VisibleLimit;

            // Clamped, because the count is what the dispatch wanted to write and the buffer holds what
            // it had room for. A count above the capacity is a frame that asked for more pages than one
            // frame can act on anyway.
            for (var i = 0; i < Math.Min(wanted, RequestCapacity); i++) {
                var page = requestReadback[i + 1];

                if (Locate(page) is { } key) {
                    Residency.Request(key);
                }
            }
        }

        return Residency.Service(maxLoads);
    }

    /// <summary>Which mesh's page a global page index belongs to.</summary>
    /// <remarks>
    ///     A linear scan over the registered meshes, which is the right shape at this size: the list is
    ///     short, this runs a few dozen times a frame rather than a few million, and the alternative —
    ///     a page-to-source table over every page in the scene — is a megabyte to avoid an inner loop
    ///     nobody has measured.
    /// </remarks>
    PageKey? Locate(uint page) {
        foreach (var mesh in meshes) {
            if (page >= (uint)mesh.FirstPage && page < (uint)(mesh.FirstPage + mesh.PageCount)) {
                return new(mesh.Source, (int)page - mesh.FirstPage);
            }
        }

        return null;
    }

    /// <summary>Which effect key selects the traversal.</summary>
    /// <remarks>
    ///     The object cull's key with <c>Clusters</c> turned on, rather than a key of its own — one
    ///     shader, two dispatch shapes. <c>Late</c> is deliberately absent: a two-phase traversal is a
    ///     real idea and it is not this phase's, because subtracting a previous pass's answer needs the
    ///     answer to be a bitset a lane owns a word of, and a compacted append-only list is not.
    /// </remarks>
    static EffectKey Key(bool occlusion) =>
        EffectKey.Of(
            GpuCulling.ShaderName,
            [
                new(GpuCulling.OcclusionKey, occlusion ? "true" : "false"),
                new(GpuClusterCulling.ClustersKey, "true"),
                new(GpuCulling.LateKey, "false")
            ],
            global::Vixen.Rendering.Materials.MaterialCompiler.PassComposition()
        );

    static long RequestBytes => (long)(RequestCapacity + 1) * sizeof(uint);

    int VisibleLimit => Math.Max((int)(visibleCapacity / sizeof(uint)) - VisibleBase, 0);

    /// <summary>Uploads the meshes' records, if a registration changed them.</summary>
    /// <remarks>
    ///     A whole-buffer write on a dirty flag rather than anything incremental, and that is the
    ///     honest shape: these change when a mesh is registered, which happens at load. An incremental
    ///     path here would be machinery for a frame that never has any work for it.
    /// </remarks>
    void UploadMeshes() {
        if ((!meshesDirty && !morphsDirty) || !staging.IsValid) {
            return;
        }

        // Staged and then copied, rather than written straight in. Every one of these is device-local —
        // the traversal reads them for every cluster it tests, which is the memory that has to be fast —
        // and device-local memory is not host-writable at all. The copies go in the frame's own list, at
        // the head of it, which `Record` is already the place for.
        pendingMeshCopies.Clear();
        stagedMeshBytes = 0;

        Stage(clusterBuffer, MemoryMarshal.AsBytes<CullCluster>(clusters.ToArray()));
        Stage(childBuffer, MemoryMarshal.AsBytes<uint>(children.Count > 0 ? children.ToArray() : [0u]));
        Stage(rootBuffer, MemoryMarshal.AsBytes<uint>(roots.Count > 0 ? roots.ToArray() : [0u]));
        Stage(geometryBuffer, MemoryMarshal.AsBytes<RasterCluster>(geometry.ToArray()));
        Stage(gridBuffer, MemoryMarshal.AsBytes<RasterMesh>(grids.ToArray()));
        Stage(materialBuffer, MemoryMarshal.AsBytes<uint>(materials.Count > 0 ? materials.ToArray() : [0u]));

        // ⚠ A single zero word for a scene with no blend shapes in it, not nothing. The three rasters
        // declare the binding whatever the branch does, and an uninitialised device-local buffer is
        // what a shader that took the branch by mistake would scatter a face across.
        Stage(morphBuffer, MemoryMarshal.AsBytes<uint>(morphs.Count > 0 ? morphs.ToArray() : [0u]));

        meshesDirty = false;
        morphsDirty = false;
    }

    /// <summary>Lays one mesh's blend shapes out in the morph buffer, and says where they went.</summary>
    /// <remarks>
    ///     <para>
    ///         Four runs, appended and never moved: the per-cluster table, the source-vertex runs the
    ///         table points into, the per-vertex prefix and the entries. A mesh is retired in place
    ///         rather than removed — see <see cref="Unregister" /> — so an absolute offset written here
    ///         stays true for the life of the system, which is what lets the cluster table hold
    ///         absolute offsets and cost one read instead of two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source-vertex runs are <c>MeshletMesh.Vertices</c> verbatim</b>, laid out
    ///         exactly as the DAG has them, so a cluster's run starts at its own
    ///         <c>Meshlet.VertexOffset</c>. Re-packing them per cluster would be the same bytes in a
    ///         different order and one more thing that can disagree with
    ///         <c>MeshletPageBuilder.Write</c>, which walked the same array to build the page.
    ///     </para>
    /// </remarks>
    RasterMesh Morphed(MeshletMesh mesh, MorphIndex index, RasterMesh grid) {
        var clusterBase = morphs.Count;

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            morphs.Add(0u);
        }

        var verticesBase = morphs.Count;

        foreach (var vertex in mesh.Vertices) {
            morphs.Add((uint)vertex);
        }

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            morphs[clusterBase + i] = (uint)(verticesBase + mesh.Meshlets[i].VertexOffset);
        }

        var runBase = morphs.Count;
        morphs.AddRange(index.Runs);

        var entryBase = morphs.Count;
        morphs.AddRange(index.Entries);

        morphsDirty = true;

        return grid with {
            MorphClusterBase = (uint)clusterBase,
            MorphRunBase = (uint)runBase,
            MorphEntryBase = (uint)entryBase
        };
    }

    /// <summary>Copies one array into the staging buffer and remembers where it has to end up.</summary>
    void Stage(BufferHandle destination, ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty || stagedMeshBytes + bytes.Length > MeshStagingBytes) {
            return;
        }

        device.Write(staging, stagedMeshBytes, bytes);
        pendingMeshCopies.Add((stagedMeshBytes, destination, bytes.Length));
        stagedMeshBytes += bytes.Length;
    }

    /// <summary>How many bytes the mesh records take between them, for the staging buffer.</summary>
    long MeshStagingBytes =>
        ((long)clusters.Count * Unsafe.SizeOf<CullCluster>())
        + ((long)geometry.Count * Unsafe.SizeOf<RasterCluster>())
        + ((long)grids.Count * Unsafe.SizeOf<RasterMesh>())
        + ((long)(children.Count + roots.Count + materials.Count + morphs.Count + 4) * sizeof(uint));

    void UploadViews(
        IReadOnlyList<RenderView> frameViews,
        ReadOnlySpan<float> errorScale,
        ReadOnlySpan<float> errorThreshold,
        ReadOnlySpan<float> softwareThreshold
    ) {
        if (packedViews.Length < frameViews.Count) {
            packedViews = new CullView[Math.Max(frameViews.Count, 4)];
        }

        // ⚠ **The capability gate, and it is one line on purpose.** A device with no 64-bit atomic
        // cannot run `ClusterSoftwareRaster.rvn` at all, and the cheapest way to say so is a threshold
        // of zero: the traversal then routes every cluster to the hardware raster and the frame is
        // exactly the frame phase 4 drew. A gate anywhere else would be a second place for "is the
        // software path on" to be answered, and the way that fails is a cluster in a list nothing
        // dispatches over — a hole rather than a slower frame.
        var routable = device.Features.HasInt64Atomics;

        var levels = Occluders?.Levels ?? 0;
        var usable = levels > 0 && Occluders is { IsBuilt: true, View.IsValid: true } && projections.Matches(frameViews.Count);

        for (var i = 0; i < frameViews.Count; i++) {
            // Occlusion per view rather than per dispatch, and against the matrix this view was seen
            // with in the frame the pyramid was built in — see OccluderProjections, which is the same
            // bookkeeping the object cull does because it is the same question.
            var known = projections.TryGet(i, out var projection);
            var tested = usable && known;

            var view = GpuCulling.Pack(
                frameViews[i],
                0,
                0,
                tested ? projection : default,
                tested ? levels : 0
            );

            view.ErrorScale = i < errorScale.Length ? errorScale[i] : 0f;
            view.ErrorThreshold = i < errorThreshold.Length ? errorThreshold[i] : 0f;

            view.SoftwareThreshold = routable && i < softwareThreshold.Length
                ? MathF.Max(softwareThreshold[i], 0f)
                : 0f;

            packedViews[i] = view;
        }

        projections.Remember(frameViews);

        views.Begin();
        views.Add(packedViews.AsSpan(0, frameViews.Count));
        views.Upload();
    }

    /// <summary>
    ///     Turns the residency service's answer into the slot table both the traversal and the raster
    ///     read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A slot per page, not a bit per page, and that is the point.</b> The traversal only asks
    ///         the yes-or-no question, so a bitset would do for it — but the raster has to find the
    ///         cluster's bytes, which means it needs the slot. Two tables would be two authorities on
    ///         whether a page is present, and the way that fails is a cluster drawn out of a slot holding
    ///         a different page's geometry: the right shape, in the right place, made of the wrong mesh.
    ///     </para>
    ///     <para>
    ///         Rebuilt every frame rather than tracked, because it is one word per page and a scene's
    ///         pages number in the thousands — the whole table is smaller than the bookkeeping that would
    ///         keep it incremental.
    ///     </para>
    ///     <para>
    ///         <b>Absent is all ones, not zero.</b> Zero is a real slot — the first one — so a table
    ///         cleared to zero says every page of every mesh is sitting in slot zero, which draws the
    ///         whole scene out of one page's bytes. See <c>Cull.PageAbsent</c>.
    ///     </para>
    /// </remarks>
    void UploadResidency() {
        var count = Math.Max(PageCount, 1);

        if (residency.Length < count) {
            residency = new uint[count];
        }

        residency.AsSpan(0, count).Fill(PageAbsent);

        if (Residency is not null) {
            foreach (var mesh in meshes) {
                for (var page = 0; page < mesh.PageCount; page++) {
                    if (!Residency.TryGetPlacement(new(mesh.Source, page), out var placement)) {
                        continue;
                    }

                    residency[mesh.FirstPage + page] = (uint)placement.Slot;

                    // Resident and about to be read is exactly what Touch means, and without it the
                    // pages a frame draws are the ones it evicts — a pool that thrashes hardest on the
                    // geometry nearest the camera.
                    Residency.Touch(new(mesh.Source, page));
                }
            }
        }

        residencyBits.Begin();
        residencyBits.Add(residency.AsSpan(0, count));
        residencyBits.Upload();
    }

    /// <summary>Points every binding of the set at something — see <see cref="GpuCulling.SetBindings" />.</summary>
    void Bind(int viewCount) {
        // The four the traversal does not read. `objects` and `visibility` are the object cull's answer
        // and this dispatch has its own, and the occluder texture is real when there is a pyramid.
        writes[0] = DescriptorWrite.Storage(CullingKeys.ObjectsBinding, clusterBuffer, 0, sizeof(uint) * 8);
        writes[2] = DescriptorWrite.Storage(CullingKeys.VisibilityBinding, requestBuffer, 0, sizeof(uint));
        writes[3] = DescriptorWrite.Texture(CullingKeys.OccludersBinding, Occluding());

        writes[1] = DescriptorWrite.Storage(
            CullingKeys.ViewsBinding,
            views.Buffer,
            views.Offset,
            (long)viewCount * Unsafe.SizeOf<CullView>()
        );

        writes[4] = DescriptorWrite.Storage(
            CullingKeys.ClusterRecordsBinding,
            clusterBuffer,
            0,
            (long)clusters.Count * Unsafe.SizeOf<CullCluster>()
        );

        writes[5] = DescriptorWrite.Storage(
            CullingKeys.InstancesBinding,
            instances.Buffer,
            instances.Offset,
            (long)InstanceCount * Unsafe.SizeOf<CullInstance>()
        );

        writes[6] = DescriptorWrite.Storage(
            CullingKeys.ChildrenBinding,
            childBuffer,
            0,
            (long)Math.Max(children.Count, 1) * sizeof(uint)
        );

        writes[7] = DescriptorWrite.Storage(
            CullingKeys.RootsBinding,
            rootBuffer,
            0,
            (long)Math.Max(roots.Count, 1) * sizeof(uint)
        );

        writes[8] = DescriptorWrite.Storage(CullingKeys.VisibleBinding, visibleBuffer, 0, visibleCapacity);
        writes[9] = DescriptorWrite.Storage(CullingKeys.RequestsBinding, requestBuffer, 0, RequestBytes);

        writes[10] = DescriptorWrite.Storage(
            CullingKeys.ResidencyBinding,
            residencyBits.Buffer,
            residencyBits.Offset,
            (long)Math.Max(PageCount, 1) * sizeof(uint)
        );

        device.UpdateDescriptorSet(descriptors[ring], writes);
    }

    TextureViewHandle Occluding() =>
        Occluders is { IsBuilt: true } pyramid && pyramid.View.IsValid ? pyramid.View : placeholderView;

    bool EnsureBuffers() {
        // ⚠ Capped at what a pixel can name, which became a bound on the *list* rather than on the
        // accepted count when phase 6 put the software raster's entries at the far end of it. Twenty-five
        // bits of slot is thirty-three million entries; at four kilobytes an instance the cap is reached
        // by a scene of thirty-two thousand virtualized objects, and past it a software-routed cluster
        // would pack a slot that wraps into another cluster's triangle index.
        var entries = Math.Min((long)InstanceCount * VisiblePerInstance, GpuClusterRaster.MaximumSlots);
        var wanted = (entries + VisibleBase) * sizeof(uint);
        var wantedMorphs = Math.Max((long)morphs.Count * sizeof(uint), 256);

        // ⚠ The morph tables are in the condition, because they are the one mesh-record buffer that can
        // grow after the first frame in a way nothing else notices. A morphed mesh registered into a
        // scene whose buffers already exist would otherwise be staged into an allocation sized before
        // it — a run of deltas truncated at whatever the last mesh needed, which is a face reading
        // another mesh's entries and not a face at rest.
        if (clusterBuffer.IsValid && visibleCapacity >= wanted && morphCapacity >= wantedMorphs) {
            return Occluding().IsValid;
        }

        Release();

        clusterBuffer = device.CreateBuffer(
            new(
                Math.Max((long)clusters.Count * Unsafe.SizeOf<CullCluster>(), 256),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Clusters"
            )
        );

        childBuffer = device.CreateBuffer(
            new(
                Math.Max((long)children.Count * sizeof(uint), 256),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Children"
            )
        );

        rootBuffer = device.CreateBuffer(
            new(
                Math.Max((long)roots.Count * sizeof(uint), 256),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Roots"
            )
        );

        geometryBuffer = device.CreateBuffer(
            new(
                Math.Max((long)geometry.Count * Unsafe.SizeOf<RasterCluster>(), 256),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Geometry"
            )
        );

        morphBuffer = device.CreateBuffer(
            new(
                wantedMorphs,
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Morphs"
            )
        );

        morphCapacity = wantedMorphs;

        gridBuffer = device.CreateBuffer(
            new(
                Math.Max((long)grids.Count * Unsafe.SizeOf<RasterMesh>(), 256),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Grids"
            )
        );

        materialBuffer = device.CreateBuffer(
            new(
                Math.Max((long)materials.Count * sizeof(uint), 256),
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Materials"
            )
        );

        visibleBuffer = device.CreateBuffer(
            new(
                wanted,
                BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination | BufferUsage.Indirect,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Visible"
            )
        );

        requestBuffer = device.CreateBuffer(
            new(
                RequestBytes,
                BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterCulling.Requests"
            )
        );

        // The requests and the visible count in one readback, because they are read together at the
        // head of a frame and two host-visible buffers would be two maps to answer one question.
        requestReadbackBuffer = device.CreateBuffer(
            new(
                RequestBytes + ((long)VisibleBase * sizeof(uint)),
                BufferUsage.CopyDestination,
                MemoryAccess.HostReadback,
                "ClusterCulling.RequestReadback"
            )
        );

        zeros = device.CreateBuffer(
            new(sizeof(uint) * 4, BufferUsage.CopySource, MemoryAccess.HostUpload, "ClusterCulling.Zeros")
        );

        staging = device.CreateBuffer(
            new(
                Math.Max(MeshStagingBytes, 256),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "ClusterCulling.Staging"
            )
        );

        if (zeros.IsValid) {
            device.Write(zeros, 0, MemoryMarshal.AsBytes<uint>([0u, 0u, 0u, 0u]));
        }

        if (!placeholder.IsValid) {
            placeholder = device.CreateTexture(
                new(PixelFormat.R32Float, 1, 1, TextureUsage.Sampled, Name: "ClusterCulling.NoOccluders")
            );

            placeholderView = placeholder.IsValid ? device.CreateTextureView(placeholder) : default;
            placeholderReady = false;
        }

        visibleCapacity = wanted;
        requestReadback = new uint[RequestCapacity + 1 + VisibleBase];
        meshesDirty = true;
        visibleState = ResourceState.Undefined;
        requestState = ResourceState.Undefined;

        return Occluding().IsValid
            && clusterBuffer.IsValid
            && geometryBuffer.IsValid
            && gridBuffer.IsValid
            && morphBuffer.IsValid
            && materialBuffer.IsValid
            && childBuffer.IsValid
            && rootBuffer.IsValid
            && visibleBuffer.IsValid
            && requestBuffer.IsValid
            && requestReadbackBuffer.IsValid
            && zeros.IsValid
            && staging.IsValid;
    }

    bool EnsureDescriptors(Effect effect) {
        if (ReferenceEquals(allocatedFor, effect) && descriptors.Length > 0) {
            return true;
        }

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        var count = Math.Max(device.FramesInFlight, 1);
        descriptors = new DescriptorSetHandle[count];

        for (var index = 0; index < count; index++) {
            descriptors[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)Slot], "ClusterCulling");

            if (!descriptors[index].IsValid) {
                return false;
            }
        }

        allocatedFor = effect;
        ring = 0;

        return true;
    }

    void Release() {
        foreach (var buffer in (BufferHandle[])
                 [
                     clusterBuffer, childBuffer, rootBuffer, geometryBuffer, gridBuffer, materialBuffer,
                     morphBuffer, visibleBuffer, requestBuffer, requestReadbackBuffer, zeros, staging
                 ]) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        clusterBuffer = default;
        childBuffer = default;
        rootBuffer = default;
        geometryBuffer = default;
        gridBuffer = default;
        materialBuffer = default;
        morphBuffer = default;
        visibleBuffer = default;
        requestBuffer = default;
        requestReadbackBuffer = default;
        zeros = default;
        staging = default;

        pendingMeshCopies.Clear();
        stagedMeshBytes = 0;
        visibleCapacity = 0;
        morphCapacity = 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Release();

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        descriptors = [];

        if (placeholderView.IsValid) {
            device.Destroy(placeholderView);
            placeholderView = default;
        }

        if (placeholder.IsValid) {
            device.Destroy(placeholder);
            placeholder = default;
        }

        instances.Dispose();
        views.Dispose();
        residencyBits.Dispose();
        bones.Dispose();
        morphWeights.Dispose();

        meshes.Clear();
        clusters.Clear();
        children.Clear();
        roots.Clear();
        morphs.Clear();
        geometry.Clear();
        grids.Clear();
        materials.Clear();
    }

    sealed record PendingTraversal(PipelineHandle Pipeline, int InstanceCount, int ViewCount);
}
