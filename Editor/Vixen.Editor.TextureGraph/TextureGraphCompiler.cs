// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
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
public readonly record struct TextureGraphOutput(string Usage, int Image, NodeId Node);

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
public readonly record struct TextureGraphNodeImage(NodeId Node, string Port, int Image);

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
public readonly record struct TextureGraphKernel(string Kernel, string Source, NodeId Node);

/// <summary>One external image a compiled graph needs, and what fills it.</summary>
/// <param name="Image">Its index in <see cref="TexturePlan.Images" />. The entry is marked external.</param>
/// <param name="Node">The node that asked for it, so an author can be sent to it.</param>
/// <param name="Asset">
///     What the graph references, when the picture is somebody else's — an imported bitmap. Empty when
///     the node baked <see cref="Texels" /> itself.
/// </param>
/// <param name="Width">The picture's width in texels, or <c>0</c> when only the host knows it.</param>
/// <param name="Height">Its height.</param>
/// <param name="Texels">
///     The bytes, tightly packed, top row first, in the image's own <see cref="TextureImage.Format" />
///     — exactly what <c>TextureUploads.Add</c> takes. Empty when <paramref name="Asset" /> names the
///     picture instead.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>Carried by the compiler rather than by the plan, for
///         <see cref="TextureGraphOutput" />'s reason and not a new one.</b> A plan's external image
///         says "the caller supplies this one" and deliberately says nothing about <em>what</em> — that
///         is what lets the same plan be re-evaluated over a different bitmap without recompiling. So
///         the join between "image 3 is external" and "it is the gradient this node baked" lives here,
///         and a bake reads it off the compiler to build the dictionary
///         <c>TexturePlanEvaluator.Evaluate</c> already takes.
///     </para>
///     <para>
///         <b>Two shapes because there are two kinds of caller-supplied picture, and only one of them
///         can be produced by a pure compilation.</b> A ramp and a curve table are baked on the CPU
///         from the control an artist dragged — <see cref="TextureRamp" /> — so the compiler carries
///         the bytes and a host needs no knowledge at all. An imported image is an asset: resolving it
///         means an <c>AssetDatabase</c>, which a compiler that runs on every edit must not touch, so
///         what crosses is the reference and the host does the reading.
///     </para>
/// </remarks>
public readonly record struct TextureGraphExternal(
    int Image,
    NodeId Node,
    string Asset,
    int Width,
    int Height,
    ImmutableArray<byte> Texels
);

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
public sealed class TextureGraphCompiler : NodeGraphCompiler<TexturePlan> {
    readonly ImmutableArray<TextureImage>.Builder images = ImmutableArray.CreateBuilder<TextureImage>();
    readonly ImmutableArray<TextureOp>.Builder ops = ImmutableArray.CreateBuilder<TextureOp>();
    readonly List<TextureChannels> channels = [];
    readonly List<TextureGraphOutput> outputs = [];
    readonly List<TextureGraphExternal> externals = [];

    /// <summary>Which image an output port's variable names.</summary>
    readonly Dictionary<string, int> imageOf = new(StringComparer.Ordinal);

    /// <summary>How many ops each node has dispatched, which is the ordinal in its op's identity.</summary>
    /// <remarks>
    ///     ⚠ <b>Per node rather than one running counter, which would be the op index again.</b> See
    ///     <see cref="Identify" /> — <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a>.
    /// </remarks>
    readonly Dictionary<NodeId, int> emitted = [];

    /// <summary>The splat inserted for one grey image, so two colour ports fed by it share one op.</summary>
    readonly Dictionary<int, int> promotions = [];

    /// <summary>The resample inserted to bring one image to one level, shared the same way.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="promotions" />' twin over the other axis, and the second half of
    ///     <a href="https://github.com/Rikarin/Vixen/issues/779">#779</a>.</b> A node's images are
    ///     the size of the <em>finest</em> thing arriving at it, so an input coarser than that has to
    ///     be brought up — every pointwise kernel reads coordinate-for-coordinate with a clamp, and
    ///     a mismatch there is three quarters of an edge smear rather than an error.
    /// </remarks>
    readonly Dictionary<(int Image, int Level), int> rescales = [];

    /// <summary>What each port whose value was written as an expression folded to.</summary>
    readonly Dictionary<(NodeId Node, string Port), float> folded = [];

    /// <summary>Which image each node's output port wrote, in the order they were allocated.</summary>
    readonly List<TextureGraphNodeImage> nodeImages = [];

