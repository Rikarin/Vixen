// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #218's hydraulic erosion, through its node on a device, against conservation and closed forms.</summary>
/// <remarks>
///     The oracles: ground and sediment together neither appear nor vanish; level ground has nowhere to send its
///     water, so it keeps its height to the bit and its water follows the rain and evaporation's geometric series;
///     and a hill loses ground at its top and gains it at its foot, where the water that ran off it stands.
/// </remarks>
public class TextureHydraulicErosionDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>A cone in the middle of level ground: 0.1 around it, 0.8 at the top.</summary>
    static float Hill(int x, int y) {
        var distance = MathF.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f));

        return 0.1f + 0.7f * MathF.Max(1f - distance / 20f, 0f);
    }

    static float Sum(Lanes picture) =>
        (float)Enumerable.Range(0, Side * Side).Sum(at => (double)picture.At(at % Side, at / Side, 0));

    [Fact]
    public void Erosion_conserves_ground_and_moves_it_from_a_hill_s_top_to_its_foot() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var hill = Grey(Side, Side, Hill);
        var before = Bake(
            device,
            "Surface/Hydraulic Erosion",
            Side,
            Side,
            [new("Input", hill, true)],
            static node => node.SetValue("Iterations", 1f)
        );
        var after = Bake(device, "Surface/Hydraulic Erosion", Side, Side, [new("Input", hill, true)]);
        var water = Bake(
            device,
            "Surface/Hydraulic Erosion",
            Side,
            Side,
            [new("Input", hill, true)],
            output: "Water"
        );

        // ⚠ The input goes in through eight bits, so the sums are compared with each other and not with Hill.
        output.WriteLine(
            $"material {Sum(before)} after one step and {Sum(after)} after 64; top {before.At(31, 31, 0)} then "
            + $"{after.At(31, 31, 0)}, foot {before.At(31, 53, 0)} then {after.At(31, 53, 0)}; water at the top "
            + $"{water.At(31, 31, 0)} and at the foot {water.At(31, 53, 0)}"
        );

        Assert.Equal(Sum(before), Sum(after), Sum(before) * 0.0002f);
        Assert.True(after.At(31, 31, 0) < before.At(31, 31, 0) - 0.01f, "the hill's top did not wear");
        Assert.True(after.At(31, 53, 0) > before.At(31, 53, 0) + 0.001f, "nothing settled at the hill's foot");
        Assert.True(water.At(31, 53, 0) > water.At(31, 31, 0), "the water did not run off the hill");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(40)]
    public void Level_ground_keeps_its_height_and_its_water_follows_the_rain(int steps) {
        using var device = TextureKernelHarness.Open();

        const float Rain = 0.02f;
        const float Evaporation = 0.1f;

        var level = Grey(Side, Side, static (_, _) => 0.4f);

        void Configure(GraphNode node) {
            node.SetValue("Iterations", steps);
            node.SetValue("Rain", Rain);
            node.SetValue("Evaporation", Evaporation);
        }

        var height = Bake(device, "Surface/Hydraulic Erosion", Side, Side, [new("Input", level, true)], Configure);
        var water = Bake(
            device,
            "Surface/Hydraulic Erosion",
            Side,
            Side,
            [new("Input", level, true)],
            Configure,
            output: "Water"
        );

        // After n steps of rain r and evaporation e, w = r(1 - e)(1 - (1 - e)^n) / e, and Water reads w·e/r.
        var expected = (1f - Evaporation) * (1f - MathF.Pow(1f - Evaporation, steps));

        for (var at = 0; at < Side * Side; at += 37) {
            Assert.Equal(Unit(0.4f), height.At(at % Side, at / Side, 0));
            Assert.Equal(expected, water.At(at % Side, at / Side, 0), 4);
        }
    }
}
