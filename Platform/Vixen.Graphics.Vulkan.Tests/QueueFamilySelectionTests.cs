// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Queue-family policy against the four hardware shapes it has to get right, none of which are
///     on the machine running this.
/// </summary>
public sealed class QueueFamilySelectionTests {
    const QueueFlags Universal =
        QueueFlags.GraphicsBit | QueueFlags.ComputeBit | QueueFlags.TransferBit | QueueFlags.SparseBindingBit;

    /// <summary>Apple silicon through MoltenVK, and lavapipe: one family that does everything.</summary>
    static readonly QueueFamilyCandidate[] SingleUniversalFamily = [new(0, Universal, 1, true)];

    /// <summary>A discrete AMD card: universal, compute-only, and a transfer-only DMA engine.</summary>
    static readonly QueueFamilyCandidate[] DiscreteWithDedicatedQueues = [
        new(0, Universal, 1, true),
        new(1, QueueFlags.ComputeBit | QueueFlags.TransferBit, 4, false),
        new(2, QueueFlags.TransferBit, 1, false)
    ];

    [Fact]
    public void OneUniversalFamilyDoesEverything() {
        Assert.True(QueueFamilySelection.TryPlan(SingleUniversalFamily, true, out var plan, out var reason), reason);

        Assert.Equal(0u, plan.Graphics);
        Assert.Equal(0u, plan.Compute);
        Assert.Equal(0u, plan.Transfer);
        Assert.Equal(0u, plan.Present);
    }

    /// <summary>
    ///     The flags say the universal family can do compute and transfer too. Choosing it anyway
    ///     would be defensible and would also mean async compute never overlaps anything, so the
    ///     preference for a dedicated family is asserted rather than left to reading.
    /// </summary>
    [Fact]
    public void DedicatedFamiliesAreChosenOverTheUniversalOne() {
        Assert.True(
            QueueFamilySelection.TryPlan(DiscreteWithDedicatedQueues, true, out var plan, out var reason),
            reason
        );

        Assert.Equal(0u, plan.Graphics);
        Assert.Equal(1u, plan.Compute);
        Assert.Equal(2u, plan.Transfer);
        Assert.True(plan.HasAsyncCompute);
        Assert.True(plan.HasAsyncTransfer);
    }

    [Fact]
    public void OneFamilyMeansNeitherAsyncComputeNorAsyncTransfer() {
        Assert.True(QueueFamilySelection.TryPlan(SingleUniversalFamily, true, out var plan, out _));

        Assert.False(plan.HasAsyncCompute);
        Assert.False(plan.HasAsyncTransfer);
        Assert.False(plan.NeedsSeparatePresent);
    }

    /// <summary>
    ///     A family advertising graphics implicitly supports transfer, so a driver is free to leave
    ///     <c>TransferBit</c> off it. Asking for the bit rather than for its absence elsewhere would
    ///     then find no transfer family on hardware that has one.
    /// </summary>
    [Fact]
    public void TransferIsFoundWhenTheGraphicsFamilyOmitsTheImpliedBit() {
        QueueFamilyCandidate[] families = [
            new(0, QueueFlags.GraphicsBit | QueueFlags.ComputeBit, 1, true),
            new(1, QueueFlags.TransferBit, 2, false)
        ];

        Assert.True(QueueFamilySelection.TryPlan(families, true, out var plan, out var reason), reason);
        Assert.Equal(1u, plan.Transfer);
    }

    /// <summary>
    ///     Some drivers put present on a family that cannot draw. The plan has to notice, because a
    ///     swapchain image then has to cross families and the present path is not where to find that
    ///     out.
    /// </summary>
    [Fact]
    public void PresentFallsBackToAFamilyThatCannotDraw() {
        QueueFamilyCandidate[] families = [
            new(0, Universal, 1, false),
            new(1, QueueFlags.TransferBit, 1, true)
        ];

        Assert.True(QueueFamilySelection.TryPlan(families, true, out var plan, out var reason), reason);

        Assert.Equal(0u, plan.Graphics);
        Assert.Equal(1u, plan.Present);
        Assert.True(plan.NeedsSeparatePresent);
    }

    /// <summary>
    ///     Two graphics families, one of which presents. Picking the first would work and would cost
    ///     an ownership transfer on every presented image, forever, invisibly.
    /// </summary>
    [Fact]
    public void AGraphicsFamilyThatPresentsIsPreferredToOneThatDoesNot() {
        QueueFamilyCandidate[] families = [
            new(0, Universal, 1, false),
            new(1, Universal, 1, true)
        ];

        Assert.True(QueueFamilySelection.TryPlan(families, true, out var plan, out var reason), reason);

        Assert.Equal(1u, plan.Graphics);
        Assert.False(plan.NeedsSeparatePresent);
    }

    [Fact]
    public void OffscreenWorkDoesNotNeedAPresentFamily() {
        QueueFamilyCandidate[] families = [new(0, Universal, 1, false)];

        Assert.True(QueueFamilySelection.TryPlan(families, false, out var plan, out var reason), reason);
        Assert.Equal(0u, plan.Graphics);
    }

    [Fact]
    public void ADeviceThatCannotDrawIsRejectedWithAReason() {
        QueueFamilyCandidate[] families = [new(0, QueueFlags.ComputeBit, 1, false)];

        Assert.False(QueueFamilySelection.TryPlan(families, false, out _, out var reason));
        Assert.Contains("draw", reason);
    }

    [Fact]
    public void ADeviceThatCannotPresentIsRejectedWithAReason() {
        QueueFamilyCandidate[] families = [new(0, Universal, 1, false)];

        Assert.False(QueueFamilySelection.TryPlan(families, true, out _, out var reason));
        Assert.Contains("present", reason);
    }

    [Fact]
    public void NoFamiliesAtAllIsRejectedWithADifferentReason() {
        Assert.False(QueueFamilySelection.TryPlan([], true, out _, out var reason));
        Assert.Contains("no queue families", reason);
    }

    /// <summary>An empty family is a family that cannot be asked for a queue.</summary>
    [Fact]
    public void AFamilyWithNoQueuesIsNotChosen() {
        QueueFamilyCandidate[] families = [
            new(0, Universal, 0, true),
            new(1, Universal, 1, true)
        ];

        Assert.True(QueueFamilySelection.TryPlan(families, true, out var plan, out var reason), reason);
        Assert.Equal(1u, plan.Graphics);
    }

    /// <summary>
    ///     <c>vkCreateDevice</c> rejects a create-info that names the same family twice, so the
    ///     de-duplication is a correctness requirement rather than tidiness.
    /// </summary>
    [Fact]
    public void DistinctFamiliesNamesEachFamilyOnce() {
        Assert.True(QueueFamilySelection.TryPlan(SingleUniversalFamily, true, out var one, out _));
        Assert.Equal([0u], one.DistinctFamilies());

        Assert.True(QueueFamilySelection.TryPlan(DiscreteWithDedicatedQueues, true, out var many, out _));
        Assert.Equal([0u, 1u, 2u], many.DistinctFamilies());
    }
}
