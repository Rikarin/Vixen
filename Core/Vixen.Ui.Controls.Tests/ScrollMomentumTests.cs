// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Dragging the content, and the fling that carries it on afterwards.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The premise the momentum work was actually blocked on was not the curve.</b>
///         <see cref="ScrollView" /> handled no pointer and no drag at all — it scrolled from the
///         wheel, the keyboard and its bars — so there was no gesture with an <i>end</i> for a
///         velocity to be taken from. A deceleration curve is a dozen lines; a finger to attach it to
///         did not exist.
///     </para>
///     <para>
///         ⚠ <b>Every drag here is made of real pointer events</b>, so the gesture recogniser's own
///         eight-pixel slop, its one-way drag latch and its stage sequence are all in the loop. A
///         test that raised a <c>DragEvent</c> directly would pass against a view that no real input
///         can ever reach, which is the state this file is about.
///     </para>
/// </remarks>
public class ScrollMomentumTests {
    const float Step = 20f;
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>A view whose content is ten times as tall as it is.</summary>
    static (ControlFixture Fixture, ScrollView View) Tall(bool dragToScroll = true) {
        var fixture = new ControlFixture(css: """
            root  { width: 400px; height: 300px; }
            #view { width: 100px; height: 60px; }
            #body { width: 100px; height: 600px; }
            """);

        var view = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        fixture.Document.Create("div", view.Content, "body");

        view.DragToScroll = dragToScroll;

        fixture.Update();

        // Primes the view's own clock, so the first measured interval is a frame and not the whole
        // of the document's life.
        fixture.Advance(Frame);

        return (fixture, view);
    }

    /// <summary>Drags upwards from the middle of the view, one frame per step.</summary>
    /// <returns>Where the pointer ended up.</returns>
    static float Flick(ControlFixture fixture, ScrollView view, int steps, PointerType type = PointerType.Unknown) {
        var bounds = view.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Press(x, y, type: type);

        for (var step = 1; step <= steps; step++) {
            fixture.MovePointer(x, y - (Step * step), type: type);
            fixture.Advance(Frame);
        }

        return y - (Step * steps);
    }

    /// <summary>
    ///     ⚠ The offset moves <i>against</i> the finger. A drag moves the content and the offset is
    ///     how far down the content the viewport is, so dragging upwards scrolls down. It is the one
    ///     path in this control where the number the user is moving is not the number being stored.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The move that <i>starts</i> the drag scrolls nothing, and that is deliberate rather
    ///     than an off-by-one.</b> The recogniser calls a press a drag once it has wandered past its
    ///     slop, and honouring that first delta would jump the content by however far the finger got
    ///     before the threshold was crossed — a visible lurch at the beginning of every scroll. A
    ///     drag begins where the finger is when it becomes one. Here the steps are twenty pixels, so
    ///     exactly one of the three is swallowed; on a real device a step is a frame's worth of
    ///     movement and the loss is the slop itself.
    /// </remarks>
    [Fact]
    public void Dragging_the_content_scrolls_the_view_the_other_way() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        Flick(fixture, view, steps: 3);

        Assert.Equal(Step * 2, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ The whole point: the content keeps going after the finger has gone. A view with no fling
    ///     stops dead on release, which is the single most immediate "not a native application" tell
    ///     on a Mac.
    /// </summary>
    [Fact]
    public void Releasing_a_flick_carries_the_content_on() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var y = Flick(fixture, view, steps: 4);
        var released = view.ScrollTop;

        fixture.Release(view.Bounds.X + (view.Bounds.Width * 0.5f), y);

        Assert.True(view.IsFlinging);

        fixture.Advance(Frame);

        Assert.True(view.ScrollTop > released);
    }

    /// <summary>
    ///     ⚠ And it stops. An exponential decay never reaches zero, so without a floor the offset
    ///     would go on changing by a millionth of a pixel every frame for the life of the document —
    ///     and every one of those frames invalidates positions and rebuilds the draw list.
    /// </summary>
    [Fact]
    public void A_fling_comes_to_rest_rather_than_decaying_for_ever() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var y = Flick(fixture, view, steps: 4);
        fixture.Release(view.Bounds.X + (view.Bounds.Width * 0.5f), y);

        Assert.True(view.IsFlinging);

        // A ceiling on frames rather than a budget in seconds: this asserts that the loop terminates,
        // and two seconds of frames is absurdly more than a fling with a third-of-a-second time
        // constant can need.
        for (var frame = 0; frame < 120 && view.IsFlinging; frame++) {
            fixture.Advance(Frame);
        }

