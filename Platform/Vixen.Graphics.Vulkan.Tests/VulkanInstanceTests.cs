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
        VulkanRequirement.Available(TryOpen(out var instance, out var reason), reason ?? "no Vulkan");
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
        VulkanRequirement.Available(TryOpen(out var instance, out var reason), reason ?? "no Vulkan");
        using var owned = instance!;

        if (OperatingSystem.IsMacOS()) {
            Assert.True(owned.PortabilityEnabled, "MoltenVK needs portability enumeration and it was not on.");
        }
    }

    /// <summary>
    ///     A layer that is installed is a layer that must actually be on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The backend survives a layer that enumerates and will not load — it retries without
    ///         it rather than refusing to start. That fallback is right, and it is also exactly the
    ///         kind of graceful degradation that hides a broken setup for months: everything works,
    ///         nothing is validated, and the first anyone hears of it is a corrupted resource that
    ///         validation would have named on the frame it happened.
    ///     </para>
    ///     <para>
    ///         So the fallback is asserted <em>against</em> here. Skipped where no layer is
    ///         installed, because that is a legitimate machine; failed where one is installed and
    ///         silently inert, because that is not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ValidationIsOnWhereTheLayerIsInstalled() {
        VulkanRequirement.Available(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");
        VulkanRequirement.Available(TryOpen(out var instance, out var reason), reason ?? "no Vulkan");
        using var owned = instance!;

        Assert.True(
            owned.ValidationEnabled,
            "VK_LAYER_KHRONOS_validation is installed but the instance came up without it, so nothing "
            + "is being validated. On macOS this is the Homebrew layer-manifest problem: the process "
            + "needs DYLD_LIBRARY_PATH=/opt/homebrew/lib, which .runsettings sets for `dotnet test`. "
            + "Running the suite without those settings is the usual cause."
        );
    }

    /// <summary>
    ///     Disposing an instance must leave the process able to create another one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Which sounds too obvious to assert, and is exactly the sort of thing that was broken:
    ///         <c>Dispose</c> also disposed the <c>Vk</c> it had been handed, which unloads
    ///         <c>libvulkan</c> — so the second instance in a process called through entry points
    ///         into unmapped memory and the whole process went down with SIGSEGV.
    ///     </para>
    ///     <para>
    ///         It survived review because a single-instance test passes perfectly, and it survived
    ///         the test suite because the loader was falling back to a native context that does not
    ///         own its handle. Two instances, in one process, in that order, is the shape that finds
    ///         it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnInstanceCanBeCreatedAfterOneIsDisposed() {
        VulkanRequirement.Available(TryOpen(out var first, out var reason), reason ?? "no Vulkan");
        first!.Dispose();

        Assert.True(TryOpen(out var second, out var again), again);
        using var owned = second!;

        Assert.NotEqual(default, owned.Handle);
    }

    [Fact]
    public unsafe void PhysicalDevicesAreEnumerated() {
        VulkanRequirement.Available(TryOpen(out var instance, out var reason), reason ?? "no Vulkan");
        using var owned = instance!;

        uint count = 0;
        Assert.Equal(Result.Success, owned.Api.EnumeratePhysicalDevices(owned.Handle, ref count, null));
        Assert.True(count > 0, "The instance enumerated no physical devices, which is what the portability flag prevents.");
    }
}
