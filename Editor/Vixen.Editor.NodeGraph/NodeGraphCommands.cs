// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;

namespace Vixen.Editor.NodeGraph;

/// <summary>The half of an undoable graph edit that every one of them shares.</summary>
/// <remarks>
///     <para>
///         <b>A command holds the model, not the document.</b> A graph is edited the same way whether
///         it is an open asset, a sub-graph being previewed, or a fixture in a test with no project
///         behind it — so the document is optional and its only job is to be marked dirty. That is
///         also what makes every command here testable without a command stack.
///     </para>
///     <para>
///         ⚠ <b>Undo depends on identities never being reused.</b> <see cref="NodeGraphModel.Restore" />
///         puts a node back under the identity it had, which is what lets the edges a deletion took
///         with it be restored by name rather than rewritten. Every command below relies on it, and a
///         model that renumbered on undo would break all of them at once.
///     </para>
/// </remarks>
public abstract class NodeGraphCommand : IEditorCommand {
    /// <summary>Starts a command against a graph.</summary>
    /// <param name="graph">The graph it edits.</param>
    /// <param name="document">The document to mark as touched, when the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    protected NodeGraphCommand(NodeGraphModel graph, EditorDocument? document) {
        ArgumentNullException.ThrowIfNull(graph);

        Graph = graph;
        Document = document;
    }

    /// <summary>The graph being edited.</summary>
    protected NodeGraphModel Graph { get; }

    /// <summary>The document it belongs to, if it belongs to one.</summary>
    protected EditorDocument? Document { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply();
        Touch(context);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Revert();
        Touch(context);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Declared here, and virtual, rather than left to the interface's default.</b> Interface
    ///     mapping is fixed at the type that lists the interface — this one — so a derived class that
    ///     merely declared a matching method would never be called through <see cref="IEditorCommand" />
    ///     and its merging would silently do nothing. It has to be an override of something on this
    ///     class, and it therefore has to exist on this class.
    /// </remarks>
    public virtual bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        return false;
    }

    /// <summary>Makes the change. Called once on execute and again on every redo.</summary>
    protected abstract void Apply();

    /// <summary>Puts back exactly what <see cref="Apply" /> changed.</summary>
    protected abstract void Revert();

    /// <summary>Marks the document, and tells whatever is showing the graph to catch up.</summary>
    /// <remarks>
    ///     <see cref="NodeGraphModel.Touch" /> because the three lists and a node's position are
    ///     mutated in place by several of these, and a list does not know which graph it is in. It is
    ///     cheap and idempotent, so raising it once more than strictly needed costs a realise.
    /// </remarks>
    void Touch(EditorContext context) {
        Graph.Touch();

        if (Document is not null) {
            context.Touch(Document);
        }
    }
}

/// <summary>Putting one node into a graph.</summary>
public sealed class AddNodeCommand : NodeGraphCommand {
    readonly string type;
    readonly Vector2 position;

    GraphNode? node;
    GraphEdge[] detached = [];

    /// <summary>Describes adding a node.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="type">Which node type, by path.</param>
    /// <param name="position">Where it goes.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentException"><paramref name="type" /> is empty.</exception>
    public AddNodeCommand(NodeGraphModel graph, string type, Vector2 position, EditorDocument? document = null)
        : base(graph, document) {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        this.type = type;
        this.position = position;
    }

    /// <inheritdoc />
    public override string Name => $"Add {type[(type.LastIndexOf('/') + 1)..]}";

    /// <summary>The node, once it has been added.</summary>
    /// <remarks>
    ///     The same node on every redo, because <see cref="NodeGraphModel.Restore" /> puts it back
    ///     under its own identity. That is what lets a caller connect something to it and have the
    ///     connection survive an undo and a redo of both.
    /// </remarks>
    public GraphNode Node =>
        node ?? throw new InvalidOperationException("The node does not exist until the command has been executed.");

