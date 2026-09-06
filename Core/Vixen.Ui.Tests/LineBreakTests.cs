// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>CSS Text §5.2's <c>line-break</c>, from a declaration to a line.</summary>
/// <remarks>
///     <para>
///         <b>The half <c>Vixen.Ui.Text.Tests.CssLineBreakTailoringTests</c> cannot reach, and the
///         one this repository keeps finding missing.</b> That file judges <see cref="LineBreaker" />
///         against a transcribed oracle and would go on passing if no CSS declaration ever reached
///         it — the commonest defect here is a finished thing nothing calls, and a property that is
///         interned, inherited and read by a method with no caller is exactly that shape. These four
///         assert the route: a declaration in
///         a stylesheet, through <c>UiDocument.LineBreakOf</c> and <c>UiElement.Block</c>, to a
///         paragraph that breaks somewhere it otherwise would not.
///     </para>
///     <para>
///         ⚠ <b>Latin text and <c>anywhere</c> rather than kana and <c>strict</c>, which is a fixture
///         decision and not a shortcut.</b> The tailorings that matter most are about small kana, and
///         the test font has no kana in it — a paragraph of <c>.notdef</c> has whatever width the
///         face gives glyph zero, so a line-count assertion over it would be measuring the fallback
///         rather than the property. <c>anywhere</c> is the one value whose effect is visible in a
///         script the font can draw, so it is the one the wiring is proved with; which characters
///         each strictness moves is settled where there is no font at all.
///     </para>
/// </remarks>
public class LineBreakTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>A narrow label, styled by the caller, holding one unbreakable word.</summary>
    /// <remarks>
    ///     ⚠ One word and no space in it, so UAX#14 offers no opportunity anywhere inside it and the
    ///     untailored answer is one line however narrow the box is. That is what makes the line count
    ///     an assertion about the property rather than about the width.
    /// </remarks>
    static int Lines(string rootStyle, string labelStyle) {
        using var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);
        document.Load(
            $"root {{ width: 400px; height: 300px; align-items: flex-start; {rootStyle} }} "
            + $"label {{ width: 40px; {labelStyle} }}"
        );

        var element = document.Root.Add("label");
        element.Text = "aaaaaaaaaaaaaaaa";
        document.Update();

        return element.Block()!.Lines.Length;
    }

    /// <summary>Without the property, an unbreakable word is one line however narrow the box.</summary>
    /// <remarks>
    ///     The instrument, and it has to be asserted rather than assumed: if this were already several
    ///     lines, every assertion below would be about <c>overflow-wrap</c>'s last resort instead.
    /// </remarks>
    [Fact]
    public void A_word_with_no_opportunity_in_it_is_one_line() =>
        Assert.Equal(1, Lines(string.Empty, string.Empty));

    /// <summary><c>line-break: anywhere</c> puts an opportunity between every two characters.</summary>
    [Fact]
    public void Anywhere_breaks_a_word_that_has_no_opportunity_in_it() =>
        Assert.True(Lines(string.Empty, "line-break: anywhere;") > 1);

    /// <summary>⚠ And it inherits, which is the whole reason it is a property and not an argument.</summary>
    /// <remarks>
    ///     CSS makes <c>line-break</c> inherited because the strictness a paragraph wants is a
    ///     property of the language it is written in — a statement about a subtree. Declared on the
    ///     container and read on the element that owns the glyphs, which is the only route there is:
    ///     Vixen has no line box shared between elements.
    /// </remarks>
    [Fact]
    public void It_inherits_from_the_container() =>
        Assert.True(Lines("line-break: anywhere;", string.Empty) > 1);

    /// <summary>And the three values that are not <c>anywhere</c> leave a Latin word alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that says the reader is reading rather than matching.</b> A
    ///     <c>LineBreakOf</c> that mapped every keyword it did not recognise onto
    ///     <see cref="LineBreakStrictness.Anywhere" /> — or a stylesheet that reached
    ///     <c>overflow-wrap</c>'s <c>anywhere</c> by accident, which is the same word interned once
    ///     and read by two properties — passes the two tests above and fails all three of these.
    /// </remarks>
    /// <param name="value">The keyword.</param>
    [Theory]
    [InlineData("loose")]
    [InlineData("normal")]
    [InlineData("strict")]
    public void The_other_keywords_do_not_break_a_latin_word(string value) =>
        Assert.Equal(1, Lines(string.Empty, $"line-break: {value};"));
}
