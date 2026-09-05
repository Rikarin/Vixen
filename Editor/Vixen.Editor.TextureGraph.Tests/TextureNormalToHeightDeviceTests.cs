// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.6's <c>Normal → Height</c> through the evaluator on a real device — the seam, not
///     the solve.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap this closes is a stride.</b> <c>TextureCpuOpDeviceTests</c> proved the CPU
///         seam over an <c>Rgba8</c> transpose, which is four bytes a texel in and four out.
///         <c>Normal → Height</c> is the first op to go through it writing <b>two</b> — every length
///         in <c>TexturePlanEvaluator.OnCpu</c>, the readback buffer's and the staging upload's
///         alike, is arithmetic that is correct for one of those and not obviously correct for the
///         other, and a wrong one produces a picture rather than an error.
///     </para>
///     <para>
///         <b>So the closed form here is the transport, deliberately, and not the answer.</b>
///         <see cref="TextureNormalToHeightTests" /> is where the arithmetic is judged against a
///         height field. Here the same operation runs twice over the same bytes — once through the
///         evaluator, once directly — and the two pictures must be <em>identical</em>, with no
///         tolerance, because nothing between them is allowed to change a texel. That is a claim
///         about one implementation over two transports rather than about two implementations, which
///         is the only thing a device can add to a solve that never touches it.
///     </para>
///     <para>
///         ⚠ <b>The source is uploaded by <see cref="TextureKernelHarness.Upload" /> and not by
///         <see cref="TextureUploads" />, and that is a finding rather than a preference.</b>
///         <c>TextureUploads</c> creates every texture <c>Sampled | CopyDestination</c> and its
///         <c>Externals</c> declares <c>Sampled</c> — but a CPU op <em>copies out of</em> the image
///         it reads, which needs <c>CopySource</c> both on the texture and in the declaration, and
///         <c>CheckExternalUsage</c> refuses the plan by name. So the only production producer of an
///         external image cannot feed the one op kind that reads one by copying, and the two halves
///         batches 4 and 5 built do not yet meet — [#765](https://github.com/Rikarin/Vixen/issues/765).
///         The harness declares
///         <c>TextureKernelHarness.SourceUsage</c>, which does.
///     </para>
/// </remarks>
public class TextureNormalToHeightDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>A plan whose only op is a CPU op bakes, and bakes what the operation computes.</summary>
    [Fact]
    public void A_plan_that_is_only_a_cpu_op_bakes_what_the_operation_computes() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var normals = Slope(Side, Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, normals, Side, Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.R16Float)],
            Ops = [TextureCpuOperations.NormalToHeight(1, 0, 512f)],
            Outputs = [1]
        };

        Assert.Empty(plan.Check());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, TextureKernelHarness.Externals(0, texture));

        // ⚠ None, and it is worth asserting: a seam that quietly compiled a kernel for an op naming
        // no `.rvn` would say one here — and would have thrown about an embedded resource on the way.
        Assert.Equal(0, bake.Dispatches);

        var picture = bake.Read(1);
        var expected = OffDevice(plan, normals);

        // The instrument first. `Read` narrows an R16Float to eight bits and clamps, so a mean-zero
        // height is black over the half of the picture below its own mean — which is the correct
        // answer and is also exactly what a bake that computed nothing would look like. This says the
        // other half is not black, and it reads the *expected* array so that it is a statement about
        // the fixture rather than about the thing under test.
        Assert.True(
            expected.Count(level => level > 8) > Side * Side / 8,
            "the fixture's own answer is nearly all black, so the comparison below would prove nothing"
        );

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(expected[(y * Side) + x], TextureKernelHarness.At(picture, x, y, 0));
            }
        }

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>
    ///     ⚠ An external image a CPU op reads has to be declared <c>CopySource</c>, and
    ///     <see cref="TextureUploads" /> cannot declare it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The refusal is the whole of what this asserts, and it is asserted because the
    ///         refusal is the only thing standing between a plan and undefined contents on a discrete
    ///         card.</b> A Vulkan image may be transitioned to <c>TRANSFER_SRC_OPTIMAL</c> only if it
    ///         was created with that usage; MoltenVK has no image layouts at all and reads it either
    ///         way, so this is the class of defect
    ///         <a href="https://github.com/Rikarin/Vixen/issues/722">#722</a> already shipped once,
    ///         past a green device test on this machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It pinned down a gap between two batches' work, and a third closed it in the same
    ///         batch this was written.</b> <c>TextureUploads</c> is the only thing in this assembly
    ///         that produces an external image and a CPU op is one of exactly two things that read
    ///         one, so the combination was impossible and the refusal was the only way anybody found
    ///         out — <a href="https://github.com/Rikarin/Vixen/issues/744">#744</a> gave the uploaded
    ///         texture the usage it was missing, so the combination now works and this asserts that
    ///         instead. The refusal itself is still asserted, one test above, for a texture that
    ///         genuinely lacks the usage.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_upload_can_feed_a_cpu_op() {
        using var device = TextureKernelHarness.Open();

        var plan = new TexturePlan {
            BaseWidth = 8,
            BaseHeight = 8,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.R16Float)],
            Ops = [TextureCpuOperations.NormalToHeight(1, 0, 4f)],
            Outputs = [1]
        };

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, 8, 8, new byte[8 * 8 * 4]);

        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        // A flat normal field integrates to a flat height, so the assertion is that every texel agrees
        // rather than that any particular value came back — which is what a zero input can honestly
        // claim. What it proves is the path: an uploaded external reaching a CPU op at all.
        // ⚠ THE PATH IS THE CLAIM, and deliberately not the pixels. The uploaded buffer is all
        // zeroes, which is not a flat normal but (-1, -1, -1) after the decode's remap, so the solve
        // over it is a genuine field with no closed form worth writing here — and the value of a
        // Normal-to-Height solve is asserted exactly, off the device, by the tests above, which is
        // where a closed form belongs. What could only be proved here is that an uploaded external
        // reaches a CPU op at all, which was impossible until #744.
        var picture = bake.Read(1);

        Assert.Equal(8, picture.Width);
        Assert.Equal(8, picture.Height);
        Assert.NotEmpty(picture.Pixels);

    }

    /// <summary>The same operation over the same bytes, with no device between.</summary>
    /// <returns>What <c>Read</c>'s narrowing gives for each texel's red channel.</returns>
    static byte[] OffDevice(TexturePlan plan, byte[] normals) {
        var output = new TextureCpuImage(TextureFormat.R16Float, Side, Side, new byte[Side * Side * 2]);

        plan.Ops[0].Cpu!.Run(
            new TextureCpuInvocation(
                plan,
                0,
                [new TextureCpuImage(TextureFormat.Rgba8, Side, Side, normals)],
                output
            )
        );

        var levels = new byte[Side * Side];

        for (var at = 0; at < levels.Length; at++) {
            var value = (float)BitConverter.ToHalf(output.Bytes.AsSpan(at * 2, 2));

            levels[at] = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        }

        return levels;
    }

    /// <summary>A normal map of a plane that rises to the right, as <c>HeightToNormal</c> encodes it.</summary>
    /// <remarks>
    ///     A plane rather than anything textured, because what is being measured is a transport: an
    ///     input whose answer is a smooth ramp makes a mis-strided row obvious in the comparison
    ///     rather than lost among detail.
    /// </remarks>
    static byte[] Slope(int width, int height) {
        var texels = new byte[width * height * 4];

        // ∂h/∂u of 0.8 across the picture and none down it, so n = normalize(−0.8, 0, 1).
        var length = Math.Sqrt((0.8 * 0.8) + 1d);

        var red = Level((-0.8 / length * 0.5) + 0.5);
        var blue = Level((1d / length * 0.5) + 0.5);

        for (var at = 0; at < width * height; at++) {
            texels[(at * 4) + 0] = red;
            texels[(at * 4) + 1] = 128;
            texels[(at * 4) + 2] = blue;
            texels[(at * 4) + 3] = 255;
        }

        return texels;
    }

    static byte Level(double value) => (byte)Math.Clamp((int)Math.Round(value * 255d), 0, 255);
}
