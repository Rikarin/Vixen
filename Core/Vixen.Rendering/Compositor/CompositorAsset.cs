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

    /// <summary>
    ///     The shader every draw in this stage uses instead of its material's, or empty for the
    ///     material's own.
    /// </summary>
    /// <remarks>
    ///     <c>ShadowCaster</c> is what a caster stage names, and it is the whole reason a stage may
    ///     override at all: a shadow map records depth, so a caster has no business evaluating a BRDF
    ///     — and the same mesh is drawn with its material in one stage and depth-only in another, in
    ///     the same frame. <see cref="RenderStage.ShaderName" /> has always taken it; no document
    ///     could say it.
    /// </remarks>
    public string Shader { get; init; } = string.Empty;

    /// <summary>Whether the overriding shader takes its compose slots from the material.</summary>
    /// <remarks>
    ///     False for <c>ShadowCaster</c> and <c>DepthOnly</c>, which declare no slots — handing them a
    ///     material's features splits the variant cache once per material for shaders that compile to
    ///     the same bytes. A G-buffer stage sets it, because <c>GBufferPass</c> does declare
    ///     <c>surface</c>.
    /// </remarks>
    public bool ComposeFromMaterial { get; init; }
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

    /// <summary>What happens to its colour attachments at the start of the pass.</summary>
    /// <remarks>
    ///     Clearing by default, which is what a frame's first pass wants and what
    ///     <see cref="RenderPassRenderer" /> has always done. A pass drawing on top of another's
    ///     output says <see cref="LoadAction.Load" />.
    /// </remarks>
    public LoadAction Load { get; init; } = LoadAction.Clear;

    /// <summary>
    ///     Which of the colour attachments keep what is already in them, whatever
    ///     <see cref="Load" /> says.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because a pass's attachments are not all the same kind of thing.</b> A shading
    ///         pass that accumulates into a colour a sky pass already filled has to <em>load</em> that
    ///         one, and loading the normals beside it is a read of memory no earlier pass wrote —
    ///         which the graph refuses by name rather than handing over last frame's contents.
    ///     </para>
    ///     <para>
    ///         By name rather than by index, and by exception rather than as a full list: a pass has
    ///         one opinion about most of its targets and a different one about the target the frame
    ///         is accumulating into, so <c>loaded: [SceneHdr]</c> says exactly that and stays right
    ///         when a target is added above it.
    ///     </para>
    /// </remarks>
    public string[] Loaded { get; init; } = [];

    /// <summary>
    ///     What they are cleared to, opaque.
    /// </summary>
    /// <remarks>
    ///     A <see cref="Color3" /> rather than the renderer's <c>Color4</c>, because that one carries
    ///     no <c>[DataContract]</c> and would not survive a save. Alpha is one: a colour attachment
    ///     cleared to a transparent black is a frame that composites against whatever it is presented
    ///     over, which is not a thing a pass has ever wanted here.
    ///
    ///     ⚠ This is what a frame's *background* is. A pass whose geometry does not cover the screen
    ///     shows this everywhere else — so a level with no sky renders black above its walls until
    ///     somebody sets it, and that reads as a missing pass rather than as a missing sky.
    /// </remarks>
    public Color3 ClearColour { get; init; }

    /// <summary>What happens to its depth attachment.</summary>
    public LoadAction DepthLoad { get; init; } = LoadAction.Clear;

    /// <summary>
    ///     What depth is cleared to.
    /// </summary>
    /// <remarks>
    ///     ⚠ Zero, and that is the far plane rather than the near one: the engine uses reversed depth,
    ///     so a pass clearing to one starts with everything already closer than anything it draws and
    ///     produces an empty image with no error anywhere.
    /// </remarks>
    public float ClearDepth { get; init; }

    /// <summary>Whether depth is bound read-only, for a pass that tests but does not write.</summary>
    public bool ReadOnlyDepth { get; init; }

    /// <summary>Which of the four conventional sets it binds.</summary>
    /// <remarks>
    ///     Per-view or lower for a pass, so the materials drawing into it rebind sets 2 and 3 without
    ///     disturbing what the pass put down.
    /// </remarks>
    public DescriptorSetSlot Slot { get; init; } = DescriptorSetSlot.PerView;

    /// <summary>What the pass binds once, before anything under it draws.</summary>
    public ResourceBindingAsset[] Bindings { get; init; } = [];

    /// <summary>
    ///     Frame textures the pass hands to the scene's set, for whatever draws inside it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         How the shadow atlas reaches a shading pass — see
    ///         <see cref="RenderPassRenderer.SceneTextures" /> for why the <em>consuming</em> pass is
    ///         the one entitled to publish a graph resource. The mechanism was there and no document
    ///         could reach it, which meant set 0's <c>shadowMap</c> binding could only ever be filled
    ///         from C#.
    ///     </para>
    ///     <para>
    ///         Publishing implies reading, so a name here does not also need a line in
    ///         <see cref="Reads" />.
    ///     </para>
    /// </remarks>
    public ScenePublishAsset[] SceneTextures { get; init; } = [];

    /// <summary>Frame buffers it hands to the scene's set, on the same terms.</summary>
    public ScenePublishAsset[] SceneBuffers { get; init; } = [];

    /// <summary>Which pass's names the published resources are qualified by.</summary>
    /// <remarks>
    ///     Empty leaves the renderer's own default. Assigning the empty string instead would qualify
    ///     every published name with nothing, and the set writer would look up a key no shader owns —
    ///     a frame that is dark for a reason no document mentions.
    /// </remarks>
    public string Shader { get; init; } = string.Empty;

    /// <summary>What draws into it.</summary>
    public ISceneRendererAsset[] Children { get; init; } = [];
}

