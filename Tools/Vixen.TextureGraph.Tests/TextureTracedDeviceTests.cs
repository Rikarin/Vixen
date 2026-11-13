// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>
///     Patina #176's traced occlusion, bent normal and irradiance, each through its node on a device, against a
///     closed form.
/// </summary>
/// <remarks>
///     Two closed forms. Flat ground sees all of the sky. At the foot of a cliff of unbounded height half the sky is
///     gone, because every ray comes with its opposite and exactly one of the two faces the cliff; and the
///     cosine-weighted mean of the half that remains is <c>(4 / 3π, 0, 2 / 3)</c>, which normalised leans
///     <c>(0.537, 0, 0.844)</c> away from the cliff.
/// </remarks>
public class TextureTracedDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>Nothing but ground.</summary>
    static readonly byte[] Flat = Grey(Side, Side, static (_, _) => 0f);

    /// <summary>A plateau of height one on the left half, ground on the right: a cliff along x = 32.</summary>
    static readonly byte[] Cliff = Grey(Side, Side, static (x, _) => x < Side / 2 ? 1f : 0f);

    /// <summary>The cliff a hundred image widths tall, which no ray that meets it can clear.</summary>
    static Action<Vixen.NodeGraph.GraphNode> Tall(int samples = 64) =>
        node => {
            node.SetValue("Height Scale", 100f);
            node.SetValue("Radius", 64f);
            node.SetValue("Samples", samples);
        };

    static Lanes Trace(Vixen.Graphics.Vulkan.VulkanDevice device, string path, byte[] height, int samples = 64) =>
        Bake(device, path, Side, Side, [new("Height", height, true)], Tall(samples), tiling: false);

    [Fact]
    public void Flat_ground_sees_all_of_the_sky_and_its_bent_normal_is_straight_up() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var occlusion = Trace(device, "Surface/Traced Occlusion", Flat);
        var bent = Trace(device, "Surface/Bent Normal", Flat);

        for (var y = 0; y < Side; y += 7) {
            for (var x = 0; x < Side; x += 5) {
                Assert.Equal(1f, occlusion.At(x, y, 0));
                Assert.Equal(0.5f, bent.At(x, y, 0), 0.001f);
                Assert.Equal(0.5f, bent.At(x, y, 1), 0.001f);
                Assert.Equal(1f, bent.At(x, y, 2), 0.001f);
            }
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(8)]
    public void The_foot_of_a_cliff_sees_half_the_sky_and_the_plateau_all_of_it(int samples) {
        using var device = TextureKernelHarness.Open();

        var occlusion = Trace(device, "Surface/Traced Occlusion", Cliff, samples);

        for (var y = 8; y < Side - 8; y += 9) {
            // Every pair has one ray toward the cliff and one away, so half, whatever the count.
            Assert.Equal(0.5f, occlusion.At(Side / 2 + 1, y, 0), 0.02f);

            // Nothing on the plateau stands above it.
            Assert.Equal(1f, occlusion.At(10, y, 0));

            // Far out on the ground the cliff is still there, but lower in the sky.
            Assert.True(occlusion.At(Side - 2, y, 0) > occlusion.At(Side / 2 + 1, y, 0), $"y {y}");
        }
    }

    [Fact]
    public void The_bent_normal_at_the_foot_of_a_cliff_leans_away_by_the_cosine_weighted_mean() {
        using var device = TextureKernelHarness.Open();

        var bent = Trace(device, "Surface/Bent Normal", Cliff);

        // (4 / 3π, 0, 2 / 3) normalised.
        var across = 4f / (3f * MathF.PI);
        var up = 2f / 3f;
        var length = MathF.Sqrt(across * across + up * up);

        for (var y = 16; y < Side - 16; y += 8) {
            Assert.Equal(0.5f + 0.5f * across / length, bent.At(Side / 2 + 1, y, 0), 0.03f);
            Assert.Equal(0.5f, bent.At(Side / 2 + 1, y, 1), 0.03f);
            Assert.Equal(0.5f + 0.5f * up / length, bent.At(Side / 2 + 1, y, 2), 0.03f);
        }
    }

    [Fact]
    public void Irradiance_is_the_sky_where_rays_get_out_and_the_light_where_they_strike() {
        using var device = TextureKernelHarness.Open();

        // Under a white sky with nothing wired to Light, the occlusion in every lane.
        var occlusion = Trace(device, "Surface/Traced Occlusion", Cliff);
        var white = Trace(device, "Surface/Irradiance", Cliff);

        for (var x = 0; x < Side; x += 3) {
            Assert.Equal(occlusion.At(x, 30, 0), white.At(x, 30, 0), 0.001f);
            Assert.Equal(occlusion.At(x, 30, 0), white.At(x, 30, 2), 0.001f);
        }

        // A black sky and a cliff face lit orange: the foot gets half the orange, the plateau nothing.
        var orange = Solid(Side, Side, 1f, 0.6f, 0.2f);
        var bounced = Bake(
            device,
            "Surface/Irradiance",
            Side,
            Side,
            [new("Height", Cliff, true), new("Light", orange)],
            node => {
                Tall()(node);
                node.SetValue("Sky", 0f, 0f, 0f, 1f);
            },
            tiling: false
        );

        Assert.Equal(0.5f * Unit(1f), bounced.At(Side / 2 + 1, 30, 0), 0.02f);
        Assert.Equal(0.5f * Unit(0.6f), bounced.At(Side / 2 + 1, 30, 1), 0.02f);
        Assert.Equal(0.5f * Unit(0.2f), bounced.At(Side / 2 + 1, 30, 2), 0.02f);
        Assert.Equal(0f, bounced.At(10, 30, 0));
    }
}
