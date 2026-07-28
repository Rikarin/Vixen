// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     The same visibility bitset, decided by a compute dispatch instead of by the job system.
/// </summary>
/// <remarks>
///     <para>
///         [docs/plan/06] asks for "object bounds uploaded once, frustum ... culling in compute" —
///         this is the host half of that, and <c>Raven/Library/Pipeline/Culling.rvn</c> is the other.
///         It answers exactly what <see cref="VisibilityGroup" /> answers, in exactly the same bits,
///         which is what makes the two interchangeable at
///         <see cref="RenderSystem.Visibility" /> rather than merely comparable.
///     </para>
///     <para>
///         <strong>It is the CPU group plus a front end, not a reimplementation of it.</strong>
///         Everything that reads an answer — <see cref="Words" />, <see cref="Hide" />,
///         <see cref="IsVisible" /> — is the composed group's, and the dispatch's readback is written
///         into that group's storage. So there is one bitset implementation in the engine and one
///         place a consumer's assumptions can be wrong, and the fallback below costs nothing to
///         provide.
///     </para>
///     <para>
///         <strong>It falls back rather than failing.</strong> No effect system, no pipeline cache, a
///         variant that has not compiled yet, a provider that does not report layouts: each of those
///         is a frame that still has to be drawn, and each of them culls on the CPU instead.
///         <see cref="CulledOnDevice" /> says which happened. This is also what a GL or WebGL target
///         gets, which is what doc 06 means by "the CPU path remains".
///     </para>
///     <para>
///         <strong>The readback is a stall, and choosing to pay it is the design.</strong> The
///         interface promises that the bits are this frame's when <see cref="Cull" /> returns, and
///         sorting, preparation and recording all read them within the same frame — so the
///         alternative is not "no wait" but "an answer one frame old", which is geometry popping at
///         the edges of a moving camera and nothing anywhere to say why. What makes it affordable is
///         the queue it waits on: the dispatch goes to <see cref="IGraphicsDevice.ComputeQueue" />,
///         so on hardware with a real async compute queue the wait does not drain the frame being
///         rendered. The RHI has no fence, which is why the wait is queue-wide rather than on this
///         submission alone.
///     </para>
///     <para>
///         <strong>And the readback is optional.</strong> With <see cref="ReadBack" /> off there is
///         no wait at all: the dispatch goes into the frame's own list through
///         <see cref="Compositor.GpuCullingRenderer" />, <see cref="GpuDrawArguments" /> turns the
///         bits into draw calls without them ever leaving the device, and what the host is handed is
///         the conservative set — everything that could be visible — for the work list to be built
///         from. That is the endpoint doc 06 describes, and what it trades for the stall is
///         recording draws that turn out to be empty.
///     </para>
/// </remarks>
public sealed class GpuVisibilityGroup : IVisibilityGroup {
    /// <summary>What the answer is written into, and what answers every read of it.</summary>
    readonly VisibilityGroup results = new();

    readonly IGraphicsDevice device;
    readonly UploadBuffer<CullObject> objects = new("Culling.Objects");
    readonly UploadBuffer<CullView> views = new("Culling.Views");
    readonly DescriptorWrite[] writes = new DescriptorWrite[4];

    CullObject[] packedObjects = [];
    CullView[] packedViews = [];
    uint[] words = [];

    // Each view's matrix from the frame the pyramid was built in, which is the only matrix its
    // rectangle may be projected with. Indexed by view index, and dropped whole whenever the number
    // of views changes: index 2 being "the second cascade" is a convention of the host's, and a
    // frame that added a view has renumbered everything after it.
    Matrix4x4[] previous = [];
    bool[] projectable = [];
    int remembered = -1;

    BufferHandle visibility;
    BufferHandle readback;
    long capacity;

    // What the visibility buffer was left in, so a barrier can name a truthful `before`. The copy at
    // the end of a cull leaves it in CopySource, which is what the next frame's first barrier says.
    ResourceState state = ResourceState.Undefined;

    // One set per frame in flight, rotated per cull. With the readback there is a wait to hide
    // behind and one would do; without it the previous frame's dispatch may still be reading the set
    // this frame is rewriting, which is the race DescriptorAllocator exists for.
    DescriptorSetHandle[] descriptors = [];
    int ring;

