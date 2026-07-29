// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Where a dropped node lands relative to the row it was dropped on.</summary>
public enum DropPosition : byte {
    /// <summary>As a child of it.</summary>
    Into,

    /// <summary>As its previous sibling.</summary>
    Before,

    /// <summary>As its next sibling.</summary>
    After
}

/// <summary>One realised row of a <see cref="TreeView" />.</summary>
/// <remarks>
///     ⚠ <b>Rebound rather than rebuilt.</b> Scrolling a tree recycles these — the row that leaves
///     the top is the row that appears at the bottom, with a different node in it — so a scroll
///     costs a handful of property writes rather than a tear-down and a rebuild of everything on
///     screen. It is also why the row holds its node rather than the other way round.
/// </remarks>
public sealed partial class TreeRow : Control {
    /// <inheritdoc />
    protected override string TagName => "tree-row";

    /// <summary>Which node it is showing, or <c>null</c> if it is parked.</summary>
    public TreeNode? Node { get; internal set; }

    /// <summary>The chevron, blank — but still occupying its column — for a node with no children.</summary>
    /// <remarks>
    ///     ⚠ <b>Blanked rather than hidden, and that is the whole of why a leaf lines up.</b> A
    ///     chevron taken out of the flow pulls its row a chevron's width to the left, so a tree of
    ///     mixed rows has two left edges and a node's text jumps sideways the moment it gains a
    ///     child. The geometry is what changes; the box never does.
    /// </remarks>
    public Icon Chevron { get; private set; } = null!;

    /// <summary>The row's own glyph, from <see cref="TreeNode.Icon" />.</summary>
    /// <inheritdoc cref="Chevron" select="remarks" />
    public Icon Glyph { get; private set; } = null!;

    /// <summary>The spacer that indents it by its depth.</summary>
    public UiElement Indent { get; private set; } = null!;

    /// <summary>The text.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <summary>The field shown while the row is being renamed.</summary>
    public TextBox? Editor { get; internal set; }

    /// <summary>The tree this row belongs to, for the metrics its guides are drawn from.</summary>
    /// <remarks>
    ///     Set by the pool that made it. A row has no way to find its tree otherwise — it is a child
    ///     of a scroll region rather than of the control — and walking the parents on every draw
    ///     would be a walk per row per frame for an answer that never changes.
    /// </remarks>
    internal TreeView? Owner { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Indent = Part("tree-indent");
        Chevron = Part<Icon>(classNames: "tree-chevron");
        Glyph = Part<Icon>(classNames: "tree-glyph");
        Label = Part("tree-label");

        Chevron.Geometry = ControlIcons.ChevronRight;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The guides are drawn rather than built out of elements.</b> A row is pooled and
    ///         its depth changes on every rebind, so an element per level would mean adding and
    ///         removing children as the view scrolls — which is the one thing virtualisation exists
    ///         to stop. Two rectangles per level, from arithmetic the row already has, cost nothing
    ///         and cannot get out of step with the indent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An ancestor's line stops where that ancestor's last child is.</b> Drawing a full
    ///         column at every level would put a line down the left of rows that have nothing above
    ///         them in that branch, which reads as a nesting that is not there.
    ///     </para>
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Owner is not { } tree || Node is not { } node || HasClass("parked")) {
            return;
        }

        var indent = tree.Indent;
        var depth = node.Depth;

        if (indent <= 0f || depth <= 0) {
            return;
        }

        var colour = tree.GuideColour;
        var bounds = Bounds;
        var origin = Indent.AbsoluteLeft;
        var middle = bounds.Y + (bounds.Height * 0.5f);

        // Each ancestor between the root and this node's parent, and only where the branch carries
        // on below this row.
        var walk = node.Parent;

        for (var level = depth - 1; level >= 1; level--) {
            if (walk is not null && HasFollowingSibling(walk)) {
                Vertical(Column(level), bounds.Y, bounds.Y + bounds.Height);
            }

            walk = walk?.Parent;
        }

        // This row's own: down to the middle, then across to the chevron. A node with a sibling
        // after it carries the line on past its own row; the last child of a branch does not.
        var x = Column(depth);

        Vertical(x, bounds.Y, HasFollowingSibling(node) ? bounds.Y + bounds.Height : middle);
        context.FillRectangle(new Rectangle(x, middle - (Thickness * 0.5f), MathF.Max(0f, indent * 0.5f), Thickness), colour);