    /// <inheritdoc />
    protected override void Apply() {
        if (node is null) {
            node = Graph.Add(type, position);

            return;
        }

        Graph.Restore(node);

        // Edges only exist here when something connected to the node and was undone past this point
        // and redone — in which case they were detached by this command's own Revert.
        foreach (var edge in detached) {
            Graph.Connect(edge.From, edge.To);
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        if (node is not null) {
            Graph.Remove(node.Id, out detached);
        }
    }
}

/// <summary>Taking some nodes out of a graph, and the wires and group memberships with them.</summary>
/// <remarks>
///     ⚠ <b>One command for the whole selection.</b> Deleting five nodes is one edit and one keystroke
///     puts it back; a composite of five would undo correctly and would fill the history with five
///     entries nobody can read — the same argument <c>SetMembersCommand</c> makes about a
///     multi-object field edit.
/// </remarks>
public sealed class RemoveNodesCommand : NodeGraphCommand {
    readonly ImmutableArray<NodeId> targets;
    readonly List<GraphNode> removed = [];
    readonly List<GraphEdge> detached = [];
    readonly List<(GraphGroup Group, int Index, NodeId Node)> memberships = [];

    /// <summary>Describes removing some nodes.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="nodes">Which nodes. Ones the graph has not got are ignored.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes" /> is null.</exception>
    public RemoveNodesCommand(NodeGraphModel graph, IEnumerable<NodeId> nodes, EditorDocument? document = null)
        : base(graph, document) {
        ArgumentNullException.ThrowIfNull(nodes);

        targets = [.. nodes];
    }

    /// <inheritdoc />
    public override string Name => targets.Length > 1 ? $"Delete {targets.Length} Nodes" : "Delete Node";

    /// <inheritdoc />
    protected override void Apply() {
        removed.Clear();
        detached.Clear();
        memberships.Clear();

        // Group membership is read before the removal, because NodeGraphModel.Remove strips it and
        // does not say where from — and a node that came back at the end of a group it was in the
        // middle of is a group box that changes shape when nothing changed.
        foreach (var id in targets) {
            foreach (var group in Graph.Groups) {
                var index = group.Nodes.IndexOf(id);

                if (index >= 0) {
                    memberships.Add((group, index, id));
                }
            }
        }

        foreach (var id in targets) {
            if (Graph.Remove(id, out var edges) is { } node) {
                removed.Add(node);
                detached.AddRange(edges);
            }
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        foreach (var node in removed) {
            Graph.Restore(node);
        }

        // After every node is back, because an edge names two of them and the second may have been
        // removed by this same command.
        foreach (var edge in detached) {
            Graph.Connect(edge.From, edge.To);
        }

        // Ascending, so each insertion lands at the index it was read from: putting the later ones
        // back first would shift the earlier ones' indices out from under them.
        foreach (var (group, index, node) in memberships.OrderBy(entry => entry.Index)) {
            group.Nodes.Insert(Math.Min(index, group.Nodes.Count), node);
        }
    }
}

/// <summary>Wiring an output to an input.</summary>
public sealed class ConnectCommand : NodeGraphCommand {
    readonly PortRef from;
    readonly PortRef to;

    GraphEdge? replaced;

    /// <summary>Describes making a connection.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="from">The output.</param>
    /// <param name="to">The input.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    public ConnectCommand(NodeGraphModel graph, PortRef from, PortRef to, EditorDocument? document = null)
        : base(graph, document) {
        this.from = from;
        this.to = to;
    }

    /// <inheritdoc />
    public override string Name => "Connect";

    /// <inheritdoc />
    protected override void Apply() => replaced = Graph.Connect(from, to);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The wire that was displaced comes back.</b> Dragging onto an occupied input replaces
    ///     what was there, and an undo that only removed the new wire would leave the input empty —
    ///     which is not the state the author pressed undo to get back to.
    /// </remarks>
    protected override void Revert() {
        Graph.Disconnect(to);

        if (replaced is { } edge) {
            Graph.Connect(edge.From, edge.To);
        }
    }
}

/// <summary>Taking the wire off an input.</summary>
public sealed class DisconnectCommand : NodeGraphCommand {
    readonly PortRef to;

    GraphEdge? removed;

