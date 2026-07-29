// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     A texture the frame declares and the render graph owns.
/// </summary>
/// <remarks>
///     <para>
///         The part of a compositor that used to be the host's problem. A document that can say "a
///         half-resolution R11G11B10 bloom chain" is a document that can describe a post-processing
///         pipeline; one that could only refer to textures somebody else made could describe the
///         order of passes and nothing about what flows between them.
///     </para>
///     <para>
///         Declared, not imported — which means the graph may give two of these the same memory when
///         their lifetimes do not overlap, and may skip allocating one whose only writer got culled.
///         A resource that has to survive the frame is an import instead; see
///         <see cref="ImportedTexture" />.
///     </para>
/// </remarks>
[DataContract("Resource")]
public sealed record RenderResourceAsset {
    /// <summary>What passes refer to it by.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its format.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8UNorm;

    /// <summary>What it is for.</summary>
    public TextureUsage Usage { get; init; } = TextureUsage.ColourTarget | TextureUsage.Sampled;

    /// <summary>Its width in pixels, or 0 to take <see cref="Scale" /> of the frame's.</summary>
    public int Width { get; init; }

    /// <summary>Its height in pixels, or 0 to take <see cref="Scale" /> of the frame's.</summary>
    public int Height { get; init; }

    /// <summary>
    ///     What fraction of the frame's size to be, when no explicit size is given.
    /// </summary>
    /// <remarks>
    ///     A fraction rather than a size, so a bloom chain authored at half resolution stays half
    ///     resolution on a window nobody anticipated. Rounded up and floored at one, so a chain of
    ///     halvings ends at a 1×1 texture rather than at a zero-sized one the backend refuses.
    /// </remarks>
    public float Scale { get; init; } = 1f;

    /// <summary>How many samples it has.</summary>
    public int SampleCount { get; init; } = 1;

    /// <summary>This declaration as a texture description, against a frame of a given size.</summary>
    public TextureDescription Describe(Int2 frameSize) {
        var width = Width > 0 ? Width : Math.Max((int)MathF.Ceiling(frameSize.X * Scale), 1);
        var height = Height > 0 ? Height : Math.Max((int)MathF.Ceiling(frameSize.Y * Scale), 1);

        return new(Format, width, height, Usage, SampleCount: SampleCount, Name: Name);
    }
}

/// <summary>
///     A buffer the frame declares and the render graph owns.
/// </summary>
/// <remarks>
///     A cluster list is the case this exists for: written by a compute pass and read by the shading
///     pass in the same frame, and needed by nothing outside it. Declaring it rather than importing
///     it is what lets the graph drop the whole culling pass when nothing consumes the result — and
///     is why a light list, which the host fills before the frame begins, is an import instead.
/// </remarks>
[DataContract("Buffer")]
public sealed record RenderBufferAsset {
    /// <summary>What passes refer to it by.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How many bytes it holds.</summary>
    public long Size { get; init; }

    /// <summary>What it is for.</summary>
    public BufferUsage Usage { get; init; } = BufferUsage.Storage;

    /// <summary>This declaration as a buffer description.</summary>
    public BufferDescription Describe() => new(Math.Max(Size, 1), Usage, MemoryAccess.DeviceLocal, Name);
}

/// <summary>One node of an authored compositor graph.</summary>
/// <remarks>
///     <para>
///         An interface with a <c>[DataContract]</c> name per implementation, which is how the rest
///         of the engine does polymorphism in a file: the contract name is the YAML tag, so
///         <c>!SingleStage</c> selects the type and nothing keeps a registration table in sync.
///     </para>
///     <para>
///         Deliberately a <em>parallel</em> model rather than annotations on
///         <see cref="SceneRenderer" /> itself. The runtime node holds texture views, render stages
///         and a command list; the asset holds names. Merging them would mean a type that is half
///         serialisable, and the half that is not is the half a file cannot express.
///     </para>
/// </remarks>
public interface ISceneRendererAsset {
    /// <summary>The node's name, for debug groups and for a human reading the file.</summary>
    string Name { get; }

