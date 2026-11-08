// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.NodeGraph;

/// <summary>A reroute dot: a node that only bends a wire.</summary>
/// <remarks>
///     <para>
///         <b>A node in the document rather than a point on an edge.</b> An edge is two port
///         references and nothing else, which is what keeps a file diffable and a merge resolvable; a
///         bend stored on it would be a third thing every reader of edges had to carry. A dot as a
///         node is saved, moved, selected, copied and undone by machinery that already exists.
///     </para>
///     <para>
///         ⚠ <b>No node library declares it, and no compiler sees it.</b> <see cref="NodeBypass.Apply" />
///         takes every reroute out of the graph a walk is over, so it means exactly what a straight
///         wire from its source would. What it carries is whatever feeds it — see <see cref="KindOf" />.
///     </para>
/// </remarks>
public static class Reroutes {
    /// <summary>The node-type path a reroute dot is stored under.</summary>
    public const string Type = "Reroute";

    /// <summary>Its one input.</summary>
    public const string In = "In";

    /// <summary>Its one output.</summary>
    public const string Out = "Out";

    /// <summary>Whether a node type is a reroute dot.</summary>
    /// <param name="type">The path.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    public static bool IsReroute(string type) => string.Equals(type, Type, StringComparison.Ordinal);

    /// <summary>A reroute's ports, carrying one kind.</summary>
    /// <param name="kind">What it carries, or <see cref="PortKind.None" /> before anything is wired to it.</param>
    /// <returns>The type.</returns>
    public static NodeTypeDefinition Definition(PortKind kind) =>
        new(
            Type,
            [new(In, PortDirection.Input, kind), new(Out, PortDirection.Output, kind)],
            static () => new SubGraphNode(),
            "Bends a wire. It carries whatever reaches it.",
            false
        );

    /// <summary>What a reroute carries: what feeds it, or failing that, what it feeds.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="reroute">The reroute.</param>
    /// <param name="typeOf">A node type for every node that is not a reroute, or null when it has none.</param>
    /// <returns>The kind, or <see cref="PortKind.None" /> when nothing is wired to it at either end.</returns>
    /// <remarks>
    ///     Upstream first, through any chain of reroutes, because the source is what decides what a
    ///     wire carries. A dot with nothing feeding it takes the kind of the first input it reaches,
    ///     so that a wire dragged into it later is checked against where it is going.
    /// </remarks>
    public static PortKind KindOf(NodeGraphModel graph, NodeId reroute, Func<GraphNode, NodeTypeDefinition?> typeOf) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(typeOf);

        HashSet<NodeId> seen = [];

        for (var at = reroute; seen.Add(at);) {
            if (graph.Source(new(at, In)) is not { } from || !graph.TryGet(from.Node, out var source)) {
                break;
            }

            if (!IsReroute(source.Type)) {
                return typeOf(source)?.Port(from.Port, PortDirection.Output)?.Kind ?? PortKind.None;
            }

            at = from.Node;
        }

        return Downstream(graph, reroute, typeOf, []);
    }

    static PortKind Downstream(
        NodeGraphModel graph,
        NodeId reroute,
        Func<GraphNode, NodeTypeDefinition?> typeOf,
        HashSet<NodeId> seen
    ) {
        if (!seen.Add(reroute)) {
            return PortKind.None;
        }

        foreach (var edge in graph.Edges) {
            if (edge.From.Node != reroute || !graph.TryGet(edge.To.Node, out var target)) {
                continue;
            }

            var kind = IsReroute(target.Type)
                ? Downstream(graph, target.Id, typeOf, seen)
                : typeOf(target)?.Port(edge.To.Port, PortDirection.Input)?.Kind ?? PortKind.None;

            if (kind != PortKind.None) {
                return kind;
            }
        }

        return PortKind.None;
    }
}

/// <summary>Taking bypassed nodes and reroute dots out of a graph, and wiring round them.</summary>
/// <remarks>
///     <para>
///         <b>A rewrite of the graph a compiler walks, not something each compiler does.</b>
///         <see cref="NodeGraphCompiler{TArtefact}" /> runs <see cref="Apply" /> on the author's graph
///         and again after inlining, so a node switched off inside a sub-graph is honoured the same as
///         one in the author's own graph, and no front end's visitor has a "skip it" branch to forget.
///     </para>
///     <para>
///         ⚠ <b>The author's graph is never touched.</b> A graph with nothing to take out comes back
///         as itself; otherwise the answer is a copy, with the same node identities, so a diagnostic
///         about a node downstream still names a node on the author's canvas.
///     </para>
/// </remarks>
public static class NodeBypass {
    /// <summary>The input a node passes on through one of its outputs while it is bypassed.</summary>
    /// <param name="type">The node's type.</param>
    /// <param name="output">The output, by name.</param>
    /// <returns>The first input of the same kind, or null when it has none.</returns>
    /// <remarks>
    ///     ⚠ <b>Same kind, not merely one a wire would accept.</b> A bypassed Levels passes its image
    ///     on; a node whose only input is a number and whose output is an image has nothing to pass,
    ///     and wiring the number through would hand an image input a float.
    /// </remarks>
    public static string? Through(NodeTypeDefinition type, string output) {
        ArgumentNullException.ThrowIfNull(type);

        if (Reroutes.IsReroute(type.Path)) {
            return string.Equals(output, Reroutes.Out, StringComparison.Ordinal) ? Reroutes.In : null;
        }

        if (type.Port(output, PortDirection.Output) is not { } leaving) {
            return null;
        }

        foreach (var port in type.Ports) {
            if (port.Direction == PortDirection.Input && port.Kind == leaving.Kind) {
                return port.Name;
            }
        }

        return null;
    }