    /// <summary>Describes removing a connection.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="to">The input the wire arrives at.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    public DisconnectCommand(NodeGraphModel graph, PortRef to, EditorDocument? document = null)
        : base(graph, document) => this.to = to;

    /// <inheritdoc />
    public override string Name => "Disconnect";

    /// <inheritdoc />
    protected override void Apply() => removed = Graph.Disconnect(to);

    /// <inheritdoc />
    protected override void Revert() {
        if (removed is { } edge) {
            Graph.Connect(edge.From, edge.To);
        }
    }
}

/// <summary>Moving some nodes.</summary>
/// <remarks>
///     <para>
///         <b>Merging is what makes a drag one undo step</b>, the same bargain <c>SetMembersCommand</c>
///         makes for a slider: two of these merge when they move the same nodes, and the merged one
///         keeps the earlier's starting positions, so one undo goes back to before the drag rather
///         than to one mouse-move ago. <c>CommandStack.Seal</c> on the pointer release ends the run.
///     </para>
///     <para>
///         ⚠ <b>The starting positions are read when the command is built, not when it is applied.</b>
///         A view that has already moved its own copy of the nodes — which is what a live drag is —
///         must build this before writing anything back to the model, or the "before" it records is
///         the "after".
///     </para>
/// </remarks>
public sealed class MoveNodesCommand : NodeGraphCommand {
    readonly Dictionary<NodeId, Vector2> before = [];
    readonly Dictionary<NodeId, Vector2> after;
    readonly bool coalesces;

    /// <summary>Describes moving some nodes.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="positions">Where each node is to end up.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <param name="name">What the entry is called.</param>
    /// <param name="coalesces">Whether consecutive moves of the same nodes become one entry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="positions" /> is null.</exception>
    public MoveNodesCommand(
        NodeGraphModel graph,
        IReadOnlyDictionary<NodeId, Vector2> positions,
        EditorDocument? document = null,
        string name = "Move Nodes",
        bool coalesces = true
    ) : base(graph, document) {
        ArgumentNullException.ThrowIfNull(positions);

        after = new(positions);
        Name = name;
        this.coalesces = coalesces;

        foreach (var id in after.Keys) {
            if (graph.TryGet(id, out var node)) {
                before[id] = node.Position;
            }
        }
    }

    MoveNodesCommand(
        NodeGraphModel graph,
        Dictionary<NodeId, Vector2> before,
        Dictionary<NodeId, Vector2> after,
        EditorDocument? document,
        string name
    ) : base(graph, document) {
        this.before = before;
        this.after = after;
        coalesces = true;
        Name = name;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    protected override void Apply() => Write(after);

    /// <inheritdoc />
    protected override void Revert() => Write(before);

    /// <inheritdoc />
    public override bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (!coalesces
            || previous is not MoveNodesCommand earlier
            || !earlier.coalesces
            || !ReferenceEquals(earlier.Graph, Graph)
            || earlier.after.Count != after.Count) {
            return false;
        }

        foreach (var id in after.Keys) {
            if (!earlier.after.ContainsKey(id)) {
                return false;
            }
        }

        merged = new MoveNodesCommand(Graph, earlier.before, after, Document, Name);

        return true;
    }

    void Write(Dictionary<NodeId, Vector2> positions) {
        foreach (var (id, position) in positions) {
            if (Graph.TryGet(id, out var node)) {
                node.Position = position;
            }
        }
    }
}

/// <summary>Typing a number into an unconnected input.</summary>
public sealed class SetPortValueCommand : NodeGraphCommand {
    readonly NodeId node;
    readonly string port;
    readonly float[] value;
    readonly float[]? previous;
    readonly bool had;

    /// <summary>Describes setting an inline value.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="node">The node.</param>
    /// <param name="port">Which of its inputs.</param>
    /// <param name="value">The lanes it takes.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public SetPortValueCommand(
        NodeGraphModel graph,
        NodeId node,
        string port,
        float[] value,
        EditorDocument? document = null
    ) : base(graph, document) {
        ArgumentException.ThrowIfNullOrEmpty(port);
        ArgumentNullException.ThrowIfNull(value);

        this.node = node;
        this.port = port;
        this.value = [.. value];

        // ⚠ Whether there *was* one, not only what it was. A port that never had an inline value
        // takes its type's default, and an undo that wrote a zero back would silently pin it to a
        // number the node type is free to change.
        float[]? held = null;

        had = graph.TryGet(node, out var target) && target.Values.TryGetValue(port, out held);
        previous = held is null ? null : [.. held];
    }

