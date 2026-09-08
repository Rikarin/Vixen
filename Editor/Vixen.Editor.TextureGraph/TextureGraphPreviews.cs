// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Graphics;

namespace Vixen.Editor.TextureGraph;

/// <summary>Where a preview's pixels become a number the interface can draw.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A picture and not a texture, and the reason is a Vulkan rule rather than a
///         convenience.</b> <c>ShaderGraphPreviewRenderer</c>'s equivalent hands over a
///         <c>TextureViewHandle</c>, because it draws its own targets on the graphics queue. A
///         texture graph's images are written by <c>TexturePlanEvaluator</c> on
///         <see cref="IGraphicsDevice.ComputeQueue" /> and every one of them is
///         <c>ResourceSharing.Exclusive</c> — so reading one from the queue family the interface
///         draws on, without a queue-family ownership transfer, leaves its contents <em>undefined by
///         specification</em>. It would look perfect here: MoltenVK reports one family for both, and
///         every adapter this engine is developed on does. It is the same mistake as
///         <a href="https://github.com/Rikarin/Vixen/issues/617">#617</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/679">#679</a>, which is twice already.
///     </para>
///     <para>
///         <b>So the pixels go through the host.</b> <c>TextureBake.Read</c> already copies on the
///         queue that wrote the image and hands back bytes; the host uploads those into a texture of
///         its own, on its own queue, and answers with the number its image commands take. At
///         <see cref="TextureGraphPreviews.Size" /> squared that is sixteen kilobytes per node, which
///         is a price worth paying to be right on a discrete card.
///     </para>
///     <para>
///         ⚠ <b>Public, and an <c>InternalsVisibleTo</c> naming the editor's own texturing plugin was
///         the wrong answer</b> — <a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a>.
///         That is cheaper: two internal types, no guide page, no <c>CheckApi</c> baseline. It is
///         also the arrangement <c>ModuleReferenceTests</c> exists to refuse, and it fired on the
///         attempt — what a plugin reaches is reached through the <em>public</em> surface, because a
///         friend assembly works for the one plugin this repository ships and for no third party.
///         The shader graph's <c>IPreviewImages</c> is public one folder over for the same reason.
///     </para>
/// </remarks>
public interface ITexturePreviewImages {
    /// <summary>Names a picture, and returns the number to draw it by.</summary>
    /// <param name="picture">The pixels, top row first, eight bits per channel.</param>
    /// <param name="existing">
    ///     The number this node had, or zero. A sink that can write into the texture it already made
    ///     keeps the number valid; one that cannot releases it and answers with a new one.
    /// </param>
    /// <returns>The number, or zero if it could not be named.</returns>
    ulong Register(Vixen.Core.Imaging.Bitmap picture, ulong existing);

    /// <summary>Gives up a number, because the node it belonged to is gone.</summary>
    /// <param name="image">What <see cref="Register" /> returned.</param>
    void Release(ulong image);
}

