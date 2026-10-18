// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A wrapping text with a zero flex basis, inside a column sized to its content (#1412).</summary>
/// <remarks>
///     <para>
///         The mixer's side column without the editor: a column that is <c>align-self: flex-start</c>
///         in a 300 px parent with <c>min-width: 100%</c> — a <c>scroll-content</c>'s rules — holding a
///         list, a row, a 72 px label and a <c>flex: 1 0 0</c> message. Its content was 88 px wide,
///         because the message's zero basis contributed nothing, and the minimum made it 300.
///     </para>
///     <para>
///         ⚠ <b>Since #1440 the message's content counts</b> — a growable item's max-content
///         contribution is its content — and since #1448 a row offered "at most" a width is capped
///         there, so the column is 300 from its content and the offer before the minimum is read. The
///         two tests below that assert 300 still hold; what they guard is now reached by a different
///         road, and the #1412 re-entry no longer fires for this tree.
///     </para>
///     <para>
///         ⚠ <b>The cause was not the zero basis and not the max-content pass</b>, which is what the
///         issue suspected. The column's items were measured at the column's CONTENT width, before
///         <c>min-width</c> raised it; the row was then stretched to 300 and laid out again, but the
///         list kept the height it had been measured at. <c>flex-basis: 0</c> is only what made the
///         content width small enough for the difference to show.
///     </para>
/// </remarks>
public class ZeroBasisWrapHeightTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    const string Message = "19 bus(es), 0 snapshot(s). Built without errors.";

    static (UiDocument Document, UiElement Content, UiElement List, UiElement Row, UiElement Stage, UiElement Text) Build(string extra = "") {
        var document = new UiDocument(900f, 600f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root    { width: 800px; height: 600px; flex-direction: row; align-items: flex-start; }
              side    { flex-direction: column; width: 300px; height: 500px; overflow: hidden; }
              content { flex-direction: column; flex-shrink: 0; align-self: flex-start; min-width: 100%; }
              list    { flex-direction: column; gap: 2px; }
              row     { flex-direction: row; align-items: center; gap: 8px; padding: 0px 4px; }
              stage   { width: 72px; flex-shrink: 0; }
              message { flex-grow: 1; min-width: 0px; flex-basis: 0px; }
              stage, message { font-family: Test; font-size: 16px; line-height: 20px; }
              {{extra}}
              """
        );

        var side = document.Root.Add("side");
        var content = side.Add("content");
        var list = content.Add("list");
        var row = list.Add("row");
        var stage = row.Add("stage");
        stage.Text = "Build";
        var message = row.Add("message");
        message.Text = Message;

        document.Update();

        return (document, content, list, row, stage, message);
    }

    /// <summary>
    ///     ⚠ <b>The defect as filed, in the smallest tree that has it</b>: the column's width comes
    ///     from <c>min-width: 100%</c> and not from its content, and the list around one two-line row
    ///     was as tall as the message laid out at zero width — seven words, seven lines, 140 px.
    /// </summary>
    [Fact]
    public void A_column_widened_by_its_minimum_is_as_tall_as_its_row() {
        var (_, content, list, row, _, message) = Build();

        // The premise: the width is the minimum's, and at that width the message wraps once.
        Assert.Equal(300f, content.Bounds.Width, 0.5f);
        Assert.Equal(2, message.Block()!.Lines.Length);
        Assert.Equal(40f, row.Bounds.Height, 0.5f);

        Assert.Equal(row.Bounds.Height, list.Bounds.Height, 0.5f);
        Assert.Equal(row.Bounds.Height, content.Bounds.Height, 0.5f);
    }

    /// <summary>
    ///     The same column given its width outright is the oracle: CSS decides a block-level box's
    ///     width before its contents, so where that width came from cannot change what is inside it.
    /// </summary>
    [Fact]
    public void A_column_widened_by_its_minimum_lays_out_as_one_given_that_width() {
        var (_, _, widenedList, widenedRow, _, widenedMessage) = Build();
        var (_, _, givenList, givenRow, _, givenMessage) = Build("content { width: 300px; }");

        Assert.Equal(givenMessage.Bounds, widenedMessage.Bounds);
        Assert.Equal(givenRow.Bounds, widenedRow.Bounds);
        Assert.Equal(givenList.Bounds, widenedList.Bounds);
    }

    /// <summary>
    ///     ⚠ <b>With nothing for the minimum to widen, a growable zero-basis item still counts its
    ///     content (#1440).</b> CSS Flexbox §9.9.1 makes a growable item's max-content contribution its
    ///     content, and lets the flex base size clamp it only from above and only when the item is not
    ///     growable — so the column is as wide as the row's content allows within the 300 px it is
    ///     offered, and the message, back on its zero basis once that width is decided, grows into
    ///     what is left. Chrome 153 draws the content 300x40 and the message 212x40.
    /// </summary>
    /// <remarks>
    ///     Before the fix the row took the declared zero at face value under a fit-content offer, so
    ///     the column was 88 wide — the stage, the gap and the padding — and the message 0 wide and
    ///     seven lines tall. Only a max-content offer reached the content branch.
    /// </remarks>
    [Fact]
    public void A_growable_zero_basis_item_counts_its_content_under_a_fit_content_offer() {
        var (_, content, _, row, _, message) = Build("content { min-width: 0px; }");

        Assert.Equal(300f, content.Bounds.Width, 0.5f);
        Assert.Equal(40f, content.Bounds.Height, 0.5f);
        Assert.Equal(212f, message.Bounds.Width, 0.5f);
        Assert.Equal(40f, message.Bounds.Height, 0.5f);
        Assert.Equal(40f, row.Bounds.Height, 0.5f);
    }

    /// <summary>
    ///     ⚠ <b>A minimum below the offer no longer decides this column, because its content is wider
    ///     than the offer (#1440).</b> This test used to assert 300: offered 400 px with a 300 px
    ///     minimum, the column's content asked for 88 — the message's zero basis contributing
    ///     nothing — so the clamp decided. That 88 was this engine's reading and not CSS's: the message
    ///     is growable, so its content counts, the row's one-line width exceeds the offer, and
    ///     fit-content gives the offer. The column is 400 wide, as Chrome would draw it, and the list
    ///     still hugs its row — the #1412 property this file exists for.
    /// </summary>
    [Fact]
    public void A_minimum_below_the_offer_does_not_narrow_a_column_whose_content_is_wider() {
        var (_, content, list, row, _, message) = Build("side { width: 400px; } content { min-width: 300px; }");

        Assert.Equal(400f, content.Bounds.Width, 0.5f);
        Assert.Equal(312f, message.Bounds.Width, 0.5f);
        Assert.Equal(2, message.Block()!.Lines.Length);
        Assert.Equal(row.Bounds.Height, list.Bounds.Height, 0.5f);
        Assert.Equal(row.Bounds.Height, content.Bounds.Height, 0.5f);
    }
}
