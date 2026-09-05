// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.6's exception to § D3 on a real device: an op in the middle of a plan that is not a
///     dispatch.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/688">#688</a>'s seam, and what it has
///         to survive is the two seams either side of it.</b> A <see cref="TextureOp.Cpu" /> op ends
///         the command list in flight, waits, copies its inputs into host memory, runs, copies the
///         answer back and opens a new one — so there are four places to get a barrier wrong and each
///         of them produces a picture. The chain here is <c>invert → transpose → invert</c>, whose
///         closed form is the transpose of the source, exactly, in every channel of every texel.
///     </para>
///     <para>
///         ⚠ <b>Every wrong wiring this can have produces a <em>different</em> exact answer, which is
///         what makes the equality worth asserting.</b> A CPU op reading the plan's external image
///         instead of the dispatch before it gives <c>255 − transpose(s)</c>; one whose upload never
///         landed gives the invert of whatever was in the pooled texture; one whose output went to
///         the wrong pool slot gives the invert of the first dispatch's own output. None of them is
///         near the right answer and none of them is black, so none would survive a "the picture is
///         not empty" assertion either.
///     </para>
///     <para>
///         ⚠ <b>The transpose is not a node and doc 48 § D3 is not being bent.</b> Nothing in
///         <c>Shaders/</c> transposes, so this is not a CPU twin of a kernel — see
///         <see cref="TransposeRgba8" />. What § 4.6 puts here is <c>Normal → Height</c>, a Poisson
///         solve, and a later slice writes it.
///     </para>
/// </remarks>
public class TextureCpuOpDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    static TextureOp Invert(int target, int source) =>
        new() {
            Kernel = "Invert",
            Output = target,
            Inputs = [source],
            Parameters = [new("invertR", 1f), new("invertG", 1f), new("invertB", 1f), new("invertA", 1f)]
        };

    static TextureOp Transpose(int target, int source) =>
        new() { Kernel = "Transpose", Output = target, Inputs = [source], Cpu = new TransposeRgba8() };

    /// <summary>
    ///     ⚠ A CPU op reads what the dispatch before it wrote, and the dispatch after it reads what
    ///     the CPU op wrote.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both halves of the round trip in one equality</b>, because inverting either side of
    ///         a transpose is the transpose again: <c>1 − (1 − s[y, x])</c> is <c>s[y, x]</c>
    ///         exactly, with no tolerance, in eight-bit as in floating point.
    ///     </para>
    ///     <para>
    ///         <b>The pool is part of the claim.</b> Only image 3 is an output, so image 1 dies the
    ///         moment the CPU op has read it and image 3 is written into the very texture image 1
    ///         used — two allocations for three ops. An upload that went to a slot by op index rather
    ///         than by the schedule would be read back as the answer here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_cpu_op_round_trips_between_two_dispatches() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [Invert(1, 0), Transpose(2, 1), Invert(3, 2)],
            Outputs = [3]
        };

        Assert.Empty(plan.Check());
        Assert.Equal(2, TexturePoolSchedule.For(plan).Allocations);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

        // Two, not three: the op in the middle is not a dispatch, and a seam that quietly compiled a
        // kernel for it would say three here.
        Assert.Equal(2, bake.Dispatches);

        var picture = bake.Read(3);
        var expected = AsPicture(source);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                for (var channel = 0; channel < 4; channel++) {
                    Assert.Equal(
                        TextureKernelHarness.At(expected, y, x, channel),
                        TextureKernelHarness.At(picture, x, y, channel)
                    );
                }
            }
        }

        device.Destroy(staging);
        device.Destroy(texture);
    }

    /// <summary>
    ///     A CPU op may be the first op in a plan and read the caller's own image, and a later
    ///     dispatch still reads that image correctly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Nothing tracks an external image's state.</b> The plan's contract is that the
    ///         caller's textures arrive in <see cref="ResourceState.ShaderRead" /> and the evaluator
    ///         never moves them — so a CPU op, which has to see one as
    ///         <see cref="ResourceState.CopySource" /> to copy it, is the one place that contract can
    ///         be broken. Op 2 samples image 0 <em>after</em> the CPU op has copied it, which is what
    ///         makes the second picture here worth reading.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured, not assumed: the restore barrier itself is not witnessed by these two
    ///         pictures on this adapter.</b> Deleting it leaves both assertions green on an Apple
    ///         M1 Max — a unified-memory adapter reads an image left in a transfer layout perfectly
    ///         well — so this test's name claims the pictures and not the layout. The only witness
    ///         for a layout is the validation layers, and this assembly cannot use them:
    ///         <c>VulkanDiagnostics</c> is process-wide and every device class here opens its own
    ///         device in parallel, so a message would be attributed to whichever test was running.
    ///         <c>Platform/Vixen.Graphics.Vulkan.Tests/ValidationCleanTests.cs</c> is the shape that
    ///         works and it is serialised into a collection to get there.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_cpu_op_can_read_the_callers_own_image_and_a_later_dispatch_still_reads_it() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            // The CPU op is first, so it reads the caller's texture rather than a pooled one; op 2
            // reads that same texture again afterwards.
            Ops = [Transpose(1, 0), Invert(2, 1), Invert(3, 0)],
            Outputs = [2, 3]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

        Assert.Equal(2, bake.Dispatches);

        var transposed = bake.Read(2);
        var straight = bake.Read(3);
        var expected = AsPicture(source);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                Assert.Equal(
                    255 - TextureKernelHarness.At(expected, y, x, 0),
                    TextureKernelHarness.At(transposed, x, y, 0)
                );

                Assert.Equal(
                    255 - TextureKernelHarness.At(expected, x, y, 0),
                    TextureKernelHarness.At(straight, x, y, 0)
                );
            }
        }

        device.Destroy(staging);
        device.Destroy(texture);
    }

    static Bitmap AsPicture(byte[] pixels) => new(Side, Side, pixels);
}