/// <summary>
///     Doc 48 § M4's per-node previews: what every node of a graph is producing, as a picture under
///     the node.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Batch 4 recorded that this needed "a device-side preview path split out of
///         <c>Evaluate</c>". It does not, and the reason is worth writing down.</b> The obstacle
///         looked like the pool: an intermediate image's texture is handed to the next image that
///         needs one the moment its last reader has run, so reading one back after the bake gives a
///         picture of the wrong node. But which images the pool may reuse is the <em>plan's</em>
///         decision and not the evaluator's — <c>TexturePoolSchedule</c> never reuses a slot holding
///         an image in <c>TexturePlan.Outputs</c> — so a plan compiled with
///         <see cref="TextureGraphCompiler.PreviewEveryNode" /> keeps every node's image, and one
///         ordinary <c>Evaluate</c> then holds all of them at once. Nothing in the evaluator changed.
///     </para>
///     <para>
///         <b>Two tiers, and the split is where the cost is.</b> Compiling the graph is a walk that
///         appends records to two lists and is done whenever the graph has changed; evaluating it
///         allocates a texture per node and dispatches, and is done in <see cref="Update" /> rather
///         than in <see cref="TryGet" />, which is called from a draw and is no place to record
///         commands on a device. A node whose picture is a frame old keeps showing it rather than
///         blinking empty, which is <c>ShaderGraphPreviewRenderer</c>'s rule for the same reason.
///     </para>
///     <para>
///         ⚠ <b>A preview compilation is not a bake's.</b> Every image is kept, so nothing is pooled
///         and a forty-node graph is forty textures — at <see cref="Size" /> squared, which is what
///         makes that affordable and why the size is not the graph's.
///     </para>
///     <para>
///         <b><c>TexturingModule</c> is what builds one</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a>, closed by a
///         construction beside <c>preview</c> and <c>stackPreview</c>, a
///         <c>TexturePreviewImages</c> over the host's <c>IEditorGraphics</c>,
///         <c>view.Canvas.PreviewSource = …</c> in the graph panel's factory and
///         <c>PluginContext.OnUpdate</c> driving <see cref="Update" />. ⚠ <b>The obstacle was never
///         the panel</b>: this type constructed its own <c>TexturePlanEvaluator</c>, so the wiring
///         would have put a third one into a session that goes to some length to hold one, arriving
///         through the commit that closed the issue. It takes a lease now, which is what
///         <c>LayerStackPreview</c> and <c>TextureGraphPreview</c> take.
///     </para>
/// </remarks>
public sealed class TextureGraphPreviews : INodePreviewSource, IDisposable {
    /// <summary>How big a preview is, in texels.</summary>
    /// <remarks>
    ///     Bigger than <c>NodePreviewLayer.Size</c> draws it, so a zoomed-in canvas does not show a
    ///     soft square, and small enough that a graph full of them is a few megabytes.
    /// </remarks>
    public const int Size = 64;

    readonly Func<TextureGraphCompiler?> compilers;
    readonly ITexturePreviewImages? images;
    readonly Func<TexturePlanEvaluator?> evaluators;
    readonly Dictionary<(NodeGraphModel Graph, NodeId Node), ulong> registered = [];

    /// <summary>The nodes whose picture only a host with an asset database could supply.</summary>
    /// <remarks>
    ///     ⚠ The channel <see cref="Skipped" /> could not be — that counter is an instrument for a
    ///     test, and this is what an author sees. Rebuilt per rebuild along with
    ///     <see cref="registered" />, so a node that stops being tainted stops being hatched.
    /// </remarks>
    readonly HashSet<(NodeGraphModel Graph, NodeId Node)> unavailable = [];

    readonly HashSet<NodeGraphModel> watched = [];
    readonly List<NodeGraphModel> dirty = [];

    bool disposed;

    /// <summary>Builds a preview source over an evaluator somebody else owns.</summary>
    /// <param name="evaluators">
    ///     Answers the one evaluator for the host's <em>current</em> device, or <see langword="null" />
    ///     when there is none. Asked on every rebuild rather than once.
    /// </param>
    /// <param name="compilers">
    ///     Makes a compiler over the node library the graphs are edited against, with whatever
    ///     parameters, arguments and sub-graph library the host has, or <see langword="null" /> when
    ///     there is no canvas to read one off. Its resolution and its
    ///     <see cref="TextureGraphCompiler.PreviewEveryNode" /> are overridden here.
    /// </param>
    /// <param name="images">
    ///     What turns a picture into a number the interface draws, or <see langword="null" /> for a
    ///     source whose pictures nobody shows — which is what a test has and what a headless editor
    ///     has.
    /// </param>
    /// <exception cref="ArgumentNullException">Any of the first three is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It used to build its own <c>TexturePlanEvaluator</c> from the device, and that is
    ///         the reason <a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a> was not
    ///         one line.</b> The host's rule is that no pane owns an evaluator —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>,
    ///         <a href="https://github.com/Rikarin/Vixen/issues/988">#988</a>, and
    ///         <c>PreviewLeaseTests</c> counts both the builds and the asks so that neither
    ///         direction can hide — because an evaluator is a pipeline and a shader module per
    ///         kernel and output format. A third one, per session, for the swatches under the nodes,
    ///         would have been the cost that rule exists to prevent, arriving through the wiring
    ///         commit that closed the issue.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked on every rebuild rather than held, and the <em>device</em> goes with
    ///         it.</b> The first version of this took an <c>IGraphicsDevice</c> as well and cached
    ///         it in a field, which is only half of the shape <c>LayerStackPreview</c> and
    ///         <c>TextureGraphPreview</c> have: they both re-read <c>graphics.Device</c> at the top
    ///         of every use. Holding the device and asking for its evaluator per rebuild would have
    ///         asked the lease for the evaluator of a device that is <em>gone</em> — the one
    ///         question the lease cannot answer safely, arriving from the only pane whose device
    ///         nothing re-reads. So this takes one delegate that answers both, and
    ///         <see langword="null" /> means there is nothing to draw on yet.
    ///     </para>
    /// </remarks>
    public TextureGraphPreviews(
        Func<TexturePlanEvaluator?> evaluators,
        Func<TextureGraphCompiler?> compilers,
        ITexturePreviewImages? images = null
    ) {
        ArgumentNullException.ThrowIfNull(evaluators);
        ArgumentNullException.ThrowIfNull(compilers);

        this.evaluators = evaluators;
        this.compilers = compilers;
        this.images = images;
    }

