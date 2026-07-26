// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;

namespace Vixen.Graphics.Vulkan;

/// <summary>One <c>VkQueue</c>, and the fence that says when a frame's work on it has finished.</summary>
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

    internal VulkanQueue(VulkanDevice owner, Vk api, Device device, Queue handle, uint family, int framesInFlight) {
        this.owner = owner;
        this.api = api;
        this.device = device;
        Handle = handle;
        Family = family;
        fences = new Fence[framesInFlight];

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

    /// <inheritdoc />
    public void Submit(ReadOnlySpan<ICommandList> lists) {
        if (lists.IsEmpty) {
            return;
        }

        var buffers = stackalloc CommandBuffer[lists.Length];

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

        owner.SubmitTo(this, buffers, lists.Length);
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
    }
}
