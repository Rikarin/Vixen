// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     One effect over the whole screen: the node every post-process pass is made of.
/// </summary>
/// <remarks>
///     <para>
///         The edge between "the compositor can express a bloom chain" and "the compositor has one".
///         Everything else in the tree draws <em>objects</em> — a stage's sorted list, a shadow
///         cascade's casters — and no post effect has any. This draws three vertices and nothing else,
///         unless <see cref="Vertices" /> and <see cref="Instances" /> say otherwise.
///     </para>
///     <para>
///         <strong>A triangle, and no vertex buffer at all.</strong> The positions come out of
///         <c>SV_VertexID</c> in <c>Library/PostFx/Fullscreen.rvn</c>, so there is nothing to bind and
///         nothing to allocate. A triangle rather than a quad because two triangles meeting across the
///         screen have a diagonal seam where the interpolators are least accurate, and because six
///         vertices to cover a rectangle is two more than are needed.
///     </para>
///     <para>
///         <strong>It declares its own pass</strong>, like every other node that needs graph
///         resources. A post effect is one effect into one target reading a handful of others, which
///         is a pass — putting several into one <see cref="RenderPassRenderer" /> would mean each
///         reading what the one before it wrote inside a pass that cannot barrier between them.
///     </para>
///     <para>
///         <strong>The pipeline cache is its own, and not <see cref="PipelineCache" />.</strong> Three
///         of that key's four parts are degenerate here: there is no vertex layout, no stage list, and
///         the "stage" is the node. What is left is the effect, the output's formats and the blend —
///         which is exactly what this is keyed by.
///     </para>
/// </remarks>
public sealed class FullScreenRenderer : SceneRenderer, IDisposable {
    readonly Dictionary<(Effect Effect, RenderOutput Output, BlendState Blend), PipelineHandle> pipelines = [];
    readonly Dictionary<string, GraphTexture> textures = new(StringComparer.Ordinal);
    readonly Dictionary<string, GraphBuffer> buffers = new(StringComparer.Ordinal);
    EffectConstants? constants;
    bool disposed;

    /// <summary>The shader to run.</summary>
    public required string ShaderName { get; init; }

    /// <summary>Its permutations, and the values its uniform block is filled from.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>Which permutation keys the shader's variants are selected by.</summary>
    public IReadOnlyList<ParameterKey> PermutationKeys { get; set; } = [];

    /// <summary>The names of the colour attachments it writes.</summary>
    public IList<string> ColourTargets { get; } = [];

    /// <summary>The names of the textures it samples.</summary>
    public IList<string> Reads { get; } = [];

    /// <summary>The names of the buffers it reads.</summary>
    public IList<string> BufferReads { get; } = [];

    /// <summary>What happens to the colour attachments at the start of the pass.</summary>
    /// <remarks>
    ///     <see cref="LoadAction.DontCare" /> by default, and that is the right default here rather
    ///     than a risky one: a full-screen pass writes every pixel of its target, so clearing it first
    ///     is a whole extra write of the attachment — which on a tiler is a read of main memory the
    ///     pass then throws away.
    /// </remarks>
    public LoadAction Load { get; set; } = LoadAction.DontCare;

    /// <summary>What to clear the attachments to, when <see cref="Load" /> clears.</summary>
    public Color4 ClearColour { get; set; }

    /// <summary>How its output combines with what is already there.</summary>
    /// <remarks>
    ///     Opaque by default. Additive is what a bloom composite or a light-streak pass wants, and it
    ///     is on the node rather than on a stage because a full-screen pass has no stage — there is
    ///     one draw and the node is all of it.
    /// </remarks>
    public BlendState Blend { get; set; } = BlendState.Opaque;

    /// <summary>The viewport, or null for the whole target.</summary>
    public Viewport? Viewport { get; set; }

