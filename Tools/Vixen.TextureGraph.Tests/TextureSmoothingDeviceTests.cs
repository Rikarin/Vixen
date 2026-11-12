// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TextureGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #173's filters, each through its node on a device, against closed forms.</summary>
/// <remarks>
///     ⚠ <b>Never over a field that is affine in x and y.</b> Its mean over any symmetric window is its own
///     value, so every averaging filter leaves it alone at every strength and a test over it passes with
///     the filter switched off (<a href="https://github.com/Rikarin/Vixen/issues/694">#694</a>). These use
///     impulses, steps and staircases, where a filter that did nothing is visibly wrong.
/// </remarks>
public class TextureSmoothingDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    static void Each(Action<int, int> check) {
        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                check(x, y);
            }
        }
    }

    /// <summary>A vertical step: black left of the middle, white from it.</summary>
    static readonly byte[] Step = Grey(Side, Side, static (x, _) => x < Side / 2 ? 0f : 1f);

    /// <summary>One white texel in black.</summary>
    static readonly byte[] Speck = Grey(Side, Side, static (x, y) => x == 20 && y == 20 ? 1f : 0f);

    [Fact]
    public void A_median_removes_a_speck_and_keeps_a_step_exactly_where_it_was() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var speck = Bake(device, "Filters/Median", Side, Side, [new("Input", Speck)]);

        Each((x, y) => Assert.Equal(0f, speck.At(x, y, 0)));

        var step = Bake(
            device,
            "Filters/Median",
            Side,
            Side,
            [new("Input", Step)],
            static node => node.SetValue("Radius", 2f)
        );

        Each((x, y) => Assert.Equal(x < Side / 2 ? 0f : 1f, step.At(x, y, 0)));

        // And a corner is rounded: the outside corner of a white quadrant has only four white texels in
        // its 3×3 window, a minority.
        var corner = Bake(
            device,
            "Filters/Median",
            Side,
            Side,
            [new("Input", Grey(Side, Side, static (x, y) => x >= 32 && y >= 32 ? 1f : 0f))]
        );

        Assert.Equal(0f, corner.At(32, 32, 0));
        Assert.Equal(1f, corner.At(33, 33, 0));
    }

    [Fact]
    public void An_edge_preserving_blur_keeps_a_step_that_a_tolerant_one_smears() {
        using var device = TextureKernelHarness.Open();

        var tight = Bake(
            device,
            "Filters/Edge-Preserving Blur",
            Side,
            Side,
            [new("Input", Step)],
            static node => {
                node.SetValue("Sigma", 3f);
                node.SetValue("Edge Tolerance", 0.05f);
            }
        );

        Each((x, y) => Assert.Equal(x < Side / 2 ? 0f : 1f, tight.At(x, y, 0), 0.001f));

        var loose = Bake(
            device,
            "Filters/Edge-Preserving Blur",
            Side,
            Side,
            [new("Input", Step)],
            static node => {
                node.SetValue("Sigma", 3f);
                node.SetValue("Edge Tolerance", 100f);
            }
        );

        // With a tolerance nothing exceeds it is a gaussian, which reaches both texels beside the step.
        Assert.InRange(loose.At(Side / 2 - 1, 10, 0), 0.2f, 0.5f);
        Assert.InRange(loose.At(Side / 2, 10, 0), 0.5f, 0.8f);

        // And inside a flat region, either way, nothing moves.
        Assert.Equal(1f, loose.At(Side - 20, 10, 0), 0.001f);
    }

    [Fact]
    public void A_painterly_smooth_keeps_a_step_sharp_and_flattens_fine_stripes() {
        using var device = TextureKernelHarness.Open();

        var step = Bake(device, "Filters/Painterly Smooth", Side, Side, [new("Input", Step)]);

        Each((x, y) => Assert.Equal(x < Side / 2 ? 0f : 1f, step.At(x, y, 0), 0.001f));

        // One-texel stripes vary alike in every quadrant, so each texel takes about their mean.
        var stripes = Bake(
            device,
            "Filters/Painterly Smooth",
            Side,
            Side,
            [new("Input", Grey(Side, Side, static (x, _) => x % 2))]
        );

        Each((x, y) => Assert.InRange(stripes.At(x, y, 0), 0.3f, 0.7f));
    }

    [Fact]
    public void A_spin_blur_leaves_rings_alone_and_smears_spokes() {
        using var device = TextureKernelHarness.Open();

        // A function of the distance from the centre only is unchanged by any turn about it.
        static float Ring(int x, int y) {
            var dx = x + 0.5f - Side / 2f;
            var dy = y + 0.5f - Side / 2f;

            return 0.5f + 0.4f * MathF.Cos(MathF.Sqrt(dx * dx + dy * dy) * 0.3f);
        }

        var rings = Bake(
            device,
            "Filters/Spin Blur",
            Side,
            Side,
            [new("Input", Grey(Side, Side, Ring))],
            static node => node.SetValue("Angle", 0.6f)
        );

        // Inside the inscribed circle, so that no arc leaves the image for the far side of the wrap.
        Each((x, y) => {
                var dx = x + 0.5f - Side / 2f;
                var dy = y + 0.5f - Side / 2f;

                if (dx * dx + dy * dy < 28f * 28f) {
                    Assert.Equal(Unit(Ring(x, y)), rings.At(x, y, 0), 0.03f);
                }
            }
        );

        // Half black and half white about a vertical through the centre: above the centre the boundary is
        // a spoke, and a turn smears across it.
        var spokes = Bake(
            device,
            "Filters/Spin Blur",
            Side,
            Side,
            [new("Input", Step)],
            static node => node.SetValue("Angle", 0.6f)
        );

        Assert.InRange(spokes.At(Side / 2, 4, 0), 0.2f, 0.8f);
        Assert.InRange(spokes.At(Side / 2 - 1, 4, 0), 0.2f, 0.8f);
    }

    [Fact]
    public void An_anti_alias_ramps_a_staircase_and_leaves_a_straight_edge_and_a_flat_alone() {
        using var device = TextureKernelHarness.Open();

        var straight = Bake(device, "Filters/Anti-Alias", Side, Side, [new("Input", Step)]);

        Each((x, y) => Assert.Equal(x < Side / 2 ? 0f : 1f, straight.At(x, y, 0), 0.0001f));

        // A shallow staircase: the boundary climbs one texel every four.
        var stairs = Grey(Side, Side, static (x, y) => y >= x / 4 + 8 ? 1f : 0f);
        var smoothed = Bake(device, "Filters/Anti-Alias", Side, Side, [new("Input", stairs)]);
        var softened = 0;

        Each((x, y) => {
                var value = smoothed.At(x, y, 0);

                softened += value is > 0.05f and < 0.95f ? 1 : 0;

                // Far from the boundary nothing moves. ⚠ The graph tiles, and the staircase does not: across
                // the wrap its two ends meet as a vertical step of fifteen texels, which is an edge too.
                if (Math.Abs(y - (x / 4 + 8)) > 3 && x is > 1 and < Side - 2) {
                    Assert.Equal(y >= x / 4 + 8 ? 1f : 0f, value, 0.0001f);
                }
            }
        );

        Assert.True(softened >= Side, $"only {softened} texels of the staircase came out between black and white");
    }

    [Fact]
    public void A_non_uniform_blur_reaches_forty_texels_where_it_used_to_stop_at_twelve() {
        using var device = TextureKernelHarness.Open();

        const int Wide = 128;
        var line = Grey(Wide, Wide, static (x, _) => x == 64 ? 1f : 0f);
        var out_ = Bake(
            device,
            "Filters/Non-Uniform Blur",
            Wide,
            Wide,
            [new("Input", line), new("Radius Map", Solid(Wide, Wide, 1f, 1f, 1f), true)],
            static node => node.SetValue("Max Radius", 40f)
        );

        // Taps at forty either side reach the line, so the texels forty away see it, and none beyond do.
        Assert.True(out_.At(64 + 40, 50, 0) > 0f, "the texel forty to the right never saw the line");
        Assert.True(out_.At(64 - 40, 50, 0) > 0f, "the texel forty to the left never saw the line");
        Assert.Equal(0f, out_.At(64 + 42, 50, 0));
        Assert.Equal(0f, out_.At(64 - 42, 50, 0));

        // And the plan is not refused at the bake that resolves past the old ceiling.
        Assert.Empty(
            TextureFilters.Verify(
                new() {
                    BaseWidth = 64,
                    BaseHeight = 64,
                    BakeLevelOffset = -2,
                    Images = [
                        new(TextureFormat.Rgba8, External: true), new(TextureFormat.R8, External: true),
                        new(TextureFormat.Rgba16Float)
                    ],
                    Ops = [TextureFilters.NonUniformBlurOp(2, 0, 1, 40f)],
                    Outputs = [2]
                }
            )
        );
    }

    [Fact]
    public void A_glow_bleeds_light_past_bright_regions_and_adds_nothing_from_dim_ones() {
        using var device = TextureKernelHarness.Open();

        // A white square on the right half, a dim one under the threshold on the left.
        var picture = Grey(
            Side,
            Side,
            static (x, y) => y is >= 24 and < 40 ? x is >= 40 and < 56 ? 1f : x is >= 8 and < 24 ? 0.4f : 0f : 0f
        );
        var out_ = Bake(
            device,
            "Utility/Glow",
            Side,
            Side,
            [new("Input", picture)],
            static node => {
                node.SetValue("Threshold", 0.5f);
                node.SetValue("Radius", 3f);
            }
        );

        // Just outside the bright square, light it did not have.
        Assert.True(out_.At(38, 32, 0) > 0.05f, $"the glow did not reach outside the square: {out_.At(38, 32, 0)}");

        // The dim square is below the threshold, so it and its surroundings are as they were.
        Assert.Equal(Unit(0.4f), out_.At(16, 32, 0), 0.002f);
        Assert.Equal(0f, out_.At(6, 32, 0), 0.002f);

        // Far from anything, still black.
        Assert.Equal(0f, out_.At(30, 4, 0), 0.002f);
    }
}