    SetPortValueCommand(
        NodeGraphModel graph,
        NodeId node,
        string port,
        float[] value,
        float[]? previous,
        bool had,
        EditorDocument? document
    ) : base(graph, document) {
        this.node = node;
        this.port = port;
        this.value = value;
        this.previous = previous;
        this.had = had;
    }

    /// <inheritdoc />
    public override string Name => $"Set {port}";

    /// <inheritdoc />
    protected override void Apply() {
        if (Graph.TryGet(node, out var target)) {
            target.SetValue(port, value);
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        if (!Graph.TryGet(node, out var target)) {
            return;
        }

        if (had && previous is not null) {
            target.SetValue(port, previous);
        } else {
            target.ClearValue(port);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The merged command keeps the earlier one's "before", including whether there was
    ///     one.</b> Dragging a number field produces one of these per frame, and a merge that kept the
    ///     newest "before" would undo to the value one mouse-move ago — and, worse, would pin a port
    ///     that started with no inline value at all to whatever it read on the first frame.
    /// </remarks>
    public override bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (previous is not SetPortValueCommand earlier
            || !ReferenceEquals(earlier.Graph, Graph)
            || earlier.node != node
            || !string.Equals(earlier.port, port, StringComparison.Ordinal)) {
            return false;
        }

        merged = new SetPortValueCommand(Graph, node, port, value, earlier.previous, earlier.had, Document);

        return true;
    }
}

/// <summary>Setting the inline text on one of a node's inputs.</summary>
/// <remarks>
///     <see cref="SetPortValueCommand" />'s twin, for the ports made of names rather than of lanes —
///     see <see cref="GraphNode.Texts" /> for why those exist. Everything about it is the same
///     argument: whether there <i>was</i> a text is recorded as well as what it was, and a merge
///     keeps the earlier one's, so typing a resource name is one undo entry rather than one a
///     keystroke.
/// </remarks>
public sealed class SetPortTextCommand : NodeGraphCommand {
    readonly NodeId node;
    readonly string port;
    readonly string value;
    readonly string? previous;
    readonly bool had;

    /// <summary>Describes setting an inline text.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="node">The node.</param>
    /// <param name="port">Which of its inputs.</param>
    /// <param name="value">What it says.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    public SetPortTextCommand(
        NodeGraphModel graph,
        NodeId node,
        string port,
        string value,
        EditorDocument? document = null
    ) : base(graph, document) {
        ArgumentException.ThrowIfNullOrEmpty(port);
        ArgumentNullException.ThrowIfNull(value);

        this.node = node;
        this.port = port;
        this.value = value;

        string? held = null;

        had = graph.TryGet(node, out var target) && target.Texts.TryGetValue(port, out held);
        previous = held;
    }

    SetPortTextCommand(
        NodeGraphModel graph,
        NodeId node,
        string port,
        string value,
        string? previous,
        bool had,
        EditorDocument? document
    ) : base(graph, document) {
        this.node = node;
        this.port = port;
        this.value = value;
        this.previous = previous;
        this.had = had;
    }

    /// <inheritdoc />
    public override string Name => $"Set {port}";

    /// <inheritdoc />
    protected override void Apply() {
        if (Graph.TryGet(node, out var target)) {
            target.SetText(port, value);
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        if (!Graph.TryGet(node, out var target)) {
            return;
        }

        if (had && previous is not null) {
            target.SetText(port, previous);
        } else {
            target.ClearText(port);
        }
    }

    /// <inheritdoc />
    /// <inheritdoc cref="SetPortValueCommand.TryMergeWith" path="/remarks" />
    public override bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (previous is not SetPortTextCommand earlier
            || !ReferenceEquals(earlier.Graph, Graph)
            || earlier.node != node
            || !string.Equals(earlier.port, port, StringComparison.Ordinal)) {
            return false;
        }

        merged = new SetPortTextCommand(Graph, node, port, value, earlier.previous, earlier.had, Document);

        return true;
    }
}

/// <summary>Drawing a box round some nodes.</summary>
public sealed class AddGroupCommand : NodeGraphCommand {
    readonly GraphGroup group;

