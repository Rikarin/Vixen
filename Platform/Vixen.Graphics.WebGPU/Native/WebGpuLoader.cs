// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.WebGPU;
using Vixen.Platform.Native;

namespace Vixen.Graphics.WebGPU.Native;

/// <summary>Finding Dawn or wgpu-native, wherever it was installed.</summary>
/// <remarks>
///     <para>
///         The same problem <c>VulkanLoader</c> solves and the same shape of answer, for the same
///         two reasons. <c>WebGPU.GetApi()</c> builds Silk.NET's default context, which finds a
///         native library through <c>Assembly.Location</c> and <c>DependencyContext.Default</c> —
///         neither of which exists in a NativeAOT binary, and both of which ILC reports (R11). And
///         the dynamic linker's own search does not include <c>/opt/homebrew/lib</c> on Apple
///         silicon, so a machine with a perfectly good <c>libwgpu_native.dylib</c> fails to load it
///         with an error that says nothing about paths.
///     </para>
///     <para>
///         <b>Unlike Vulkan, there is no system WebGPU on any desktop.</b> Nothing ships Dawn or
///         wgpu-native as part of the OS, and there is no NuGet package carrying the binaries for
///         the RIDs the engine targets — <c>Silk.NET.WebGPU</c> is bindings only. So the ordinary
///         outcome on a developer's machine is that this fails, and the failure is not an error:
///         backend selection moves on to Vulkan, the way it moves on when a GPU is missing. That is
///         why <see cref="TryLoad" /> reports rather than throws, and why the message says where to
///         put the library rather than "install WebGPU".
///     </para>
/// </remarks>
static class WebGpuLoader {
    static readonly Lock Gate = new();

    static Silk.NET.WebGPU.WebGPU? loaded;
    static string? failure;

    /// <summary>Where the library was found, for logging at boot.</summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>
    ///     <c>wgpuDevicePoll</c>, if the implementation is wgpu-native and exports it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Without this, nothing asynchronous ever completes.</b> WebGPU's buffer map and its
    ///         submitted-work callback are promises, and something has to turn the crank; the C API's
    ///         answer is <c>wgpuInstanceProcessEvents</c>, which wgpu-native 0.19 defines and does not
    ///         implement. Its real one is <c>wgpuDevicePoll</c>, which lives in <c>wgpu.h</c> rather
    ///         than <c>webgpu.h</c> — so it is an extension, Silk.NET does not bind it, and it is
    ///         resolved here by name.
    ///     </para>
    ///     <para>
    ///         Zero on Dawn, which implements <c>wgpuInstanceProcessEvents</c> properly and needs no
    ///         extension. The binding calls whichever it has.
    ///     </para>
    /// </remarks>
    public static nint DevicePoll { get; private set; }

    /// <summary>Whether the loaded implementation is wgpu-native rather than Dawn.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This decides one struct's layout, and getting it wrong is a crash.</b>
    ///         <c>Silk.NET.WebGPU</c> 2.23.0 binds a Dawn-shaped <c>webgpu.h</c>: its
    ///         <c>WGPURenderPassColorAttachment</c> carries a <c>depthSlice</c> field that
    ///         wgpu-native did not have until v22.1.0.1 — which is also the release that
    ///         <em>removed</em> three entry points Silk still declares. There is therefore no
    ///         wgpu-native release whose ABI matches this binding on both counts, and the last one
    ///         that matches on functions is v0.19.4.1, which is what
    ///         <c>build/native-dependencies.json</c> pins. See
    ///         <c>NativeWebGpuBinding.BeginRenderPass</c> for the one place that differs.
    ///     </para>
    ///     <para>
    ///         Told apart by <c>wgpuDevicePoll</c>, which is wgpu-native's own extension and which
    ///         Dawn does not export. That is a fact about the library in front of us rather than a
    ///         guess from a version string nobody publishes.
    ///     </para>
    /// </remarks>
    public static bool IsWgpuNative { get; private set; }

    /// <summary>
    ///     Entry points <c>Silk.NET.WebGPU</c> declares that a newer implementation has removed.
    /// </summary>
    /// <remarks>
    ///     Checked at load rather than discovered at the first call. Silk resolves entry points
    ///     lazily through a function pointer, so a missing one is not a link error — it is a null
    ///     pointer called some frames later, which on every platform is a segmentation fault with a
    ///     stack that names nothing useful. Three names cost microseconds and turn that into a
    ///     sentence.
    /// </remarks>
    static readonly string[] Required = [
        "wgpuCreateInstance",
        "wgpuAdapterGetProperties",
        "wgpuDeviceSetUncapturedErrorCallback",
        "wgpuSurfaceGetPreferredFormat"
    ];

