// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Textures;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     <para>
///         <b>Which way up an imported texture is.</b> Everything in this pipeline is top-left-first:
///         <see cref="MinimalPng" /> writes its rows top first, <c>TextureImporterTests</c> asserts
///         the bytes come back unchanged, and <c>SpriteRect</c>'s <c>Y</c> is measured down from the
///         top of the sheet. That makes "row zero is the top row" the pipeline's convention by
///         construction, and every decoder owes it.
///     </para>
///     <para>
///         TGA is the one format that can disagree with itself about that — the image descriptor's
///         bit 5 says which end the top is at, and both settings are files a paint program will hand
///         an artist. Nothing in this repository asserted the bit was honoured, and a flipped albedo
///         and a flipped normal map both render <i>plausibly</i>: this is exactly the silent class
///         the engine keeps paying for.
///     </para>
/// </summary>
public sealed class TgaOrientationTests {
    /// <summary>
    ///     A fixture that is asymmetric in both axes, so a vertical flip, a horizontal flip and a
    ///     transpose are each a different failure rather than all of them passing.
    /// </summary>
    static byte[] Asymmetric(int width, int height) {
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var texel = ((y * width) + x) * 4;
                pixels[texel] = (byte)(16 + (x * 32));          // R climbs left to right.
                pixels[texel + 1] = (byte)(16 + (y * 64));      // G climbs top to bottom.
                pixels[texel + 2] = 7;
                pixels[texel + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    ///     The claim, stated once: a top-down TGA and a bottom-up TGA of the same picture decode to
    ///     the same pixels, in the pipeline's order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOriginBitDecidesWhichRowIsTheTopAndBothWaysDecodeTheSame(bool topDown) {
        var pixels = Asymmetric(4, 3);

        using var file = new MemoryStream(MinimalTga.Write(4, 3, pixels, topDown));
        var decoded = new StbImageDecoder().Decode(file, ".tga");

        Assert.Equal(4, decoded.Width);
        Assert.Equal(3, decoded.Height);
        Assert.Equal(pixels, decoded.Level(0).ToArray());
    }

    /// <summary>
    ///     And the fixture is not accidentally symmetric — if it were, the test above would pass on a
    ///     decoder that ignored the bit entirely.
    /// </summary>
    [Fact]
    public void TheTwoFilesReallyDoDifferInTheirBytes() {
        var pixels = Asymmetric(4, 3);

        Assert.NotEqual(MinimalTga.Write(4, 3, pixels, topDown: true), MinimalTga.Write(4, 3, pixels, topDown: false));
    }
}
