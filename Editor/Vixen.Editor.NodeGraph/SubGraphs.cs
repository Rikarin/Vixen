// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.NodeGraph;

/// <summary>The instance a sub-graph node makes. It is never asked to do anything.</summary>
/// <remarks>
///     A <see cref="NodeTypeDefinition" /> has to be able to make one, and a sub-graph node never
///     survives to be visited: <see cref="SubGraphs.Flatten" /> replaces it with the graph's contents
///     before the compiler walks anything. So this exists to satisfy the definition and to be a
///     recognisable thing in a debugger, and its <see cref="Bind" /> does nothing because there are no
///     port fields to fill.
/// </remarks>
public sealed class SubGraphNode : Node {
    /// <inheritdoc />
    public override void Bind(NodeBinding binding) { }
}

/// <summary>Where a graph's sub-graphs are found, by node-type path.</summary>
/// <remarks>
///     An interface rather than a dictionary parameter, because the editor resolves a sub-graph
///     through the asset database and a test resolves it from three graphs it built by hand, and
///     inlining should not know the difference.
/// </remarks>
public interface ISubGraphSource {
    /// <summary>The graph a node type stands for, if it stands for one.</summary>
    /// <param name="type">The node type's path.</param>
    /// <param name="graph">The graph, when there is one.</param>
    /// <returns><see langword="true" /> if that node type is a sub-graph.</returns>
    bool TryGet(string type, [NotNullWhen(true)] out NodeGraphModel? graph);
}

/// <summary>Sub-graphs held in memory, keyed by the node-type path that stands for each.</summary>
public sealed class SubGraphLibrary : ISubGraphSource {
    readonly Dictionary<string, NodeGraphModel> graphs = new(StringComparer.Ordinal);

    /// <summary>Every path registered, in no particular order.</summary>
    public IEnumerable<string> Paths => graphs.Keys;

    /// <summary>Adds a graph, and the node type that stands for it.</summary>
    /// <param name="path">The menu path a containing graph will store.</param>
    /// <param name="graph">The graph.</param>
    /// <param name="registry">The registry to add the derived node type to, when there is one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty or already registered.</exception>
    public void Add(string path, NodeGraphModel graph, NodeTypeRegistry? registry = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(graph);

        if (!graphs.TryAdd(path, graph)) {
            throw new ArgumentException(
                $"Two sub-graphs claim the path '{path}'. A saved graph names its nodes by path, so one "
                + "path has to mean one thing.",
                nameof(path)
            );
        }

        registry?.Add(SubGraphs.Definition(graph, path));
    }

    /// <inheritdoc />
    public bool TryGet(string type, [NotNullWhen(true)] out NodeGraphModel? graph) => graphs.TryGetValue(type, out graph);
}

/// <summary>Where an inlined node came from.</summary>
/// <param name="Node">The synthetic identity it has in the flattened graph.</param>
/// <param name="Source">
///     The sub-graph node in the author's own graph that it came out of — the thing they can select.
///     For a node inlined from a sub-graph inside a sub-graph this is still the outermost one, because
///     that is the only node the open document has.
/// </param>
/// <param name="Type">The node-type path of the sub-graph it was written in, which is the innermost.</param>
/// <param name="Inner">The identity it had in that sub-graph's own file.</param>
/// <param name="Expansion">
///     Which expansion of that sub-graph it came out of — a key into
///     <see cref="NodeGraphInlining.Expansions" />, and never zero for a node that was inlined.
///     ⚠ <b>Not the same question as <see cref="Type" />.</b> Two nodes of the same published type in
///     one graph are two expansions with two sets of settings, so a caller that keyed anything on the
///     path would give both of them whichever author's numbers it happened to read first.
/// </param>
public readonly record struct NodeOrigin(NodeId Node, NodeId Source, string Type, NodeId Inner, int Expansion);

