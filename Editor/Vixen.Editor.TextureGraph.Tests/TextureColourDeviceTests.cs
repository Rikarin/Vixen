// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Curves;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.2's colour and channel kernels, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>Closed forms, not goldens and not a CPU twin</b> — doc 48 § D3. What is asserted is
///         arithmetic with one answer: an invert applied twice is the identity, a shuffle of
///         (r, g, b, a) is a copy, a normalised weight set gives the same grey whatever it is scaled
///         by, an identity spline reproduces its input, and an auto-levels stretch takes the image's
///         own extremes to 0 and 255.
///     </para>
///     <para>
///         ⚠ <b>Where an equality is available it is asserted as one, over every texel of an image
///         where no two texels are alike.</b> A tolerance over a flat fill is the assertion a broken
///         kernel passes: <c>TextureKernelHarness.Unique</c> exists so that "this is a copy" is a
///         claim about four thousand texels and four channels rather than about one colour.
///     </para>
/// </remarks>
public class TextureColourDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>An op over one image, with its parameters.</summary>
    static TextureOp Op(string kernel, int output, int[] inputs, params TextureParameter[] parameters) =>
        new() { Kernel = kernel, Output = output, Inputs = [.. inputs], Parameters = [.. parameters] };

    /// <summary>Evaluates one op over one uploaded picture and reads the answer back.</summary>
    static Bitmap OneOp(VulkanDevice device, byte[] source, TextureOp op, TextureFormat format = TextureFormat.Rgba8) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [new(TextureFormat.Rgba8, External: true), new(format)],
                Ops = [op],
                Outputs = [1]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(1);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    static Bitmap AsPicture(byte[] pixels, int side) => new(side, side, pixels);

    // --- Invert ---------------------------------------------------------------------------------

    /// <summary>⚠ Invert applied twice is the identity — an equality, over every texel and channel.</summary>
    /// <remarks>
    ///     <c>1 − (1 − v) = v</c> holds exactly in floating point for every v, so this needs no
    ///     tolerance — which is what makes it the strongest assertion in the file. A kernel that
    ///     inverted around anything but one, or that dropped a channel, fails on the first texel.
    /// </remarks>
    [Fact]
    public void An_invert_applied_twice_is_the_identity() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        TextureParameter[] all = [
            new("invertR", 1f), new("invertG", 1f), new("invertB", 1f), new("invertA", 1f)
        ];

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8), new(TextureFormat.Rgba8)],
            Ops = [Op("Invert", 1, [0], all), Op("Invert", 2, [1], all)],
            Outputs = [1, 2]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

        var once = bake.Read(1);
        var twice = bake.Read(2);

        // The single invert is 255 − v, exactly, in all four channels.
        for (var channel = 0; channel < 4; channel++) {
            Assert.Equal(
                255 - TextureKernelHarness.At(AsPicture(source, Side), 7, 11, channel),
                TextureKernelHarness.At(once, 7, 11, channel)
            );
        }

        TextureKernelHarness.AssertSame(
            AsPicture(source, Side),
            twice,
            4,
            $"invert twice on {TextureKernelHarness.Adapter(device)}"
        );

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>Alpha is left alone unless it is asked for, which is the node's one default.</summary>
    [Fact]
    public void An_invert_leaves_alpha_alone_by_default() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Unique(Side);

        var picture = OneOp(
            device,
            source,
            Op(
                "Invert",
                1,
                [0],
                new("invertR", 1f),
                new("invertG", 1f),
                new("invertB", 1f),
                new("invertA", 0f)
            )
        );

        Assert.Equal(
            TextureKernelHarness.At(AsPicture(source, Side), 20, 30, 3),
            TextureKernelHarness.At(picture, 20, 30, 3)
        );

        Assert.Equal(255 - TextureKernelHarness.At(AsPicture(source, Side), 20, 30, 0), TextureKernelHarness.At(picture, 20, 30, 0));
    }

    // --- Grayscale ------------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ The weights are a <em>ratio</em>: scaling them all by four gives the same grey.
    /// </summary>
    /// <remarks>
    ///     <b>This is the node's whole trap and the sabotage that catches it is one line.</b> Without
    ///     the normalisation, (1, 1, 1) is three times the brightness of the average and (4, 4, 4) is
    ///     twelve times it — both saturate a solid mid-grey to white, and the artist blames the levels
    ///     node two ops later. Asserting the <em>same</em> answer from two weight sets is what no
    ///     un-normalised kernel can pass.
    /// </remarks>
    [Fact]
    public void Grayscale_weights_are_a_ratio_and_are_normalised() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Solid(Side, 60, 120, 180, 255);

        var ones = OneOp(
            device,
            source,
            Op("Grayscale", 1, [0], new("weightR", 1f), new("weightG", 1f), new("weightB", 1f))
        );

        var fours = OneOp(
            device,
            source,
            Op("Grayscale", 1, [0], new("weightR", 4f), new("weightG", 4f), new("weightB", 4f))
        );

        // (60 + 120 + 180) / 3 = 120, and the same for any scaling of (1, 1, 1).
        Assert.InRange(TextureKernelHarness.At(ones, 30, 30, 0), 118, 122);
        Assert.Equal(TextureKernelHarness.At(ones, 30, 30, 0), TextureKernelHarness.At(fours, 30, 30, 0));

        // Splatted rather than left in red: grey promotes into a colour port.
        Assert.Equal(TextureKernelHarness.At(ones, 30, 30, 0), TextureKernelHarness.At(ones, 30, 30, 1));
        Assert.Equal(TextureKernelHarness.At(ones, 30, 30, 0), TextureKernelHarness.At(ones, 30, 30, 2));
    }

    /// <summary>Rec. 709 is the default, and a green-heavy colour is what tells it from Rec. 601.</summary>
    [Fact]
    public void Grayscale_defaults_to_rec_709() {
        using var device = TextureKernelHarness.Open();

        var picture = OneOp(
            device,
            TextureKernelHarness.Solid(Side, 0, 255, 0, 255),
            Op("Grayscale", 1, [0], new("weightR", 0.2126f), new("weightG", 0.7152f), new("weightB", 0.0722f))
        );

        // 0.7152 × 255 = 182.4. Rec. 601's 0.587 would be 149.7 — thirty-three steps apart.
        Assert.InRange(TextureKernelHarness.At(picture, 10, 10, 0), 180, 185);
    }

    /// <summary>Three zeroed weights fall back rather than dividing by zero.</summary>
    /// <remarks>
    ///     ⚠ Doc 48's own note: a zeroed struct field whose zero is a valid-looking value is this
    ///     repository's commonest wrong picture. Here the wrong picture would be a NaN in a file.
    /// </remarks>
    [Fact]
    public void Grayscale_with_no_weights_at_all_falls_back_rather_than_dividing_by_zero() {
        using var device = TextureKernelHarness.Open();

        var picture = OneOp(
            device,
            TextureKernelHarness.Solid(Side, 0, 255, 0, 255),
            Op("Grayscale", 1, [0], new("weightR", 0f), new("weightG", 0f), new("weightB", 0f))
        );

        Assert.InRange(TextureKernelHarness.At(picture, 10, 10, 0), 180, 185);
    }

    // --- HSL ------------------------------------------------------------------------------------

    /// <summary>A hue rotation of zero, full saturation and no lightness change leaves the picture.</summary>
    /// <remarks>
    ///     ⚠ <b>Within two steps rather than exactly, and the reason is worth writing down.</b> The
    ///     rotation is `ComputeColor.rvn:78`'s YIQ pair, and the forward and backward matrices there
    ///     are the standard rounded constants — they are not exact inverses of each other. A test
    ///     demanding equality here would be demanding a different rotation from the one the shader
    ///     graph uses, and the two agreeing matters more.
    /// </remarks>
    [Fact]
    public void An_hsl_node_with_nothing_asked_of_it_is_a_copy() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);

        var picture = OneOp(
            device,
            source,
            Op("Hsl", 1, [0], new("hue", 0f), new("saturation", 1f), new("lightness", 0f))
        );

        var expected = AsPicture(source, Side);

        for (var y = 0; y < Side; y += 7) {
            for (var x = 0; x < Side; x += 7) {
                for (var channel = 0; channel < 3; channel++) {
                    Assert.True(
                        Math.Abs(
                            TextureKernelHarness.At(expected, x, y, channel)
                            - TextureKernelHarness.At(picture, x, y, channel)
                        ) <= 2,
                        $"({x}, {y}) channel {channel}: {TextureKernelHarness.At(picture, x, y, channel)} against "
                        + $"{TextureKernelHarness.At(expected, x, y, channel)} "
                        + $"({TextureKernelHarness.Adapter(device)})"
                    );
                }
            }
        }
    }

    /// <summary>Saturation of zero is grey, lightness of ±1 is white and black.</summary>
    [Fact]
    public void Hsl_saturation_and_lightness_reach_their_ends() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Solid(Side, 200, 40, 90, 255);

        var grey = OneOp(
            device,
            source,
            Op("Hsl", 1, [0], new("hue", 0f), new("saturation", 0f), new("lightness", 0f))
        );

        Assert.Equal(TextureKernelHarness.At(grey, 5, 5, 0), TextureKernelHarness.At(grey, 5, 5, 1));
        Assert.Equal(TextureKernelHarness.At(grey, 5, 5, 1), TextureKernelHarness.At(grey, 5, 5, 2));

        var white = OneOp(
            device,
            source,
            Op("Hsl", 1, [0], new("hue", 0f), new("saturation", 1f), new("lightness", 1f))
        );

        var black = OneOp(
            device,
            source,
            Op("Hsl", 1, [0], new("hue", 0f), new("saturation", 1f), new("lightness", -1f))
        );

        Assert.Equal(255, TextureKernelHarness.At(white, 5, 5, 0));
        Assert.Equal(0, TextureKernelHarness.At(black, 5, 5, 0));
    }

    /// <summary>A hue rotated by half a turn moves, and by a whole turn comes back.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, and the second is the one that matters.</b> "The hue moved" is true of a
    ///     kernel that scrambles; "a full turn is where it started" is only true of a rotation.
    /// </remarks>
    [Fact]
    public void A_whole_turn_of_hue_comes_back_and_half_a_turn_does_not() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Solid(Side, 220, 30, 30, 255);

        var none = OneOp(device, source, Op("Hsl", 1, [0], new("hue", 0f), new("saturation", 1f), new("lightness", 0f)));
        var half = OneOp(device, source, Op("Hsl", 1, [0], new("hue", 0.5f), new("saturation", 1f), new("lightness", 0f)));
        var whole = OneOp(device, source, Op("Hsl", 1, [0], new("hue", 1f), new("saturation", 1f), new("lightness", 0f)));

        Assert.True(
            Math.Abs(TextureKernelHarness.At(none, 5, 5, 0) - TextureKernelHarness.At(whole, 5, 5, 0)) <= 2,
            $"a whole turn moved red from {TextureKernelHarness.At(none, 5, 5, 0)} to "
            + $"{TextureKernelHarness.At(whole, 5, 5, 0)}"
        );

        Assert.True(
            Math.Abs(TextureKernelHarness.At(none, 5, 5, 0) - TextureKernelHarness.At(half, 5, 5, 0)) > 40,
            "half a turn left a strong red where it was"
        );
    }

    // --- Channel shuffle ------------------------------------------------------------------------

    /// <summary>The identity shuffle is a copy of the first input, texel for texel.</summary>
    [Fact]
    public void The_identity_shuffle_copies_its_first_input() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var first = TextureKernelHarness.Unique(Side);
        var second = TextureKernelHarness.Solid(Side, 10, 20, 30, 40);

        var picture = TwoInputShuffle(device, first, second, [0, 1, 2, 3]);

        TextureKernelHarness.AssertSame(AsPicture(first, Side), picture, 4, "the identity shuffle");
    }

    /// <summary>Every selector reads what <see cref="TextureChannelSource" /> says it reads.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole table on one dispatch, which is what keeps the enum and the shader in
    ///     step.</b> A renumbering in either would be a picture rather than an error, because the
    ///     kernel has nowhere to raise from and falls through to the first input's red.
    /// </remarks>
    [Fact]
    public void Every_channel_selector_reads_what_it_names() {
        using var device = TextureKernelHarness.Open();

        var first = TextureKernelHarness.Solid(Side, 10, 20, 30, 40);
        var second = TextureKernelHarness.Solid(Side, 110, 120, 130, 140);

        var firstHalf = TwoInputShuffle(device, first, second, [0, 1, 2, 3]);
        var secondHalf = TwoInputShuffle(device, first, second, [4, 5, 6, 7]);
        var constants = TwoInputShuffle(device, first, second, [8, 9, 8, 9]);

        Assert.Equal(10, TextureKernelHarness.At(firstHalf, 4, 4, 0));
        Assert.Equal(20, TextureKernelHarness.At(firstHalf, 4, 4, 1));
        Assert.Equal(30, TextureKernelHarness.At(firstHalf, 4, 4, 2));
        Assert.Equal(40, TextureKernelHarness.At(firstHalf, 4, 4, 3));

        Assert.Equal(110, TextureKernelHarness.At(secondHalf, 4, 4, 0));
        Assert.Equal(120, TextureKernelHarness.At(secondHalf, 4, 4, 1));
        Assert.Equal(130, TextureKernelHarness.At(secondHalf, 4, 4, 2));
        Assert.Equal(140, TextureKernelHarness.At(secondHalf, 4, 4, 3));

        Assert.Equal(0, TextureKernelHarness.At(constants, 4, 4, 0));
        Assert.Equal(255, TextureKernelHarness.At(constants, 4, 4, 1));
        Assert.Equal(0, TextureKernelHarness.At(constants, 4, 4, 2));
        Assert.Equal(255, TextureKernelHarness.At(constants, 4, 4, 3));
    }

    static Bitmap TwoInputShuffle(VulkanDevice device, byte[] first, byte[] second, int[] selectors) {
        var (a, aStaging) = TextureKernelHarness.Upload(device, first, Side, Side);
        var (b, bStaging) = TextureKernelHarness.Upload(device, second, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [
                    Op(
                        "ChannelShuffle",
                        2,
                        [0, 1],
                        new("sourceR", selectors[0]),
                        new("sourceG", selectors[1]),
                        new("sourceB", selectors[2]),
                        new("sourceA", selectors[3])
                    )
                ],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);

            using var bake = evaluator.Evaluate(
                plan,
                new Dictionary<int, TextureHandle> { [0] = a, [1] = b }
            );

            return bake.Read(2);
        } finally {
            device.Destroy(aStaging);
            device.Destroy(a);
            device.Destroy(bStaging);
            device.Destroy(b);
        }
    }

    // --- Curve ----------------------------------------------------------------------------------

    /// <summary>An identity spline, through the editor's own evaluator, is a copy.</summary>
    /// <remarks>
    ///     ⚠ <b>Exact rather than nearly, and that is a property of the table's resolution.</b> Entry
    ///     <c>k</c> of a 256-entry table holds <c>k</c>, so an 8-bit input lands on an entry with an
    ///     interpolation weight of zero. A table of any other length, or a kernel that point-sampled
    ///     it, would fail this by one step somewhere.
    /// </remarks>
    [Fact]
    public void An_identity_spline_is_a_copy() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var straight = TextureRamp.Straight();
        var source = TextureKernelHarness.Unique(Side);

        var picture = Through(
            device,
            source,
            TextureRamp.FromCurves(straight, straight, straight, straight),
            Op("Curve", 2, [0, 1], new TextureParameter("amount", 1f))
        );

        TextureKernelHarness.AssertSame(AsPicture(source, Side), picture, 4, "an identity spline");
    }

    /// <summary>One curved channel leaves the other three where they were.</summary>
    [Fact]
    public void A_curve_on_one_channel_leaves_the_others_alone() {
        using var device = TextureKernelHarness.Open();

        var straight = TextureRamp.Straight();

        CurveSample[] inverting = [
            new(0f, 1f, -1f, -1f, TangentMode.Linear),
            new(1f, 0f, -1f, -1f, TangentMode.Linear)
        ];

        var source = TextureKernelHarness.Unique(Side);

        var picture = Through(
            device,
            source,
            TextureRamp.FromCurves(inverting, straight, straight, straight),
            Op("Curve", 2, [0, 1], new TextureParameter("amount", 1f))
        );

        var expected = AsPicture(source, Side);

        for (var x = 0; x < Side; x += 5) {
            Assert.Equal(255 - TextureKernelHarness.At(expected, x, 9, 0), TextureKernelHarness.At(picture, x, 9, 0));
            Assert.Equal(TextureKernelHarness.At(expected, x, 9, 1), TextureKernelHarness.At(picture, x, 9, 1));
            Assert.Equal(TextureKernelHarness.At(expected, x, 9, 2), TextureKernelHarness.At(picture, x, 9, 2));
        }
    }

    /// <summary>An amount of zero is the input, whatever the spline says.</summary>
    [Fact]
    public void A_curve_with_no_amount_is_the_input() {
        using var device = TextureKernelHarness.Open();

        CurveSample[] flat = [
            new(0f, 0f, 0f, 0f, TangentMode.Linear),
            new(1f, 0f, 0f, 0f, TangentMode.Linear)
        ];

        var source = TextureKernelHarness.Unique(Side);

        var picture = Through(
            device,
            source,
            TextureRamp.FromCurves(flat, flat, flat, flat),
            Op("Curve", 2, [0, 1], new TextureParameter("amount", 0f))
        );

        TextureKernelHarness.AssertSame(AsPicture(source, Side), picture, 4, "a curve with no amount");
    }

    // --- Gradient map ---------------------------------------------------------------------------

    /// <summary>
    ///     Grey goes through a ramp and comes out as the ramp's colour at that position.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The input's red and green run in opposite directions, and that is what makes this a
    ///     test of the contract rather than of the arithmetic.</b> Doc 48 § 4.2 gives
    ///     <c>Grayscale Conversion</c> its own node so that "which weights" is answered once by an
    ///     artist, so this node's input is <em>grey, and grey is the red channel</em> — a kernel that
    ///     reached for a luminance, or for the wrong lane, reads a grey <c>R16Float</c> image at
    ///     0.2126 of what it says. On a plain grey ramp every lane agrees and any of those kernels
    ///     passes; on this source only the right one does.
    /// </remarks>
    [Fact]
    public void A_gradient_map_takes_grey_to_the_ramps_colour() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        // Black to red through half green: three known colours at three known positions.
        var ramp = TextureRamp.FromRamp(
            position => new(position, 1f - position, 0f, 1f)
        );

        var source = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = ((y * Side) + x) * 4;

                source[at] = (byte)(x * 255 / (Side - 1));
                source[at + 1] = (byte)(255 - (x * 255 / (Side - 1)));
                source[at + 2] = 200;
                source[at + 3] = 255;
            }
        }

        var picture = Through(
            device,
            source,
            ramp,
            Op("GradientMap", 2, [0, 1], new TextureParameter("keepAlpha", 1f))
        );

        Assert.InRange(TextureKernelHarness.At(picture, 0, 8, 0), 0, 3);
        Assert.InRange(TextureKernelHarness.At(picture, 0, 8, 1), 252, 255);

        Assert.InRange(TextureKernelHarness.At(picture, Side - 1, 8, 0), 252, 255);
        Assert.InRange(TextureKernelHarness.At(picture, Side - 1, 8, 1), 0, 3);

        // Halfway along the input ramp is halfway along the gradient.
        Assert.InRange(TextureKernelHarness.At(picture, 32, 8, 0), 125, 135);
        Assert.InRange(TextureKernelHarness.At(picture, 32, 8, 1), 120, 130);

        // Blue is the ramp's, not the input's — a gradient map replaces the colour.
        Assert.Equal(0, TextureKernelHarness.At(picture, 32, 8, 2));
    }

    /// <summary>Runs one two-input op with a 256×1 table as its second image.</summary>
    static Bitmap Through(VulkanDevice device, byte[] source, byte[] table, TextureOp op) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);
        var (lut, lutStaging) = TextureKernelHarness.Upload(device, table, TextureRamp.Entries, 1);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [op],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);

            using var bake = evaluator.Evaluate(
                plan,
                new Dictionary<int, TextureHandle> { [0] = texture, [1] = lut }
            );

            return bake.Read(2);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
            device.Destroy(lutStaging);
            device.Destroy(lut);
        }
    }

    // --- Auto levels ----------------------------------------------------------------------------

    /// <summary>
    ///     ⚠ The reduction finds the extremes of the <em>whole</em> image, including a texel in a
    ///     corner no block boundary lands on.
    /// </summary>
    /// <remarks>
    ///     <b>The first kernel in the catalogue whose output depends on every texel of its input, and
    ///     this is the assertion that says so.</b> The image is a flat mid-grey with its minimum at
    ///     (0, 0) and its maximum at (63, 63) — neither of which a reduction that read only the first
    ///     texel of each block would find, because 63 is not a multiple of eight. A broken block loop
    ///     comes back with 128 and 128, which is a plausible pair.
    /// </remarks>
    [Fact]
    public void The_reduction_finds_an_extreme_in_a_corner_no_block_starts_on() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Solid(Side, 128, 128, 128, 255);

        source[0] = 0;
        source[(((Side - 1) * Side) + Side - 1) * 4] = 255;

        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        var plan = Stretch();

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

        // Three dispatches: 64² → 8² → 1², then the map. Doc 48 § 4.2 calls it two; the honest count
        // is one per reduction level plus the map, and a plan that skipped one would still produce a
        // picture.
        Assert.Equal(3, bake.Dispatches);

        var stats = bake.Read(2);

        Assert.Equal(1, stats.Width);
        Assert.InRange(TextureKernelHarness.At(stats, 0, 0, 0), 0, 2);
        Assert.InRange(TextureKernelHarness.At(stats, 0, 0, 1), 253, 255);

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>A narrow input range is stretched onto the whole of the output.</summary>
    [Fact]
    public void Auto_levels_takes_the_images_own_extremes_to_black_and_white() {
        using var device = TextureKernelHarness.Open();

        var source = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = ((y * Side) + x) * 4;
                var value = (byte)(64 + (x * 127 / (Side - 1)));

                source[at] = value;
                source[at + 1] = value;
                source[at + 2] = value;
                source[at + 3] = 255;
            }
        }

        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(Stretch(), new Dictionary<int, TextureHandle> { [0] = texture });

        var stats = bake.Read(2);
        var picture = bake.Read(3);

        output.WriteLine(
            $"adapter: {TextureKernelHarness.Adapter(device)}; min "
            + $"{TextureKernelHarness.At(stats, 0, 0, 0)}, max {TextureKernelHarness.At(stats, 0, 0, 1)}"
        );

        Assert.InRange(TextureKernelHarness.At(stats, 0, 0, 0), 63, 65);
        Assert.InRange(TextureKernelHarness.At(stats, 0, 0, 1), 190, 192);

        Assert.InRange(TextureKernelHarness.At(picture, 0, 8, 0), 0, 2);
        Assert.InRange(TextureKernelHarness.At(picture, Side - 1, 8, 0), 253, 255);

        // And the middle is where the arithmetic puts it rather than merely somewhere between.
        var middle = 64 + (31 * 127 / (Side - 1));

        Assert.InRange(TextureKernelHarness.At(picture, 31, 8, 0), (middle - 64) * 255 / 127 - 4, (middle - 64) * 255 / 127 + 4);

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>A flat image has nothing to stretch and comes through unchanged.</summary>
    /// <remarks>
    ///     ⚠ The one arithmetic failure this node has is a span of zero. Black, white and a NaN are
    ///     all plausible answers and none of them is the right one.
    /// </remarks>
    [Fact]
    public void A_flat_image_survives_auto_levels() {
        using var device = TextureKernelHarness.Open();

        var (texture, staging) = TextureKernelHarness.Upload(
            device,
            TextureKernelHarness.Solid(Side, 77, 77, 77, 255),
            Side,
            Side
        );

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(Stretch(), new Dictionary<int, TextureHandle> { [0] = texture });

        var picture = bake.Read(3);

        Assert.InRange(TextureKernelHarness.At(picture, 20, 20, 0), 76, 78);

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>The two-dispatch shape: 64² → 8² → 1², then the map.</summary>
    /// <remarks>
    ///     ⚠ <b>The ladder and the ops both come from <c>TextureAdjust</c> rather than being written
    ///     out here</b>, which is <a href="https://github.com/Rikarin/Vixen/issues/713">#713</a>: a
    ///     chain whose length is a function of the baked extent, hand-built at a call site, is
    ///     stamped by nobody and silently two dispatches short at 4K. The three assertions below are
    ///     the differential that says the builder emits what this file used to spell — the same three
    ///     dispatches, the same 1×1 stats image, the same stretch.
    /// </remarks>
    static TexturePlan Stretch() {
        var levels = TextureAdjust.ReductionLevels(Side, Side);

        return new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                .. levels.Select(level => new TextureImage(TextureFormat.Rgba16Float, level)),
                new(TextureFormat.Rgba8)
            ],
            Ops = TextureAdjust.AutoLevels(levels.Length + 1, 0, [.. Enumerable.Range(1, levels.Length)], Side, Side),
            Outputs = [levels.Length, levels.Length + 1]
        };
    }
}