    /// <summary>Whether the node runs.</summary>
    bool Enabled { get; }
}

/// <summary>A blend state by name, because a file should not spell out seven factors.</summary>
public enum BlendPreset {
    /// <summary>Overwrite.</summary>
    Opaque,

    /// <summary>Straight alpha.</summary>
    AlphaBlend,

    /// <summary>Premultiplied alpha.</summary>
    PremultipliedAlpha,

    /// <summary>Additive.</summary>
    Additive
}

/// <summary>A depth state by name.</summary>
public enum DepthPreset {
    /// <summary>Test and write, with the engine's reversed comparison.</summary>
    TestAndWrite,

    /// <summary>Test but do not write — what a transparent stage wants.</summary>
    TestOnly,

    /// <summary>No depth at all.</summary>
    Disabled
}

/// <summary>One render stage, as a file declares it.</summary>
/// <remarks>
///     Blend and depth are named presets rather than full states, because those four and those three
///     are what a stage has ever wanted and an author writing out seven blend factors is an author
///     about to get one of them wrong. A project that genuinely needs another supplies its own
///     <see cref="IPipelineDescriber" />, which is where an unusual pipeline belongs anyway.
/// </remarks>
[DataContract("RenderStage")]
public sealed record RenderStageAsset {
    /// <summary>What the stage is called, and what a node refers to it by.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How its work is ordered.</summary>
    public RenderSortMode SortMode { get; init; } = RenderSortMode.FrontToBack;

    /// <summary>How its fragments combine with the target.</summary>
    public BlendPreset Blend { get; init; } = BlendPreset.Opaque;

    /// <summary>What its draws do with depth.</summary>
    public DepthPreset Depth { get; init; } = DepthPreset.TestAndWrite;

    /// <summary>Which faces it discards.</summary>
    public CullMode Cull { get; init; } = CullMode.Back;

    /// <summary>A constant added to depth — a shadow-caster stage's peter-panning knob.</summary>
    public float DepthBias { get; init; }

    /// <summary>A factor on the polygon's depth slope.</summary>
    public float DepthBiasSlope { get; init; }

    /// <summary>Whether to clamp depth rather than clip it, so a caster in front of near still casts.</summary>
    public bool DepthClamp { get; init; }
}

/// <summary>Several nodes, run in order.</summary>
[DataContract("Sequence")]
public sealed record SequenceAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The children, in the order they run.</summary>
    public ISceneRendererAsset[] Children { get; init; } = [];
}

/// <summary>A render pass, and what draws into it.</summary>
[DataContract("RenderPass")]
public sealed record RenderPassAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     The names of its colour attachments, in the order the shader writes them.
    /// </summary>
    /// <remarks>
    ///     Names, not textures. A texture handle belongs to a device that did not exist when the file
    ///     was written, so the file says <em>which</em> target and the host binds the name — which is
    ///     also what lets one authored compositor run against a swapchain, an offscreen buffer or a
    ///     test's scratch texture without changing.
    /// </remarks>
    public string[] ColourTargets { get; init; } = [];

    /// <summary>The name of its depth attachment, if it has one.</summary>
    public string? DepthTarget { get; init; }

    /// <summary>How many samples its attachments have.</summary>
    public int SampleCount { get; init; } = 1;

    /// <summary>The names of resources this pass samples.</summary>
    /// <remarks>
    ///     Not optional bookkeeping. A pass that samples the shadow atlas must say so: that read is
    ///     the edge that orders the shadow pass before it and puts a barrier between them, and — if
    ///     nothing declares it — the edge whose absence gets the shadow pass culled for producing
    ///     something nobody wanted.
    /// </remarks>
    public string[] Reads { get; init; } = [];

    /// <summary>The names of buffers this pass reads — a cluster list, a light list.</summary>
    public string[] BufferReads { get; init; } = [];

    /// <summary>Which of the four conventional sets it binds.</summary>
    /// <remarks>
    ///     Per-view or lower for a pass, so the materials drawing into it rebind sets 2 and 3 without
    ///     disturbing what the pass put down.
    /// </remarks>
    public DescriptorSetSlot Slot { get; init; } = DescriptorSetSlot.PerView;

    /// <summary>What the pass binds once, before anything under it draws.</summary>
    public ResourceBindingAsset[] Bindings { get; init; } = [];

    /// <summary>What draws into it.</summary>
    public ISceneRendererAsset[] Children { get; init; } = [];
}

