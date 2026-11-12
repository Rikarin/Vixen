// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #176's two compounds, through the published node types on a device.</summary>
public class TextureSurfaceCompoundDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>The split is exact: the broad shapes plus twice the detail less one is the picture again.</summary>
    [Fact]
    public void A_frequency_split_adds_back_up_to_its_input() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        Wire[] noise = [new("Input", [], true, "Noise/Clouds")];

        var low = Bake(device, "Utility/Frequency Split", Side, Side, noise, output: "Low");
        var high = Bake(device, "Utility/Frequency Split", Side, Side, noise, output: "High");
        var input = Bake(device, "Colour/Levels", Side, Side, [new("Input", [], true, "Noise/Clouds")]);

        var detail = 0.0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(input.At(x, y, 0), low.At(x, y, 0) + 2f * high.At(x, y, 0) - 1f, 0.0005f);

                detail += Math.Abs(high.At(x, y, 0) - 0.5f);
            }
        }

        // ⚠ The instrument: a split that put everything in Low and a flat half in High adds up too.
        Assert.True(detail / (Side * Side) > 0.005, $"the detail is {detail / (Side * Side)} about a half");
    }

    /// <summary>
    ///     Nought outside the shape, one deep inside it, and rising monotonically in between: the height
    ///     <c>Surface/Bevel</c> bends its normal by, which a layer can now use as a height.
    /// </summary>
    [Fact]
    public void A_bevels_height_rises_from_the_edge_to_one_over_its_width() {
        using var device = TextureKernelHarness.Open();

        // A square from 8 to 55. The default width reaches 0.004 + 0.15 · 0.2 of 64: a little over two texels.
        var square = Grey(Side, Side, static (x, y) => x is >= 8 and < 56 && y is >= 8 and < 56 ? 1f : 0f);

        var height = Bake(device, "Surface/Bevel Height", Side, Side, [new("Mask", square, true)], tiling: false);
        var wide = Bake(
            device,
            "Surface/Bevel Height",
            Side,
            Side,
            [new("Mask", square, true)],
            static node => node.SetText("width", "1"),
            tiling: false
        );

        Assert.Equal(0f, height.At(4, 32, 0));
        Assert.Equal(1f, height.At(32, 32, 0), 0.0001f);
        Assert.Equal(1f, height.At(20, 32, 0), 0.0001f);

        for (var x = 9; x < 32; x++) {
            Assert.True(wide.At(x, 32, 0) >= wide.At(x - 1, 32, 0), $"x {x}");
        }

        // The wide bevel reaches 13 texels in, so twenty in is the plateau and four in is part of the way.
        Assert.InRange(wide.At(12, 32, 0), 0.05f, 0.8f);
        Assert.Equal(1f, wide.At(28, 32, 0), 0.0001f);
    }
}
