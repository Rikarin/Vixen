// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.VirtualGeometry;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>Which registered DAG an object draws, and nothing about how.</summary>
/// <remarks>
///     The virtualized counterpart of <see cref="MeshDraw" />, and much smaller than one: a draw here
///     names no index range, no vertex offset and no instance count, because none of those are decided
///     until the traversal has chosen a cut. What an object contributes is a DAG and a place to put it.
/// </remarks>
public struct VirtualGeometryDraw {
    /// <summary>Which registration this object draws, or <c>-1</c> for an object that draws none.</summary>
    public int Mesh;

    /// <summary>Where it is, in world space.</summary>
    /// <remarks>
    ///     A position rather than a matrix, for <see cref="CullInstance" />'s reason: every test in the
    ///     traversal wants a world-space sphere and the factor an object-space length scales by, and a
    ///     rotation does not enter into either — the bound is a sphere.
    /// </remarks>
    public Vector3 Position;

    /// <summary>The largest scale factor any of its axes has.</summary>
    /// <remarks>
    ///     The largest and not the average, because it is what a bound and an error are both multiplied
    ///     by — and a bound that is too small culls geometry that is on screen, which is the failure
    ///     nobody can debug from a screenshot. An error that is too large refines sooner than it had to,
    ///     which costs a frame nothing anybody notices.
    /// </remarks>
    public float Scale;

    /// <summary>
    ///     Where this object's bone matrices start in the frame's palette, or zero if it has none.
    /// </summary>
    /// <remarks>
    ///     Written by <see cref="VirtualGeometryRenderFeature.SetBones" /> rather than by whoever fills
    ///     the draw, because it is an index into a buffer that is rebuilt every frame — a value a scene
    ///     cannot know and an extraction must not cache. Zero can mean "none" for
    ///     <see cref="GpuCulling.NoBones" />'s reason.
    /// </remarks>
    public int FirstBone;

    /// <summary>How far this object's pose can move its geometry, in object space.</summary>
    /// <seealso cref="CullInstance.MotionRadius" />
    public float MotionRadius;

    /// <summary>
    ///     Where this object's blend-shape weights start in the frame's weight buffer, or zero if it
    ///     has none.
    /// </summary>
    /// <remarks>
    ///     <see cref="FirstBone" />'s twin in every respect, including that zero means "none" — see
    ///     <see cref="GpuCulling.NoWeights" />. Written by
    ///     <see cref="VirtualGeometryRenderFeature.SetMorphWeights" /> rather than by whoever fills the
    ///     draw, because it indexes a buffer that is rebuilt every frame.
    /// </remarks>
    public int FirstWeight;

    /// <summary>How far this object's expression can move its geometry, in object space.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside <see cref="MotionRadius" /> and not folded into it</b>, because the two are
    ///     written by different callers on different frames — a face can be morphed and unskinned, and
    ///     a single field would have whichever of the two wrote last. They are added where the record
    ///     is packed, which is the one place that has both.
    /// </remarks>
    public float MorphRadius;

    /// <summary>Whether this object contributes an instance at all.</summary>
    public bool IsDrawable => Mesh >= 0;

    /// <summary>Whether it has a palette this frame.</summary>
    public bool IsSkinned => FirstBone > 0;

    /// <summary>Whether it has blend-shape weights this frame.</summary>
    public bool IsMorphed => FirstWeight > 0;
}

