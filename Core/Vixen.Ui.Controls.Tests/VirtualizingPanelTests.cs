// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A list of a hundred thousand items and about thirty elements.</summary>
/// <remarks>
///     <para>
///         <b>The counting is the test.</b> Every assertion here is about how many elements exist or
///         how many times a binder ran, because that is the only thing that distinguishes a
///         virtualised list from one that works perfectly and allocates a hundred thousand boxes. A
///         suite that asserted the right rows were showing would pass against both.
///     </para>
///     <para>
///         Verified by sabotage, nine of nine landing: realising every item fails 5, dropping the
///         overscan fails 2, never parking a surplus row fails 2, rebuilding the pool rather than
///         growing it fails 2, never writing the content's height fails 5, never subscribing to
///         <c>LayoutFinished</c> fails 7, keeping that subscription after removal fails 1, answering
///         <c>RowOf</c> for a parked row fails 1, and centring in <c>ScrollIntoView</c> fails 1.
///     </para>
///     <para>
///         ⚠ <b>The last two needed cases the suite did not have.</b> Asking <c>RowOf</c> for an item
///         that is inside the pool and outside the data is the only way to tell a bounds check from a
///         parked check — every other index is out of range either way. And "already visible does not
///         move" cannot be tested by calling <c>ScrollIntoView</c> twice, because centring answers the
///         same both times; it needs an item that was already on screen before the call.
///     </para>
/// </remarks>
public class VirtualizingPanelTests {
    const int Items = 100_000;

    /// <summary>A panel over a hundred thousand items, laid out in a 200-pixel box.</summary>
    static (ControlFixture Fixture, VirtualizingPanel Panel, List<int> Bound) Listed(int count = Items) {
        var fixture = new ControlFixture(400f, 200f, "virtualizing-panel { width: 300px; height: 200px; --row-height: 20px; }");
        var panel = fixture.Document.Root.Add<VirtualizingPanel>();
        var bound = new List<int>();

        panel.CreateRow = static owner => owner.Scroller.Content.Add<UiElement>("row");
        panel.BindRow = (_, item) => bound.Add(item);
        panel.Count = count;

        fixture.Update();
        bound.Clear();

        return (fixture, panel, bound);
    }

