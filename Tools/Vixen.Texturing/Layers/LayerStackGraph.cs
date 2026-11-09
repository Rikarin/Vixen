// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Vixen.Core;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Painting;

namespace Vixen.Texturing.Layers;

/// <summary>One thing wrong with a stack, about a layer an artist can select.</summary>
/// <param name="Severity">Whether it stops the compile.</param>
/// <param name="Layer">The <see cref="LayerAsset.Id" /> it is about, or empty for the set itself.</param>
/// <param name="Message">What is wrong, as a person would say it.</param>
public readonly record struct LayerStackProblem(NodeSeverity Severity, string Layer, string Message) {
    /// <summary>A problem that stops the compile.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The problem.</returns>
    public static LayerStackProblem Refusal(string layer, string message) => new(NodeSeverity.Error, layer, message);

    /// <summary>A problem the compile reports and then compiles anyway.</summary>
    /// <param name="layer">Which layer.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The problem.</returns>
    public static LayerStackProblem Warning(string layer, string message) => new(NodeSeverity.Warning, layer, message);

    /// <inheritdoc />
    public override string ToString() => Layer.Length == 0 ? Message : $"layer '{Layer}': {Message}";
}

/// <summary>One image input of a filter, as a host offers a slot for it.</summary>
/// <param name="Port">Its name, which <see cref="LayerAsset.Inputs" /> is keyed by.</param>
/// <param name="Summary">What the node says it reads there.</param>
/// <param name="Subject">
///     Whether it is the picture the filter changes, which reads the stack beneath in the channel being built until the
///     layer says otherwise, and is always a mask step's mask.
/// </param>
/// <param name="Optional">Whether the node does without it, so a slot nobody fills is left empty rather than refused.</param>
/// <param name="Channel">
///     The channel it is named after, which it reads beneath until the layer says otherwise, or empty.
/// </param>
public sealed record LayerFilterInput(string Port, string Summary, bool Subject, bool Optional, string Channel);

/// <summary>What one node in an exploded graph came out of, so a comment can say so.</summary>
/// <param name="Node">The node.</param>
/// <param name="Text">The sentence to put beside it.</param>
/// <remarks>
///     ⚠ <b>Collected during the build and used only by <see cref="LayerStackExplode" />.</b>
///     Compiling never reads them, which is what keeps doc 48 exit criterion 6's differential
///     meaningful: if a note could reach the compiler, the exploded graph and the compiled one would
///     agree because they were the same object rather than because they mean the same thing.
/// </remarks>
public readonly record struct LayerNote(NodeId Node, string Text);

/// <summary>Where one mask entry's pixels come from, and whether a compositor may read it.</summary>
/// <param name="Port">The port carrying the image.</param>
/// <param name="Opaque">Whether its alpha is 1 everywhere, so a compositor may read it as a number.</param>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             <paramref name="Opaque" /> is what stops a mask entry disappearing into the mask
///             stack's own blends
///         </b>
///         — <a href="https://github.com/Rikarin/Vixen/issues/874">#874</a>.
///         A mask is a <em>number</em>, and the stack that composites mask entries composites them
///         with <c>Colour/Blend</c>, which is a compositor: it reads the alpha it is handed as
///         coverage, so an entry whose alpha is 0 blends by nothing however bright its red is. A
///         constant and a folded anchor are opaque by construction; a bitmap's alpha is a PNG's, a
///         generator's is whatever the compound wrote, and a mesh map's is whatever the <em>baker</em>
///         wrote — so those three are forced before they meet a blend, and this says which is which.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An anchor used to arrive here as a slot with no port at all, and no longer
///             does.
///         </b>
///         The deferral that keeps <c>NodeGraphModel.TryConnect</c> the only thing refusing
///         a loop — doc 48 § D10 — now lives in <c>LayerStackGraph.Anchored</c>, which builds the
///         fold an anchor's coverage needs and defers only the edges into it. Every slot therefore
///         carries a real port, and wiring one is <c>graph.Connect</c> rather than a branch that
///         might connect nothing.
///     </para>
/// </remarks>
public readonly record struct MaskSlot(PortRef Port, bool Opaque);

