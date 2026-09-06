// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Vixen.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Texturing.Painting;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>One thing wrong with a stack, about a layer an artist can select.</summary>
/// <param name="Severity">Whether it stops the compile.</param>
/// <param name="Layer">The <see cref="LayerAsset.Id" /> it is about, or empty for the set itself.</param>
/// <param name="Message">What is wrong, as a person would say it.</param>
readonly record struct LayerStackProblem(NodeSeverity Severity, string Layer, string Message) {
    /// <summary>A problem that stops the compile.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The problem.</returns>
    public static LayerStackProblem Refusal(string layer, string message) =>
        new(NodeSeverity.Error, layer, message);

    /// <summary>A problem the compile reports and then compiles anyway.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The problem.</returns>
    public static LayerStackProblem Warning(string layer, string message) =>
        new(NodeSeverity.Warning, layer, message);

    /// <inheritdoc />
    public override string ToString() =>
        Layer.Length == 0 ? Message : $"layer '{Layer}': {Message}";
}

/// <summary>What one node in an exploded graph came out of, so a comment can say so.</summary>
/// <param name="Node">The node.</param>
/// <param name="Text">The sentence to put beside it.</param>
/// <remarks>
///     ⚠ <b>Collected during the build and used only by <see cref="LayerStackExplode" />.</b>
///     Compiling never reads them, which is what keeps doc 48 exit criterion 6's differential
///     meaningful: if a note could reach the compiler, the exploded graph and the compiled one would
///     agree because they were the same object rather than because they mean the same thing.
/// </remarks>
readonly record struct LayerNote(NodeId Node, string Text);

/// <summary>Where one mask entry's pixels come from, and whether a compositor may read it.</summary>
/// <param name="Port">The port carrying the image.</param>
/// <param name="Opaque">Whether its alpha is 1 everywhere, so a compositor may read it as a number.</param>
/// <remarks>
///     <para>
///         ⚠ <b><paramref name="Opaque" /> is what stops a mask entry disappearing into the mask
///         stack's own blends</b> — <a href="https://github.com/Rikarin/Vixen/issues/874">#874</a>.
///         A mask is a <em>number</em>, and the stack that composites mask entries composites them
///         with <c>Colour/Blend</c>, which is a compositor: it reads the alpha it is handed as
///         coverage, so an entry whose alpha is 0 blends by nothing however bright its red is. A
///         constant and a folded anchor are opaque by construction; a bitmap's alpha is a PNG's, a
///         generator's is whatever the compound wrote, and a mesh map's is whatever the <em>baker</em>
///         wrote — so those three are forced before they meet a blend, and this says which is which.
///     </para>
///     <para>
///         ⚠ <b>An anchor used to arrive here as a slot with no port at all, and no longer
///         does.</b> The deferral that keeps <c>NodeGraphModel.TryConnect</c> the only thing refusing
///         a loop — doc 48 § D10 — now lives in <c>LayerStackGraph.Anchored</c>, which builds the
///         fold an anchor's coverage needs and defers only the edges into it. Every slot therefore
///         carries a real port, and wiring one is <c>graph.Connect</c> rather than a branch that
///         might connect nothing.
///     </para>
/// </remarks>
readonly record struct MaskSlot(PortRef Port, bool Opaque);

