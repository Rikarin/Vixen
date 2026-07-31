// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using CanvasGraph = Vixen.Ui.Controls.Advanced.NodeGraph;
using CanvasGroup = Vixen.Ui.Controls.Advanced.GraphGroup;
using CanvasNode = Vixen.Ui.Controls.Advanced.GraphNode;
using CanvasPort = Vixen.Ui.Controls.Advanced.GraphPort;

namespace Vixen.Editor.NodeGraph;

/// <summary>A sticky note, as an element.</summary>
public sealed partial class NodeCommentView : Control {
    /// <inheritdoc />
    protected override string TagName => "graph-note";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The note it is showing, or <c>null</c> if it is parked.</summary>
    public GraphComment? Comment { get; internal set; }

    /// <summary>Where the text goes.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Body = Part("graph-note-body");
    }
}

/// <summary>Why a wire was refused.</summary>
/// <param name="From">The output it left.</param>
/// <param name="To">The input it was dropped on.</param>
/// <param name="Reason">What to tell the author.</param>
public readonly record struct ConnectionRefusal(PortRef From, PortRef To, string Reason);

/// <summary>
///     A <see cref="NodeGraphModel" /> on a <see cref="NodeCanvas" />, with every gesture going
///     through the undo stack.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two graph types, and the view is the only thing that knows both.</b>
///         <see cref="NodeGraphModel" /> is the document — identities, port names, saved and diffed —
///         and <c>Vixen.Ui.Controls.Advanced.NodeGraph</c> is what a canvas draws, which is boxes with
///         sockets on and no idea what a node type is. Keeping them apart is what lets the model be
///         tested against numbers and the canvas be tested against a fixture with three nodes called
///         "a", "b" and "c"; this class is the projection between them and it is one direction only.
///         The model is the truth and the canvas is a picture of it.
///     </para>
///     <para>
///         ⚠ <b>Every structural change reprojects the whole graph.</b> A cheaper incremental update
///         is possible and was not written, for two reasons: the canvas culls to the viewport already,
///         so the expensive part is bounded by the screen rather than by the graph; and a projection
///         that is rebuilt cannot drift from the model, which an incremental one does the first time
///         somebody adds an edit path and forgets a case. A drag is the one thing that does <i>not</i>
///         reproject — it writes positions in place — because that is the path that runs every frame.
///     </para>
///     <para>
///         ⚠ <b>The canvas edits its own copy optimistically, and that is on purpose.</b> Dragging a
///         wire connects it in the picture before this class has recorded anything, which is what makes
///         the gesture feel live. What follows is either a command — and the reprojection agrees with
///         it — or a reprojection alone, which puts the picture back. A canvas that had to ask
///         permission before drawing would be a canvas with a frame of latency in every gesture.
///     </para>
///     <para>
///         <b>No stack means read-only.</b> Every edit here goes through <see cref="Stack" />, so a
///         view without one shows a graph and refuses to change it — which is exactly what a preview
///         of a sub-graph, or of an asset that is not open for editing, should do.
///     </para>
/// </remarks>
public sealed class NodeGraphView : Control {
    readonly Dictionary<NodeId, CanvasNode> shown = [];
    readonly Dictionary<CanvasNode, NodeId> identities = [];
    readonly Dictionary<CanvasPort, Socket> sockets = [];
    readonly Dictionary<Socket, CanvasPort> anchors = [];
    readonly Dictionary<CanvasGroup, GraphGroup> groups = [];
    readonly List<NodeCommentView> notes = [];
    readonly HashSet<NodeId> selection = [];

    NodeGraphModel graph = new();
    NodeTypeRegistry registry = new();
    EditorDocument? edited;

    bool projecting;
    bool typing;
    Vector2 placed = new(float.NaN, float.NaN);
    float scaled = float.NaN;
    Vector2 pointer;

    PortRef pending;
    PortDirection pendingSide;
    Vector2 pendingAt;
    bool pendingWire;

    /// <summary>Where a port of a node is, as the projection keys it.</summary>
    readonly record struct Socket(NodeId Node, string Port, PortDirection Direction);

    /// <inheritdoc />
    protected override string TagName => "node-graph";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The canvas everything is drawn on.</summary>
    public NodeCanvas Canvas { get; private set; } = null!;

    /// <summary>The overview in the corner, which is the canvas's own.</summary>
    public NodeMinimap Minimap => Canvas.Minimap;

    /// <summary>The swatches under the nodes that asked for one.</summary>
    public NodePreviewLayer Previews { get; private set; } = null!;

    /// <summary>The create-a-node popup.</summary>
    public NodeSearchPopup Search { get; private set; } = null!;