    /// <summary>Loads Dawn or wgpu-native, reporting failure rather than throwing.</summary>
    /// <param name="api">The API, when it loaded.</param>
    /// <param name="reason">Why it did not, when it did not.</param>
    public static bool TryLoad(
        [NotNullWhen(true)] out Silk.NET.WebGPU.WebGPU? api,
        [NotNullWhen(false)] out string? reason
    ) {
        lock (Gate) {
            if (loaded is not null) {
                api = loaded;
                reason = null;
                return true;
            }

            if (failure is not null) {
                api = null;
                reason = failure;
                return false;
            }

            foreach (var library in Libraries) {
                NativeLibraries.Describe(new(library, [], [.. Prefixes()]));
            }

            NativeLibraries.Register(typeof(Silk.NET.WebGPU.WebGPU).Assembly);

            foreach (var candidate in Candidates()) {
                if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                    continue;
                }

                var missing = Required.Where(name => !NativeLibrary.TryGetExport(handle, name, out _)).ToArray();

                if (missing.Length > 0) {
                    // Not `continue`: a library that loaded under the right name and exports the
                    // wrong entry points is the version problem, not a search problem, and trying
                    // the next candidate would report "not found" for something that was found.
                    failure = TooNew(candidate, missing);
                    api = null;
                    reason = failure;

                    return false;
                }

                loaded = FromHandle(handle);
                ResolvedPath = candidate;
                DevicePoll = NativeLibrary.TryGetExport(handle, "wgpuDevicePoll", out var poll) ? poll : 0;
                IsWgpuNative = DevicePoll != 0;
                api = loaded;
                reason = null;

                return true;
            }

            failure = InstallHint();
            api = null;
            reason = failure;
            return false;
        }
    }

    /// <summary>
    ///     The names the two implementations ship under.
    /// </summary>
    /// <remarks>
    ///     wgpu-native calls itself <c>wgpu_native</c> and Dawn calls itself <c>webgpu_dawn</c>, and
    ///     an application that fetched one of them into its own <c>runtimes/</c> layout could have
    ///     used either name. Both are tried; neither is preferred, because the API they expose is the
    ///     same header.
    /// </remarks>
    static readonly string[] Libraries = ["wgpu_native", "webgpu_dawn", "webgpu"];

    static Silk.NET.WebGPU.WebGPU FromHandle(nint handle) =>
        new(new LamdaNativeContext(name => NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0));

    static IEnumerable<string> Candidates() {
        foreach (var library in Libraries) {
            foreach (var candidate in NativeLibraries.Candidates(library)) {
                yield return candidate;
            }
        }

        // Undecorated last, so the OS searches its own paths only after the application's own layout
        // has been given the chance to answer. A machine-wide copy beating the one an application
        // shipped and was tested against is the failure this ordering exists to prevent.
        foreach (var library in Libraries) {
            foreach (var name in NativeLibraryNames.For(library)) {
                yield return name;
            }
        }
    }

    static IEnumerable<string> Prefixes() {
        var explicitly = Environment.GetEnvironmentVariable("VIXEN_WEBGPU_PATH");

        if (!string.IsNullOrEmpty(explicitly)) {
            yield return explicitly;
        }

        if (OperatingSystem.IsMacOS()) {
            yield return "/opt/homebrew/lib";
            yield return "/usr/local/lib";
            yield break;
        }

        if (OperatingSystem.IsLinux()) {
            yield return "/usr/lib/x86_64-linux-gnu";
            yield return "/usr/lib64";
            yield return "/usr/local/lib";
        }
    }

    static string TooNew(string path, string[] missing) =>
        $"'{path}' is a WebGPU implementation this binding cannot call: it does not export "
        + $"{string.Join(", ", missing)}. Those entry points were removed from webgpu.h in 2024 and "
        + "Silk.NET.WebGPU 2.23.0 still declares them, so a newer Dawn or wgpu-native is not "
        + "loadable here — calling one would be a null function pointer rather than an error. "
        + "build/native-dependencies.json pins the last release that works, and moving past it "
        + "means moving Silk.NET.WebGPU first.";

    static string InstallHint() =>
        "No WebGPU implementation could be loaded. Silk.NET.WebGPU is bindings only and no desktop "
        + "operating system ships Dawn or wgpu-native, so one has to be provided: drop "
        + "libwgpu_native (or webgpu_dawn) beside the executable, into "
        + "runtimes/<rid>/native/, or somewhere on the loader's path — or point VIXEN_WEBGPU_PATH at "
        + "the directory holding it. In a browser none of this applies: WebGPU is reached through "
        + "navigator.gpu by Vixen.Graphics.WebGPU.Browser.";
}