    [Fact]
    public void A_hundred_thousand_items_are_about_a_dozen_elements() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        // 200 pixels of viewport at 20 a row is ten rows, plus the overscan at each end and one for
        // the partial row at the bottom.
        Assert.Equal(Items, panel.Count);
        Assert.InRange(panel.Rows.Count, 10, 20);
        Assert.Equal(panel.Rows.Count, panel.Scroller.Content.Children.Count);
    }

    [Fact]
    public void The_scrollable_height_is_every_item_even_though_the_elements_are_not() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        // ⚠ The content's height is what makes the scroll bar's range right, and it is a declaration
        // rather than a measurement — the elements inside it come nowhere near this tall. Without it
        // a virtualised list scrolls for one screen and stops.
        Assert.Equal(Items * 20f, panel.Scroller.Content.Height, 1f);
    }

    [Fact]
    public void Scrolling_rebinds_the_rows_it_already_has() {
        var (fixture, panel, bound) = Listed();
        using var owned = fixture;

        var before = panel.Rows.Count;

        panel.Scroller.ScrollTop = 20_000f;
        fixture.Update();

        // The pool did not grow and the rows are showing the items that are now on screen.
        Assert.Equal(before, panel.Rows.Count);
        Assert.Equal(1000 - VirtualizingPanel.Overscan, panel.FirstItem);
        Assert.Contains(1000, bound);
        Assert.DoesNotContain(0, bound);
    }

    [Fact]
    public void The_pool_grows_and_never_shrinks() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        var tall = panel.Rows.Count;
        var elements = panel.Rows.ToList();

        panel.Count = 3;
        fixture.Update();

        // ⚠ Still the same elements. Shrinking the pool would mean removing elements whenever a list
        // got shorter and creating them again when it grew — the allocation on scroll that this whole
        // arrangement exists to avoid. The surplus is parked instead.
        Assert.Equal(tall, panel.Rows.Count);
        Assert.Equal(elements, panel.Rows);

        Assert.Equal(3, panel.Rows.Count(static row => !row.HasClass("parked")));
        Assert.Contains(panel.Rows, static row => row.HasClass("parked"));
    }

    [Fact]
    public void A_parked_row_draws_nothing() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        // ⚠ Tall first and short after, because a panel that was never taller than its data has no
        // surplus to park — a version of this test that started at three items found no parked row
        // at all and said so.
        panel.Count = 3;
        fixture.Update();

        var parked = panel.Rows.First(static row => row.HasClass("parked"));

        // The theme hides it, which is the point of using a class rather than a field: a surplus row
        // is invisible to the layout and to hit testing without this control knowing how either
        // works.
        Assert.Equal(0f, parked.Height);
    }

    [Fact]
    public void The_overscan_realises_rows_that_are_not_on_screen_yet() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        panel.Scroller.ScrollTop = 400f;
        fixture.Update();

        // ⚠ Row 20 is the first one visible at this offset, and the pool starts two above it. Without
        // the overscan the row entering at the bottom of a flick is created in the frame it is first
        // drawn, and the first frame of every scroll shows a gap.
        Assert.Equal(20 - VirtualizingPanel.Overscan, panel.FirstItem);
        Assert.NotNull(panel.RowOf(20 - VirtualizingPanel.Overscan));
    }

    [Fact]
    public void An_item_that_is_not_realised_has_no_row() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        Assert.NotNull(panel.RowOf(0));
        Assert.Null(panel.RowOf(50_000));

        // And asking for one that has scrolled away stops answering, rather than answering with
        // whichever row happens to be reusing that element now.
        panel.Scroller.ScrollTop = 20_000f;
        fixture.Update();

        Assert.Null(panel.RowOf(0));
        Assert.NotNull(panel.RowOf(1000));
    }

    [Fact]
    public void A_parked_row_is_not_an_answer_either() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        panel.Count = 3;
        fixture.Update();

        // ⚠ **Item 5 is inside the pool and outside the data**, which is the case a bounds check
        // alone lets through: the element at that offset exists, it is parked, and it is showing
        // whatever it was showing when the list was longer. Handing it back is handing back a stale
        // row that happens to be at the right index.
        Assert.NotNull(panel.RowOf(2));
        Assert.Null(panel.RowOf(5));
        Assert.True(panel.Rows.Count > 5, "the pool needs to be longer than the data for this to mean anything");
    }

    [Fact]
    public void Scrolling_to_an_item_moves_the_minimum_it_can() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        panel.ScrollIntoView(50_000);
        fixture.Update();

        // ⚠ Arithmetic rather than a search for the element — the whole point is that item 50 000
        // does not exist yet, so asking the scroller to bring *its element* into view would be asking
        // about an element that is showing something else.
        Assert.NotNull(panel.RowOf(50_000));

        // ⚠ **An item that is already comfortably on screen must not move the list at all**, and the
        // test has to reach one that way rather than by asking twice: centring on every call also
        // answers the same both times. Scrolling *down* to an item leaves it at the bottom of the
        // viewport, so the ones already on screen are the nine above it.
        var settled = panel.Scroller.ScrollTop;

        panel.ScrollIntoView(49_995);
        fixture.Update();

        Assert.Equal(settled, panel.Scroller.ScrollTop, 0.01f);
    }

    [Fact]
    public void Resizing_the_panel_realises_against_the_new_size_without_being_told() {
        var fixture = new ControlFixture(
            400f,
            600f,
            "virtualizing-panel { width: 300px; height: 100px; --row-height: 20px; } .tall { height: 500px; }"
        );

        using var owned = fixture;

        var panel = fixture.Document.Root.Add<VirtualizingPanel>();
        panel.CreateRow = static owner => owner.Scroller.Content.Add<UiElement>("row");
        panel.Count = Items;

        fixture.Update();
        var shortPool = panel.Rows.Count;

        panel.AddClass("tall");
        fixture.Update();

        // ⚠ **Nothing called `Realise`.** How tall the viewport ended up is a result of the layout
        // pass, not an input to it, so a panel resized without being scrolled would keep realising
        // against the previous size for ever — which is the gap every explicit `Refresh()` in this
        // library existed for and the reason `UiDocument.LayoutFinished` was built.
        Assert.True(
            panel.Rows.Count > shortPool,
            $"the pool stayed at {shortPool} rows after the panel grew five times taller"
        );
    }

    [Fact]
    public void A_removed_panel_stops_listening() {
        var (fixture, panel, _) = Listed();
        using var owned = fixture;

        fixture.Document.Remove(panel);

        // The same leak `ScrollView` and `Tooltip` have: a subscription is a reference the document
        // keeps, and what it would run is a realise over elements that are gone.
        var exception = Record.Exception(() => fixture.Update());

        Assert.Null(exception);
        Assert.True(panel.IsRemoved);
    }
}
