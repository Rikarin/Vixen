// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
///         This was written against the specification and the Silk.NET bindings on a machine with no
///         Vulkan loader, at the user's direction and with that stated, and has since met a real
///         driver (MoltenVK on macOS, with the Khronos validation layer). The Vulkan calls turned out
///         to be right; what was wrong was the layer <em>underneath</em> them — finding the loader,
///         and then who owns it — which is what <see cref="VulkanLoader" /> and the ownership note in
///         <see cref="Dispose" /> are about.
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

    /// <summary>
    ///     The validation callback, as an address rather than a delegate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This used to be a <c>static readonly</c> delegate field, and that was wrong on iOS
    ///         in a way nothing caught until the sample was run on one.</b> Converting a delegate to
    ///         a function pointer needs a native-to-managed thunk, and .NET builds one by emitting
    ///         code at run time. iOS forbids that, so the first <c>vkCreateInstance</c> died with
    ///         <c>ExecutionEngineException: Attempting to JIT compile method '(wrapper
    ///         native-to-managed) …VulkanInstance:Report' while running in aot-only mode</c>.
    ///     </para>
    ///     <para>
    ///         <c>nuke CheckAotIos</c> did not see it, and that is worth knowing about the gate
    ///         rather than only about this bug: ILC's analysis is over the call graph, and nothing in
    ///         the graph says <c>Marshal.GetFunctionPointerForDelegate</c> will need a thunk it
    ///         cannot generate. A gate that compiles is not a gate that runs.
    ///     </para>
    ///     <para>
    ///         <c>[UnmanagedCallersOnly]</c> makes the compiler emit a real, statically compiled
    ///         entry point, so <c>&amp;Report</c> is an address that exists in the binary. It also
    ///         removes the lifetime problem the delegate field was there to solve: there is no object
    ///         to keep alive.
    ///     </para>
    /// </remarks>
    static readonly PfnDebugUtilsMessengerCallbackEXT CallbackPointer = new(&Report);

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

    /// <summary>The Vulkan version the instance was created against, packed as Vulkan packs it.</summary>
    /// <remarks>
    ///     The ceiling on what is usable, and not the same question as what a <em>device</em>
    ///     supports. A 1.1 instance on a 1.4 device can only reach 1.1 core functionality; everything
    ///     above has to come from extensions. Which is why this is asked for and carried rather than
    ///     assumed — see the note on the application's <c>ApiVersion</c> below.
    /// </remarks>
    public uint ApiVersion { get; private init; }

    /// <summary>
    ///     Whether the validation layer is <em>installed</em>, which is a different question from
    ///     whether it will load.
    /// </summary>
    /// <remarks>
    ///     The gap between the two is the whole reason <see cref="LayerLoadHint" /> exists, and it is
    ///     what lets the test suite tell "this machine has no layers" (skip) apart from "this machine
    ///     has layers and they are not working" (fail, loudly, with the fix).
    /// </remarks>
    internal static bool ValidationLayerInstalled =>
        VulkanLoader.TryLoad(out var api, out _) && HasLayer(api, ValidationLayer);

    /// <summary>Creates an instance with the documented defaults, or explains why it could not.</summary>
    /// <returns>The instance.</returns>
    /// <exception cref="PlatformNotSupportedException">
    ///     Vulkan is not installed, or the instance could not be created — with the reason, and on
    ///     macOS with the SDK the reason usually points at.
    /// </exception>
    public static VulkanInstance Create() => Create(new VulkanInstanceOptions());

    /// <summary>Creates an instance, or explains why it could not.</summary>
    /// <param name="options">What to create.</param>
    /// <returns>The instance.</returns>
    /// <exception cref="PlatformNotSupportedException">
    ///     Vulkan is not installed, or the instance could not be created.
    /// </exception>
    /// <remarks>
    ///     Two overloads rather than one with <c>= default</c>. A record struct's property
    ///     initialisers do not run for <c>default</c>, so an omitted argument would have silently
    ///     meant <c>EnableValidation = false</c> — the opposite of what the property documents, and
    ///     invisible at every call site.
    /// </remarks>
    public static VulkanInstance Create(VulkanInstanceOptions options) {
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

        var instanceVersion = LoaderVersion(api);
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

        bool validation = options.EnableValidation
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

                // The highest the loader offers, not the 1.1 floor.
                //
                // An earlier draft asked for exactly the floor, reasoning that it would stop a driver
                // enabling behaviour we had not tested against. That reasoning is wrong, and wrong in
                // a way only a second driver revealed: the instance version is the ceiling on what
                // *core* functionality is reachable, so a 1.1 instance on a 1.4 device cannot use
                // core dynamic rendering at all — every structure above 1.1 has to arrive through an
                // extension instead. MoltenVK accepted the mismatch silently; lavapipe's validation
                // named it. Nothing is enabled by asking: device features remain opt-in, one at a
                // time, and the floor is still enforced by AdapterSelection.
                ApiVersion = instanceVersion
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

            if (result == Result.ErrorLayerNotPresent && validation) {
                // The layer was enumerated and then would not load. On macOS this is a packaging
                // problem rather than ours: Homebrew's manifest names the layer library by bare
                // filename, and the dynamic linker resolves that against /usr/local/lib and
                // /usr/lib — not /opt/homebrew/lib, where the dylib is. Pre-loading it by absolute
                // path does not help, because the loader's own dlopen still uses the bare name.
                //
                // Retry without it rather than refusing to start. Running unvalidated is bad;
                // failing to open a window because a *development* aid is mispackaged is worse, and
                // the warning says exactly what to do about it.
                if (options.Logger is { } fallbackLogger) {
                    VulkanLog.ValidationLayerWouldNotLoad(fallbackLogger, LayerLoadHint());
                }

                validation = false;
                create.EnabledLayerCount = 0;
                create.PpEnabledLayerNames = null;
                create.PNext = null;
                result = api.CreateInstance(&create, null, &handle);
            }

            if (result != Result.Success) {
                reason = $"vkCreateInstance failed with {result}."
                    + (OperatingSystem.IsMacOS()
                        ? " On macOS this usually means the Vulkan SDK is not installed or "
                        + "VK_ICD_FILENAMES does not point at MoltenVK's ICD."
                        : string.Empty);

                return false;
            }

            instance = new(api, handle, options.Logger, validation) {
                PortabilityEnabled = portability,
                ApiVersion = instanceVersion
            };
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

        // The instance is ours; the API is not. VulkanLoader loads libvulkan once and hands the same
        // Vk to every instance in the process, and Vk.Dispose() unloads the library — so disposing it
        // here leaves every cached entry point in that Vk pointing into unmapped memory, and the next
        // instance created jumps to a stale address and takes the process with it.
        //
        // This was latent for as long as the loader had to fall back to probing explicit paths, whose
        // native context does not own the handle and whose Dispose does nothing. The moment
        // DYLD_LIBRARY_PATH made Vk.GetApi() succeed, the owning context came back and the second
        // instance in the process started segfaulting — a good reminder that "it passes" and "it is
        // correct" are different claims.
        handle = default;
    }

    /// <summary>What to do about a layer that enumerates and then will not load.</summary>
    static string LayerLoadHint() =>
        OperatingSystem.IsMacOS()
            ? "Homebrew's layer manifest names the library by bare filename and /opt/homebrew/lib is not "
            + "on the dynamic linker's search path. Start the process with "
            + "DYLD_LIBRARY_PATH=/opt/homebrew/lib, or install the LunarG SDK, which uses absolute paths."
            : "Check that the layer's library_path in its manifest resolves to a file that exists.";

    static DebugUtilsMessengerCreateInfoEXT MessengerDescription() => new() {
        SType = StructureType.DebugUtilsMessengerCreateInfoExt,

        // Info and verbose are deliberately off: they are voluminous, they are mostly the layers
        // narrating themselves, and a log that has to be filtered to be read does not get read.
        MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
        MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
            | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
            | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
        PfnUserCallback = CallbackPointer
    };

    /// <summary>What the validation layers call when they have something to say.</summary>
    /// <remarks>
    ///     <c>Cdecl</c> because that is what <c>VKAPI_PTR</c> is everywhere except 32-bit Windows,
    ///     which this does not target.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static Bool32 Report(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData
    ) {
        // Nothing may escape: an exception crossing back into native code from here terminates the
        // process, and doing that because a log line could not be formatted would be a spectacular
        // way to lose the actual validation message.
        try {
            var message = data->PMessage is null
                ? "(no message)"
                : Marshal.PtrToStringUTF8((nint)data->PMessage) ?? "(unreadable message)";

            // Written to the console rather than through the ILogger the instance was given, because
            // the callback is static — Vulkan hands back a void* and a captured delegate would have
            // to be pinned for the life of the instance.
            Console.Error.WriteLine($"[vulkan] {severity}: {message}");

            // And recorded, so that the test suite can fail on a validation error rather than
            // printing one. A message on the console is not a gate; it is a thing that scrolls past,
            // which is how both of the first two bugs this backend had survived a green test run.
            VulkanDiagnostics.Record((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0, message);
        } catch (Exception) {
            // Deliberately swallowed and deliberately not logged: the logging is what failed.
        }

        // False, always: returning true aborts the call that triggered the message, which is a
        // debugging aid the specification reserves for layer development and which turns a warning
        // into a crash.
        return false;
    }

    /// <summary>The highest Vulkan version this loader supports.</summary>
    /// <remarks>
    ///     <c>vkEnumerateInstanceVersion</c> arrived in 1.1 and a 1.0 loader does not export it, in
    ///     which case Silk's dispatch fails and 1.0 is the honest answer — which
    ///     <see cref="AdapterSelection" /> then rejects with a readable message rather than this
    ///     failing obscurely here.
    /// </remarks>
    static uint LoaderVersion(Vk api) {
        try {
            uint version = 0;

            return api.EnumerateInstanceVersion(ref version) == Result.Success
                ? Math.Max(version, AdapterSelection.MinimumApiVersion)
                : AdapterSelection.MinimumApiVersion;
        } catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException) {
            return AdapterSelection.MinimumApiVersion;
        }
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
