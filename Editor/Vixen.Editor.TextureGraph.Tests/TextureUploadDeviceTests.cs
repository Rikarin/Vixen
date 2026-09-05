// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>That the texels a caller uploads are the texels a kernel reads, on a real device.</summary>
/// <remarks>
///     <para>
///         <b>The seam doc 48 § 4.1's <c>Text</c> and <c>Svg Path</c> arrive through.</b> Neither can
///         be a compute kernel — a kernel has no rasteriser and cannot reach a font or a path parser
///         — so both are filled on the CPU and enter the plan as an external image, which is what
///         <see cref="TextureUploads" /> makes. <a href="https://github.com/Rikarin/Vixen/issues/687">#687</a>
///         named this as the missing step, and this file is the proof that it is not lossy.
///     </para>
///     <para>
///         ⚠ <b>Every test here names its adapter and skips loudly rather than passing without
///         one.</b> Without a real device a headless run falls back to the Null device, exits 0 and
///         prints identical healthy counters — and a round trip asserted there would have proved that
///         a black image equals a black image, because <c>NullDevice</c> reads back zeroes whatever
///         was written. <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure.
///     </para>
///     <para>
///         ⚠ <b>The kernel is <c>Invert</c> with all four switches off, which is an exact copy.</b>
///         A copy is what makes the assertion an equality over every texel rather than a tolerance:
///         an eight-bit unorm survives the round trip through a float and back exactly, so anything
///         that resamples, offsets or drops a channel between the staging buffer and the storage
///         image shows on the first texel it touches. The pattern is
///         <c>TextureKernelHarness.Unique</c>, where no two texels are alike — a flat fill is the
///         picture a broken upload also produces.
///     </para>
/// </remarks>
public class TextureUploadDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>An op that copies its input, channel for channel.</summary>
    static TextureOp Copy(int output, int input) =>
        new() {
            Kernel = "Invert",
            Output = output,
            Inputs = [input],
            Parameters = [new("invertR", 0f), new("invertG", 0f), new("invertB", 0f), new("invertA", 0f)]
        };

    static TexturePlan Plan(TextureFormat source) =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(source, External: true), new(TextureFormat.Rgba8)],
            Ops = [Copy(1, 0)],
            Outputs = [1]
        };

    /// <summary>⚠ An uploaded picture reaches a kernel texel for texel and channel for channel.</summary>
    [Fact]
    public void An_uploaded_picture_is_what_the_kernel_reads() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"upload round trip on {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var plan = Plan(TextureFormat.Rgba8);

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Side, Side, source);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        TextureKernelHarness.AssertSame(
            new(Side, Side, source),
            bake.Read(1),
            4,
            $"uploaded picture on {TextureKernelHarness.Adapter(device)}"
        );
    }

    /// <summary>⚠ A single-channel mask uploads, and a kernel reads it as <c>(r, 0, 0, 1)</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two claims at once, and both are load-bearing for doc 48 § 4.1's grey sources.</b>
    ///         The first is that <see cref="TextureFormat.R8" /> is uploadable at all, although
    ///         <see cref="TextureFormats.IsStorable" /> is false for it — that predicate is about what
    ///         a kernel may *write*, and a mask is read. The second is the shape a sampled read of one
    ///         has: red carries the mask and green, blue and alpha are the constants the target fills
    ///         in, so a graph that wants the mask in all three channels needs a splat and does not get
    ///         one for free.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ramp is what makes this an equality rather than a shape check.</b> Sixty-four
    ///         distinct levels across the image: an upload that read the buffer with the wrong stride
    ///         — four bytes a texel is the natural mistake, because that is what every other picture
    ///         in these suites is — comes back as a ramp four times as steep, wrapping four times
    ///         across the row, and every texel but the first disagrees.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_single_channel_mask_uploads_and_is_read_as_red_alone() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"R8 upload on {TextureKernelHarness.Adapter(device)}");

        Assert.False(TextureFormats.IsStorable(TextureFormat.R8));

        var mask = new byte[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                mask[(y * Side) + x] = (byte)(x * 4);
            }
        }

        var plan = Plan(TextureFormat.R8);

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Side, Side, mask);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(1);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(mask[(y * Side) + x], TextureKernelHarness.At(picture, x, y, 0));
                Assert.Equal(0, TextureKernelHarness.At(picture, x, y, 1));
                Assert.Equal(0, TextureKernelHarness.At(picture, x, y, 2));
                Assert.Equal(255, TextureKernelHarness.At(picture, x, y, 3));
            }
        }
    }

    /// <summary>⚠ A coverage field arrives as the level it rounds to, end to end.</summary>
    /// <remarks>
    ///     <b>The whole path a rasterised glyph or a filled path takes.</b>
    ///     <c>GlyphRasterizer.Rasterize</c> hands back exactly this — a <c>float[]</c>, row-major, row
    ///     0 at the top, each value in <c>[0, 1]</c> — so what is asserted here is that a coverage of
    ///     one half is 128 in the picture and not 127. Half a step, uniformly, on every anti-aliased
    ///     edge in a shape: it looks like a font weight rather than like arithmetic, which is why the
    ///     rounding is asserted at both ends of the seam.
    /// </remarks>
    [Fact]
    public void A_coverage_field_arrives_as_the_level_it_rounds_to() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"coverage upload on {TextureKernelHarness.Adapter(device)}");

        var coverage = new float[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                coverage[(y * Side) + x] = x / (float)(Side - 1);
            }
        }

        // The three the rounding is actually about: nothing, exactly half, and everything.
        coverage[0] = 0f;
        coverage[1] = 0.5f;
        coverage[2] = 1f;

        var plan = Plan(TextureFormat.R8);

        using var uploads = new TextureUploads(device);

        uploads.AddCoverage(plan, 0, Side, Side, coverage);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(1);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                // ⚠ Written out rather than calling `TextureUploads.Quantize`, which is what
                // produced the bytes: an assertion against the code under test is an assertion that
                // cannot fail, and this loop is 4 096 texels of exactly that shape.
                Assert.Equal(
                    (byte)((coverage[(y * Side) + x] * 255f) + 0.5f),
                    TextureKernelHarness.At(picture, x, y, 0)
                );
            }
        }

        Assert.Equal(0, TextureKernelHarness.At(picture, 0, 0, 0));
        Assert.Equal(128, TextureKernelHarness.At(picture, 1, 0, 0));
        Assert.Equal(255, TextureKernelHarness.At(picture, 2, 0, 0));
    }

    /// <summary>A picture that is not the plan's base resolution is read at its own size.</summary>
    /// <remarks>
    ///     ⚠ <b>An external image is the one place an absolute size enters a plan, and this is what
    ///     that means in practice.</b> The plan's base is 64² and the upload is 16 wide; every kernel
    ///     clamps its taps to the <em>source's</em> dimensions, so the right-hand three quarters of
    ///     the output repeat the source's last column rather than reading outside it. A kernel that
    ///     clamped to the target's dimensions instead — the recurring mistake in this folder — would
    ///     sample far outside a 16-wide texture, and what a Vulkan implementation returns there is not
    ///     the edge.
    /// </remarks>
    [Fact]
    public void An_upload_smaller_than_the_plan_is_clamped_to_its_own_edge() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"undersized upload on {TextureKernelHarness.Adapter(device)}");

        const int Narrow = 16;

        var source = TextureKernelHarness.Ramp(Narrow);
        var plan = Plan(TextureFormat.Rgba8);

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Narrow, Narrow, source);

        Assert.Equal(new Int2(Narrow, Narrow), uploads.SizeOf(0));

        // ⚠ The plan's own answer is the nominal one, and it disagrees. `TexturePlan.SizeOf` reads a
        // size off the image's level, and nothing allocates an external image, so for one it is a
        // number no picture produced.
        Assert.Equal(new Int2(Side, Side), plan.SizeOf(0));

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(1);

        // The first sixteen columns are the ramp itself; everything past them is its last column.
        for (var x = 0; x < Narrow; x++) {
            Assert.Equal(source[x * 4], TextureKernelHarness.At(picture, x, 3, 0));
        }

        for (var x = Narrow; x < Side; x++) {
            Assert.Equal(255, TextureKernelHarness.At(picture, x, 3, 0));
        }
    }
}