    /// <summary>The kernels the graph's own nodes wrote, by the name their op gives.</summary>
    readonly List<TextureGraphKernel> kernels = [];

    /// <summary>What this compilation is actually using, once the graph has had its say.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside the properties rather than in them, because <see cref="Adopt" /> used to write
    ///     the graph's declarations back into the host's own fields.</b> A compiler is reusable — the
    ///     preview pane keeps one and hands it every graph in the document — so a graph declaring a
    ///     seed left that seed on the compiler, and the next graph, which declared nothing and was
    ///     therefore promised the host's number, drew with somebody else's. The properties are the
    ///     host's input and stay it; what a compilation used is on the plan.
    /// </remarks>
    readonly List<TextureGraphParameter> declared = [];

    TextureEmitter emitter = null!;

    // ⚠ Zero until `Adopt` has run, rather than a second copy of `BaseWidth`'s default. `Begin`
    // calls it unconditionally, so the only way to read these unset is a compilation that never
    // started — and a plan with a base of 0×0 is refused by `TexturePlan.Validate` in those words,
    // which is a better failure than a plausible 1024 nobody asked for.
    int authoredWidth;
    int authoredHeight;
    uint authoredSeed;

    /// <summary>The graph being walked — the flattened one, not the author's. See the base class.</summary>
    NodeGraphModel? graph;

    /// <summary>Starts a compiler over a node library.</summary>
    /// <param name="registry">The node types the graph may contain.</param>
    public TextureGraphCompiler(NodeTypeRegistry registry) : base(registry) { }

    /// <summary>The width the graph was <em>authored</em> at, in texels, when the graph says nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The host's answer, and the graph's own declaration wins over it</b> —
    ///         <see cref="Adopt" />, and <c>NodeGraphModel.Settings</c> is where a file keeps it since
    ///         <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>. Doc 48 § D8 says "the
    ///         graph declares a base resolution"; this is what a preview asks for and what a graph
    ///         written in code gets.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An input only.</b> Compiling does not write a graph's declaration back into it —
    ///         a compiler is reusable, and a number left here by one document is a number the next
    ///         one silently inherits. What a compilation actually used is on the plan:
    ///         <see cref="TexturePlan.BaseWidth" />, <see cref="TexturePlan.BaseHeight" /> and
    ///         <see cref="TexturePlan.Seed" />.
    ///     </para>
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

    /// <summary>The exposed parameters a host declares, for a graph that declares none of its own.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The host's list, for <see cref="BaseWidth" />'s reason and with the same
    ///         precedence:</b> a graph carrying its own — <c>NodeGraphModel.Parameters</c>, since
    ///         <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a> — replaces it for that
    ///         compilation and does not replace what is here.
    ///         <see cref="TextureGraphParameters.Definition" /> is what turns such a list into the
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

    /// <summary>What fills each external image the last compilation asked for.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a bake turns into <c>TexturePlanEvaluator.Evaluate</c>'s externals.</b> Every
    ///         entry whose <see cref="TextureGraphExternal.Texels" /> is filled can be uploaded with no
    ///         further knowledge — <c>TextureGraphExternals.Upload</c> is that walk — and an entry that
    ///         names an <see cref="TextureGraphExternal.Asset" /> is one only a host with an asset
    ///         database can resolve.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A plan with an entry here cannot be evaluated without it.</b>
    ///         <c>ExternalViews</c> refuses an external image no texture was supplied for, which is
    ///         the right refusal and is thrown at bake time — so a host that ignores this list gets an
    ///         exception rather than a picture.
    ///     </para>
    /// </remarks>
    public ImmutableArray<TextureGraphExternal> Externals { get; private set; } = [];

    /// <inheritdoc />
    protected override void Begin(NodeGraphModel graph) {
        this.graph = graph;
        images.Clear();
        ops.Clear();
        channels.Clear();
        outputs.Clear();
        externals.Clear();
        imageOf.Clear();
        promotions.Clear();
        rescales.Clear();
        folded.Clear();
        nodeImages.Clear();
        kernels.Clear();
        Outputs = [];
        NodeImages = [];
        Kernels = [];
        Externals = [];

        emitter = new(this);

        Adopt(graph);
        Bind(graph);
    }

