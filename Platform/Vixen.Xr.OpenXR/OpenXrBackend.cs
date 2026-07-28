// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.Extensions.Logging;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Vixen.Core.Mathematics;

namespace Vixen.Xr.OpenXR;

/// <summary>How to set the backend up.</summary>
public readonly record struct OpenXrOptions() {
    /// <summary>What to tell the runtime the application is called.</summary>
    /// <remarks>
    ///     Not decoration: a runtime's own settings, its per-application overrides and its performance
    ///     overlay all key off this, and several of them show it to the user.
    /// </remarks>
    public string ApplicationName { get; init; } = "Vixen";

    /// <summary>Where the backend logs.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Whether to ask for a handheld device rather than a headset.</summary>
    /// <remarks>
    ///     The other form factor OpenXR has: a phone or tablet doing AR through its own screen. The
    ///     rest of this module does not care which — a session is a session and a view is a view —
    ///     but the runtime has to be asked for the right one.
    /// </remarks>
    public bool Handheld { get; init; }
}

/// <summary>The OpenXR runtime, if this machine has one.</summary>
/// <remarks>
///     <para>
///         <b>Constructing this is safe on a machine with no runtime, no headset and no loader.</b>
///         That is the point of the shape: <c>IsAvailable</c> answers "could this process open a
///         session" and <c>UnavailableReason</c> says why not in a sentence, because "VR did not
///         start" has half a dozen completely different causes and the person reading the log needs
///         to know which.
///     </para>
///     <para>
///         <b>The order of operations is dictated by the runtime.</b> An OpenXR instance exists
///         before the graphics device does, because the runtime names the Vulkan extensions the
///         device must be created with and the physical device it must be created on — and neither
///         can be applied afterwards. So: construct this, ask <see cref="GetVulkanRequirements" />,
///         create <c>VulkanDevice</c> with what it said, then <see cref="CreateSession" />.
///     </para>
///     <para>
///         <b><c>XR_KHR_vulkan_enable</c> rather than <c>_enable2</c>.</b> The second revision has
///         the runtime create the Vulkan instance and device itself, which would mean handing this
///         module the job of building the engine's device — and <c>VulkanDevice</c> is where that
///         lives. The first revision's contract is exactly the one this engine wants: the runtime
///         says what it needs, the engine creates the device, the runtime is handed it.
///     </para>
/// </remarks>
public sealed unsafe class OpenXrBackend : IXrBackend {
    readonly XR? api;
    readonly ILogger? logger;
    readonly KhrVulkanEnable? vulkan;

    Instance instance;
    bool disposed;

    /// <summary>Connects to the runtime, or reports why it could not.</summary>
    /// <param name="options">How to set it up.</param>
    public OpenXrBackend(OpenXrOptions options = default) {
        logger = options.Logger;

        try {
            api = XR.GetApi();
        } catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException
            or TypeInitializationException or EntryPointNotFoundException) {
            // The overwhelmingly common case on a developer machine and on every CI runner: there is
            // no loader. Selection constructs every candidate backend, so this must not be fatal.
            UnavailableReason = $"The OpenXR loader could not be loaded ({exception.Message}).";
            Report();

            return;
        }