    /// <summary>Describes adding a group.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="title">What it is called.</param>
    /// <param name="members">What goes in it.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="members" /> is null.</exception>
    public AddGroupCommand(
        NodeGraphModel graph,
        string title,
        IEnumerable<NodeId> members,
        EditorDocument? document = null
    ) : base(graph, document) {
        ArgumentNullException.ThrowIfNull(members);

        group = new() { Title = title };
        group.Nodes.AddRange(members);
    }

    /// <inheritdoc />
    public override string Name => "Group Nodes";

    /// <summary>The group, which is the same object on every redo.</summary>
    public GraphGroup Group => group;

    /// <inheritdoc />
    protected override void Apply() {
        if (!Graph.Groups.Contains(group)) {
            Graph.Groups.Add(group);
        }
    }

    /// <inheritdoc />
    protected override void Revert() => Graph.Groups.Remove(group);
}

/// <summary>Taking a box away, leaving the nodes where they are.</summary>
public sealed class RemoveGroupCommand : NodeGraphCommand {
    readonly GraphGroup group;

    int index = -1;

    /// <summary>Describes removing a group.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="group">The group.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group" /> is null.</exception>
    public RemoveGroupCommand(NodeGraphModel graph, GraphGroup group, EditorDocument? document = null)
        : base(graph, document) {
        ArgumentNullException.ThrowIfNull(group);

        this.group = group;
    }

    /// <inheritdoc />
    public override string Name => "Ungroup";

    /// <inheritdoc />
    protected override void Apply() {
        index = Graph.Groups.IndexOf(group);

        if (index >= 0) {
            Graph.Groups.RemoveAt(index);
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        if (index >= 0) {
            Graph.Groups.Insert(Math.Min(index, Graph.Groups.Count), group);
        }
    }
}

/// <summary>Retitling a group.</summary>
public sealed class RenameGroupCommand : NodeGraphCommand {
    readonly GraphGroup group;
    readonly string title;
    readonly string previous;

    /// <summary>Describes retitling a group.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="group">The group.</param>
    /// <param name="title">Its new title.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group" /> is null.</exception>
    public RenameGroupCommand(NodeGraphModel graph, GraphGroup group, string title, EditorDocument? document = null)
        : base(graph, document) {
        ArgumentNullException.ThrowIfNull(group);

        this.group = group;
        this.title = title;
        previous = group.Title;
    }

    /// <inheritdoc />
    public override string Name => "Rename Group";

    /// <inheritdoc />
    protected override void Apply() => group.Title = title;

    /// <inheritdoc />
    protected override void Revert() => group.Title = previous;
}

/// <summary>Adding a sticky note.</summary>
public sealed class AddCommentCommand : NodeGraphCommand {
    readonly GraphComment comment;

    /// <summary>Describes adding a note.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="text">What it says.</param>
    /// <param name="position">Where it goes.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    public AddCommentCommand(NodeGraphModel graph, string text, Vector2 position, EditorDocument? document = null)
        : base(graph, document) =>
        comment = new() { Text = text, Position = position };

    /// <inheritdoc />
    public override string Name => "Add Note";

    /// <summary>The note, which is the same object on every redo.</summary>
    public GraphComment Comment => comment;

    /// <inheritdoc />
    protected override void Apply() {
        if (!Graph.Comments.Contains(comment)) {
            Graph.Comments.Add(comment);
        }
    }

    /// <inheritdoc />
    protected override void Revert() => Graph.Comments.Remove(comment);
}

/// <summary>Editing a sticky note.</summary>
public sealed class SetCommentCommand : NodeGraphCommand {
    readonly GraphComment comment;
    readonly string text;
    readonly string previous;

