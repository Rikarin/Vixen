// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Which way data flows through a port.</summary>
public enum PortDirection : byte {
    /// <summary>It consumes. A wire ends here.</summary>
    Input,

    /// <summary>It produces. A wire starts here.</summary>
    Output
}

/// <summary>What an unconnected port's value is typed into.</summary>
public enum PortEditorKind : byte {
    /// <summary>Nothing. The port is a socket and no more.</summary>
    None,

    /// <summary>A box of digits per lane.</summary>
    Number,

    /// <summary>A tick.</summary>
    Toggle
}

/// <summary>The value a port takes when nothing is wired to it, as the canvas shows it.</summary>
/// <remarks>
///     <para>
///         <b>A picture of a value, not the value.</b> What a port's number <i>is</i> lives in the
///         document — <c>Vixen.Editor.NodeGraph.GraphNode.Values</c> — because that is what is saved,
///         undone and compiled. This is what the canvas draws and what a gesture writes into before
///         anything is recorded, the same bargain <see cref="NodeGraph.Connect" /> makes: the picture
///         moves first and the command follows, because a field that waited for a round trip through
///         a command stack would drop keystrokes.
///     </para>
///     <para>
///         ⚠ <b>Lanes are fixed at construction.</b> A port that is a <c>float3</c> is three boxes for
///         as long as it exists, and a caller that wants a different width makes a different editor —
///         which is what a reprojection does anyway. Growing one in place would mean the element pool
///         showing it had to notice, and it is rebound rather than rebuilt.
///     </para>
/// </remarks>
public sealed class PortEditor {
    readonly float[] value;

    /// <summary>Creates one.</summary>
    /// <param name="kind">What it looks like.</param>
    /// <param name="lanes">How many numbers it holds. One for a toggle.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lanes" /> is not one to four.</exception>
    public PortEditor(PortEditorKind kind, int lanes = 1) {
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lanes, 4);

        Kind = kind;
        value = new float[lanes];
    }

    /// <summary>What it looks like.</summary>
    public PortEditorKind Kind { get; }

    /// <summary>How many numbers it holds.</summary>
    public int Lanes => value.Length;

    /// <summary>How many decimal places each box shows.</summary>
    public int Decimals { get; set; } = 3;

    /// <summary>Whether it may be typed into.</summary>
    /// <remarks>
    ///     A graph shown without an undo stack behind it still shows its numbers — a sub-graph being
    ///     previewed is worth reading — and a field that silently accepted an edit nothing recorded
    ///     would be worse than one that refuses.
    /// </remarks>
    public bool ReadOnly { get; set; }

    /// <summary>What the letter beside each box says, or empty for none.</summary>
    /// <remarks>
    ///     Four boxes in a row are unreadable without them and one box is unreadable with one, so a
    ///     single-lane editor is normally given none.
    /// </remarks>
    public string LaneNames { get; set; } = "";

    /// <summary>One lane.</summary>
    /// <param name="lane">Which.</param>
    /// <returns>Its value.</returns>
    public float this[int lane] {
        get => value[lane];
        set => this.value[lane] = value;
    }

    /// <summary>Whether it is on, for a <see cref="PortEditorKind.Toggle" />.</summary>
    public bool IsOn {
        get => value[0] != 0f;
        set => this.value[0] = value ? 1f : 0f;
    }

    /// <summary>Fills the lanes from a span, padding with the last one it was given.</summary>
    /// <param name="lanes">The values. A shorter span pads, a longer one is truncated.</param>
    /// <remarks>
    ///     Padded with the last rather than with zero, for the reason a shader compiler pads a
    ///     constant the same way: a one-number default on a port being shown as three means "and the
    ///     same again", not "and then black".
    /// </remarks>
    public void Set(ReadOnlySpan<float> lanes) {
        for (var index = 0; index < value.Length; index++) {
            value[index] = lanes.IsEmpty ? 0f : lanes[Math.Min(index, lanes.Length - 1)];
        }
    }

    /// <summary>The lanes, copied.</summary>
    /// <returns>A fresh array.</returns>
    public float[] ToArray() => [.. value];
}

/// <summary>One socket on the side of a node.</summary>
/// <remarks>
///     ⚠ <b>A port knows where it is without being laid out.</b>
///     <see cref="NodeCanvas.AnchorOf" /> works the position out from the node's rectangle and the
///     port's index rather than from a port element's box, and that is load-bearing: the node at the
///     far end of a wire is very often scrolled off the canvas and has no elements at all. A wire
///     whose endpoint came from layout would be drawn to the origin whenever the thing it connects
///     to was out of sight.
/// </remarks>
public sealed class GraphPort {
    internal GraphPort(GraphNode node, string name, PortDirection direction, int index) {
        Node = node;
        Name = name;
        Direction = direction;
        Index = index;
    }

