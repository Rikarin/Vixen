// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Vixen.Platform.Linux;

/// <summary>Which accessibility settings the desktop is running with, asked of <c>gsettings</c>.</summary>
/// <remarks>
///     <para>
///         <b><c>org.gnome.desktop.interface enable-animations</c> and
///         <c>org.gnome.desktop.a11y.interface high-contrast</c>.</b> GNOME-named and not GNOME-only,
///         exactly as the colour scheme is: these are the keys GTK itself reads, so every desktop
///         that wants GTK applications to follow its settings writes them.
///     </para>
///     <para>
///         ⚠ <b><c>enable-animations</c> is the inverse of the preference</b>, the same way Windows'
///         <c>SPI_GETCLIENTAREAANIMATION</c> is. It says whether animation is wanted, so <c>false</c>
///         is the user asking for less of it.
///     </para>
///     <para>
///         ⚠ <b>Two subprocesses, so this is read once and never polled</b> — see
///         <c>DesktopAppearance</c>, which enforces that for the appearance and for this together.
///         Following a change on Linux needs a portal subscription, which this does not have.
///     </para>
///     <para>
///         A missing schema is <c>null</c> rather than <c>false</c>: a desktop with no
///         <c>org.gnome.desktop.a11y.interface</c> has not told us the user is happy with the
///         default palette, it has told us nothing.
///     </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class LinuxAccessibility {
    /// <summary>Reads the current settings.</summary>
    /// <returns>The settings, with an axis left <c>null</c> where there is no <c>gsettings</c> or no
    /// such key.</returns>
    public static SystemAccessibility Read() {
        if (!ExternalTool.Exists("gsettings")) {
            return SystemAccessibility.Unknown;
        }

        var animations = Boolean("org.gnome.desktop.interface", "enable-animations");

        return new SystemAccessibility(
            animations is null ? null : !animations,
            Boolean("org.gnome.desktop.a11y.interface", "high-contrast"),
            Number("org.gnome.desktop.interface", "text-scaling-factor")
        );
    }

    /// <summary>Reads a <c>gsettings</c> key that holds a double.</summary>
    /// <remarks>
    ///     ⚠ <b><c>text-scaling-factor</c> and not <c>scaling-factor</c>.</b> The second is the
    ///     integer HiDPI multiplier and scales the whole interface, which SDL already reports as a
    ///     DPI scale; this one scales text alone and is the accessibility setting. Reading the wrong
    ///     one would double every length on a retina desktop and call it a text preference.
    ///
    ///     <para>
    ///         Invariant parsing, because <c>gsettings</c> prints a C locale double whatever the
    ///         session's language is — a French desktop still says <c>1.25</c>, and parsing it under
    ///         the current culture would read that as one hundred and twenty-five.
    ///     </para>
    /// </remarks>
    static float? Number(string schema, string key) {
        if (!ExternalTool.TryRead("gsettings", ["get", schema, key], out var output)) {
            return null;
        }

        return float.TryParse(
            Encoding.UTF8.GetString(output).Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value
        ) && value > 0f
            ? value
            : null;
    }

    static bool? Boolean(string schema, string key) {
        if (!ExternalTool.TryRead("gsettings", ["get", schema, key], out var output)) {
            return null;
        }

        return Encoding.UTF8.GetString(output).Trim() switch {
            "true" => true,
            "false" => false,
            _ => null
        };
    }
}
