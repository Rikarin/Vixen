// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Ai;
using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>A behaviour tree, open for editing: the canvas, the blackboard, and what is selected.</summary>
/// <remarks>
///     <para>
///         The mandatory editor of doc 37 § Part 5, in four columns: the tree drawn top-down with its
///         decorators stacked above each node and its services below, an execution-index badge on
///         every header, the blackboard's key list, and the selected thing's settings generated from
///         its declaration with the compiler's complaints under them.
///     </para>
///     <para>
///         ⚠ <b>The canvas is <c>NodeCanvas</c> and the model is not <c>NodeGraphModel</c>.</b>
///         doc 37 § D19 works through the framework's four rules: a tree passes two of them and fails
///         two more that a shader graph has never needed — a composite's children are <i>ordered</i>
///         where <c>Edges</c> is an unordered list, and a decorator <i>attaches</i> where the model
///         has no notion of an attachment. Adding both to the framework was refused because a
///         framework that grows a feature for one consumer grows a feature every consumer's tests
///         have to consider. So the document projects onto the canvas and every edit goes back
///         through the model.
///     </para>
///     <para>
///         ⚠ <b>Selecting a decorator shades what it can interrupt.</b> That is the payoff for taking
///         Unity's scope rule over Unreal's: an observer reaches the siblings under its own parent
///         composite and no further, which makes the region a subtree — and a subtree is a thing that
///         can be drawn. A rule you can draw is a rule an author can predict.
///     </para>
/// </remarks>
public sealed class BehaviorTreeView : Control {
    readonly BehaviorTreeProjection projection = new();

    BehaviorTreeDocument? document;
    AgentDebugModel? live;
    BehaviorNodeContent? selected;
    BehaviorAttachmentContent? selectedAttachment;
    BehaviorAttachmentSlot selectedSlot;
    bool listening;
    Vector2 pointer;

    /// <inheritdoc />
    protected override string TagName => "behaviortree-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The tree.</summary>
    public NodeCanvas Canvas { get; private set; } = null!;

    /// <summary>The column beside it.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The blackboard's key list.</summary>
    public UiElement Keys { get; private set; } = null!;

    /// <summary>The selected thing's settings.</summary>
    public UiElement Inspector { get; private set; } = null!;

    /// <summary>The attachment strip for the selected node.</summary>
    public UiElement Attachments { get; private set; } = null!;

    /// <summary>What the last compile said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <summary>The button that compiles the tree.</summary>
    public Button Build { get; private set; } = null!;

    /// <summary>The button that lays it out.</summary>
    public Button Arrange { get; private set; } = null!;

    /// <summary>The button that searches for a node to add under the selection, or beside it.</summary>
    public Button AddNode { get; private set; } = null!;

    /// <summary>The button that searches for a decorator to put on the selected node.</summary>
    public Button AddDecorator { get; private set; } = null!;

    /// <summary>The button that searches for a service to put on the selected composite.</summary>
    public Button AddService { get; private set; } = null!;

    /// <summary>The search-to-create popup, filtered by what may go where.</summary>
    /// <remarks>
    ///     Opened three ways, and until #1370 by none: Space over the tree (a node, at the pointer,
    ///     as the shader graph does), <see cref="AddNode" />, and the two buttons over the attachment
    ///     list. It is a root child, so it goes when this view does — see <see cref="OnRemoved" />.
    /// </remarks>
    public BehaviorSearchPopup Search { get; private set; } = null!;

    /// <summary>The node that is selected, or null.</summary>
    public BehaviorNodeContent? Selected => selected;

    /// <summary>The attachment that is selected, or null.</summary>
    public BehaviorAttachmentContent? SelectedAttachment => selectedAttachment;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var body = Add("behaviortree-body");

        Canvas = body.Add<NodeCanvas>();

        // Top-down, which is what makes a parent sit over the branch it owns. The canvas anchors a
        // wire at the top and bottom edges in this orientation rather than at the sides.
        Canvas.Orientation = GraphOrientation.Vertical;
        Canvas.SelectionChanged += _ => Picked();
        Canvas.NodesMoved += _ => Moved();
        Canvas.Dropped += (_, node, point) => Dropped(node, point);

        Side = body.Add("behaviortree-side");

        var toolbar = Side.Add("behaviortree-toolbar");

        Build = toolbar.Add<Button>();
        Build.Label = "Compile";
        Arrange = toolbar.Add<Button>();
        Arrange.Label = "Lay out";
        AddNode = toolbar.Add<Button>();
        AddNode.Label = "Add node";

