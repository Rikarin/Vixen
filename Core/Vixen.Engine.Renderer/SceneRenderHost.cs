// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;

namespace Vixen.Engine.Renderer;

/// <summary>
///     A render system, a compositor and a device, joined into a frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>The link every path was missing.</b> Every piece of drawing a frame existed and was
///         tested — features extract, a document builds a compositor, a graph aliases and orders the
///         passes, a command list executes them — and nothing outside a test project put them together.
///         <c>new RenderSystem()</c> appeared only in tests; the samples opened a device and issued
///         draws directly. This is that assembly, and it is deliberately small: what it adds is the
///         order, which is the only part a host can get wrong silently.
///     </para>
///     <para>
///         <b>It does not run the render system's phases, and that is the thing about it worth reading
///         twice.</b> The obvious host extracts, culls, prepares and sorts, and then builds the graph —
///         and that is wrong twice over. <c>GraphicsCompositor.Build</c> already collects the frame's
///         views and calls <see cref="RenderSystem.Draw" /> itself, because the views come from the
///         compositor's own nodes and culling before they are collected culls against the previous
///         frame's. A host that did it anyway would extract every feature twice per frame: a
///         correct-looking frame, and a renderer that profiles as mysteriously half as fast.
///     </para>
///     <para>
///         So what is left is three calls in an order — reset the graph, build it, execute it into a
///         list the caller owns. This opens no command list and submits nothing, because when a frame is
///         submitted and what it is submitted with is the application's business.
///     </para>
///     <para>
///         <b>It does not own the device or the effect system.</b> A device outlives a frame and may be
///         lost and recreated under a host that has its own opinions about swapchains; an effect system
///         is shared with everything else that compiles a shader. What this owns is the render system,
///         the graph's transient pool and the compositor it built.
///     </para>
/// </remarks>
public sealed class SceneRenderHost : IDisposable {
    readonly IGraphicsDevice device;
    bool disposed;

    /// <summary>Sets up a host on a device.</summary>
    /// <param name="device">The device every pass records against.</param>
    /// <param name="effects">Where variants are compiled.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SceneRenderHost(IGraphicsDevice device, EffectSystem effects) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(effects);

        this.device = device;