/// <summary>The graph a texture set's stack is, and what building it had to say.</summary>
/// <param name="Graph">The nodes and wires.</param>
/// <param name="Problems">What the build reported.</param>
/// <param name="Notes">What each layer's composite node came out of.</param>
sealed record LayerStackBuild(
    NodeGraphModel Graph,
    ImmutableArray<LayerStackProblem> Problems,
    ImmutableArray<LayerNote> Notes
) {
    /// <summary>Whether anything stops this graph being compiled.</summary>
    public bool HasErrors {
        get {
            foreach (var problem in Problems) {
                if (problem.Severity == NodeSeverity.Error) {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>A texture set's layer stack, as a node graph.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 48 § D1's load-bearing decision, and this class <em>is</em> it.</b> The layer
///         stack does not get an evaluator of its own and it does not get a plan emitter of its own
///         either: it builds a <c>NodeGraphModel</c> out of the same <c>[Node]</c> classes an artist
///         wires by hand, and <c>TextureGraphCompiler</c> turns that into the <c>TexturePlan</c>. So
///         "overlay" cannot come to mean two things, because there is only one thing that knows what
///         it means — <c>Blend.rvn</c>, reached the same way from both front ends.
///     </para>
///     <para>
///         <b>Which is also what makes <c>LayerStackExplode</c> honest rather than a second
///         renderer.</b> Explode writes <em>this</em> graph out, with comments and a layout;
///         compiling compiles it. Doc 48 exit criterion 6 asks for a stack and its explosion to bake
///         byte-identical outputs, and <c>LayerStackExplodeTests</c> compares the two compilations
///         op by op with the explosion taken back off a YAML round trip — which is where the
///         difference between the two paths actually lives.
///     </para>
///     <para>
///         ⚠ <b>An anchor is compiled as an edge and the cycle check is
///         <c>NodeGraphModel.TryConnect</c>'s.</b> Doc 48 § D10 says the stack compiles
///         <em>through</em> the graph model rather than growing a second cycle check, so every
///         anchor edge is deferred to the end of the build and then offered to the model: an anchor
///         onto a layer at or above its own closes a loop in the graph and comes back as
///         <see cref="GraphConnectionError.Cycle" />, which is reported verbatim. Nothing here walks
///         the stack looking for loops.
///     </para>
/// </remarks>
static class LayerStackGraph {
    /// <summary>How deeply groups may nest before the build gives up.</summary>
    /// <remarks>
    ///     A hang check and not a design limit: a <c>.vxlayers</c> is a file, a file can be
    ///     hand-edited, and a group that contains itself would otherwise recurse until the stack
    ///     overflows. Sixteen is far past what an artist builds.
    /// </remarks>
    public const int MaxGroupDepth = 16;

    /// <summary>The stack of one texture set, as a graph.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">Which of its sets.</param>
    /// <param name="registry">
    ///     The node types a generator or a mask effect is looked up in, or <see langword="null" />
    ///     for this build's own library with the shipped compounds published into it.
    /// </param>
    /// <returns>The graph and what building it had to say.</returns>
    /// <exception cref="ArgumentNullException">The stack or the set is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The registry is an input to <em>building</em> and not only to compiling, which it was
    ///     not before M8.</b> A generator and a mask effect are named by node-type path, and which
    ///     port of one carries the image is a fact only the registry has — so a builder without one
    ///     would have to hard-code a port name per effect, which is exactly the second list
    ///     <c>LayerFilterKind</c>'s hand-written ports already are.
    /// </remarks>
    public static LayerStackBuild Build(
        LayerStackAsset stack,
        TextureSetAsset set,
        NodeTypeRegistry? registry = null
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);

        Builder builder = new(stack, set, registry ?? LayerStackCompiler.Library(out _));

        return builder.Run();
    }

    /// <summary>The port a filter's numbers may name, per filter kind.</summary>
    /// <param name="filter">Which adjustment.</param>
    /// <returns>The node type, and the scalar ports it takes.</returns>
    /// <remarks>
    ///     ⚠ <b>Written here rather than read off the registry, and the difference is what a wrong
    ///     name does.</b> A registry lookup would accept every port the node declares, including its
    ///     image input — so a stack naming <c>Input</c> in <see cref="LayerAsset.Settings" /> would
    ///     overwrite the wire that carries the layers beneath it with a constant, silently. The list
    ///     is the numbers, and only the numbers.
    /// </remarks>
    public static (string Type, string[] Ports) Filter(LayerFilterKind filter) =>
        filter switch {
            LayerFilterKind.Levels => (
                "Colour/Levels",
                ["Input Black", "Input White", "Gamma", "Output Black", "Output White", "Dither"]
            ),
            LayerFilterKind.Hsl => ("Colour/HSL", ["Hue", "Saturation", "Lightness"]),
            LayerFilterKind.Blur => ("Filters/Blur", ["Radius"]),
            LayerFilterKind.Invert => ("Colour/Invert", ["Red", "Green", "Blue", "Alpha"]),
            LayerFilterKind.Grayscale => ("Colour/Grayscale", ["Weight R", "Weight G", "Weight B"]),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Not a filter this build knows.")
        };

    /// <summary>The walk, with the graph it is filling and the problems it is collecting.</summary>
    sealed class Builder(LayerStackAsset stack, TextureSetAsset set, NodeTypeRegistry library) {
        /// <summary>The node types a generator or a mask effect is resolved against.</summary>
        NodeTypeRegistry Library => library;

        readonly List<LayerStackProblem> problems = [];
        readonly List<LayerNote> notes = [];

        /// <summary>Anchor edges, held until every layer's result node exists.</summary>
        /// <remarks>
        ///     ⚠ <b>Deferred so that the graph model does the cycle check.</b> Connected as each
        ///     mask was built, a forward anchor would name a node that does not exist yet and the
        ///     builder would have to decide for itself whether that is a loop or a wire it has not
        ///     reached — which is the second cycle check § D10 forbids. Applied at the end, every
        ///     node exists, and a forward or self anchor is a genuine loop in a real graph that
        ///     <see cref="NodeGraphModel.TryConnect" /> refuses on its own terms.
        /// </remarks>
        readonly List<(string Channel, string Anchor, string Layer, PortRef To)> anchors = [];

        /// <summary>Each layer's composite result, per channel, for an anchor to read.</summary>
        readonly Dictionary<(string Channel, string Layer), PortRef> results = [];

        readonly NodeGraphModel graph = new() {
            Name = set.Name.Length > 0 ? $"{stack.Name} · {set.Name}" : stack.Name
        };

        float column;
        float row;

        public LayerStackBuild Run() {
            if (set.Channels.Count == 0) {
                problems.Add(LayerStackProblem.Refusal(
                    "",
                    $"Texture set '{set.Name}' declares no channels, so there is nothing for its layers to write. "
                    + "A set's channel list is doc 48 § D11's, and a stack with none compiles to a plan with no "
                    + "outputs — which evaluates and produces nothing anybody can look at."
                ));
            }

            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (var channel in set.Channels) {
                if (!seen.Add(channel.Usage)) {
                    problems.Add(LayerStackProblem.Refusal(
                        "",
                        $"Texture set '{set.Name}' declares the channel '{channel.Usage}' twice. Two Output nodes "
                        + "under one usage is two maps a bake would write to one file, last one winning."
                    ));
                }
            }

            Duplicates();

            for (var index = 0; index < set.Channels.Count; index++) {
                row = index;
                column = 0f;

                Channel(set.Channels[index]);
            }

            Anchors();

            return new(graph, [.. problems], [.. notes]);
        }

        /// <summary>Refuses two layers sharing an identity, because an anchor names one of them.</summary>
        void Duplicates() {
            HashSet<string> ids = new(StringComparer.Ordinal);

            void Walk(List<LayerAsset> layers) {
                foreach (var layer in layers) {
                    if (layer.Id.Length > 0 && !ids.Add(layer.Id)) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            $"Two layers share the id '{layer.Id}'. An anchor names a layer by id, so a duplicate "
                            + "makes the mask read whichever of the two the walk reached first — a picture that "
                            + "changes when the artist reorders layers that are not the anchored one."
                        ));
                    }

                    Walk(layer.Children);
                }
            }

            Walk(set.Layers);
        }

        /// <summary>One channel's chain: a base colour, every layer over it, and an output.</summary>
        void Channel(ChannelAsset channel) {
            var start = Add("Source/Uniform");

            start.SetValue("Colour", Colour(channel.Default, channel.Usage, ""));

            PortRef cursor = new(start.Id, "Out");

            cursor = Stack(set.Layers, channel, cursor, 0);

            var output = Add("Output/Output");

            output.SetText("Usage", channel.Usage);
            graph.Connect(cursor, new(output.Id, "Input"));

            notes.Add(new(output.Id, $"Channel '{channel.Usage}' of texture set '{set.Name}'."));
        }

        /// <summary>Every layer of one list composited over a cursor, bottom first.</summary>
        PortRef Stack(List<LayerAsset> layers, ChannelAsset channel, PortRef cursor, int depth) {
            if (depth >= MaxGroupDepth) {
                problems.Add(LayerStackProblem.Refusal(
                    "",
                    $"Groups nest more than {MaxGroupDepth} deep in texture set '{set.Name}'. That is a hang "
                    + "check rather than a design limit — a hand-edited .vxlayers can name a group inside itself."
                ));

                return cursor;
            }

            foreach (var layer in layers) {
                cursor = Layer(layer, channel, cursor, depth);
            }

            return cursor;
        }

        /// <summary>One layer composited over the cursor, or the cursor unchanged.</summary>
        PortRef Layer(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) {
            if (!layer.Enabled || !layer.Writes(channel.Usage)) {
                return cursor;
            }

            if (layer.Projection != LayerProjection.Uv) {
                problems.Add(LayerStackProblem.Refusal(
                    layer.Id,
                    $"Projection '{layer.Projection}' needs a node that samples an image by a projected world "
                    + "position, blended by the world normal. ⚠ The two mesh maps it reads — 'position' and "
                    + "'world' — are bakeable and reachable now, so what is left is the projection node "
                    + "itself: #815. Only Uv compiles in this build."
                ));

                return cursor;
            }

            var content = Content(layer, channel, cursor, depth);

            if (content is not { } foreground) {
                return cursor;
            }

            foreground = Mask(layer, channel, foreground);

            var blend = Add("Colour/Blend");

            blend.SetText("Mode", layer.Blend.ToString());
            blend.SetValue("Opacity", Opacity(layer, channel));

            // ⚠ **A filter layer's content *is* the backdrop, so it composites atop it and never over
            // it** — [#845](https://github.com/Rikarin/Vixen/issues/845). `Adjustment` wires the
            // cursor into the filter node and hands the node back, so the foreground here carries the
            // cursor's own coverage; blending it over the cursor accumulated that coverage with
            // itself. Over an opaque canvas the two rules agree exactly, which is why this was
            // invisible until a group isolated a chain: at K = ½ a filter left the group ¾ covered and
            // applied a third less adjustment than it was asked for. Every other kind of layer *does*
            // arrive on top and keeps `Over`.
            if (layer.Kind == LayerKind.Filter) {
                blend.SetText("Coverage", "Atop");
            }

            graph.Connect(cursor, new(blend.Id, "Background"));
            graph.Connect(foreground, new(blend.Id, "Foreground"));

            PortRef result = new(blend.Id, "Out");

            if (layer.Id.Length > 0) {
                results[(channel.Usage, layer.Id)] = result;
            }

            notes.Add(new(
                blend.Id,
                $"Layer '{(layer.Name.Length > 0 ? layer.Name : layer.Id)}' · {layer.Kind} · {layer.Blend} at "
                + $"{(layer.Opacity * 100f).ToString("0.#", CultureInfo.InvariantCulture)}%"
                + (layer.Mask.Source == LayerMaskSource.None ? "" : $" · {layer.Mask.Source} mask")
                + $" · channel '{channel.Usage}'."
            ));

            return result;
        }

        /// <summary>
        ///     What the layer puts on top, or <see langword="null" /> when it contributes nothing.
        /// </summary>
        PortRef? Content(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) =>
            layer.Kind switch {
                LayerKind.Fill => Fill(layer, channel),
                LayerKind.Filter => Adjustment(layer, cursor),
                LayerKind.Group => Group(layer, channel, cursor, depth),
                LayerKind.Paint => Paint(layer, channel),
                _ => throw new ArgumentOutOfRangeException(nameof(layer), layer.Kind, "Not a layer kind this build knows.")
            };

        /// <summary>A group's children, passed through or isolated, or nothing when they add none.</summary>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>A group passes through under <see cref="LayerBlendMode.Copy" /> and isolates
        ///         under every other mode, which is the distinction
        ///         <a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> · 1 was missing.</b>
        ///         Compositing the children onto the cursor and blending the <em>result</em> back
        ///         over that same cursor applies the group's mode to the whole canvas rather than to
        ///         what the group covered: with a canvas of ½ and a group of one fully-masked child,
        ///         a <c>Multiply</c> group baked ¼ where it had to bake ½. The cursor is both "what
        ///         this group wrote" and "what was already there", and a non-Copy operator cannot
        ///         tell them apart.
        ///     </para>
        ///     <para>
        ///         <b>So an isolated group's children start from transparency, and the alpha they
        ///         accumulate <em>is</em> the group's coverage.</b> <c>Blend.rvn</c> computes
        ///         <c>amount = saturate(opacity) · saturate(b.w)</c>, so a texel no child covered
        ///         arrives with alpha 0, blends by 0 and leaves the backdrop exactly as it was —
        ///         whatever the operator. That is what makes the group's own mode, opacity and mask
        ///         mean something without a coverage channel of this class's own.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>And starting a chain from transparency is the one thing in this file that
        ///         asks anything of <c>Blend.rvn</c> beyond an opaque backdrop, which is why
        ///         <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a> · 2 landed there
        ///         rather than here.</b> The kernel used to implement the opaque-backdrop
        ///         specialisation of source-over — exact for every chain that starts at a source,
        ///         and premultiplying for one that starts at nothing — so a group of partial
        ///         coverage handed back a colour already lerped towards black and its own blend then
        ///         consumed it as a straight one, darkening it by exactly its coverage. The fix is
        ///         the general form in the kernel and no node at all here: this class does not need
        ///         to un-premultiply what a correct compositor never premultiplied.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Pass-through is kept for <c>Copy</c> rather than isolating everything, and
        ///         the reason is a child's own operator.</b> A child set to <c>Multiply</c> inside an
        ///         isolated group multiplies against an empty backdrop and darkens to nothing — the
        ///         standard behaviour of an isolated group in every compositor, and exactly why
        ///         grouping layers under the default mode must not change the picture. Under
        ///         <c>Copy</c> the result is the same either way for a covered texel, so the mode
        ///         that means "do not reinterpret my children" is the one that does not isolate.
        ///     </para>
        /// </remarks>
        PortRef? Group(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) {
            if (layer.Blend == LayerBlendMode.Copy) {
                var passed = Stack(layer.Children, channel, cursor, depth + 1);

                // Every child was disabled, restricted to other channels, or refused. Blending the
                // cursor over itself would be a dispatch that changes nothing — and one whose *op
                // index* moves every seed downstream of it.
                return passed == cursor ? null : passed;
            }

            var empty = Add("Source/Uniform");

            empty.SetValue("Colour", 0f, 0f, 0f, 0f);

            PortRef transparent = new(empty.Id, "Out");
            var inner = Stack(layer.Children, channel, transparent, depth + 1);

            if (inner != transparent) {
                return inner;
            }

            // Nothing composited onto it, so the backdrop this group would have blended is a
            // transparent constant nothing reads. Removed rather than left dangling: an unread
            // Source/Uniform is still an op in the plan, and an op is an index every seed downstream
            // of it derives from.
            graph.Remove(empty.Id, out _);

            return null;
        }

        /// <summary>M9's layer: a bitmap source reading one channel of the layer's canvas.</summary>
        /// <remarks>
        ///     <para>
        ///         <b>Exactly <see cref="LayerFillSource.Texture" />'s node, with a reference a host
        ///         resolves differently</b> —
        ///         <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>. A paint layer is a
        ///         picture; the only thing that distinguishes it from an imported one is where the
        ///         bytes come from, and that is a question for whoever fills the plan's externals.
        ///         So there is no paint node, no paint op and no second path through the compiler —
        ///         which is what keeps doc 48 § D1's "the stack goes through the same compiler"
        ///         true of the layer kind that could most easily have broken it.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>One reference per channel, because a <c>.vxpaint</c> is one image per
        ///         channel.</b> <c>PaintCanvas</c>' own remarks say why the file is shaped that way:
        ///         a layer that paints roughness alone must not also carry a base-colour buffer it
        ///         never writes. <see cref="Content" /> is called once per channel the set writes, so
        ///         this emits the bitmap for <em>that</em> channel and the unwritten ones never
        ///         appear.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>A layer nobody has painted on is a warning and not a refusal, and the
        ///         difference is what an artist sees.</b> A refusal takes the whole stack's plan with
        ///         it — <c>LayerStackCompiler.Compile</c> stops on <c>HasErrors</c> — so the moment a
        ///         panel created a paint layer, every other layer in the stack would stop previewing
        ///         until the first stroke landed. An empty canvas contributes nothing, which is
        ///         exactly what an unpainted layer means.
        ///     </para>
        /// </remarks>
        PortRef? Paint(LayerAsset layer, ChannelAsset channel) {
            var path = layer.Paint.Trim();

            if (path.Length == 0) {
                problems.Add(LayerStackProblem.Warning(
                    layer.Id,
                    "This paint layer names no .vxpaint, so it has nothing to composite. A canvas is written "
                    + "the first time the layer is painted on; until then the layer keeps its blend mode, its "
                    + "opacity and its channel enables and contributes nothing."
                ));

                return null;
            }

            var node = Add("Source/Bitmap");

            node.SetText("Source", PaintReference.Reference(path, channel.Usage));

            // The texture fill's two settings, for its reasons: linear because the colour space is a
            // fact about the picture, and bilinear because a canvas authored at one resolution is
            // read at whatever the bake asked for.
            node.SetText("Space", "Linear");
            node.SetText("Filter", "Bilinear");

            return new(node.Id, "Out");
        }

        /// <summary>A fill layer's source node.</summary>
        PortRef? Fill(LayerAsset layer, ChannelAsset channel) {
            switch (layer.Fill) {
                case LayerFillSource.Constant: {
                    if (!layer.Values.TryGetValue(channel.Usage, out var authored)) {
                        // ⚠ Nothing, rather than the channel's own Default — #807 · 2. A layer that
                        // restricts no channels writes all of them, so a fill authoring base colour
                        // alone reaches roughness too; falling back to the channel's base default
                        // there composites 0.9 over whatever a layer beneath it had set, as though
                        // an artist had asked for it. "This layer has nothing to say about that
                        // channel" is the only thing an absent entry can mean, and the way to say it
                        // is to cover nothing. `LayerAsset.Values`' own remarks describe the fill
                        // that sets one channel and leaves the rest; this is that sentence made true.
                        return null;
                    }

                    var node = Add("Source/Uniform");
                    var colour = Colour(authored, channel.Usage, layer.Id);

                    // ⚠ Alpha 1 here and the authored alpha folded into the opacity by `Opacity`.
                    // `Blend.rvn` computes `amount = saturate(opacity) * saturate(b.w)`, so for values
                    // in range the two are the same number — and doing it this way means a mask, which
                    // *replaces* the foreground's alpha, cannot throw the constant's alpha away. #790.
                    node.SetValue("Colour", colour[0], colour[1], colour[2], 1f);

                    return new(node.Id, "Out");
                }

                case LayerFillSource.Texture: {
                    if (!layer.Textures.TryGetValue(channel.Usage, out var asset) || asset.Trim().Length == 0) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            $"This layer is a texture fill and names no image for channel '{channel.Usage}', which "
                            + "it writes. Restrict the layer's channels or give it an image: a bitmap with no "
                            + "reference is refused at compile time rather than filled with black."
                        ));

                        return null;
                    }

                    var node = Add("Source/Bitmap");

                    node.SetText("Source", asset.Trim());

                    // Linear, which is `BitmapNode`'s own default and the one that does nothing: the
                    // colour space is a fact about the picture and a stack cannot see the picture.
                    node.SetText("Space", "Linear");
                    node.SetText("Filter", "Bilinear");

                    return new(node.Id, "Out");
                }

                case LayerFillSource.Graph: {
                    // The same resolution a generator mask gets, and deliberately the same code: a
                    // graph fill and a generator are one mechanism — a published compound named by
                    // its node-type path, inlined by the compiler's SubGraphSource — pointed at the
                    // colour instead of at the mask.
                    var path = layer.Graph.Trim();

                    if (path.Length == 0) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            "This layer is a graph fill and names no graph. A graph fill is a published "
                            + ".vxtexgraph, named by its path in the node menu."
                        ));

                        return null;
                    }

                    if (!Library.TryGet(path, out var type)) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            $"This layer is a graph fill naming '{path}', which is not a node type this project "
                            + "has. A published compound is registered under its path in the library folder."
                        ));

                        return null;
                    }

                    if (OnlyImage(type, PortDirection.Output) is not { } output) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            $"The graph '{path}' does not have exactly one image output, so which of its results "
                            + "this layer fills with is not something this file can decide."
                        ));

                        return null;
                    }

                    var node = Add(path);

                    return new(node.Id, output.Name);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(layer), layer.Fill, "Not a fill this build knows.");
            }
        }

        /// <summary>A filter layer's adjustment, reading everything under it.</summary>
        PortRef? Adjustment(LayerAsset layer, PortRef cursor) {
            var (type, ports) = Filter(layer.Filter);
            var node = Add(type);

            graph.Connect(cursor, new(node.Id, "Input"));

            foreach (var (port, value) in layer.Settings) {
                var known = false;

                foreach (var candidate in ports) {
                    if (string.Equals(candidate, port, StringComparison.Ordinal)) {
                        known = true;

                        break;
                    }
                }

                if (!known) {
                    problems.Add(LayerStackProblem.Warning(
                        layer.Id,
                        $"'{port}' is not a number the '{layer.Filter}' filter takes — it takes "
                        + $"{string.Join(", ", ports)}. The value is dropped rather than written to a port that "
                        + "might be the image input."
                    ));

                    continue;
                }

                node.SetValue(port, value);
            }

            return new(node.Id, "Out");
        }

        /// <summary>The mask multiplied into the foreground's coverage, or the foreground unchanged.</summary>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>The mask is composited before it is shuffled, and the shuffle is still what
        ///         puts it into alpha.</b> A mask stack is a chain of <c>Colour/Blend</c> nodes
        ///         exactly like the layer stack it masks — the same operators, the same kernel — and
        ///         only its <em>result</em> becomes the foreground's coverage. Doc 48 § D10's "a mask
        ///         is itself a small stack" is therefore not a second compositor: it is this one,
        ///         called on a smaller list.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Multiplied and not <em>replaced</em>, which is
        ///         <a href="https://github.com/Rikarin/Vixen/issues/790">#790</a> and half of
        ///         <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a>.</b> A mask is
        ///         coverage and so is the foreground's alpha, and two coverages compose by
        ///         multiplication. Replacing agrees with multiplying exactly when the foreground's
        ///         alpha is already 1 — which a constant fill's is, by construction, which is why
        ///         the defect stayed invisible while a fill was the only content a stack could
        ///         express. It is not 1 for an imported image with a transparent region, and it is
        ///         emphatically not 1 for a group, where the alpha <em>is</em> what the group
        ///         covered: replacing it there throws away the isolation the group was built for and
        ///         a group masked to white composites everything, including the texels none of its
        ///         children wrote.
        ///     </para>
        ///     <para>
        ///         <b>Three nodes rather than one, because this library has no arithmetic node.</b>
        ///         The product has to be computed in a colour lane — <c>Colour/Blend</c>'s alpha rule
        ///         only ever <em>raises</em> alpha, so no composite can express one — and both
        ///         operands have to be made opaque first, because <c>Blend</c> is a compositor and
        ///         reads an alpha it is handed as coverage rather than as a number. Hence a shuffle
        ///         that lifts the foreground's alpha into grey, a shuffle that lifts the mask's red
        ///         into grey, and a <c>Multiply</c> between them.
        ///         <a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>'s fold removes all
        ///         four dispatches for a constant mask, and ⚠ that fold is <em>exact</em> under this
        ///         rule and was not under the old one: <c>amount</c> is already
        ///         <c>opacity · mask · alpha</c>.
        ///     </para>
        /// </remarks>
        PortRef Mask(LayerAsset layer, ChannelAsset channel, PortRef foreground) {
            var cursor = MaskImage(layer, channel);

            if (cursor is not { } mask) {
                return foreground;
            }

            var carried = Add("Colour/Channel Shuffle");

            // The foreground's own coverage, as a grey, forced opaque so that the Multiply below
            // reads it as a number rather than as something to composite by.
            carried.SetText("Red From", "FirstAlpha");
            carried.SetText("Green From", "FirstAlpha");
            carried.SetText("Blue From", "FirstAlpha");
            carried.SetText("Alpha From", "One");

            graph.Connect(foreground, new(carried.Id, "First"));

            var opaque = Add("Colour/Channel Shuffle");

            // And the mask's red, the same way. ⚠ A mask's own alpha is not part of what it means —
            // a bitmap mask is read for its red — so it is replaced rather than carried, or a PNG
            // whose alpha happens to be zero would mask nothing.
            opaque.SetText("Red From", "FirstRed");
            opaque.SetText("Green From", "FirstRed");
            opaque.SetText("Blue From", "FirstRed");
            opaque.SetText("Alpha From", "One");

            graph.Connect(mask.Port, new(opaque.Id, "First"));

            var product = Add("Colour/Blend");

            product.SetText("Mode", nameof(LayerBlendMode.Multiply));
            product.SetValue("Opacity", 1f);

            graph.Connect(new(carried.Id, "Out"), new(product.Id, "Background"));
            graph.Connect(new(opaque.Id, "Out"), new(product.Id, "Foreground"));

            var shuffle = Add("Colour/Channel Shuffle");

            // Written out rather than left to the node's defaults, because an exploded graph is
            // something an artist reads: the three that pass through and the one that does not are
            // the whole of what a mask is.
            shuffle.SetText("Red From", "FirstRed");
            shuffle.SetText("Green From", "FirstGreen");
            shuffle.SetText("Blue From", "FirstBlue");
            shuffle.SetText("Alpha From", "SecondRed");

            graph.Connect(foreground, new(shuffle.Id, "First"));
            graph.Connect(new(product.Id, "Out"), new(shuffle.Id, "Second"));

            return new(shuffle.Id, "Out");
        }

        /// <summary>The mask's own composited image, or <see langword="null" /> for no mask at all.</summary>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>The entries are composited by <c>Colour/Blend</c>, so every operand of one of
        ///         those blends is forced opaque first</b> —
        ///         <a href="https://github.com/Rikarin/Vixen/issues/874">#874</a>, which is
        ///         <see cref="Mask" />'s rule (#832) one level down. <c>Blend</c> is a compositor: it
        ///         reads the alpha it is handed as coverage rather than as a number, so a bitmap
        ///         entry whose PNG happens to be transparent composited by nothing at all — the
        ///         *exact* failure <see cref="Mask" />'s own <c>opaque</c> shuffle exists to prevent,
        ///         arriving one level up where nothing forced it.
        ///     </para>
        ///     <para>
        ///         <b>Forced where it is needed and not everywhere</b>, which is what
        ///         <c>MaskSlot.Opaque</c> is for: a constant, a mesh map and a folded anchor are
        ///         opaque by construction, and a one-entry mask reaches no blend here at all — so the
        ///         plain single-source mask #789 costs out compiles to exactly the nodes it did.
        ///     </para>
        /// </remarks>
        MaskSlot? MaskImage(LayerAsset layer, ChannelAsset channel) {
            var mask = layer.Mask;
            List<MaskLayerAsset> entries = [];

            if (mask.Source != LayerMaskSource.None) {
                // The legacy single source, as the bottom entry. Its blend and opacity are the
                // neutral ones, so a stack with only a base compiles to exactly the nodes it did
                // before this file learned about mask stacks.
                entries.Add(new() {
                    Source = mask.Source,
                    Value = mask.Value,
                    Asset = mask.Asset,
                    Anchor = mask.Anchor,
                    Generator = mask.Generator,
                    Map = mask.Map,
                    Paint = mask.Paint
                });
            } else if (mask.Paint.Length > 0) {
                problems.Add(LayerStackProblem.Warning(
                    layer.Id,
                    $"This layer's mask names a paint file ('{mask.Paint}') and its source is None, so the "
                    + "painted pixels are not read. A painted mask is M9 (#574)."
                ));
            }

            foreach (var entry in mask.Layers) {
                if (entry.Enabled) {
                    entries.Add(entry);
                }
            }

            MaskSlot? cursor = null;

            foreach (var entry in entries) {
                var source = MaskSource(entry, channel, layer.Id);

                if (source is not { } wire) {
                    // Already reported. A refusal stops the compile, so there is no picture to be
                    // wrong about; the walk continues so that one bad entry does not hide the rest.
                    continue;
                }

                if (cursor is not { } beneath) {
                    if (entry.Blend != LayerBlendMode.Copy) {
                        problems.Add(LayerStackProblem.Warning(
                            layer.Id,
                            $"The bottom entry of this mask composites with '{entry.Blend}' and there is nothing "
                            + "beneath it, so the operator does nothing. A mask stack starts from its first "
                            + "entry rather than from a black constant, which is what keeps a plain one-source "
                            + "mask a single node."
                        ));
                    }

                    cursor = wire;

                    continue;
                }

                var blend = Add("Colour/Blend");

                blend.SetText("Mode", entry.Blend.ToString());
                blend.SetValue("Opacity", entry.Opacity);

                graph.Connect(Opaque(beneath).Port, new(blend.Id, "Background"));
                graph.Connect(Opaque(wire).Port, new(blend.Id, "Foreground"));

                // A composite of two opaque operands is opaque — `Blend`'s alpha rule is
                // `αb + (1 − αb)·αs`, which is 1 whenever both are — so a three-entry stack forces
                // the bottom one once and never the running result.
                cursor = new(new PortRef(blend.Id, "Out"), true);
            }

            if (cursor is not { } composited) {
                return null;
            }

            foreach (var effect in mask.Effects) {
                if (effect.Enabled) {
                    composited = Effect(composited, effect, layer.Id, channel);
                }
            }

            return composited;
        }

        /// <summary>The same mask entry with its alpha replaced by 1, or the entry unchanged.</summary>
        /// <param name="slot">The entry.</param>
        /// <returns>A slot a compositor may read as a number.</returns>
        /// <remarks>
        ///     ⚠ <b>The red is splatted across the colour lanes as well, so that this is the same
        ///     shuffle <see cref="Mask" /> ends with rather than a second convention.</b> A mask is
        ///     read for its red everywhere in this file; carrying green and blue through would make a
        ///     coloured bitmap entry composite differently under <c>Multiply</c> from the way the same
        ///     bitmap composites as the only entry, and nothing downstream would say so. #874.
        /// </remarks>
        MaskSlot Opaque(MaskSlot slot) {
            if (slot.Opaque) {
                return slot;
            }

            var node = Add("Colour/Channel Shuffle");

            node.SetText("Red From", "FirstRed");
            node.SetText("Green From", "FirstRed");
            node.SetText("Blue From", "FirstRed");
            node.SetText("Alpha From", "One");

            graph.Connect(slot.Port, new(node.Id, "First"));

            return new(new PortRef(node.Id, "Out"), true);
        }

        /// <summary>An anchored layer's result, as the opaque number a mask entry is.</summary>
        /// <param name="anchor">The <see cref="LayerAsset.Id" /> being anchored.</param>
        /// <param name="channel">Which channel's chain to read that layer's result from.</param>
        /// <param name="layerId">The layer the mask belongs to, for a diagnostic.</param>
        /// <returns>The port carrying <c>red · coverage</c>.</returns>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>An anchor is the one mask source whose alpha <em>is</em> meaningful, and
        ///         folding it into the value is what #832 stopped doing by accident</b> —
        ///         <a href="https://github.com/Rikarin/Vixen/issues/874">#874</a>. An anchor resolves
        ///         to another layer's <em>evaluated result</em>, and for a layer inside an isolated
        ///         group that result's alpha is the group's coverage. Read for its red alone, an
        ///         anchor onto a layer covering a quarter of the texel masks as though it covered all
        ///         of it — "use that layer as a mask" quietly becoming "use the colour that layer
        ///         would have had".
        ///     </para>
        ///     <para>
        ///         <b>The product rather than the coverage alone, and it is the answer that changes
        ///         nothing for every stack that already worked.</b> Anchoring a generator or a fill —
        ///         opaque, coverage 1 — gives <c>red · 1</c>, which is the red it gave before; the
        ///         coverage only bites where the anchored layer does not cover, which is where the
        ///         old kernel's premultiplied result also faded. Reading the alpha alone would make
        ///         an anchor onto any ordinary layer a constant 1, which is not a mask.
        ///     </para>
        ///     <para>
        ///         <b>Three nodes, for <see cref="Mask" />'s reason: this library has no arithmetic
        ///         node</b>, so a product is computed in a colour lane by a <c>Multiply</c> between
        ///         two operands that have each been made opaque first. ⚠ <b>Two deferred edges out of
        ///         one anchor</b>, because both shuffles read the same result and a shuffle selects
        ///         rather than multiplies; <see cref="Anchors" /> reports a refused anchor once.
        ///     </para>
        /// </remarks>
        PortRef Anchored(string anchor, ChannelAsset channel, string layerId) {
            var value = Add("Colour/Channel Shuffle");

            value.SetText("Red From", "FirstRed");
            value.SetText("Green From", "FirstRed");
            value.SetText("Blue From", "FirstRed");
            value.SetText("Alpha From", "One");

            var covered = Add("Colour/Channel Shuffle");

            covered.SetText("Red From", "FirstAlpha");
            covered.SetText("Green From", "FirstAlpha");
            covered.SetText("Blue From", "FirstAlpha");
            covered.SetText("Alpha From", "One");

            anchors.Add((channel.Usage, anchor, layerId, new(value.Id, "First")));
            anchors.Add((channel.Usage, anchor, layerId, new(covered.Id, "First")));

            var product = Add("Colour/Blend");

            product.SetText("Mode", nameof(LayerBlendMode.Multiply));
            product.SetValue("Opacity", 1f);

            graph.Connect(new(value.Id, "Out"), new(product.Id, "Background"));
            graph.Connect(new(covered.Id, "Out"), new(product.Id, "Foreground"));

            return new(product.Id, "Out");
        }

        /// <summary>One adjustment over a mask, as any node with one image in and one image out.</summary>
        MaskSlot Effect(MaskSlot cursor, MaskEffectAsset effect, string layerId, ChannelAsset channel) {
            var path = effect.Node.Trim();

            if (path.Length == 0) {
                problems.Add(LayerStackProblem.Refusal(
                    layerId,
                    "A mask effect names no node type. An effect is any single-input graph — Levels, Blur, "
                    + "Warp, or a published compound — named by its path in the node menu."
                ));

                return cursor;
            }

            if (!Library.TryGet(path, out var type)) {
                problems.Add(LayerStackProblem.Refusal(
                    layerId,
                    $"A mask effect names '{path}', which is not a node type this project has. A published "
                    + "compound is registered under its path in the library folder; a built-in is under its "
                    + "category, such as 'Colour/Levels'."
                ));

                return cursor;
            }

            var input = OnlyImage(type, PortDirection.Input);
            var output = OnlyImage(type, PortDirection.Output);

            if (input is null || output is null) {
                problems.Add(LayerStackProblem.Refusal(
                    layerId,
                    $"'{path}' is not a single-input graph: doc 48 § 4.10 says a mask effect has one image in "
                    + "and one image out, and this type has "
                    + $"{Images(type, PortDirection.Input).ToString(CultureInfo.InvariantCulture)} in and "
                    + $"{Images(type, PortDirection.Output).ToString(CultureInfo.InvariantCulture)} out. "
                    + "A two-input node is a composite rather than an effect, and which of its images the mask "
                    + "would be is not something this file can decide."
                ));

                return cursor;
            }

            var node = Add(path);

            graph.Connect(cursor.Port, new(node.Id, input.Name));

            foreach (var (port, value) in effect.Values) {
                // ⚠ Derived from the type rather than from a list per effect, and what it is
                // protecting is the image wire. A value named 'Input' written to `Colour/Levels`
                // would replace the mask this effect is adjusting with a constant, and the picture
                // would be the effect over nothing at all.
                if (string.Equals(port, input.Name, StringComparison.Ordinal)
                    || type.Port(port, PortDirection.Input) is not { } declared
                    || declared.Kind == PortKind.Image) {
                    problems.Add(LayerStackProblem.Warning(
                        layerId,
                        $"'{port}' is not a number '{path}' takes, so the value is dropped rather than written "
                        + "to a port that might be the image the effect reads."
                    ));

                    continue;
                }

                node.SetValue(port, value);
            }

            foreach (var (setting, value) in effect.Texts) {
                if (type.Setting(setting) is null) {
                    problems.Add(LayerStackProblem.Warning(
                        layerId,
                        $"'{setting}' is not a setting '{path}' declares, so it is dropped."
                    ));

                    continue;
                }

                node.SetText(setting, value);
            }

            // ⚠ Not opaque: an effect is *any* single-image node, so what it leaves in alpha is the
            // node's business — `Colour/Invert` with its alpha flag set turns a fully covered mask
            // into a transparent one. Effects run after the entries are composited, so the only
            // consumer downstream is `Mask`, which forces the result opaque itself; the flag is here
            // so that a future caller that composites an effect's output cannot forget. #874.
            return new(new PortRef(node.Id, output.Name), false);
        }

        /// <summary>
        ///     The node one mask entry reads, or <see langword="null" /> when it was refused. An
        ///     anchor comes back as the node that folded it, with its edge deferred by
        ///     <see cref="Into" />.
        /// </summary>
        MaskSlot? MaskSource(MaskLayerAsset entry, ChannelAsset channel, string layerId) {
            switch (entry.Source) {
                case LayerMaskSource.None:
                    return null;

                case LayerMaskSource.Constant: {
                    var node = Add("Source/Uniform");
                    var value = entry.Value;

                    node.SetValue("Colour", value, value, value, 1f);

                    return new(new PortRef(node.Id, "Out"), true);
                }

                case LayerMaskSource.Texture: {
                    if (entry.Asset.Trim().Length == 0) {
                        problems.Add(LayerStackProblem.Refusal(
                            layerId,
                            "This layer's mask is a texture and names no image. A bitmap with no reference is "
                            + "refused at compile time rather than filled with black."
                        ));

                        return null;
                    }

                    var node = Add("Source/Bitmap");

                    node.SetText("Source", entry.Asset.Trim());
                    node.SetText("Space", "Linear");
                    node.SetText("Filter", "Bilinear");

                    // ⚠ Not opaque: a PNG carries whatever alpha it was authored with, and a mask's
                    // alpha is not part of what it means — a bitmap mask is read for its red. #874.
                    return new(new PortRef(node.Id, "Out"), false);
                }

                case LayerMaskSource.Anchor:
                    if (entry.Anchor.Length == 0) {
                        problems.Add(LayerStackProblem.Refusal(
                            layerId,
                            "This layer's mask is an anchor and names no layer. An anchor is a reference to another "
                            + "layer's evaluated result, by its id."
                        ));

                        return null;
                    }

                    return new(Anchored(entry.Anchor, channel, layerId), true);

                case LayerMaskSource.Bake: {
                    var node = Add("Source/Mesh Map");

                    // ⚠ Written through even when it is empty or misspelt. `Source/Mesh Map` refuses
                    // a name nothing bakes, naming the setting — so a check here would be a second
                    // opinion about `TextureMeshMaps.Known` that could disagree with it.
                    node.SetText("Map", entry.Map.Trim());

                    // ⚠ Not opaque, and this one is worth saying out loud: `Source/Mesh Map` compiles
                    // to a `Bitmap` over an external image, so a bake's alpha is whatever the baker
                    // wrote into that PNG rather than 1. The four grey maps are forced opaque by the
                    // compiler's own promotion shuffle — but only when they meet a wider image, which
                    // is not something this file can promise. #874.
                    return new(new PortRef(node.Id, "Out"), false);
                }

                case LayerMaskSource.Generator: {
                    var path = entry.Generator.Trim();

                    if (path.Length == 0) {
                        problems.Add(LayerStackProblem.Refusal(
                            layerId,
                            "This layer's mask is a generator and names no compound. A generator is a published "
                            + ".vxtexgraph reading the mesh maps by usage, named by its path — 'Generators/Dirt'."
                        ));

                        return null;
                    }

                    if (!Library.TryGet(path, out var type)) {
                        problems.Add(LayerStackProblem.Refusal(
                            layerId,
                            $"This layer's mask names the generator '{path}', which is not a node type this "
                            + "project has. The shipped compounds are published from this assembly and a "
                            + "project's own from its compound folder; a path in neither binds nothing."
                        ));

                        return null;
                    }

                    if (OnlyImage(type, PortDirection.Output) is not { } output) {
                        problems.Add(LayerStackProblem.Refusal(
                            layerId,
                            $"The generator '{path}' does not have exactly one image output, so which of its "
                            + "results is the mask is not something this file can decide."
                        ));

                        return null;
                    }

                    var node = Add(path);

                    // ⚠ Not opaque: a generator is a published compound, and nothing constrains what
                    // it writes into alpha. #874.
                    return new(new PortRef(node.Id, output.Name), false);
                }

                case LayerMaskSource.Paint: {
                    // ⚠ The same node the texture mask emits, and the same reading of it: a mask is
                    // read for its *red*, which the `Colour/Channel Shuffle` above this does. So a
                    // mask canvas is painted in white and its coverage is the value of the channel,
                    // never an alpha — a painted mask whose alpha carried the coverage would mask
                    // nothing, for the exact reason `Into` gives for replacing a bitmap's alpha.
                    var painted = entry.Paint.Trim();

                    if (painted.Length == 0) {
                        problems.Add(LayerStackProblem.Warning(
                            layerId,
                            "This layer's mask is painted and names no .vxpaint, so it has nothing to read. A "
                            + "canvas is written the first time the mask is painted on; until then the entry "
                            + "contributes nothing."
                        ));

                        return null;
                    }

                    var node = Add("Source/Bitmap");

                    node.SetText("Source", PaintReference.Reference(painted, PaintReference.Mask));
                    node.SetText("Space", "Linear");
                    node.SetText("Filter", "Bilinear");

                    return new(new PortRef(node.Id, "Out"), "");
                }

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(entry),
                        entry.Source,
                        "Not a mask this build knows."
                    );
            }
        }

        /// <summary>The one image port of a type in one direction, or null when it has none or many.</summary>
        static PortDefinition? OnlyImage(NodeTypeDefinition type, PortDirection direction) {
            PortDefinition? found = null;

            foreach (var port in type.Ports) {
                if (port.Direction != direction || port.Kind != PortKind.Image) {
                    continue;
                }

                if (found is not null) {
                    return null;
                }

                found = port;
            }

            return found;
        }

        /// <summary>How many image ports a type has in one direction, for the message that says so.</summary>
        static int Images(NodeTypeDefinition type, PortDirection direction) {
            var count = 0;

            foreach (var port in type.Ports) {
                if (port.Direction == direction && port.Kind == PortKind.Image) {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Wires every anchor, and lets the graph model refuse the ones that loop.</summary>
        /// <remarks>
        ///     ⚠ <b>One anchor is more than one edge, so a refusal is reported once</b> —
        ///     <see cref="Anchored" /> wires the same result into two shuffles to fold its coverage
        ///     into its value (<a href="https://github.com/Rikarin/Vixen/issues/874">#874</a>). Without
        ///     the set below, one anchor onto a layer above its own would put the same sentence in
        ///     front of an artist twice, per channel — which is #842's shape and worth not repeating.
        /// </remarks>
        void Anchors() {
            HashSet<(string Channel, string Anchor, string Layer)> reported = [];

            foreach (var (channel, anchor, layer, to) in anchors) {
                if (anchor.Length == 0) {
                    // Already reported by `MaskSource`; the edge has nowhere to come from.
                    continue;
                }

                if (!results.TryGetValue((channel, anchor), out var from)) {
                    if (reported.Add((channel, anchor, layer))) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer,
                            $"The anchor names layer '{anchor}', which writes nothing to channel '{channel}' — it "
                            + "is disabled, restricted to other channels, or not in this set. An anchor reads a "
                            + "layer's result, so there has to be one."
                        ));
                    }

                    continue;
                }

                if (graph.TryConnect(from, to, out _, out var error)) {
                    continue;
                }

                if (!reported.Add((channel, anchor, layer))) {
                    continue;
                }

                problems.Add(LayerStackProblem.Refusal(
                    layer,
                    $"The anchor onto layer '{anchor}' is refused: {GraphInvariants.Describe(error, from, to)} "
                    + "⚠ An anchor onto a layer at or above its own is a loop, and the graph model is what says "
                    + "so — the stack does not check for one itself."
                ));
            }
        }

        /// <summary>The layer's opacity, with a constant fill's own alpha folded in.</summary>
        /// <remarks>
        ///     ⚠ <b>Only an <em>authored</em> colour's alpha is folded.</b> A constant fill with no
        ///     entry for this channel no longer compiles at all (#807 · 2), so reading the channel's
        ///     base default here would be folding the alpha of a colour that never reaches the
        ///     graph — a number taken from a layer that is not there.
        /// </remarks>
        static float Opacity(LayerAsset layer, ChannelAsset channel) {
            var opacity = layer.Opacity;

            if (layer.Kind != LayerKind.Fill || layer.Fill != LayerFillSource.Constant) {
                return opacity;
            }

            if (!layer.Values.TryGetValue(channel.Usage, out var colour)) {
                return opacity;
            }

            return colour.Length == 4 ? opacity * colour[3] : opacity;
        }

        /// <summary>Four numbers, or the opaque black a malformed entry becomes.</summary>
        float[] Colour(float[] value, string usage, string layer) {
            if (value.Length == 4) {
                return value;
            }

            problems.Add(LayerStackProblem.Refusal(
                layer,
                $"The colour for channel '{usage}' has {value.Length.ToString(CultureInfo.InvariantCulture)} "
                + "numbers and a colour is four — red, green, blue, alpha, linear."
            ));

            return [0f, 0f, 0f, 1f];
        }

        /// <summary>A node, laid out so that the exploded graph reads left to right.</summary>
        GraphNode Add(string type) {
            var node = graph.Add(type, new Vector2(column * 220f, row * 260f));

            column++;

            return node;
        }
    }
}