    /// <summary>Takes what the graph declares about itself over what the host guessed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 48 § D8's "the graph declares a base resolution" and § D5's "the seed is part
    ///         of the graph", which until <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>
    ///         were properties of whoever constructed this class.</b> A saved <c>.vxtexgraph</c>
    ///         therefore came back at the host's default — and the seed is the sharper half, because a
    ///         resolution that reopened wrong is visible and a seed that reopened different is simply
    ///         a different picture, on another machine, of a material somebody signed off.
    ///     </para>
    ///     <para>
    ///         <b>A graph that declares nothing keeps the host's values</b>, which is every graph
    ///         built in code and every one written before the field existed. So this is additive: the
    ///         properties are still the way a preview asks for 256×256, and the file wins where the
    ///         file has an opinion.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it reads them rather than writing them, which the first version of this did
    ///         not.</b> Assigning the answer back into <see cref="BaseWidth" />, <see cref="Seed" />
    ///         and <see cref="Parameters" /> made the sentence above true exactly once per compiler:
    ///         a document declaring a seed left it behind, and the next graph — promised the host's
    ///         number because it declares nothing — drew with somebody else's. A compiler is
    ///         reusable, so what one compilation resolved lives for that compilation and reaches the
    ///         caller on the plan.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bake level offset is deliberately <em>not</em> read.</b> It says how big this
    ///         run is making the material, which is a decision of the run and not a property of the
    ///         graph — a saved one would be somebody's preview resolution baked into the asset.
    ///     </para>
    /// </remarks>
    void Adopt(NodeGraphModel graph) {
        authoredWidth = TextureGraphSettings.Extent(graph, TextureGraphSettings.BaseWidth, BaseWidth, out var width);
        authoredHeight = TextureGraphSettings.Extent(graph, TextureGraphSettings.BaseHeight, BaseHeight, out var height);
        authoredSeed = TextureGraphSettings.SeedOf(graph, Seed, out var seed);

        foreach (var problem in new[] { width, height, seed }) {
            if (problem is not null) {
                // Against no node, because it is the graph's own declaration — the same choice
                // `Bind` makes for a parameter list that does not hold together.
                Report(new(TextureDiagnostics.GraphSettingIgnored, problem, NodeId.None, "", NodeSeverity.Warning));
            }
        }

        declared.Clear();

        // ⚠ Replaced rather than merged. Two lists of knobs under one name is a graph whose
        // parameter means whichever the walk reached first, and a host that set some of its own is
        // a host that has not been updated — the file is the declaration.
        declared.AddRange(
            graph.Parameters.Count == 0 ? Parameters : TextureGraphParameters.Declared(graph.Parameters)
        );
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
        foreach (var problem in TextureGraphParameters.Check(declared)) {
            // Against no node, because a parameter belongs to the graph rather than to any node in
            // it. There is nothing to select and saying so is better than picking one at random.
            Report(new(
                TextureDiagnostics.BuilderRefusedTheNumbers,
                "This graph's parameters do not hold together: " + problem,
                NodeId.None
            ));
        }

        ParameterValues = TextureGraphParameters.Read(declared, Arguments, out var refused);

        foreach (var problem in refused) {
            Report(new(TextureDiagnostics.ParameterOverrideIgnored, problem, NodeId.None, "", NodeSeverity.Warning));
        }

        foreach (var (scope, expressions) in Collect(graph)) {
            var parameters = scope.Length == 0
                ? declared
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
    ///         own parameters, and after
    ///         <see cref="SubGraphs.Flatten(NodeGraphModel,ISubGraphSource,out IReadOnlyList{NodeDiagnostic})" />
    ///         it sits in a graph whose parameters are somebody else's — so binding the whole
    ///         flattened graph against one
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
                        TextureDiagnostics.ExpressionOnAPortThatTakesNone,
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
                        TextureDiagnostics.ExpressionOnAPortThatTakesNone,
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
                TextureDiagnostics.NothingToCompile,
                instance is SubGraphNode
                    ? $"'{definition.Path}' is a published graph, and nothing inlined it. The compiler was "
                      + "given no library to resolve sub-graphs through, so its contents never reached this plan."
                    : $"'{definition.Path}' is in this graph's library but is not a texture node, so there is "
                      + "nothing it could dispatch.",
                node.Id
            ));