        Effects = effects;
        System = new();
        Graph = new(device);
        Builder = new(System);
    }

    /// <summary>The scene's side of the frame: the features, the objects and the views.</summary>
    public RenderSystem System { get; }

    /// <summary>Where variants are compiled.</summary>
    public EffectSystem Effects { get; }

    /// <summary>The graph the compositor builds into, and its transient pool.</summary>
    public RenderGraph Graph { get; }

    /// <summary>
    ///     What turns a document into nodes, and what a host hands its device-owned resources to.
    /// </summary>
    /// <remarks>
    ///     Exposed rather than hidden because the division it embodies is the point: a document decides
    ///     where the passes go and a host decides what exists. <see cref="VirtualGeometrySystem.Supply" />
    ///     is the same handover for the virtualized path.
    /// </remarks>
    public CompositorBuilder Builder { get; }

    /// <summary>The compositor the last <see cref="Load" /> built, or null.</summary>
    public GraphicsCompositor? Compositor { get; private set; }

    /// <summary>
    ///     The node that draws a frame's <c>DebugDraw</c>, appended to every document this builds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Here rather than at the host's call site, and that placement is the whole of what
    ///         makes it survive.</b> <see cref="Load" /> is callable again — a project that swaps its
    ///         quality preset swaps its compositor, and <c>Samples/13</c> reloads its own document
    ///         once the shader library is attached — so a host that appended the node to the tree it
    ///         got back from the first build would be holding a node in a compositor the frame stopped
    ///         drawing. That is the same shape as the editor's two <c>DiagnosticsModule</c>s: the
    ///         instance that was fed is not the instance that draws, and it fails by showing nothing.
    ///     </para>
    ///     <para>
    ///         Null is a build with no debug drawing in it, which is every build that did not ask for
    ///         one. Assigning it after a <see cref="Load" /> takes effect at the next one.
    ///     </para>
    /// </remarks>
    public DebugOverlayRenderer? Debug { get; set; }

    /// <summary>How many pixels the frame covers.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Setting this is the resize seam, and it is seated here for two reasons the
    ///         compositor cannot answer.</b> The device is one: <see cref="IResizeTarget.Reset" />
    ///         releases textures frames in flight may still reference, so the walk has to be preceded
    ///         by an idle, and a <see cref="GraphicsCompositor" /> has no device — <c>Build</c> takes
    ///         one as an optional argument that is null in most of the tests that call it. The frame
    ///         is the other: <c>Build</c> runs with a command list open and a graph half declared,
    ///         where releasing a texture the frame has already imported is the very use-after-free the
    ///         idle exists to prevent. A host writes the new size between frames, which is the only
    ///         moment either can be done.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Before a document is loaded this is a no-op, not a queued size.</b>
    ///         <see cref="Load" /> replaces the compositor, and a size written to the one being
    ///         replaced would be discarded with it — so a host sets this after loading, which is the
    ///         order <c>AppGraphics</c> already uses.
    ///     </para>
    /// </remarks>
    public Int2 FrameSize {
        get => Compositor?.FrameSize ?? default;
        set {
            if (Compositor is { } compositor) {
                compositor.Resize(value, device.WaitIdle);
            }
        }
    }

    /// <summary>How many frames <see cref="Draw(ICommandList)" /> has recorded.</summary>
    /// <remarks>
    ///     A counter rather than a log, and it is what a headless run asserts against: "the stack starts,
    ///     runs N frames and stops" is the whole claim a sample makes, and something has to be able to
    ///     say the frames happened.
    /// </remarks>
    public int FrameCount { get; private set; }

    /// <summary>Builds the frame a document describes.</summary>
    /// <param name="asset">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="asset" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         Callable again — a project that swapped its quality preset swapped its compositor — and the
    ///         imports do not survive it, because they were bound to the resource names of the document that
    ///         is being replaced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Neither does the tree, and that is a release rather than a drop.</b> A compositor
    ///         node may own device memory a frame cannot — a cached shadow atlas is the case that
    ///         forced it — so the replaced compositor is disposed here. Without this line a reload
    ///         leaks every such node: about 95 MB of shadow atlas in sample 13, per reload, forever.
    ///         <see cref="GraphicsCompositor.Dispose" /> says why no idle is needed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Built first, released second.</b> A document that fails to bind throws out of the
    ///         build, and the frame that was already loaded goes on drawing — with its nodes intact,
    ///         which is only true because nothing was freed before the new tree existed.
    ///         <c>CompositorBuilder.Build</c> releases its own half-built tree on the way out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Debug" /> survives, because the host owns it and the compositor does
    ///         not.</b> It is appended to each tree rather than built into one — see
    ///         <see cref="Append" /> — so it is still the same live node after the swap. That
    ///         distinction is the whole of the ownership rule; <see cref="GraphicsCompositor.Own" />
    ///         states it.
    ///     </para>
    /// </remarks>
    public void Load(GraphicsCompositorAsset asset) {
        ArgumentNullException.ThrowIfNull(asset);
        ObjectDisposedException.ThrowIf(disposed, this);

        var built = Builder.Build(asset);

        Compositor?.Dispose();
        Compositor = built;
        Append(Compositor, Debug);
    }

    /// <summary>Puts a node at the end of a built frame, whatever shape the document was.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Appended to the built tree rather than expanded into the document.</b> A
    ///         transformer would mean every authored <c>.vxcompositor</c> in the world silently
    ///         gaining a node it does not declare, and a document that round-tripped through the
    ///         editor would then declare it — which is a diagnostic switch becoming part of a
    ///         project's content. What the tree gets is not saved anywhere.
    ///     </para>
    ///     <para>
    ///         A root that is not a sequence is wrapped in one, because a document's <c>game:</c> may
    ///         be a single node — <c>!StandardFrame</c> expands to a sequence, but a one-pass frame
    ///         written by hand does not have to.
    ///     </para>
    /// </remarks>
    static void Append(GraphicsCompositor compositor, SceneRenderer? node) {
        if (node is null) {
            return;
        }

        if (compositor.Game is not { } root) {
            compositor.Game = node;
            return;
        }

        if (root is SceneRendererSequence sequence) {
            sequence.Children.Add(node);
            return;
        }

        var wrapped = new SceneRendererSequence { Name = root.Name };

        wrapped.Children.Add(root);
        wrapped.Children.Add(node);

        compositor.Game = wrapped;
    }

    /// <summary>Lends the frame a texture the host owns.</summary>
    /// <param name="name">The resource name the document uses.</param>
    /// <param name="texture">The texture.</param>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    /// <exception cref="InvalidOperationException">No document has been loaded.</exception>
    /// <remarks>
    ///     <b>The frame's last target has to be one of these.</b> A transient nothing reads afterwards is
    ///     a pass the graph is right to cull, so the only thing that makes the final pass matter is that
    ///     its target belongs to somebody outside the graph — the swapchain image, usually. An import
    ///     also wins over a resource the document declares by the same name, which is what lets one
    ///     document run against a swapchain in one preset and an offscreen buffer in another.
    /// </remarks>
    public void Import(string name, ImportedTexture texture) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Compositor is not { } compositor) {
            throw new InvalidOperationException(
                "Load a compositor before importing into it — an import is bound to a resource name, and "
                + "the names belong to the document."
            );
        }

        compositor.Imports[name] = texture;
    }

    /// <summary>Records one frame.</summary>
    /// <param name="list">An open command list. Not finished or submitted here.</param>
    /// <returns>Whether a frame was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     False when no document has been loaded, which is a host that has not finished starting rather
    ///     than an error — the same reading every node in the compositor gives an unsupplied resource.
    /// </remarks>
    public bool Draw(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Compositor is not { } compositor) {
            return false;
        }

        // No System.Draw() here: Build collects the frame's views and runs the phases itself, in that
        // order, because the views come from the compositor's own nodes. Calling it first extracts every
        // feature twice and culls the first pass against last frame's views — which draws a
        // correct-looking frame and profiles as a renderer that is mysteriously half as fast.
        Graph.Reset();
        compositor.Build(Graph, Effects, device);
        Graph.Execute(list);

        FrameCount++;

        return true;
    }

    /// <summary>Records one frame onto the device's queues, one command list per segment.</summary>
    /// <param name="queues">Where the lists come from and where they go.</param>
    /// <returns>Whether a frame was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="queues" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         The overload a host needs once it sets <c>Graph.Scheduling</c> to
    ///         <c>QueueScheduling.Async</c>: a frame on two queues is more than one command list, so
    ///         the graph has to decide how many there are and hand each one back as it fills. Unlike
    ///         <see cref="Draw(ICommandList)" />, this submits.
    ///     </para>
    ///     <para>
    ///         Valid whatever the scheduling says — a single-queue frame is one segment — so a host
    ///         that always draws this way is not choosing async compute by doing so.
    ///     </para>
    /// </remarks>
    public bool Draw(IRenderGraphQueues queues) {
        ArgumentNullException.ThrowIfNull(queues);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Compositor is not { } compositor) {
            return false;
        }

        Graph.Reset();
        compositor.Build(Graph, Effects, device);
        Graph.Execute(queues);

        FrameCount++;

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The compositor first, and the graph's pool after it.</b> A node's release is a
    ///     <c>Destroy</c> on the device, not on the pool — the whole reason those nodes own their
    ///     textures is that a pooled one may be handed to something else the moment a frame ends — but
    ///     a node that read a pooled resource is entitled to expect the pool to outlive it. The
    ///     render system is last for its own reason: a feature's tear-down gives table slots back.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Compositor?.Dispose();
        Graph.DisposePool();
        System.Dispose();
    }
}
