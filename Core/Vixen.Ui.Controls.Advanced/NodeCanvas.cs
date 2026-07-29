// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One socket, as an element.</summary>
/// <remarks>
///     ⚠ <b>It exists to be hit, not to say where the wire goes.</b> A wire's endpoint is worked out
///     from the node's rectangle — see <see cref="NodeCanvas.AnchorOf" /> — because the node at the
///     other end is very often culled and has no elements. What this is for is the pointer: a drag
///     has to start on <i>something</i>, and a dot the theme can make bigger on hover is a better
///     target than a coordinate the canvas compares against.
/// </remarks>
public sealed partial class NodePortView : Control {
    /// <inheritdoc />
    protected override string TagName => "node-port";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The port it shows, or <c>null</c> if it is parked.</summary>
    public GraphPort? Port { get; internal set; }

    /// <summary>The dot a wire appears to come out of.</summary>
    public UiElement Dot { get; private set; } = null!;

    /// <summary>The name beside it.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Dot = Part("node-dot");
        Label = Part("node-port-label");
    }
}

/// <summary>One node, as an element.</summary>
/// <remarks>
///     ⚠ <b>Rebound rather than rebuilt</b>, the same bargain <c>TreeRow</c> makes: the pool is the
///     size of what fits on screen, and panning a graph of ten thousand nodes rebinds thirty
///     elements rather than creating and destroying thousands.
/// </remarks>
public sealed partial class NodeItem : Control {
    readonly List<NodePortView> inputs = [];
    readonly List<NodePortView> outputs = [];

    /// <inheritdoc />
    protected override string TagName => "node-item";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The node it is showing, or <c>null</c> if it is parked.</summary>
    public GraphNode? Node { get; internal set; }

    /// <summary>The title bar.</summary>
    public UiElement Header { get; private set; } = null!;

    /// <summary>The row holding the two columns of ports.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>The left column.</summary>
    public UiElement InputColumn { get; private set; } = null!;

    /// <summary>The right column.</summary>
    public UiElement OutputColumn { get; private set; } = null!;

    /// <summary>The port elements on the left, including the parked ones.</summary>
    public IReadOnlyList<NodePortView> Inputs => inputs;

    /// <summary>Ditto, on the right.</summary>
    public IReadOnlyList<NodePortView> Outputs => outputs;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Header = Part("node-header");
        Body = Part("node-body");

        InputColumn = Body.Add("node-inputs");
        OutputColumn = Body.Add("node-outputs");
    }

    /// <remarks>
    ///     ⚠ <b>The header's height and the ports' pitch are written rather than left to the
    ///     theme.</b> A wire is drawn to an anchor the canvas computes from those two numbers, and a
    ///     theme that expressed them in <c>em</c> would agree with the arithmetic at one font size
    ///     and drift from it at every other — a wire that visibly misses the dot it is attached to.
    /// </remarks>
    internal void Bind(GraphNode node, float headerHeight, float portPitch) {
        Node = node;
        Header.Text = node.Title;
        Header.SetStyle("height", Inline.Px(headerHeight));

        Fill(inputs, InputColumn, node.Inputs, "input", portPitch);
        Fill(outputs, OutputColumn, node.Outputs, "output", portPitch);
    }

    /// <summary>The element showing a port, if this item has one for it.</summary>
    /// <param name="port">The port.</param>
    /// <returns>The element, or <c>null</c>.</returns>
    public NodePortView? ViewOf(GraphPort port) {
        var pool = port.Direction == PortDirection.Input ? inputs : outputs;

        foreach (var view in pool) {
            if (ReferenceEquals(view.Port, port)) {
                return view;
            }
        }

        return null;
    }

    /// <remarks>
    ///     ⚠ <b>The pool only grows, here as everywhere else in this assembly.</b> A node with six
    ///     inputs rebound onto one with two parks four elements rather than removing them — and
    ///     removal is final in this framework, so a pool that shrank would have to create fresh
    ///     elements the next time a six-input node scrolled back in.
    /// </remarks>
    static void Fill(List<NodePortView> pool, UiElement column, IReadOnlyList<GraphPort> ports, string className, float pitch) {
        while (pool.Count < ports.Count) {
            var view = column.Add<NodePortView>();
            view.AddClass(className);

            pool.Add(view);
        }

        for (var i = 0; i < pool.Count; i++) {
            var view = pool[i];

            if (i >= ports.Count) {
                view.Port = null;
                view.AddClass("parked");

                continue;
            }

            view.RemoveClass("parked");
            view.Port = ports[i];
            view.Label.Text = ports[i].Name;
            view.SetStyle("height", Inline.Px(pitch));
        }
    }
}

/// <summary>A group's box, behind the nodes that are in it.</summary>
public sealed partial class NodeGroupView : Control {
    /// <inheritdoc />
    protected override string TagName => "node-group";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The group it shows.</summary>
    public GraphGroup? Group { get; internal set; }

