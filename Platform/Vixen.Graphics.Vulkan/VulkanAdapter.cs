// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Vixen.Graphics.Vulkan;

/// <summary>One physical device, with everything the selection policy and device creation need.</summary>
sealed unsafe class VulkanAdapter : IGraphicsAdapter {
    internal const string PortabilitySubset = "VK_KHR_portability_subset";

    VulkanAdapter(PhysicalDevice handle, PhysicalDeviceProperties properties, string name) {
        Handle = handle;
        Properties = properties;
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public AdapterKind Kind => Properties.DeviceType switch {
        PhysicalDeviceType.DiscreteGpu => AdapterKind.Discrete,
        PhysicalDeviceType.IntegratedGpu => AdapterKind.Integrated,
        PhysicalDeviceType.Cpu or PhysicalDeviceType.VirtualGpu => AdapterKind.Software,
        _ => AdapterKind.Unknown
    };

    /// <inheritdoc />
    public string DriverVersion { get; private init; } = "";

    /// <inheritdoc />
    public ulong DeviceMemory { get; private init; }

    /// <inheritdoc />
    public required GraphicsDeviceFeatures Features { get; init; }

    internal PhysicalDevice Handle { get; }

    internal PhysicalDeviceProperties Properties { get; }

    internal required PhysicalDeviceMemoryProperties Memory { get; init; }

    internal required PhysicalDeviceFeatures Supported { get; init; }

    internal required HashSet<string> Extensions { get; init; }

    internal required QueueFamilyPlan Queues { get; init; }

    /// <summary>Everything the instance can see, described.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="surface">The surface a device would have to present to, or <c>0</c> for none.</param>
    /// <param name="khrSurface">The surface extension, needed only when a surface was given.</param>
    public static List<VulkanAdapter> Enumerate(
        VulkanInstance instance,
        SurfaceKHR surface,
        KhrSurface? khrSurface
    ) {
        var api = instance.Api;
        uint count = 0;
        var adapters = new List<VulkanAdapter>();

        if (api.EnumeratePhysicalDevices(instance.Handle, ref count, null) != Result.Success || count == 0) {
            return adapters;
        }

        var devices = new PhysicalDevice[count];

        fixed (PhysicalDevice* first = devices) {
            if (api.EnumeratePhysicalDevices(instance.Handle, &count, first) != Result.Success) {
                return adapters;
            }
        }

        foreach (var device in devices) {
            if (Describe(api, device, surface, khrSurface) is { } adapter) {
                adapters.Add(adapter);
            }
        }

        return adapters;
    }

    /// <summary>Reduces an adapter to what <see cref="AdapterSelection" /> reasons about.</summary>
    public AdapterCandidate ToCandidate() =>
        new(
            Name,
            Kind,
            DeviceMemory,
            Properties.ApiVersion,
            Extensions.Contains(KhrSwapchain.ExtensionName),
            CanPresent,
            HasGraphicsQueue
        );

    internal required bool CanPresent { get; init; }

    internal required bool HasGraphicsQueue { get; init; }

    static VulkanAdapter? Describe(
        Vk api,
        PhysicalDevice device,
        SurfaceKHR surface,
        KhrSurface? khrSurface
    ) {
        PhysicalDeviceProperties properties;
        api.GetPhysicalDeviceProperties(device, &properties);

        PhysicalDeviceMemoryProperties memory;
        api.GetPhysicalDeviceMemoryProperties(device, &memory);

        PhysicalDeviceFeatures supported;
        api.GetPhysicalDeviceFeatures(device, &supported);

        var name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "(unnamed device)";
        var extensions = DeviceExtensions(api, device);
        var families = QueueFamilies(api, device, surface, khrSurface);

        // A device with no graphics family is not one we can plan queues for, and TryPlan says so.
        // It still has to be described rather than dropped, because AdapterSelection's whole job is
        // to name every device it rejected and why.
        var planned = QueueFamilySelection.TryPlan(
            families.ToArray(),
            surface.Handle != 0,
            out var plan,
            out _
        );

        var kind = properties.DeviceType switch {
            PhysicalDeviceType.DiscreteGpu => AdapterKind.Discrete,
            PhysicalDeviceType.IntegratedGpu => AdapterKind.Integrated,
            PhysicalDeviceType.Cpu or PhysicalDeviceType.VirtualGpu => AdapterKind.Software,
            _ => AdapterKind.Unknown
        };

        return new(device, properties, name) {
            DriverVersion = AdapterSelection.Describe(properties.DriverVersion),
            DeviceMemory = LocalMemory(memory),
            Memory = memory,
            Supported = supported,
            Extensions = extensions,
            Queues = plan,
            HasGraphicsQueue = planned || families.Any(f => (f.Flags & QueueFlags.GraphicsBit) != 0),
            CanPresent = families.Any(f => f.CanPresent),
            Features = VulkanFeatures.Translate(
                supported,
                properties.Limits,
                extensions,
                properties.ApiVersion,
                plan,
                VulkanFeatures.HasUnifiedMemory(memory, kind)
            )
        };
    }

    static ulong LocalMemory(in PhysicalDeviceMemoryProperties memory) {
        var largest = 0UL;

        for (var index = 0; index < memory.MemoryHeapCount; index++) {
            var heap = memory.MemoryHeaps[index];

            if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0) {
                largest = Math.Max(largest, heap.Size);
            }
        }

        return largest;
    }

    static HashSet<string> DeviceExtensions(Vk api, PhysicalDevice device) {
        var names = new HashSet<string>(StringComparer.Ordinal);
        uint count = 0;

        if (api.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null) != Result.Success) {
            return names;
        }

        var properties = new ExtensionProperties[count];

        fixed (ExtensionProperties* first = properties) {
            if (api.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, first) != Result.Success) {
                return names;
            }

            for (var index = 0u; index < count; index++) {
                if (SilkMarshal.PtrToString((nint)first[index].ExtensionName) is { } name) {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    static List<QueueFamilyCandidate> QueueFamilies(
        Vk api,
        PhysicalDevice device,
        SurfaceKHR surface,
        KhrSurface? khrSurface
    ) {
        uint count = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);

        var properties = new QueueFamilyProperties[count];
        var families = new List<QueueFamilyCandidate>((int)count);

        fixed (QueueFamilyProperties* first = properties) {
            api.GetPhysicalDeviceQueueFamilyProperties(device, &count, first);

            for (var index = 0u; index < count; index++) {
                var present = false;

                if (surface.Handle != 0 && khrSurface is not null) {
                    Bool32 supported = false;

                    present = khrSurface.GetPhysicalDeviceSurfaceSupport(device, index, surface, &supported)
                        == Result.Success
                        && supported;
                }

                families.Add(new(index, first[index].QueueFlags, first[index].QueueCount, present));
            }
        }

        return families;
    }

    /// <summary>Picks the best adapter, or explains why none will do.</summary>
    public static bool TrySelect(
        List<VulkanAdapter> adapters,
        bool presentRequired,
        [NotNullWhen(true)] out VulkanAdapter? chosen,
        [NotNullWhen(false)] out string? reason
    ) {
        chosen = null;
        var candidates = new AdapterCandidate[adapters.Count];

        for (var index = 0; index < adapters.Count; index++) {
            candidates[index] = adapters[index].ToCandidate();
        }

        if (!AdapterSelection.TrySelect(candidates, presentRequired, out var winner, out reason)) {
            return false;
        }

        chosen = adapters[winner];
        return true;
    }
}
