// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Two panes and a bar, and the arithmetic that decides where the bar is.</summary>
/// <remarks>
///     ⚠ <b>Asserted on the laid-out widths rather than on the declarations the control writes.</b>
///     A test that checked for <c>flex-grow: 0.25</c> would pass against every version of this
///     control that wrote the string and got the layout wrong — including the one that forgets
///     <c>flex-basis</c>, which is the mistake this whole arrangement exists to avoid and the one a
///     string comparison cannot see.
/// </remarks>
public class SplitViewTests {
    /// <summary>Wide enough that a pixel of rounding is not a fifth of the answer.</summary>
    const float Width = 400f;

    /// <summary>What `split-bar` is, in the theme.</summary>
    const float Bar = 6f;

    /// <summary>How far the laid-out answer may be from the arithmetic: the layout rounds to pixels.</summary>
    const float Pixel = 1f;

    /// <summary>What the two panes share.</summary>
    const float Span = Width - Bar;

    [Fact]
    public void A_split_at_its_default_halves_what_the_two_panes_share() {
        var (fixture, split) = Opened();

        using (fixture) {
            Assert.Equal(0.5f, split.Ratio);
            Assert.Equal(Span * 0.5f, split.First.Width, Pixel);
            Assert.Equal(Span * 0.5f, split.Second.Width, Pixel);
        }
    }

    [Fact]
    public void The_ratio_is_where_the_bar_is() {
        var (fixture, split) = Opened();

        using (fixture) {
            split.Ratio = 0.25f;
            fixture.Update();

            Assert.Equal(Span * 0.25f, split.First.Width, Pixel);
            Assert.Equal(Span * 0.75f, split.Second.Width, Pixel);
        }
    }

    /// <summary>
    ///     ⚠ <b>The one that fails without <c>flex-basis: 0px</c>.</b> A flex item's basis is its
    ///     content by default and the grow factors share out only what is left over, so a pane
    ///     holding something wide takes its content plus its share — and a split set to a quarter
    ///     comes out at a half. This is that case with a number on it: the same ratio, against a pane
    ///     whose content is wider than the ratio allows.
    /// </summary>
    [Fact]
    public void A_pane_holding_something_too_wide_is_still_the_ratio_it_was_given() {
        var (fixture, split) = Opened("panel.wide { width: 320px; height: 20px; }");

        using (fixture) {
            split.First.Add<Panel>().AddClass("wide");
            split.Ratio = 0.25f;
            fixture.Update();

            Assert.Equal(Span * 0.25f, split.First.Width, Pixel);
        }
    }

    /// <summary>
    ///     ⚠ Widening the minimum past where the bar already is moves the bar. A minimum that only
    ///     applied to the next assignment would leave a split outside its own minimum for ever.
    /// </summary>
    [Fact]
    public void The_minimum_clamps_the_ratio_in_both_directions_and_afterwards() {
        var (fixture, split) = Opened();

        using (fixture) {
            split.Ratio = 0.01f;
            Assert.Equal(split.MinimumRatio, split.Ratio);

            split.Ratio = 0.99f;
            Assert.Equal(1f - split.MinimumRatio, split.Ratio);

            split.Ratio = 0.2f;
            split.MinimumRatio = 0.35f;

            Assert.Equal(0.35f, split.Ratio);
        }
    }

    [Fact]
    public void A_vertical_split_shares_the_height_instead() {
        var (fixture, split) = Opened();

        using (fixture) {
            split.Orientation = Orientation.Vertical;
            split.Ratio = 0.25f;
            fixture.Update();

            // The bar is six pixels the other way round now, so the span is the height's.
            Assert.Equal((200f - Bar) * 0.25f, split.First.Height, Pixel);
            Assert.Equal(Width, split.First.Width, Pixel);
        }
    }

    /// <summary>
    ///     ⚠ Both panes and the bar, in that order. A bar appended after the parts — which is what
    ///     `Part` does if it is asked for last — puts both panes on one side of it.
    /// </summary>
    [Fact]
    public void The_bar_is_between_the_two_panes() {
        var (fixture, split) = Opened();

        using (fixture) {
            Assert.Equal([split.First, split.Bar, split.Second], split.Children);
        }
    }

    /// <summary>
    ///     ⚠ The two calls generated markup makes, rather than a <c>.vxml</c> fixture: a
    ///     <c>&lt;SplitView&gt;</c> with a nested tag emits <c>ctx.Element(Inner(n), …)</c> and one
    ///     with <c>slot="second"</c> emits <c>ctx.Element(Into(n, "second"), …)</c>. Asking those two
    ///     directly is what proves the pair of hosts is wired, and <c>Into</c> throws rather than
    ///     falling back — so a slot name a control does not publish is loud.
    /// </summary>
    [Fact]
    public void Markup_reaches_the_near_pane_by_default_and_the_far_one_by_name() {
        var (fixture, split) = Opened();

        using (fixture) {
            Assert.Same(split.First, BuildContext.Inner(split));
            Assert.Same(split.Second, BuildContext.Into(split, SplitView.SecondSlot));
            Assert.Throws<InvalidOperationException>(() => BuildContext.Into(split, "third"));
        }
    }

    static (ControlFixture Fixture, SplitView Split) Opened(string? extra = null) {
        var fixture = new ControlFixture(
            css: $"split-view {{ width: {Width}px; height: 200px; }}" + (extra is null ? "" : " " + extra)
        );

        var split = fixture.Add<SplitView>();
        fixture.Update();

        return (fixture, split);
    }
}
