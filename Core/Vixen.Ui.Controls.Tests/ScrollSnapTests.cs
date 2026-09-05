// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>CSS Scroll Snap, which is the one scroll family whose deferral premise was true.</summary>
/// <remarks>
///     <para>
///         <b>Doc 43 § Part 8 § 3.</b> The twenty-two <c>scroll-m-*</c>/<c>scroll-p-*</c> roots were
///         four property reads inside a control that already scrolled — the deferral there was
///         "deferred until somebody checks whether the feature landed". <c>snap-*</c> is the other
///         kind: a scroll that comes to rest has to choose among candidates nothing enumerated, and
///         neither of the two gestures anybody writes <c>snap-y</c> for had an <i>end</i> at which
///         "comes to rest" was defined.
///     </para>
///     <para>
///         ⚠ <b>Which is why half this file is about <i>when</i> rather than <i>where</i>.</b> A snap
///         that fired on every wheel notch would pass every arithmetic test here and would be
///         unusable — the content would jump back under a flick still in progress — so the tests that
///         matter most are the ones that watch a view stay unsnapped while the gesture runs.
///     </para>
///     <para>
///         The clock is <c>ControlFixture.Advance</c>'s, which the test moves by hand. Nothing here
///         waits on the machine.
///     </para>
/// </remarks>
public class ScrollSnapTests {
    /// <summary>The idle the wheel's terminator wants, with a frame's slack, in one place.</summary>
    static readonly TimeSpan Still = TimeSpan.FromSeconds(ScrollView.SnapIdleSeconds + 0.05f);

    /// <summary>Half of it — long enough to be a real wait and short enough to be mid-gesture.</summary>
    static readonly TimeSpan Briefly = TimeSpan.FromSeconds(ScrollView.SnapIdleSeconds * 0.4f);

    /// <summary>A 100×60 view over five 40-pixel rows, so the snap positions are 0, 40, 80, 120, 140.</summary>
    /// <remarks>
    ///     ⚠ <b>The last one is clamped and that is deliberate.</b> Five rows of forty in a viewport
    ///     of sixty can travel 140, so the fifth row's <c>start</c> position is 160 and the offset
    ///     that reaches it does not exist. A snap that handed back an unreachable position would be
    ///     clamped by the coercion on the way in and look correct from the outside — until a
    ///     <c>mandatory</c> container asked again on the next layout and got the same unreachable
    ///     answer for ever.
    /// </remarks>
    static (ControlFixture Fixture, ScrollView View, UiElement[] Rows) Rows(string view, string rows = "", string extra = "") {
        var fixture = new ControlFixture(css: $$"""
            root  { width: 400px; height: 300px; }
            #view { width: 100px; height: 60px; {{view}} }
            .row  { width: 100px; height: 40px; {{rows}} }
            {{extra}}
            """);

        var scroller = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        var made = new UiElement[5];

        for (var index = 0; index < made.Length; index++) {
            made[index] = fixture.Document.Create("div", scroller.Content, Names[index], "row");
        }

        fixture.Update();
        return (fixture, scroller, made);
    }

    static readonly string[] Names = ["zero", "one", "two", "three", "four"];

    /// <summary>A 100×60 view over five 40-pixel columns, for the axis the other helper cannot reach.</summary>
    static (ControlFixture Fixture, ScrollView View) Columns(string view, string columns) {
        var fixture = new ControlFixture(css: $$"""
            root  { width: 400px; height: 300px; }
            #view scroll-content { flex-direction: row; }
            #view { width: 60px; height: 100px; {{view}} }
            .col  { width: 40px; height: 100px; flex-shrink: 0; {{columns}} }
            """);

        var scroller = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");

        for (var index = 0; index < 5; index++) {
            fixture.Document.Create("div", scroller.Content, Names[index], "col");
        }

        fixture.Update();
        return (fixture, scroller);
    }

    /// <summary>Turns the wheel and then leaves it alone for long enough to count as stopped.</summary>
    static void Flick(ControlFixture fixture, ScrollView view, float delta) {
        fixture.Advance(TimeSpan.FromMilliseconds(16));
        fixture.Wheel(view, delta);
        fixture.Advance(Still);
    }

    /// <summary>A mandatory container puts the wheel on a candidate, and an undeclared one does not.</summary>
    /// <remarks>
    ///     ⚠ <b>The undeclared half is the instrument check.</b> A snap implemented as "round the
    ///     offset to the nearest row" would pass the first case and would also snap a view that never
    ///     asked to, which is every scroll view in the editor.
    /// </remarks>
    [Theory]
    [InlineData("", 25f)]
    [InlineData("scroll-snap-type: y mandatory", 40f)]
    public void The_wheel_comes_to_rest_on_a_candidate_only_where_the_container_asks(string declaration, float rest) {
        var (fixture, view, _) = Rows(declaration, "scroll-snap-align: start");
        using var scope = fixture;

        Flick(fixture, view, 25f);

        Assert.Equal(rest, view.ScrollTop, 1);
    }

