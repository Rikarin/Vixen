// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>A stroke kept to a quick mask in the atlas, on a canvas whose left half the mask lets through.</summary>
public class PaintKeepTests {
    const int Side = 64;
    const uint Red = 0xFF0000FFu;

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 12f, Flow = 1f, Falloff = 0f };

    /// <summary>White on the left half, black on the right.</summary>
    static PaintImage HalfMask() {
        PaintImage mask = new(Side, Side, PaintImage.Pack(0f, 0f, 0f, 1f));

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side / 2; x++) {
                mask.Put(x, y, PaintImage.Pack(1f, 1f, 1f, 1f));
            }
        }

        return mask;
    }

    static PaintImage Stroked(IPaintKeep? keep, PaintBrush brush) {
        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(Side, Side), brush, Red, 0, keep: keep);

        stroke.MoveTo(new(8f, 32f));
        stroke.MoveTo(new(56f, 32f));

        return image;
    }

    /// <summary>
    ///     Across the whole canvas, the stroke paints only where the mask is white; inverted, only where it is black.
    ///     With no keep it paints both halves, so the test can tell a keep that holds everything back.
    /// </summary>
    [Fact]
    public void A_kept_stroke_paints_only_where_the_mask_lets_it_and_inverted_only_where_it_does_not() {
        var mask = HalfMask();
        var free = Stroked(null, Hard);
        var kept = Stroked(new PaintImageKeep(mask, Side, Side), Hard);
        var inverted = Stroked(new PaintImageKeep(mask, Side, Side, true), Hard);

        Assert.NotEqual(0u, free.At(16, 32));
        Assert.NotEqual(0u, free.At(48, 32));

        Assert.Equal(Red, kept.At(16, 32));
        Assert.Equal(0u, kept.At(48, 32));

        Assert.Equal(0u, inverted.At(16, 32));
        Assert.Equal(Red, inverted.At(48, 32));
    }

    /// <summary>
    ///     A grey keep holds an accumulating stroke to its share however many stamps cross a texel, as a grey stencil
    ///     does: scaling each stamp's weight alone was built back up by the stamps that follow.
    /// </summary>
    [Fact]
    public void A_grey_keep_holds_an_accumulating_stroke_to_its_share() {
        PaintImage grey = new(Side, Side, PaintImage.Pack(0.5f, 0.5f, 0.5f, 1f));
        var image = Stroked(
            new PaintImageKeep(grey, Side, Side),
            Hard with { Spacing = 0.05f, Blend = PaintStampBlend.Accumulate }
        );

        Assert.InRange(PaintImage.Channel(image.At(32, 32), 3), 0.4f, 0.6f);
    }

    /// <summary>A mask of another size than the canvas is stretched over it, so its left half still keeps the left.</summary>
    [Fact]
    public void A_mask_of_another_size_is_stretched_over_the_canvas() {
        PaintImage small = new(2, 2, PaintImage.Pack(0f, 0f, 0f, 1f));

        small.Put(0, 0, PaintImage.Pack(1f, 1f, 1f, 1f));
        small.Put(0, 1, PaintImage.Pack(1f, 1f, 1f, 1f));

        var image = Stroked(new PaintImageKeep(small, Side, Side), Hard);

        Assert.Equal(Red, image.At(16, 32));
        Assert.Equal(0u, image.At(48, 32));
    }
}
