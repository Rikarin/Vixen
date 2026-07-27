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

    /// <summary>The names of resources this pass samples.</summary>
    public IList<string> Reads { get; } = [];

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
        var sampled = Reads.Select(read => frame.Texture(ToString(), read)).ToArray();

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

                pass.Execute(
                    graphContext => {
                        var context = frame.Context(graphContext.CommandList);
                        var previous = context.Output;
                        context.Output = output;

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
