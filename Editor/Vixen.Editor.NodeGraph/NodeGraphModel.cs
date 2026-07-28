// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.NodeGraph;

/// <summary>A node's identity within one graph.</summary>
/// <param name="Value">The number. Assigned by the graph and never reused.</param>
/// <remarks>
///     Never reused, so an undo that re-adds a deleted node re-adds it with the identity it had —
///     which is what lets the edges that pointed at it be restored by the same command without
///     rewriting them.
/// </remarks>
public readonly record struct NodeId(int Value) {
    /// <summary>No node.</summary>
    public static NodeId None => new(0);

    /// <summary>Whether this names a node at all.</summary>
    public bool IsValid => Value != 0;

    /// <inheritdoc />
    public override string ToString() => IsValid ? $"#{Value}" : "#none";
}

/// <summary>One end of an edge: a port of a node.</summary>
/// <param name="Node">Which node.</param>
/// <param name="Port">Which of its ports, by name.</param>
public readonly record struct PortRef(NodeId Node, string Port) {
    /// <inheritdoc />
    public override string ToString() => $"{Node}.{Port}";
}

/// <summary>One connection, from an output to an input.</summary>
/// <param name="From">The output it leaves.</param>
/// <param name="To">The input it arrives at.</param>
public readonly record struct GraphEdge(PortRef From, PortRef To);

/// <summary>One node in a graph.</summary>
/// <remarks>
///     Deliberately not an instance of the node <i>class</i>. A graph is a document — it is saved,
///     loaded, undone and compared — and the class is a thing the compiler makes when it needs one.
///     Keeping them apart is what lets a graph hold a node type that is not registered in this
///     process, which is exactly what a graph saved against a missing plugin is.
/// </remarks>
public sealed class GraphNode {
    readonly Dictionary<string, float[]> values = new(StringComparer.Ordinal);

    internal GraphNode(NodeId id, string type, Vector2 position) {
        Id = id;
        Type = type;
        Position = position;
    }

    /// <summary>Its identity.</summary>
    public NodeId Id { get; }

    /// <summary>Which node type it is, by path.</summary>
    public string Type { get; }

    /// <summary>Where it sits on the canvas.</summary>
    public Vector2 Position { get; set; }

    /// <summary>The inline values its unconnected inputs have been given.</summary>
    /// <remarks>
    ///     Only the ones an author has actually changed. A port that is not here takes the default
    ///     from its <see cref="PortDefinition" />, so a node type that changes a default changes it
    ///     for every saved graph that never overrode it — which is the behaviour a default is for.
    /// </remarks>
    public IReadOnlyDictionary<string, float[]> Values => values;

    /// <summary>Sets an inline value.</summary>
    /// <param name="port">The input port's name.</param>
    /// <param name="value">Its lanes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public void SetValue(string port, params float[] value) {
        ArgumentNullException.ThrowIfNull(value);
        values[port] = value;
    }

    /// <summary>Forgets an inline value, so the port takes its type's default again.</summary>
    /// <param name="port">The input port's name.</param>
    /// <returns><see langword="true" /> if there was one.</returns>
    public bool ClearValue(string port) => values.Remove(port);
}

/// <summary>A box drawn round some nodes, with a title.</summary>
public sealed class GraphGroup {
    /// <summary>What the group is called.</summary>
    public string Title { get; set; } = "Group";

    /// <summary>Which nodes are in it.</summary>
    public List<NodeId> Nodes { get; } = [];
}

/// <summary>A sticky note.</summary>
/// <remarks>
///     In the model rather than in the view because it is content: it is saved, it is diffed, and it
///     is the only place a graph can say <i>why</i>. A comment that lived in the view would be lost
///     the first time anyone opened the file in a different one.
/// </remarks>
public sealed class GraphComment {
    /// <summary>What it says.</summary>
    public string Text { get; set; } = "";

    /// <summary>Where it sits.</summary>
    public Vector2 Position { get; set; }

    /// <summary>How big it is.</summary>
    public Vector2 Size { get; set; } = new(200f, 100f);
}

