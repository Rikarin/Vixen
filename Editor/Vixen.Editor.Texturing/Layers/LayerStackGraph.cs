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
    /// <summary>Which layer emitted each node, for the nodes a layer emitted.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What a <c>NodeDiagnostic</c> has no room for, and without which a panel can
    ///         neither name a layer nor dedupe by one</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/880">#880</a>. A diagnostic names a
    ///         node in the exploded graph, and for a stack nobody has exploded that is a node nobody
    ///         can see; the thing an artist can act on is the layer, and every node this builder
    ///         emits inside a layer's walk belongs to exactly one.
    ///     </para>
    ///     <para>
    ///         <b>A node is missing from this rather than mapped to an empty string</b>: the base
    ///         constant and the <c>Output</c> of each channel are the set's rather than any layer's,
    ///         and "no layer" is a different answer from "a layer whose id is empty". A layer with no
    ///         id is not recorded either, for the same reason — an anchor names a layer by id, so a
    ///         layer without one is not a thing a sentence can name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An <em>inlined</em> node's diagnostic is already re-addressed before it gets
    ///         here.</b> <c>NodeGraphCompiler.Report</c> rewrites a complaint about a node inside a
    ///         compound onto the sub-graph node it came out of — which is a node this builder added
    ///         — so a generator's own diagnostics land on a key this map has.
    ///     </para>
    /// </remarks>
    public ImmutableDictionary<NodeId, string> Layers { get; init; } =
        ImmutableDictionary<NodeId, string>.Empty;

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

        /// <summary>Which layer emitted each node — <see cref="LayerStackBuild.Layers" />.</summary>
        readonly Dictionary<NodeId, string> owners = [];

        /// <summary>How many nodes each (channel, layer) has emitted — see <see cref="Named" />.</summary>
        readonly Dictionary<(string Usage, string Layer), int> ordinals = [];

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

        /// <summary>The layer whose walk is running, or empty outside every layer.</summary>
        /// <remarks>
        ///     ⚠ <b>A scope rather than an argument threaded through nine methods, and the difference
        ///     is what a new emitter does.</b> Every node this builder makes goes through
        ///     <see cref="Add" />; a parameter would have to be passed correctly by each of
        ///     <c>Fill</c>, <c>Paint</c>, <c>Adjustment</c>, <c>Mask</c>, <c>MaskImage</c>,
        ///     <c>MaskSource</c>, <c>Opaque</c>, <c>Anchored</c> and <c>Effect</c>, and a tenth added
        ///     later would silently emit unowned nodes. Set once, by <see cref="Layer" />, it is
        ///     right for everything reached from there including a method nobody has written yet.
        /// </remarks>
        string owner = "";

        /// <summary>The channel whose chain is being built, which is the other half of a node's name.</summary>
        /// <remarks>
        ///     ⚠ <b>In the identity because <see cref="Content" /> is called once per channel</b>, so
        ///     one layer emits one set of nodes per map the set writes. Without it every channel's
        ///     copy of a layer would want the same id and <see cref="Named" /> would spend the whole
        ///     stack in its collision probe.
        /// </remarks>
        string usage = "";

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

            return new(graph, [.. problems], [.. notes]) { Layers = owners.ToImmutableDictionary() };
        }

        /// <summary>Refuses two layers sharing an identity, because an anchor names one of them.</summary>
        /// <remarks>
        ///     ⚠ <b>The empty id counts, and it used to be exempt</b>
        ///     (<a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>).
        ///     <see cref="LayerAsset.Id" /> defaults to empty, so a hand-written file that names no
        ///     ids gave every one of its layers the same one — and the exemption meant the check
        ///     that was supposed to make that impossible was the one thing that let it through.
        ///     It is not only an anchor that reads the id: <c>LayerPath</c> addresses every editing
        ///     command by it, so two layers sharing one means every row of the second drives the
        ///     first, and an artist reordering row four watches row two move.
        /// </remarks>
        void Duplicates() {
            // ⚠ The rule is `LayerStackEdit.Ambiguous` and not a walk of its own, because the panel
            // asks the same question about the same set — #893. A refusal here that the rows did not
            // agree with is a stack the compiler will not build whose layers the panel still offers
            // to reorder, which is the defect rather than the message about it.
            var ambiguous = LayerStackEdit.Ambiguous(set);
            HashSet<string> reported = new(StringComparer.Ordinal);

            void Walk(List<LayerAsset> layers) {
                foreach (var layer in layers) {
                    if (ambiguous.Contains(layer.Id) && reported.Add(layer.Id)) {
                        problems.Add(LayerStackProblem.Refusal(
                            layer.Id,
                            layer.Id.Length == 0
                                ? "Two layers have no id. A layer is addressed by id — by an anchor reading its "
                                + "result, and by every editing command in the panel — so two that share one are "
                                + "one layer as far as both are concerned: the second's row moves the first. Give "
                                + "each layer in the file an 'id'."
                                : $"Two layers share the id '{layer.Id}'. An anchor names a layer by id, so a "
                                + "duplicate makes the mask read whichever of the two the walk reached first — a "
                                + "picture that changes when the artist reorders layers that are not the anchored "
                                + "one."
                        ));
                    }

                    Walk(layer.Children);
                }
            }

            Walk(set.Layers);
        }

        /// <summary>One channel's chain: a base colour, every layer over it, and an output.</summary>
        void Channel(ChannelAsset channel) {
            usage = channel.Usage;

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

        /// <summary>One layer composited over the cursor, with everything it emits attributed to it.</summary>
        /// <remarks>
        ///     ⚠ <b>Saved and restored rather than assigned, because a group's children are layers
        ///     too.</b> <see cref="Group" /> re-enters <see cref="Stack" />, so a child sets
        ///     <see cref="owner" /> to its own id and has to hand the group's back — the group's own
        ///     blend node is emitted <em>after</em> its children have run. Assigning without
        ///     restoring would file every group's composite under whichever child happened to be
        ///     last.
        /// </remarks>
        PortRef Layer(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) {
            var outer = owner;

            owner = layer.Id;

            try {
                return Composite(layer, channel, cursor, depth);
            } finally {
                owner = outer;
            }
        }

        /// <summary>One layer composited over the cursor, or the cursor unchanged.</summary>
        PortRef Composite(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) {
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

            var opacity = Opacity(layer, channel);
            var folded = Folds(layer.Mask, opacity);

            if (folded is { } scaled) {
                opacity = scaled;
            } else {
                foreground = Mask(layer, channel, foreground);
            }

            var blend = Add("Colour/Blend");

            blend.SetText("Mode", layer.Blend.ToString());
            blend.SetValue("Opacity", opacity);

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
                + (folded is null ? "" : ", folded into the opacity")
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
                    // in range the two are the same number. #790.
                    //
                    // ⚠ The reason written here was "a mask *replaces* the foreground's alpha, so
                    // this is what stops it throwing the constant's alpha away" — and #832, whose own
                    // commit is the one that made it false, changed `Mask` to *multiply*. Under a
                    // multiply the two arrangements are equal on both sides of the mask, so the fold
                    // is no longer load-bearing; it is kept because `amount` is the same number either
                    // way and the alternative is a diff nothing needs. See `Mask`, which is where the
                    // rule is stated.
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
        ///     </para>
        ///     <para>
        ///         ⚠ <b>None of which happens for a mask that is one constant.</b>
        ///         <a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>'s fold is above, in
        ///         <see cref="Folds" />: <c>amount</c> is already <c>opacity · mask · alpha</c>, so a
        ///         constant mask is a number the layer's own opacity absorbs. It removes <b>five</b>
        ///         ops per masked layer per channel and not the four these three nodes are — the
        ///         mask's own <c>Source/Uniform</c> is the fifth, and it is a dispatch like any other.
        ///         The fold is exact under this rule and was not under the old one, which is why it
        ///         waited for <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a>.
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
        ///         <c>MaskSlot.Opaque</c> is for: a constant and a folded anchor are opaque by
        ///         construction — ⚠ a mesh map is <em>not</em>, because a bake's alpha is whatever the
        ///         baker wrote into that PNG — and a one-entry mask reaches no blend here at all, so the
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
                    + "painted pixels are not read. Set the mask's source to Paint to read them."
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

                    // ⚠ Not opaque, for the `Bake` case's reason exactly. A `.vxpaint` channel is
                    // Rgba8 and its alpha is whatever the strokes put there — a canvas nobody has
                    // painted the corners of carries alpha 0 in them — so this is a picture whose
                    // alpha is data rather than coverage, and #874's rule says say so. The mask is
                    // read for its red in any case.
                    return new(new PortRef(node.Id, "Out"), false);
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

        /// <summary>
        ///     The opacity a whole constant mask folds into, or <see langword="null" /> when the mask
        ///     has to be compiled.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>, and the
        ///         objection that held it back is gone rather than answered.</b> The fold was refused
        ///         while a mask <em>replaced</em> the foreground's alpha: replacing does not commute
        ///         with an opacity, so the two were only arithmetically the same for a content whose
        ///         alpha was already 1.
        ///         <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a> made a mask
        ///         <em>multiply</em> into that alpha — two coverages compose by multiplication — and
        ///         multiplication commutes. <c>Blend.rvn</c> computes
        ///         <c>amount = saturate(opacity) · saturate(b.w)</c> and reads <c>b.w</c> nowhere
        ///         else, so folding is exactly a reassociation of one product.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Which is why both numbers are range-checked, and why that is not
        ///         defensiveness.</b> <c>saturate</c> is the identity only inside the unit interval,
        ///         so an opacity of 2 with a mask of ½ is <c>saturate(2) · ½ = ½</c> unfolded and
        ///         <c>saturate(1) = 1</c> folded. Both numbers come out of a YAML file a person can
        ///         hand-edit. Outside the interval the mask is compiled, which keeps the fold an
        ///         identity rather than an approximation that is usually right.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>It is <em>not</em> exact for a content whose own alpha exceeds one</b> —
        ///         which a <c>Levels</c> filter over the alpha lane can produce, since the
        ///         intermediates are <c>rgba16f</c>. There the two orders of saturation disagree, and
        ///         both answers are arbitrary: a coverage above 1 is not a quantity either form of
        ///         the arithmetic has a meaning for. Said here rather than guarded, because the guard
        ///         would have to be a claim about what a node produces, which this file cannot make.
        ///     </para>
        ///     <para>
        ///         <b>The other half of #789's reason for waiting was that folding makes the mask path
        ///         unreachable for the one case a device-free test can build, "which is how a mask
        ///         that never worked ships green".</b> That is no longer true: an anchor mask, a bake
        ///         mask and any mask with two entries all reach the full path with no imported image,
        ///         and <c>LayerStackCompileTests</c> now builds its shuffle assertions on a bake.
        ///     </para>
        /// </remarks>
        static float? Folds(MaskAsset mask, float opacity) {
            if (mask.Source != LayerMaskSource.Constant || !Unit(mask.Value) || !Unit(opacity)) {
                return null;
            }

            // A disabled entry or effect is skipped by `MaskImage` and reports nothing, so a mask
            // whose extras are all switched off really is the bare constant this folds.
            foreach (var entry in mask.Layers) {
                if (entry.Enabled) {
                    return null;
                }
            }

            foreach (var effect in mask.Effects) {
                if (effect.Enabled) {
                    return null;
                }
            }

            return opacity * mask.Value;
        }

        /// <summary>Whether <c>saturate</c> would leave a number alone.</summary>
        static bool Unit(float value) => value is >= 0f and <= 1f;

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
        /// <remarks>
        ///     The one funnel every node in a built stack comes through, which is why
        ///     <see cref="LayerStackBuild.Layers" /> is filled here rather than at each emitter — and
        ///     why <see cref="Named" /> can give every node an identity that does not depend on the
        ///     order the walk reached it in.
        /// </remarks>
        GraphNode Add(string type) {
            var node = graph.Add(Named(), type, new Vector2(column * 220f, row * 260f));

            column++;

            if (owner.Length > 0) {
                owners[node.Id] = owner;
            }

            return node;
        }

        /// <summary>An identity for the next node, from what it is rather than from when it was made.</summary>
        /// <returns>The id, which is free in this graph.</returns>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/875">#875</a>, and the reason
        ///         it had to land <em>here</em> rather than only in the plan.</b>
        ///         <c>TexturePlan.SeedFor</c> now mixes <c>TextureOp.Identity</c>, which
        ///         <c>TextureGraphCompiler</c> derives from the <c>NodeId</c> that emitted the op —
        ///         and for a hand-authored <c>.vxtexgraph</c> that is the end of it, because a node id
        ///         is written in the file and <c>NodeGraphModel</c> never reuses one. A stack has no
        ///         such file: this class builds a fresh model on every compile, so
        ///         <c>NodeGraphModel.Add(string, …)</c>'s counter would hand every node after an
        ///         inserted layer a new number and every noise under it a new picture. Substituting
        ///         one counter for another is what #875 refutes, not what it asks for.
        ///     </para>
        ///     <para>
        ///         <b>So the identity is the answer to "which node is this?" asked of the document.</b>
        ///         The channel, the layer, and how many nodes that layer has already emitted for that
        ///         channel — a triple that does not move when a layer is inserted beneath, above or
        ///         beside it, and which changes only when the layer itself is rewritten. ⚠ It has to
        ///         be the <em>id</em> and not a side table, because the identity must survive
        ///         <c>LayerStackExplode</c>'s YAML round trip: exit criterion 6 compiles a graph that
        ///         came off a file, and what is in the file is the node ids.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>A layer with no id shares one scope with every other layer that has none</b>,
        ///         so those nodes are numbered by walk order within the channel and move exactly as
        ///         they did before. That is honest rather than ideal: an id is what an anchor names,
        ///         so a layer without one is already not a thing this file can refer to.
        ///     </para>
        ///     <para>
        ///         <b>A collision walks forward, and that is the one place order leaks back in.</b>
        ///         Two scopes hashing to one number would otherwise make
        ///         <c>NodeGraphModel.Add(NodeId, …)</c> throw out of a panel build. The probe is
        ///         deterministic for a given document and the ids it lands on are stable for it; what
        ///         it cannot promise is that inserting a layer leaves a <em>collided</em> node's id
        ///         alone. Over a 2³⁰ space and a stack's worth of nodes that is not a case anybody
        ///         will meet, and the alternative — a wider id — is a change to <c>NodeId</c>.
        ///     </para>
        /// </remarks>
        NodeId Named() {
            var scope = (usage, owner);

            ordinals.TryGetValue(scope, out var ordinal);
            ordinals[scope] = ordinal + 1;

            var hash = 2166136261u;

            foreach (var character in usage) {
                hash = unchecked((hash ^ character) * 16777619u);
            }

            hash = unchecked((hash ^ '\n') * 16777619u);

            foreach (var character in owner) {
                hash = unchecked((hash ^ character) * 16777619u);
            }

            hash = unchecked(((hash ^ '\n') * 16777619u) + (uint)ordinal);

            // Kept well inside `int`, because `NodeGraphModel.Add(NodeId, …)` raises its own counter
            // to the largest id it has seen and a sub-graph inlining then allocates above that.
            var id = new NodeId((int)(hash % 0x3FFFFFFFu) + 1);

            while (graph.TryGet(id, out _)) {
                id = new NodeId(id.Value % 0x3FFFFFFF + 1);
            }

            return id;
        }
    }
}
