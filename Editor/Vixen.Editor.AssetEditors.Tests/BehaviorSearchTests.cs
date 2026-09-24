// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Input;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The behaviour tree's search-to-create popup, reached the way a person reaches it.</summary>
/// <remarks>
///     ⚠ <b>Every test here opens it through a gesture and never through <c>OpenSearch</c>.</b> The
///     popup was built, styled and kept for months while nothing in the editor called the one method
///     that shows it (#1370); a test that called that method would have been green the whole time.
/// </remarks>
public class BehaviorSearchTests {
    [Fact]
    public void Space_over_the_tree_opens_it_at_the_pointer_and_Enter_adds_the_best_match_under_the_selection() {
        using var harness = new ViewHarness();
        var (view, document) = Open(harness);
        var root = document.Model.Content.Root!;
        var before = root.Children.Count;

        Assert.False(view.Search.IsOpen);

        var (x, y) = Over(harness, view);

        harness.Ui.PressKey(InputKey.Space);
        harness.Ui.Frame();

        Assert.True(view.Search.IsOpen, "Space over the tree did not open the search.");

        // A composite's child row takes a composite or a task, and nothing that attaches.
        Assert.Equal([BehaviorSlot.Composite, BehaviorSlot.Task], view.Search.Slots);
        Assert.Contains(view.Search.Matches, type => type.Type == "Sequence");
        Assert.Contains(view.Search.Matches, type => type.Type == "Wait");
        Assert.All(view.Search.Matches, type => Assert.True(type.Slot is BehaviorSlot.Composite or BehaviorSlot.Task));

        // At the pointer, in document space, and holding the focus so the next letter lands in it.
        Assert.Equal(x, view.Search.AbsoluteLeft, 0.5f);
        Assert.Equal(y, view.Search.AbsoluteTop, 0.5f);
        Assert.Same(view.Search.Query, harness.Ui.Document.Focused);

        harness.Ui.TypeText("Sequence");
        Assert.Equal("Sequence", view.Search.Matches[0].Type);

        harness.Ui.PressKey(InputKey.Enter);
        harness.Ui.Frame();

        Assert.False(view.Search.IsOpen);
        Assert.Equal(before + 1, root.Children.Count);
        Assert.Equal("Sequence", root.Children[^1].Type);
    }

    [Fact]
    public void A_node_asked_for_on_a_leaf_goes_beside_it_rather_than_under_it() {
        using var harness = new ViewHarness();
        var (view, document) = Open(harness);
        var root = document.Model.Content.Root!;
        var leaf = root.Children[0];

        view.Select(leaf);
        Over(harness, view);

        harness.Ui.PressKey(InputKey.Space);
        harness.Ui.TypeText("Selector");
        harness.Ui.PressKey(InputKey.Enter);

        Assert.Empty(leaf.Children);
        Assert.Equal(2, root.Children.Count);
        Assert.Same(leaf, root.Children[0]);
        Assert.Equal("Selector", root.Children[1].Type);
    }

    [Fact]
    public void The_attachment_buttons_open_it_for_their_own_slot() {
        using var harness = new ViewHarness();
        var (view, document) = Open(harness);
        var root = document.Model.Content.Root!;

        Click(harness, view.AddDecorator);

        Assert.True(view.Search.IsOpen, "Add decorator did not open the search.");
        Assert.Equal([BehaviorSlot.Decorator], view.Search.Slots);
        Assert.NotEmpty(view.Search.Matches);
        Assert.All(view.Search.Matches, type => Assert.Equal(BehaviorSlot.Decorator, type.Slot));

        var first = view.Search.Matches[0];

        Click(harness, view.Search.Results.Children[0]);

        Assert.False(view.Search.IsOpen);
        Assert.Equal(first.Type, Assert.Single(root.Decorators).Type);

        Click(harness, view.AddService);

        Assert.True(view.Search.IsOpen, "Add service did not open the search on a composite.");
        Assert.All(view.Search.Matches, type => Assert.Equal(BehaviorSlot.Service, type.Slot));

        harness.Ui.PressKey(InputKey.Escape);
        Assert.False(view.Search.IsOpen);

        // A service attaches to a composite, so on a leaf the button has nothing to do.
        view.Select(root.Children[0]);
        harness.Ui.Frame();

        Assert.True(view.AddService.Disabled);
        Assert.False(view.AddDecorator.Disabled);
    }

    [Fact]
    public void The_toolbar_button_opens_it_for_a_node() {
        using var harness = new ViewHarness();
        var (view, _) = Open(harness);

        Click(harness, view.AddNode);

        Assert.True(view.Search.IsOpen, "Add node did not open the search.");
        Assert.Equal([BehaviorSlot.Composite, BehaviorSlot.Task], view.Search.Slots);
    }

