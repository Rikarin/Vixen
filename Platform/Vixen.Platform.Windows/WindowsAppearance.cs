// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>What appearance Windows is set to, out of the Personalize key.</summary>
/// <remarks>
///     <para>
///         <b><c>AppsUseLightTheme</c> and not <c>SystemUsesLightTheme</c>.</b> Windows carries two
///         switches under one settings page: the second one is the taskbar and the Start menu, the
///         first one is applications. A window is an application, so the first is the one that
///         answers what an application's own palette should be — and users do set them differently,
///         which is why reading the wrong one is a bug nobody can reproduce.
///     </para>
///     <para>
///         ⚠ <b>The value is <i>light</i>, so it inverts.</b> One means light and zero means dark,
///         which is the reverse of every other flag in this file and is exactly the reading mistake
///         that would ship a dark application to a light desktop.
///     </para>
///     <para>
///         Absent on Windows builds older than 1809, where the whole feature did not exist. That
///         reads as <see cref="SystemColorScheme.Light" /> and not
///         <see cref="SystemColorScheme.Unknown" />: a Windows with no dark mode is a Windows that
///         is light, and reporting no preference there would leave an application choosing for
///         itself on a system that has already answered.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsAppearance {
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Reads the current appearance.</summary>
    /// <returns>The appearance. Never <see cref="SystemColorScheme.Unknown" /> on a working
    /// Windows.</returns>
    public static unsafe SystemColorScheme Read() {
        var size = (uint)sizeof(uint);

        var status = Win32.RegGetValue(
            Win32.HkeyCurrentUser,
            Personalize,
            "AppsUseLightTheme",
            Win32.RrfRtRegDword,
            out _,
            out var light,
            ref size
        );

        // ERROR_SUCCESS. Anything else — the key missing on a pre-1809 build, a policy denying the
        // read — is a system with no dark mode to report.
        return status is not 0 ? SystemColorScheme.Light
            : light == 0 ? SystemColorScheme.Dark
            : SystemColorScheme.Light;
    }
}
