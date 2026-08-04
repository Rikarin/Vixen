// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Core.Mathematics;
using Vixen.Editor.Ai;
using Vixen.Editor.Core;
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
    BehaviorNodeContent? selected;
    BehaviorAttachmentContent? selectedAttachment;
    BehaviorAttachmentSlot selectedSlot;
    bool listening;

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

    /// <summary>The search-to-create popup, filtered by what may go where.</summary>
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
        Arrange.Text = "Lay out";

        Side.Add("panel-title").Text = "Blackboard";
        Keys = Side.Add("behaviortree-keys");

        Side.Add("panel-title").Text = "Attachments";
        Attachments = Side.Add("behaviortree-attachments");

        Side.Add("panel-title").Text = "Settings";
        Inspector = Side.Add("behaviortree-inspector");

        Side.Add("panel-title").Text = "Diagnostics";
        Diagnostics = Side.Add("behaviortree-diagnostics");

        Search = Add<BehaviorSearchPopup>();
        Search.Chosen += Created;

        AddHandler<ClickEvent>(static (element, args) => ((BehaviorTreeView) element).Chosen(args));
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
    }

    /// <summary>Opens the search popup for what may go in a slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="x">Where to put the popup.</param>
    /// <param name="y">Ditto.</param>
    public void OpenSearch(BehaviorSlot slot, float x, float y) {
        if (document is null) {
            return;
        }

        Search.Show(document.Model.Schema, slot, x, y);
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
        if (document is null || selected is null) {
            return;
        }

        switch (type.Slot) {
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
                var added = BehaviorTreeModel.Make(type);

                added.X = selected.X;
                added.Y = selected.Y + 130f;
                document.Edit("Add Node", model => model.Insert(selected, added));

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
        }
    }
}
