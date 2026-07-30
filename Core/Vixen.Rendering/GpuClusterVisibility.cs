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
    int PageCount
);

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
///         improvement 3 of <c>docs/virtualized-geometry.md</c>, and is why the shader is one shader.
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

    // The instances, incrementally: a scene of a hundred thousand virtualized objects of which one
    // moved is one record uploaded, for PersistentUploadBuffer's reason and measured the same way.
    readonly PersistentUploadBuffer<CullInstance> instances = new("ClusterCulling.Instances");
    readonly UploadBuffer<CullView> views = new("ClusterCulling.Views");
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
    BufferHandle visibleBuffer;
    BufferHandle requestBuffer;
    BufferHandle requestReadbackBuffer;
    BufferHandle zeros;

    // One texel, for the frames with no pyramid and a variant that declares one anyway — the same
    // stand-in GpuVisibilityGroup keeps, and needed here for exactly the same reason: a permutation
    // folds away the sampling and leaves the declaration, so the set still has a binding to fill.
    TextureHandle placeholder;
    TextureViewHandle placeholderView;
    bool placeholderReady;

    long visibleCapacity;
    bool meshesDirty = true;

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
    ///     and because improvement 6 of <c>docs/virtualized-geometry.md</c> means the same service will
    ///     one day hold texture and shadow pages, which have nothing to do with this dispatch.
    /// </remarks>
    public PageResidency? Residency { get; set; }

    /// <summary>How many meshes are registered.</summary>
    public int MeshCount => meshes.Count;

    /// <summary>How many clusters they have between them.</summary>
    public int ClusterCount => clusters.Count;

    /// <summary>How many pages, which is what the residency bitset has to cover.</summary>
    public int PageCount { get; private set; }

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

    /// <summary>Registers a mesh's DAG, and pins its root page.</summary>
    /// <param name="mesh">The DAG, from <c>MeshletBuilder</c>.</param>
    /// <param name="pages">Where its geometry is, from <c>MeshletPageBuilder</c>.</param>
    /// <param name="source">The id its pages will carry in a <see cref="PageKey" />.</param>
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
    public ClusterMesh Register(MeshletMesh mesh, MeshletPageSet pages, int source) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pages);
        ObjectDisposedException.ThrowIf(disposed, this);

        var scene = GpuClusterCulling.Flatten(mesh, pages);

        var entry = new ClusterMesh(
            source,
            clusters.Count,
            scene.Clusters.Length,
            roots.Count,
            scene.Roots.Length,
            PageCount,
            pages.Pages.Length
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

        PageCount += pages.Pages.Length;
        meshes.Add(entry);
        meshesDirty = true;

        if (pages.Pages.Length > 0) {
            Residency?.Pin(new(source, 0));
        }

        return entry;
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
        instances.Upload();
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
        ReadOnlySpan<float> errorThreshold
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
        UploadViews(frameViews, errorScale, errorThreshold);
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

        list.CopyBuffer(zeros, 0, visibleBuffer, 0, sizeof(uint));
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
        list.CopyBuffer(visibleBuffer, 0, requestReadbackBuffer, RequestBytes, sizeof(uint));

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
        VisibleOverflowed = false;

        if (Residency is null) {
            return 0;
        }

        if (requestReadbackBuffer.IsValid) {
            device.Read(requestReadbackBuffer, 0, MemoryMarshal.AsBytes(requestReadback.AsSpan()));

            var wanted = (int)requestReadback[0];
            RequestedPages = wanted;

            VisibleClusters = (int)requestReadback[^1];
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
            ]
        );

    static long RequestBytes => (long)(RequestCapacity + 1) * sizeof(uint);

    int VisibleLimit => Math.Max((int)(visibleCapacity / sizeof(uint)) - 1, 0);

    /// <summary>Uploads the meshes' records, if a registration changed them.</summary>
    /// <remarks>
    ///     A whole-buffer write on a dirty flag rather than anything incremental, and that is the
    ///     honest shape: these change when a mesh is registered, which happens at load. An incremental
    ///     path here would be machinery for a frame that never has any work for it.
    /// </remarks>
    void UploadMeshes() {
        if (!meshesDirty) {
            return;
        }

        device.Write(clusterBuffer, 0, MemoryMarshal.AsBytes<CullCluster>(clusters.ToArray()));
        device.Write(childBuffer, 0, MemoryMarshal.AsBytes<uint>(children.Count > 0 ? children.ToArray() : [0u]));
        device.Write(rootBuffer, 0, MemoryMarshal.AsBytes<uint>(roots.Count > 0 ? roots.ToArray() : [0u]));

        meshesDirty = false;
    }

    void UploadViews(
        IReadOnlyList<RenderView> frameViews,
        ReadOnlySpan<float> errorScale,
        ReadOnlySpan<float> errorThreshold
    ) {
        if (packedViews.Length < frameViews.Count) {
            packedViews = new CullView[Math.Max(frameViews.Count, 4)];
        }

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

            packedViews[i] = view;
        }

        projections.Remember(frameViews);

        views.Begin();
        views.Add(packedViews.AsSpan(0, frameViews.Count));
        views.Upload();
    }

    /// <summary>Turns the residency service's answer into the bitset the traversal reads.</summary>
    /// <remarks>
    ///     <para>
    ///         Rebuilt every frame rather than tracked, because it is one bit per page and a scene's
    ///         pages number in the thousands — the whole bitset is smaller than the bookkeeping that
    ///         would keep it incremental, and a stale bit here means a cluster drawn from a slot holding
    ///         another page's bytes.
    ///     </para>
    ///     <para>
    ///         <b>Absent when there is no service, rather than present.</b> A traversal that believed
    ///         every page was resident would draw the finest cut it could reach out of a pool with
    ///         nothing in it, which is the one failure this whole arrangement is shaped to make
    ///         impossible.
    ///     </para>
    /// </remarks>
    void UploadResidency() {
        var wordCount = Math.Max((PageCount + 31) / 32, 1);

        if (residency.Length < wordCount) {
            residency = new uint[wordCount];
        }

        Array.Clear(residency, 0, wordCount);

        if (Residency is not null) {
            foreach (var mesh in meshes) {
                for (var page = 0; page < mesh.PageCount; page++) {
                    if (!Residency.IsResident(new(mesh.Source, page))) {
                        continue;
                    }

                    var global = mesh.FirstPage + page;
                    residency[global / 32] |= 1u << (global % 32);

                    // Resident and about to be read is exactly what Touch means, and without it the
                    // pages a frame draws are the ones it evicts — a pool that thrashes hardest on the
                    // geometry nearest the camera.
                    Residency.Touch(new(mesh.Source, page));
                }
            }
        }

        residencyBits.Begin();
        residencyBits.Add(residency.AsSpan(0, wordCount));
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
            (long)Math.Max((PageCount + 31) / 32, 1) * sizeof(uint)
        );

        device.UpdateDescriptorSet(descriptors[ring], writes);
    }

    TextureViewHandle Occluding() =>
        Occluders is { IsBuilt: true } pyramid && pyramid.View.IsValid ? pyramid.View : placeholderView;

    bool EnsureBuffers() {
        var wanted = (long)(((long)InstanceCount * VisiblePerInstance) + 1) * sizeof(uint);

        if (clusterBuffer.IsValid && visibleCapacity >= wanted) {
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
                RequestBytes + sizeof(uint),
                BufferUsage.CopyDestination,
                MemoryAccess.HostReadback,
                "ClusterCulling.RequestReadback"
            )
        );

        zeros = device.CreateBuffer(
            new(sizeof(uint) * 4, BufferUsage.CopySource, MemoryAccess.HostUpload, "ClusterCulling.Zeros")
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
        requestReadback = new uint[RequestCapacity + 2];
        meshesDirty = true;
        visibleState = ResourceState.Undefined;
        requestState = ResourceState.Undefined;

        return Occluding().IsValid
            && clusterBuffer.IsValid
            && childBuffer.IsValid
            && rootBuffer.IsValid
            && visibleBuffer.IsValid
            && requestBuffer.IsValid
            && requestReadbackBuffer.IsValid
            && zeros.IsValid;
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
                 [clusterBuffer, childBuffer, rootBuffer, visibleBuffer, requestBuffer, requestReadbackBuffer, zeros]) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        clusterBuffer = default;
        childBuffer = default;
        rootBuffer = default;
        visibleBuffer = default;
        requestBuffer = default;
        requestReadbackBuffer = default;
        zeros = default;
        visibleCapacity = 0;
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

        meshes.Clear();
        clusters.Clear();
        children.Clear();
        roots.Clear();
    }

    sealed record PendingTraversal(PipelineHandle Pipeline, int InstanceCount, int ViewCount);
}
