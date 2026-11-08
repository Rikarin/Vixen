// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>The § 4.3 kernels on an image that is not square, which is where a per-axis mistake lives.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every space-kernel assertion in the branch this file was added to was made at
///         64×64</b>, and a square image is the one shape that cannot see the defect
///         <a href="https://github.com/Rikarin/Vixen/issues/677">#677</a> names: a footprint whose
///         columns are divided by the wrong output extent is *exactly right* when the two extents are
///         one number. A whole class of arithmetic — anything of the form
///         <c>something / size.x</c> against <c>something / size.y</c> — is invisible until the image
///         is oblong.
///     </para>
///     <para>
///         <b>The oracle is the same one § 4.3 is measured by everywhere else</b>: a one-texel
///         checkerboard has a mean of exactly one half, so a correct minification of it is 128
///         everywhere and a point-sampled one is 0 or 255 everywhere. Those two are as far apart as a
///         picture gets. ⚠ The 256×64 quarter turn below used the first half — it minified four times
///         — until Patina #175 made the turn one in texels; it now uses the second, because a turn in
///         texels reads one texel per texel and a wrongly divided footprint is a grey.
///     </para>
/// </remarks>
public class TextureAspectDeviceTests(ITestOutputHelper output) {
    /// <summary>A quarter turn, in the radians `Transform2D` takes since #735.</summary>
    /// <remarks>
    ///     ⚠ It was <c>0.25f</c> here, because the kernel's rotation was in turns — the one thing
    ///     that stayed the same across that change is what the picture has to look like.
    /// </remarks>
    const float QuarterTurn = MathF.PI / 2f;

    /// <summary>The wide axis. Four times the tall one, so a footprint divided by the wrong one is four.</summary>
    const int Wide = 256;

    const int Tall = 64;

    static TextureOp Op(string kernel, int output, int[] inputs, TextureParameter[] parameters) =>
        new() { Kernel = kernel, Output = output, Inputs = [.. inputs], Parameters = [.. parameters] };

    static TextureParameter[] Transform(float rotation, TextureTiling tiling, TextureFilter filter) => [
        new("rotation", rotation),
        new("scaleX", 1f),
        new("scaleY", 1f),
        new("offsetX", 0f),
        new("offsetY", 0f),
        new("shearX", 0f),
        new("shearY", 0f),
        new("tiling", (float)tiling),
        new("filter", (float)filter)
    ];

    /// <summary>One op over one uploaded picture, on a plan whose base is not square.</summary>
    static Bitmap OneOp(
        VulkanDevice device,
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int baseWidth,
        int baseHeight,
        TextureOp op
    ) {
        var (texture, staging) = TextureKernelHarness.Upload(device, source, sourceWidth, sourceHeight);

        try {
            var plan = new TexturePlan {
                BaseWidth = baseWidth,
                BaseHeight = baseHeight,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
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

    /// <summary>
    ///     ⚠ A quarter turn on a 4:1 image is taken in texels, so it minifies nothing — and the footprint
    ///     still has to be measured per axis to say so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This said the opposite until Patina #175</b>, and the arithmetic was right for what the
    ///         kernel did then: it turned the unit square, so a quarter turn laid the 256 columns of a
    ///         256×64 image onto its 64 rows, and a correct footprint averaged four of them into 128. That
    ///         is a turn that squashes a circle into an ellipse by the aspect, which no artist turning a
    ///         picture means. The kernel now conjugates its inverse by the aspect, so the turn happens in
    ///         a square space and every texel centre lands on a texel centre.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The guard <a href="https://github.com/Rikarin/Vixen/issues/677">#677</a> put here
    ///         survives the change of answer.</b> At a quarter turn the inverse is now
    ///         <c>(0 ¼ / −4 0)</c>, and the footprints are <c>|(0, −4 · 64/256)| = 1</c> and
    ///         <c>|(¼ · 256/64, 0)| = 1</c>: one tap. Dividing either column by the other axis's extent
    ///         makes a footprint of four, four taps, and a grey where a point-sampled turn of a column
    ///         checkerboard is a row checkerboard of pure black and white.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_quarter_turn_on_a_wide_image_is_taken_in_texels_and_minifies_nothing() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            TextureKernelHarness.Columns(Wide, Tall),
            Wide,
            Tall,
            Wide,
            Tall,
            Op("Transform2D", 1, [0], Transform(QuarterTurn, TextureTiling.Wrap, TextureFilter.Point))
        );

        Assert.Equal(Wide, picture.Width);
        Assert.Equal(Tall, picture.Height);

        AssertStriped(picture, alongX: true, $"a column checkerboard on a {Wide}×{Tall} image, turned a quarter, on {TextureKernelHarness.Adapter(device)}");
    }

    /// <summary>The transpose of the same case, so neither axis is right by accident.</summary>
    /// <remarks>
    ///     A 64×256 image turned a quarter maps a <em>row</em> checkerboard onto columns, which exercises
    ///     the other column of the inverse. A fix that corrected <c>footX</c> and left <c>footY</c> alone
    ///     passes the test above and fails this one.
    /// </remarks>
    [Fact]
    public void A_quarter_turn_on_a_tall_image_is_taken_in_texels_too() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            TextureKernelHarness.Rows(Tall, Wide),
            Tall,
            Wide,
            Tall,
            Wide,
            Op("Transform2D", 1, [0], Transform(QuarterTurn, TextureTiling.Wrap, TextureFilter.Point))
        );

