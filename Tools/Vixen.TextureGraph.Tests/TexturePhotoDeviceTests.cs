// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>
///     Patina #178's photo nodes and #218's anti-repetition and terrain nodes, each through its node on a device, against
///     closed forms and conservation laws rather than pictures.
/// </summary>
/// <remarks>
///     The oracles: a light divided out leaves both halves of a photo equally bright and its texture as contrasty as it
///     was; a constant rebuilt from patches is the same constant; a variance-preserving blend keeps the variance a plain
///     one halves; four renderings of a known normal give that normal back; and erosion moves material without making
///     or losing any.
/// </remarks>
public class TexturePhotoDeviceTests(ITestOutputHelper output) {
    const int Wide = 256;

    /// <summary>A texture of two greys under a light that brightens from left to right, tinted as it goes.</summary>
    static (float R, float G, float B, float A) Lit(int x, int y) {
        var albedo = ((x / 4) + (y / 4)) % 2 == 0 ? 0.3f : 0.6f;
        var light = 0.35f + (0.6f * x / Wide);
        var warm = 0.8f + (0.4f * x / Wide);

        return (albedo * light * warm, albedo * light, albedo * light * (2f - warm), 1f);
    }

    static float Mean(Lanes picture, int lane, int fromX, int toX) {
        var sum = 0f;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = fromX; x < toX; x++) {
                sum += picture.At(x, y, lane);
            }
        }

        return sum / ((toX - fromX) * picture.Height);
    }

    [Fact]
    public void Flatten_light_evens_the_halves_and_keeps_the_texture_s_contrast() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var photo = Picture(Wide, Wide, Lit);
        var flat = Bake(device, "Photo/Flatten Light", Wide, Wide, [new("Input", photo)], tiling: false);
        var untouched = Bake(device, "Photo/Flatten Light", Wide, Wide, [new("Input", photo)], node => node.SetValue("Strength", 0f), tiling: false);

        // Before: the right quarter is twice as bright as the left. After: within a few percent.
        var before = Mean(untouched, 1, 3 * Wide / 4, Wide) / Mean(untouched, 1, 0, Wide / 4);
        var after = Mean(flat, 1, 3 * Wide / 4, Wide) / Mean(flat, 1, 0, Wide / 4);

        output.WriteLine($"right over left: {before} before, {after} after");
        Assert.True(before > 1.7f);
        Assert.Equal(1f, after, 0.06f);

        // ⚠ A gain and not an offset: the two greys of the checker stay in their ratio of two on both sides.
        foreach (var x in (int[]) [20, 236]) {
            var dark = flat.At(x - (x % 8), 2, 1);
            var light = flat.At(x - (x % 8) + 4, 2, 1);

            Assert.Equal(2f, light / dark, 0.12f);
        }
    }

    [Fact]
    public void Flatten_light_by_colour_evens_a_cast_that_by_luminance_keeps() {
        using var device = TextureKernelHarness.Open();

        var photo = Picture(Wide, Wide, Lit);
        var luminance = Bake(device, "Photo/Flatten Light", Wide, Wide, [new("Input", photo)], tiling: false);
        var colour = Bake(device, "Photo/Flatten Light", Wide, Wide, [new("Input", photo)], node => node.SetText("Mode", "Colour"), tiling: false);

        static float Warmth(Lanes picture, int from, int to) => Mean(picture, 0, from, to) / Mean(picture, 2, from, to);

        // The light warms from left to right. Luminance keeps every hue, so the cast survives; Colour removes it.
        Assert.True(Warmth(luminance, 3 * Wide / 4, Wide) / Warmth(luminance, 0, Wide / 4) > 1.5f);
        Assert.Equal(1f, Warmth(colour, 3 * Wide / 4, Wide) / Warmth(colour, 0, Wide / 4), 0.08f);
    }

    [Fact]
    public void Patches_of_a_constant_are_the_constant_and_keep_a_noise_s_variance() {
        using var device = TextureKernelHarness.Open();

        const int Side = 128;

        var constant = Bake(device, "Space/Break Up Tiling", Side, Side, [new("Input", Solid(Side, Side, 0.4f, 0.4f, 0.4f))]);

        for (var at = 0; at < Side * Side; at += 97) {
            Assert.Equal(Unit(0.4f), constant.At(at % Side, at / Side, 0), 3);
        }

        // A weighted mean of unrelated patches shrinks a field's variance by the sum of the squared weights, and the
        // preserving blend divides that back out. ⚠ The field is smooth on purpose: a patch is read bilinear at a
        // fraction of a texel, and white noise read that way keeps four ninths of its variance whatever the blend does
        // — which is what this test measured on its first run, and blamed on the blend.
        var noise = Grey(
            Side,
            Side,
            static (x, y) => 0.5f + (0.2f * MathF.Sin((0.41f * x) + (1.3f * MathF.Sin(0.13f * y))))
                + (0.15f * MathF.Sin((0.37f * y) + (0.7f * MathF.Cos(0.21f * x))))
        );
        static float Spread(float[] values) {
            var mean = values.Average();

            return values.Select(value => (value - mean) * (value - mean)).Average();
        }

        static float Variance(Lanes picture) =>
            Spread([..Enumerable.Range(0, picture.Width * picture.Height).Select(at => picture.At(at % picture.Width, at / picture.Width, 0))]);

        var source = Spread([..Enumerable.Range(0, Side * Side).Select(at => noise[at * 4] / 255f)]);
        var kept = Variance(Bake(device, "Space/Break Up Tiling", Side, Side, [new("Input", noise)], node => node.SetValue("Softness", 1f)));
        var averaged = Variance(Bake(device, "Space/Break Up Tiling", Side, Side, [new("Input", noise)], node => {
                node.SetValue("Softness", 1f);
                node.SetValue("Keep Contrast", 0f);
            }
        ));

        output.WriteLine($"variance: {source} in, {kept} kept, {averaged} averaged");
        Assert.Equal(1f, kept / source, 0.2f);
        Assert.True(averaged / source < 0.8f, $"averaging kept {averaged / source} of the variance");
    }

    [Fact]
    public void Tile_from_patches_makes_a_ramp_that_does_not_tile_into_one_that_does() {
        using var device = TextureKernelHarness.Open();

        const int Side = 128;

        // A ramp: its left and right edges are as different as two texels of it can be.
        var ramp = Grey(Side, Side, static (x, _) => 0.1f + (0.8f * x / (Side - 1)));
        var tiled = Bake(device, "Photo/Tile from Patches", Side, Side, [new("Input", ramp)]);

        float Seam(Func<int, float> across) => Enumerable.Range(0, Side).Average(across);

        var seam = Seam(y => MathF.Abs(tiled.At(0, y, 0) - tiled.At(Side - 1, y, 0)));
        var inside = Seam(y => MathF.Abs(tiled.At(Side / 2, y, 0) - tiled.At((Side / 2) - 1, y, 0)));

        output.WriteLine($"step across the seam {seam}, inside {inside}; the ramp's own seam is 0.8");

        // The ramp's seam of 0.8 is gone. ⚠ That the result is periodic — the cell folded before it is hashed — is the
        // tiling roll call's to hold, and it goes red when the fold is removed; this comparison did not, because an
        // unfolded grid is as rough inside as across the seam.
        Assert.True(seam < 0.1f, $"the seam steps {seam} against {inside} inside");

        // And every texel is a texel of the ramp, from inside its edges: nothing is extrapolated.
        for (var at = 0; at < Side * Side; at += 31) {
            Assert.InRange(tiled.At(at % Side, at / Side, 0), 0.08f, 0.92f);
        }
    }

    [Fact]
    public void Four_renderings_of_a_known_surface_give_its_normal_and_albedo_back() {
        using var device = TextureKernelHarness.Open();

        const int Side = 64;
        const float Height = MathF.PI / 4f;

        // The left half faces right and the right half faces down the picture, both 30° off flat; albedo 0.8.
        static (float X, float Y, float Z) Normal(int x) =>
            x < Side / 2 ? (0.5f, 0f, 0.8660254f) : (0f, 0.5f, 0.8660254f);

        byte[] Lit(float lx, float ly) =>
            Grey(Side, Side, (x, _) => {
                var n = Normal(x);

                return 0.8f * MathF.Max(0f, (n.X * lx * MathF.Cos(Height)) + (n.Y * ly * MathF.Cos(Height)) + (n.Z * MathF.Sin(Height)));
            });

        TextureNodeBench.Wire[] photos = [
            new("Lit from Left", Lit(-1f, 0f)),
            new("Lit from Right", Lit(1f, 0f)),
            new("Lit from Top", Lit(0f, -1f)),
            new("Lit from Bottom", Lit(0f, 1f))
        ];

        var normal = Bake(device, "Photo/Normal from Lighting", Side, Side, photos, output: "Normal");
        var albedo = Bake(device, "Photo/Normal from Lighting", Side, Side, photos, output: "Albedo");

        foreach (var x in (int[]) [10, 50]) {
            var expected = Normal(x);

            Assert.Equal((expected.X * 0.5f) + 0.5f, normal.At(x, 20, 0), 0.02f);
            Assert.Equal((expected.Y * 0.5f) + 0.5f, normal.At(x, 20, 1), 0.02f);
            Assert.Equal((expected.Z * 0.5f) + 0.5f, normal.At(x, 20, 2), 0.02f);
            Assert.Equal(0.8f, albedo.At(x, 20, 0), 0.03f);
        }
    }

    [Fact]
    public void Erosion_conserves_material_settles_a_cliff_and_leaves_a_gentle_slope_alone() {
        using var device = TextureKernelHarness.Open();

        const int Side = 64;

        static float Sum(Lanes picture) =>
            Enumerable.Range(0, Side * Side).Sum(at => picture.At(at % Side, at / Side, 0));

        static float Steepest(Lanes picture) =>
            Enumerable.Range(0, Side * Side).Max(at => MathF.Abs(picture.At(at % Side, at / Side, 0) - picture.At(((at % Side) + 1) % Side, at / Side, 0)));

        var plateau = Grey(Side, Side, static (x, y) => x is >= 24 and < 40 && y is >= 24 and < 40 ? 0.9f : 0.1f);
        var before = Bake(device, "Surface/Thermal Erosion", Side, Side, [new("Input", plateau, Grey: true)], node => node.SetValue("Iterations", 1f));
        var after = Bake(device, "Surface/Thermal Erosion", Side, Side, [new("Input", plateau, Grey: true)], node => node.SetValue("Iterations", 64f));

        output.WriteLine($"material {Sum(before)} after one step and {Sum(after)} after 64; steepest {Steepest(before)} then {Steepest(after)}");

        Assert.Equal(Sum(before), Sum(after), Sum(before) * 0.0005f);
        Assert.True(Steepest(after) < Steepest(before) * 0.5f, "64 steps did not settle the cliff");

        // A sine whose steepest step is 0.0196 a texel, under the default talus of 2 across 64 texels, 0.031.
        var gentle = Grey(Side, Side, static (x, _) => 0.5f + (0.2f * MathF.Sin(2f * MathF.PI * x / Side)));
        var left = Bake(device, "Surface/Thermal Erosion", Side, Side, [new("Input", gentle, Grey: true)]);

        for (var x = 0; x < Side; x++) {
            Assert.Equal(Unit(0.5f + (0.2f * MathF.Sin(2f * MathF.PI * x / Side))), left.At(x, 7, 0), 4);
        }
    }
}