/// <summary>One sub-graph node, as it was expanded: the graph it stood for and what it was set to.</summary>
/// <param name="Type">The node-type path of the sub-graph that was expanded.</param>
/// <param name="Source">
///     The node in the author's own graph to blame for it — the outermost one, exactly as
///     <see cref="NodeOrigin.Source" /> is, because that is the only node a canvas has.
/// </param>
/// <param name="Settings">
///     What the sub-graph node carried, by key: <see cref="GraphNode.Texts" />, copied. A published
///     graph's knobs are stored there under their own names, so this is the author's overrides —
///     and it holds whatever else that table held, because the flattener does not know which keys a
///     given kind of graph calls parameters.
/// </param>
/// <param name="Path">
///     Every sub-graph node walked through to reach this expansion, outermost first and this
///     expansion's own node last. One element for a sub-graph node in the author's graph, two for one
///     nested inside it, and so on.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>The flattener throws the sub-graph node away, and this is what survives it.</b>
///         Inlining replaces the node with the graph's contents, so the numbers an author typed on it
///         reached nothing at all — a knob that accepted a value, saved it, and changed no picture
///         (<a href="https://github.com/Rikarin/Vixen/issues/742">#742</a>). What is recorded is the
///         table rather than an interpretation of it: which keys are parameters is a question only
///         the graph's own compiler can answer.
///     </para>
///     <para>
///         ⚠ <b><see cref="Path" /> is the middle of the walk, and only its two ends used to be
///         recorded</b> — <a href="https://github.com/Rikarin/Vixen/issues/925">#925</a>.
///         <see cref="Source" /> is the outermost node and <see cref="NodeOrigin.Inner" /> is an
///         identity in the innermost file, so two sibling instances of one compound nested inside
///         another share both, share <see cref="Type" />, and are indistinguishable to anything
///         downstream — two noise generators drawing one picture. <see cref="NodeOrigin.Expansion" />
///         does distinguish them and cannot be used for it: it is a walk-ordered counter, so an
///         insertion moves it, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a> one level in.
///     </para>
///     <para>
///         <b>Every element is stable within its own file</b>, which is the property that makes this
///         usable where the counter is not: each is a <see cref="NodeId"/> read out of the
///         <em>containing</em> graph's document, and a document never renumbers or reuses one. So the
///         chain moves only when an author moves the compound node it names.
///     </para>
/// </remarks>
public readonly record struct SubGraphExpansion(
    string Type,
    NodeId Source,
    IReadOnlyDictionary<string, string> Settings,
    ImmutableArray<NodeId> Path
);

/// <summary>Which inlined node came out of which sub-graph node.</summary>
/// <remarks>
///     <para>
///         <b>The half of the mapping that makes a diagnostic actionable.</b>
///         <see cref="SubGraphs.Flatten" /> gives the nodes it copies out of a sub-graph fresh
///         identities, because the author's own graph already owns the ones it has. A complaint about
///         one of those names something that is in no document and on no canvas, so nothing can be
///         selected, framed or highlighted. This is the way back.
///     </para>
///     <para>
///         ⚠ <b>It is not a rename.</b> The flattened graph is what was compiled and its identities are
///         the ones in the emitted variable names, so both are worth keeping: <see cref="Resolve" />
///         answers "what does the author click on" and <see cref="NodeOrigin.Inner" /> answers "what
///         was it called where they wrote it".
///     </para>
/// </remarks>
public sealed class NodeGraphInlining {
    /// <summary>Nothing was inlined.</summary>
    public static NodeGraphInlining Empty { get; } =
        new(new Dictionary<NodeId, NodeOrigin>(), new Dictionary<int, SubGraphExpansion>());

    readonly IReadOnlyDictionary<NodeId, NodeOrigin> origins;
    readonly IReadOnlyDictionary<int, SubGraphExpansion> expansions;

    internal NodeGraphInlining(
        IReadOnlyDictionary<NodeId, NodeOrigin> origins,
        IReadOnlyDictionary<int, SubGraphExpansion> expansions
    ) {
        this.origins = origins;
        this.expansions = expansions;
    }

    /// <summary>Every node that came out of a sub-graph, by the identity it was given.</summary>
    public IReadOnlyDictionary<NodeId, NodeOrigin> Origins => origins;

    /// <summary>Every sub-graph node that was expanded, by <see cref="NodeOrigin.Expansion" />.</summary>
    public IReadOnlyDictionary<int, SubGraphExpansion> Expansions => expansions;

    /// <summary>Whether anything was inlined at all.</summary>
    public bool IsEmpty => origins.Count == 0;

    /// <summary>Where one node came from, if it came from a sub-graph.</summary>
    /// <param name="node">The identity in the flattened graph.</param>
    /// <param name="origin">Where it came from.</param>
    /// <returns><see langword="true" /> if it was inlined.</returns>
    public bool TryGet(NodeId node, out NodeOrigin origin) => origins.TryGetValue(node, out origin);

    /// <summary>The node an author can select for one node of the flattened graph.</summary>
    /// <param name="node">The identity in the flattened graph.</param>
    /// <returns>The sub-graph node it came out of, or the same identity when it was theirs already.</returns>
    public NodeId Resolve(NodeId node) => origins.TryGetValue(node, out var origin) ? origin.Source : node;