    /// <summary>The node it belongs to.</summary>
    public GraphNode Node { get; }

    /// <summary>What it is called.</summary>
    public string Name { get; set; }

    /// <summary>Which side it is on.</summary>
    public PortDirection Direction { get; }

    /// <summary>Where it comes among the ports on its own side.</summary>
    public int Index { get; internal set; }

    /// <summary>Whether more than one wire may end here.</summary>
    /// <remarks>
    ///     ⚠ <b>An input takes one wire and an output takes many</b>, which is the asymmetry every
    ///     data-flow graph has: a value can feed anything, and a slot that expected one value cannot
    ///     be handed two. <see cref="NodeGraph.Connect" /> enforces it by replacing rather than
    ///     refusing, because a user who drags a second wire into an occupied input means to change
    ///     what feeds it. The rule itself is <see cref="GraphInvariants.Arriving" />'s, so the
    ///     document model enforces the same one.
    /// </remarks>
    public bool AllowsMany => Direction == PortDirection.Output;

    /// <summary>What an author types into it when nothing is wired to it, or <c>null</c> for nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Set on inputs, and only shown while the input is unconnected.</b> A port fed by a wire
    ///     takes its value from that wire, and a box of digits beside it would be a number the
    ///     compiler ignores — which is how somebody comes to spend an afternoon changing a field that
    ///     does nothing. The canvas hides it rather than the projection removing it, because the
    ///     canvas is the thing that knows a wire was dropped a frame before anything is recorded.
    /// </remarks>
    public PortEditor? Editor { get; set; }

    /// <summary>Whatever the application wants to hang off it.</summary>
    public object? Tag { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"{Node.Title}.{Name}";
}

/// <summary>One box on the canvas.</summary>
public sealed class GraphNode {
    readonly List<GraphPort> inputs = [];
    readonly List<GraphPort> outputs = [];

    /// <summary>Creates a node.</summary>
    /// <param name="title">What is written across the top of it.</param>
    public GraphNode(string title = "") => Title = title;

    /// <summary>What is written across the top of it.</summary>
    public string Title { get; set; }

    /// <summary>Its top-left corner, in graph space.</summary>
    public Vector2 Position { get; set; }

    /// <summary>How wide it is, in graph units.</summary>
    public float Width { get; set; } = 140f;

    /// <summary>Its ports, in order down the left side.</summary>
    public IReadOnlyList<GraphPort> Inputs => inputs;

    /// <summary>Its ports, in order down the right side.</summary>
    public IReadOnlyList<GraphPort> Outputs => outputs;

    /// <summary>The group it is in, if any.</summary>
    public GraphGroup? Group { get; internal set; }

    /// <summary>Whatever the application wants to hang off it.</summary>
    public object? Tag { get; set; }

    /// <summary>Adds a port on the left.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The port.</returns>
    public GraphPort AddInput(string name) {
        var port = new GraphPort(this, name, PortDirection.Input, inputs.Count);
        inputs.Add(port);

        return port;
    }

    /// <summary>Adds a port on the right.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The port.</returns>
    public GraphPort AddOutput(string name) {
        var port = new GraphPort(this, name, PortDirection.Output, outputs.Count);
        outputs.Add(port);

        return port;
    }

    /// <summary>How many ports the taller of its two sides has.</summary>
    public int Rows => Math.Max(inputs.Count, outputs.Count);

    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>A connection from an output to an input.</summary>
public sealed class GraphWire {
    internal GraphWire(GraphPort from, GraphPort to) {
        From = from;
        To = to;
    }

    /// <summary>The output end.</summary>
    public GraphPort From { get; }

    /// <summary>The input end.</summary>
    public GraphPort To { get; }

    /// <inheritdoc />
    public override string ToString() => $"{From} → {To}";
}

/// <summary>A titled rectangle that some nodes are inside.</summary>
/// <remarks>
///     Membership is a list rather than a hit test against the rectangle. A group whose contents
///     were "whatever is inside it right now" would gain and lose members as nodes were dragged
///     past, which makes "move this group" a gesture whose effect depends on what happened to be
///     overlapping at the time.
/// </remarks>
public sealed class GraphGroup {
    readonly List<GraphNode> nodes = [];

    /// <summary>Creates a group.</summary>
    /// <param name="title">Its label.</param>
    public GraphGroup(string title = "") => Title = title;

    /// <summary>Its label.</summary>
    public string Title { get; set; }

    /// <summary>What is in it.</summary>
    public IReadOnlyList<GraphNode> Nodes => nodes;

    /// <summary>How much room is left around the nodes inside it.</summary>
    public float Padding { get; set; } = 16f;