    /// <summary>Describes retyping a note.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="comment">The note.</param>
    /// <param name="text">What it should say.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="comment" /> is null.</exception>
    public SetCommentCommand(NodeGraphModel graph, GraphComment comment, string text, EditorDocument? document = null)
        : base(graph, document) {
        ArgumentNullException.ThrowIfNull(comment);

        this.comment = comment;
        this.text = text;
        previous = comment.Text;
    }

    /// <inheritdoc />
    public override string Name => "Edit Note";

    /// <inheritdoc />
    protected override void Apply() => comment.Text = text;

    /// <inheritdoc />
    protected override void Revert() => comment.Text = previous;
}

/// <summary>Removing a sticky note.</summary>
public sealed class RemoveCommentCommand : NodeGraphCommand {
    readonly GraphComment comment;

    int index = -1;

    /// <summary>Describes removing a note.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="comment">The note.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="comment" /> is null.</exception>
    public RemoveCommentCommand(NodeGraphModel graph, GraphComment comment, EditorDocument? document = null)
        : base(graph, document) {
        ArgumentNullException.ThrowIfNull(comment);

        this.comment = comment;
    }

    /// <inheritdoc />
    public override string Name => "Delete Note";

    /// <inheritdoc />
    protected override void Apply() {
        index = Graph.Comments.IndexOf(comment);

        if (index >= 0) {
            Graph.Comments.RemoveAt(index);
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        if (index >= 0) {
            Graph.Comments.Insert(Math.Min(index, Graph.Comments.Count), comment);
        }
    }
}

/// <summary>Dropping a copied fragment into a graph.</summary>
/// <remarks>
///     <para>
///         <b>The pasted nodes get fresh identities and the fragment's edges are rewritten to
///         match.</b> Pasting into the graph the fragment came from is the ordinary case, and reusing
///         the identities there would collide with the originals.
///     </para>
///     <para>
///         ⚠ <b>The identities are minted once and reused on every redo.</b> A paste that renumbered
///         each time would leave an undone-and-redone paste holding different nodes from the ones the
///         author then selected and moved, so the next undo would move the wrong things back.
///     </para>
/// </remarks>
public sealed class PasteCommand : NodeGraphCommand {
    readonly NodeGraphAsset fragment;
    readonly Vector2 offset;
    readonly List<GraphNode> pasted = [];
    readonly Dictionary<int, NodeId> renumbered = [];

    bool minted;

    /// <summary>Describes pasting a fragment.</summary>
    /// <param name="graph">The graph to paste into.</param>
    /// <param name="fragment">What was copied, as <see cref="NodeGraphClipboard.Copy" /> made it.</param>
    /// <param name="offset">How far to move it from where it was cut.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fragment" /> is null.</exception>
    public PasteCommand(
        NodeGraphModel graph,
        NodeGraphAsset fragment,
        Vector2 offset = default,
        EditorDocument? document = null
    ) : base(graph, document) {
        ArgumentNullException.ThrowIfNull(fragment);

        this.fragment = fragment;
        this.offset = offset;
    }

    /// <inheritdoc />
    public override string Name => fragment.Nodes.Length > 1 ? $"Paste {fragment.Nodes.Length} Nodes" : "Paste";

    /// <summary>What was pasted, once it has been.</summary>
    public IReadOnlyList<GraphNode> Pasted => pasted;