/// <summary>The graph a texture set's stack is, and what building it had to say.</summary>
/// <param name="Graph">The nodes and wires.</param>
/// <param name="Problems">What the build reported.</param>
/// <param name="Notes">What each layer's composite node came out of.</param>
public sealed record LayerStackBuild(
    NodeGraphModel Graph,
    ImmutableArray<LayerStackProblem> Problems,
    ImmutableArray<LayerNote> Notes
) {
    /// <summary>Which layer emitted each node, for the nodes a layer emitted.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             What a <c>NodeDiagnostic</c> has no room for, and without which a panel can
    ///             neither name a layer nor dedupe by one
    ///         </b>
    ///         —
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
    ///         ⚠
    ///         <b>
    ///             An <em>inlined</em> node's diagnostic is already re-addressed before it gets
    ///             here.
    ///         </b>
    ///         <c>NodeGraphCompiler.Report</c> rewrites a complaint about a node inside a
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
///         <b>
///             Which is also what makes <c>LayerStackExplode</c> honest rather than a second
///             renderer.
///         </b>
///         Explode writes <em>this</em> graph out, with comments and a layout;
///         compiling compiles it. Doc 48 exit criterion 6 asks for a stack and its explosion to bake
///         byte-identical outputs, and <c>LayerStackExplodeTests</c> compares the two compilations
///         op by op with the explosion taken back off a YAML round trip — which is where the
///         difference between the two paths actually lives.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An anchor is compiled as an edge and the cycle check is
///             <c>NodeGraphModel.TryConnect</c>'s.
///         </b>
///         Doc 48 § D10 says the stack compiles
///         <em>through</em> the graph model rather than growing a second cycle check, so every
///         anchor edge is deferred to the end of the build and then offered to the model: an anchor
///         onto a layer at or above its own closes a loop in the graph and comes back as
///         <see cref="GraphConnectionError.Cycle" />, which is reported verbatim. Nothing here walks
///         the stack looking for loops.
///     </para>
/// </remarks>
public static class LayerStackGraph {
    /// <summary>How deeply groups may nest before the build gives up.</summary>
    /// <remarks>
    ///     A hang check and not a design limit: a <c>.vxlayers</c> is a file, a file can be
    ///     hand-edited, and a group that contains itself would otherwise recurse until the stack
    ///     overflows. Sixteen is far past what an artist builds.
    /// </remarks>
    public const int MaxGroupDepth = 16;

    /// <summary>The channel a set's height is painted in, which also writes its normal.</summary>
    public const string HeightUsage = "height";

    /// <summary>The usage of the normal output a height writes, and of the channel it is combined over.</summary>
    public const string NormalUsage = "normal";

    /// <summary>How far a whole height stands, as a share of the atlas's side: a height of 1 is a sixty-fourth.</summary>
    /// <remarks>
    ///     ⚠ <b>The slope <c>Height to Normal</c> measures is per unit of UV, so this is what turns a painted 0 to 1
    ///     into a surface.</b> At 1 a soft brush's rise over a few dozen texels is a near-vertical wall; at a
    ///     sixty-fourth, half a height over a sixty-fourth of the atlas leans the normal by about 27°, which reads as
    ///     a bump and not as a cliff. A per-set height scale is the texture set settings' work.
    /// </remarks>
    public const float ReliefIntensity = 1f / 64f;

    /// <summary>How far <see cref="LayerHeightNormal.Smooth" /> blurs the height, in texels at the base resolution.</summary>
    public const float SmoothRadius = 1f;

    /// <summary>The stack of one texture set, as a graph.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">Which of its sets.</param>
    /// <param name="registry">
    ///     The node types a generator or a mask effect is looked up in. ⚠ <see langword="null" />
    ///     means the built-in library <b>plus the compounds this build ships, and no project's own</b>
    ///     — which is the default that made a project compound <c>TG0001</c> in the panel, so a
    ///     caller that has a project should pass one built with its <c>Assets</c> folder.
    /// </param>
    /// <param name="draft">
    ///     Whether a slot nobody has filled yet is a warning and leaves that part of the layer out, rather than refusing
    ///     the stack. For a host someone is editing in, and not for a bake.
    /// </param>
    /// <returns>The graph and what building it had to say.</returns>
    /// <exception cref="ArgumentNullException">The stack or the set is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The registry is an input to <em>building</em> and not only to compiling, which it
    ///             was not before M8.
    ///         </b>
    ///         A generator and a mask effect are named by node-type path, and
    ///         which port of one carries the image is a fact only the registry has — so a builder
    ///         without one would have to hard-code a port name per effect, which is exactly the
    ///         second list <c>LayerFilterKind</c>'s hand-written ports already are.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The default is the quiet one <a href="https://github.com/Rikarin/Vixen/issues/858">#858</a>
    ///             was about, one call deeper, and it is left in place deliberately
    ///         </b>
    ///         —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/946">#946</a>. This is a static method
    ///         with no document, so there is no folder it could find on its own; a caller that has a
    ///         project passes the registry <c>LayerStackCompiler.Library(out _, assets)</c> built from
    ///         it, which is what <c>LayerStackCompiler.Compile</c> does since
    ///         <a href="https://github.com/Rikarin/Vixen/issues/924">#924</a>. Growing an
    ///         <c>assets</c> parameter here instead would be a second way to say the same thing and a
    ///         second default for a caller to leave alone.
    ///         <b>
    ///             So: null is for a caller with no
    ///             project, and it is not the library.
    ///         </b>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the default costs is a <em>refusal</em>, not silence and not <c>TG0001</c>.</b>
    ///         A fill or a mask effect naming a compound out of the project's <c>Assets/Compounds</c>
    ///         is caught here, before a node is emitted, and comes back as a
    ///         <c>LayerStackProblem.Refusal</c> whose sentence names the path — the issue predicted
    ///         <c>TG0001</c>, which is what the <em>compiler</em> says about a type it has and cannot
    ///         inline. <c>ProjectCompoundDeviceTests</c> holds both halves.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The one non-test caller of the default is <see cref="LayerStackExplode.Explode" />,
    ///             and nothing in the editor calls that either.
    ///         </b>
    ///         So the fallback is unreachable in a
    ///         running editor today — which is a statement with a shelf life: an Explode command,
    ///         when one is wired, has to hand this a registry built from the project or an exploded
    ///         stack loses every project compound the author used.
    ///     </para>
    /// </remarks>
    public static LayerStackBuild Build(
        LayerStackAsset stack,
        TextureSetAsset set,
        NodeTypeRegistry? registry = null,
        bool draft = false,
        ISubGraphSource? subGraphs = null
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);

        if (registry is null) {
            registry = LayerStackCompiler.Library(out var published);
            subGraphs ??= published;
        }

        Builder builder = new(stack, set, registry) { Drafting = draft, SubGraphs = subGraphs };

        return builder.Run();
    }

    /// <summary>The same stack, built to answer <em>how much</em> each layer covers rather than what colour it is.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">A set holding the one layer whose coverage is wanted — see the remarks.</param>
    /// <param name="registry">The node types, as <see cref="Build" /> takes them.</param>
    /// <returns>The graph and what building it had to say.</returns>
    /// <exception cref="ArgumentNullException">The stack or the set is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What a splat map's channels are</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>. Every layer's mask,
    ///         its opacity and its blend already decide how much of a texel it takes; a coverage build
    ///         is the same walk with the <em>colour</em> replaced by one, so what comes out of the
    ///         chain is that amount and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not a second compositor and that is the point.</b> The mask stack, the mask
    ///         effects, the anchors, the groups and the opacity folding are all the ones a picture
    ///         goes through — <c>LayerStackSplat</c> hands this one layer at a time and the arithmetic
    ///         that turns the answers into weights is in <c>MaterialBake.Splat</c>. Emitting a
    ///         coverage chain of its own here would be the second implementation of "how much does
    ///         this layer cover" that § D1 exists to prevent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A paint layer's coverage is its canvas's <em>alpha</em>, not a constant.</b>
    ///         Substituting white for a paint layer the way a fill takes it would say the layer covers
    ///         the whole atlas — which is the failure this mode exists to compute correctly, since
    ///         M9's painted masks are the weights an artist actually made. So the canvas is read and
    ///         its colour lanes forced to one with its alpha kept.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A filter layer contributes no coverage at all.</b> An adjustment reads what is
    ///         under it and changes the colour; treated as a fill it would claim the whole surface,
    ///         and a stack with a Levels layer on top would come back as one layer covering everything.
    ///     </para>
    /// </remarks>
    public static LayerStackBuild Weights(
        LayerStackAsset stack,
        TextureSetAsset set,
        NodeTypeRegistry? registry = null,
        ISubGraphSource? subGraphs = null
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);

        if (registry is null) {
            registry = LayerStackCompiler.Library(out var published);
            subGraphs ??= published;
        }

        Builder builder = new(stack, set, registry) { Weighing = true, SubGraphs = subGraphs };

        return builder.Run();
    }

    /// <summary>
    ///     The same stack, built so that every channel's output is one group's own composite — what that group is
    ///     flattened into (Patina #107).
    /// </summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">Which of its sets.</param>
    /// <param name="group">The <see cref="LayerAsset.Id" /> of the group.</param>
    /// <param name="backdrop">
    ///     Whether a pass-through group keeps what is beneath it rather than being isolated —
    ///     <see cref="LayerStackEdit.CanFlatten" /> says which a group needs.
    /// </param>
    /// <param name="registry">The node types, as <see cref="Build" /> takes them.</param>
    /// <param name="draft">As <see cref="Build" /> takes it.</param>
    /// <param name="subGraphs">As <see cref="Build" /> takes them.</param>
    /// <returns>The graph and what building it had to say.</returns>
    /// <exception cref="ArgumentNullException">The stack or the set is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Before the group's own mask, opacity and blend</b>, which stay on the flattened group and apply to its
    ///         canvas exactly as they applied to its children's composite. So a flattened group composites the same
    ///         picture, up to the eight bits a canvas holds.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole stack is built, and only the outputs change.</b> A child's mask may read an anchor point
    ///         anywhere beneath it, and that picture has to exist; the layers above the group feed nothing.
    ///     </para>
    /// </remarks>
    public static LayerStackBuild Capture(
        LayerStackAsset stack,
        TextureSetAsset set,
        string group,
        bool backdrop,
        NodeTypeRegistry? registry = null,
        bool draft = false,
        ISubGraphSource? subGraphs = null
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrEmpty(group);

        if (registry is null) {
            registry = LayerStackCompiler.Library(out var published);
            subGraphs ??= published;
        }

        Builder builder = new(stack, set, registry) {
            Drafting = draft,
            SubGraphs = subGraphs,
            Capturing = group,
            Backdrop = backdrop
        };

        return builder.Run();
    }

    /// <summary>The port a filter's numbers may name, per filter kind.</summary>
    /// <param name="filter">Which adjustment.</param>
    /// <returns>The node type, and the scalar ports it takes.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Written here rather than read off the registry, and the difference is what a wrong
    ///         name does.
    ///     </b>
    ///     A registry lookup would accept every port the node declares, including its
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
            LayerFilterKind.Grayscale => ("Colour/Grayscale", ["Weight R", "Weight G", "Weight B", "Normalise"]),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Not a filter this build knows.")
        };

    /// <summary>
    ///     Whether a node type can be a filter layer: an image in, which is its subject, and one image out — or one per
    ///     channel, each named by the channel's usage — and a type that does not say it is no adjustment.
    /// </summary>
    /// <param name="type">The node type or published compound.</param>
    /// <returns>Whether <see cref="LayerAsset.FilterNode" /> can name it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The compile's own rule, for a host that wants to offer the list.</b> <c>Published</c> refuses
    ///         whatever this says no to, so a menu built from anything else offers refusals.
    ///     </para>
    ///     <para>
    ///         <b>The first image input is the subject and every other one is a source</b> (Patina #113): a warp's
    ///         field, a slope blur's slope. Each reads what <see cref="LayerAsset.Inputs" /> names, so a second image no
    ///         longer makes a node a composite the file cannot place. What still does is the node saying so —
    ///         <see cref="NodeTypeDefinition.Adjustment" />: a blend, a selector, a scatter, and the nodes whose output
    ///         measures their input rather than being a picture of it (Patina #220).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One output per channel is a material filter</b> (Patina #117): in each channel it writes its output
    ///         named by that channel's usage, and a channel it has no output for it leaves alone. The other narrowings are
    ///         <see cref="IdentityOnAStack" />: nodes the compile takes and that cannot change any picture a stack hands
    ///         them, so offering them would offer nothing; and <see cref="ReadsNoPicture" />: nodes whose one image is a
    ///         mesh map, which a stack would hand the picture beneath instead (Patina #179).
    ///     </para>
    /// </remarks>
    public static bool IsAdjustment(NodeTypeDefinition type) {
        ArgumentNullException.ThrowIfNull(type);

        if (!type.Adjustment
            || IdentityOnAStack.Contains(type.Path)
            || ReadsNoPicture.Contains(type.Path)
            || Builder.Images(type, PortDirection.Input) == 0) {
            return false;
        }

        var outputs = Builder.Images(type, PortDirection.Output);

        return outputs == 1 || (outputs > 1 && PerChannel(type));
    }

    /// <summary>Whether a node type can be a step on a mask: a filter with one picture out, named after no channel.</summary>
    /// <param name="type">The node type or published compound.</param>
    /// <returns>Whether <see cref="MaskEffectAsset.Node" /> can name it.</returns>
    public static bool IsMaskStep(NodeTypeDefinition type) =>
        IsAdjustment(type) && Builder.Images(type, PortDirection.Output) == 1 && !PerChannel(type);

    /// <summary>A filter's image inputs, its subject first, each with what it asks of what feeds it.</summary>
    /// <param name="type">The node type or published compound.</param>
    /// <param name="registry">The node types, compounds included, which is what answers the questions.</param>
    /// <param name="subGraphs">Where compounds come from.</param>
    /// <returns>The inputs, in the type's order, the first being the subject.</returns>
    /// <exception cref="ArgumentNullException">The type or the registry is null.</exception>
    public static IReadOnlyList<LayerFilterInput> Inputs(
        NodeTypeDefinition type,
        NodeTypeRegistry registry,
        ISubGraphSource? subGraphs
    ) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);

        List<LayerFilterInput> inputs = [];

        foreach (var port in type.Ports) {
            if (port is not { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                continue;
            }

            var subject = inputs.Count == 0;

            inputs.Add(
                new(
                    port.Name,
                    port.Summary,
                    subject,
                    !subject && TextureImageInputs.Optional(registry, subGraphs, type.Path, port.Name),
                    Usages.Contains(port.Name) ? port.Name : ""
                )
            );
        }

        return inputs;
    }

    /// <summary>The usages a channel can have, which is what an input or an output named after a channel is named.</summary>
    static readonly HashSet<string> Usages = Usage();

    static HashSet<string> Usage() {
        NodeTypeRegistry registry = new();

        TextureGraph.NodeTypes.Register(registry);

        return new(registry.Get("Output/Output").Setting("Usage")!.Accepted, StringComparer.Ordinal);
    }

    /// <summary>Whether every image output of a type is named after a channel.</summary>
    static bool PerChannel(NodeTypeDefinition type) {
        foreach (var port in type.Ports) {
            if (port is { Direction: PortDirection.Output, Kind: PortKind.Image } && !Usages.Contains(port.Name)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>One-image nodes whose image is not a picture, so a stack has nothing to hand them.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused, not merely left off the list</b> (Patina #179). Each reads § D12's <c>position</c>
    ///     bake through its one image port, and a filter layer or a mask step would hand it the colour or the
    ///     mask beneath instead — a noise of a colour read as a place on the model, which is a plausible
    ///     picture of nothing. A stack reaches them through a fill's graph, where the port is wired to a mesh map.
    /// </remarks>
    static readonly HashSet<string> ReadsNoPicture = new(StringComparer.Ordinal) {
        "Source/Volume Gradient",
        "Source/Volume Mask",
        "Source/Volume Noise"
    };

    /// <summary>One-image nodes the compile would take and that cannot change a stack's picture.</summary>
    /// <remarks>
    ///     ⚠ <b>Left off the list a host offers, and still accepted by name</b> (Patina #171). Every
    ///     picture a filter layer or a mask step reads is opaque — a channel beneath is composited over its
    ///     default, and a mask is greyed — so multiplying or dividing colour by an alpha of one is the
    ///     picture it was handed. Offering them would be two menu entries that do nothing, which is the
    ///     defect a menu read off the registry is supposed to make impossible.
    /// </remarks>
    static readonly HashSet<string> IdentityOnAStack = new(StringComparer.Ordinal) {
        "Colour/Premultiply",
        "Colour/Unpremultiply"
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

        /// <summary>The stack just beneath each layer, per channel, by the layer's id.</summary>
        readonly Dictionary<(string Channel, string Layer), PortRef> below = [];

        /// <summary>
        ///     Inputs reading the stack beneath a layer in a channel whose walk had not reached it yet, wired at the end
        ///     as an anchor's edges are and for their reason: the graph model is what refuses a loop.
        /// </summary>
        readonly List<(string Channel, string Layer, PortRef To)> beneath = [];

        /// <summary>Each anchor point's picture, per channel, by its name — the stack as it stood just above it.</summary>
        readonly Dictionary<(string Channel, string Anchor), PortRef> results = [];

        /// <summary>Every anchor point the set declares, which is what tells a misspelt name from an unreached one.</summary>
        readonly HashSet<string> declared = new(StringComparer.Ordinal);

        /// <summary>What <see cref="Capturing" /> composited, per channel, before its own mask and opacity.</summary>
        readonly Dictionary<string, PortRef> captures = new(StringComparer.Ordinal);

        // ⚠ Both false, because every image in a stack is an atlas: the far edge is another island
        // whether or not a mesh map appears in the graph.
        readonly NodeGraphModel graph = new() {
            Name = set.Name.Length > 0 ? $"{stack.Name} · {set.Name}" : stack.Name,
            Settings = {
                [TextureGraphSettings.TilingX] = "false",
                [TextureGraphSettings.TilingY] = "false"
            }
        };

        float column;
        float row;

        /// <summary>Whether this walk answers coverage rather than colour — see <see cref="Weights" />.</summary>
        public bool Weighing { get; init; }

        /// <summary>Whether a slot nobody has filled yet leaves its layer unfinished rather than the stack broken.</summary>
        /// <remarks>
        ///     ⚠ <b>Refusing is right for a bake and wrong under a hand.</b> A bake that filled an empty
        ///     slot with something would be a wrong picture nobody asked for, so the default stays. An
        ///     artist choosing a generator is half way through a sentence, and a refusal takes every
        ///     other layer's picture away while they finish it. What an empty slot contributes here is
        ///     what an unpainted canvas already contributes: nothing, and a warning that says so.
        /// </remarks>
        public bool Drafting { get; init; }

        /// <summary>Where the registry's compounds come from, which is what says whether one measures its input.</summary>
        public ISubGraphSource? SubGraphs { get; init; }

        /// <summary>
        ///     The group whose own composite is the output of every channel, or empty for the stack's picture — see
        ///     <see cref="LayerStackGraph.Capture" />.
        /// </summary>
        public string Capturing { get; init; } = "";

        /// <summary>Whether a pass-through <see cref="Capturing" /> keeps what is beneath it, rather than isolating.</summary>
        public bool Backdrop { get; init; }

        /// <summary>Says a slot is empty, which refuses unless this is a draft.</summary>
        void Unfilled(string layer, string message) =>
            problems.Add(
                Drafting ? LayerStackProblem.Warning(layer, message) : LayerStackProblem.Refusal(layer, message)
            );

        /// <summary>The layer whose walk is running, or empty outside every layer.</summary>
        /// <remarks>
        ///     ⚠
        ///     <b>
        ///         A scope rather than an argument threaded through nine methods, and the difference
        ///         is what a new emitter does.
        ///     </b>
        ///     Every node this builder makes goes through
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
                problems.Add(
                    LayerStackProblem.Refusal(
                        "",
                        $"Texture set '{set.Name}' declares no channels, so there is nothing for its layers to write. "
                        + "A set's channel list is doc 48 § D11's, and a stack with none compiles to a plan with no "
                        + "outputs — which evaluates and produces nothing anybody can look at."
                    )
                );
            }

            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (var channel in set.Channels) {
                if (!seen.Add(channel.Usage)) {
                    problems.Add(
                        LayerStackProblem.Refusal(
                            "",
                            $"Texture set '{set.Name}' declares the channel '{channel.Usage}' twice. Two Output nodes "
                            + "under one usage is two maps a bake would write to one file, last one winning."
                        )
                    );
                }
            }

            Duplicates();

            Dictionary<string, PortRef> ends = new(StringComparer.Ordinal);

            for (var index = 0; index < set.Channels.Count; index++) {
                row = index;
                column = 0f;

                ends[set.Channels[index].Usage] = Channel(set.Channels[index]);
            }

            if (Relieved) {
                row = set.Channels.Count;
                column = 0f;

                Relief(ends[HeightUsage], ends.TryGetValue(NormalUsage, out var normal) ? normal : null);
            }

            Anchors();
            Beneaths();

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
                    if (layer.AnchorPoint.Length > 0 && !declared.Add(layer.AnchorPoint)) {
                        problems.Add(
                            LayerStackProblem.Refusal(
                                layer.Id,
                                $"Two layers publish the anchor point '{layer.AnchorPoint}'. A reference names an anchor "
                                + "point, so a second one of that name is a picture nobody could choose. Rename one."
                            )
                        );
                    }

                    if (ambiguous.Contains(layer.Id) && reported.Add(layer.Id)) {
                        problems.Add(
                            LayerStackProblem.Refusal(
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
                            )
                        );
                    }

                    Walk(layer.Children);
                }
            }

            Walk(set.Layers);
        }

        /// <summary>One channel's chain: a base colour, every layer over it, and an output.</summary>
        /// <returns>The chain's end, which a normal channel under a height hands to <see cref="Relief" /> instead.</returns>
        PortRef Channel(ChannelAsset channel) {
            usage = channel.Usage;

            var start = Add("Source/Uniform");

            start.SetValue("Colour", Colour(channel.Default, channel.Usage, ""));

            PortRef cursor = new(start.Id, "Out");

            cursor = Stack(set.Layers, channel, cursor, 0);

            if (Relieved && channel.Usage == NormalUsage) {
                return cursor;
            }

            // A capture's output is the group's own composite, and transparency where the group writes nothing.
            if (Capturing.Length > 0) {
                cursor = captures.TryGetValue(channel.Usage, out var captured) ? captured : Transparent();
            }

            var output = Add("Output/Output");

            output.SetText("Usage", channel.Usage);
            graph.Connect(cursor, new(output.Id, "Input"));

            notes.Add(new(output.Id, $"Channel '{channel.Usage}' of texture set '{set.Name}'."));

            return cursor;
        }

        /// <summary>Whether the set's height reaches its normal, which it does for a picture and not for coverage.</summary>
        bool Relieved =>
            !Weighing && Capturing.Length == 0 && set.Channels.Exists(static channel => channel.Usage == HeightUsage);

        /// <summary>The set's <c>normal</c> output: the height's slope, over the normal channel when there is one.</summary>
        /// <param name="height">The end of the height channel's chain.</param>
        /// <param name="normal">The end of the normal channel's chain, or null for a set with none.</param>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>Grey first, because <c>Height to Normal</c> refuses colour</b>, and a channel's chain is four
        ///         lanes however grey its paint is. A painted height is the same in each lane, so the weights do not
        ///         matter for paint and give a plain luminance for anything else.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Combined by reorientation and not replaced</b>, so a normal channel filled with a baked normal
        ///         keeps its detail and the paint's bumps lean with it. Whether a bake reaches the normal channel by
        ///         itself is a separate question this does not answer.
        ///     </para>
        /// </remarks>
        void Relief(PortRef height, PortRef? normal) {
            usage = NormalUsage;

            var grey = Add("Colour/Grayscale");

            graph.Connect(height, new(grey.Id, "Input"));

            PortRef cursor = new(grey.Id, "Out");

            if (set.HeightNormal == LayerHeightNormal.Smooth) {
                var blur = Add("Filters/Blur");

                blur.SetValue("Radius", SmoothRadius);
                graph.Connect(cursor, new(blur.Id, "Input"));
                cursor = new(blur.Id, "Out");
            }

            var slope = Add("Surface/Height to Normal");

            slope.SetValue("Intensity", ReliefIntensity);
            graph.Connect(cursor, new(slope.Id, "Height"));
            cursor = new(slope.Id, "Out");

            if (normal is { } beneath) {
                var combine = Add("Surface/Normal Combine");

                graph.Connect(beneath, new(combine.Id, "Base"));
                graph.Connect(cursor, new(combine.Id, "Detail"));
                cursor = new(combine.Id, "Out");
            }

            var output = Add("Output/Output");

            output.SetText("Usage", NormalUsage);
            graph.Connect(cursor, new(output.Id, "Input"));

            notes.Add(
                new(
                    output.Id,
                    $"Normal of texture set '{set.Name}': its height's slope ({set.HeightNormal})"
                    + (normal is null ? "." : ", combined over its normal channel.")
                )
            );
        }

        /// <summary>Every layer of one list composited over a cursor, bottom first.</summary>
        PortRef Stack(List<LayerAsset> layers, ChannelAsset channel, PortRef cursor, int depth) {
            if (depth >= MaxGroupDepth) {
                problems.Add(
                    LayerStackProblem.Refusal(
                        "",
                        $"Groups nest more than {MaxGroupDepth} deep in texture set '{set.Name}'. That is a hang "
                        + "check rather than a design limit — a hand-edited .vxlayers can name a group inside itself."
                    )
                );

                return cursor;
            }

            foreach (var layer in layers) {
                cursor = Layer(layer, channel, cursor, depth);
            }

            return cursor;
        }

        /// <summary>One layer composited over the cursor, with everything it emits attributed to it.</summary>
        /// <remarks>
        ///     ⚠
        ///     <b>
        ///         Saved and restored rather than assigned, because a group's children are layers
        ///         too.
        ///     </b>
        ///     <see cref="Group" /> re-enters <see cref="Stack" />, so a child sets
        ///     <see cref="owner" /> to its own id and has to hand the group's back — the group's own
        ///     blend node is emitted <em>after</em> its children have run. Assigning without
        ///     restoring would file every group's composite under whichever child happened to be
        ///     last.
        /// </remarks>
        PortRef Layer(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) {
            var outer = owner;

            // What lies beneath it in this channel, for an input that reads it from another channel's walk. Its
            // children are given the same until a walk reaches them, which one never does when this layer is off or
            // leaves the channel alone — and then what lies beneath them is what lies beneath it.
            below[(channel.Usage, layer.Id)] = cursor;
            Beneath(layer.Children, channel.Usage, cursor);

            owner = layer.Id;

            try {
                var result = Composite(layer, channel, cursor, depth);

                // ⚠ In every channel, and whether or not the layer wrote this one — Patina #111. The picture at an anchor
                // point is the stack as it stands there; a layer that leaves roughness alone has a roughness above it
                // all the same, and refusing the stack because it wrote nothing was a refusal of a correct file.
                if (layer.AnchorPoint.Length > 0) {
                    results[(channel.Usage, layer.AnchorPoint)] = result;
                }

                return result;
            } finally {
                owner = outer;
            }
        }

        /// <summary>Gives layers no walk has reached yet what lies beneath their parent.</summary>
        void Beneath(List<LayerAsset> layers, string usage, PortRef cursor) {
            foreach (var layer in layers) {
                below.TryAdd((usage, layer.Id), cursor);
                Beneath(layer.Children, usage, cursor);
            }
        }

        /// <summary>One layer composited over the cursor, or the cursor unchanged.</summary>
        PortRef Composite(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) {
            if (!layer.Enabled || !layer.Writes(channel.Usage)) {
                return cursor;
            }

            var content = Content(layer, channel, cursor, depth);
            var effects = layer.Kind != LayerKind.Group && layer.Children.Count > 0;

            if (content is null && !effects) {
                return cursor;
            }

            var foreground = content is { } own ? Project(layer, own) : Transparent();

            // ⚠ A layer's children are its effects: a small stack run over its own content, inside its
            // mask, so that a fill or a filter added to a layer stays that layer's. A layer with no
            // content in this channel still has theirs, over nothing.
            if (effects) {
                var stacked = Stack(layer.Children, channel, foreground, depth + 1);

                if (content is null && stacked == foreground) {
                    return cursor;
                }

                foreground = stacked;
            }

            if (Capturing.Length > 0 && layer.Id == Capturing) {
                captures[channel.Usage] = foreground;
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

            notes.Add(
                new(
                    blend.Id,
                    $"Layer '{(layer.Name.Length > 0 ? layer.Name : layer.Id)}' · {layer.Kind} · {layer.Blend} at "
                    + $"{(layer.Opacity * 100f).ToString("0.#", CultureInfo.InvariantCulture)}%"
                    + (layer.Mask.Source == LayerMaskSource.None ? "" : $" · {layer.Mask.Source} mask")
                    + (folded is null ? "" : ", folded into the opacity")
                    + $" · channel '{channel.Usage}'."
                )
            );

            return result;
        }

        /// <summary>
        ///     What the layer puts on top, or <see langword="null" /> when it contributes nothing.
        /// </summary>
        PortRef? Content(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) =>
            layer.Kind switch {
                LayerKind.Fill => Weighing ? Covers() : Fill(layer, channel),

                // ⚠ Nothing, under `Weighing`. An adjustment is not a thing that covers a texel — it
                // reads what is under it — and a filter layer taken as a fill would claim the whole
                // surface, so a stack with a Levels layer on top would weigh as one layer everywhere.
                LayerKind.Filter => Weighing ? null : Adjustment(layer, channel, cursor),
                // ⚠ A flattened group is its canvas, exactly as a paint layer is, and its children are not walked: that
                // is the whole of what flattening buys. Except while it is being captured, which is how the canvas is made.
                LayerKind.Group when layer.Flattened && layer.Id != Capturing =>
                    Weighing ? Covered(Paint(layer, channel)) : Paint(layer, channel),
                LayerKind.Group => Group(layer, channel, cursor, depth),
                LayerKind.Paint => Weighing ? Covered(Paint(layer, channel)) : Paint(layer, channel),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(layer),
                    layer.Kind,
                    "Not a layer kind this build knows."
                )
            };

        /// <summary>A fill's coverage, which is all of it: one, everywhere.</summary>
        /// <remarks>
        ///     ⚠ <b>Whatever the fill actually is.</b> A constant, a texture and a generator all cover
        ///     every texel they are composited over — what limits them is the mask and the opacity,
        ///     which <see cref="Composite" /> applies to this exactly as it applies them to a colour.
        ///     Reading the fill's own pixels here would weigh a dark texture as a layer that is barely
        ///     there.
        /// </remarks>
        PortRef Covers() {
            var node = Add("Source/Uniform");

            node.SetValue("Colour", 1f, 1f, 1f, 1f);

            return new(node.Id, "Out");
        }

        /// <summary>A painted canvas as a coverage: its alpha kept, its colour forced to one.</summary>
        /// <remarks>
        ///     ⚠ <b>The alpha is the whole of what a paint layer covers</b>, and it is the one kind
        ///     for which <see cref="Covers" /> would be wrong rather than merely coarse: an unpainted
        ///     canvas is transparent everywhere and a white constant in its place says the layer took
        ///     the whole atlas. The shuffle is <see cref="Opaque" />'s, pointed the other way — that
        ///     one makes a number out of a picture's red, this one makes a picture out of a number in
        ///     alpha, because <c>Blend</c> reads alpha as the coverage.
        /// </remarks>
        PortRef? Covered(PortRef? content) {
            if (content is not { } painted) {
                return null;
            }

            var node = Add("Colour/Channel Shuffle");

            node.SetText("Red From", "One");
            node.SetText("Green From", "One");
            node.SetText("Blue From", "One");
            node.SetText("Alpha From", "FirstAlpha");

            graph.Connect(painted, new(node.Id, "First"));

            return new(node.Id, "Out");
        }

        /// <summary>A group's children, passed through or isolated, or nothing when they add none.</summary>
        /// <remarks>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             A group passes through under <see cref="LayerBlendMode.Copy" /> and isolates
        ///             under every other mode, which is the distinction
        ///             <a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> · 1 was missing.
        ///         </b>
        ///         Compositing the children onto the cursor and blending the <em>result</em> back
        ///         over that same cursor applies the group's mode to the whole canvas rather than to
        ///         what the group covered: with a canvas of ½ and a group of one fully-masked child,
        ///         a <c>Multiply</c> group baked ¼ where it had to bake ½. The cursor is both "what
        ///         this group wrote" and "what was already there", and a non-Copy operator cannot
        ///         tell them apart.
        ///     </para>
        ///     <para>
        ///         <b>
        ///             So an isolated group's children start from transparency, and the alpha they
        ///             accumulate <em>is</em> the group's coverage.
        ///         </b>
        ///         <c>Blend.rvn</c> computes
        ///         <c>amount = saturate(opacity) · saturate(b.w)</c>, so a texel no child covered
        ///         arrives with alpha 0, blends by 0 and leaves the backdrop exactly as it was —
        ///         whatever the operator. That is what makes the group's own mode, opacity and mask
        ///         mean something without a coverage channel of this class's own.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             And starting a chain from transparency is the one thing in this file that
        ///             asks anything of <c>Blend.rvn</c> beyond an opaque backdrop, which is why
        ///             <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a> · 2 landed there
        ///             rather than here.
        ///         </b>
        ///         The kernel used to implement the opaque-backdrop
        ///         specialisation of source-over — exact for every chain that starts at a source,
        ///         and premultiplying for one that starts at nothing — so a group of partial
        ///         coverage handed back a colour already lerped towards black and its own blend then
        ///         consumed it as a straight one, darkening it by exactly its coverage. The fix is
        ///         the general form in the kernel and no node at all here: this class does not need
        ///         to un-premultiply what a correct compositor never premultiplied.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             Pass-through is kept for <c>Copy</c> rather than isolating everything, and
        ///             the reason is a child's own operator.
        ///         </b>
        ///         A child set to <c>Multiply</c> inside an
        ///         isolated group multiplies against an empty backdrop and darkens to nothing — the
        ///         standard behaviour of an isolated group in every compositor, and exactly why
        ///         grouping layers under the default mode must not change the picture. Under
        ///         <c>Copy</c> the result is the same either way for a covered texel, so the mode
        ///         that means "do not reinterpret my children" is the one that does not isolate.
        ///     </para>
        /// </remarks>
        PortRef Transparent() {
            var empty = Add("Source/Uniform");

            empty.SetValue("Colour", 0f, 0f, 0f, 0f);

            return new(empty.Id, "Out");
        }

        PortRef? Group(LayerAsset layer, ChannelAsset channel, PortRef cursor, int depth) {
            // A captured pass-through group is isolated unless it keeps its backdrop: the canvas it is flattened into is
            // composited over that backdrop again, and holding it twice would be the backdrop over itself.
            var isolating = layer.Id == Capturing && Capturing.Length > 0 && !Backdrop;

            if (layer.Blend == LayerBlendMode.Copy && !isolating) {
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

            // ⚠ Kept when an anchor point inside is that transparency: an anchored child that wrote nothing here still
            // publishes the picture at its point, and that picture is this node.
            if (inner != transparent || results.ContainsValue(transparent)) {
                return inner == transparent ? null : inner;
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
        ///         <b>
        ///             Exactly <see cref="LayerFillSource.Texture" />'s node, with a reference a host
        ///             resolves differently
        ///         </b>
        ///         —
        ///         <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>. A paint layer is a
        ///         picture; the only thing that distinguishes it from an imported one is where the
        ///         bytes come from, and that is a question for whoever fills the plan's externals.
        ///         So there is no paint node, no paint op and no second path through the compiler —
        ///         which is what keeps doc 48 § D1's "the stack goes through the same compiler"
        ///         true of the layer kind that could most easily have broken it.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             One reference per channel, because a <c>.vxpaint</c> is one image per
        ///             channel.
        ///         </b>
        ///         <c>PaintCanvas</c>' own remarks say why the file is shaped that way:
        ///         a layer that paints roughness alone must not also carry a base-colour buffer it
        ///         never writes. <see cref="Content" /> is called once per channel the set writes, so
        ///         this emits the bitmap for <em>that</em> channel and the unwritten ones never
        ///         appear.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             A layer nobody has painted on is a warning and not a refusal, and the
        ///             difference is what an artist sees.
        ///         </b>
        ///         A refusal takes the whole stack's plan with
        ///         it — <c>LayerStackCompiler.Compile</c> stops on <c>HasErrors</c> — so the moment a
        ///         panel created a paint layer, every other layer in the stack would stop previewing
        ///         until the first stroke landed. An empty canvas contributes nothing, which is
        ///         exactly what an unpainted layer means.
        ///     </para>
        /// </remarks>
        PortRef? Paint(LayerAsset layer, ChannelAsset channel) {
            var path = layer.Paint.Trim();

            if (path.Length == 0) {
                // ⚠ Not worth saying in a draft: it is what a layer nobody has stroked yet looks like.
                if (!Drafting) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layer.Id,
                            "This paint layer names no .vxpaint, so it has nothing to composite. A canvas is written "
                            + "the first time the layer is painted on; until then the layer keeps its blend mode, its "
                            + "opacity and its channel enables and contributes nothing."
                        )
                    );
                }

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

        /// <summary>
        ///     ⚠ Doc 48 § D10's triplanar and planar projections, which were modelled and refused
        ///     until <a href="https://github.com/Rikarin/Vixen/issues/815">#815</a>.
        /// </summary>
        /// <param name="layer">The layer whose projection is being applied.</param>
        /// <param name="content">What the layer puts on top, in the atlas.</param>
        /// <returns>The projected content, or <paramref name="content" /> unchanged.</returns>
        /// <remarks>
        ///     <para>
        ///         <b>
        ///             What a projection is for, because "put the picture on by world position" reads
        ///             as a novelty until you ask what it buys.
        ///         </b>
        ///         A layer projected triplanar covers a
        ///         mesh whose UVs it was never authored for — which is the whole premise of a shipped
        ///         smart material. Without it every <c>.vxsmartmat</c> is a material for one atlas,
        ///         and § D10's member had been there since M7 with a refusal behind it.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             Two <c>Source/Mesh Map</c> nodes rather than anything this class fetches
        ///             itself.
        ///         </b>
        ///         The projection needs the <c>position</c> and <c>world</c> bakes, and
        ///         the only way a graph asks for one is by usage — the bake decides which mesh a
        ///         <c>meshmap:position</c> resolves to. So a projected layer's plan carries two more
        ///         externals, and a host that cannot supply them says so with the same sentence it
        ///         already uses for a generator's <c>curvature</c>.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             Planar goes down <see cref="LayerAsset.PlanarAxis" />, which this file used
        ///             to choose
        ///         </b>
        ///         — <a href="https://github.com/Rikarin/Vixen/issues/1032">#1032</a>.
        ///         <c>LayerProjection.Planar</c> is "one planar projection along an axis" and had
        ///         nowhere to put the axis, so every planar layer ever authored went down y: the
        ///         right default and a default the <em>compiler</em> picked, which on anything
        ///         box-shaped gives a valid planar projection of the wrong face and no clue why.
        ///         Y stays the default, so nothing that exists changes picture.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>A constant fill and a non-fill layer are warned rather than projected.</b> A
        ///         constant is the same colour at every world position, so projecting it would emit
        ///         three dispatches to reproduce the number it started with; a paint layer's pixels
        ///         are authored <em>in the atlas</em>, and a filter reads what is under it. Silently
        ///         ignoring the member in those cases is the defect this workstream produces most —
        ///         a mechanism whose caller passes the default — so each one says so by name.
        ///     </para>
        /// </remarks>
        PortRef Project(LayerAsset layer, PortRef content) {
            // ⚠ Skipped entirely while weighing, warnings included. A projection decides *where on
            // the mesh* a fill's pixels land, and a coverage has no pixels to land: the answer is one
            // wherever the layer is composited either way. Running it would emit two mesh-map
            // externals a coverage bake has no use for, and — worse — the constant-fill caution,
            // which would then be reported against a constant this build substituted rather than
            // against anything the author wrote.
            if (Weighing) {
                return content;
            }

            // ⚠ Before the early return, because a UV layer is one of the two places an axis means
            // nothing — and Y is not checked here because it is the default: "the author chose y"
            // and "the author said nothing" are one state, which is why `LayerAxis`' zero is X.
            if (layer.Projection is not (LayerProjection.Planar or LayerProjection.Cylindrical)
                && layer.PlanarAxis != LayerAxis.Y) {
                problems.Add(
                    LayerStackProblem.Warning(
                        layer.Id,
                        $"Axis '{layer.PlanarAxis}' is set on a {layer.Projection} projection and does nothing there. "
                        + "An axis chooses the single plane a Planar projection uses or the line a Cylindrical one "
                        + "wraps around; Triplanar blends all three planes by the world normal, a sphere has no "
                        + "axis to choose, and Uv is the mesh's own atlas."
                    )
                );
            }

            if (layer.Projection == LayerProjection.Uv) {
                return Placed(layer, content);
            }

            if (layer.Kind != LayerKind.Fill) {
                problems.Add(
                    LayerStackProblem.Warning(
                        layer.Id,
                        $"Projection '{layer.Projection}' is set on a {layer.Kind} layer and does nothing there. A "
                        + "projection decides how a fill covers a surface: a paint layer's pixels are already in the "
                        + "atlas, a filter reads whatever is under it, and a group is its children. The layer is "
                        + "composited through its UVs."
                    )
                );

                return content;
            }

            if (layer.Fill == LayerFillSource.Constant) {
                problems.Add(
                    LayerStackProblem.Warning(
                        layer.Id,
                        $"Projection '{layer.Projection}' is set on a constant fill and changes nothing — a constant "
                        + "is the same colour at every world position. Give the layer a texture or a graph, or leave "
                        + "the projection at Uv; the three dispatches a projection costs would reproduce the number "
                        + "the layer started with."
                    )
                );

                return content;
            }

            var place = Add("Source/Mesh Map");
            var facing = Add("Source/Mesh Map");
            var node = Add("Space/Triplanar");

            place.SetText("Map", "position");
            facing.SetText("Map", "world");
            // ⚠ `LayerAxis`' names are `Space/Triplanar`'s own `Axis` setting, so this is the member
            // rather than a mapping — a fourth axis is a word in each file and no table. #1032.
            node.SetText(
                "Axis",
                layer.Projection switch {
                    LayerProjection.Triplanar => "Triplanar",
                    LayerProjection.Spherical => "Spherical",
                    LayerProjection.Cylindrical => $"Cylinder{layer.PlanarAxis}",
                    _ => layer.PlanarAxis.ToString()
                }
            );

            graph.Connect(content, new(node.Id, "Input"));
            graph.Connect(new(place.Id, "Out"), new(node.Id, "Position"));
            graph.Connect(new(facing.Id, "Out"), new(node.Id, "Normal"));

            // ⚠ The layer's own numbers, by port name — `Adjustment`'s mechanism pointed at a fill
            // rather than at a filter, and it is here so that a projection is not a mechanism whose
            // caller always passes the default. A projected layer that could not say how many times
            // its picture repeats across the mesh would be usable at exactly one mesh size. The home
            // those numbers deserve is #1032's other half: `LayerAsset.Settings`' own doc comment
            // says "the filter's numbers", which is now narrower than what reads it.
            var placement = layer.Placement;
            node.SetValue("Scale", placement.Scale);
            node.SetValue("Sharpness", placement.Sharpness);
            node.SetValue("Rotation", placement.Rotation);
            node.SetValue("Offset X", placement.OffsetX);
            node.SetValue("Offset Y", placement.OffsetY);

            // ⚠ A graph's numbers are the graph's: `Fill` hands them to its ports, and a graph with
            // its own Scale must not move the projection as well.
            if (layer.Fill == LayerFillSource.Graph) {
                return new(node.Id, "Out");
            }

            foreach (var (port, value) in layer.Settings) {
                if (!string.Equals(port, "Scale", StringComparison.Ordinal)
                    && !string.Equals(port, "Sharpness", StringComparison.Ordinal)) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layer.Id,
                            $"'{port}' is not a number a projection takes — it takes Scale and Sharpness. The value is "
                            + "dropped rather than written to a port that might be the image input."
                        )
                    );

                    continue;
                }

                node.SetValue(port, value);
            }

            return new(node.Id, "Out");
        }

        /// <summary>A UV fill's own scale, rotation and offset, through <c>Space/Transform 2D</c>.</summary>
        /// <remarks>
        ///     Nothing at all when it has none, so that a stack without placement compiles to the plan
        ///     it always did. ⚠ <see cref="LayerPlacement.Scale" /> is a repeat count and the node's is a
        ///     size, so the two are reciprocal. Rotation here does not break tiling as it would before
        ///     a projection: the node reads its source wrapped, and its result is not tiled again.
        /// </remarks>
        PortRef Placed(LayerAsset layer, PortRef content) {
            var placement = layer.Placement;
            if (placement with { Sharpness = 4f } == new LayerPlacement()) {
                return content;
            }

            if (layer.Kind != LayerKind.Fill || layer.Fill == LayerFillSource.Constant) {
                problems.Add(
                    LayerStackProblem.Warning(
                        layer.Id,
                        "A placement is set here and does nothing: it moves a texture or a graph on the surface, "
                        + "and this layer is neither."
                    )
                );

                return content;
            }

            if (placement.Scale <= 0f) {
                problems.Add(
                    LayerStackProblem.Refusal(
                        layer.Id,
                        $"A scale of {placement.Scale} has nothing to repeat. A placement's scale is above zero."
                    )
                );

                return content;
            }

            var node = Add("Space/Transform 2D");

            node.SetValue("Rotation", placement.Rotation);
            node.SetValue("Scale X", 1f / placement.Scale);
            node.SetValue("Scale Y", 1f / placement.Scale);
            node.SetValue("Offset X", placement.OffsetX);
            node.SetValue("Offset Y", placement.OffsetY);
            graph.Connect(content, new(node.Id, "Input"));

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
                        Unfilled(
                            layer.Id,
                            $"This layer is a texture fill and names no image for channel '{channel.Usage}', which "
                            + "it writes. Restrict the layer's channels or give it an image: a bitmap with no "
                            + "reference is refused at compile time rather than filled with black."
                        );

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
                        Unfilled(
                            layer.Id,
                            "This layer is a graph fill and names no graph. A graph fill is a published "
                            + ".vxtexgraph, named by its path in the node menu."
                        );

                        return null;
                    }

                    if (!Library.TryGet(path, out var type)) {
                        problems.Add(
                            LayerStackProblem.Refusal(
                                layer.Id,
                                $"This layer is a graph fill naming '{path}', which is not a node type this project "
                                + "has. A published compound is registered under its path in the library folder."
                            )
                        );

                        return null;
                    }

                    // One image out fills every channel the layer writes. A material has one per
                    // channel, named by its usage, and a channel it has no map for is one it leaves.
                    var output = OnlyImage(type, PortDirection.Output) ?? ByUsage(type, channel.Usage);

                    if (output is null) {
                        if (!HasImageOutput(type)) {
                            problems.Add(
                                LayerStackProblem.Refusal(
                                    layer.Id,
                                    $"The graph '{path}' has no image output, so there is nothing for this layer to fill with."
                                )
                            );
                        }

                        return null;
                    }

                    // A graph's image inputs read what `Inputs` names, and one nothing feeds is a slot nobody has filled.
                    // ⚠ "The channel being written" is allowed here: a fill writes each channel itself.
                    if (Bound(path, type, null, layer.Inputs, layer.Id, channel, null, false) is not { } node) {
                        return null;
                    }

                    foreach (var (port, value) in layer.Settings) {
                        if (HasInput(type, port)) {
                            node.SetValue(port, value);
                        }
                    }

                    // A compound's knobs are settings, as on a filter, and one the graph has since
                    // dropped says so rather than leaving a number that moves nothing.
                    foreach (var (setting, value) in layer.Texts) {
                        if (type.Setting(setting) is null) {
                            problems.Add(
                                LayerStackProblem.Warning(
                                    layer.Id,
                                    $"'{setting}' is not a setting '{path}' declares, so it is dropped."
                                )
                            );

                            continue;
                        }

                        node.SetText(setting, value);
                    }

                    return new(node.Id, output.Name);
                }

                case LayerFillSource.Anchor: {
                    if (Read(layer.Anchor, layer.Id, true) is not { } read) {
                        return null;
                    }

                    // A shuffle that passes everything through, so the fill has a port now and the anchor's picture
                    // arrives on it when every layer's has been built — the same deferral a mask's anchor takes.
                    var node = Add("Colour/Channel Shuffle");

                    node.SetText("Red From", "FirstRed");
                    node.SetText("Green From", "FirstGreen");
                    node.SetText("Blue From", "FirstBlue");
                    node.SetText("Alpha From", "FirstAlpha");

                    anchors.Add((read, layer.Anchor.Anchor, layer.Id, new(node.Id, "First")));

                    return new(node.Id, "Out");
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(layer), layer.Fill, "Not a fill this build knows.");
            }
        }

        /// <summary>A filter layer's adjustment, reading everything under it.</summary>
        /// <remarks>
        ///     ⚠ <b>Two paths, and the second is doc 48 § D10's fourth kind</b> —
        ///     <a href="https://github.com/Rikarin/Vixen/issues/1068">#1068</a>. A named node type
        ///     goes through <see cref="Published" />, which is the mask-effect resolution pointed at
        ///     a layer; the five <c>LayerFilterKind</c> members go through the code below, untouched,
        ///     because <c>LayerStackExplodeTests</c>' byte-identical differential is what says this
        ///     change added a kind rather than moved the existing ones.
        /// </remarks>
        PortRef? Adjustment(LayerAsset layer, ChannelAsset channel, PortRef cursor) {
            if (layer.FilterNode.Trim().Length > 0) {
                return Published(layer, channel, cursor);
            }

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
                    problems.Add(
                        LayerStackProblem.Warning(
                            layer.Id,
                            $"'{port}' is not a number the '{layer.Filter}' filter takes — it takes "
                            + $"{string.Join(", ", ports)}. The value is dropped rather than written to a port that "
                            + "might be the image input."
                        )
                    );

                    continue;
                }

                node.SetValue(port, value);
            }

            return new(node.Id, "Out");
        }

        /// <summary>A filter layer whose adjustment is a node type it names, published or built in.</summary>
        /// <remarks>
        ///     <para>
        ///         <b><c>Effect</c>'s resolution, on a layer instead of on a mask</b> —
        ///         <a href="https://github.com/Rikarin/Vixen/issues/1068">#1068</a>. The two are the
        ///         same question: find the type's single <c>Image</c> input and its single
        ///         <c>Image</c> output, wire what is beneath into the first, and hand the second on.
        ///         A type with two images in is a composite rather than an adjustment, and which of
        ///         its inputs the layer beneath would be is not something a stack file can decide.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             The ports are derived from the type rather than listed, and what that is
        ///             protecting is the image wire.
        ///         </b>
        ///         <c>LayerFilterKind</c>'s path above carries a
        ///         hand-written port list per filter for exactly this reason — a setting called
        ///         <c>Input</c> written to <c>Colour/Levels</c> would replace everything beneath the
        ///         layer with a constant, and the picture would be the filter over nothing at all.
        ///         A compound's ports cannot be listed here, so the check is the one
        ///         <c>MaskEffectAsset</c>'s remarks specify: not the image input, declared by the
        ///         type, and not itself an image.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>A refusal rather than a pass-through, unlike a mask effect.</b>
        ///         <c>Effect</c> answers a bad node by returning the cursor unchanged, because a mask
        ///         with one broken adjustment is still a mask. A filter layer <em>is</em> its
        ///         adjustment: returning the cursor would silently delete the layer from the stack
        ///         and leave a picture that looks like a working file with one layer switched off.
        ///     </para>
        /// </remarks>
        PortRef? Published(LayerAsset layer, ChannelAsset channel, PortRef cursor) {
            var path = layer.FilterNode.Trim();

            if (!Library.TryGet(path, out var type)) {
                problems.Add(
                    LayerStackProblem.Refusal(
                        layer.Id,
                        $"This filter layer names '{path}', which is not a node type this project has. A published "
                        + "compound is registered under its path in the library folder; a built-in is under its "
                        + "category, such as 'Colour/Levels'."
                    )
                );

                return null;
            }

            var input = FirstImage(type);
            var outputs = Images(type, PortDirection.Output);

            if (input is null || outputs == 0 || (outputs > 1 && !PerChannel(type))) {
                problems.Add(
                    LayerStackProblem.Refusal(
                        layer.Id,
                        $"'{path}' is not a filter: a filter reads an image and writes one, or one per channel named "
                        + "after it, and this type has "
                        + $"{Images(type, PortDirection.Input).ToString(CultureInfo.InvariantCulture)} in and "
                        + $"{outputs.ToString(CultureInfo.InvariantCulture)} out."
                    )
                );

                return null;
            }

            // ⚠ A material filter writes the channels it has an output for and leaves the rest as they were.
            if ((PerChannel(type) ? ByUsage(type, channel.Usage) : OnlyImage(type, PortDirection.Output))
                is not { } output) {
                return null;
            }

            if (Bound(path, type, input, layer.Inputs, layer.Id, channel, cursor, false) is not { } node) {
                return null;
            }

            foreach (var (port, value) in layer.Settings) {
                if (string.Equals(port, input.Name, StringComparison.Ordinal)
                    || type.Port(port, PortDirection.Input) is not { } declared
                    || declared.Kind == PortKind.Image) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layer.Id,
                            $"'{port}' is not a number '{path}' takes, so the value is dropped rather than written "
                            + "to a port that might be the image the filter reads."
                        )
                    );

                    continue;
                }

                node.SetValue(port, value);
            }

            // ⚠ `Effect`'s second loop, and the member it reads did not exist on a layer until
            // #1079: a compound whose behaviour is chosen by a string setting — which is most of the
            // ones worth publishing — took that setting's default here, silently. Only on this path,
            // because the five `LayerFilterKind` members declare no settings at all and must go on
            // compiling to exactly the ops `LayerStackExplodeTests` photographs.
            foreach (var (setting, value) in layer.Texts) {
                if (type.Setting(setting) is null) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layer.Id,
                            $"'{setting}' is not a setting '{path}' declares, so it is dropped."
                        )
                    );

                    continue;
                }

                node.SetText(setting, value);
            }

            return new(node.Id, output.Name);
        }

        /// <summary>The mask multiplied into the foreground's coverage, or the foreground unchanged.</summary>
        /// <remarks>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             The mask is composited before it is shuffled, and the shuffle is still what
        ///             puts it into alpha.
        ///         </b>
        ///         A mask stack is a chain of <c>Colour/Blend</c> nodes
        ///         exactly like the layer stack it masks — the same operators, the same kernel — and
        ///         only its <em>result</em> becomes the foreground's coverage. Doc 48 § D10's "a mask
        ///         is itself a small stack" is therefore not a second compositor: it is this one,
        ///         called on a smaller list.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             Multiplied and not <em>replaced</em>, which is
        ///             <a href="https://github.com/Rikarin/Vixen/issues/790">#790</a> and half of
        ///             <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a>.
        ///         </b>
        ///         A mask is
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
        ///         ⚠
        ///         <b>
        ///             The entries are composited by <c>Colour/Blend</c>, so every operand of one of
        ///             those blends is forced opaque first
        ///         </b>
        ///         —
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
                entries.Add(
                    new() {
                        Source = mask.Source,
                        Value = mask.Value,
                        Asset = mask.Asset,
                        Anchor = mask.Anchor,
                        Generator = mask.Generator,
                        GeneratorValues = mask.GeneratorValues,
                        GeneratorTexts = mask.GeneratorTexts,
                        GeneratorInputs = mask.GeneratorInputs,
                        Map = mask.Map,
                        Paint = mask.Paint
                    }
                );
            } else if (mask.Paint.Length > 0) {
                problems.Add(
                    LayerStackProblem.Warning(
                        layer.Id,
                        $"This layer's mask names a paint file ('{mask.Paint}') and its source is None, so the "
                        + "painted pixels are not read. Set the mask's source to Paint to read them."
                    )
                );
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
                        problems.Add(
                            LayerStackProblem.Warning(
                                layer.Id,
                                $"The bottom entry of this mask composites with '{entry.Blend}' and there is nothing "
                                + "beneath it, so the operator does nothing. A mask stack starts from its first "
                                + "entry rather than from a black constant, which is what keeps a plain one-source "
                                + "mask a single node."
                            )
                        );
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
        ///     ⚠
        ///     <b>
        ///         The red is splatted across the colour lanes as well, so that this is the same
        ///         shuffle <see cref="Mask" /> ends with rather than a second convention.
        ///     </b>
        ///     A mask is
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

        /// <summary>
        ///     The channel a reference reads, or <see langword="null" /> when it reads none it may — which is already
        ///     reported.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="layerId">The layer it belongs to, for a diagnostic.</param>
        /// <param name="writing">
        ///     Whether "the channel being built" is a choice it may make: a fill's or a filter's may, since that layer
        ///     writes each channel itself; a mask's may not, or one mask would be another picture in every channel.
        /// </param>
        string? Read(AnchorReference reference, string layerId, bool writing) {
            if (reference.Anchor.Length == 0) {
                Unfilled(layerId, "This layer reads an anchor point and names none. Choose one, by its name.");

                return null;
            }

            // A coverage set has a channel of its own and one layer in it, so the anchor cannot resolve whatever it names;
            // `Missing` says why, once, and in the words for that verb.
            if (Weighing) {
                return usage;
            }

            if (reference.Channel.Length == 0) {
                if (writing) {
                    return usage;
                }

                Unfilled(
                    layerId,
                    $"This layer reads the anchor point '{reference.Anchor}' and names no channel. A mask is one picture "
                    + "whatever channel it is applied to, so it says which of the set's channels it reads."
                );

                return null;
            }

            if (!set.Channels.Exists(channel => channel.Usage == reference.Channel)) {
                Unfilled(
                    layerId,
                    $"This layer reads the anchor point '{reference.Anchor}' in '{reference.Channel}', which is not a "
                    + $"channel of texture set '{set.Name}'."
                );

                return null;
            }

            return reference.Channel;
        }

        /// <summary>A node with every image input wired to what it reads, or null when one has nothing to read yet.</summary>
        /// <param name="path">The node type's path.</param>
        /// <param name="type">The node type.</param>
        /// <param name="subject">Its first image input, which <paramref name="cursor" /> feeds, or null for a fill.</param>
        /// <param name="inputs">Where its inputs read, by port.</param>
        /// <param name="layerId">The layer it belongs to.</param>
        /// <param name="channel">The channel being built.</param>
        /// <param name="cursor">What the subject reads until the layer says otherwise: the stack beneath, or a mask.</param>
        /// <param name="mask">Whether this is a mask step, whose subject is always its mask and whose pictures are one channel.</param>
        /// <remarks>
        ///     <para>
        ///         <b>Patina #113 and #114, and ADR 0017.</b> An input <paramref name="inputs" /> names reads what it
        ///         names. One it does not reads, in order: the stack beneath in the channel it is named after, when its
        ///         name is one of the set's channels — which is how a material filter reads height and roughness at once;
        ///         <paramref name="cursor" />, when it is the subject; nothing, when the node does without it. Any other
        ///         is a slot nobody has filled.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Every slot is decided before the node is added</b>, so an empty one leaves nothing half-wired in
        ///         the graph for a draft's compile to trip over. A port that measures — a warp's field, a slope — takes
        ///         the grey of what it reads, as the subject always has.
        ///     </para>
        /// </remarks>
        GraphNode? Bound(
            string path,
            NodeTypeDefinition type,
            PortDefinition? subject,
            Dictionary<string, LayerInputAsset> inputs,
            string layerId,
            ChannelAsset channel,
            PortRef? cursor,
            bool mask
        ) {
            foreach (var port in inputs.Keys) {
                if (type.Port(port, PortDirection.Input) is not { Kind: PortKind.Image }) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layerId,
                            $"'{port}' is not an image input of '{path}', so what it reads is dropped."
                        )
                    );
                }
            }

            List<(PortDefinition Port, LayerInputAsset? Input, string Read)> plan = [];
            var ready = true;

            foreach (var port in type.Ports) {
                if (port is not { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                    continue;
                }

                var own = subject is not null && string.Equals(port.Name, subject.Name, StringComparison.Ordinal);

                if (mask && own) {
                    if (inputs.ContainsKey(port.Name)) {
                        problems.Add(
                            LayerStackProblem.Warning(
                                layerId,
                                $"'{port.Name}' is the mask a step on a mask changes, so what it was told to read is "
                                + "dropped."
                            )
                        );
                    }

                    plan.Add((port, null, ""));

                    continue;
                }

                if (inputs.TryGetValue(port.Name, out var input)) {
                    if (Readable(input, path, port.Name, layerId, channel, mask) is { } read) {
                        plan.Add((port, input, read));
                    } else {
                        ready = false;
                    }

                    continue;
                }

                if (set.Channels.Exists(held => string.Equals(held.Usage, port.Name, StringComparison.Ordinal))) {
                    plan.Add((port, LayerInputAsset.BeneathIn(port.Name), port.Name));

                    continue;
                }

                if (own && cursor is not null) {
                    plan.Add((port, null, ""));

                    continue;
                }

                if (TextureImageInputs.Optional(Library, SubGraphs, path, port.Name)) {
                    continue;
                }

                Unfilled(
                    layerId,
                    $"'{path}' reads '{port.Name}' from nothing yet. Choose what it reads: the layers beneath in a "
                    + "channel, an anchor point, a generator, an image or a mesh map."
                );

                ready = false;
            }

            if (!ready) {
                return null;
            }

            var node = Add(path);

            foreach (var (port, input, read) in plan) {
                PortRef to = new(node.Id, port.Name);

                if (TextureImageInputs.Measures(Library, SubGraphs, path, port.Name)) {
                    var grey = Add("Colour/Grayscale");

                    // A mask is read for its red, as every mask source is; a picture for its luminance. ⚠ And a generator
                    // or a mesh map for its red too: most are one channel already, and `Grayscale` reads one channel as
                    // (r, 0, 0) and weighs it down to a fifth of itself under the luminance weights.
                    if (mask || input?.Source is LayerInputSource.Generator or LayerInputSource.Bake) {
                        grey.SetValue("Weight R", 1f);
                        grey.SetValue("Weight G", 0f);
                        grey.SetValue("Weight B", 0f);
                    }

                    graph.Connect(new(grey.Id, "Out"), to);
                    to = new(grey.Id, "Input");
                }

                if (input is null) {
                    graph.Connect(cursor!.Value, to);
                } else {
                    Source(input, read, layerId, to);
                }
            }

            return node;
        }

        /// <summary>
        ///     Whether an input can be read, saying why not when it cannot: the channel it reads, its anchor point's, or
        ///     an empty string for a source that reads none; null when it cannot.
        /// </summary>
        string? Readable(
            LayerInputAsset input,
            string path,
            string port,
            string layerId,
            ChannelAsset channel,
            bool mask
        ) {
            switch (input.Source) {
                case LayerInputSource.Beneath: {
                    var usage = input.Channel.Trim();

                    if (usage.Length == 0) {
                        if (mask) {
                            Unfilled(
                                layerId,
                                $"'{port}' of the mask step '{path}' reads the layers beneath in no channel. A mask is "
                                + "one picture whatever channel it is applied to, so it says which channel it reads."
                            );

                            return null;
                        }

                        return channel.Usage;
                    }

                    if (!set.Channels.Exists(held => string.Equals(held.Usage, usage, StringComparison.Ordinal))) {
                        Unfilled(
                            layerId,
                            $"'{port}' of '{path}' reads the layers beneath in '{usage}', which is not a channel of "
                            + $"texture set '{set.Name}'."
                        );

                        return null;
                    }

                    return usage;
                }

                case LayerInputSource.Anchor:
                    return Read(input.Anchor, layerId, !mask);

                case LayerInputSource.Generator: {
                    var generator = input.Generator.Trim();

                    if (generator.Length == 0) {
                        Unfilled(layerId, $"'{port}' of '{path}' reads a generator and names none.");

                        return null;
                    }

                    if (!Library.TryGet(generator, out var type)) {
                        problems.Add(
                            LayerStackProblem.Refusal(
                                layerId,
                                $"'{port}' of '{path}' reads the generator '{generator}', which is not a node type this "
                                + "project has."
                            )
                        );

                        return null;
                    }

                    if (Images(type, PortDirection.Input) > 0 || OnlyImage(type, PortDirection.Output) is null) {
                        problems.Add(
                            LayerStackProblem.Refusal(
                                layerId,
                                $"'{port}' of '{path}' reads '{generator}', which is not a generator: a generator reads "
                                + "no image and writes one."
                            )
                        );

                        return null;
                    }

                    return "";
                }

                case LayerInputSource.Texture:
                    if (input.Asset.Trim().Length == 0) {
                        Unfilled(layerId, $"'{port}' of '{path}' reads an image and names none.");

                        return null;
                    }

                    return "";

                case LayerInputSource.Bake:
                    if (input.Map.Trim().Length == 0) {
                        Unfilled(layerId, $"'{port}' of '{path}' reads a mesh map and names none.");

                        return null;
                    }

                    return "";

                default:
                    throw new ArgumentOutOfRangeException(nameof(input), input.Source, "Not an input this build knows.");
            }
        }

        /// <summary>Wires what one input reads into a port, now or once the walk has reached it.</summary>
        /// <param name="input">What it reads, already found readable.</param>
        /// <param name="read">The channel <see cref="Readable" /> found.</param>
        /// <param name="layerId">The layer reading it.</param>
        /// <param name="to">The port.</param>
        void Source(LayerInputAsset input, string read, string layerId, PortRef to) {
            switch (input.Source) {
                case LayerInputSource.Beneath:
                    if (below.TryGetValue((read, layerId), out var from)) {
                        graph.Connect(from, to);
                    } else {
                        beneath.Add((read, layerId, to));
                    }

                    return;

                case LayerInputSource.Anchor:
                    anchors.Add((read, input.Anchor.Anchor, layerId, to));

                    return;

                case LayerInputSource.Generator: {
                    var path = input.Generator.Trim();

                    Library.TryGet(path, out var type);

                    var node = Add(path);

                    foreach (var (port, value) in input.Values) {
                        if (type!.Port(port, PortDirection.Input) is not { Kind: not PortKind.Image }) {
                            problems.Add(
                                LayerStackProblem.Warning(layerId, $"'{port}' is not a number '{path}' takes, so it is dropped.")
                            );

                            continue;
                        }

                        node.SetValue(port, value);
                    }

                    foreach (var (setting, value) in input.Texts) {
                        if (type!.Setting(setting) is null) {
                            problems.Add(
                                LayerStackProblem.Warning(
                                    layerId,
                                    $"'{setting}' is not a setting '{path}' declares, so it is dropped."
                                )
                            );

                            continue;
                        }

                        node.SetText(setting, value);
                    }

                    graph.Connect(new(node.Id, OnlyImage(type!, PortDirection.Output)!.Name), to);

                    return;
                }

                case LayerInputSource.Texture: {
                    var node = Add("Source/Bitmap");

                    node.SetText("Source", input.Asset.Trim());
                    node.SetText("Space", "Linear");
                    node.SetText("Filter", "Bilinear");
                    graph.Connect(new(node.Id, "Out"), to);

                    return;
                }

                case LayerInputSource.Bake: {
                    var node = Add("Source/Mesh Map");

                    node.SetText("Map", input.Map.Trim());
                    graph.Connect(new(node.Id, "Out"), to);

                    return;
                }
            }
        }

        /// <summary>A type's first image input, which is a filter's subject, or null when it reads no image.</summary>
        static PortDefinition? FirstImage(NodeTypeDefinition type) {
            foreach (var port in type.Ports) {
                if (port is { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                    return port;
                }
            }

            return null;
        }

        /// <summary>Feeds a node's image inputs from anchor points, by port name.</summary>
        /// <param name="node">The node.</param>
        /// <param name="type">Its type.</param>
        /// <param name="path">Its type's path, for a diagnostic.</param>
        /// <param name="inputs">The anchor point read by each port.</param>
        /// <param name="layerId">The layer it belongs to, for a diagnostic.</param>
        /// <param name="writing">As <see cref="Read" /> takes it.</param>
        void Fed(
            GraphNode node,
            NodeTypeDefinition type,
            string path,
            Dictionary<string, AnchorReference> inputs,
            string layerId,
            bool writing
        ) {
            foreach (var (port, reference) in inputs) {
                if (type.Port(port, PortDirection.Input) is not { Kind: PortKind.Image }) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layerId,
                            $"'{port}' is not an image input of '{path}', so the anchor point '{reference.Anchor}' "
                            + "feeding it is dropped."
                        )
                    );

                    continue;
                }

                PortRef to = new(node.Id, port);

                if (Read(reference, layerId, writing) is { } read) {
                    anchors.Add((read, reference.Anchor, layerId, to));
                } else if (Drafting) {
                    // Already said. White, as an unresolved anchor is in a draft, so the node still compiles.
                    var nothing = Add("Source/Uniform");

                    nothing.SetValue("Colour", 1f, 1f, 1f, 1f);
                    graph.Connect(new(nothing.Id, "Out"), to);
                }
            }
        }

        /// <summary>An anchor point's picture, as the opaque number a mask entry is.</summary>
        /// <param name="anchor">The <see cref="LayerAsset.AnchorPoint" /> read.</param>
        /// <param name="channel">The channel it is read in, by usage — the reference's choice and not the chain's.</param>
        /// <param name="layerId">The layer the mask belongs to, for a diagnostic.</param>
        /// <returns>The port carrying <c>red · coverage</c>.</returns>
        /// <remarks>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             An anchor is the one mask source whose alpha <em>is</em> meaningful, and
        ///             folding it into the value is what #832 stopped doing by accident
        ///         </b>
        ///         —
        ///         <a href="https://github.com/Rikarin/Vixen/issues/874">#874</a>. An anchor resolves
        ///         to another layer's <em>evaluated result</em>, and for a layer inside an isolated
        ///         group that result's alpha is the group's coverage. Read for its red alone, an
        ///         anchor onto a layer covering a quarter of the texel masks as though it covered all
        ///         of it — "use that layer as a mask" quietly becoming "use the colour that layer
        ///         would have had".
        ///     </para>
        ///     <para>
        ///         <b>
        ///             The product rather than the coverage alone, and it is the answer that changes
        ///             nothing for every stack that already worked.
        ///         </b>
        ///         Anchoring a generator or a fill —
        ///         opaque, coverage 1 — gives <c>red · 1</c>, which is the red it gave before; the
        ///         coverage only bites where the anchored layer does not cover, which is where the
        ///         old kernel's premultiplied result also faded. Reading the alpha alone would make
        ///         an anchor onto any ordinary layer a constant 1, which is not a mask.
        ///     </para>
        ///     <para>
        ///         <b>
        ///             Three nodes, for <see cref="Mask" />'s reason: this library has no arithmetic
        ///             node
        ///         </b>
        ///         , so a product is computed in a colour lane by a <c>Multiply</c> between
        ///         two operands that have each been made opaque first. ⚠
        ///         <b>
        ///             Two deferred edges out of
        ///             one anchor
        ///         </b>
        ///         , because both shuffles read the same result and a shuffle selects
        ///         rather than multiplies; <see cref="Anchors" /> reports a refused anchor once.
        ///     </para>
        /// </remarks>
        PortRef Anchored(string anchor, string channel, string layerId) {
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

            anchors.Add((channel, anchor, layerId, new(value.Id, "First")));
            anchors.Add((channel, anchor, layerId, new(covered.Id, "First")));

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
                problems.Add(
                    LayerStackProblem.Refusal(
                        layerId,
                        "A mask effect names no node type. An effect is any single-input graph — Levels, Blur, "
                        + "Warp, or a published compound — named by its path in the node menu."
                    )
                );

                return cursor;
            }

            if (!Library.TryGet(path, out var type)) {
                problems.Add(
                    LayerStackProblem.Refusal(
                        layerId,
                        $"A mask effect names '{path}', which is not a node type this project has. A published "
                        + "compound is registered under its path in the library folder; a built-in is under its "
                        + "category, such as 'Colour/Levels'."
                    )
                );

                return cursor;
            }

            var input = FirstImage(type);
            var output = OnlyImage(type, PortDirection.Output);

            if (input is null || output is null) {
                problems.Add(
                    LayerStackProblem.Refusal(
                        layerId,
                        $"'{path}' is not a step a mask can take: doc 48 § 4.10 says a mask effect reads the mask "
                        + "into its first image input and writes one image, and this type has "
                        + $"{Images(type, PortDirection.Input).ToString(CultureInfo.InvariantCulture)} in and "
                        + $"{Images(type, PortDirection.Output).ToString(CultureInfo.InvariantCulture)} out."
                    )
                );

                return cursor;
            }

            // ⚠ A step with an input nobody has filled is passed over, as a broken step always was: a mask with one
            // unfinished step is still a mask.
            if (Bound(path, type, input, effect.Inputs, layerId, channel, cursor.Port, true) is not { } node) {
                return cursor;
            }

            foreach (var (port, value) in effect.Values) {
                // ⚠ Derived from the type rather than from a list per effect, and what it is
                // protecting is the image wire. A value named 'Input' written to `Colour/Levels`
                // would replace the mask this effect is adjusting with a constant, and the picture
                // would be the effect over nothing at all.
                if (string.Equals(port, input.Name, StringComparison.Ordinal)
                    || type.Port(port, PortDirection.Input) is not { } declared
                    || declared.Kind == PortKind.Image) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layerId,
                            $"'{port}' is not a number '{path}' takes, so the value is dropped rather than written "
                            + "to a port that might be the image the effect reads."
                        )
                    );

                    continue;
                }

                node.SetValue(port, value);
            }

            foreach (var (setting, value) in effect.Texts) {
                if (type.Setting(setting) is null) {
                    problems.Add(
                        LayerStackProblem.Warning(
                            layerId,
                            $"'{setting}' is not a setting '{path}' declares, so it is dropped."
                        )
                    );

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
        ///     anchor comes back as the node that folded it, with its edge deferred into
        ///     <see cref="anchors" />. ⚠ This named <c>Into</c>, which nothing in this file has ever
        ///     declared — the first broken link found here after #821 turned the instrument on.
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
                        Unfilled(
                            layerId,
                            "This layer's mask is a texture and names no image. A bitmap with no reference is "
                            + "refused at compile time rather than filled with black."
                        );

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
                    // ⚠ A mask names its channel — Patina #111. Read in the channel being built, one anchor mask was a
                    // different picture under every channel the layer writes.
                    if (Read(entry.Anchor, layerId, false) is not { } read) {
                        return null;
                    }

                    return new(Anchored(entry.Anchor.Anchor, read, layerId), true);

                case LayerMaskSource.Bake: {
                    if (Drafting && entry.Map.Trim().Length == 0) {
                        Unfilled(layerId, "This layer's mask is a mesh map and names none.");

                        return null;
                    }

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
                        Unfilled(
                            layerId,
                            "This layer's mask is a generator and names no compound. A generator is a published "
                            + ".vxtexgraph reading the mesh maps by usage, named by its path — 'Generators/Dirt'."
                        );

                        return null;
                    }

                    if (!Library.TryGet(path, out var type)) {
                        problems.Add(
                            LayerStackProblem.Refusal(
                                layerId,
                                $"This layer's mask names the generator '{path}', which is not a node type this "
                                + "project has. The shipped compounds are published from this assembly and a "
                                + "project's own from its compound folder; a path in neither binds nothing."
                            )
                        );

                        return null;
                    }

                    if (OnlyImage(type, PortDirection.Output) is not { } output) {
                        problems.Add(
                            LayerStackProblem.Refusal(
                                layerId,
                                $"The generator '{path}' does not have exactly one image output, so which of its "
                                + "results is the mask is not something this file can decide."
                            )
                        );

                        return null;
                    }

                    var node = Add(path);

                    Fed(node, type, path, entry.GeneratorInputs, layerId, false);

                    foreach (var (port, value) in entry.GeneratorValues) {
                        if (type.Port(port, PortDirection.Input) is not { Kind: not PortKind.Image }) {
                            problems.Add(
                                LayerStackProblem.Warning(
                                    layerId,
                                    $"'{port}' is not a number '{path}' takes, so the value is dropped."
                                )
                            );

                            continue;
                        }

                        node.SetValue(port, value);
                    }

                    foreach (var (setting, value) in entry.GeneratorTexts) {
                        if (type.Setting(setting) is null) {
                            problems.Add(
                                LayerStackProblem.Warning(
                                    layerId,
                                    $"'{setting}' is not a setting '{path}' declares, so it is dropped."
                                )
                            );

                            continue;
                        }

                        node.SetText(setting, value);
                    }

                    // ⚠ Not opaque: a generator is a published compound, and nothing constrains what
                    // it writes into alpha. #874.
                    return new(new PortRef(node.Id, output.Name), false);
                }

                case LayerMaskSource.Paint: {
                    // ⚠ The same node the texture mask emits, and the same reading of it: a mask is
                    // read for its *red*, which the `Colour/Channel Shuffle` above this does. So a
                    // mask canvas is painted in white and its coverage is the value of the channel,
                    // never an alpha — a painted mask whose alpha carried the coverage would mask
                    // nothing, for the exact reason the texture case above gives for reading the red
                    // channel rather than the alpha. ⚠ This said "the reason `Into` gives", and
                    // nothing in this file has ever declared an `Into` — the same dead name the
                    // cref on `MaskSource` carried, which is what a rename leaves behind when no
                    // instrument can see either half.
                    var painted = entry.Paint.Trim();

                    if (painted.Length == 0) {
                        // ⚠ Not worth saying in a draft: it is what a mask nobody has stroked yet looks like.
                        if (!Drafting) {
                            problems.Add(
                                LayerStackProblem.Warning(
                                    layerId,
                                    "This layer's mask is painted and names no .vxpaint, so it has nothing to read. A "
                                    + "canvas is written the first time the mask is painted on; until then the entry "
                                    + "contributes nothing."
                                )
                            );
                        }

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

        /// <summary>The image output a material writes one channel through, by that channel's usage.</summary>
        static PortDefinition? ByUsage(NodeTypeDefinition type, string usage) {
            foreach (var port in type.Ports) {
                if (port is { Direction: PortDirection.Output, Kind: PortKind.Image }
                    && string.Equals(port.Name, usage, StringComparison.Ordinal)) {
                    return port;
                }
            }

            return null;
        }

        static bool HasImageOutput(NodeTypeDefinition type) {
            foreach (var port in type.Ports) {
                if (port is { Direction: PortDirection.Output, Kind: PortKind.Image }) {
                    return true;
                }
            }

            return false;
        }

        static bool HasInput(NodeTypeDefinition type, string name) {
            foreach (var port in type.Ports) {
                if (port.Direction == PortDirection.Input
                    && port.Kind != PortKind.Image
                    && string.Equals(port.Name, name, StringComparison.Ordinal)) {
                    return true;
                }
            }

            return false;
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
        internal static int Images(NodeTypeDefinition type, PortDirection direction) {
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
                        Unfilled(layer, Missing(channel, anchor));
                    }

                    // White, which is the mask contributing nothing: the shuffles are already in the
                    // graph, and left unwired they would fail the compile a draft exists to keep.
                    if (Drafting) {
                        var nothing = Add("Source/Uniform");

                        nothing.SetValue("Colour", 1f, 1f, 1f, 1f);
                        graph.Connect(new(nothing.Id, "Out"), to);
                    }

                    continue;
                }

                if (graph.TryConnect(from, to, out _, out var error)) {
                    continue;
                }

                if (!reported.Add((channel, anchor, layer))) {
                    continue;
                }

                problems.Add(
                    LayerStackProblem.Refusal(
                        layer,
                        $"The anchor point '{anchor}' is refused: {GraphInvariants.Describe(error, from, to)} "
                        + "⚠ Reading an anchor point at or above the layer that reads it is a loop, and the graph model "
                        + "is what says so — the stack does not check for one itself."
                    )
                );
            }
        }

        /// <summary>Wires every input that reads the stack beneath its layer in a channel walked after its own.</summary>
        /// <remarks>
        ///     ⚠ <b>A loop is the graph model's to refuse, as an anchor's is.</b> A filter reading height beneath it and
        ///     writing normal is no loop; one whose height beneath reads its own normal through an anchor is, and
        ///     <see cref="NodeGraphModel.TryConnect" /> says so on its own terms.
        /// </remarks>
        void Beneaths() {
            HashSet<(string Channel, string Layer)> reported = [];

            foreach (var (channel, layer, to) in beneath) {
                if (!below.TryGetValue((channel, layer), out var from)) {
                    // Every layer is walked in every channel of the set, so this is a channel the set does not have, and
                    // `Source` already said so.
                    continue;
                }

                if (graph.TryConnect(from, to, out _, out var error) || !reported.Add((channel, layer))) {
                    continue;
                }

                problems.Add(
                    LayerStackProblem.Refusal(
                        layer,
                        $"This layer reads the layers beneath it in '{channel}', and that is refused: "
                        + $"{GraphInvariants.Describe(error, from, to)} The layers beneath it in '{channel}' read "
                        + "what this layer makes."
                    )
                );
            }
        }

        /// <summary>What an anchor onto a layer with no result is told.</summary>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>Two sentences, because under <see cref="Weighing" /> the first one is a lie</b>
        ///         — <a href="https://github.com/Rikarin/Vixen/issues/1137">#1137</a>. A coverage build
        ///         compiles a set holding <em>one</em> layer, so the anchored layer is missing from
        ///         that set however plainly it is present in the stack the artist is looking at. "Not
        ///         in this set" then reads as an accusation against a file that is correct, and an
        ///         anchor mask is not exotic: "mask this layer by what the layer below covers" is one
        ///         of the two things anchors were added for.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Still a refusal, and the narrow set is still the right build.</b> A coverage is
        ///         the picture path's own arithmetic run over one layer, which is what makes it the
        ///         same masks, the same effects, the same folding and the same <c>Blend</c> kernel a
        ///         frame goes through. Keeping every layer so an anchor resolves would not merely cost
        ///         a graph per layer: under <see cref="Weighing" /> a layer's result is a
        ///         <em>coverage</em> rather than a colour, so the anchor would read a different number
        ///         from the one the picture's anchor reads, and the two products of one stack would
        ///         disagree in a way no message could explain. Saying which verb cannot answer is the
        ///         honest half.
        ///     </para>
        /// </remarks>
        string Missing(string channel, string anchor) =>
            Weighing
                ? $"This layer reads the anchor point '{anchor}', and a coverage is resolved one layer at a "
                + "time — so there is no other layer here for the anchor to read, whatever the stack holds. "
                + "⚠ The stack itself is fine and its picture bakes: it is the weights that cannot be taken. "
                + "Replace the anchor with a mask of its own, or group the two layers so the pair weighs as one."
                : !declared.Contains(anchor)
                    ? $"No layer of texture set '{set.Name}' publishes an anchor point named '{anchor}'."
                    : $"The anchor point '{anchor}' is never reached in channel '{channel}': it is inside a group that "
                    + "is disabled, flattened or restricted to other channels, so the stack has no picture at that "
                    + "point to read.";

        /// <summary>
        ///     The opacity a whole constant mask folds into, or <see langword="null" /> when the mask
        ///     has to be compiled.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             <a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>, and the
        ///             objection that held it back is gone rather than answered.
        ///         </b>
        ///         The fold was refused
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
        ///         ⚠
        ///         <b>
        ///             Which is why both numbers are range-checked, and why that is not
        ///             defensiveness.
        ///         </b>
        ///         <c>saturate</c> is the identity only inside the unit interval,
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
        ///         <b>
        ///             The other half of #789's reason for waiting was that folding makes the mask path
        ///             unreachable for the one case a device-free test can build, "which is how a mask
        ///             that never worked ships green".
        ///         </b>
        ///         That is no longer true: an anchor mask, a bake
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
        ///     <para>
        ///         ⚠ <b>Only an <em>authored</em> colour's alpha is folded.</b> A constant fill with no
        ///         entry for this channel no longer compiles at all (#807 · 2), so reading the channel's
        ///         base default here would be folding the alpha of a colour that never reaches the
        ///         graph — a number taken from a layer that is not there.
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             Under <see cref="Weighing" /> the question has no channel, so neither does the
        ///             answer
        ///         </b>
        ///         — <a href="https://github.com/Rikarin/Vixen/issues/1138">#1138</a>.
        ///         <c>LayerStackSplat.Coverage</c> compiles a set whose one channel is called
        ///         <c>mask</c>, and a fill's <c>Values</c> are keyed by <em>real</em> usages, so the
        ///         lookup below misses and the alpha was not folded: a fill whose base colour is
        ///         <c>[…, 0.5]</c> covered half the texel in the picture and weighed <b>one</b> in the
        ///         splat map. Two products of one stack disagreeing about the same stack, and the
        ///         failure is a look change with a bake in front of it — a half-strength moss layer
        ///         that comes back solid once the material is layered. <see cref="Covers" /> above is
        ///         already channel-blind for the same reason, so this is the two halves of one
        ///         question answered the same way.
        ///     </para>
        /// </remarks>
        float Opacity(LayerAsset layer, ChannelAsset channel) {
            var opacity = layer.Opacity;

            if (layer.Kind != LayerKind.Fill || layer.Fill != LayerFillSource.Constant) {
                return opacity;
            }

            if (Weighing) {
                return opacity * Covering(layer);
            }

            if (!layer.Values.TryGetValue(channel.Usage, out var colour)) {
                return opacity;
            }

            return colour.Length == 4 ? opacity * colour[3] : opacity;
        }

        /// <summary>How much of a texel a constant fill covers, across every channel it writes.</summary>
        /// <remarks>
        ///     <para>
        ///         <b>The maximum rather than the mean or the first</b>, because a coverage is one
        ///         number and a fill may author a different alpha per channel. The most of the texel
        ///         this layer takes anywhere is the honest single answer, and it agrees with the
        ///         picture on the ordinary stack, where one alpha is authored or all of them are equal.
        ///     </para>
        ///     <para>
        ///         <b>Every entry, without re-reading the channel enables.</b>
        ///         <c>LayerStackSplat.Coverage</c> clears them on the layer it asks about and on every
        ///         layer beneath it — a layer restricted to <c>baseColor</c> still has a coverage — so
        ///         by the time this runs there is no entry here the layer does not write. ⚠ That used
        ///         to be true of the top layer alone, and <see cref="Composite" /> turning the deeper
        ///         ones away was described here as the other half of the argument rather than as the
        ///         defect it was: a group of restricted children weighed nothing
        ///         (<a href="https://github.com/Rikarin/Vixen/issues/1182">#1182</a>).
        ///     </para>
        ///     <para>
        ///         ⚠
        ///         <b>
        ///             One is what a fill authoring nothing gets, and that is deliberately not a
        ///             change here.
        ///         </b>
        ///         Such a layer contributes to no channel in the picture and still
        ///         weighs one, because <see cref="Covers" /> is channel-blind — a separate question
        ///         about whether a fill that writes nothing is a layer at all, and one this method
        ///         must not answer by returning zero for it.
        ///     </para>
        /// </remarks>
        static float Covering(LayerAsset layer) {
            var alpha = 0f;
            var authored = false;

            foreach (var colour in layer.Values.Values) {
                if (colour.Length != 4) {
                    continue;
                }

                authored = true;
                alpha = MathF.Max(alpha, colour[3]);
            }

            return authored ? alpha : 1f;
        }

        /// <summary>Four numbers, or the opaque black a malformed entry becomes.</summary>
        float[] Colour(float[] value, string usage, string layer) {
            if (value.Length == 4) {
                return value;
            }

            problems.Add(
                LayerStackProblem.Refusal(
                    layer,
                    $"The colour for channel '{usage}' has {value.Length.ToString(CultureInfo.InvariantCulture)} "
                    + "numbers and a colour is four — red, green, blue, alpha, linear."
                )
            );

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
        ///         ⚠
        ///         <b>
        ///             <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a>, and the reason
        ///             it had to land <em>here</em> rather than only in the plan.
        ///         </b>
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
