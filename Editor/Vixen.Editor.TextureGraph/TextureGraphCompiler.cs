// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph;

/// <summary>One map a compiled graph produces: which image it is, and what it is <em>for</em>.</summary>
/// <param name="Usage">
///     <c>baseColor</c> · <c>normal</c> · <c>roughness</c> · <c>metalness</c> · <c>occlusion</c> ·
///     <c>height</c> · <c>emissive</c> · <c>opacity</c> · <c>mask</c> — doc 48 § 4.8's list.
/// </param>
/// <param name="Image">Its index in <see cref="TexturePlan.Images" />.</param>
/// <param name="Node">The <c>Output</c> node that named it, so an author can be sent to it.</param>
/// <remarks>
///     ⚠ <b>Carried by the compiler rather than by the plan, and that is a gap rather than a
///     design.</b> <see cref="TexturePlan.Outputs" /> is a list of indices with no names on it — it
///     is the evaluator's artefact and the evaluator has no use for a usage — so the join between "an
///     image survived" and "it is the roughness map" lives here, and a bake reads it off the
///     compiler. A layer stack compiling to the same plan would need its own copy of this, which is
///     the second list doc 48 § D1 exists to prevent —
///     <a href="https://github.com/Rikarin/Vixen/issues/718">#718</a>.
/// </remarks>
readonly record struct TextureGraphOutput(string Usage, int Image, NodeId Node);

/// <summary>Which image one node's output port wrote.</summary>
/// <param name="Node">
///     The node an author can select — put back through <see cref="NodeGraphInlining" />, so a node
///     that came out of a sub-graph is named by the sub-graph node in the open document.
/// </param>
/// <param name="Port">Its output port.</param>
/// <param name="Image">Its index in <see cref="TexturePlan.Images" />.</param>
/// <remarks>
///     ⚠ <b>Only useful with <see cref="TextureGraphCompiler.PreviewEveryNode" /> set, and that is
///     not a convenience.</b> An image a plan does not keep is freed the moment its last reader has
///     run and its texture is handed to the next image that needs one — that is what makes the pool
///     cheap — so reading one back after the bake gives whatever was written into it afterwards,
///     which is a picture, of the wrong node, with nothing anywhere saying so.
/// </remarks>
readonly record struct TextureGraphNodeImage(NodeId Node, string Port, int Image);

/// <summary>One kernel a graph authored, rather than one this assembly ships.</summary>
/// <param name="Kernel">The shader's name, which is what the op that runs it names.</param>
/// <param name="Source">Its Raven, ready for <c>TextureKernels.Variant</c>'s format rewrite.</param>
/// <param name="Node">The node that wrote it.</param>
/// <remarks>
///     ⚠ <b>Carried by the compiler rather than by the plan, and — exactly as with
///     <see cref="TextureGraphOutput" /> — that is a gap rather than a design.</b> A plan's op names
///     a kernel and <c>TexturePlanEvaluator</c> resolves that name through the assembly's embedded
///     sources, so a plan holding one of these <em>does not evaluate</em>: it throws naming a kernel
///     nothing has. Doc 48 § D6's node is landed to the point the section is actually about — the
///     real Raven compiler, and diagnostics mapped back to the node — and the last wire is
///     <a href="https://github.com/Rikarin/Vixen/issues/729">#729</a>.
/// </remarks>
readonly record struct TextureGraphKernel(string Kernel, string Source, NodeId Node);

