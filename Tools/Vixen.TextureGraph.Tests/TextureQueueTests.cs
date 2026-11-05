// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>Which queue a bake touches its textures from, asserted where the queues are distinguishable.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>On the Null device deliberately, and it is the only file in this suite that is.</b>
///         <c>NullDevice</c> builds three distinct submitters — "unlike every real device the tree
///         runs on", in its own words. MoltenVK, and every unified adapter, collapses compute onto the
///         graphics family, so <a href="https://github.com/Rikarin/Vixen/issues/617">#617</a> —
///         dispatching on the compute queue and copying the result back on the graphics queue, on
///         <c>ResourceSharing.Exclusive</c> images, with no ownership transfer — produced a correct
///         picture on every machine this engine has been developed on and undefined texels on a
///         discrete card. A device test cannot see that here; the Null device can, because there the
///         two queues are two objects.
///     </para>
///     <para>
///         <b>Nothing here looks at a pixel, and that is the point.</b> This suite's rule is that a
///         texture-graph assertion on the Null device would be proving that a black image equals a
///         black image. What is asserted is a queue, which is the one thing the Null device knows more
///         about than the real one.
///     </para>
/// </remarks>
public class TextureQueueTests {
    /// <summary>A plan of one identity levels curve over an image the caller supplies.</summary>
    static TexturePlan Plan() =>
        new() {
            BaseWidth = 16,
            BaseHeight = 16,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Levels",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [
                        new("inputBlack", 0f),
                        new("inputWhite", 1f),
                        new("gamma", 1f),
                        new("outputBlack", 0f),
                        new("outputWhite", 1f),
                        new("dither", 0f)
                    ]
                }
            ],
            Outputs = [1]
        };

    static TextureHandle Source(NullDevice device) =>
        device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: "source")
        );

    /// <summary>
    ///     ⚠ The read-back submits to the queue the dispatches ran on, and it is the compute queue.
    /// </summary>
    /// <remarks>
    ///     <b>Two halves, and the second is the one that failed.</b> The first says the evaluation
    ///     runs on <see cref="IGraphicsDevice.ComputeQueue" />; the second says
    ///     <see cref="TextureBake.Read(int)" /> uses that same submitter rather than a second one — which
    ///     it can only do because <see cref="TextureBake" /> is handed the submitter and takes both
    ///     its command-list kind and its submission from it. Handing the bake
    ///     <c>device.GraphicsQueue</c>, or restoring the <c>QueueKind.Graphics</c> literal the
    ///     read-back used to carry, turns this red.
    /// </remarks>
    [Fact]
    public void A_bake_and_its_read_back_are_one_queue_and_it_is_the_compute_one() {
        using var device = new NullDevice(new() { Record = true });

        // The premise. On any real adapter in this tree these are the same object, which is why the
        // defect this file exists for could not be seen on one.
        Assert.NotSame(device.GraphicsQueue, device.ComputeQueue);

        var source = Source(device);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(Plan(), new Dictionary<int, TextureHandle> { [0] = source });

        Assert.Same(device.ComputeQueue, bake.Queue);
        Assert.Equal(QueueKind.Compute, bake.Queue.Kind);

        var before = device.Recorder!.CountOf(RecordedCommandKind.CopyTextureToBuffer);

        bake.Read(1);

        Assert.Equal(before + 1, device.Recorder.CountOf(RecordedCommandKind.CopyTextureToBuffer));
        Assert.Same(device.ComputeQueue, bake.Queue);

        // ⚠ And from the stream, not from the field the type under test was made to expose. Until
        // the plain `Submit` recorded which queue it went to (#633) this half was unassertable, so
        // the file could only ask the bake what it thought its queue was — and a bake that
        // dispatched on compute and copied back on graphics, which is undefined on a discrete card
        // and correct on every adapter this engine is developed on (#617), satisfies both
        // `Assert.Same` above.
        var submissions = device.Recorder.OfKind(RecordedCommandKind.Submit);

        // The count first: `Assert.All` over an empty list asserts nothing at all.
        Assert.NotEmpty(submissions);
        Assert.All(submissions, submission => Assert.Equal((long)QueueKind.Compute, submission.A));

        device.Destroy(source);
    }

    /// <summary>
    ///     A plan with three outputs: <c>0 → 1</c>, <c>1 → 2</c> and <c>1 → 3</c>, the last at half size.
    /// </summary>
    static TexturePlan ThreeOutputs() {
        var op = Plan().Ops[0];

        return new() {
            BaseWidth = 16,
            BaseHeight = 16,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.R16Float),
                new(TextureFormat.Rgba16Float, LevelOffset: 1)
            ],
            Ops = [
                op,
                op with { Output = 2, Inputs = [1] },
                new() {
                    Kernel = "Resample",
                    Output = 3,
                    Inputs = [1],
                    ReadsOtherExtents = true,
                    Parameters = [new("filter", 2f)]
                }
            ],
            Outputs = [1, 2, 3]
        };
    }

    /// <summary>
    ///     ⚠ Many images are one submission: that the device is waited for once is the whole of what
    ///     the batch is for, and a loop over <see cref="TextureBake.Read(int)" /> fails the count.
    /// </summary>
    [Fact]
    public void Reading_several_images_is_one_submission_with_a_copy_for_each() {
        using var device = new NullDevice(new() { Record = true });
        var source = Source(device);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(ThreeOutputs(), new Dictionary<int, TextureHandle> { [0] = source });

        var submissions = device.Recorder!.CountOf(RecordedCommandKind.Submit);
        var copies = device.Recorder.CountOf(RecordedCommandKind.CopyTextureToBuffer);

        var pictures = bake.Read([3, 1, 2, 1]);

        Assert.Equal(submissions + 1, device.Recorder.CountOf(RecordedCommandKind.Submit));

        // The repeat costs no copy, and still gets its answer.
        Assert.Equal(copies + 3, device.Recorder.CountOf(RecordedCommandKind.CopyTextureToBuffer));
        Assert.Equal([8, 16, 16, 16], pictures.Select(static picture => picture.Width));

        device.Destroy(source);
    }

    /// <summary>A refused image in a batch refuses the batch, before anything is on the device.</summary>
    [Fact]
    public void A_batch_with_an_image_the_bake_does_not_hold_copies_nothing() {
        using var device = new NullDevice(new() { Record = true });
        var source = Source(device);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(ThreeOutputs(), new Dictionary<int, TextureHandle> { [0] = source });

        var submissions = device.Recorder!.CountOf(RecordedCommandKind.Submit);

        Assert.Throws<ArgumentException>(() => bake.Read([1, 0]));
        Assert.Equal(submissions, device.Recorder.CountOf(RecordedCommandKind.Submit));
        Assert.Empty(bake.Read([]));

        device.Destroy(source);
    }
}
