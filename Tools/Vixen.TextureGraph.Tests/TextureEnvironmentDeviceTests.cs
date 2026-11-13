// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #180's nodes for graphs that make environments, each through its node on a device.</summary>
/// <remarks>
///     The oracles are closed forms rather than pictures: Preetham's sky reads exactly its zenith luminance straight
///     up and is brightest at its sun; a turn of a whole number of columns is a shift and a half turn of roll is the
///     picture upside down and mirrored; a hard light adds its intensity over 2π(1 − cos r) of solid angle; the
///     ground and the sky either side of the horizon share it evenly; and a stop doubles the light.
/// </remarks>
public class TextureEnvironmentDeviceTests(ITestOutputHelper output) {
    const int Wide = 256;
    const int High = 128;

    /// <summary>A smooth colour picture with distinct texels, so a shift or a flip is seen.</summary>
    static (float R, float G, float B, float A) Smooth(int x, int y) =>
        (0.5f + 0.3f * MathF.Sin(2f * MathF.PI * x / Wide), 0.2f + 0.6f * y / High,
            0.5f + 0.3f * MathF.Cos(2f * MathF.PI * (x + 2 * y) / Wide), 1f);

    static float Luminance(Lanes picture, int x, int y) =>
        0.2126f * picture.At(x, y, 0) + 0.7152f * picture.At(x, y, 1) + 0.0722f * picture.At(x, y, 2);

    /// <summary>Preetham's zenith luminance, in kilocandelas per square metre.</summary>
    static float Zenith(float turbidity, float sunTheta) {
        var chi = (4f / 9f - turbidity / 120f) * (MathF.PI - 2f * sunTheta);

        return (4.0453f * turbidity - 4.971f) * MathF.Tan(chi) - 0.2155f * turbidity + 2.4192f;
    }

    [Fact]
    public void A_sky_reads_its_zenith_luminance_straight_up_its_sun_where_it_stands_and_ground_below() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const float Azimuth = 1f;
        const float Elevation = 0.5f;

        var sky = Bake(
            device,
            "Environment/Sky",
            Wide,
            High,
            [],
            static node => {
                node.SetValue("Sun Azimuth", Azimuth);
                node.SetValue("Sun Elevation", Elevation);
                node.SetValue("Turbidity", 4f);
                node.SetValue("Intensity", 1f);
            },
            tiling: false
        );

        // Round the top row, θ is a fortieth of a degree: Perez's ratio there is one to a fraction of a percent.
        var top = Enumerable.Range(0, Wide).Average(x => Luminance(sky, x, 0));
        var expected = Zenith(4f, MathF.PI / 2f - Elevation);

        output.WriteLine($"zenith {top} against Preetham's {expected}");
        Assert.Equal(expected, top, expected * 0.02f);

        var (bestX, bestY) = (0, 0);

        for (var y = 0; y < High; y++) {
            for (var x = 0; x < Wide; x++) {
                if (Luminance(sky, x, y) > Luminance(sky, bestX, bestY)) {
                    (bestX, bestY) = (x, y);
                }
            }
        }

        var sunX = (Azimuth / (2f * MathF.PI) + 0.5f) * Wide - 0.5f;
        var sunY = (MathF.PI / 2f - Elevation) / MathF.PI * High - 0.5f;

        output.WriteLine($"brightest at ({bestX}, {bestY}), the sun at ({sunX}, {sunY})");
        Assert.InRange(bestX, sunX - 1.5f, sunX + 1.5f);
        Assert.InRange(bestY, sunY - 1.5f, sunY + 1.5f);

