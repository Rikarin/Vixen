// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The seam, and the hairline that only appears after mipping.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D13's warning and issue #574's third exit line.</b> A stroke that crosses a UV
///         island edge paints up to the last covered texel and stops. At mip 0 that is correct and
///         <em>looks</em> correct, which is the whole difficulty: the defect is invisible in the view
///         the artist is painting in and appears when the object is far enough away to be sampling
///         mip 3.
///     </para>
///     <para>
///         ⚠ <b>The oracle is closed-form rather than eyeballed.</b> A hard-edged brush at full flow
///         and full opacity paints every covered texel it reaches to exactly 255. A mip-3 texel is
///         the mean of an 8×8 block. So a block that lies entirely inside the disc must read 255 when
///         the gutter is filled, and exactly <c>255 · (64 − unpainted) / 64</c> when it is not — a
///         number this file computes rather than measures. Doc 06's rule about getting a picture is
///         answered by making the picture's arithmetic exact.
///     </para>
///     <para>
///         ⚠ <b>And the same test asserts the un-dilated value, which is the sabotage built in.</b>
///         Turning the dilation off has to change the number; a seam test that only asserted the
///         good value would stay green against a dilation that did nothing, which is exactly how a
///         gutter that never ran ships.
///     </para>
/// </remarks>
public class PaintSeamTests {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>The layout the whole file reasons about: islands at 0…29 and 34…63, gutter at 30…33.</summary>
    const int GutterStart = 30;

    const int GutterWidth = 4;

    /// <summary>Mip 3 across the seam is flat with the dilation and dips without it.</summary>
    /// <remarks>
    ///     The 8×8 block at columns 24…31 holds six covered columns and the gutter's first two, so
    ///     without a dilation sixteen of its sixty-four texels are unpainted — 255 · 48 / 64 = 191.
    /// </remarks>
    [Theory]
    [InlineData(4, 1f)]
    [InlineData(0, 0.75f)]
    public void A_stroke_across_an_island_edge_survives_mip_three_only_when_it_is_dilated(
        int gutter,
        float expected
    ) {
        PaintImage image = new(64, 64);
        PaintStroke stroke = new(image, PaintStrokeTests.Islands(64, 64), PaintStrokeTests.Hard(24f), Opaque, gutter);

        stroke.MoveTo(new(32f, 32f));

        // Columns 24…31: six covered and the gutter's first two. Sixteen of the block's sixty-four
        // texels, which is where 0.75 comes from — computed, not measured.
        Assert.Equal(expected, MipAlpha(image, 24, 24), 3);

        // The instrument, twice over. A block well inside an island must read one whichever way the
        // dilation was set — if it does not, this is measuring the brush rather than the seam, and
        // the number above would be a fact about the falloff.
        Assert.Equal(1f, MipAlpha(image, 16, 16), 3);
        Assert.Equal(1f, MipAlpha(image, 40, 40), 3);
    }

    /// <summary>The dilation fills the gutter and nothing else.</summary>
    [Fact]
    public void The_dilation_reaches_exactly_the_gutter() {
        PaintImage image = new(64, 64);
        PaintStroke stroke = new(image, PaintStrokeTests.Islands(64, 64), PaintStrokeTests.Hard(24f), Opaque, 4);

        stroke.MoveTo(new(32f, 32f));

        for (var x = GutterStart; x < GutterStart + GutterWidth; x++) {
            Assert.Equal(255u, image.At(x, 32) >> 24);
        }

        Assert.True(stroke.DilatedTexels > 0, "Nothing was dilated at all.");
    }

