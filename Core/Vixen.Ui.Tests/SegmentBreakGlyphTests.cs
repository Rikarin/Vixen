// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A line that ends at a forced break draws its letters and nothing after them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The face gets a say in what a newline looks like, and the whole defect is that it
///         does.</b> A line the wrapper ended at a forced break is the substring <i>including</i> its
///         terminator, so the run is re-shaped from <c>"ab\n"</c> and HarfBuzz maps U+000A through
///         the cmap like any other character. A face without a glyph for it — OpenSans has none —
///         shapes it to <c>.notdef</c>, and until <c>TextRun.Place</c> learned to skip a segment
///         break, a hollow box was drawn at the end of every line but the last of any hard-broken
///         paragraph. It moved nothing, because <c>TextLine.Terminator</c> already takes the
///         newline's advance off the reported width; it was only visible.
///     </para>
///     <para>
///         ⚠ <b>So every assertion here is about glyph <i>ids</i>, never counts.</b> A count is
///         satisfied by a face whose newline happens to map to a blank glyph, and the test has to be
///         one such a face cannot pass by accident: the first line of <c>"ab\ncd"</c> must place
///         exactly the ids that <c>"ab"</c> alone places, in that order, at those positions.
///     </para>
///     <para>
///         The set skipped is CSS Text § 4.1.1's segment breaks — the same characters
///         <c>TextLine.IsSegmentBreak</c> reads when it takes a terminator's advance off a line — and
///         not U+000A alone, which is why the theory below walks each of them plus the two-character
///         <c>CRLF</c>.
///     </para>
/// </remarks>
public class SegmentBreakGlyphTests {
    const float Tolerance = 0.05f;
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>A wide box, so the only line breaks are the ones the text forces.</summary>
    static TextLayout Block(string text, string label = "") {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: 200px; height: 300px; align-items: flex-start; }
              label { font-family: Test; font-size: 16px; {{label}} }
              """
        );

        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        var block = element.Block();
        Assert.NotNull(block);

        return block;
    }

    /// <summary>Where the glyphs of one line are, straight from the runs.</summary>
    /// <remarks>
    ///     Read through <c>TextRun.Place</c> rather than <c>TextLine.Place</c>, because the run is
    ///     where the suppression lives and where every other consumer — the draw list, the caret's
    ///     hit test, the decoration bars — reads glyphs from.
    /// </remarks>
    static List<PositionedGlyph> Glyphs(TextLine line) {
        var glyphs = new List<PositionedGlyph>();

        foreach (var run in line.Runs) {
            run.Place(glyphs);
        }

        return glyphs;
    }

    static ushort[] Ids(TextLine line) => Glyphs(line).Select(glyph => glyph.GlyphId).ToArray();

    /// <summary>The instrument: the face has no glyph for six of the breaks and a blank one for CR.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Without this the theory below could go green on a face that maps every break to a
    ///         blank glyph, and then say nothing about the suppression at all. OpenSans maps six of
    ///         the seven to nothing, so a run shaped with one of them in it has a <c>.notdef</c>
    ///         somewhere — glyph 0, the box.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it maps U+000D to glyph 2</b>, the <c>nonmarkingreturn</c> older TrueType
    ///         faces carry, which is a blank glyph with an advance. That row was written expecting
    ///         zero and went red on the first run, and it is kept as the better instrument: the CR
    ///         theory row below is the case where a count of glyphs would have said the line was
    ///         fine because the box happened to be invisible, and it is exactly the face's say that
    ///         the suppression takes away.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData('\n', 0)]
    [InlineData('\r', 2)]
    [InlineData('\u000b', 0)]
    [InlineData('\u000c', 0)]
    [InlineData('\u0085', 0)]
    [InlineData('\u2028', 0)]
    [InlineData('\u2029', 0)]
    public void The_face_gets_a_say_in_what_a_segment_break_looks_like(char terminator, int glyph) =>
        Assert.Equal(glyph, Font.GlyphFor(terminator));

    /// <summary>The first line of a hard-broken paragraph places exactly its letters.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u000b")]
    [InlineData("\u000c")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void A_line_before_a_hard_break_draws_its_letters_and_nothing_else(string terminator) {
        var block = Block($"ab{terminator}cd");
        Assert.Equal(2, block.Lines.Length);

        var expected = Ids(Block("ab").Lines[0]);
        Assert.Equal(2, expected.Length);
        Assert.DoesNotContain((ushort)0, expected);

        Assert.Equal(expected, Ids(block.Lines[0]));
        Assert.Equal(Ids(Block("cd").Lines[0]), Ids(block.Lines[1]));
    }

    /// <summary>Skipping the terminator moves none of the letters before it.</summary>
    /// <remarks>
    ///     A suppression that filtered the shaped text rather than skipping the placement would
    ///     re-cluster the run and could move the pen; the letters have to sit where <c>"ab"</c>
    ///     alone puts them.
    /// </remarks>
    [Fact]
    public void The_letters_sit_where_they_would_without_the_break() {
        var alone = Glyphs(Block("ab").Lines[0]);
        var broken = Glyphs(Block("ab\ncd").Lines[0]);

        Assert.Equal(alone.Count, broken.Count);

        for (var i = 0; i < alone.Count; i++) {
            Assert.Equal(alone[i].X, broken[i].X, Tolerance);
        }
    }

    /// <summary>The tab's own suppression still holds beside the new one.</summary>
    /// <remarks>
    ///     A tab and a newline in one paragraph: the first line is <c>a</c>, a gap, <c>b</c>, and no
    ///     box for either control character.
    /// </remarks>
    [Fact]
    public void A_tab_and_a_break_on_one_line_draw_neither() {
        var block = Block("a\tb\ncd");
        Assert.Equal(2, block.Lines.Length);
        Assert.Equal(Ids(Block("ab").Lines[0]), Ids(block.Lines[0]));
    }

    /// <summary>Under <c>white-space: pre</c>, where hard breaks are the only breaks, the same holds.</summary>
    [Fact]
    public void Pre_formatted_text_draws_no_box_at_its_line_ends() {
        var block = Block("ab\ncd\nef", "white-space: pre;");
        Assert.Equal(3, block.Lines.Length);

        var ab = Ids(Block("ab").Lines[0]);
        Assert.Equal(ab, Ids(block.Lines[0]));
        Assert.Equal(Ids(Block("cd").Lines[0]), Ids(block.Lines[1]));
    }
}
