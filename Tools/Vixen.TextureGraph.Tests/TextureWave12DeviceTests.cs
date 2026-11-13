// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TextureGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>
///     Patina wave 12's nodes through their graphs on a device: #119's anisotropic painterly smooth and environment
///     light, and #178's fill. Each is baked at a size that is not a power of two as well as one that is, because a
///     wrap or a pyramid that assumed one is right at 64 and wrong at 90.
/// </summary>
public class TextureWave12DeviceTests(ITestOutputHelper output) {
    /// <summary>The sizes every check is made at: one a power of two, and two that are not.</summary>
    public static TheoryData<int, int> Sizes => new() { { 64, 64 }, { 90, 70 }, { 75, 48 } };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void An_anisotropic_smooth_keeps_a_step_exactly_where_it_was(int width, int height) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var half = width / 2;
        var step = Bake(
            device,
            "Filters/Anisotropic Painterly Smooth",
            width,
            height,
            [new("Input", Grey(width, height, (x, _) => x < half ? 0f : 1f))]
        );

        var worst = (Error: 0f, X: 0, Y: 0);

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var error = Math.Abs((x < half ? 0f : 1f) - step.At(x, y, 0));

                if (error > worst.Error) {
                    worst = (error, x, y);
                }
            }
        }

        Assert.True(worst.Error < 0.002f, $"{worst.Error} at ({worst.X}, {worst.Y}) of {width}×{height}");
    }

    /// <summary>
    ///     Stripes with grain in them: along a stripe the window stretches, so the grain is averaged away and the
    ///     stripe keeps its own value. A filter that did nothing leaves the grain; one that blurred across the
    ///     stripes moves their values toward the middle.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public void An_anisotropic_smooth_flattens_grain_along_stripes_and_keeps_the_stripes(int width, int height) {
        using var device = TextureKernelHarness.Open();

        static float Stripe(int y) => y / 8 % 2 == 0 ? 0.25f : 0.75f;

        static float Grain(int x, int y) => (x * 7 + y * 13) % 5 * 0.025f - 0.05f;

        var picture = Grey(width, height, (x, y) => Stripe(y) + Grain(x, y));
        var smoothed = Bake(
            device,
            "Filters/Anisotropic Painterly Smooth",
            width,
            height,
            [new("Input", picture)],
            static node => node.SetValue("Radius", 4f)
        );

        double before = 0, after = 0;
        var counted = 0;

        // The middle rows of each whole stripe, clear of the edges and of a stripe the wrap cut short.
        for (var y = 0; y + 8 <= height; y += 8) {
            for (var x = 0; x < width; x++) {
                foreach (var row in (int[])[y + 3, y + 4]) {
                    var expected = Unit(Stripe(row));

                    before += Math.Abs(Unit(Stripe(row) + Grain(x, row)) - expected);
                    after += Math.Abs(smoothed.At(x, row, 0) - expected);
                    counted++;

                    Assert.Equal(expected, smoothed.At(x, row, 0), 0.06f);
                }
            }
        }

        output.WriteLine($"mean grain before {before / counted:0.0000}, after {after / counted:0.0000}");
        Assert.True(after < before * 0.5, $"the grain went from {before / counted} to {after / counted}");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_fill_paints_a_hole_out_from_its_rim_and_leaves_the_rest_alone(int width, int height) {
        using var device = TextureKernelHarness.Open();

        var half = width / 2;

        // Dark on the left, light on the right; one hole inside the dark half and one across the boundary.
        bool InLeftHole(int x, int y) => x >= 8 && x < 18 && y >= 10 && y < 22;

        bool InMiddleHole(int x, int y) => x >= half - 6 && x < half + 6 && y >= 30 && y < 40;

        var picture = Picture(width, height, (x, _) => x < half ? (0.2f, 0.3f, 0.4f, 1f) : (0.8f, 0.7f, 0.6f, 1f));

        // What is under the holes is a colour the fill must not show.
        var spoiled = Picture(
            width,
            height,
            (x, y) => InLeftHole(x, y) || InMiddleHole(x, y) ? (1f, 0f, 1f, 1f)
                : x < half ? (0.2f, 0.3f, 0.4f, 1f) : (0.8f, 0.7f, 0.6f, 1f)
        );
        var mask = Grey(width, height, (x, y) => InLeftHole(x, y) || InMiddleHole(x, y) ? 1f : 0f);

        var filled = Bake(
            device,
            "Photo/Fill Holes",
            width,
            height,
            [new("Input", spoiled), new("Mask", mask, true)]
        );

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                if (InLeftHole(x, y)) {
                    // Wholly inside the dark half, so the fill is the dark colour.
                    Assert.Equal(Unit(0.2f), filled.At(x, y, 0), 0.03f);
                    Assert.Equal(Unit(0.4f), filled.At(x, y, 2), 0.03f);
                } else if (InMiddleHole(x, y)) {
                    // Across the boundary: between the two, never the magenta beneath.
                    Assert.InRange(filled.At(x, y, 0), 0.15f, 0.85f);
                    Assert.InRange(filled.At(x, y, 1), 0.25f, 0.75f);
                } else {
                    // Kept exactly, to half-float rounding.
                    Assert.Equal(Unit(x < half ? 0.2f : 0.8f), filled.At(x, y, 0), 0.002f);
                }
            }
        }

        // The left of the crossing hole leans dark and its right leans light.
        Assert.True(filled.At(half - 5, 35, 0) < filled.At(half + 4, 35, 0));

        // And a mask with nothing in it leaves the picture as it was.
        var untouched = Bake(
            device,
            "Photo/Fill Holes",
            width,
            height,
            [new("Input", picture), new("Mask", Grey(width, height, static (_, _) => 0f), true)]
        );

        Assert.Equal(Unit(0.4f), untouched.At(3, 3, 2), 0.002f);
        Assert.Equal(Unit(0.6f), untouched.At(width - 3, height - 3, 2), 0.002f);
    }

    /// <summary>A hole at the edge of a tiling picture is filled from across the wrap.</summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_fill_at_the_edge_reaches_across_the_wrap(int width, int height) {
        using var device = TextureKernelHarness.Open();

        // A light band down the right edge and dark everywhere else. The hole is at the left edge, so the light
        // it can be filled from is across the wrap and nowhere on its own side.
        var picture = Grey(width, height, (x, _) => x >= width - 6 ? 0.9f : 0.1f);
        var mask = Grey(width, height, static (x, y) => x < 3 && y is >= 10 and < 30 ? 1f : 0f);
        var filled = Bake(device, "Photo/Fill Holes", width, height, [new("Input", picture), new("Mask", mask, true)]);

        // The same picture in a graph that does not tile sees only the one light column on its own side.
        var clamped = Bake(
            device,
            "Photo/Fill Holes",
            width,
            height,
            [new("Input", picture), new("Mask", mask, true)],
            tiling: false
        );

        output.WriteLine($"edge fill: tiled {filled.At(0, 20, 0)}, clamped {clamped.At(0, 20, 0)}");
        Assert.True(filled.At(0, 20, 0) > 0.3f, $"the edge was filled with {filled.At(0, 20, 0)}");
        Assert.True(
            filled.At(0, 20, 0) > clamped.At(0, 20, 0) + 0.15f,
            $"tiled {filled.At(0, 20, 0)} against clamped {clamped.At(0, 20, 0)}"
        );
    }

    /// <summary>
    ///     Under a uniform environment every way a surface faces receives the same light, which is the
    ///     environment's own: the normalisation a renderer's irradiance has.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_uniform_environment_lights_every_normal_alike(int width, int height) {
        using var device = TextureKernelHarness.Open();

        var normals = Picture(
            width,
            height,
            static (x, y) => {
                var a = x * 0.37f + y * 0.11f;

                return (0.5f + 0.45f * MathF.Cos(a), 0.5f + 0.45f * MathF.Sin(a), 0.75f, 1f);
            }
        );
        var lit = Bake(
            device,
            "Surface/Environment Light",
            width,
            height,
            [new("Normal", normals), new("Environment", Solid(width, height, 0.6f, 0.4f, 0.2f))]
        );

        for (var y = 0; y < height; y += 3) {
            for (var x = 0; x < width; x += 3) {
                Assert.Equal(Unit(0.6f), lit.At(x, y, 0), 0.03f);
                Assert.Equal(Unit(0.2f), lit.At(x, y, 2), 0.03f);
            }
        }
    }

    /// <summary>
    ///     A white sky over black ground: a surface facing up sees all of the sky, one facing sideways half of it,
    ///     and one facing down none — Lambert's closed forms, 1, ½ and 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_sky_lights_a_face_by_how_much_of_it_the_face_sees(int width, int height) {
        using var device = TextureKernelHarness.Open();

        var sky = Grey(width, height, (_, y) => y < height / 2 ? 1f : 0f);
        var third = width / 3;

        // Tangent space: up, then toward the picture's right (a wall facing +x), then down-ish is not a tangent
        // normal, so the third region repeats up and the world-space bake below takes the downward face.
        var tangent = Picture(width, height, (x, _) => x < third ? (0.5f, 0.5f, 1f, 1f) : (1f, 0.5f, 0.5f, 1f));
        var lit = Bake(
            device,
            "Surface/Environment Light",
            width,
            height,
            [new("Normal", tangent), new("Environment", sky)]
        );

        Assert.Equal(1f, lit.At(third / 2, height / 2, 0), 0.05f);
        Assert.Equal(0.5f, lit.At(width - 4, height / 2, 0), 0.05f);

        // World space: green is up, so a green of nought faces straight down.
        var world = Picture(width, height, (x, _) => x < third ? (0.5f, 1f, 0.5f, 1f) : (0.5f, 0f, 0.5f, 1f));
        var below = Bake(
            device,
            "Surface/Environment Light",
            width,
            height,
            [new("Normal", world), new("Environment", sky)],
            static node => node.SetText("Space", "World")
        );

        Assert.Equal(1f, below.At(third / 2, height / 2, 0), 0.05f);
        Assert.Equal(0f, below.At(width - 4, height / 2, 0), 0.05f);
    }

    /// <summary>A sun on one side of the horizon lights the faces turned to it, and turning the sky moves it.</summary>
    [Fact]
    public void Turning_the_environment_turns_where_the_light_comes_from() {
        using var device = TextureKernelHarness.Open();

        const int Side = 64;

        // The environment's u is once round the horizon from −π: a bright quarter about u = ½ is the +x side.
        var sun = Grey(Side, Side, static (x, y) => x is >= 24 and < 40 && y is >= 24 and < 40 ? 1f : 0f);
        var facing = Picture(Side, Side, static (x, _) => x < Side / 2 ? (1f, 0.5f, 0.5f, 1f) : (0f, 0.5f, 0.5f, 1f));
        var lit = Bake(
            device,
            "Surface/Environment Light",
            Side,
            Side,
            [new("Normal", facing), new("Environment", sun)]
        );

        Assert.True(lit.At(8, 8, 0) > 4 * lit.At(56, 8, 0) + 0.01f, $"{lit.At(8, 8, 0)} against {lit.At(56, 8, 0)}");

        var turned = Bake(
            device,
            "Surface/Environment Light",
            Side,
            Side,
            [new("Normal", facing), new("Environment", sun)],
            static node => node.SetValue("Rotation", MathF.PI)
        );

        Assert.True(turned.At(56, 8, 0) > 4 * turned.At(8, 8, 0) + 0.01f);
    }
}
