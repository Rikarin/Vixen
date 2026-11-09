// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #171's colour and blend operators, each through its node on a device, against closed forms.</summary>
/// <remarks>
///     Each expected value is arithmetic with one answer — a clamp of a ramp, a colour landing on the colour
///     it was told to become, an identity table returning its input — rather than a second implementation
///     of the kernel, which is the CPU twin doc 48 § D3 bans.
/// </remarks>
public class TextureColourOperatorDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>Within rounding of a 32-bit bake.</summary>
    const float Close = 0.002f;

    static readonly byte[] Ramp = Grey(Side, Side, (x, y) => ((y * Side) + x) / (float)((Side * Side) - 1));

    static float RampAt(int x, int y) => Unit(((y * Side) + x) / (float)((Side * Side) - 1));

    static void Each(Action<int, int> check) {
        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                check(x, y);
            }
        }
    }

    [Fact]
    public void A_clamp_holds_every_channel_between_its_two_values_and_leaves_the_middle_alone() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var out_ = Bake(device, "Colour/Clamp", Side, Side, [new("Input", Ramp)], node => {
            node.SetValue("Low", 0.25f);
            node.SetValue("High", 0.75f);
        });

        Each((x, y) => Assert.Equal(Math.Clamp(RampAt(x, y), 0.25f, 0.75f), out_.At(x, y, 0), Close));
    }

    [Fact]
    public void A_threshold_is_a_hard_step_at_its_level_and_a_smooth_one_across_its_softness() {
        using var device = TextureKernelHarness.Open();

        var hard = Bake(device, "Colour/Threshold", Side, Side, [new("Input", Ramp)], node => node.SetValue("Level", 0.5f));

        Each((x, y) => Assert.Equal(RampAt(x, y) >= 0.5f ? 1f : 0f, hard.At(x, y, 0)));

        var soft = Bake(device, "Colour/Threshold", Side, Side, [new("Input", Grey(Side, Side, (x, _) => x < 32 ? 0.5f : 0.35f))], node => {
            node.SetValue("Level", Unit(0.5f));
            node.SetValue("Softness", 0.2f);
        });

        // Exactly at the level is half way up the smooth step, and below its foot is nothing.
        Assert.Equal(0.5f, soft.At(0, 0, 0), Close);
        Assert.Equal(0f, soft.At(40, 0, 0), Close);
    }

    /// <summary>
    ///     Each comparison holds exactly where its closed form says over a ramp, and the tolerance widens the two
    ///     equalities and nothing else (Patina #110).
    /// </summary>
    [Theory]
    [InlineData("Greater")]
    [InlineData("GreaterOrEqual")]
    [InlineData("Less")]
    [InlineData("LessOrEqual")]
    [InlineData("Equal")]
    [InlineData("NotEqual")]
    public void A_compare_holds_where_its_comparison_does(string operation) {
        using var device = TextureKernelHarness.Open();
        const float Value = 0.5f;
        const float Tolerance = 0.1f;

        var out_ = Bake(device, "Analysis/Compare", Side, Side, [new("Input", Ramp)], node => {
            node.SetText("Operation", operation);
            node.SetValue("Value", Value);
            node.SetValue("Tolerance", Tolerance);
        });

        Each((x, y) => {
            var v = RampAt(x, y);
            var holds = operation switch {
                "Greater" => v > Value,
                "GreaterOrEqual" => v >= Value,
                "Less" => v < Value,
                "LessOrEqual" => v <= Value,
                "Equal" => MathF.Abs(v - Value) <= Tolerance,
                _ => MathF.Abs(v - Value) > Tolerance
            };

            // A ramp texel within a half-float step of an edge may land either side of it.
            if (MathF.Abs(v - Value) < 0.002f || MathF.Abs(MathF.Abs(v - Value) - Tolerance) < 0.002f) {
                return;
            }

            Assert.Equal(holds ? 1f : 0f, out_.At(x, y, 0));
        });
    }

    [Fact]
    public void A_quantise_puts_each_quarter_of_the_range_on_one_of_four_even_values() {
        using var device = TextureKernelHarness.Open();

        var out_ = Bake(device, "Colour/Quantise", Side, Side, [new("Input", Ramp)], node => node.SetValue("Levels", 4f));

        Each((x, y) => Assert.Equal(MathF.Min(MathF.Floor(RampAt(x, y) * 4f), 3f) / 3f, out_.At(x, y, 0), Close));
    }

    [Fact]
    public void A_channel_mix_swaps_red_and_blue_and_adds_its_offset_to_green() {
        using var device = TextureKernelHarness.Open();

        var picture = Picture(Side, Side, (x, y) => (x / 63f, y / 63f, 0.2f, 1f));
        var out_ = Bake(device, "Colour/Channel Mix", Side, Side, [new("Input", picture)], node => {
            node.SetValue("Red from Red", 0f);
            node.SetValue("Red from Blue", 1f);
            node.SetValue("Blue from Blue", 0f);
            node.SetValue("Blue from Red", 1f);
            node.SetValue("Green Offset", 0.25f);
        });

        Each((x, y) => {
            Assert.Equal(Unit(0.2f), out_.At(x, y, 0), Close);
            Assert.Equal(Unit(y / 63f) + 0.25f, out_.At(x, y, 1), Close);
            Assert.Equal(Unit(x / 63f), out_.At(x, y, 2), Close);
        });
    }

    [Fact]
    public void An_image_ramp_reads_the_row_it_is_told_from_its_left_end_to_its_right() {
        using var device = TextureKernelHarness.Open();

        // The ramp's top row runs red to green and every other row is blue, so the wrong row is blue.
        var ramp = Picture(Side, Side, (x, y) => y == 0 ? (1f - (x / 63f), x / 63f, 0f, 1f) : (0f, 0f, 1f, 1f));
        var input = Grey(Side, Side, (x, _) => x / 63f);
        var out_ = Bake(
            device,
            "Colour/Image Ramp",
            Side,
            Side,
            [new("Input", input, Grey: true), new("Ramp", ramp)],
            node => node.SetValue("Row", 0f)
        );

        Each((x, y) => {
            Assert.Equal(Unit(1f - (x / 63f)), out_.At(x, y, 0), 0.01f);
            Assert.Equal(Unit(x / 63f), out_.At(x, y, 1), 0.01f);
            Assert.Equal(0f, out_.At(x, y, 2), Close);
        });
    }

    [Fact]
    public void A_replaced_colour_lands_exactly_on_its_target_and_its_neighbours_keep_their_distance() {
        using var device = TextureKernelHarness.Open();

        // Red, a darker red and a grey: red to blue is a third of a turn of hue and nothing else.
        var picture = Picture(Side, Side, (x, _) => x < 20 ? (1f, 0f, 0f, 1f) : x < 40 ? (0.4f, 0f, 0f, 1f) : (0.6f, 0.6f, 0.6f, 0.5f));
        var out_ = Bake(device, "Colour/Replace Colour", Side, Side, [new("Input", picture)], node => {
            node.SetValue("From", 1f, 0f, 0f, 1f);
            node.SetValue("To", 0f, 0f, 1f, 1f);
        });

        Near((0f, 0f, 1f, 1f), out_.Texel(5, 5));
        Near((0f, 0f, Unit(0.4f), 1f), out_.Texel(30, 5));

        // A grey has no hue to turn and keeps its lightness, and alpha is left alone.
        Near((Unit(0.6f), Unit(0.6f), Unit(0.6f), Unit(0.5f)), out_.Texel(50, 5));
    }

    [Fact]
    public void A_colour_match_moves_the_images_mean_onto_its_target() {
        using var device = TextureKernelHarness.Open();

        // Two greys whose mean is 0.4, matched to 0.5: lightness goes up by a tenth everywhere.
        var picture = Grey(Side, Side, (x, _) => x < 32 ? 0.2f : 0.6f);
        var out_ = Bake(device, "Colour/Colour Match", Side, Side, [new("Input", picture)], node => node.SetValue("Target", 0.5f, 0.5f, 0.5f, 1f));
        var mean = (Unit(0.2f) + Unit(0.6f)) / 2f;

        Each((x, y) => Assert.Equal((x < 32 ? Unit(0.2f) : Unit(0.6f)) + 0.5f - mean, out_.At(x, y, 0), 0.004f));

        // A flat colour's mean is itself, so the whole image becomes the target.
        var flat = Bake(device, "Colour/Colour Match", Side, Side, [new("Input", Solid(Side, Side, 0.8f, 0.2f, 0.2f))], node => node.SetValue("Target", 0.2f, 0.4f, 0.8f, 1f));

        Assert.Equal(0.2f, flat.At(9, 9, 0), 0.01f);
        Assert.Equal(0.4f, flat.At(9, 9, 1), 0.01f);
        Assert.Equal(0.8f, flat.At(9, 9, 2), 0.01f);
    }

    /// <summary>
    ///     ⚠ At a size no power of two divides, the ladder's blocks hang off the edge, and a block that covers
    ///     nothing must count for nothing.
    /// </summary>
    /// <remarks>
    ///     Fifty texels reduce in blocks of five into twelve, so the last two of the twelve lie wholly outside
    ///     the image. Weighed as whole blocks they pull the mean of a flat colour towards black, and the match
    ///     lands short of its target.
    /// </remarks>
    [Fact]
    public void A_colour_match_measures_a_flat_image_of_any_size_as_its_own_colour() {
        using var device = TextureKernelHarness.Open();

        const int Odd = 50;

        var flat = Bake(device, "Colour/Colour Match", Odd, Odd, [new("Input", Solid(Odd, Odd, 0.6f, 0.6f, 0.6f))], node => node.SetValue("Target", 0.6f, 0.6f, 0.6f, 1f));

        for (var y = 0; y < Odd; y++) {
            for (var x = 0; x < Odd; x++) {
                Assert.Equal(Unit(0.6f), flat.At(x, y, 0), 0.004f);
            }
        }
    }

    [Fact]
    public void A_colour_range_moves_its_own_colour_fully_its_rim_partly_and_everything_else_not_at_all() {
        using var device = TextureKernelHarness.Open();

        var picture = Picture(Side, Side, (x, _) => x < 20 ? (1f, 0f, 0f, 1f) : x < 40 ? (0.8f, 0f, 0f, 1f) : (0f, 1f, 0f, 1f));
        var out_ = Bake(device, "Colour/Replace Colour Range", Side, Side, [new("Input", picture)], node => {
            node.SetValue("From", 1f, 0f, 0f, 1f);
            node.SetValue("To", 0f, 0f, 1f, 1f);
            node.SetValue("Tolerance", 0.1f);
            node.SetValue("Softness", 0.2f);
        });

        Near((0f, 0f, 1f, 1f), out_.Texel(5, 5));

        // 0.8 red is 0.2 away: half way through the softness, so half the move.
        var distance = 1f - Unit(0.8f);
        var weight = 1f - ((distance - 0.1f) / 0.2f);

        Assert.Equal(Unit(0.8f) - weight, out_.At(30, 5, 0), 0.003f);
        Assert.Equal(weight, out_.At(30, 5, 2), 0.003f);
        Near((0f, 1f, 0f, 1f), out_.Texel(50, 5));
    }

    [Fact]
    public void A_dissolve_shows_one_input_or_the_other_outright_in_the_proportion_of_its_opacity() {
        using var device = TextureKernelHarness.Open();

        Wire[] wires = [new("Background", Solid(Side, Side, 0f, 0f, 0f)), new("Foreground", Solid(Side, Side, 1f, 1f, 1f))];
        var out_ = Bake(device, "Colour/Dissolve", Side, Side, wires, node => node.SetValue("Opacity", 0.3f));
        var shown = 0;

        Each((x, y) => {
            var value = out_.At(x, y, 0);

            Assert.True(value is 0f or 1f, $"({x}, {y}) is {value}, which is neither input");
            shown += value == 1f ? 1 : 0;
        });

        Assert.InRange(shown / (float)(Side * Side), 0.26f, 0.34f);

        // A mask of nothing on the left half keeps the background there, whatever the draw.
        var masked = Bake(device, "Colour/Dissolve", Side, Side, [.. wires, new("Mask", Grey(Side, Side, (x, _) => x < 32 ? 0f : 1f), Grey: true)], node => node.SetValue("Opacity", 1f));

        Each((x, y) => Assert.Equal(x < 32 ? 0f : 1f, masked.At(x, y, 0)));

        // Another seed is another scatter.
        var reseeded = Bake(device, "Colour/Dissolve", Side, Side, wires, node => node.SetValue("Opacity", 0.3f), seed: 99);
        var moved = 0;

        Each((x, y) => moved += out_.At(x, y, 0) != reseeded.At(x, y, 0) ? 1 : 0);

        Assert.True(moved > 100, $"only {moved} texels changed with the seed");
    }

    [Fact]
    public void The_srgb_curve_decodes_and_encodes_by_the_standards_piecewise_formula() {
        using var device = TextureKernelHarness.Open();

        static float Decode(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        static float Encode(float l) => l <= 0.0031308f ? l * 12.92f : (1.055f * MathF.Pow(l, 1f / 2.4f)) - 0.055f;

        var decoded = Bake(device, "Colour/sRGB to Linear", Side, Side, [new("Input", Ramp)]);
        var encoded = Bake(device, "Colour/Linear to sRGB", Side, Side, [new("Input", Ramp)]);

        Each((x, y) => {
            Assert.Equal(Decode(RampAt(x, y)), decoded.At(x, y, 0), 0.001f);
            Assert.Equal(Encode(RampAt(x, y)), encoded.At(x, y, 0), 0.001f);
        });
    }

    [Fact]
    public void Premultiplying_scales_colour_by_alpha_and_unpremultiplying_undoes_it_except_where_alpha_is_nought() {
        using var device = TextureKernelHarness.Open();

        var picture = Picture(Side, Side, (x, _) => x < 32 ? (0.8f, 0.4f, 0.2f, 0.5f) : (0.6f, 0.6f, 0.6f, 0f));
        var times = Bake(device, "Colour/Premultiply", Side, Side, [new("Input", picture)]);
        var over = Bake(device, "Colour/Unpremultiply", Side, Side, [new("Input", picture)]);
        var alpha = Unit(0.5f);

        Near((Unit(0.8f) * alpha, Unit(0.4f) * alpha, Unit(0.2f) * alpha, alpha), times.Texel(4, 4));
        Near((0f, 0f, 0f, 0f), times.Texel(40, 4));
        Near((Unit(0.8f) / alpha, Unit(0.4f) / alpha, Unit(0.2f) / alpha, alpha), over.Texel(4, 4));
        Near((0f, 0f, 0f, 0f), over.Texel(40, 4));
    }

    [Fact]
    public void An_identity_lookup_strip_returns_its_input_and_a_swapped_one_swaps() {
        using var device = TextureKernelHarness.Open();

        // A 16-point cube is a strip 256 wide and 16 tall, and the plan is that size so the strip is read
        // texel for texel.
        const int Points = 16;
        const int Wide = Points * Points;

        byte[] Strip(bool swap) =>
            Picture(Wide, Points, (x, y) => {
                var r = (x % Points) / (float)(Points - 1);
                var g = y / (float)(Points - 1);
                var b = (x / Points) / (float)(Points - 1);

                return swap ? (b, g, r, 1f) : (r, g, b, 1f);
            });

        var input = Picture(Wide, Points, (x, y) => (x / 255f, y / 15f, ((x * 7) + (y * 13)) % 256 / 255f, 1f));
        var same = Bake(device, "Colour/Lookup Table", Wide, Points, [new("Input", input), new("Table", Strip(false))]);
        var swapped = Bake(device, "Colour/Lookup Table", Wide, Points, [new("Input", input), new("Table", Strip(true))]);

        for (var y = 0; y < Points; y++) {
            for (var x = 0; x < Wide; x++) {
                var at = ((y * Wide) + x) * 4;
                var (r, g, b) = (input[at] / 255f, input[at + 1] / 255f, input[at + 2] / 255f);

                // The strip's own values are bytes, so its lattice is a byte away from exact.
                Assert.Equal(r, same.At(x, y, 0), 0.006f);
                Assert.Equal(g, same.At(x, y, 1), 0.006f);
                Assert.Equal(b, same.At(x, y, 2), 0.006f);
                Assert.Equal(b, swapped.At(x, y, 0), 0.006f);
                Assert.Equal(r, swapped.At(x, y, 2), 0.006f);
            }
        }
    }

    [Fact]
    public void A_palette_snaps_each_colour_to_its_nearest_entry_and_uses_only_the_first_count() {
        using var device = TextureKernelHarness.Open();

        var picture = Picture(Side, Side, (x, _) => x < 16 ? (0.9f, 0.1f, 0.1f, 1f) : x < 32 ? (0.6f, 0.6f, 0.6f, 0.4f) : x < 48 ? (0.2f, 0.2f, 0.2f, 1f) : (0.1f, 0.1f, 0.9f, 1f));
        var three = Bake(device, "Colour/Palette", Side, Side, [new("Input", picture)], node => {
            node.SetValue("Count", 3f);
            node.SetValue("Colour 1", 0f, 0f, 0f, 1f);
            node.SetValue("Colour 2", 1f, 1f, 1f, 1f);
            node.SetValue("Colour 3", 1f, 0f, 0f, 1f);
            node.SetValue("Colour 4", 0f, 0f, 1f, 1f);
        });

        Near((1f, 0f, 0f, 1f), three.Texel(4, 0));
        Near((1f, 1f, 1f, Unit(0.4f)), three.Texel(20, 0));
        Near((0f, 0f, 0f, 1f), three.Texel(40, 0));

        // Blue is the fourth entry and the count is three, so the blue texel goes to the nearest of the rest.
        Near((0f, 0f, 0f, 1f), three.Texel(60, 0));
    }

    [Fact]
    public void A_selector_passes_on_the_input_its_index_names_and_transparent_black_for_an_empty_slot() {
        using var device = TextureKernelHarness.Open();

        Wire[] wires = [new("Input 1", Solid(Side, Side, 1f, 0f, 0f)), new("Input 2", Solid(Side, Side, 0f, 1f, 0f))];

        Near((1f, 0f, 0f, 1f), Bake(device, "Colour/Select Input", Side, Side, wires, node => node.SetValue("Index", 1f)).Texel(3, 3));
        Near((0f, 1f, 0f, 1f), Bake(device, "Colour/Select Input", Side, Side, wires, node => node.SetValue("Index", 2f)).Texel(3, 3));
        Near((0f, 0f, 0f, 0f), Bake(device, "Colour/Select Input", Side, Side, wires, node => node.SetValue("Index", 3f)).Texel(3, 3));
    }

    /// <summary>One texel's four lanes, each within rounding of a 32-bit bake.</summary>
    static void Near((float R, float G, float B, float A) expected, (float R, float G, float B, float A) actual) =>
        Assert.True(
            MathF.Abs(expected.R - actual.R) < Close && MathF.Abs(expected.G - actual.G) < Close
            && MathF.Abs(expected.B - actual.B) < Close && MathF.Abs(expected.A - actual.A) < Close,
            $"expected {expected} and baked {actual}"
        );
}
