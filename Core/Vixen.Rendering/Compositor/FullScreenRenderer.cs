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
///         cascade's casters — and no post effect has any. This draws three vertices and nothing else.
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

    /// <summary>The set it binds: its source textures, its samplers, its uniform block.</summary>
    public DescriptorBindings Descriptors { get; } = new() { Slot = DescriptorSetSlot.PerMaterial };

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

    /// <summary>The device its pipelines and its uniform block are created on.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>How many distinct pipelines this node has compiled.</summary>
    public int PipelineCount => pipelines.Count;

    /// <summary>How many times its uniform block has gone to the GPU.</summary>
    public int UploadCount => constants?.UploadCount ?? 0;

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        var device = Device ?? frame.Device;

        if (device is null || Modules is null || ColourTargets.Count == 0) {
            return;
        }

        var key = EffectKey.From(ShaderName, Parameters, PermutationKeys);

        if (frame.Effects.Resolve(key) is not { } effect) {
            // Reported through EffectSystem.Misses like every other, which is what keeps "no runtime
            // compilation in a shipping build" a test rather than a hope.
            return;
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
            return;
        }

        // Filled here rather than in the pass body, because the values are the host's and the body
        // runs inside a command list. Writing a host-visible buffer there would be a map and a copy
        // between two draws.
        constants ??= new(device, $"{this}.Constants");
        var hasConstants = ConstantBinding is not null && constants.Update(effect, Parameters);
        var bound = Descriptors.Resolve(ToString(), textures, buffers);
        var sampled = Reads.Select(name => textures[name]).ToArray();
        var consumed = BufferReads.Select(name => buffers[name]).ToArray();

        var extra = hasConstants
            ? new[] { DescriptorWrite.Uniform(ConstantBinding!.Value, constants.Buffer, 0, constants.Size) }
            : [];

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
                        bound?.Bind(context, extra);

                        // Three vertices, no vertex buffer and no index buffer. The whole reason the
                        // triangle is generated from the vertex index rather than read from memory.
                        context.CommandList.Draw(3, 1);
                    }
                );
            }
        );
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