        Assert.False(view.IsFlinging);

        var resting = view.ScrollTop;
        fixture.Advance(Frame);

        Assert.Equal(resting, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ A fling stepped in one large frame must land where the same fling stepped in twenty
    ///     does. That is what makes the curve a time constant rather than a per-frame multiplier —
    ///     the commonest way an inertial scroll is written wrong, and one that is invisible on the
    ///     machine it was tuned on.
    /// </summary>
    [Fact]
    public void The_curve_is_the_same_at_any_frame_rate() {
        var slow = Coast(TimeSpan.FromMilliseconds(64), 40);
        var fast = Coast(TimeSpan.FromMilliseconds(8), 320);

        Assert.InRange(fast, slow - 2f, slow + 2f);
        Assert.True(slow > 0f);

        static float Coast(TimeSpan frame, int frames) {
            var (fixture, view) = Tall();
            using var _ = fixture;

            var y = Flick(fixture, view, steps: 4);
            fixture.Release(view.Bounds.X + (view.Bounds.Width * 0.5f), y);

            var released = view.ScrollTop;

            for (var step = 0; step < frames && view.IsFlinging; step++) {
                fixture.Advance(frame);
            }

            return view.ScrollTop - released;
        }
    }

    /// <summary>
    ///     ⚠ <b>Off by default <i>for a device that has not said it is a finger</i></b>, which is
    ///     what the property now means. A mouse drag inside a scroll view is a text selection or a
    ///     marquee on every desktop, so a view that dragged for everybody would take all of those
    ///     away — and <c>PointerType.Unknown</c> is treated as the desktop case rather than guessed
    ///     into the touch one, for the same reason the enum's default is not <c>Mouse</c>.
    /// </summary>
    [Theory]
    [InlineData(PointerType.Unknown)]
    [InlineData(PointerType.Mouse)]
    public void A_view_that_was_not_asked_to_drag_does_not(PointerType type) {
        var (fixture, view) = Tall(dragToScroll: false);
        using var _ = fixture;

        Assert.False(view.DragToScroll);

        var y = Flick(fixture, view, steps: 4, type: type);
        fixture.Release(view.Bounds.X + (view.Bounds.Width * 0.5f), y, type: type);

        Assert.Equal(0f, view.ScrollTop);
        Assert.False(view.IsFlinging);
    }

    /// <summary>A finger scrolls the content without anybody having asked for it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half that was a working feature nothing called.</b> <c>DragToScroll</c> was
    ///         opt-in and off because no control could tell a finger from a mouse, so a touch head
    ///         got no content dragging at all until an application thought to turn it on — and an
    ///         application written on a desktop never would. The device kind on <c>DragEvent</c> is
    ///         what lets the default be right for both.
    ///     </para>
    ///     <para>
    ///         ⚠ A pen counts as a finger here and that is deliberate: neither has a cursor, so
    ///         neither is doing the text selection or the marquee the mouse branch is protecting.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(PointerType.Touch)]
    [InlineData(PointerType.Pen)]
    public void A_finger_scrolls_a_view_that_was_never_asked(PointerType type) {
        var (fixture, view) = Tall(dragToScroll: false);
        using var _ = fixture;

        Assert.False(view.DragToScroll);

        var y = Flick(fixture, view, steps: 4, type: type);
        fixture.Release(view.Bounds.X + (view.Bounds.Width * 0.5f), y, type: type);

        // Upwards, so the offset goes down the content — the one path where the number the user is
        // moving is not the number being stored.
        Assert.True(view.ScrollTop > 0f);
    }

    /// <summary>
    ///     ⚠ An axis that has reached an end has no speed left. Without that the fling goes on
    ///     decaying against the clamp for a second after it visibly stopped, and a flick back the
    ///     other way inside that second starts from a velocity the content has not had since it hit
    ///     the edge.
    /// </summary>
    [Fact]
    public void A_fling_that_reaches_the_end_stops_there() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var y = Flick(fixture, view, steps: 4);
        fixture.Release(view.Bounds.X + (view.Bounds.Width * 0.5f), y);

        view.ScrollTop = view.MaximumTop;

        for (var frame = 0; frame < 120 && view.IsFlinging; frame++) {
            fixture.Advance(Frame);
        }

        Assert.False(view.IsFlinging);
        Assert.Equal(view.MaximumTop, view.ScrollTop);
    }
}
