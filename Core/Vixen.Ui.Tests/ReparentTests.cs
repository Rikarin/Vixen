// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Moving an element, and everything under it, to a different parent.</summary>
public class ReparentTests {
    const float Tolerance = 0.001f;

    static UiDocument Documented(string css = "root { width: 400px; height: 300px; } .box { width: 40px; height: 20px; }") {
        var document = new UiDocument(400f, 300f);
        document.Load(css);

        return document;
    }

    [Fact]
    public void It_moves_the_element_in_all_three_stores() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .left { width: 100px; height: 100px; }
            .right { width: 200px; height: 100px; }
            .box { width: 40px; height: 20px; }
        """);

        var left = document.Root.Add("div", classNames: "left");
        var right = document.Root.Add("div", classNames: "right");
        var box = left.Add("div", classNames: "box");

        document.Update();
        Assert.Equal(0f, box.AbsoluteLeft, Tolerance);

        document.Reparent(box, right);
        document.Update();

        Assert.Same(right, box.Parent);
        Assert.Contains(box, right.Children);
        Assert.DoesNotContain(box, left.Children);

        // The layout followed, which is the store a reparent that only touched the element tree
        // would have left behind.
        Assert.Equal(100f, box.AbsoluteLeft, Tolerance);
    }

    [Fact]
    public void The_cascade_sees_it_under_its_new_parent() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .box { width: 40px; height: 20px; }
            .wide .box { width: 111px; }
        """);

        var plain = document.Root.Add("div");
        var wide = document.Root.Add("div", classNames: "wide");
        var box = plain.Add("div", classNames: "box");

        document.Update();
        Assert.Equal(40f, box.Width, Tolerance);

        document.Reparent(box, wide);
        document.Update();

        // The whole reason the slot is rebuilt rather than moved: a descendant selector is answered
        // by where the slot sits, so a subtree that kept its old slot would keep its old style.
        Assert.Equal(111f, box.Width, Tolerance);
    }

    [Fact]
    public void The_element_keeps_its_identity_its_handlers_and_its_children() {
        using var document = Documented();

        var first = document.Root.Add("div");
        var second = document.Root.Add("div");

        var box = first.Add("div", classNames: "box");
        var inner = box.Add("div", classNames: "box");

        var clicks = 0;
        box.AddHandler<TapEvent>((_, _) => clicks++);

        box.Text = null;
        inner.Text = "kept";

        document.Reparent(box, second);
        document.Update();

        Assert.Same(inner, Assert.Single(box.Children));
        Assert.Same(box, inner.Parent);
        Assert.Equal("kept", inner.Text);

        box.Raise(new TapEvent { Count = 1 });
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void It_carries_the_state_the_classes_and_the_inline_style() {
        using var document = Documented();

        var first = document.Root.Add("div");
        var second = document.Root.Add("div");

        var box = first.Add("div", "chosen", "box");
        box.State = ElementState.Checked;
        box.SetStyle("width", "77px");

        document.Reparent(box, second);
        document.Update();

        Assert.Equal(ElementState.Checked, box.State);
        Assert.True(box.HasClass("box"));
        Assert.Equal(77f, box.Width, Tolerance);
    }

    [Fact]
    public void It_lands_where_it_was_asked_to() {
        using var document = Documented();

        var source = document.Root.Add("div");
        var target = document.Root.Add("div");

        var first = target.Add("div", classNames: "box");
        var second = target.Add("div", classNames: "box");
        var moved = source.Add("div", classNames: "box");

        document.Reparent(moved, target, 1);

        Assert.Equal([first, moved, second], target.Children);
    }

    [Fact]
    public void The_same_parent_is_a_reorder_rather_than_a_rebuild() {
        using var document = Documented();

        var parent = document.Root.Add("div");
        var first = parent.Add("div", classNames: "box");
        var second = parent.Add("div", classNames: "box");

        var before = document.Styles.Tree.Count;

        document.Reparent(second, parent, 0);

        Assert.Equal([second, first], parent.Children);

        // Reordering already has an answer that rebuilds no slots, and falling through to the
        // rebuild would be correct and strictly worse.
        Assert.Equal(before, document.Styles.Tree.Count);
    }

    [Fact]
    public void Moving_an_element_inside_itself_is_refused() {
        using var document = Documented();

        var outer = document.Root.Add("div");
        var inner = outer.Add("div");

        Assert.Throws<InvalidOperationException>(() => document.Reparent(outer, inner));
        Assert.Throws<InvalidOperationException>(() => document.Reparent(document.Root, outer));
    }

    [Fact]
    public void The_slots_it_left_behind_are_reclaimed() {
        using var document = Documented();

        var first = document.Root.Add("div");
        var second = document.Root.Add("div");

        var box = first.Add("div", classNames: "box");
        for (var i = 0; i < 100; i++) {
            box.Add("div", classNames: "box");
        }

        var dead = document.Styles.Tree.DeadCount;

        document.Reparent(box, second);

        // ⚠ One tombstone per element moved — the price of rebuilding the slots rather than moving
        // them, and the reason a docking layout dragged around all afternoon does not grow without
        // bound: this is exactly what the compaction heuristic already exists to notice.
        Assert.Equal(dead + 101, document.Styles.Tree.DeadCount);

        Assert.True(document.CompactStyles());

        document.Update();

        Assert.Same(box, Assert.Single(second.Children));
        Assert.Equal(100, box.Children.Count);
    }
}