/// <summary>
///     A graph: nodes, the edges between them, and the furniture round them.
/// </summary>
/// <remarks>
///     <para>
///         <b>One model for three graphs.</b> Shader, VFX and animation graphs differ in what their
///         nodes mean and in what they compile to, not in what a graph <i>is</i>. Building three of
///         these is three times the work of building one, and doc 11 says so.
///     </para>
///     <para>
///         <b>It refuses cycles as they are made, not when they are compiled.</b> A graph that cannot
///         contain a cycle is a graph <see cref="NodeGraphCompiler" /> can walk without a visited set
///         and a graph a view can lay out without one. The alternative — allow it, report it later —
///         means every consumer has to be robust against a structure the model already knows is
///         wrong, and it means an author finds out about a mistake at a different time from making it.
///     </para>
///     <para>
///         <b>An input takes one edge.</b> Connecting a second replaces the first, because that is
///         what an author dragging a wire onto an occupied port means every time. An output takes any
///         number; a value read twice is read twice.
///     </para>
/// </remarks>
public sealed class NodeGraphModel {
    readonly Dictionary<NodeId, GraphNode> nodes = [];
    readonly List<GraphEdge> edges = [];

    int next;

    /// <summary>What the graph is called. A sub-graph is referenced by this.</summary>
    public string Name { get; set; } = "";

    /// <summary>Its nodes, in insertion order of identity.</summary>
    public IReadOnlyCollection<GraphNode> Nodes => nodes.Values;

    /// <summary>Its edges.</summary>
    public IReadOnlyList<GraphEdge> Edges => edges;

    /// <summary>Its group boxes.</summary>
    public List<GraphGroup> Groups { get; } = [];

    /// <summary>Its sticky notes.</summary>
    public List<GraphComment> Comments { get; } = [];

    /// <summary>Adds a node.</summary>
    /// <param name="type">Which node type, by path.</param>
    /// <param name="position">Where it sits.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentException"><paramref name="type" /> is empty.</exception>
    /// <remarks>
    ///     The type is not checked against a registry, deliberately. A graph is a document and a
    ///     registry is a thing this process happens to have loaded; refusing to hold a node whose
    ///     plugin is missing would mean opening such a file destroyed it.
    /// </remarks>
    public GraphNode Add(string type, Vector2 position = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var node = new GraphNode(new(++next), type, position);

        nodes.Add(node.Id, node);

        return node;
    }

    /// <summary>Adds a node under an identity chosen by the caller.</summary>
    /// <param name="id">The identity it is to have.</param>
    /// <param name="type">Which node type, by path.</param>
    /// <param name="position">Where it sits.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="id" /> is not a valid identity or is already taken, or
    ///     <paramref name="type" /> is empty.
    /// </exception>
    /// <remarks>
    ///     For a loader, which has to preserve the identities the file gave. Renumbering on load would
    ///     mean a save, load and save cycle rewrote every node and every edge in the file, and the
    ///     diff of a graph nobody changed would be the whole graph.
    /// </remarks>
    public GraphNode Add(NodeId id, string type, Vector2 position = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (!id.IsValid) {
            throw new ArgumentException($"{id} is not an identity a node can have.", nameof(id));
        }

        var node = new GraphNode(id, type, position);

        if (!nodes.TryAdd(id, node)) {
            throw new ArgumentException($"{id} is already in this graph.", nameof(id));
        }

        next = Math.Max(next, id.Value);

        return node;
    }

    /// <summary>Adds a node back under the identity it had, for an undo.</summary>
    /// <param name="node">The node, as <see cref="Remove" /> returned it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="node" /> is null.</exception>
    /// <exception cref="ArgumentException">That identity is taken.</exception>
    /// <remarks>
    ///     The reason <see cref="NodeId" />s are never reused. An undo that re-added a node under a
    ///     fresh identity would have to rewrite every edge the redo is about to restore, and the two
    ///     lists would have to agree about the order they did it in.
    /// </remarks>
    public void Restore(GraphNode node) {
        ArgumentNullException.ThrowIfNull(node);

        if (!nodes.TryAdd(node.Id, node)) {
            throw new ArgumentException($"{node.Id} is already in this graph.", nameof(node));
        }

        next = Math.Max(next, node.Id.Value);
    }

    /// <summary>One node by identity.</summary>
    /// <param name="id">Its identity.</param>
    /// <param name="node">The node, when there is one.</param>
    /// <returns><see langword="true" /> if there is.</returns>
    public bool TryGet(NodeId id, [NotNullWhen(true)] out GraphNode? node) => nodes.TryGetValue(id, out node);

    /// <summary>Removes a node and every edge touching it.</summary>
    /// <param name="id">Its identity.</param>
    /// <param name="detached">The edges that were removed with it, for an undo to put back.</param>
    /// <returns>The node, or null when there was none.</returns>
    public GraphNode? Remove(NodeId id, out GraphEdge[] detached) {
        if (!nodes.Remove(id, out var node)) {
            detached = [];

            return null;
        }

        List<GraphEdge> removed = [];

        for (var index = edges.Count - 1; index >= 0; index--) {
            if (edges[index].From.Node == id || edges[index].To.Node == id) {
                removed.Add(edges[index]);
                edges.RemoveAt(index);
            }
        }

        foreach (var group in Groups) {
            group.Nodes.Remove(id);
        }

        removed.Reverse();
        detached = [.. removed];

        return node;
    }

