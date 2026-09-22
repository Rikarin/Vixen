// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>touch-action</c> deciding whether a finger's drag is the scroll view's to take.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every drag here is made of real pointer events</b>, for <c>ScrollMomentumTests</c>'
///         reason: the recogniser's slop, the <c>Started</c> event that carries the slop travel, and
///         the bubble from the touched element up to the view are all in the loop, and a test that
///         raised a <c>DragEvent</c> on the view directly would pass against a reader that never
///         looked at the element the finger actually landed on.
///     </para>
///     <para>
///         ⚠ <b>The baseline is asserted in the same fixture as the refusal.</b> A view that never
///         scrolled under a finger would satisfy every "did not scroll" theory below, so each
///         refusing case is paired with the identical gesture on an undeclared element, which must
///         scroll. That pair is what makes "nothing moved" evidence rather than absence.
///     </para>
/// </remarks>
public class TouchActionTests {
    const float Step = 20f;
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>A view whose content is larger than it both ways, with a target row inside.</summary>
    /// <param name="css">Rules appended after the fixture's own, for the target and its wrapper.</param>
    static (ControlFixture Fixture, ScrollView View, UiElement Target) Scrollable(string css = "", bool dragToScroll = false) {
        var fixture = new ControlFixture(css: $$"""
            root    { width: 400px; height: 300px; }
            #view   { width: 100px; height: 100px; }
            #body   { width: 600px; height: 600px; display: flex; flex-direction: column; }
            #wrap   { width: 600px; height: 600px; }
            #target { width: 600px; height: 600px; }
            {{css}}
            """);

        var view = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        var body = fixture.Document.Create("div", view.Content, "body");
        var wrap = fixture.Document.Create("div", body, "wrap");
        var target = fixture.Document.Create("div", wrap, "target");

        view.DragToScroll = dragToScroll;

        fixture.Update();
        fixture.Advance(Frame);

        return (fixture, view, target);
    }

    /// <summary>Drags from the middle of the view by a per-step offset, one frame per step.</summary>
    /// <remarks>
    ///     The view's middle rather than the target's: the target fills the content, so wherever
    ///     the view is scrolled to, its middle is over the target — and a press placed by the
    ///     target's own box would land outside the clip the moment the content moved.
    /// </remarks>
    static void Swipe(ControlFixture fixture, ScrollView view, float dx, float dy, int steps = 3, PointerType type = PointerType.Touch) {
        var bounds = view.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(x, y, type: type);

        for (var step = 1; step <= steps; step++) {
            fixture.MovePointer(x + (dx * step), y + (dy * step), type: type);
            fixture.Advance(Frame);
        }

        fixture.Release(x + (dx * steps), y + (dy * steps), type: type);
    }

    [Fact]
    public void A_finger_on_an_undeclared_element_scrolls_the_view() {
        var (fixture, view, target) = Scrollable();
        using var _ = fixture;

        Swipe(fixture, view, 0f, -Step);

        Assert.True(view.ScrollTop > 0f);
    }