/// <summary>One value in the per-view block, and where it sits.</summary>
/// <remarks>
///     Named by the parameter key a shader's bindings were generated under, so a document says
///     <c>Vixen.ViewProjection</c> rather than an offset it would have to keep in step with a struct
///     it cannot see. A name nothing has interned is a mistake the build reports rather than a value
///     that silently never arrives.
/// </remarks>
[DataContract("ViewMember")]
public sealed record ViewMemberAsset {
    /// <summary>The parameter key's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its byte offset within the block.</summary>
    public int Offset { get; init; }

    /// <summary>How many bytes it occupies.</summary>
    public int Size { get; init; }
}

/// <summary>The uniform block every view in the frame shares — set 1.</summary>
/// <remarks>
///     <para>
///         The one part of the four-set convention a document has a reason to describe. Sets 2 and 3
///         belong to a material and a draw and follow from the shaders; set 1 is a contract
///         <em>between</em> shaders — a descriptor set survives a pipeline change only if the layouts
///         agree up to it — so the frame is the only thing that can state it.
///     </para>
///     <para>
///         Declaring it with no members takes the standard block: the view-projection at 0 and the
///         view position at 64, which is what <see cref="ViewConstants" /> writes for every view
///         whether or not anybody asked.
///     </para>
/// </remarks>
[DataContract("ViewBlock")]
public sealed record ViewBlockAsset {
    /// <summary>Which of the four conventional sets holds it.</summary>
    public DescriptorSetSlot Set { get; init; } = DescriptorSetSlot.PerView;

    /// <summary>Which binding within that set.</summary>
    public uint Binding { get; init; }

    /// <summary>How large the block is, in bytes.</summary>
    public int Size { get; init; } = 80;

    /// <summary>
    ///     Which shader stages read it. The default is what an ordinary material wants; a shadow
    ///     caster needs <see cref="ShaderStage.Vertex" /> alone.
    /// </summary>
    public ShaderStage Stages { get; init; } = ShaderStage.Vertex | ShaderStage.Fragment;

    /// <summary>What is in it, or empty for the standard block.</summary>
    public ViewMemberAsset[] Members { get; init; } = [];
}

/// <summary>The samplers a document may name, by what they are for.</summary>
/// <remarks>
///     A preset rather than the twelve fields of a <see cref="SamplerDescription" />, for the same
///     reason <see cref="BlendPreset" /> is a preset: an author picks a behaviour, and a document full
///     of address modes and LOD biases is one nobody can read. A project needing something else sets
///     the binding's sampler in code, which is what the asset model always falls back to.
/// </remarks>
public enum SamplerPreset {
    /// <summary>No sampler — what a binding that is not one has.</summary>
    /// <remarks>
    ///     A member rather than a nullable enum, because a <c>Nullable&lt;TEnum&gt;</c> has no
    ///     generated serializer and "absent" is a perfectly good value for an enum to carry.
    /// </remarks>
    None,

    /// <summary>Trilinear, clamped — what a full-screen pass reading a render target wants.</summary>
    LinearClamp,

    /// <summary>Unfiltered and clamped — a lookup where interpolation would be nonsense.</summary>
    PointClamp,

    /// <summary>Trilinear, repeating — an ordinary surface texture.</summary>
    LinearRepeat,

    /// <summary>Depth comparison, for a shadow map.</summary>
    Shadow
}

