// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #116's area fill by colour: every flat area of a picture filled on its own.</summary>
/// <remarks>
///     The oracle: a picture of three flat areas, and a fourth of the first area's colour cut off from it, comes back
///     as four constants, one per area, each different, the two areas of one colour told apart because they do not
///     touch. ⚠ The texels along a line between two areas are the mask's gaps, filled from the nearest area, which
///     is a tie between the two: the line may land a texel either side, so a texel beside another area is not read.
/// </remarks>
public class TextureAreaFillDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>Which area a texel is in: red on the left, green and blue on the right, and a red square in the blue.</summary>
    static int Area(int x, int y) =>
        x is >= 44 and < 56 && y is >= 44 and < 56 ? 3
        : x < Side / 2 ? 0
        : y < Side / 2 ? 1
        : 2;

    static readonly (float R, float G, float B)[] Colours = [(0.9f, 0.1f, 0.1f), (0.1f, 0.8f, 0.2f), (0.2f, 0.3f, 0.9f),
        (0.9f, 0.1f, 0.1f)];

    [Theory]
    [InlineData("Random")]
    [InlineData("Colour")]
    public void Each_flat_area_is_one_value_and_no_two_areas_share_one(string kind) {
        using var device = TextureKernelHarness.Open();

        var picture = Picture(
            Side,
            Side,
            static (x, y) => Colours[Area(x, y)] is var (r, g, b) ? (r, g, b, 1f) : default
        );
        var filled = Bake(
            device,
            "Utility/Area Fill",
            Side,
            Side,
            [new("Input", picture)],
            node => node.SetText("Kind", kind),
            tiling: false
        );
        var values = new float[4];

        for (var area = 0; area < values.Length; area++) {
            values[area] = float.NaN;
        }

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var area = Area(x, y);
                var value = filled.At(x, y, 0);

                if (Area(Math.Max(x - 1, 0), y) != area || Area(Math.Min(x + 1, Side - 1), y) != area
                    || Area(x, Math.Max(y - 1, 0)) != area || Area(x, Math.Min(y + 1, Side - 1)) != area) {
                    continue;
                }

                if (float.IsNaN(values[area])) {
                    values[area] = value;
                }

                Assert.True(
                    MathF.Abs(values[area] - value) < 1e-5f,
                    $"area {area} reads {values[area]} and {value} at ({x}, {y})"
                );
            }
        }

        output.WriteLine($"{kind}: {string.Join(", ", values)}");

        Assert.Equal(4, values.Distinct().Count());
    }
}