        float Column(int level) => origin + ((level - 1) * indent) + (indent * 0.5f);

        void Vertical(float at, float from, float to) =>
            context.FillRectangle(new Rectangle(at - (Thickness * 0.5f), from, Thickness, to - from), colour);
    }

    /// <summary>How wide a guide line is, in pixels.</summary>
    const float Thickness = 1f;

    static bool HasFollowingSibling(TreeNode node) =>
        node.Parent is { } parent && parent.IndexOf(node) < parent.Children.Count - 1;
}

/// <summary>A tree, of which only what is on screen exists as elements.</summary>
/// <remarks>
///     <para>
///         <b>Virtualised, and the virtualising is <see cref="VirtualizingPanel" />'s.</b> The nodes
///         are model objects; the rows are a pool of elements the size of the viewport, rebound as
///         the view scrolls. A million-node tree is a million <see cref="TreeNode" />s and about
///         thirty <see cref="TreeRow" />s.
///     </para>
///     <para>
///         ⚠ <b>The pooling used to be written here.</b> It was the same code as the panel's — a
///         capacity from the viewport, a first index from the scroll offset, a pool that only grows
///         and parks its surplus — and two copies of an arithmetic that has to agree is one copy too
///         many. What is left of it here is what is actually about a <i>tree</i>: flattening the
///         expanded nodes into a list, and binding a row to one.
///     </para>
///     <para>
///         ⚠ <b>Rows are positioned absolutely, at a fixed height.</b> Virtualisation needs to know
///         where row 40 000 is without having measured the 39 999 above it, and a fixed height is
///         what makes that arithmetic instead of a walk. Variable-height rows need a running-sum
///         index that is maintained as things expand; that is a different control and is owed.
///     </para>
///     <para>
///         <b>A resize no longer needs telling, and this control no longer watches for one.</b> The
///         panel subscribes to <see cref="UiDocument.LayoutFinished" /> directly rather than through
///         <c>Control.WhenResized</c>, because it has to realise when the view is <i>scrolled</i> as
///         well as when it is resized — which is the case that helper's own remarks send to the event
///         itself. What is left for a tree to notice is a change to its <i>nodes</i>, and that
///         arrives through <see cref="Refresh" />, which stays public.
///     </para>
/// </remarks>
public sealed partial class TreeView : Control {
    readonly List<TreeNode> visible = [];
    readonly List<TreeRow> rows = [];
    readonly HashSet<TreeNode> selection = [];

    TreeNode? anchor;
    TreeNode? dragging;

    /// <summary>A row pressed inside the selection, whose collapse is waiting for the release.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this a multi-selection cannot be dragged at all.</b> Pressing a row selects
    ///     it, so pressing one of five selected rows to drag them leaves one selected before the drag
    ///     has begun — and every consumer that moves "the selection when it includes the dragged row"
    ///     moves one thing. The fix is every file manager's: a press inside the selection changes
    ///     nothing, and the release collapses it only if no drag happened.
    /// </remarks>
    TreeNode? pending;
    TreeNode? dropTarget;
    DropPosition dropPosition;
    int rowHeightId;
    int indentId;
    int guideColorId;

    /// <summary>How many rows are realised above and below the viewport.</summary>
    /// <remarks>
    ///     Kept as a name a caller can read, and it is <see cref="VirtualizingPanel.Overscan" />'s —
    ///     the pooling is that control's now, and two constants that had to agree would eventually
    ///     not.
    /// </remarks>
    public const int Overscan = VirtualizingPanel.Overscan;

    /// <inheritdoc />
    protected override string TagName => "tree-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>The invisible node everything hangs off.</summary>
    /// <remarks>
    ///     A real node rather than a list of roots, so that "move this to the top level" is the same
    ///     operation as "move this into that folder" and the drag code has one case rather than two.
    ///     It is never drawn.
    /// </remarks>
    public TreeNode Root { get; } = new();

    /// <summary>The virtualiser the rows live in.</summary>
    public VirtualizingPanel Panel { get; private set; } = null!;

    /// <summary>The scroller inside it.</summary>
    public ScrollView Scroller => Panel.Scroller;

    /// <summary>The line shown while a drag is over a row.</summary>
    public UiElement DropIndicator { get; private set; } = null!;

    /// <summary>How tall a row is, from <c>--row-height</c>.</summary>
    public float RowHeight => Document.LengthOf(Style, rowHeightId) ?? 22f;

