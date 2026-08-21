// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>Submitting with a wait value, against a driver.</summary>
/// <remarks>
///     <para>
///         <b>What the fakes cannot check.</b> A timeline semaphore is a driver object with rules the
///         validation layers enforce — it has to be created from a feature that was <em>enabled</em>,
///         its signal values have to climb, and a <c>VkTimelineSemaphoreSubmitInfo</c> has to carry
///         exactly as many values as its submission has semaphores. None of that has a fake.
///     </para>
///     <para>
///         ⚠ <b>These do not demonstrate overlap, and cannot.</b> Every Vulkan device in reach —
///         MoltenVK on Apple silicon, lavapipe in CI — exposes one universal queue family, so
///         <c>ComputeQueue</c> and <c>GraphicsQueue</c> are the same <c>VkQueue</c> and a cross-queue
///         wait is satisfied by submission order whether or not the semaphore does anything. What is
///         under test here is that the primitive is real, is accepted, and orders correctly; that it
///         buys frame time needs hardware with a second queue family, which nothing here has.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class TimelineSubmitTests {
    static bool TryOpen(out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(new(), out device, out reason);

    /// <summary>The capability and the queues agree, and the capability is about a granted feature.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that used to be impossible to fail.</b> The flag was computed
    ///     from the API version alone — 1.2 or the extension — which is a claim that the structure
    ///     <em>exists</em>, not that the device grants the bit, and nothing enabled the feature at
    ///     device creation either. A device reporting true would have been refused at the first
    ///     <c>vkCreateSemaphore</c>. Asking the queue closes that: it has a timeline only if one was
    ///     actually created.
    /// </remarks>
    [Fact]
    public void TheCapabilityMeansTheQueuesHaveOne() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var claimed = owned.Features.HasTimelineSemaphores;

        Assert.Equal(claimed, owned.GraphicsQueue.HasTimeline);
        Assert.Equal(claimed, owned.ComputeQueue.HasTimeline);
        Assert.Equal(claimed, owned.TransferQueue.HasTimeline);
    }

    /// <summary>A wait-value submission is accepted, and its points climb.</summary>
    [Fact]
    public void PointsClimbWithEachSubmission() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        VulkanRequirement.Available(
            owned.Features.HasTimelineSemaphores,
            $"'{owned.Adapter.Name}' has no timeline semaphores"
        );

        var first = Submit(owned, []);
        var second = Submit(owned, [first]);

        Assert.False(first.IsNone);
        Assert.True(second.Value > first.Value);

        owned.WaitIdle();
    }

    /// <summary>A queue with no timeline says so rather than failing at the submission.</summary>
    /// <remarks>
    ///     Skipped on every device that has them, which is every device here — so it is a statement
    ///     about the shape of the refusal rather than a test that runs. It is worth having anyway:
    ///     the branch it covers is the one a GLES-class Vulkan 1.1 driver takes.
    /// </remarks>
    [Fact]
    public void AQueueWithoutOneRefusesRatherThanFailing() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        if (owned.Features.HasTimelineSemaphores) {
            Assert.True(owned.GraphicsQueue.HasTimeline);
            return;
        }

        using var list = owned.BeginCommandList(QueueKind.Graphics, "work");
        list.Finish();

        var failure = Assert.Throws<NotSupportedException>(() => owned.GraphicsQueue.Submit([list], []));
        Assert.Contains("timeline", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cross-queue wait orders a copy after the write it depends on.</summary>
    /// <remarks>
    ///     <para>
    ///         The end-to-end shape a scheduled frame has: one queue produces, another consumes, and
    ///         the consumer is submitted with the producer's point rather than after a drain. The
    ///         data landing intact is what says the submission was well-formed and the driver ran it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not evidence that the semaphore ordered anything</b>, on any device here: the two
    ///         kinds share a family, so the second submission was already ordered after the first.
    ///         Deleting the wait would leave this passing. It is a smoke test for the submission
    ///         path — validation-clean, correct result — and the ordering claim itself is asserted
    ///         where it can be, against the Null backend's three real queues.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ACopyBehindAWaitSeesTheDataWrittenBeforeIt() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        VulkanRequirement.Available(
            owned.Features.HasTimelineSemaphores,
            $"'{owned.Adapter.Name}' has no timeline semaphores"
        );

        const int Bytes = 256;

        var source = owned.CreateBuffer(new(Bytes, BufferUsage.CopySource, MemoryAccess.HostUpload, "source"));
        var middle = owned.CreateBuffer(
            new(Bytes, BufferUsage.CopySource | BufferUsage.CopyDestination, Name: "middle")
        );

        var readback = owned.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "readback")
        );

        var written = new byte[Bytes];

        for (var index = 0; index < Bytes; index++) {
            written[index] = (byte)(index * 7);
        }

        owned.Write(source, 0, written);

        // The producer, on the compute queue.
        var produced = TimelinePoint.None;

        using (var list = owned.BeginCommandList(QueueKind.Compute, "produce")) {
            list.CopyBuffer(source, 0, middle, 0, Bytes);
            list.Finish();
            produced = owned.ComputeQueue.Submit([list], []);
        }

        // The consumer, on the graphics queue, waiting for the producer's point rather than draining.
        using (var list = owned.BeginCommandList(QueueKind.Graphics, "consume")) {
            list.CopyBuffer(middle, 0, readback, 0, Bytes);
            list.Finish();
            owned.GraphicsQueue.Submit([list], [produced]);
        }

        owned.WaitIdle();

        var read = new byte[Bytes];
        owned.Read(readback, 0, read);
        Assert.Equal(written, read);

        owned.Destroy(source);
        owned.Destroy(middle);
        owned.Destroy(readback);
    }

    /// <summary>The whole wait-value path says nothing to the validation layers.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion the rest of this file depends on.</b> A malformed
    ///     <c>VkTimelineSemaphoreSubmitInfo</c> — a value array shorter than its semaphore array, a
    ///     signal below the counter's current value, a semaphore created from a feature that was
    ///     never enabled — is accepted by MoltenVK without a word and is a validation error on
    ///     lavapipe. On macOS the layer does not load unless <c>DYLD_LIBRARY_PATH</c> points at it,
    ///     which is why this skips rather than passes when it is absent.
    /// </remarks>
    [Fact]
    public void TheWaitValuePathIsValidationClean() {
        VulkanRequirement.Available(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        VulkanRequirement.Available(
            owned.ValidationEnabled,
            "the instance came up without validation, so there is nothing to assert"
        );

        VulkanRequirement.Available(
            owned.Features.HasTimelineSemaphores,
            $"'{owned.Adapter.Name}' has no timeline semaphores"
        );

        VulkanDiagnostics.Reset();

        // A chain of four, each waiting on the last, across all three kinds — which on a one-family
        // device is one queue counted three ways, and on a discrete card is three.
        var first = Submit(owned, [], QueueKind.Graphics);
        var second = Submit(owned, [first], QueueKind.Compute);
        var third = Submit(owned, [second], QueueKind.Transfer);
        Submit(owned, [second, third], QueueKind.Graphics);

        owned.WaitIdle();

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0 && VulkanDiagnostics.WarningCount == 0,
            $"The validation layers reported {VulkanDiagnostics.ErrorCount} error(s) and "
            + $"{VulkanDiagnostics.WarningCount} warning(s):"
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    static TimelinePoint Submit(
        VulkanDevice device,
        ReadOnlySpan<TimelinePoint> waitFor,
        QueueKind kind = QueueKind.Graphics
    ) {
        using var list = device.BeginCommandList(kind, $"{kind} work");
        list.Finish();

        var submitter = kind switch {
            QueueKind.Compute => device.ComputeQueue,
            QueueKind.Transfer => device.TransferQueue,
            _ => device.GraphicsQueue
        };

        return submitter.Submit([list], waitFor);
    }
}
