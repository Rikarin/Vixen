// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     The frame's structure: which views exist, which stages they draw, and into what.
/// </summary>
/// <remarks>
///     <para>
///         The third of the three ideas docs/plan/06 takes from Stride verbatim, and the last to be
///         built. <strong>The frame is data the user edits, not code.</strong> "Swap forward for
///         deferred" is a different tree of <see cref="SceneRenderer" />s rather than a different
///         build of the engine, and the three shipped presets — Forward+, deferred, mobile forward —
///         are three assets over the same features.
///     </para>
///     <para>
///         <strong>Collect runs before the render system, and that ordering is the design.</strong>
///         Views are declared by the nodes that draw them, so the frame's view list is derived from
///         the compositor rather than handed to it: a shadow cascade exists because something draws
///         it, and a stage is in a view's mask because a node asked for it. A host that set the mask
///         itself would eventually set one nothing draws, and pay for culling into a list nobody
///         reads.
///     </para>
///     <para>
///         What is <em>not</em> here is the render graph. A node names its own attachments today,
///         where later it will name a transient resource and let barriers and aliasing be derived —
///         but that changes where a <see cref="RenderPassRenderer" /> gets its textures, not the
///         shape of this tree.
///     </para>
/// </remarks>
public sealed class GraphicsCompositor(RenderSystem system) : IDisposable {
    readonly List<RenderView> views = [];
    readonly List<IDisposable> owned = [];
    bool disposed;

    /// <summary>The render system this composes a frame for.</summary>
    public RenderSystem System { get; } = system;

    /// <summary>The root of the graph — the whole frame.</summary>
    public SceneRenderer? Game { get; set; }

    /// <summary>Takes over something that lives exactly as long as this frame's tree does.</summary>
    /// <param name="resource">The node, block or handle wrapper to release on <see cref="Dispose" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resource" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">This compositor has already been disposed.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The answer to "who frees a node's device memory", and it is deliberately not the
    ///         node's author's problem.</b> A compositor node may own device memory — a cached shadow
    ///         atlas has to, because every <c>!Resource</c> in a document is transient and the graph's
    ///         pool exists to recycle precisely the memory a cache must keep — and a document reload
    ///         builds a whole new tree and drops the old one. Every such node's <c>Dispose</c> existed
    ///         and nothing called it, which is a leak of the atlas itself: about 95 MB of it in
    ///         sample 13, on a reload the editor performs whenever the file changes on disk.
    ///     </para>
    ///     <para>
    ///         So a compositor owns what was made <em>for</em> it. <see cref="CompositorBuilder" />
    ///         calls this for every node it builds, at the one place every node kind passes through —
    ///         including the ones a factory in another assembly makes, which is what stops the next
    ///         node type from having to remember anything. It also calls it for the view block and its
    ///         set layout, which are per-build for the same reason and were leaking on the same path.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What a host built itself is the host's, and must not be handed over here.</b>
    ///         <c>SceneRenderHost.Debug</c> is the case that proves it: that node is appended to every
    ///         tree the host builds and is meant to survive the reload, so a compositor that disposed
    ///         everything it could reach through <see cref="SceneRenderer.Nested" /> would hand the
    ///         next frame a dead node. Reachability is not ownership; being built for this tree is.
    ///     </para>
    ///     <para>
    ///         Idempotent in the only sense that matters: <see cref="Dispose" /> disposes each entry
    ///         once, and a resource handed over twice is disposed twice — which every
    ///         <c>Dispose</c> in this tree is written to tolerate, but which nothing here does on
    ///         purpose.
    ///     </para>
    /// </remarks>
    public void Own(IDisposable resource) {
        ArgumentNullException.ThrowIfNull(resource);
        ObjectDisposedException.ThrowIf(disposed, this);

        owned.Add(resource);
    }