    /// <summary>How far each level is indented, from <c>--indent</c>.</summary>
    public float Indent => Document.LengthOf(Style, indentId) ?? 14f;

    /// <summary>What the indent guides are drawn in, from <c>--tree-guide-color</c>.</summary>
    /// <remarks>
    ///     A faint neutral by default rather than the text colour, because a guide that reads as
    ///     loudly as a name is one the eye has to filter out of every row.
    /// </remarks>
    public Color4 GuideColour =>
        Document.ColorOf(Style, guideColorId) ?? new Color4(0.5f, 0.5f, 0.55f, 0.35f);

    /// <summary>The nodes currently showing, in order, including the ones scrolled past.</summary>
    public IReadOnlyList<TreeNode> Visible => visible;

    /// <summary>The rows that exist as elements, in pool order rather than in node order.</summary>
    /// <remarks>
    ///     Kept as a typed list of this control's own rather than read off
    ///     <see cref="VirtualizingPanel.Rows" />, which is <c>UiElement</c>: the pool is the panel's
    ///     and the <i>type</i> of what is in it is this control's, and a cast per access would be
    ///     both a lie about ownership and an allocation in a loop.
    /// </remarks>
    public IReadOnlyList<TreeRow> Rows => rows;

    /// <summary>What is selected.</summary>
    public IReadOnlyCollection<TreeNode> Selection => selection;

    /// <summary>Whether more than one node may be selected at a time.</summary>
    [UiProperty(Default = true)]
    public partial bool MultiSelect { get; set; }

    /// <summary>Whether nodes may be dragged into other nodes.</summary>
    [UiProperty(Default = true)]
    public partial bool AllowDrag { get; set; }

    /// <summary>Whether a double-click starts an inline rename instead of raising <see cref="Activated" />.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Rename-in-place is a tree gesture, not an outliner one.</b> A hierarchy and a
    ///         content browser both want "double-click the row, type the new name", and both had to
    ///         reach for <see cref="BeginRename" /> from an <see cref="Activated" /> handler of their
    ///         own — which is the same three lines written twice and the place the two panels came to
    ///         disagree about whether the row is selected first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Enter still activates.</b> Only the pointer gesture is claimed, so a browser whose
    ///         double-click renames has not lost the way to open a file — the keyboard and the
    ///         context menu are unchanged, and a caller that wants the old behaviour leaves this off.
    ///     </para>
    /// </remarks>
    [UiProperty]
    public partial bool RenameOnActivate { get; set; }

    /// <summary>Raised when the selection changes.</summary>
    public event Action<TreeView>? SelectionChanged;

    /// <summary>Raised when a node is activated — double-clicked, or Enter pressed on it.</summary>
    public event Action<TreeView, TreeNode>? Activated;

    /// <summary>Raised after a rename is committed. Returning without setting the text refuses it.</summary>
    public event Action<TreeView, TreeNode, string>? Renamed;

    /// <summary>Raised whenever a row is bound to a node, so a consumer can decorate it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>How a tree grows columns without knowing what they mean.</b> An outliner needs an
    ///         eye and a padlock beside every name; a file browser might want a source-control mark.
    ///         Neither belongs in this control — a generic tree that knew what "hidden" meant would
    ///         be the wrong place for it — and neither can be done from outside without a hook,
    ///         because the rows are pooled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It fires on every <i>re</i>bind, which is what a virtualised list mostly does.</b>
    ///         Thirty rows serve a tree of any size, so a handler that appended an element per call
    ///         would add one per scrolled row for the life of the panel. Make the element once —
    ///         keyed off the row, not the node — and update it here.
    ///     </para>
    /// </remarks>
    public event Action<TreeRow, TreeNode>? RowBound;

    /// <summary>Raised after a drag has moved a node.</summary>
    public event Action<TreeView, TreeNode>? Moved;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        rowHeightId = Document.PropertyId("--row-height");
        indentId = Document.PropertyId("--indent");
        guideColorId = Document.PropertyId("--tree-guide-color");

        Panel = Part<VirtualizingPanel>();
        Panel.CreateRow = owner => {
            var row = owner.Scroller.Content.Add<TreeRow>();

            row.Owner = this;
            rows.Add(row);

            return row;
        };

        Panel.BindRow = (row, index) => Bind((TreeRow) row, visible[index], index);

        DropIndicator = Part("tree-drop-indicator");
        DropIndicator.AddClass("hidden");


