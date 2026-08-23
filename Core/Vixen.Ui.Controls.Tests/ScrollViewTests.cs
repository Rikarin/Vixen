// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The four CSS families <see cref="ScrollView" /> reads — doc 43 A18.</summary>
/// <remarks>
///     <para>
///         <b>These are the claim the utility families rest on.</b>
///         <c>UtilityConsumptionGateTests</c> proves that <i>something</i> in the engine moves when
///         each property changes, which is what stops an inert family being registered; it cannot say
///         that the right thing moved in the right direction. That is this file, and the two are the
///         pair the gate's own remarks describe.
///     </para>
///     <para>
///         ⚠ <b>Every test here builds a real nested pair of scroll views</b>, because three of the
///         four families are only meaningful in one: <c>scroll-margin</c> is read off the target of
///         somebody else's <c>ScrollIntoView</c>, and <c>overscroll-behavior</c> is a question about
///         what the wheel does when it runs out of one box and reaches the next.
///     </para>
/// </remarks>
public class ScrollViewTests {
    /// <summary>An outer view whose content is a spacer, an inner view, and another spacer.</summary>
    /// <remarks>
    ///     The sizes are the ones that make each branch reachable rather than round numbers: the
    ///     inner has to be able to sit past the outer's bottom edge <i>and</i> past its right one, so
    ///     the spacers are wider than the viewport and the inner carries a left margin.
    /// </remarks>
    static (ControlFixture Fixture, ScrollView Outer, ScrollView Inner, UiElement Mark) Nest(string css) {
        var fixture = new ControlFixture(css: $$"""
            root   { width: 400px; height: 300px; }
            #outer { width: 100px; height: 60px; }
            #lead  { width: 260px; height: 90px; }
            #trail { width: 260px; height: 90px; }
            #inner { width: 60px; height: 40px; margin-left: 110px; }
            #above { width: 140px; height: 50px; }
            #mark  { width: 30px; height: 12px; margin-left: 70px; }
            #below { width: 140px; height: 50px; }
            {{css}}
            """);

        var outer = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "outer");
        fixture.Document.Create("div", outer.Content, "lead");

        var inner = outer.Content.Add<ScrollView>(null, "inner");
        fixture.Document.Create("div", outer.Content, "trail");

        fixture.Document.Create("div", inner.Content, "above");
        var mark = fixture.Document.Create("div", inner.Content, "mark");
        fixture.Document.Create("div", inner.Content, "below");

