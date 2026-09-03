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
        bool graphics = true,
        string driver = "1.0.0"
    ) =>
        new(name, kind, memory, version, swapchain, present, graphics, driver);

    [Fact]
    public void ADiscreteCardBeatsAnIntegratedOneWhateverTheirMemory() {
        AdapterCandidate[] candidates = [
            Device("Integrated", AdapterKind.Integrated, 32UL << 30),
            Device("Discrete", AdapterKind.Discrete, 8UL << 30)
        ];

        Assert.True(AdapterSelection.TrySelect(candidates, true, GpuDenyList.Empty, out var index, out _));
        Assert.Equal(1, index);
    }

    [Fact]
    public void MemoryBreaksTiesWithinAClass() {
        AdapterCandidate[] candidates = [
            Device("Small", AdapterKind.Discrete, 4UL << 30),
            Device("Large", AdapterKind.Discrete, 24UL << 30)
        ];

        Assert.True(AdapterSelection.TrySelect(candidates, true, GpuDenyList.Empty, out var index, out _));
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

        Assert.True(AdapterSelection.TrySelect(onlySoftware, true, GpuDenyList.Empty, out var index, out _));
        Assert.Equal(0, index);

        AdapterCandidate[] both = [Device("llvmpipe", AdapterKind.Software), Device("Card", AdapterKind.Integrated)];

        Assert.True(AdapterSelection.TrySelect(both, true, GpuDenyList.Empty, out var chosen, out _));
        Assert.Equal(1, chosen);
    }

    [Fact]
    public void ADeviceBelowTheFloorIsRejectedBySayingWhichVersionItReported() {
        AdapterCandidate[] candidates = [Device("Ancient", AdapterKind.Discrete, version: 1u << 22)];

        Assert.False(AdapterSelection.TrySelect(candidates, true, GpuDenyList.Empty, out _, out var reason));
        Assert.Contains("1.0.0", reason, StringComparison.Ordinal);
        Assert.Contains("needs 1.1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceThatCannotPresentIsUsableWhenNothingHasToBePresented() {
        AdapterCandidate[] candidates = [Device("Offscreen", AdapterKind.Discrete, swapchain: false, present: false)];

        Assert.False(AdapterSelection.TrySelect(candidates, true, GpuDenyList.Empty, out _, out _));
        Assert.True(AdapterSelection.TrySelect(candidates, false, GpuDenyList.Empty, out var index, out _));
        Assert.Equal(0, index);
    }

    [Fact]
    public void ADeviceWithNoGraphicsQueueIsNeverUsable() {
        AdapterCandidate[] candidates = [Device("ComputeOnly", AdapterKind.Discrete, graphics: false)];

        Assert.False(AdapterSelection.TrySelect(candidates, false, GpuDenyList.Empty, out _, out var reason));
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

        Assert.False(AdapterSelection.TrySelect(candidates, true, GpuDenyList.Empty, out _, out var reason));
        Assert.Contains("Ancient", reason, StringComparison.Ordinal);
        Assert.Contains("Headless", reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Enumerating nothing on macOS almost always means the portability bit was left off, and the
    ///     symptom — "no Vulkan devices found" on a machine that works — gives no hint of that.
    /// </summary>
    [Fact]
    public void EnumeratingNothingPointsAtThePortabilityFlag() {
        Assert.False(AdapterSelection.TrySelect([], true, GpuDenyList.Empty, out _, out var reason));
        Assert.Contains("portability_enumeration", reason, StringComparison.Ordinal);
        Assert.Contains("VK_ICD_FILENAMES", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionIsDescribedTheWayVulkanPacksIt() {
        Assert.Equal("1.3.280", AdapterSelection.Describe((1u << 22) | (3u << 12) | 280u));
        Assert.Equal("1.1.0", AdapterSelection.Describe(AdapterSelection.MinimumApiVersion));
    }

    /// <summary>A denied device is refused, and the whole machine falls through with it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the deny-list being <i>reached</i> rather than merely existing.</b> Doc 10 §
    ///     Android asks for a curated list keyed on GPU and driver version because a phone can
    ///     report Vulkan, answer every capability query correctly, and fail on one extension in one
    ///     driver branch — so the refusal has to happen here, between enumeration and device
    ///     creation, which is the last moment it is still a choice.
    /// </remarks>
    [Fact]
    public void ADeniedDeviceIsRefusedAndSaysWhy() {
        AdapterCandidate[] candidates = [Device("Mali-G78 MC14", AdapterKind.Integrated, driver: "38.1.0")];
        var denied = new GpuDenyList([new("Mali-G78", GpuDenyList.Any, "dynamic rendering is advertised and absent")]);

        Assert.False(AdapterSelection.TrySelect(candidates, true, denied, out _, out var reason));
        Assert.Contains("deny-list", reason, StringComparison.Ordinal);
        Assert.Contains("dynamic rendering is advertised and absent", reason, StringComparison.Ordinal);
    }

    /// <summary>And a second, undenied GPU in the same machine is chosen instead.</summary>
    [Fact]
    public void AnUndeniedDeviceInTheSameMachineStillWins() {
        AdapterCandidate[] candidates = [
            Device("Denied", AdapterKind.Discrete, 24UL << 30, driver: "1.0.0"),
            Device("Fine", AdapterKind.Integrated, 8UL << 30, driver: "1.0.0")
        ];

        var denied = new GpuDenyList([new("Denied", GpuDenyList.Any, "known broken")]);

        Assert.True(AdapterSelection.TrySelect(candidates, true, denied, out var index, out _));
        Assert.Equal(1, index);
    }

    /// <summary>The deny-list is asked before the capability floor, so the reason names the cause.</summary>
    /// <remarks>
    ///     ⚠ A device denied <em>and</em> below the floor would otherwise be reported as "reports
    ///     Vulkan 1.0", which sends the reader after a driver update that will not help.
    /// </remarks>
    [Fact]
    public void TheDenyListIsAskedBeforeTheVersionFloor() {
        AdapterCandidate[] candidates = [
            Device("Ancient", AdapterKind.Discrete, version: 1u << 22, driver: "0.1")
        ];

        var denied = new GpuDenyList([new("Ancient", GpuDenyList.Any, "on the list as well")]);

        Assert.False(AdapterSelection.TrySelect(candidates, true, denied, out _, out var reason));
        Assert.Contains("on the list as well", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Vixen needs 1.1", reason, StringComparison.Ordinal);
    }

    /// <summary>The driver version reaches the policy, which is the only thing that reads it.</summary>
    [Fact]
    public void ARuleNamingADriverVersionSparesTheOtherVersions() {
        var denied = new GpuDenyList([new("Adreno", "512.502", "crashes on rotation")]);

        Assert.False(
            AdapterSelection.TrySelect(
                [Device("Adreno (TM) 640", AdapterKind.Integrated, driver: "512.502")],
                true,
                denied,
                out _,
                out _
            )
        );

        Assert.True(
            AdapterSelection.TrySelect(
                [Device("Adreno (TM) 640", AdapterKind.Integrated, driver: "512.530")],
                true,
                denied,
                out _,
                out _
            )
        );
    }
}
