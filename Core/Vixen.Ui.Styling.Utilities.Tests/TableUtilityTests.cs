// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The table utilities: what they emit, and that a table written in classes lays out.</summary>
/// <remarks>
///     ⚠ <b>The last fact is the one the others cannot stand in for.</b> <c>border-spacing</c> is an
///     assembly of two <c>var()</c> fragments, so an emission test proves the text and nothing about
///     whether the substituted pair reaches <c>LayoutStyleBuilder</c> as two lengths. The document
///     answers that with numbers no single fragment could produce.
/// </remarks>
public class TableUtilityTests {
    [Fact]
    public void The_table_family_is_display_bare_and_table_layout_with_a_value() {
        var fixture = new UtilityFixture();

        Assert.Equal(["display: table"], fixture.Emits("table"));
        Assert.Equal(["table-layout: fixed"], fixture.Emits("table-fixed"));
        Assert.Equal(["table-layout: auto"], fixture.Emits("table-auto"));
        Assert.Equal(["display: table-row"], fixture.Emits("table-row"));
        Assert.Equal(["display: table-cell"], fixture.Emits("table-cell"));
    }

    /// <summary>Each spacing family sets its fragments and emits the one assembly beside them.</summary>
    [Fact]
    public void Border_spacing_is_two_fragments_and_one_assembly() {
        var fixture = new UtilityFixture();
        var assembly = $"border-spacing: {UtilityComposition.BorderSpacing()}";

        Assert.Equal(["--tw-border-spacing-x: 8px", "--tw-border-spacing-y: 8px", assembly], fixture.Emits("border-spacing-2"));
        Assert.Equal(["--tw-border-spacing-x: 1px", assembly], fixture.Emits("border-spacing-x-px"));
        Assert.Equal(["--tw-border-spacing-y: 16px", assembly], fixture.Emits("border-spacing-y-4"));
    }

    /// <summary>v4's grammar: no keyword and no fraction, because neither is a length the property takes.</summary>
    [Theory]
    [InlineData("border-spacing-auto")]
    [InlineData("border-spacing-1/2")]
    [InlineData("border-spacing-full")]
    public void A_value_border_spacing_cannot_take_is_not_a_class(string candidate) {
        Assert.Null(new UtilityFixture().Declarations(candidate));
    }

    /// <summary>
    ///     A fixed table written entirely in classes: §17.5.2.1 and §17.6.1 with 8 across and 16 down.
    /// </summary>
    /// <remarks>
    ///     200 wide, three gutters of 8: 176 for two columns, the first stated at 40, so the second is
    ///     136 and starts 48 into the row. One row of a 20-tall cell: 16 + 20 + 16 = 52. ⚠ A lone
    ///     <c>border-spacing-x-2</c> would give 8 across and 0 down; a lone <c>-y-4</c> 0 and 16 — so
    ///     the pair is only produced by the two fragments composing in one assembly.
    /// </remarks>
    [Fact]
    public void A_table_written_in_classes_lays_out_with_its_spacing() {
        string[] table = ["table", "table-fixed", "border-spacing-x-2", "border-spacing-y-4"];
        string[] row = ["table-row"];
        string[] cell = ["table-cell"];

        using var document = new UiDocument(400f, 300f);
        document.Load(new UtilityGenerator(AssembledReaderProbe.Tokens).Generate([.. table, .. row, .. cell]), StyleOrigin.Author);
        document.Load(
            """
            root { flex-direction: row; align-items: flex-start; }
            #t { width: 200px; }
            #a { width: 40px; height: 20px; }
            """,
            StyleOrigin.Author
        );

        var t = document.Create("div", document.Root, "t", table);
        var r = document.Create("div", t, null, row);
        var a = document.Create("div", r, "a", cell);
        var b = document.Create("div", r, null, cell);
        document.Update();

        Assert.Equal(200f, t.Width, 0.001f);
        Assert.Equal(52f, t.Height, 0.001f);
        Assert.Equal((8f, 16f), (r.Left, r.Top));
        Assert.Equal(40f, a.Width, 0.001f);
        Assert.Equal((48f, 136f, 20f), (b.Left, b.Width, b.Height));
    }
}
