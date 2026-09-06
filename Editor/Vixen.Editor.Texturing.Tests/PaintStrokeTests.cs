// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A stroke: spacing, the opacity cap, determinism and the undo record.</summary>
public class PaintStrokeTests {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>A click without a drag is one stamp, which is what makes this the Single tool too.</summary>
    [Fact]
    public void The_first_move_always_stamps() {
        PaintImage image = new(64, 64);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(64, 64), Hard(8f), Opaque, gutter: 0);

        stroke.MoveTo(new(32f, 32f));

        Assert.Equal(1, stroke.StampCount);
        Assert.Equal(0xFFu, image.At(32, 32) >> 24);
    }

    /// <summary>Spacing is a property of the path, not of the frame rate.</summary>
    /// <remarks>
    ///     ⚠ <b>The property doc 31 built <c>BrushStroke</c> for, restated in texels.</b> The same
    ///     drag delivered as one long move and as forty short ones has to lay the same stamps down,
    ///     or a stroke painted on a fast machine is denser than the same stroke on a slow one. The
    ///     carried distance across pointer events is what makes it true, and it is the half a
    ///     reimplementation would get wrong.
    /// </remarks>
    [Fact]
    public void One_long_move_and_forty_short_ones_lay_down_the_same_stamps() {
        PaintImage sparse = new(128, 128);
        PaintImage dense = new(128, 128);
        var coverage = PaintCoverage.Everywhere(128, 128);
        var brush = Hard(6f) with { Flow = 0.2f, Spacing = 0.25f };

        PaintStroke one = new(sparse, coverage, brush, Opaque, gutter: 0);

        one.MoveTo(new(20f, 64f));
        one.MoveTo(new(100f, 64f));

        PaintStroke many = new(dense, coverage, brush, Opaque, gutter: 0);

        many.MoveTo(new(20f, 64f));

        for (var step = 1; step <= 40; step++) {
            many.MoveTo(new(20f + (step * 2f), 64f));
        }

        Assert.Equal(one.StampCount, many.StampCount);
        Assert.Equal(sparse.Texels, dense.Texels);

        // The instrument: a stroke that laid down one stamp either way would satisfy both equalities
        // and would prove nothing about the carried distance.
        Assert.True(one.StampCount > 40, $"Only {one.StampCount} stamps — the spacing is not being walked.");
    }

    /// <summary>
    ///     ⚠ Opacity caps the stroke however many stamps cross a texel; flow is what one stamp
    ///     deposits.
    /// </summary>
    /// <remarks>
    ///     The defect this is about is the one where a slow drag paints darker than a fast one over
    ///     the same ground, because each stamp composited onto the last stamp's output. Both halves
    ///     are asserted: the build-up passes the flow of a single stamp, and it stops at the cap.
    /// </remarks>
    [Theory]
    [InlineData(1f, 255)]
    [InlineData(0.75f, 191)]
    [InlineData(0.5f, 128)]
    public void A_stroke_builds_up_to_its_opacity_and_stops_there(float opacity, int expected) {
        PaintImage image = new(64, 64);
        var brush = Hard(10f) with { Flow = 0.5f, Opacity = opacity, Spacing = 0.02f };
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(64, 64), brush, Opaque, gutter: 0);

        stroke.MoveTo(new(22f, 32f));
        stroke.MoveTo(new(42f, 32f));

        Assert.True(stroke.StampCount > 50, $"Only {stroke.StampCount} stamps crossed the texel.");
        Assert.Equal((uint)expected, image.At(32, 32) >> 24);
    }

    /// <summary>Jitter is deterministic, and it does something.</summary>
    /// <remarks>
    ///     ⚠ <b>The second assertion is the one that matters.</b> A jitter that never moved anything
    ///     would make the first two images identical and the determinism claim vacuous — which is
    ///     precisely the shape of test this repository keeps shipping.
    /// </remarks>
    [Fact]
    public void Jitter_repeats_for_a_seed_and_differs_between_seeds() {
        var brush = Hard(6f) with { PositionJitter = 0.8f, AngleJitter = 1f, SizeJitter = 0.6f, Spacing = 0.5f };

        var first = Painted(brush, 0xABCDEF01u);
        var again = Painted(brush, 0xABCDEF01u);
        var other = Painted(brush, 0x12345678u);
        var still = Painted(brush with { PositionJitter = 0f, AngleJitter = 0f, SizeJitter = 0f }, 0xABCDEF01u);

        Assert.Equal(first.Texels, again.Texels);
        Assert.NotEqual(first.Texels, other.Texels);
        Assert.NotEqual(first.Texels, still.Texels);
    }

    /// <summary>Smoothing lags the path, which is what a lazy mouse is.</summary>
    [Fact]
    public void Smoothing_pulls_the_stroke_behind_the_pointer() {
        PaintImage direct = new(128, 128);
        PaintImage lagged = new(128, 128);
        var coverage = PaintCoverage.Everywhere(128, 128);
        var brush = Hard(4f) with { Spacing = 0.5f };

        PaintStroke straight = new(direct, coverage, brush, Opaque, gutter: 0);
        PaintStroke smoothed = new(lagged, coverage, brush, Opaque, gutter: 0, smoothing: 0.8f);

        foreach (var stroke in new[] { straight, smoothed }) {
            stroke.MoveTo(new(20f, 20f));

            for (var step = 1; step <= 30; step++) {
                stroke.MoveTo(new(20f + (step * 3f), 20f));
            }
        }

        Assert.True(straight.StampCount > smoothed.StampCount, "Smoothing did not shorten the painted path.");
        Assert.Equal(0xFFu, direct.At(108, 20) >> 24);
        Assert.Equal(0x00u, lagged.At(108, 20) >> 24);
    }

    /// <summary>Undo restores exactly, and redo puts back exactly.</summary>
    /// <remarks>
    ///     ⚠ <b>A low flow, deliberately, and the first version of this test was worthless without
    ///     it.</b> At full flow a texel reaches the cap on the stamp that first covers it and every
    ///     later stamp skips it, so the record is written once per texel <em>whatever</em> the
    ///     recording rule is — and re-recording on every crossing, which is the defect
    ///     <c>TerrainStroke</c>'s <c>TryAdd</c> exists to prevent, left this green. A flow of 0.3
    ///     makes each texel grow across several stamps, which is when the two rules differ.
    /// </remarks>
    [Fact]
    public void A_stroke_undoes_and_redoes_to_the_byte() {
        PaintImage image = new(96, 96);

        image.Fill(0x40302010u);

        var original = (byte[])image.Texels.Clone();
        PaintStroke stroke = new(image, Islands(96, 96), Hard(9f) with { Flow = 0.3f, Spacing = 0.3f }, Opaque);

        stroke.MoveTo(new(20f, 48f));
        stroke.MoveTo(new(76f, 48f));

        var painted = (byte[])image.Texels.Clone();
        var redo = stroke.Capture();

        Assert.NotEqual(original, painted);

        stroke.Undo();

        Assert.Equal(original, image.Texels);

        redo.Redo();

        Assert.Equal(painted, image.Texels);
    }

    /// <summary>The record holds the texels the stroke touched and not the atlas.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TerrainStroke</c>'s argument, restated where the numbers are much worse.</b> A
    ///     dense before-image of a 4K layer is 67 MB per drag whatever the artist did; this asserts
    ///     the record is a fraction of the atlas rather than trusting that it is.
    /// </remarks>
    [Fact]
    public void The_undo_record_is_sparse() {
        PaintImage image = new(1024, 1024);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(1024, 1024), Hard(16f), Opaque, gutter: 0);

        stroke.MoveTo(new(512f, 512f));

        Assert.True(stroke.RecordedTexels > 0, "The stroke recorded nothing at all.");
        Assert.True(
            stroke.RecordedTexels < 1024 * 1024 / 100,
            $"{stroke.RecordedTexels} texels recorded for one 16-texel stamp on a 1024² layer."
        );
    }

    /// <summary>A stamp on its own writes only the texels an island covers.</summary>
    /// <remarks>
    ///     <para>
    ///         Which is why the gutter is empty afterwards, and why <c>PaintSeamTests</c> is a
    ///         separate file: everything between the islands arrives from the dilation and from
    ///         nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <em>only</em> is half the claim and it used to be the whole test.</b> An
    ///         empty gutter and no dilated texels are both exactly what a stamp that painted nothing
    ///         at all leaves behind — coverage refused everywhere, a radius that came out zero, a
    ///         brush whose opacity was dropped — so the two assertions could not tell "wrote only the
    ///         island" from "wrote nothing". The islands either side are read first, and their alpha
    ///         is what makes the gutter's a statement about where the stamp stopped.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stamp_paints_only_the_texels_an_island_covers() {
        PaintImage image = new(64, 64);
        PaintStroke stroke = new(image, Islands(64, 64), Hard(24f), Opaque, gutter: 0);

        stroke.MoveTo(new(32f, 32f));

        // The instrument, on both sides of the gutter: a 24-texel hard brush at the centre reaches
        // well past it, so an island texel left transparent is a stamp that did not happen.
        Assert.Equal(0xFFu, image.At(29, 32) >> 24);
        Assert.Equal(0xFFu, image.At(34, 32) >> 24);

        for (var y = 0; y < 64; y++) {
            for (var x = 30; x < 34; x++) {
                Assert.Equal(0x00u, image.At(x, y) >> 24);
            }
        }

        Assert.Equal(0, stroke.DilatedTexels);
    }

    internal static PaintBrush Hard(float radius) =>
        PaintBrush.Default with {
            Radius = radius,
            Flow = 1f,
            Opacity = 1f,
            Falloff = 0f,
            Curve = BrushFalloffKind.Linear,
            Spacing = 0.25f
        };

    /// <summary>Two islands with a four-texel gutter between them at columns 30…33.</summary>
    internal static PaintCoverage Islands(int width, int height) {
        var raster = new bool[width * height];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                raster[(y * width) + x] = x < 30 || x >= 34;
            }
        }

        return PaintCoverage.FromRaster(width, height, raster);
    }

    static PaintImage Painted(PaintBrush brush, uint seed) {
        PaintImage image = new(128, 128);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(128, 128), brush, Opaque, 0, 0f, seed);

        stroke.MoveTo(new(20f, 64f));
        stroke.MoveTo(new(108f, 64f));

        return image;
    }
}