        AddHandler<KeyEvent>(static (element, args) => ((TreeView) element).Keyed(args));
        AddHandler<PointerEvent>(static (element, args) => ((TreeView) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((TreeView) element).Tapped(args));
        AddHandler<DragEvent>(static (element, args) => ((TreeView) element).Dragged(args));
    }

    /// <summary>Rebuilds the list of visible nodes and realises the rows for it.</summary>
    /// <remarks>
    ///     The one entry point after anything changes: an expansion, an added node, a resize. It is
    ///     cheap for everything except the flatten, which is O(visible) — the nodes that are showing,
    ///     not the nodes that exist.
    /// </remarks>
    public void Refresh() {
        visible.Clear();
        Flatten(Root);

        // ⚠ Setting the count is the whole of it now. The panel writes the scrollable height,
        // realises against the viewport it actually has, and does so again on `LayoutFinished` — so
        // the `Document.Update()` that used to be here, to turn a just-written height declaration
        // into a measurement before `ScrollView.Refresh` read it, is somebody else's problem and is
        // no longer a layout pass in the middle of a data change.
        Panel.Count = visible.Count;

        // ⚠ **And a realise even when the count did not change**, which a test caught. Adding a child
        // to a collapsed node leaves the number of visible rows exactly as it was while changing what
        // one of them says — and assigning a property its existing value does nothing at all, so the
        // row would keep drawing a leaf that has children.
        Panel.Realise();
    }

    /// <summary>Opens or closes a node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="expanded">Which.</param>
    public void Expand(TreeNode node, bool expanded = true) {
        ArgumentNullException.ThrowIfNull(node);

        if (expanded) {
            node.EnsurePopulated();
        }

        node.IsExpanded = expanded;
        Refresh();
    }

    /// <summary>Opens every node between a node and the root, so that it is visible.</summary>
    /// <param name="node">The node.</param>
    public void Reveal(TreeNode node) {
        ArgumentNullException.ThrowIfNull(node);

        for (var walk = node.Parent; walk is not null; walk = walk.Parent) {
            walk.EnsurePopulated();
            walk.IsExpanded = true;
        }

        Refresh();

        var index = visible.IndexOf(node);
        if (index < 0) {
            return;
        }

        // By index rather than by element, because the row may not be realised yet — the whole point
        // of virtualisation is that the thing being scrolled to does not exist until it is nearly on
        // screen.
        Panel.ScrollIntoView(index);
        Panel.Realise();
    }

    /// <summary>Selects a node, or adds it to or removes it from the selection.</summary>
    /// <param name="node">The node.</param>
    /// <param name="modifiers">What was held: Control toggles, Shift extends.</param>
    public void Select(TreeNode? node, ModifierKeys modifiers = ModifierKeys.None) {
        if (node is null) {
            selection.Clear();
            Restate();

            return;
        }

        if (MultiSelect && modifiers.HasFlag(ModifierKeys.Shift) && anchor is not null) {
            var from = visible.IndexOf(anchor);
            var to = visible.IndexOf(node);

            if (from >= 0 && to >= 0) {
                selection.Clear();

                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) {
                    selection.Add(visible[i]);
                }

                Restate();
                return;
            }
        }

        if (MultiSelect && modifiers.HasFlag(ModifierKeys.Control)) {
            if (!selection.Remove(node)) {
                selection.Add(node);
            }

            // ⚠ The anchor follows a Ctrl-click. A Shift-click after one extends from the thing that
            // was just touched, which is what every file manager does and what makes
            // "Ctrl-click here, Shift-click there" select the range between them.
            anchor = node;
            Restate();

            return;
        }

        selection.Clear();
        selection.Add(node);
        anchor = node;