        Side.Add("panel-title").Text = "Blackboard";
        Keys = Side.Add("behaviortree-keys");

        Side.Add("panel-title").Text = "Attachments";

        var adding = Side.Add("behaviortree-toolbar");

        AddDecorator = adding.Add<Button>();
        AddDecorator.Label = "Add decorator";
        AddService = adding.Add<Button>();
        AddService.Label = "Add service";

        Attachments = Side.Add("behaviortree-attachments");

        Side.Add("panel-title").Text = "Settings";
        Inspector = Side.Add("behaviortree-inspector");

        Side.Add("panel-title").Text = "Diagnostics";
        Diagnostics = Side.Add("behaviortree-diagnostics");

        // A root child, as the shader graph's is: an overlay has to hang outside whatever clips this
        // panel, which for a tree docked in a corner is most of the window.
        Search = Document.Root.Add<BehaviorSearchPopup>();
        Search.Chosen += Created;

        AddHandler<ClickEvent>(static (element, args) => ((BehaviorTreeView) element).Chosen(args));

        // Capture, so Space is taken before the canvas reads it as "activate the node the arrows
        // are on" — the trade `NodeGraphView` makes for the same key, and Enter still does that.
        // Only with the focus in the canvas, though; see `Keyed`.
        AddHandler<KeyEvent>(static (element, args) => ((BehaviorTreeView) element).Keyed(args), RoutingStrategy.Capture);
        AddHandler<PointerEvent>(
            static (element, args) => ((BehaviorTreeView) element).pointer = new(args.X, args.Y),
            RoutingStrategy.Capture,
            handledEventsToo: true
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The popup is a root child, so it does not go when this does</b> — <c>NodeGraphView</c>'s
    ///     debt, paid the same way.
    /// </remarks>
    protected override void OnRemoved() {
        if (Search is { IsRemoved: false } popup) {
            Document.Remove(popup);
        }

        base.OnRemoved();
    }

    /// <summary>Shows a document.</summary>
    /// <param name="value">The tree.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public void Show(BehaviorTreeDocument value) {
        ArgumentNullException.ThrowIfNull(value);

        document = value;

        if (!listening) {
            document.Changed += _ => Refresh();
            listening = true;
        }

        selected = document.Model.Content.Root;
        Refresh();
        Compile();
    }

    /// <summary>Selects a node, and shades what a selected decorator could interrupt.</summary>
    /// <param name="node">The node, or null for nothing.</param>
    /// <param name="attachment">Which of its attachments, or null.</param>
    /// <param name="slot">Which list that attachment is in.</param>
    public void Select(
        BehaviorNodeContent? node,
        BehaviorAttachmentContent? attachment = null,
        BehaviorAttachmentSlot slot = BehaviorAttachmentSlot.Decorator
    ) {
        selected = node;
        selectedAttachment = attachment;
        selectedSlot = slot;

        RefreshAttachments();
        RefreshInspector();
        RefreshOverlay();
    }

    /// <summary>Follows a running agent, tinting the canvas by what it is doing.</summary>
    /// <param name="value">The debugger's model, or null to stop following one.</param>
    /// <remarks>
    ///     ⚠ <b>doc 37 § Part 5's "Live, in play mode", and it is the canvas rather than a second
    ///     panel.</b> The agent debugger already lists the active path; what an author wants while
    ///     reading a tree is to see it <i>on the tree</i>, tinted by what each node last returned —
    ///     "why is the second child running" is answered by the first one being red.
    /// </remarks>
    public void Follow(AgentDebugModel? value) {
        live = value;

        RefreshLive();
    }

    /// <summary>Re-tints the canvas from whatever agent is being followed.</summary>
    /// <returns>How many boxes were tinted.</returns>
    public int RefreshLive() {
        var tinted = projection.Live(live?.Instance);

        // ⚠ Breakpoints are drawn from the model rather than from the instance, because a breakpoint
        // exists whether or not an agent is currently running the tree — and an author setting one
        // before pressing play must see it.
        if (live is { } model && document is not null) {
            var tree = Symbol.Intern(document.Model.Content.Name);
            var index = 0;

            foreach (var node in document.Model.Walk()) {
                if (projection.BoxOf(node) is { } box && model.Breakpoints.Contains(tree, index)) {
                    box.Badge = $"● {index.ToString(CultureInfo.InvariantCulture)}";
                }

                index++;
            }
        }

        return tinted;
    }

