// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Silk.NET.SDL;

namespace Vixen.Platform.Desktop;

/// <summary>Finding SDL, and saying something useful when it is not there.</summary>
/// <remarks>
///     <para>
///         <c>Silk.NET.SDL</c> is bindings and nothing else — no native binary ships with the
///         package (verified: it has no <c>.Native</c> companion on nuget.org, unlike most of
///         Silk.NET's bindings). So <c>libSDL2</c> comes from the system or from the acquisition
///         step <c>docs/plan/10 § Cross-platform discipline</c> assigns to
///         <c>Vixen.Platform.Native</c>, and until that project exists the failure mode worth
///         designing for is "it is not installed".
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

            try {
                loaded = Sdl.GetApi();
                api = loaded;
                return true;
            } catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException
                                                    or TypeInitializationException) {
                failure = exception;
                api = null;
                return false;
            }
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
