// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Hover, press, crossing an edge, and the offset that moves a box after layout.</summary>
public class HoverTests {
    const float Tolerance = 0.001f;

    static UiDocument Documented(string css) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);

        return document;
    }

    static PointerEvent At(float x, float y, PointerAction action = PointerAction.Moved, PointerButton button = PointerButton.None) =>
        new() { X = x, Y = y, Action = action, Button = button };

    [Fact]
    public void Hovering_a_child_hovers_its_ancestors_too() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .outer { width: 200px; height: 200px; }
            .inner { width: 50px; height: 50px; }
        """);

        var outer = document.Root.Add("div", classNames: "outer");
        var inner = outer.Add("div", classNames: "inner");

        document.Update();
        document.Dispatch(At(10f, 10f));

        // What CSS means by `:hover`, and what makes `.card:hover .button` work at all.
        Assert.True((inner.State & ElementState.Hover) != 0);
        Assert.True((outer.State & ElementState.Hover) != 0);
        Assert.True((document.Root.State & ElementState.Hover) != 0);
        Assert.Same(inner, document.Hovered);
    }

    [Fact]
    public void Leaving_clears_it_and_the_common_ancestors_never_lose_it() {
        using var document = Documented("""
            root { width: 400px; height: 300px; flex-direction: row; }
            .box { width: 100px; height: 100px; }
        """);

        var first = document.Root.Add("div", classNames: "box");
        var second = document.Root.Add("div", classNames: "box");

        document.Update();
        document.Dispatch(At(10f, 10f));

        var crossings = 0;
        document.Root.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action is PointerAction.Entered or PointerAction.Exited) {
                    crossings++;
                }
            },
            RoutingStrategy.Direct
        );

        document.Dispatch(At(150f, 10f));

        Assert.Equal(ElementState.None, first.State & ElementState.Hover);
        Assert.True((second.State & ElementState.Hover) != 0);

        // ⚠ The root is in both chains, so it is left alone. Switching `:hover` off and on again on
        // the whole path to the root every time the pointer moves a pixel would restyle it sixty
        // times a second and restart every transition on it.
        Assert.Equal(0, crossings);
    }

    [Fact]
    public void Crossing_an_edge_is_told_to_the_element_that_was_crossed() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .box { width: 100px; height: 100px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        document.Update();

        var actions = new List<PointerAction>();
        box.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action is PointerAction.Entered or PointerAction.Exited) {
                    actions.Add(args.Action);
                }
            },
            RoutingStrategy.Direct
        );

        document.Dispatch(At(10f, 10f));
        document.Dispatch(At(20f, 20f));
        document.Dispatch(At(300f, 200f));

        // Once in and once out. A move within the element is not a crossing.
        Assert.Equal([PointerAction.Entered, PointerAction.Exited], actions);
    }

    [Fact]
    public void A_press_marks_the_chain_active_and_a_release_clears_it() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .box { width: 100px; height: 100px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        document.Update();

        document.Dispatch(At(10f, 10f, PointerAction.Pressed, PointerButton.Primary));
        Assert.True((box.State & ElementState.Active) != 0);

        // ⚠ Moving off it does not release it. That is what makes a press you can back out of by
        // releasing elsewhere look right while you are deciding.
        document.Dispatch(At(300f, 200f));
        Assert.True((box.State & ElementState.Active) != 0);

        document.Dispatch(At(300f, 200f, PointerAction.Released, PointerButton.Primary));
        Assert.Equal(ElementState.None, box.State & ElementState.Active);
    }

    [Fact]
    public void A_capture_does_not_move_the_hover() {
        using var document = Documented("""
            root { width: 400px; height: 300px; flex-direction: row; }
            .box { width: 100px; height: 100px; }
        """);

        var first = document.Root.Add("div", classNames: "box");
        var second = document.Root.Add("div", classNames: "box");

        document.Update();
        document.CapturePointer(first);
        document.Dispatch(At(150f, 10f));

        // Everything else about a captured pointer goes to the capturing element. Hover is the
        // exception, because `:hover` is a statement about where the pointer *is*.
        Assert.Same(second, document.Hovered);
    }

    [Fact]
    public void Removing_a_hovered_element_forgets_it() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .box { width: 100px; height: 100px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        document.Update();
        document.Dispatch(At(10f, 10f));

        box.Remove();
        document.Update();

        // Without this the next move walks a list holding an element whose style slot has been
        // handed to somebody else, and clears `:hover` on a stranger.
        document.Dispatch(At(20f, 20f));

        Assert.Same(document.Root, document.Hovered);
    }

    [Fact]
    public void An_offset_moves_an_element_and_everything_in_it() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .outer { width: 100px; height: 100px; }
            .inner { width: 20px; height: 20px; }
        """);

        var outer = document.Root.Add("div", classNames: "outer");
        var inner = outer.Add("div", classNames: "inner");

        document.Update();
        Assert.Equal(0f, inner.AbsoluteLeft, Tolerance);

        outer.OffsetX = 30f;
        outer.OffsetY = -10f;
        document.Update();

        Assert.Equal(30f, outer.AbsoluteLeft, Tolerance);
        Assert.Equal(-10f, outer.AbsoluteTop, Tolerance);
        Assert.Equal(30f, inner.AbsoluteLeft, Tolerance);

        // It moves the element, not the space it occupies — the same bargain as a CSS transform.
        Assert.Equal(0f, outer.Left, Tolerance);
    }

    [Fact]
    public void A_shifted_element_is_clicked_where_it_is_drawn() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 50px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        box.OffsetX = 100f;

        document.Update();

        Assert.Same(box, document.HitTest(110f, 10f));
        Assert.Same(document.Root, document.HitTest(10f, 10f));
    }

    [Fact]
    public void An_ancestors_state_restyles_its_descendants() {
        using var document = Documented("""
            root { width: 400px; height: 300px; }
            .outer { width: 100px; height: 100px; }
            .inner { width: 20px; height: 20px; }
            .outer:hover .inner { width: 60px; }
        """);

        var outer = document.Root.Add("div", classNames: "outer");
        var inner = outer.Add("div", classNames: "inner");

        document.Update();
        Assert.Equal(20f, inner.Width, Tolerance);

        document.Dispatch(At(10f, 10f));
        document.Update();

        // ⚠ The regression test for a style-sharing cache that outlived its pass. The key describes
        // what an element *is* — including its own state and which parent it hangs off, but nothing
        // about that parent's state — so an entry cached before the hover would be served after it,
        // and every descendant rule in every theme would be one frame permanently behind.
        Assert.Equal(60f, inner.Width, Tolerance);

        document.Dispatch(At(300f, 200f));
        document.Update();

        Assert.Equal(20f, inner.Width, Tolerance);
    }
}