/// <summary>One resource a node binds, as a document says it.</summary>
/// <remarks>
///     <para>
///         <strong>Prefer <see cref="Name" /> to <see cref="Binding" />.</strong> A binding index is
///         the shader's — Raven assigns it from declaration order within a set — so a document that
///         writes one down is recording a number that changes when a resource is added above it.
///         Naming the shader's own name for the resource resolves it against the effect's plan
///         instead.
///     </para>
///     <para>
///         The index remains for a shader whose provider reports no plan — a test fake, a host
///         supplying effects of its own. The shipped ones do report it: a baked <c>EffectData</c>
///         carries the binding plan and <c>EffectLoader</c> puts it on the effect.
///     </para>
/// </remarks>
[DataContract("Binding")]
public sealed record ResourceBindingAsset {
    /// <summary>The shader's own name for this resource, resolved against its binding plan.</summary>
    public string? Name { get; init; }

    /// <summary>Its index within the set, when <see cref="Name" /> is absent or unknown.</summary>
    public uint Binding { get; init; }

    /// <summary>What it binds. Taken from the shader's plan when <see cref="Name" /> resolves.</summary>
    public DescriptorKind Kind { get; init; } = DescriptorKind.SampledTexture;

    /// <summary>The frame resource to bind, by the name the document gave it.</summary>
    public string Resource { get; init; } = string.Empty;

    /// <summary>The sampler, for a sampler binding.</summary>
    public SamplerPreset Sampler { get; init; } = SamplerPreset.None;

    /// <summary>Where in the buffer the binding starts.</summary>
    public long Offset { get; init; }

    /// <summary>How much of the buffer, or zero for the rest of it.</summary>
    public long Size { get; init; }
}

/// <summary>One effect over the whole screen — a post-process pass.</summary>
/// <remarks>
///     What makes doc 06's "the frame is data the user edits" true of post-processing rather than only
///     of geometry. Two things used to make it impossible: a binding index is a shader's decision and
///     a sampler is a device handle. A binding may now name what the shader calls it, and a sampler is
///     a preset, so neither is left.
/// </remarks>
[DataContract("FullScreen")]
public sealed record FullScreenAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The shader to run.</summary>
    public string Shader { get; init; } = string.Empty;

    /// <summary>The colour attachments it writes.</summary>
    public string[] ColourTargets { get; init; } = [];

    /// <summary>The textures it samples.</summary>
    public string[] Reads { get; init; } = [];

    /// <summary>The buffers it reads.</summary>
    public string[] BufferReads { get; init; } = [];

    /// <summary>How its output combines with what is already there.</summary>
    public BlendPreset Blend { get; init; } = BlendPreset.Opaque;

    /// <summary>What happens to the attachments at the start of the pass.</summary>
    /// <remarks>
    ///     Discarded by default, because a full-screen pass writes every pixel and clearing first is a
    ///     whole extra write — which on a tiler is a read of main memory the pass throws away.
    /// </remarks>
    public LoadAction Load { get; init; } = LoadAction.DontCare;

    /// <summary>Which binding the uniform block occupies, or null for a shader with none.</summary>
    public uint? ConstantBinding { get; init; }

    /// <summary>Which of the four conventional sets it binds.</summary>
    public DescriptorSetSlot Slot { get; init; } = DescriptorSetSlot.PerMaterial;

    /// <summary>What it binds, and where.</summary>
    public ResourceBindingAsset[] Bindings { get; init; } = [];
}

