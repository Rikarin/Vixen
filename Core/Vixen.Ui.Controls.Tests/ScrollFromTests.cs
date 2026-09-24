// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>ScrollView.ScrollFrom</c>: a margin beside a view that scrolls it as though it were inside (#1365).</summary>
/// <remarks>
///     ⚠ <b>Real pointer and wheel events through the document</b>, for <c>TouchActionTests</c>'
///     reason — the bubble from the element the finger lands on is the thing under test, and a
///     <c>DragEvent</c> raised on the view directly would pass whether the margin was wired or not.
///     Every refusal is paired with a baseline in which the same gesture does scroll.
/// </remarks>
public class ScrollFromTests {
    const float Step = 20f;
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>A 40-wide margin beside a 100×100 view over a 600×600 body, both inside one row.</summary>
    static (ControlFixture Fixture, ScrollView View, UiElement Margin) Pair(string css = "", bool wired = true) {
        var fixture = new ControlFixture(css: $$"""
            root    { width: 400px; height: 300px; }
            #pair   { flex-direction: row; width: 140px; height: 100px; }
            #margin { width: 40px; height: 100px; flex-shrink: 0; }
            #view   { width: 100px; height: 100px; flex-shrink: 0; }
            #body   { width: 600px; height: 600px; }
            {{css}}
            """);

        var pair = fixture.Document.Create("div", fixture.Document.Root, "pair");
        var margin = fixture.Document.Create("div", pair, "margin");
        var view = fixture.Document.Create<ScrollView>(null, pair, "view");
        fixture.Document.Create("div", view.Content, "body");

        if (wired) {
            view.ScrollFrom(margin);
        }

        fixture.Update();
        fixture.Advance(Frame);

        return (fixture, view, margin);
    }

    static void Swipe(ControlFixture fixture, UiElement from, float dx, float dy) {
        var bounds = from.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(x, y, type: PointerType.Touch);

        for (var step = 1; step <= 3; step++) {
            fixture.MovePointer(x + (dx * step), y + (dy * step), type: PointerType.Touch);
            fixture.Advance(Frame);
        }

        fixture.Release(x + (dx * 3f), y + (dy * 3f), type: PointerType.Touch);
    }

    [Fact]
    public void A_finger_on_the_margin_scrolls_the_view_only_once_it_is_wired() {
        var (unwired, idle, bare) = Pair(wired: false);

        using (unwired) {
            Swipe(unwired, bare, 0f, -Step);
            Assert.Equal(0f, idle.ScrollTop);
        }

        var (fixture, view, margin) = Pair();
        using var _ = fixture;

        Swipe(fixture, margin, 0f, -Step);

        Assert.True(view.ScrollTop > 0f, "a finger on a wired margin left the view where it was");
    }

    [Fact]
    public void A_wheel_over_the_margin_scrolls_the_view() {
        var (fixture, view, margin) = Pair();
        using var _ = fixture;

        fixture.Wheel(margin, 30f);

        Assert.Equal(30f, view.ScrollTop);
    }

    /// <summary>The margin's own chain counts, exactly as an element between a finger and the view does.</summary>
    [Fact]
    public void Touch_action_none_on_the_margin_keeps_the_finger_from_the_view() {
        var (fixture, view, margin) = Pair("#margin { touch-action: none; }");
        using var _ = fixture;

        Swipe(fixture, margin, 0f, -Step);

        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>And so does the view's own declaration, which is the other half of the chain.</summary>
    [Fact]
    public void Touch_action_none_on_the_view_keeps_the_finger_from_it_there_too() {
        var (fixture, view, margin) = Pair("#view { touch-action: none; }");
        using var _ = fixture;

        Swipe(fixture, margin, 0f, -Step);

        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ <b>But not an ancestor of both.</b> A declaration above the view never narrows a
    ///     gesture on its content, because the chain stops at the view; the margin is treated as
    ///     part of the view, so its chain stops at the margin. Asking the document for the chain from
    ///     the margin's finger to the view would not meet the view — it is not an ancestor — and
    ///     would walk to the root, intersecting this <c>pan-x</c> and refusing a vertical scroll the
    ///     content itself would take.
    /// </summary>
    [Fact]
    public void A_declaration_above_both_narrows_the_margin_no_more_than_it_narrows_the_content() {
        var (fixture, view, margin) = Pair("#pair { touch-action: pan-x; }");
        using var _ = fixture;

        Swipe(fixture, view, 0f, -Step);
        var fromContent = view.ScrollTop;

        Assert.True(fromContent > 0f, "the content itself did not scroll, so the margin's answer proves nothing");

        view.StopFling();
        view.ScrollTop = 0f;
        fixture.Advance(Frame);

        Swipe(fixture, margin, 0f, -Step);

        Assert.True(view.ScrollTop > 0f, "a declaration above the view refused the margin's vertical scroll");
    }
}