/// <summary>One frame resource a pass hands to the scene's set.</summary>
/// <remarks>
///     A pair rather than a dictionary because the direction is not obvious enough to leave to
///     ordering: <see cref="Binding" /> is the <em>shader's</em> name and <see cref="Resource" /> is
///     the <em>frame's</em>, and a document that swapped them would resolve nothing and say nothing.
/// </remarks>
[DataContract("ScenePublish")]
public sealed record ScenePublishAsset {
    /// <summary>The shader's name for the binding — <c>shadowMap</c>.</summary>
    public string Binding { get; init; } = string.Empty;

    /// <summary>The frame's name for the resource that fills it — <c>ShadowAtlas</c>.</summary>
    public string Resource { get; init; } = string.Empty;
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

/// <summary>Host bytes into a buffer the frame declared.</summary>
/// <remarks>
///     <para>
///         What the document decides is <em>where</em> the copy goes and what it fills; what the host
///         supplies is the bytes — through <see cref="CompositorBuilder.Uploads" />, or through the
///         node's own <c>OnUpload</c>. That division is the same one <c>GpuCulling</c> already has,
///         and for the same reason: a file can say a histogram starts cleared, and cannot say what
///         this frame's emitters are.
///     </para>
///     <para>
///         A node whose bytes nothing ever sets declares no pass at all, which is what an authored
///         frame running against a host that has not wired it up should cost.
///     </para>
/// </remarks>
[DataContract("Upload")]
public sealed record BufferUploadAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The buffer to fill, which must be declared as a copy destination.</summary>
    public string Buffer { get; init; } = string.Empty;

    /// <summary>Where in that buffer the bytes land.</summary>
    public long Offset { get; init; }
}

/// <summary>A buffer the frame produced, back on the host.</summary>
/// <remarks>
///     The node a numeric shader gate, an auto-exposure chain and a device-side reap all needed, and
///     which none of them could write for themselves: the copy out of device-local memory is a pass,
///     and a pass that is not in the graph is a pass with no barrier between it and whatever produced
///     the value.
/// </remarks>
[DataContract("Readback")]
public sealed record BufferReadbackAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The buffer to read, which must be declared as a copy source.</summary>
    public string Buffer { get; init; } = string.Empty;

    /// <summary>Where in that buffer to start.</summary>
    public long Offset { get; init; }

    /// <summary>How many bytes, or zero for the rest of the buffer from <see cref="Offset" />.</summary>
    public long Size { get; init; }

    /// <summary>
    ///     How many frames sit between the copy and the read.
    /// </summary>
    /// <remarks>
    ///     Zero is the stall — the host submits, waits, and fetches. Anything at or above the
    ///     device's frames in flight costs nothing and is a value that many frames old, which is what
    ///     a document should normally say; see <see cref="BufferReadbackRenderer.Latency" /> for why
    ///     the range between the two buys nothing.
    /// </remarks>
    public int Latency { get; init; }
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

    /// <summary>
    ///     The view whose frustum the cascades are fitted to — the camera.
    /// </summary>
    /// <remarks>
    ///     ⚠ Empty leaves the node's own fallback camera, which looks down −Z from the origin. A
    ///     frame that forgets this fits every cascade to a camera nobody is looking through, so the
    ///     shadows are correct for a view that does not exist and absent from the one that does.
    /// </remarks>
    public string View { get; init; } = string.Empty;
}

