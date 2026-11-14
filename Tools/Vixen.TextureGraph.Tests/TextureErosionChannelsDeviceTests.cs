// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #283's erosion channels, through their node on a device, against closed forms.</summary>
/// <remarks>
///     The oracles: level ground drains nowhere, so nothing is cut; a valley gathers everything uphill of it into one
///     floor, so the channel runs along the floor and strongest at its outlet, and a slope far from the floor is
///     left alone; and the same bytes rolled by whole texels of the counting grid are cut into the same picture
///     rolled.
/// </remarks>
public class TextureErosionChannelsDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>
    ///     Water down the steepest way and nowhere else. ⚠ The meander is a hash of a texel's place on the counting
    ///     grid, as a noise's is, so the roll below holds of the drainage only without it.
    /// </summary>
    static readonly Action<GraphNode> Straight = static node => node.SetValue("Meander", 0f);

    [Fact]
    public void Level_ground_drains_nowhere_and_is_left_as_it_was() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var level = Grey(Side, Side, static (_, _) => 0.4f);
        var height = Bake(device, "Surface/Erosion Channels", Side, Side, [new("Input", level, true)]);
        var flow = Bake(device, "Surface/Erosion Channels", Side, Side, [new("Input", level, true)], output: "Flow");

        for (var at = 0; at < Side * Side; at += 29) {
            Assert.Equal(Unit(0.4f), height.At(at % Side, at / Side, 0));
            Assert.Equal(0f, flow.At(at % Side, at / Side, 0));
        }
    }

    /// <summary>
    ///     A valley falling toward its top row, its floor down the middle: every texel drains to the floor and the floor
    ///     to the top row, so the floor near the top counts nearly the whole picture and a high slope counts itself.
    /// </summary>
    [Fact]
    public void A_valley_gathers_into_one_channel_along_its_floor_strongest_at_its_outlet() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        static float Valley(int x, int y) => 0.5f * MathF.Abs(x - 31.5f) / 32f + 0.4f * y / Side;

        var valley = Grey(Side, Side, static (x, y) => Valley(x, y));
        var wires = new Wire[] { new("Input", valley, true) };
        var height = Bake(device, "Surface/Erosion Channels", Side, Side, wires, tiling: false);
        var flow = Bake(device, "Surface/Erosion Channels", Side, Side, wires, tiling: false, output: "Flow");

        output.WriteLine(
            $"flow at the outlet {flow.At(31, 2, 0)}, upstream {flow.At(31, 40, 0)}, on the slope {flow.At(4, 40, 0)}; "
            + $"height at the outlet {height.At(31, 2, 0)} against {Unit(Valley(31, 2))}"
        );

        Assert.True(flow.At(31, 2, 0) > 0.5f, "the floor at the outlet carries no channel");
        Assert.True(flow.At(31, 2, 0) > flow.At(31, 40, 0), "the channel is no stronger downstream");
        Assert.Equal(0f, flow.At(4, 40, 0));
        Assert.Equal(Unit(Valley(4, 40)), height.At(4, 40, 0));
        Assert.True(height.At(31, 2, 0) < Unit(Valley(31, 2)) - 0.05f, "the outlet's floor was not cut");
    }

    /// <summary>
    ///     An even slope drains in parallel runs down the fall line at no meander, and in runs that bend and merge with
    ///     one: Patina #290.
    /// </summary>
    /// <remarks>
    ///     At nought every texel drains to the one straight below it, so every column away from the sides gathers the
    ///     same water and the bottom row's flow is one number. With meander a run may take a diagonal, two runs that
    ///     meet carry the water of both, and the row's flow spreads. ⚠ The nought half is the instrument: it shows the
    ///     slope really does lay parallel runs, so the spread is the meander's and not the slope's.
    /// </remarks>
    [Fact]
    public void An_even_slope_drains_in_parallel_runs_until_they_meander_and_merge() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var slope = Grey(Side, Side, static (_, y) => 0.2f + 0.6f * y / Side);
        var wires = new Wire[] { new("Input", slope, true) };
        var straight = Bake(
            device,
            "Surface/Erosion Channels",
            Side,
            Side,
            wires,
            Straight,
            tiling: false,
            output: "Flow"
        );
        var wandering = Bake(
            device,
            "Surface/Erosion Channels",
            Side,
            Side,
            wires,
            static node => node.SetValue("Meander", 0.5f),
            tiling: false,
            output: "Flow"
        );

        static (float Low, float High) Spread(Lanes flow) {
            var columns = Enumerable.Range(8, Side - 16).Select(x => flow.At(x, 1, 0)).ToArray();

            return (columns.Min(), columns.Max());
        }

        var (low, high) = Spread(straight);
        var (wanderingLow, wanderingHigh) = Spread(wandering);

        output.WriteLine($"straight {low}..{high}, meandering {wanderingLow}..{wanderingHigh}");

        Assert.True(high - low <= 1f / 255f, $"the straight runs gather unequally: {low}..{high}");
        Assert.True(wanderingHigh - wanderingLow > 0.2f, $"the runs did not merge: {wanderingLow}..{wanderingHigh}");
    }

    /// <summary>
    ///     A bowl drains to its bottom with no outlet, and with one on its rim it fills and spills there: Patina #290.
    /// </summary>
    /// <remarks>
    ///     ⚠ The unwired half is the instrument: water in the bowl really does stop at its bottom and leaves the rim dry,
    ///     so a channel at the outlet is the fill's.
    /// </remarks>
    [Fact]
    public void A_bowl_spills_toward_its_outlet_only_when_one_is_wired() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var bowl = Grey(Side, Side, static (x, y) => 0.2f + 0.6f * MathF.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f)) / 45f);
        var outlet = Grey(Side, Side, static (x, y) => x < 2 && y is >= 30 and < 34 ? 1f : 0f);
        Action<GraphNode> calm = static node => {
            node.SetValue("Meander", 0f);
            node.SetValue("Smoothing", 0f);
        };
        var stopped = Bake(
            device,
            "Surface/Erosion Channels",
            Side,
            Side,
            [new("Input", bowl, true)],
            calm,
            tiling: false,
            output: "Flow"
        );
        var spilt = Bake(
            device,
            "Surface/Erosion Channels",
            Side,
            Side,
            [new("Input", bowl, true), new("Outlets", outlet, true)],
            calm,
            tiling: false,
            output: "Flow"
        );

        static float AtOutlet(Lanes flow) =>
            Enumerable.Range(28, 9).SelectMany(static _ => Enumerable.Range(0, 3), (y, x) => flow.At(x, y, 0)).Max();

        output.WriteLine(
            $"at the outlet {AtOutlet(stopped)} unwired and {AtOutlet(spilt)} wired; at the bottom "
            + $"{stopped.At(32, 32, 0)} unwired and {spilt.At(32, 32, 0)} wired"
        );

        Assert.True(stopped.At(32, 32, 0) > 0.5f, "the bowl's water does not gather at its bottom");
        Assert.True(AtOutlet(stopped) < 0.05f, "the rim carries a channel with no outlet to draw it");
        Assert.True(AtOutlet(spilt) > 0.3f, "the bowl does not spill toward its outlet");
        Assert.True(spilt.At(32, 32, 0) < stopped.At(32, 32, 0), "the filled bowl still gathers at its bottom");
    }

    /// <summary>
    ///     An input rolled by whole texels of the counting grid is cut into the same picture rolled, at a size the
    ///     node counts at and at one it halves, where a whole texel of the grid is two of the picture's.
    /// </summary>
    [Theory]
    [InlineData(48, 40, 17, 23)]
    [InlineData(512, 480, 34, 18)]
    public void An_input_rolled_by_whole_grid_texels_is_cut_into_the_same_picture_rolled(
        int width,
        int height,
        int byX,
        int byY
    ) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        float Ground(int x, int y) =>
            0.5f
            + 0.2f * MathF.Sin(2f * MathF.PI * 2f * x / width) * MathF.Cos(2f * MathF.PI * y / height)
            + 0.1f * MathF.Sin(2f * MathF.PI * (3f * x / width + 2f * y / height));

        var plain = Grey(width, height, Ground);
        var rolled = Grey(width, height, (x, y) => Ground((x + width - byX) % width, (y + height - byY) % height));

        foreach (var port in (string[])["Out", "Flow"]) {
            var first = Bake(
                device,
                "Surface/Erosion Channels",
                width,
                height,
                [new("Input", plain, true)],
                Straight,
                output: port
            );
            var second = Bake(
                device,
                "Surface/Erosion Channels",
                width,
                height,
                [new("Input", rolled, true)],
                Straight,
                output: port
            );
            var cut = 0;

            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    // ⚠ To a hundred-thousandth, not the bit: where a texel falls on a halved grid is a product rounded
                    // differently at a different column, which moved a cut height by a few ulps at 512.
                    Assert.Equal(first.At(x, y, 0), second.At((x + byX) % width, (y + byY) % height, 0), 1e-5f);
                    cut += port == "Flow" && first.At(x, y, 0) > 0f ? 1 : 0;
                }
            }

            // The instrument: a picture with channels in it, or the roll holds of nothing.
            Assert.True(port == "Out" || cut > width * height / 100, $"{width}x{height} cut {cut} texels");
        }
    }
}