    /// <summary>How many vertices the draw is, and how many instances of them.</summary>
    /// <remarks>
    ///     <para>
    ///         Three and one, which is the triangle this type is named for and what every post effect
    ///         in the engine leaves alone. They exist for the one pass that covers <em>part</em> of the
    ///         screen: <c>docs/plan/35 § D8</c>'s tile classification draws two triangles per screen
    ///         tile and one instance per tile, so that the most expensive fragment shader in the frame
    ///         runs over the tiles that have water in them instead of over the frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here says <em>where</em> those instances go</b> — that is the shader's, out
    ///         of <c>SV_InstanceID</c> and whatever the classification wrote, exactly as the triangle's
    ///         own corners come out of <c>SV_VertexID</c>. This node still binds no vertex buffer and
    ///         no index buffer, which is what keeps a tiled pass the same node as an untiled one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A partial-coverage draw wants <see cref="Load" /> set to
    ///         <see cref="LoadAction.Load" /></b>, and the default is <see cref="LoadAction.DontCare" />
    ///         because a full-screen pass writes every pixel. Left alone, the pixels no instance
    ///         covered hold whatever the allocator handed over — which on most drivers is the previous
    ///         frame and reads as smearing rather than as an uninitialised target.
    ///     </para>
    /// </remarks>
    public int Vertices { get; set; } = 3;

    /// <inheritdoc cref="Vertices" />
    public int Instances { get; set; } = 1;

    /// <summary>What fills the shader's compose slots, for a pass that has any.</summary>
    /// <remarks>
    ///     <para>
    ///         Empty for almost every post effect, because almost none of them composes anything — a
    ///         blur is a blur. <c>DistanceFieldAo</c> is the exception: it declares
    ///         <c>compose val distanceField</c> so that a project which traces nothing still compiles,
    ///         and a slot a compilation declares has to be <i>bound</i>, whether or not anything
    ///         reaches it.
    ///     </para>
    ///     <para>
    ///         <b>Without this a composing pass cannot be built at all, and fails silently.</b> The key
    ///         would carry no composition, the compiler would refuse the unbound slot, and
    ///         <see cref="EffectSystem" /> would record a miss and return nothing — so the node draws
    ///         no pixels and the frame looks like one where the pass was simply not scheduled. That is
    ///         what a material's <c>MaterialCompiler.OptionalSlots</c> does for a material, and a
    ///         full-screen pass has no material.
    ///     </para>
    /// </remarks>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The default is the pass composition rather than an empty one, and the empty one was
    ///         a black frame.</b> A compilation is the whole library, and every compose slot a shader
    ///         in it declares without a default of its own must be bound — RVN2073, and
    ///         <c>MaterialFeatures.rvn</c>'s own remarks explain why the library refuses to default
    ///         them. So a tonemap, which has no opinion whatever about a material's third surface
    ///         feature, still has to name one.
    ///     </para>
    ///     <para>
    ///         <c>DistanceFieldAoRenderer</c> and the other composing passes reached for
    ///         <see cref="Materials.MaterialCompiler.PassComposition(string, string)" /> already and
    ///         the plain ones did not, so every plain full-screen pass in every frame failed to
    ///         compile — and failed the way this type's own remarks above describe, as a node that
    ///         draws nothing rather than as an error. The tonemap is the pass that writes the
    ///         swapchain, so the whole frame went with it.
    ///     </para>
    ///     <para>
    ///         A pass with a slot it does care about overrides this with a composition naming it, and
    ///         the defaults underneath are the same either way.
    ///     </para>
    /// </remarks>
    public ShaderComposition Composition { get; set; } = Materials.MaterialCompiler.PassComposition();

    /// <summary>The set it binds: its source textures, its samplers, its uniform block.</summary>
    public DescriptorBindings Descriptors { get; } = new() { Slot = DescriptorSetSlot.PerMaterial };