/// <summary>The sun's shadow as a virtual map: doc 22 phase 7.</summary>
/// <remarks>
///     <see cref="ShadowMapAsset" />'s replacement rather than its sibling. Four cascades are four
///     fixed resolutions over a whole frustum, and a virtualized scene's geometry is finer than any of
///     them — so the cascade's own texel size becomes the visible limit. This fits a clipmap instead,
///     and allocates only the pages some pixel actually asked for. A document with both nodes in it
///     renders two shadow maps and shades from the virtual one wherever it has a drawn page, which is
///     the fall-through <c>ClusteredShading.Shadow</c> is written for.
/// </remarks>
[DataContract("VirtualShadow")]
public sealed record VirtualShadowAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The name of the stage that draws depth-only casters.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>Which depth resource the marking pass reads.</summary>
    public string Depth { get; init; } = "SceneDepth";

    /// <summary>The view the clipmap is centred on and whose depth is marked — the camera.</summary>
    /// <remarks>
    ///     ⚠ Empty marks nothing at all, which is a map that allocates no pages and shades nothing.
    ///     Unlike a cascade's fallback camera there is no useful default here: a clipmap is centred on
    ///     a camera by definition.
    /// </remarks>
    public string View { get; init; } = string.Empty;

    /// <summary>How many levels the clipmap has.</summary>
    public int Levels { get; init; } = 8;

    /// <summary>How wide level zero is, in world units.</summary>
    public float FirstExtent { get; init; } = 10f;

    /// <summary>How deep each level's box is along the light, which is its caster range.</summary>
    public float DepthRange { get; init; } = 400f;

    /// <summary>How many pages may be allocated, and drawn, per frame.</summary>
    public int PagesPerFrame { get; init; } = 16;
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

    /// <summary>Whether the survivors are packed into a run per batch rather than left at their slot.</summary>
    /// <remarks>
    ///     <para>
    ///         What makes one command cover a whole batch: the padded form writes a record for every
    ///         candidate and submits a command per candidate whatever culling decided, and this writes
    ///         only survivors and reads how many there were out of a buffer the host never sees.
    ///     </para>
    ///     <para>
    ///         Needs <see cref="IndirectDraws" />, and needs <c>HasDrawIndirectCount</c> on the device
    ///         — GL, WebGPU and Metal have no draw whose count comes from the device, so there the
    ///         request is answered with the padded form and the frame is exactly what it was.
    ///     </para>
    /// </remarks>
    public bool Compact { get; init; }

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

/// <summary>
///     The cluster traversal: virtualized geometry's answer to <see cref="GpuCullingAsset" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>The same division of labour, and it is the reason this exists as a node at all.</b> A
///         document decides where the traversal runs — before the draws its answer feeds, after the
///         pyramid it tests against — and a host decides whether the project has virtualized geometry.
///         Building this with nothing supplied is how a document says "walk the cluster DAG" to a host
///         that has none, and it is a node that then does nothing.
///     </para>
///     <para>
///         It carries no settings of its own, which is not an oversight. Everything the traversal is
///         parameterised by is a property of the scene rather than of the frame: the error threshold is
///         the project's quality setting on <c>VirtualGeometryRenderFeature</c>, the page budget is the
///         residency manager's, and the views are the frame's. What is left for a document to say is
///         exactly where in the frame it happens, which is what a node is.
///     </para>
/// </remarks>
[DataContract("ClusterCulling")]
public sealed record ClusterCullingAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;
}