    /// <summary>The expansion one inlined node came directly out of.</summary>
    /// <param name="node">The identity in the flattened graph.</param>
    /// <param name="expansion">The sub-graph node it was copied out of, and what that node was set to.</param>
    /// <returns><see langword="true" /> if it was inlined.</returns>
    /// <remarks>
    ///     ⚠ <b>Directly, which for a nested sub-graph is not the node the author can select.</b>
    ///     <see cref="NodeOrigin.Source" /> is the outermost node because that is what a diagnostic
    ///     has to name; the settings that apply to a node two levels in are the ones written on the
    ///     inner sub-graph node, inside the published graph's own file, and answering with the
    ///     outermost node's would hand a graph somebody else's numbers.
    /// </remarks>
    public bool TryGetExpansion(NodeId node, out SubGraphExpansion expansion) {
        if (origins.TryGetValue(node, out var origin)) {
            return expansions.TryGetValue(origin.Expansion, out expansion);
        }

        expansion = default;

        return false;
    }

    /// <summary>Where a node was, as a sentence to put on the end of a diagnostic.</summary>
    /// <param name="node">The identity in the flattened graph.</param>
    /// <returns>The sentence, or an empty string when the node is the author's own.</returns>
    public string Describe(NodeId node) =>
        origins.TryGetValue(node, out var origin)
            ? $" It is {origin.Inner} inside '{origin.Type}', which {origin.Source} stands for."
            : "";
}

/// <summary>What lifting some nodes out into a sub-graph would do.</summary>
/// <param name="Graph">The new graph, with its interface and its entry and exit nodes.</param>
/// <param name="Extracted">Which nodes moved, in the order the graph held them.</param>
/// <param name="Incoming">The edges that arrived from outside, which became the graph's inputs.</param>
/// <param name="Outgoing">The edges that left for outside, which became its outputs.</param>
/// <param name="Inputs">Which interface input each external output now feeds.</param>
/// <param name="Outputs">Which interface output each internal output now leaves by.</param>
public sealed record SubGraphExtraction(
    NodeGraphModel Graph,
    ImmutableArray<NodeId> Extracted,
    ImmutableArray<GraphEdge> Incoming,
    ImmutableArray<GraphEdge> Outgoing,
    IReadOnlyDictionary<PortRef, string> Inputs,
    IReadOnlyDictionary<PortRef, string> Outputs
);

/// <summary>
///     A graph inside a graph: the entry and exit nodes, the node type a containing graph stores, and
///     the inlining that removes both before anything is compiled.
/// </summary>
/// <remarks>
///     <para>
///         <b>Inlining rather than a call.</b> Every target these graphs compile to — Raven source, an
///         array of VFX operations — is a straight-line program over values, and neither has a function
///         to call or a stack to put one on. So a sub-graph is a macro: <see cref="Flatten" /> turns a
///         graph containing sub-graph nodes into an equivalent graph containing none, and the compiler
///         that walks the result has no idea sub-graphs exist.
///     </para>
///     <para>
///         <b>The boundary nodes are not registry types.</b> Their ports are the open graph's
///         <see cref="NodeGraphModel.Interface" />, so a registry entry for them would have to be
///         re-registered every time a different sub-graph was opened — and two graphs open at once
///         would disagree about what one path meant. <see cref="Boundary" /> builds the definition for
///         the graph in hand instead, and the view asks for it by name.
///     </para>
///     <para>
///         ⚠ <b>The entry node's ports face the other way from the interface's.</b> A port the
///         interface calls an <i>input</i> — a value the containing graph feeds in — is an
///         <i>output</i> of the entry node, because inside the sub-graph that is where the value comes
///         from. Getting this backwards produces a sub-graph whose wires all refuse to connect and no
///         clue as to why.
///     </para>
/// </remarks>
public static class SubGraphs {
    /// <summary>The node type of a sub-graph's entry node, which shows the interface's inputs.</summary>
    public const string InputType = "Sub-graph/Input";

    /// <summary>And of its exit node, which shows the interface's outputs.</summary>
    public const string OutputType = "Sub-graph/Output";

    /// <summary>How deep sub-graphs may nest before inlining gives up.</summary>
    /// <remarks>
    ///     A backstop, not a design limit. Recursion is already refused by name — a graph cannot
    ///     contain itself at any depth — so reaching this means a chain of thirty-two distinct graphs,
    ///     which is a mistake whatever it is.
    /// </remarks>
    public const int MaximumDepth = 32;

    /// <summary>The node type a containing graph stores for a sub-graph.</summary>
    /// <param name="graph">The sub-graph.</param>
    /// <param name="path">The menu path, and the key the containing graph stores.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public static NodeTypeDefinition Definition(NodeGraphModel graph, string path) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Inputs first, as the generator orders a compiled node's, so a sub-graph is drawn the same
        // way as everything beside it.
        var ports = ImmutableArray.CreateBuilder<PortDefinition>(graph.Interface.Count);

        foreach (var port in graph.Interface) {
            if (port.Direction == PortDirection.Input) {
                ports.Add(port);
            }
        }

        foreach (var port in graph.Interface) {
            if (port.Direction == PortDirection.Output) {
                ports.Add(port);
            }
        }