/// <summary>A compute dispatch, and the resources it declares.</summary>
/// <remarks>
///     The last node kind that was code-only. Its value over a hand-written dispatch is the two lists
///     it declares: a pass that says it writes a buffer, beside one that says it reads it, is a pass
///     the graph orders first and puts a barrier after — and a document can now say so.
/// </remarks>
[DataContract("Compute")]
public sealed record ComputeAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The compute shader to run.</summary>
    public string Shader { get; init; } = string.Empty;

    /// <summary>The textures it samples.</summary>
    public string[] Reads { get; init; } = [];

    /// <summary>The textures it writes, as storage images.</summary>
    public string[] Writes { get; init; } = [];

    /// <summary>The buffers it reads.</summary>
    public string[] BufferReads { get; init; } = [];

    /// <summary>The buffers it writes.</summary>
    public string[] BufferWrites { get; init; } = [];

    /// <summary>How many workgroups to run, across each axis.</summary>
    /// <remarks>
    ///     Three numbers rather than one vector, because that is what reads well in a document and
    ///     because a workgroup count is three independent decisions about a grid rather than a point
    ///     in space.
    /// </remarks>
    public int GroupsX { get; init; } = 1;

    /// <inheritdoc cref="GroupsX" />
    public int GroupsY { get; init; } = 1;

    /// <inheritdoc cref="GroupsX" />
    public int GroupsZ { get; init; } = 1;

    /// <summary>Which of the four conventional sets it binds.</summary>
    public DescriptorSetSlot Slot { get; init; } = DescriptorSetSlot.PerMaterial;

    /// <summary>What it binds, and where.</summary>
    public ResourceBindingAsset[] Bindings { get; init; } = [];
}

/// <summary>One stage drawn from one view.</summary>
[DataContract("SingleStage")]
public sealed record SingleStageAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The name of the view to draw from.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>The name of the stage to draw.</summary>
    public string Stage { get; init; } = string.Empty;
}

/// <summary>A directional light's cascaded shadow map.</summary>
[DataContract("ShadowMap")]
public sealed record ShadowMapAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The name of the stage that draws depth-only casters.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>The name of the depth atlas to render into.</summary>
    public string Atlas { get; init; } = string.Empty;

    /// <summary>How many cascades to fit.</summary>
    public int CascadeCount { get; init; } = 4;

    /// <summary>One cascade's side in texels.</summary>
    public int Resolution { get; init; } = 1024;

    /// <summary>How far shadows are drawn — not the camera's far plane.</summary>
    public float ShadowDistance { get; init; } = 150f;

    /// <summary>How far to blend the splits from uniform toward logarithmic.</summary>
    public float SplitLambda { get; init; } = 0.75f;

    /// <summary>How far behind a cascade the light's near plane sits.</summary>
    public float Extrusion { get; init; } = 50f;
}

/// <summary>The depth pyramid the next frame's culling tests against.</summary>
/// <remarks>
///     Placed after whatever fills depth, because what it reduces is this frame's and what consumes
///     it is next frame's <c>Cull</c> — see <see cref="HiZRenderer" />. A document with this node and
///     no <c>GpuCulling</c> builds a pyramid nothing reads, which costs a dispatch chain and is
///     otherwise harmless.
/// </remarks>
[DataContract("HiZ")]
public sealed record HiZAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The name of the depth texture to reduce.</summary>
    public string Depth { get; init; } = string.Empty;
}

