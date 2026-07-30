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

    /// <summary>Whether this object contributes an instance at all.</summary>
    public bool IsDrawable => Mesh >= 0;

    /// <summary>Whether it has a palette this frame.</summary>
    public bool IsSkinned => FirstBone > 0;
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

    readonly List<int> registered = [];

    float[] errorScale = [];
    float[] errorThreshold = [];

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

    /// <summary>How many meshes have been registered.</summary>
    public int RegisteredMeshes => registered.Count;

    /// <summary>
    ///     Registers a mesh so objects can draw it, and makes its geometry reachable.
    /// </summary>
    /// <param name="mesh">The DAG, as the build wrote it.</param>
    /// <param name="pages">Its page records, with or without their data.</param>
    /// <param name="source">The id the pool knows its page blob by.</param>
    /// <returns>The index to put in <see cref="VirtualGeometryDraw.Mesh" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">There is no traversal to register it with.</exception>
    /// <remarks>
    ///     The source id is the caller's rather than derived, because it has to agree with whatever
    ///     <see cref="IMeshletPageSource" /> was handed the same mesh's blob — and only the caller that
    ///     did both knows.
    /// </remarks>
    public int Register(MeshletMesh mesh, MeshletPageSet pages, int source) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pages);

        if (Visibility is null) {
            throw new InvalidOperationException(
                "Set Visibility before registering a mesh — the flattened records live in it, and a "
                + "registration that went nowhere would be an object that silently draws nothing."
            );
        }

        Visibility.Register(mesh, pages, source);
        registered.Add(source);

        return registered.Count - 1;
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
        }

        Visibility.Prepare(system.Views, errorScale.AsSpan(0, system.Views.Count), errorThreshold.AsSpan(0, system.Views.Count));
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
            MotionRadius = draw.IsSkinned ? draw.MotionRadius : 0f
        };
    }

    void Reserve(int viewCount) {
        if (errorScale.Length >= viewCount) {
            return;
        }

        Array.Resize(ref errorScale, Math.Max(viewCount, 4));
        Array.Resize(ref errorThreshold, Math.Max(viewCount, 4));
    }
}
