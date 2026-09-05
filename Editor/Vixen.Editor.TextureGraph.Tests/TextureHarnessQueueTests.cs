// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>Which queue the suites' shared upload helper touches an exclusive texture from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/679">#679</a>, and it is
///         <a href="https://github.com/Rikarin/Vixen/issues/617">#617</a> a second time.</b>
///         <c>TextureKernelHarness.Upload</c> recorded on <c>QueueKind.Graphics</c> and submitted to
///         <c>device.GraphicsQueue</c> while <see cref="TexturePlanEvaluator" /> dispatches on
///         <see cref="IGraphicsDevice.ComputeQueue" />. Every texture a bake reads is
///         <c>ResourceSharing.Exclusive</c>, so on an adapter with a compute family of its own —
///         a discrete AMD or NVIDIA card — reading one written from a second family without a
///         queue-family ownership transfer is undefined by specification. The validation layers say
///         nothing, because it is undefined behaviour and not invalid usage.
///     </para>
///     <para>
///         ⚠ <b>No device test can see this, and that is why the assertion is here.</b> On MoltenVK
///         and every other unified adapter in this tree <c>GraphicsQueue</c> and <c>ComputeQueue</c>
///         are literally the same object (<c>VulkanDevice</c> resolves both through one
///         <c>byFamily</c> table), so a reference comparison, a kind comparison and the picture
///         itself are all identical either way. <c>NullDevice</c> builds three distinct submitters,
///         which makes it the only place in the tree where the choice is observable at all — the same
///         reason <c>TextureQueueTests</c> asserts the bake's own queue there.
///     </para>
///     <para>
///         <b>It is a source of truth for the two suites that share the helper, not for one.</b>
///         #679's lasting half is that <c>Open</c>, <c>Adapter</c>, <c>Upload</c> and the patterns
///         exist once; every device-test file forwards to them, so there is one queue decision in the
///         project and this is the test of it.
///     </para>
/// </remarks>
public class TextureHarnessQueueTests {
    /// <summary>⚠ The upload runs on the queue the evaluator dispatches on, and it is the compute one.</summary>
    /// <remarks>
    ///     <b>Two halves, and the first is the instrument.</b> A queue assertion over an empty list of
    ///     submissions is vacuously true, so the copy is asserted first: on the day <c>Upload</c>
    ///     stops uploading, this says so rather than passing.
    /// </remarks>
    [Fact]
    public void The_shared_upload_runs_on_the_queue_the_evaluator_dispatches_on() {
        using var device = new NullDevice(new() { Record = true });

        // The premise. On any real adapter in this tree these are the same object, which is why the
        // defect this file exists for could not be seen on one.
        Assert.NotSame(device.GraphicsQueue, device.ComputeQueue);
        Assert.Equal(QueueKind.Compute, device.ComputeQueue.Kind);

        device.Recorder!.Clear();

        var (texture, staging) = TextureKernelHarness.Upload(device, TextureKernelHarness.Ramp(8), 8, 8);

        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.CopyBufferToTexture));

        // ⚠ The wait is what carries the answer. `ICommandSubmitter.Submit` records nothing about
        // its queue on this device — only the timeline overload does, and neither the harness nor
        // the evaluator uses it — but `WaitIdle` is recorded *by the queue it was called on*, so a
        // helper that had begun its list on one queue and waited on another would be visible here
        // too.
        var waits = device.Recorder.OfKind(RecordedCommandKind.QueueWaitIdle);

        Assert.NotEmpty(waits);

        foreach (var wait in waits) {
            Assert.Equal(QueueKind.Compute, (QueueKind)wait.A);
        }

        device.Destroy(staging);
        device.Destroy(texture);
    }
}
