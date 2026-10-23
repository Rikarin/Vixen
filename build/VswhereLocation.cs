// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;

namespace Vixen.Build;

/// <summary>
///     Where <c>vswhere.exe</c> is, so that a NativeAOT publish on Windows can find the C++ linker:
///     the half of <c>CheckAot</c> that is about this machine rather than about the code.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Without it on <c>PATH</c> the link fails as MSB3073, exit 123, with no ILC finding
///         anywhere</b> (<a href="https://github.com/Rikarin/Vixen/issues/1354">#1354</a>). The
///         mechanism, read out of <c>Microsoft.DotNet.ILCompiler</c> 10.0.12 and reproduced:
///         <c>findvcvarsall.bat</c> finds <c>vswhere.exe</c> by its absolute path and is fine, but the
///         <c>vcvarsall.bat</c> it then calls runs <c>vswhere.exe</c> by name and prints
///         "'vswhere.exe' is not recognized" when the installer directory is not on <c>PATH</c>. The
///         script still exits 0. ILC's targets capture that output with <c>ConsoleToMSBuild</c> and
///         take everything before the first <c>#</c> as the linker's directory — so the error text
///         becomes the head of <c>CppLinker</c>, and running a "file" by that name is Windows'
///         ERROR_INVALID_NAME, 123. The gate read <c>CheckAot Failed</c> and looked exactly like a
///         trim annotation ILC had rejected.
///     </para>
///     <para>
///         Dependency-free and linked into <c>Vixen.ApiCheck.Tests</c>, for the reason every rule
///         here is: <c>build/_build.csproj</c> is outside the solution.
///     </para>
/// </remarks>
public static class VswhereLocation {
    /// <summary>Where the Visual Studio installer puts <c>vswhere.exe</c>, under <c>%ProgramFiles(x86)%</c>.</summary>
    public const string InstallerDirectory = @"Microsoft Visual Studio\Installer";

    /// <summary>
    ///     The <c>PATH</c> a NativeAOT link on Windows needs, or <see langword="null" /> when
    ///     <c>vswhere.exe</c> is nowhere this knows to look.
    /// </summary>
    /// <param name="path">The current <c>PATH</c>, <c>;</c>-separated.</param>
    /// <param name="programFilesX86">The value of <c>%ProgramFiles(x86)%</c>.</param>
    /// <param name="fileExists">Whether a file exists, so the decision can be tested off Windows.</param>
    /// <returns>
    ///     <paramref name="path" /> unchanged when <c>vswhere.exe</c> is already on it; the installer
    ///     directory prepended when it is there; <see langword="null" /> otherwise.
    /// </returns>
    public static string? PathFor(string? path, string? programFilesX86, Func<string, bool> fileExists) {
        ArgumentNullException.ThrowIfNull(fileExists);

        path ??= string.Empty;

        if (path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => fileExists(entry.TrimEnd('\\') + @"\vswhere.exe"))) {
            return path;
        }

        if (string.IsNullOrWhiteSpace(programFilesX86)) {
            return null;
        }

        var installer = programFilesX86.TrimEnd('\\') + @"\" + InstallerDirectory;

        return fileExists(installer + @"\vswhere.exe")
            ? path.Length == 0 ? installer : installer + ";" + path
            : null;
    }

    /// <summary>What the gate says when <see cref="PathFor" /> finds nothing.</summary>
    /// <remarks>
    ///     ⚠ It has to say the one thing a reader gets wrong: that this is not an ILC finding.
    /// </remarks>
    public const string Missing =
        "vswhere.exe is neither on PATH nor at %ProgramFiles(x86)%\\" + InstallerDirectory + ", so the NativeAOT "
        + "link cannot find the C++ linker. This is this machine, not the code: without it the publish fails as "
        + "MSB3073 exit 123 before ILC reports anything, and a red CheckAot is not a trim or AOT finding. Install "
        + "Visual Studio's \"Desktop development with C++\" workload, or put vswhere.exe on PATH.";
}
