// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Vixen.Core;
using Vixen.Editor.NodeGraph;

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
    /// <returns>The graph and what building it had to say.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static LayerStackBuild Build(LayerStackAsset stack, TextureSetAsset set) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);

        Builder builder = new(stack, set);

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
    sealed class Builder(LayerStackAsset stack, TextureSetAsset set) {
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
                    $"Projection '{layer.Projection}' needs the position and world-normal mesh maps and a node "
                    + "that reads them, which is M8 (#573). Only Uv compiles in this build."
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
                LayerKind.Paint => Paint(layer),
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

        /// <summary>M9's layer, refused with the issue that will build it.</summary>
        /// <remarks>
        ///     ⚠ <b>A tripwire, and the message names the issue that removes it.</b> Doc 48 § M9
        ///     (<a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>) is the brush; what M7
        ///     owes is a document shaped for it, which is <see cref="LayerAsset.Paint" /> holding a
        ///     path and never pixels. Compiling a paint layer needs the <c>.vxpaint</c> uploaded as
        ///     an external image, and there is nothing yet that writes one.
        /// </remarks>
        PortRef? Paint(LayerAsset layer) {
            problems.Add(LayerStackProblem.Refusal(
                layer.Id,
                "A Paint layer's pixels come from a .vxpaint beside the stack, and nothing writes one yet: the "
                + "brush is doc 48 § M9 (#574). The layer is held in the document, keeps its blend mode, its "
                + "opacity and its channel enables, and does not compile."
            ));

            return null;
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

                case LayerFillSource.Graph:
                    problems.Add(LayerStackProblem.Refusal(
                        layer.Id,
                        $"A graph fill inlines a published .vxtexgraph ('{layer.Graph}') as a sub-graph, which "
                        + "needs the ISubGraphSource a project supplies. That is M8 (#573)."
                    ));

                    return null;

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

        /// <summary>The mask multiplied into the foreground's alpha, or the foreground unchanged.</summary>
        PortRef Mask(LayerAsset layer, ChannelAsset channel, PortRef foreground) {
            var source = MaskSource(layer);

            if (source is null && layer.Mask.Source != LayerMaskSource.Anchor) {
                return foreground;
            }

            var shuffle = Add("Colour/Channel Shuffle");

            // Written out rather than left to the node's defaults, because an exploded graph is
            // something an artist reads: the three that pass through and the one that does not are
            // the whole of what a mask is.
            shuffle.SetText("Red From", "FirstRed");
            shuffle.SetText("Green From", "FirstGreen");
            shuffle.SetText("Blue From", "FirstBlue");
            shuffle.SetText("Alpha From", "SecondRed");

            graph.Connect(foreground, new(shuffle.Id, "First"));

            PortRef second = new(shuffle.Id, "Second");

            if (source is { } wire) {
                graph.Connect(wire, second);
            } else {
                anchors.Add((channel.Usage, layer.Mask.Anchor, layer.Id, second));
            }

            return new(shuffle.Id, "Out");
        }

        /// <summary>
        ///     The node a mask reads, <see langword="null" /> for no mask and for an anchor — which is
        ///     wired at the end.
        /// </summary>
        PortRef? MaskSource(LayerAsset layer) {
            var mask = layer.Mask;

            switch (mask.Source) {
                case LayerMaskSource.None:
                    if (mask.Paint.Length > 0) {
                        problems.Add(LayerStackProblem.Warning(
                            layer.Id,
                            $"This layer's mask names a paint file ('{mask.Paint}') and its source is None, so the "
                            + "painted pixels are not read. A painted mask is M9 (#574)."
                        ));
                    }

                    return null;

                case LayerMaskSource.Constant: {
                    var node = Add("Source/Uniform");
                    var value = mask.Value;

                    node.SetValue("Colour", value, value, value, 1f);

                    return new(node.Id, "Out");
                }

                case LayerMaskSource.Texture: {
                    if (mask.Asset.Trim().Length == 0) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            "This layer's mask is a texture and names no image. A bitmap with no reference is "
                            + "refused at compile time rather than filled with black."
                        ));

                        return null;
                    }

                    var node = Add("Source/Bitmap");

                    node.SetText("Source", mask.Asset.Trim());
                    node.SetText("Space", "Linear");
                    node.SetText("Filter", "Bilinear");

                    return new(node.Id, "Out");
                }

                case LayerMaskSource.Anchor:
                    if (mask.Anchor.Length == 0) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            "This layer's mask is an anchor and names no layer. An anchor is a reference to another "
                            + "layer's evaluated result, by its id."
                        ));
                    }

                    return null;

                case LayerMaskSource.Generator:
                    problems.Add(LayerStackProblem.Refusal(
                        layer.Id,
                        "A generator mask is a shipped .vxtexgraph reading the mesh maps by usage, which is M8 "
                        + "(#573)."
                    ));

                    return null;

                default:
                    throw new ArgumentOutOfRangeException(nameof(layer), mask.Source, "Not a mask this build knows.");
            }
        }

        /// <summary>Wires every anchor, and lets the graph model refuse the ones that loop.</summary>
        void Anchors() {
            foreach (var (channel, anchor, layer, to) in anchors) {
                if (anchor.Length == 0) {
                    // Already reported by `MaskSource`; the edge has nowhere to come from.
                    continue;
                }

                if (!results.TryGetValue((channel, anchor), out var from)) {
                    problems.Add(LayerStackProblem.Refusal(
                        layer,
                        $"The anchor names layer '{anchor}', which writes nothing to channel '{channel}' — it is "
                        + "disabled, restricted to other channels, or not in this set. An anchor reads a layer's "
                        + "result, so there has to be one."
                    ));

                    continue;
                }

                if (graph.TryConnect(from, to, out _, out var error)) {
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
