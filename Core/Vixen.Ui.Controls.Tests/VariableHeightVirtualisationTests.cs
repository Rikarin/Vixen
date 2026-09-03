// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A list whose rows are not all the same height, and still about a dozen elements.</summary>
/// <remarks>
///     <para>
///         <b>The oracle is closed form rather than eyeballed.</b> Every fixture here alternates two
///         heights, so where item <c>i</c> starts and how tall the whole list is are both expressions
///         a test can write down — and a running-sum index that is subtly wrong disagrees with them
///         at some index rather than looking plausible. A suite that only asserted "the rows are in
///         order" would pass against a list that put every row at <c>i × 22</c>.
///     </para>
///     <para>
///         ⚠ <b>Counting elements is still the other half.</b> The point of the index is that a
///         hundred thousand variable rows cost the same as a hundred thousand uniform ones; an
///         implementation that walked the list to find the offset would pass every arithmetic
///         assertion below and be quadratic. <see cref="A_hundred_thousand_variable_rows_are_still_a
///         _dozen_elements" /> is what says otherwise, and it says it by counting rather than by
///         timing — a clock here would be the flake this repository already knows about.
///     </para>
///     <para>
///         ⚠ <b><see cref="Measuring_settles" /> is the anti-loop test.</b> A virtualiser whose
///         heights depend on a layout whose heights depend on the virtualiser can ask for another
///         pass for ever, which draws a frame one pass stale and shows up as nothing at all except
///         <c>UiDocument.Settled</c> being false. It is the same claim <c>ResizeTests</c> makes for
///         the uniform path.
///     </para>
/// </remarks>
public class VariableHeightVirtualisationTests {
    const float Short = 10f;
    const float Tall = 30f;

    /// <summary>Even items are short and odd ones are tall, so a pair is 40 pixels.</summary>
    static float Height(int item) => item % 2 == 0 ? Short : Tall;

    /// <summary>Where item <c>i</c> starts, worked out rather than asked.</summary>
    static float Expected(int item) => ((item / 2) * (Short + Tall)) + (item % 2 == 0 ? 0f : Short);

    /// <summary>A panel whose heights the caller states, which needs no measurement and no estimate.</summary>
    static (ControlFixture Fixture, VirtualizingPanel Panel) Stated(int count) {
        var fixture = new ControlFixture(
            400f,
            200f,
            "virtualizing-panel { width: 300px; height: 200px; --row-height: 20px; }"
        );

        var panel = fixture.Document.Root.Add<VirtualizingPanel>();

        panel.CreateRow = static owner => owner.Scroller.Content.Add<UiElement>("row");
        panel.Count = count;

        for (var item = 0; item < count; item++) {
            panel.SetRowHeight(item, Height(item));
        }

        fixture.Update();

        return (fixture, panel);
    }

    /// <summary>A panel that finds out how tall its rows are by looking at them.</summary>
    static (ControlFixture Fixture, VirtualizingPanel Panel) Measured(int count) {
        var fixture = new ControlFixture(
            400f,
            200f,
            """
            virtualizing-panel { width: 300px; height: 200px; --row-height: 20px; }
            .short { height: 10px; }
            .tall  { height: 30px; }
            """
        );

        var panel = fixture.Document.Root.Add<VirtualizingPanel>();

        panel.MeasureRows = true;
        panel.CreateRow = static owner => owner.Scroller.Content.Add<UiElement>("row");

        panel.BindRow = static (row, item) => {
            row.RemoveClass(item % 2 == 0 ? "tall" : "short");
            row.AddClass(item % 2 == 0 ? "short" : "tall");
        };

        panel.Count = count;

        // Three passes: one to place the rows on the estimate, one to read back what they turned out
        // to be, one to settle on the answer.
        fixture.Update();
        fixture.Update();
        fixture.Update();

        return (fixture, panel);
    }

    /// <summary>The offsets are the running sum, at every index rather than at a chosen one.</summary>
    [Fact]
    public void An_items_offset_is_every_height_above_it_added_up() {
        var (fixture, panel) = Stated(200);
        using var owned = fixture;

        for (var item = 0; item < 200; item++) {
            Assert.Equal(Expected(item), panel.OffsetOf(item), 0.01f);
            Assert.Equal(Height(item), panel.HeightOf(item), 0.01f);
        }

        // ⚠ And the total, which is the scroll range. A list whose offsets are right and whose total
        // is the count times the estimate scrolls to two thirds of itself and stops.
        Assert.Equal(100f * (Short + Tall), panel.TotalHeight, 0.01f);
        Assert.Equal(panel.TotalHeight, panel.Scroller.Content.Height, 1f);
    }

    /// <summary>And the inverse agrees with it, which is what a scroll offset is turned into.</summary>
    /// <remarks>
    ///     ⚠ <b>Both edges of each row, because the interesting failures are off by one.</b> An index
    ///     that answered the row above at every boundary would put the list one row out for exactly
    ///     the offsets a scroll bar comes to rest on.
    /// </remarks>
    [Fact]
    public void The_item_at_an_offset_is_the_one_whose_run_covers_it() {
        var (fixture, panel) = Stated(200);
        using var owned = fixture;

        for (var item = 0; item < 200; item++) {
            Assert.Equal(item, panel.ItemAt(Expected(item)));
            Assert.Equal(item, panel.ItemAt(Expected(item) + Height(item) - 0.5f));
        }
    }