    /// <summary>How wide a node is drawn, in graph units.</summary>
    /// <remarks>
    ///     Wide enough for a name and the number beside it, because an unconnected input is shown as
    ///     both. A narrower node is a node whose <c>float3</c> is three boxes with no digits in them.
    /// </remarks>
    public float NodeWidth { get; set; } = 240f;

    /// <summary>How many decimal places a number on a node shows.</summary>
    /// <remarks>
    ///     Three, which is the inspector's, so the same value does not read as two different numbers
    ///     depending on which of the two an author is looking at.
    /// </remarks>
    public int Decimals { get; set; } = 3;

    /// <summary>The graph being shown.</summary>
    /// <remarks>
    ///     Settable, because an editor shows one graph and then another. Assigning drops the selection:
    ///     a selection carried across would name nodes of a graph nobody is looking at, and Delete
    ///     would then edit it.
    /// </remarks>
    public NodeGraphModel Graph {
        get => graph;
        set {
            ArgumentNullException.ThrowIfNull(value);

            if (ReferenceEquals(graph, value)) {
                return;
            }

            graph.Changed -= OnGraphChanged;
            graph = value;
            graph.Changed += OnGraphChanged;

            selection.Clear();
            Project();
        }
    }

    /// <summary>The node types the graph may contain.</summary>
    public NodeTypeRegistry Registry {
        get => registry;
        set {
            ArgumentNullException.ThrowIfNull(value);

            registry = value;
            Project();
        }
    }

    /// <summary>Where sub-graphs are found, when this graph has any.</summary>
    /// <remarks>
    ///     Only used to tell a sub-graph node from an ordinary one, so that double-clicking it opens
    ///     the graph it stands for. Inlining is the compiler's, not the view's.
    /// </remarks>
    public ISubGraphSource? SubGraphSource { get; set; }

    /// <summary>Where a preview swatch's colour comes from.</summary>
    public INodePreviewSource? PreviewSource { get; set; }

    /// <summary>Where copy and paste put things.</summary>
    public NodeGraphClipboard Clipboard { get; set; } = NodeGraphClipboard.Default;

    /// <summary>Where edits are recorded, or null for a view that cannot be edited.</summary>
    public CommandStack? Stack { get; set; }

    /// <summary>The document to mark dirty, if the graph belongs to one.</summary>
    /// <remarks>
    ///     Not called <c>Document</c>: a <see cref="UiElement" /> already has one of those and it is
    ///     the interface tree this control lives in. Setting it takes the stack from it, which is the
    ///     arrangement that is right nine times in ten; <see cref="Stack" /> is still settable for the
    ///     tenth.
    /// </remarks>
    public EditorDocument? EditedDocument {
        get => edited;
        set {
            edited = value;
            Stack = value?.Stack;
        }
    }

    /// <summary>Whether edits are refused because there is nowhere to record them.</summary>
    public bool IsReadOnly => Stack is null;

    /// <summary>What is selected, by identity.</summary>
    public IReadOnlyCollection<NodeId> Selection => selection;

    /// <summary>Raised after the selection changes.</summary>
    public event Action<NodeGraphView>? SelectionChanged;

    /// <summary>Raised when a wire was dropped somewhere it could not go.</summary>
    public event Action<NodeGraphView, ConnectionRefusal>? ConnectionRefused;

    /// <summary>Raised when a node standing for a sub-graph is opened.</summary>
    public event Action<NodeGraphView, string, NodeGraphModel>? SubGraphOpened;

    /// <summary>Raised after the model this view is showing changed, however it changed.</summary>
    /// <remarks>
    ///     The model's own <see cref="NodeGraphModel.Changed" /> says the same thing, and this exists
    ///     so that a panel beside the canvas does not have to follow the graph the view is pointed at
    ///     through every reassignment to hear it.
    /// </remarks>
    public event Action<NodeGraphView>? GraphChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Canvas = Part<NodeCanvas>();
        Canvas.SelectionChanged += _ => Reselected();
        Canvas.NodesMoved += _ => Dropped();
        Canvas.Connected += (_, wire) => Wired(wire);
        Canvas.Activated += (_, node) => Opened(node);
        Canvas.PortEdited += (_, port) => Typed(port);

        // A child of the canvas rather than of its surface, so it is drawn after the node elements.
        // See NodePreviewLayer for what that costs and why it is the arrangement that works.
        Previews = Canvas.Add<NodePreviewLayer>();
        Previews.View = this;

        // An overlay is a root child: that is what lets it hang outside whatever is clipping this
        // panel, which for a graph docked in a corner is most of the window.
        Search = Document.Root.Add<NodeSearchPopup>();
        Search.Accepted += (_, result) => Created(result);

        graph.Changed += OnGraphChanged;

