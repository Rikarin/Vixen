// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Ui.Renderer;

namespace Vixen.Editor.App;

/// <summary>Renders the scene through a <see cref="GraphicsCompositor" /> and hands the interface the result.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ScenePresenter" />'s three calls, over the standard renderer instead of the
///         tool one.</b> <c>EditorHost.Record</c> talks to a presenter through exactly
///         <c>Resize</c>, <c>Upload</c> and <c>Declare</c>, so what changes a pane from "the editor's
///         own shapes, lit by one key direction" to "the frame a game would draw" is a second type
///         implementing those three — not a mode flag on the first, which would be one object holding
///         two renderers and two answers for every question asked of it.
///     </para>
///     <para>
///         ⚠ <b>The frame is built into the window's graph, not into one of its own.</b>
///         <c>GraphicsCompositor.Build</c> takes a <see cref="RenderGraph" />, resets nothing and
///         executes nothing — so the pane hands it the same graph <c>EditorHost</c> declares its
///         interface pass into, and the barrier between the frame's last write and the interface
///         sampling it is derived rather than placed by hand. A second graph would produce the same
///         pixels and give the interface's pass nothing to declare a read against.
///     </para>
///     <para>
///         ⚠ <b>The pane lends the frame its own two targets, and gets the depth back for nothing.</b>
///         An import wins over a resource the document declares by the same name, and the default
///         frame calls its two <c>SceneColour</c> and <c>SceneDepth</c> — which are exactly the two
///         textures a presenter already owns. So the colour the interface samples <em>is</em> the
///         frame's output, and the depth the frame wrote is a real attachment the tool overlay can
///         test against.
///     </para>
///     <para>
///         ⚠ <b>The tools are a node appended to the built tree, and it is the depth link that makes
///         them a viewport rather than a decal.</b>
///         <c>Platform/Vixen.Graphics.Golden.Tests/ViewportOverlayImageTests</c> renders the
///         arrangement and renders the sabotage beside it: a <see cref="RenderPassRenderer" /> that
///         <em>loads</em> the frame's colour and <em>loads</em> its depth rather than clearing either,
///         declaring <c>ReadOnlyDepth</c> because <c>LineRenderer</c>'s pipeline is
///         <c>DepthStencilState.TestOnly</c> and the attachment therefore stays readable after it.
///         The same placement <c>SceneRenderHost.Load</c> already uses for <c>DebugOverlayRenderer</c>.
///     </para>
///     <para>
///         ⚠ <b>What it does not draw is what the tool presenter draws with device geometry</b>: the
///         block-out shapes, the terrain, the vegetation and the water. Those are the tool renderer's
///         preview of things that reach a compositor-driven frame through extraction instead, and
///         drawing them here as well would be one surface composited twice.
///     </para>
/// </remarks>
sealed class FramePresenter : IDisposable {
    /// <summary>What the frame document calls the colour the first pane's interface samples.</summary>
    public const string ColourTarget = "SceneColour";

    /// <summary>And what it calls the depth that pane's tools test against.</summary>
    public const string DepthTarget = "SceneDepth";

    /// <summary>What the document calls a given pane's colour.</summary>
    /// <remarks>
    ///     ⚠ <b>The first pane's name is unsuffixed and that is load-bearing.</b> A project's own
    ///     <c>.vxcompositor</c> names <c>SceneColour</c> and knows nothing about panes, so the pane an
    ///     authored frame draws into has to be the one whose target carries that name — see
    ///     <c>EditorWorldRenderer.Trees</c>.
    /// </remarks>
    public static string Colour(int pane) => pane == 0 ? ColourTarget : ColourTarget + pane.ToString();

    /// <summary>And its depth.</summary>
    /// <inheritdoc cref="Colour" path="/remarks" />
    public static string Depth(int pane) => pane == 0 ? DepthTarget : DepthTarget + pane.ToString();

    /// <summary>The depth format, which is the engine's reversed-Z one.</summary>
    public const PixelFormat DepthFormat = PixelFormat.Depth32Float;

