// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #115's Pixelate and #116's Smooth Bevel, each against a closed form.</summary>
public class TexturePixelateBevelDeviceTests(ITestOutputHelper output) {
    const int Side = 128;

    /// <summary>
    ///     Pixelate cuts an exact ramp into flat blocks, each the ramp's value at the block's middle — at a count that is
    ///     not a power of two, which a <c>Resample</c> chain could not give.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(5)]
    public void Pixelate_holds_each_block_at_its_middle(int blocks) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = Bake(
            device,
            "Adjust/Pixelate",
            Side,
            Side,
            [new("Input", [], Source: "Source/Gradient")],
            node => {
                node.SetText("across", blocks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                node.SetText("down", "3");
            }
        );

        var width = (float)Side / blocks;
        var checkedTexels = 0;

        for (var x = 0; x < Side; x++) {
            var block = (int)((x + 0.5f) / width);
            var start = block * width;

            // ⚠ The texels either side of a block's edge are left alone: a sampled middle can round to either.
            if (x + 0.5f - start < 1f || start + width - (x + 0.5f) < 1f) {
                continue;
            }

            var middle = (block + 0.5f) / blocks;

            for (var y = 0; y < Side; y += 9) {
                Assert.Equal(middle, picture.At(x, y, 0), 0.01f);
                checkedTexels++;
            }
        }

        Assert.True(checkedTexels > Side * 5, $"Only {checkedTexels} texels were examined.");
    }

    /// <summary>
    ///     A smooth bevel is nought outside the shape and one deep inside, as Bevel Height is, and has no crease where
    ///     the rise meets the top: the sharpest bend along a line through it is well under Bevel Height's.
    /// </summary>
    [Fact]
    public void A_smooth_bevel_rises_without_a_crease() {
        using var device = TextureKernelHarness.Open();

        // A square 96 texels wide, so its top is flat and the rise is straight-sided away from the corners.
        var square = Grey(Side, Side, static (x, y) => x is >= 16 and < 112 && y is >= 16 and < 112 ? 1f : 0f);
        Wire[] mask = [new("Mask", square, true)];

        var sharp = Bake(device, "Surface/Bevel Height", Side, Side, mask, Bevel);
        var smooth = Bake(device, "Surface/Smooth Bevel", Side, Side, mask, Bevel);

        Assert.Equal(0f, smooth.At(4, 64, 0), 0.01f);
        Assert.Equal(1f, smooth.At(64, 64, 0), 0.02f);

        // The rise goes up and never back down on the way in.
        for (var x = 17; x < 64; x++) {
            Assert.True(smooth.At(x, 64, 0) >= smooth.At(x - 1, 64, 0) - 0.002f, $"It falls at {x}.");
        }

        Assert.True(
            Bend(smooth) < Bend(sharp) * 0.5f,
            $"The smooth bevel bends by {Bend(smooth)}, Bevel Height by {Bend(sharp)}."
        );
    }

    /// <summary>A rise of 0.004 + 0.5 · 0.2 of 128 texels, about thirteen, at a middling profile.</summary>
    static void Bevel(GraphNode node) {
        node.SetText("width", "0.5");
        node.SetText("profile", "0.5");
    }

    /// <summary>The largest second difference along the middle row, inside the shape.</summary>
    static float Bend(Lanes picture) {
        var largest = 0f;

        for (var x = 20; x < 108; x++) {
            var bend = MathF.Abs(picture.At(x + 1, 64, 0) - 2f * picture.At(x, 64, 0) + picture.At(x - 1, 64, 0));

            largest = MathF.Max(largest, bend);
        }

        return largest;
    }
}
