// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>What the backend does with a barrier that names two queues.</summary>
/// <remarks>
///     ⚠ <b>Almost every machine this runs on has one universal queue family</b> — MoltenVK exposes
///     one, lavapipe exposes one — so the interesting assertion here is the <em>collapse</em>: two
///     different <see cref="QueueKind" />s that land on the same family must record
///     <c>VK_QUEUE_FAMILY_IGNORED</c> rather than the same index twice, because that is what makes an
///     async-scheduled frame identical to an unscheduled one on the hardware the engine is developed
///     on. The discrete-card case is covered by <c>QueueFamilySelectionTests</c>, which plans against
///     families no machine here has.
/// </remarks>
[Collection("Vulkan")]
public sealed class QueueOwnershipTests {
    static bool TryOpen(out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(new(), out device, out reason);

    /// <summary>Two kinds on one family are not an ownership transfer at all.</summary>
    [Fact]
    public void TwoKindsOnOneFamilyCollapseToIgnored() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var (source, destination) = owned.FamiliesFor(QueueKind.Graphics, QueueKind.Compute);

        if (owned.Features.HasAsyncCompute) {
            Assert.NotEqual(source, destination);
            Assert.NotEqual(Vk.QueueFamilyIgnored, source);
            return;
        }

        Assert.Equal(Vk.QueueFamilyIgnored, source);
        Assert.Equal(Vk.QueueFamilyIgnored, destination);
    }

    /// <summary>A queue never transfers to itself, whatever the device looks like.</summary>
    [Fact]
    public void OneKindIsNeverATransfer() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        foreach (var kind in Enum.GetValues<QueueKind>()) {
            var (source, destination) = owned.FamiliesFor(kind, kind);

            Assert.Equal(Vk.QueueFamilyIgnored, source);
            Assert.Equal(Vk.QueueFamilyIgnored, destination);
        }
    }

    /// <summary>A list at neither end of a transfer records neither half, and is refused.</summary>
    /// <remarks>
    ///     Checked before the family collapse, so the diagnostic is the same on a device where the two
    ///     kinds share a family as on one where they do not — otherwise the mistake is invisible on
    ///     every machine anybody develops on and appears on the card in CI.
    /// </remarks>
    [Fact]
    public void AThirdQueueIsRefusedEvenWhereTheFamiliesAreOne() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(new(256, BufferUsage.Storage, Name: "shared"));
        using var list = owned.BeginCommandList(QueueKind.Transfer, "bystander");

        BufferBarrier[] barriers = [
            new(buffer, ResourceState.ShaderWrite, ResourceState.ShaderRead, QueueKind.Graphics, QueueKind.Compute)
        ];

        var failure = Assert.Throws<InvalidOperationException>(() => {
            list.Barrier(new(barriers, []));
        });

        Assert.Contains("neither end", failure.Message, StringComparison.Ordinal);
        owned.Destroy(buffer);
    }

    /// <summary>Both halves of a transfer are accepted on the lists they belong on.</summary>
    [Fact]
    public void BothHalvesAreAcceptedOnTheirOwnQueues() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(new(256, BufferUsage.Storage, Name: "shared"));

        BufferBarrier[] barriers = [
            new(buffer, ResourceState.ShaderWrite, ResourceState.ShaderRead, QueueKind.Graphics, QueueKind.Compute)
        ];

        using (var release = owned.BeginCommandList(QueueKind.Graphics, "release")) {
            release.Barrier(new(barriers, []));
            release.Finish();
        }

        using (var acquire = owned.BeginCommandList(QueueKind.Compute, "acquire")) {
            acquire.Barrier(new(barriers, []));
            acquire.Finish();
        }

        owned.Destroy(buffer);
    }

    /// <summary>
    ///     Two lists opened on one thread for two different queues get pools from their own families.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The bug this replaces was invisible on every device the engine has run on.</b> Command
    ///     pools were keyed by thread and frame, so the first list a thread opened in a frame decided
    ///     the family for every later one — and a buffer allocated from a graphics family's pool and
    ///     submitted to a compute queue is undefined behaviour, not a validation error. On a device
    ///     with one universal family the two are the same pool and nothing is wrong, which is why it
    ///     survived: it can only be a bug on hardware that has the second queue in the first place.
    /// </remarks>
    [Fact]
    public void ListsForDifferentQueuesComeFromDifferentPools() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        using var graphics = owned.BeginCommandList(QueueKind.Graphics, "graphics");
        using var compute = owned.BeginCommandList(QueueKind.Compute, "compute");

        graphics.Finish();
        compute.Finish();

        // Submitting each to its own queue is the check: a buffer from the wrong family's pool is
        // what the driver would reject, and there is no way to ask which pool a buffer came from.
        owned.GraphicsQueue.Submit([graphics]);
        owned.ComputeQueue.Submit([compute]);
        owned.WaitIdle();
    }
}
