// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A change to an external of another size than the op reading it, scaled into the op's texels (Patina #279): a
///     proxy's plan reads a canvas at full size, and a stroke repairs only where it reaches.
/// </summary>
public class TextureScaledReachTests {
    /// <summary>
    ///     ⚠ Every output texel whose filtered read takes a texel of the change is inside the scaled rectangle, at any
    ///     two sizes, larger or smaller. The read is <c>Bitmap.rvn</c>'s, in single precision as the shader does it:
    ///     a rectangle one texel too narrow is a stale texel at the edge of every stroke under a proxy.
    /// </summary>
    [Fact]
    [Trait("Category", "Property")]
    public void A_scaled_change_covers_every_texel_whose_read_reaches_it() {
        Gen.Select(Gen.Int[1, 600], Gen.Int[1, 600], Gen.Int[0, 599], Gen.Int[1, 40])
            .Where(static t => t.Item3 < t.Item1)
            .Sample(static (from, to, low, width) => {
                    var high = Math.Min(from, low + width);
                    var scaled = TexturePlanEvaluator.Scaled(
                        new(low, 0, high - low, 1),
                        new Int2(from, 1),
                        new Int2(to, 1)
                    );

                    for (var texel = 0; texel < to; texel++) {
                        var at = (texel + 0.5f) / to * from - 0.5f;
                        var first = (int)MathF.Floor(at);

                        // The two taps, clamped to the source as `Math.Address` does when nothing wraps.
                        var reads = Math.Clamp(first, 0, from - 1) is var a && a >= low && a < high
                            || Math.Clamp(first + 1, 0, from - 1) is var b && b >= low && b < high;

                        if (reads) {
                            Assert.InRange(texel, scaled.X, scaled.EndX - 1);
                        }
                    }
                }
            );
    }

    /// <summary>At one size the rectangle is only grown, by two texels on each side, and clipped to the image.</summary>
    [Fact]
    public void A_change_at_the_same_size_is_grown_and_clipped() {
        var scaled = TexturePlanEvaluator.Scaled(new(0, 10, 4, 4), new(64, 64), new(64, 64));

        Assert.Equal(new TextureRect(0, 8, 6, 8), scaled);
    }
}
