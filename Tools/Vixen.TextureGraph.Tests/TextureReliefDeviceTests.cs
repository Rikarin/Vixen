// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #176's height shadow and normal mix, each through its node on a device, against a closed form.</summary>
public class TextureReliefDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>A wall of height one across the image, x 40..43, on flat ground of nought.</summary>
    static readonly byte[] Wall = Grey(Side, Side, (x, _) => x is >= 40 and < 44 ? 1f : 0f);

    /// <summary>
    ///     A wall of height one, a height scale of a quarter at 64 texels — sixteen texels tall — and a light to
    ///     the right at 45°: the shadow is sixteen texels long, left of the wall, and the ground past it is lit.
    /// </summary>
    [Fact]
    public void A_wall_casts_a_shadow_as_long_as_its_height_over_the_tangent_of_the_light() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var shadow = Shadow(device, MathF.PI / 4f, 0f);

        for (var y = 0; y < Side; y += 9) {
            // ⚠ x = 24 is exactly sixteen texels from the wall, where the horizon equals the elevation: a
            // tie either way is right, so it is not asserted.
            for (var x = 0; x < 24; x++) {
                Assert.Equal(1f, shadow.At(x, y, 0));
            }

            for (var x = 25; x < 40; x++) {
                Assert.Equal(0f, shadow.At(x, y, 0));
            }

            for (var x = 40; x < Side; x++) {
                Assert.Equal(1f, shadow.At(x, y, 0));
            }
        }

        // Twice the elevation's tangent, half the shadow: atan(2) puts the edge eight texels out.
        var steeper = Shadow(device, MathF.Atan(2f), 0f);

        Assert.Equal(1f, steeper.At(31, 10, 0));
        Assert.Equal(0f, steeper.At(33, 10, 0));
    }

    [Fact]
    public void A_light_overhead_or_behind_the_wall_casts_nothing_toward_it() {
        using var device = TextureKernelHarness.Open();

        var overhead = Shadow(device, MathF.PI / 2f, 0f);

        // The light to the left: the wall shadows the ground to its right instead.
        var behind = Shadow(device, MathF.PI / 4f, MathF.PI);

        for (var x = 0; x < Side; x++) {
            Assert.Equal(1f, overhead.At(x, 20, 0));
        }

        Assert.Equal(1f, behind.At(30, 20, 0));
        Assert.Equal(0f, behind.At(50, 20, 0));
        Assert.Equal(1f, behind.At(62, 20, 0));
    }

    [Fact]
    public void Softness_turns_the_edge_into_a_ramp_that_falls_toward_the_wall() {
        using var device = TextureKernelHarness.Open();

        var soft = Shadow(device, MathF.PI / 4f, 0f, softness: 0.3f);

        // Monotone non-increasing from the open ground to the wall's foot, and partly lit at the old edge.
        for (var x = 1; x < 40; x++) {
            Assert.True(soft.At(x, 20, 0) <= soft.At(x - 1, 20, 0) + 0.0001f, $"x {x}");
        }

        Assert.InRange(soft.At(24, 20, 0), 0.3f, 0.7f);
        Assert.Equal(0f, soft.At(39, 20, 0));
    }

    [Fact]
    public void A_shadow_over_the_seam_lands_on_the_other_side_where_the_graph_tiles() {
        using var device = TextureKernelHarness.Open();

        // The wall at the left edge, the light to the right: the ground left of it is the right edge.
        var edge = Grey(Side, Side, (x, _) => x < 4 ? 1f : 0f);
        var tiled = Bake(device, "Surface/Height Shadow", Side, Side, [new("Height", edge, Grey: true)], Light(MathF.PI / 4f, 0f, 0f));
        var clamped = Bake(
            device,
            "Surface/Height Shadow",
            Side,
            Side,
            [new("Height", edge, Grey: true)],
            Light(MathF.PI / 4f, 0f, 0f),
            tiling: false
        );

        Assert.Equal(0f, tiled.At(Side - 4, 20, 0));
        Assert.Equal(1f, clamped.At(Side - 4, 20, 0));
    }

    static Action<Vixen.NodeGraph.GraphNode> Light(float elevation, float angle, float softness) =>
        node => {
            node.SetValue("Elevation", elevation);
            node.SetValue("Angle", angle);
            node.SetValue("Height Scale", 0.25f);
            node.SetValue("Softness", softness);
        };

    static TextureNodeBench.Lanes Shadow(Vixen.Graphics.Vulkan.VulkanDevice device, float elevation, float angle, float softness = 0f) =>
        Bake(device, "Surface/Height Shadow", Side, Side, [new("Height", Wall, Grey: true)], Light(elevation, angle, softness), tiling: false);

    /// <summary>A normal leaning right, (0.6, 0, 0.8), and one leaning down, (0, 0.6, 0.8), encoded.</summary>
    static readonly byte[] Right = Solid(Side, Side, 0.8f, 0.5f, 0.9f);

    static readonly byte[] Down = Solid(Side, Side, 0.5f, 0.8f, 0.9f);

    [Fact]
    public void A_normal_mix_is_each_map_at_its_ends_and_the_unit_mean_direction_between() {
        using var device = TextureKernelHarness.Open();

        // A mask that is nought on the left quarter, one on the right quarter and a half between.
        var mask = Grey(Side, Side, (x, _) => x < 16 ? 0f : x >= 48 ? 1f : 0.5f);

        var mixed = Bake(
            device,
            "Surface/Normal Blend",
            Side,
            Side,
            [new("Background", Right), new("Foreground", Down), new("Mask", mask, Grey: true)]
        );

        var right = Unit(Decode(Right));
        var down = Unit(Decode(Down));
        var half = Unit((right.X + down.X, right.Y + down.Y, right.Z + down.Z));

        Near(right, Decode(mixed.Texel(5, 5)));
        Near(down, Decode(mixed.Texel(60, 5)));
        Near(half, Decode(mixed.Texel(30, 5)));

        // ⚠ The instrument: a plain colour mix lands somewhere else, shorter than one.
        var plain = (X: (right.X + down.X) / 2f, Y: (right.Y + down.Y) / 2f, Z: (right.Z + down.Z) / 2f);

        Assert.True(MathF.Sqrt((plain.X * plain.X) + (plain.Y * plain.Y) + (plain.Z * plain.Z)) < 0.99f);

        var length = Decode(mixed.Texel(30, 5));

        Assert.Equal(1f, MathF.Sqrt((length.X * length.X) + (length.Y * length.Y) + (length.Z * length.Z)), 0.002f);

        // Unmasked, the opacity alone decides, everywhere.
        var faint = Bake(
            device,
            "Surface/Normal Blend",
            Side,
            Side,
            [new("Background", Right), new("Foreground", Down)],
            node => node.SetValue("Opacity", 0.5f)
        );

        Near(half, Decode(faint.Texel(5, 5)));
        Near(half, Decode(faint.Texel(60, 60)));
    }

    static (float X, float Y, float Z) Decode(byte[] picture) =>
        ((picture[0] / 255f * 2f) - 1f, (picture[1] / 255f * 2f) - 1f, (picture[2] / 255f * 2f) - 1f);

    static (float X, float Y, float Z) Decode((float R, float G, float B, float A) texel) =>
        ((texel.R * 2f) - 1f, (texel.G * 2f) - 1f, (texel.B * 2f) - 1f);

    static (float X, float Y, float Z) Unit((float X, float Y, float Z) v) {
        var length = MathF.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));

        return (v.X / length, v.Y / length, v.Z / length);
    }

    static void Near((float X, float Y, float Z) expected, (float X, float Y, float Z) actual) {
        Assert.Equal(expected.X, actual.X, 0.003f);
        Assert.Equal(expected.Y, actual.Y, 0.003f);
        Assert.Equal(expected.Z, actual.Z, 0.003f);
    }

    /// <summary>
    ///     Patina #168's height extrude: a block of height 0.8 pushed right keeps its height along the push at no
    ///     falloff, falls linearly to nought at the full length at a falloff of one, and leaves the side it moves
    ///     away from alone.
    /// </summary>
    [Fact]
    public void A_height_extrudes_along_its_angle_and_falls_by_its_falloff() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // A block over x 20..27; a length of a quarter of 64 is sixteen texels.
        var block = Grey(Side, Side, (x, y) => x is >= 20 and < 28 && y is >= 20 and < 44 ? 0.8f : 0f);

        TextureNodeBench.Lanes Extrude(float falloff) =>
            Bake(
                device,
                "Surface/Height Extrude",
                Side,
                Side,
                [new("Height", block, Grey: true)],
                node => {
                    node.SetValue("Angle", 0f);
                    node.SetValue("Length", 0.25f);
                    node.SetValue("Falloff", falloff);
                },
                tiling: false
            );

        var prism = Extrude(0f);
        var slope = Extrude(1f);

        Assert.Equal(0.8f, prism.At(24, 30, 0), 0.01f);
        Assert.Equal(0.8f, prism.At(27 + 12, 30, 0), 0.01f);
        Assert.Equal(0f, prism.At(27 + 18, 30, 0), 0.01f);
        Assert.Equal(0f, prism.At(15, 30, 0), 0.01f);
        Assert.Equal(0f, prism.At(35, 50, 0), 0.01f);

        // Eight texels past the block's last column the nearest block texel is eight back: half the length.
        Assert.Equal(0.8f * (1f - (8f / 16f)), slope.At(27 + 8, 30, 0), 0.03f);
        Assert.Equal(0.8f, slope.At(24, 30, 0), 0.01f);
    }
}