    /// <summary>The frame's set 0, for a pass whose shader declares anything per-frame.</summary>
    /// <remarks>
    ///     <para>
    ///         Null for almost every post effect, because almost none of them reads anything the frame
    ///         owns — a blur reads its source and nothing else. <c>DistanceFieldAo</c> is the exception:
    ///         the clipmap it traces belongs to the frame rather than to the pass, so its volumes, its
    ///         sampler and the numbers describing them are set 0 bindings that
    ///         <c>GlobalDistanceFieldRenderer</c> fills.
    ///     </para>
    ///     <para>
    ///         <b>Nothing else in this node's path would bind it.</b> A mesh feature binds set 0 for a
    ///         geometry pass and <see cref="RenderPassRenderer" /> only puts it in the context for
    ///         children to find; a full-screen pass has no feature and no children. Without this the
    ///         pipeline declares a set nothing binds, which is a validation error at submit whether or
    ///         not the shader samples it.
    ///     </para>
    /// </remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>Where the uniform block goes, or null for an effect that has none.</summary>
    /// <remarks>
    ///     The binding index the shader gave it, which nothing yet reflects — the same seam
    ///     <see cref="ResourceBinding.Binding" /> is. The block itself is not a graph resource, so it
    ///     is not a <see cref="ResourceBinding" />: the node owns the buffer, fills it from
    ///     <see cref="Parameters" /> and appends the write.
    /// </remarks>
    public uint? ConstantBinding { get; set; }

    /// <summary>Where shader modules come from. Set before the first frame that builds.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>Where a described sampler comes from, for a binding that names one by value.</summary>
    /// <remarks>
    ///     Shared rather than owned, because a sampler is pure state and a device caps how many exist
    ///     — a chain of post passes each making its own reaches that cap on drivers that allow four
    ///     thousand.
    /// </remarks>
    public SamplerCache? Samplers { get; set; }

    /// <summary>The device its pipelines and its uniform block are created on.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How many distinct pipelines this node has compiled.</summary>
    public int PipelineCount => pipelines.Count;

