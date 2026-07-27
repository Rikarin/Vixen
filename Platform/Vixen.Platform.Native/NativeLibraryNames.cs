// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform.Native;

/// <summary>What a native library's file is actually called.</summary>
/// <remarks>
///     <para>
///         A <c>DllImport</c> names a library the way its author thinks of it — <c>vulkan</c>,
///         <c>SDL2</c> — and every operating system spells that differently. The default resolution
///         rules cover the common spellings; what they do not cover is the **versioned soname**,
///         which on Linux and macOS is the file that actually exists.
///     </para>
///     <para>
///         <c>libvulkan.so</c> and <c>libvulkan.dylib</c> are development symlinks, installed by the
///         <i>-dev</i> package and absent from a runtime-only install. The real files are
///         <c>libvulkan.so.1</c> and <c>libvulkan.1.dylib</c>. A machine with a working Vulkan and no
///         SDK installed therefore fails to load a library that is sitting right there, and the
///         exception says nothing about which name it tried. That is not hypothetical: it is the
///         first thing that went wrong when the Vulkan backend met a real driver, and it is recorded
///         in <c>VulkanLoader</c>'s own remarks.
///     </para>
/// </remarks>
public static class NativeLibraryNames {
    /// <summary>Every file name a library might have on this platform, most likely first.</summary>
    /// <param name="library">The name as a <c>DllImport</c> spells it — <c>vulkan</c>.</param>
    /// <param name="versions">Soname versions to try, in order — <c>1</c> for <c>libvulkan.so.1</c>.</param>
    /// <returns>The candidates.</returns>
    public static IEnumerable<string> For(string library, params ReadOnlySpan<string> versions) =>
        ForPlatform(library, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), [.. versions]);

    /// <summary>The same, for a platform that is not this one.</summary>
    /// <param name="library">The library.</param>
    /// <param name="windows">Whether to spell it Windows's way.</param>
    /// <param name="macOS">Whether to spell it macOS's way.</param>
    /// <param name="versions">Soname versions to try.</param>
    /// <returns>The candidates.</returns>
    /// <remarks>
    ///     Split out so the tests can assert all three spellings from one machine. A rule about
    ///     Windows that can only be checked on Windows is a rule that is checked once a release.
    /// </remarks>
    public static IEnumerable<string> ForPlatform(
        string library,
        bool windows,
        bool macOS,
        IReadOnlyList<string> versions
    ) {
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentNullException.ThrowIfNull(versions);

        if (windows) {
            yield return $"{library}.dll";

            foreach (var version in versions) {
                yield return $"{library}-{version}.dll";
            }

            yield break;
        }

        if (macOS) {
            yield return $"lib{library}.dylib";

            foreach (var version in versions) {
                yield return $"lib{library}.{version}.dylib";
            }

            yield break;
        }

        yield return $"lib{library}.so";

        foreach (var version in versions) {
            yield return $"lib{library}.so.{version}";
        }
    }
}
