// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     The selection policy, as a pure function — which is what lets the interesting cases be tested
///     on a machine with no Vulkan: a laptop with two GPUs, a CI runner with only lavapipe, a driver
///     reporting 1.0.
/// </summary>
public class AdapterSelectionTests {
    static AdapterCandidate Device(
        string name,
        AdapterKind kind,
        ulong memory = 0,
        uint version = (1u << 22) | (3u << 12),
        bool swapchain = true,
        bool present = true,
        bool graphics = true
    ) =>
        new(name, kind, memory, version, swapchain, present, graphics);

    [Fact]
    public void ADiscreteCardBeatsAnIntegratedOneWhateverTheirMemory() {
        AdapterCandidate[] candidates = [
            Device("Integrated", AdapterKind.Integrated, 32UL << 30),
            Device("Discrete", AdapterKind.Discrete, 8UL << 30)
        ];

        Assert.True(AdapterSelection.TrySelect(candidates, true, out var index, out _));
        Assert.Equal(1, index);
    }

    [Fact]
    public void MemoryBreaksTiesWithinAClass() {
        AdapterCandidate[] candidates = [
            Device("Small", AdapterKind.Discrete, 4UL << 30),
            Device("Large", AdapterKind.Discrete, 24UL << 30)
        ];

        Assert.True(AdapterSelection.TrySelect(candidates, true, out var index, out _));
        Assert.Equal(1, index);
    }

    /// <summary>
    ///     lavapipe is a conformant Vulkan 1.3 driver with no GPU and is what makes the whole backend
    ///     testable on a standard CI runner. A selector that filtered software devices out would make
    ///     the most valuable CI leg in the plan impossible.
    /// </summary>
    [Fact]
    public void ASoftwareDeviceIsRankedLastButNeverSkipped() {
        AdapterCandidate[] onlySoftware = [Device("llvmpipe", AdapterKind.Software)];

        Assert.True(AdapterSelection.TrySelect(onlySoftware, true, out var index, out _));
        Assert.Equal(0, index);

        AdapterCandidate[] both = [Device("llvmpipe", AdapterKind.Software), Device("Card", AdapterKind.Integrated)];

        Assert.True(AdapterSelection.TrySelect(both, true, out var chosen, out _));
        Assert.Equal(1, chosen);
    }

    [Fact]
    public void ADeviceBelowTheFloorIsRejectedBySayingWhichVersionItReported() {
        AdapterCandidate[] candidates = [Device("Ancient", AdapterKind.Discrete, version: 1u << 22)];

        Assert.False(AdapterSelection.TrySelect(candidates, true, out _, out var reason));
        Assert.Contains("1.0.0", reason, StringComparison.Ordinal);
        Assert.Contains("needs 1.1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceThatCannotPresentIsUsableWhenNothingHasToBePresented() {
        AdapterCandidate[] candidates = [Device("Offscreen", AdapterKind.Discrete, swapchain: false, present: false)];

        Assert.False(AdapterSelection.TrySelect(candidates, true, out _, out _));
        Assert.True(AdapterSelection.TrySelect(candidates, false, out var index, out _));
        Assert.Equal(0, index);
    }

    [Fact]
    public void ADeviceWithNoGraphicsQueueIsNeverUsable() {
        AdapterCandidate[] candidates = [Device("ComputeOnly", AdapterKind.Discrete, graphics: false)];

        Assert.False(AdapterSelection.TrySelect(candidates, false, out _, out var reason));
        Assert.Contains("no queue family that can draw", reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     "No suitable GPU found" is the least useful error a graphics engine can produce, and every
    ///     fact needed to do better is available at the moment it would be thrown away.
    /// </summary>
    [Fact]
    public void TheFailureNamesEveryDeviceAndWhyEachWasRejected() {
        AdapterCandidate[] candidates = [
            Device("Ancient", AdapterKind.Discrete, version: 1u << 22),
            Device("Headless", AdapterKind.Integrated, present: false)
        ];

        Assert.False(AdapterSelection.TrySelect(candidates, true, out _, out var reason));
        Assert.Contains("Ancient", reason, StringComparison.Ordinal);
        Assert.Contains("Headless", reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Enumerating nothing on macOS almost always means the portability bit was left off, and the
    ///     symptom — "no Vulkan devices found" on a machine that works — gives no hint of that.
    /// </summary>
    [Fact]
    public void EnumeratingNothingPointsAtThePortabilityFlag() {
        Assert.False(AdapterSelection.TrySelect([], true, out _, out var reason));
        Assert.Contains("portability_enumeration", reason, StringComparison.Ordinal);
        Assert.Contains("VK_ICD_FILENAMES", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionIsDescribedTheWayVulkanPacksIt() {
        Assert.Equal("1.3.280", AdapterSelection.Describe((1u << 22) | (3u << 12) | 280u));
        Assert.Equal("1.1.0", AdapterSelection.Describe(AdapterSelection.MinimumApiVersion));
    }
}
