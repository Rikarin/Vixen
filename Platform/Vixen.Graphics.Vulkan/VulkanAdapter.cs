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

    /// <summary>
    ///     What the device said about acceleration structures, all-false where there was nothing to
    ///     ask.
    /// </summary>
    /// <remarks>
    ///     Kept for the reason <see cref="Indexing" /> is: device creation enables the bits it reads
    ///     out of these rather than a second copy of the answers, so the capability reported and the
    ///     features enabled cannot drift apart.
    /// </remarks>
    internal required PhysicalDeviceAccelerationStructureFeaturesKHR Acceleration { get; init; }

    /// <summary>What it said about ray queries, likewise.</summary>
    internal required PhysicalDeviceRayQueryFeaturesKHR RayQuery { get; init; }

    /// <summary>What it said about buffer device addresses, likewise.</summary>
    internal required PhysicalDeviceBufferDeviceAddressFeatures Addressing { get; init; }

    /// <summary>What <c>VkPhysicalDeviceTimelineSemaphoreFeatures</c> said about it.</summary>
    /// <remarks>
    ///     Kept so device creation can enable exactly the bit the report was made from, rather than
    ///     asking a second time and hoping the two answers agree.
    /// </remarks>
    internal required PhysicalDeviceTimelineSemaphoreFeatures Timeline { get; init; }

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
            HasGraphicsQueue,
            DriverVersion
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
        var (acceleration, rayQuery, addressing) = RayTracing(api, device, extensions, usable);
        var atomics = AtomicInt64(api, device, extensions, usable);
        var timeline = TimelineSemaphores(api, device, extensions, usable);
        var depthResolve = DepthStencilResolve(api, device, extensions, usable);

        return new(device, properties, name) {
            UsableApiVersion = usable,
            Indexing = indexing,
            Acceleration = acceleration,
            RayQuery = rayQuery,
            Addressing = addressing,
            Timeline = timeline,
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
                indexingLimits,
                acceleration,
                rayQuery,
                addressing,
                atomics,
                timeline,
                depthResolve
            )
        };
    }

    /// <summary>What the device says about resolving a depth attachment, where there is anything to say.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The physical device.</param>
    /// <param name="extensions">Its device extensions.</param>
    /// <param name="usable">The version actually reachable through this instance.</param>
    /// <remarks>
    ///     Properties rather than features — a resolve mode is something the device does or does not
    ///     do, with nothing to enable — and gated on the extension for the reason
    ///     <see cref="DescriptorIndexing" /> gives. ⚠ The all-zero answer from a device that was
    ///     never asked still means <c>SampleZero</c> once
    ///     <c>VulkanFeatures.FromDepthResolveModes</c> has read it, because the spec requires that
    ///     one of everybody; the gate is about not confusing "declines Min" with "was not asked".
    /// </remarks>
    static PhysicalDeviceDepthStencilResolveProperties DepthStencilResolve(
        Vk api,
        PhysicalDevice device,
        HashSet<string> extensions,
        uint usable
    ) {
        if (usable < VulkanFeatures.Version12 && !extensions.Contains(VulkanFeatures.DepthStencilResolve)) {
            return default;
        }

        var resolve = new PhysicalDeviceDepthStencilResolveProperties {
            SType = StructureType.PhysicalDeviceDepthStencilResolveProperties
        };

        var properties = new PhysicalDeviceProperties2 {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &resolve
        };

        api.GetPhysicalDeviceProperties2(device, &properties);

        // The chain pointer is a stack address that does not outlive this method — DescriptorIndexing
        // clears its own for the same reason.
        resolve.PNext = null;

        return resolve;
    }

    /// <summary>What the device says about 64-bit atomics, where there is anything to say.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The physical device.</param>
    /// <param name="extensions">Its device extensions.</param>
    /// <param name="usable">The version actually reachable through this instance.</param>
    /// <remarks>
    ///     One call rather than two, because there are no limits to go with it: a 64-bit atomic is a
    ///     yes-or-no about the operation, not a budget. Asked only where the feature exists, for the
    ///     reason <see cref="DescriptorIndexing" /> gives.
    /// </remarks>
    static PhysicalDeviceShaderAtomicInt64Features AtomicInt64(
        Vk api,
        PhysicalDevice device,
        IReadOnlySet<string> extensions,
        uint usable
    ) {
        if (usable < AdapterSelection.MinimumApiVersion || !VulkanFeatures.HasAtomicInt64(extensions, usable)) {
            return default;
        }

        var atomics = new PhysicalDeviceShaderAtomicInt64Features {
            SType = StructureType.PhysicalDeviceShaderAtomicInt64Features
        };

        var features = new PhysicalDeviceFeatures2 {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &atomics
        };

        api.GetPhysicalDeviceFeatures2(device, &features);

        // The chain pointer is a stack address that does not outlive this method — see the same line in
        // DescriptorIndexing, for the same reason.
        atomics.PNext = null;

        return atomics;
    }

    /// <summary>What the device says about timeline semaphores, where there is anything to say.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The physical device.</param>
    /// <param name="extensions">Its device extensions.</param>
    /// <param name="usable">The version actually reachable through this instance.</param>
    /// <remarks>
    ///     One call and no limits, the <see cref="AtomicInt64" /> shape. ⚠ <b>1.2 is not the
    ///     answer.</b> Timeline semaphores went core in 1.2, which makes the structure exist and
    ///     leaves the bit optional — and reading the version instead of the bit is what made
    ///     <c>HasTimelineSemaphores</c> a claim no device had ever been asked to keep.
    /// </remarks>
    static PhysicalDeviceTimelineSemaphoreFeatures TimelineSemaphores(
        Vk api,
        PhysicalDevice device,
        IReadOnlySet<string> extensions,
        uint usable
    ) {
        if (usable < AdapterSelection.MinimumApiVersion
            || !VulkanFeatures.HasTimelineSemaphores(extensions, usable)) {
            return default;
        }

        var timeline = new PhysicalDeviceTimelineSemaphoreFeatures {
            SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures
        };

        var features = new PhysicalDeviceFeatures2 {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &timeline
        };

        api.GetPhysicalDeviceFeatures2(device, &features);

        // The chain pointer is a stack address that does not outlive this method — see the same line in
        // DescriptorIndexing, for the same reason.
        timeline.PNext = null;

        return timeline;
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

    /// <summary>What the device says about ray tracing, where there is anything to say.</summary>
    /// <param name="api">The Vulkan entry points.</param>
    /// <param name="device">The physical device.</param>
    /// <param name="extensions">Its device extensions.</param>
    /// <param name="usable">The version actually reachable through this instance.</param>
    /// <remarks>
    ///     One query with three structures chained, the <see cref="DescriptorIndexing" /> pattern,
    ///     and gated for the same reason: an all-zero structure back from a device without the
    ///     extensions means "no", an all-zero structure that was never written means the same, and
    ///     telling them apart afterwards is impossible. Buffer device address is asked here rather
    ///     than assumed from the 1.2 requirement because 1.2 makes the structure <em>exist</em>, not
    ///     the feature true — the specification leaves every 1.2 feature bit optional.
    /// </remarks>
    static (
        PhysicalDeviceAccelerationStructureFeaturesKHR Acceleration,
        PhysicalDeviceRayQueryFeaturesKHR RayQuery,
        PhysicalDeviceBufferDeviceAddressFeatures Addressing
        ) RayTracing(Vk api, PhysicalDevice device, IReadOnlySet<string> extensions, uint usable) {
        if (usable < AdapterSelection.MinimumApiVersion
            || !VulkanFeatures.HasRayTracingExtensions(extensions, usable)) {
            return (default, default, default);
        }

        var acceleration = new PhysicalDeviceAccelerationStructureFeaturesKHR {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr
        };

        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            PNext = &acceleration
        };

        var addressing = new PhysicalDeviceBufferDeviceAddressFeatures {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = &rayQuery
        };

        var features = new PhysicalDeviceFeatures2 {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &addressing
        };

        api.GetPhysicalDeviceFeatures2(device, &features);

        // Stack addresses, cleared before the copies leave — the DescriptorIndexing comment.
        acceleration.PNext = null;
        rayQuery.PNext = null;
        addressing.PNext = null;

        return (acceleration, rayQuery, addressing);
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
        GpuDenyList denied,
        [NotNullWhen(true)] out VulkanAdapter? chosen,
        [NotNullWhen(false)] out string? reason
    ) {
        chosen = null;
        var candidates = new AdapterCandidate[adapters.Count];

        for (var index = 0; index < adapters.Count; index++) {
            candidates[index] = adapters[index].ToCandidate();
        }

        if (!AdapterSelection.TrySelect(candidates, presentRequired, denied, out var winner, out reason)) {
            return false;
        }

        chosen = adapters[winner];
        return true;
    }
}
