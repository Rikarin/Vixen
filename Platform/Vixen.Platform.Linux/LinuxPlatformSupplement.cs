// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Linux;

/// <summary>What Linux knows that SDL does not.</summary>
/// <remarks>
///     <para>
///         Two of the four are conditional, which is the difference between this supplement and the
///         other two: on Windows and macOS the operating system ships the picker and the clipboard,
///         and on Linux the desktop does. A session with neither <c>zenity</c> nor <c>kdialog</c>
///         keeps the portable dialogs and does not report
///         <see cref="PlatformCapabilities.NativeDialogs" />, because reporting a capability the
///         machine does not have is worse than not having it.
///     </para>
///     <para>
///         The other two are unconditional: <c>sched_setaffinity</c> is the kernel's and sysfs is
///         mounted on anything that boots.
///     </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformSupplement : IPlatformSupplement {
    /// <inheritdoc />
    public string Name => "Linux";

    /// <inheritdoc />
    public PlatformServices Augment(in PlatformServices baseline) {
        var services = baseline with {
            Power = new LinuxPowerInfo(baseline.Power),
            Processors = new LinuxProcessorTopology()
        };

        if (LinuxClipboard.IsAvailable) {
            services = services with { Clipboard = new LinuxClipboard(baseline.Clipboard) };
        }

        if (LinuxDialogs.IsAvailable) {
            services = services with {
                Dialogs = new LinuxDialogs(baseline.Dialogs),
                Capabilities = services.Capabilities | PlatformCapabilities.NativeDialogs
            };
        }

        return services;
    }

    /// <inheritdoc />
    public void Dispose() { }
}