    /// <summary>Connects an output to an input.</summary>
    /// <param name="from">The output.</param>
    /// <param name="to">The input.</param>
    /// <returns>The edge that was replaced, when the input already had one.</returns>
    /// <exception cref="ArgumentException">
    ///     Either end names a node the graph does not have, both ends are the same node, or the edge
    ///     would close a cycle.
    /// </exception>
    public GraphEdge? Connect(PortRef from, PortRef to) {
        if (!nodes.ContainsKey(from.Node)) {
            throw new ArgumentException($"{from.Node} is not in this graph.", nameof(from));
        }

        if (!nodes.ContainsKey(to.Node)) {
            throw new ArgumentException($"{to.Node} is not in this graph.", nameof(to));
        }

        if (from.Node == to.Node) {
            throw new ArgumentException("A node cannot be connected to itself.", nameof(to));
        }

        if (Reaches(to.Node, from.Node)) {
            throw new ArgumentException(
                $"Connecting {from} to {to} would close a cycle: {to.Node} already feeds {from.Node}.",
                nameof(to)
            );
        }

        GraphEdge? replaced = null;

        for (var index = 0; index < edges.Count; index++) {
            if (edges[index].To == to) {
                replaced = edges[index];
                edges.RemoveAt(index);

                break;
            }
        }

        edges.Add(new(from, to));

        return replaced;
    }

    /// <summary>Disconnects whatever arrives at an input.</summary>
    /// <param name="to">The input.</param>
    /// <returns>The edge that was removed, or null when there was none.</returns>
    public GraphEdge? Disconnect(PortRef to) {
        for (var index = 0; index < edges.Count; index++) {
            if (edges[index].To == to) {
                var edge = edges[index];

                edges.RemoveAt(index);

                return edge;
            }
        }

        return null;
    }

    /// <summary>What arrives at an input, if anything.</summary>
    /// <param name="to">The input.</param>
    /// <returns>The output feeding it, or null.</returns>
    public PortRef? Source(PortRef to) {
        foreach (var edge in edges) {
            if (edge.To == to) {
                return edge.From;
            }
        }

        return null;
    }

    /// <summary>
    ///     The nodes in an order where every node comes after everything feeding it.
    /// </summary>
    /// <returns>The order.</returns>
    /// <remarks>
    ///     Kahn's algorithm, and it cannot fail: the graph refuses cycles as they are made, so every
    ///     node's in-degree reaches zero. That is the return on refusing them early — a topological
    ///     sort with no error path is one every consumer can use without deciding what to do when
    ///     there is not one.
    /// </remarks>
    public IReadOnlyList<GraphNode> Ordered() {
        Dictionary<NodeId, int> incoming = [];

        foreach (var node in nodes.Values) {
            incoming[node.Id] = 0;
        }

        foreach (var edge in edges) {
            incoming[edge.To.Node]++;
        }

        Queue<NodeId> ready = [];

        // Insertion order, so a graph with no edges compiles in the order it was built rather than in
        // whatever order the dictionary happens to enumerate. A golden source test needs that.
        foreach (var node in nodes.Values) {
            if (incoming[node.Id] == 0) {
                ready.Enqueue(node.Id);
            }
        }

        List<GraphNode> order = new(nodes.Count);

        while (ready.Count > 0) {
            var id = ready.Dequeue();

            order.Add(nodes[id]);

            foreach (var edge in edges) {
                if (edge.From.Node != id) {
                    continue;
                }

                if (--incoming[edge.To.Node] == 0) {
                    ready.Enqueue(edge.To.Node);
                }
            }
        }

        return order;
    }

    /// <summary>Whether one node feeds another, directly or through any number of others.</summary>
    bool Reaches(NodeId from, NodeId to) {
        if (from == to) {
            return true;
        }

        Queue<NodeId> pending = new([from]);
        HashSet<NodeId> seen = [from];

        while (pending.Count > 0) {
            var current = pending.Dequeue();

            foreach (var edge in edges) {
                if (edge.From.Node != current || !seen.Add(edge.To.Node)) {
                    continue;
                }

                if (edge.To.Node == to) {
                    return true;
                }

                pending.Enqueue(edge.To.Node);
            }
        }

        return false;
    }
}
