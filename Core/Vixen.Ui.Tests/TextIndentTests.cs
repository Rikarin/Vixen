// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>text-indent</c> as the element resolves, wraps, measures and hit-tests it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The caret is why this file is longer than the feature.</b> An indent moves every glyph
///         on the first line and moves nothing on the others, so a hit test that did not learn it
///         lands a character out — <i>only</i> on the first line, and only by however many characters
///         the indent happens to cover. That is the failure mode <c>docs/plan/43</c> split
///         <c>text-transform</c> off to avoid, and the reason it is asserted here as a round trip
///         rather than as a pair of numbers: <c>CaretIndexAt(CaretOffset(i)) == i</c> is false for
///         exactly one wrong constant on either side, and true for the right one on both.
///     </para>
///     <para>
///         ⚠ <b>And the measurement, which is the half that has no visible symptom.</b> The indent
///         occupies width as surely as the glyphs do, so <c>TextLayout.Width</c> maximises over
///         <c>Offset + Width</c>. Left out, a shrink-to-fit box comes out an indent too narrow and
///         clips the line it was measuring — which looks like a wrapping bug and is not one.
///     </para>
/// </remarks>
public class TextIndentTests {
    const float Tolerance = 0.01f;
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    static UiDocument Documented(string label) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);
        document.Load($"root {{ width: 400px; height: 300px; align-items: flex-start; }} label {{ {label} }}");

        return document;
    }

    static UiElement Labelled(UiDocument document, string text) {
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        return element;
    }

    [Fact]
    public void An_element_that_declares_nothing_has_no_indent() {
        using var document = Documented("width: 80px;");

        Assert.Equal(0f, Labelled(document, "aa bb").TextIndent);
    }

    [Fact]
    public void The_declared_length_reaches_the_element_in_pixels() {
        using var document = Documented("width: 80px; font-size: 10px; text-indent: 2em;");

        Assert.Equal(20f, Labelled(document, "aa bb").TextIndent, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>It is computed and inherited, not inherited and then computed.</b> A child with a
    ///     different font size gets the ancestor's <i>resolved</i> pixels, which is what
    ///     <c>line-height</c> and <c>letter-spacing</c> do and is the whole reason none of the three
    ///     is in <c>InheritedProperties</c>. Inheriting the text <c>2em</c> would indent the child by
    ///     twice its own size, which here would be forty pixels rather than twenty.
    /// </summary>
    [Fact]
    public void A_child_inherits_the_resolved_length_rather_than_the_declaration() {
        using var document = Documented("width: 80px;");
        document.Load("#outer { font-size: 10px; text-indent: 2em; } #inner { font-size: 20px; }");

        var outer = document.Root.Add("div", "outer");
        var inner = outer.Add("label", "inner");
        inner.Text = "aa bb";
        document.Update();

        Assert.Equal(20f, inner.TextIndent, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A percentage resolves to zero, and that is a refusal rather than a bug.</b> CSS
    ///     measures one against the containing block's width, which is a layout result the style pass
    ///     does not have — and the style pass is where the value has to be computed, because an
    ///     <c>em</c> on it must measure against the element that wrote it. Recorded here so the
    ///     refusal is a fact somebody can find rather than a surprise.
    /// </summary>
    [Fact]
    public void A_percentage_is_refused_rather_than_guessed_at() {
        using var document = Documented("width: 80px; text-indent: 50%;");

        Assert.Equal(0f, Labelled(document, "aa bb").TextIndent);
    }

    /// <summary>
    ///     ⚠ <b>A unit that is not a distance is refused, and it is not the percentage's refusal.</b>
    ///     A percentage is a value this engine understands and cannot resolve here, so it lands on
    ///     the initial value deliberately — the fact above. <c>text-indent: 200ms</c> is not a value
    ///     at all, and reached that same zero through <c>LengthContext.PixelsPer</c>, which answers
    ///     nothing for a duration: the declaration thrown away and the element indented by nought,
    ///     which is exactly what an element that never declared the property looks like. It is left
    ///     inherited now, which is what dropping a declaration means — and the inherited twelve
    ///     pixels here are what tell the two outcomes apart.
    /// </summary>
    [Theory]
    [InlineData("200ms")]
    [InlineData("90deg")]
    [InlineData("0.5turn")]
    public void A_unit_that_is_not_a_distance_is_refused_rather_than_zeroed(string indent) {
        using var document = Documented("width: 80px;");
        document.Load($"#outer {{ text-indent: 12px; }} #inner {{ text-indent: {indent}; }}");

        var outer = document.Root.Add("div", "outer");
        var inner = outer.Add("label", "inner");
        inner.Text = "aa bb";
        document.Update();

        Assert.Equal(12f, inner.TextIndent, Tolerance);
    }

    /// <summary>The indent lands on the first line and on no other.</summary>
    [Fact]
    public void Only_the_first_line_carries_the_offset() {
        using var document = Documented("width: 60px; text-indent: 12px;");
        var block = Labelled(document, "aa bb cc dd ee").Block()!;

        Assert.True(block.Lines.Length > 1, "the label did not wrap, so there is no second line");
        Assert.Equal(12f, block.Lines[0].Offset, Tolerance);
        Assert.All(block.Lines.Skip(1), line => Assert.Equal(0f, line.Offset));
    }

    /// <summary>
    ///     ⚠ <b>It narrows the first line rather than only moving it</b>, which is the half a shift
    ///     applied afterwards would miss.
    /// </summary>
    [Fact]
    public void The_first_line_wraps_earlier_than_it_would_have() {
        using var document = Documented("width: 60px;");
        var plain = Labelled(document, "aa bb cc dd ee").Block()!;

        using var indented = Documented("width: 60px; text-indent: 30px;");
        var pushed = Labelled(indented, "aa bb cc dd ee").Block()!;

        Assert.True(
            pushed.Lines[0].Length < plain.Lines[0].Length,
            $"the first line still holds {pushed.Lines[0].Length} characters of {plain.Lines[0].Length}"
        );
    }

    /// <summary>And the block measures the indent as well as the glyphs.</summary>
    [Fact]
    public void The_indent_counts_towards_the_blocks_width() {
        using var document = Documented("width: 400px;");
        var plain = Labelled(document, "aa").Block()!;

        using var indented = Documented("width: 400px; text-indent: 16px;");
        var pushed = Labelled(indented, "aa").Block()!;

        Assert.Equal(plain.Width + 16f, pushed.Width, Tolerance);
    }

    /// <summary>A hanging indent pulls the first line out to the left.</summary>
    [Fact]
    public void A_negative_indent_hangs_the_first_line() {
        using var document = Documented("width: 400px; text-indent: -8px;");
        var block = Labelled(document, "aa bb").Block()!;

        Assert.Equal(-8f, block.Lines[0].Offset, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>The caret round trip, on the indented line and on the one below it.</b> Every caret
    ///     index on the first line is offset by the indent and every one below it is not, so a
    ///     hit test that learned the indent for all lines or for none fails one half or the other.
    /// </summary>
    [Fact]
    public void Every_caret_index_survives_the_round_trip_through_pixels() {
        const string Text = "aa bb cc dd ee";

        using var document = Documented("width: 60px; text-indent: 12px;");
        var block = Labelled(document, Text).Block()!;

        Assert.True(block.Lines.Length > 1, "one line, so the second half of this test is vacuous");

        foreach (var line in block.Lines) {
            for (var index = line.Start; index <= line.Start + line.Length; index++) {
                // Whitespace has no caret position of its own once it is trimmed off the end of a
                // line, so the last index of a line that ends in a space is not a round trip. Every
                // other index is.
                if (index < Text.Length && char.IsWhiteSpace(Text[index])) {
                    continue;
                }

                Assert.Equal(index, line.CaretIndexAt(line.CaretOffset(index)));
            }
        }
    }

    /// <summary>The first character of the first line sits at the indent, not at zero.</summary>
    [Fact]
    public void The_caret_before_the_first_character_is_at_the_indent() {
        using var document = Documented("width: 400px; text-indent: 12px;");
        var block = Labelled(document, "aa bb").Block()!;

        Assert.Equal(12f, block.CaretAt(0).X, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A click inside the indent lands on the first character rather than off the line.</b>
    ///     The white space at the start of an indented paragraph is part of that paragraph's line, and
    ///     clicking it is how anybody puts a caret at the start of one.
    /// </summary>
    [Fact]
    public void A_point_inside_the_indent_lands_on_the_first_character() {
        using var document = Documented("width: 400px; text-indent: 40px;");
        var block = Labelled(document, "aa bb").Block()!;

        Assert.Equal(0, block.CaretIndexAt(4f, 0f));
        Assert.Equal(0, block.CaretIndexAt(0f, 0f));
    }

    /// <summary>
    ///     ⚠ <b>A hit test on the <i>second</i> line is not shifted, which is the assertion that
    ///     fails for an implementation that applied the indent to the block instead of the line.</b>
    /// </summary>
    [Fact]
    public void The_second_line_is_hit_tested_from_zero() {
        using var document = Documented("width: 60px; text-indent: 12px;");
        var block = Labelled(document, "aa bb cc dd ee").Block()!;

        Assert.True(block.Lines.Length > 1);

        var second = block.Lines[1];
        Assert.Equal(second.Start, second.CaretIndexAt(0f));
        Assert.Equal(0f, second.CaretOffset(second.Start), Tolerance);
    }
}
