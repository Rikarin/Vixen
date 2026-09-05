// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using System.Text;

namespace Vixen.Platform.Linux;

/// <summary>What appearance the desktop is set to, asked of <c>gsettings</c>.</summary>
/// <remarks>
///     <para>
///         <b>The freedesktop answer is <c>org.gnome.desktop.interface color-scheme</c>.</b> It is
///         GNOME-named and not GNOME-only: the XDG appearance portal reads the same key, and KDE,
///         Cinnamon and Budgie all write it so that GTK applications follow their theme. There is no
///         more portable source, which is why a desktop that has none of this reports
///         <see cref="SystemColorScheme.Unknown" /> rather than guessing from a GTK theme name.
///     </para>
///     <para>
///         ⚠ <b>This spawns a process, so it is read once and not polled.</b> Every other platform's
///         appearance is a memory or registry read that a frame loop can afford sixty times a
///         second; this one is a fork, an exec and a D-Bus round trip. A desktop platform that
///         polled it would spend more time asking about the theme than drawing — see
///         <c>DesktopAppearance</c>, which is where that decision is enforced rather than here.
///         Following a change on Linux needs a portal subscription, which this does not have.
///     </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxAppearance {
    /// <summary>Reads the current appearance.</summary>
    /// <returns>The appearance, or <see cref="SystemColorScheme.Unknown" /> where there is no
    /// <c>gsettings</c> or no such key.</returns>
    public static SystemColorScheme Read() {
        if (!ExternalTool.Exists("gsettings")) {
            return SystemColorScheme.Unknown;
        }

        if (!ExternalTool.TryRead("gsettings", ["get", "org.gnome.desktop.interface", "color-scheme"], out var output)) {
            return SystemColorScheme.Unknown;
        }

        // `'prefer-dark'`, quotes included — gsettings prints the GVariant rather than the string.
        var value = Encoding.UTF8.GetString(output).Trim().Trim('\'');

        return value switch {
            "prefer-dark" => SystemColorScheme.Dark,
            "prefer-light" => SystemColorScheme.Light,

            // ⚠ `default` is not light. It is the user never having chosen, which is exactly what
            // `Unknown` means and exactly what a stylesheet's `(prefers-color-scheme: light)` must
            // not match — the GNOME default has been the light palette and the key still says the
            // user did not ask for it.
            _ => SystemColorScheme.Unknown
        };
    }
}