    /// <summary>
    ///     ⚠ One island's dilation never writes the island beside it, which is
    ///     <c>MapBaker.Dilate</c>'s rule and the reason it reads <c>Coverage</c> and never writes it.
    /// </summary>
    /// <remarks>
    ///     A stamp entirely inside the left island, close enough that its gutter reaches all four
    ///     unpainted columns and therefore touches the right island's first column. That column must
    ///     stay empty: a dilation that crossed it would paint a strip of the wrong island's surface,
    ///     which is a visible smear rather than a hairline.
    /// </remarks>
    [Fact]
    public void A_dilation_stops_at_the_next_island() {
        PaintImage image = new(64, 64);
        PaintStroke stroke = new(image, PaintStrokeTests.Islands(64, 64), PaintStrokeTests.Hard(8f), Opaque, 4);

        stroke.MoveTo(new(25f, 32f));

        Assert.Equal(255u, image.At(29, 32) >> 24);
        Assert.Equal(255u, image.At(GutterStart + GutterWidth - 1, 32) >> 24);
        Assert.Equal(0u, image.At(GutterStart + GutterWidth, 32) >> 24);
    }

    /// <summary>A dilation over an atlas with no gutter in it writes nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         The degenerate case, and the cheapest evidence that the dilation is keyed on coverage
    ///         rather than on distance from the stamp: with every texel covered there is nowhere for
    ///         it to go.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which a stroke that did nothing also satisfies</b>, so the stamp is read back
    ///         first. Zero dilated texels is the answer for a dilation that is correctly idle and for
    ///         a stroke whose brush, coverage or opacity has stopped working — and it was the whole
    ///         of this test.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stack_with_no_islands_has_nothing_to_dilate_into() {
        PaintImage image = new(64, 64);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(64, 64), PaintStrokeTests.Hard(12f), Opaque, 8);

        stroke.MoveTo(new(32f, 32f));

        Assert.Equal(0xFFu, image.At(32, 32) >> 24);
        Assert.Equal(0, stroke.DilatedTexels);
    }

    /// <summary>An undo removes the dilation too.</summary>
    [Fact]
    public void Undo_puts_the_gutter_back() {
        PaintImage image = new(64, 64);

        image.Fill(0x11223344u);

        var original = (byte[])image.Texels.Clone();
        PaintStroke stroke = new(image, PaintStrokeTests.Islands(64, 64), PaintStrokeTests.Hard(24f), Opaque, 4);

        stroke.MoveTo(new(32f, 32f));

        Assert.NotEqual(original, image.Texels);
        Assert.NotEqual(0x11223344u, image.At(GutterStart, 32));

        stroke.Undo();

        Assert.Equal(original, image.Texels);
    }

    /// <summary>A UV triangle raster marks the texels the triangle covers, slivers included.</summary>
    [Fact]
    public void Coverage_rasterised_from_triangles_marks_the_triangle_and_not_the_rest() {
        var coverage = PaintCoverage.FromTriangles(
            32,
            32,
            [new(0.1f, 0.1f), new(0.9f, 0.1f), new(0.1f, 0.9f)]
        );

        Assert.True(coverage.IsCovered(6, 6), "The triangle's interior is not covered.");
        Assert.False(coverage.IsCovered(30, 30), "A texel outside the triangle was marked covered.");
        Assert.True(coverage.CoveredTexels > 100, $"Only {coverage.CoveredTexels} texels — the raster is empty.");
        Assert.True(coverage.CoveredTexels < 32 * 32, "Every texel was marked, so the raster is not a triangle.");
    }

    /// <summary>One mip-3 texel's alpha: the mean of an 8×8 block, in one pass and in floats.</summary>
    /// <remarks>
    ///     ⚠ <b>Averaged once rather than three times, deliberately.</b> A real mip chain quantises at
    ///     every level, and three roundings of a value that lands on a half turn the closed-form
    ///     answer into a number that has to be measured to be known — which is a test that says what
    ///     the code does rather than what it should do. The defect is the mean, so the mean is what
    ///     this reads.
    /// </remarks>
    static float MipAlpha(PaintImage image, int x, int y) {
        var total = 0f;

        for (var row = 0; row < 8; row++) {
            for (var column = 0; column < 8; column++) {
                total += PaintImage.Channel(image.At(x + column, y + row), 3);
            }
        }

        return total / 64f;
    }
}