/// <summary>
///     The visibility buffer: the draw that fills it, the binning that sorts it, and the shading.
/// </summary>
/// <remarks>
///     <para>
///         Three passes in one node, and the ordering between them is deliberately not something a
///         document can get wrong — see <see cref="VisibilityBufferRenderer.Tiles" />. What a document
///         chooses is the names: which depth this shares with the classic geometry, and which colour
///         target the resolve writes radiance into.
///     </para>
///     <para>
///         <b>The depth is named rather than created</b>, because a frame that also draws classic
///         geometry wants both in one depth buffer or the two occlude each other not at all. The colour
///         is named for the same reason and a stronger one: what a resolve writes is radiance into the
///         scene colour the forward pass and every post-effect already share, which is the whole of
///         improvement 2 in <c>docs/plan/22-virtualized-geometry.md</c>. A resolve with a target of its
///         own would be a second colour buffer to composite, and compositing it is what a GBuffer
///         resolve does.
///     </para>
/// </remarks>
[DataContract("VisibilityBuffer")]
public sealed record VisibilityBufferAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>What the identity target is called in the graph. Created by this node.</summary>
    /// <remarks>
    ///     The one resource here the node owns, because nothing else produces it: one <c>uint</c> per
    ///     pixel naming a visible cluster and a triangle, which only this pass writes and only its own
    ///     resolve reads. Naming it anyway lets a debug view read it.
    /// </remarks>
    public string Output { get; init; } = "VisibilityBuffer";

    /// <summary>The depth this shares with whatever else draws geometry. Named, not created.</summary>
    public string Depth { get; init; } = "SceneDepth";

    /// <summary>The scene colour the resolve adds radiance to. Named, not created.</summary>
    public string Colour { get; init; } = "SceneColour";

    /// <summary>The view it draws from, by the name the document's views are known by.</summary>
    /// <remarks>
    ///     A virtualized document has no <c>SingleStage</c> in it — a cluster draw is not a stage — so
    ///     this is the only place a view enters the frame. A document that names none collects none, and
    ///     the traversal then has nothing to choose a cut for.
    /// </remarks>
    public string View { get; init; } = string.Empty;

    /// <summary>Which of the frame's views it draws.</summary>
    public int ViewIndex { get; init; }

    /// <summary>Which stages' objects it draws, by name. Empty means every stage.</summary>
    /// <remarks>
    ///     The traversal filters instances by the same stage intersection the object cull uses, and
    ///     this is the view's half of it. Empty is every stage rather than none, because a cluster draw
    ///     is not a stage — there is no per-stage command a narrower default would correspond to — and
    ///     a mask of none is a visibility buffer that is permanently, silently empty. See
    ///     <see cref="VisibilityBufferRenderer.Stages" />.
    /// </remarks>
    public string[] Stages { get; init; } = [];
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

    /// <summary>
    ///     Which passes' compose slots the atlas is published under.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Empty renders the atlas and shows it to nobody</b>, which is what this node did
    ///         for its whole life before there was a shader that could read one. The entries are
    ///         qualified — the pass, then the shader filling its slot, as in
    ///         <c>ForwardPlus.PunctualShadowAtlas</c> — because a composed slot's bindings are named
    ///         for what fills it.
    ///     </para>
    ///     <para>
    ///         And naming a pass here is only half of it: the pass has to compose the slot too, or the
    ///         bindings are written under a prefix no variant declares and resolve to nothing.
    ///         <see cref="Materials.MaterialCompiler.ForwardPunctualShadowSlot" /> is the other half.
    ///     </para>
    /// </remarks>
    public string[] Passes { get; init; } = [];

    /// <summary>How far the depth comparison is nudged, in depth units.</summary>
    public float ConstantBias { get; init; } = 0.0015f;

    /// <summary>How much more of that a surface gets as it turns away from the light.</summary>
    public float SlopeBias { get; init; } = 0.004f;
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

    /// <summary>
    ///     Whether the frame draws the GPU-driven way, or null for the way every device can.
    /// </summary>
    /// <remarks>
    ///     Here rather than on a node because it is not a pass: it is where a material's values live,
    ///     where an object's matrix lives, and therefore whether any two draws in the frame can be one
    ///     command. A node could not say it — the answer has to be the same for every pass that draws.
    /// </remarks>
    public GpuDrivenAsset? GpuDriven { get; init; }

    /// <summary>The root of the graph — the whole frame.</summary>
    public ISceneRendererAsset? Game { get; init; }

    /// <summary>The frame a project with no compositor of its own is drawn with.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>One opaque stage into a colour and a depth target, and nothing else.</b> It is what
    ///         makes "a new project renders something" true: a host with no <c>.vxcompositor</c> to
    ///         load has no frame at all, and the difference between that and a broken renderer is
    ///         invisible from the outside — a black window either way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here rather than in a head, because there are two heads and they must agree.</b>
    ///         A game falling back to one default and an editor falling back to another would make
    ///         the viewport disagree with the build for every project that had not authored a frame —
    ///         which is exactly the projects most likely to be looking at the viewport to find out
    ///         what their scene looks like.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A property rather than a static field, because the asset holds arrays.</b> A
    ///         shared instance is a shared <c>Stages</c> array, and a caller that sorted or replaced
    ///         one element of it would change what every later default is.
    ///     </para>
    /// </remarks>
    public static GraphicsCompositorAsset Default => new() {
        Version = CompositorBuilder.SupportedVersion,
        Stages = [new() { Name = "Opaque" }],
        Resources = [
            new() { Name = "SceneColour", Format = PixelFormat.Bgra8UNormSrgb },
            new() {
                Name = "SceneDepth",
                Format = PixelFormat.Depth32Float,
                Usage = TextureUsage.DepthStencilTarget
            }
        ],
        Game = new SequenceAsset {
            Name = "Frame",
            Children = [
                new RenderPassAsset {
                    Name = "Main",
                    ColourTargets = ["SceneColour"],
                    DepthTarget = "SceneDepth",
                    Children = [new SingleStageAsset { Name = "Opaque", View = "Camera", Stage = "Opaque" }]
                }
            ]
        }
    };
}

