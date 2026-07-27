// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.SDL;
using Vixen.Platform.Native;

namespace Vixen.Platform.Desktop;

/// <summary>Finding SDL, and saying something useful when it is not there.</summary>
/// <remarks>
///     <para>
///         <c>Silk.NET.SDL</c> is bindings and nothing else — no native binary ships with the
///         package (verified: it has no <c>.Native</c> companion on nuget.org, unlike most of
///         Silk.NET's bindings). So <c>libSDL2</c> comes from the application's own
///         <c>runtimes/&lt;rid&gt;/native/</c> layout or from the system, in that order, which is
///         what <see cref="NativeLibraries" /> decides.
///     </para>
///     <para>
///         <b><c>Sdl.GetApi()</c> is not called, for the same reason <c>VulkanLoader</c> stopped
///         calling <c>Vk.GetApi()</c>.</b> It builds Silk.NET's default context, which finds a native
///         library through <c>Assembly.Location</c> and <c>DependencyContext.Default</c> — neither of
///         which exists in a NativeAOT binary, and both of which ILC reports as IL3000/IL3002. Loading
///         the library here and handing Silk a context over the handle keeps that code out of the
///         graph entirely rather than suppressing what it says. See R11.
///     </para>
///     <para>
///         The default failure is a <see cref="DllNotFoundException" /> naming a file, which tells a
///         developer nothing about what to do. <see cref="Load" /> replaces it with the command for
///         their platform, and <see cref="TryLoad" /> lets a test skip instead of failing on a
///         machine that was never going to be able to run it.
///     </para>
/// </remarks>
public static class SdlLibrary {
    static readonly Lock Gate = new();

    static Sdl? loaded;
    static Exception? failure;

    /// <summary>Whether SDL could be found and loaded.</summary>
    /// <remarks>
    ///     Costs one load attempt the first time and nothing afterwards, including when it failed —
    ///     retrying a missing library once per call would turn a clear error into a slow one.
    /// </remarks>
    public static bool IsAvailable => TryLoad(out _);

    /// <summary>Loads SDL, or explains how to install it.</summary>
    /// <returns>The API. Owned by this class; do not dispose it.</returns>
    /// <exception cref="PlatformNotSupportedException">SDL could not be loaded.</exception>
    public static Sdl Load() {
        if (TryLoad(out var api)) {
            return api;
        }

        throw new PlatformNotSupportedException(InstallHint(), failure);
    }

    /// <summary>Loads SDL, reporting failure rather than throwing.</summary>
    /// <param name="api">The API, when it loaded.</param>
    /// <returns><see langword="false" /> if SDL is not installed.</returns>
    public static bool TryLoad([NotNullWhen(true)] out Sdl? api) {
        lock (Gate) {
            if (loaded is not null) {
                api = loaded;
                return true;
            }

            if (failure is not null) {
                api = null;
                return false;
            }

            NativeLibraries.Describe(new("SDL2", [], [.. Prefixes()]));
            NativeLibraries.Register(typeof(Sdl).Assembly);

            foreach (var candidate in Candidates()) {
                if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                    continue;
                }

                loaded = new(
                    new LamdaNativeContext(
                        name => NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0
                    )
                );

                api = loaded;
                return true;
            }

            failure = new DllNotFoundException(InstallHint());
            api = null;
            return false;
        }
    }

    /// <summary>
    ///     Every path to try, most specific first: the application's own layout, then the bare names.
    /// </summary>
    /// <remarks>
    ///     <b>SDL's Linux soname does not follow the usual pattern</b>, which is why the names are
    ///     spelled out rather than produced from a version list. The file is
    ///     <c>libSDL2-2.0.so.0</c> — the ABI version is in the <em>stem</em>, not only in the suffix —
    ///     so <c>libSDL2.so.0</c>, which is what a version list would generate, does not exist on any
    ///     machine. The unversioned <c>libSDL2.so</c> is a development symlink and is absent from a
    ///     runtime-only install, so a Linux box with a perfectly good SDL has neither of the names
    ///     the obvious code would look for.
    /// </remarks>
    static IEnumerable<string> Candidates() {
        foreach (var candidate in NativeLibraries.Candidates("SDL2")) {
            yield return candidate;
        }

        // Undecorated, so the OS searches its own paths — what `Sdl.GetApi()` used to provide.
        foreach (var name in NativeLibraryNames.For("SDL2")) {
            yield return name;
        }

        if (OperatingSystem.IsLinux()) {
            yield return "libSDL2-2.0.so.0";
        }
    }

    /// <summary>Where SDL is installed, beyond the application's own directory.</summary>
    /// <remarks>
    ///     macOS's dynamic linker searches <c>/usr/local/lib</c> and not <c>/opt/homebrew/lib</c>,
    ///     which is where Homebrew puts everything on Apple silicon — the same trap the Vulkan loader
    ///     fell into first.
    /// </remarks>
    static IEnumerable<string> Prefixes() {
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
        var command = OperatingSystem.IsMacOS()
            ? "brew install sdl2"
            : OperatingSystem.IsLinux()
                ? "apt install libsdl2-2.0-0   (or the equivalent for your distribution)"
                : "vcpkg install sdl2, or place SDL2.dll beside the executable";

        return $"SDL2 could not be loaded. Silk.NET.SDL ships bindings only, so the native library "
            + $"has to come from the system: {command}";
    }
}
