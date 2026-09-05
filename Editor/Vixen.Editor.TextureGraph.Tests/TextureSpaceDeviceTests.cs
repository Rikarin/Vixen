// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.3's space kernels, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>Three laws carry this file, and each catches a different class of mistake.</b>
///         <b>Identity</b> — a transform by nothing, a tile of one, a crop of everything and a
///         resample to the same size are all <em>copies</em>, texel for texel, over an image where no
///         two texels are alike. <b>Involution and idempotence</b> — a flip twice is the identity and
///         a reflect twice is a reflect, which are different laws and a test that asserted the wrong
///         one would pass on a symmetric image. <b>Area</b> — a one-texel column checkerboard has a
///         mean of exactly one half, so any correct minification of it is 128 everywhere and any
///         point-sampled one is 0 or 255 everywhere.
///     </para>
///     <para>
///         ⚠ <b>That third law is the whole of § 4.3's warning.</b> Minification that is not
///         area-correct aliases, and the aliasing is blamed on the noise upstream rather than on the
///         transform. 128 against 0-or-255 is as far apart as a picture gets, which is why it is
///         asserted rather than eyeballed.
///     </para>
/// </remarks>
public class TextureSpaceDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    static TextureOp Op(string kernel, int output, int[] inputs, TextureParameter[] parameters) =>
        new() { Kernel = kernel, Output = output, Inputs = [.. inputs], Parameters = [.. parameters] };

    static TextureParameter[] Identity() => [
        new("rotation", 0f),
        new("scaleX", 1f),
        new("scaleY", 1f),
        new("offsetX", 0f),
        new("offsetY", 0f),
        new("shearX", 0f),
        new("shearY", 0f),
        new("tiling", (float)TextureTiling.Clamp),
        new("filter", (float)TextureFilter.Point)
    ];

    static TextureParameter[] Transform(
        float rotation,
        float scale,
        TextureTiling tiling,
        TextureFilter filter
    ) => [
        new("rotation", rotation),
        new("scaleX", scale),
        new("scaleY", scale),
        new("offsetX", 0f),
        new("offsetY", 0f),
        new("shearX", 0f),
        new("shearY", 0f),
        new("tiling", (float)tiling),
        new("filter", (float)filter)
    ];

    static Bitmap AsPicture(byte[] pixels, int side) => new(side, side, pixels);

    /// <summary>One op over one uploaded picture, into an image the plan sizes.</summary>
    static Bitmap OneOp(VulkanDevice device, byte[] source, int side, TextureOp op, int outputLevel = 0) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, side, side);

        try {
            var plan = new TexturePlan {
                BaseWidth = side,
                BaseHeight = side,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8, outputLevel)],
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

    /// <summary>Asserts every texel is within a tolerance of one value, and says the worst one.</summary>
    static void AssertFlat(Bitmap picture, int expected, int tolerance, string what) {
        var worst = 0;
        var at = (0, 0);

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                var difference = Math.Abs(TextureKernelHarness.At(picture, x, y, 0) - expected);

                if (difference > worst) {
                    worst = difference;
                    at = (x, y);
                }
            }
        }

        Assert.True(
            worst <= tolerance,
            $"{what}: the worst texel is ({at.Item1}, {at.Item2}), {worst} away from {expected}."
        );
    }

    // --- Transform 2D ---------------------------------------------------------------------------

    /// <summary>An identity transform is a copy, texel for texel.</summary>
    /// <remarks>
    ///     ⚠ <b>Exact rather than nearly, and only because the sample count collapses.</b> At the
    ///     identity the footprint of an output texel is exactly one source texel, so the
    ///     supersampling loop takes one tap, at the texel's own centre. A kernel that always
    ///     supersampled — or that offset by half a texel — would be softer than its input here, which
    ///     is a difference of one or two steps that no tolerance-based test would show.
    /// </remarks>
    [Fact]
    public void An_identity_transform_is_a_copy() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var picture = OneOp(device, source, Side, Op("Transform2D", 1, [0], Identity()));

        TextureKernelHarness.AssertSame(AsPicture(source, Side), picture, 4, "an identity transform");
    }

    /// <summary>A half turn is a flip in both axes, exactly.</summary>
    [Fact]
    public void A_half_turn_is_a_flip_in_both_axes() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Unique(Side);

        var picture = OneOp(
            device,
            source,
            Side,
            Op("Transform2D", 1, [0], Transform(0.5f, 1f, TextureTiling.Clamp, TextureFilter.Point))
        );

        var expected = AsPicture(source, Side);

        for (var y = 0; y < Side; y += 3) {
            for (var x = 0; x < Side; x += 3) {
                Assert.Equal(
                    TextureKernelHarness.At(expected, Side - 1 - x, Side - 1 - y, 0),
                    TextureKernelHarness.At(picture, x, y, 0)
                );
            }
        }
    }

    /// <summary>
    ///     ⚠ Minified by eight, a one-texel checkerboard is grey — which is § 4.3's whole warning.
    /// </summary>
    /// <remarks>
    ///     <b>The closed form: the pattern's mean is exactly one half, so any area-correct
    ///     minification of it is 128 everywhere.</b> A point-sampled transform gives 0 or 255
    ///     everywhere and a badly-sampled one gives a moiré — and the artefact is invariably blamed on
    ///     the generator upstream, because a transform node has no obvious reason to change what a
    ///     noise looks like. The evaluator binds no samplers, so there is no mip chain to ask for:
    ///     the kernel derives the footprint from its own inverse matrix and boxes over it.
    /// </remarks>
    [Fact]
    public void A_minified_checkerboard_is_grey_rather_than_aliased() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            TextureKernelHarness.Columns(Side),
            Side,
            Op("Transform2D", 1, [0], Transform(0f, 0.125f, TextureTiling.Wrap, TextureFilter.Point))
        );

        AssertFlat(picture, 128, 8, $"a checkerboard minified eight times on {TextureKernelHarness.Adapter(device)}");
    }

    /// <summary>
    ///     ⚠ Doc 48 § D8's own criterion: the same graph at 64² and at 256², downsampled, agree.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>And the finding underneath it.</b> Issue #567's exit criterion is agreement between
    ///         a 1K and a downsampled 4K bake within 2/255, and § D8's machinery for it —
    ///         <c>TextureParameterUnit.TexelsAtBase</c> and <c>TexturePlan.Resolve</c> — applies to
    ///         lengths in texels. <b>Not one parameter of § 4.2's or § 4.3's thirteen kernels is a
    ///         length in texels.</b> A rotation is in turns, a scale is a ratio, an offset is a
    ///         fraction of the image, a rect is normalised and a repeat is a count — so these kernels
    ///         are resolution-independent by construction, and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/619">#619</a>'s rework of the base
    ///         resolution cannot change what any of them does.
    ///         <c>TextureColourKernelTests.No_kernel_here_takes_a_length_in_texels</c> is what keeps
    ///         that true.
    ///     </para>
    ///     <para>
    ///         So the criterion is asserted here on the kernel most able to fail it — a rotation, which
    ///         resamples every texel — with the downsample done by <c>Resample</c>'s box in the same
    ///         plan rather than in C#. The border is left out: outside the image the two runs clamp to
    ///         a different number of texels, which is a property of the addressing rather than of the
    ///         scale.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_rotation_agrees_between_a_small_bake_and_a_downsampled_large_one() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int Large = 256;

        var small = Rotated(device, Side, Side, 0);
        var large = Rotated(device, Large, Side, 2);

        var worst = 0;

        for (var y = 12; y < Side - 12; y++) {
            for (var x = 12; x < Side - 12; x++) {
                worst = Math.Max(
                    worst,
                    Math.Abs(TextureKernelHarness.At(small, x, y, 0) - TextureKernelHarness.At(large, x, y, 0))
                );
            }
        }

        output.WriteLine($"worst disagreement between the 64² bake and the downsampled 256² one: {worst}/255");

        Assert.True(worst <= 2, $"the two bakes disagree by {worst}/255 on {TextureKernelHarness.Adapter(device)}");
    }

    /// <summary>A ramp rotated an eighth of a turn at one base, brought back to 64² by a box.</summary>
    static Bitmap Rotated(VulkanDevice device, int side, int read, int downLevels) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var value = (byte)(x * 255 / (side - 1));

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        var (texture, staging) = TextureKernelHarness.Upload(device, pixels, side, side);

        try {
            var plan = new TexturePlan {
                BaseWidth = side,
                BaseHeight = side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8),
                    new(TextureFormat.Rgba8, downLevels)
                ],
                Ops = [
                    Op("Transform2D", 1, [0], Transform(0.125f, 1f, TextureTiling.Clamp, TextureFilter.Bilinear)),
                    Op("Resample", 2, [1], [new("filter", (float)TextureFilter.Box)])
                ],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());
            Assert.Equal(read, plan.SizeOf(2).X);

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return bake.Read(2);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    // --- Mirror ---------------------------------------------------------------------------------

    /// <summary>⚠ A flip is its own inverse: twice is the identity, exactly.</summary>
    [Fact]
    public void A_flip_is_its_own_inverse() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        TextureParameter[] flip = [
            new("axis", (float)TextureMirrorAxis.X),
            new("mode", (float)TextureMirrorMode.Flip),
            new("offset", 0.5f)
        ];

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8), new(TextureFormat.Rgba8)],
            Ops = [Op("Mirror", 1, [0], flip), Op("Mirror", 2, [1], flip)],
            Outputs = [1, 2]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

        var once = bake.Read(1);
        var twice = bake.Read(2);
        var expected = AsPicture(source, Side);

        // One flip is the reversal, and that half has to be asserted too — an identity kernel would
        // pass the involution on its own.
        for (var x = 0; x < Side; x++) {
            Assert.Equal(TextureKernelHarness.At(expected, Side - 1 - x, 9, 0), TextureKernelHarness.At(once, x, 9, 0));
        }

        TextureKernelHarness.AssertSame(expected, twice, 4, "a flip twice");

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>⚠ A reflect is idempotent rather than involutive, and its output is symmetric.</summary>
    /// <remarks>
    ///     <b>The two laws are different and a test that mixed them up would pass.</b> Reflecting an
    ///     image about its middle copies one half onto the other; doing it again changes nothing,
    ///     which is <em>not</em> the identity — and on a symmetric test image it would look like one.
    ///     What says the node did its job is that the result reads the same either side of the line.
    /// </remarks>
    [Fact]
    public void A_reflect_is_idempotent_and_leaves_a_symmetric_picture() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Unique(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        TextureParameter[] reflect = [
            new("axis", (float)TextureMirrorAxis.X),
            new("mode", (float)TextureMirrorMode.Reflect),
            new("offset", 0.5f)
        ];

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8), new(TextureFormat.Rgba8)],
            Ops = [Op("Mirror", 1, [0], reflect), Op("Mirror", 2, [1], reflect)],
            Outputs = [1, 2]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

        var once = bake.Read(1);
        var twice = bake.Read(2);

        TextureKernelHarness.AssertSame(once, twice, 4, "a reflect twice");

        // The left half is the source's, and the right half is the left half backwards.
        var expected = AsPicture(source, Side);

        for (var x = 0; x < Side; x++) {
            Assert.Equal(
                TextureKernelHarness.At(once, x, 5, 0),
                TextureKernelHarness.At(once, Side - 1 - x, 5, 0)
            );
        }

        for (var x = 0; x < Side / 2; x++) {
            Assert.Equal(TextureKernelHarness.At(expected, x, 5, 0), TextureKernelHarness.At(once, x, 5, 0));
        }

        // And it is genuinely not the identity, which is the half that stops "idempotent" being
        // satisfied by a kernel that does nothing at all.
        Assert.NotEqual(
            TextureKernelHarness.At(expected, Side - 1, 5, 0),
            TextureKernelHarness.At(once, Side - 1, 5, 0)
        );

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>The corner axis folds both ways at once.</summary>
    [Fact]
    public void A_corner_flip_reverses_both_axes() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Unique(Side);

        var picture = OneOp(
            device,
            source,
            Side,
            Op(
                "Mirror",
                1,
                [0],
                [
                    new("axis", (float)TextureMirrorAxis.Corner),
                    new("mode", (float)TextureMirrorMode.Flip),
                    new("offset", 0.5f)
                ]
            )
        );

        var expected = AsPicture(source, Side);

        for (var y = 0; y < Side; y += 5) {
            for (var x = 0; x < Side; x += 5) {
                Assert.Equal(
                    TextureKernelHarness.At(expected, Side - 1 - x, Side - 1 - y, 2),
                    TextureKernelHarness.At(picture, x, y, 2)
                );
            }
        }
    }

    // --- Tile -----------------------------------------------------------------------------------

    /// <summary>A repeat of one, unshifted, is a copy.</summary>
    [Fact]
    public void A_tile_of_one_is_a_copy() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);

        var picture = OneOp(
            device,
            source,
            Side,
            Op(
                "Tile",
                1,
                [0],
                [new("repeatX", 1f), new("repeatY", 1f), new("offsetX", 0f), new("offsetY", 0f)]
            )
        );

        TextureKernelHarness.AssertSame(AsPicture(source, Side), picture, 4, "a tile of one");
    }

    /// <summary>A repeat of two is periodic in both axes, exactly.</summary>
    /// <remarks>
    ///     ⚠ <b>Periodicity is the property, not "it looks tiled".</b> A kernel that scaled the UV
    ///     without wrapping produces something that also looks like four copies at a glance and is
    ///     not periodic at all.
    /// </remarks>
    [Fact]
    public void A_tile_of_two_repeats_exactly() {
        using var device = TextureKernelHarness.Open();

        var picture = OneOp(
            device,
            TextureKernelHarness.Unique(Side),
            Side,
            Op(
                "Tile",
                1,
                [0],
                [new("repeatX", 2f), new("repeatY", 2f), new("offsetX", 0f), new("offsetY", 0f)]
            )
        );

        for (var y = 0; y < Side / 2; y++) {
            for (var x = 0; x < Side / 2; x++) {
                Assert.Equal(
                    TextureKernelHarness.At(picture, x, y, 0),
                    TextureKernelHarness.At(picture, x + (Side / 2), y, 0)
                );

                Assert.Equal(
                    TextureKernelHarness.At(picture, x, y, 1),
                    TextureKernelHarness.At(picture, x, y + (Side / 2), 1)
                );
            }
        }
    }

    /// <summary>A checkerboard tiled four times is grey, not a moiré.</summary>
    [Fact]
    public void A_tiled_checkerboard_is_grey_rather_than_aliased() {
        using var device = TextureKernelHarness.Open();

        var picture = OneOp(
            device,
            TextureKernelHarness.Columns(Side),
            Side,
            Op(
                "Tile",
                1,
                [0],
                [new("repeatX", 4f), new("repeatY", 4f), new("offsetX", 0f), new("offsetY", 0f)]
            )
        );

        AssertFlat(picture, 128, 4, $"a checkerboard tiled four times on {TextureKernelHarness.Adapter(device)}");
    }

    // --- Crop -----------------------------------------------------------------------------------

    /// <summary>The whole rect onto the same size is a copy, under both filters.</summary>
    /// <param name="filter">
    ///     The kernel's own selector — <c>0</c> point, <c>1</c> bilinear. ⚠ The number rather than
    ///     <c>TextureFilter</c>, because the enum is internal to the assembly under test.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The bilinear case is the one that carries this test, and the point case on its own
    ///     would not have.</b> The mistake a crop is actually prone to is a half-texel drift — reading
    ///     at the output texel's corner instead of its centre — and the point filter <em>rounds that
    ///     away</em>: a source position half a texel low still lands on the same texel. Under
    ///     bilinear it is a fifty-fifty blend of two neighbours, and every texel of a picture where no
    ///     two are alike disagrees.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_crop_of_everything_is_a_copy(int filter) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        Assert.Contains(filter, new[] { (int)TextureFilter.Point, (int)TextureFilter.Bilinear });

        var source = TextureKernelHarness.Unique(Side);

        var picture = OneOp(
            device,
            source,
            Side,
            Op(
                "Crop",
                1,
                [0],
                [
                    new("rectX", 0f), new("rectY", 0f), new("rectW", 1f), new("rectH", 1f),
                    new("filter", filter)
                ]
            )
        );

        TextureKernelHarness.AssertSame(AsPicture(source, Side), picture, 4, $"a filter-{filter} crop of everything");
    }

    /// <summary>
    ///     ⚠ A quarter rect onto a half-sized image is the source's quadrant, texel for texel.
    /// </summary>
    /// <remarks>
    ///     <b>The one place doc 48 § D8's relative rule is answered rather than inherited</b>: the
    ///     rect is in the source's normalised space and the target's size is the plan's, so a 1:1 crop
    ///     is the pair that agrees. ⚠ And the finding this test is the evidence for:
    ///     <c>TextureImage</c> sizes an image only by a <c>LevelOffset</c>, a power of two from the
    ///     base, so a 1:1 crop is available at the power-of-two rects and nowhere else. A crop to 37%
    ///     of the width has no image to write into. See
    ///     <a href="https://github.com/Rikarin/Vixen/issues/619">#619</a>, which is reworking that
    ///     model.
    /// </remarks>
    [Fact]
    public void A_quarter_rect_onto_a_half_sized_image_is_the_quadrant_itself() {
        using var device = TextureKernelHarness.Open();

        var source = TextureKernelHarness.Unique(Side);

        var picture = OneOp(
            device,
            source,
            Side,
            Op(
                "Crop",
                1,
                [0],
                [
                    new("rectX", 0f), new("rectY", 0f), new("rectW", 0.5f), new("rectH", 0.5f),
                    new("filter", (float)TextureFilter.Point)
                ]
            ),
            1
        );

        Assert.Equal(Side / 2, picture.Width);

        var expected = AsPicture(source, Side);

        for (var y = 0; y < Side / 2; y++) {
            for (var x = 0; x < Side / 2; x++) {
                for (var channel = 0; channel < 4; channel++) {
                    Assert.Equal(
                        TextureKernelHarness.At(expected, x, y, channel),
                        TextureKernelHarness.At(picture, x, y, channel)
                    );
                }
            }
        }
    }

    // --- Resample -------------------------------------------------------------------------------

    /// <summary>A resample to the same size is a copy, under point and under box alike.</summary>
    /// <param name="filter">
    ///     The kernel's own selector — <c>0</c> point, <c>2</c> box. ⚠ The number rather than
    ///     <c>TextureFilter</c>, because the enum is internal to the assembly under test and a public
    ///     theory's parameter cannot be.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_resample_to_the_same_size_is_a_copy(int filter) {
        using var device = TextureKernelHarness.Open();

        Assert.Contains(filter, new[] { (int)TextureFilter.Point, (int)TextureFilter.Box });

        var source = TextureKernelHarness.Unique(Side);
        var picture = OneOp(device, source, Side, Op("Resample", 1, [0], [new("filter", filter)]));

        TextureKernelHarness.AssertSame(
            AsPicture(source, Side),
            picture,
            4,
            $"a filter-{filter} resample to the same size"
        );
    }

    /// <summary>
    ///     ⚠ Halved, a checkerboard is grey under the box and black-and-white under the point.
    /// </summary>
    /// <remarks>
    ///     <b>Both halves in one test, because the second is what makes the first mean something.</b>
    ///     "The box gives 128" would be satisfied by a kernel that always returned 128; the point
    ///     filter on the same input and the same plan giving 0 or 255 is what says the two filters are
    ///     two filters. This is the closed form § 4.3 names for the cheap half of a blur chain.
    /// </remarks>
    [Fact]
    public void A_halved_checkerboard_is_grey_under_the_box_and_not_under_the_point() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Columns(Side);

        var boxed = OneOp(device, source, Side, Op("Resample", 1, [0], [new("filter", (float)TextureFilter.Box)]), 1);
        var pointed = OneOp(device, source, Side, Op("Resample", 1, [0], [new("filter", (float)TextureFilter.Point)]), 1);

        Assert.Equal(Side / 2, boxed.Width);

        AssertFlat(boxed, 128, 2, $"a halved checkerboard, boxed, on {TextureKernelHarness.Adapter(device)}");

        for (var x = 0; x < Side / 2; x++) {
            var value = TextureKernelHarness.At(pointed, x, 4, 0);

            Assert.True(
                value is 0 or 255,
                $"the point filter produced {value} at column {x}, which is neither of the two values in the input — "
                + "so it is not a point sample."
            );
        }
    }
}
