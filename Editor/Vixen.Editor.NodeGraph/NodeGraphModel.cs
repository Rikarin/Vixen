// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
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
///     <para>
///         ⚠ <b>Those three rules are not written here.</b> They are
///         <see cref="GraphInvariants" />', and <c>Vixen.Ui.Controls.Advanced.NodeGraph</c> — the
///         graph a canvas draws — calls the same three methods and gets the same
///         <see cref="GraphConnectionError" /> values back. Two graph types is deliberate; two sets
///         of rules was not, and a change to one of them now has nowhere to land but the shared
///         place. What is still different is delivery: see <see cref="Connect" />.
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

    /// <summary>The ports this graph has when another graph contains it as a node.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Declared here rather than derived from the boundary nodes.</b> A sub-graph's entry
    ///         and exit nodes show these ports — see <see cref="SubGraphs" /> — and deriving the list
    ///         from them instead would mean a graph with no exit node had no outputs, so deleting one
    ///         node silently changed the signature every containing graph is wired against.
    ///     </para>
    ///     <para>
    ///         Empty for a graph nobody uses as a sub-graph, which is most of them. Mutating it is not
    ///         noticed; call <see cref="Touch" /> after.
    ///     </para>
    /// </remarks>
    public List<PortDefinition> Interface { get; } = [];

    /// <summary>Raised after anything structural changes.</summary>
    /// <remarks>
    ///     What a view subscribes to. Every method on this type raises it for itself; the three
    ///     public lists and <see cref="GraphNode.Position" /> cannot, because a list does not know
    ///     which graph it is in — so whatever mutated one calls <see cref="Touch" />.
    /// </remarks>
    public event Action<NodeGraphModel>? Changed;

    /// <summary>Says the graph changed in a way it had no way to notice.</summary>
    /// <remarks>
    ///     For the caller who moved a node, retitled a group or edited the interface. The same
    ///     arrangement <c>Vixen.Ui.Controls.Advanced.NodeGraph</c> has, and for the same reason.
    /// </remarks>
    public void Touch() => Changed?.Invoke(this);

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
        Changed?.Invoke(this);

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
        Changed?.Invoke(this);

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
        Changed?.Invoke(this);
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

        // The same cascade the canvas graph runs, for the same reason: an edge naming a node that is
        // not there is one that would be saved and reloaded as a connection to nothing.
        GraphInvariants.Detach(edges, static edge => edge.From.Node, static edge => edge.To.Node, id, removed);

        foreach (var group in Groups) {
            group.Nodes.Remove(id);
        }

        detached = [.. removed];

        Changed?.Invoke(this);

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
    /// <remarks>
    ///     ⚠ <b>It throws where the canvas graph returns null, and that is the only thing the two
    ///     still do differently.</b> This one is called by commands, by a loader and by the sub-graph
    ///     surgery — callers that computed both ends and have no branch for "it did not happen", so a
    ///     refusal here means the caller is wrong and the exception is the report. The canvas graph is
    ///     called at the end of a drag, where a pointer released over a port that cannot take the wire
    ///     is an ordinary outcome and throwing would make every clumsy drop a crash. Use
    ///     <see cref="TryConnect" /> to get the same answer without the difference.
    /// </remarks>
    public GraphEdge? Connect(PortRef from, PortRef to) {
        if (TryConnect(from, to, out var replaced, out var error)) {
            return replaced;
        }

        throw new ArgumentException(
            GraphInvariants.Describe(error, from, to),
            error == GraphConnectionError.FromNotInGraph ? nameof(from) : nameof(to)
        );
    }

    /// <summary>Connects an output to an input, and says why not when it will not.</summary>
    /// <param name="from">The output.</param>
    /// <param name="to">The input.</param>
    /// <param name="replaced">The edge that was displaced, when the input already had one.</param>
    /// <param name="error">Why it was refused, or <see cref="GraphConnectionError.None" />.</param>
    /// <returns><see langword="true" /> if the edge was made.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="GraphConnectionError.WrongDirection" /> never comes back from here.</b> A
    ///     <see cref="PortRef" /> is a node and a name; which side of the node it is on is a fact
    ///     about a node <i>type</i>, and a document deliberately does not depend on a registry — so
    ///     that a graph saved against a missing plugin still holds its wiring. The canvas graph, whose
    ///     ports are objects that know their side, is what reports it, and
    ///     <see cref="NodeGraphView" /> is what refuses a wire on grounds of port kind.
    /// </remarks>
    public bool TryConnect(PortRef from, PortRef to, out GraphEdge? replaced, out GraphConnectionError error) {
        replaced = null;

        if (!nodes.ContainsKey(from.Node)) {
            error = GraphConnectionError.FromNotInGraph;

            return false;
        }

        if (!nodes.ContainsKey(to.Node)) {
            error = GraphConnectionError.ToNotInGraph;

            return false;
        }

        if (from.Node == to.Node) {
            error = GraphConnectionError.SameNode;

            return false;
        }

        if (GraphInvariants.Reaches(
                edges,
                static edge => edge.From.Node,
                static edge => edge.To.Node,
                to.Node,
                from.Node
            )) {
            error = GraphConnectionError.Cycle;

            return false;
        }

        var displaced = GraphInvariants.Arriving(edges, static edge => edge.To, to);

        if (displaced >= 0) {
            replaced = edges[displaced];
            edges.RemoveAt(displaced);
        }

        edges.Add(new(from, to));

        error = GraphConnectionError.None;
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>Disconnects whatever arrives at an input.</summary>
    /// <param name="to">The input.</param>
    /// <returns>The edge that was removed, or null when there was none.</returns>
    public GraphEdge? Disconnect(PortRef to) {
        var index = GraphInvariants.Arriving(edges, static edge => edge.To, to);

        if (index < 0) {
            return null;
        }

        var edge = edges[index];

        edges.RemoveAt(index);
        Changed?.Invoke(this);

        return edge;
    }

    /// <summary>What arrives at an input, if anything.</summary>
    /// <param name="to">The input.</param>
    /// <returns>The output feeding it, or null.</returns>
    public PortRef? Source(PortRef to) {
        var index = GraphInvariants.Arriving(edges, static edge => edge.To, to);

        return index >= 0 ? edges[index].From : null;
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
}
