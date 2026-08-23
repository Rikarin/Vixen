// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Input;
using Vixen.Ui.Composition;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Virtualisation, lazy children, selection, renaming and drag-reorder.</summary>
public class TreeViewTests {
    static TreeView Populated(AdvancedFixture fixture, int roots = 3, int children = 2) {
        var tree = fixture.Add<TreeView>();

        for (var i = 0; i < roots; i++) {
            var node = tree.Root.Add($"root{i}");

            for (var j = 0; j < children; j++) {
                node.Add($"child{i}-{j}");
            }
        }

        tree.Refresh();
        fixture.Update();

        return tree;
    }

    [Fact]
    public void Only_the_expanded_nodes_are_visible() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture);

        Assert.Equal(3, tree.Visible.Count);

        tree.Expand(tree.Root.Children[0]);

        Assert.Equal(5, tree.Visible.Count);
        Assert.Equal(["root0", "child0-0", "child0-1", "root1", "root2"], tree.Visible.Select(static n => n.Text));

        tree.Expand(tree.Root.Children[0], false);
        Assert.Equal(3, tree.Visible.Count);
    }

    [Fact]
    public void A_huge_tree_realises_only_what_fits() {
        using var fixture = new AdvancedFixture(css: "tree-view { height: 220px; }");

        var tree = fixture.Add<TreeView>();

        for (var i = 0; i < 100_000; i++) {
            tree.Root.Add($"node{i}");
        }

        tree.Refresh();
        fixture.Update();
        tree.Refresh();

        Assert.Equal(100_000, tree.Visible.Count);

        // ⚠ The claim doc 09 makes about virtualisation, asserted rather than described: ten rows of
        // viewport plus the overscan, not a hundred thousand elements.
        Assert.True(tree.Rows.Count < 20, $"realised {tree.Rows.Count} rows");
        Assert.Equal("node0", tree.Rows[0].Node?.Text);
    }

    [Fact]
    public void Scrolling_rebinds_the_rows_rather_than_making_new_ones() {
        using var fixture = new AdvancedFixture(css: "tree-view { height: 220px; }");

        var tree = fixture.Add<TreeView>();

        for (var i = 0; i < 1_000; i++) {
            tree.Root.Add($"node{i}");
        }

        tree.Refresh();
        fixture.Update();
        tree.Refresh();

        var before = tree.Rows.Count;
        var element = tree.Rows[0];

        tree.Scroller.ScrollTop = 2200f;
        fixture.Update();

        Assert.Equal(before, tree.Rows.Count);

        // The same element, showing a different node. That is what makes a scroll cost property
        // writes rather than a tear-down of everything on screen.
        Assert.Same(element, tree.Rows[0]);
        Assert.Equal("node98", tree.Rows[0].Node?.Text);
    }

    [Fact]
    public void Children_are_loaded_the_first_time_a_node_is_opened_and_not_again() {
        using var fixture = new AdvancedFixture();

        var tree = fixture.Add<TreeView>();
        var folder = tree.Root.Add("folder");

        var loads = 0;
        folder.HasChildren = true;
        folder.Populate = node => {
            loads++;
            node.Add("inside");
        };

        tree.Refresh();
        Assert.Empty(folder.Children);

        tree.Expand(folder);
        Assert.Equal(1, loads);
        Assert.Equal("inside", Assert.Single(folder.Children).Text);

        // ⚠ Folding and unfolding must not append the same children again — the classic duplicated
        // tree, and the reason the populate is a callback with a flag rather than an event.
        tree.Expand(folder, false);
        tree.Expand(folder);

        Assert.Equal(1, loads);
        Assert.Single(folder.Children);
    }

    [Fact]
    public void A_click_selects_and_the_row_says_so() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture);

        var row = tree.Rows[1];
        fixture.Click(row);

        Assert.Same(tree.Root.Children[1], Assert.Single(tree.Selection));
        Assert.True((row.State & ElementState.Checked) != 0);
        Assert.Equal(ElementState.None, tree.Rows[0].State & ElementState.Checked);
    }

    [Fact]
    public void Control_click_toggles_and_shift_click_takes_the_range() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 5, children: 0);

        tree.Select(tree.Visible[0]);
        tree.Select(tree.Visible[2], ModifierKeys.Control);

        Assert.Equal(2, tree.Selection.Count);

        tree.Select(tree.Visible[4], ModifierKeys.Shift);

        // ⚠ The anchor followed the Ctrl-click, so the range runs from there — which is what
        // "Ctrl-click here, Shift-click there" means in every file manager.
        Assert.Equal(3, tree.Selection.Count);
        Assert.Contains(tree.Visible[3], tree.Selection);
        Assert.DoesNotContain(tree.Visible[0], tree.Selection);
    }

    [Fact]
    public void A_single_select_tree_ignores_the_modifiers() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 3, children: 0);

        tree.MultiSelect = false;

        tree.Select(tree.Visible[0]);
        tree.Select(tree.Visible[1], ModifierKeys.Control);

        Assert.Same(tree.Visible[1], Assert.Single(tree.Selection));
    }

    [Fact]
    public void The_arrows_walk_the_tree_and_open_it() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture);

        fixture.Document.Focus(tree);

        fixture.Type(InputKey.Down);
        Assert.Same(tree.Root.Children[0], Assert.Single(tree.Selection));

        // Right opens a closed node; Right again steps into it.
        fixture.Type(InputKey.Right);
        Assert.True(tree.Root.Children[0].IsExpanded);

        fixture.Type(InputKey.Right);
        Assert.Same(tree.Root.Children[0].Children[0], Assert.Single(tree.Selection));

        // Left on a leaf goes to its parent; Left on an open node closes it.
        fixture.Type(InputKey.Left);
        Assert.Same(tree.Root.Children[0], Assert.Single(tree.Selection));

        fixture.Type(InputKey.Left);
        Assert.False(tree.Root.Children[0].IsExpanded);
    }

    [Fact]
    public void Home_and_end_reach_the_ends_and_ctrl_a_takes_everything() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 4, children: 0);

        fixture.Document.Focus(tree);

        fixture.Type(InputKey.End);
        Assert.Same(tree.Visible[3], Assert.Single(tree.Selection));

        fixture.Type(InputKey.Home);
        Assert.Same(tree.Visible[0], Assert.Single(tree.Selection));

        fixture.Type(InputKey.A, ModifierKeys.Control);
        Assert.Equal(4, tree.Selection.Count);
    }

    [Fact]
    public void Renaming_puts_a_field_in_the_row_and_commits_on_enter() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 2, children: 0);

        var node = tree.Root.Children[0];
        var renamed = 0;

        tree.Renamed += (_, _, _) => renamed++;
        tree.BeginRename(node);

        var row = tree.RowOf(node)!;
        Assert.NotNull(row.Editor);
        Assert.True(row.Editor!.IsFocused);
        Assert.Equal("root0", row.Editor.SelectedText);

        fixture.TypeText("renamed");
        fixture.Type(InputKey.Enter);

        Assert.Equal("renamed", node.Text);
        Assert.Equal(1, renamed);
        Assert.Null(tree.RowOf(node)?.Editor);
    }

    [Fact]
    public void Escape_abandons_a_rename() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 2, children: 0);

        var node = tree.Root.Children[0];
        tree.BeginRename(node);

        fixture.TypeText("nonsense");
        fixture.Type(InputKey.Escape);

        Assert.Equal("root0", node.Text);
        Assert.Null(tree.RowOf(node)?.Editor);
    }

    [Fact]
    public void F2_starts_a_rename_on_the_selection() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 2, children: 0);

        fixture.Document.Focus(tree);
        tree.Select(tree.Visible[1]);

        fixture.Type(InputKey.F2);

        Assert.NotNull(tree.RowOf(tree.Visible[1])?.Editor);
    }

    [Fact]
    public void A_node_can_be_moved_beside_or_into_another() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 3, children: 0);

        var first = tree.Root.Children[0];
        var last = tree.Root.Children[2];

        Assert.True(tree.MoveNode(first, last, DropPosition.Into));

        Assert.Same(last, first.Parent);
        Assert.True(last.IsExpanded);
        Assert.Equal(2, tree.Root.Children.Count);
    }

    [Fact]
    public void Moving_a_node_down_by_one_lands_it_below_its_neighbour() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 3, children: 0);

        var first = tree.Root.Children[0];
        var second = tree.Root.Children[1];

        tree.MoveNode(first, second, DropPosition.After);

        // ⚠ The index of the target shifts when a sibling before it is taken out, and a move that
        // did not account for it would put the node back where it started.
        Assert.Equal(["root1", "root0", "root2"], tree.Root.Children.Select(static n => n.Text));
    }

    [Fact]
    public void A_node_cannot_be_dropped_inside_itself() {
        using var fixture = new AdvancedFixture();

        var tree = fixture.Add<TreeView>();
        var folder = tree.Root.Add("folder");
        var inside = folder.Add("inside");

        tree.Refresh();

        Assert.False(tree.MoveNode(folder, inside, DropPosition.Into));
        Assert.False(tree.MoveNode(folder, folder, DropPosition.Into));

        Assert.Same(folder, inside.Parent);
    }

    [Fact]
    public void A_point_answers_with_the_node_showing_there() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 3, children: 0);

        var bounds = tree.Rows[1].Bounds;

        Assert.Same(
            tree.Root.Children[1],
            tree.NodeAt(bounds.X + 20f, bounds.Y + (bounds.Height * 0.5f))
        );

        // Below the last row is not a row, which is what lets a context menu tell "on this entity"
        // from "in the empty part of the panel" — two different things for a Create command.
        Assert.Null(tree.NodeAt(bounds.X + 20f, bounds.Y + (bounds.Height * 40f)));
        Assert.Null(tree.NodeAt(bounds.X - 500f, bounds.Y));
    }

    [Fact]
    public void Dragging_a_row_shows_an_indicator_and_moves_the_node() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 3, children: 0);

        var source = tree.Rows[0];
        var target = tree.Rows[2].Bounds;

        fixture.Press(source.Bounds.X + 20f, source.Bounds.Y + 4f);
        fixture.Move(target.X + 20f, target.Y + (target.Height * 0.5f));
        fixture.Move(target.X + 20f, target.Y + (target.Height * 0.5f));

        Assert.False(tree.DropIndicator.HasClass("hidden"));

        fixture.Release(target.X + 20f, target.Y + (target.Height * 0.5f));

        Assert.True(tree.DropIndicator.HasClass("hidden"));
        Assert.Equal(2, tree.Root.Children.Count);
        Assert.Equal("root0", tree.Root.Children[1].Children[0].Text);
    }

    [Fact]
    public void A_double_click_activates_a_node() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 2, children: 0);

        TreeNode? activated = null;
        tree.Activated += (_, node) => activated = node;

        var bounds = tree.Rows[0].Bounds;
        var x = bounds.X + 20f;
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(x, y);
        fixture.Release(x, y);
        fixture.Press(x, y);
        fixture.Release(x, y);

        Assert.Same(tree.Root.Children[0], activated);
    }

    [Fact]
    public void A_leaf_shows_no_chevron() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 2, children: 0);

        Assert.True(tree.Rows[0].HasClass("leaf"));

        tree.Root.Children[0].Add("child");
        tree.Refresh();

        Assert.False(tree.Rows[0].HasClass("leaf"));
    }

    [Fact]
    public void Revealing_a_node_opens_its_ancestors_and_scrolls_to_it() {
        using var fixture = new AdvancedFixture(css: "tree-view { height: 100px; }");

        var tree = fixture.Add<TreeView>();
        var branch = tree.Root.Add("branch");

        for (var i = 0; i < 50; i++) {
            tree.Root.Add($"filler{i}");
        }

        var deep = branch.Add("deep");
        tree.Refresh();
        fixture.Update();

        tree.Reveal(deep);

        Assert.True(branch.IsExpanded);
        Assert.Contains(deep, tree.Visible);

        // It is near the top, so nothing had to scroll — the reveal moves the minimum it can.
        Assert.Equal(0f, tree.Scroller.ScrollTop, 0.001f);

        var last = tree.Visible[^1];
        tree.Reveal(last);

        Assert.True(tree.Scroller.ScrollTop > 0f);
        Assert.NotNull(tree.RowOf(last));
    }

    // ── The selection as a value, which is what markup can name ──────────────

    /// <summary>
    ///     ⚠ <b>Before anything is selected it is an <i>empty</i> array and not a defaulted one</b>,
    ///     which is the whole reason <c>OnCreated</c> publishes. <c>default(ImmutableArray&lt;T&gt;)</c>
    ///     wraps a null and throws on the first <c>foreach</c> — a trap that would only ever fire in
    ///     the panel nobody had clicked in yet.
    /// </summary>
    [Fact]
    public void The_selection_is_an_empty_value_before_anything_is_selected() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture);

        Assert.False(tree.SelectedNodes.IsDefault);
        Assert.Empty(tree.SelectedNodes);
    }

    /// <summary>
    ///     ⚠ <b>What <c>change:SelectedNodes="@(nodes =&gt; …)"</c> emits, run rather than described.</b>
    ///     The two lambdas are the emitter's shape exactly — the reader names the property and types
    ///     the handler — so this fails at compose if the property is not registered, and reports the
    ///     wrong count if <c>Restate</c> does not write it.
    /// </summary>
    [Fact]
    public void A_change_binding_can_name_the_selection() {
        using var fixture = new AdvancedFixture();
        var watcher = BuildContext.Build<SelectionWatcher>(fixture.Document, fixture.Document.Root);
        var tree = watcher.Tree;

        tree.Root.Add("a");
        tree.Root.Add("b");
        tree.Refresh();
        fixture.Update();

        tree.Select(tree.Visible[0]);
        Assert.Same(tree.Visible[0], Assert.Single(Assert.Single(watcher.Seen)));

        // ⚠ And a click on the row that is already the only one selected is not a change, where
        // `SelectionChanged` fires for it. That is the difference worth having: a panel writing an
        // undo entry from this one does not post an entry per click on the same row.
        tree.Select(tree.Visible[0]);
        Assert.Single(watcher.Seen);

        tree.Select(tree.Visible[1]);
        Assert.Equal(2, watcher.Seen.Count);
    }

    /// <summary>
    ///     And <c>Selection</c> itself is still not a property, which is not an oversight. A
    ///     <see cref="HashSet{T}" /> behind a read-only view is the same instance before and after
    ///     every change, so nothing riding <c>PropertyChanged</c> could ever have reported it — the
    ///     control keeps it because its own <c>Contains</c> checks run on it, and publishes a value
    ///     beside it.
    /// </summary>
    [Fact]
    public void The_read_only_view_is_still_not_a_property_and_says_so() {
        using var fixture = new AdvancedFixture();

        var thrown = Assert.Throws<ArgumentException>(
            () => BuildContext.Build<BadWatcher>(fixture.Document, fixture.Document.Root)
        );

        Assert.Contains("'tree-view' has no property called 'Selection'", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other leg: assigning it is how code and a <c>bind:</c> say "these nodes", and it goes
    ///     through the same repaint a click does rather than leaving the rows disagreeing with the
    ///     set.
    /// </summary>
    [Fact]
    public void Assigning_the_selection_selects_and_repaints() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 3, children: 0);

        var raised = 0;
        tree.SelectionChanged += _ => raised++;

        tree.SelectedNodes = [tree.Visible[1], tree.Visible[2]];

        Assert.Equal(2, tree.Selection.Count);
        Assert.Contains(tree.Visible[1], tree.Selection);
        Assert.Contains(tree.Visible[2], tree.Selection);
        Assert.Equal(1, raised);

        Assert.True((tree.Rows[1].State & ElementState.Checked) != 0);
        Assert.Equal(ElementState.None, tree.Rows[0].State & ElementState.Checked);

        // ⚠ The re-entrant write did not double back: `Restate` publishes, sees the set it was just
        // given, and writes nothing — so the property still holds what was assigned.
        Assert.Equal(2, tree.SelectedNodes.Length);

        // A Shift-click after an assignment extends from the last node written, the way it extends
        // from the last node clicked.
        tree.Select(tree.Visible[0], ModifierKeys.Shift);
        Assert.Equal(3, tree.Selection.Count);
    }

    /// <summary>
    ///     ⚠ <b>And a single-select tree cannot be talked into holding two.</b> Every gesture clears
    ///     the selection before writing it, so a control left holding two rows it never agreed to
    ///     would keep them until something else was clicked — the assignment takes the last, which is
    ///     what a click on each in turn would have left.
    /// </summary>
    [Fact]
    public void Assigning_two_nodes_to_a_single_select_tree_keeps_the_last() {
        using var fixture = new AdvancedFixture();
        var tree = Populated(fixture, roots: 3, children: 0);

        tree.MultiSelect = false;
        tree.SelectedNodes = [tree.Visible[0], tree.Visible[1]];

        Assert.Same(tree.Visible[1], Assert.Single(tree.Selection));
        Assert.Same(tree.Visible[1], Assert.Single(tree.SelectedNodes));
    }

    /// <summary>What a <c>&lt;TreeView change:SelectedNodes="@(…)" /&gt;</c> compiles to.</summary>
    sealed class SelectionWatcher : Component {
        public TreeView Tree { get; private set; } = null!;

        public List<ImmutableArray<TreeNode>> Seen { get; } = [];

        protected override void Build(BuildContext ctx) {
            Tree = ctx.Child<TreeView>(null);
            ctx.Changed(Tree, "SelectedNodes", () => Tree.SelectedNodes, nodes => Seen.Add(nodes));
        }
    }

    /// <summary>And what <c>change:Selection</c> compiles to, which is a name nothing registered.</summary>
    sealed class BadWatcher : Component {
        protected override void Build(BuildContext ctx) {
            var tree = ctx.Child<TreeView>(null);
            ctx.Changed(tree, "Selection", () => tree.Selection, static _ => { });
        }
    }
}
