// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenAL;
using Vixen.Platform.Native;

namespace Vixen.Audio.Backend.OpenAL;

/// <summary>Finding OpenAL, including under the three names it actually ships as.</summary>
/// <remarks>
///     <para>
///         <b><c>AL.GetApi()</c> is not called, and that is what clears the AOT gate.</b> It builds
///         Silk.NET's default context, which finds a native library through
///         <c>Assembly.Location</c> and <c>DependencyContext.Default</c> — neither of which exists in
///         a NativeAOT binary, and both of which ILC reports as IL3000/IL3002. This is the same
///         finding <c>Vixen.Graphics.Vulkan</c>'s <c>VulkanLoader</c> records, arrived at the same
///         way: rooting this assembly in <c>Tools/Vixen.AotProbe</c> with the call in place produces
///         six diagnostics, and without it produces none.
///     </para>
///     <para>
///         <b>Three names, because OpenAL has never agreed with itself about one.</b> The Windows
///         ABI name is <c>OpenAL32.dll</c> — for 64-bit builds too, which is a twenty-year-old
///         accident nobody can fix now — while OpenAL Soft's own build produces <c>soft_oal.dll</c>,
///         and that is what the <c>Silk.NET.OpenAL.Soft.Native</c> package ships. Unix is more
///         civilised: <c>libopenal.so.1</c> and <c>libopenal.1.dylib</c>, with the unversioned name
///         as a development symlink a runtime-only install may not have.
///     </para>
///     <para>
///         The application's own <c>runtimes/&lt;rid&gt;/native/</c> layout is searched before the
///         machine's, so a game plays through the OpenAL Soft it shipped and was tested against
///         rather than through whatever an old install left in <c>/usr/lib</c>.
///     </para>
/// </remarks>
static class OpenALLoader {
    static readonly Lock Gate = new();

    static AL? loadedAl;
    static ALContext? loadedAlc;
    static string? failure;

    /// <summary>Where OpenAL was found, for logging at boot.</summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>Loads OpenAL, reporting failure rather than throwing.</summary>
    /// <param name="al">The AL API, when it loaded.</param>
    /// <param name="alc">The ALC API, when it loaded.</param>
    /// <param name="reason">Why it did not, when it did not.</param>
    /// <returns>Whether it loaded.</returns>
    public static bool TryLoad(
        [NotNullWhen(true)] out AL? al,
        [NotNullWhen(true)] out ALContext? alc,
        [NotNullWhen(false)] out string? reason
    ) {
        lock (Gate) {
            if (loadedAl is not null && loadedAlc is not null) {
                al = loadedAl;
                alc = loadedAlc;
                reason = null;
                return true;
            }

            if (failure is not null) {
                al = null;
                alc = null;
                reason = failure;
                return false;
            }

            foreach (var name in Libraries) {
                NativeLibraries.Describe(new NativeLibrarySpec(name, ["1"], []));
            }

            // So that anything in the binding library that still goes through a DllImport — the
            // extension loaders do — resolves through the application's layout as well.
            NativeLibraries.Register(typeof(AL).Assembly);

            foreach (var candidate in Candidates()) {
                if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                    continue;
                }

                // alcOpenDevice rather than alGenSources: a handle that exports the context half is
                // an OpenAL, and a handle that exports neither is something else with a similar
                // name that happened to be on the path first.
                if (!NativeLibrary.TryGetExport(handle, "alcOpenDevice", out _)) {
                    continue;
                }

                var context = new LamdaNativeContext(
                    symbol => NativeLibrary.TryGetExport(handle, symbol, out var address) ? address : 0
                );

                loadedAl = new AL(context);
                loadedAlc = new ALContext(context);
                ResolvedPath = candidate;

                al = loadedAl;
                alc = loadedAlc;
                reason = null;
                return true;
            }

            failure = InstallHint();
            al = null;
            alc = null;
            reason = failure;
            return false;
        }
    }

    /// <summary>The names OpenAL ships under, most specific first.</summary>
    static IEnumerable<string> Libraries {
        get {
            if (OperatingSystem.IsWindows()) {
                // OpenAL Soft's own name first, because it is what the package ships and what an
                // application will have next to it. OpenAL32 is what a system-wide install is called.
                yield return "soft_oal";
                yield return "OpenAL32";
                yield break;
            }

            yield return "openal";
        }
    }

    static IEnumerable<string> Candidates() {
        foreach (var library in Libraries) {
            // The application's own layout first, so a shipped copy beats a system one.
            foreach (var candidate in NativeLibraries.Candidates(library)) {
                yield return candidate;
            }
        }

        foreach (var library in Libraries) {
            // Then undecorated, so the operating system searches its own paths — the behaviour
            // AL.GetApi() used to provide before it was removed from this path.
            foreach (var name in NativeLibraryNames.For(library, "1")) {
                yield return name;
            }
        }

        if (OperatingSystem.IsMacOS()) {
            // Homebrew's prefix on Apple silicon, which the dynamic linker does not search. The same
            // trap the Vulkan loader documents at length, and it costs one line to avoid.
            yield return "/opt/homebrew/lib/libopenal.dylib";
        }
    }

    static string InstallHint() {
        if (OperatingSystem.IsWindows()) {
            return "OpenAL was not found. A published build carries soft_oal.dll in "
                + "runtimes/<rid>/native; in a development build, check that the "
                + "Silk.NET.OpenAL.Soft.Native package restored.";
        }

        if (OperatingSystem.IsMacOS()) {
            return "OpenAL was not found. A published build carries libopenal.dylib in "
                + "runtimes/<rid>/native; otherwise `brew install openal-soft` provides one. "
                + "macOS's own OpenAL.framework is a deprecated 1.1 shim and is not used.";
        }

        return "OpenAL was not found. A published build carries libopenal.so in "
            + "runtimes/<rid>/native; otherwise install libopenal1 (Debian) or openal-soft (Arch, "
            + "Fedora).";
    }
}