    /// <inheritdoc />
    protected override void Apply() {
        if (minted) {
            foreach (var node in pasted) {
                Graph.Restore(node);
            }
        } else {
            Mint();
            minted = true;
        }

        // The edges after every node, in both branches: an edge names two nodes and the second may
        // not have been added yet.
        foreach (var edge in Edges()) {
            Graph.Connect(edge.From, edge.To);
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        foreach (var node in pasted) {
            Graph.Remove(node.Id, out _);
        }
    }

    void Mint() {
        foreach (var entry in fragment.Nodes) {
            var node = Graph.Add(entry.Type, new Vector2(entry.X, entry.Y) + offset);

            renumbered[entry.Id] = node.Id;
            pasted.Add(node);

            foreach (var (port, value) in entry.Values) {
                node.SetValue(port, [.. value]);
            }
        }
    }

    IEnumerable<GraphEdge> Edges() {
        foreach (var edge in fragment.Edges) {
            if (renumbered.TryGetValue(edge.FromNode, out var from) && renumbered.TryGetValue(edge.ToNode, out var to)) {
                yield return new(new(from, edge.FromPort), new(to, edge.ToPort));
            }
        }
    }
}

/// <summary>Laying a graph out automatically.</summary>
/// <remarks>
///     A <see cref="MoveNodesCommand" /> under a different name and with merging off: an auto-layout
///     is one deliberate edit, and merging it into whatever drag came before it would make one undo
///     put back both.
/// </remarks>
public static class LayoutCommand {
    /// <summary>Describes laying a graph out.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="registry">The node library, for how tall each node is.</param>
    /// <param name="options">How much room to leave.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <returns>The command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public static MoveNodesCommand For(
        NodeGraphModel graph,
        NodeTypeRegistry? registry = null,
        NodeLayoutOptions options = default,
        EditorDocument? document = null
    ) =>
        new(graph, NodeGraphLayout.Arrange(graph, registry, options), document, "Auto-Layout", coalesces: false);
}

/// <summary>Replacing some of a graph's nodes with one node standing for a graph of their own.</summary>
/// <remarks>
///     <b>The extraction is computed first and applied here.</b> <see cref="SubGraphs.Extract" /> works
///     out the new graph and its interface without changing anything; this is the part that is an edit
///     — remove the nodes, add the one that stands for them, and rewire what crossed the boundary.
/// </remarks>
public sealed class ExtractSubGraphCommand : NodeGraphCommand {
    readonly SubGraphExtraction extraction;
    readonly string path;
    readonly Vector2 position;
    readonly List<GraphNode> removed = [];
    readonly List<GraphEdge> detached = [];

    GraphNode? standIn;

    /// <summary>Describes extracting a sub-graph.</summary>
    /// <param name="graph">The containing graph.</param>
    /// <param name="extraction">What <see cref="SubGraphs.Extract" /> worked out.</param>
    /// <param name="path">The node-type path the sub-graph is registered under.</param>
    /// <param name="position">Where the node standing for it goes.</param>
    /// <param name="document">The document, if the graph belongs to one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="extraction" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public ExtractSubGraphCommand(
        NodeGraphModel graph,
        SubGraphExtraction extraction,
        string path,
        Vector2 position,
        EditorDocument? document = null
    ) : base(graph, document) {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        this.extraction = extraction;
        this.path = path;
        this.position = position;
    }

    /// <inheritdoc />
    public override string Name => "Extract Sub-graph";

    /// <summary>The node standing for the sub-graph, once it exists.</summary>
    public GraphNode StandIn =>
        standIn ?? throw new InvalidOperationException("The node does not exist until the command has been executed.");

    /// <inheritdoc />
    protected override void Apply() {
        removed.Clear();
        detached.Clear();

        foreach (var id in extraction.Extracted) {
            if (Graph.Remove(id, out var edges) is { } node) {
                removed.Add(node);
                detached.AddRange(edges);
            }
        }

        if (standIn is null) {
            standIn = Graph.Add(path, position);
        } else {
            Graph.Restore(standIn);
        }

        foreach (var edge in extraction.Incoming) {
            if (extraction.Inputs.TryGetValue(edge.From, out var port) && Graph.TryGet(edge.From.Node, out _)) {
                Graph.Connect(edge.From, new(standIn.Id, port));
            }
        }

        foreach (var edge in extraction.Outgoing) {
            if (extraction.Outputs.TryGetValue(edge.From, out var port) && Graph.TryGet(edge.To.Node, out _)) {
                Graph.Connect(new(standIn.Id, port), edge.To);
            }
        }
    }

    /// <inheritdoc />
    protected override void Revert() {
        if (standIn is not null) {
            Graph.Remove(standIn.Id, out _);
        }

        foreach (var node in removed) {
            Graph.Restore(node);
        }

        foreach (var edge in detached) {
            Graph.Connect(edge.From, edge.To);
        }
    }
}
