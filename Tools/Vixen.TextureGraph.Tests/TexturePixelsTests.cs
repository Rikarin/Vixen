// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A whole picture and a single texel have to agree on what a half means as a byte.</summary>
public class TexturePixelsTests {
    [Fact]
    public void Every_half_rounds_the_same_through_the_table_and_the_slow_path() {
        const int Side = 256;
        var raw = new byte[Side * Side * 4];
        for (var bits = 0; bits < 1 << 16; bits++) {
            BitConverter.TryWriteBytes(raw.AsSpan(bits * 2), (ushort)bits);
        }

        var table = TexturePixels.ToBitmap(raw, Side, Side / 2, TextureFormat.Rgba16Float).Pixels;
        var slow = TexturePixels.ToBitmap(raw.AsSpan(), Side, Side / 2, TextureFormat.Rgba16Float).Pixels;

        Assert.Equal(slow.Length, table.Length);
        Assert.True(slow.AsSpan().SequenceEqual(table));
    }

    [Fact]
    public void Rows_keep_their_order() {
        var raw = new byte[2 * 3 * 8];
        var half = BitConverter.HalfToUInt16Bits((Half)1f);
        BitConverter.TryWriteBytes(raw.AsSpan((1 * 2 + 1) * 8), half);

        var picture = TexturePixels.ToBitmap(raw, 2, 3, TextureFormat.Rgba16Float);

        Assert.Equal(255, picture.Pixels[(1 * 2 + 1) * 4]);
        Assert.Equal(0, picture.Pixels[(2 * 2 + 1) * 4]);
    }

    [Fact]
    public void A_format_with_no_table_still_converts() {
        var picture = TexturePixels.ToBitmap(new byte[] { 200 }, 1, 1, TextureFormat.R8);

        Assert.Equal(new byte[] { 200, 200, 200, 255 }, picture.Pixels);
    }
}