    /// <summary>How much room the title bar takes above them.</summary>
    public float HeaderHeight { get; set; } = 20f;

    /// <summary>Takes a node into the group, out of whatever group it was in.</summary>
    /// <param name="node">The node.</param>
    public void Add(GraphNode node) {
        ArgumentNullException.ThrowIfNull(node);

        node.Group?.nodes.Remove(node);
        node.Group = this;

        nodes.Add(node);
    }

    /// <summary>Takes a node out.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether it was in.</returns>
    public bool Remove(GraphNode node) {
        ArgumentNullException.ThrowIfNull(node);

        if (!nodes.Remove(node)) {
            return false;
        }

        node.Group = null;
        return true;
    }
}

/// <summary>The nodes, the wires and the groups. No elements, no canvas, no view.</summary>
/// <remarks>
///     <para>
///         <b>Kept apart from <see cref="NodeCanvas" /> on purpose</b>, for the reason
///         <c>DockLayout</c> is kept apart from <c>DockingHost</c>: this is what a shader graph is
///         saved as, compiled from and compared against, and a model that could only be read out of
///         a live element tree would make every one of those depend on a document, a stylesheet and
///         a font.
///     </para>
///     <para>
///         ⚠ <b>It refuses a cycle.</b> A shader graph and a VFX graph are both evaluated by walking
///         from the outputs back, and a cycle is not a graph with a mistake in it — it is a walk
///         that does not terminate. Refusing at the moment of connection is the only place the user
///         can be told which wire was the problem.
///     </para>
///     <para>
///         ⚠ <b>The rules are not this type's.</b> The cycle refusal, the cascade a deletion causes
///         and the one-wire-per-input rule are <see cref="GraphInvariants" />'s, and the document
///         model — <c>Vixen.Editor.NodeGraph.NodeGraphModel</c> — calls the same three methods with
///         the same <see cref="GraphConnectionError" /> coming back. That is deliberate and it is the
///         only thing keeping two graph types from drifting into two sets of rules. What differs is
///         how a refusal is delivered: see <see cref="Connect" />.
///     </para>
/// </remarks>
public sealed class NodeGraph {
    readonly List<GraphNode> nodes = [];
    readonly List<GraphWire> wires = [];
    readonly List<GraphGroup> groups = [];

    /// <summary>The nodes, in the order they were added.</summary>
    public IReadOnlyList<GraphNode> Nodes => nodes;

    /// <summary>The wires.</summary>
    public IReadOnlyList<GraphWire> Wires => wires;

    /// <summary>The groups.</summary>
    public IReadOnlyList<GraphGroup> Groups => groups;

    /// <summary>Raised after anything structural changes — a node, a wire or a group.</summary>
    public event Action<NodeGraph>? Changed;

    /// <summary>Adds a node.</summary>
    /// <param name="title">Its title.</param>
    /// <param name="position">Where it goes, in graph space.</param>
    /// <returns>The node.</returns>
    public GraphNode AddNode(string title, Vector2 position = default) {
        var node = new GraphNode(title) { Position = position };
        nodes.Add(node);

        Changed?.Invoke(this);
        return node;
    }

    /// <summary>Adds a node that was made elsewhere.</summary>
    /// <param name="node">The node.</param>
    /// <returns>It.</returns>
    public GraphNode AddNode(GraphNode node) {
        ArgumentNullException.ThrowIfNull(node);

        nodes.Add(node);
        Changed?.Invoke(this);

        return node;
    }

    /// <summary>Takes a node out, and every wire that touched it.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(GraphNode node) {
        ArgumentNullException.ThrowIfNull(node);

        if (!nodes.Remove(node)) {
            return false;
        }

        // ⚠ The wires go with it, by the same cascade the document model uses — see
        // GraphInvariants.Detach for why a graph that kept them would reload with a connection to a
        // node that does not exist.
        GraphInvariants.Detach(wires, static wire => wire.From.Node, static wire => wire.To.Node, node);
        node.Group?.Remove(node);

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>Adds a group.</summary>
    /// <param name="title">Its label.</param>
    /// <param name="members">What goes in it.</param>
    /// <returns>The group.</returns>
    public GraphGroup AddGroup(string title, params ReadOnlySpan<GraphNode> members) {
        var group = new GraphGroup(title);

        foreach (var node in members) {
            group.Add(node);
        }

        groups.Add(group);
        Changed?.Invoke(this);

        return group;
    }

    /// <summary>Takes a group out, leaving its nodes where they are.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(GraphGroup group) {
        ArgumentNullException.ThrowIfNull(group);

        if (!groups.Remove(group)) {
            return false;
        }

