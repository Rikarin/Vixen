// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The edge that gives, and the spring that takes it back.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What blocked this was not the curve either — it was that there was no number allowed
///         to be out of range.</b> <c>ScrollTop</c> coerces into <c>[0, MaximumTop]</c>, and it has to:
///         it is what the bars show, what <c>ScrollIntoView</c> computes against and what a snap
///         position is measured in. So the stretch is a second offset that never reaches any of
///         those, added to the scroll offset on the way to <see cref="UiElement.OffsetY" /> and
///         nowhere else.
///     </para>
///     <para>
///         ⚠ <b>And it is what finally makes <c>overscroll-behavior: contain</c> and <c>none</c>
///         differ.</b> The pair differ in CSS only over the local effect at the boundary, and this
///         engine had no local effect — <c>ScrollViewTests</c> asserted the two were identical, and
///         said in as many words that the assertion was a claim about this engine which should fail
///         the day a rubber band appeared. This is that day.
///     </para>
///     <para>
///         ⚠ <b>Every gesture here is made of real pointer events</b>, for the reason
///         <see cref="ScrollMomentumTests" /> gives: a test that raised a <c>DragEvent</c> directly
///         would pass against a view no real input can reach.
///     </para>
/// </remarks>
public class ScrollRubberBandTests {
    const float Step = 20f;
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>A view whose content is ten times as tall as it is, sitting at its top edge.</summary>
    static (ControlFixture Fixture, ScrollView View) Tall(string declarations = "") {
        var fixture = new ControlFixture(css: $$"""
            root  { width: 400px; height: 300px; }
            #view { width: 100px; height: 60px; {{declarations}} }
            #body { width: 100px; height: 600px; }
            """);

        var view = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        fixture.Document.Create("div", view.Content, "body");

        view.DragToScroll = true;

        fixture.Update();

        // Primes the view's own clock, so the first measured interval is a frame and not the whole
        // of the document's life.
        fixture.Advance(Frame);

        return (fixture, view);
    }

    static (float X, float Y) Middle(ScrollView view) =>
        (view.Bounds.X + (view.Bounds.Width * 0.5f), view.Bounds.Y + (view.Bounds.Height * 0.5f));

    /// <summary>
    ///     Drags <i>downwards</i> from the middle of a view already at its top, one frame per step,
    ///     and reports what the edge gave on each.
    /// </summary>
    /// <remarks>
    ///     ⚠ The first step buys nothing, and that is the recogniser rather than this: a press becomes
    ///     a drag only once it has wandered past its slop, and the delta that crossed the threshold is
    ///     deliberately not honoured. So the raw pull after <c>n</c> steps is <c>Step × (n − 1)</c>.
    /// </remarks>
    static float[] PullDown(ControlFixture fixture, ScrollView view, int steps) {
        var (x, y) = Middle(view);
        var given = new float[steps];

        fixture.Press(x, y);

        for (var step = 1; step <= steps; step++) {
            fixture.MovePointer(x, y + (Step * step));
            fixture.Advance(Frame);

            given[step - 1] = view.OverscrollTop;
        }

        return given;
    }

    /// <summary>
    ///     ⚠ The offset stays where the clamp put it and the content moves anyway. That split is the
    ///     whole design: a view being held past its start is at <c>ScrollTop 0</c> for every question
    ///     anybody asks it, and is visibly somewhere else.
    /// </summary>
    [Fact]
    public void Dragging_past_the_start_stretches_the_content_instead_of_stopping_dead() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var given = PullDown(fixture, view, steps: 3);

        Assert.Equal(0f, view.ScrollTop);
        Assert.True(view.IsRubberBanding);
        Assert.True(given[^1] < 0f);