/// <summary>
///     Draws virtualized geometry: the scene half of <see cref="GpuClusterVisibility" />.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="MeshRenderFeature" />'s counterpart, and the same division of labour.</b> That
///         one owns a draw per object and lets <see cref="TransformRenderFeature" /> say where the object
///         is and <see cref="MaterialRenderFeature" /> say what it is drawn with. This one owns which DAG
///         an object draws and hands the traversal an instance record per frame; what the traversal
///         decides is a list of clusters, and what draws them is phase 4's raster.
///     </para>
///     <para>
///         <b>It draws nothing itself, and that is not a gap.</b> A root feature's <c>Draw</c> turns a
///         work list into commands, and there is no per-object command here to issue: the whole point of
///         the arrangement is that one indirect draw covers every cluster of every instance, and the host
///         never learns how many there are. So this feature extracts, prepares, and stops — see
///         <see cref="Compositor.ClusterCullingRenderer" /> for where the dispatch goes and
///         <c>docs/plan/22-virtualized-geometry.md</c> phase 4 for what consumes it.
///     </para>
///     <para>
///         <b>Registration is a load-time act and instancing is a frame-time one.</b>
///         <see cref="Register" /> is called once per mesh — it flattens the DAG, offsets its page
///         indices into the pool's global numbering and pins its root page — and
///         <see cref="Prepare" /> then only writes an instance record per object, incrementally, so a
///         scene of a hundred thousand virtualized objects of which one moved uploads one record.
///     </para>
/// </remarks>
public sealed class VirtualGeometryRenderFeature : RootRenderFeature {
    /// <summary>How many pixels of deviation a cut tolerates by default.</summary>
    /// <remarks>
    ///     One, which is the number the whole scheme is designed around: a cluster whose simplification
    ///     moves the surface by less than a pixel is a cluster whose refinement nobody can see. Nanite's
    ///     default and the same argument.
    /// </remarks>
    public const float DefaultErrorThreshold = 1f;

    /// <summary>What a view's software-raster threshold is unless a host measures otherwise.</summary>
    /// <remarks>
    ///     <b>Zero, meaning never.</b> <c>docs/plan/22-virtualized-geometry.md</c> phase 6 says the
    ///     software raster is worth turning on once profiling shows sub-pixel triangles dominating and
    ///     not before, so this ships off rather than at a guess: where the crossover between a compute
    ///     scanline raster and a quad-shading fixed-function one falls is a property of the hardware, and
    ///     a default that was wrong would be a frame that is slower for a reason nothing reports.
    /// </remarks>
    public const float DefaultSoftwareThreshold = 0f;

    /// <summary>The source id of each registration, by the index <see cref="Register" /> returned.</summary>
    /// <remarks>
    ///     ⚠ <b>Append-only, and a retired entry keeps its place.</b> The index is what a scene put in
    ///     <see cref="VirtualGeometryDraw.Mesh" />, and compacting this list would silently point every
    ///     object past the retired one at its neighbour's geometry. <c>-1</c> marks a retired slot.
    /// </remarks>
    readonly List<int> registered = [];

    // Kept beside the registrations rather than only handed to the traversal, because the *host* needs
    // them too: the steps a weight is multiplied by before upload, the names a clip binds by, and the
    // reach a bound is inflated by are all here and nowhere else.
    readonly List<MorphIndex?> morphs = [];

    int retired;

    float[] errorScale = [];
    float[] errorThreshold = [];
    float[] softwareThreshold = [];

    /// <inheritdoc />
    public override string Name => "VirtualGeometry";

    /// <summary>One draw per object.</summary>
    public RenderDataKey<VirtualGeometryDraw> Draws { get; private set; }

    /// <summary>The traversal this feature feeds. Null does nothing at all.</summary>
    /// <remarks>
    ///     Settable rather than created here, because it owns device buffers and a feature is created
    ///     before there is a device — the same reason <see cref="MeshRenderFeature.Pipelines" /> is
    ///     supplied rather than built.
    /// </remarks>
    public GpuClusterVisibility? Visibility { get; set; }

    /// <summary>Where page bytes are read from and put. Null draws whatever is already resident.</summary>
    public MeshletPagePool? Pages { get; set; }

    /// <summary>How many pixels of deviation a cut tolerates.</summary>
    /// <remarks>
    ///     One knob for every view, deliberately. A per-view threshold is a real thing to want — a
    ///     shadow cascade tolerates more than the camera does — but it is a property of the view and
    ///     belongs on <see cref="RenderView" /> when something needs it, not a dictionary here that
    ///     every caller would have to know to fill.
    /// </remarks>
    public float ErrorThreshold { get; set; } = DefaultErrorThreshold;

    /// <summary>
    ///     How wide a cluster may be on screen, in pixels, before it goes to the hardware raster.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Phase 6's routing, and <see cref="DefaultSoftwareThreshold" /> is why it is off. A cluster
    ///         holds a hundred and twenty-eight triangles, so a threshold of <c>t</c> routes clusters
    ///         whose triangles are roughly <c>t / 11</c> pixels a side — which is the regime where a
    ///         hardware rasterizer's quad granularity wastes most of what it launches.
    ///     </para>
    ///     <para>
    ///         One knob for every view, on <see cref="ErrorThreshold" />'s terms and for its reason. A
    ///         device without <see cref="GraphicsDeviceFeatures.HasInt64Atomics" /> ignores it entirely —
    ///         see <see cref="GpuClusterSoftwareRaster.Supported" />, and
    ///         <see cref="GpuClusterVisibility.SoftwareClusters" /> for what actually happened.
    ///     </para>
    /// </remarks>
    public float SoftwareThreshold { get; set; } = DefaultSoftwareThreshold;