/// <summary>
///     The culling dispatch, and the draw arguments it feeds.
/// </summary>
/// <remarks>
///     <para>
///         Placed at the head of the frame, before anything it decides for is drawn. The node is what
///         makes <see cref="GpuVisibilityGroup.ReadBack" /> false usable at all: with no wait, the
///         only ordering this RHI can express is a barrier between two things in one queue, so the
///         dispatch has to be recorded where the draws are.
///     </para>
///     <para>
///         <strong>What the document decides is placement; what the host supplies is the
///         resources.</strong> A visibility group holds device memory across frames and a pyramid
///         holds a frame of depth, neither of which a file can create — the same division
///         <c>Descriptors</c> and <c>Samplers</c> already have on <see cref="CompositorBuilder" />.
///         Building this node with none of them supplied is how a document says "cull on the GPU" to
///         a host that has decided not to, and it is a node that then does nothing.
///     </para>
/// </remarks>
[DataContract("GpuCulling")]
public sealed record GpuCullingAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     Whether the bits come back to the host, which is what the interface's promise costs.
    /// </summary>
    /// <remarks>
    ///     True is the safe reading: the work list is exactly what is visible and everything
    ///     downstream is unchanged. False removes the stall and makes the host's list a superset that
    ///     the draw arguments narrow — see <see cref="GpuVisibilityGroup.ReadBack" /> for why that is
    ///     opt-in rather than the default.
    /// </remarks>
    public bool ReadBack { get; init; } = true;

    /// <summary>Whether the pass also turns the bits into indirect draw arguments.</summary>
    /// <remarks>
    ///     Only meaningful with <see cref="ReadBack" /> off, where it is what removes the objects the
    ///     host recorded and the device rejected. Twenty bytes per object per view is the cost, which
    ///     is why it is a choice rather than a consequence.
    /// </remarks>
    public bool IndirectDraws { get; init; }

    /// <summary>Which of a two-phase cull's dispatches this node is.</summary>
    /// <remarks>
    ///     <para>
    ///         Left alone, a document has one culling node and one phase, which is the one-phase
    ///         culler: correct, and a frame behind on anything that stops being occluded.
    ///     </para>
    ///     <para>
    ///         Two-phase is a <em>second</em> node with <c>phase: Late</c>, placed after the draws the
    ///         main node's answer produced and after the <c>HiZ</c> node that reduced them. The
    ///         ordering is the feature — which is why it is expressed by where the node sits rather
    ///         than by a flag saying "two-phase, please" — and it is also why the late node needs no
    ///         <see cref="ReadBack" /> of its own: a late phase only exists on the in-frame path, so
    ///         declaring one is declaring that, and <see cref="CompositorBuilder" /> turns the readback
    ///         off rather than letting a document ask for two things that cannot both be true.
    ///     </para>
    /// </remarks>
    public CullPhase Phase { get; init; }
}

/// <summary>Spot and point light shadows in one atlas.</summary>
[DataContract("PunctualShadows")]
public sealed record PunctualShadowAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The name of the stage that draws depth-only casters.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>The name of the depth atlas to render into.</summary>
    public string Atlas { get; init; } = string.Empty;

    /// <summary>One tile's side in texels.</summary>
    public int Resolution { get; init; } = 512;

    /// <summary>How many tiles the atlas is across.</summary>
    public int TilesPerSide { get; init; } = 4;
}

/// <summary>
///     A whole authored frame: the stages it has, and the tree that draws them.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06's third idea, as a file. "Swap forward for deferred" is a different document
///         rather than a different build, and the three shipped presets are three of these over the
///         same features.
///     </para>
///     <para>
///         <see cref="Version" /> is checked rather than ignored. A file from a later editor is
///         refused by number, naming both versions, because the alternative is binding what it
///         understands and silently dropping what it does not — which produces a frame that is
///         missing a pass and says nothing about it.
///     </para>
/// </remarks>
[DataContract("GraphicsCompositor")]
public sealed record GraphicsCompositorAsset {
    /// <summary>The schema version this document is written in.</summary>
    /// <remarks>
    ///     Two. Version 1 named textures the host had already made; version 2 declares them, because
    ///     a document that cannot describe what flows between its passes cannot describe a
    ///     post-processing pipeline at all. There is no migration — nothing has shipped a version 1
    ///     document, and a chain that upgraded one would be a chain with nothing in it.
    /// </remarks>
    public int Version { get; init; } = 2;

    /// <summary>The stages, which nodes refer to by name.</summary>
    public RenderStageAsset[] Stages { get; init; } = [];

    /// <summary>The transient targets the frame declares, which passes refer to by name.</summary>
    public RenderResourceAsset[] Resources { get; init; } = [];

    /// <summary>The transient buffers it declares.</summary>
    public RenderBufferAsset[] Buffers { get; init; } = [];

    /// <summary>The per-view block every shader in the frame shares, or null for a frame with none.</summary>
    public ViewBlockAsset? ViewBlock { get; init; }

    /// <summary>The root of the graph — the whole frame.</summary>
    public ISceneRendererAsset? Game { get; init; }
}