        Restate();
    }

    /// <summary>Puts a text box in a row so its node can be renamed.</summary>
    /// <param name="node">The node.</param>
    /// <remarks>
    ///     ⚠ <b>The row has to be realised.</b> Renaming something scrolled off screen is not a
    ///     gesture anybody makes with a pointer, and it is one that can arrive from code — so this
    ///     reveals the node first, which realises its row, and only then edits it.
    /// </remarks>
    public void BeginRename(TreeNode node) {
        ArgumentNullException.ThrowIfNull(node);

        Reveal(node);

        if (RowOf(node) is not { } row || row.Editor is not null) {
            return;
        }

        var editor = row.Add<TextBox>();
        editor.Value = node.Text;
        editor.AddClass("tree-editor");

        row.Editor = editor;
        row.Label.AddClass("hidden");

        Document.Focus(editor);
        editor.SelectAll();

        editor.Submitted += _ => CommitRename(row, true);

        editor.AddHandler<KeyEvent>(
            (_, args) => {
                if (args is { Action: KeyAction.Pressed, Key: InputKey.Escape }) {
                    CommitRename(row, false);
                    args.Handled = true;
                }
            }
        );

        // ⚠ Losing the focus commits, which is the third way out and the one people use without
        // meaning to. Enter and Escape are deliberate; clicking on something else is how a rename
        // actually ends most of the time, and an editor that threw the typed name away then is one
        // that loses a rename every session. Committing is also the safer of the two: a name typed
        // and abandoned can be undone, and a name typed and discarded cannot be recovered.
        editor.AddHandler<FocusEvent>(
            (_, args) => {
                // The rename may already have ended — `CommitRename` moves the focus itself, which
                // raises this on the way out — and asking twice would run the handler twice.
                if (!args.Gained && row.Editor is not null) {
                    CommitRename(row, true);
                }
            }
        );
    }

    /// <summary>Moves a node next to or into another one.</summary>
    /// <param name="node">What to move.</param>
    /// <param name="target">What to move it relative to.</param>
    /// <param name="position">Where.</param>
    /// <returns>Whether the move was possible.</returns>
    /// <remarks>
    ///     ⚠ <b>A node cannot be dropped inside itself.</b> It is a gesture users make by accident —
    ///     drag a folder, hesitate, release over one of its own children — and the result is a cycle
    ///     the tree cannot represent and the flatten cannot walk out of.
    /// </remarks>
    public bool MoveNode(TreeNode node, TreeNode target, DropPosition position) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(target);

        if (node.Contains(target) || ReferenceEquals(node, target)) {
            return false;
        }

        if (position == DropPosition.Into) {
            target.EnsurePopulated();
            target.Add(node);
            target.IsExpanded = true;
        } else {
            var parent = target.Parent ?? Root;
            var index = parent.IndexOf(target);

            // ⚠ Read *after* the node has been taken out of its old place when the two share a
            // parent, or moving something down by one lands it back where it started: the index of
            // the target shifts when a sibling before it is removed.
            if (ReferenceEquals(node.Parent, parent) && parent.IndexOf(node) < index) {
                index--;
            }

            parent.Add(node, position == DropPosition.Before ? index : index + 1);
        }

        Refresh();
        Moved?.Invoke(this, node);

        return true;
    }

    /// <summary>The node showing at a point.</summary>
    /// <param name="x">Where, in document coordinates.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>The node, or <c>null</c> if the point is not on a row.</returns>
    /// <remarks>
    ///     ⚠ <b>What a context menu needs, and the reason it is public.</b> A secondary click has to
    ///     decide what it is <i>about</i> before anything is shown, and the answer is the row under
    ///     the pointer rather than the selection — which is usually, but not always, the same thing.
    ///     Without this the caller has to walk <see cref="Rows" /> and repeat the hit test, including
    ///     the part about skipping parked rows, which is exactly the sort of duplicated arithmetic
    ///     that stops agreeing.
    /// </remarks>
    public TreeNode? NodeAt(float x, float y) => RowAt(x, y)?.Node;

    /// <summary>The realised row showing a node, if it has one.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The row, or <c>null</c> if it is not on screen.</returns>
    public TreeRow? RowOf(TreeNode node) {
        foreach (var row in rows) {
            // ⚠ Parked rows are skipped, and they were not before: the panel no longer clears a
            // surplus row's node, so one keeps showing whatever it last held. Answering with it
            // would be answering with a row that is not on screen and is not this node's.
            if (!row.HasClass("parked") && ReferenceEquals(row.Node, node)) {
                return row;
            }
        }

        return null;
    }

    void Flatten(TreeNode node) {
        foreach (var child in node.Children) {
            visible.Add(child);

            if (child.IsExpanded) {
                Flatten(child);
            }
        }
    }

    void Bind(TreeRow row, TreeNode node, int index) {
        _ = index;

        row.Node = node;
        row.Label.Text = node.Text;

        row.Indent.SetStyle(
            "width",
            (node.Depth * Indent).ToString("0.##", CultureInfo.InvariantCulture) + "px"
        );

        // ⚠ Null rather than hidden for a leaf. The element stays in the flow and keeps its width, so
        // a row that has no children lines up with one that has — and gaining a child changes the
        // glyph rather than shifting the whole row sideways.
        row.Chevron.Geometry = node.HasChildren
            ? node.IsExpanded ? ControlIcons.ChevronDown : ControlIcons.ChevronRight
            : null;

        row.Glyph.Geometry = node.Icon;

        if (node.HasChildren) {
            row.RemoveClass("leaf");
        } else {
            row.AddClass("leaf");
        }

        if (selection.Contains(node)) {
            row.State |= ElementState.Checked;
        } else {
            row.State &= ~ElementState.Checked;
        }

        RowBound?.Invoke(row, node);
    }

    void Restate() {
        foreach (var row in rows) {
            if (row.Node is { } node && selection.Contains(node)) {
                row.State |= ElementState.Checked;
            } else {
                row.State &= ~ElementState.Checked;
            }
        }

        SelectionChanged?.Invoke(this);
    }

    void CommitRename(TreeRow row, bool commit) {
        if (row.Editor is not { } editor || row.Node is not { } node) {
            return;
        }

        var text = editor.Value ?? string.Empty;

        row.Editor = null;
        row.Label.RemoveClass("hidden");

        editor.Remove();
        Document.Focus(this);

        if (!commit || string.Equals(text, node.Text, StringComparison.Ordinal)) {
            return;
        }

        // ⚠ The model is written before the handler runs, and the handler may write it again. That
        // is deliberate: an application that validates a name — refusing a duplicate, say — puts the
        // old one back, and one that does not gets the obvious behaviour for free.
        node.Text = text;
        Renamed?.Invoke(this, node, text);

        Refresh();
    }

    /// <summary>The row an element is part of, if it is part of one.</summary>
    static TreeRow? RowUnder(UiElement? element) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (walk is TreeRow row) {
                return row;
            }
        }

        return null;
    }

    TreeRow? RowAt(float x, float y) {
        foreach (var row in rows) {
            if (row.Node is null || row.HasClass("parked")) {
                continue;
            }

            var bounds = row.Bounds;

            if (x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height) {
                return row;
            }
        }

        return null;
    }

    void Pointed(PointerEvent args) {
        if (args is not { Action: PointerAction.Pressed, Button: PointerButton.Primary }) {
            return;
        }

        Document.Focus(this);

        if (RowAt(args.X, args.Y) is not { Node: { } node } row) {
            return;
        }

        // The chevron takes the press and the row does not. Clicking a folder's arrow opens it
        // without selecting it, which is what every file manager does.
        if (node.HasChildren && args.X < row.Label.AbsoluteLeft) {
            Expand(node, !node.IsExpanded);
            args.Handled = true;

            return;
        }

        // ⚠ Deferred rather than applied, when the press is inside the selection and nothing is
        // held. That is the case where the user is either about to drag all of it or about to
        // narrow to this one, and the press cannot tell which — only the release can.
        if (MultiSelect && args.Modifiers == ModifierKeys.None && selection.Count > 1 && selection.Contains(node)) {
            pending = node;
            return;
        }

        pending = null;
        Select(node, args.Modifiers);
    }

    void Tapped(TapEvent args) {
        // A tap is a press and a release with no drag between them, which is the answer the press
        // was waiting for: the user meant this row and not the five that were selected.
        if (pending is { } narrowed) {
            pending = null;
            Select(narrowed, ModifierKeys.None);
        }

        if (args.Count == 2 && RowAt(args.X, args.Y) is { Node: { } node }) {
            // ⚠ The run ends with the activation. Expanding a folder moves a different row under a
            // pointer that has not moved, so the next double-click would otherwise be counted as
            // taps three and four and would activate nothing.
            Document.Gestures.EndTapRun();

            if (RenameOnActivate) {
                // ⚠ Selected first, because every consumer of a rename acts on the row that was
                // double-clicked — and a press inside a multi-selection is deferred, so the row may
                // not be the selection yet when the second tap arrives.
                Select(node);
                BeginRename(node);
            } else {
                Activated?.Invoke(this, node);
            }

            args.Handled = true;
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || visible.Count == 0) {
            return;
        }

        var current = selection.Count > 0 ? visible.FindIndex(node => selection.Contains(node)) : -1;
        var node = current >= 0 ? visible[current] : null;

        switch (args.Key) {
            case InputKey.Down:
                Step(current + 1, args.Modifiers);
                break;

            case InputKey.Up:
                Step(current <= 0 ? 0 : current - 1, args.Modifiers);
                break;

            case InputKey.Home:
                Step(0, args.Modifiers);
                break;

            case InputKey.End:
                Step(visible.Count - 1, args.Modifiers);
                break;

            case InputKey.Right when node is not null:
                // Open it, or step into it if it is already open. One key that means "go deeper",
                // which is what a keyboard user expects and what two keys would not give.
                if (node.HasChildren && !node.IsExpanded) {
                    Expand(node);
                } else {
                    Step(current + 1, ModifierKeys.None);
                }

                break;

            case InputKey.Left when node is not null:
                if (node.IsExpanded) {
                    Expand(node, false);
                } else if (node.Parent is { } parent && !ReferenceEquals(parent, Root)) {
                    Select(parent);
                    Reveal(parent);
                }

                break;

            case InputKey.Enter or InputKey.KeypadEnter when node is not null:
                Activated?.Invoke(this, node);
                break;

            case InputKey.F2 when node is not null:
                BeginRename(node);
                break;

            case InputKey.A when args.Modifiers.HasFlag(ModifierKeys.Control) && MultiSelect:
                selection.Clear();

                foreach (var candidate in visible) {
                    selection.Add(candidate);
                }

                Restate();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Step(int index, ModifierKeys modifiers) {
        if (visible.Count == 0) {
            return;
        }

        var node = visible[Math.Clamp(index, 0, visible.Count - 1)];

        Select(node, modifiers);
        Reveal(node);
    }

    /// <summary>Drags a node onto another one, showing where it would land.</summary>
    /// <remarks>
    ///     ⚠ <b>The top and bottom quarters of a row mean "beside", the middle half means "into".</b>
    ///     Without the distinction there is no way to say "make this a sibling rather than a child",
    ///     which is most of what reordering a hierarchy is — and the indicator is what makes the
    ///     three zones visible rather than something the user discovers by getting it wrong.
    /// </remarks>
    void Dragged(DragEvent args) {
        switch (args.Stage) {
            // ⚠ The row is taken from the event's *source* rather than from where the pointer is.
            // A drag does not begin until the pointer has passed the slop threshold, which is
            // several rows away in a tree of twenty-pixel rows — so hit-testing here picks up
            // whatever the pointer has arrived at and drags the wrong node.
            case DragStage.Started when AllowDrag && RowUnder(args.Source) is { Node: { } node }:
                // The drag is the other answer the press was waiting for: the whole selection moves.
                pending = null;
                dragging = node;
                Track(args.X, args.Y);

                break;

            case DragStage.Moved when dragging is not null:
                Track(args.X, args.Y);
                break;

            case DragStage.Completed when dragging is { } node:
                if (dropTarget is { } target) {
                    MoveNode(node, target, dropPosition);
                }

                Cancel();
                break;

            case DragStage.Cancelled:
                Cancel();
                break;

            default:
                break;
        }
    }

    void Track(float x, float y) {
        dropTarget = null;

        if (RowAt(x, y) is not { Node: { } node } row || dragging is not { } source || source.Contains(node)) {
            DropIndicator.AddClass("hidden");
            return;
        }

        var bounds = row.Bounds;
        var fraction = bounds.Height <= 0f ? 0.5f : (y - bounds.Y) / bounds.Height;

        dropPosition = fraction switch {
            < 0.25f => DropPosition.Before,
            > 0.75f => DropPosition.After,
            _ => DropPosition.Into
        };

        dropTarget = node;

        DropIndicator.RemoveClass("hidden");
        DropIndicator.SetStyle("width", bounds.Width.ToString("0.##", CultureInfo.InvariantCulture) + "px");

        var into = dropPosition == DropPosition.Into;

        DropIndicator.SetStyle("height", into ? bounds.Height.ToString("0.##", CultureInfo.InvariantCulture) + "px" : "2px");

        var top = dropPosition switch {
            DropPosition.Before => bounds.Y,
            DropPosition.After => bounds.Y + bounds.Height,
            _ => bounds.Y
        };

        DropIndicator.OffsetX += bounds.X - DropIndicator.AbsoluteLeft;
        DropIndicator.OffsetY += top - DropIndicator.AbsoluteTop;
    }

    void Cancel() {
        dragging = null;
        dropTarget = null;

        DropIndicator.AddClass("hidden");
    }
}