    /// <summary>
    ///     How many pixels tall the output is, which is what turns a pixel budget into a distance.
    /// </summary>
    /// <remarks>
    ///     Here rather than on <see cref="RenderView" />, because a view carries the resolution-independent
    ///     half of the projection — see <see cref="RenderView.ScreenHeightScale" /> — and the pixels are a
    ///     property of the target every view of the frame renders into. Whoever owns the output sets it;
    ///     leaving it at zero makes every error project to zero, which draws every object at its coarsest
    ///     level rather than at none.
    /// </remarks>
    public int ScreenHeight { get; set; }

    /// <summary>How many page loads may be started per frame.</summary>
    /// <remarks>
    ///     A ceiling on I/O rather than on the queue, which is <see cref="PageResidency.Service" />'s
    ///     distinction: a camera that turns to face a city asks for everything at once, and issuing all
    ///     of it spends the frame's bandwidth on pages the next frame will not want either.
    /// </remarks>
    public int LoadsPerFrame { get; set; } = 8;

    /// <summary>How many pages the last frame's traversal wanted and did not have.</summary>
    /// <remarks>
    ///     Zero in a steady state, and a positive number every frame is a budget too small for the scene
    ///     — which draws something coarser rather than something broken, and is still worth being able to
    ///     see. <see cref="PageResidency.Rejections" /> is the sharper signal; this one says the loop is
    ///     running at all.
    /// </remarks>
    public int RequestedPages { get; private set; }

    /// <summary>How many meshes are registered and drawable.</summary>
    /// <remarks>
    ///     Live registrations rather than every registration ever made — a retired one is a slot that
    ///     keeps its number and draws nothing, so counting it would make a level unload look like it did
    ///     nothing at all.
    /// </remarks>
    public int RegisteredMeshes => registered.Count - retired;

