// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Null;
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
    ///     <see cref="TextureBake.Read" /> uses that same submitter rather than a second one — which
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

        device.Destroy(source);
    }
}