/// <summary>
///     A texture graph, as a <see cref="TexturePlan" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Pure, and doc 48 § D5 is explicit about why.</b> Nothing here opens a device, allocates
///         a texture or dispatches anything: compiling a graph is a walk over nodes that appends
///         records to two lists, so it is testable with no provider, cheap enough to run on every
///         edit, and the thing a background bake is handed rather than the thing a background bake
///         is. Every assertion about what a graph <em>means</em> is therefore an assertion about a
///         value, and the one test that needs a GPU is the differential that proves the value is the
///         one the evaluator already draws correctly.
///     </para>
///     <para>
///         <b>Images are allocated per output port and freed by the pool.</b> A node asks for the
///         image it writes and gets a fresh index; nothing here reuses one, because
///         <see cref="TexturePoolSchedule" /> already reads the op order as a liveness and does it
///         better than a compiler could — an image is written exactly once, which is the invariant
///         that makes that possible and the reason a two-pass filter asks for a scratch rather than
///         writing its output twice.
///     </para>
///     <para>
///         ⚠ <b>Grey against colour is decided here and nowhere else.</b>
///         <see cref="PortKinds.Accepts" /> passes every image-to-image wire, deliberately — a
///         <see cref="PortKind" /> carries no format — so doc 48 § Part 4's promotion rule is this
///         class's: a node resolves to the widest thing arriving at its image inputs, a grey feeding
///         one that resolved to colour is splatted by an inserted <c>ChannelShuffle</c>, and a colour
///         arriving at a port that <em>measures</em> is a type error naming that port. It is
///         <see cref="PortKind.Dynamic" />'s widening rule reused rather than a second type system.
///     </para>
/// </remarks>
sealed class TextureGraphCompiler : NodeGraphCompiler<TexturePlan> {
    readonly ImmutableArray<TextureImage>.Builder images = ImmutableArray.CreateBuilder<TextureImage>();
    readonly ImmutableArray<TextureOp>.Builder ops = ImmutableArray.CreateBuilder<TextureOp>();
    readonly List<TextureChannels> channels = [];
    readonly List<TextureGraphOutput> outputs = [];

    /// <summary>Which image an output port's variable names.</summary>
    readonly Dictionary<string, int> imageOf = new(StringComparer.Ordinal);

    /// <summary>The splat inserted for one grey image, so two colour ports fed by it share one op.</summary>
    readonly Dictionary<int, int> promotions = [];

    /// <summary>What each port whose value was written as an expression folded to.</summary>
    readonly Dictionary<(NodeId Node, string Port), float> folded = [];

    /// <summary>Which image each node's output port wrote, in the order they were allocated.</summary>
    readonly List<TextureGraphNodeImage> nodeImages = [];

    /// <summary>The kernels the graph's own nodes wrote, by the name their op gives.</summary>
    readonly List<TextureGraphKernel> kernels = [];

    TextureEmitter emitter = null!;

    /// <summary>The graph being walked — the flattened one, not the author's. See the base class.</summary>
    NodeGraphModel? graph;

    /// <summary>Starts a compiler over a node library.</summary>
    /// <param name="registry">The node types the graph may contain.</param>
    public TextureGraphCompiler(NodeTypeRegistry registry) : base(registry) { }

    /// <summary>The width the graph was <em>authored</em> at, in texels.</summary>
    /// <remarks>
    ///     ⚠ <b>A property of the compiler because the model has nowhere to put it.</b> Doc 48 § D8
    ///     says "the graph declares a base resolution" and <c>NodeGraphModel</c> carries a name, a
    ///     node list and an interface — nothing a number fits in. Until it does, a host sets this and
    ///     a <c>.vxtexgraph</c> cannot round-trip it —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>, which also covers
    ///     <see cref="Seed" />: doc 48 § D5 says the seed is part of the graph, and a seed a host sets
    ///     is a seed that changes between machines.
    /// </remarks>
    public int BaseWidth { get; set; } = 1024;

    /// <summary>The height the graph was authored at.</summary>
    public int BaseHeight { get; set; } = 1024;

    /// <summary>How much bigger this bake is than the resolution the graph was authored at.</summary>
    /// <remarks>
    ///     Passed straight to <see cref="TexturePlan.BakeLevelOffset" />, in the same currency and
    ///     with the same sign: <c>0</c> bakes at the authoring resolution and <c>-2</c> bakes a 1K
    ///     graph at 4K. It reaches the walk as well as the plan, because a node whose dispatch
    ///     <em>count</em> depends on the resolution — a jump flood — has to be told which one.
    /// </remarks>
    public int BakeLevelOffset { get; set; }

