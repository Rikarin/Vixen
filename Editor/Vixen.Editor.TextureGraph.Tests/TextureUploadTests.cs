// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>What <see cref="TextureUploads" /> refuses, and which queue it fills a texture from.</summary>
/// <remarks>
///     <para>
///         <b>None of this needs an adapter, which is why it is a file of its own.</b> The device
///         suite next door proves that uploaded texels reach a kernel unchanged and skips loudly on a
///         machine with no GPU; everything here is a claim about the seam itself and holds on the
///         Null device, so it runs everywhere and on the day nobody has a card it still says
///         something.
///     </para>
///     <para>
///         ⚠ <b>The queue assertion can only be made here.</b> On MoltenVK and every other unified
///         adapter in this tree <c>GraphicsQueue</c> and <c>ComputeQueue</c> are literally the same
///         object, so the mismatch that
///         <a href="https://github.com/Rikarin/Vixen/issues/617">#617</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/679">#679</a> both were is invisible on
///         one — same kind, same object, same picture. <c>NullDevice</c> builds three distinct
///         submitters and is the only place in the tree where the choice is observable at all.
///     </para>
/// </remarks>
public class TextureUploadTests {
    const int Side = 8;

    /// <summary>A plan whose first image is external and whose second an op writes.</summary>
    static TexturePlan Plan(TextureFormat external) =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(external, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Invert",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [new("invertR", 0f), new("invertG", 0f), new("invertB", 0f), new("invertA", 0f)]
                }
            ],
            Outputs = [1]
        };

    /// <summary>⚠ The upload runs on the queue the evaluator dispatches on, and it is the compute one.</summary>
    /// <remarks>
    ///     <b>Three halves, and the first two are the instrument.</b> A queue assertion over an empty
    ///     list of recorded commands is vacuously true, so the premise — that the two queues are
    ///     distinguishable at all on this device — and the copy itself are asserted first. On the day
    ///     <see cref="TextureUploads.Add" /> stops copying anything, this says so rather than passing.
    /// </remarks>
    [Fact]
    public void An_upload_fills_its_texture_from_the_queue_the_evaluator_dispatches_on() {
        using var device = new NullDevice(new() { Record = true });

        // The premise. On any real adapter in this tree these are the same object, which is why the
        // defect this assertion exists for could not be seen on one.
        Assert.NotSame(device.GraphicsQueue, device.ComputeQueue);
        Assert.Equal(QueueKind.Compute, device.ComputeQueue.Kind);

        device.Recorder!.Clear();

        using var uploads = new TextureUploads(device);

        uploads.Add(Plan(TextureFormat.Rgba8), 0, Side, Side, TextureKernelHarness.Unique(Side));

        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.CopyBufferToTexture));

        // ⚠ The wait is what carries the answer. `ICommandSubmitter.Submit` records nothing about
        // its queue on this device — only the timeline overload does, and this does not use it —
        // but `WaitIdle` is recorded *by the queue it was called on*, so an upload that began its
        // list on one queue and waited on another is visible here.
        var waits = device.Recorder.OfKind(RecordedCommandKind.QueueWaitIdle);

        Assert.NotEmpty(waits);

        foreach (var wait in waits) {
            Assert.Equal(QueueKind.Compute, (QueueKind)wait.A);
        }
    }

    /// <summary>
    ///     The declarations come back under the image indices the evaluator looks them up by, and
    ///     each says what its texture was created with.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The usage is asserted against the constant the texture is created from</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/744">#744</a>. Writing the three bits out
    ///     here instead would make this test the second place the answer is kept, which is the
    ///     arrangement the declaration exists to remove: what has to be true is that the two readers
    ///     of <c>UploadUsage</c> are the creation and the declaration, and
    ///     <see cref="An_upload_is_created_with_everything_a_plan_may_do_to_it" /> is what pins the
    ///     bits themselves.
    /// </remarks>
    [Fact]
    public void The_externals_map_is_keyed_by_the_image_index() {
        using var device = new NullDevice();
        using var uploads = new TextureUploads(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8), new(TextureFormat.R8, External: true)],
            Ops = [new() { Kernel = "Uniform", Output = 0, Parameters = [] }],
            Outputs = [0]
        };

        var texture = uploads.AddCoverage(plan, 1, Side, Side, new float[Side * Side]);

        Assert.Equal(1, uploads.Count);
        Assert.Equal(new TextureExternal(texture, TextureUploads.UploadUsage), Assert.Contains(1, uploads.Externals));
        Assert.DoesNotContain(0, uploads.Externals);
        Assert.Equal(new Int2(Side, Side), uploads.SizeOf(1));
    }

    /// <summary>A plan whose CPU op reads an uploaded bitmap is accepted rather than refused.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/744">#744</a>, and it is a claim
    ///         about a discrete card asserted on a machine that cannot see it.</b> An upload was
    ///         created <c>Sampled | CopyDestination</c> and handed over as a bare handle, so the first
    ///         plan to read one with a <see cref="TextureOp.Cpu" /> op met
    ///         <c>TexturePlanEvaluator</c>'s refusal from the other side — the refusal being the good
    ///         outcome, and what it replaced being undefined behaviour. § 4.6's
    ///         <c>Normal → Height</c> Poisson solve is the node that gets there first.
    ///     </para>
    ///     <para>
    ///         <b>It is the whole chain rather than the constant</b>: the usage the texture was
    ///         created with, the declaration <see cref="TextureUploads.Externals" /> hands over, and
    ///         the requirement the evaluator computes from the plan. Any one of the three changing
    ///         alone turns this red — which is what a test of <c>UploadUsage</c>'s bits could not do,
    ///         because it would be the second copy of the answer.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_uploaded_bitmap_can_be_read_by_a_cpu_op() {
        using var device = new NullDevice();
        using var uploads = new TextureUploads(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Transpose", Output = 1, Inputs = [0], Cpu = new TransposeRgba8() }],
            Outputs = [1]
        };

        uploads.Add(plan, 0, Side, Side, new byte[Side * Side * 4]);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        Assert.Equal(0, bake.Dispatches);
    }

    /// <summary>⚠ And an upload is not a storage image, which is the bit that must stay off.</summary>
    /// <remarks>
    ///     An external image is never written by an op and <c>TexturePlan.Validate</c> refuses a plan
    ///     where one is, so <see cref="TextureUsage.Storage" /> here would be a bit asked for on every
    ///     bitmap an author imports and needed by none — and for several formats a conformant device
    ///     does not have to support it at all. Nothing above would notice: a usage nobody exercises is
    ///     invisible to every behavioural test there is.
    /// </remarks>
    [Fact]
    public void An_upload_is_not_created_as_a_storage_image() =>
        Assert.Equal(TextureUsage.None, TextureUploads.UploadUsage & TextureUsage.Storage);

    /// <summary>⚠ An R8 mask uploads, although no kernel can write one.</summary>
    /// <remarks>
    ///     <b>The guard that must not be there.</b> <see cref="TextureFormats.IsStorable" /> is false
    ///     for <see cref="TextureFormat.R8" /> — Raven declares no <c>r8</c> storage image and Vulkan
    ///     does not require one — and the natural mistake is to reuse that predicate here, because it
    ///     is the assembly's existing answer to "may this format be used". It is the answer to a
    ///     different question: a mask is *read*, it costs a quarter of what RGBA costs, and doc 48
    ///     § 4.1's grey sources are exactly this. Both halves are asserted, so a reader can see that
    ///     the format really is the unwritable one.
    /// </remarks>
    [Fact]
    public void A_single_channel_mask_uploads_even_though_a_kernel_cannot_write_that_format() {
        using var device = new NullDevice();
        using var uploads = new TextureUploads(device);

        Assert.False(TextureFormats.IsStorable(TextureFormat.R8));

        var texture = uploads.Add(Plan(TextureFormat.R8), 0, Side, Side, new byte[Side * Side]);

        Assert.True(texture.IsValid);
    }

    /// <summary>⚠ An upload for an image the plan does not mark external is refused.</summary>
    /// <remarks>
    ///     The evaluator consults the externals dictionary only for images the plan marks external,
    ///     so a handle filed under an internal image's index is silently ignored — and what comes out
    ///     is whatever the kernel wrote over the caller's own pixels, which is a plausible picture
    ///     nothing points at.
    /// </remarks>
    [Fact]
    public void An_upload_for_an_image_a_kernel_writes_is_refused() {
        using var device = new NullDevice();
        using var uploads = new TextureUploads(device);

        var refusal = Assert.Throws<ArgumentException>(
            () => uploads.Add(Plan(TextureFormat.Rgba8), 1, Side, Side, TextureKernelHarness.Unique(Side))
        );

        Assert.Contains("not external", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, uploads.Count);
    }

    /// <summary>A second upload for one image is refused rather than leaking the first.</summary>
    [Fact]
    public void One_image_cannot_be_uploaded_twice() {
        using var device = new NullDevice();
        using var uploads = new TextureUploads(device);
        var plan = Plan(TextureFormat.Rgba8);

        uploads.Add(plan, 0, Side, Side, TextureKernelHarness.Unique(Side));

        Assert.Throws<ArgumentException>(() => uploads.Add(plan, 0, Side, Side, TextureKernelHarness.Unique(Side)));
        Assert.Equal(1, uploads.Count);
    }

    /// <summary>⚠ A byte count that is not the size times the format's texel is refused.</summary>
    /// <remarks>
    ///     <b>The interesting case is the second one, where the count is right for a picture and
    ///     wrong for this image.</b> Four bytes a texel is what a caller has in hand after decoding
    ///     almost anything; handing that to an R8 image would copy the first quarter of it and leave
    ///     the rest of the texture undefined, which reads as a mask whose bottom three rows in four
    ///     are somebody else's memory.
    /// </remarks>
    [Theory]
    [InlineData(TextureFormat.Rgba8, Side * Side * 4 - 4)]
    [InlineData(TextureFormat.R8, Side * Side * 4)]
    [InlineData(TextureFormat.Rgba8, Side * Side)]
    public void A_buffer_that_is_not_the_picture_is_refused(TextureFormat format, int length) {
        using var device = new NullDevice();
        using var uploads = new TextureUploads(device);

        Assert.Throws<ArgumentException>(() => uploads.Add(Plan(format), 0, Side, Side, new byte[length]));
        Assert.Equal(0, uploads.Count);
    }

    /// <summary>A coverage field into anything but a single-channel image is refused.</summary>
    [Fact]
    public void A_coverage_field_into_a_colour_image_is_refused() {
        using var device = new NullDevice();
        using var uploads = new TextureUploads(device);

        Assert.Throws<ArgumentException>(
            () => uploads.AddCoverage(Plan(TextureFormat.Rgba8), 0, Side, Side, new float[Side * Side])
        );
    }

    /// <summary>⚠ Coverage is rounded to the nearest level and not truncated.</summary>
    /// <remarks>
    ///     <b>Asserted here rather than only through a device, because this is the half that skips
    ///     otherwise.</b> <c>(byte)(c * 255)</c> — the spelling a reader reaches for — gives 127 for
    ///     a half-covered texel, 254 for 0.999 and 255 only for exactly 1.0, so every value but the
    ///     last comes out a step dark. On a rasterised glyph that is a uniform thinning, which looks
    ///     like a font weight and not like an off-by-one.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 128)]
    [InlineData(1f, 255)]
    [InlineData(1f / 255f, 1)]
    [InlineData(-0.5f, 0)]
    [InlineData(2f, 255)]
    public void A_coverage_value_becomes_the_nearest_level(float coverage, int expected) =>
        Assert.Equal(expected, TextureUploads.Quantize(coverage));

    /// <summary>Disposing releases the textures, and a disposed set refuses rather than crashing.</summary>
    [Fact]
    public void A_disposed_set_holds_nothing_and_accepts_nothing() {
        using var device = new NullDevice();
        var uploads = new TextureUploads(device);
        var plan = Plan(TextureFormat.Rgba8);

        uploads.Add(plan, 0, Side, Side, TextureKernelHarness.Unique(Side));
        uploads.Dispose();

        Assert.Equal(0, uploads.Count);
        Assert.Empty(uploads.Externals);
        Assert.Throws<ObjectDisposedException>(
            () => uploads.Add(plan, 0, Side, Side, TextureKernelHarness.Unique(Side))
        );

        // Twice is a no-op rather than a second round of Destroy calls on dead handles.
        uploads.Dispose();
    }
}
