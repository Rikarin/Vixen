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
    : SubRenderFeature, IDrawSubFeature, IPermutationSubFeature, ISunSource, IPunctualLightSource, IDisposable {
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
    // ⚠ 1280 rather than the default 256, for the reason PunctualShadowRenderer's tile buffer says at
    // length: a light's index carries the ring's own offset (see SceneLightBase), so the region stride
    // has to be a whole number of records. 1280 is the least common multiple of the eighty-byte
    // PunctualLightData and the 256-byte binding alignment, so it satisfies both whatever the scene's
    // light count grows to. Left at 256 the base is a fraction, truncates, and every cluster's list
    // addresses lights one or two entries along.
    readonly UploadBuffer<PunctualLightData> scene = new("ForwardLighting.Scene") { Alignment = 1280 };
    readonly UploadBuffer<ObjectRecord> records = new("ForwardLighting.Objects");
    readonly List<PermutationKey<bool>> recordKeys = [];
    readonly List<PermutationKey<bool>> contributed = [];
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
    DescriptorSetHandle descriptors;
    DescriptorAllocator? sets;

    /// <summary>Creates the feature, interning its permutation key.</summary>
    public ForwardLightingRenderFeature() {
        keys = [ParameterKeys.NewPermutation(false, "Vixen.Clustered")];
        contributed.AddRange(keys);
    }

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
    ///         changes every offset — set it before the first frame, and it has to match the shader's
    ///         <c>MaxLights</c> permutation, which <see cref="MaxLightsKey" /> is how a host says.
    ///     </para>
    ///     <para>
    ///         When more lights reach an object than fit, the dimmest are dropped — see
    ///         <see cref="Select" />.
    ///     </para>
    /// </remarks>
    public int MaxLightsPerObject { get; set; } = 8;

    /// <summary>
    ///     The shader permutation that has to carry <see cref="MaxLightsPerObject" />, for the pass named.
    /// </summary>
    /// <param name="shaderName">The shading pass, as <see cref="ShaderName" /> names it.</param>
    /// <returns>The key, interned under <c>&lt;pass&gt;.MaxLights</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         <strong><c>CascadeCount</c>'s defect, one array along, and it was live in the same
    ///         way.</strong> <c>ClusteredShading.rvn</c> declares <c>[Permutation] val MaxLights: int
    ///         = 16</c> and sizes <c>lights[MaxLights]</c> from it, this feature writes a block sized
    ///         from its own eight, and nothing joined the two — so every default frame bound a
    ///         768-byte per-draw range at a block the variant declares 1296 bytes of, and the tiers
    ///         that ask for four lights an object got eight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the disagreement is not.</b> The shader's loop is bounded by
    ///         <c>MaxLights</c> and broken out of at <c>lightCount</c>, and this feature never writes
    ///         a count larger than the block it sized — so neither side ever reads a slot nobody
    ///         wrote, whichever is shorter, and the comments here that said it did were wrong. What
    ///         actually happens is that <em>the shorter of the two silently wins</em>: shorter here
    ///         and <see cref="Select" /> drops the dimmest lights before they are ever written;
    ///         shorter in the shader and the loop stops early on lights this feature did write.
    ///     </para>
    ///     <para>
    ///         Given to <c>MaterialRenderFeature.SetPermutation</c>, which registers the key as well
    ///         as setting the value — a value under a key <c>PermutationKeys</c> does not carry
    ///         reaches no compiler, and the variant quietly takes the <c>.rvn</c>'s sixteen.
    ///         <c>CompositorBuilder</c> is what calls this for a document's shading passes.
    ///     </para>
    /// </remarks>
    public static PermutationKey<int> MaxLightsKey(string shaderName) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        return ParameterKeys.NewPermutation(ShaderDefaultMaxLights, $"{shaderName}.MaxLights");
    }

    /// <summary>What <c>ClusteredShading.rvn</c> declares <c>MaxLights</c> as.</summary>
    /// <remarks>
    ///     The shader's own number rather than this feature's default eight, on
    ///     <c>ShadowMapRenderer.ShaderDefaultCascades</c>' terms: the default a key is interned with
    ///     is what a host that never sets one selects on, so it has to be the value the compiler
    ///     would have used anyway.
    /// </remarks>
    const int ShaderDefaultMaxLights = 16;

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

    /// <summary>
    ///     Which entry of <see cref="SceneBuffer" /> this frame's lights start at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The whole buffer is bound, so the region has to be part of the index.</b> The light
    ///         list is a ring — one region per frame in flight, because a region the device is still
    ///         reading cannot be rewritten — and the route it takes to a descriptor set carries a
    ///         handle with nowhere to put an offset: <see cref="EffectSetWriter" /> fills a storage
    ///         binding with <c>DescriptorWrite.Storage(binding, handle)</c> and nothing else. So a
    ///         consumer that indexed from zero would read whichever region another frame is writing —
    ///         a light list one to three frames stale, which is invisible standing still and wrong
    ///         precisely while anything moves.
    ///     </para>
    ///     <para>
    ///         This is the number <c>ClusterCulling.lightBase</c> takes, and the culling pass folds it
    ///         into the indices it writes — so a cluster's entry addresses the buffer rather than the
    ///         region, and the shading pass needs to know nothing about the ring. The same shape as
    ///         <c>ClusteredShading.objectBase</c>, <c>transformBase</c> and
    ///         <see cref="Compositor.PunctualShadowRenderer.TileBase" />, and the same reason.
    ///     </para>
    ///     <para>
    ///         Zero on the first frame and on any device with one frame in flight, which is why a test
    ///         that only ever renders one frame cannot see this being wrong.
    ///     </para>
    /// </remarks>
    public int SceneLightBase => (int)(scene.Offset / Unsafe.SizeOf<PunctualLightData>());

    /// <summary>
    ///     Where to publish the scene's light buffer, or null to publish it nowhere.
    /// </summary>
    /// <remarks>
    ///     <see cref="SceneConstants" />' collection, normally. The buffer is a binding of the frame's
    ///     set — <c>ForwardPlus.lightBuffer</c>, which the clustered variant indexes into — and it is
    ///     recreated whenever the scene outgrows it, so a host that read the handle once and wrote it
    ///     down would be binding a buffer the device has since retired. Publishing it from
    ///     <see cref="Prepare" /> means the name always resolves to this frame's.
    /// </remarks>
    public ParameterCollection? Scene { get; set; }

    /// <summary>Which pass's names to publish under.</summary>
    public string ShaderName { get; set; } = "ForwardPlus";

    /// <inheritdoc />
    /// <remarks>
    ///     Two, once records are on: whether the lights are clustered, and whether the per-object
    ///     scalars are records. Both are facts about the frame rather than the object, so every object
    ///     shares one bit of each and no pair of them can differ.
    /// </remarks>
    public IReadOnlyList<PermutationKey<bool>> PermutationKeys => contributed;

    /// <inheritdoc />
    public bool ValueOf(RenderSystem system, RenderObjectId id, int index) =>
        index < keys.Count ? Clustered : UseRecords;

    /// <summary>
    ///     Whether this frame's per-object scalars go into a buffer rather than into a bound block.
    /// </summary>
    /// <remarks>
    ///     Set through <see cref="EnableRecords" /> rather than directly, because the host's choice
    ///     and the shader's have to be the same one — see there.
    /// </remarks>
    public bool UseRecords { get; private set; }

    /// <summary>The buffer the per-object records live in, for a host binding it once a frame.</summary>
    public BufferHandle RecordBuffer => records.Buffer;

    /// <summary>Which record this frame's objects start at.</summary>
    public int RecordBase => RecordBaseOf(records.Offset);

    /// <summary>What was written this frame, for a test or an inspector.</summary>
    public ReadOnlySpan<ObjectRecord> Records => records.Items;

    static int RecordBaseOf(long offset) => (int)(offset / System.Runtime.CompilerServices.Unsafe.SizeOf<ObjectRecord>());

    /// <summary>
    ///     Turns the per-object record path on, where the draw can address a record at all.
    /// </summary>
    /// <param name="shaderKey">
    ///     The shader's own permutation — <c>ForwardPlusKeys.UseObjectRecords</c> for the shipped
    ///     forward pass.
    /// </param>
    /// <param name="addressable">
    ///     Whether the draw's instance index is the object's slot, which is what
    ///     <see cref="TransformRenderFeature.EnableRecords" /> answered.
    /// </param>
    /// <returns>Whether records were turned on.</returns>
    /// <remarks>
    ///     <para>
    ///         <strong>The precondition is another feature's answer, which is why it is a
    ///         parameter.</strong> What addresses a record is <c>SV_InstanceID</c>, and the API adds
    ///         <c>firstInstance</c> into it — which holds the object's slot only because the transform
    ///         record path put it there. With the matrix still pushed, every draw carries zero and
    ///         every object would read record zero's probe: a picture, and a plausible one.
    ///     </para>
    ///     <para>
    ///         ⚠ Call before the first <c>Prepare</c>, for the reason every <c>EnableRecords</c> says:
    ///         a permutation that changed after a variant resolved leaves that variant compiled for
    ///         the old value.
    ///     </para>
    /// </remarks>
    public bool EnableRecords(PermutationKey<bool> shaderKey, bool addressable) {
        UseRecords = addressable;

        // Contributed whichever way it went, so the number of bits `FlagsOf` packs does not depend on
        // the answer — an object that hashed to a different variant on two devices for that reason
        // would be a cache split with no cause.
        contributed.Clear();
        contributed.AddRange(keys);
        contributed.Add(shaderKey);
        recordKeys.Clear();
        recordKeys.Add(shaderKey);

        return UseRecords;
    }

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
    ///     <para>
    ///         One, not all of them: a second sun is a stylistic choice a project can make by putting
    ///         it in a per-frame block of its own, and giving every fragment two directional lights to
    ///         evaluate for the sake of the scenes that have two is a cost the ones that have one
    ///         would pay as well.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Derived from <see cref="Lights" /> on every read, and it used to be a field
    ///         <see cref="Prepare" /> latched.</b> That is a phase later than the earliest reader:
    ///         <c>ShadowMapRenderer.Collect</c> asks which way the sun points <em>before</em> the
    ///         render system runs any of its phases, so a latched field answered with the previous
    ///         frame's sun — and on the first frame, with none at all. What that cost was one frame
    ///         of cascades fitted along <c>ShadowMapRenderer.LightDirection</c> while the shading pass
    ///         was lit along the real sun, which is exactly the two-derivations-of-one-fact split
    ///         <c>CompositorBuilder.Sun</c>'s remarks exist to prevent, and it reported itself as a
    ///         <see cref="Compositor.SceneRenderer.Degraded" /> on a frame whose scene had a perfectly
    ///         good sun in it. Measured on sample 03: 14.2° apart.
    ///     </para>
    ///     <para>
    ///         The list is refilled by extraction before anything renders — <c>LightExtractionSystem</c>
    ///         runs in <c>SystemPhase.PreRender</c> — so reading it here is reading this frame's scene
    ///         from any phase, which is the property a latched field cannot have. The scan is over the
    ///         scene's lights and is the same walk extraction already makes once; a scene big enough
    ///         for that to matter is one whose per-object light selection dwarfs it.
    ///     </para>
    /// </remarks>
    public RenderLight? Sun {
        get {
            RenderLight? found = null;
            var brightest = 0f;

            for (var i = 0; i < lights.Count; i++) {
                var light = lights[i];

                if (light.Kind != LightKind.Directional) {
                    continue;
                }

                var luminance = light.Colour.Luminance() * light.Intensity;

                // `found is null` first, so a scene whose only directional light is black — an
                // intensity of zero, which is how a light says it is off — still names a sun rather
                // than reporting none. That is what the latched form did, and a node that fits
                // cascades wants a direction even from a sun contributing nothing.
                if (found is null || luminance > brightest) {
                    found = light;
                    brightest = luminance;
                }
            }

            return found;
        }
    }

    /// <summary>The buffer every object's block lives in.</summary>
    public BufferHandle Buffer => buffer;

    /// <summary>This frame's set, valid from the moment <see cref="Prepare" /> has run.</summary>
    /// <remarks>
    ///     A different handle most frames, and never one a frame still in flight is reading — see the
    ///     type's remarks. A host building a <em>pipeline layout</em> wants <see cref="Layout" />,
    ///     which does not change; nothing should hold this across a frame boundary.
    /// </remarks>
    public DescriptorSetHandle Descriptors => descriptors;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Exactly the condition <see cref="Draw" /> returns early on, stated once so a draw loop
    ///         can ask before the run instead of finding out per node. The clustered path assigns no
    ///         lights per object — a fragment looks itself up in the grid — so it binds nothing per
    ///         node and a run of nodes stays mergeable.
    ///     </para>
    ///     <para>
    ///         ⚠ The uniform-list path is the other answer, and it is not one a gate can talk out of:
    ///         each object's light block is at its own dynamic offset in one buffer, and a dynamic
    ///         offset travels in the bind rather than in the block. There is no place inside a merged
    ///         command to change it.
    ///     </para>
    /// </remarks>
    public bool IsRecording => !Clustered && descriptors.IsValid;

    /// <summary>The layout its set is made from — the pass's own, taken from the effect.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The shader's, not this feature's, and that is the whole point.</b> A set bound at
    ///         slot <i>n</i> has to have been allocated from a layout identically defined to the one
    ///         the pipeline was created with — same binding index, same kind, same stages, same count
    ///         — and only the shader knows all four. This feature used to build one from what it
    ///         believed instead, and believed the block was read by the fragment stage alone where
    ///         <c>ForwardPlus</c> declares it for the vertex stage as well. Two layouts that differ in
    ///         any of the four are incompatible, and the draw is a validation error and a GPU fault.
    ///     </para>
    ///     <para>
    ///         The same arrangement <see cref="ViewConstants.Layout" /> has, for the same reason and
    ///         with the same shape:
    ///     </para>
    ///     <code>lighting.Layout = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw];</code>
    ///     <para>
    ///         ⚠ Unset, this binds nothing and <see cref="IsRecording" /> is false — the honest answer
    ///         for a feature that cannot know what shape to allocate. It is not a regression from what
    ///         a host got before: what it got before was a frame the driver refused.
    ///     </para>
    /// </remarks>
    public DescriptorSetLayoutHandle Layout { get; set; }

    /// <summary>Where an unset <see cref="Layout" /> is taken from, once a shader has resolved.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because a host cannot set <see cref="Layout" /> before there is a shader, and the
    ///         first frame without it is a GPU fault rather than a dark frame.</b> The layout has to be
    ///         the pass's own; the pass's own comes off a resolved variant; and the first variant
    ///         resolves inside the first frame, after every constructor a host has run. Adopting it at
    ///         the start of that same frame's <c>Prepare</c> is the earliest moment it exists — and it
    ///         is early enough, because a sub-feature added after the material one prepares after it.
    ///     </para>
    ///     <para>
    ///         Explicit and settable rather than reached through <c>Parent</c>: a host with two shading
    ///         passes has two of each, and which material feature owns this one's layout is a fact
    ///         about how it was assembled. Null leaves <see cref="Layout" /> alone, which is what a
    ///         host that sets it by hand — every device test — already does.
    ///     </para>
    /// </remarks>
    public MaterialRenderFeature? Materials { get; set; }

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

    /// <summary>Whether an effect has a set at <see cref="Slot" /> to bind into.</summary>
    /// <remarks>
    ///     <para>
    ///         Null is true, not false: a host drawing hand-written modules has no reflection to ask,
    ///         and it had this set bound before there was anything to ask. An effect with no
    ///         reflected bindings at all is the same case.
    ///     </para>
    ///     <para>
    ///         ⚠ Asked of the <em>bindings</em> and not of <c>SetLayouts[Slot].IsValid</c>, which is
    ///         what this was written as first. A pipeline layout has an entry for every set below the
    ///         highest one the shader uses, so a shader that declares set 2 and nothing per-object
    ///         still has a perfectly valid — and completely empty — layout at set 3. Checking the
    ///         handle answers "is there a slot", and the question is "is there anything in it".
    ///     </para>
    /// </remarks>
    bool Declares(Effect? effect) {
        if (effect is not { Bindings.Length: > 0 } resolved) {
            return true;
        }

        foreach (var binding in resolved.Bindings) {
            if (binding.Set == Slot) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Takes the pass's set layout off a resolved variant, the first frame there is one.</summary>
    /// <remarks><see cref="Materials" /> says why this cannot happen any earlier.</remarks>
    void AdoptLayout() {
        if (Layout.IsValid || Materials?.AnyResolved is not { } effect) {
            return;
        }

        var slot = (int)Slot;

        if (effect.SetLayouts.Length > slot && effect.SetLayouts[slot].IsValid) {
            Layout = effect.SetLayouts[slot];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Per visible object, which is the reason preparation is a phase of its own: an object the
    ///     frustum rejected has no fragments, so choosing lights for it is work with no output. In a
    ///     scene that culls well that is most of the scene.
    /// </remarks>
    protected internal override void Prepare(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        used = 0;

        if (Device is null || Parent is null) {
            return;
        }

        AdoptLayout();

        SplitByKind();
        UploadScene();
        Publish();

        // One tick of the ring per frame, and Prepare is what a frame is here. It has to happen on
        // both paths: the clustered path allocates no set, and a ring that only advanced on the
        // frames that did would hand one back while a forward frame in flight was still reading it.
        Ring().BeginFrame();

        UploadRecords(system);

        if (Clustered) {
            // No light list per object. The cluster grid is what a fragment looks itself up in, so
            // choosing eight lights for an object here would be work whose answer no shader reads —
            // but the probe still is per object, and that is what UploadRecords above carries.
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

        // ⚠ Only where the effect being drawn with declares the set. The same rule the material set
        // already follows one level up, and it took a shadow pass to find: a stage that overrode the
        // shader is drawing something that reads no per-object light list, so its pipeline layout has
        // an *empty* set 3 — and a descriptor set allocated from another layout is not compatible
        // with an empty one. Every caster draw was a validation error naming two layouts by number.
        if (!Declares(context.Effect)) {
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
    /// <summary>
    ///     Fills and uploads one record per object slot, when the frame reads them from a buffer.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Per <em>slot</em> and not per visible object, because a slot is what the draw's
    ///         instance index carries — the same reason <c>TransformRenderFeature</c> writes the whole
    ///         object array. A compacted list is addressed by object, so there is no per-frame
    ///         renumbering anything could agree on.
    ///     </para>
    ///     <para>
    ///         Every slot, including the ones nothing occupies. Skipping the invisible would leave
    ///         last frame's probe at a slot an object moved into, and the record is sixteen bytes.
    ///     </para>
    ///     <para>
    ///         ⚠ There is a record even with the path off, for the reason every buffer in this family
    ///         has one: <c>objects</c> is declared by the shader whichever way the permutation went,
    ///         and a set short one entry is not bound at all — so the frame would lose set 0 and go
    ///         dark rather than lose its probes.
    ///     </para>
    /// </remarks>
    void UploadRecords(RenderSystem system) {
        records.Device = Device;

        if (!UseRecords) {
            if (!records.Buffer.IsValid) {
                records.Add([default]);
                records.Upload();
            }

            PublishRecords();
            return;
        }

        records.Begin();

        var objects = system.Objects.All;
        var count = system.Objects.Count;

        for (var index = 0; index < count; index++) {
            var record = default(ObjectRecord);

            if (index < objects.Length && objects[index] is { IsAlive: true } candidate) {
                record = RecordOf(candidate.Bounds);
            }

            records.Add([record]);
        }

        if (count == 0) {
            records.Add([default]);
        }

        records.Upload();
        PublishRecords();
    }

    /// <summary>Which probe reaches a volume, and how much of it shows.</summary>
    ObjectRecord RecordOf(BoundingSphere bounds) {
        if (Probes is not { } selector || selector.Select(bounds.Center) is not { } chosen) {
            return default;
        }

        var index = selector.Probes.IndexOf(chosen.Probe);
        return index < 0 ? default : new() { ProbeIndex = index, ProbeWeight = chosen.Weight };
    }

    void PublishRecords() {
        if (Scene is not { } parameters || !records.Buffer.IsValid) {
            return;
        }

        parameters.Set(ParameterKeys.New<BufferHandle>($"{ShaderName}.objects"), records.Buffer);
        parameters.Set(ParameterKeys.New<int>($"{ShaderName}.objectBase"), RecordBase);
    }

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

        if (flattened.Length < Math.Max(punctual.Count, 1)) {
            flattened = new PunctualLightData[Math.Max(punctual.Count, 64)];
        }

        for (var i = 0; i < punctual.Count; i++) {
            flattened[i] = lights[punctual[i]].ToGpu();
        }

        // One empty entry rather than nothing when the scene has no punctual lights at all. The
        // buffer is a *binding* of the frame's set, and a set is written wholly or not at all — so a
        // scene lit by the sun alone would otherwise fail to bind set 0 and draw nothing. Nobody
        // reads it: the count is what ends both loops.
        if (punctual.Count == 0) {
            flattened[0] = default;
        }

        scene.Add(flattened.AsSpan(0, Math.Max(punctual.Count, 1)));
        scene.Upload();
    }

    /// <summary>Hands this frame's light buffer to whatever binds the frame's set.</summary>
    /// <remarks>
    ///     On both paths, because both read it: the clustered variant indexes into it through the
    ///     cluster lists and the unclustered one declares the binding whether or not its loop uses it,
    ///     and a set is written wholly or not at all.
    /// </remarks>
    void Publish() {
        if (Scene is not { } parameters || !scene.Buffer.IsValid) {
            return;
        }

        parameters.Set(ParameterKeys.New<BufferHandle>($"{ShaderName}.lightBuffer"), scene.Buffer);
    }

    /// <summary>Which of <see cref="Lights" /> go in the per-object and cluster lists.</summary>
    /// <remarks>
    ///     Directional lights are left out, and picking the brightest of them is <see cref="Sun" />'s
    ///     own job rather than a second output of this walk — that split is what lets a node ask which
    ///     way the sun points from a phase that runs before this one.
    /// </remarks>
    void SplitByKind() {
        punctual.Clear();

        for (var i = 0; i < lights.Count; i++) {
            if (lights[i].Kind == LightKind.Directional) {
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

        if (!Layout.IsValid) {
            // Nothing to allocate from. See the remarks on Layout: a set of the wrong shape is worse
            // than no set, because the driver refuses the draw rather than the frame drawing dark.
            descriptors = default;
            return;
        }

        // Dynamic, matching the layout: a set written as a plain uniform buffer takes no offset at
        // bind time, so the per-object offset below would be ignored and every object would light
        // itself with the first one's block.
        //
        // The size is the *block's*, not the buffer's: a dynamic offset names where a block starts and
        // the descriptor says how far it extends. Binding the whole buffer would let a shader read
        // every other object's lights, which validation layers do report and drivers do not.
        write[0] = DescriptorWrite.DynamicUniform(Binding, buffer, 0, stride);
        descriptors = Ring().Allocate(Layout, write);
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
        records.Dispose();

        // The ring first: it destroys the sets it made, and destroying a layout still named by a set
        // is the ordering a validation layer complains about. The layout itself is not destroyed here
        // and must not be — it is the effect's, shared by every pipeline built from that variant.
        sets?.Dispose();
        sets = null;

        if (Device is not null && buffer.IsValid) {
            Device.Destroy(buffer);
        }

        buffer = default;
        descriptors = default;
    }
}

/// <summary>The per-object scalars a shading pass reads when they are records of a buffer.</summary>
/// <remarks>
///     Sixteen bytes at the object's own slot, matching <c>ObjectRecord</c> in
///     <c>Raven/Library/Geometry/Transform.rvn</c> member for member. Explicit layout is not needed —
///     four four-byte fields in declaration order is what std430 gives — but the padding field is,
///     because a shader's struct is padded to sixteen whether or not a host writes it.
/// </remarks>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct ObjectRecord {
    /// <summary>Which of the bound probe cubes reaches this object.</summary>
    public int ProbeIndex;

    /// <summary>How much of it shows through, against the environment behind it.</summary>
    public float ProbeWeight;

    /// <summary>How many entries of the object's light list are filled.</summary>
    public int LightCount;

    /// <summary>Padding to the shader's sixteen.</summary>
    public int Reserved;
}