        Assert.Equal(Tall, picture.Width);
        Assert.Equal(Wide, picture.Height);

        AssertStriped(picture, alongX: false, $"a row checkerboard on a {Tall}×{Wide} image, turned a quarter, on {TextureKernelHarness.Adapter(device)}");
    }

    /// <summary>Every texel pure black or white, constant along one axis and alternating along the other.</summary>
    static void AssertStriped(Bitmap picture, bool alongX, string what) {
        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                var value = TextureKernelHarness.At(picture, x, y, 0);
                var first = alongX ? TextureKernelHarness.At(picture, 0, y, 0) : TextureKernelHarness.At(picture, x, 0, 0);
                var neighbour = alongX
                    ? TextureKernelHarness.At(picture, x, (y + 1) % picture.Height, 0)
                    : TextureKernelHarness.At(picture, (x + 1) % picture.Width, y, 0);

                Assert.True(value is 0 or 255, $"{what}: ({x}, {y}) is {value}, a mix where a turn in texels reads one texel.");
                Assert.True(value == first, $"{what}: ({x}, {y}) is {value} and its stripe began {first}.");
                Assert.True(value != neighbour, $"{what}: ({x}, {y}) and its neighbour across the stripes are both {value}.");
            }
        }
    }

    /// <summary>
    ///     ⚠ <c>Resample</c>'s box counts source texels per output texel <em>per axis</em>, and a
    ///     source with one aspect ratio read into a target with another is where that shows.
    /// </summary>
    /// <remarks>
    ///     <b>An external image is the only way to make the two ratios differ</b>, because every
    ///     relative image in a plan is the same level offset in both axes. A 256×64 source read into
    ///     a 64×64 target is 4:1 across and 1:1 down — so a row checkerboard has to survive it
    ///     unchanged, alternating 0 and 255 down the image. A box that used one ratio for both axes
    ///     would average four rows together and answer 128 everywhere: a flat picture where the
    ///     source's whole content was the alternation.
    /// </remarks>
    [Fact]
    public void A_box_resample_counts_its_ratio_along_each_axis_separately() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            TextureKernelHarness.Rows(Wide, Tall),
            Wide,
            Tall,
            Tall,
            Tall,
            Op("Resample", 1, [0], [new("filter", (float)TextureFilter.Box)])
        );

        Assert.Equal(Tall, picture.Width);
        Assert.Equal(Tall, picture.Height);

        for (var y = 0; y < Tall; y++) {
            var wanted = y % 2 == 0 ? 0 : 255;
            var measured = TextureKernelHarness.At(picture, 17, y, 0);

            Assert.True(
                Math.Abs(measured - wanted) <= 2,
                $"row {y} of a 4:1 box resample is {measured} and the source's row is {wanted} — a box "
                + $"that averaged four rows would answer 128 ({TextureKernelHarness.Adapter(device)})"
            );
        }
    }

    /// <summary>
    ///     <c>Tile</c>'s repeat is counted in normalised space, so an uneven repeat minifies one axis
    ///     alone.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>And that is a refutation rather than a fix.</b> <c>Tile</c> derives no per-axis
    ///     footprint at all — it supersamples <c>max(repeatX, repeatY)</c> along both axes — so
    ///     #677's mistake cannot be made there. It over-samples the unminified axis and is never
    ///     under-sampled, which costs a little work and no correctness. What was missing was any test
    ///     at an uneven repeat, so this is the one: eight across and one down, over a column
    ///     checkerboard, is a pure 8× minification in x and must come back grey.
    /// </remarks>
    [Fact]
    public void An_uneven_tile_repeat_still_area_averages_the_axis_it_minifies() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = OneOp(
            device,
            TextureKernelHarness.Columns(Tall, Tall),
            Tall,
            Tall,
            Tall,
            Tall,
            Op(
                "Tile",
                1,
                [0],
                [new("repeatX", 8f), new("repeatY", 1f), new("offsetX", 0f), new("offsetY", 0f)]
            )
        );

        AssertFlat(
            picture,
            128,
            8,
            $"a column checkerboard tiled eight across and once down on {TextureKernelHarness.Adapter(device)}"
        );
    }
}