    /// <summary>The bar along the top, which is what drags the whole group.</summary>
    public UiElement Header { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        Header = Part("node-group-header");
    }
}

/// <summary>The layer the wires are drawn on.</summary>
/// <remarks>
///     <para>
///         An element of its own, and it has to be. <see cref="UiElement.OnDraw" /> runs before an
///         element's children, so wires drawn by the canvas would be under the groups as well as
///         under the nodes — and a group's box is opaque. A sibling between the two puts them
///         exactly where they belong: over the groups, under the nodes.
///     </para>
///     <para>
///         It takes no pointer events. A wire is selected by clicking near it, which the canvas
///         answers with arithmetic against the curve, and a full-canvas element that swallowed
///         clicks would make everything under it unreachable.
///     </para>
/// </remarks>
public sealed class NodeWireLayer : UiElement {
    readonly PathBuilder path = new();

    /// <inheritdoc />
    protected override string TagName => "node-wires";

    /// <summary>The canvas it draws for.</summary>
    public NodeCanvas? Canvas { get; internal set; }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Canvas is not { } canvas) {
            return;
        }

        var thickness = MathF.Max(1f, canvas.WireThickness * canvas.Zoom);

        foreach (var wire in canvas.Graph.Wires) {
            var lit = canvas.Selection.Contains(wire.From.Node) || canvas.Selection.Contains(wire.To.Node);

            Curve(canvas, canvas.ToScreen(canvas.AnchorOf(wire.From)), canvas.ToScreen(canvas.AnchorOf(wire.To)));
            context.Stroke(path, lit ? canvas.WireActiveColor : canvas.WireColor, thickness, cap: LineCap.Round);
        }

        // The one being dragged, from its port to the pointer. Drawn last so it is over the rest,
        // which is what makes it readable while it crosses a dense graph.
        if (canvas.PendingPort is { } pending) {
            Curve(canvas, canvas.ToScreen(canvas.AnchorOf(pending)), canvas.PendingPoint);
            context.Stroke(path, canvas.WireActiveColor, thickness, cap: LineCap.Round);
        }
    }

    /// <summary>A cubic with horizontal handles, which is what makes a wire read as a wire.</summary>
    /// <remarks>
    ///     ⚠ <b>The handle length grows with the horizontal gap and never shrinks below a
    ///     minimum.</b> Proportional alone gives a straight line between two ports at the same
    ///     height, so a wire that goes backwards — an output to the right of the input it feeds —
    ///     passes straight through both nodes instead of looping out and round.
    /// </remarks>
    void Curve(NodeCanvas canvas, Vector2 from, Vector2 to) {
        var reach = MathF.Max(canvas.WireCurvature * canvas.Zoom, MathF.Abs(to.X - from.X) * 0.5f);

        path.Clear()
            .MoveTo(from)
            .CubicTo(new Vector2(from.X + reach, from.Y), new Vector2(to.X - reach, to.Y), to);
    }
}

/// <summary>What a drag on the canvas is doing.</summary>
enum CanvasDrag : byte {
    None,
    Pan,
    Marquee,
    Nodes,
    Group,
    Wire
}

/// <summary>An infinite canvas of nodes joined by wires.</summary>
/// <remarks>
///     <para>
///         <b>Zoom is arithmetic, not a transform.</b> Nothing in <c>Vixen.Ui</c> scales a subtree —
///         there is no <c>transform</c> property and adding one is a change to the layout tree, the
///         hit test and the draw list at once. So the canvas converts graph coordinates to screen
///         coordinates itself and writes the answer as a position and a size, and the node's
///         <c>font-size</c> goes with it so that its insides scale too. Everything in the theme
///         below a node is therefore written in <c>em</c>, which is what makes one number carry the
///         whole scale.
///     </para>
///     <para>
///         ⚠ <b>A wire's endpoint is arithmetic too</b>, from the node's rectangle and the port's
///         index rather than from a laid-out port's box. A graph is mostly off screen, the node at
///         the far end of a wire usually has no elements at all, and an endpoint read from layout
///         would collapse to the origin exactly when the wire leaves the viewport. It also means a
///         wire is never a frame behind the node it is attached to, which a laid-out endpoint always
///         is.
///     </para>
///     <para>
///         <b>Culled by a pool</b>, like <c>TreeView</c>'s rows: the elements are the nodes that
///         intersect the viewport plus a margin, rebound as the canvas pans. Ten thousand nodes is
///         ten thousand <see cref="GraphNode" />s and a few dozen <see cref="NodeItem" />s.
///     </para>
///     <para>
///         <b>A resize is noticed rather than announced.</b> What the canvas realises is what its own
///         box reaches, which is a result of the layout pass — so <c>Control.WhenResized</c> hangs
///         <see cref="Refresh" /> on <see cref="UiDocument.LayoutFinished" /> and it runs on the
///         passes where the box actually moved. Panning and zooming realise for themselves, because
///         those are writes this control makes and can see.
///     </para>
/// </remarks>
public sealed partial class NodeCanvas : Control {
    readonly List<GraphNode> visible = [];
    readonly List<NodeItem> items = [];
    readonly Dictionary<GraphGroup, NodeGroupView> groups = [];
    readonly HashSet<GraphNode> selection = [];
    readonly List<GraphNode> moving = [];
    readonly PathBuilder grid = new();

