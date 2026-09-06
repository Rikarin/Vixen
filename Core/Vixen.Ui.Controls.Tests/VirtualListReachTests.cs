// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>How far a `.vxml` reaches `VirtualizingPanel` today, measured rather than asserted.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#758 says the escape hatch is `use=` and a pair of lambdas, and three audits have
///         repeated it without anybody running one.</b> No `.vxml` in the tree had ever taken that
///         route — which is what the issue's own corrected "done looks like" says: there is no
///         `use=` block to delete because none was ever written. So this writes one.
///         `Markup/VirtualListSheet.vxml` is a markup panel over ten thousand items, and it
///         virtualises.
///     </para>
///     <para>
///         <b>Which narrows the issue rather than closing it.</b> The gap is not reach — the control
///         is reachable, and now demonstrably. The gap is that a row <i>template</i> has no markup
///         construct: `CreateRow` builds an element tree in C# and `BindRow` writes it by index, and
///         both live in `@code` in a file whose whole subject is the tree. That is ergonomics, and
///         ergonomics is what the `@rows` block in the issue would buy.
///     </para>
///     <para>
///         ⚠ <b>Asserted by counting elements, per the issue's own criterion</b> — never by a
///         screenshot and never by elapsed time. Ten thousand items against a pool of about a dozen
///         is the only thing that distinguishes a virtualised list from a correct one that allocated
///         ten thousand boxes.
///     </para>
/// </remarks>
public class VirtualListReachTests {
    const int Items = 10_000;

    /// <summary>The viewport and the row height, so the pool's size is arithmetic rather than luck.</summary>
    const string Css = "virtualizing-panel { width: 300px; height: 200px; --row-height: 20px; }";

    [Fact]
    public void A_markup_panel_over_ten_thousand_items_builds_about_a_dozen_rows() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        // The whole claim: the control knows about all of them and the document holds a pool.
        Assert.Equal(Items, sheet.List.Count);
        Assert.InRange(sheet.List.Rows.Count, 10, 20);
        Assert.Equal(sheet.List.Rows.Count, sheet.List.Scroller.Content.Children.Count);

        // And the rows it did build are showing the items they should.
        Assert.Equal("row 0", sheet.List.Rows[0].Text);
    }

    /// <summary>Scrolling rebinds the rows it already has rather than making more.</summary>
    /// <remarks>
    ///     ⚠ Counted, not timed: <c>Bound</c> is every index a row was written with, so "the pool
    ///     was reused" is a fact about a list rather than about how long anything took.
    /// </remarks>
    [Fact]
    public void Scrolling_a_markup_panel_rebinds_the_pool_instead_of_growing_it() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        var pool = sheet.List.Rows.Count;

        sheet.Bound.Clear();
        sheet.List.Scroller.ScrollTop = 2_000f;
        fixture.Update();

        // ⚠ 2 000 pixels at 20 a row is item 100, and the pool starts `Overscan` rows above it —
        // written as the constant rather than as 98, because 98 is a number that tells the next
        // reader nothing and would have to be re-derived if the slack ever changed.
        var top = 100 - VirtualizingPanel.Overscan;

        Assert.Equal(pool, sheet.List.Rows.Count);
        Assert.Equal(top, sheet.List.FirstItem);
        Assert.Contains(100, sheet.Bound);
        Assert.Equal("row " + top, sheet.List.Rows[0].Text);
    }

    /// <summary>
    ///     ⚠ <b>The instrument.</b> Nothing above distinguishes "the markup reached the control" from
    ///     "the control does this by itself": a panel nobody filled has a count of zero and no rows,
    ///     so every assertion above is about the `use=` having run.
    /// </summary>
    [Fact]
    public void The_same_tag_without_the_use_block_lists_nothing() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var panel = fixture.Document.Root.Add<VirtualizingPanel>();

        fixture.Update();

        Assert.Equal(0, panel.Count);
        Assert.Empty(panel.Rows);
    }

    static VirtualListSheet Sheet(ControlFixture fixture) {
        var sheet = new VirtualListSheet { Items = [.. Enumerable.Range(0, Items).Select(i => "row " + i)] };

        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        return sheet;
    }
}