    DescriptorSetSlot slot;
    Effect? allocatedFor;
    PendingCull? pending;
    bool disposed;

    /// <summary>Creates a group that culls on a device.</summary>
    /// <param name="device">The device the dispatch runs on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public GpuVisibilityGroup(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        objects.Device = device;
        views.Device = device;
    }

    /// <summary>Where the culling variant is resolved from. Null culls on the CPU.</summary>
    /// <remarks>
    ///     The ordinary <see cref="EffectSystem" />, so the culling shader is permuted, cached and
    ///     baked exactly like every other — and a shipping build cannot compile it for the same
    ///     structural reason it cannot compile a vertex shader.
    /// </remarks>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null culls on the CPU.</summary>
    /// <remarks>
    ///     Shared rather than owned, and for the reason <see cref="Compositor.ComputeRenderer" />
    ///     shares one: a pipeline is created once per effect and lives as long as the device, so a
    ///     renderer holding a private cache would compile the same module a second time.
    /// </remarks>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>
    ///     Last frame's depth, min-reduced, for the occlusion half of the pass. Null tests the
    ///     frustum alone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The pyramid is built by <see cref="Compositor.HiZRenderer" /> at the end of a frame
    ///         and read at the start of the next, which is the only depth a pass that runs before
    ///         anything is drawn can have. Setting this selects the <c>Occlusion</c> variant of the
    ///         shader; leaving it null resolves one that declares no texture at all.
    ///     </para>
    ///     <para>
    ///         A view is only tested against it when this group saw that view's matrix in the frame
    ///         the pyramid was built in — so the first frame, and any frame after the view list
    ///         changed shape, is frustum-only rather than wrong.
    ///     </para>
    /// </remarks>
    public HiZPyramid? Occluders { get; set; }

    /// <summary>Whether the last <see cref="Cull" /> tested against a depth pyramid as well.</summary>
    /// <remarks>
    ///     Distinct from <see cref="CulledOnDevice" /> because the occlusion half turns itself off
    ///     for reasons the frustum half does not have — a pyramid that has never been built, a frame
    ///     whose view list changed — and a scene that is quietly not being occlusion culled looks
    ///     exactly like one where occlusion culling is not helping.
    /// </remarks>
    public bool OcclusionTested { get; private set; }

    /// <summary>
    ///     Whether the bits come back to the host, which is what the interface's promise costs.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         True by default, and then this group is what its class remarks describe: a dispatch,
    ///         a wait and a copy, after which <see cref="Words" /> is exactly what is visible and
    ///         everything downstream is unchanged.
    ///     </para>
    ///     <para>
    ///         <strong>False removes the stall and moves the decision into the draw call.</strong>
    ///         Nothing is submitted or waited on: the dispatch is left for
    ///         <see cref="Record" /> to put in the frame's own list, and the host bitset is filled
    ///         with every live object the view's stages want — every object that <em>could</em> be
    ///         visible rather than every object that is. The work list is then a superset, and what
    ///         removes the rest is <see cref="GpuDrawArguments" />, which zeroes the instance count of
    ///         everything the dispatch culled. Nothing is drawn that should not be; what costs is the
    ///         recording of draws that turn out to be empty.
    ///     </para>
    ///     <para>
    ///         So with this off, <see cref="IsVisible" /> and <see cref="VisibleCount" /> answer
    ///         "could be seen" rather than "is seen" — which is the one place this group stops being
    ///         interchangeable with the CPU one, and the reason it is opt-in. A feature that narrows
    ///         the set with <see cref="Hide" /> still works, because that clears a bit on the host and
    ///         a host-cleared bit removes the object from the work list entirely.
    ///     </para>
    /// </remarks>
    public bool ReadBack { get; set; } = true;

    /// <summary>Whether the last <see cref="Cull" /> ran on the device rather than on the CPU.</summary>
    /// <remarks>
    ///     Worth exposing rather than inferring: "the GPU path is on" and "the GPU path ran" differ
    ///     by a shader variant that has not compiled yet, and a frame budget that silently measures
    ///     the fallback is a measurement of the wrong thing.
    /// </remarks>
    public bool CulledOnDevice { get; private set; }

    /// <summary>
    ///     The device-side bits, as <see cref="GpuCulling.WordSize" />-object words, view-major.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Invalid until the first dispatch. Exposed because it is what a pass that does not need
    ///         the host's copy would consume — a compaction producing indirect draw arguments is the
    ///         whole point of computing them here, and it can read this without the round trip
    ///         <see cref="Cull" /> pays for the CPU's benefit.
    ///     </para>
    ///     <para>
    ///         <strong>It is the frustum's answer, not the frame's.</strong> <see cref="Hide" /> — LOD
    ///         selection, and anything else that narrows the set afterwards — clears a bit in the
    ///         host's copy and nothing here, because sending it back would be a second round trip in
    ///         the direction this exists to avoid. Whatever eventually draws from this has to apply
    ///         those refinements on the device, which is where they will belong by then.
    ///     </para>
    /// </remarks>
    public BufferHandle Bits => visibility;

    /// <inheritdoc />
    public int ViewCount => results.ViewCount;

    /// <inheritdoc />
    public bool IsVisible(int viewIndex, RenderObjectId id) => results.IsVisible(viewIndex, id);

    /// <inheritdoc />
    public ReadOnlySpan<ulong> Words(int viewIndex) => results.Words(viewIndex);

    /// <inheritdoc />
    public void Hide(int viewIndex, RenderObjectId id) => results.Hide(viewIndex, id);

    /// <inheritdoc />
    public int VisibleCount(int viewIndex) => results.VisibleCount(viewIndex);

    /// <inheritdoc cref="IVisibilityGroup.Cull" />
    /// <remarks>
    ///     One dispatch for the whole frame: <c>x</c> covers a view's words and <c>y</c> is the view,
    ///     so a scene with four shadow cascades is not four submissions. It is also why the per-view
    ///     numbers travel in the view record rather than in a uniform block — see
    ///     <see cref="CullView" />.
    /// </remarks>
    public void Cull(RenderObjectStore store, IReadOnlyList<RenderView> views, JobScheduler? scheduler = null) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(views);
        ObjectDisposedException.ThrowIf(disposed, this);

        OcclusionTested = false;
        CulledOnDevice = TryCullOnDevice(store, views);

        if (!CulledOnDevice) {
            results.Cull(store, views, scheduler);
        }

        // Both paths, because the pyramid is rebuilt by the frame rather than by this: a frame that
        // fell back to the CPU still drew, so its matrices are the ones the depth it left behind was
        // drawn with. Remembering only on the device path would test one frame's rectangles against
        // another frame's pixels the moment a single frame fell back.
        Remember(views);
    }

    /// <summary>Runs the dispatch and reads it back, or answers false having done nothing.</summary>
    bool TryCullOnDevice(RenderObjectStore store, IReadOnlyList<RenderView> frameViews) {
        if (store.Count == 0 || frameViews.Count == 0 || Effects is null || Pipelines is null) {
            return false;
        }

        var occlusion = Occluders is { IsBuilt: true, Levels: > 0 } pyramid
            && pyramid.View.IsValid
            && remembered == frameViews.Count;

        // A placeholder is a variant that has not compiled yet, and culling with it would produce an
        // empty frame rather than a differently-shaded one — the failure a placeholder exists to
        // avoid. The CPU answers this frame and the device answers the next.
        if (Effects.Resolve(Key(occlusion)) is not { IsPlaceholder: false } effect) {
            return false;
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid
            || effect.BindingOf("objects") is not { } objectBinding
            || effect.BindingOf("views") is not { } viewBinding
            || effect.BindingOf("visibility") is not { } outputBinding) {
            return false;
        }

        // The variant that tests occlusion is the one that declares the texture, so a variant
        // without the binding is one that will not read it — which is the permutation doing its job
        // rather than something to fail on. The reverse is not survivable: a set with a binding
        // nothing filled is a validation error on one backend and a sampled nothing on another, so a
        // variant asking for a pyramid this group cannot supply is a frame the CPU takes.
        var occluderBinding = effect.BindingOf("occluders");

        if (occluderBinding is not null && Occluders?.View.IsValid != true) {
            return false;
        }

        occlusion &= occluderBinding is not null;

        if (objectBinding.Set != viewBinding.Set
            || objectBinding.Set != outputBinding.Set
            || (occluderBinding is { } bound && bound.Set != objectBinding.Set)) {
            throw new InvalidOperationException(
                $"'{GpuCulling.ShaderName}' declares its bindings across more than one descriptor "
                + "set, and one dispatch binds one set. This is a change to the shader rather than "
                + "something a frame can work around."
            );
        }

        if (!TryAllocateSet(effect, objectBinding.Set)) {
            return false;
        }

        var objectCount = store.Count;
        var wordCount = GpuCulling.WordsFor(objectCount);
        var bytes = GpuCulling.BufferSize(frameViews.Count, wordCount);

        if (!EnsureVisibility(bytes)) {
            return false;
        }

        var tested = Upload(store, frameViews, objectCount, wordCount, occlusion);

        ring = descriptors.Length == 0 ? 0 : (ring + 1) % descriptors.Length;
        Bind(objectBinding, viewBinding, outputBinding, occluderBinding, objectCount, frameViews.Count, bytes);

        if (ReadBack) {
            Dispatch(pipeline, wordCount, frameViews.Count, bytes);
            Read(frameViews.Count, objectCount, wordCount);
        } else {
            pending = new(pipeline, wordCount, frameViews.Count);
            Conservative(store, frameViews, objectCount);
        }

        OcclusionTested = tested;
        return true;
    }

    /// <summary>
    ///     Fills the host bitset with everything that could be visible, for a cull that is not coming
    ///     back.
    /// </summary>
    /// <remarks>
    ///     The two rejections a work list needs whatever the device decides: a dead slot has no
    ///     feature to draw it, and an object in no stage this view draws would be collected into a
    ///     list it does not belong in. What is deliberately <em>not</em> here is the frustum test —
    ///     that is the work being moved to the device, and doing it here as well would be doing it
    ///     twice.
    /// </remarks>
    void Conservative(RenderObjectStore store, IReadOnlyList<RenderView> frameViews, int objectCount) {
        results.Reset(frameViews.Count, objectCount);

        var all = store.All;

        for (var view = 0; view < frameViews.Count; view++) {
            var stages = frameViews[view].Stages;
            var bits = results.Fill(view);

            for (var i = 0; i < objectCount; i++) {
                ref readonly var candidate = ref all[i];

                if (candidate.IsAlive && candidate.Stages.Intersects(stages)) {
                    bits[i >> 6] |= 1UL << (i & 63);
                }
            }
        }
    }

    /// <summary>The key for the variant this frame wants.</summary>
    /// <remarks>
    ///     The permutation is named either way rather than left to its default when it is off, so
    ///     that the two frames either side of a pyramid appearing ask for two keys that differ in
    ///     their value rather than in their <em>shape</em> — a bundle records what a run asked for,
    ///     and "Culling with no permutations" is a third variant nobody wants baked.
    /// </remarks>
    static EffectKey Key(bool occlusion) =>
        EffectKey.Of(GpuCulling.ShaderName, [new(GpuCulling.OcclusionKey, occlusion ? "true" : "false")]);

    /// <summary>Records the matrices the next frame's occlusion test will project with.</summary>
    void Remember(IReadOnlyList<RenderView> frameViews) {
        if (previous.Length < frameViews.Count) {
            Array.Resize(ref previous, frameViews.Count);
            Array.Resize(ref projectable, frameViews.Count);
        }

        for (var i = 0; i < frameViews.Count; i++) {
            previous[i] = frameViews[i].ViewProjection;

            // A view whose matrix was never set — a caller that supplied a frustum and nothing else
            // — leaves identity behind, which projects a scene into a rectangle that means nothing.
            // Better to skip occlusion for it than to occlude by arithmetic about nowhere.
            projectable[i] = previous[i] != Matrix4x4.Identity;
        }

        remembered = frameViews.Count;
    }

    /// <summary>Packs this frame's objects and views and writes them to their upload rings.</summary>
    /// <returns>Whether any view ended up with a pyramid to test against.</returns>
    bool Upload(
        RenderObjectStore store,
        IReadOnlyList<RenderView> frameViews,
        int objectCount,
        int wordCount,
        bool occlusion
    ) {
        Reserve(ref packedObjects, objectCount);
        Reserve(ref packedViews, frameViews.Count);

        var all = store.All;
        var levels = Occluders?.Levels ?? 0;
        var any = false;

        for (var i = 0; i < objectCount; i++) {
            packedObjects[i] = GpuCulling.Pack(all[i]);
        }

        for (var i = 0; i < frameViews.Count; i++) {
            // Occlusion per view rather than per dispatch: only a view this group saw in the frame
            // the pyramid was built in has a matrix its rectangle may be projected with.
            var tested = occlusion && levels > 0 && i < projectable.Length && projectable[i];
            any |= tested;

            // The counts are the same for every view and are carried per view anyway, because that is
            // what lets one dispatch cover all of them without a uniform block to rewrite per view.
            packedViews[i] = GpuCulling.Pack(
                frameViews[i],
                objectCount,
                wordCount,
                tested ? previous[i] : default,
                tested ? levels : 0
            );
        }

        objects.Begin();
        objects.Add(packedObjects.AsSpan(0, objectCount));
        objects.Upload();

        views.Begin();
        views.Add(packedViews.AsSpan(0, frameViews.Count));
        views.Upload();

        return any;
    }

    /// <summary>Points the set at this frame's regions of the two rings, and at the output.</summary>
    /// <remarks>
    ///     Rewritten every frame because the ring moved, which is safe here for a reason that would
    ///     not hold elsewhere: <see cref="Dispatch" /> waits, so no submission of this group's is ever
    ///     in flight while the set is being written.
    ///     <para>
    ///         The sizes are the live counts rather than the whole buffer, which is what makes
    ///         <c>objects.Length</c> and <c>views.Length</c> mean something in the shader — a binding
    ///         covering the ring's slack would tell it there were objects where there is only capacity.
    ///     </para>
    /// </remarks>
    void Bind(
        EffectBinding objectBinding,
        EffectBinding viewBinding,
        EffectBinding outputBinding,
        EffectBinding? occluderBinding,
        int objectCount,
        int viewCount,
        long bytes
    ) {
        writes[0] = DescriptorWrite.Storage(
            objectBinding.Binding,
            objects.Buffer,
            objects.Offset,
            (long)objectCount * Unsafe.SizeOf<CullObject>()
        );

        writes[1] = DescriptorWrite.Storage(
            viewBinding.Binding,
            views.Buffer,
            views.Offset,
            (long)viewCount * Unsafe.SizeOf<CullView>()
        );

        writes[2] = DescriptorWrite.Storage(outputBinding.Binding, visibility, 0, bytes);
        var written = 3;

        // Whenever the variant declares it, whether or not any view ends up testing against it: a
        // binding the shader declares is one the set must fill, and a view that is not being
        // occlusion tested says so through its own flag rather than by leaving a hole here.
        if (occluderBinding is { } occluders && Occluders is { } pyramid) {
            writes[written++] = DescriptorWrite.Texture(occluders.Binding, pyramid.View);
        }

        device.UpdateDescriptorSet(descriptors[ring], writes.AsSpan(0, written));
    }

    /// <summary>Records the dispatch and the copy out of it, submits, and waits.</summary>
    void Dispatch(PipelineHandle pipeline, int wordCount, int viewCount, long bytes) {
        using var list = device.BeginCommandList(QueueKind.Compute, "Culling");

        RecordDispatch(list, pipeline, wordCount, viewCount, ResourceState.CopySource);
        list.CopyBuffer(visibility, 0, readback, 0, bytes);
        list.Finish();

        device.ComputeQueue.Submit([list]);

        // The RHI has no fence, so this is the whole queue rather than this submission — which is the
        // reason the dispatch was put on the compute queue rather than the graphics one. See the
        // class remarks for why the wait is paid at all.
        device.ComputeQueue.WaitIdle();
    }

    /// <summary>The dispatch itself, into whichever list is going to carry it.</summary>
    void RecordDispatch(ICommandList list, PipelineHandle pipeline, int wordCount, int viewCount, ResourceState after) {
        list.Barrier(new([new(visibility, state, ResourceState.ShaderWrite)], []));
        list.BindPipeline(pipeline);
        list.BindDescriptorSet(slot, descriptors[ring]);
        list.Dispatch(GpuCulling.Groups(wordCount), viewCount);

        list.Barrier(new([new(visibility, ResourceState.ShaderWrite, after)], []));
        state = after;
    }

    /// <summary>
    ///     Records the dispatch a <see cref="ReadBack" />-less cull left pending, into the frame's own
    ///     list.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>False when there was nothing pending, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <strong>The frame's list, not one of this group's.</strong> With no readback there is
    ///         no wait, and the only ordering this RHI can express without a fence or a semaphore is
    ///         a barrier between two things in the same queue. So the dispatch has to be recorded
    ///         where the draws that consume it are recorded, which is what
    ///         <see cref="Compositor.GpuCullingRenderer" /> arranges.
    ///     </para>
    ///     <para>
    ///         Leaves the bits in <see cref="ResourceState.ShaderRead" />, which is what
    ///         <see cref="GpuDrawArguments" /> needs and what the barrier at the head of the next
    ///         frame's dispatch transitions back out of.
    ///     </para>
    /// </remarks>
    public bool Record(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (pending is not { } work) {
            return false;
        }

        pending = null;
        RecordDispatch(list, work.Pipeline, work.WordCount, work.ViewCount, ResourceState.ShaderRead);

        return true;
    }

    /// <summary>A dispatch that has been prepared but not yet recorded.</summary>
    readonly record struct PendingCull(PipelineHandle Pipeline, int WordCount, int ViewCount);

    /// <summary>Puts the completed readback into the composed group's bitset.</summary>
    void Read(int viewCount, int objectCount, int wordCount) {
        var total = viewCount * wordCount;
        Reserve(ref words, total);

        device.Read(readback, 0, MemoryMarshal.AsBytes(words.AsSpan(0, total)));
        results.Reset(viewCount, objectCount);

        for (var view = 0; view < viewCount; view++) {
            GpuCulling.Unpack(words.AsSpan(view * wordCount, wordCount), results.Fill(view));
        }
    }

    /// <summary>Allocates the set from the layout the pipeline was built with, once per effect.</summary>
    /// <remarks>
    ///     From <see cref="Effect.SetLayouts" /> rather than from a layout of this group's own: a set
    ///     is only bindable to a pipeline whose layout it was allocated from, so building one here
    ///     would produce something the validation layers reject and a release driver mis-binds in
    ///     silence.
    /// </remarks>
    bool TryAllocateSet(Effect effect, DescriptorSetSlot wanted) {
        var slots = Math.Max(1, device.FramesInFlight);

        if (ReferenceEquals(allocatedFor, effect) && descriptors.Length == slots) {
            return true;
        }

        if ((int)wanted >= effect.SetLayouts.Length || !effect.SetLayouts[(int)wanted].IsValid) {
            return false;
        }

        DestroySets();
        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            descriptors[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)wanted], "Culling");

            if (!descriptors[index].IsValid) {
                return false;
            }
        }

        slot = wanted;
        ring = 0;
        allocatedFor = effect;

        return true;
    }

    void DestroySets() {
        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        descriptors = [];
        allocatedFor = null;
    }

    /// <summary>Makes sure the device has room for a frame's answer and for the copy of it.</summary>
    /// <remarks>
    ///     Grown by doubling and never shrunk, because the size follows the object count and a scene
    ///     that reaches a high-water mark reaches it again next frame. Growing destroys the old pair,
    ///     which is safe for the same reason the descriptor rewrite is: nothing of this group's is in
    ///     flight.
    /// </remarks>
    bool EnsureVisibility(long bytes) {
        if (bytes <= 0) {
            return false;
        }

        if (visibility.IsValid && readback.IsValid && bytes <= capacity) {
            return true;
        }

        if (visibility.IsValid) {
            device.Destroy(visibility);
        }

        if (readback.IsValid) {
            device.Destroy(readback);
        }

        capacity = Math.Max(bytes, capacity * 2);

        visibility = device.CreateBuffer(
            new(capacity, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, "Culling.Visibility")
        );

        readback = device.CreateBuffer(
            new(capacity, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "Culling.Readback")
        );

        // A new buffer has been used for nothing, whatever the old one was left in.
        state = ResourceState.Undefined;

        return visibility.IsValid && readback.IsValid;
    }

    static void Reserve<T>(ref T[] array, int required) {
        if (array.Length < required) {
            Array.Resize(ref array, Math.Max(required, Math.Max(array.Length * 2, 256)));
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        pending = null;

        DestroySets();

        if (visibility.IsValid) {
            device.Destroy(visibility);
            visibility = default;
        }

        if (readback.IsValid) {
            device.Destroy(readback);
            readback = default;
        }

        objects.Dispose();
        views.Dispose();
        results.Dispose();

        packedObjects = [];
        packedViews = [];
        words = [];
        capacity = 0;
    }
}
