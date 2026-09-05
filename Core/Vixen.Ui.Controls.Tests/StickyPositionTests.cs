// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>CSS Position §3.3's <c>position: sticky</c>, against a real <see cref="ScrollView" />.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here scrolls a real <see cref="ScrollView" /> rather than writing an
///         offset by hand, and that is the point of the file rather than a nicety.</b> A sticky box's
///         whole content is the relationship between three rectangles that no synthetic double owns
///         together — the box's own flow position, the scrollport it is held inside, and the
///         containing block it may not leave. A test that set an offset directly would be asserting
///         the arithmetic against itself.
///     </para>
///     <para>
///         ⚠ <b>The assertions are on <see cref="UiElement.AbsoluteTop" />, which is what BOTH
///         consumers of a position read.</b> The draw list and the hit test take the accumulated
///         value, so an element that sticks in the drawing and not in the hit test is not a state
///         this can be in — which is the same guarantee <c>translate</c> gets from landing in the
///         same sum, and the reason sticky was put there rather than beside it.
///     </para>
/// </remarks>
public class StickyPositionTests {
    /// <summary>A 100×60 scroller over a tall column: a spacer, a sticky header, and a filler.</summary>
    /// <remarks>
    ///     The header is the second child so that there is somewhere for it to be scrolled <i>to</i>:
    ///     a sticky box that is already at the top of its scroller is indistinguishable from a
    ///     static one, which is how a sticky implementation that does nothing passes a careless test.
    /// </remarks>
    static (ControlFixture Fixture, ScrollView View, UiElement Header) Build(string css) {
        var fixture = new ControlFixture(css: $$"""
            root    { width: 400px; height: 300px; }
            #view   { width: 100px; height: 60px; }
            #lead   { width: 80px; height: 50px; }
            #band   { width: 80px; height: 200px; flex-direction: column; }
            #pad    { width: 80px; height: 40px; }
            #header { width: 80px; height: 20px; position: sticky; top: 0; }
            #filler { width: 80px; height: 140px; }
            #tail   { width: 80px; height: 300px; }
            {{css}}
            """);

        var view = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        fixture.Document.Create("div", view.Content, "lead");

        var band = fixture.Document.Create("div", view.Content, "band");

        // ⚠ Forty points of padding INSIDE the band, and it is what makes the floor observable. With
        // the header as its containing block's first child, "no higher than the port" and "no higher
        // than the band" are the same number at every scroll position, so an implementation that
        // assigned the inset instead of flooring with it passed every test here. The pad separates
        // the two rectangles by forty points; see `A_sticky_box_is_where_the_flow_put_it_...`.
        fixture.Document.Create("div", band, "pad");

        var header = fixture.Document.Create("div", band, "header");
        fixture.Document.Create("div", band, "filler");

        // ⚠ Three hundred points of nothing, and without them two of the tests below assert against a
        // scroll position the view refuses to reach: `MaximumTop` is the content height less the
        // viewport, so a column that ends at the band's own bottom edge cannot be scrolled past it —
        // and the containing-block clamp, which is the whole difference between `sticky` and `fixed`,
        // never gets a chance to fire.
        fixture.Document.Create("div", view.Content, "tail");

        fixture.Update();

        return (fixture, view, header);
    }

    static float StickyTop(ControlFixture fixture, ScrollView view, UiElement header, float scrollTop) {
        view.ScrollTop = scrollTop;
        fixture.Update();

        return header.AbsoluteTop - view.AbsoluteTop;
    }

    /// <summary>Before it is reached, a sticky box is exactly where the flow put it.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that a `fixed`-shaped implementation fails.</b> Treating the inset as an
    ///     assignment rather than as a floor pins the header to the port's top edge from the first
    ///     frame, which looks convincing in a screenshot and is wrong for every scroll position
    ///     before the box arrives.
    /// </remarks>
    [Fact]
    public void A_sticky_box_is_where_the_flow_put_it_until_the_scroll_reaches_it() {
        var (fixture, view, header) = Build("");
        using var scope = fixture;

        // The header starts 90 points down: 50 of lead spacer and 40 of pad inside its own band.
        Assert.Equal(90f, StickyTop(fixture, view, header, 0f), 1);
        Assert.Equal(70f, StickyTop(fixture, view, header, 20f), 1);
        Assert.Equal(0f, StickyTop(fixture, view, header, 90f), 1);
    }

    /// <summary>Past that point it holds at the inset, however far the scroll goes.</summary>
    [Fact]
    public void A_sticky_box_holds_at_its_inset_once_the_scroll_passes_it() {
        var (fixture, view, header) = Build("");
        using var scope = fixture;

        Assert.Equal(0f, StickyTop(fixture, view, header, 100f), 1);
        Assert.Equal(0f, StickyTop(fixture, view, header, 200f), 1);
    }

    /// <summary>The inset is honoured, and it is measured from the scrollport rather than the document.</summary>
    [Fact]
    public void The_inset_is_measured_from_the_scrollport() {
        var (fixture, view, header) = Build("#header { top: 8px }");
        using var scope = fixture;

        Assert.Equal(8f, StickyTop(fixture, view, header, 120f), 1);
    }