    /// <summary>How many times a graph has been compiled to a plan — the cheap tier.</summary>
    public int Compilations { get; private set; }

    /// <summary>How many times a plan has been evaluated on the device — the expensive tier.</summary>
    public int Bakes { get; private set; }

    /// <summary>How many graphs got no pictures at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted rather than reported.</b> A graph an author is halfway through wiring does
    ///     not compile most of the time, and a preview source that raised its diagnostics would be
    ///     a second, noisier copy of the panel that already shows them. There is no device, or no
    ///     canvas, or the graph does not compile, or every picture in it is one only a host with an
    ///     asset database could supply — <see cref="Skipped" /> is the same thing per node.
    /// </remarks>
    public int Refusals { get; private set; }

    /// <summary>How many node pictures have been left undrawn because only a host could fill them.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument for <a href="https://github.com/Rikarin/Vixen/issues/1089">#1089</a>'s
    ///         remainder.</b> A graph reading an imported bitmap still bakes; what cannot be drawn is
    ///         the node reading it and everything computed from it, because this side holds an
    ///         evaluator and a compiler and no <c>AssetDatabase</c>. Counted per node per rebuild.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A missing swatch is otherwise indistinguishable from a preview source that has
    ///         stopped running</b>, which is #1089 one level up: the crash it fixed was invisible
    ///         because a blank canvas is what "no device yet" looks like too. <see cref="Refusals" />
    ///         and this counter separate "the whole graph was skipped" from "these nodes were", and
    ///         <see cref="Bakes" /> separates both from "nothing ran at all".
    ///     </para>
    /// </remarks>
    public int Skipped { get; private set; }

    /// <summary>How many nodes have a picture.</summary>
    public int Live => registered.Count;

    /// <summary>How many graphs are waiting to be evaluated.</summary>
    public int Pending => dirty.Count;

    /// <summary>Says a graph's pictures are out of date.</summary>
    /// <param name="graph">The graph.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <remarks>
    ///     Called for the host, and by <see cref="TryGet" /> the first time it sees a graph — so a
    ///     canvas that draws a graph nobody has touched still gets pictures.
    /// </remarks>
    public void Invalidate(NodeGraphModel graph) {
        ArgumentNullException.ThrowIfNull(graph);

        if (!dirty.Contains(graph)) {
            dirty.Add(graph);
        }
    }

    /// <inheritdoc cref="INodePreviewSource.TryGet" />
    /// <remarks>
    ///     ⚠ <b>It never compiles and never evaluates.</b> This is called from the canvas's draw,
    ///     once per visible node, which is no place to record commands on a device — so what it does
    ///     is answer with whatever picture already exists and ask for a rebuild.
    /// </remarks>
    public bool TryGet(
        NodeGraphModel graph,
        GraphNode node,
        NodeTypeDefinition definition,
        out NodePreview preview
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        if (watched.Add(graph)) {
            // ⚠ Subscribed once per graph and never unsubscribed until Dispose, which is what makes
            // a preview follow an edit. `NodeGraphModel.Changed` is what a command stack raises.
            graph.Changed += Invalidate;
            Invalidate(graph);
        }

        if (registered.TryGetValue((graph, node.Id), out var image) && image != 0) {
            preview = new(new Color4(1f, 1f, 1f, 1f), image);

            return true;
        }

        if (unavailable.Contains((graph, node.Id))) {
            // ⚠ `true` with no picture, which is the state that had no way to be expressed until
            // #1092. Answering `false` here leaves a gap, and a gap is what this source looks like
            // when it has stopped running altogether — the symptom #1089 was invisible behind. The
            // canvas draws a hatched swatch for it, so an author can see that the panel is alive and
            // that this node's picture is theirs to supply.
            preview = new(new Color4(1f, 1f, 1f, 1f), Unavailable: true);

            return true;
        }

        preview = default;

        return false;
    }