    /// <summary>What the graded target is.</summary>
    /// <remarks>
    ///     ⚠ <b>A colour format the swapchain's is not.</b> The frame is sampled by the interface
    ///     rather than presented, so it wants a linear target and the grade encodes to sRGB itself —
    ///     a UNorm-sRGB one would be decoded on the way into the interface's shader and encoded again
    ///     on the way out, which is a scene visibly washed out next to the panels around it.
    /// </remarks>
    public const PixelFormat ColourFormat = PixelFormat.Rgba8UNorm;

    /// <summary>What the pass this appends is called, in a degradation report and in a profile.</summary>
    const string OverlayNode = "Tools";

    readonly IGraphicsDevice device;
    readonly EditorWorldRenderer world;

    /// <summary>Which pane this is, which is what every name in the frame is keyed by.</summary>
    /// <remarks>
    ///     ⚠ <b>The view, the colour, the depth and the linear target in between are all this pane's,
    ///     and the number is how the document names them.</b> Two presenters sharing an index would
    ///     be two panes lending the frame one pair of textures and aiming one view — the arrangement
    ///     where four panes in one mode look different from each other because only the last one
    ///     wrote.
    /// </remarks>
    readonly int pane;

    /// <summary>The document's names for this pane's two targets, made once.</summary>
    readonly string colourTarget;
    readonly string depthTarget;

    /// <summary>The depth-tested lines: the grid, the markers, the parent lines.</summary>
    readonly LineRenderer lines;

    /// <summary>The gizmo's shafts, in a renderer of their own so they escape the depth test.</summary>
    /// <remarks><see cref="ScenePresenter.overlay" />'s argument exactly: one buffer, one pipeline.</remarks>
    readonly LineRenderer overlay;

    /// <summary>The gizmo's solid heads, which are triangles and cannot share a line buffer.</summary>
    readonly MeshRenderer handles;

    readonly SceneLines geometry = new();
    readonly List<LineVertex> pending = [];
    readonly List<(string Node, string Reason)> degradations = [];

    /// <summary>Each mode's tree with this pane's tool pass after it, made once per tree.</summary>
    readonly Dictionary<SceneRenderer, SceneRenderer> tooled = [];

    TextureHandle colour;
    TextureViewHandle colourView;
    TextureHandle depth;
    TextureViewHandle depthView;
    Int2 size;

    /// <summary>This frame's camera, held between <see cref="Upload" /> and the overlay's record.</summary>
    Matrix4x4 viewProjection = Matrix4x4.Identity;

    /// <summary>The mesh feature's running totals as of the last frame, so the pane can report deltas.</summary>
    int lastDraws;
    long lastIndices;

    bool disposed;

    /// <summary>What the interface calls this presenter's texture.</summary>
    /// <inheritdoc cref="ScenePresenter.Image" />
    public ulong Image { get; }

    /// <summary>The colour format the target, the frame and the tool pipelines agree on.</summary>
    public PixelFormat Format { get; }

    /// <summary>How wide the target is, in render pixels.</summary>
    public int Width => size.X;

    /// <summary>How tall it is.</summary>
    public int Height => size.Y;

    /// <summary>Whether there is a target to draw into.</summary>
    public bool IsReady => colourView.IsValid;

    /// <summary>Builds the pane's tool pipelines and appends its overlay to the frame.</summary>
    /// <param name="device">The device.</param>
    /// <param name="world">The renderer whose frame this presents. Not owned.</param>
    /// <param name="shaders">The two line stages.</param>
    /// <param name="meshShaders">The two mesh stages, for the gizmo's solid handles.</param>
    /// <param name="format">What the target's colour format is.</param>
    /// <param name="image">What the interface calls this presenter's texture. Unique per pane.</param>
    /// <param name="pane">
    ///     Which pane this is, in the scene panel's reading order. It selects the view the frame is
    ///     drawn from and the names the frame's targets are lent under, so two presenters may not
    ///     share one.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no such pane, or the image id is zero.</exception>
    public FramePresenter(
        IGraphicsDevice device,
        EditorWorldRenderer world,
        LineShaders shaders,
        MeshShaders meshShaders,
        PixelFormat format,
        ulong image,
        int pane = 0
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfZero(image);
        ArgumentOutOfRangeException.ThrowIfNegative(pane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pane, EditorWorldRenderer.MaxPanes);

        this.device = device;
        this.world = world;
        this.pane = pane;

        colourTarget = Colour(pane);
        depthTarget = Depth(pane);

        Format = format;
        Image = image;

        // ⚠ The output the tool pipelines are built against has to be the pane's own, and both halves
        // of it: a line pipeline created for a depth format the pass does not have is one the driver
        // refuses, which in a viewport is a grid that stops appearing the day somebody changes the
        // frame's depth buffer.
        var output = new RenderOutput([format], DepthFormat);

        lines = new(device, shaders, output);
        overlay = new(device, shaders, output);

        // A few hundred vertices at most: three arm heads of a dozen segments each, and the ball.
        handles = new(device, meshShaders, output, 4096, 8192);
    }

