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

    /// <summary>What is actually reachable: the lesser of the device's version and the instance's.</summary>
    /// <remarks>
    ///     A 1.4 device behind a 1.1 instance is a 1.1 device as far as core functionality goes, and
    ///     every decision about "is this core or does it need an extension" has to ask this rather
    ///     than the device alone.
    /// </remarks>
    internal required uint UsableApiVersion { get; init; }

    internal required PhysicalDeviceMemoryProperties Memory { get; init; }

    internal required PhysicalDeviceFeatures Supported { get; init; }

    /// <summary>
    ///     What the device said about descriptor indexing, all-false where there was nothing to ask.
    /// </summary>
    /// <remarks>
    ///     Kept rather than reduced to <see cref="GraphicsDeviceFeatures.HasBindless" />, because
    ///     device creation has to hand the same structure back as the set of features to
    ///     <em>enable</em> — and a device created without them behaves exactly like one that never had
    ///     them, with no error anywhere to say which of the two happened.
    /// </remarks>
    internal required PhysicalDeviceDescriptorIndexingFeatures Indexing { get; init; }

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
            if (Describe(api, device, surface, khrSurface, instance.ApiVersion) is { } adapter) {
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
        KhrSurface? khrSurface,
        uint instanceVersion
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

        var usable = Math.Min(properties.ApiVersion, instanceVersion);
        var (indexing, indexingLimits) = DescriptorIndexing(api, device, extensions, usable);

        return new(device, properties, name) {
            UsableApiVersion = usable,
            Indexing = indexing,
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
                usable,
                plan,
                VulkanFeatures.HasUnifiedMemory(memory, kind),

                // The *graphics* family's, because that is the queue a frame's passes are recorded
                // on. Asking the device instead would be `timestampComputeAndGraphics`, which is a
                // stronger claim than the profiler needs and one several drivers decline while
                // still timing the graphics queue perfectly well.
                planned
                    ? families.FirstOrDefault(family => family.Index == plan.Graphics).TimestampValidBits
                    : 0,
                indexing,
                indexingLimits
            )
        };
    }

    /// <summary>What the device says about descriptor indexing, where there is anything to say.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The physical device.</param>
    /// <param name="extensions">Its device extensions.</param>
    /// <param name="usable">The version actually reachable through this instance.</param>
    /// <remarks>
    ///     Two calls rather than one because the features and the limits are different structures on
    ///     different queries, and the engine needs both: the features decide whether a table is
    ///     possible and the limits decide how large it may be. A device with no descriptor indexing
    ///     is not asked at all — <c>vkGetPhysicalDeviceFeatures2</c> itself is core only from 1.1,
    ///     which <see cref="AdapterSelection.MinimumApiVersion" /> already makes the floor, but a
    ///     device below that floor still has to be *described* so selection can name why it was
    ///     rejected.
    /// </remarks>
    static (PhysicalDeviceDescriptorIndexingFeatures Features, PhysicalDeviceDescriptorIndexingProperties Limits)
        DescriptorIndexing(Vk api, PhysicalDevice device, IReadOnlySet<string> extensions, uint usable) {
        if (usable < AdapterSelection.MinimumApiVersion || !VulkanFeatures.HasDescriptorIndexing(extensions, usable)) {
            return (default, default);
        }

        var indexing = new PhysicalDeviceDescriptorIndexingFeatures {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures
        };

        var features = new PhysicalDeviceFeatures2 {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &indexing
        };

        api.GetPhysicalDeviceFeatures2(device, &features);

        var indexingLimits = new PhysicalDeviceDescriptorIndexingProperties {
            SType = StructureType.PhysicalDeviceDescriptorIndexingProperties
        };

        var properties = new PhysicalDeviceProperties2 {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &indexingLimits
        };

        api.GetPhysicalDeviceProperties2(device, &properties);

        // The chain pointers are stack addresses that do not outlive this method, and a caller
        // copying the struct on would carry them. Cleared so the copy is what it looks like: a
        // record of answers, with nothing pointing anywhere.
        indexing.PNext = null;
        indexingLimits.PNext = null;

        return (indexing, indexingLimits);
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

                families.Add(
                    new(
                        index,
                        first[index].QueueFlags,
                        first[index].QueueCount,
                        present,
                        first[index].TimestampValidBits
                    )
                );
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