    /// <summary>`proximity` snaps only when a candidate is near, and `mandatory` always does.</summary>
    /// <remarks>
    ///     The viewport is sixty tall, so <see cref="ScrollView.SnapProximity" /> puts the threshold
    ///     at fifteen: an offset of twenty is twenty from both neighbours and is left alone, and one
    ///     of thirty is ten from the row below and is not. ⚠ Both cases are needed — a
    ///     <c>proximity</c> that always snapped would be <c>mandatory</c> spelled differently, and a
    ///     <c>proximity</c> that never did would be an inert value in a registered family.
    /// </remarks>
    [Theory]
    [InlineData("proximity", 20f, 20f)]
    [InlineData("proximity", 30f, 40f)]
    [InlineData("mandatory", 20f, 0f)]
    [InlineData("mandatory", 30f, 40f)]
    public void Proximity_leaves_a_scroll_that_stopped_between_candidates_where_it_is(string strictness, float delta, float rest) {
        var (fixture, view, _) = Rows($"scroll-snap-type: y {strictness}", "scroll-snap-align: start");
        using var scope = fixture;

        Flick(fixture, view, delta);

        Assert.Equal(rest, view.ScrollTop, 1);
    }

    /// <summary>⚠ It snaps when the wheel stops, and not while it is still turning.</summary>
    /// <remarks>
    ///     <b>The property the whole feature is about, and the one an arithmetic test cannot see.</b>
    ///     A wheel is a stream of deltas with no terminator in it, so a snap has to be keyed to a
    ///     silence — and a snap keyed to the delta instead would drag the content back to the last
    ///     row under every flick in progress. The first assertion is what goes red if that ever
    ///     becomes true.
    /// </remarks>
    [Fact]
    public void A_flick_still_in_progress_is_not_snapped() {
        var (fixture, view, _) = Rows("scroll-snap-type: y mandatory", "scroll-snap-align: start");
        using var scope = fixture;

        fixture.Advance(TimeSpan.FromMilliseconds(16));
        fixture.Wheel(view, 25f);

        Assert.Equal(25f, view.ScrollTop, 1);

        fixture.Advance(Briefly);
        Assert.Equal(25f, view.ScrollTop, 1);

        fixture.Wheel(view, 4f);
        fixture.Advance(Briefly);

        Assert.Equal(29f, view.ScrollTop, 1);

        fixture.Advance(Still);
        Assert.Equal(40f, view.ScrollTop, 1);
    }

    /// <summary>Where a candidate lines up is its own business, per axis.</summary>
    /// <remarks>
    ///     Row two runs from 80 to 120 in a viewport 60 tall. <c>start</c> puts its top at the
    ///     snapport's top — offset 80; <c>end</c> puts its bottom at the snapport's bottom — offset
    ///     60; <c>center</c> puts its middle at the middle — offset 70.
    /// </remarks>
    [Theory]
    [InlineData("start", 80f)]
    [InlineData("center", 70f)]
    [InlineData("end", 60f)]
    public void The_alignment_says_which_edge_of_the_candidate_meets_which_edge_of_the_snapport(string align, float rest) {
        var (fixture, view, _) = Rows("scroll-snap-type: y mandatory", extra: $"#two {{ scroll-snap-align: {align}; }}");
        using var scope = fixture;

        Flick(fixture, view, 68f);

        Assert.Equal(rest, view.ScrollTop, 1);
    }

    /// <summary>`scroll-padding` moves the snapport's edge, and the snap position with it.</summary>
    /// <remarks>
    ///     ⚠ <b>Off the container, not off the candidate</b> — the same asymmetry
    ///     <c>ScrollView.InsetOf</c> exists for. Row two's <c>start</c> position is 80 with no
    ///     padding; twelve pixels of <c>scroll-padding-top</c> asks for that much room above whatever
    ///     lands there, so it comes to rest twelve pixels earlier.
    /// </remarks>
    [Fact]
    public void Scroll_padding_on_the_container_moves_where_a_candidate_lands() {
        var (fixture, view, _) = Rows(
            "scroll-snap-type: y mandatory; scroll-padding-top: 12px",
            extra: "#two { scroll-snap-align: start; }"
        );

        using var scope = fixture;

        Flick(fixture, view, 68f);

        Assert.Equal(68f, view.ScrollTop, 1);
    }

    /// <summary>⚠ A scroll may not pass a candidate that says it must not be passed.</summary>
    /// <remarks>
    ///     <c>scroll-snap-stop: always</c> is not a claim about where a scroll ended — the nearest
    ///     candidate to the destination is still 120 here — but about what it went <i>over</i> on the
    ///     way, which is the whole reason the gesture's origin has to survive it.
    /// </remarks>
    [Theory]
    [InlineData("", 120f)]
    [InlineData("scroll-snap-stop: always", 40f)]
    public void A_snap_stop_of_always_catches_a_scroll_passing_over_it(string declaration, float rest) {
        var (fixture, view, _) = Rows(
            "scroll-snap-type: y mandatory",
            "scroll-snap-align: start",
            $"#one {{ {declaration} }}"
        );

        using var scope = fixture;

        view.ScrollTo(120f, 0f);
        fixture.Update();

        Assert.Equal(rest, view.ScrollTop, 1);
    }