    /// <summary>The plan's seed, from which every op's is derived.</summary>
    public uint Seed { get; set; }

    /// <summary>What each output the last compilation produced is for.</summary>
    /// <remarks>
    ///     Read after <see cref="NodeGraphCompiler{T}.Compile" />, the way <c>Inlining</c> is. Empty
    ///     until one has run, and for a graph whose <c>Output</c> nodes all failed.
    /// </remarks>
    public ImmutableArray<TextureGraphOutput> Outputs { get; private set; } = [];

    /// <summary>The graph's exposed parameters: doc 48 § D9's knobs.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A property of the compiler for <see cref="BaseWidth" />'s reason</b> —
    ///         <c>NodeGraphModel</c> carries a name, a node list and an interface, and a parameter
    ///         list fits in none of them
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>). A host sets it, and
    ///         <see cref="TextureGraphParameters.Definition" /> is what turns the same list into the
    ///         settings a <em>containing</em> graph's node shows.
    ///     </para>
    ///     <para>
    ///         What reads it is <see cref="TextureGraphExpressions" />: every scalar port may carry an
    ///         expression over these instead of a number, which is doc 48 § D6's answer to Designer's
    ///         function graphs.
    ///     </para>
    /// </remarks>
    public List<TextureGraphParameter> Parameters { get; } = [];

    /// <summary>What an author or a <c>.vxsmartmat</c> overrode a parameter to, by name.</summary>
    /// <remarks>
    ///     The shape a sub-graph node's <see cref="GraphNode.Texts" /> already has, so overriding a
    ///     published graph's knobs is handing this the node's own texts. A key naming no parameter is
    ///     ignored; a value that does not parse, or falls outside the declared range, is a
    ///     <c>TG0015</c> and the parameter keeps its default.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Arguments { get; set; }

    /// <summary>What each parameter was worth in the last compilation.</summary>
    public IReadOnlyDictionary<string, float> ParameterValues { get; private set; } =
        new Dictionary<string, float>(StringComparer.Ordinal);

    /// <summary>Whether every node's output is kept, so each can be looked at.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what a per-node preview needs, and it is the whole of what it needs from
    ///         the evaluator.</b> Batch 4 recorded that previews would want a device-side path split
    ///         out of <c>Evaluate</c>; they do not. An image the plan keeps is not pooled over, so a
    ///         plan compiled with this set and evaluated once holds <em>every</em> node's picture at
    ///         the end of one bake, and <c>TextureBake.Read</c> already reads any of them back on the
    ///         queue that wrote it. See <see cref="NodeImages" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It also stops <c>TG0005</c> refusing a graph with no <c>Output</c> node</b>,
    ///         because a graph an author is halfway through building is exactly when they want to see
    ///         what a node is producing, and demanding a terminal node first would make previews
    ///         useless in the half hour the graph is being made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it costs a texture per node.</b> Nothing is pooled, so a preview compilation
    ///         is emphatically not the compilation a bake runs — which is why it is a flag on the
    ///         compiler rather than something the evaluator decides.
    ///     </para>
    /// </remarks>
    public bool PreviewEveryNode { get; set; }

    /// <summary>Which image each node's output port wrote in the last compilation.</summary>
    public ImmutableArray<TextureGraphNodeImage> NodeImages { get; private set; } = [];

    /// <summary>The kernels the last compilation's own nodes authored.</summary>
    /// <remarks>
    ///     Empty for every graph containing no <c>Pixel Processor</c>. See
    ///     <see cref="TextureGraphKernel" /> for why a plan holding one does not yet evaluate.
    /// </remarks>
    public ImmutableArray<TextureGraphKernel> Kernels { get; private set; } = [];

    /// <inheritdoc />
    protected override void Begin(NodeGraphModel graph) {
        this.graph = graph;
        images.Clear();
        ops.Clear();
        channels.Clear();
        outputs.Clear();
        imageOf.Clear();
        promotions.Clear();
        folded.Clear();
        nodeImages.Clear();
        kernels.Clear();
        Outputs = [];
        NodeImages = [];
        Kernels = [];

        emitter = new(this);

        Bind(graph);
    }

    /// <summary>Resolves the graph's parameters, and every expression written over them.</summary>
    /// <remarks>
    ///     ⚠ <b>Before the walk and not during it, because one compilation folds every expression at
    ///     once.</b> Raven is asked once per <em>graph</em> — one source holding one <c>const val</c>
    ///     per parameter and one per expression — so a graph of forty expression fields costs one
    ///     parse and one bind rather than forty, and every expression is bound against the same
    ///     parameter declarations in the same order. Folding them node by node during the walk would
    ///     be forty compilations of forty nearly identical files.
    /// </remarks>
    void Bind(NodeGraphModel graph) {
        foreach (var problem in TextureGraphParameters.Check(Parameters)) {
            // Against no node, because a parameter belongs to the graph rather than to any node in
            // it. There is nothing to select and saying so is better than picking one at random.
            Report(new("TG0011", "This graph's parameters do not hold together: " + problem, NodeId.None));
        }

        ParameterValues = TextureGraphParameters.Read(Parameters, Arguments, out var refused);

        foreach (var problem in refused) {
            Report(new("TG0015", problem, NodeId.None, "", NodeSeverity.Warning));
        }

        foreach (var (scope, expressions) in Collect(graph)) {
            var parameters = scope.Length == 0
                ? Parameters
                : (SubGraphSource as ITextureGraphLibrary)?.ParametersOf(scope) ?? [];

            var values = scope.Length == 0
                ? ParameterValues
                : TextureGraphParameters.Read(parameters, null, out _);

            var results = TextureGraphExpressions.Fold(parameters, values, expressions, out var diagnostics);

            foreach (var diagnostic in diagnostics) {
                Report(diagnostic);
            }

            foreach (var result in results) {
                if (result.Folded) {
                    folded[(result.Node, result.Port)] = result.Value;
                }
            }
        }
    }

    /// <summary>Every port of the graph whose value was written as an expression, by scope.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Walked in <c>Ordered</c>'s order and each node's keys sorted</b>, because the
    ///         order decides which line of the generated source each expression lands on — and a line
    ///         number is what a Raven complaint is mapped back through. A dictionary's own order
    ///         would make the mapping depend on hashing, which is stable within a run and not across
    ///         them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Grouped by which graph each node was <em>written</em> in, not by which graph is
    ///         being compiled.</b> An inlined node's expression was authored against the sub-graph's
    ///         own parameters, and after <see cref="SubGraphs.Flatten" /> it sits in a graph whose
    ///         parameters are somebody else's — so binding the whole flattened graph against one
    ///         parameter list would report "undefined name" for every published graph that has a knob,
    ///         or, worse, silently bind to a containing parameter of the same name and produce a
    ///         picture. <see cref="NodeGraphInlining" /> already says which graph each node came out
    ///         of; the scope is that path, and the empty string is the author's own graph.
    ///     </para>
    /// </remarks>
    List<(string Scope, List<TextureExpression> Expressions)> Collect(NodeGraphModel graph) {
        List<(string Scope, List<TextureExpression> Expressions)> scopes = [];

        List<TextureExpression> For(string scope) {
            foreach (var (name, expressions) in scopes) {
                if (string.Equals(name, scope, StringComparison.Ordinal)) {
                    return expressions;
                }
            }

            List<TextureExpression> made = [];

            scopes.Add((scope, made));

            return made;
        }

        foreach (var node in graph.Ordered()) {
            if (!Registry.TryGet(node.Type, out var definition)) {
                continue;
            }

            foreach (var key in node.Texts.Keys.Order(StringComparer.Ordinal)) {
                if (!TextureGraphExpressions.IsExpression(key, out var port)) {
                    continue;
                }

                if (definition.Port(port, PortDirection.Input) is not { } declared) {
                    Report(new(
                        "TG0016",
                        $"An expression is stored for '{port}', which this node has no input called. It was "
                        + "written against a version of the node type that had one.",
                        node.Id,
                        port
                    ));

                    continue;
                }

                if (declared.Kind is PortKind.Image or PortKind.Flow or PortKind.Texture or PortKind.Sampler) {
                    // ⚠ Refused rather than folded and thrown away. An expression is one float, and
                    // there is no number an image port could take — see `Constant`. Accepting it here
                    // would produce a field an author can type into whose value nothing ever reads.
                    Report(new(
                        "TG0016",
                        $"'{port}' carries a {declared.Kind} and an expression is one number. A node's image "
                        + "inputs are wired, not computed.",
                        node.Id,
                        port
                    ));

                    continue;
                }

                // ⚠ An empty field is *not* an expression, and this is where that is decided. Clearing
                // the box is how an author goes back to the number typed on the port, and a UI that
                // wrote the empty string back would otherwise turn every cleared field into a
                // diagnostic — which is a refusal an author cannot act on, because the thing it asks
                // them to do is what they just did.
                if (string.IsNullOrWhiteSpace(node.Texts[key])) {
                    continue;
                }

                For(Inlining.TryGet(node.Id, out var origin) ? origin.Type : "")
                    .Add(new(node.Id, port, node.Texts[key]));
            }
        }

        return scopes;
    }

    /// <inheritdoc />
    protected override void Visit(GraphNode node, NodeTypeDefinition definition, Node instance, NodeBinding binding) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(definition);

        if (instance is not TextureNode texture) {
            // ⚠ A sub-graph node that reaches the walk is a sub-graph that was not inlined, and
            // saying "it is not a texture node" of one is true and useless: it is a published graph,
            // it is exactly the right thing to have on the canvas, and what is missing is the
            // library. That is a host's mistake rather than an author's, and it is worth its own
            // sentence — this is the failure a compiler with no `SubGraphSource` produces for every
            // sub-graph in every graph, all at once.
            Report(new(
                "TG0001",
                instance is SubGraphNode
                    ? $"'{definition.Path}' is a published graph, and nothing inlined it. The compiler was "
                      + "given no library to resolve sub-graphs through, so its contents never reached this plan."
                    : $"'{definition.Path}' is in this graph's library but is not a texture node, so there is "
                      + "nothing it could dispatch.",
                node.Id
            ));

            return;
        }

        emitter.Enter(node, binding, Resolve(node, definition));
        texture.Compile(emitter);
    }

    /// <inheritdoc />
    protected override TexturePlan? Finish(NodeGraphModel graph) {
        if (outputs.Count == 0 && !PreviewEveryNode) {
            Report(new(
                "TG0005",
                "This graph has no Output node, so everything it computes is freed before anything can "
                + "read it. Add one from the Output category.",
                NodeId.None
            ));

            return null;
        }

        // Deduplicated and in one order, because `Outputs` is what the pool reads as "never reuse
        // this slot" and an image named twice would be a second entry saying the same thing.
        List<int> kept = [];

        foreach (var output in outputs) {
            if (!kept.Contains(output.Image)) {
                kept.Add(output.Image);
            }
        }

        if (PreviewEveryNode) {
            foreach (var written in nodeImages) {
                if (!kept.Contains(written.Image)) {
                    kept.Add(written.Image);
                }
            }
        }

        var plan = new TexturePlan {
            BaseWidth = BaseWidth,
            BaseHeight = BaseHeight,
            BakeLevelOffset = BakeLevelOffset,
            Seed = Seed,
            Images = images.ToImmutable(),
            Ops = ops.ToImmutable(),
            Outputs = [.. kept]
        };

        // ⚠ The plan's own refusals, said as diagnostics rather than left for the evaluator to throw
        // at bake time. Every one of them is a compiler bug rather than an author's mistake — an
        // image read before it is written, an op writing an image twice — and a message that names
        // no node is still very much better than an exception three frames away in a background
        // task. Ask what this prints on the day the compiler is wrong: a `TG0009` per problem,
        // rather than a plan that validates because nothing checked it.
        var problems = plan.Validate();

        if (problems.Length > 0) {
            foreach (var problem in problems) {
                Report(new("TG0009", "The compiler produced a plan that does not hold together: " + problem, NodeId.None));
            }

            return null;
        }

        Outputs = [.. outputs];
        NodeImages = [.. nodeImages];
        Kernels = [.. kernels];

        return plan;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>An image has no literal, and the empty string is what says so.</b> An unconnected
    ///     image input is a hole — <see cref="PortKind.Image" />'s own remarks say there is nothing to
    ///     type into one — so what reaches the node's field is text nothing reads, and
    ///     <see cref="Read" /> reports the hole against the port rather than inventing a black image
    ///     nobody asked for.
    /// </remarks>
    protected override string Constant(ReadOnlySpan<float> value, PortKind kind) =>
        kind switch {
            PortKind.Image or PortKind.Flow or PortKind.Texture or PortKind.Sampler => "",
            PortKind.Bool => value.Length > 0 && value[0] != 0f ? "true" : "false",
            PortKind.Int => ((int)(value.Length > 0 ? value[0] : 0f)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => Literal.Of(value.Length > 0 ? value[0] : 0f)
        };

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Never called for an image, and that is not an accident of this graph's node set.</b>
    ///     <see cref="NodeGraphCompiler{T}" /> only converts where <see cref="PortKinds.Accepts" />
    ///     said yes and the two kinds <em>differ</em>, and grey against colour is one kind — so the
    ///     one conversion a texture graph actually performs cannot be spelled here. It is
    ///     <see cref="Read" />'s inserted <c>ChannelShuffle</c>, which is a dispatch rather than an
    ///     expression, which is the whole reason doc 48 § Part 4's rule belongs to a compiler.
    /// </remarks>
    protected override string Convert(string expression, PortKind from, PortKind target) => expression;

    /// <summary>Says something about a node, for <see cref="TextureEmitter" />.</summary>
    internal void Say(NodeDiagnostic diagnostic) => Report(diagnostic);

    /// <summary>One axis at this bake's level 0, with <see cref="TexturePlan.SizeOf" />'s clamping.</summary>
    /// <remarks>
    ///     ⚠ <b>The same arithmetic as <c>TexturePlan</c>'s, in a second place, because a node that
    ///     needs it needs it <em>while</em> the plan is being built.</b> A jump flood's dispatch count
    ///     is <c>log2</c> of the image it writes, so <c>Distance</c> has to know the bake's resolution
    ///     before there is a plan to ask. <c>The_size_a_node_was_told_is_the_size_the_plan_reports</c>
    ///     is what keeps the two honest.
    /// </remarks>
    internal int Extent(int baseExtent) {
        var level = BakeLevelOffset;

        return level >= 0
            ? Math.Max(1, baseExtent >> Math.Min(level, 31))
            : (int)Math.Min(TexturePlan.MaxExtent, (long)baseExtent << Math.Min(-level, 31));
    }

    /// <summary>What one image carries.</summary>
    internal TextureChannels ChannelsOf(int image) =>
        image >= 0 && image < channels.Count ? channels[image] : TextureChannels.Grey;

    /// <summary>Appends one dispatch, unless a diagnostic has already made it meaningless.</summary>
    /// <remarks>
    ///     ⚠ <b>An op naming a negative image is dropped rather than appended.</b> A node whose input
    ///     was missing has already been reported against — the plan is withheld whatever this does —
    ///     and appending the op anyway would put an index outside the table into
    ///     <see cref="TexturePlan.Validate" />'s hands, which would then say the compiler is broken
    ///     on top of the message the author can actually act on.
    /// </remarks>
    internal void Dispatch(TextureOp op) {
        ArgumentNullException.ThrowIfNull(op);

        if (op.Output < 0) {
            return;
        }

        foreach (var input in op.Inputs) {
            if (input < 0) {
                return;
            }
        }

        ops.Add(op);
    }

    /// <summary>Keeps an image past the evaluation, under a usage.</summary>
    internal void Keep(GraphNode node, int image, string usage) {
        if (image < 0) {
            return;
        }

        // ⚠ No check that the usage is one of the nine, and no refusal of an empty one. `OutputNode`
        // canonicalises before it gets here and reports what it did not recognise, so a second
        // refusal on this side would be an unreachable branch — which is a claim about a
        // node-library convention rather than about this method, and is stated here rather than
        // written as code nothing can execute.
        foreach (var existing in outputs) {
            if (!string.Equals(existing.Usage, usage, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            Report(new(
                "TG0006",
                $"Two Output nodes both write '{usage}', and a bake writes one file per usage — so one of "
                + $"them is the map and the graph does not say which. The other is {existing.Node}.",
                node.Id,
                "Usage"
            ));

            return;
        }

        outputs.Add(new(usage, image, node.Id));
    }

    /// <summary>Allocates the image one output port carries.</summary>
    internal int Write(GraphNode node, string port, TextureChannels wanted) {
        var image = Allocate(TextureEmitter.FormatOf(wanted), wanted);

        imageOf[Variable(node, port)] = image;

        // ⚠ The identity put back through the inlining, because this is read by something showing a
        // picture *beside a node on a canvas* — and a node that came out of a sub-graph is in no
        // document. `Report` does the same for every diagnostic, and this is the same question.
        nodeImages.Add(new(Inlining.Resolve(node.Id), port, image));

        return image;
    }

    /// <summary>What one port's expression folded to, or null when it carries no expression.</summary>
    /// <param name="node">The node.</param>
    /// <param name="port">The port's name.</param>
    /// <returns>The number, or null.</returns>
    /// <remarks>
    ///     <b>Read by <see cref="TextureEmitter.Number(string,int)" /> before it reads the port's own
    ///     value</b>, so doc 48 § Part 4's "every scalar parameter accepts a Raven expression" costs a
    ///     node nothing and a node library nothing: a node asks for its number the way it always did.
    /// </remarks>
    internal float? Expression(GraphNode node, string port) =>
        node is not null && folded.TryGetValue((node.Id, port), out var value) ? value : null;

    /// <summary>Records a kernel a node authored, so a host can find its source.</summary>
    /// <param name="node">The node that wrote it.</param>
    /// <param name="kernel">The shader's name, which its op names.</param>
    /// <param name="source">The Raven.</param>
    /// <remarks>
    ///     ⚠ <b>Two nodes whose expressions are identical write one kernel, because the name is
    ///     derived from the source.</b> That is what stops a graph with eight identical Pixel
    ///     Processors compiling eight identical modules, and it is why this is a set rather than a
    ///     list keyed by node.
    /// </remarks>
    internal void Declare(GraphNode node, string kernel, string source) {
        ArgumentNullException.ThrowIfNull(node);

        foreach (var existing in kernels) {
            if (string.Equals(existing.Kernel, kernel, StringComparison.Ordinal)) {
                return;
            }
        }

        kernels.Add(new(kernel, source, Inlining.Resolve(node.Id)));
    }

    /// <summary>Allocates an image no port names.</summary>
    internal int Scratch(TextureFormat format) =>
        Allocate(format, format == TextureFormat.R16Float ? TextureChannels.Grey : TextureChannels.Colour);

    /// <summary>The image arriving at one input, under doc 48 § Part 4's promotion rule.</summary>
    internal int Read(GraphNode node, string port, NodeBinding binding, TextureChannels wanted, bool strict) {
        if (!binding.IsConnected(port)) {
            Report(new(
                "TG0002",
                $"'{port}' has no image connected. There is no literal image an author could type into a "
                + "port instead, so a source node is what fills one.",
                node.Id,
                port
            ));

            return -1;
        }

        if (Upstream(node, port) is not { } source) {
            // ⚠ Said rather than passed over, even though whatever went wrong upstream has already
            // been reported. A node whose output port is an image and which never asked for one is a
            // node-library bug, and it is the exact shape of failure that would otherwise compile to
            // a plan missing a dispatch and nothing to point at.
            Report(new(
                "TG0008",
                $"'{port}' is wired, and whatever feeds it produced no image. Either the node upstream "
                + "failed, or its output is not an image this compiler can carry.",
                node.Id,
                port
            ));

            return -1;
        }

        var arriving = ChannelsOf(source);

        if (arriving == wanted) {
            return source;
        }

        if (strict) {
            Report(new(
                "TG0004",
                $"'{port}' is measured rather than composited, so it takes a single channel and a colour "
                + "arrived at it. There is no luminance a colour and a mask agree on — put a Grayscale "
                + "node in between and say which one this graph means.",
                node.Id,
                port
            ));

            return source;
        }

        // Grey into colour: the splat. Shared, because two ports fed by one grey image want one op —
        // and because a promotion per port would be a texture per port in the pool.
        if (promotions.TryGetValue(source, out var promoted)) {
            return promoted;
        }

        promoted = Allocate(TextureFormat.Rgba16Float, TextureChannels.Colour);

        // ⚠ The same image bound to both of `ChannelShuffle`'s inputs, because the kernel declares two
        // and the evaluator binds an op's images over them positionally. Selector 0 is the first
        // input's red on all three colour lanes; 9 is a constant one, which is the alpha a grey mask
        // read as a colour has to have — a splatted zero would make every promoted image invisible to
        // the very Blend node the promotion exists for.
        ops.Add(
            new() {
                Kernel = "ChannelShuffle",
                Output = promoted,
                Inputs = [source, source],
                Parameters = [new("sourceR", 0f), new("sourceG", 0f), new("sourceB", 0f), new("sourceA", 9f)]
            }
        );

        promotions[source] = promoted;

        return promoted;
    }

    /// <summary>What a node's image ports resolve to: the widest thing arriving at one.</summary>
    /// <remarks>
    ///     <see cref="PortKinds.Resolve" />'s rule, over channels instead of lanes. A node with
    ///     nothing wired is grey for the same reason a node with nothing wired is a float: it has to
    ///     be something, and the narrowest is the one that promotes into anything later.
    /// </remarks>
    TextureChannels Resolve(GraphNode node, NodeTypeDefinition definition) {
        var widest = TextureChannels.Grey;

        foreach (var port in definition.Ports) {
            if (port is not { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                continue;
            }

            // Read off the graph rather than off the binding, which is not built yet — the same
            // arrangement `NodeGraphCompiler.Resolve` makes for a dynamic port, and sound for the
            // same reason: the walk is in dependency order, so whatever feeds this port has already
            // allocated its image.
            if (Upstream(node, port.Name) is not { } image) {
                continue;
            }

            var arriving = ChannelsOf(image);

            if (arriving > widest) {
                widest = arriving;
            }
        }

        return widest;
    }

    /// <summary>The image feeding one input, or null when nothing usable does.</summary>
    int? Upstream(GraphNode node, string port) =>
        graph?.Source(new(node.Id, port)) is { } source
        && graph.TryGet(source.Node, out var from)
        && imageOf.TryGetValue(Variable(from, source.Port), out var image)
            ? image
            : null;

    int Allocate(TextureFormat format, TextureChannels carried) {
        images.Add(new(format));
        channels.Add(carried);

        return images.Count - 1;
    }
}