    /// <summary>Whether a graph holds anything <see cref="Apply" /> would take out.</summary>
    /// <param name="graph">The graph.</param>
    /// <returns><see langword="true" /> if it does.</returns>
    public static bool Applies(NodeGraphModel graph) {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var node in graph.Nodes) {
            if (node.Bypassed || Reroutes.IsReroute(node.Type)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The graph with its bypassed nodes and reroute dots taken out and wired round.</summary>
    /// <param name="graph">The graph. Not modified.</param>
    /// <param name="typeOf">A node's type, or null when this process has none for it.</param>
    /// <returns><paramref name="graph" /> itself when there is nothing to take out, otherwise a copy.</returns>
    /// <remarks>
    ///     Every wire leaving a taken-out node is traced back through it — and through any chain of
    ///     them — to what feeds its pass-through input, see <see cref="Through" />. A wire with nothing
    ///     at the end of that trace is dropped, so the input it arrived at takes its own value. A
    ///     bypassed node whose type is unknown passes nothing, which is what an unknown node would
    ///     have produced anyway.
    /// </remarks>
    public static NodeGraphModel Apply(NodeGraphModel graph, Func<GraphNode, NodeTypeDefinition?> typeOf) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(typeOf);

        if (!Applies(graph)) {
            return graph;
        }

        NodeGraphModel result = new();

        graph.CopyDocumentTo(result);

        foreach (var node in graph.Nodes) {
            if (Out(node)) {
                continue;
            }

            var copy = result.Add(node.Id, node.Type, node.Position);

            foreach (var (port, value) in node.Values) {
                copy.SetValue(port, [.. value]);
            }

            foreach (var (port, text) in node.Texts) {
                copy.SetText(port, text);
            }
        }

        foreach (var edge in graph.Edges) {
            if (!result.TryGet(edge.To.Node, out _) || Trace(graph, edge.From, typeOf) is not { } from) {
                continue;
            }

            result.Connect(from, edge.To);
        }

        return result;
    }

    /// <summary>The wires that would join what feeds some nodes to what they feed, were they removed.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="removing">The nodes.</param>
    /// <param name="typeOf">A node's type, or null when this process has none for it.</param>
    /// <returns>One wire per input outside the set that a removed node fed and something can still feed.</returns>
    /// <remarks>
    ///     Delete-and-relink: the same trace as <see cref="Apply" />, through the nodes being removed
    ///     instead of the bypassed ones, so removing a node and bypassing it wire the same way.
    /// </remarks>
    public static ImmutableArray<GraphEdge> Bridges(
        NodeGraphModel graph,
        IReadOnlySet<NodeId> removing,
        Func<GraphNode, NodeTypeDefinition?> typeOf
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(removing);
        ArgumentNullException.ThrowIfNull(typeOf);

        var bridges = ImmutableArray.CreateBuilder<GraphEdge>();

        foreach (var edge in graph.Edges) {
            if (removing.Contains(edge.To.Node) || !removing.Contains(edge.From.Node)) {
                continue;
            }

            if (Trace(graph, edge.From, typeOf, removing) is { } from) {
                bridges.Add(new(from, edge.To));
            }
        }

        return bridges.ToImmutable();
    }

    static bool Out(GraphNode node) => node.Bypassed || Reroutes.IsReroute(node.Type);

    /// <summary>Where a wire really comes from, once the nodes being taken out are gone.</summary>
    static PortRef? Trace(
        NodeGraphModel graph,
        PortRef from,
        Func<GraphNode, NodeTypeDefinition?> typeOf,
        IReadOnlySet<NodeId>? removing = null
    ) {
        // Bounded by the node count rather than by a visited set: the graph refuses cycles, so a
        // trace that has not ended after visiting every node once never will.
        for (var steps = 0; steps <= graph.Nodes.Count; steps++) {
            if (!graph.TryGet(from.Node, out var node)) {
                return null;
            }

            var taken = removing?.Contains(node.Id) ?? Out(node);

            if (!taken) {
                return from;
            }

            var type = Reroutes.IsReroute(node.Type) ? Reroutes.Definition(PortKind.None) : typeOf(node);

            if (type is null || Through(type, from.Port) is not { } input || graph.Source(new(node.Id, input)) is not { } upstream) {
                return null;
            }

            from = upstream;
        }

        return null;
    }
}