    /// <summary>
    ///     Registers a mesh so objects can draw it, and makes its geometry reachable.
    /// </summary>
    /// <param name="mesh">The DAG, as the build wrote it.</param>
    /// <param name="pages">Its page records, with or without their data.</param>
    /// <param name="source">The id the pool knows its page blob by.</param>
    /// <param name="morphIndex">
    ///     Its blend shapes re-indexed by vertex, or null for a mesh with none — see
    ///     <see cref="MorphIndex" /> for why the paged path gathers rather than scatters.
    /// </param>
    /// <returns>The index to put in <see cref="VirtualGeometryDraw.Mesh" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">There is no traversal to register it with.</exception>
    /// <remarks>
    ///     The source id is the caller's rather than derived, because it has to agree with whatever
    ///     <see cref="IMeshletPageSource" /> was handed the same mesh's blob — and only the caller that
    ///     did both knows.
    /// </remarks>
    public int Register(MeshletMesh mesh, MeshletPageSet pages, int source, MorphIndex? morphIndex = null) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pages);

        if (Visibility is null) {
            throw new InvalidOperationException(
                "Set Visibility before registering a mesh — the flattened records live in it, and a "
                + "registration that went nowhere would be an object that silently draws nothing."
            );
        }

        Visibility.Register(mesh, pages, source, morphIndex);
        registered.Add(source);
        morphs.Add(morphIndex);

        return registered.Count - 1;
    }

    /// <summary>
    ///     Retires a mesh, so its pages go back to the pool and objects drawing it draw nothing.
    /// </summary>
    /// <param name="mesh">The index <see cref="Register" /> returned.</param>
    /// <returns>The source id it was registered under, or <c>-1</c> if it was already retired.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such registration.</exception>
    /// <exception cref="InvalidOperationException">There is no traversal holding it.</exception>
    /// <remarks>
    ///     The counterpart <see cref="Register" /> never had, and the reason a level unload leaked: a
    ///     registration pins a root page, a pinned page is never evicted, and nothing here ever said a
    ///     mesh had gone. The source id comes back because it is what the page blob is filed under and
    ///     the caller is what has to close it — see <c>VirtualGeometrySystem.Release</c>, which does
    ///     both halves so they cannot be done singly.
    /// </remarks>
    public int Unregister(int mesh) {
        ArgumentOutOfRangeException.ThrowIfNegative(mesh);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mesh, registered.Count);

        if (Visibility is null) {
            throw new InvalidOperationException(
                "Set Visibility before retiring a mesh — the records live in it, and a retirement that "
                + "went nowhere would be a pinned page nothing ever gives back."
            );
        }

        var source = registered[mesh];

        if (source < 0) {
            return -1;
        }

        Visibility.Unregister(mesh);
        registered[mesh] = -1;

        // ⚠ Dropped with the registration. The traversal retires a mesh in place and keeps its slot, so
        // an index stays valid for ever — and a table left behind would be the shapes of a mesh that is
        // gone, handed to whatever draws next under the same index.
        morphs[mesh] = null;
        retired++;

        return source;
    }

    /// <summary>Starts a frame's bone palettes. Call before the first <see cref="SetBones" />.</summary>
    /// <exception cref="InvalidOperationException">There is no traversal to hold them.</exception>
    /// <remarks>
    ///     Explicit and not folded into <see cref="Prepare" />, for
    ///     <see cref="SkinningRenderFeature.Begin" />'s reason: what fills a palette is the animation
    ///     system, and it runs before the render system's phases do.
    /// </remarks>
    public void BeginBones() {
        if (Visibility is null) {
            throw new InvalidOperationException(
                "Set Visibility before beginning a frame's palettes — the buffer they go in lives in it."
            );
        }

        Visibility.BeginBones();
    }

    /// <summary>Gives an object its pose for this frame.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="palette">
    ///     Its skinning matrices — <c>inverseBindPose * boneWorld</c>, one per bone, in the order the
    ///     mesh's page vertices index them. Empty makes the object unskinned again.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> is null.</exception>
    /// <exception cref="InvalidOperationException">There is no traversal to hold the palette.</exception>
    /// <remarks>
    ///     <para>
    ///         <see cref="SkinningRenderFeature.SetBones" />'s counterpart, taking the same matrices in
    ///         the same order — a host that skins a mesh on both paths hands the same array to both.
    ///         They are two buffers rather than one, which is a pose duplicated for a mesh drawn both
    ///         ways; the alternative is a feature reaching into another feature's ring, and the two
    ///         features are optional independently.
    ///     </para>
    ///     <para>
    ///         The motion radius is computed here rather than asked for, because everything it needs is
    ///         here: the pose, and the bind-pose bound the registration recorded. A host that knows
    ///         better can overwrite <see cref="VirtualGeometryDraw.MotionRadius" /> afterwards.
    ///     </para>
    /// </remarks>
    public void SetBones(RenderSystem system, RenderObjectId id, ReadOnlySpan<Matrix4x4> palette) {
        ArgumentNullException.ThrowIfNull(system);

        if (Visibility is null) {
            throw new InvalidOperationException(
                "Set Visibility before giving an object a palette — the buffer it goes in lives in it."
            );
        }

        ref var draw = ref system.Objects.Data.Data(Draws)[id.Index];

        if (palette.IsEmpty) {
            draw.FirstBone = 0;
            draw.MotionRadius = 0f;

            return;
        }

        draw.FirstBone = Visibility.AddBones(palette);

        draw.MotionRadius = draw.IsDrawable && draw.Mesh < Visibility.MeshCount
            ? GpuClusterCulling.MotionRadiusFor(palette, Visibility.MeshAt(draw.Mesh).Center, Visibility.MeshAt(draw.Mesh).Radius)
            : 0f;
    }

    /// <summary>Starts a frame's blend-shape weights. Call before the first <see cref="SetMorphWeights" />.</summary>
    /// <exception cref="InvalidOperationException">There is no traversal to hold them.</exception>
    /// <remarks>
    ///     <see cref="BeginBones" />'s twin and explicit for its reason: what fills the weights is an
    ///     animation system, and it runs before the render system's phases do.
    /// </remarks>
    public void BeginMorphs() {
        if (Visibility is null) {
            throw new InvalidOperationException(
                "Set Visibility before beginning a frame's weights — the buffer they go in lives in it."
            );
        }

        Visibility.BeginMorphs();
    }

    /// <summary>Gives an object its expression for this frame.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="weights">
    ///     One per <c>MeshData.MorphTargets</c> entry, in that order. Shorter is read as zero for the
    ///     rest and empty returns the object to rest, which is <c>MorphRenderFeature.SetWeights</c>'
    ///     rule and has to be the same one.
    /// </param>
    /// <returns>Whether the object's mesh has blend shapes at all.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> is null.</exception>
    /// <exception cref="InvalidOperationException">There is no traversal to hold them.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The steps are folded in here.</b> What reaches the device is
    ///         <c>(weight x positionStep, weight x normalStep)</c> per shape, so the shader multiplies
    ///         once — <c>MorphKernel.Step</c>'s argument, that one division on the host is one float
    ///         both processors then agree about exactly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An all-zero expression still takes a run.</b> It costs one entry per shape and it
    ///         keeps the branch a property of the mesh rather than of the frame — an instance that
    ///         dropped its run when every weight reached zero would flicker between two code paths,
    ///         and the two are only equal if this one is right.
    ///     </para>
    /// </remarks>
    public bool SetMorphWeights(RenderSystem system, RenderObjectId id, ReadOnlySpan<float> weights) {
        ArgumentNullException.ThrowIfNull(system);

        if (Visibility is null) {
            throw new InvalidOperationException(
                "Set Visibility before giving an object weights — the buffer they go in lives in it."
            );
        }

        ref var draw = ref system.Objects.Data.Data(Draws)[id.Index];

        if (!draw.IsDrawable || draw.Mesh >= morphs.Count || morphs[draw.Mesh] is not { } index) {
            draw.FirstWeight = 0;
            draw.MorphRadius = 0f;

            return false;
        }

        var scaled = new Vector2[index.ShapeCount];

        for (var shape = 0; shape < scaled.Length; shape++) {
            var weight = shape < weights.Length ? weights[shape] : 0f;
            scaled[shape] = new(weight * index.PositionSteps[shape], weight * index.NormalSteps[shape]);
        }

        draw.FirstWeight = Visibility.AddMorphWeights(scaled);
        draw.MorphRadius = index.Radius(weights);

        return true;
    }

    /// <summary>What an object's mesh calls each of its weight slots.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <returns>The shape names, in slot order, or empty when its mesh has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b><c>MorphRenderFeature.ShapesOf</c>'s counterpart, and it exists for the same reason.</b>
    ///     A clip names a shape, <c>BlendShapeWeights</c> is addressed by slot, and the ordinal a
    ///     source file used is not the ordinal <c>MeshData.MorphTargets</c> ended up with. Something on
    ///     each path has to have seen both ends; on this one it is the registration.
    /// </remarks>
    public ReadOnlySpan<string> ShapesOf(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);

        var draw = system.Objects.Data.Data(Draws)[id.Index];

        return draw.IsDrawable && draw.Mesh < morphs.Count && morphs[draw.Mesh] is { } index
            ? index.Names
            : [];
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);
        Draws = system.Objects.Data.Register<VirtualGeometryDraw>();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>The requests are serviced first, before anything this frame is decided.</b> What comes
    ///         back is the previous frame's answer — the dispatch that wrote it has been submitted and
    ///         not waited for — so servicing it here is what makes a page asked for in one frame resident
    ///         for a later one. See <see cref="GpuClusterVisibility.ServiceRequests" /> for why being a
    ///         frame late costs a coarser cut rather than a hole.
    ///     </para>
    ///     <para>
    ///         <b>An instance per object slot, not per drawable object.</b> The records are indexed the
    ///         way every other per-object buffer is, so a slot that draws nothing is a dead instance
    ///         rather than a hole in the numbering — which is what lets the incremental upload compare a
    ///         slot against what the device already holds. An object that stopped being drawable writes a
    ///         record with no <c>Alive</c> bit, and the traversal returns from its workgroup immediately.
    ///     </para>
    /// </remarks>
    protected internal override void Prepare(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        if (Visibility is null) {
            return;
        }

        RequestedPages = Visibility.RequestedPages;
        Visibility.ServiceRequests(LoadsPerFrame);

        var count = system.Objects.Count;
        var draws = system.Objects.Data.Data(Draws);
        var all = system.Objects.All;

        Visibility.Begin(count);

        for (var index = 0; index < count && index < draws.Length; index++) {
            Visibility.Set(index, Pack(Visibility, draws[index], all[index]));
        }

        Reserve(system.Views.Count);

        for (var i = 0; i < system.Views.Count; i++) {
            var view = system.Views[i];

            // The scale, not the threshold, is what the view contributes: a deviation of e object-space
            // units at distance d covers e * scale / d pixels. A view whose ScreenHeightScale is zero —
            // a shadow cascade, a probe face — gets a scale of zero and therefore accepts every cluster
            // at its root, which is the same opt-out LodRenderFeature reads from the same field. Phase 7
            // is what makes a shadow view ask this question properly.
            errorScale[i] = GpuClusterCulling.ErrorScaleFor(view.ScreenHeightScale, ScreenHeight);
            errorThreshold[i] = ErrorThreshold;
            softwareThreshold[i] = SoftwareThreshold;
        }

        // ⚠ Discarded here and answered elsewhere, which is worth saying because the discard used to be
        // the whole of it. False is ordinary — the culling variant still compiling is what the first
        // frames after a shader-cache miss look like — and it means no buffers were made, while
        // MeshCount goes on counting registrations. So the answer this drops is kept on the object as
        // GpuClusterVisibility.Visible, and every consumer of the traversal asks that handle before
        // binding anything of the traversal's: see GpuClusterRaster.Ready, GpuVisibilityTiles.Record and
        // GpuClusterResolve.Prepare. Nothing useful is left for this method to do with it — the frame's
        // virtualized objects are absent until the variant lands, and there is no fallback to take
        // because MeshExtractionSystem sent them down this path instead of the ordinary one.
        _ = Visibility.Prepare(
            system.Views,
            errorScale.AsSpan(0, system.Views.Count),
            errorThreshold.AsSpan(0, system.Views.Count),
            softwareThreshold.AsSpan(0, system.Views.Count)
        );
    }

    /// <summary>One object as the traversal reads it.</summary>
    /// <remarks>
    ///     The stage mask and the liveness come from the <see cref="RenderObject" /> rather than from the
    ///     draw, which is what makes <c>Hide</c> and a stage filter work here exactly as they work for the
    ///     object cull — the traversal tests the same two things first and in the same order.
    /// </remarks>
    static CullInstance Pack(GpuClusterVisibility visibility, in VirtualGeometryDraw draw, in RenderObject candidate) {
        if (!draw.IsDrawable || draw.Mesh >= visibility.MeshCount) {
            return default;
        }

        var mesh = visibility.MeshAt(draw.Mesh);

        return new() {
            FirstCluster = (uint)mesh.FirstCluster,
            ClusterCount = (uint)mesh.ClusterCount,
            FirstRoot = (uint)mesh.FirstRoot,
            RootCount = (uint)mesh.RootCount,
            Position = draw.Position,
            Scale = draw.Scale,
            StagesLow = (uint)candidate.Stages.Bits,
            StagesHigh = (uint)(candidate.Stages.Bits >> 32),
            Flags = candidate.IsAlive ? GpuCulling.Alive : 0u,
            Mesh = (uint)draw.Mesh,
            FirstBone = draw.IsSkinned ? (uint)draw.FirstBone : GpuCulling.NoBones,
            FirstWeight = draw.IsMorphed ? (uint)draw.FirstWeight : GpuCulling.NoWeights,

            // ⚠ The two inflations added, and both guarded by their own flag. Every bound in the DAG is
            // a rest-pose bound, so a traversal that tested them as they stand culls a dropped jaw by
            // where it is not — and a cluster that is not drawn does not say so anywhere.
            MotionRadius = (draw.IsSkinned ? draw.MotionRadius : 0f)
                + (draw.IsMorphed ? draw.MorphRadius : 0f)
        };
    }

    void Reserve(int viewCount) {
        if (errorScale.Length >= viewCount) {
            return;
        }

        Array.Resize(ref errorScale, Math.Max(viewCount, 4));
        Array.Resize(ref errorThreshold, Math.Max(viewCount, 4));
        Array.Resize(ref softwareThreshold, Math.Max(viewCount, 4));
    }
}
