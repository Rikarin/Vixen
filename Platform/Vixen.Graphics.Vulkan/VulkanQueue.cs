// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Vixen.Graphics.Vulkan;

/// <summary>One <c>VkQueue</c>, the fence that says when a frame's work on it has finished, and its counter.</summary>
/// <remarks>
///     <para>
///         There is one of these per <em>distinct</em> queue family, and the device's three
///         submitters may all point at the same one — which is what happens on Apple silicon and on
///         lavapipe. Sharing the object rather than duplicating it is what keeps the fence accounting
///         honest: two submitters signalling the same fence in one frame would be a fence signalled
///         twice without an intervening reset, which is invalid and which validation reports as a
///         confusing complaint about the *wait*.
///     </para>
///     <para>
///         A fence per frame slot rather than one per queue. The point of frames in flight is that
///         the CPU may run ahead, and a single fence would mean waiting for the work just submitted
///         rather than for the work submitted a frame ago.
///     </para>
/// </remarks>
sealed unsafe class VulkanQueue : ICommandSubmitter {
    readonly Vk api;
    readonly Device device;
    readonly Fence[] fences;
    readonly VulkanDevice owner;

    internal VulkanQueue(
        VulkanDevice owner,
        Vk api,
        Device device,
        Queue handle,
        uint family,
        int framesInFlight,
        bool timelines
    ) {
        this.owner = owner;
        this.api = api;
        this.device = device;
        Handle = handle;
        Family = family;
        fences = new Fence[framesInFlight];

        if (timelines) {
            // ⚠ One counter per queue *object*, which is per distinct family — so on a device whose
            // three kinds share a family there is one counter, and a compute point and a graphics
            // point are two values on it. That is what makes the cross-queue wait collapse to
            // "already ordered" rather than to a second synchronisation primitive doing nothing.
            //
            // A device-wide counter would be the bug: submissions on different queues finish in an
            // order nobody controls, and a timeline semaphore signalled with a value below its
            // current one is invalid usage.
            var type = new SemaphoreTypeCreateInfo {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
                InitialValue = 0
            };

            var info = new SemaphoreCreateInfo {
                SType = StructureType.SemaphoreCreateInfo,
                PNext = &type
            };

            VkSemaphore created;
            VulkanDevice.Check(api.CreateSemaphore(device, &info, null, &created), "vkCreateSemaphore");
            Timeline = created;
        }

        for (var index = 0; index < framesInFlight; index++) {
            // Created signalled, so the first frame does not wait for work that was never submitted.
            var info = new FenceCreateInfo {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit
            };

            fixed (Fence* target = &fences[index]) {
                api.CreateFence(device, &info, null, target);
            }
        }
    }

    /// <inheritdoc />
    public QueueKind Kind { get; internal set; }

    internal Queue Handle { get; }

    internal uint Family { get; }

    /// <summary>Its counter, or the null handle where the device has no timeline semaphores.</summary>
    internal VkSemaphore Timeline { get; }

    /// <summary>How many values have been handed out on <see cref="Timeline" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Read and advanced under the device's submission lock, never here.</b> The value a
    ///     submission signals has to be allocated in the same critical section that calls
    ///     <c>vkQueueSubmit</c>: two threads that took their values first and submitted afterwards
    ///     could submit them in the other order, which signals the counter backwards.
    /// </remarks>
    internal ulong Issued { get; set; }

    /// <inheritdoc />
    public bool HasTimeline => Timeline.Handle != 0;

    /// <inheritdoc />
    public TimelinePoint Submit(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<TimelinePoint> waitFor) {
        if (!HasTimeline) {
            throw new NotSupportedException(
                $"The {Kind} queue cannot submit with a wait value: '{owner.Adapter.Name}' reports no "
                + "timeline semaphores. Check HasTimeline before calling."
            );
        }

        if (lists.IsEmpty) {
            // Nothing to wait for and nothing to signal. Returning None rather than a value is what
            // keeps a caller from waiting on work that was never submitted — a device-side hang with
            // no message of any kind.
            return TimelinePoint.None;
        }

        var buffers = stackalloc CommandBuffer[lists.Length];
        Collect(lists, buffers);
        return owner.SubmitTo(this, buffers, lists.Length, waitFor, produce: true);
    }

    /// <inheritdoc />
    public void Submit(ReadOnlySpan<ICommandList> lists) {
        if (lists.IsEmpty) {
            return;
        }

        var buffers = stackalloc CommandBuffer[lists.Length];
        Collect(lists, buffers);
        owner.SubmitTo(this, buffers, lists.Length, []);
    }

    /// <summary>Checks every list belongs here and is recorded, and collects their buffers.</summary>
    void Collect(ReadOnlySpan<ICommandList> lists, CommandBuffer* buffers) {
        for (var index = 0; index < lists.Length; index++) {
            if (lists[index] is not VulkanCommandList list) {
                throw new ArgumentException(
                    $"A {lists[index].GetType().Name} was submitted to the Vulkan backend.",
                    nameof(lists)
                );
            }

            if (!list.IsRecorded) {
                throw new InvalidOperationException(
                    $"Command list '{list.Name}' was submitted without Finish() having been called, so "
                    + "the driver would read a buffer that is still being written."
                );
            }

            buffers[index] = list.Buffer;
        }
    }

    /// <inheritdoc />
    public void WaitIdle() => api.QueueWaitIdle(Handle);

    internal Fence FenceFor(int frame) => fences[frame];

    /// <summary>Waits for the work submitted in a frame slot, then makes the fence reusable.</summary>
    internal void WaitAndReset(int frame) {
        fixed (Fence* fence = &fences[frame]) {
            api.WaitForFences(device, 1, fence, true, ulong.MaxValue);
            api.ResetFences(device, 1, fence);
        }
    }

    internal void Destroy() {
        foreach (var fence in fences) {
            api.DestroyFence(device, fence, null);
        }

        if (HasTimeline) {
            api.DestroySemaphore(device, Timeline, null);
        }
    }
}
