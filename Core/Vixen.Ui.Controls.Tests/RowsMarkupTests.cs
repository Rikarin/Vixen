// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>@rows</c> over a real <c>VirtualizingPanel</c>, through the source generator.</summary>
/// <remarks>
///     <para>
///         <b><c>PooledListTests</c>' three claims, with the row template written as markup.</b> That
///         file drives <c>BuildContext.Pool</c> from <c>@code</c>; this one drives nothing — the
///         <c>.vxml</c> says <c>@rows</c>, the generator compiles it to the same call, and the panel
///         fills itself. So these are the claims #758's markup spelling has to keep: a row per slot
///         and not per item, a scroll that rebinds without rebuilding, and a count that stays live.
///     </para>
///     <para>
///         ⚠ <b>Counted in elements rather than in body runs.</b> A counter bumped from an attribute
///         expression was the first draft, and it lagged: an expression in markup is an effect, and a
///         slot the panel makes in <c>LayoutFinished</c> has not flushed its effects by the end of that
///         pass. The element instances are not an effect — the same dozen before and after a scroll is
///         exactly the claim that no row was rebuilt.
///     </para>
///     <para>
///         ⚠ The text is on the row's child, because an interpolation in markup is a text node —
///         which is why the assertions read <see cref="Label" /> rather than <c>Row.Text</c>.
///     </para>
/// </remarks>
public class RowsMarkupTests {
    const int Items = 10_000;

    const string Css = "virtualizing-panel { width: 300px; height: 200px; --row-height: 20px; }";

    [Fact]
    public void A_markup_row_template_builds_one_row_per_slot_and_not_one_per_item() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        Assert.Equal(Items, sheet.List.Count);
        Assert.InRange(sheet.List.Rows.Count, 10, 20);

        // Ten thousand items and a dozen rows, and every element in the scroller is one of them.
        Assert.Equal(sheet.List.Rows.Count, sheet.List.Scroller.Content.Children.Count);
        Assert.All(sheet.List.Rows, row => Assert.Equal("virtual-row", row.Tag));
        Assert.All(sheet.List.Rows, row => Assert.True(row.HasClass("line")));

        Assert.Equal("row 0", Label(sheet.List.Rows[0]));
        Assert.Equal("row 1", Label(sheet.List.Rows[1]));
    }

    [Fact]
    public void Scrolling_rebinds_a_markup_row_without_rebuilding_it() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        var before = sheet.List.Rows.ToArray();

        sheet.Bound.Clear();
        sheet.List.Scroller.ScrollTop = 2_000f;

        // Two passes, for `PooledListTests.Scrolling_rebinds_a_slot_without_rebuilding_it`'s reason:
        // the panel binds from `LayoutFinished`, so the bindings that read the write run next pass.
        fixture.Update();
        fixture.Update();

        var top = 100 - VirtualizingPanel.Overscan;

        // The same elements, now showing other items: rebound, not rebuilt.
        Assert.Equal(before, sheet.List.Rows);
        Assert.Equal(top, sheet.List.FirstItem);
        Assert.Contains(100, sheet.Bound);
        Assert.Equal("row " + top, Label(sheet.List.Rows[0]));
    }

    [Fact]
    public void A_markup_row_template_follows_a_count_that_changes() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        var before = sheet.List.Rows.ToArray();

        sheet.Items.Value = [.. Enumerable.Range(0, 5).Select(i => "short " + i)];
        fixture.Update();
        fixture.Update();

        Assert.Equal(5, sheet.List.Count);
        Assert.Equal("short 0", Label(sheet.List.Rows[0]));

        // The pool was re-used rather than re-made: the count is its own effect.
        Assert.Equal(before, sheet.List.Rows);
    }

    /// <summary>What a row says — the text of the node its interpolation made.</summary>
    static string Label(UiElement row) => row.Children.Single().Text ?? string.Empty;

    static RowsSheet Sheet(ControlFixture fixture) {
        var sheet = new RowsSheet();

        sheet.Items.Value = [.. Enumerable.Range(0, Items).Select(i => "row " + i)];
        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);

        // Two passes: the panel grows its pool in `LayoutFinished`, after the pass that measured it.
        fixture.Update();
        fixture.Update();

        return sheet;
    }
}