    /// <summary>How many times its uniform block has gone to the GPU.</summary>
    public int UploadCount => constants?.UploadCount ?? 0;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The body is <see cref="Declare" /> so that declining has to say why.</b> This node's
    ///     documented failure was a <c>return;</c> above the resolve when <c>CompositorBuilder</c> had
    ///     left <see cref="Device" /> and <see cref="Modules" /> unset: the effect system recorded no
    ///     miss, and the tonemap — the node that writes the swapchain — silently did not run. A helper
    ///     returning <c>string?</c> cannot leave without producing an answer, and <c>return null</c> is
    ///     the healthy claim rather than the absence of one.
    /// </remarks>
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        Degrade(Declare(frame));
    }

    string? Declare(CompositorFrame frame) {
        var device = Device ?? frame.Device;

        if (device is null) {
            return "no Device and the frame has none, so this pass was never declared and whatever "
                + "was in its colour target is what the next node reads";
        }

        if (Modules is null) {
            return "no Modules, so no pipeline could be built and this pass was never declared — "
                + "whatever was in its colour target is what the next node reads";
        }

        if (ColourTargets.Count == 0) {
            return "no ColourTargets, so there is nowhere to draw and this pass was never declared";
        }

        var key = EffectKey.From(ShaderName, Parameters, PermutationKeys, Composition);

        if (frame.Effects.Resolve(key) is not { } effect) {
            // Also reported through EffectSystem.Misses, which is what keeps "no runtime compilation
            // in a shipping build" a test rather than a hope. Said here as well because a miss is a
            // list of keys and this is the node that wanted one.
            return $"the effect '{ShaderName}' did not resolve, so this pass was never declared";
        }

        var colours = new GraphTexture[ColourTargets.Count];
        var formats = new PixelFormat[ColourTargets.Count];

        for (var i = 0; i < ColourTargets.Count; i++) {
            colours[i] = frame.Texture(ToString(), ColourTargets[i]);
            formats[i] = frame.FormatOf(ToString(), ColourTargets[i]);
        }

        textures.Clear();
        buffers.Clear();

        foreach (var name in Reads) {
            textures[name] = frame.Texture(ToString(), name);
        }

        foreach (var name in BufferReads) {
            buffers[name] = frame.Buffer(ToString(), name);
        }

        if (!Descriptors.Layout.IsValid && (int)Descriptors.Slot < effect.SetLayouts.Length) {
            Descriptors.Layout = effect.SetLayouts[(int)Descriptors.Slot];
        }

        var output = new RenderOutput(formats, PixelFormat.Undefined, 1);
        var pipeline = PipelineFor(device, effect, output);

        if (!pipeline.IsValid) {
            return $"the pipeline for '{ShaderName}' is not valid — a stage module was missing or the "
                + "device refused it — so this pass was never declared";
        }

        // Filled here rather than in the pass body, because the values are the host's and the body
        // runs inside a command list. Writing a host-visible buffer there would be a map and a copy
        // between two draws.
        constants ??= new(device, $"{this}.Constants");
        var hasConstants = ConstantBinding is not null && constants.Update(effect, Parameters);
        var bound = Descriptors.Resolve(ToString(), textures, buffers, effect, Samplers);
        var sampled = Reads.Select(name => textures[name]).ToArray();
        var consumed = BufferReads.Select(name => buffers[name]).ToArray();

        // At the block's own offset, not at zero: the buffer holds one region per frame in flight so
        // that changing a value does not overwrite what an unfinished frame is reading.
        var extra = hasConstants
            ? new[] {
                DescriptorWrite.Uniform(
                    ConstantBinding!.Value,
                    constants.Buffer,
                    constants.Offset,
                    constants.Size
                )
            }
            : [];

        // Read here rather than in the body, with everything else the body closes over: a graph is
        // declared long before it runs, and a count taken at execution time would be next frame's.
        var vertices = Math.Max(Vertices, 0);
        var instances = Math.Max(Instances, 0);

        frame.Graph.AddPass(
            ToString(),
            pass => {
                foreach (var colour in colours) {
                    pass.ColourAttachment(colour, Load, ClearColour);
                }

                foreach (var read in sampled) {
                    pass.Reads(read);
                }

                foreach (var read in consumed) {
                    pass.Reads(read);
                }

                pass.Execute(
                    context => {
                        if (Viewport is { } viewport) {
                            context.CommandList.SetViewport(viewport);

                            context.CommandList.SetScissor(
                                new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height)
                            );
                        }

                        context.CommandList.BindPipeline(pipeline);

                        // Before the pass's own set, though the order does not matter to a driver —
                        // it matters to a reader, because set 0 is the frame's and set 2 is this
                        // node's, and a pass that skipped the first draws with whatever was there.
                        SceneConstants?.Bind(context.CommandList, effect);

                        bound?.Bind(context, extra);

                        // No vertex buffer and no index buffer, whatever the counts are. The whole
                        // reason the corners are generated from the vertex index rather than read
                        // from memory — and what lets a tiled pass be this node with a count on it
                        // rather than a second node.
                        context.CommandList.Draw(vertices, instances);
                    }
                );
            }
        );

        return null;
    }

    PipelineHandle PipelineFor(IGraphicsDevice device, Effect effect, in RenderOutput output) {
        var key = (effect, output, Blend);

        if (pipelines.TryGetValue(key, out var existing)) {
            return existing;
        }

        var vertex = Modules!.ModuleOf(effect, ShaderStage.Vertex);

        if (!vertex.IsValid) {
            pipelines[key] = PipelineHandle.Null;
            return PipelineHandle.Null;
        }

        var targets = new ColourTargetState[output.ColourCount];

        for (var i = 0; i < targets.Length; i++) {
            targets[i] = new(output.ColourFormats[i], Blend);
        }

        var created = device.CreateGraphicsPipeline(
            new(
                vertex,
                Modules.ModuleOf(effect, ShaderStage.Fragment),
                effect.Layout,
                targets,
                // No vertex buffers, nothing culled, no depth. A triangle covering the screen has no
                // consistent winding to cull by and nothing to test against.
                null,
                PrimitiveTopology.TriangleList,
                RasterizerState.TwoSided,
                DepthStencilState.Disabled,
                PixelFormat.Undefined,
                1,
                $"{effect.Key.ShaderName}/{this}"
            )
        );

        pipelines[key] = created;
        return created;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        constants?.Dispose();
        pipelines.Clear();
    }
}
