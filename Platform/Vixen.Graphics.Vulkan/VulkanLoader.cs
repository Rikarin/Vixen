// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;

namespace Vixen.Graphics.Vulkan;

/// <summary>Finding the Vulkan loader, including where package managers actually put it.</summary>
/// <remarks>
///     <para>
///         <c>Vk.GetApi()</c> asks the OS to resolve <c>libvulkan</c> by name, and on macOS the
///         dynamic linker's default search path is <c>/usr/local/lib</c> and <c>/usr/lib</c> — not
///         <c>/opt/homebrew/lib</c>, which is where Homebrew puts everything on Apple silicon. So a
///         machine with a working, discoverable Vulkan (<c>vulkaninfo</c> lists MoltenVK, the ICD is
///         registered, everything is fine) still fails to load with a <c>DllNotFoundException</c>,
///         and the error says nothing about paths.
///     </para>
///     <para>
///         This was the very first thing to go wrong when the backend met a real SDK, and it is
///         worth writing down that it was <em>not</em> a Vulkan problem: nothing in the instance
///         code had run yet. Setting <c>DYLD_LIBRARY_PATH</c> would work and is the wrong fix — it
///         is stripped by SIP in some launch paths, it has to be set before the process starts, and
///         it makes running the engine depend on the shell it was started from.
///     </para>
///     <para>
///         So: try the OS first, and fall back to probing the handful of places a loader is actually
///         installed. Explicit, portable, and visible in a stack trace.
///     </para>
/// </remarks>
static class VulkanLoader {
    static readonly Lock Gate = new();

    static Vk? loaded;
    static string? failure;

    /// <summary>Where the loader was found, for logging at boot.</summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>Loads Vulkan, reporting failure rather than throwing.</summary>
    /// <param name="api">The API, when it loaded.</param>
    /// <param name="reason">Why it did not, when it did not.</param>
    public static bool TryLoad([NotNullWhen(true)] out Vk? api, [NotNullWhen(false)] out string? reason) {
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

            try {
                loaded = Vk.GetApi();
                ResolvedPath = "(system search path)";
                api = loaded;
                reason = null;
                return true;
            } catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException
                                                    or TypeInitializationException) {
                // Fall through to the explicit probe.
            }

            foreach (var candidate in Candidates()) {
                if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                    continue;
                }

                loaded = new(
                    new LamdaNativeContext(
                        name => NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0
                    )
                );

                ResolvedPath = candidate;
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
    ///     The places a Vulkan loader is installed, most specific first.
    /// </summary>
    /// <remarks>
    ///     <c>VULKAN_SDK</c> comes first because a developer who set it meant it. Then the package
    ///     managers, then the versioned soname — the Loader ships <c>libvulkan.1.dylib</c> and
    ///     <c>libvulkan.so.1</c> as the real files and the unversioned name as a development symlink
    ///     that a runtime-only install may not have.
    /// </remarks>
    static IEnumerable<string> Candidates() {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");

        if (OperatingSystem.IsMacOS()) {
            if (!string.IsNullOrEmpty(sdk)) {
                yield return Path.Combine(sdk, "lib", "libvulkan.dylib");
                yield return Path.Combine(sdk, "lib", "libvulkan.1.dylib");
            }

            yield return "/opt/homebrew/lib/libvulkan.dylib";
            yield return "/opt/homebrew/lib/libvulkan.1.dylib";
            yield return "/usr/local/lib/libvulkan.dylib";
            yield return "/usr/local/lib/libvulkan.1.dylib";

            // Last resort: MoltenVK linked directly, which is the shipping flavour docs/plan/10
            // describes. It works and it silently costs the validation layers, because MoltenVK does
            // not load layers itself — so it is tried only after every loader path has failed.
            yield return "/opt/homebrew/lib/libMoltenVK.dylib";
            yield break;
        }

        if (OperatingSystem.IsLinux()) {
            if (!string.IsNullOrEmpty(sdk)) {
                yield return Path.Combine(sdk, "lib", "libvulkan.so.1");
            }

            yield return "libvulkan.so.1";
            yield return "/usr/lib/x86_64-linux-gnu/libvulkan.so.1";
            yield return "/usr/lib64/libvulkan.so.1";
            yield break;
        }

        if (!string.IsNullOrEmpty(sdk)) {
            yield return Path.Combine(sdk, "Bin", "vulkan-1.dll");
        }

        yield return "vulkan-1.dll";
    }

    static string InstallHint() {
        var command = OperatingSystem.IsMacOS()
            ? "brew install vulkan-loader molten-vk vulkan-tools, or install the LunarG Vulkan SDK"
            : OperatingSystem.IsLinux()
                ? "apt install libvulkan1 mesa-vulkan-drivers (or the equivalent)"
                : "install your GPU vendor's driver, and the Vulkan SDK for the validation layers";

        return $"Vulkan could not be loaded. Silk.NET.Vulkan ships bindings only, so the loader has to "
            + $"come from the system: {command}. It was not on the dynamic linker's search path and was "
            + "not at any of the usual install locations either.";
    }
}