    /// <summary>Compiles the tree and lists what it said.</summary>
    /// <returns>The template, or null.</returns>
    public BehaviorTreeTemplate? Compile() {
        if (document is null) {
            return null;
        }

        var template = document.Compile();

        Empty(Diagnostics);

        foreach (var diagnostic in document.Diagnostics) {
            var row = Diagnostics.Add("analysis-row");

            row.Add("analysis-stage").Text = diagnostic.Node.IsSome ? diagnostic.Node.ToString() : "tree";
            row.Add("analysis-message").Text = diagnostic.Message;
        }

        // Said on success too, for the reason the shader graph's list gives: a list that empties
        // itself when everything is fine cannot be told apart from one that never ran.
        if (template is not null && document.Diagnostics.Count == 0) {
            var row = Diagnostics.Add("analysis-row");

            row.Add("analysis-stage").Text = "tree";
            row.Add("analysis-message").Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{template.Name} compiles: {template.Count} node(s), {template.Decorators.Length} decorator(s), "
                + $"{template.Services.Length} service(s), {template.MemorySize} bytes an agent."
            );
        }

        return template;
    }

    /// <summary>Rebuilds every panel from the document.</summary>
    public void Refresh() {
        if (document is null) {
            return;
        }

        Canvas.Graph = projection.Project(document.Model);

        if (selected is not null && projection.BoxOf(selected) is null) {
            // The selection outlived the node — a delete, or an undo that took the subtree with it.
            selected = document.Model.Content.Root;
            selectedAttachment = null;
        }

        RefreshKeys();
        RefreshAttachments();
        RefreshInspector();
        RefreshOverlay();
        RefreshLive();
    }

    /// <summary>Opens the search popup for what may go in a slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="x">Where to put the popup, in document space.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>Whether it opened: a decorator needs a selected node, and a service a selected composite.</returns>
    public bool OpenSearch(BehaviorSlot slot, float x, float y) {
        if (document is null) {
            return false;
        }

        switch (slot) {
            case BehaviorSlot.Decorator when selected is null:
            case BehaviorSlot.Service when !IsComposite(selected):
                return false;

            case BehaviorSlot.Composite or BehaviorSlot.Task:
                return OpenNodeSearch(x, y);
        }

        Search.Show(document.Model.Schema, slot, x, y);

        return true;
    }

    /// <summary>Opens the search popup for a node to add under the selection, or beside it.</summary>
    /// <param name="x">Where to put the popup, in document space.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>Whether there was anywhere to put one.</returns>
    /// <remarks>
    ///     A composite's child row takes a composite or a task, so both are offered. What the new node
    ///     goes under is decided when it is chosen — <see cref="PlaceFor" />.
    /// </remarks>
    public bool OpenNodeSearch(float x, float y) {
        if (document is null || PlaceFor(document.Model) is null) {
            return false;
        }

        Search.Show(document.Model.Schema, [BehaviorSlot.Composite, BehaviorSlot.Task], x, y);

        return true;
    }

    /// <summary>Where a node created now would go: under the selection, or beside it.</summary>
    /// <returns>
    ///     The parent (null for "becomes the root") and the index among its children, or null when
    ///     there is nowhere — a task is the root and cannot have a sibling.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>A task takes no children, so a node asked for "under" one goes after it.</b> Inserting
    ///     under a leaf was what <see cref="Created" /> would have done had anything ever called it,
    ///     and the compiler would then have refused the tree.
    /// </remarks>
    (BehaviorNodeContent? Parent, int At)? PlaceFor(BehaviorTreeModel model) {
        if (model.Content.Root is null) {
            return (null, -1);
        }

        if (selected is null) {
            return null;
        }

        if (IsComposite(selected)) {
            return (selected, -1);
        }

        return model.Parent(selected) is { } parent ? (parent, parent.Children.IndexOf(selected) + 1) : null;
    }

    bool IsComposite(BehaviorNodeContent? node) =>
        node is not null && document is not null
        && (node.Children.Count > 0 || document.Model.TypeOf(node) is { Slot: BehaviorSlot.Composite });

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || args.Key != InputKey.Space || args.Modifiers != ModifierKeys.None) {
            return;
        }

        // ⚠ Only while the focus is on the canvas. This handler sees the capture leg of the whole
        // view, side column included, and Space is how a focused button is pressed: taken here, it
        // opened the node search in place of the button — "Add decorator" asked for a composite.
        // Refusing everything outside the canvas also covers whatever control the column gains next.
        // A caret inside the canvas keeps its spaces: `ITextInputTarget` rather than `TextField`,
        // for the reason #650 gave `CommandDispatcher` — a code editor wants them too.
        if (!OnCanvas(Document.Focused) || Document.Focused is ITextInputTarget) {
            return;
        }

        if (OpenNodeSearch(pointer.X, pointer.Y)) {
            args.Handled = true;
        }
    }

    bool OnCanvas(UiElement? focused) {
        for (var walk = focused; walk is not null && !ReferenceEquals(walk, this); walk = walk.Parent) {
            if (ReferenceEquals(walk, Canvas)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Opens the search under a button, which is where somebody who clicked it is looking.</summary>
    void OpenUnder(UiElement button, BehaviorSlot slot) {
        var at = button.Bounds;

        OpenSearch(slot, at.Left, at.Bottom + 4f);
    }

    /// <summary>Greys out the attachment buttons that have nothing to attach to.</summary>
    void RefreshButtons() {
        AddDecorator.Disabled = selected is null;
        AddService.Disabled = !IsComposite(selected);
        AddNode.Disabled = document is null || PlaceFor(document.Model) is null;
    }

    void RefreshKeys() {
        Empty(Keys);

        if (document is null) {
            return;
        }

        foreach (var key in document.Model.Content.Keys) {
            var row = Keys.Add("behaviortree-key");

            row.Add("key-name").Text = key.Name;
            row.Add("key-type").Text = key.Type.ToString();
        }
    }

    void RefreshAttachments() {
        Empty(Attachments);
        RefreshButtons();

        if (selected is null || document is null) {
            return;
        }

        Rows(selected.Decorators, BehaviorAttachmentSlot.Decorator);
        Rows(selected.Services, BehaviorAttachmentSlot.Service);

        void Rows(List<BehaviorAttachmentContent> list, BehaviorAttachmentSlot slot) {
            foreach (var attachment in list) {
                var row = Attachments.Add("behaviortree-attachment");

                row.Add("attachment-kind").Text = slot == BehaviorAttachmentSlot.Decorator ? "decorator" : "service";
                row.Add("attachment-name").Text = document!.Model.TypeOf(attachment)?.Label ?? attachment.Type;

                if (ReferenceEquals(attachment, selectedAttachment)) {
                    row.AddClass("selected");
                }
            }
        }
    }

    /// <summary>
    ///     Draws the selected thing's settings from its declaration, with no per-node editor code.
    /// </summary>
    /// <remarks>
    ///     doc 34's <c>GoalKindSchema</c> generates a goal's inspector the same way, and for the same
    ///     reason: an inspector written by hand per node is fifty places for a label and a tooltip to
    ///     drift from what the node does, in a library that is a list this document expects to grow.
    /// </remarks>
    void RefreshInspector() {
        Empty(Inspector);

        if (document is null) {
            return;
        }

        var type = selectedAttachment is { } attachment
            ? document.Model.TypeOf(attachment)
            : selected is null ? null : document.Model.TypeOf(selected);

        if (type is null) {
            Inspector.Add("inspector-note").Text = selected is null
                ? "Nothing selected."
                : $"'{selected.Type}' is not a node this build knows.";

            return;
        }

        Inspector.Add("inspector-note").Text = type.Description;

        var fields = selectedAttachment?.Fields ?? selected!.Fields;

        foreach (var field in type.Fields) {
            var row = Inspector.Add("inspector-row");

            row.Add("inspector-label").Text = field.Label;
            row.Add("inspector-value").Text = BehaviorNodeSchema.Read(type, fields, field.Name);
            row.Add("inspector-hint").Text = field.Description;
        }
    }

    void RefreshOverlay() {
        Canvas.Overlay.Clear();

        if (document is null || selected is null || selectedAttachment is null
            || selectedSlot != BehaviorAttachmentSlot.Decorator) {
            Canvas.Refresh();

            return;
        }

        var scope = document.Model.AbortScope(selected, selectedAttachment);

        if (scope.Count > 0
            && projection.RegionOf(scope, Canvas) is { Width: > 0f } region) {
            Canvas.Overlay.Add(new(region, new Color4(0.42f, 0.48f, 0.88f, 0.14f), new Color4(0.42f, 0.48f, 0.88f, 0.5f)));
        }

        Canvas.Refresh();
    }

    void Picked() {
        if (Canvas.Selection.Count == 1) {
            foreach (var box in Canvas.Selection) {
                Select(BehaviorTreeProjection.NodeOf(box));
            }

            return;
        }

        Select(null);
    }

    void Moved() {
        if (document is null) {
            return;
        }

        // ⚠ One entry for the whole drag, and it merges with the one before it: the canvas writes
        // positions as the pointer moves and tells us once on release, so this is already the end of
        // the gesture rather than a step in it.
        document.Edit(
            "Move Nodes",
            model => {
                foreach (var box in Canvas.Graph.Nodes) {
                    if (BehaviorTreeProjection.NodeOf(box) is { } node) {
                        model.Move(node, box.Position.X, box.Position.Y);
                    }
                }
            },
            mergeKey: "move"
        );
    }

    /// <summary>Turns a node dropped between two siblings into a reorder.</summary>
    /// <remarks>
    ///     ⚠ <b>The canvas reports the gesture and this decides what it meant</b>, because a canvas
    ///     cannot know that a tree's child order is a list where a dataflow graph's is nothing at
    ///     all. Dropping a node onto a sibling's half makes it that sibling's neighbour; dropping it
    ///     onto a composite makes it that composite's child.
    /// </remarks>
    void Dropped(GraphNode box, Vector2 point) {
        if (document is null || BehaviorTreeProjection.NodeOf(box) is not { } moved) {
            return;
        }

        var target = Under(point, except: moved);

        if (target is null || ReferenceEquals(target, moved)) {
            return;
        }

        var model = document.Model;

        // Onto a composite's box: become its child. Onto anything else: become its neighbour, on the
        // side the pointer landed.
        if (target.Children.Count > 0 || model.TypeOf(target) is { Slot: BehaviorSlot.Composite }) {
            document.Edit("Reparent Node", edited => edited.Reparent(moved, target));

            return;
        }

        if (model.Parent(target) is not { } parent) {
            return;
        }

        var at = parent.Children.IndexOf(target) + (point.X > target.X + 90f ? 1 : 0);

        document.Edit("Reorder Node", edited => edited.Reparent(moved, parent, at));
    }

    BehaviorNodeContent? Under(Vector2 point, BehaviorNodeContent except) {
        if (document is null) {
            return null;
        }

        foreach (var node in document.Model.Walk()) {
            if (ReferenceEquals(node, except) || projection.BoxOf(node) is not { } box) {
                continue;
            }

            if (Canvas.RectOf(box).Contains(point)) {
                return node;
            }
        }

        return null;
    }

    void Created(BehaviorNodeType type) {
        if (document is null) {
            return;
        }

        switch (type.Slot) {
            case BehaviorSlot.Decorator or BehaviorSlot.Service when selected is null:
                return;

            case BehaviorSlot.Decorator:
                document.Edit(
                    "Add Decorator",
                    model => model.Attach(selected, BehaviorAttachmentSlot.Decorator, BehaviorTreeModel.MakeAttachment(type))
                );

                break;

            case BehaviorSlot.Service:
                document.Edit(
                    "Add Service",
                    model => model.Attach(selected, BehaviorAttachmentSlot.Service, BehaviorTreeModel.MakeAttachment(type))
                );

                break;

            default:
                if (PlaceFor(document.Model) is not var (parent, at)) {
                    return;
                }

                var added = BehaviorTreeModel.Make(type);

                // Under its parent, or beside the leaf it was asked for beside. The positions are a
                // first guess the layout button corrects; what matters is that it is not on top of
                // anything.
                if (selected is not null) {
                    var beside = !ReferenceEquals(parent, selected);

                    added.X = selected.X + (beside ? 200f : 0f);
                    added.Y = selected.Y + (beside ? 0f : 130f);
                }

                document.Edit("Add Node", model => model.Insert(parent, added, at));

                break;
        }
    }

/// <summary>Empties a list element, which the framework does one child at a time.</summary>
    /// <remarks>
    ///     Removal is final in this framework — see <c>NodeCanvas</c>'s pooling — so a panel that is
    ///     rebuilt on every selection change pays for it. These lists are a handful of rows and the
    ///     alternative is a pool per panel, which is machinery for a picture nobody scrolls.
    /// </remarks>
    static void Empty(UiElement list) {
        while (list.Children.Count > 0) {
            list.Children[^1].Remove();
        }
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, Build)) {
                Compile();
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Arrange)) {
                document?.Layout();
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddNode)) {
                var at = AddNode.Bounds;

                OpenNodeSearch(at.Left, at.Bottom + 4f);
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddDecorator) || ReferenceEquals(element, AddService)) {
                OpenUnder(element, ReferenceEquals(element, AddDecorator) ? BehaviorSlot.Decorator : BehaviorSlot.Service);
                args.Handled = true;

                return;
            }
        }
    }
}