    NodeGraph graph = new();
    CanvasDrag drag;
    Vector2 dragOrigin;
    Vector2 dragStart;
    Rectangle band;
    GraphNode? reduceTo;
    bool dragged;

    ComputedStyle? measured;
    Metrics metrics;

    int gridColor;
    int wireColor;
    int wireActiveColor;
    int marqueeColor;
    int headerHeight;
    int portPitch;
    int nodePadding;
    int nodeFontSize;

    /// <summary>How much of the canvas beyond each edge is still realised, as a fraction of it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not zero.</b> A pool sized exactly to the viewport creates the node entering at the
    ///     edge in the same frame it is drawn, so the first frame of a pan shows a hole where it
    ///     should be. A fifth of a screen either way is a handful of elements.
    /// </remarks>
    public const float Overscan = 0.2f;

    /// <inheritdoc />
    protected override string TagName => "node-canvas";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>The nodes, wires and groups being shown.</summary>
    /// <remarks>
    ///     Settable, because an editor shows one graph and then another and the canvas outlives
    ///     both. Assigning drops the selection and rebuilds — a selection carried over would hold
    ///     nodes from a graph nobody is looking at, and Delete would then edit it.
    /// </remarks>
    public NodeGraph Graph {
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
            Refresh();
        }
    }

    /// <summary>Where the nodes go.</summary>
    public UiElement Surface { get; private set; } = null!;

    /// <summary>Where the group boxes go, under the wires.</summary>
    public UiElement GroupLayer { get; private set; } = null!;

    /// <summary>The layer the wires are drawn on.</summary>
    public NodeWireLayer WireLayer { get; private set; } = null!;

    /// <summary>The rubber band, hidden unless one is being dragged.</summary>
    public UiElement Marquee { get; private set; } = null!;

    /// <summary>The overview in the corner.</summary>
    public NodeMinimap Minimap { get; private set; } = null!;

    /// <summary>The node elements that exist, including the parked ones.</summary>
    public IReadOnlyList<NodeItem> Items => items;

    /// <summary>The nodes that are realised, in order.</summary>
    public IReadOnlyList<GraphNode> Visible => visible;

    /// <summary>What is selected.</summary>
    public IReadOnlyCollection<GraphNode> Selection => selection;

    /// <summary>The graph point at the canvas's top-left corner.</summary>
    [UiProperty(Changed = nameof(OnViewChanged))]
    public partial Vector2 Pan { get; set; }

    /// <summary>How many screen pixels one graph unit is.</summary>
    [UiProperty(Default = 1f, Coerce = nameof(ClampZoom), Changed = nameof(OnViewChanged))]
    public partial float Zoom { get; set; }

    /// <summary>The closest the view may get.</summary>
    [UiProperty(Default = 0.1f)]
    public partial float MinimumZoom { get; set; }

    /// <summary>The furthest out it may get.</summary>
    [UiProperty(Default = 4f)]
    public partial float MaximumZoom { get; set; }

    /// <summary>How far apart the grid lines are, in graph units. Also what a drag snaps to.</summary>
    [UiProperty(Default = 16f)]
    public partial float GridSize { get; set; }

    /// <summary>Whether a dragged node lands on the grid.</summary>
    [UiProperty(Default = true)]
    public partial bool SnapToGrid { get; set; }

    /// <summary>Whether more than one node may be selected.</summary>
    [UiProperty(Default = true)]
    public partial bool MultiSelect { get; set; }

    /// <summary>How wide a wire is at a zoom of one.</summary>
    [UiProperty(Default = 2f)]
    public partial float WireThickness { get; set; }

    /// <summary>How far a wire's handles reach at a zoom of one.</summary>
    [UiProperty(Default = 60f)]
    public partial float WireCurvature { get; set; }

    /// <summary>The port a wire is being dragged from, if one is.</summary>
    public GraphPort? PendingPort { get; private set; }

    /// <summary>Where that wire's loose end is, in document space.</summary>
    public Vector2 PendingPoint { get; private set; }

    /// <summary>The colour of a wire, from <c>--wire-color</c>.</summary>
    public Color4 WireColor => Document.ColorOf(Style, wireColor) ?? new Color4(0.55f, 0.58f, 0.63f, 1f);

    /// <summary>The colour of a selected or pending one, from <c>--wire-active-color</c>.</summary>
    public Color4 WireActiveColor => Document.ColorOf(Style, wireActiveColor) ?? Document.ForegroundOf(this);

    /// <summary>How tall a node's title bar is, in graph units, from <c>--node-header</c>.</summary>
    public float HeaderHeight {
        get {
            Measure();
            return metrics.Header;
        }
    }

    /// <summary>How far apart the ports are down a node's side, from <c>--port-pitch</c>.</summary>
    public float PortPitch {
        get {
            Measure();
            return metrics.Pitch;
        }
    }

    /// <summary>How much room is left under the last port, from <c>--node-padding</c>.</summary>
    public float NodePadding {
        get {
            Measure();
            return metrics.Padding;
        }
    }

    /// <summary>A node's font size at a zoom of one, from <c>--node-font-size</c>.</summary>
    public float NodeFontSize {
        get {
            Measure();
            return metrics.FontSize;
        }
    }

    /// <summary>Reads the four graph-unit lengths out of the cascade, at most once per restyle.</summary>
    /// <remarks>
    ///     ⚠ <b>Cached, and it has to be.</b> <see cref="UiDocument.LengthOf" /> parses the interned
    ///     value every call, and <see cref="RectOf(GraphNode)" /> reads three of these — so a canvas
    ///     of ten thousand nodes was doing thirty thousand parses per realise, and the minimap,
    ///     which walks every node twice, made that quadratic. Measured at four minutes for one test.
    ///     <para>
    ///         The key is the <see cref="UiElement.Style" /> reference, which is sound because the
    ///         cascade interns computed styles: the same object means the same declarations, and a
    ///         restyle that changed any of these four hands out a different one.
    ///     </para>
    /// </remarks>
    void Measure() {
        if (ReferenceEquals(measured, Style)) {
            return;
        }

        measured = Style;

        metrics = new Metrics(
            Document.LengthOf(Style, headerHeight) ?? 22f,
            Document.LengthOf(Style, portPitch) ?? 18f,
            Document.LengthOf(Style, nodePadding) ?? 6f,
            Document.LengthOf(Style, nodeFontSize) ?? 12f
        );
    }

    readonly record struct Metrics(float Header, float Pitch, float Padding, float FontSize);

    /// <summary>Raised when the selection changes.</summary>
    public event Action<NodeCanvas>? SelectionChanged;

    /// <summary>Raised after a drag has moved some nodes.</summary>
    public event Action<NodeCanvas>? NodesMoved;

    /// <summary>Raised when a node is double-clicked.</summary>
    public event Action<NodeCanvas, GraphNode>? Activated;

    /// <summary>Raised after a wire is made by dragging.</summary>
    public event Action<NodeCanvas, GraphWire>? Connected;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        gridColor = Document.PropertyId("--grid-color");
        wireColor = Document.PropertyId("--wire-color");
        wireActiveColor = Document.PropertyId("--wire-active-color");
        marqueeColor = Document.PropertyId("--marquee-color");
        headerHeight = Document.PropertyId("--node-header");
        portPitch = Document.PropertyId("--port-pitch");
        nodePadding = Document.PropertyId("--node-padding");
        nodeFontSize = Document.PropertyId("--node-font-size");

        Surface = Part("node-surface");
        GroupLayer = Surface.Add("node-groups");

        WireLayer = Surface.Add<NodeWireLayer>();
        WireLayer.Canvas = this;

        Marquee = Part("node-marquee");
        Marquee.AddClass("hidden");

        Minimap = Part<NodeMinimap>();
        Minimap.Canvas = this;

        graph.Changed += OnGraphChanged;

        // A resize changes what the viewport covers, so it changes which nodes are realised — and a
        // canvas that is resized without being panned has nothing else to hear about it from.
        WhenResized(Refresh);

        AddHandler<PointerEvent>(static (element, args) => ((NodeCanvas) element).Pointed(args));
        AddHandler<WheelEvent>(static (element, args) => ((NodeCanvas) element).Wheeled(args));
        AddHandler<KeyEvent>(static (element, args) => ((NodeCanvas) element).Keyed(args));
        AddHandler<TapEvent>(static (element, args) => ((NodeCanvas) element).Tapped(args));
    }

    /// <summary>Draws the grid, under everything.</summary>
    /// <remarks>
    ///     ⚠ <b>The spacing doubles rather than the lines thinning out.</b> A grid drawn at a fixed
    ///     graph spacing becomes a solid fill at a zoom of a tenth — thousands of stroke segments
    ///     for a picture that is one colour — so the step is multiplied until the lines are far
    ///     enough apart to be lines.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;
        if (bounds.Width <= 0f || bounds.Height <= 0f || GridSize <= 0f) {
            return;
        }

        var step = GridSize;
        while (step * Zoom < 6f) {
            step *= 2f;

            if (step > 1e6f) {
                return;
            }
        }

        grid.Clear();

        var first = MathF.Floor(Pan.X / step) * step;

        for (var x = first; ToScreen(new Vector2(x, 0f)).X < bounds.Right; x += step) {
            var screen = ToScreen(new Vector2(x, 0f)).X;

            if (screen >= bounds.Left) {
                grid.MoveTo(new Vector2(screen, bounds.Top)).LineTo(new Vector2(screen, bounds.Bottom));
            }
        }

        for (var y = MathF.Floor(Pan.Y / step) * step; ToScreen(new Vector2(0f, y)).Y < bounds.Bottom; y += step) {
            var screen = ToScreen(new Vector2(0f, y)).Y;

            if (screen >= bounds.Top) {
                grid.MoveTo(new Vector2(bounds.Left, screen)).LineTo(new Vector2(bounds.Right, screen));
            }
        }

        context.Stroke(grid, Document.ColorOf(Style, gridColor) ?? new Color4(0f, 0f, 0f, 0.06f), 1f);
    }

    // ── Coordinates ──────────────────────────────────────────────────────────

    /// <summary>Where a graph point is, in document space.</summary>
    /// <param name="point">The point, in graph units.</param>
    /// <returns>Where it is on screen.</returns>
    public Vector2 ToScreen(Vector2 point) =>
        new(
            Surface.AbsoluteLeft + ((point.X - Pan.X) * Zoom),
            Surface.AbsoluteTop + ((point.Y - Pan.Y) * Zoom)
        );

    /// <summary>Where a document-space point is, in graph units.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The graph point.</returns>
    public Vector2 ToGraph(float x, float y) =>
        new(
            Pan.X + ((x - Surface.AbsoluteLeft) / Zoom),
            Pan.Y + ((y - Surface.AbsoluteTop) / Zoom)
        );

    /// <summary>How tall a node is, in graph units.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Its height.</returns>
    /// <remarks>
    ///     Computed rather than stored, so that adding a port to a node makes it taller without
    ///     anybody remembering to resize it — and so that a saved graph does not carry a height that
    ///     disagrees with the ports it also carries.
    /// </remarks>
    public float HeightOf(GraphNode node) {
        ArgumentNullException.ThrowIfNull(node);
        Measure();

        return metrics.Header + (Math.Max(1, node.Rows) * metrics.Pitch) + metrics.Padding;
    }

    /// <summary>Where a node is, in graph units.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Its rectangle.</returns>
    public Rectangle RectOf(GraphNode node) {
        ArgumentNullException.ThrowIfNull(node);
        return new Rectangle(node.Position.X, node.Position.Y, node.Width, HeightOf(node));
    }

    /// <summary>Where a group's box is, in graph units.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its rectangle, or an empty one if it holds no nodes.</returns>
    public Rectangle RectOf(GraphGroup group) {
        ArgumentNullException.ThrowIfNull(group);

        if (group.Nodes.Count == 0) {
            return Rectangle.Empty;
        }

        var bounds = RectOf(group.Nodes[0]);

        for (var i = 1; i < group.Nodes.Count; i++) {
            bounds = Rectangle.Union(bounds, RectOf(group.Nodes[i]));
        }

        return new Rectangle(
            bounds.X - group.Padding,
            bounds.Y - group.Padding - group.HeaderHeight,
            bounds.Width + (group.Padding * 2f),
            bounds.Height + (group.Padding * 2f) + group.HeaderHeight
        );
    }

    /// <summary>Where a wire attaches to a port, in graph units.</summary>
    /// <param name="port">The port.</param>
    /// <returns>The point.</returns>
    public Vector2 AnchorOf(GraphPort port) {
        ArgumentNullException.ThrowIfNull(port);
        Measure();

        var node = port.Node;

        return new Vector2(
            port.Direction == PortDirection.Input ? node.Position.X : node.Position.X + node.Width,
            node.Position.Y + metrics.Header + ((port.Index + 0.5f) * metrics.Pitch)
        );
    }

    /// <summary>What the canvas can see, in graph units.</summary>
    public Rectangle View => new(Pan.X, Pan.Y, Width / Zoom, Height / Zoom);

    /// <summary>The element showing a node, if it has one.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The element, or <c>null</c> if it is culled.</returns>
    public NodeItem? ItemOf(GraphNode node) {
        foreach (var item in items) {
            if (ReferenceEquals(item.Node, node)) {
                return item;
            }
        }

        return null;
    }

    /// <summary>The element showing a group, if there is one.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The element, or <c>null</c>.</returns>
    public NodeGroupView? ViewOf(GraphGroup group) => groups.GetValueOrDefault(group);

    // ── Refreshing ───────────────────────────────────────────────────────────

    /// <summary>Brings the elements back into agreement with the graph and the view.</summary>
    /// <remarks>
    ///     The one entry point after anything changes. Called for every structural change to the
    ///     graph automatically — <see cref="NodeGraph.Changed" /> is subscribed — and by hand after a
    ///     resize, which nothing announces.
    /// </remarks>
    public void Refresh() {
        // Views for groups that gained one and parking for those that lost theirs. Parked rather
        // than removed because removal is final: a group deleted and undone would need a fresh
        // element, and the undo would then have to know about elements.
        foreach (var group in graph.Groups) {
            if (!groups.ContainsKey(group)) {
                var view = GroupLayer.Add<NodeGroupView>();
                view.Group = group;

                groups[group] = view;
            }
        }

        foreach (var (group, view) in groups) {
            if (graph.Groups.Contains(group)) {
                continue;
            }

            view.Group = null;
            view.AddClass("parked");
        }

        // ⚠ A selection can outlive the nodes in it — `NodeGraph.Remove` does not know there is a
        // canvas — and a stale entry would light up whatever element was bound next. Through a set
        // rather than `Contains` per node, because the obvious version is quadratic in the size of
        // a graph that has just had one node deleted from it.
        if (selection.Count > 0) {
            var known = new HashSet<GraphNode>(graph.Nodes);
            selection.RemoveWhere(node => !known.Contains(node));
        }

        Realise();
    }

    /// <summary>Binds the pool to what is on screen and positions everything.</summary>
    void Realise() {
        if (Width <= 0f || Height <= 0f || Zoom <= 0f) {
            return;
        }

        var view = View;
        var margin = new Vector2(view.Width * Overscan, view.Height * Overscan);
        var realised = new Rectangle(
            view.X - margin.X,
            view.Y - margin.Y,
            view.Width + (margin.X * 2f),
            view.Height + (margin.Y * 2f)
        );

        visible.Clear();

        foreach (var node in graph.Nodes) {
            if (RectOf(node).Intersects(realised)) {
                visible.Add(node);
            }
        }

        while (items.Count < visible.Count) {
            items.Add(Surface.Add<NodeItem>());
        }

        var fontSize = Inline.Px(NodeFontSize * Zoom);
        var header = HeaderHeight * Zoom;
        var pitch = PortPitch * Zoom;

        for (var i = 0; i < items.Count; i++) {
            var item = items[i];

            if (i >= visible.Count) {
                item.Node = null;
                item.AddClass("parked");

                continue;
            }

            var node = visible[i];
            var rectangle = RectOf(node);
            var origin = ToScreen(rectangle.Position);

            item.RemoveClass("parked");
            item.Bind(node, header, pitch);

            item.Place(
                origin.X - Surface.AbsoluteLeft,
                origin.Y - Surface.AbsoluteTop,
                rectangle.Width * Zoom,
                rectangle.Height * Zoom
            );

            // The one property that carries the zoom into everything a node is made of: the theme
            // writes a node's padding, its gaps and its port sizes in `em`, so one number here
            // scales the lot without a transform anybody would have to implement.
            item.SetStyle("font-size", fontSize);

            if (selection.Contains(node)) {
                item.State |= ElementState.Checked;
            } else {
                item.State &= ~ElementState.Checked;
            }
        }

        foreach (var (group, gview) in groups) {
            if (gview.Group is null) {
                continue;
            }

            var rectangle = RectOf(group);
            var origin = ToScreen(rectangle.Position);

            gview.Header.Text = group.Title;
            gview.Place(
                origin.X - Surface.AbsoluteLeft,
                origin.Y - Surface.AbsoluteTop,
                rectangle.Width * Zoom,
                rectangle.Height * Zoom
            );

            gview.Header.SetStyle("height", Inline.Px(group.HeaderHeight * Zoom));
            gview.SetStyle("font-size", fontSize);
        }
    }

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>Selects a node, or adds it to or removes it from the selection.</summary>
    /// <param name="node">The node, or <c>null</c> to select nothing.</param>
    /// <param name="modifiers">What was held: Control toggles, Shift adds.</param>
    public void Select(GraphNode? node, ModifierKeys modifiers = ModifierKeys.None) {
        if (node is null) {
            if (selection.Count == 0) {
                return;
            }

            selection.Clear();
            Restate();

            return;
        }

        if (MultiSelect && modifiers.HasFlag(ModifierKeys.Control)) {
            if (!selection.Remove(node)) {
                selection.Add(node);
            }

            Restate();
            return;
        }

        if (MultiSelect && modifiers.HasFlag(ModifierKeys.Shift)) {
            selection.Add(node);
            Restate();

            return;
        }

        if (selection.Count == 1 && selection.Contains(node)) {
            return;
        }

        selection.Clear();
        selection.Add(node);

        Restate();
    }

    /// <summary>Selects everything.</summary>
    public void SelectAll() {
        if (!MultiSelect) {
            return;
        }

        selection.Clear();

        foreach (var node in graph.Nodes) {
            selection.Add(node);
        }

        Restate();
    }

    /// <summary>Selects nothing.</summary>
    public void ClearSelection() => Select(null);

    /// <summary>Pans and zooms until everything fits, or until the selection does.</summary>
    /// <param name="selectionOnly">Whether to frame only what is selected.</param>
    /// <param name="margin">How much room to leave round it, in screen pixels.</param>
    public void ZoomToFit(bool selectionOnly = false, float margin = 32f) {
        var source = selectionOnly && selection.Count > 0 ? selection : (IEnumerable<GraphNode>) graph.Nodes;

        Rectangle? bounds = null;

        foreach (var node in source) {
            bounds = bounds is { } union ? Rectangle.Union(union, RectOf(node)) : RectOf(node);
        }

        if (bounds is not { } target || Width <= 0f || Height <= 0f) {
            return;
        }

        var usable = new Vector2(MathF.Max(1f, Width - (margin * 2f)), MathF.Max(1f, Height - (margin * 2f)));

        Zoom = MathF.Min(
            target.Width <= 0f ? MaximumZoom : usable.X / target.Width,
            target.Height <= 0f ? MaximumZoom : usable.Y / target.Height
        );

        // Centred on the target rather than pinned to its corner. Read after the assignment above,
        // because the zoom was clamped and pinning against the value that was asked for rather than
        // the one that was taken puts a graph slightly off screen at both limits.
        Pan = new Vector2(
            target.Center.X - (Width / Zoom * 0.5f),
            target.Center.Y - (Height / Zoom * 0.5f)
        );
    }

    void Restate() {
        foreach (var item in items) {
            if (item.Node is { } node && selection.Contains(node)) {
                item.State |= ElementState.Checked;
            } else {
                item.State &= ~ElementState.Checked;
            }
        }

        SelectionChanged?.Invoke(this);
    }

    // ── Input ────────────────────────────────────────────────────────────────

    float ClampZoom(float value) => Math.Clamp(value, MathF.Max(0.001f, MinimumZoom), MathF.Max(0.001f, MaximumZoom));

    void OnViewChanged(Vector2 previous, Vector2 current) => Realise();

    void OnViewChanged(float previous, float current) => Realise();

    void OnGraphChanged(NodeGraph changed) => Refresh();

    /// <summary>Zooms about the pointer, so the graph point under it stays under it.</summary>
    /// <remarks>
    ///     ⚠ <b>About the pointer rather than the centre.</b> Zooming about the centre means every
    ///     approach to a node is a zoom followed by a pan to put it back where it was, which is what
    ///     makes a canvas feel like it is fighting the user.
    /// </remarks>
    void Wheeled(WheelEvent args) {
        var before = ToGraph(args.X, args.Y);
        var factor = MathF.Exp(-args.DeltaY * 0.0015f);

        Zoom *= factor;

        var after = ToGraph(args.X, args.Y);
        Pan += before - after;

        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.A when args.Modifiers.HasFlag(ModifierKeys.Control):
                SelectAll();
                break;

            case InputKey.Delete or InputKey.Backspace when selection.Count > 0:
                foreach (var node in selection.ToArray()) {
                    graph.Remove(node);
                }

                selection.Clear();
                Restate();

                break;

            case InputKey.F:
                ZoomToFit(selection.Count > 0);
                break;

            case InputKey.Escape when PendingPort is not null:
                Cancel();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Tapped(TapEvent args) {
        if (args.Count == 2 && Under<NodeItem>(args.Source) is { Node: { } node }) {
            Activated?.Invoke(this, node);
            args.Handled = true;
        }
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed:
                Begin(args);
                break;

            case PointerAction.Moved when drag != CanvasDrag.None:
                Track(args);
                break;

            case PointerAction.Released when drag != CanvasDrag.None:
                Finish(args);
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Begin(PointerEvent args) {
        Document.Focus(this);

        dragOrigin = new Vector2(args.X, args.Y);
        dragStart = ToGraph(args.X, args.Y);

        // A middle or secondary press pans, wherever it landed. That is the one gesture that has to
        // work over a node as well as over the background, because a dense graph has no background.
        if (args.Button is PointerButton.Middle or PointerButton.Secondary) {
            drag = CanvasDrag.Pan;
            dragStart = Pan;

            Document.CapturePointer(this);
            return;
        }

        if (args.Button != PointerButton.Primary) {
            return;
        }

        if (Under<NodePortView>(args.Source) is { Port: { } port }) {
            StartWire(port, args);
            return;
        }

        if (Under<NodeItem>(args.Source) is { Node: { } node }) {
            // ⚠ <b>A plain press on something already selected changes nothing yet.</b> The press is
            // very often the start of a drag of the whole selection, and reducing to one here makes
            // dragging a selection of five move one of them — the bug every canvas has had once. The
            // reduction is owed, though: a press and release that moved nothing does mean "just
            // this one", so it is remembered and applied by `Finish`.
            if (selection.Contains(node) && (args.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) {
                reduceTo = node;
            } else {
                reduceTo = null;
                Select(node, args.Modifiers);
            }

            moving.Clear();
            moving.AddRange(selection.Contains(node) ? selection : [node]);

            dragged = false;
            drag = CanvasDrag.Nodes;

            Document.CapturePointer(this);
            return;
        }

        // Only the header reaches here: the theme gives a group's body `pointer-events: none`, so a
        // press on the empty space inside a group falls through to the canvas and starts a marquee,
        // which is what a big translucent box over a third of the graph has to do.
        if (Under<NodeGroupView>(args.Source) is { Group: { } group }) {
            moving.Clear();
            moving.AddRange(group.Nodes);

            drag = CanvasDrag.Group;
            Document.CapturePointer(this);

            return;
        }

        if (!args.Modifiers.HasFlag(ModifierKeys.Shift) && !args.Modifiers.HasFlag(ModifierKeys.Control)) {
            Select(null);
        }

        drag = CanvasDrag.Marquee;
        band = new Rectangle(dragStart.X, dragStart.Y, 0f, 0f);

        Document.CapturePointer(this);
    }

    /// <remarks>
    ///     ⚠ <b>A press on a connected input picks the wire up rather than starting a second
    ///     one.</b> An input takes one wire, so dragging off one is the only gesture that can mean
    ///     "move this connection somewhere else" — and without it, rerouting is delete-then-drag
    ///     with no affordance for the delete.
    /// </remarks>
    void StartWire(GraphPort port, PointerEvent args) {
        if (port.Direction == PortDirection.Input && graph.Wire(port) is { } existing) {
            graph.Disconnect(existing);
            PendingPort = existing.From;
        } else {
            PendingPort = port;
        }

        PendingPoint = new Vector2(args.X, args.Y);
        drag = CanvasDrag.Wire;

        Document.CapturePointer(this);
    }

    void Track(PointerEvent args) {
        var point = ToGraph(args.X, args.Y);

        switch (drag) {
            case CanvasDrag.Pan:
                // ⚠ Against the pointer's screen delta divided by the zoom, not against ToGraph of
                // the new position: the second reads Pan, which is what is being written, so the
                // view would accelerate away under the cursor.
                Pan = dragStart
                    - new Vector2((args.X - dragOrigin.X) / Zoom, (args.Y - dragOrigin.Y) / Zoom);

                break;

            case CanvasDrag.Nodes or CanvasDrag.Group:
                var delta = point - dragStart;

                foreach (var node in moving) {
                    node.Position = Snap(node.Position + delta);
                }

                dragged = true;
                dragStart = point;

                Realise();
                break;

            case CanvasDrag.Marquee:
                band = Rectangle.FromCorners(dragStart, point);
                ShowMarquee();

                break;

            case CanvasDrag.Wire:
                PendingPoint = new Vector2(args.X, args.Y);
                Document.Invalidate();

                break;

            default:
                break;
        }
    }

    void Finish(PointerEvent args) {
        switch (drag) {
            case CanvasDrag.Nodes or CanvasDrag.Group:
                if (dragged) {
                    NodesMoved?.Invoke(this);
                } else if (reduceTo is { } only) {
                    // Nothing moved, so the press meant what a click means: just this one.
                    selection.Clear();
                    selection.Add(only);

                    Restate();
                }

                break;

            case CanvasDrag.Marquee:
                foreach (var node in graph.Nodes) {
                    if (RectOf(node).Intersects(band)) {
                        selection.Add(node);
                    }
                }

                Restate();
                break;

            case CanvasDrag.Wire when PendingPort is { } from:
                // Hit-tested at the release rather than tracked on the way, because a wire may be
                // dropped on empty canvas — which means "no connection" — and a target remembered
                // from the last port the pointer crossed would connect it to that instead.
                if (Under<NodePortView>(Document.HitTest(args.X, args.Y)) is { Port: { } to }
                    && graph.Connect(from, to) is { } wire) {
                    Connected?.Invoke(this, wire);
                }

                break;

            default:
                break;
        }

        Cancel();
    }

    void Cancel() {
        drag = CanvasDrag.None;
        PendingPort = null;
        reduceTo = null;
        dragged = false;

        moving.Clear();
        Marquee.AddClass("hidden");

        Document.ReleasePointer();
        Document.Invalidate();
    }

    Vector2 Snap(Vector2 point) =>
        SnapToGrid && GridSize > 0f
            ? new Vector2(MathF.Round(point.X / GridSize) * GridSize, MathF.Round(point.Y / GridSize) * GridSize)
            : point;

    void ShowMarquee() {
        var origin = ToScreen(band.Position);

        Marquee.RemoveClass("hidden");
        Marquee.Place(
            origin.X - AbsoluteLeft,
            origin.Y - AbsoluteTop,
            band.Width * Zoom,
            band.Height * Zoom
        );
    }

    /// <summary>The nearest ancestor of an element that is of a given kind, itself included.</summary>
    static T? Under<T>(UiElement? element) where T : UiElement {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (walk is T found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>The colour the rubber band is drawn in, for the minimap to match.</summary>
    internal Color4 MarqueeColor => Document.ColorOf(Style, marqueeColor) ?? new Color4(0.23f, 0.42f, 0.94f, 0.25f);
}