    /// <summary>How many things this frame will release when it is disposed.</summary>
    /// <remarks>
    ///     A counter rather than a list, on <see cref="Degradations" />' terms: what a host or a test
    ///     wants to know is whether the ownership arrived at all, and the answer to "which ones" is
    ///     the tree itself. A leak test asserts a device's live resource count returns to where it
    ///     started; this is what says the count returning to zero was not simply nothing happening.
    /// </remarks>
    public int OwnedCount => owned.Count;

    /// <summary>Releases every node and block this frame owns.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Called by whoever replaces or drops this compositor</b> — <c>SceneRenderHost.Load</c>
    ///         before it installs the next document's tree, <c>SceneRenderHost.Dispose</c> at
    ///         shutdown, and the editor's own reload, which does not go through <c>Load</c>. Those are
    ///         the two ends of a compositor's life and there is no third.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Safe with frames in flight, and it does not idle the device.</b> Every
    ///         <c>IGraphicsDevice.Destroy</c> is deferred by the backend until the frames that could
    ///         still reference the object have retired — <c>VulkanDevice.Retire</c> runs the queued
    ///         actions from <c>BeginFrame</c>, which has just waited on the fence of frame
    ///         <em>n</em> − <c>FramesInFlight</c> — so a node that releases its texture through the
    ///         device is already using the deferred path. That is why this differs from
    ///         <see cref="Resize" />, which takes an idle: a resize hands the same textures back and
    ///         then <em>reuses the node</em>, where this ends the node.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not from inside a frame.</b> A tree being built has already imported its owned
    ///         textures into the graph, and freeing one there is the use-after-free the deferral
    ///         cannot help with — the handle is invalid to the caller the moment it is returned.
    ///         Between frames is the only moment, which is where a reload happens anyway.
    ///     </para>
    ///     <para>
    ///         In reverse order of acquisition, so a node built after the block it reads is released
    ///         before it; and idempotent, because a host that disposes a compositor it has also
    ///         replaced should not have to remember which.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"A type holding a <see cref="GraphicsCompositor" /> must be
    ///         <see cref="IDisposable" />" was costed as a checked rule and declined, and the reason
    ///         it was declined is not the one it looks like.</b> It is not that the rule would fire
    ///         on correct code — every one of the twenty-one types that holds a compositor today is
    ///         already <c>IDisposable</c>, so it would ship green and stay green. It is that
    ///         <em>being</em> <c>IDisposable</c> is not <em>calling</em> this. Eighteen of them are
    ///         test fixtures carrying
    ///         <c>public required GraphicsCompositor Compositor { get; init; }</c>, a borrowed
    ///         reference; they implement <c>IDisposable</c> for a <c>RenderSystem</c> and a graph
    ///         pool of their own and never dispose the compositor at all — see
    ///         <c>PostProcessTests.Harness</c> and <c>BloomTests.Harness</c>, which are correct as
    ///         written. So the rule is satisfied by exactly the code it exists to catch: a holder
    ///         that grew a <c>Dispose</c> for something else and forgot this call passes it, which
    ///         is the shape #327 had.
    ///     </para>
    ///     <para>
    ///         The formulation with teeth — "a type with a compositor field must call
    ///         <c>Dispose</c> on it" — inverts the cost and fires on all eighteen borrowers, because
    ///         ownership is not recoverable from a type: an <c>init</c> auto-property is a field, and
    ///         nothing distinguishes the compositor a host built from the one a fixture was handed.
    ///         Nor could either live in <c>CheckArchitecture</c>, which reads <c>.csproj</c> XML and
    ///         knows nothing of types. What <em>is</em> checkable is checked: that every node a
    ///         builder makes reaches <see cref="Own" /> and that disposing gives the memory back, by
    ///         <c>CompositorLifetimeTests</c> against <c>NullDevice.LiveResourceCount</c>; and that
    ///         each of the two production holders releases the tree it replaces, by
    ///         <c>SceneRenderHostTests.Loading_a_second_document_disposes_the_first_ones_tree</c> and
    ///         <c>EditorWorldRendererTests</c>' reload test.
    ///     </para>
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }

        owned.Clear();
    }

    /// <summary>Lays a frame's post-process overlay over every node that takes one.</summary>
    /// <param name="overlay">
    ///     What <c>PostProcessVolumeSystem</c> folded the camera's volumes into.
    /// </param>
    /// <returns>How many nodes took it.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Called between frames, before <see cref="Build" />.</b> A node's parameters are read
    ///         at build time, so an overlay applied now takes effect this frame with nothing rebuilt —
    ///         which is the entire reason volumes are cheap: the graph's shape does not depend on
    ///         them, only its uniforms do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty overlay is still applied, and that is not a wasted walk.</b> A node lays
    ///         the overlay over its authored values every frame rather than accumulating, so "no
    ///         volumes reach the camera" has to be delivered for a node to go back to what its
    ///         document said. Skipping the call when nothing contributes would leave the last volume's
    ///         look burnt in after the player walked out of it.
    ///     </para>
    /// </remarks>
    public int Apply(in PostProcessOverlay overlay) {
        var applied = 0;

        Visit(Game, in overlay, ref applied);

        return applied;

        static void Visit(SceneRenderer? node, in PostProcessOverlay overlay, ref int applied) {
            if (node is null) {
                return;
            }

            if (node is IPostProcessTarget target) {
                target.Apply(in overlay);
                applied++;
            }

            // ⚠ Disabled nodes are visited too. `Enabled` is what stops a node *drawing*; a node
            // switched off this frame and on again the next would otherwise come back holding the
            // overlay from whenever it was last enabled.
            foreach (var child in node.Nested) {
                Visit(child, in overlay, ref applied);
            }
        }
    }

    /// <summary>
    ///     Every node that drew something other than what it was asked for, in tree order.
    /// </summary>
    /// <param name="into">Where the pairs go. Not cleared — the caller decides.</param>
    /// <returns>How many were added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The one place that answers "this frame drew — and here is what it drew differently
    ///         than you asked".</b> Every renderer already had the answer and none of it was
    ///         collectable: the reasons sat on nine different property names, on types in four
    ///         assemblies, read by a sample's hand-written log line that named each node by hand and
    ///         went stale the moment one was added. <see cref="SceneRenderer.Degraded" /> is the one
    ///         name and this is the walk, over <see cref="SceneRenderer.Nested" /> — so a node type
    ///         that did not exist when this was written is still in the list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Disabled nodes are skipped, which is the opposite of <see cref="Apply" />.</b> An
    ///         overlay has to reach a node that is off, because it is what the node goes back to when
    ///         it comes on. A degrade is a claim about a draw, and a node that is off did not draw —
    ///         reporting its last enabled frame's reason would be the stale answer this exists to
    ///         avoid.
    ///     </para>
    ///     <para>
    ///         Fill-a-collection rather than an enumerable property, on <see cref="Apply" />'s terms:
    ///         a host that wants this every frame should not allocate to ask, and the normal answer is
    ///         nothing at all.
    ///     </para>
    /// </remarks>
    public int Degradations(ICollection<(string Node, string Reason)> into) {
        ArgumentNullException.ThrowIfNull(into);

        var found = 0;

        Visit(Game, into, ref found);

        return found;

        static void Visit(SceneRenderer? node, ICollection<(string, string)> into, ref int found) {
            if (node is not { Enabled: true }) {
                return;
            }

            if (node.Degraded is { } reason) {
                into.Add((node.ToString(), reason));
                found++;
            }

            foreach (var child in node.Nested) {
                Visit(child, into, ref found);
            }
        }
    }

    /// <summary>The views this frame's collect phase declared, in first-use order.</summary>
    public IReadOnlyList<RenderView> Views => views;

    /// <summary>Declares that this frame uses a view. Idempotent.</summary>
    /// <remarks>
    ///     <para>
    ///         Called from <see cref="SceneRenderer.Collect" />. Idempotent because a view is normally
    ///         drawn by more than one node — an opaque stage and a transparent one share a camera —
    ///         and making each node declare it is what keeps a node independent of what else is in
    ///         the tree.
    ///     </para>
    ///     <para>
    ///         First use in a frame clears the view's stage mask, so a stage removed from the tree
    ///         stops being collected for rather than lingering in a mask nobody rebuilt. Add stages
    ///         through <see cref="Use(RenderView, RenderStage)" />, which orders the two correctly.
    ///     </para>
    /// </remarks>
    public void Use(RenderView view) {
        ArgumentNullException.ThrowIfNull(view);

        if (views.Contains(view)) {
            return;
        }

        view.Stages = RenderStageMask.None;
        views.Add(view);
    }

    /// <summary>Declares that this frame draws a stage from a view.</summary>
    public void Use(RenderView view, RenderStage stage) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(stage);

        Use(view);
        view.Stages |= stage.Mask;
    }

    /// <summary>Textures the host lends the frame, by the name the tree refers to them by.</summary>
    /// <remarks>
    ///     The swapchain image, and anything that has to outlive a frame — a cached shadow atlas, a
    ///     history buffer for temporal antialiasing. Everything else should be declared instead, so
    ///     the graph can size it, alias it and drop it.
    /// </remarks>
    public Dictionary<string, ImportedTexture> Imports { get; } = new(StringComparer.Ordinal);

    /// <summary>Buffers the host lends the frame, by name.</summary>
    /// <remarks>
    ///     A scene's light list and the cluster list a compute pass writes into it. Imported rather
    ///     than declared because the host filled the first before the frame began and owns both, and
    ///     because a buffer a later frame reads is by definition not transient.
    /// </remarks>
    public Dictionary<string, ImportedBuffer> BufferImports { get; } = new(StringComparer.Ordinal);

    /// <summary>Transient resources the frame declares, by name.</summary>
    /// <remarks>
    ///     Filled by <see cref="CompositorBuilder" /> from the asset's <c>resources</c>, or by a host
    ///     building a tree in code. They are the graph's to allocate, which means two whose lifetimes
    ///     do not overlap can be the same memory.
    /// </remarks>
    public IList<RenderResourceAsset> Resources { get; } = [];

    /// <summary>Transient buffers the frame declares, by name.</summary>
    public IList<RenderBufferAsset> BufferResources { get; } = [];

    /// <summary>The frame's reference size, which a scaled resource is a fraction of.</summary>
    /// <remarks>
    ///     ⚠ <b>Assigning this does not tell the frame's nodes.</b> Almost every node re-reads it each
    ///     build and needs nothing; the few that lay device state out against it are reached by
    ///     <see cref="Resize" />, which is what a host writes a new size through.
    /// </remarks>
    public Int2 FrameSize { get; set; } = new(1, 1);

    /// <summary>Moves the frame to a new size and lets the nodes that had laid state out against the
    ///     old one lay it again.</summary>
    /// <param name="size">The new size.</param>
    /// <param name="idle">
    ///     Waits for the device to finish everything in flight. Called at most once, immediately
    ///     before the first node is reset, and not at all when no node needs one.
    /// </param>
    /// <returns>How many nodes were reset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="idle" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Called between frames, never from inside one.</b> A node's <see cref="IResizeTarget.Reset" />
    ///         releases textures the frames still in flight may reference, which is why the idle is a
    ///         parameter rather than advice: a caller cannot reach the walk without supplying one, and
    ///         a compositor has no device of its own to call it on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The idle is deferred until a node actually wants it.</b> A frame whose nodes all
    ///         re-derive their extents — which is most frames — pays nothing for a resize, and a
    ///         window drag would otherwise stall the device once per pixel of travel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unchanged size is not a resize.</b> A host writes the size every time it
    ///         rebuilds a swapchain, including the rebuilds that come back the same — a surface that
    ///         keeps reporting <c>Suboptimal</c> does exactly that — and resetting on those would
    ///         restart every temporal chain in the frame, for ever, at no size change at all.
    ///     </para>
    /// </remarks>
    public int Resize(Int2 size, Action idle) {
        ArgumentNullException.ThrowIfNull(idle);

        if (size == FrameSize) {
            return 0;
        }

        FrameSize = size;

        var idled = false;
        var reset = 0;

        Visit(Game, idle, ref idled, ref reset);

        return reset;

        static void Visit(SceneRenderer? node, Action idle, ref bool idled, ref int reset) {
            if (node is null) {
                return;
            }

            if (node is IResizeTarget target) {
                // Once, and only once something is about to be released — see the remarks.
                if (!idled) {
                    idled = true;
                    idle();
                }

                target.Reset();
                reset++;
            }

            // ⚠ Disabled nodes are reset too, for Apply's reason one step further on: a node switched
            // off across the resize and on again afterwards would otherwise come back holding a
            // lattice laid over a frame size that no longer exists, and refuse the first build that
            // reaches it.
            foreach (var child in node.Nested) {
                Visit(child, idle, ref idled, ref reset);
            }
        }
    }

    /// <summary>
    ///     Runs a whole frame: collect, the render system's phases, then declare the graph's passes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The three in this order and not another. Collect must precede culling because it is
    ///         what decides what is culled; building must follow sorting because a pass body draws
    ///         the order sorting produced. See <see cref="RenderSystem.Draw" /> for the same argument
    ///         one level down.
    ///     </para>
    ///     <para>
    ///         Nothing is recorded here. The caller executes the graph, which is what places the
    ///         barriers, allocates the transients and drops the passes nothing needed — and which is
    ///         also the seam a caller uses to inspect a frame before it runs it.
    ///     </para>
    /// </remarks>
    public CompositorFrame Build(RenderGraph graph, EffectSystem effects, IGraphicsDevice? device = null) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(effects);

        var frame = new CompositorFrame {
            Graph = graph,
            Effects = effects,
            Device = device,
            Size = FrameSize
        };

        foreach (var (name, imported) in Imports) {
            frame.Add(
                name,
                graph.ImportTexture(
                    imported.Texture,
                    imported.View,
                    imported.Description,
                    imported.EntryState,
                    imported.ExitState
                ),
                imported.Description
            );
        }

        foreach (var (name, imported) in BufferImports) {
            frame.Add(
                name,
                graph.ImportBuffer(imported.Buffer, imported.Description, imported.EntryState, imported.ExitState),
                imported.Description
            );
        }

        foreach (var declared in Resources) {
            // An import of the same name wins. A host that has a real texture for something the
            // document also describes — a scene colour that is the swapchain image in one preset and
            // an offscreen buffer in another — should not have to edit the document to say so.
            if (frame.Has(declared.Name)) {
                continue;
            }

            var texture = declared.Describe(FrameSize);
            frame.Add(declared.Name, graph.CreateTexture(texture), texture);
        }

        foreach (var declared in BufferResources) {
            if (frame.HasBuffer(declared.Name)) {
                continue;
            }

            var description = declared.Describe();
            frame.Add(declared.Name, graph.CreateBuffer(description), description);
        }

        if (Game is { Enabled: true }) {
            Collect();
            System.Draw();
            Game.Build(this, frame);
        }

        return frame;
    }

    /// <summary>Runs the collect phase alone and hands the views to the render system.</summary>
    /// <remarks>
    ///     Separate from <see cref="Build" /> for the caller that wants the phases on its own job
    ///     graph — collect on the main thread, the system's phases as jobs, building afterwards. It
    ///     is the same sequence either way.
    /// </remarks>
    public void Collect() {
        if (Game is not { Enabled: true }) {
            return;
        }

        // Cleared each frame rather than accumulated: a view that stopped being drawn — a probe that
        // went out of range, a cascade the shadow distance dropped — must stop being culled for, and
        // a list that only ever grows would keep paying for it.
        views.Clear();
        Game.Collect(this);
        System.SetViews(views);
    }
}
