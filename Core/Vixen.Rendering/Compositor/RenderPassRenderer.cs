// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     A render pass, and the renderers that draw into it.
/// </summary>
/// <remarks>
///     <para>
///         The node that resolves what a stage deliberately does not: a stage is <em>which</em>
///         objects and in what order, a pass is where they land. One stage feeds several passes and
///         one pass draws several stages, so the compositor is where the two meet.
///     </para>
///     <para>
///         <strong>It declares a pass rather than opening one.</strong> Targets are named, and the
///         render graph decides what they are: how big, whether two of them can share memory,
///         whether the contents ever have to reach memory at all, what barriers precede the pass, and
///         whether the pass is worth running. A node that called <c>BeginRenderPass</c> itself would
///         be answering all of those with "I do not know".
///     </para>
///     <para>
///         <strong><see cref="Reads" /> is not optional bookkeeping.</strong> A pass that samples the
///         shadow atlas must say so, because that read is the edge that orders the shadow pass before
///         it and puts a barrier between them — and, if nothing says so, the edge that keeps the
///         shadow pass from being culled for producing something nobody wanted.
///     </para>
///     <para>
///         There is still no separate "clear" renderer: clearing is a load action on an attachment.
///         Issuing it as its own operation costs a tile-based GPU a full extra pass writing a colour
///         the next pass overwrites.
///     </para>
/// </remarks>
public sealed class RenderPassRenderer : SceneRenderer {
    /// <summary>The names of its colour attachments, in the order the shader writes them.</summary>
    public IList<string> ColourTargets { get; } = [];

    /// <summary>The name of its depth attachment, or null for a pass with none.</summary>
    public string? DepthTarget { get; set; }

    /// <summary>The names of textures this pass samples.</summary>
    public IList<string> Reads { get; } = [];

    /// <summary>The names of buffers it reads.</summary>
    /// <remarks>
    ///     Separate from <see cref="Reads" /> because a buffer and a texture are different resources
    ///     in the graph, not because a pass thinks of them differently. A forward pass reading the
    ///     cluster list a compute pass wrote is the case this exists for, and it is the same edge:
    ///     the read orders the two and puts the barrier between them.
    /// </remarks>
    public IList<string> BufferReads { get; } = [];

    /// <summary>What to do with the colour attachments at the start of the pass.</summary>
    public LoadAction Load { get; set; } = LoadAction.Clear;

    /// <summary>What to clear them to.</summary>
    public Color4 ClearColour { get; set; }

    /// <summary>What to do with depth at the start of the pass.</summary>
    public LoadAction DepthLoad { get; set; } = LoadAction.Clear;

    /// <summary>
    ///     What to clear depth to. Zero is <em>far</em> under the engine's reversed-Z convention.
    /// </summary>
    public float ClearDepth { get; set; }

    /// <summary>Whether the pass only tests depth, which lets a shader sample it at the same time.</summary>
    public bool ReadOnlyDepth { get; set; }

    /// <summary>How many samples its attachments have.</summary>
    public int SampleCount { get; set; } = 1;

    /// <summary>The viewport to set, or null for the whole target.</summary>
    public Viewport? Viewport { get; set; }

    /// <summary>The set the pass binds once, before anything under it draws.</summary>
    /// <remarks>
    ///     Where a frame's shared reads belong: the shadow atlas, the cluster list, the depth buffer a
    ///     post pass samples. Declaring the read was only ever half of it — the read orders the passes
    ///     and places the barrier, and this is what actually puts the resource in front of a shader.
    ///     <see cref="DescriptorBindings.Slot" /> should be <see cref="DescriptorSetSlot.PerView" />
    ///     or lower for a pass, so that the materials drawing into it rebind set 2 and 3 without
    ///     disturbing it.
    /// </remarks>
    public DescriptorBindings Descriptors { get; } = new() { Slot = DescriptorSetSlot.PerView };

    /// <summary>What draws into this pass.</summary>
    public IList<SceneRenderer> Children { get; } = [];

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        foreach (var child in Children) {
            if (child.Enabled) {
                child.Collect(compositor);
            }
        }
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        var colours = new GraphTexture[ColourTargets.Count];
        var formats = new PixelFormat[ColourTargets.Count];

        for (var i = 0; i < ColourTargets.Count; i++) {
            colours[i] = frame.Texture(ToString(), ColourTargets[i]);
            formats[i] = frame.FormatOf(ToString(), ColourTargets[i]);
        }

        var depth = DepthTarget is { Length: > 0 } name ? frame.Texture(ToString(), name) : GraphTexture.None;
        var depthFormat = depth.IsValid ? frame.FormatOf(ToString(), DepthTarget!) : PixelFormat.Undefined;
        var output = new RenderOutput(formats, depthFormat, SampleCount);
        var textures = Reads.ToDictionary(read => read, read => frame.Texture(ToString(), read), StringComparer.Ordinal);
        var buffers = BufferReads.ToDictionary(read => read, read => frame.Buffer(ToString(), read), StringComparer.Ordinal);
        var bound = Descriptors.Resolve(ToString(), textures, buffers);
        var sampled = Reads.Select(read => textures[read]).ToArray();
        var consumed = BufferReads.Select(read => buffers[read]).ToArray();

        frame.Graph.AddPass(
            ToString(),
            pass => {
                foreach (var colour in colours) {
                    pass.ColourAttachment(colour, Load, ClearColour);
                }

                if (depth.IsValid) {
                    pass.DepthAttachment(depth, DepthLoad, ClearDepth, readOnly: ReadOnlyDepth);
                }

                foreach (var read in sampled) {
                    pass.Reads(read);
                }

                foreach (var read in consumed) {
                    pass.Reads(read);
                }

                pass.Execute(
                    graphContext => {
                        var context = frame.Context(graphContext.CommandList);
                        var previous = context.Output;
                        context.Output = output;

                        bound?.Bind(graphContext);

                        if (Viewport is { } viewport) {
                            graphContext.CommandList.SetViewport(viewport);

                            graphContext.CommandList.SetScissor(
                                new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height)
                            );
                        }

                        foreach (var child in Children) {
                            if (child.Enabled) {
                                child.Record(compositor, context);
                            }
                        }

                        context.Output = previous;
                    }
                );
            }
        );
    }
}