    /// <summary>The mode's tree, with this pane's tool pass at the end of it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Appended to the built tree rather than written into the document</b>, which is the
    ///         argument <c>SceneRenderHost.Append</c> makes for the debug overlay and it holds harder
    ///         here: a document that round-tripped through the editor having gained the editor's own
    ///         gizmo pass would be a project whose shipped frame draws editor tools.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Load</c> on both attachments, which is the whole of the arrangement.</b> The
    ///         colour is the frame's finished picture and the depth is what the frame wrote — a pass
    ///         that cleared either would wipe the composition or throw away the occlusion that makes a
    ///         marker behind a wall read as being behind it.
    ///     </para>
    /// </remarks>
    SceneRenderer Tooled(SceneRenderer tree) {
        if (tooled.TryGetValue(tree, out var existing)) {
            return existing;
        }

        var pass = new RenderPassRenderer {
            // ⚠ Named per pane, because a degradation report and a profile timeline name a node —
            // and four passes called "Tools" is a timeline that cannot say which pane stalled.
            Name = pane == 0 ? OverlayNode : $"{OverlayNode} {pane}",
            Load = LoadAction.Load,
            DepthTarget = depthTarget,
            DepthLoad = LoadAction.Load,

            // Tested and never written — every pipeline recorded below is `TestOnly` or untested —
            // so the pass can say so and the attachment stays readable by anything after it.
            ReadOnlyDepth = true
        };

        pass.ColourTargets.Add(colourTarget);

        pass.Children.Add(
            new DelegateSceneRenderer {
                OnRecord = (_, context) => Record(context.CommandList)
            }
        );

        // ⚠ Wrapped rather than appended into the mode's own sequence, and memoised per tree. The
        // trees are the builder's — subtrees of the one built document — so appending would edit the
        // frame the document describes, and a second pane doing the same would give one tree two
        // tool passes drawing the other pane's gizmo.
        var wrapped = new SceneRendererSequence { Name = tree.Name };

        wrapped.Children.Add(tree);
        wrapped.Children.Add(pass);

        tooled[tree] = wrapped;

        return wrapped;
    }

    /// <summary>Makes the target match the viewport, and re-registers it if it changed.</summary>
    /// <param name="viewport">The pane.</param>
    /// <param name="renderer">The interface's renderer, which holds the registration.</param>
    /// <returns>Whether there is a target to draw into.</returns>
    /// <inheritdoc cref="ScenePresenter.Resize" path="/remarks" />
    public bool Resize(SceneViewport viewport, UiRenderer renderer) {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(renderer);
        ObjectDisposedException.ThrowIf(disposed, this);

        var wanted = new Int2(viewport.Control.RenderWidth, viewport.Control.RenderHeight);

        if (wanted.X <= 0 || wanted.Y <= 0) {
            // A collapsed dock panel, a hidden tab, the frame before the first layout. A zero-sized
            // texture is what a device refuses to make, so there is nothing to do but wait.
            return IsReady;
        }

        if (wanted == size && IsReady) {
            return true;
        }

        Release();

        size = wanted;

        colour = device.CreateTexture(Describe());
        colourView = device.CreateTextureView(colour);
        depth = device.CreateTexture(DescribeDepth());
        depthView = device.CreateTextureView(depth);

        // ⚠ Released and re-registered rather than unregistered and registered afresh, for the reason
        // `ScenePresenter.Resize` gives at length: unregistering destroys the number's descriptor
        // sets, this runs once a frame for as long as a splitter is being dragged, and the backend's
        // pools are deliberately created without `FreeDescriptorSetBit`.
        renderer.RegisterImage(Image, colourView);
        viewport.Control.RenderTarget = Image;

        // ⚠ The frame's reference size is *not* written here any more, and that is the whole of what
        // N panes cost the arrangement. A compositor has one reference size and four panes have four
        // extents, so a presenter writing its own would leave the frame sized by whichever pane
        // resized last — and the linear target a pane attaches beside its own colour would then be
        // another pane's size, which is a framebuffer the driver refuses rather than a picture that
        // is wrong. `EditorWorldRenderer.Size` gives this pane's targets their explicit extent and
        // `Compose` writes the reference size once, from the largest pane.
        viewport.Picking?.Abandon();

        return true;
    }

