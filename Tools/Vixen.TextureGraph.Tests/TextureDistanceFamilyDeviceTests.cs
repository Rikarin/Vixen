// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #174's distance metrics, the colour a distance spreads, and a flood fill's colour and ramp.</summary>
/// <remarks>
///     Each through its node on a device, against a closed form: a single lit texel's distance under each
///     metric is that metric's length exactly, a spread colour is the colour of the nearer of two shapes, and
///     a ramp across a rectangle is its position along the direction over the rectangle's projected length.
/// </remarks>
public class TextureDistanceFamilyDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>One lit texel at (20, 30).</summary>
    static readonly byte[] Dot = Grey(Side, Side, (x, y) => x == 20 && y == 30 ? 1f : 0f);

    [Theory]
    [InlineData("Euclidean")]
    [InlineData("Manhattan")]
    [InlineData("Chebyshev")]
    public void A_single_texels_distance_is_the_metrics_length(string metric) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const float MaxDistance = 0.5f;

        var field = Bake(
            device,
            "Analysis/Distance",
            Side,
            Side,
            [new("Mask", Dot, Grey: true)],
            node => {
                node.SetText("Metric", metric);
                node.SetValue("Max Distance", MaxDistance);
            },
            tiling: false
        );

        var reach = MaxDistance * Side;
        var differs = false;

        foreach (var (x, y) in ((int, int)[])[(20, 30), (25, 30), (24, 33), (10, 22), (28, 39), (31, 25)]) {
            var (dx, dy) = (Math.Abs(x - 20), Math.Abs(y - 30));
            var expected = metric switch {
                "Manhattan" => dx + dy,
                "Chebyshev" => Math.Max(dx, dy),
                _ => MathF.Sqrt((dx * dx) + (dy * dy))
            };

            Assert.Equal(Math.Min(expected / reach, 1f), field.At(x, y, 0), 0.0005f);
            differs |= Math.Abs(expected - MathF.Sqrt((dx * dx) + (dy * dy))) > 1f;
        }

        // The instrument: the texels above have to tell the three metrics apart, or a kernel ignoring the
        // setting would pass all three cases.
        Assert.Equal(metric != "Euclidean", differs);
    }

    [Fact]
    public void A_source_spreads_the_colour_of_the_nearer_shape_and_fades_with_the_distance() {
        using var device = TextureKernelHarness.Open();

        // Two lit squares: x 8..11 red, x 52..55 blue, both y 30..33.
        var mask = Grey(Side, Side, (x, y) => y is >= 30 and <= 33 && (x is >= 8 and <= 11 || x is >= 52 and <= 55) ? 1f : 0f);
        // ⚠ Red only left of x = 16, so a texel at x = 20 that comes back red read the colour at the shape and
        // not the colour under itself, which is blue.
        var colour = Picture(Side, Side, (x, _) => x < 16 ? (1f, 0f, 0f, 1f) : (0f, 0f, 1f, 1f));

        const float MaxDistance = 0.25f;

        Wire[] wires = [new("Mask", mask, Grey: true), new("Source", colour)];

        var spread = Bake(device, "Analysis/Distance Colour", Side, Side, wires, node => node.SetValue("Max Distance", MaxDistance), tiling: false);
        var faded = Bake(
            device,
            "Analysis/Distance Colour",
            Side,
            Side,
            wires,
            node => {
                node.SetValue("Max Distance", MaxDistance);
                node.SetValue("Fade", 1f);
            },
            tiling: false
        );

        // Inside and near each square: its own colour, whole.
        Assert.Equal((1f, 0f, 0f, 1f), spread.Texel(10, 31));
        Assert.Equal((1f, 0f, 0f, 1f), spread.Texel(20, 31));
        Assert.Equal((0f, 0f, 1f, 1f), spread.Texel(44, 32));

        // Beyond the reach (16 texels) of both: transparent black.
        Assert.Equal((0f, 0f, 0f, 0f), spread.Texel(31, 5));

        // Faded: the colour times one less the distance over the reach.
        Assert.Equal(1f - (8f / (MaxDistance * Side)), faded.At(19, 31, 0), 0.0005f);
        Assert.Equal(1f, faded.At(10, 31, 0), 0.0005f);
        Assert.Equal(0f, faded.At(19, 31, 2), 0.0005f);
    }

    /// <summary>Two islands: a 12×6 bar at (4, 4) and a 6×10 block at (30, 20). Background elsewhere.</summary>
    static readonly byte[] Islands = Grey(
        Side,
        Side,
        (x, y) => (x is >= 4 and < 16 && y is >= 4 and < 10) || (x is >= 30 and < 36 && y is >= 20 and < 30) ? 1f : 0f
    );

    [Fact]
    public void An_islands_colour_is_one_colour_and_its_red_is_the_random_value() {
        using var device = TextureKernelHarness.Open();

        var colour = Bake(device, "Analysis/Flood Fill", Side, Side, [new("Mask", Islands, Grey: true)], node => node.SetText("Kind", "Colour"), tiling: false);
        var random = Bake(device, "Analysis/Flood Fill", Side, Side, [new("Mask", Islands, Grey: true)], tiling: false);

        var bar = colour.Texel(4, 4);
        var block = colour.Texel(30, 20);

        for (var y = 4; y < 10; y++) {
            for (var x = 4; x < 16; x++) {
                Assert.Equal(bar, colour.Texel(x, y));
            }
        }

        Assert.Equal(block, colour.Texel(35, 29));
        Assert.NotEqual(bar, block);

        // Three lanes that are not one grey, and a red that is the Random kind's value under the same seed.
        Assert.False(Math.Abs(bar.R - bar.G) < 0.001f && Math.Abs(bar.G - bar.B) < 0.001f);
        Assert.Equal(random.At(4, 4, 0), bar.R, 0.0001f);
        Assert.Equal(random.At(30, 20, 0), block.R, 0.0001f);
        Assert.Equal((0f, 0f, 0f, 1f), colour.Texel(50, 50));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(MathF.PI / 2f)]
    [InlineData(MathF.PI)]
    public void A_ramp_runs_across_its_islands_box_along_the_angle(float angle) {
        using var device = TextureKernelHarness.Open();

        var ramp = Bake(
            device,
            "Analysis/Flood Fill",
            Side,
            Side,
            [new("Mask", Islands, Grey: true)],
            node => {
                node.SetText("Kind", "Gradient");
                node.SetValue("Angle", angle);
            },
            tiling: false
        );

        foreach (var (x0, y0, w, h) in ((int, int, int, int)[])[(4, 4, 12, 6), (30, 20, 6, 10)]) {
            for (var y = y0; y < y0 + h; y++) {
                for (var x = x0; x < x0 + w; x++) {
                    var along = (x - x0 + 0.5f) / w;
                    var down = (y - y0 + 0.5f) / h;
                    var expected = angle switch {
                        0f => along,
                        MathF.PI => 1f - along,
                        _ => down
                    };

                    Assert.Equal(expected, ramp.At(x, y, 0), 0.001f);
                }
            }
        }

        Assert.Equal(0f, ramp.At(50, 50, 0));
    }

    [Fact]
    public void Angle_variation_turns_each_island_its_own_way() {
        using var device = TextureKernelHarness.Open();

        // Two identical squares, so only the variation can make their ramps differ.
        var twins = Grey(Side, Side, (x, y) => y is >= 10 and < 20 && (x is >= 5 and < 15 || x is >= 40 and < 50) ? 1f : 0f);

        TextureNodeBench.Lanes Ramp(float variation) =>
            Bake(
                device,
                "Analysis/Flood Fill",
                Side,
                Side,
                [new("Mask", twins, Grey: true)],
                node => {
                    node.SetText("Kind", "Gradient");
                    node.SetValue("Angle Variation", variation);
                },
                tiling: false
            );

        var still = Ramp(0f);
        var turned = Ramp(MathF.Tau);

        var stillDiffers = false;
        var turnedDiffers = false;

        for (var y = 10; y < 20; y++) {
            for (var x = 0; x < 10; x++) {
                stillDiffers |= Math.Abs(still.At(5 + x, y, 0) - still.At(40 + x, y, 0)) > 0.001f;
                turnedDiffers |= Math.Abs(turned.At(5 + x, y, 0) - turned.At(40 + x, y, 0)) > 0.05f;
            }
        }

        Assert.False(stillDiffers);
        Assert.True(turnedDiffers);
    }

    /// <summary>
    ///     Patina #174's directional distance: a texel's distance from the shape measured back against the angle,
    ///     the walked length over the reach, and one wherever the walk never meets the shape.
    /// </summary>
    /// <remarks>
    ///     One lit column at x = 10. Along angle nought a texel to the right of it is its distance from the column
    ///     over the reach, and every texel to its left is one, because walking left never reaches it. ⚠ The
    ///     second half is what tells this from <c>Analysis/Distance</c>, which would draw the left side as
    ///     close as the right.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(MathF.PI)]
    public void A_directional_distance_measures_only_against_its_angle(float angle) {
        using var device = TextureKernelHarness.Open();

        const float MaxDistance = 0.5f;

        var column = Grey(Side, Side, (x, _) => x == 32 ? 1f : 0f);
        var field = Bake(
            device,
            "Analysis/Directional Distance",
            Side,
            Side,
            [new("Mask", column, Grey: true)],
            node => {
                node.SetValue("Angle", angle);
                node.SetValue("Max Distance", MaxDistance);
            },
            tiling: false
        );

        var reach = MaxDistance * Side;
        var downstream = angle == 0f ? 1 : -1;

        Assert.Equal(0f, field.At(32, 20, 0), 0.0005f);

        foreach (var steps in (int[])[3, 7, 12, 20]) {
            var x = 32 + (downstream * steps);

            Assert.Equal(steps / reach, field.At(x, 20, 0), 0.04f);
            Assert.Equal(1f, field.At(32 - (downstream * steps), 20, 0), 0.0005f);
        }
    }

    /// <summary>On a tiling graph the walk wraps, so a streak leaving the right edge comes in at the left.</summary>
    [Fact]
    public void A_directional_distance_wraps_on_a_tiling_graph() {
        using var device = TextureKernelHarness.Open();

        var column = Grey(Side, Side, (x, _) => x == 60 ? 1f : 0f);
        var field = Bake(device, "Analysis/Directional Distance", Side, Side, [new("Mask", column, Grey: true)]);

        // Eight texels past the column, four of them across the seam.
        Assert.Equal(8f / (0.25f * Side), field.At(4, 30, 0), 0.04f);
    }
}