        // Straight down is the ground, untouched.
        Assert.Equal(0.1f, sky.At(17, High - 1, 0), 4);
        Assert.Equal(0.09f, sky.At(17, High - 1, 1), 4);
        Assert.Equal(0.08f, sky.At(17, High - 1, 2), 4);
    }

    [Fact]
    public void A_yaw_of_whole_columns_is_a_shift_and_a_half_roll_is_the_picture_turned_over() {
        using var device = TextureKernelHarness.Open();

        const int Columns = 37;

        var picture = Picture(Wide, High, Smooth);
        var before = Bake(device, "Environment/Panorama Rotate", Wide, High, [new("Input", picture)], tiling: false);
        var turned = Bake(
            device,
            "Environment/Panorama Rotate",
            Wide,
            High,
            [new("Input", picture)],
            static node => node.SetValue("Yaw", 2f * MathF.PI * Columns / Wide),
            tiling: false
        );
        var over = Bake(
            device,
            "Environment/Panorama Rotate",
            Wide,
            High,
            [new("Input", picture)],
            static node => node.SetValue("Roll", MathF.PI),
            tiling: false
        );

        for (var y = 0; y < High; y += 7) {
            for (var x = 0; x < Wide; x += 5) {
                for (var lane = 0; lane < 3; lane++) {
                    Assert.Equal(before.At(((x - Columns) % Wide + Wide) % Wide, y, lane), turned.At(x, y, lane), 3);
                    Assert.Equal(before.At(Wide - 1 - x, High - 1 - y, lane), over.At(x, y, lane), 0.02f);
                }
            }
        }
    }

    [Fact]
    public void A_hard_light_adds_its_intensity_over_its_solid_angle() {
        using var device = TextureKernelHarness.Open();

        const float Radius = 0.3f;

        var light = Bake(
            device,
            "Environment/Panorama Light",
            Wide,
            High,
            [new("Input", Solid(Wide, High, 0f, 0f, 0f))],
            static node => {
                node.SetValue("Size", Radius);
                node.SetValue("Softness", 0f);
                node.SetValue("Intensity", 1f);
                node.SetValue("Elevation", 0.3f);
            },
            tiling: false
        );

        var total = 0.0;

        for (var y = 0; y < High; y++) {
            var theta = (y + 0.5) / High * Math.PI;
            var texel = 2.0 * Math.PI / Wide * (Math.PI / High) * Math.Sin(theta);

            for (var x = 0; x < Wide; x++) {
                total += light.At(x, y, 0) * texel;
            }
        }

        var expected = 2.0 * Math.PI * (1.0 - Math.Cos(Radius));

        output.WriteLine($"light {total} against 2π(1 − cos r) = {expected}");
        Assert.Equal(expected, total, expected * 0.04);
    }

    [Fact]
    public void Ground_and_sky_share_the_horizon_evenly() {
        using var device = TextureKernelHarness.Open();

        var horizon = Bake(
            device,
            "Environment/Horizon",
            Wide,
            High,
            [new("Input", Solid(Wide, High, 1f, 1f, 1f))],
            tiling: false
        );

        Assert.Equal(1f, horizon.At(9, 0, 0));
        Assert.Equal(0.1f, horizon.At(9, High - 1, 0), 4);

        // The rows either side of the horizon sit at ±r, where the fade is s and 1 − s: together 1 + ground.
        Assert.Equal(1.1f, horizon.At(9, High / 2 - 1, 0) + horizon.At(9, High / 2, 0), 4);
        Assert.True(horizon.At(9, High / 2 - 1, 0) > horizon.At(9, High / 2, 0), "the sky is not above the ground");
    }

    [Fact]
    public void A_stop_doubles_the_light_and_nothing_clamps() {
        using var device = TextureKernelHarness.Open();

        var grey = Solid(8, 8, 0.4f, 0.2f, 0.6f, 0.5f);
        var brighter = Bake(
            device,
            "Colour/Exposure",
            8,
            8,
            [new("Input", grey)],
            static node => node.SetValue("Stops", 2f)
        );
        var darker = Bake(
            device,
            "Colour/Exposure",
            8,
            8,
            [new("Input", grey)],
            static node => node.SetValue("Stops", -1f)
        );

        Assert.Equal(4f * Unit(0.4f), brighter.At(3, 3, 0), 4);
        Assert.Equal(4f * Unit(0.6f), brighter.At(3, 3, 2), 4);
        Assert.Equal(Unit(0.5f), brighter.At(3, 3, 3), 4);
        Assert.Equal(0.5f * Unit(0.2f), darker.At(3, 3, 1), 4);
    }
}