    /// <summary>Evaluates whatever is out of date.</summary>
    /// <param name="graphs">How many graphs may be evaluated in one call.</param>
    /// <exception cref="ObjectDisposedException">This source has been disposed.</exception>
    /// <remarks>
    ///     ⚠ <b>Rationed, for <c>ShaderGraphPreviewRenderer.RebuildsPerUpdate</c>'s reason.</b> Two
    ///     graph tabs and a paste would otherwise be several bakes between two frames, which is a
    ///     freeze spent on pictures nobody has looked at yet.
    /// </remarks>
    public void Update(int graphs = 1) {
        ObjectDisposedException.ThrowIf(disposed, this);

        for (var done = 0; done < graphs && dirty.Count > 0; done++) {
            var graph = dirty[0];

            dirty.RemoveAt(0);
            Rebuild(graph);
        }
    }

    /// <summary>Gives every picture back, and asks for all of them again.</summary>
    /// <exception cref="ObjectDisposedException">This source has been disposed.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What a device loss needs, and <see cref="Dispose" /> is not it.</b> The host
    ///         takes its device down and comes back with another — a window closed and reopened —
    ///         and the source outlives that, because the canvas is still holding it. What does
    ///         <em>not</em> outlive it is every number handed out: those name textures the host made
    ///         on the device that is going. A source that kept them would draw a swatch through a
    ///         handle whose texture no longer exists, which is a use-after-free the interface
    ///         resolves silently rather than a blank square.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And every watched graph is marked dirty rather than left alone</b>, so the
    ///         pictures come back when the device does. Without that half this would be correct and
    ///         invisible: the swatches would go and never return until the author edited something.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Disposing instead would be worse than doing nothing.</b> A disposed source is
    ///         still the canvas's <c>PreviewSource</c>, and its <see cref="Update" /> throws — from
    ///         a plugin's per-frame work, which <c>PluginHost.Update</c> answers by unloading the
    ///         plugin.
    ///     </para>
    /// </remarks>
    public void Drop() {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var image in registered.Values) {
            if (image != 0) {
                images?.Release(image);
            }
        }

        registered.Clear();

        // ⚠ Not cleared, and the asymmetry is deliberate. Every number handed out named a texture on
        // the device that is going; "this node's picture is somebody else's to supply" names nothing
        // of the device's and is still true on the next one. Clearing it would blank the hatches for
        // the frames between a device loss and the rebuild that follows it, which is the flicker
        // this flag exists to remove.
        foreach (var graph in watched) {
            Invalidate(graph);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var graph in watched) {
            graph.Changed -= Invalidate;
        }

        foreach (var image in registered.Values) {
            if (image != 0) {
                images?.Release(image);
            }
        }

        registered.Clear();
        unavailable.Clear();
        watched.Clear();
        dirty.Clear();