        // ⚠ And the graph's own parameters as the node's settings, which is the seam #730 widened
        // `SettingDefinition` for and nothing then declared. A published graph's knobs live on the
        // model since #719 and this is the one place that turns a graph into a node type — so
        // without this line every sub-graph node in every front end is drawn with no knobs at all,
        // and the kind, the range and the group a parameter carries reach nothing to draw them.
        return new(
            path,
            ports.ToImmutable(),
            static () => new SubGraphNode(),
            graph.Name,
            false,
            [.. graph.Parameters]
        );
    }

    /// <summary>The node type of one of a sub-graph's own boundary nodes.</summary>
    /// <param name="graph">The graph being edited.</param>
    /// <param name="type">Either <see cref="InputType" /> or <see cref="OutputType" />.</param>
    /// <returns>The type, whose ports are the graph's interface turned round.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="type" /> is not one of the two.</exception>
    public static NodeTypeDefinition Boundary(NodeGraphModel graph, string type) {
        ArgumentNullException.ThrowIfNull(graph);

        var entry = string.Equals(type, InputType, StringComparison.Ordinal);

        if (!entry && !string.Equals(type, OutputType, StringComparison.Ordinal)) {
            throw new ArgumentException($"'{type}' is not a boundary node type.", nameof(type));
        }

        // Turned round: see the ⚠ on this class. An interface input arrives at the entry node from
        // outside and leaves it going in, so inside the graph it is an output.
        var wanted = entry ? PortDirection.Input : PortDirection.Output;
        var facing = entry ? PortDirection.Output : PortDirection.Input;

        var ports = ImmutableArray.CreateBuilder<PortDefinition>();

        foreach (var port in graph.Interface) {
            if (port.Direction == wanted) {
                ports.Add(port with { Direction = facing });
            }
        }

        return new(
            type,
            ports.ToImmutable(),
            static () => new SubGraphNode(),
            entry ? "What the containing graph feeds in." : "What this graph hands back."
        );
    }

    /// <summary>Whether a node type is one of the two boundary types.</summary>
    /// <param name="type">The path.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    public static bool IsBoundary(string type) =>
        string.Equals(type, InputType, StringComparison.Ordinal)
        || string.Equals(type, OutputType, StringComparison.Ordinal);

    /// <summary>Whether a graph contains any node that stands for a sub-graph.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="source">Where sub-graphs are found.</param>
    /// <returns><see langword="true" /> if flattening it would change anything.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static bool ContainsSubGraph(NodeGraphModel graph, ISubGraphSource source) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);

        foreach (var node in graph.Nodes) {
            if (source.TryGet(node.Type, out _)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces every sub-graph node with the contents of the graph it stands for.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="source">Where sub-graphs are found.</param>
    /// <param name="diagnostics">Everything that had to be dropped, in the order it was found.</param>
    /// <returns>An equivalent graph containing no sub-graph nodes and no boundary nodes.</returns>
    /// <exception cref="ArgumentNullException">Either reference argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The top-level identities survive and the inlined ones do not.</b> A diagnostic names
    ///         a node, and one about the author's own graph has to name a node the author can select —
    ///         so the nodes that were already there keep the identities they had. What comes out of a
    ///         sub-graph is new and gets fresh identities, and a complaint about one would name
    ///         something the author cannot click on. The overload taking a
    ///         <see cref="NodeGraphInlining" /> is the way back from one to the other, and
    ///         <c>NodeGraphCompiler</c> reads it for every diagnostic it reports.
    ///     </para>
    ///     <para>
    ///         <b>An unconnected sub-graph input becomes an inline value on whatever it fed.</b> The
    ///         entry node disappears, so there is nothing left to carry a default; pushing the value
    ///         down to every port the entry node fed is what keeps a sub-graph that was dropped in and
    ///         not wired up doing what the graph it stands for does.
    ///     </para>
    /// </remarks>
    public static NodeGraphModel Flatten(
        NodeGraphModel graph,
        ISubGraphSource source,
        out IReadOnlyList<NodeDiagnostic> diagnostics
    ) => Flatten(graph, source, out diagnostics, out _);

    /// <inheritdoc cref="Flatten(NodeGraphModel,ISubGraphSource,out IReadOnlyList{NodeDiagnostic})" />
    /// <param name="graph">The graph.</param>
    /// <param name="source">Where sub-graphs are found.</param>
    /// <param name="diagnostics">Everything that had to be dropped, in the order it was found.</param>
    /// <param name="inlining">Which inlined node came out of which sub-graph node.</param>
    public static NodeGraphModel Flatten(
        NodeGraphModel graph,
        ISubGraphSource source,
        out IReadOnlyList<NodeDiagnostic> diagnostics,
        out NodeGraphInlining inlining
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);

        var flattener = new Flattener(source);
        var result = flattener.Run(graph);

        diagnostics = flattener.Diagnostics;
        inlining = flattener.Inlining;

        return result;
    }

    /// <summary>
    ///     Lifts some of a graph's nodes out into a graph of their own, with an interface made of the
    ///     wires that crossed the boundary.
    /// </summary>
    /// <param name="graph">The graph to take them from. Not modified.</param>
    /// <param name="selection">Which nodes.</param>
    /// <param name="name">What the new graph is called.</param>
    /// <param name="registry">The node library, for reading the kind of a port that crossed.</param>
    /// <returns>The extraction: the new graph, and how the containing graph is to be rewired.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> or the selection is null.</exception>
    /// <exception cref="ArgumentException">The selection is empty or names a node the graph has not got.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Nothing is changed here.</b> The extraction is a description and
    ///         <see cref="ExtractSubGraphCommand" /> is what applies it — because applying it is an
    ///         undoable edit and computing it is arithmetic, and a method that did both could only be
    ///         tested through a command stack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One interface port per distinct crossing, not per crossing edge.</b> An external
    ///         output feeding three of the selected nodes becomes one input on the sub-graph that fans
    ///         out inside it, which is what the author drew. One port per edge would produce three
    ///         identical inputs wired to the same thing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="NodeGraphModel.Parameters" /> cross whole, and the alternative was
    ///         argued and refused — <a href="https://github.com/Rikarin/Vixen/issues/802">#802</a>.</b>
    ///         A copied node keeps its <see cref="GraphNode.Texts" />, which is where a front end
    ///         stores an expression an author wrote over the graph's knobs, so an extraction that took
    ///         the texts and left the declarations produced a sub-graph carrying
    ///         <c>amount * 32f</c> and declaring no <c>amount</c>. The tidier rule — carry only the
    ///         parameters the selection <em>mentions</em> — needs to know what an expression is, and
    ///         that is a front end's question: the marker, the identifier syntax and the folder all
    ///         live in <c>Vixen.Editor.TextureGraph</c>, and this assembly deliberately knows nothing
    ///         about any of them. So the choice here is between a knob nobody uses, which an author
    ///         can see and delete, and an expression that binds against nothing, which an author
    ///         cannot see at all until the containing graph reports an undefined name.
    ///     </para>
    ///     <para>
    ///         <b><see cref="NodeGraphModel.Settings" /> deliberately do not cross</b>, and the
    ///         silence about that was the other half of #802. A texture graph's settings are its base
    ///         resolution and its seed; <see cref="Flatten" /> keeps the <em>containing</em> graph's
    ///         and drops an inlined one's, so a sub-graph that carried a copy of them would be
    ///         carrying two numbers that are read exactly nowhere and shown in an inspector as though
    ///         they were.
    ///     </para>
    /// </remarks>
    public static SubGraphExtraction Extract(
        NodeGraphModel graph,
        IReadOnlyCollection<NodeId> selection,
        string name,
        NodeTypeRegistry? registry = null
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Count == 0) {
            throw new ArgumentException("There is nothing to extract.", nameof(selection));
        }

        HashSet<NodeId> inside = [];

        foreach (var id in selection) {
            if (!graph.TryGet(id, out _)) {
                throw new ArgumentException($"{id} is not in this graph.", nameof(selection));
            }

            inside.Add(id);
        }

        var extracted = new NodeGraphModel { Name = name };

        // ⚠ The knobs, because the copies below carry the expressions written against them. See the
        // remarks: this is the whole of #802, and it is one line because the parameter list is a
        // side table on the model rather than a property of whatever compiles it.
        extracted.Parameters.AddRange(graph.Parameters);

        List<NodeId> moved = [];
        Rectangle? bounds = null;

        foreach (var node in graph.Nodes) {
            if (!inside.Contains(node.Id)) {
                continue;
            }

            moved.Add(node.Id);

            var copy = extracted.Add(node.Id, node.Type, node.Position);

            foreach (var (port, value) in node.Values) {
                copy.SetValue(port, [.. value]);
            }

            // ⚠ And the names. A node extracted into a sub-graph without them is the same node with
            // every setting blanked, which is a silent change to what the graph renders.
            foreach (var (port, text) in node.Texts) {
                copy.SetText(port, text);
            }

            var box = new Rectangle(node.Position.X, node.Position.Y, 1f, 1f);
            bounds = bounds is { } union ? Rectangle.Union(union, box) : box;
        }

        var extent = bounds ?? Rectangle.Empty;
        var entry = extracted.Add(InputType, new(extent.X - 240f, extent.Y));
        var exit = extracted.Add(OutputType, new(extent.Right + 240f, extent.Y));

        Dictionary<PortRef, string> inbound = [];
        Dictionary<PortRef, string> outbound = [];
        List<GraphEdge> incoming = [];
        List<GraphEdge> outgoing = [];
        Names names = new();

        foreach (var edge in graph.Edges) {
            var fromInside = inside.Contains(edge.From.Node);
            var toInside = inside.Contains(edge.To.Node);

            if (fromInside && toInside) {
                extracted.Connect(edge.From, edge.To);

                continue;
            }

            if (!fromInside && toInside) {
                // Keyed on the external output, so one value feeding three of the selected nodes is
                // one port on the sub-graph rather than three of them wired to the same thing.
                if (!inbound.TryGetValue(edge.From, out var port)) {
                    port = names.Take(Suggest(graph, edge.To));
                    inbound[edge.From] = port;

                    extracted.Interface.Add(new(port, PortDirection.Input, KindOf(graph, registry, edge.From, PortDirection.Output)));
                }

                extracted.Connect(new(entry.Id, port), edge.To);
                incoming.Add(edge);

                continue;
            }

            if (!fromInside || toInside) {
                continue;
            }

            if (!outbound.TryGetValue(edge.From, out var leaving)) {
                leaving = names.Take(Suggest(graph, edge.From));
                outbound[edge.From] = leaving;

                extracted.Interface.Add(new(leaving, PortDirection.Output, KindOf(graph, registry, edge.From, PortDirection.Output)));
                extracted.Connect(edge.From, new(exit.Id, leaving));
            }

            outgoing.Add(edge);
        }

        return new(extracted, [.. moved], [.. incoming], [.. outgoing], inbound, outbound);
    }

    /// <summary>What a port of a node carries, or <see cref="PortKind.Dynamic" /> when nobody knows.</summary>
    /// <remarks>
    ///     Dynamic is the honest answer for a node type that is not registered — a graph saved against
    ///     a missing plugin is exactly that — and it is also the answer that lets the sub-graph resolve
    ///     the width from whatever ends up connected, which is what the author would have chosen.
    /// </remarks>
    static PortKind KindOf(NodeGraphModel graph, NodeTypeRegistry? registry, PortRef port, PortDirection direction) {
        if (registry is null || !graph.TryGet(port.Node, out var node) || !registry.TryGet(node.Type, out var definition)) {
            return PortKind.Dynamic;
        }

        return definition.Port(port.Port, direction)?.Kind ?? PortKind.Dynamic;
    }

    /// <summary>A readable name for an interface port, from the node and port it came from.</summary>
    static string Suggest(NodeGraphModel graph, PortRef port) {
        if (!graph.TryGet(port.Node, out var node)) {
            return port.Port;
        }

        var title = node.Type[(node.Type.LastIndexOf('/') + 1)..];

        return title.Length == 0 ? port.Port : $"{title} {port.Port}";
    }

    /// <summary>Hands out names that are not already taken, by adding a number.</summary>
    sealed class Names {
        readonly HashSet<string> taken = new(StringComparer.Ordinal);

        public string Take(string wanted) {
            var name = string.IsNullOrWhiteSpace(wanted) ? "Value" : wanted;

            if (taken.Add(name)) {
                return name;
            }

            for (var index = 2; ; index++) {
                var candidate = $"{name} {index}";

                if (taken.Add(candidate)) {
                    return candidate;
                }
            }
        }
    }

    /// <summary>The inlining, which is one walk per graph with a shared accumulator.</summary>
    sealed class Flattener(ISubGraphSource source) {
        readonly List<NodeDiagnostic> diagnostics = [];
        readonly HashSet<string> open = new(StringComparer.Ordinal);
        readonly Dictionary<NodeId, NodeOrigin> origins = [];
        readonly Dictionary<int, SubGraphExpansion> expansions = [];

        NodeGraphModel result = null!;
        int next;
        int expanded;

        public IReadOnlyList<NodeDiagnostic> Diagnostics => diagnostics;

        public NodeGraphInlining Inlining =>
            origins.Count == 0 ? NodeGraphInlining.Empty : new(origins, expansions);

        public NodeGraphModel Run(NodeGraphModel graph) {
            result = new() { Name = graph.Name };
            next = 0;

            // Above every identity the author's graph already uses, so an inlined node never lands on
            // one of theirs — which is what makes preserving the top-level ones possible at all.
            foreach (var node in graph.Nodes) {
                next = Math.Max(next, node.Id.Value);
            }

            Expand(graph, default, [], [], preserve: true, depth: 0, NodeId.None, "", expansion: 0, path: []);

            // Everything the author's own graph is besides its nodes and edges — its furniture, its
            // interface, its settings and its parameters. A group inside a sub-graph describes that
            // graph's layout and there is no layout left to describe once it has been inlined; the
            // *declarations* are the containing graph's for the same reason, because the graph being
            // compiled is the outer one.
            //
            // ⚠ The list of what that means lives on `NodeGraphModel` beside the fields, and #780 is
            // why: three of them were spelled out here, and the two added on the day this was last
            // read were dropped without a diagnostic.
            graph.CopyDocumentTo(result);

            return result;
        }

        /// <summary>Copies one graph into the result, and answers with what its outputs became.</summary>
        /// <param name="graph">The graph being copied.</param>
        /// <param name="offset">How far to move its nodes, so an inlined graph lands near its node.</param>
        /// <param name="inbound">Where each of its interface inputs is fed from, when it is.</param>
        /// <param name="constants">And the value each unfed one takes.</param>
        /// <param name="preserve">Whether identities are kept, which only the outermost graph does.</param>
        /// <param name="depth">How far in this is.</param>
        /// <param name="origin">
        ///     The sub-graph node in the author's own graph that everything copied here came out of, or
        ///     <see cref="NodeId.None" /> for the author's graph itself. It stays the outermost one
        ///     however deep the nesting goes, because it is the only node the open document has.
        /// </param>
        /// <param name="type">And the node-type path of the sub-graph being copied, which is the innermost.</param>
        /// <param name="expansion">
        ///     Which expansion this walk is, or <c>0</c> for the author's own graph. It is the key
        ///     every node copied here is stamped with, and what a compiler looks the sub-graph node's
        ///     settings up by.
        /// </param>
        /// <param name="path">
        ///     The sub-graph nodes walked through to get here, outermost first — empty for the
        ///     author's own graph. ⚠ Carried down rather than reconstructed afterwards, because this
        ///     recursion <em>is</em> the chain and nothing that reads the result can see it.
        ///     <see cref="SubGraphExpansion.Path" /> is what it is for.
        /// </param>
        Dictionary<string, PortRef> Expand(
            NodeGraphModel graph,
            Vector2 offset,
            Dictionary<string, PortRef> inbound,
            Dictionary<string, float[]> constants,
            bool preserve,
            int depth,
            NodeId origin,
            string type,
            int expansion,
            ImmutableArray<NodeId> path
        ) {
            Dictionary<NodeId, NodeId> local = [];
            Dictionary<NodeId, Dictionary<string, PortRef>> nested = [];
            Dictionary<string, PortRef> produced = new(StringComparer.Ordinal);

            var entry = NodeId.None;

            foreach (var node in graph.Nodes) {
                if (string.Equals(node.Type, InputType, StringComparison.Ordinal)) {
                    entry = node.Id;

                    break;
                }
            }

            // Dependency order, so the node an edge comes from has already been copied by the time
            // the edge is traced — and so a nested expansion is asked for its inputs only once every
            // one of them exists.
            foreach (var node in graph.Ordered()) {
                if (IsBoundary(node.Type)) {
                    if (string.Equals(node.Type, OutputType, StringComparison.Ordinal)) {
                        foreach (var edge in graph.Edges) {
                            if (edge.To.Node == node.Id && Trace(edge.From) is { } from) {
                                produced[edge.To.Port] = from;
                            }
                        }
                    }

                    continue;
                }

                if (source.TryGet(node.Type, out var child)) {
                    nested[node.Id] = Descend(graph, node, child, offset, depth, origin, path, Trace, Held);

                    continue;
                }

                var id = preserve ? node.Id : new NodeId(++next);
                var copy = result.Add(id, node.Type, node.Position + offset);

                local[node.Id] = id;

                // The way back. Recorded here rather than derived afterwards because this is the only
                // line that knows both halves at once, and a synthetic identity that reached a
                // diagnostic without one names a node on no canvas.
                if (origin.IsValid) {
                    origins[id] = new(id, origin, type, node.Id, expansion);
                }

                foreach (var (port, value) in node.Values) {
                    copy.SetValue(port, [.. value]);
                }

                foreach (var (port, text) in node.Texts) {
                    copy.SetText(port, text);
                }
            }

            // The edges after every node, because an edge into a node the walk reached early may come
            // out of a nested expansion the walk reached late.
            foreach (var edge in graph.Edges) {
                if (!local.TryGetValue(edge.To.Node, out var target)) {
                    continue;
                }

                if (Trace(edge.From) is { } from) {
                    result.Connect(from, new(target, edge.To.Port));

                    continue;
                }

                // Nothing upstream: either the entry node's port was not fed, or a sub-graph output it
                // came through had nothing wired to it inside. Only the first has a value to leave.
                if (edge.From.Node == entry
                    && constants.TryGetValue(edge.From.Port, out var value)
                    && result.TryGet(target, out var node)) {
                    node.SetValue(edge.To.Port, [.. value]);
                }
            }

            return produced;

            PortRef? Trace(PortRef upstream) {
                if (upstream.Node == entry) {
                    return inbound.TryGetValue(upstream.Port, out var fed) ? fed : null;
                }

                if (nested.TryGetValue(upstream.Node, out var outputs)) {
                    return outputs.TryGetValue(upstream.Port, out var made) ? made : null;
                }

                return local.TryGetValue(upstream.Node, out var copied) ? new PortRef(copied, upstream.Port) : null;
            }

            /// <summary>The constant behind a wire that runs back to an entry port nobody fed.</summary>
            float[]? Held(PortRef upstream) =>
                upstream.Node == entry && constants.TryGetValue(upstream.Port, out var value) ? value : null;
        }

        /// <summary>Expands one sub-graph node, having worked out what arrives at it.</summary>
        /// <param name="owner">The graph the sub-graph node is written in.</param>
        /// <param name="node">The sub-graph node itself.</param>
        /// <param name="child">The graph it stands for.</param>
        /// <param name="offset">How far to move the nodes copied out of it.</param>
        /// <param name="depth">How far in the enclosing walk already is.</param>
        /// <param name="origin">The outermost sub-graph node, or <see cref="NodeId.None" />.</param>
        /// <param name="path">The chain of sub-graph nodes above this one, outermost first.</param>
        /// <param name="trace">Where a wire arriving at this node comes from, in the result.</param>
        /// <param name="held">The constant behind a wire running back to an entry port nobody fed.</param>
        /// <returns>What each of the child graph's outputs became.</returns>
        Dictionary<string, PortRef> Descend(
            NodeGraphModel owner,
            GraphNode node,
            NodeGraphModel child,
            Vector2 offset,
            int depth,
            NodeId origin,
            ImmutableArray<NodeId> path,
            Func<PortRef, PortRef?> trace,
            Func<PortRef, float[]?> held
        ) {
            // ⚠ Not `node.Id` unconditionally. A sub-graph node found *inside* a sub-graph is not in
            // the open document either, so a refusal about it has to be blamed on the outermost node —
            // which is what the author has on their canvas.
            var blamed = origin.IsValid ? origin : node.Id;

            if (depth >= MaximumDepth) {
                diagnostics.Add(new(
                    "NG0110",
                    $"Sub-graphs nest more than {MaximumDepth} deep at '{node.Type}', which is a mistake "
                    + "whatever it is. Nothing below it was inlined.",
                    blamed
                ));

                return [];
            }

            if (!open.Add(node.Type)) {
                diagnostics.Add(new(
                    "NG0111",
                    $"'{node.Type}' contains itself, directly or through another sub-graph. A sub-graph is "
                    + "inlined rather than called, so a recursive one has no finite form.",
                    blamed
                ));

                return [];
            }

            try {
                Dictionary<string, PortRef> inbound = new(StringComparer.Ordinal);
                Dictionary<string, float[]> constants = new(StringComparer.Ordinal);

                foreach (var edge in owner.Edges) {
                    if (edge.To.Node != node.Id) {
                        continue;
                    }

                    if (trace(edge.From) is { } from) {
                        inbound[edge.To.Port] = from;
                    } else if (held(edge.From) is { } inherited) {
                        // ⚠ The wire runs back to an entry port of the graph we are already inside,
                        // which itself was not fed. The value the *enclosing* walk settled on travels
                        // down rather than stopping here — reading the declared default instead would
                        // throw away whatever the outermost caller typed into the port.
                        constants[edge.To.Port] = inherited;
                    }
                }

                foreach (var port in child.Interface) {
                    if (port.Direction != PortDirection.Input
                        || inbound.ContainsKey(port.Name)
                        || constants.ContainsKey(port.Name)) {
                        continue;
                    }

                    constants[port.Name] = node.Values.TryGetValue(port.Name, out var inline)
                        ? [.. inline]
                        : [.. port.Default];
                }

                // ⚠ Per expansion and not per type, and the settings are copied rather than held.
                // Two `Generators/Dirt` nodes in one graph are two sets of numbers, and the table
                // this reads is the live one on a node the author goes on editing — a reference kept
                // here would make a finished compilation answer with what they typed afterwards.
                var expansion = ++expanded;

                // ⚠ And the chain of sub-graph nodes walked through to get here, which is the middle
                // of the walk that used to be thrown away — #925. `node.Id` is an identity in
                // `owner`'s own document, so every element of it is stable under an insertion
                // anywhere else, which is exactly what `expansion` above is not.
                var descended = path.Add(node.Id);

                expansions[expansion] = new(
                    node.Type,
                    blamed,
                    new Dictionary<string, string>(node.Texts, StringComparer.Ordinal),
                    descended
                );

                return Expand(
                    child,
                    offset + node.Position,
                    inbound,
                    constants,
                    preserve: false,
                    depth + 1,
                    blamed,
                    node.Type,
                    expansion,
                    descended
                );
            } finally {
                open.Remove(node.Type);
            }
        }

    }
}
