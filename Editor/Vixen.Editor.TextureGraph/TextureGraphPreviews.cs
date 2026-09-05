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
/// </remarks>
interface ITexturePreviewImages {
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
///         ⚠ <b>Nothing in the editor builds one of these yet.</b> It is the seam doc 48 § M4 asks
///         for and § D5 says results arrive back through; the panel that would own it is the same
///         batch's other half. Said here rather than discovered later.
///     </para>
/// </remarks>
sealed class TextureGraphPreviews : INodePreviewSource, IDisposable {
    /// <summary>How big a preview is, in texels.</summary>
    /// <remarks>
    ///     Bigger than <c>NodePreviewLayer.Size</c> draws it, so a zoomed-in canvas does not show a
    ///     soft square, and small enough that a graph full of them is a few megabytes.
    /// </remarks>
    public const int Size = 64;

    readonly Func<TextureGraphCompiler> compilers;
    readonly ITexturePreviewImages? images;
    readonly TexturePlanEvaluator evaluator;
    readonly Dictionary<(NodeGraphModel Graph, NodeId Node), ulong> registered = [];
    readonly HashSet<NodeGraphModel> watched = [];
    readonly List<NodeGraphModel> dirty = [];

    bool disposed;

    /// <summary>Builds a preview source on a device.</summary>
    /// <param name="device">Where the images are evaluated.</param>
    /// <param name="compilers">
    ///     Makes a compiler over the node library the graphs are edited against, with whatever
    ///     parameters, arguments and sub-graph library the host has. Its resolution and its
    ///     <see cref="TextureGraphCompiler.PreviewEveryNode" /> are overridden here.
    /// </param>
    /// <param name="images">
    ///     What turns a picture into a number the interface draws, or <see langword="null" /> for a
    ///     source whose pictures nobody shows — which is what a test has and what a headless editor
    ///     has.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> or <paramref name="compilers" /> is null.</exception>
    public TextureGraphPreviews(
        IGraphicsDevice device,
        Func<TextureGraphCompiler> compilers,
        ITexturePreviewImages? images = null
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(compilers);

        this.compilers = compilers;
        this.images = images;

        evaluator = new(device);
    }

    /// <summary>How many times a graph has been compiled to a plan — the cheap tier.</summary>
    public int Compilations { get; private set; }

    /// <summary>How many times a plan has been evaluated on the device — the expensive tier.</summary>
    public int Bakes { get; private set; }

    /// <summary>How many graphs were refused a picture because they did not compile.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted rather than reported.</b> A graph an author is halfway through wiring does
    ///     not compile most of the time, and a preview source that raised its diagnostics would be
    ///     a second, noisier copy of the panel that already shows them.
    /// </remarks>
    public int Refusals { get; private set; }

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
            preview = new(new Color4(1f, 1f, 1f, 1f), "", image);

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
        watched.Clear();
        dirty.Clear();
        evaluator.Dispose();
    }

    void Rebuild(NodeGraphModel graph) {
        var compiler = compilers();

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

        using var bake = evaluator.Evaluate(plan);

        Bakes++;

        // ⚠ The last image a node wrote and not the first. A node with two output ports — and a node
        // whose ports were allocated in declaration order — has several entries here, and the one an
        // author is looking at under the node is its result rather than an intermediate of it.
        Dictionary<NodeId, int> shown = [];

        foreach (var written in compiler.NodeImages) {
            shown[written.Node] = written.Image;
        }

        foreach (var (node, image) in shown) {
            var picture = bake.Read(image);
            var had = registered.GetValueOrDefault((graph, node));
            var made = images?.Register(picture, had) ?? 0;

            if (had != 0 && made != had) {
                images?.Release(had);
            }

            registered[(graph, node)] = made;
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
