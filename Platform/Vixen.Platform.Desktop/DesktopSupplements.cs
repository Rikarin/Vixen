// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Platform.Linux;
using Vixen.Platform.MacOS;
using Vixen.Platform.Windows;

namespace Vixen.Platform.Desktop;

/// <summary>Choosing the per-OS supplement for the machine this is running on.</summary>
/// <remarks>
///     <para>
///         <b>The dependency points this way round on purpose.</b> <c>Vixen.Platform.Windows</c>,
///         <c>.Linux</c> and <c>.MacOS</c> reference the contracts and nothing else; this assembly
///         references all three and picks one. The alternative — each per-OS assembly registering
///         itself when it loads — reads better and does not work: .NET loads an assembly when
///         something first calls into it, so a module initialiser in an assembly nobody has called
///         into yet has not run, and the registration would depend on whether anything else happened
///         to touch it first.
///     </para>
///     <para>
///         <b>What it costs a build that will only ever run on one of them.</b> A framework-dependent
///         or portable publish carries all three assemblies, which together are a few tens of
///         kilobytes of IL. A RID-specific publish carries one: <c>OperatingSystem.IsWindows()</c>
///         and its neighbours are substituted with constants by the trimmer when the target platform
///         is known, so the other two branches — and everything only they reach — are removed.
///     </para>
/// </remarks>
public static class DesktopSupplements {
    /// <summary>The supplement for this operating system, or <see langword="null" /> on one that has
    /// none.</summary>
    /// <remarks>
    ///     Returns <see langword="null" /> rather than throwing on an unrecognised platform. This is
    ///     called from a constructor whose job is to produce a working platform, and "no per-OS
    ///     improvements are available" is a complete answer — the portable implementation is what is
    ///     left, and it works.
    /// </remarks>
    public static IPlatformSupplement? ForCurrentOperatingSystem() {
        if (OperatingSystem.IsWindows()) {
            return new WindowsPlatformSupplement();
        }

        if (OperatingSystem.IsMacOS()) {
            return new MacOSPlatformSupplement();
        }

        // Linux and no further. The same code would largely work on FreeBSD — the pickers and the
        // clipboard tools are in ports — but sysfs is not there, `[SupportedOSPlatform("linux")]` is
        // what the assembly claims, and a guard that is wider than the annotation is a promise
        // nothing has checked.
        return OperatingSystem.IsLinux() ? new LinuxPlatformSupplement() : null;
    }
}
