// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     <c>table-layout</c> and <c>border-spacing</c> across the bridge, and a table in a live document.
/// </summary>
/// <remarks>
///     ⚠ <b>Written through a stylesheet, not inline</b>, so that ExCSS is part of what is measured:
///     a property ExCSS dropped while parsing would leave the initial value here, which is exactly the
///     "parsed, cascaded and moved nothing" shape doc 43 keeps finding. The invalid values are the
///     exception, and are inline for <see cref="BridgeFixture.BuildInline" />'s reason.
/// </remarks>
public class TableBridgeTests {
    const float Tolerance = 0.001f;

    [Theory]
    [InlineData("table-layout: fixed", TableLayout.Fixed)]
    [InlineData("table-layout: auto", TableLayout.Auto)]
    [InlineData("", TableLayout.Auto)]
    public void Table_layout_reaches_the_layout_style(string declaration, TableLayout expected) {
        var style = new BridgeFixture().Build(declaration);

        Assert.Equal(expected, style.TableLayout);
    }

    /// <summary>One length is both axes; two are horizontal, then vertical — CSS 2.1 §17.6.1.</summary>
    [Theory]
    [InlineData("border-spacing: 4px", 4f, 4f)]
    [InlineData("border-spacing: 4px 8px", 4f, 8f)]
    [InlineData("border-spacing: 0", 0f, 0f)]
    [InlineData("border-spacing: 1em 2em", 16f, 32f)]
    public void Border_spacing_reaches_the_layout_style(string declaration, float horizontal, float vertical) {
        var style = new BridgeFixture().Build(declaration, LengthContext.ForViewport(1000f, 500f).WithFontSize(16f));

        Assert.Equal(StyleLength.Points(horizontal), style.BorderSpacing[(int)Dimension.Width]);
        Assert.Equal(StyleLength.Points(vertical), style.BorderSpacing[(int)Dimension.Height]);
    }

    /// <summary>A value outside the grammar writes nothing, rather than a zero or half a pair.</summary>
    [Theory]
    [InlineData("-2px")]
    [InlineData("10%")]
    [InlineData("4px -1px")]
    [InlineData("1px 2px 3px")]
    [InlineData("auto")]
    public void An_invalid_border_spacing_writes_nothing(string value) {
        var declared = new BridgeFixture().BuildInline(("border-spacing", value));
        var undeclared = new BridgeFixture().BuildInline();

        Assert.Equal(undeclared.BorderSpacing[(int)Dimension.Width], declared.BorderSpacing[(int)Dimension.Width]);
        Assert.Equal(undeclared.BorderSpacing[(int)Dimension.Height], declared.BorderSpacing[(int)Dimension.Height]);
    }

    /// <summary>
    ///     A fixed table written in CSS lays out as CSS 2.1 §17.5.2.1 says: the closed form of
    ///     <c>TableLayoutTests.A_fixed_table_takes_its_columns_from_the_first_row</c>, end to end.
    /// </summary>
    /// <remarks>
    ///     ⚠ The root is a row flex container, so the table is a flex item with no stretch to give it
    ///     the root's width: 300 is the table's own <c>width</c>, and 105 is §17.5.2.1's share of it —
    ///     (300 − 4 × 10 − 50) / 2. A bridge that dropped any of the four properties gives another
    ///     number: no <c>display</c> is a flex container, no <c>table-layout</c> shares by content, no
    ///     <c>border-spacing</c> makes it 125.
    /// </remarks>
    [Fact]
    public void A_fixed_table_in_a_document_shares_its_width_by_the_first_row() {
        using var document = new UiDocument(400f, 300f);
        document.Load(
            """
            root { width: 400px; height: 300px; flex-direction: row; align-items: flex-start; }
            .t { display: table; table-layout: fixed; width: 300px; border-spacing: 10px; }
            .r { display: table-row; }
            .c { display: table-cell; }
            .narrow { width: 50px; }
            .tall { height: 40px; }
            """
        );

        var table = document.Root.Add("div", classNames: "t");
        var row = table.Add("div", classNames: "r");
        var a = row.Add("div", null, "c", "narrow");
        var b = row.Add("div", null, "c", "tall");
        var c = row.Add("div", classNames: "c");
        document.Update();

        Assert.Equal(300f, table.Width, Tolerance);
        Assert.Equal(60f, table.Height, Tolerance);

        Assert.Equal(50f, a.Width, Tolerance);
        Assert.Equal(105f, b.Width, Tolerance);
        Assert.Equal(60f, b.Left, Tolerance);
        Assert.Equal(175f, c.Left, Tolerance);

        // The row's height is its tallest cell's, and every cell is that tall.
        Assert.Equal(40f, a.Height, Tolerance);
        Assert.Equal(40f, c.Height, Tolerance);
    }
}
