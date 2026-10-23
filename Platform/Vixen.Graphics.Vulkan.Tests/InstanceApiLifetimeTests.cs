// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     What an instance's <c>Vk</c> remembers must not outlive the instance (#1515).
/// </summary>
/// <remarks>
///     <para>
///         Silk.NET's <c>Vk.GetPhysicalDevices(instance)</c> — the call underneath every
///         <c>TryGetDeviceExtension</c> the device constructor makes — caches its answer in a
///         dictionary keyed by the instance handle's <em>value</em>, and nothing evicts an entry when
///         the instance is destroyed. A <c>VkInstance</c> is a heap address and the loader reuses
///         them, so with one <c>Vk</c> shared by every instance in the process, a new instance at a
///         dead one's address was answered with the dead one's freed <c>VkPhysicalDevice</c>
///         handles — and any of those Silk had not already asked about went to
///         <c>vkEnumerateDeviceExtensionProperties</c> as a use-after-free.
///     </para>
///     <para>
///         So this creates and destroys devices one after another, the shape every GPU test assembly
///         has, until the instance address has repeated, and asks each live device's API for its
///         physical devices the way the constructor did. The answer must be the ones a fresh
///         <c>vkEnumeratePhysicalDevices</c> on that instance reports. A run in which no address
///         repeated proves nothing about the defect, so it skips rather than passes.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class InstanceApiLifetimeTests {
    const int Attempts = 32;
    const int ReusesWanted = 3;

    [Fact]
    public void ARecreatedInstanceIsAnsweredWithItsOwnPhysicalDevices() {
        var seen = new HashSet<nint>();
        var reuses = 0;
        var wrong = new List<string>();

        for (var attempt = 0; attempt < Attempts && reuses < ReusesWanted; attempt++) {
            var opened = VulkanDevice.TryCreate(new(), out var device, out var reason);
            VulkanRequirement.Available(opened, reason ?? "no Vulkan");

            using (device) {
                var instance = device!.Instance;
                var address = instance.Handle.Handle;
                var reused = !seen.Add(address);

                if (reused) {
                    reuses++;
                }

                var cached = instance.Api.GetPhysicalDevices(instance.Handle).Select(d => d.Handle).Order().ToArray();
                var live = Enumerate(instance).Select(d => d.Handle).Order().ToArray();

                if (!cached.SequenceEqual(live)) {
                    wrong.Add(
                        $"attempt {attempt}: instance 0x{address:X}{(reused ? " (a reused address)" : "")} "
                        + $"was answered with [{Hex(cached)}] while it enumerates [{Hex(live)}]"
                    );
                }
            }
        }

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} device(s) were told about physical devices that are not their instance's — "
            + "a destroyed instance's freed handles:\n" + string.Join("\n", wrong)
        );

        if (reuses == 0) {
            Assert.Skip($"No instance address repeated in {Attempts} devices, so nothing here could see a stale cache.");
        }
    }

    static unsafe PhysicalDevice[] Enumerate(VulkanInstance instance) {
        uint count = 0;
        Assert.Equal(Result.Success, instance.Api.EnumeratePhysicalDevices(instance.Handle, ref count, null));
        var devices = new PhysicalDevice[count];

        fixed (PhysicalDevice* target = devices) {
            Assert.Equal(Result.Success, instance.Api.EnumeratePhysicalDevices(instance.Handle, ref count, target));
        }

        return devices;
    }

    static string Hex(nint[] handles) => string.Join(", ", handles.Select(h => $"0x{h:X}"));
}