            return;
        }

        emitter.Enter(node, binding, Resolve(node, definition, out var level), level);
        texture.Compile(emitter);
    }

    /// <inheritdoc />
    protected override TexturePlan? Finish(NodeGraphModel graph) {
        if (outputs.Count == 0 && !PreviewEveryNode) {
            Report(new(
                TextureDiagnostics.NoOutputNode,
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

        var authored = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var kernel in kernels) {
            authored[kernel.Kernel] = kernel.Source;
        }

        var plan = new TexturePlan {
            BaseWidth = authoredWidth,
            BaseHeight = authoredHeight,
            BakeLevelOffset = BakeLevelOffset,
            Seed = authoredSeed,
            Images = images.ToImmutable(),
            Ops = ops.ToImmutable(),
            Outputs = [.. kept],

            // ⚠ On the plan, which is what makes a Pixel Processor's op *runnable* — #729. Until
            // this line the generated Raven reached `Kernels` below and nowhere else, so the plan a
            // graph with one compiled to threw at bake time naming a shader nothing had.
            Kernels = authored.ToImmutable()
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
                Report(new(
                    TextureDiagnostics.PlanDoesNotHoldTogether,
                    "The compiler produced a plan that does not hold together: " + problem,
                    NodeId.None
                ));
            }

            return null;
        }

        Outputs = [.. outputs];
        NodeImages = [.. nodeImages];
        Kernels = [.. kernels];
        Externals = [.. externals];

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

    /// <summary>The width this compilation's level-0 images are measured against.</summary>
    /// <remarks>
    ///     ⚠ <b>What the <em>graph</em> declared, falling back to <see cref="BaseWidth" />.</b> A node
    ///     asking how big the image it is about to write is has to be told the number the plan will
    ///     report, and since #780 that number is the file's rather than the host's whenever the file
    ///     has an opinion.
    /// </remarks>
    internal int AuthoredWidth => authoredWidth;

    /// <summary>And the height.</summary>
    internal int AuthoredHeight => authoredHeight;

    /// <summary>One axis at a level offset from the base, at this bake.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same arithmetic as <c>TexturePlan</c>'s, in a second place, because a node
    ///         that needs it needs it <em>while</em> the plan is being built.</b> A jump flood's
    ///         dispatch count is <c>log2</c> of the image it writes, so <c>Distance</c> has to know
    ///         the bake's resolution before there is a plan to ask.
    ///         <c>The_size_a_node_was_told_is_the_size_the_plan_reports</c> is what keeps the two
    ///         honest.
    ///     </para>
    ///     <para>
    ///         The image's own offset and the bake's added, which is
    ///         <see cref="TexturePlan.LevelOf" />'s sum — so a node asking for the size of the
    ///         scratch it is about to allocate gets the number the plan will report for it.
    ///     </para>
    /// </remarks>
    internal int Extent(int baseExtent, int levelOffset) {
        var level = BakeLevelOffset + levelOffset;

        return level >= 0
            ? Math.Max(1, baseExtent >> Math.Min(level, 31))
            : (int)Math.Min(TexturePlan.MaxExtent, (long)baseExtent << Math.Min(-level, 31));
    }

    /// <summary>Where one already-allocated image sits, counted in levels from the authoring base.</summary>
    /// <remarks>
    ///     ⚠ <b>The image's own offset, without this bake's</b> — so that a node building a ladder
    ///     <em>relative</em> to its input adds to it rather than to a number the bake has already moved.
    ///     It is <see cref="TextureImage.LevelOffset" /> and not <see cref="TexturePlan.LevelOf" />.
    /// </remarks>
    internal int LevelOf(int image) => image >= 0 && image < images.Count ? images[image].LevelOffset : 0;

    /// <summary>How big one already-allocated image is at this bake, in texels.</summary>
    /// <remarks>
    ///     ⚠ <b>Nominal for an external image</b>, whose size is the caller's picture's and is not
    ///     known here at all — <c>TextureUploads.SizeOf</c> is what remembers that one.
    /// </remarks>
    internal Int2 SizeOf(int image) =>
        new(Extent(authoredWidth, LevelOf(image)), Extent(authoredHeight, LevelOf(image)));

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
    internal void Dispatch(GraphNode node, TextureOp op) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(op);

        if (op.Output < 0) {
            return;
        }

        foreach (var input in op.Inputs) {
            if (input < 0) {
                return;
            }
        }

        // ⚠ Counted before the drops above would have skipped it, or a node whose first op was
        // dropped would hand its second op the first one's identity — and the identity would then
        // depend on whether an unrelated input was missing.
        emitted.TryGetValue(node.Id, out var ordinal);
        emitted[node.Id] = ordinal + 1;

        ops.Add(op with { Identity = Identify(node, ordinal) });
    }

    /// <summary>The stable name of one op: which node emitted it, and its ordinal within that node.</summary>
    /// <param name="node">The node, in the <em>flattened</em> graph.</param>
    /// <param name="ordinal">How many ops that node has already emitted.</param>
    /// <returns>A number to mix into the op's seed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/875">#875</a>: the op's index was
    ///         the seed, and an index is what an insertion moves.</b> A <c>NodeId</c> is not: it is
    ///         written in the <c>.vxtexgraph</c>, <c>NodeGraphModel</c> never reuses one, and adding
    ///         a node hands out a fresh number rather than renumbering the existing ones. So a noise
    ///         keeps its picture when an author wires something in beneath it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An inlined node is named by where it came from and not by the identity it was
    ///         given</b>, because <c>SubGraphs.Flatten</c> numbers inlined nodes from above the outer
    ///         graph's highest id — so adding any node to the author's graph renumbers every node
    ///         inside every compound in it. <see cref="NodeGraphInlining" /> keeps the pair that does
    ///         not move: the sub-graph node it stands for, and its own id inside that compound.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The compound's <em>path</em> is mixed in as well, and the collision it prevents is
    ///         real rather than theoretical.</b> <c>NodeOrigin.Source</c> is the outermost sub-graph
    ///         node however deep the nesting goes, and <c>Inner</c> is an identity in the innermost
    ///         file — so a compound containing a compound, each with a node numbered 3, would give
    ///         two different ops the same name without it.
    ///     </para>
    ///     <para>
    ///         <b>The ordinal is what lets one node emit several.</b> <c>AutoLevels</c> is a reduction
    ///         chain and <c>Distance</c> is a jump flood; every one of their dispatches needs a name
    ///         of its own, and the order a node emits them in is a property of the node rather than
    ///         of the graph around it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Hashed" /> and not <c>string.GetHashCode</c>, and the difference is
    ///         the whole promise.</b> .NET randomises a string's hash per process, so a compound path
    ///         hashed that way would give one material a different picture on every launch — the
    ///         failure <see cref="TexturePlan.SeedFor" />'s "the same on every machine and every run"
    ///         exists to rule out, arriving through the one input that is a string.
    ///     </para>
    /// </remarks>
    uint Identify(GraphNode node, int ordinal) {
        var identity = (uint)node.Id.Value;

        if (Inlining.TryGet(node.Id, out var origin)) {
            identity = unchecked(
                (0x9E3779B9u * (uint)origin.Source.Value)
                ^ (0x85EBCA6Bu * (uint)origin.Inner.Value)
                ^ Hashed(origin.Type)
            );
        }

        // MurmurHash3's finalizer, which is `TexturePlan.SeedFor`'s own mix — used here so that the
        // ordinal and the node are folded together rather than living in separate bit ranges a
        // second hash would then have to separate again.
        var value = unchecked((0xC2B2AE35u * identity) + (uint)ordinal + 1u);

        value ^= value >> 16;
        value = unchecked(value * 0x85EBCA6Bu);
        value ^= value >> 13;
        value = unchecked(value * 0xC2B2AE35u);
        value ^= value >> 16;

        return value;
    }

    /// <summary>A string's hash, the same in every process — FNV-1a over its UTF-16 code units.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The hash.</returns>
    /// <remarks>
    ///     Four lines rather than a dependency, and the property that matters is the one
    ///     <c>string.GetHashCode</c> deliberately does not have: it is seeded per process, so the
    ///     same graph would seed its noise differently on every launch.
    /// </remarks>
    static uint Hashed(string text) {
        var hash = 2166136261u;

        foreach (var character in text) {
            hash = unchecked((hash ^ character) * 16777619u);
        }

        return hash;
    }

    /// <summary>Keeps an image past the evaluation, under a usage.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The terminus is where <a href="https://github.com/Rikarin/Vixen/issues/779">#779</a>'s
    ///         rule stops, and <a href="https://github.com/Rikarin/Vixen/issues/805">#805</a> is what
    ///         it cost not to say so.</b> A node's output is the size of what it reads — everywhere
    ///         inside a graph, which is right. Applied here it made <em>one map</em> a different size
    ///         from its siblings: <c>Resample(Half) → Output("baseColor")</c> beside a bare
    ///         <c>Output("roughness")</c> validated, baked both, and threw out of
    ///         <c>MaterialBake.Extent</c> — a stack trace where a picture was asked for, which is the
    ///         failure mode this tree refuses everywhere else.
    ///     </para>
    ///     <para>
    ///         <b>A map's resolution is declared twice already and neither place is a node.</b>
    ///         <see cref="TexturePlan.BaseWidth" /> is what the file asked for and
    ///         <see cref="TexturePlan.BakeLevelOffset" /> is what the bake asked for on top of it. A
    ///         <c>Space/Resample</c> says where in the graph the <em>work</em> happens — which is
    ///         already how it behaves at every other node, because <see cref="Rescale" /> magnifies a
    ///         half-resolution branch back the instant it meets a base-resolution sibling. The
    ///         terminus is one more such meeting, and what it meets is the texture set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Level zero and not <see cref="authoredWidth" />, and the difference is a whole
    ///         bake.</b> A terminus pinned to the authoring size would hand a 4K bake a 1K map and
    ///         put the same throw back one level further out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is <em>said</em>, which is the half neither shape #805 named had.</b>
    ///         Refusing makes a legal-looking graph illegal; rescaling in silence undoes what the
    ///         author wrote with nothing on screen. <c>TG0022</c> is a warning rather than an error
    ///         because the plan it produces is sound and the picture is the one the graph describes —
    ///         what the author cannot otherwise see is that a resample they wrote does not decide how
    ///         big the file is.
    ///     </para>
    /// </remarks>
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
                TextureDiagnostics.TwoOutputsOneUsage,
                $"Two Output nodes both write '{usage}', and a bake writes one file per usage — so one of "
                + $"them is the map and the graph does not say which. The other is {existing.Node}.",
                node.Id,

                // The node's own field rather than the string, because the port name is the join
                // between a diagnostic and the row an editor highlights, and a literal here is a
                // join a rename cannot break loudly.
                nameof(Nodes.OutputNode.Usage)
            ));

            return;
        }

        var level = LevelOf(image);

        if (level != 0) {
            var arrived = SizeOf(image);

            Report(new(
                TextureDiagnostics.OutputResampledToTheGraphsMaps,
                $"'{usage}' is computed at {arrived.X}×{arrived.Y} and this graph's maps are "
                + $"{Extent(authoredWidth, 0)}×{Extent(authoredHeight, 0)}, so the compiler resampled it back. A "
                + "texture set is one material's maps over one atlas, so they are one size — a Resample says at "
                + "which resolution part of a graph is computed and not how big its map is. Resample back before "
                + "the Output to say it in the graph.",
                node.Id,
                nameof(Nodes.OutputNode.Usage),
                NodeSeverity.Warning
            ));

            image = Rescale(image, 0);
        }

        outputs.Add(new(usage, image, node.Id));
    }

    /// <summary>Allocates the image one output port carries.</summary>
    internal int Write(GraphNode node, string port, TextureChannels wanted, int levelOffset) {
        var image = Allocate(TextureEmitter.FormatOf(wanted), wanted, levelOffset);

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
    internal int Scratch(TextureFormat format, int levelOffset) =>
        Allocate(
            format,
            format == TextureFormat.R16Float ? TextureChannels.Grey : TextureChannels.Colour,
            levelOffset
        );

    /// <summary>Allocates an image the caller supplies, and records what fills it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one entry in a plan's table nothing writes, and the only place an absolute
    ///         size enters a graph</b> — doc 48 § D8. Its <see cref="TextureImage.LevelOffset" /> is
    ///         therefore nominal and left at the base: every kernel clamps its taps to the
    ///         <em>source's</em> own dimensions, so a ramp 256 texels wide is read correctly by an op
    ///         writing a 4K image, and a level on it would be a number nothing reads pretending
    ///         otherwise.
    ///     </para>
    ///     <para>
    ///         <b>The byte count is checked here rather than at the upload</b>, because here there is
    ///         a node to blame. <c>TextureUploads.Add</c> makes the same check and throws; a
    ///         diagnostic naming the node is what an author can act on.
    ///     </para>
    /// </remarks>
    internal int External(
        GraphNode node,
        TextureFormat format,
        TextureChannels carried,
        string asset,
        int width,
        int height,
        ReadOnlySpan<byte> texels
    ) {
        ArgumentNullException.ThrowIfNull(node);

        if (asset.Length == 0) {
            var expected = (long)width * height * TextureFormats.BytesPerTexel(format);

            if (width <= 0 || height <= 0 || texels.Length != expected) {
                Report(new(
                    TextureDiagnostics.BakedPictureIsTheWrongSize,
                    $"This node bakes its own {width}×{height} {format} picture and handed over {texels.Length} "
                    + $"byte(s) where {expected} are needed. An external image is uploaded exactly as it is "
                    + "written down — there is nothing that could resample it.",
                    node.Id
                ));

                return -1;
            }
        }

        var image = Allocate(format, carried, 0, external: true);

        externals.Add(new(image, Inlining.Resolve(node.Id), asset, width, height, [.. texels]));

        return image;
    }

    /// <summary>The image arriving at one input, under doc 48 § Part 4's promotion rule.</summary>
    internal int Read(
        GraphNode node,
        string port,
        NodeBinding binding,
        TextureChannels wanted,
        int level,
        bool strict
    ) {
        if (!binding.IsConnected(port)) {
            Report(new(
                TextureDiagnostics.NoImage,
                $"'{port}' has no image connected. There is no literal image an author could type into a "
                + "port instead, so a source node is what fills one.",
                node.Id,
                port
            ));

            return -1;
        }

        if (Upstream(node, port) is not { } arrived) {
            // ⚠ Said rather than passed over, even though whatever went wrong upstream has already
            // been reported. A node whose output port is an image and which never asked for one is a
            // node-library bug, and it is the exact shape of failure that would otherwise compile to
            // a plan missing a dispatch and nothing to point at.
            Report(new(
                TextureDiagnostics.UpstreamProducedNoImage,
                $"'{port}' is wired, and whatever feeds it produced no image. Either the node upstream "
                + "failed, or its output is not an image this compiler can carry.",
                node.Id,
                port
            ));

            return -1;
        }

        // The size first and the channels after, because a rescale of one channel is cheaper than a
        // rescale of four — and because the splat below has to be allocated at this node's level
        // too, which it is by construction once its own source is.
        var source = Rescale(arrived, level);
        var arriving = ChannelsOf(source);

        if (arriving == wanted) {
            return source;
        }

        if (strict) {
            Report(new(
                TextureDiagnostics.ColourWhereOneChannelIsWanted,
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

        // ⚠ At the source's level and not at the base. The splat is a `ChannelShuffle`, which is a
        // pointwise kernel like any other — a base-sized promotion of a half-resolution mask is
        // #779's corner crop, inserted by the compiler itself rather than by any node.
        promoted = Allocate(TextureFormat.Rgba16Float, TextureChannels.Colour, LevelOf(source));

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

    /// <summary>The same image at one level, resampling into a new one when it is not there already.</summary>
    /// <param name="source">The image arriving.</param>
    /// <param name="level">Where the node reading it keeps its images.</param>
    /// <returns>An image at <paramref name="level" /> holding that picture.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>ChannelShuffle</c> promotion's twin, and for the same reason —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/779">#779</a>.</b> Two images meeting
    ///         at one node have to be the same size, because a kernel reads its inputs at the
    ///         coordinate it is writing and clamps: a 256² blend of a 256² and a 128² image is the
    ///         second one's top-left quarter stretched over nothing, with its edge row smeared down
    ///         the rest. Refusing the graph instead would be refusing something an author can
    ///         perfectly well mean.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both directions, and the filter is derived from which one this call is.</b> Inside
    ///         a graph it is always a magnification — <see cref="Resolve" /> takes the <em>finest</em>
    ///         level arriving, so a node never asks an image to get smaller — and that used to be
    ///         written here as "nothing here is ever asked to make an image smaller and
    ///         <c>Bilinear</c> is the whole answer". <see cref="Keep" /> is the caller that made it
    ///         false: a terminus rescales to level zero, and a level offset is signed, so
    ///         <c>Resample(Quadruple) → Output</c> arrives here as a <b>4:1 minification</b>.
    ///         <c>Resample.rvn</c>'s header is the authority on what that needs — "<c>Box</c> is the
    ///         default and the only correct choice going down", because halving with anything that
    ///         reads four texels keeps a fixed neighbourhood however far the ratio moves and drops
    ///         the rest. Going up, a box degenerates to one sample and <c>Bilinear</c> is the one to
    ///         reach for. <a href="https://github.com/Rikarin/Vixen/issues/829">#829</a>.
    ///     </para>
    ///     <para>
    ///         <b>Shared between ports</b>, so a half-resolution mask arriving at two inputs of one
    ///         node is one resample and one texture rather than two of each. The key is the pair, and
    ///         the filter is a function of the pair, so a shared entry cannot carry the wrong one.
    ///     </para>
    /// </remarks>
    int Rescale(int source, int level) {
        if (source < 0 || source >= images.Count || images[source].LevelOffset == level) {
            return source;
        }

        // ⚠ An external cannot arrive here, and that used to be written as a branch. `External`
        // hands its index straight back to the node that asked and never puts it in `imageOf`, so
        // the only two routes into this method — `Upstream`, and `Keep` by way of it — cannot
        // produce one. Said here rather than guarded, for `Keep`'s reason one method up: an
        // unreachable branch is a claim about the compiler's own shape rather than about this
        // method, and `A_table_baked_by_a_node_below_the_base_is_read_at_its_own_size` is where a
        // claim can go red. It would not have to be rescaled in any case — a kernel samples an
        // external in normalised space precisely so a plan need not know how big it is.
        if (rescales.TryGetValue((source, level), out var scaled)) {
            return scaled;
        }

        // ⚠ The comparison is between two level offsets rather than between two extents, so that
        // `BakeLevelOffset` — which moves both by the same amount — cannot change which filter a
        // rescale gets. Which way a rescale goes is a property of the graph, not of what the bake
        // was asked for.
        var coarser = level > images[source].LevelOffset;

        scaled = Allocate(images[source].Format, ChannelsOf(source), level);

        ops.Add(
            new() {
                Kernel = TextureColourKernels.Resample,
                Output = scaled,
                Inputs = [source],

                // #801: this op exists *because* the two sizes differ, which is #779's fix.
                ReadsOtherExtents = true,
                Parameters = [new("filter", (float)(coarser ? TextureFilter.Box : TextureFilter.Bilinear))]
            }
        );

        rescales[(source, level)] = scaled;

        return scaled;
    }

    /// <summary>What a node's image ports resolve to: the widest and the finest thing arriving.</summary>
    /// <param name="node">The node about to compile.</param>
    /// <param name="definition">Its type, which is what says which of its ports are images.</param>
    /// <param name="level">
    ///     Where the images it allocates sit, in levels from the authoring base: the smallest level
    ///     offset — the <em>largest</em> image — arriving at one of its inputs, and zero for a node
    ///     with none.
    /// </param>
    /// <returns>The channels.</returns>
    /// <remarks>
    ///     <para>
    ///         <see cref="PortKinds.Resolve" />'s rule, over channels instead of lanes. A node with
    ///         nothing wired is grey for the same reason a node with nothing wired is a float: it has
    ///         to be something, and the narrowest is the one that promotes into anything later.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the same question over size, which is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/779">#779</a>.</b> A node's output is
    ///         the size of what it reads rather than the size of the graph — one rule, in one place,
    ///         rather than forty nodes each remembering to ask. The three shapes this was weighed
    ///         against are worth naming, because two of them are wrong: <em>refusing</em> a node
    ///         whose output level differs from its input's would make <c>Space/Resample</c> a node
    ///         nothing may be wired downstream of, and <em>inserting</em> a resample back up to the
    ///         base would make it a node with no effect. Deriving is the only one of the three that
    ///         leaves "the target's size is the scale" meaning anything.
    ///     </para>
    ///     <para>
    ///         <b>The finest rather than the coarsest, because a promotion must not throw detail
    ///         away.</b> That is the same choice "widest wins" makes one axis over, and it is what
    ///         makes the resample <see cref="Read" /> inserts always a magnification.
    ///     </para>
    /// </remarks>
    TextureChannels Resolve(GraphNode node, NodeTypeDefinition definition, out int level) {
        var widest = TextureChannels.Grey;
        int? finest = null;

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

            var at = LevelOf(image);

            if (finest is null || at < finest) {
                finest = at;
            }
        }

        level = finest ?? 0;

        return widest;
    }

    /// <summary>The image feeding one input, or null when nothing usable does.</summary>
    int? Upstream(GraphNode node, string port) =>
        graph?.Source(new(node.Id, port)) is { } source
        && graph.TryGet(source.Node, out var from)
        && imageOf.TryGetValue(Variable(from, source.Port), out var image)
            ? image
            : null;

    int Allocate(TextureFormat format, TextureChannels carried, int levelOffset = 0, bool external = false) {
        images.Add(new(format, levelOffset, external));
        channels.Add(carried);

        return images.Count - 1;
    }
}