    /// <summary>
    ///     A sticky box stops at the bottom of its own containing block instead of following the
    ///     reader down the document.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the entire difference between <c>sticky</c> and <c>fixed</c>, and it is the
    ///     clamp that is easiest to leave out.</b> The header's containing block is the 200-point
    ///     band; the band starts 50 points into the content, so its bottom edge is at 250 and the
    ///     header's last resting place is 230. Scrolled to 250 the port's top is at content 250, so a
    ///     header still pinned to the port would read 0 — a heading that outlived its own section.
    /// </remarks>
    [Fact]
    public void A_sticky_box_does_not_leave_its_containing_block() {
        var (fixture, view, header) = Build("");
        using var scope = fixture;

        // Content 250 is the band's bottom edge; the header's top may reach 230 and no further, so
        // relative to a port whose top is at content 250 it has gone twenty points above it.
        Assert.Equal(-20f, StickyTop(fixture, view, header, 250f), 1);
    }

    /// <summary>An <c>auto</c> inset does not stick that edge, so a box with none never moves.</summary>
    /// <remarks>
    ///     §3.3: the offsets decide which edges participate. Reading a missing <c>top</c> as
    ///     <c>top: 0</c> would pin every sticky box in a document to the top of its scroller, which
    ///     is the shape of "the feature works" that is actually the feature ignoring its input.
    /// </remarks>
    [Fact]
    public void A_sticky_box_with_no_inset_on_an_axis_does_not_stick_on_it() {
        var (fixture, view, header) = Build("#header { top: auto }");
        using var scope = fixture;

        Assert.Equal(-30f, StickyTop(fixture, view, header, 120f), 1);
    }

    /// <summary>Stickiness reaches the subtree, because a stuck box carries its contents.</summary>
    [Fact]
    public void A_sticky_box_carries_its_children() {
        var (fixture, view, header) = Build("#label { width: 40px; height: 10px }");
        using var scope = fixture;

        var label = fixture.Document.Create("div", header, "label");
        fixture.Update();

        view.ScrollTop = 120f;
        fixture.Update();

        Assert.Equal(header.AbsoluteTop, label.AbsoluteTop, 1);
        Assert.Equal(0f, label.AbsoluteTop - view.AbsoluteTop, 1);
    }

    /// <summary>
    ///     A sticky box is the containing block of its absolutely positioned descendants, and needs
    ///     no second declaration to be one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CSS Position 3 § 2 lists <c>sticky</c> among the <i>positioned</i> values, and
    ///         this store read it as <c>static</c>.</b> So an absolute child anchored to whatever
    ///         ancestor happened to be positioned — silently, several levels up — and
    ///         <c>top: 0; bottom: 0</c> gave a plausible-looking box of the wrong height rather than
    ///         an error. <c>AdvancedTheme.vcss</c> carried a <c>contain: layout</c> beside the
    ///         <c>sticky</c> to buy the containing block back; that declaration is gone, and this
    ///         test is what its absence rests on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this prints on the day the feature is not there is 300, not zero.</b> The
    ///         root is a containing block whatever its <c>position</c> says, because it is the
    ///         outermost box — so the failing answer is a cell as tall as the whole surface, which
    ///         looks like a layout rather than like a bug. That is why the assertion is the sticky
    ///         box's own height and not a relation such as "no taller than the root".
    ///     </para>
    ///     <para>
    ///         The last assertion is the one that catches the wrong repair: mapping <c>sticky</c> to
    ///         <c>relative</c> also makes it a containing block, and reads <c>top</c> as a layout
    ///         offset besides, so the box itself would move twelve points before anything was ever
    ///         scrolled.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_sticky_box_is_the_containing_block_of_its_absolute_children() {
        using var fixture = new ControlFixture(css: """
            root  { width: 400px; height: 300px; flex-direction: column; }
            #lead { width: 80px; height: 50px; }
            #here { width: 80px; height: 20px; position: sticky; top: 12px; }
            #cell { position: absolute; top: 0px; bottom: 0px; left: 0px; right: 0px; }
            """);

        fixture.Document.Create("div", fixture.Document.Root, "lead");
        var here = fixture.Document.Create("div", fixture.Document.Root, "here");
        var cell = fixture.Document.Create("div", here, "cell");
        fixture.Update();

        Assert.Equal(20f, cell.Height, 1);
        Assert.Equal(80f, cell.Width, 1);
        Assert.Equal(here.AbsoluteTop, cell.AbsoluteTop, 1);

        // ⚠ And the sticky box itself has not moved by its own inset. `top: 12px` is a floor against
        // a scroll position and there is no scroller here, so twelve points of drift would be the
        // `relative` repair having been made by mistake.
        Assert.Equal(50f, here.AbsoluteTop, 1);
    }

    /// <summary>A box with no scrolling ancestor is sticky against nothing and does not move.</summary>
    /// <remarks>
    ///     ⚠ <b>The unbounded scrollport is a real state and not a defensive branch.</b> An element
    ///     under a plain panel has no port; giving it the surface instead would make
    ///     <c>position: sticky</c> behave as <c>position: fixed</c>, which doc 09 refuses on purpose.
    /// </remarks>
    [Fact]
    public void A_sticky_box_outside_any_scroller_stays_in_flow() {
        using var fixture = new ControlFixture(css: """
            root  { width: 400px; height: 300px; flex-direction: column; }
            #lead { width: 80px; height: 50px; }
            #here { width: 80px; height: 20px; position: sticky; top: 0; }
            """);

        fixture.Document.Create("div", fixture.Document.Root, "lead");
        var here = fixture.Document.Create("div", fixture.Document.Root, "here");
        fixture.Update();

        Assert.Equal(50f, here.AbsoluteTop, 1);
    }
}
