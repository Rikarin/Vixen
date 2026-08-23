// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>Where a decoration line goes comes out of the face, and these are the faces' numbers.</summary>
/// <remarks>
///     <para>
///         <b>The expectations were read out of the font binaries with a separate tool, not out of
///         <see cref="FontFace" />.</b> A test that asserts what the code returns is a test that
///         cannot fail; these numbers came from parsing <c>post</c> and <c>OS/2</c> in Python before
///         a line of <see cref="FontFace.Decoration" /> existed, so a wrong table offset or a scale
///         HarfBuzz applied that we did not expect fails here rather than reading as house style.
///     </para>
///     <para>
///         ⚠ <b>The spread across these four faces is the argument for the feature.</b> Twenty
///         design units per 2048 in one and 184 in another is a factor of nine, at the same em size,
///         in two fonts a document could reasonably mix. Any constant is wrong for one of them.
///     </para>
/// </remarks>
public class DecorationMetricsTests {
    /// <summary>A face's own tables, read back unchanged.</summary>
    /// <param name="name">Which font.</param>
    /// <param name="underlineOffset"><c>post.underlinePosition</c>.</param>
    /// <param name="underlineThickness"><c>post.underlineThickness</c>.</param>
    /// <param name="strikeoutOffset"><c>OS/2.yStrikeoutPosition</c>.</param>
    /// <param name="strikeoutThickness"><c>OS/2.yStrikeoutSize</c>.</param>
    [Theory]
    [InlineData(TestFonts.Arabic, -277, 184, 1201, 184)]
    [InlineData(TestFonts.Kannada, -154, 102, 570, 102)]
    [InlineData(TestFonts.Cff, -75, 50, 300, 50)]
    [InlineData("TestShapeLana.ttf", -130, 90, 530, 102)]
    public void A_face_reports_its_own_decoration_metrics(
        string name,
        int underlineOffset,
        int underlineThickness,
        int strikeoutOffset,
        int strikeoutThickness
    ) {
        var decoration = TestFonts.Load(name).Decoration;

        Assert.Equal(underlineOffset, decoration.UnderlineOffset);
        Assert.Equal(underlineThickness, decoration.UnderlineThickness);
        Assert.Equal(strikeoutOffset, decoration.StrikeoutOffset);
        Assert.Equal(strikeoutThickness, decoration.StrikeoutThickness);
    }

    /// <summary>Two faces disagree about a thickness by nine times, which is why it is not a constant.</summary>
    [Fact]
    public void Two_faces_on_the_same_grid_want_underlines_nine_times_apart() {
        var arabic = TestFonts.Load(TestFonts.Arabic);
        var lana = TestFonts.Load("TestShapeLana.ttf");

        Assert.Equal(arabic.UnitsPerEm, lana.UnitsPerEm);
        Assert.True(
            arabic.Decoration.UnderlineThickness > lana.Decoration.UnderlineThickness * 2,
            "the two faces are supposed to disagree; if they no longer do, this test is measuring nothing"
        );
    }

    /// <summary>A face whose <c>post</c> table is zeroed is synthesised from, not believed.</summary>
    /// <remarks>
    ///     <c>TestGSUBOne.otf</c> carries a real <c>post</c> table with both fields set to zero, and
    ///     an <c>OS/2</c> whose strikeout <i>position</i> is honest and whose strikeout <i>size</i> is
    ///     also zero. So one face exercises three of the four fallbacks and none of the fourth, which
    ///     is why it is asserted field by field rather than as a whole record.
    /// </remarks>
    [Fact]
    public void A_zeroed_table_is_treated_as_no_opinion_rather_than_as_an_answer() {
        var font = TestFonts.Load(TestFonts.ContextualLatin);
        var decoration = font.Decoration;

        Assert.Equal(1000, font.UnitsPerEm);

        // Synthesised: a twentieth of an em thick, a tenth of an em down.
        Assert.Equal(50, decoration.UnderlineThickness);
        Assert.Equal(-100, decoration.UnderlineOffset);

        // Kept: the face's strikeout position is a real number and only its size is missing.
        Assert.Equal(300, decoration.StrikeoutOffset);
        Assert.Equal(decoration.UnderlineThickness, decoration.StrikeoutThickness);
    }

    /// <summary>Every face this repository ships gives a drawable line.</summary>
    /// <remarks>
    ///     ⚠ <b>The invariant the drawing code relies on, asserted over the whole set rather than
    ///     over the four faces above.</b> A zero thickness draws nothing and a strikeout at or below
    ///     the baseline is an underline wearing the wrong name — either would be a silent blank
    ///     rather than a failure, and both are exactly what an unguarded font table hands you.
    /// </remarks>
    [Theory]
    [InlineData(TestFonts.Arabic)]
    [InlineData(TestFonts.Kannada)]
    [InlineData(TestFonts.Cff)]
    [InlineData(TestFonts.ContextualLatin)]
    [InlineData("TestShapeLana.ttf")]
    [InlineData("TestShapeEthi.ttf")]
    [InlineData("Zycon.ttf")]
    [InlineData("NotoSansBalinese-Regular.ttf")]
    public void Whatever_the_face_says_the_line_is_drawable(string name) {
        var decoration = TestFonts.Load(name).Decoration;

        Assert.True(decoration.UnderlineThickness > 0, "an underline with no thickness draws nothing");
        Assert.True(decoration.StrikeoutThickness > 0, "a line-through with no thickness draws nothing");
        Assert.True(decoration.UnderlineOffset < 0, "an underline belongs below the baseline");
        Assert.True(decoration.StrikeoutOffset > 0, "a line-through belongs above it");
    }
}