        // ⚠ The evaluator is *not* disposed, because this does not own one. Disposing a lent
        // evaluator would call `WaitIdle` and `Destroy` on pipelines two other panes are still
        // holding — which is the use-after-free the lender's own device guard exists to avoid,
        // reached from the one place nothing would think to look.
    }

    /// <summary>Drops what this source was saying about one graph's unavailable nodes.</summary>
    /// <param name="graph">The graph being rebuilt.</param>
    void Forget(NodeGraphModel graph) => unavailable.RemoveWhere(entry => entry.Graph == graph);

    void Rebuild(NodeGraphModel graph) {
        // ⚠ Asked *before* the compile, and the order is the whole of #1015's second half. The
        // remark on the branch below said "a rebuild that cannot draw leaves the graph dirty" and
        // that was false: `Update` takes the graph off the list before calling this, and neither
        // refusal put it back — so a graph whose first rebuild happened before the host had a device
        // was clean afterwards and stayed blank until somebody edited it. That is *the* state the
        // editor starts in (`IEditorGraphics` is published with a null `Device` and acquires one
        // when the window can present), so the wiring that closed the issue would have shipped a
        // panel whose swatches appear only after the first keystroke. Asking first also means the
        // frames before the device arrives cost nothing rather than a compile each.
        if (evaluators() is not { } evaluator) {
            // No device, which is an ordinary state rather than a fault — the host has not finished
            // starting, or it has just lost one.
            Refusals++;
            Invalidate(graph);

            return;
        }

        if (compilers() is not { } compiler) {
            // And no canvas: nothing is being edited, so there is no node library to compile the
            // graph against. Dirty for the same reason, because opening the panel is what fixes it.
            Refusals++;
            Invalidate(graph);

            return;
        }

        compiler.BaseWidth = Size;
        compiler.BaseHeight = Size;
        compiler.BakeLevelOffset = 0;
        compiler.PreviewEveryNode = true;

        var compilation = compiler.Compile(graph);

        Compilations++;

        if (compilation.Artefact is not { } plan || compilation.HasErrors) {
            Refusals++;

            return;
        }

        // ⚠ Until #1089 this called the bare-handle `Evaluate` with no externals at all, so a
        // `Source/Bitmap` naming an imported image made `ExternalViews` raise `ArgumentException`
        // straight out of `Update` — a plugin's per-frame work, which `PluginHost.Update` answers by
        // unloading the plugin. The first answer was to refuse any graph with an external, and that
        // was far coarser than the defect: it dropped every swatch on every node, and ⚠ a
        // *`Source/Gradient` with no ramp asset* emits an external too — the compiler bakes the
        // black-to-white strip itself and carries the bytes — so the ordinary gradient, the curve
        // and the gradient map went blank along with the bitmap they have nothing to do with.
        //
        // So the externals whose bytes the compilation already carries are uploaded. The device is
        // the one behind the evaluator that was just leased rather than one held in a field here:
        // `TextureUploads` needs a device, and a field would be exactly the shape the constructor's
        // remarks refuse — on the frame after a device loss it would name a device that is gone.
        using TextureUploads uploads = new(evaluator.Device);
        var owed = TextureGraphExternals.Upload(uploads, plan, compiler.Externals);

        // ⚠ An upload per rebuild is precisely the cost `TextureUploads`' own remarks call "the cost
        // that arrangement hides" — and it is the right trade *here* and nowhere else. A preview
        // compiles at `Size`, so a ramp is `TextureRamp.Entries`×1 and every other carried picture
        // is 64×64: kilobytes, against a bake that already opens a frame and waits for the device
        // once per node to read the pictures back. At the panel's own resolution it would not be.

        // The asset-backed entries are the ones this side genuinely cannot fill, and their images
        // taint everything computed from them. Collected before anything is uploaded for them, so a
        // graph whose every node is downstream of one costs no bake at all.
        HashSet<int> unresolved = [];

        foreach (var external in owed) {
            unresolved.Add(external.Image);
        }

        if (unresolved.Count > 0) {
            // One forward pass is enough because `TexturePlan.Check` refuses a plan whose op reads an
            // image no earlier op wrote — "an intermediate is written by an earlier op or supplied by
            // the caller" — so the op list is in write-before-read order and a taint cannot travel
            // backwards.
            foreach (var op in plan.Ops) {
                foreach (var input in op.Inputs) {
                    if (unresolved.Contains(input)) {
                        unresolved.Add(op.Output);

                        break;
                    }
                }
            }

            if (compiler.NodeImages.All(written => unresolved.Contains(written.Image))) {
                // Every picture in the graph is somebody else's, which is what a graph that is only
                // a bitmap and an output looks like. Refused rather than baked for nothing, and not
                // marked dirty: nothing will change until the author edits or a host with an asset
                // database supplies the picture, so re-baking every frame would be a spin.
                Refusals++;

                // ⚠ And every node in it is hatched rather than left blank, which is the branch that
                // most needed #1092: a graph with no bake at all draws *nothing*, and a canvas of
                // empty nodes is precisely what a dead preview source looks like. `Refusals` counts
                // it for a test; this is what the author reads.
                //
                // ⚠ <see cref="Skipped" /> is deliberately not moved by this branch. It counts nodes
                // dropped *from a bake that happened* — "the same thing per node" as a refusal, its
                // own remark says — and a refusal has already been counted here. Two counters both
                // rising for one event would make the pair unable to tell the two states apart,
                // which is the only reason either of them exists.
                Forget(graph);

                foreach (var written in compiler.NodeImages) {
                    unavailable.Add((graph, written.Node));
                }

                return;
            }

            foreach (var image in unresolved) {
                if (!plan.Images[image].External) {
                    continue;
                }

                // ⚠ One black texel, so the *rest* of the graph bakes. Nothing computed from it is
                // ever shown — a plausible picture drawn from a stand-in is the failure this whole
                // path exists to avoid, and black is a perfectly plausible mask. `TexturePlan.Check`
                // exempts an external image from the extent guard, so a 1×1 is legal here; the bake
                // collects one caution per op that reads it, which nothing draws.
                uploads.Add(plan, image, 1, 1, new byte[TextureFormats.BytesPerTexel(plan.Images[image].Format)]);
            }
        }

        // ⚠ The evaluator is the one asked for at the top of this method rather than one held in a
        // field, which is what "no pane owns an evaluator" means in practice — see the constructor.
        // `PreviewLeaseTests` counts this question for the two panes;
        // `A_preview_source_takes_its_evaluator_from_the_lease_on_every_rebuild` counts it for this
        // one, because a source that asked once on the way in leaves every count about the lender's
        // own builds green.
        //
        // ⚠ Declared after the uploads so that it is disposed *first*: the bake owns a view over
        // every external texture and destroys it, and the textures those view are the uploads'.
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        Bakes++;

        // ⚠ The last image a node wrote and not the first. A node with two output ports — and a node
        // whose ports were allocated in declaration order — has several entries here, and the one an
        // author is looking at under the node is its result rather than an intermediate of it.
        Dictionary<NodeId, int> shown = [];

        foreach (var written in compiler.NodeImages) {
            shown[written.Node] = written.Image;
        }

        // ⚠ Filtered after the dictionary is built rather than while it is being filled, and the
        // difference is a node with two output ports. Skipping a tainted entry on the way in would
        // leave such a node showing an *earlier* image it wrote — a clean intermediate of itself,
        // which is a picture and is not its result. Taking the last write and then removing it is
        // the same rule the loop above states.
        // ⚠ Cleared for this graph before the loop below refills it, so a node that stops being
        // downstream of an unresolved import stops being hatched. Without this the flag would be a
        // one-way door: the author supplies the picture, the bake succeeds, and the node keeps the
        // "somebody else's to supply" swatch for the rest of the session.
        Forget(graph);

        foreach (var (node, image) in shown.ToArray()) {
            if (!unresolved.Contains(image)) {
                continue;
            }

            shown.Remove(node);
            unavailable.Add((graph, node));
            Skipped++;
        }

        var refused = false;

        foreach (var (node, image) in shown) {
            var picture = bake.Read(image);
            var had = registered.GetValueOrDefault((graph, node));
            var made = images?.Register(picture, had) ?? 0;

            if (had != 0 && made != had) {
                images?.Release(had);
            }

            refused |= made == 0 && images is not null;
            registered[(graph, node)] = made;
        }

        // ⚠ A sink that could not name a picture leaves the graph dirty, for the same reason a
        // missing device does — and this half was the one that actually fired. `EditorHost` acquires
        // the device in `Present()` and creates the thumbnail surface in `Sync()`, both *after*
        // `editor.Update`, so the first frame that has a device has no surface: a full compile, a
        // GPU bake and a `WaitIdle` producing zero swatches, after which `Update` had already taken
        // the graph off the dirty list and the panel stayed blank until the author typed something.
        // ⚠ `images is not null` is load bearing — a source with no sink at all (a test, a headless
        // editor) registers nothing by design and must not spin.
        if (refused) {
            Invalidate(graph);
        }

        // A node that has left the graph gives its number up, or the host holds a texture for a node
        // nothing will ever ask about again.
        foreach (var key in registered.Keys.ToArray()) {
            if (key.Graph != graph || shown.ContainsKey(key.Node)) {
                continue;
            }

            if (registered[key] != 0) {
                images?.Release(registered[key]);
            }

            registered.Remove(key);
        }
    }
}