        fixture.Update();
        return (fixture, outer, inner, mark);
    }

    /// <summary>Scrolls the outer view until the inner one is actually on screen.</summary>
    /// <remarks>
    ///     ⚠ <b>Every wheel test needs this and none of the `ScrollIntoView` ones do.</b> The inner
    ///     sits at a 110-pixel left margin inside a 100-pixel viewport, so at rest it is entirely
    ///     clipped — which is exactly what the inset tests want and exactly what a hit-tested event
    ///     cannot cope with. A wheel aimed at a clipped element reaches nothing, the outer never
    ///     moves, and the failure reads as a broken scroll chain rather than as a test pointing at
    ///     empty space.
    /// </remarks>
    static void Reveal(ScrollView outer) {
        outer.ScrollTop = 90f;
        outer.ScrollLeft = 110f;
    }

    static void Reveal(ControlFixture fixture, ScrollView inner) {
        var outer = (ScrollView) inner.Parent!.Parent!;

        Reveal(outer);
        fixture.Update();
    }

    /// <summary>`scroll-margin` is read off the target, and it leaves that much more of it showing.</summary>
    /// <remarks>
    ///     Approached from above — the outer starts at its stop, so the inner is past the top edge and
    ///     the near-edge branch is the one that runs. A `scroll-margin-top` asks for eight further
    ///     pixels of room above the target, so the view stops eight pixels earlier.
    /// </remarks>
    [Theory]
    [InlineData("", 0f)]
    [InlineData("scroll-margin-top: 8px", 8f)]
    [InlineData("scroll-margin-bottom: 8px", 0f)]
    public void Scroll_margin_on_the_target_offsets_where_it_comes_to_rest(string declaration, float earlier) {
        Assert.Equal(Rest("") - earlier, Rest(declaration), 1);

        static float Rest(string css) {
            var (fixture, outer, inner, _) = Nest($"#inner {{ {css} }}");
            using var scope = fixture;

            outer.ScrollTop = outer.MaximumTop;
            fixture.Update();

            outer.ScrollIntoView(inner);
            fixture.Update();

            return outer.ScrollTop;
        }
    }

    /// <summary>And it is read off the target rather than off the view doing the scrolling.</summary>
    /// <remarks>
    ///     ⚠ <b>The same eight pixels written on the container do nothing here</b>, which is the half
    ///     of the contract a reader that took both insets off one element would fail. CSS Scroll Snap
    ///     §6: `scroll-margin` belongs to the target, `scroll-padding` to the scroll container.
    /// </remarks>
    [Fact]
    public void Scroll_margin_written_on_the_scroller_is_not_the_targets() {
        var (fixture, outer, inner, _) = Nest("#outer { scroll-margin-top: 8px; }");
        using var scope = fixture;

        outer.ScrollTop = outer.MaximumTop;
        fixture.Update();

        outer.ScrollIntoView(inner);
        fixture.Update();

        var (bare, plain, spot, _) = Nest("");
        using var other = bare;

        plain.ScrollTop = plain.MaximumTop;
        bare.Update();

        plain.ScrollIntoView(spot);
        bare.Update();

        Assert.Equal(plain.ScrollTop, outer.ScrollTop, 1);
    }

    /// <summary>`scroll-padding` is read off the container, and it insets the viewport.</summary>
    /// <remarks>
    ///     ⚠ The declaration is on the <i>inner</i> view here and on the target in the test above,
    ///     which is the whole distinction: the same eight pixels written on the other element would
    ///     do nothing at all, and a reader that took both off one element would pass both tests only
    ///     because the numbers happen to match.
    /// </remarks>
    [Fact]
    public void Scroll_padding_on_the_container_insets_the_viewport() {
        var plain = Rest("");
        var inset = Rest("scroll-padding-top: 8px");

        Assert.Equal(plain - 8f, inset, 1);

        static float Rest(string declaration) {
            var (fixture, _, inner, mark) = Nest($"#inner {{ {declaration} }}");
            using var scope = fixture;

            inner.ScrollTop = inner.MaximumTop;
            fixture.Update();

            inner.ScrollIntoView(mark);
            fixture.Update();

            return inner.ScrollTop;
        }
    }

    /// <summary>The logical edges fold onto physical ones the way `direction` says.</summary>
    /// <remarks>
    ///     ⚠ <b>The pair swap under `rtl`, which is the only thing that makes them worth having.</b>
    ///     A reader that mapped `-inline-start` onto the left unconditionally passes the `ltr` half
    ///     of this and fails the other, and every editor panel is `ltr`.
    /// </remarks>
    [Theory]
    [InlineData("ltr", "scroll-padding-inline-start: 8px", true)]
    [InlineData("rtl", "scroll-padding-inline-start: 8px", false)]
    [InlineData("ltr", "scroll-padding-inline-end: 8px", false)]
    [InlineData("rtl", "scroll-padding-inline-end: 8px", true)]
    public void The_logical_scroll_insets_fold_against_direction(string direction, string declaration, bool leading) {
        var (fixture, _, inner, mark) = Nest($"#inner {{ direction: {direction}; {declaration} }}");
        using var scope = fixture;

        // Approached from the left, so only a *left* inset can move anything.
        inner.ScrollLeft = inner.MaximumLeft;
        fixture.Update();

        inner.ScrollIntoView(mark);
        fixture.Update();

        var moved = inner.ScrollLeft;

        var (bare, _, plain, spot) = Nest($"#inner {{ direction: {direction}; }}");
        using var other = bare;

        plain.ScrollLeft = plain.MaximumLeft;
        bare.Update();

        plain.ScrollIntoView(spot);
        bare.Update();

        Assert.Equal(leading ? plain.ScrollLeft - 8f : plain.ScrollLeft, moved, 1);
    }

    /// <summary>`scroll-behavior: smooth` arrives late, and `auto` arrives at once.</summary>
    [Fact]
    public void Scroll_behavior_smooth_eases_instead_of_jumping() {
        var (fixture, _, inner, mark) = Nest("#inner { scroll-behavior: smooth; }");
        using var scope = fixture;

        inner.ScrollIntoView(mark);
        fixture.Update();

        Assert.True(inner.IsScrolling, "a smooth scroll is still on its way after the call");
        Assert.Equal(0f, inner.ScrollTop, 1);

        fixture.Advance(TimeSpan.FromMilliseconds(16));
        var first = inner.ScrollTop;

        Assert.True(first > 0f, "it has started");
        Assert.True(inner.IsScrolling, "and has not finished in one frame");

        // Far enough for the exponential to be inside the half-pixel snap.
        for (var frame = 0; frame < 40; frame++) {
            fixture.Advance(TimeSpan.FromMilliseconds(16));
        }

        Assert.False(inner.IsScrolling);
        Assert.True(inner.ScrollTop > first);
    }

    /// <summary>Without it, the same scroll is over before the next frame.</summary>
    [Fact]
    public void The_initial_behaviour_is_still_a_jump() {
        var (fixture, _, inner, mark) = Nest("");
        using var scope = fixture;

        inner.ScrollIntoView(mark);

        Assert.False(inner.IsScrolling);
        Assert.True(inner.ScrollTop > 0f);
    }

    /// <summary>A wheel on a view that has run out chains outwards, unless it is told not to.</summary>
    /// <remarks>
    ///     ⚠ <b>`contain` and `none` are asserted to behave identically</b>, which is a claim about
    ///     this engine rather than about CSS — there is no rubber-band here for `none` to suppress on
    ///     top of stopping the chain. Written down as a test so that the day one appears, this fails
    ///     rather than the difference going unnoticed. See <see cref="OverscrollBehavior" />.
    /// </remarks>
    [Theory]
    [InlineData("", true)]
    [InlineData("overscroll-behavior: auto", true)]
    [InlineData("overscroll-behavior: contain", false)]
    [InlineData("overscroll-behavior: none", false)]
    [InlineData("overscroll-behavior-y: contain", false)]
    [InlineData("overscroll-behavior-x: contain", true)]
    public void Overscroll_behavior_decides_whether_the_wheel_reaches_the_outer_view(string declaration, bool chains) {
        var (fixture, outer, inner, _) = Nest($"#inner {{ {declaration} }}");
        using var scope = fixture;

        // ⚠ The outer is positioned so the inner is *visible*, and that is not decoration: the wheel
        // is hit-tested, the outer clips, and an inner scrolled off the edge takes no wheel at all —
        // which fails as "the chain is broken" and is nothing of the kind.
        inner.ScrollTop = inner.MaximumTop;
        Reveal(outer);
        fixture.Update();

        var before = outer.ScrollTop;
        fixture.Wheel(inner, 30f);

        Assert.Equal(chains ? before + 30f : before, outer.ScrollTop, 1);
    }

    /// <summary>An axis that still has room is scrolled, and never chains whatever the property says.</summary>
    [Fact]
    public void A_view_with_room_left_keeps_the_wheel_to_itself() {
        var (fixture, outer, inner, _) = Nest("#inner { overscroll-behavior: auto; }");
        using var scope = fixture;

        Reveal(outer);
        fixture.Update();

        var before = outer.ScrollTop;
        fixture.Wheel(inner, 10f);

        Assert.Equal(10f, inner.ScrollTop, 1);
        Assert.Equal(before, outer.ScrollTop, 1);
    }

    /// <summary>The wheel abandons an easing in flight rather than fighting it.</summary>
    [Fact]
    public void A_wheel_settles_a_smooth_scroll_that_was_still_running() {
        var (fixture, _, inner, mark) = Nest("#inner { scroll-behavior: smooth; }");
        using var scope = fixture;

        Reveal(fixture, inner);

        inner.ScrollIntoView(mark);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.True(inner.IsScrolling);

        fixture.Wheel(inner, 4f);
        Assert.False(inner.IsScrolling);

        var stopped = inner.ScrollTop;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal(stopped, inner.ScrollTop, 1);
    }

    /// <summary>Nothing declared is the behaviour that shipped before any of this was read.</summary>
    /// <remarks>
    ///     The regression guard for the whole change: `ScrollIntoView` moved the minimum that works
    ///     and still does, and every caller in the tree — `TreeView`, `DataGrid`, the focus hook —
    ///     depends on that rather than on centring.
    /// </remarks>
    [Fact]
    public void With_nothing_declared_it_still_moves_the_minimum_that_works() {
        var (fixture, _, inner, mark) = Nest("");
        using var scope = fixture;

        inner.ScrollIntoView(mark);
        fixture.Update();

        var once = inner.ScrollTop;

        inner.ScrollIntoView(mark);
        fixture.Update();

        Assert.Equal(once, inner.ScrollTop, 1);
        Assert.Equal(mark.Bounds.Bottom, inner.Bounds.Bottom, 1);
    }
}