/// <summary>
///     What a document asks for when it wants draws merged, and what a device may refuse.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Every flag here is a request, not a setting.</strong> Each one is gated on a
///         capability inside the feature that implements it — <c>HasBindless</c> for records,
///         <c>HasDrawIndirectCount</c> for compaction and transforms — so a document that asks for
///         all of it runs unchanged on GL, on WebGL2 and on MoltenVK below argument-buffer tier 2,
///         and draws the same image through a descriptor set per material. That is the reason a
///         document may ask at all: if asking could break a target, it would have to be a build
///         configuration instead.
///     </para>
///     <para>
///         ⚠ <strong>The pieces are separable and mostly should not be separated.</strong>
///         Compaction without records merges nothing, because objects still bind a set each; records
///         without compaction remove binds and leave the command per object. They are separate flags
///         because they fail independently on real hardware, not because a project should pick and
///         choose.
///     </para>
/// </remarks>
[DataContract("GpuDriven")]
public sealed record GpuDrivenAsset {
    /// <summary>
    ///     Which pass's permutations to set, since the keys are the shader's own.
    /// </summary>
    /// <remarks>
    ///     A shader that does not declare them is simply never asked for the variant, which is what a
    ///     document naming the wrong pass gets: the ordinary path, and no error. Naming it here rather
    ///     than searching every loaded shader keeps the answer a document's rather than a scan's.
    /// </remarks>
    public string Shader { get; init; } = "ForwardPlus";

    /// <summary>Whether a material's values are a record of one buffer rather than a set per draw.</summary>
    public bool MaterialRecords { get; init; }

    /// <summary>Whether an object's world matrix is a record rather than a push constant.</summary>
    /// <remarks>
    ///     Worth nothing without <see cref="GpuCullingAsset.Compact" />, and the feature says so
    ///     itself: with no merged command to gain, a buffer read per vertex is a straight loss against
    ///     a constant already in the command stream, so it declines to turn on.
    /// </remarks>
    public bool TransformRecords { get; init; }
}