        // Capture, so a key this view claims is taken before the canvas acts on it. Delete is the one
        // that matters: the canvas would remove nodes from its own copy of the graph, which is a
        // change nothing recorded and the next reprojection would silently undo.
        AddHandler<KeyEvent>(static (element, args) => ((NodeGraphView) element).Keyed(args), RoutingStrategy.Capture);
        AddHandler<PointerEvent>(static (element, args) => ((NodeGraphView) element).Pointed(args), RoutingStrategy.Capture);

        // ⚠ Bubbling, so these run *after* the canvas has written Pan or Zoom, and before the frame's
        // layout. Nothing announces a pan — the two properties realise the canvas's own elements and
        // raise no event — so a note drawn in graph coordinates would otherwise be repositioned by
        // OnDraw, which is a frame too late and visibly lags the nodes it is pinned between. The
        // draw-time check below is still there, for a pan somebody wrote in code.
        AddHandler<PointerEvent>(static (element, _) => ((NodeGraphView) element).Panned(), handledEventsToo: true);
        AddHandler<WheelEvent>(static (element, _) => ((NodeGraphView) element).Panned(), handledEventsToo: true);
    }

    void Panned() {
        if (Canvas.Pan != placed || !Canvas.Zoom.Equals(scaled)) {
            PlaceNotes();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The popup is a root child, so it does not go when this does.</b> The same debt
    ///     <see cref="Menu" /> pays for its submenus, and for the same reason: hanging outside
    ///     whatever is clipping this panel means not being inside it. The model subscription goes too
    ///     — a removed view that kept reprojecting would hold the graph alive and do it for nothing.
    /// </remarks>
    protected override void OnRemoved() {
        graph.Changed -= OnGraphChanged;

        if (Search is { IsRemoved: false } popup) {
            Document.Remove(popup);
        }

        base.OnRemoved();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The backstop for a pan nobody gestured.</b> The two input handlers above catch a drag
    ///     and a wheel in time to be laid out the same frame; a <c>Pan</c> written in code — a
    ///     bookmark, a "go to node", a test — is announced by nothing at all, and this is where it is
    ///     noticed. One frame later than the gesture path, which is the honest cost of a property
    ///     with no change event on it. Guarded on the two numbers, so a still canvas writes nothing.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        Panned();
    }

    // ── The projection ───────────────────────────────────────────────────────

    /// <summary>The node type at a path, including the two a sub-graph's boundary nodes use.</summary>
    /// <param name="type">The path.</param>
    /// <returns>The type, or null when nothing is registered there.</returns>
    /// <remarks>
    ///     The boundary types are built from the open graph's own interface rather than looked up —
    ///     see <see cref="SubGraphs.Boundary" /> for why they cannot be in a registry.
    /// </remarks>
    public NodeTypeDefinition? Definition(string type) {
        ArgumentNullException.ThrowIfNull(type);

        if (SubGraphs.IsBoundary(type)) {
            return SubGraphs.Boundary(graph, type);
        }

        return registry.TryGet(type, out var definition) ? definition : null;
    }

    /// <summary>The model node an element on the canvas is showing.</summary>
    /// <param name="node">The canvas's node.</param>
    /// <returns>The model's, or null if the projection does not know it.</returns>
    public GraphNode? NodeOf(CanvasNode node) {
        ArgumentNullException.ThrowIfNull(node);

        return identities.TryGetValue(node, out var id) && graph.TryGet(id, out var found) ? found : null;
    }

    /// <summary>Rebuilds the picture from the model.</summary>
    /// <remarks>
    ///     Public because a caller that changed something the model cannot notice — a node's position,
    ///     a group's title — calls <see cref="NodeGraphModel.Touch" />, and one that changed the
    ///     registry calls this.
    /// </remarks>
    public void Project() {
        var built = new CanvasGraph();

        shown.Clear();
        identities.Clear();
        sockets.Clear();
        anchors.Clear();
        groups.Clear();

        foreach (var node in graph.Nodes) {
            var definition = Definition(node.Type);
            var view = built.AddNode(Title(node, definition), node.Position);

            view.Width = NodeWidth;
            view.Tag = node.Id;

            shown[node.Id] = view;
            identities[view] = node.Id;

            if (definition is not null) {
                foreach (var port in definition.Ports) {
                    Anchor(view, node.Id, port.Name, port.Direction, node, port);
                }

                continue;
            }

            // ⚠ A node whose type is not registered still shows the ports its edges name. A graph
            // saved against a plugin that is not loaded opens with its wiring visible and can be
            // saved again unchanged, which is the difference between "this node is missing" and
            // "this file has been quietly destroyed".
            foreach (var edge in graph.Edges) {
                if (edge.From.Node == node.Id) {
                    Anchor(view, node.Id, edge.From.Port, PortDirection.Output);
                }

                if (edge.To.Node == node.Id) {
                    Anchor(view, node.Id, edge.To.Port, PortDirection.Input);
                }
            }
        }

        foreach (var edge in graph.Edges) {
            if (anchors.TryGetValue(new(edge.From.Node, edge.From.Port, PortDirection.Output), out var from)
                && anchors.TryGetValue(new(edge.To.Node, edge.To.Port, PortDirection.Input), out var to)) {
                built.Connect(from, to);
            }
        }

        foreach (var group in graph.Groups) {
            List<CanvasNode> members = [];

            foreach (var id in group.Nodes) {
                if (shown.TryGetValue(id, out var member)) {
                    members.Add(member);
                }
            }

            // ⚠ A node the model has in two groups lands in the last of them. The canvas's group
            // membership is a back-pointer on the node, so it holds one; the model does not, because
            // a document should not lose an author's grouping to a drawing limitation.
            groups[built.AddGroup(group.Title, [.. members])] = group;
        }

        projecting = true;

        try {
            Canvas.Graph = built;
            Reselect();
        } finally {
            projecting = false;
        }

        Notes();
        PlaceNotes();
    }

    void Anchor(
        CanvasNode view,
        NodeId id,
        string port,
        PortDirection direction,
        GraphNode? node = null,
        PortDefinition? definition = null
    ) {
        var key = new Socket(id, port, direction);

        if (anchors.ContainsKey(key)) {
            return;
        }

        var socket = direction == PortDirection.Input ? view.AddInput(port) : view.AddOutput(port);

        if (node is not null && definition is not null) {
            socket.Editor = Inline(node, definition);
        }

        anchors[key] = socket;
        sockets[socket] = key;
    }

    /// <summary>What an unconnected input takes, as the canvas shows it, or null for a port that takes nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The author's value or the type's default, and the difference is not written
    ///         down here.</b> A port with nothing typed into it shows its type's default, which is
    ///         also what the compiler reads — see <c>NodeGraphCompiler.Value</c> — so the picture and
    ///         the emitted source agree without the projection having to say which of the two it was
    ///         showing. What records the difference is <see cref="SetPortValueCommand" />, because an
    ///         undo has to be able to put a port back to having no value at all.
    ///     </para>
    ///     <para>
    ///         <b>An unregistered node type gets none.</b> Its ports come off its edges, so nothing
    ///         knows what kind they are or how wide — and a box of digits over a value the graph
    ///         cannot type is a number that would be written into a file the missing plugin owns.
    ///     </para>
    /// </remarks>
    PortEditor? Inline(GraphNode node, PortDefinition port) {
        if (port.Direction != PortDirection.Input) {
            return null;
        }

        var fields = PortKinds.Fields(port.Kind);

        if (fields <= 0) {
            return null;
        }

        var editor = new PortEditor(port.Kind == PortKind.Bool ? PortEditorKind.Toggle : PortEditorKind.Number, fields) {
            Decimals = port.Kind is PortKind.Int ? 0 : Decimals,
            ReadOnly = IsReadOnly,
            LaneNames = fields > 1 ? "XYZW" : ""
        };

        editor.Set(node.Values.TryGetValue(port.Name, out var written) ? written : port.Default.AsSpan());

        return editor;
    }

    /// <summary>What is written across the top of a node.</summary>
    /// <remarks>
    ///     The whole path when the type is not registered, rather than its last segment: a node
    ///     labelled "Gone" says nothing, and one labelled "Plugin/Gone" says which plugin is missing.
    /// </remarks>
    static string Title(GraphNode node, NodeTypeDefinition? definition) => definition?.Title ?? node.Type;

    void Notes() {
        while (notes.Count < graph.Comments.Count) {
            var note = Canvas.Surface.Add<NodeCommentView>();

            // Straight after the group layer and the wire layer, so a note sits over the wires and
            // under the nodes. The node elements are appended by the canvas as it realises them, so
            // anything added later would land on top of every node on screen.
            Document.Move(note, Math.Min(2, Canvas.Surface.Children.Count - 1));
            notes.Add(note);
        }

        for (var index = 0; index < notes.Count; index++) {
            var note = notes[index];

            if (index >= graph.Comments.Count) {
                note.Comment = null;
                note.AddClass("parked");

                continue;
            }

            note.RemoveClass("parked");
            note.Comment = graph.Comments[index];
            note.Body.Text = graph.Comments[index].Text;
        }
    }

    void PlaceNotes() {
        placed = Canvas.Pan;
        scaled = Canvas.Zoom;

        foreach (var note in notes) {
            if (note.Comment is not { } comment) {
                continue;
            }

            var origin = Canvas.ToScreen(comment.Position);

            note.SetStyle("left", Px(origin.X - Canvas.Surface.AbsoluteLeft));
            note.SetStyle("top", Px(origin.Y - Canvas.Surface.AbsoluteTop));
            note.SetStyle("width", Px(comment.Size.X * Canvas.Zoom));
            note.SetStyle("height", Px(comment.Size.Y * Canvas.Zoom));
            note.SetStyle("font-size", Px(12f * Canvas.Zoom));
        }
    }

    static string Px(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    // ── Editing ──────────────────────────────────────────────────────────────

    /// <summary>Adds a node.</summary>
    /// <param name="type">Which node type, by path.</param>
    /// <param name="position">Where it goes, in graph units.</param>
    /// <returns>The node, or null when the view is read-only.</returns>
    public GraphNode? Create(string type, Vector2 position) {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var command = new AddNodeCommand(graph, type, Snap(position), edited);

        return Run(command) ? command.Node : null;
    }

    /// <summary>Deletes what is selected, and the wires that touched it.</summary>
    /// <returns>Whether anything went.</returns>
    public bool DeleteSelection() =>
        selection.Count > 0 && Run(new RemoveNodesCommand(graph, [.. selection], edited));

    /// <summary>Draws a box round what is selected.</summary>
    /// <param name="title">What the box is called.</param>
    /// <returns>Whether one was made.</returns>
    public bool GroupSelection(string title = "Group") =>
        selection.Count > 0 && Run(new AddGroupCommand(graph, title, [.. selection], edited));

    /// <summary>Removes every group that holds a selected node, leaving the nodes where they are.</summary>
    /// <returns>Whether anything went.</returns>
    public bool UngroupSelection() {
        List<GraphGroup> doomed = [];

        foreach (var group in graph.Groups) {
            foreach (var id in group.Nodes) {
                if (selection.Contains(id)) {
                    doomed.Add(group);

                    break;
                }
            }
        }

        if (doomed.Count == 0 || Stack is not { } stack) {
            return false;
        }

        // One entry however many boxes it took, because "ungroup this" is one thing the author did.
        using (stack.BeginTransaction("Ungroup")) {
            foreach (var group in doomed) {
                Run(new RemoveGroupCommand(graph, group, edited));
            }
        }

        return true;
    }

    /// <summary>Adds a sticky note.</summary>
    /// <param name="text">What it says.</param>
    /// <param name="position">Where it goes.</param>
    /// <returns>The note, or null when the view is read-only.</returns>
    public GraphComment? AddComment(string text, Vector2 position) {
        var command = new AddCommentCommand(graph, text, position, edited);

        return Run(command) ? command.Comment : null;
    }

    /// <summary>Copies what is selected.</summary>
    /// <returns>Whether anything was copied.</returns>
    public bool CopySelection() => Clipboard.Take(graph, selection);

    /// <summary>Copies what is selected and deletes it.</summary>
    /// <returns>Whether anything went.</returns>
    public bool CutSelection() {
        if (Stack is not { } stack || !CopySelection()) {
            return false;
        }

        using (stack.BeginTransaction("Cut")) {
            return DeleteSelection();
        }
    }

    /// <summary>Pastes whatever was copied, offset so it does not land on the original.</summary>
    /// <param name="at">Where to put it, or null for a small offset from where it was cut.</param>
    /// <returns>Whether anything arrived.</returns>
    public bool Paste(Vector2? at = null) {
        if (Clipboard.Content is not { Nodes.Length: > 0 } fragment) {
            return false;
        }

        var offset = NodeGraphClipboard.DefaultOffset;

        if (at is { } target) {
            var corner = new Vector2(fragment.Nodes[0].X, fragment.Nodes[0].Y);

            foreach (var node in fragment.Nodes) {
                corner = new(Math.Min(corner.X, node.X), Math.Min(corner.Y, node.Y));
            }

            offset = target - corner;
        }

        var command = new PasteCommand(graph, fragment, offset, edited);

        if (!Run(command)) {
            return false;
        }

        // Selecting what was pasted is what makes the next gesture — a drag — mean the new nodes
        // rather than the ones they were copied from.
        selection.Clear();

        foreach (var node in command.Pasted) {
            selection.Add(node.Id);
        }

        Project();

        return true;
    }

    /// <summary>Lays the whole graph out left to right.</summary>
    /// <returns>Whether anything moved.</returns>
    public bool AutoLayout() => Run(LayoutCommand.For(graph, registry, LayoutOptions(), edited));

    /// <summary>What the layout should assume about node sizes, read off the canvas's theme.</summary>
    /// <remarks>
    ///     Read from the canvas rather than guessed, so a theme that makes nodes taller makes the
    ///     automatic layout leave more room instead of overlapping them.
    /// </remarks>
    public NodeLayoutOptions LayoutOptions() =>
        new(
            default,
            80f,
            24f,
            NodeWidth,
            Canvas.HeaderHeight,
            Canvas.PortPitch,
            Canvas.NodePadding
        );

    /// <summary>Lifts what is selected out into a sub-graph.</summary>
    /// <param name="name">What the new graph is called.</param>
    /// <param name="path">The node-type path the sub-graph will be registered under.</param>
    /// <param name="library">The library to register it in, so the node that replaces it resolves.</param>
    /// <returns>The new graph, or null when nothing was selected or the view is read-only.</returns>
    /// <remarks>
    ///     ⚠ <b>The sub-graph is registered before the edit is recorded.</b> The command adds a node of
    ///     a type that has to resolve for the reprojection that follows to draw its ports — and undo
    ///     does not unregister it, deliberately: a redo would then be adding a node of a type that had
    ///     gone away.
    /// </remarks>
    public NodeGraphModel? ExtractSelection(string name, string path, SubGraphLibrary library) {
        ArgumentNullException.ThrowIfNull(library);

        if (selection.Count == 0 || Stack is null) {
            return null;
        }

        var extraction = SubGraphs.Extract(graph, [.. selection], name, registry);
        var centre = Centre();

        library.Add(path, extraction.Graph, registry);

        if (!Run(new ExtractSubGraphCommand(graph, extraction, path, centre, edited))) {
            return null;
        }

        SubGraphSource ??= library;

        return extraction.Graph;
    }

    /// <summary>Pans and zooms until everything fits, or until the selection does.</summary>
    /// <param name="selectionOnly">Whether to frame only what is selected.</param>
    public void Frame(bool selectionOnly = false) => Canvas.ZoomToFit(selectionOnly);

    /// <summary>Selects some nodes and nothing else.</summary>
    /// <param name="nodes">Which.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes" /> is null.</exception>
    public void Select(IEnumerable<NodeId> nodes) {
        ArgumentNullException.ThrowIfNull(nodes);

        selection.Clear();

        foreach (var id in nodes) {
            selection.Add(id);
        }

        projecting = true;

        try {
            Reselect();
        } finally {
            projecting = false;
        }

        SelectionChanged?.Invoke(this);
    }

    /// <summary>Opens the create-a-node popup, with nothing wired to it.</summary>
    /// <param name="x">Where, in document space.</param>
    /// <param name="y">And vertically.</param>
    public void OpenSearch(float x, float y) {
        pendingWire = false;
        pendingAt = Canvas.ToGraph(x, y);

        Search.Show(registry, null, x, y);
    }

    bool Run(IEditorCommand command) {
        if (Stack is not { } stack) {
            return false;
        }

        stack.Execute(command);

        return true;
    }

    Vector2 Snap(Vector2 point) =>
        Canvas.SnapToGrid && Canvas.GridSize > 0f
            ? new Vector2(
                MathF.Round(point.X / Canvas.GridSize) * Canvas.GridSize,
                MathF.Round(point.Y / Canvas.GridSize) * Canvas.GridSize
            )
            : point;

    Vector2 Centre() {
        if (selection.Count == 0) {
            return default;
        }

        var total = Vector2.Zero;
        var counted = 0;

        foreach (var id in selection) {
            if (graph.TryGet(id, out var node)) {
                total += node.Position;
                counted++;
            }
        }

        return counted == 0 ? default : total / counted;
    }

    // ── What the canvas tells us ─────────────────────────────────────────────

    void OnGraphChanged(NodeGraphModel changed) {
        if (!projecting && !typing) {
            Project();
        }

        GraphChanged?.Invoke(this);
    }

    void Reselected() {
        if (projecting) {
            return;
        }

        selection.Clear();

        foreach (var node in Canvas.Selection) {
            if (identities.TryGetValue(node, out var id)) {
                selection.Add(id);
            }
        }

        SelectionChanged?.Invoke(this);
    }

    void Reselect() {
        Canvas.ClearSelection();

        foreach (var id in selection) {
            if (shown.TryGetValue(id, out var node)) {
                Canvas.Select(node, ModifierKeys.Shift);
            }
        }
    }

    /// <remarks>
    ///     ⚠ <b>The "before" comes from the model, which the drag has not touched.</b> The canvas has
    ///     already written the new positions onto its own nodes, so the difference between the two is
    ///     exactly what moved — and building the command before writing anything back is what makes
    ///     it record the right starting point.
    /// </remarks>
    void Dropped() {
        if (projecting) {
            return;
        }

        Dictionary<NodeId, Vector2> moved = [];

        foreach (var (id, view) in shown) {
            if (graph.TryGet(id, out var node) && node.Position != view.Position) {
                moved[id] = view.Position;
            }
        }

        if (moved.Count == 0 || !Run(new MoveNodesCommand(graph, moved, edited))) {
            return;
        }

        // The drag is over, so the next one is a separate undo entry. Merging is what makes a drag
        // that produced several commands one entry; sealing is what stops two drags becoming one.
        Stack?.Seal();
    }

    void Wired(GraphWire wire) {
        if (projecting) {
            return;
        }

        if (!sockets.TryGetValue(wire.From, out var from) || !sockets.TryGetValue(wire.To, out var to)) {
            Project();

            return;
        }

        var source = new PortRef(from.Node, from.Port);
        var target = new PortRef(to.Node, to.Port);

        if (Refuses(source, target) is { } reason) {
            Project();
            ConnectionRefused?.Invoke(this, new(source, target, reason));

            return;
        }

        if (Stack is not { } stack) {
            Project();

            return;
        }

        // A wire pulled off an input and dropped on another one is a reroute, which is two edits and
        // one gesture: the old edge has to go or the graph ends up with both.
        var detached = PulledOff();

        using (stack.BeginTransaction("Connect")) {
            if (detached is { } edge && edge.To != target) {
                Run(new DisconnectCommand(graph, edge.To, edited));
            }

            Run(new ConnectCommand(graph, source, target, edited));
        }
    }

    /// <summary>Records a number typed into a port on the canvas.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It does not reproject, which is the second of the two exceptions this class
    ///         makes — the first being a drag.</b> For the same reason: dragging across a number field
    ///         raises this every frame, and rebuilding the whole projection per frame is a cost that
    ///         grows with the graph rather than with the screen. The canvas already holds exactly what
    ///         was written, because the field wrote it there before telling anybody, so there is
    ///         nothing for a reprojection to correct.
    ///     </para>
    ///     <para>
    ///         <b>Merging is what makes a scrub one undo entry.</b> <see cref="SetPortValueCommand" />
    ///         folds the run into one and keeps the first "before" — including whether the port had a
    ///         value at all.
    ///     </para>
    /// </remarks>
    void Typed(CanvasPort port) {
        if (projecting || port.Editor is not { } editor || !sockets.TryGetValue(port, out var key)) {
            return;
        }

        if (!graph.TryGet(key.Node, out var node)) {
            return;
        }

        var value = editor.ToArray();

        if (Stack is { } stack) {
            typing = true;

            try {
                stack.Execute(new SetPortValueCommand(graph, key.Node, key.Port, value, edited));
            } finally {
                typing = false;
            }

            return;
        }

        // ⚠ A view with no stack marks its editors read-only — see Inline — so this is unreachable
        // through the canvas. It is here for the same reason NodeInspector's is: a caller driving the
        // projection directly should write to the model rather than to a picture of it.
        node.SetValue(key.Port, value);
        graph.Touch();
    }

    void Opened(CanvasNode node) {
        if (identities.TryGetValue(node, out var id)
            && graph.TryGet(id, out var found)
            && SubGraphSource?.TryGet(found.Type, out var child) == true) {
            SubGraphOpened?.Invoke(this, found.Type, child!);
        }
    }

    void Created(NodeSearchResult result) {
        if (Stack is not { } stack) {
            return;
        }

        if (!pendingWire) {
            Create(result.Type.Path, pendingAt);

            return;
        }

        using (stack.BeginTransaction("Add Node")) {
            if (Create(result.Type.Path, pendingAt) is not { } node || result.Port.Length == 0) {
                return;
            }

            var made = new PortRef(node.Id, result.Port);

            Run(pendingSide == PortDirection.Output
                ? new ConnectCommand(graph, pending, made, edited)
                : new ConnectCommand(graph, made, pending, edited));
        }
    }

    /// <summary>The model edge the canvas has taken out of its own picture, if it has taken one.</summary>
    /// <remarks>
    ///     ⚠ <b>Worked out by comparing rather than reported.</b> <c>NodeCanvas</c> picks a wire up
    ///     when the press lands on a connected input — which is the only gesture that can mean "move
    ///     this connection" — and it does so by disconnecting its own graph, with no event for it. The
    ///     difference between the model's edges and the picture's wires is therefore exactly what was
    ///     picked up, and needs nothing from the canvas.
    /// </remarks>
    GraphEdge? PulledOff() {
        HashSet<(NodeId, string, NodeId, string)> drawn = [];

        foreach (var wire in Canvas.Graph.Wires) {
            if (sockets.TryGetValue(wire.From, out var from) && sockets.TryGetValue(wire.To, out var to)) {
                drawn.Add((from.Node, from.Port, to.Node, to.Port));
            }
        }

        foreach (var edge in graph.Edges) {
            if (!drawn.Contains((edge.From.Node, edge.From.Port, edge.To.Node, edge.To.Port))) {
                return edge;
            }
        }

        return null;
    }

    /// <summary>Why a wire may not be made, or null when it may.</summary>
    /// <remarks>
    ///     <b>Type checking lives here rather than in the model.</b> The model refuses what it cannot
    ///     represent — a cycle, a node wired to itself — and a port's <i>kind</i> is a fact about a
    ///     registry, which a document deliberately does not depend on. So a graph saved against a
    ///     missing plugin still holds its wiring, and the view is what stops a new one being made
    ///     between two ports that could never carry each other.
    /// </remarks>
    string? Refuses(PortRef from, PortRef to) {
        if (!graph.TryGet(from.Node, out var source) || !graph.TryGet(to.Node, out var sink)) {
            return "One end of that wire is not in the graph.";
        }

        if (Definition(source.Type) is not { } output || Definition(sink.Type) is not { } input) {
            return null;
        }

        if (output.Port(from.Port, PortDirection.Output) is not { } left
            || input.Port(to.Port, PortDirection.Input) is not { } right) {
            return null;
        }

        if (left.Kind == PortKind.Dynamic || right.Kind == PortKind.Dynamic) {
            return null;
        }

        return PortKinds.Accepts(left.Kind, right.Kind)
            ? null
            : $"'{from.Port}' carries a {left.Kind} and '{to.Port}' wants a {right.Kind}. There is no "
            + "conversion between those two that would mean anything.";
    }

    // ── Input ────────────────────────────────────────────────────────────────

    void Pointed(PointerEvent args) {
        pointer = new(args.X, args.Y);

        if (args.Action != PointerAction.Released || Canvas.PendingPort is not { } port) {
            return;
        }

        // Hit-tested here rather than waiting for the canvas: if the release is over a port the
        // canvas is about to connect it and raise Connected, which is the path that handles it.
        for (var walk = Document.HitTest(args.X, args.Y); walk is not null; walk = walk.Parent) {
            if (walk is NodePortView) {
                return;
            }
        }

        if (!sockets.TryGetValue(port, out var socket)) {
            return;
        }

        // Pulled off an input and dropped on nothing: that is a disconnection, not a request for a
        // new node. Dragged from a port that was empty: that is search-to-create.
        if (PulledOff() is { } detached) {
            Run(new DisconnectCommand(graph, detached.To, edited));

            return;
        }

        pending = new(socket.Node, socket.Port);
        pendingSide = socket.Direction;
        pendingAt = Snap(Canvas.ToGraph(args.X, args.Y));
        pendingWire = true;

        var kind = KindOf(socket) ?? PortKind.Dynamic;
        var wanted = socket.Direction == PortDirection.Output ? PortDirection.Input : PortDirection.Output;

        Search.Show(registry, new PortFilter(kind, wanted), args.X, args.Y);
    }

    PortKind? KindOf(Socket socket) {
        if (!graph.TryGet(socket.Node, out var node) || Definition(node.Type) is not { } definition) {
            return null;
        }

        return definition.Port(socket.Port, socket.Direction)?.Kind;
    }

    /// <remarks>
    ///     ⚠ <b>Claimed on the capture leg, so this runs before the field the key was typed into.</b>
    ///     The canvas has the same guard for the same reason; both are needed, because this handler
    ///     would otherwise take Delete out of a number box on a node and delete the node instead.
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || Document.Focused is TextField) {
            return;
        }

        var control = args.Modifiers.HasFlag(ModifierKeys.Control) || args.Modifiers.HasFlag(ModifierKeys.Meta);

        switch (args.Key) {
            case InputKey.Delete or InputKey.Backspace when selection.Count > 0:
                DeleteSelection();
                break;

            case InputKey.C when control:
                CopySelection();
                break;

            case InputKey.X when control:
                CutSelection();
                break;

            case InputKey.V when control:
                Paste(Canvas.ToGraph(pointer.X, pointer.Y));
                break;

            case InputKey.G when control && args.Modifiers.HasFlag(ModifierKeys.Shift):
                UngroupSelection();
                break;

            case InputKey.G when control:
                GroupSelection();
                break;

            case InputKey.L when control:
                AutoLayout();
                break;

            case InputKey.Space:
                OpenSearch(pointer.X, pointer.Y);
                break;

            default:
                return;
        }

        // Claimed, so the canvas's own handler does not act on the same key — which for Delete would
        // be a second, unrecorded deletion from its copy of the graph.
        args.Handled = true;
    }
}