    /// <summary>
    ///     ⚠ Fourteen composites and tasks in a popup capped at 320 px: the list has to scroll to its
    ///     last row, and the field must not pay for the rows with its own height.
    /// </summary>
    [Fact]
    public void Every_offered_row_can_be_scrolled_to_and_the_field_keeps_its_height() {
        using var harness = new ViewHarness();
        var (view, _) = Open(harness);

        Over(harness, view, nearBottom: true);
        harness.Ui.PressKey(InputKey.Space);
        harness.Ui.Frame();

        var search = view.Search;
        var crowded = search.Query.Height;

        Assert.Equal(14, search.Results.Children.Count);

        // Opened near the bottom of the window, and still all on screen.
        Assert.True(
            search.AbsoluteTop + search.Height <= harness.Ui.Document.Viewport.ViewportHeight + 0.5f,
            $"the popup ends at {search.AbsoluteTop + search.Height:0}, below the {harness.Ui.Document.Viewport.ViewportHeight:0} px window."
        );

        search.List.ScrollTo(search.List.MaximumTop, 0f);
        harness.Ui.Frame();

        var last = search.Results.Children[^1];

        // Inside the list's port and inside the popup's own box, which clips too: a list that grew
        // to its rows instead of scrolling would hold the last one inside itself and below the popup.
        var floor = MathF.Min(search.List.AbsoluteTop + search.List.Height, search.AbsoluteTop + search.Height);

        Assert.True(
            last.AbsoluteTop >= search.List.AbsoluteTop - 0.5f && last.AbsoluteTop + last.Height <= floor + 0.5f,
            $"at the end of the list's scroll the last row ({last.AbsoluteTop:0}–{last.AbsoluteTop + last.Height:0}) is not inside "
            + $"{search.List.AbsoluteTop:0}–{floor:0}, so no pointer can reach it."
        );

        harness.Ui.TypeText("sel");
        harness.Ui.Frame();

        Assert.Equal(2, search.Results.Children.Count);
        Assert.Equal(search.Query.Height, crowded, 0.5f);
    }

    /// <summary>
    ///     ⚠ A type still offered after a keystroke keeps its row, even when the ranking moves it —
    ///     the markup's keyed <c>@for</c>, where the hand-written popup removed every row and built
    ///     them all again on each letter (#89).
    /// </summary>
    [Fact]
    public void A_row_still_offered_after_a_keystroke_is_the_same_element_where_it_moved_to() {
        using var harness = new ViewHarness();
        var (view, _) = Open(harness);

        Over(harness, view);
        harness.Ui.PressKey(InputKey.Space);
        harness.Ui.Frame();

        var search = view.Search;
        var before = Row(search, "Random selector");

        Assert.Same(before, search.Results.Children[3]);

        harness.Ui.TypeText("sel");
        harness.Ui.Frame();

        // Selector on the prefix, then Random selector on the substring: the row has moved up two.
        Assert.Equal(["Selector", "Random selector"], search.Matches.Select(type => type.Label));
        Assert.Same(before, search.Results.Children[1]);
        Assert.Same(before, Row(search, "Random selector"));
        Assert.False(before.IsRemoved);
    }

    static UiElement Row(BehaviorSearchPopup search, string label) =>
        Assert.Single(search.Results.Children, row => row.Children.Count > 0 && row.Children[0].Text == label);

    [Fact]
    public void A_press_outside_closes_it_and_the_popup_goes_with_the_view() {
        using var harness = new ViewHarness();
        var (view, _) = Open(harness);

        Over(harness, view);
        harness.Ui.PressKey(InputKey.Space);
        Assert.True(view.Search.IsOpen);

        Click(harness, view.Diagnostics);
        Assert.False(view.Search.IsOpen);

        // ⚠ A root child, so removing the view does not take it along by itself.
        var popup = view.Search;

        view.Remove();

        Assert.True(popup.IsRemoved);
    }

    static (BehaviorTreeView View, BehaviorTreeDocument Document) Open(ViewHarness harness) {
        var path = harness.Project.Write("Assets/Guard.vxbt", string.Empty);
        var document = new BehaviorTreeDocument(harness.Project.Project, AssetId.Empty, path);
        var view = harness.Ui.Document.Root.Add<BehaviorTreeView>();

        view.SetStyle("height", "760px");
        view.Show(document);
        harness.Ui.Frame();

        return (view, document);
    }

    /// <summary>Puts the focus in the canvas and the pointer over an empty part of it.</summary>
    /// <remarks>
    ///     The focus is placed rather than clicked for: a press on empty canvas clears the selection,
    ///     and what these tests are about is what happens to the selection after.
    /// </remarks>
    static (float X, float Y) Over(ViewHarness harness, BehaviorTreeView view, bool nearBottom = false) {
        var x = view.Canvas.AbsoluteLeft + 40f;
        var y = nearBottom ? view.Canvas.AbsoluteTop + view.Canvas.Height - 60f : view.Canvas.AbsoluteTop + 120f;

        harness.Ui.Document.Focus(view.Canvas);
        harness.Ui.MovePointer(x, y);

        return (x, y);
    }

    static void Click(ViewHarness harness, UiElement element) {
        var x = element.AbsoluteLeft + (element.Width / 2f);
        var y = element.AbsoluteTop + (element.Height / 2f);

        harness.Ui.MovePointer(x, y);
        harness.Ui.PressPointer();
        harness.Ui.ReleasePointer();
        harness.Ui.Frame();
    }
}
