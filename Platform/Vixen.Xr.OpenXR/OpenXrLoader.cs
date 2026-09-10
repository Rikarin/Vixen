// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenXR;
using Vixen.Platform.Native;

namespace Vixen.Xr.OpenXR;

/// <summary>Finding the OpenXR loader, which the runtime installs rather than the game shipping it.</summary>
/// <remarks>
///     <para>
///         <b><c>XR.GetApi()</c> is not called, and that is what clears the AOT gate.</b> It builds
///         Silk.NET's default context, which finds a native library by asking where its own managed
///         assembly is on disk (<c>Assembly.Location</c>) and by reading the dependency manifest
///         (<c>DependencyContext.Default</c>) — neither of which exists in a NativeAOT binary, and
///         both of which ILC reports as IL3000/IL3002. This is the same finding
///         <c>Vixen.Graphics.Vulkan</c>'s <c>VulkanLoader</c> and
///         <c>Vixen.Audio.Backend.OpenAL</c>'s <c>OpenALLoader</c> both record, and
///         <c>Tools/Vixen.AotProbe/README.md</c> named this call site before it was written:
///         <i>"A new Silk.NET backend that calls <c>GetApi()</c> will fail this gate, and that is the
///         intended outcome."</i> It was the only reason <c>Vixen.Xr</c> and <c>Vixen.Xr.OpenXR</c>
///         were on <c>NotRooted.txt</c>.
///     </para>
///     <para>
///         ⚠ <b>Nothing is shipped beside the game, unlike OpenAL.</b> The OpenXR loader belongs to
///         the runtime — SteamVR, the Oculus software, Monado — and a copy carried in the
///         application would shadow the one that knows where the headset is, which the module's own
///         csproj says in as many words. So the application's <c>runtimes/&lt;rid&gt;/native/</c>
///         layout is still searched first, because a developer who put one there meant it, and the
///         undecorated name that lets the dynamic linker answer is the case that will actually fire.
///     </para>
///     <para>
///         ⚠ <b><c>xrGetInstanceProcAddr</c> is the probe, and a handle that lacks it is not an
///         OpenXR.</b> It is the one function the specification requires every loader to export, and
///         checking for it is what keeps something else on the search path with a similar name from
///         being adopted as a runtime and failing four calls later with a null function pointer.
///     </para>
///     <para>
///         <b>Symbol lookup, not <c>DllImport</c>, is what the API object is built over.</b> A
///         <c>LamdaNativeContext</c> over <see cref="NativeLibrary.TryGetExport" /> is behaviourally
///         what Silk.NET's own default context does once it has resolved the file; the difference is
///         only in how the file was found, which is the whole of the AOT problem.
///     </para>
/// </remarks>
static class OpenXrLoader {
    /// <summary>The name a loader ships under, on every platform that has one.</summary>
    /// <remarks>
    ///     One name and not three, unlike OpenAL: the Khronos loader is <c>openxr_loader.dll</c> on
    ///     Windows and <c>libopenxr_loader.so.1</c> everywhere else, and the vendors' own loaders
    ///     keep to it because the specification's loader-negotiation contract is written against it.
    /// </remarks>
    const string Library = "openxr_loader";

    static readonly Lock Gate = new();

    static XR? loaded;
    static string? failure;

    /// <summary>Where the loader was found, for logging at boot.</summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>Loads OpenXR, reporting failure rather than throwing.</summary>
    /// <param name="api">The API, when it loaded.</param>
    /// <param name="reason">Why it did not, when it did not.</param>
    /// <returns>Whether it loaded.</returns>
    public static bool TryLoad([NotNullWhen(true)] out XR? api, [NotNullWhen(false)] out string? reason) {
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

            NativeLibraries.Describe(new NativeLibrarySpec(Library, ["1"], []));

            // So that anything in the binding library that still goes through a DllImport resolves
            // through the application's layout as well.
            NativeLibraries.Register(typeof(XR).Assembly);

            foreach (var candidate in Candidates()) {
                if (!NativeLibrary.TryLoad(candidate, out var handle)) {
                    continue;
                }

                if (!NativeLibrary.TryGetExport(handle, "xrGetInstanceProcAddr", out _)) {
                    continue;
                }

                loaded = new XR(
                    new LamdaNativeContext(symbol => NativeLibrary.TryGetExport(handle, symbol, out var address)
                        ? address
                        : 0)
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

    /// <summary>Every path to try, most specific first.</summary>
    static IEnumerable<string> Candidates() {
        // The application's own layout first, so a loader a developer deliberately placed beside the
        // game beats the machine's.
        foreach (var candidate in NativeLibraries.Candidates(Library)) {
            yield return candidate;
        }

        // Then undecorated, so the operating system searches its own paths — the behaviour
        // XR.GetApi() used to provide before it was removed from this path. This is the case that
        // fires on a machine with a runtime installed, which is every machine that has a headset.
        foreach (var name in NativeLibraryNames.For(Library, "1")) {
            yield return name;
        }
    }

    static string InstallHint() =>
        "No OpenXR loader was found. Silk.NET.OpenXR ships bindings only and this engine deliberately "
        + "carries no loader of its own, because the one that knows where the headset is belongs to the "
        + "runtime: install SteamVR, the Oculus/Meta software, or Monado (`apt install libopenxr-loader1` "
        + "or the equivalent) and start it before the game.";
}
