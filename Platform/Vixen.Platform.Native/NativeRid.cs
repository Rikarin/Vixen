// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Platform.Native;

/// <summary>Which runtime identifier this process is, and what it will accept instead.</summary>
/// <remarks>
///     <para>
///         <b>Computed, not looked up.</b> .NET's RID graph is a build-time artefact: it is in the
///         NuGet package's dependency resolution and in <c>runtimeconfig.json</c>, and neither of
///         those is present in a NativeAOT application, which is one native binary and nothing else.
///         So the fallback chain is written here, in the four lines it actually takes, rather than
///         read from a file that will not be there on the target this project exists for.
///     </para>
///     <para>
///         The chain is architecture-specific first, then architecture-neutral: a binary built for
///         <c>osx-arm64</c> is preferred over one merely built for <c>osx</c>, and the latter is
///         accepted because a great many native packages ship one directory per operating system and
///         let the fat binary inside sort out the architecture.
///     </para>
/// </remarks>
public static class NativeRid {
    /// <summary>The runtime identifier this process is running as — <c>osx-arm64</c>.</summary>
    public static string Current { get; } = $"{OperatingSystemPart()}-{ArchitecturePart()}";

    /// <summary>The operating-system half on its own — <c>osx</c>.</summary>
    public static string CurrentOperatingSystem { get; } = OperatingSystemPart();

    /// <summary>
    ///     The identifiers a native directory may be named, most specific first.
    /// </summary>
    /// <returns>The chain — <c>osx-arm64</c>, then <c>osx</c>.</returns>
    public static IReadOnlyList<string> Chain { get; } = [Current, CurrentOperatingSystem];

    /// <summary>The chain for a runtime identifier other than this process's.</summary>
    /// <param name="rid">The identifier — <c>win-x64</c>.</param>
    /// <returns>It, and its operating-system half if it has one.</returns>
    /// <remarks>
    ///     For a build machine laying out someone else's target, and for the tests, which otherwise
    ///     could only assert whatever the machine they run on happens to be.
    /// </remarks>
    public static IReadOnlyList<string> ChainFor(string rid) {
        ArgumentException.ThrowIfNullOrEmpty(rid);

        var separator = rid.LastIndexOf('-');

        return separator <= 0 ? [rid] : [rid, rid[..separator]];
    }

    static string OperatingSystemPart() =>
        OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? "osx"
        : OperatingSystem.IsIOS() ? "ios"
        : OperatingSystem.IsAndroid() ? "android"
        : OperatingSystem.IsBrowser() ? "browser"
        : "linux";

    static string ArchitecturePart() =>
        RuntimeInformation.ProcessArchitecture switch {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.Wasm => "wasm",
            var other => other.ToString().ToLowerInvariant()
        };
}
