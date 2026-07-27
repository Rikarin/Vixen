// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Taking an element out of all three stores at once.</summary>
public class RemovalTests {
    const float Tolerance = 0.001f;

    static UiDocument Rows(int count = 3) {
        var document = new UiDocument(100f, 100f);

        // Stacked, because CSS's initial `flex-direction` is `row` and a list of rows laid out
        // across the screen would make every assertion about a top edge read zero.
        document.Load("""
            root { width: 100px; height: 100px; flex-direction: column; }
            row { width: 100px; height: 20px; }
        """);

        for (var i = 0; i < count; i++) {
            document.Root.Add("row");
        }

        document.Update();
        return document;
    }

    [Fact]
    public void A_removed_element_stops_being_laid_out() {
        using var document = Rows();

        var second = document.Root.Children[1];
        var third = document.Root.Children[2];

        Assert.Equal(20f, second.Top, Tolerance);
        Assert.Equal(40f, third.Top, Tolerance);

        second.Remove();
        document.Update();

        // The row below moves up, which is the whole point and the thing an append-only tree could
        // not do at all.
        Assert.Equal(2, document.Root.Children.Count);
        Assert.Equal(20f, third.Top, Tolerance);
    }

    [Fact]
    public void A_removed_element_stops_being_drawn() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; flex-direction: column; }
            row { width: 100px; height: 20px; background-color: #ffffff; }
        """);

        var first = document.Root.Add("row");
        document.Root.Add("row");

        document.Update();
        document.Draw();
        Assert.Equal(2, document.Drawing.Commands.Count);

        first.Remove();
        document.Update();

        Assert.True(document.Draw());
        Assert.Single(document.Drawing.Commands);
    }

    [Fact]
    public void The_subtree_goes_with_it() {
        using var document = Rows(count: 0);

        var panel = document.Root.Add("row");
        var child = panel.Add("row");
        var grandchild = child.Add("row");

        document.Update();

        var live = document.Styles.Tree.LiveCount;
        panel.Remove();

        // Not orphaned and not reparented — gone. A child left in the store would keep matching
        // selectors and keep being resolved every pass, for a tree it is no longer part of.
        Assert.True(panel.IsRemoved);
        Assert.True(child.IsRemoved);
        Assert.True(grandchild.IsRemoved);
        Assert.Empty(document.Root.Children);

        // ⚠ Asserted against the style tree as well, because the two are separate walks and the
        // element flags above are set by the document rather than by the store. Without this, a
        // store that killed only the element it was handed still passed: the descendants would be
        // unreachable from any live parent and cascaded every frame regardless.
        Assert.Equal(live - 3, document.Styles.Tree.LiveCount);
    }

    [Fact]
    public void Using_a_removed_element_says_so_rather_than_answering() {
        using var document = Rows();

        var row = document.Root.Children[0];
        row.Remove();

        // ⚠ Throwing is the kind option. The node ids it still holds address slots the layout tree
        // has already handed to somebody else, so answering would mean reading another element's
        // width and restyling a stranger — a wrong answer rather than an absent one.
        var thrown = Assert.Throws<InvalidOperationException>(() => row.AddClass("selected"));
        Assert.Contains("removed", thrown.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => _ = row.Width);
    }

    [Fact]
    public void Removing_the_same_element_twice_says_so() {
        using var document = Rows();

        var row = document.Root.Children[0];
        row.Remove();

        Assert.Throws<InvalidOperationException>(row.Remove);
    }

    [Fact]
    public void The_root_cannot_be_removed() {
        using var document = Rows();

        // A document is its tree. The alternative to refusing is a null check in every pass, paid
        // forever for a case nobody wants.
        Assert.Throws<InvalidOperationException>(document.Root.Remove);
    }

    [Fact]
    public void The_siblings_after_it_learn_their_new_positions() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; flex-direction: column; }
            row { width: 100px; height: 20px; }
            row:first-child { height: 50px; }
        """);

        var first = document.Root.Add("row");
        var second = document.Root.Add("row");

        document.Update();
        Assert.Equal(50f, first.Height, Tolerance);
        Assert.Equal(20f, second.Height, Tolerance);

        first.Remove();
        document.Update();

        // ⚠ `IndexInParent` is what the structural selectors read, so it has to come down with the
        // removal. Left stale, the second row still believes it is the second — striped rows that
        // stripe wrongly, and a `:first-child` rule that lands on nothing.
        Assert.Equal(50f, second.Height, Tolerance);
    }

    [Fact]
    public void Removing_the_focused_element_clears_the_focus() {
        using var document = Rows(count: 0);

        var panel = document.Root.Add("row");
        var field = panel.Add("row");
        field.Focusable = true;

        document.Update();
        document.Focus(field);

        // Removed by an *ancestor*, which is the case a reference comparison against the removed
        // element alone would miss — and the one that happens, because a dialog closing takes the
        // field inside it with it.
        panel.Remove();

        Assert.Null(document.Focused);
    }

    [Fact]
    public void Removing_the_capturing_element_releases_the_pointer() {
        using var document = Rows();

        var row = document.Root.Children[0];
        document.CapturePointer(row);

        row.Remove();

        // Left captured, every pointer event in the application would be routed to a detached object
        // for the rest of the session — and hit testing, which is what would have found something
        // else, is exactly what capture turns off.
        Assert.Null(document.Captured);
    }

    [Fact]
    public void Removing_the_element_a_drag_started_on_ends_the_drag_quietly() {
        using var document = Rows();

        var row = document.Root.Children[0];
        var drags = 0;
        row.AddHandler<DragEvent>((_, _) => drags++);

        document.Dispatch(new PointerEvent {
            Action = PointerAction.Pressed, X = 10f, Y = 10f, Timestamp = TimeSpan.Zero
        });

        document.Dispatch(new PointerEvent {
            Action = PointerAction.Moved, X = 60f, Y = 10f, Timestamp = TimeSpan.FromMilliseconds(20)
        });

        Assert.Equal(1, drags);
        row.Remove();

        // ⚠ Silently rather than as a cancellation: a cancelled drag tells its target to put back
        // what it was carrying, and the target is the thing being deleted. Raising an event on an
        // element mid-removal hands a handler a half-detached tree to react to.
        document.Dispatch(new PointerEvent {
            Action = PointerAction.Moved, X = 70f, Y = 10f, Timestamp = TimeSpan.FromMilliseconds(40)
        });

        Assert.Equal(1, drags);
    }

    [Fact]
    public void A_removed_element_is_not_under_the_pointer() {
        using var document = Rows();

        var row = document.Root.Children[0];
        Assert.Same(row, document.HitTest(10f, 10f));

        row.Remove();
        document.Update();

        Assert.NotSame(row, document.HitTest(10f, 10f));
    }

    [Fact]
    public void A_removed_element_is_not_a_tab_stop() {
        using var document = Rows();

        var first = document.Root.Children[0];
        var second = document.Root.Children[1];

        first.Focusable = true;
        second.Focusable = true;

        first.Remove();

        Assert.Equal([second], UiDocument.TabOrder(document.Root));
    }

    [Fact]
    public void What_is_left_still_cascades_correctly() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; flex-direction: column; font-size: 10px; }
            panel { width: 100px; height: 50px; font-size: 2em; }
            row { width: 4em; height: 10px; }
        """);

        var doomed = document.Root.Add("panel");
        doomed.Add("row");

        var kept = document.Root.Add("panel");
        var row = kept.Add("row");

        document.Update();
        Assert.Equal(80f, row.Width, Tolerance);

        doomed.Remove();
        document.Update();

        // ⚠ The surviving element's slot number did not move, because a removed slot is tombstoned
        // rather than handed back — which is what keeps a parent's index below its children's, and
        // that is what the cascade's ascending walk and the bloom's forward sweep both rest on.
        // Reused, this row would resolve before its panel and inherit a font size from the frame
        // before.
        Assert.Equal(80f, row.Width, Tolerance);
        Assert.Equal(20f, row.FontSize, Tolerance);
    }

    [Fact]
    public void Removal_leaves_a_slot_behind_and_says_so() {
        using var document = Rows();

        var tree = document.Styles.Tree;
        var before = tree.Count;

        document.Root.Children[0].Remove();

        // ⚠ Honest rather than tidy. The slot is tombstoned and never reused, so a document that
        // builds and tears down a list every frame grows without bound — and this is the number that
        // says so. Compaction is what fixes it, because rebuilding the arrays without the dead slots
        // preserves relative order where reuse is exactly what does not.
        Assert.Equal(before, tree.Count);
        Assert.Equal(1, tree.DeadCount);
        Assert.Equal(before - 1, tree.LiveCount);
    }

    [Fact]
    public void A_removed_element_is_not_resolved_again() {
        using var document = Rows(count: 10);

        var applied = document.Update();
        Assert.False(applied);

        for (var i = 9; i >= 5; i--) {
            document.Root.Children[i].Remove();
        }

        document.Update();

        // Five gone, five left, and the pass touched no more than the five that are still there —
        // a tombstone that was still cascaded every frame would be a leak of work as well as memory.
        Assert.Equal(5, document.Styles.Tree.LiveCount - 1);
        Assert.Equal(5, document.Root.Children.Count);
    }

    [Fact]
    public void The_layout_slot_comes_straight_back() {
        using var document = Rows();

        var before = document.Layout.NodeCount;
        document.Root.Children[0].Remove();

        // ⚠ The asymmetry is deliberate and worth knowing: the layout tree reuses its slots and the
        // style tree cannot. The layout algorithm descends from the root, so it never cared what
        // order the slots were in; the cascade walks the array by index and reads each parent's
        // resolved table, so for it the slot number *is* the ordering.
        Assert.Equal(before - 1, document.Layout.NodeCount);

        document.Root.Add("row");
        Assert.Equal(before, document.Layout.NodeCount);
    }

    [Fact]
    public void A_state_change_after_a_removal_still_reaches_the_right_element() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; flex-direction: column; }
            row { width: 100px; height: 20px; }
            row.tall { height: 60px; }
        """);

        var doomed = document.Root.Add("row");
        var kept = document.Root.Add("row");

        document.Update();
        doomed.Remove();

        kept.AddClass("tall");
        document.Update();

        // The incremental restyle path queues by slot index, so this is the one that would go wrong
        // if a tombstone were ever reused underneath it.
        Assert.Equal(60f, kept.Height, Tolerance);
        Assert.Equal(ElementState.None, kept.State);
    }
}
