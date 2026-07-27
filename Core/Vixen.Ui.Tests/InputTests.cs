// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Hit testing and event routing.</summary>
public class InputTests {
    const float Tolerance = 0.001f;

    static UiDocument Laid(string css) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        return document;
    }

    [Fact]
    public void Layout_results_become_document_space_rectangles() {
        using var document = Laid("""
            root { width: 400px; height: 300px; padding-left: 30px; padding-top: 20px; }
            .box { width: 50px; height: 40px; }
        """);

        var outer = document.Root.Add("div", classNames: "box");
        var inner = outer.Add("div", classNames: "box");

        document.Update();

        Assert.Equal(30f, outer.AbsoluteLeft, Tolerance);
        Assert.Equal(20f, outer.AbsoluteTop, Tolerance);

        // The child's own Left is relative to its parent; its absolute position is the sum.
        Assert.Equal(0f, inner.Left, Tolerance);
        Assert.Equal(30f, inner.AbsoluteLeft, Tolerance);
        Assert.Equal(20f, inner.AbsoluteTop, Tolerance);
    }

    [Fact]
    public void A_point_finds_the_deepest_element_under_it() {
        using var document = Laid("""
            root { width: 400px; height: 300px; }
            .outer { width: 200px; height: 200px; }
            .inner { width: 50px; height: 50px; }
        """);

        var outer = document.Root.Add("div", classNames: "outer");
        var inner = outer.Add("div", classNames: "inner");

        document.Update();

        Assert.Same(inner, document.HitTest(10f, 10f));
        Assert.Same(outer, document.HitTest(100f, 100f));
        Assert.Same(document.Root, document.HitTest(300f, 250f));
        Assert.Null(document.HitTest(-1f, 10f));
    }

    [Fact]
    public void The_last_sibling_wins_because_it_is_the_one_on_top() {
        using var document = Laid("""
            root { width: 400px; height: 300px; }
            .layer { position: absolute; left: 0px; top: 0px; width: 100px; height: 100px; }
        """);

        var under = document.Root.Add("div", classNames: "layer");
        var over = document.Root.Add("div", classNames: "layer");

        document.Update();

        // Both cover the point. The later sibling is painted over the earlier one, so it is the one
        // a click lands on — testing in document order would return whatever is underneath.
        Assert.Same(over, document.HitTest(50f, 50f));
        Assert.NotSame(under, document.HitTest(50f, 50f));
    }

    [Fact]
    public void A_child_hanging_outside_its_parent_is_still_clickable() {
        using var document = Laid("""
            root { width: 400px; height: 300px; }
            .small { width: 20px; height: 20px; }
            .escapes { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px; }
        """);

        var parent = document.Root.Add("div", classNames: "small");
        var escapes = parent.Add("div", classNames: "escapes");

        document.Update();

        // ⚠ `overflow: visible` is CSS's default and means exactly this. Skipping a subtree because
        // the point is outside its parent makes every dropdown, tooltip and popover unhittable, and
        // the bug looks like the click landing on whatever is behind them.
        Assert.Same(escapes, document.HitTest(110f, 110f));
    }

    [Fact]
    public void Overflow_hidden_cuts_off_what_hangs_outside_it() {
        using var document = Laid("""
            root { width: 400px; height: 300px; }
            .clip { width: 20px; height: 20px; overflow: hidden; }
            .escapes { position: absolute; left: 100px; top: 100px; width: 40px; height: 40px; }
        """);

        var parent = document.Root.Add("div", classNames: "clip");
        parent.Add("div", classNames: "escapes");

        document.Update();

        // Not drawn there, so not clickable there. The root is what is under the point instead.
        Assert.Same(document.Root, document.HitTest(110f, 110f));
    }

    [Fact]
    public void Pointer_events_none_is_transparent_without_making_its_children_so() {
        using var document = Laid("""
            root { width: 400px; height: 300px; }
            .overlay { position: absolute; left: 0px; top: 0px; width: 200px; height: 200px; pointer-events: none; }
            .button { position: absolute; left: 10px; top: 10px; width: 30px; height: 30px; }
        """);

        var overlay = document.Root.Add("div", classNames: "overlay");
        var button = overlay.Add("div", classNames: "button");

        document.Update();

        // ⚠ The asymmetry is the point: the layer lets clicks through, its own children do not.
        // Treating the subtree as one unit either blocks everything under a full-screen layer or
        // lets clicks through a modal.
        Assert.Same(button, document.HitTest(20f, 20f));
        Assert.Same(document.Root, document.HitTest(100f, 100f));
    }

    [Fact]
    public void An_event_goes_down_then_to_the_target_then_back_out() {
        using var document = Laid("root { width: 400px; height: 300px; } div { width: 100px; height: 100px; }");

        var outer = document.Root.Add("div");
        var inner = outer.Add("div");
        document.Update();

        var order = new List<string>();

        document.Root.AddHandler<PointerEvent>((_, _) => order.Add("root capture"), RoutingStrategy.Capture);
        outer.AddHandler<PointerEvent>((_, _) => order.Add("outer capture"), RoutingStrategy.Capture);
        inner.AddHandler<PointerEvent>((_, _) => order.Add("target"), RoutingStrategy.Direct);
        outer.AddHandler<PointerEvent>((_, _) => order.Add("outer bubble"));
        document.Root.AddHandler<PointerEvent>((_, _) => order.Add("root bubble"));

        inner.Raise(new PointerEvent { X = 1f, Y = 1f, Action = PointerAction.Pressed });

        Assert.Equal(["root capture", "outer capture", "target", "outer bubble", "root bubble"], order);
    }

    [Fact]
    public void Handling_an_event_stops_it_except_for_those_who_asked_for_it_anyway() {
        using var document = Laid("root { width: 400px; height: 300px; } div { width: 100px; height: 100px; }");

        var outer = document.Root.Add("div");
        var inner = outer.Add("div");
        document.Update();

        var reached = new List<string>();

        inner.AddHandler<PointerEvent>((_, args) => {
                reached.Add("inner");
                args.Handled = true;
            }
        );

        outer.AddHandler<PointerEvent>((_, _) => reached.Add("outer"));
        document.Root.AddHandler<PointerEvent>((_, _) => reached.Add("root, listening anyway"), handledEventsToo: true);

        inner.Raise(new PointerEvent());

        Assert.Equal(["inner", "root, listening anyway"], reached);
    }

    [Fact]
    public void A_handler_that_changes_the_tree_does_not_break_the_event_it_is_handling() {
        using var document = Laid("root { width: 400px; height: 300px; } div { width: 100px; height: 100px; }");

        var element = document.Root.Add("div");
        document.Update();

        var count = 0;
        Action<UiElement, PointerEvent> second = (_, _) => count++;

        element.AddHandler<PointerEvent>((_, _) => {
                count++;
                element.RemoveHandler(second);
            }
        );

        element.AddHandler(second);
        element.Raise(new PointerEvent());

        // ⚠ Unsubscribing from inside a handler is the ordinary case — a one-shot listener, a button
        // that detaches on click. A foreach over the handler list would throw part-way through
        // delivering the very event that caused it.
        Assert.Equal(1, count);
    }

    [Fact]
    public void An_event_remembers_where_it_started_and_says_where_it_is() {
        using var document = Laid("root { width: 400px; height: 300px; } div { width: 100px; height: 100px; }");

        var outer = document.Root.Add("div");
        var inner = outer.Add("div");
        document.Update();

        var seen = new List<(UiElement Current, RoutingPhase Phase)>();

        outer.AddHandler<PointerEvent>((_, args) => seen.Add((args.Current!, args.Phase)), RoutingStrategy.Capture);
        outer.AddHandler<PointerEvent>((_, args) => seen.Add((args.Current!, args.Phase)));

        var args = new PointerEvent();
        inner.Raise(args);

        Assert.Same(inner, args.Source);
        Assert.Equal([(outer, RoutingPhase.Capture), (outer, RoutingPhase.Bubble)], seen);
    }

    [Fact]
    public void A_pointer_event_reaches_whatever_is_under_it() {
        using var document = Laid("""
            root { width: 400px; height: 300px; }
            .box { width: 60px; height: 60px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        document.Update();

        var hits = 0;
        box.AddHandler<PointerEvent>((_, _) => hits++);

        Assert.Same(box, document.Dispatch(new PointerEvent { X = 10f, Y = 10f, Action = PointerAction.Pressed }));
        Assert.Equal(1, hits);

        Assert.Same(document.Root, document.Dispatch(new PointerEvent { X = 300f, Y = 200f }));
        Assert.Equal(1, hits);
    }

    [Fact]
    public void A_captured_pointer_keeps_reaching_the_element_that_captured_it() {
        using var document = Laid("""
            root { width: 400px; height: 300px; }
            .box { width: 60px; height: 60px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        document.Update();

        var hits = 0;
        box.AddHandler<PointerEvent>((_, _) => hits++);

        document.CapturePointer(box);

        // ⚠ Well outside the box, and it still arrives. A drag that leaves the scrollbar it started
        // on must keep reaching the scrollbar; hit testing during a drag is the bug capture exists
        // to prevent.
        Assert.Same(box, document.Dispatch(new PointerEvent { X = 390f, Y = 290f, Action = PointerAction.Moved }));
        Assert.Equal(1, hits);

        document.ReleasePointer();
        Assert.Same(document.Root, document.Dispatch(new PointerEvent { X = 390f, Y = 290f }));
        Assert.Equal(1, hits);
    }
}