/// <summary>The camera-following signed-distance clipmap the frame's traces march.</summary>
/// <remarks>
///     <para>
///         [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L1's node, and it was the
///         one part of that document a project could not reach. Every renderer in the chain existed
///         and none of them had an asset, so a game could have dynamic global illumination only by
///         building its compositor in C# — which is precisely the thing
///         <c>docs/plan/06</c> § Compositor made an asset so that a game would not have to.
///     </para>
///     <para>
///         <b>The field itself is the host's, on exactly the terms
///         <see cref="ClusterCullingAsset" />'s traversal is.</b> A <c>GlobalDistanceField</c> owns
///         volume textures that outlive a frame and a residency the camera drives; a document cannot
///         create one and should not try. What a document says is <i>where in the frame</i> the
///         clipmap is composited, which is what a node is. A project that supplies no field gets a
///         node that does nothing — the same answer the virtualized path gives a project with no
///         virtualized meshes.
///     </para>
/// </remarks>
[DataContract("GlobalDistanceField")]
public sealed record GlobalDistanceFieldAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The shader whose compose slot the clipmap's bindings are written under.</summary>
    /// <remarks>
    ///     ⚠ <b>This name is a contract with every pass that marches the field.</b>
    ///     <c>DistanceFieldAo</c>'s <c>Source</c> and this have to be the same string, because it is
    ///     the compose-slot prefix one writes and the other reads. They are not derived from each
    ///     other because a frame may march a field this node does not composite.
    /// </remarks>
    public string Shader { get; init; } = "DistanceFieldAo.GlobalDistanceField";

    /// <summary>Any further prefixes the same clipmap is written under.</summary>
    /// <remarks>
    ///     <para>
    ///         One clipmap can have more than one consumer, and the one worth having is the shading
    ///         pass: <c>ForwardPlus.GlobalDistanceField</c> hands the field to the material, which
    ///         marches it for ambient occlusion and multiplies the answer into its indirect term.
    ///         That is the only place occlusion can be applied to indirect light and not to direct,
    ///         because a forward pass has already summed the two by the time any screen-space pass
    ///         could run.
    ///     </para>
    ///     <para>
    ///         ⚠ It is one of three lines and none of them works alone: this fills the bindings, the
    ///         material's composition names <c>GlobalDistanceField</c> behind
    ///         <c>MaterialCompiler.ForwardDistanceFieldSlot</c>, and
    ///         <c>ForwardPlus.UseDistanceFieldOcclusion</c> compiles the march. Bindings without a
    ///         composition go nowhere; a composition without bindings is a set the writer fills
    ///         partially, which is every draw in the pass refused.
    ///     </para>
    /// </remarks>
    public string[] Passes { get; init; } = [];

    /// <summary>Whether the composite may use more than one thread.</summary>
    public bool Parallel { get; init; } = true;
}

/// <summary>The irradiance field whose probes carry the scene's bounced light.</summary>
/// <remarks>
///     <para>
///         [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L2's node, on the same
///         terms as <see cref="GlobalDistanceFieldAsset" />: the field, its filler and its refinement
///         policy are the host's, because they own device memory and a probe budget that outlive any
///         one frame, and what a document chooses is where the fill happens and how much of it happens
///         per frame.
///     </para>
///     <para>
///         A project that supplies no field gets a node that does nothing, and
///         <c>IndirectDiffuse</c>'s default <c>Source</c> answers "no indirect light, and the sun is
///         not shadowed" — which its own remarks are careful to point out are two different right
///         answers rather than one convenient zero.
///     </para>
/// </remarks>
[DataContract("IrradianceField")]
public sealed record IrradianceFieldAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The shader whose compose slot the field's bindings are written under.</summary>
    /// <remarks>
    ///     The counterpart of <see cref="GlobalDistanceFieldAsset.Shader" />, and it pairs with
    ///     <c>IndirectDiffuse</c>'s <c>Source</c> in exactly the same way. Empty takes the material
    ///     compiler's own name for the field shader.
    /// </remarks>
    public string Shader { get; init; } = string.Empty;

    /// <summary>How many probes are filled per frame.</summary>
    /// <remarks>
    ///     The whole of the quality-against-cost decision, and the reason it is a document's rather
    ///     than a constant: a field settles over several frames, so a higher budget converges sooner
    ///     and costs more per frame, and which of those a project wants is not something the engine
    ///     can know.
    /// </remarks>
    public int Budget { get; init; } = 8;

    /// <summary>How many times a filled probe's irradiance is dilated into its unfilled neighbours.</summary>
    public int DilationPasses { get; init; } = 1;

    /// <summary>
    ///     Which passes read the field, by their shader names.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Empty leaves <see cref="IrradianceFieldRenderer.Passes" />' own default, which is
    ///         <c>IndirectDiffuse</c> alone — the consumer that needs no material change.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Adding <c>ForwardPlus</c> here is what lets a material read the field, and a
    ///         forward material compiled with <c>UseIrradianceField</c> on and this list without it
    ///         does not draw dimly — it does not draw at all.</b> The permutation makes the shader
    ///         declare five volumes and two samplers in set 0, and <see cref="EffectSetWriter" />
    ///         writes every binding of a set or none. So the material's permutation and this list are
    ///         one decision written in two files, and the pass that fills the slot is the one that has
    ///         to be named here.
    ///     </para>
    /// </remarks>
    public string[] Passes { get; init; } = [];
}