    /// <summary>An axis nobody snapped is left alone, however hard the other one snaps.</summary>
    /// <remarks>
    ///     ⚠ The failure this is written against is one <c>Chaining</c> already had a version of: a
    ///     reader that folded the two axes into one boolean makes <c>snap-y</c> quietly snap the
    ///     horizontal scroll of everything it is written on.
    /// </remarks>
    [Fact]
    public void Snapping_one_axis_does_not_snap_the_other() {
        var (fixture, view, _) = Rows("scroll-snap-type: x mandatory", "scroll-snap-align: start");
        using var scope = fixture;

        Flick(fixture, view, 25f);

        Assert.Equal(25f, view.ScrollTop, 1);
    }

    /// <summary>And the horizontal axis snaps on its own terms.</summary>
    [Fact]
    public void The_horizontal_axis_snaps_where_the_vertical_one_would() {
        var (fixture, view) = Columns("scroll-snap-type: x mandatory", "scroll-snap-align: start");
        using var scope = fixture;

        fixture.Advance(TimeSpan.FromMilliseconds(16));
        fixture.Wheel(view, 0f, 25f);
        fixture.Advance(Still);

        Assert.Equal(40f, view.ScrollLeft, 1);
    }

    /// <summary>⚠ A `mandatory` container is snapped at rest, not only on the way to rest.</summary>
    /// <remarks>
    ///     Nothing gestured here: the offset was assigned. CSS makes <c>mandatory</c> a statement
    ///     about where the container is allowed to be, so content inserted above the viewport or a
    ///     resize has to bring it back onto a candidate — which is why the re-snap hangs off the
    ///     layout and not off the gesture. <c>proximity</c> deliberately does not, and the second
    ///     case is what stops that becoming "every snap container snaps constantly".
    /// </remarks>
    [Theory]
    [InlineData("mandatory", 40f)]
    [InlineData("proximity", 25f)]
    public void A_mandatory_container_re_snaps_after_a_layout_nobody_gestured_in(string strictness, float rest) {
        var (fixture, view, _) = Rows($"scroll-snap-type: y {strictness}", "scroll-snap-align: start");
        using var scope = fixture;

        view.ScrollTop = 25f;
        fixture.Update();

        Assert.Equal(rest, view.ScrollTop, 1);
    }

    /// <summary>A drag on the bar snaps when the thumb is let go of, and not while it is held.</summary>
    /// <remarks>
    ///     ⚠ <b>The second of the two gestures, and the one that needed a new event.</b>
    ///     <c>ScrollBar.Scrolled</c> is a position stream: it says where the thumb is and never that
    ///     the hand has come off it, so a scroll view listening to it alone can know everything about
    ///     a drag except when it ended. The mid-drag assertion is the half that fails if
    ///     <c>ScrollEnded</c> is ever replaced by snapping on every move.
    /// </remarks>
    [Fact]
    public void A_drag_on_the_bar_snaps_when_it_is_released() {
        var (fixture, view, _) = Rows("scroll-snap-type: y mandatory", "scroll-snap-align: start");
        using var scope = fixture;

        var bar = view.VerticalBar.Bounds;
        var x = bar.X + (bar.Width * 0.5f);

        fixture.Press(x, bar.Y + (bar.Height * 0.5f));

        var held = view.ScrollTop;

        Assert.Equal(70f, held, 1);

        fixture.Release(x, bar.Y + (bar.Height * 0.5f));

        Assert.Equal(80f, view.ScrollTop, 1);
    }

    /// <summary>⚠ A candidate inside a nested view belongs to that view, not to this one.</summary>
    /// <remarks>
    ///     A snap area belongs to its <i>nearest</i> scroll container. An outer view that walked into
    ///     an inner one would snap to a position the inner one is about to move out from under it,
    ///     and the symptom is an outer list that drifts whenever an inner list is scrolled.
    /// </remarks>
    [Fact]
    public void A_nested_views_candidates_are_not_this_ones() {
        var fixture = new ControlFixture(css: """
            root   { width: 400px; height: 300px; }
            #outer { width: 100px; height: 60px; scroll-snap-type: y mandatory; }
            #lead  { width: 100px; height: 100px; }
            #inner { width: 100px; height: 40px; }
            .row   { width: 100px; height: 33px; scroll-snap-align: start; }
            """);

        using var scope = fixture;

        var outer = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "outer");
        fixture.Document.Create("div", outer.Content, "lead");

        var inner = outer.Content.Add<ScrollView>(null, "inner");
        for (var index = 0; index < 3; index++) {
            fixture.Document.Create("div", inner.Content, Names[index], "row");
        }

        fixture.Update();

        Flick(fixture, outer, 25f);

        // The inner rows are the only candidates in the tree and none of them is the outer's, so the
        // outer has nothing to snap to and stays exactly where the wheel left it.
        Assert.Equal(25f, outer.ScrollTop, 1);
    }
}