        // Negative overscroll is the content moved down the screen, which is the direction a finger
        // dragging downwards took it.
        Assert.Equal(-given[^1], view.Content.OffsetY, 3);
    }

    /// <summary>
    ///     ⚠ <b>A closed-form oracle rather than a number: the curve is concave, so every step of the
    ///     drag gives less than the step before it.</b> A resistance that was a constant fraction
    ///     would satisfy "the content moved less than the finger" and would still be wrong — it
    ///     changes speed abruptly at the boundary, which reads as the finger slipping off the content.
    /// </summary>
    [Fact]
    public void The_edge_gives_less_with_every_pixel_it_is_pulled() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var given = PullDown(fixture, view, steps: 5);

        // Step 0 crossed the slop and moved nothing, so the increments start from there.
        var previous = float.MaxValue;

        for (var step = 1; step < given.Length; step++) {
            var increment = MathF.Abs(given[step] - given[step - 1]);

            Assert.True(increment > 0f, $"step {step} gave nothing");
            Assert.True(increment < previous, $"step {step} gave {increment}, no less than {previous}");

            previous = increment;
        }
    }

    /// <summary>
    ///     ⚠ <b>Bounded by the viewport, so a determined finger cannot drag the content entirely out
    ///     of its own window.</b> The curve approaches the view's height and never reaches it; without
    ///     a ceiling a long drag hands back a blank box, which looks exactly like the content having
    ///     been deleted.
    /// </summary>
    [Fact]
    public void The_stretch_can_never_reach_the_height_of_the_view() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var given = PullDown(fixture, view, steps: 40);
        var stretch = MathF.Abs(given[^1]);

        Assert.True(stretch > 0f);
        Assert.True(stretch < view.Height, $"{stretch} of a {view.Height} view");
    }

    /// <summary>
    ///     ⚠ <b>Reversible, pixel for pixel.</b> The pull accumulated is the raw distance and the
    ///     resistance is applied on the way out, so a drag that goes past the edge and comes back
    ///     arrives at exactly the offset it left. Damping the accumulation instead — the obvious
    ///     shortcut, since it needs no second number — makes a pull-and-return end somewhere else,
    ///     and the content reads as having slipped under the finger.
    /// </summary>
    [Fact]
    public void Dragging_back_towards_the_content_unwinds_the_pull_exactly() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var (x, y) = Middle(view);

        PullDown(fixture, view, steps: 4);
        Assert.True(view.IsRubberBanding);

        // Back to where the drag became a drag: three steps of pull, given back.
        fixture.MovePointer(x, y + Step);
        fixture.Advance(Frame);

        Assert.Equal(0f, view.OverscrollTop);
        Assert.Equal(0f, view.ScrollTop);
        Assert.False(view.IsRubberBanding);

        // And carrying on the same way scrolls, rather than spending another gesture climbing out of
        // a pull that was already paid back.
        fixture.MovePointer(x, y - Step);
        fixture.Advance(Frame);

        Assert.Equal(Step * 2f, view.ScrollTop, 3);
    }

    /// <summary>The stretch is given back on its own, and the content ends up at its edge.</summary>
    [Fact]
    public void Letting_go_springs_the_content_back_to_the_edge() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var (x, y) = Middle(view);

        PullDown(fixture, view, steps: 4);
        fixture.Release(x, y + (Step * 4));

        Assert.True(view.IsRubberBanding);

        // A ceiling on frames rather than a budget in seconds: this asserts that the loop terminates,
        // and two seconds of frames is absurdly more than a spring with a tenth-of-a-second time
        // constant can need.
        for (var frame = 0; frame < 120 && view.IsRubberBanding; frame++) {
            fixture.Advance(Frame);
        }

        Assert.False(view.IsRubberBanding);
        Assert.Equal(0f, view.OverscrollTop);
        Assert.Equal(0f, view.ScrollTop);
        Assert.Equal(0f, view.Content.OffsetY);
    }

    /// <summary>
    ///     ⚠ <b>A view let go of while stretched does not fling.</b> The velocity tracker samples the
    ///     scroll offset, which is pinned at the boundary — so the speed it holds is the speed of the
    ///     last pixel before the edge, and launching on it would take the content away from the edge
    ///     it is about to be pulled back to.
    /// </summary>
    [Fact]
    public void Letting_go_of_a_stretched_view_springs_rather_than_flings() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var (x, y) = Middle(view);

        PullDown(fixture, view, steps: 4);
        fixture.Release(x, y + (Step * 4));

        Assert.False(view.IsFlinging);
        Assert.True(view.IsRubberBanding);
    }

    /// <summary>
    ///     ⚠ <b>A fling that arrives at an end bounces off it</b>, which is the half of this that has
    ///     no finger in it. Before the stretch existed the fling simply lost its speed at the clamp,
    ///     and the comment saying so named this as the missing piece.
    /// </summary>
    [Fact]
    public void A_fling_that_reaches_the_end_hands_its_speed_to_the_spring() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var (x, y) = Middle(view);

        // Upwards, which scrolls down — the drag moves the content and the offset is how far down it
        // the viewport is.
        fixture.Press(x, y);

        for (var step = 1; step <= 4; step++) {
            fixture.MovePointer(x, y - (Step * step));
            fixture.Advance(Frame);
        }

        fixture.Release(x, y - (Step * 4));

        Assert.True(view.IsFlinging);

        view.ScrollTop = view.MaximumTop;
        fixture.Advance(Frame);

        Assert.True(view.OverscrollTop > 0f);
        Assert.Equal(view.MaximumTop, view.ScrollTop);
    }

    /// <summary>
    ///     ⚠ <b>The local half of <c>overscroll-behavior</c>, which had nothing to turn off until
    ///     now.</b> <c>none</c> suppresses the boundary effect and <c>contain</c> keeps it; both stop
    ///     the chain, which is the other half and is asserted in <c>ScrollViewTests</c>.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("overscroll-behavior: auto;", true)]
    [InlineData("overscroll-behavior: contain;", true)]
    [InlineData("overscroll-behavior: none;", false)]
    [InlineData("overscroll-behavior-y: none;", false)]
    [InlineData("overscroll-behavior-x: none;", true)]
    public void Overscroll_behavior_decides_whether_the_edge_gives_at_all(string declaration, bool elastic) {
        var (fixture, view) = Tall(declaration);
        using var _ = fixture;

        var given = PullDown(fixture, view, steps: 4);

        Assert.Equal(elastic, view.IsRubberBanding);
        Assert.Equal(elastic, given[^1] < 0f);

        // Either way the offset itself is unmoved, so nothing about `none` is a scroll that went
        // somewhere it should not have.
        Assert.Equal(0f, view.ScrollTop);
    }

    /// <summary>
    ///     What the instrument reads on a view that never reaches an end — the answer that would
    ///     otherwise be mistaken for a rubber band that works, since zero stretch is also what a
    ///     broken one reports.
    /// </summary>
    [Fact]
    public void A_drag_that_stays_inside_the_content_never_stretches_anything() {
        var (fixture, view) = Tall();
        using var _ = fixture;

        var (x, y) = Middle(view);

        fixture.Press(x, y);

        for (var step = 1; step <= 3; step++) {
            fixture.MovePointer(x, y - (Step * step));
            fixture.Advance(Frame);

            Assert.False(view.IsRubberBanding);
            Assert.Equal(0f, view.OverscrollTop);
        }

        Assert.Equal(Step * 2f, view.ScrollTop);
        Assert.Equal(-Step * 2f, view.Content.OffsetY, 3);
    }
}