    TextureDescription Describe() =>
        new(Format, size.X, size.Y, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "frame colour");

    TextureDescription DescribeDepth() =>
        new(DepthFormat, size.X, size.Y, TextureUsage.DepthStencilTarget, Name: "frame depth");

    /// <summary>Runs the frame's prologue and collects the tools' geometry.</summary>
    /// <param name="commands">The frame's list, open and outside a render pass.</param>
    /// <param name="document">The scene, for the grid, the markers and the gizmo.</param>
    /// <param name="viewport">The pane.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>WorldRenderer.Draw</c> is what makes the frame's own inputs arrive</b>, and
    ///         two of the things it does are silent when they do not happen.
    ///         <c>MaterialDescriptors.BeginFrame</c> is the per-frame pool's boundary — without it
    ///         the cache hands frame N+1 a set still pointing at frame N's transient textures — and
    ///         <c>GeometryResidency.Flush</c> is what copies the vertices and indices themselves, so
    ///         a frame without it draws the right counts at the right offsets out of memory nothing
    ///         wrote.
    ///     </para>
    ///     <para>
    ///         It declares nothing, because this renderer's host holds no compositor of its own —
    ///         see <see cref="EditorWorldRenderer" />'s constructor for why that is the arrangement
    ///         rather than an omission.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Outside the render pass, like everything else here.</b> This writes buffers and
    ///         stages a texture, and a Vulkan command list may not transfer inside a pass.
    ///     </para>
    /// </remarks>
    public void Upload(ICommandList commands, SceneDocument document, SceneViewport viewport) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewport);
        ObjectDisposedException.ThrowIf(disposed, this);

        var aspect = size.Y <= 0 ? 1f : (float)size.X / size.Y;

        // ⚠ This pane's view and no other's, and before the build: the frame's culling is against
        // this frustum and the collect that fills it runs inside `Compose`. `Aim` also advances the
        // view's history, which is what makes a motion vector measure this frame against last frame.
        world.Aim(pane, viewport.Camera, aspect);

        viewProjection = viewport.Camera.ViewProjection(aspect);

        // ⚠ The prologue is not here any more. `EditorWorldRenderer.Begin` runs it once a frame,
        // before any pane, because `MaterialDescriptors.BeginFrame` recycles every set handed out
        // since the last call — a second call between two panes hands pane two the sets pane one's
        // passes are still going to bind when the graph executes.
        geometry.Build(document, viewport, size.Y);

        Write(lines, geometry.World);
        Write(overlay, geometry.Overlay);

        // ⚠ The gizmo's key light follows the camera, and this is the half of it that reaches the
        // GPU. `GizmoGeometry` shades the arm shafts on the CPU from the same call and the same
        // ambient; a frame that set one and not the other draws a cone lit from the side its own arm
        // is dark on.
        handles.LightDirection = GizmoGeometry.KeyLight(viewport.Camera);
        handles.Ambient = GizmoGeometry.Ambient;
        handles.Upload(geometry.Handles, geometry.HandleIndices);

        var stats = viewport.Stats;

        // ⚠ The frame's counts rather than the collector's, because this pane's geometry is the
        // render system's: `ObjectCount` is what extraction put in the store, which is the number
        // that distinguishes an empty scene from one whose meshes have not been imported yet.
        stats.Entities = world.ObjectCount;
        stats.Segments = (geometry.World.Count + geometry.Overlay.Count) / 2;

        // ⚠ Differences, because `MeshRenderFeature` counts across the renderer's whole life rather
        // than per frame. Read as totals they are a triangle count that climbs for ever — an overlay
        // that says "125 720 tris" over two crates, which reads as a scene far heavier than it is and
        // hides the moment a level actually gets heavy.
        // ⚠ And the difference is the *frame's* rather than this pane's, which is what one counter
        // shared by four panes can honestly give: the feature counts when the graph executes, which
        // is after every pane has read this. So a quad layout reports the same number in all four
        // overlays and that number is the whole frame's. Per-pane counts want a counter per view,
        // which the feature does not have.
        var draws = world.Renderer.Meshes.DrawCount;
        var indices = world.Renderer.Meshes.IndexCount;

        stats.Draws = draws - lastDraws;
        stats.Triangles = (int)((indices - lastIndices) / 3);

        lastDraws = draws;
        lastIndices = indices;
    }

    /// <summary>Declares the frame's passes, and the tool pass over them.</summary>
    /// <param name="graph">The window's graph.</param>
    /// <param name="viewport">The pane, for the mode whose tree this frame is.</param>
    /// <param name="texture">What the interface's pass has to declare that it reads.</param>
    /// <returns>Whether a frame was declared.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The imports are rewritten every frame rather than set once.</b> A
    ///         <see cref="TextureViewHandle" /> is what a resize replaces, and an import still naming
    ///         the view a resize destroyed is a frame attaching freed memory — which is undefined
    ///         behaviour rather than an error.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the tree is chosen every frame, which is the whole of the view-mode switch.</b>
    ///         <c>Build</c>, <c>Collect</c> and <c>Degradations</c> all walk from <c>Game</c> down, so
    ///         pointing it at another subtree is a different frame with no rebuild — and a stage no
    ///         installed node asked for is a stage nothing collects into.
    ///     </para>
    /// </remarks>
    public bool Declare(RenderGraph graph, SceneViewport viewport, out GraphTexture texture) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(viewport);
        ObjectDisposedException.ThrowIf(disposed, this);

        texture = default;

        if (!Prepare(viewport, out var tree)) {
            return false;
        }

        return Take(world.Compose(graph, [tree], new(Width, Height), device.WaitIdle), out texture);
    }

    /// <summary>Lends the frame this pane's targets and hands back the tree it wants drawn.</summary>
    /// <param name="viewport">The pane, for the mode whose tree this frame is.</param>
    /// <param name="tree">The mode's tree with this pane's tool pass after it.</param>
    /// <returns>Whether this pane has anything to contribute.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewport" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half of <see cref="Declare" /> that a quad layout runs four times, before the
    ///         one build.</b> Every pane has to have lent its imports before <c>Build</c> reads them,
    ///         because a build is a snapshot: an import written afterwards reaches the next frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The imports are rewritten every frame rather than set once.</b> A
    ///         <see cref="TextureViewHandle" /> is what a resize replaces, and an import still naming
    ///         the view a resize destroyed is a frame attaching freed memory — which is undefined
    ///         behaviour rather than an error.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the tree is chosen every frame, which is the whole of the view-mode switch.</b>
    ///         <c>Build</c>, <c>Collect</c> and <c>Degradations</c> all walk from <c>Game</c> down, so
    ///         contributing another subtree is a different frame with no rebuild — and a stage no
    ///         installed node asked for is a stage nothing collects into.
    ///     </para>
    /// </remarks>
    public bool Prepare(SceneViewport viewport, [NotNullWhen(true)] out SceneRenderer? tree) {
        ArgumentNullException.ThrowIfNull(viewport);
        ObjectDisposedException.ThrowIf(disposed, this);

        tree = null;

        if (!IsReady || viewport.Modes.Resolve() is not { } mode) {
            return false;
        }

        world.Compositor.Imports[colourTarget] = new(
            colour,
            colourView,
            Describe(),
            ResourceState.Undefined,

            // Left ready to be sampled, which is what the interface's pass does with it in the same
            // frame. The graph places the barrier from this rather than from anyone remembering to.
            ResourceState.ShaderRead
        );

        world.Compositor.Imports[depthTarget] = new(
            depth,
            depthView,
            DescribeDepth(),
            ResourceState.Undefined,
            ResourceState.DepthStencilWrite
        );

        // ⚠ The linear target between them is the graph's, so it is sized rather than lent — and it
        // has to be sized to this pane, not to the frame. See `EditorWorldRenderer.Size`.
        world.Size(pane, size);

        tree = Tooled(mode);

        return true;
    }

    /// <summary>Takes this pane's finished colour out of the frame every pane was built into.</summary>
    /// <param name="frame">What <c>EditorWorldRenderer.Compose</c> returned.</param>
    /// <param name="texture">What the interface's pass has to declare that it reads.</param>
    /// <returns>Whether there is one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    public bool Take(CompositorFrame frame, out GraphTexture texture) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        texture = default;

        // ⚠ And the frame has to hold this pane's colour. <see cref="Prepare" /> is what lends it and
        // it declines for a pane whose mode resolves to no tree, so a pane asked for a texture it
        // never lent comes out of <c>CompositorFrame.Texture</c> as a <c>CompositorBindingException</c>
        // from the middle of the record loop — which takes the whole window's frame down rather than
        // this pane's.
        if (!IsReady || !frame.Has(colourTarget)) {
            return false;
        }

        degradations.Clear();

        foreach (var pair in world.Degradations) {
            degradations.Add(pair);
        }

        texture = frame.Texture(nameof(FramePresenter), colourTarget);

        return true;
    }

    /// <summary>What the last frame drew differently than it was asked to, in tree order.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Surfaced rather than logged once, because it is the difference between a preview
    ///         that is wrong and one that says it is wrong.</b> A node that declined to run — a
    ///         reflection pass with no effect system, a clipmap with no field — draws a picture that
    ///         is plausible and not the one the document describes, and nothing else in the frame
    ///         reports it.
    ///     </para>
    ///     <para>
    ///         <see cref="EditorWorldRenderer.Degraded" /> is the separate half: a fact about the
    ///         host before any document is loaded, which no node could be the condition of.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<(string Node, string Reason)> Degradations => degradations;

    /// <summary>Records the tools, inside the pass <see cref="Tooled" /> declared.</summary>
    /// <remarks>
    ///     The order is <see cref="ScenePresenter" />'s and for its reasons: the depth-tested lines
    ///     first, then the gizmo's shafts with no depth test at all — because a handle you cannot
    ///     reach through the thing it moves is a handle you cannot use — and last its solid heads, so
    ///     that an opaque cone covers the end of the line running into it rather than being drawn
    ///     over by it.
    /// </remarks>
    void Record(ICommandList commands) {
        lines.Record(commands, viewProjection);
        overlay.Record(commands, viewProjection, depthTested: false);
        handles.Record(commands, viewProjection, depthTested: false);
    }

    /// <summary>Copies a collected list into a renderer's buffer.</summary>
    /// <inheritdoc cref="ScenePresenter.Write" path="/remarks" />
    void Write(LineRenderer renderer, params IReadOnlyList<LineVertex>[] lists) {
        pending.Clear();

        foreach (var segments in lists) {
            foreach (var vertex in segments) {
                pending.Add(vertex);
            }
        }

        renderer.Upload(CollectionsMarshal.AsSpan(pending));
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        lines.Dispose();
        overlay.Dispose();
        handles.Dispose();

        Release();
    }

    void Release() {
        if (colourView.IsValid) {
            device.Destroy(colourView);
            device.Destroy(colour);
        }

        if (depthView.IsValid) {
            device.Destroy(depthView);
            device.Destroy(depth);
        }

        colourView = default;
        colour = default;
        depthView = default;
        depth = default;
        size = default;
    }
}
