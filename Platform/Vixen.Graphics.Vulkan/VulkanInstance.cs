// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Vixen.Graphics.Vulkan;

/// <summary>What to create the Vulkan instance as.</summary>
public readonly record struct VulkanInstanceOptions() {
    /// <summary>The application's name, which the driver and tools show.</summary>
    public string ApplicationName { get; init; } = "Vixen";

    /// <summary>
    ///     Whether to enable the validation layers and the debug messenger.
    /// </summary>
    /// <remarks>
    ///     On in every build but <c>Release</c>. Validation-clean-in-debug is a stated
    ///     non-negotiable ([00](../../docs/plan/00-vision-and-principles.md)), and the cost of
    ///     leaving it off is that the first thing a new backend does wrong goes unnoticed until it
    ///     corrupts something.
    /// </remarks>
    public bool EnableValidation { get; init; } = true;

    /// <summary>Extra instance extensions the surface needs — SDL says which.</summary>
    public IReadOnlyList<string> RequiredExtensions { get; init; } = [];

    /// <summary>Where validation messages go.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>The Vulkan instance, its layers, and the messenger that routes what they say.</summary>
/// <remarks>
///     <para>
///         <b>Nothing in this file has run.</b> It was written against the specification and the
///         Silk.NET bindings on a machine with no Vulkan loader, at the user's direction and with
///         that stated. The pure parts of this backend — <see cref="VulkanFormats" /> and
///         <see cref="AdapterSelection" /> — are tested; this is not, and should be treated as a
///         first draft until it has met a driver.
///     </para>
///     <para>
///         The portability handling is the part most likely to be wrong and most likely to be
///         blamed on something else. On macOS the Loader will not return MoltenVK's
///         <c>VkPhysicalDevice</c> at all unless the instance is created with
///         <c>VK_KHR_portability_enumeration</c> <em>and</em> the matching create flag — the symptom
///         is "no Vulkan devices found" on a machine that works fine
///         ([10](../../docs/plan/10-platforms.md) § macOS).
///     </para>
/// </remarks>
public sealed unsafe class VulkanInstance : IDisposable {
    const string ValidationLayer = "VK_LAYER_KHRONOS_validation";
    const string PortabilityEnumeration = "VK_KHR_portability_enumeration";

    readonly ILogger? logger;
    readonly ExtDebugUtils? debugUtils;
    readonly DebugUtilsMessengerEXT messenger;

    Instance handle;
    bool disposed;

    VulkanInstance(Vk api, Instance handle, ILogger? logger, bool validation) {
        Api = api;
        this.handle = handle;
        this.logger = logger;
        ValidationEnabled = validation;

        if (!validation || !api.TryGetInstanceExtension(handle, out ExtDebugUtils utils)) {
            return;
        }

        debugUtils = utils;
        var create = MessengerDescription();

        fixed (DebugUtilsMessengerEXT* target = &messenger) {
            if (utils.CreateDebugUtilsMessenger(handle, &create, null, target) != Result.Success) {
                debugUtils = null;
            }
        }
    }

    /// <summary>The loaded Vulkan API.</summary>
    public Vk Api { get; }

    /// <summary>The instance handle.</summary>
    public Instance Handle => handle;

    /// <summary>Whether the validation layers are on.</summary>
    public bool ValidationEnabled { get; }

    /// <summary>Whether the instance was created for a portability driver such as MoltenVK.</summary>
    public bool PortabilityEnabled { get; private init; }

    /// <summary>Creates an instance, or explains why it could not.</summary>
    /// <param name="options">What to create.</param>
    /// <returns>The instance.</returns>
    /// <exception cref="PlatformNotSupportedException">
    ///     Vulkan is not installed, or the instance could not be created — with the reason, and on
    ///     macOS with the SDK the reason usually points at.
    /// </exception>
    public static VulkanInstance Create(VulkanInstanceOptions options = default) {
        if (!TryCreate(options, out var instance, out var reason)) {
            throw new PlatformNotSupportedException(reason);
        }

        return instance;
    }

    /// <summary>Creates an instance, reporting failure rather than throwing.</summary>
    /// <param name="options">What to create.</param>
    /// <param name="instance">The instance, when it was created.</param>
    /// <param name="reason">Why it was not, when it was not.</param>
    /// <returns>Whether an instance was created.</returns>
    public static bool TryCreate(
        VulkanInstanceOptions options,
        [NotNullWhen(true)] out VulkanInstance? instance,
        [NotNullWhen(false)] out string? reason
    ) {
        instance = null;

        if (!VulkanLoader.TryLoad(out var api, out reason)) {
            return false;
        }

        var available = AvailableExtensions(api);
        var layers = new List<string>();
        var extensions = new List<string>(options.RequiredExtensions ?? []);

        // Portability first, because on macOS everything else depends on it. Both the extension and
        // the create flag: the extension alone leaves the Loader filtering MoltenVK out, which is
        // the failure that reads like a missing driver.
        var portability = available.Contains(PortabilityEnumeration);

        if (portability) {
            extensions.Add(PortabilityEnumeration);
        }

        var validation = options.EnableValidation
            && HasLayer(api, ValidationLayer)
            && available.Contains(ExtDebugUtils.ExtensionName);

        if (validation) {
            layers.Add(ValidationLayer);
            extensions.Add(ExtDebugUtils.ExtensionName);
        } else if (options.EnableValidation) {
            if (options.Logger is { } logger) {
                VulkanLog.ValidationLayersMissing(logger);
            }
        }

        var missing = (options.RequiredExtensions ?? []).Where(name => !available.Contains(name)).ToArray();

        if (missing.Length > 0) {
            reason = $"The Vulkan instance is missing required extension(s): {string.Join(", ", missing)}. "
                + "These come from the windowing system, so a mismatch usually means the loader and the "
                + "window server disagree about which surface extension to use.";

            return false;
        }

        var applicationName = (byte*)SilkMarshal.StringToPtr(
            string.IsNullOrWhiteSpace(options.ApplicationName) ? "Vixen" : options.ApplicationName
        );

        var engineName = (byte*)SilkMarshal.StringToPtr("Vixen");
        var layerHandles = SilkMarshal.StringArrayToPtr(layers);
        var extensionHandles = SilkMarshal.StringArrayToPtr(extensions);

        try {
            var application = new ApplicationInfo {
                SType = StructureType.ApplicationInfo,
                PApplicationName = applicationName,
                ApplicationVersion = new Version32(0, 1, 0),
                PEngineName = engineName,
                EngineVersion = new Version32(0, 1, 0),

                // 1.1 is the floor doc 05 states. Asking for exactly the floor rather than the
                // highest available keeps a driver from enabling behaviour we have not tested
                // against; a device that offers more is queried for it explicitly.
                ApiVersion = AdapterSelection.MinimumApiVersion
            };

            var create = new InstanceCreateInfo {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &application,
                EnabledLayerCount = (uint)layers.Count,
                PpEnabledLayerNames = (byte**)layerHandles,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionHandles,
                Flags = portability ? InstanceCreateFlags.EnumeratePortabilityBitKhr : 0
            };

            // Chained onto the create info so the layers can report what goes wrong *during*
            // instance creation, which is otherwise the one window they cannot see into — and is
            // exactly where a bad extension list fails.
            var messengerCreate = MessengerDescription();

            if (validation) {
                create.PNext = &messengerCreate;
            }

            Instance handle;
            var result = api.CreateInstance(&create, null, &handle);

            if (result != Result.Success) {
                reason = $"vkCreateInstance failed with {result}."
                    + (OperatingSystem.IsMacOS()
                        ? " On macOS this usually means the Vulkan SDK is not installed or "
                        + "VK_ICD_FILENAMES does not point at MoltenVK's ICD."
                        : string.Empty);

                return false;
            }

            instance = new(api, handle, options.Logger, validation) { PortabilityEnabled = portability };
            reason = null;
            return true;
        } finally {
            SilkMarshal.Free((nint)applicationName);
            SilkMarshal.Free((nint)engineName);
            SilkMarshal.Free(layerHandles);
            SilkMarshal.Free(extensionHandles);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (debugUtils is not null) {
            debugUtils.DestroyDebugUtilsMessenger(handle, messenger, null);
            debugUtils.Dispose();
        }

        Api.DestroyInstance(handle, null);
        Api.Dispose();
        handle = default;
    }

    static DebugUtilsMessengerCreateInfoEXT MessengerDescription() => new() {
        SType = StructureType.DebugUtilsMessengerCreateInfoExt,

        // Info and verbose are deliberately off: they are voluminous, they are mostly the layers
        // narrating themselves, and a log that has to be filtered to be read does not get read.
        MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
        MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
            | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
            | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
        PfnUserCallback = (PfnDebugUtilsMessengerCallbackEXT)Report
    };

    static uint Report(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData
    ) {
        var message = data->PMessage is null ? "(no message)" : Marshal.PtrToStringUTF8((nint)data->PMessage);

        // Written to the console rather than through the ILogger the instance was given, because the
        // callback is static — Vulkan hands back a void* and a captured delegate would have to be
        // pinned for the life of the instance. Routing it properly is the first thing to do once
        // there is a driver to test against.
        Console.Error.WriteLine($"[vulkan] {severity}: {message}");

        // Zero, always: returning non-zero aborts the call that triggered the message, which is a
        // debugging aid the specification reserves for layer development and which turns a warning
        // into a crash.
        return Vk.False;
    }

    static HashSet<string> AvailableExtensions(Vk api) {
        var names = new HashSet<string>(StringComparer.Ordinal);
        uint count = 0;

        if (api.EnumerateInstanceExtensionProperties((byte*)null, ref count, null) != Result.Success) {
            return names;
        }

        var properties = new ExtensionProperties[count];

        fixed (ExtensionProperties* first = properties) {
            if (api.EnumerateInstanceExtensionProperties((byte*)null, &count, first) != Result.Success) {
                return names;
            }

            for (var index = 0u; index < count; index++) {
                var name = SilkMarshal.PtrToString((nint)first[index].ExtensionName);

                if (name is not null) {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    static bool HasLayer(Vk api, string layer) {
        uint count = 0;

        if (api.EnumerateInstanceLayerProperties(ref count, null) != Result.Success) {
            return false;
        }

        var properties = new LayerProperties[count];

        fixed (LayerProperties* first = properties) {
            if (api.EnumerateInstanceLayerProperties(&count, first) != Result.Success) {
                return false;
            }

            for (var index = 0u; index < count; index++) {
                if (string.Equals(SilkMarshal.PtrToString((nint)first[index].LayerName), layer, StringComparison.Ordinal)) {
                    return true;
                }
            }
        }

        return false;
    }
}