    /// <summary>Scrolling puts the rows where the index says, not where a fixed height would.</summary>
    [Fact]
    public void Scrolling_realises_the_items_the_running_sum_names() {
        var (fixture, panel) = Stated(200);
        using var owned = fixture;

        // Item 100 starts at 2 000, which a uniform list of 20-pixel rows would call item 100 as well
        // — so the offset is chosen to be one the two models disagree about at the *rows*: the
        // uniform model puts 20-pixel rows there and this one alternates 10 and 30.
        panel.Scroller.ScrollTop = 2_000f;
        fixture.Update();

        Assert.Equal(100 - VirtualizingPanel.Overscan, panel.FirstItem);

        var row = panel.RowOf(100);

        Assert.NotNull(row);
        Assert.Equal(Expected(100), row.Top, 1f);
    }

    /// <summary>A hundred thousand variable rows are still about a dozen elements.</summary>
    [Fact]
    public void A_hundred_thousand_variable_rows_are_still_a_dozen_elements() {
        var (fixture, panel) = Stated(100_000);
        using var owned = fixture;

        Assert.Equal(50_000f * (Short + Tall), panel.TotalHeight, 1f);

        // 200 pixels of viewport over rows averaging 20 is ten of them, plus the overscan at each end.
        Assert.InRange(panel.Rows.Count, 10, 30);
        Assert.Equal(panel.Rows.Count, panel.Scroller.Content.Children.Count);

        panel.Scroller.ScrollTop = 1_000_000f;
        fixture.Update();

        Assert.InRange(panel.Rows.Count, 10, 30);
        Assert.Equal(50_000, panel.ItemAt(1_000_000f));
    }

    /// <summary>Forgetting the heights makes it a uniform list again.</summary>
    /// <remarks>
    ///     ⚠ <b>Which is what a list whose data changed wholesale needs.</b> Heights are per item, so
    ///     replacing the items keeps the old list's sizes against the new one's indices — a list that
    ///     looks subtly wrong rather than obviously stale.
    /// </remarks>
    [Fact]
    public void Clearing_the_heights_puts_every_row_back_on_the_estimate() {
        var (fixture, panel) = Stated(200);
        using var owned = fixture;

        panel.ClearRowHeights();
        fixture.Update();

        Assert.Equal(20f, panel.HeightOf(1), 0.01f);
        Assert.Equal(40f, panel.OffsetOf(2), 0.01f);
        Assert.Equal(200f * 20f, panel.TotalHeight, 0.01f);
    }

    /// <summary>A measuring panel learns what its rows turned out to be.</summary>
    /// <remarks>
    ///     ⚠ <b>The estimate is 20 and neither real height is</b>, so an index that quietly kept the
    ///     estimate would answer 20 for both and fail on either. That is the point of choosing 10 and
    ///     30: a panel that measured nothing and a panel that measured everything agree on the
    ///     *average*, and only the individual answers separate them.
    /// </remarks>
    [Fact]
    public void Measuring_learns_each_rows_own_height() {
        var (fixture, panel) = Measured(200);
        using var owned = fixture;

        for (var item = 0; item < panel.Rows.Count && item < 10; item++) {
            Assert.Equal(Height(item), panel.HeightOf(item), 0.01f);
            Assert.Equal(Expected(item), panel.OffsetOf(item), 0.01f);
        }
    }

    /// <summary>And it stops asking for another pass, which is the loop this arrangement can make.</summary>
    [Fact]
    public void Measuring_settles() {
        var (fixture, panel) = Measured(200);
        using var owned = fixture;

        fixture.Update();

        Assert.True(
            fixture.Document.Settled,
            "the panel asked for another layout pass after it had measured, so it never settles"
        );

        Assert.True(panel.Rows.Count > 0);
    }

    /// <summary>Learning how tall the rows above are does not drag the list out from under the reader.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the half a correction gets wrong, and the symptom is the list fighting the
    ///         wheel.</b> Every row here turns out to be ten times the estimate, so the moment the two
    ///         rows of overscan above the viewport are measured, everything below them moves down 360
    ///         pixels — on a frame the reader did nothing. A panel that wrote the new offsets without
    ///         compensating its own scroll would show them somewhere else in the list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first draft of this test did not catch it, and removing the compensation left
    ///         it green.</b> Its rows were half the estimate rather than ten times it, so the shift was
    ///         twenty pixels — two rows, and inside the tolerance of the assertion. The fixture was
    ///         rebuilt rather than the claim weakened: what makes an anchoring test mean anything is a
    ///         measurement that moves the content by much more than one row.
    ///     </para>
    ///     <para>
    ///         The assertion is on the <i>item</i> rather than on the offset, which is what "the same
    ///         place" means once the offsets underneath have changed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_measurement_anchors_the_scroll_on_the_item_that_was_at_the_top() {
        var fixture = new ControlFixture(
            400f,
            200f,
            """
            virtualizing-panel { width: 300px; height: 200px; --row-height: 20px; }
            .row { height: 200px; }
            """
        );

        using var owned = fixture;

        var panel = fixture.Document.Root.Add<VirtualizingPanel>();

        panel.MeasureRows = true;
        panel.CreateRow = static owner => owner.Scroller.Content.Add<UiElement>("row");
        panel.BindRow = static (row, _) => row.AddClass("row");
        panel.Count = 4_000;

        fixture.Update();

        // Item 1 000 on the estimate, which is what a reader scrolling into an unmeasured list is
        // looking at. Nothing below the first screen has been measured yet.
        panel.Scroller.ScrollTop = 20_000f;
        fixture.Update();

        var anchor = panel.ItemAt(panel.Scroller.ScrollTop);

        Assert.True(anchor > 0, "the fixture did not scroll, so there is nothing above the viewport to move");

        fixture.Update();
        fixture.Update();

        Assert.Equal(anchor, panel.ItemAt(panel.Scroller.ScrollTop));
    }
}