    /// <summary>
    ///     ⚠ The case the property is written for: a control inside a list that wants the finger
    ///     for itself. Until the reader existed, <c>ScrollView</c> took every touch drag whatever
    ///     the element under it said.
    /// </summary>
    [Fact]
    public void Touch_action_none_on_the_touched_element_keeps_the_finger_from_the_view() {
        var (fixture, view, target) = Scrollable("#target { touch-action: none; }");
        using var _ = fixture;

        Swipe(fixture, view, 0f, -Step);

        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ Not inherited, and yet an ancestor's declaration reaches the finger — because the user
    ///     agent intersects every element between the touched one and the scroller, per Pointer
    ///     Events § 6. A reader that looked only at the touched element would let a
    ///     <c>touch-action: none</c> panel scroll from any of its children.
    /// </summary>
    [Fact]
    public void Touch_action_on_an_element_between_the_finger_and_the_view_counts() {
        var (fixture, view, target) = Scrollable("#wrap { touch-action: none; }");
        using var _ = fixture;

        Swipe(fixture, view, 0f, -Step);

        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ Both ends of the chain, including the view itself: a scroll view that declines its own
    ///     default has a chain of one element when the finger lands on its own padding.
    /// </summary>
    [Fact]
    public void Touch_action_none_on_the_view_itself_counts() {
        var (fixture, view, target) = Scrollable("#view { touch-action: none; }");
        using var _ = fixture;

        Swipe(fixture, view, 0f, -Step);

        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ Declined is NOT handled. The drag goes on bubbling past the view to whatever wants it,
    ///     which is how a slider under a <c>none</c> element could take the gesture from an ancestor
    ///     and how an outer view walks the same chain and finds the same answer.
    /// </summary>
    [Fact]
    public void A_declined_drag_is_left_unhandled_for_whatever_is_above_the_view() {
        var (fixture, view, target) = Scrollable("#target { touch-action: none; }");
        using var _ = fixture;

        var reachedRoot = 0;
        var reachedRootUndeclared = 0;

        fixture.Document.Root.AddHandler<DragEvent>((_, args) => {
            if (args.Stage == DragStage.Started) {
                reachedRoot++;
            }
        });

        Swipe(fixture, view, 0f, -Step);

        Assert.Equal(1, reachedRoot);
        Assert.Equal(0f, view.ScrollTop);

        // And the same gesture on an undeclared sibling is consumed by the view before the root.
        var (other, otherView, otherTarget) = Scrollable();
        using var __ = other;

        other.Document.Root.AddHandler<DragEvent>((_, args) => {
            if (args.Stage == DragStage.Started) {
                reachedRootUndeclared++;
            }
        });

        Swipe(other, otherView, 0f, -Step);

        Assert.Equal(0, reachedRootUndeclared);
        Assert.True(otherView.ScrollTop > 0f);
    }

    /// <summary>
    ///     ⚠ <c>touch-action</c> governs touch and nothing else. A mouse drag on a view that asked
    ///     for <see cref="ScrollView.DragToScroll" /> scrolls through a <c>none</c> element, because
    ///     no browser lets the property stop a mouse and no author expects it to.
    /// </summary>
    [Fact]
    public void A_mouse_drag_is_not_governed_by_touch_action() {
        var (fixture, view, target) = Scrollable("#target { touch-action: none; }", dragToScroll: true);
        using var _ = fixture;

        Swipe(fixture, view, 0f, -Step, type: PointerType.Mouse);

        Assert.True(view.ScrollTop > 0f);
    }

    /// <summary>A pen is a finger for this purpose, as it is for <see cref="ScrollView.DragToScroll" />.</summary>
    [Fact]
    public void A_pen_is_governed_like_a_finger() {
        var (fixture, view, target) = Scrollable("#target { touch-action: none; }");
        using var _ = fixture;

        Swipe(fixture, view, 0f, -Step, type: PointerType.Pen);

        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ An axis withheld is dropped, not the gesture: under <c>pan-y</c> a diagonal swipe
    ///     scrolls straight down and the horizontal component goes nowhere, which is what a browser
    ///     does with it. Under <c>pan-x</c> the mirror.
    /// </summary>
    [Theory]
    [InlineData("pan-y", -0.5f, -1f, true, false)]
    [InlineData("pan-x", -1f, -0.5f, false, true)]
    [InlineData("pan-x pan-y", -0.5f, -1f, true, true)]
    [InlineData("manipulation", -0.5f, -1f, true, true)]
    [InlineData("auto", -1f, -0.5f, true, true)]
    [InlineData("pan-x", -0.5f, -1f, false, false)]
    [InlineData("pan-y", -1f, -0.5f, false, false)]
    public void An_axis_keyword_opens_only_its_own_axis(string value, float dx, float dy, bool vertical, bool horizontal) {
        var (fixture, view, target) = Scrollable($"#target {{ touch-action: {value}; }}");
        using var _ = fixture;

        // Diagonal, dominant on one axis: the keyword for that axis admits the gesture and the
        // other half either rides along or goes nowhere; the keyword for the other axis declines
        // the gesture whole, which is the last two rows.
        Swipe(fixture, view, dx * Step, dy * Step);

        Assert.Equal(vertical, view.ScrollTop > 0f);
        Assert.Equal(horizontal, view.ScrollLeft > 0f);
    }

    /// <summary>
    ///     ⚠ A gesture whose dominant axis the keyword withholds is not this view's at all — it is
    ///     declined whole, rather than started and then moved by nothing.
    /// </summary>
    [Fact]
    public void A_vertical_swipe_under_pan_x_is_declined_whole() {
        var (fixture, view, target) = Scrollable("#target { touch-action: pan-x; }");
        using var _ = fixture;

        var reachedRoot = 0;

        fixture.Document.Root.AddHandler<DragEvent>((_, args) => {
            if (args.Stage == DragStage.Started) {
                reachedRoot++;
            }
        });

        Swipe(fixture, view, 0f, -Step);

        Assert.Equal(1, reachedRoot);
        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ The directional keywords are a test on how the gesture BEGINS, and the ledger had them
    ///     down as needing a boundary check nothing computes. A finger travelling up scrolls the
    ///     content <i>down</i> — <c>ScrollTop</c> increases — so <c>pan-down</c> admits it and
    ///     <c>pan-up</c> does not. The mirror pair on the other axis.
    /// </summary>
    [Theory]
    [InlineData("pan-down", 0f, -1f, true)]
    [InlineData("pan-up", 0f, -1f, false)]
    [InlineData("pan-up", 0f, 1f, true)]
    [InlineData("pan-right", -1f, 0f, true)]
    [InlineData("pan-left", -1f, 0f, false)]
    [InlineData("pan-left", 1f, 0f, true)]
    public void A_directional_keyword_admits_only_a_gesture_that_begins_that_way(string value, float dx, float dy, bool admitted) {
        var (fixture, view, target) = Scrollable($"#target {{ touch-action: {value}; }}");
        using var _ = fixture;

        // Start away from both edges so a gesture in either direction has somewhere to go.
        view.ScrollTop = 200f;
        view.ScrollLeft = 200f;
        fixture.Update();

        Swipe(fixture, view, dx * Step, dy * Step);

        var moved = view.ScrollTop != 200f || view.ScrollLeft != 200f;

        Assert.Equal(admitted, moved);
    }

    /// <summary>
    ///     ⚠ Once admitted, the whole axis is open: a <c>pan-down</c> gesture that reverses halfway
    ///     keeps scrolling. The keyword is not a one-way valve, and a view that treated it as one
    ///     would freeze the content the moment a finger wobbled back.
    /// </summary>
    [Fact]
    public void An_admitted_gesture_may_reverse() {
        var (fixture, view, target) = Scrollable("#target { touch-action: pan-down; }");
        using var _ = fixture;

        var bounds = view.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        Assert.Same(target, fixture.Document.HitTest(x, y));

        fixture.Press(x, y, type: PointerType.Touch);
        fixture.MovePointer(x, y - Step, type: PointerType.Touch);
        fixture.Advance(Frame);
        fixture.MovePointer(x, y - (Step * 2), type: PointerType.Touch);
        fixture.Advance(Frame);

        var down = view.ScrollTop;
        Assert.True(down > 0f);

        // And back up past where it began: the content follows.
        fixture.MovePointer(x, y - Step, type: PointerType.Touch);
        fixture.Advance(Frame);

        Assert.True(view.ScrollTop < down);

        fixture.Release(x, y - Step, type: PointerType.Touch);
    }

    /// <summary>The intersection, on the document, without a view in the way.</summary>
    [Fact]
    public void The_chain_is_intersected_from_the_target_to_the_ancestor_inclusive() {
        var (fixture, view, target) = Scrollable("#wrap { touch-action: pan-y; } #target { touch-action: pan-x pan-y; } #view { touch-action: pan-x pan-down; }");
        using var _ = fixture;

        var document = fixture.Document;

        Assert.Equal(TouchAction.PanX | TouchAction.PanY, document.TouchActionOf(target));
        Assert.Equal(TouchAction.PanDown, document.TouchActionBetween(target, view));
        Assert.Equal(TouchAction.PanY, document.TouchActionBetween(target, target.Parent!));
        Assert.Equal(TouchAction.Auto, document.TouchActionOf(document.Root));
    }

    /// <summary>
    ///     The grammar, including the refusals — a value every browser rejects must not be read as
    ///     a union here, or a page written against this engine breaks in one.
    /// </summary>
    [Theory]
    [InlineData("auto", TouchAction.Auto)]
    [InlineData("none", TouchAction.None)]
    [InlineData("manipulation", TouchAction.Manipulation)]
    [InlineData("pan-x", TouchAction.PanX)]
    [InlineData("pan-y pan-x", TouchAction.PanX | TouchAction.PanY)]
    [InlineData("pan-left pinch-zoom", TouchAction.PanLeft | TouchAction.PinchZoom)]
    [InlineData("PAN-UP", TouchAction.PanUp)]
    [InlineData("pan-x pan-left", TouchAction.Auto)]
    [InlineData("none pan-x", TouchAction.Auto)]
    [InlineData("pinch-zoom pinch-zoom", TouchAction.Auto)]
    [InlineData("scroll", TouchAction.Auto)]
    [InlineData("", TouchAction.Auto)]
    public void The_value_grammar_is_the_specifications(string value, TouchAction expected) {
        using var fixture = new ControlFixture(css: $"#probe {{ touch-action: {value}; }}");
        var probe = fixture.Document.Create("div", fixture.Document.Root, "probe");
        fixture.Update();

        Assert.Equal(expected, fixture.Document.TouchActionOf(probe));
    }

    /// <summary>The admission rule in isolation, so a failure names the case rather than the frame.</summary>
    [Theory]
    [InlineData(TouchAction.None, 0f, -10f, false, TouchAction.None)]
    [InlineData(TouchAction.Auto, 0f, -10f, true, TouchAction.PanX | TouchAction.PanY)]
    [InlineData(TouchAction.PanY, 3f, -10f, true, TouchAction.PanY)]
    [InlineData(TouchAction.PanX, 3f, -10f, false, TouchAction.PanX)]
    [InlineData(TouchAction.PanLeft, 10f, 3f, true, TouchAction.PanX)]
    [InlineData(TouchAction.PanLeft, -10f, 3f, false, TouchAction.PanLeft)]
    [InlineData(TouchAction.PanY | TouchAction.PanRight, -10f, 0f, true, TouchAction.PanX | TouchAction.PanY)]
    [InlineData(TouchAction.PinchZoom, 0f, -10f, false, TouchAction.None)]
    public void Admission_is_a_sign_test_on_the_dominant_axis(TouchAction allowed, float totalX, float totalY, bool admitted, TouchAction axes) {
        Assert.Equal(admitted, ScrollView.Admits(allowed, totalX, totalY, out var actual));
        Assert.Equal(axes, actual);
    }
}
