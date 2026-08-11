// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;

namespace Vixen.Engine.Renderer;

/// <summary>
///     The compositor node that puts a frame's <see cref="DebugDraw" /> on the screen.
/// </summary>
/// <remarks>
///     <para>
///         <b>The missing half of a link that was complete at both ends.</b> Physics, navigation,
///         animation, the AI stack and water's six <c>water.show*</c> verbs have all been drawing into
///         the accumulator for phases; <see cref="DebugDrawRenderer" /> has been able to turn it into
///         two draw calls for just as long, with a golden image to prove it. What did not exist was
///         anything in a running game that owned one — <c>DebugDrawRenderer</c>'s only constructor
///         call in the tree was its own golden test — so every one of those producers wrote into a
///         list nothing read.
///     </para>
///     <para>
///         ⚠ <b>It declares its own pass rather than drawing inside somebody else's.</b> A node that
///         drew as a child of the final <c>!RenderPass</c> would be recorded <em>before</em> tone
///         mapping and the screen chain, so an overlay's colours would come out graded and its one-
///         pixel lines would come out through FXAA. A pass of its own over the frame's last colour
///         target, loading rather than clearing, is what puts it after everything — and it means no
///         document has to be edited to get it, which is the whole point: a project authoring its own
///         <c>.vxcompositor</c> would otherwise have to remember a node it never asked for.
///     </para>
///     <para>
///         ⚠ <b>Untested against depth, and that is a decision rather than an omission.</b> The
///         target it attaches to is a colour buffer at the end of the frame, by which point the
///         scene's depth may have been aliased away by the graph — and an overlay panel that could be
///         occluded is one that disappears exactly when something has gone wrong in front of it. A
///         host that wants world lines hidden by geometry draws them from inside the scene pass,
///         which is what the editor's viewport does.
///     </para>
///     <para>
///         ⚠ <b><see cref="DebugDrawRenderer.Upload" /> happens in the build phase, which is outside
///         every render pass.</b> That is the only place it can be: the accumulator is drained before the
///         graph starts executing and the writes must not land inside a pass.
///     </para>
/// </remarks>
public sealed class DebugOverlayRenderer : SceneRenderer, IDisposable {
    readonly IGraphicsDevice device;
    readonly LineShaders shaders;
    readonly DebugDraw draw;
    readonly RenderView view;

    DebugDrawRenderer? renderer;
    PixelFormat built = PixelFormat.Undefined;
    bool disposed;

    /// <summary>Builds the node over an accumulator and the view its world lines are seen from.</summary>
    /// <param name="device">The device the two line pipelines are created on.</param>
    /// <param name="shaders">The line stages — <see cref="LineShaders.Default" /> for a game.</param>
    /// <param name="draw">The accumulator every subsystem writes into.</param>
    /// <param name="view">The view whose camera the world geometry is drawn from.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DebugOverlayRenderer(IGraphicsDevice device, LineShaders shaders, DebugDraw draw, RenderView view) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(view);

        this.device = device;
        this.shaders = shaders;
        this.draw = draw;
        this.view = view;

        Name = "DebugOverlay";
    }

    /// <summary>The name of the colour target it draws over.</summary>
    /// <remarks>
    ///     <c>GraphicsOptions.Output</c>'s value, and it has to be that one: the frame's last colour
    ///     target is the only resource that belongs to somebody outside the graph, and drawing into
    ///     any other one is drawing into memory the graph is about to reuse.
    /// </remarks>
    public string Target { get; init; } = "SceneColour";

    /// <summary>The accumulator this drains, for a host that has to hand it to a system as well.</summary>
    public DebugDraw Draw => draw;

    /// <summary>How many world vertices the last frame held.</summary>
    public int WorldCount => renderer?.WorldCount ?? 0;

    /// <summary>How many screen vertices — the overlay panels — the last frame held.</summary>
    public int ScreenCount => renderer?.ScreenCount ?? 0;

    /// <summary>How many vertices the last frame dropped for want of room.</summary>
    /// <remarks>
    ///     Non-zero is an overlay or a subsystem producing more than the ring holds, and the picture
    ///     is missing its end rather than wrong — see <c>LineRenderer.Dropped</c> for why it is
    ///     counted instead of grown.
    /// </remarks>
    public int Dropped => renderer?.Dropped ?? 0;

    /// <summary>How many draws the last frame issued: zero, one or two.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero with geometry queued is the reading that says the pass did not run</b>, which is
    ///     the failure this whole node exists to make impossible — a frame that accumulates lines and
    ///     draws none of them looks exactly like a subsystem that produced nothing.
    /// </remarks>
    public int Draws => renderer?.Draws ?? 0;

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!draw.Enabled) {
            return;
        }

        var target = frame.Texture(ToString(), Target);
        var format = frame.FormatOf(ToString(), Target);

        // Lazily, and rebuilt when the frame's last target changes format — the pipelines are made
        // against a RenderOutput and the node cannot know one until a document has been built. A
        // format change is a reload with a different preset, which is rare and not worth caching
        // pipelines per format for.
        if (renderer is null || format != built) {
            renderer?.Dispose();

            renderer = new(device, shaders, new RenderOutput([format])) {
                // See the class remarks: the target here is a post-chain colour buffer, and the
                // scene's depth is not part of this pass.
                DepthTested = false
            };

            built = format;
        }

        // ⚠ Here, not in the pass body. This writes vertex memory, and the frame's whole point is
        // that the accumulator is drained once before anything is recorded — DebugDrawSystem then
        // ages what is left in PostRender, after this has read it.
        renderer.Upload(draw, view.Camera?.View ?? Matrix4x4.Identity, new(frame.Size.X, frame.Size.Y));

        if (renderer.WorldCount == 0 && renderer.ScreenCount == 0) {
            return;
        }

        var projection = view.ViewProjection;
        var pass = renderer;

        frame.Graph.AddPass(
            ToString(),
            builder => {
                builder.ColourAttachment(target, LoadAction.Load);

                // The frame's last target is imported rather than owned by the graph, so nothing
                // inside the graph reads what this writes and culling would remove the pass. This is
                // the case RenderGraphPass.SideEffect names in as many words.
                builder.SideEffect();

                builder.Execute(context => pass.Record(context.CommandList, projection));
            }
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        renderer?.Dispose();
        renderer = null;
    }
}