        try {
            if (!HasExtension(KhrVulkanEnable.ExtensionName)) {
                UnavailableReason =
                    $"The runtime does not offer {KhrVulkanEnable.ExtensionName}, and this engine renders "
                    + "with Vulkan.";
                Report();

                return;
            }

            CreateInstance(options.ApplicationName);

            if (!api.TryGetInstanceExtension(null, instance, out KhrVulkanEnable loaded)) {
                UnavailableReason = $"{KhrVulkanEnable.ExtensionName} was enabled and would not load.";
                Report();

                return;
            }

            vulkan = loaded;

            if (!TryGetSystemId(options.Handheld)) {
                Report();

                return;
            }

            ReadSystemProperties();
            IsAvailable = true;
        } catch (OpenXrException exception) {
            UnavailableReason = exception.Message;
            Report();
        }
    }

    /// <inheritdoc />
    public string Name => "OpenXR";

    /// <inheritdoc />
    public bool IsAvailable { get; private set; }

    /// <inheritdoc />
    public string UnavailableReason { get; private set; } = "";

    /// <summary>The runtime's own name for the system, when there is one.</summary>
    public XrSystemInfo System { get; private set; }

    /// <summary>The runtime's id for the system, which every later call needs.</summary>
    internal ulong SystemId { get; private set; }

    internal XR Api => api ?? throw new OpenXrException("There is no OpenXR runtime in this process.");

    internal Instance Handle => instance;

    internal ILogger? Logger => logger;

    /// <inheritdoc />
    public bool TryGetSystem(out XrSystemInfo system) {
        system = System;

        return IsAvailable;
    }

    /// <inheritdoc />
    public XrVulkanRequirements GetVulkanRequirements() {
        ThrowIfUnavailable();

        var requirements = new GraphicsRequirementsVulkanKHR {
            Type = StructureType.GraphicsRequirementsVulkanKhr
        };

        OpenXrResult.Check(
            vulkan!.GetVulkanGraphicsRequirements(instance, SystemId, &requirements),
            "xrGetVulkanGraphicsRequirementsKHR"
        );

        return new XrVulkanRequirements(
            Split(ReadExtensionList(instanceExtensions: true)),
            Split(ReadExtensionList(instanceExtensions: false)),
            Unpack(requirements.MinApiVersionSupported),
            Unpack(requirements.MaxApiVersionSupported)
        );
    }

    /// <inheritdoc />
    public nint GetVulkanPhysicalDevice(nint vulkanInstance) {
        ThrowIfUnavailable();

        var device = default(VkHandle);

        OpenXrResult.Check(
            vulkan!.GetVulkanGraphicsDevice(instance, SystemId, new VkHandle(vulkanInstance), &device),
            "xrGetVulkanGraphicsDeviceKHR"
        );

        return device.Handle;
    }

    /// <inheritdoc />
    public IXrSession CreateSession(
        in XrVulkanBinding binding,
        in XrSessionOptions options,
        IXrImageImporter? importer = null
    ) {
        ThrowIfUnavailable();

        return new OpenXrSession(this, in binding, in options, importer);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (api is not null && instance.Handle != 0) {
            api.DestroyInstance(instance);
            instance = default;
        }

        api?.Dispose();
    }

    /// <summary>Packs a version the way OpenXR does: major in the top 16, minor in the next 32.</summary>
    internal static ulong Pack(int major, int minor, int patch) =>
        ((ulong)(major & 0xFFFF) << 48) | ((ulong)(minor & 0xFFFF) << 32) | (uint)patch;

    static Version Unpack(ulong version) =>
        new((int)(version >> 48), (int)((version >> 32) & 0xFFFF), (int)(version & 0xFFFFFFFF));

    static string[] Split(string list) =>
        list.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static void WriteName(byte* destination, int capacity, string value) {
        var bytes = Encoding.UTF8.GetBytes(value);
        var count = Math.Min(bytes.Length, capacity - 1);

        for (var index = 0; index < count; index++) {
            destination[index] = bytes[index];
        }

        destination[count] = 0;
    }

    static string ReadName(byte* source, int capacity) {
        var length = 0;

        while (length < capacity && source[length] != 0) {
            length++;
        }

        return Encoding.UTF8.GetString(source, length);
    }

    bool HasExtension(string name) {
        var count = 0u;

        OpenXrResult.Check(
            api!.EnumerateInstanceExtensionProperties((byte*)null, 0, &count, null),
            "xrEnumerateInstanceExtensionProperties"
        );

        if (count == 0) {
            return false;
        }

        var properties = new ExtensionProperties[count];

        for (var index = 0; index < count; index++) {
            properties[index].Type = StructureType.ExtensionProperties;
        }

        fixed (ExtensionProperties* first = properties) {
            OpenXrResult.Check(
                api.EnumerateInstanceExtensionProperties((byte*)null, count, &count, first),
                "xrEnumerateInstanceExtensionProperties"
            );

            for (var index = 0; index < count; index++) {
                if (ReadName(first[index].ExtensionName, 128) == name) {
                    return true;
                }
            }
        }

        return false;
    }

    void CreateInstance(string applicationName) {
        var application = new ApplicationInfo {
            // 1.0.34 rather than 1.1: every runtime in the field implements 1.0, and asking for a
            // version the runtime does not have is a refused instance rather than a degraded one.
            ApiVersion = Pack(1, 0, 34),
            ApplicationVersion = 1,
            EngineVersion = 1
        };

        WriteName(application.ApplicationName, 128, applicationName);
        WriteName(application.EngineName, 128, "Vixen");

        var extensions = SilkMarshal.StringArrayToPtr([KhrVulkanEnable.ExtensionName]);

        try {
            var create = new InstanceCreateInfo {
                Type = StructureType.InstanceCreateInfo,
                ApplicationInfo = application,
                EnabledExtensionCount = 1,
                EnabledExtensionNames = (byte**)extensions
            };

            Instance created;

            OpenXrResult.Check(api!.CreateInstance(&create, &created), "xrCreateInstance");
            instance = created;
        } finally {
            SilkMarshal.Free(extensions);
        }
    }

    bool TryGetSystemId(bool handheld) {
        var info = new SystemGetInfo {
            Type = StructureType.SystemGetInfo,
            FormFactor = handheld ? FormFactor.HandheldDisplay : FormFactor.HeadMountedDisplay
        };

        ulong id;
        var result = api!.GetSystem(instance, &info, &id);

        if (result == Result.ErrorFormFactorUnavailable) {
            // The runtime is installed and the headset is not plugged in, or is asleep. Ordinary, and
            // completely different from "there is no runtime" — which is why the two say so
            // separately.
            UnavailableReason = "An OpenXR runtime is installed and no device is connected to it.";

            return false;
        }

        if (result == Result.ErrorFormFactorUnsupported) {
            UnavailableReason = "The OpenXR runtime does not support this kind of device.";

            return false;
        }

        OpenXrResult.Check(result, "xrGetSystem");
        SystemId = id;

        return true;
    }

    void ReadSystemProperties() {
        var properties = new SystemProperties { Type = StructureType.SystemProperties };

        OpenXrResult.Check(api!.GetSystemProperties(instance, SystemId, &properties), "xrGetSystemProperties");

        var count = 0u;

        OpenXrResult.Check(
            api.EnumerateViewConfigurationView(
                instance,
                SystemId,
                ViewConfigurationType.PrimaryStereo,
                0,
                &count,
                null
            ),
            "xrEnumerateViewConfigurationViews"
        );

        var views = new ViewConfigurationView[Math.Max(1, count)];

        for (var index = 0; index < views.Length; index++) {
            views[index].Type = StructureType.ViewConfigurationView;
        }

        fixed (ViewConfigurationView* first = views) {
            OpenXrResult.Check(
                api.EnumerateViewConfigurationView(
                    instance,
                    SystemId,
                    ViewConfigurationType.PrimaryStereo,
                    count,
                    &count,
                    first
                ),
                "xrEnumerateViewConfigurationViews"
            );
        }

        var view = views[0];

        System = new XrSystemInfo(
            ReadName(properties.SystemName, 256),
            (int)count,
            new Int2((int)view.RecommendedImageRectWidth, (int)view.RecommendedImageRectHeight),
            new Int2((int)view.MaxImageRectWidth, (int)view.MaxImageRectHeight),
            (int)view.RecommendedSwapchainSampleCount,
            properties.TrackingProperties.PositionTracking != 0
        );
    }

    /// <summary>Reads the space-separated extension list the runtime wants on the instance or device.</summary>
    string ReadExtensionList(bool instanceExtensions) {
        var count = 0u;

        OpenXrResult.Check(
            instanceExtensions
                ? vulkan!.GetVulkanInstanceExtension(instance, SystemId, 0, &count, (byte*)null)
                : vulkan!.GetVulkanDeviceExtension(instance, SystemId, 0, &count, (byte*)null),
            instanceExtensions ? "xrGetVulkanInstanceExtensionsKHR" : "xrGetVulkanDeviceExtensionsKHR"
        );

        if (count == 0) {
            return "";
        }

        var buffer = stackalloc byte[(int)count];

        OpenXrResult.Check(
            instanceExtensions
                ? vulkan.GetVulkanInstanceExtension(instance, SystemId, count, &count, buffer)
                : vulkan.GetVulkanDeviceExtension(instance, SystemId, count, &count, buffer),
            instanceExtensions ? "xrGetVulkanInstanceExtensionsKHR" : "xrGetVulkanDeviceExtensionsKHR"
        );

        return ReadName(buffer, (int)count);
    }

    void ThrowIfUnavailable() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsAvailable) {
            throw new InvalidOperationException(UnavailableReason);
        }
    }

    void Report() {
        if (logger is { } target) {
            OpenXrLog.Unavailable(target, UnavailableReason);
        }
    }
}
