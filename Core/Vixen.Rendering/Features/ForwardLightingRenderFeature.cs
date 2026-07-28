// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>
///     Which lights reach each object, and getting that list to the shader.
/// </summary>
/// <remarks>
///     <para>
///         Stride's <c>ForwardLightingRenderFeature</c> model, which docs/plan/06 names as the
///         forward path's per-object light list and as the fallback the clustered path degrades to
///         where compute is absent. A fragment iterates the lights its object was given rather than
///         every light in the scene, which is what makes a scene with two hundred lights cost what a
///         scene with eight does.
///     </para>
///     <para>
///         <strong>Lights are selected against objects, never against the view frustum.</strong> That
///         looks like a missed optimisation and is a correctness requirement: a lamp behind the
///         camera lights everything in front of it, so culling lights by the frustum would darken
///         exactly the objects that are on screen. The frustum has already done its work — the
///         objects considered here are the ones that survived it.
///     </para>
///     <para>
///         <strong>One buffer, one descriptor, a per-draw offset.</strong> Every object's block lives
///         in one uniform buffer and is reached through
///         <see cref="DescriptorKind.DynamicUniformBuffer" />, so a thousand objects cost a thousand
///         offsets rather than a thousand descriptor sets. Allocating a set per draw is the single
///         most common reason a Vulkan renderer ends up slower than the D3D11 one it replaced.
///     </para>
///     <para>
///         <strong>One set per frame, from a <see cref="DescriptorAllocator" />, not one set for
///         ever.</strong> The buffer is recreated when the scene outgrows it, and a set held across
///         frames would have to be rewritten to point at the new one — which is a write to a set the
///         frames still in flight are reading, and drivers execute that without a word. The ring is
///         exactly <see cref="IGraphicsDevice.FramesInFlight" /> deep, so the set this frame writes
///         is one no frame still in flight can be reading. The <em>buffer</em> needs no such care:
///         <see cref="IGraphicsDevice" /> defers every destruction until the frames that could
///         reference the handle have retired, which descriptor writes have no equivalent of.
///     </para>
///     <para>
///         The directional light is not in the list. It has no position to test against an object,
///         so it reaches everything, and paying list traversal for something present in every list is
///         paying for nothing — <c>ForwardPlus.rvn</c> takes it as its own uniform for the same
///         reason. <see cref="Sun" /> is what a per-frame binder reads.
///     </para>
/// </remarks>
public sealed class ForwardLightingRenderFeature
    : SubRenderFeature, IDrawSubFeature, IPermutationSubFeature, ISunSource, IDisposable {
    /// <summary>How many bytes precede the light array in the block.</summary>
    /// <remarks>
    ///     A <c>uint</c> count and two more scalars, because std140 starts an array of structures on a
    ///     sixteen-byte boundary whatever precedes it. Writing the array at offset four would put
    ///     every light one slot early and shade with the wrong ones — and the padding those three
    ///     scalars leave is free, which is why the probe fields went here rather than into a block of
    ///     their own.
    /// </remarks>
    public const int HeaderSize = 16;

    /// <summary>Where the chosen probe's index sits in the header.</summary>
    public const int ProbeIndexOffset = 4;

    /// <summary>Where the chosen probe's weight sits in the header.</summary>
    public const int ProbeWeightOffset = 8;

    readonly List<RenderLight> lights = [];
    readonly List<int> punctual = [];
    readonly UploadBuffer<PunctualLightData> scene = new("ForwardLighting.Scene");
    readonly List<PermutationKey<bool>> keys;

    // One element, reused: the write is the same shape every frame, and the allocator copies it only
    // when it has to make a key out of it. Building the span from a collection expression here would
    // put an array per frame in the path the rendering tests assert allocates nothing.
    readonly DescriptorWrite[] write = new DescriptorWrite[1];

    PunctualLightData[] flattened = [];
    bool disposed;

    int[] chosen = [];
    float[] scores = [];
    byte[] staging = [];
    int stride;
    int used;
    int capacity;
    BufferHandle buffer;
    DescriptorSetLayoutHandle layout;
    DescriptorSetHandle descriptors;
    DescriptorAllocator? sets;

    /// <summary>Creates the feature, interning its permutation key.</summary>
    public ForwardLightingRenderFeature() => keys = [ParameterKeys.NewPermutation(false, "Vixen.Clustered")];

    /// <inheritdoc />
    public override string Name => "ForwardLighting";

    /// <summary>Where each object's block starts, and how many lights it holds.</summary>
    public RenderDataKey<LightAssignment> Assignments { get; private set; }

    /// <summary>The scene's lights. Filled by whatever extracts them.</summary>
    /// <remarks>
    ///     A list this feature is given rather than one it discovers, for the same reason
    ///     <see cref="RootRenderFeature.Extract" /> exists: touching a scene graph is extraction's
    ///     job, and every phase after it reads flat data.
    /// </remarks>
    public IList<RenderLight> Lights => lights;

    /// <summary>How many lights one object's block has room for.</summary>
    /// <remarks>
    ///     <para>
    ///         Eight by default, which is the number Stride's forward path settled on and roughly
    ///         where the cost of a longer loop stops being repaid. It sizes the block, so changing it
    ///         changes every offset — set it before the first frame, and it must match the shader's
    ///         <c>MaxLights</c> permutation or the shader reads past its own array.
    ///     </para>
    ///     <para>
    ///         When more lights reach an object than fit, the dimmest are dropped — see
    ///         <see cref="Select" />.
    ///     </para>
    /// </remarks>
    public int MaxLightsPerObject { get; set; } = 8;

    /// <summary>What a dynamic uniform offset must be a multiple of.</summary>
    /// <remarks>
    ///     Two hundred and fifty-six, which is the largest <c>minUniformBufferOffsetAlignment</c> any
    ///     shipping Vulkan implementation reports — so the default is correct everywhere and wasteful
    ///     on most. A host that has queried the device should lower it; the waste is the difference
    ///     between this and the block's real size, per object.
    /// </remarks>
    public int OffsetAlignment { get; set; } = 256;

    /// <summary>The device the light buffer lives on. Set before the first frame that prepares.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>
    ///     Whether the scene's lights are culled into a cluster grid instead of into per-object lists.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The switch between the two halves of docs/plan/06's default pipeline, and what it turns
    ///         off is the interesting part: <strong>clustered lighting has no per-object work at
    ///         all</strong>. No selection, no block per object, no descriptor bound per draw — the
    ///         whole per-object path here goes quiet, and what replaces it is one compute dispatch and
    ///         one buffer every fragment indexes.
    ///     </para>
    ///     <para>
    ///         Which is why it is a permutation rather than a branch: the two variants read different
    ///         bindings, so a runtime branch would keep the per-draw block alive in a shader that
    ///         never looks at it. <c>ForwardPlus.rvn</c> says the same thing from the other side.
    ///     </para>
    /// </remarks>
    public bool Clustered { get; set; }

    /// <summary>Every light in the scene, as the culling pass reads them.</summary>
    /// <remarks>
    ///     One buffer for the whole scene rather than a list per object, and it is filled whichever
    ///     path is on — the clustered pass culls it, and a host that wants to inspect what a frame
    ///     was lit by has one place to look.
    /// </remarks>
    public BufferHandle SceneBuffer => scene.Buffer;

    /// <summary>How many lights the scene buffer holds this frame.</summary>
    public int SceneLightCount => scene.Count;

    /// <inheritdoc />
    public IReadOnlyList<PermutationKey<bool>> PermutationKeys => keys;

    /// <inheritdoc />
    public bool ValueOf(RenderSystem system, RenderObjectId id, int index) => Clustered;

    /// <summary>Which descriptor set the block is bound to.</summary>
    public DescriptorSetSlot Slot { get; set; } = DescriptorSetSlot.PerDraw;

    /// <summary>
    ///     Which reflection probe each object gets, or null to leave every object without one.
    /// </summary>
    /// <remarks>
    ///     Here rather than in a feature of its own because a probe's index and weight live in this
    ///     feature's block — they fit in the padding std140 leaves after the light count — and a
    ///     second feature writing into one block would mean two owners of one layout. Choosing the
    ///     probe is still not this class's business: <see cref="ReflectionProbeSelector" /> answers
    ///     that from positions and volumes alone, with no device and no frame in sight.
    /// </remarks>
    public ReflectionProbeSelector? Probes { get; set; }

    /// <summary>Which binding within that set.</summary>
    public uint Binding { get; set; }

    /// <summary>Which stages read the light list.</summary>
    public ShaderStage Stages { get; set; } = ShaderStage.Fragment;

    /// <summary>The brightest directional light, or null when the scene has none.</summary>
    /// <remarks>
    ///     One, not all of them: a second sun is a stylistic choice a project can make by putting it
    ///     in a per-frame block of its own, and giving every fragment two directional lights to
    ///     evaluate for the sake of the scenes that have two is a cost the ones that have one would
    ///     pay as well.
    /// </remarks>
    public RenderLight? Sun { get; private set; }

    /// <summary>The buffer every object's block lives in.</summary>
    public BufferHandle Buffer => buffer;

    /// <summary>This frame's set, valid from the moment <see cref="Prepare" /> has run.</summary>
    /// <remarks>
    ///     A different handle most frames, and never one a frame still in flight is reading — see the
    ///     type's remarks. A host building a <em>pipeline layout</em> wants <see cref="Layout" />,
    ///     which does not change; nothing should hold this across a frame boundary.
    /// </remarks>
    public DescriptorSetHandle Descriptors => descriptors;

    /// <summary>The layout that set was made from.</summary>
    public DescriptorSetLayoutHandle Layout => layout;

    /// <summary>How many sets the ring has had to create, which settles at frames-in-flight.</summary>
    /// <remarks>
    ///     The number a leak test wants. A frame allocates one set; growing the buffer changes what
    ///     that set says and not how many exist, so a run that grows twice still settles here.
    /// </remarks>
    public int SetCount => sets?.SetCount ?? 0;

    /// <summary>How many bytes one object's block occupies, alignment included.</summary>
    public int BlockStride => stride;

    /// <summary>How many bytes of the buffer this frame filled.</summary>
    public int UsedBytes => used;

    /// <summary>The bytes written for one object, for a test or an inspector.</summary>
    /// <remarks>
    ///     The staging copy rather than the buffer's, because a device is under no obligation to read
    ///     memory back — the Null backend keeps none at all. It is the same bytes: this is what was
    ///     uploaded.
    /// </remarks>
    public ReadOnlySpan<byte> Block(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);

        var assignment = system.Objects.Data.Data(Assignments)[id.Index];

        return assignment.Offset + stride <= used ? staging.AsSpan(assignment.Offset, stride) : default;
    }

    /// <summary>The lights one object was given, brightest first.</summary>
    public IEnumerable<PunctualLightData> LightsFor(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);

        var assignment = system.Objects.Data.Data(Assignments)[id.Index];

        for (var i = 0; i < assignment.Count; i++) {
            yield return MemoryMarshal.Read<PunctualLightData>(
                staging.AsSpan(assignment.Offset + HeaderSize + (i * Unsafe.SizeOf<PunctualLightData>()))
            );
        }
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);
        Assignments = system.Objects.Data.Register<LightAssignment>();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Per visible object, which is the reason preparation is a phase of its own: an object the
    ///     frustum rejected has no fragments, so choosing lights for it is work with no output. In a
    ///     scene that culls well that is most of the scene.
    /// </remarks>
    protected internal override void Prepare(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        Sun = null;
        used = 0;

        if (Device is null || Parent is null) {
            return;
        }

        SplitByKind();
        UploadScene();

        // One tick of the ring per frame, and Prepare is what a frame is here. It has to happen on
        // both paths: the clustered path allocates no set, and a ring that only advanced on the
        // frames that did would hand one back while a forward frame in flight was still reading it.
        Ring().BeginFrame();

        if (Clustered) {
            // Nothing per object. The cluster grid is what a fragment looks itself up in, so choosing
            // eight lights for an object here would be work whose answer no shader reads.
            descriptors = default;
            return;
        }

        Resize();
        Rebind();

        var assignments = system.Objects.Data.Data(Assignments);
        var objects = system.Objects.All;

        for (var index = 0; index < objects.Length; index++) {
            ref readonly var candidate = ref objects[index];

            if (!candidate.IsAlive || candidate.FeatureIndex != Parent.Index) {
                continue;
            }

            if (!IsVisibleAnywhere(system, index)) {
                // Left as it was rather than cleared: nothing will read it this frame, and writing a
                // zero to every culled object's slot is the per-object work culling just avoided.
                continue;
            }

            var count = Select(candidate.Bounds);
            var offset = used;
            used += stride;

            var block = staging.AsSpan(offset, stride);
            block.Clear();

            var declared = (uint)count;
            MemoryMarshal.Write(block, in declared);
            WriteProbe(block, candidate.Bounds.Center);

            for (var i = 0; i < count; i++) {
                var light = lights[chosen[i]].ToGpu();
                MemoryMarshal.Write(block[(HeaderSize + (i * Unsafe.SizeOf<PunctualLightData>()))..], in light);
            }

            assignments[index] = new(offset, count);
        }

        if (used > 0) {
            // One write for the frame rather than one per object. A host-visible write is cheap and a
            // call into the driver is not, and the blocks are contiguous by construction.
            Device.Write(buffer, 0, staging.AsSpan(0, used));
        }
    }

    /// <inheritdoc />
    public void Draw(RenderSystem system, RenderDrawContext context, in RenderNode node) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(context);

        if (Clustered || !descriptors.IsValid) {
            return;
        }

        var assignment = system.Objects.Data.Data(Assignments)[node.Object.Index];
        var offset = (uint)assignment.Offset;

        context.CommandList.BindDescriptorSet(
            Slot,
            descriptors,
            MemoryMarshal.CreateReadOnlySpan(ref offset, 1)
        );
    }

    /// <summary>
    ///     Writes which probe lights this object, and how much of it shows.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         In this block rather than in one of its own, because the header already had the room:
    ///         std140 puts the light array on a sixteen-byte boundary, so the count left twelve bytes
    ///         of padding and two of them are now these. A probe therefore costs a per-object block
    ///         that is exactly the size it already was.
    ///     </para>
    ///     <para>
    ///         <strong>This is what makes probes per object rather than per group.</strong> The cubes
    ///         are one binding with a count, bound for the frame; the volumes are an array beside
    ///         them; and an object picks both with this index. Nothing extra is bound per draw, which
    ///         is the whole reason it is an index and not a descriptor set.
    ///     </para>
    ///     <para>
    ///         No selector means index zero and weight zero, which is the shader's own default: no
    ///         probe, ambient from the environment alone.
    ///     </para>
    /// </remarks>
    void WriteProbe(Span<byte> block, Vector3 position) {
        if (Probes is not { } selector || selector.Select(position) is not { } chosenProbe) {
            return;
        }

        var index = selector.Probes.IndexOf(chosenProbe.Probe);

        if (index < 0) {
            return;
        }

        MemoryMarshal.Write(block[ProbeIndexOffset..], in index);

        var weight = chosenProbe.Weight;
        MemoryMarshal.Write(block[ProbeWeightOffset..], in weight);
    }

    /// <summary>
    ///     Picks the lights that reach a sphere, brightest first, filling <see cref="chosen" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Insertion into a list of <see cref="MaxLightsPerObject" /> rather than a sort: the list
    ///         is short and the candidate set is not, so keeping the best eight as they arrive costs
    ///         one comparison per light in the common case where a light does not make the cut.
    ///     </para>
    ///     <para>
    ///         <strong>When more lights reach an object than fit, the dimmest are dropped.</strong>
    ///         That minimises the error, and it is also the choice that pops: an object crossing the
    ///         boundary between two lights of similar brightness swaps one for the other between
    ///         frames. Clustered lighting is the answer to that rather than a longer list, which is
    ///         why the clustered path exists beside this one.
    ///     </para>
    /// </remarks>
    int Select(in BoundingSphere bounds) {
        var count = 0;

        foreach (var index in punctual) {
            var light = lights[index];
            var score = Score(light, bounds);

            if (score <= 0f) {
                continue;
            }

            if (count == chosen.Length && score <= scores[count - 1]) {
                continue;
            }

            var at = count < chosen.Length ? count++ : count - 1;

            while (at > 0 && scores[at - 1] < score) {
                scores[at] = scores[at - 1];
                chosen[at] = chosen[at - 1];
                at--;
            }

            scores[at] = score;
            chosen[at] = index;
        }

        return count;
    }

    /// <summary>How much this light matters to this object.</summary>
    /// <remarks>
    ///     The windowed inverse-square falloff the shading library uses, evaluated at the sphere's
    ///     near point rather than its centre — so a large object next to a small light is not ranked
    ///     as though the light were at its middle. The same function the fragment will evaluate, which
    ///     is what makes "the eight brightest" mean the same thing on both sides.
    /// </remarks>
    static float Score(in RenderLight light, in BoundingSphere bounds) {
        if (light.Range <= 0f) {
            return 0f;
        }

        var distance = MathF.Max(Vector3.Distance(bounds.Center, light.Position) - bounds.Radius, 0f);

        if (distance >= light.Range) {
            return 0f;
        }

        if (light.Kind == LightKind.Spot && !ConeReaches(light, bounds)) {
            return 0f;
        }

        var ratio = distance / light.Range;
        var window = Math.Clamp(1f - (ratio * ratio * ratio * ratio), 0f, 1f);

        return light.Colour.Luminance() * light.Intensity * window * window / ((distance * distance) + 1f);
    }

    /// <summary>Whether a spot light's cone touches a sphere at all.</summary>
    /// <remarks>
    ///     The standard cone-sphere test: reject behind the apex, beyond the range, or outside the
    ///     cone widened by the sphere's radius. Conservative in the right direction — it accepts
    ///     spheres a tighter test would reject, and an extra light in a list costs a few instructions
    ///     where a missing one is a dark object.
    /// </remarks>
    static bool ConeReaches(in RenderLight light, in BoundingSphere bounds) {
        var toCentre = bounds.Center - light.Position;
        var alongAxis = Vector3.Dot(toCentre, light.Direction);

        if (alongAxis < -bounds.Radius || alongAxis > light.Range + bounds.Radius) {
            return false;
        }

        var cosOuter = MathF.Cos(light.OuterAngle);
        var sinOuter = MathF.Sqrt(MathF.Max(1f - (cosOuter * cosOuter), 0f));
        var perpendicular = MathF.Sqrt(MathF.Max(toCentre.LengthSquared() - (alongAxis * alongAxis), 0f));

        return (cosOuter * perpendicular) - (alongAxis * sinOuter) <= bounds.Radius;
    }

    static bool IsVisibleAnywhere(RenderSystem system, int index) {
        foreach (var view in system.Views) {
            if (system.Visibility.IsVisible(view.Index, new(index))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Flattens every punctual light into the scene buffer, once for the frame.</summary>
    /// <remarks>
    ///     Directional lights are left out, exactly as they are left out of a per-object list and for
    ///     the same reason: the culling pass would put one in every cluster, which is paying list
    ///     traversal for something always present. The sun is a uniform on both paths.
    /// </remarks>
    void UploadScene() {
        scene.Device = Device;
        scene.Begin();

        if (punctual.Count == 0) {
            return;
        }

        if (flattened.Length < punctual.Count) {
            flattened = new PunctualLightData[Math.Max(punctual.Count, 64)];
        }

        for (var i = 0; i < punctual.Count; i++) {
            flattened[i] = lights[punctual[i]].ToGpu();
        }

        scene.Add(flattened.AsSpan(0, punctual.Count));
        scene.Upload();
    }

    void SplitByKind() {
        punctual.Clear();

        var brightest = 0f;

        for (var i = 0; i < lights.Count; i++) {
            var light = lights[i];

            if (light.Kind == LightKind.Directional) {
                var luminance = light.Colour.Luminance() * light.Intensity;

                if (Sun is null || luminance > brightest) {
                    Sun = light;
                    brightest = luminance;
                }

                continue;
            }

            punctual.Add(i);
        }
    }

    /// <summary>Sizes the scratch arrays, the staging buffer and the GPU buffer for this frame.</summary>
    void Resize() {
        var wanted = Align(HeaderSize + (MaxLightsPerObject * Unsafe.SizeOf<PunctualLightData>()), OffsetAlignment);

        // Both inputs, not just the light count: a host that queried the device and lowered the
        // alignment after the first frame would otherwise keep the stride it was given at startup,
        // and every offset would stay wrong by the difference.
        if (chosen.Length != MaxLightsPerObject || stride != wanted) {
            chosen = new int[MaxLightsPerObject];
            scores = new float[MaxLightsPerObject];
            stride = wanted;
            capacity = 0;
        }

        // Room for every live object, not for every visible one: what is visible changes every frame
        // and reallocating a GPU buffer because the camera turned is the allocation this exists to
        // avoid. The high-water mark is what the buffer is sized to.
        var required = Math.Max(Parent?.System?.Objects.Count ?? 0, 1) * stride;

        if (required <= capacity) {
            return;
        }

        var grown = Math.Max(required, Math.Max(capacity * 2, stride * 64));

        Array.Resize(ref staging, grown);
        Recreate(grown);
        capacity = grown;
    }

    void Recreate(int size) {
        if (Device is null) {
            return;
        }

        if (buffer.IsValid) {
            // Safe with frames in flight, and it is the only half of this that is: the RHI defers
            // every destruction until the frames that could still reference the handle have retired,
            // which is the backend's job precisely because a renderer cannot know when that is. The
            // set pointing at it has no such deferral, which is why Rebind takes a new one rather
            // than rewriting the old.
            Device.Destroy(buffer);
        }

        buffer = Device.CreateBuffer(
            new(size, BufferUsage.Uniform, MemoryAccess.HostUpload, "ForwardLighting.Lights")
        );
    }

    /// <summary>Takes this frame's set, pointing at whatever buffer this frame ended up with.</summary>
    void Rebind() {
        if (Device is null || !buffer.IsValid) {
            descriptors = default;
            return;
        }

        if (!layout.IsValid) {
            layout = Device.CreateDescriptorSetLayout(
                new(
                    Slot,
                    [new(Binding, DescriptorKind.DynamicUniformBuffer, Stages)],
                    "ForwardLighting"
                )
            );
        }

        // The size is the *block's*, not the buffer's: a dynamic offset names where a block starts and
        // the descriptor says how far it extends. Binding the whole buffer would let a shader read
        // every other object's lights, which validation layers do report and drivers do not.
        write[0] = DescriptorWrite.Uniform(Binding, buffer, 0, stride);
        descriptors = Ring().Allocate(layout, write);
    }

    /// <summary>The ring the frame's set comes out of, made on the first frame that needs one.</summary>
    /// <remarks>
    ///     Its own rather than one a host hands in, because what ticks it is <see cref="Prepare" />
    ///     and nothing else — a shared allocator is ticked by whoever owns the frame loop, and a
    ///     feature that guessed wrong about who that was would recycle a set early. It costs
    ///     frames-in-flight sets, which is two.
    /// </remarks>
    DescriptorAllocator Ring() => sets ??= new(Device!, "ForwardLighting");

    static int Align(int value, int alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        scene.Dispose();

        // The ring first: it destroys the sets it made, and destroying a layout still named by a set
        // is the ordering a validation layer complains about.
        sets?.Dispose();
        sets = null;

        if (Device is not null) {
            if (buffer.IsValid) {
                Device.Destroy(buffer);
            }

            if (layout.IsValid) {
                Device.Destroy(layout);
            }
        }

        buffer = default;
        layout = default;
        descriptors = default;
    }
}
