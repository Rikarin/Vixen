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

    // ── What the theme itself declares ──────────────────────────────────────────────────────

    /// <summary>A view whose entire content is one element, so a swipe in its middle lands on that element.</summary>
    /// <param name="create">Makes the element under the finger; nothing here writes a <c>touch-action</c>.</param>
    /// <remarks>
    ///     ⚠ <b>No <c>touch-action</c> in the fixture's own CSS, deliberately.</b> Every other case
    ///     in this file declares the property in the test and proves the reader; these prove that
    ///     <c>ControlTheme.vcss</c> declares it, so the only sheet that may say it is the theme —
    ///     and the theme arrives through <see cref="ControlFixture" />'s
    ///     <c>ControlTheme.Install</c> exactly as it does in an application.
    /// </remarks>
    static (ControlFixture Fixture, ScrollView View, UiElement Control) Themed(Func<UiDocument, UiElement, UiElement> create) {
        var fixture = new ControlFixture(css: """
            root  { width: 400px; height: 300px; }
            #view { width: 100px; height: 100px; }
            #body { width: 600px; height: 600px; display: flex; flex-direction: column; }
            #knob { position: relative; width: 80px; height: 60px; }
            """);

        var view = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        var body = fixture.Document.Create("div", view.Content, "body");
        var control = create(fixture.Document, body);

        fixture.Update();
        fixture.Advance(Frame);

        return (fixture, view, control);
    }

    /// <summary>Drags from the view's middle and reports how far the offset moved <i>during the drag</i>.</summary>
    /// <returns>The change in the two offsets between the press settling and the last move of the drag.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A difference between two points inside the gesture, and nothing else measures
    ///         what this file is about.</b> A focus arriving anywhere inside a scroll view makes it
    ///         <see cref="ScrollView.ScrollIntoView" /> the focused element, and the controls below
    ///         take the focus at both ends of the drag — a slider on the press, a numeric field on a
    ///         release it decided was a click rather than a scrub. An absolute reading counts that
    ///         reveal as a scroll: the first draft of these tests was red for it on the press, and
    ///         green for it on the release, which put the view back where it started after a drag
    ///         that had moved it 40 pixels.
    ///     </para>
    ///     <para>
    ///         The release is still sent, so every gesture here is a whole one and the recogniser
    ///         is left with nothing in flight — it is only read before rather than after.
    ///     </para>
    /// </remarks>
    static (float Top, float Left) DragAcross(ControlFixture fixture, ScrollView view, float dx, float dy, int steps = 3, PointerType type = PointerType.Touch) {
        var bounds = view.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(x, y, type: type);
        fixture.Advance(Frame);

        var top = view.ScrollTop;
        var left = view.ScrollLeft;

        for (var step = 1; step <= steps; step++) {
            fixture.MovePointer(x + (dx * step), y + (dy * step), type: type);
            fixture.Advance(Frame);
        }

        var moved = (view.ScrollTop - top, view.ScrollLeft - left);

        fixture.Release(x + (dx * steps), y + (dy * steps), type: type);

        return moved;
    }

    /// <summary>The paired baseline: the same gesture in the same shape on a control the theme says nothing about.</summary>
    /// <remarks>
    ///     Without this the two refusals below are satisfied by a fixture whose view never scrolled
    ///     at all — a 600×600 control in a 100×100 viewport that failed to overflow, a swipe that
    ///     missed. It is the fixture remark's rule applied to the theme's own rows.
    /// </remarks>
    [Fact]
    public void A_finger_on_a_control_the_theme_leaves_undeclared_scrolls_the_view() {
        var (fixture, view, control) = Themed(static (document, parent) => document.Create("div", parent, "knob"));
        using var _ = fixture;

        Assert.True(DragAcross(fixture, view, 0f, -Step).Top > 0f);
    }

    /// <summary>
    ///     ⚠ <b>The property's own worked example, and it was never written down.</b> A
    ///     <see cref="Slider" /> captures the pointer on the press and moves its thumb — but a
    ///     capture redirects the raw pointer events only, and the <see cref="DragEvent" /> the
    ///     recogniser reads out of them is raised on the slider and bubbles to the view above it.
    ///     So a finger dragging a slider inside a list moved the thumb <i>and</i> scrolled the list,
    ///     and the one declaration that fixes it — <c>touch-action: none</c> — existed, parsed and
    ///     resolved with nothing in the tree writing it.
    /// </summary>
    [Fact]
    public void A_finger_dragging_a_slider_never_reaches_the_view_around_it() {
        var (fixture, view, control) = Themed(static (document, parent) => document.Create<Slider>(null, parent, "knob"));
        using var _ = fixture;

        Assert.Equal(0f, DragAcross(fixture, view, 0f, -Step).Top);
    }

    /// <summary>The same for a scrollbar, where the double movement is the same gesture counted twice.</summary>
    /// <remarks>
    ///     ⚠ A <see cref="ScrollBar" /> is a <i>child of the view it drives</i>, so a finger on its
    ///     thumb is a chain of two elements ending at the view — the bar moves the offset from the
    ///     drag it handles and the view moves the same offset again from the drag that bubbled.
    /// </remarks>
    [Fact]
    public void A_finger_dragging_a_scrollbar_never_reaches_the_view_around_it() {
        var (fixture, view, control) = Themed(static (document, parent) => document.Create<ScrollBar>(null, parent, "knob"));
        using var _ = fixture;

        Assert.Equal(0f, DragAcross(fixture, view, 0f, -Step).Top);
    }

    /// <summary>
    ///     ⚠ <b><c>pan-y</c> on a numeric input, which is the axis split the keyword exists for.</b>
    ///     <c>NumericInput</c>'s scrub reads <c>args.X</c> and nothing else, so the horizontal half
    ///     of a finger's travel is the field's and the vertical half is still the list's. A blanket
    ///     <c>none</c> here would be the commonest way the property is written wrong: it would make
    ///     a list of numeric fields unscrollable from anywhere a finger naturally lands.
    /// </summary>
    [Theory]
    [InlineData(0f, -1f, true)]
    [InlineData(-1f, 0f, false)]
    public void A_numeric_input_keeps_the_horizontal_finger_and_gives_back_the_vertical(float dx, float dy, bool scrolls) {
        var (fixture, view, control) = Themed(static (document, parent) => document.Create<NumericInput>(null, parent, "knob"));
        using var _ = fixture;

        var (top, left) = DragAcross(fixture, view, dx * Step, dy * Step);

        Assert.True(scrolls == (top != 0f || left != 0f), $"top {top}, left {left}");
    }

    /// <summary>
    ///     ⚠ <b>A refusal is only half the property: the control still has to get the finger.</b> The
    ///     two theories above assert that the <i>view</i> stayed put, which a change that made
    ///     <c>touch-action: none</c> disarm the control's own drag would also satisfy — and the pair
    ///     that closes it is one gesture: the thumb moved <b>and</b> the list did not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Horizontal, because a horizontal slider's value cannot answer a vertical drag.</b>
    ///     The vertical cases above are the right axis for the view's own scroll; the knob only
    ///     reports having been dragged along its own track, so the closed-form version of the pair
    ///     has to run along it. The view overflows both ways, so <c>ScrollLeft</c> is the offset that
    ///     would have moved had the declaration not been read.
    /// </remarks>
    [Fact]
    public void A_slider_that_takes_the_finger_still_moves_its_own_thumb() {
        var (fixture, view, control) = Themed(static (document, parent) => document.Create<Slider>(null, parent, "knob"));
        using var _ = fixture;

        var slider = (Slider)control;
        var bounds = view.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(x, y, type: PointerType.Touch);
        fixture.Advance(Frame);

        var pressed = slider.Value;
        var top = view.ScrollTop;
        var left = view.ScrollLeft;

        for (var step = 1; step <= 3; step++) {
            fixture.MovePointer(x - (Step * step), y, type: PointerType.Touch);
            fixture.Advance(Frame);
        }

        Assert.True(slider.Value < pressed, $"the thumb did not move: {pressed} -> {slider.Value}");
        Assert.Equal(top, view.ScrollTop);
        Assert.Equal(left, view.ScrollLeft);

        fixture.Release(x - (Step * 3), y, type: PointerType.Touch);
    }

    /// <summary>A text field of each tag with enough text that a drag across it would select something.</summary>
    static TextField Field(UiDocument document, UiElement parent, string tag) {
        TextField field = tag == "textarea"
            ? document.Create<TextArea>(null, parent, "knob")
            : document.Create<TextBox>(null, parent, "knob");

        field.Value = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";

        return field;
    }

    /// <summary>
    ///     ⚠ <b>The family the theme could not settle, settled in the control (#1357).</b>
    ///     <see cref="TextField" /> captured a finger for its selection drag, so a finger dragging
    ///     inside a <c>textbox</c> or a <c>textarea</c> in a list moved the caret <i>and</i> scrolled
    ///     the list by the whole travel, exactly as a slider did. No keyword was the answer — a
    ///     blanket <c>none</c> makes a form unscrollable from anywhere a finger lands, and
    ///     <c>pan-y</c> does not help because the drag that scrolls is the vertical one — so the
    ///     field now does what a browser does: a finger's drag is the view's, and begins no selection.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Diagonal, because a vertical drag in a single-line field selects nothing
    ///         anyway.</b> The caret follows the drag's <c>x</c>, so a straight vertical drag lands on
    ///         the index it started from and "selected nothing" would have been true of the defect.
    ///         This one crosses a dozen characters: before the change it selected them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the view is asserted to move in the same gesture</b>, so "selected nothing" is
    ///         not satisfied by a finger that never reached the field or a view that never scrolled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a pen, which is the half nothing else covered.</b> <c>TextField.IsDirect</c>
    ///         names both devices, and a sabotage dropping the pen from it left every test green. A
    ///         pen is the device a texture artist has in hand, so taking drag-selection away from it
    ///         is the part of this rule most likely to be noticed — and it is the view's own rule
    ///         (<c>ScrollView.Dragged</c> drags for a pen), so a pen that also selected would do both
    ///         at once exactly as the finger did. <c>PlatformInput</c> reports no pen yet (see
    ///         <c>pointer-devices.md</c>), which is why only a test can reach this today.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("textbox", PointerType.Touch)]
    [InlineData("textarea", PointerType.Touch)]
    [InlineData("textbox", PointerType.Pen)]
    [InlineData("textarea", PointerType.Pen)]
    public void A_finger_dragging_a_text_field_scrolls_the_view_and_selects_nothing(string tag, PointerType device) {
        var (fixture, view, control) = Themed((document, parent) => Field(document, parent, tag));
        using var _ = fixture;

        var field = (TextField)control;
        var moved = DragAcross(fixture, view, -Step, -Step, type: device);

        Assert.True(moved.Top > 0f, $"a {device} dragging a `{tag}` no longer scrolls the view around it (top {moved.Top})");
        Assert.False(field.HasSelection, $"a {device}'s drag selected `{field.SelectedText}` in a `{tag}`");
    }

    /// <summary>
    ///     The paired half: the same drag with a mouse still selects, so what changed is the device's
    ///     answer and not the field's ability to select.
    /// </summary>
    [Theory]
    [InlineData("textbox")]
    [InlineData("textarea")]
    public void A_mouse_dragging_a_text_field_still_selects(string tag) {
        var (fixture, view, control) = Themed((document, parent) => Field(document, parent, tag));
        using var _ = fixture;

        var field = (TextField)control;
        var (x, y) = (view.Bounds.X + (view.Bounds.Width * 0.5f), view.Bounds.Y + (view.Bounds.Height * 0.5f));

        fixture.Press(x, y, type: PointerType.Mouse);

        for (var step = 1; step <= 3; step++) {
            fixture.MovePointer(x - (Step * step), y, type: PointerType.Mouse);
            fixture.Advance(Frame);
        }

        fixture.Release(x - (Step * 3), y, type: PointerType.Mouse);

        Assert.True(field.HasSelection, $"a mouse drag across a `{tag}` selected nothing");
    }

    /// <summary>A finger's caret arrives on the tap — the release that says the press was not a scroll.</summary>
    [Fact]
    public void A_finger_tap_focuses_the_field_and_puts_the_caret_where_it_landed() {
        var (fixture, view, control) = Themed(static (document, parent) => Field(document, parent, "textbox"));
        using var _ = fixture;

        var field = (TextField)control;
        var (x, y) = (field.Bounds.X + (field.Bounds.Width * 0.5f), field.Bounds.Y + (field.Bounds.Height * 0.5f));

        fixture.Press(x, y, type: PointerType.Touch);
        Assert.False(field.IsFocused, "a finger's press focused the field before it could know the press was not a scroll");

        fixture.Release(x, y, type: PointerType.Touch);

        Assert.True(field.IsFocused);
        Assert.True(field.CaretIndex > 0, $"the caret is at {field.CaretIndex}, not where the tap landed");
        Assert.False(field.HasSelection);
    }

    /// <summary>Selection without a drag: a double tap, and a finger held still, each select the word under it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_finger_selects_a_word_by_double_tap_or_by_holding_still(bool hold) {
        var (fixture, view, control) = Themed(static (document, parent) => Field(document, parent, "textbox"));
        using var _ = fixture;

        var field = (TextField)control;
        var (x, y) = (field.Bounds.X + (field.Bounds.Width * 0.5f), field.Bounds.Y + (field.Bounds.Height * 0.5f));

        if (hold) {
            fixture.Press(x, y, type: PointerType.Touch);
            fixture.Advance(TimeSpan.FromSeconds(2));
            fixture.Release(x, y, type: PointerType.Touch);
        } else {
            fixture.Press(x, y, type: PointerType.Touch);
            fixture.Release(x, y, type: PointerType.Touch);
            fixture.Press(x, y, type: PointerType.Touch);
            fixture.Release(x, y, type: PointerType.Touch);
        }

        Assert.True(field.IsFocused);
        Assert.Matches("^[a-z]+$", field.SelectedText);
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

        // ⚠ A chain of one is the element's own declaration, which is why there is no second
        // method for it: `touch-action` does not inherit, so intersecting a chain that starts and
        // ends at the same element is exactly what the cascade resolved there. `TouchActionOf` was
        // that special case spelt twice and had no caller outside this file.
        Assert.Equal(TouchAction.PanX | TouchAction.PanY, document.TouchActionBetween(target, target));
        Assert.Equal(TouchAction.PanDown, document.TouchActionBetween(target, view));
        Assert.Equal(TouchAction.PanY, document.TouchActionBetween(target, target.Parent!));
        Assert.Equal(TouchAction.Auto, document.TouchActionBetween(document.Root, document.Root));
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

        Assert.Equal(expected, fixture.Document.TouchActionBetween(probe, probe));
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
