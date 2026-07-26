// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     The half that needs a driver. Skipped where there is none, so a machine without the Vulkan
///     SDK still runs the rest of the suite rather than failing it.
/// </summary>
[Collection("Vulkan")]
public sealed class VulkanInstanceTests {
    static bool TryOpen(out VulkanInstance? instance, out string? reason) =>
        VulkanInstance.TryCreate(new() { ApplicationName = "Vixen.Tests" }, out instance, out reason);

    [Fact]
    public void AnInstanceIsCreated() {
        Assert.SkipUnless(TryOpen(out var instance, out var reason), reason ?? "no Vulkan");
        using var owned = instance!;

        Assert.NotEqual(default, owned.Handle);
    }

    /// <summary>
    ///     The failure this exists to prevent: without both the extension and the create flag the
    ///     Loader filters MoltenVK out entirely, and the symptom is "no Vulkan devices found" on a
    ///     machine that works fine.
    /// </summary>
    [Fact]
    public void PortabilityIsEnabledWhereTheLoaderOffersIt() {
        Assert.SkipUnless(TryOpen(out var instance, out var reason), reason ?? "no Vulkan");
        using var owned = instance!;

        if (OperatingSystem.IsMacOS()) {
            Assert.True(owned.PortabilityEnabled, "MoltenVK needs portability enumeration and it was not on.");
        }
    }

    [Fact]
    public unsafe void PhysicalDevicesAreEnumerated() {
        Assert.SkipUnless(TryOpen(out var instance, out var reason), reason ?? "no Vulkan");
        using var owned = instance!;

        uint count = 0;
        Assert.Equal(Result.Success, owned.Api.EnumeratePhysicalDevices(owned.Handle, ref count, null));
        Assert.True(count > 0, "The instance enumerated no physical devices, which is what the portability flag prevents.");
    }
}
