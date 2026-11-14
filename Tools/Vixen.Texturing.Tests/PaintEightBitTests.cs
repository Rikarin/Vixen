// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     Eight-bit paint is the paint it was before channels had a width (Patina #19): strokes are mixed in four lanes
///     now, and rounded to eight bits only where an eight-bit image is written. The digests were taken from the build
///     before that change, so a stroke that moved by one level anywhere fails here.
/// </summary>
public class PaintEightBitTests {
    const int Side = 64;

    [Fact]
    public void A_soft_overlapping_stroke_is_the_bytes_it_always_was() {
        var soft = PaintBrush.Default with { Radius = 9f, Flow = 0.37f, Falloff = 0.8f, Opacity = 0.83f };
        PaintImage image = new(Side, Side, PaintImage.Pack(0.2f, 0.6f, 0.1f, 0.5f));
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(Side, Side), soft, 0xC0407F21u, 0);

        stroke.MoveTo(new(10f, 30f));
        stroke.MoveTo(new(50f, 34f));
        stroke.MoveTo(new(12f, 36f));

        Assert.Equal(Pinned.Soft, Convert.ToHexString(System.IO.Hashing.XxHash128.Hash(image.ToTexels())));
    }

    [Fact]
    public void A_smear_is_the_bytes_it_always_was() {
        PaintImage image = new(Side, Side);
        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                image.Put(x, y, PaintImage.Pack(x / (float)Side, y / (float)Side, (x ^ y) / 64f, 1f));
            }
        }

        var coverage = PaintCoverage.Everywhere(Side, Side);
        var brush = PaintBrush.Default with { Radius = 7f, Flow = 0.6f, Falloff = 0.5f };
        PaintStroke stroke = new(image, coverage, brush, 0u, 0, source: new PaintSmudge(image, coverage));

        stroke.MoveTo(new(14f, 20f));
        stroke.MoveTo(new(40f, 28f));
        stroke.MoveTo(new(44f, 50f));

        Assert.Equal(Pinned.Smear, Convert.ToHexString(System.IO.Hashing.XxHash128.Hash(image.ToTexels())));
    }

    static class Pinned {
        public const string Soft = "BD5639202DFAA956979D941E47480F5A";
        public const string Smear = "2A918D2727AD85DFF28484522847DE02";
    }
}
