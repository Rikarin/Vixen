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
///         list, a row, a 72 px label and a <c>flex: 1 0 0</c> message. Its content is 88 px wide,
///         because the message's zero basis contributes nothing, and the minimum makes it 300.
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
    ///     ⚠ <b>Not only a minimum equal to the space offered.</b> Here the column is offered 400 px,
    ///     its content asks for 88 and its minimum is 300, so the answer is decided by the clamp and
    ///     only after the content is measured — a fix that turned a minimum at least as large as the
    ///     offer into a fixed width before measuring would pass the test above and fail this one.
    /// </summary>
    [Fact]
    public void A_minimum_between_the_content_and_the_offer_decides_the_width_the_items_are_measured_at() {
        var (_, content, list, row, _, message) = Build("side { width: 400px; } content { min-width: 300px; }");

        Assert.Equal(300f, content.Bounds.Width, 0.5f);
        Assert.Equal(2, message.Block()!.Lines.Length);
        Assert.Equal(row.Bounds.Height, list.Bounds.Height, 0.5f);
    }
}
