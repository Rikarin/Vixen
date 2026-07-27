// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using Vixen.Platform.Native;

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
///         So: probe explicitly, in an order this file states. Portable, and visible in a stack trace.
///     </para>
///     <para>
///         <b><c>Vk.GetApi()</c> is not called at all, and that is what clears the AOT gate.</b>
///         It builds Silk.NET's default context, which finds a native library through
///         <c>Assembly.Location</c> and <c>DependencyContext.Default</c> — neither of which exists in
///         a NativeAOT binary, and both of which ILC reports as IL3000/IL3002. R11 expected those to
///         need a deliberate suppression on the grounds that ILC's analysis is static, so code
///         unreachable in practice stays reachable in the graph. That was true while something still
///         called <c>GetApi</c>. Nothing does now, so <c>DefaultPathResolver</c> is unreachable in
///         the graph as well, and rooting this assembly reports <b>zero</b> diagnostics rather than
///         six suppressed ones. Measured both ways: putting the call back brings all six straight
///         back.
///     </para>
///     <para>
///         The loading itself goes through <see cref="NativeLibraries" />, so the application's own
///         <c>runtimes/&lt;rid&gt;/native/</c> layout answers before the machine's, and the versioned
///         soname is tried as well as the development symlink.
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

            NativeLibraries.Describe(new("vulkan", ["1"], [.. Prefixes()]));
            NativeLibraries.Register(typeof(Vk).Assembly);

            // Nothing to load where nothing can be loaded. See LinkedIntoTheProcess.
            if (StaticallyLinked && LinkedIntoTheProcess() is { } linked) {
                loaded = linked;
                ResolvedPath = StaticDescription;
                api = loaded;
                reason = null;
                return true;
            }

            foreach (var candidate in Candidates()) {
                if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                    continue;
                }

                loaded = FromHandle(handle);
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

    /// <summary>What <see cref="ResolvedPath" /> says when Vulkan is part of the executable.</summary>
    internal const string StaticDescription = "(MoltenVK, statically linked)";

    /// <summary>Whether this platform links its native code into the executable.</summary>
    /// <remarks>
    ///     iOS and tvOS. Mac Catalyst is not one of these — it is macOS underneath and has an
    ///     ordinary dynamic loader.
    /// </remarks>
    static bool StaticallyLinked => OperatingSystem.IsIOS() || OperatingSystem.IsTvOS();

    /// <summary>
    ///     Vulkan as it exists on a platform that cannot load libraries: already in the process.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         There is no system Vulkan on iOS and an application may not load a library it did not
    ///         ship inside its own bundle, so the entire probe below is meaningless there. What
    ///         happens instead is that MoltenVK is linked into the executable at build time — see
    ///         <c>Vixen.Platform.Native/build/MoltenVK.targets</c> — which makes the process image
    ///         itself the thing to ask for entry points.
    ///     </para>
    ///     <para>
    ///         <c>vkGetInstanceProcAddr</c> is the probe because it is the one function every Vulkan
    ///         implementation must export. Finding it means the archive was linked <i>and</i> its
    ///         symbols were exported, which are two separate build settings that fail independently
    ///         and neither of which says anything at build time when it is missing.
    ///     </para>
    /// </remarks>
    static Vk? LinkedIntoTheProcess() {
        var process = NativeLibrary.GetMainProgramHandle();

        return NativeLibrary.TryGetExport(process, "vkGetInstanceProcAddr", out _) ? FromHandle(process) : null;
    }

    static Vk FromHandle(nint handle) =>
        new(new LamdaNativeContext(name => NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0));

    /// <summary>
    ///     Every path to try, most specific first: the application's own layout, then the
    ///     directories a loader is actually installed in, then the bare name, then MoltenVK.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The first two groups come from <see cref="NativeLibraries" />, which already knows the
    ///         <c>runtimes/&lt;rid&gt;/native/</c> layout, the RID fallback chain and the versioned
    ///         soname — the Loader ships <c>libvulkan.1.dylib</c> and <c>libvulkan.so.1</c> as the
    ///         real files and the unversioned name as a development symlink a runtime-only install
    ///         may not have.
    ///     </para>
    ///     <para>
    ///         <b>The bare name comes after them, not before.</b> It is what the dynamic linker will
    ///         search, and letting it answer first would let a machine's older system-wide copy beat
    ///         the version the application shipped and was tested against.
    ///     </para>
    /// </remarks>
    static IEnumerable<string> Candidates() {
        foreach (var candidate in NativeLibraries.Candidates("vulkan")) {
            yield return candidate;
        }

        // Undecorated, so the OS searches its own paths — the behaviour `Vk.GetApi()` used to
        // provide before it was removed from this path. See the type's remarks for why it went.
        foreach (var name in NativeLibraryNames.For("vulkan", "1")) {
            yield return name;
        }

        if (OperatingSystem.IsWindows()) {
            yield return "vulkan-1.dll";
            yield break;
        }

        if (OperatingSystem.IsMacOS()) {
            // Last resort: MoltenVK linked directly, which is the shipping flavour docs/plan/10
            // describes. It works and it silently costs the validation layers, because MoltenVK does
            // not load layers itself — so it is tried only after every loader path has failed.
            yield return "/opt/homebrew/lib/libMoltenVK.dylib";
        }
    }

    /// <summary>
    ///     Where a Vulkan loader is installed, beyond the application's own directory.
    /// </summary>
    /// <remarks>
    ///     <c>VULKAN_SDK</c> comes first because a developer who set it meant it. Then the package
    ///     managers: macOS's dynamic linker searches <c>/usr/local/lib</c> and <c>/usr/lib</c> and
    ///     not <c>/opt/homebrew/lib</c>, which is where Homebrew puts everything on Apple silicon.
    /// </remarks>
    static IEnumerable<string> Prefixes() {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");

        if (!string.IsNullOrEmpty(sdk)) {
            yield return Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin" : "lib");
        }

        if (OperatingSystem.IsMacOS()) {
            yield return "/opt/homebrew/lib";
            yield return "/usr/local/lib";
            yield break;
        }

        if (OperatingSystem.IsLinux()) {
            yield return "/usr/lib/x86_64-linux-gnu";
            yield return "/usr/lib64";
        }
    }

    static string InstallHint() {
        if (StaticallyLinked) {
            return "Vulkan could not be loaded. This platform links its native code into the "
                + "executable, so MoltenVK has to be linked in at build time and its entry points "
                + "exported — `vkGetInstanceProcAddr` is not in this process. Import "
                + "Vixen.Platform.Native/build/MoltenVK.targets and run `./build.sh RestoreNativeDeps`.";
        }

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