        foreach (var node in group.Nodes.ToArray()) {
            group.Remove(node);
        }

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>Connects an output to an input.</summary>
    /// <param name="from">The output end. The two may be given either way round.</param>
    /// <param name="to">The input end.</param>
    /// <returns>The wire, or <c>null</c> if the connection is not allowed.</returns>
    /// <remarks>
    ///     ⚠ <b>A refusal is null here and an exception on the document model, and that difference is
    ///     the whole of the difference between them.</b> This overload is what a drag ends in: a
    ///     pointer released over a port that cannot take the wire is an ordinary outcome of an
    ///     ordinary gesture, and a canvas that threw would turn every clumsy drop into a crash. The
    ///     document model is called by commands and by a loader, where a connection that cannot be
    ///     made means the caller is wrong and should say so loudly. Same rules, same
    ///     <see cref="GraphConnectionError" /> values — <see cref="TryConnect" /> is where they are
    ///     applied, and both models go through their own thin wrapper over it.
    /// </remarks>
    public GraphWire? Connect(GraphPort from, GraphPort to) => TryConnect(from, to, out _);

    /// <summary>Connects an output to an input, and says why not when it will not.</summary>
    /// <param name="from">The output end. The two may be given either way round.</param>
    /// <param name="to">The input end.</param>
    /// <param name="error">Why it was refused, or <see cref="GraphConnectionError.None" />.</param>
    /// <returns>The wire, or <c>null</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>An occupied input is replaced rather than refused.</b> Dragging a second wire into a
    ///     slot that already has one is how everybody rewires a graph, and a version that refused
    ///     would make the gesture "delete the old wire, then drag the new one" — two steps for one
    ///     intention, and the first of them has no obvious affordance.
    ///     <para>
    ///         <see cref="GraphConnectionError.WrongDirection" /> is the one reason only this model
    ///         can give: a port here knows which side it is on, and a
    ///         <c>Vixen.Editor.NodeGraph.PortRef</c> is a name and does not.
    ///     </para>
    /// </remarks>
    public GraphWire? TryConnect(GraphPort from, GraphPort to, out GraphConnectionError error) {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        // Either way round, because a user drags from whichever end they happened to grab and a
        // canvas that only accepted output-to-input would silently do nothing half the time.
        if (from.Direction == PortDirection.Input && to.Direction == PortDirection.Output) {
            (from, to) = (to, from);
        }

        if (from.Direction != PortDirection.Output || to.Direction != PortDirection.Input) {
            error = GraphConnectionError.WrongDirection;

            return null;
        }

        // Checked because the document model checks it, and a contract the two share only holds if
        // both hold it. A port of a node in another graph is a wire this one would draw from an
        // anchor it does not own and could never save.
        if (!nodes.Contains(from.Node)) {
            error = GraphConnectionError.FromNotInGraph;

            return null;
        }

        if (!nodes.Contains(to.Node)) {
            error = GraphConnectionError.ToNotInGraph;

            return null;
        }

        if (ReferenceEquals(from.Node, to.Node)) {
            error = GraphConnectionError.SameNode;

            return null;
        }

        if (GraphInvariants.Reaches(
                wires,
                static wire => wire.From.Node,
                static wire => wire.To.Node,
                to.Node,
                from.Node
            )) {
            error = GraphConnectionError.Cycle;

            return null;
        }

        var displaced = GraphInvariants.Arriving(wires, static wire => wire.To, to);

        if (displaced >= 0) {
            wires.RemoveAt(displaced);
        }

        var wire = new GraphWire(from, to);
        wires.Add(wire);

        error = GraphConnectionError.None;
        Changed?.Invoke(this);

        return wire;
    }

    /// <summary>Removes a wire.</summary>
    /// <param name="wire">The wire.</param>
    /// <returns>Whether it was there.</returns>
    public bool Disconnect(GraphWire wire) {
        ArgumentNullException.ThrowIfNull(wire);

        if (!wires.Remove(wire)) {
            return false;
        }

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>The wire ending at an input, if it has one.</summary>
    /// <param name="port">The port.</param>
    /// <returns>The wire, or <c>null</c>.</returns>
    public GraphWire? Wire(GraphPort port) {
        ArgumentNullException.ThrowIfNull(port);

        var index = GraphInvariants.Arriving(wires, static wire => wire.To, port);

        return index >= 0 ? wires[index] : null;
    }

    /// <summary>Tells the canvas the model changed under it.</summary>
    /// <remarks>
    ///     For the caller who edited a node's title or moved one by hand. Everything on this type
    ///     raises <see cref="Changed" /> for itself; a property on <see cref="GraphNode" /> cannot,
    ///     because a node does not know which graph it is in.
    /// </remarks>
    public void Touch() => Changed?.Invoke(this);
}
