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

        return new(path, ports.ToImmutable(), static () => new SubGraphNode(), graph.Name);
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
    ///         sub-graph is new and gets fresh identities, and a complaint about one names something
    ///         the author cannot click on. That is a real gap; closing it needs a map from the
    ///         synthetic identity back to the sub-graph node it came out of, which nothing yet reads.
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
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);

        var flattener = new Flattener(source);
        var result = flattener.Run(graph);

        diagnostics = flattener.Diagnostics;

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

        NodeGraphModel result = null!;
        int next;

        public IReadOnlyList<NodeDiagnostic> Diagnostics => diagnostics;

        public NodeGraphModel Run(NodeGraphModel graph) {
            result = new() { Name = graph.Name };
            next = 0;

            // Above every identity the author's graph already uses, so an inlined node never lands on
            // one of theirs — which is what makes preserving the top-level ones possible at all.
            foreach (var node in graph.Nodes) {
                next = Math.Max(next, node.Id.Value);
            }

            Expand(graph, default, [], [], preserve: true, depth: 0);

            // The furniture is the author's own graph's. A group inside a sub-graph describes that
            // graph's layout, and there is no layout left to describe once it has been inlined.
            result.Groups.AddRange(graph.Groups);
            result.Comments.AddRange(graph.Comments);
            result.Interface.AddRange(graph.Interface);

            return result;
        }

        /// <summary>Copies one graph into the result, and answers with what its outputs became.</summary>
        /// <param name="graph">The graph being copied.</param>
        /// <param name="offset">How far to move its nodes, so an inlined graph lands near its node.</param>
        /// <param name="inbound">Where each of its interface inputs is fed from, when it is.</param>
        /// <param name="constants">And the value each unfed one takes.</param>
        /// <param name="preserve">Whether identities are kept, which only the outermost graph does.</param>
        /// <param name="depth">How far in this is.</param>
        Dictionary<string, PortRef> Expand(
            NodeGraphModel graph,
            Vector2 offset,
            Dictionary<string, PortRef> inbound,
            Dictionary<string, float[]> constants,
            bool preserve,
            int depth
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
                    nested[node.Id] = Descend(graph, node, child, offset, depth, Trace, Held);

                    continue;
                }

                var id = preserve ? node.Id : new NodeId(++next);
                var copy = result.Add(id, node.Type, node.Position + offset);

                local[node.Id] = id;

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
        Dictionary<string, PortRef> Descend(
            NodeGraphModel owner,
            GraphNode node,
            NodeGraphModel child,
            Vector2 offset,
            int depth,
            Func<PortRef, PortRef?> trace,
            Func<PortRef, float[]?> held
        ) {
            if (depth >= MaximumDepth) {
                diagnostics.Add(new(
                    "NG0110",
                    $"Sub-graphs nest more than {MaximumDepth} deep at '{node.Type}', which is a mistake "
                    + "whatever it is. Nothing below it was inlined.",
                    node.Id
                ));

                return [];
            }

            if (!open.Add(node.Type)) {
                diagnostics.Add(new(
                    "NG0111",
                    $"'{node.Type}' contains itself, directly or through another sub-graph. A sub-graph is "
                    + "inlined rather than called, so a recursive one has no finite form.",
                    node.Id
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

                return Expand(child, offset + node.Position, inbound, constants, preserve: false, depth + 1);
            } finally {
                open.Remove(node.Type);
            }
        }

    }
}
