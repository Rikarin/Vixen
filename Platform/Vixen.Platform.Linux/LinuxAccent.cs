// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using System.Text;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Linux;

/// <summary>The accent colour the desktop is set to, asked of <c>gsettings</c>.</summary>
/// <remarks>
///     <para>
///         <b><c>org.gnome.desktop.interface accent-color</c>, which arrived with GNOME 47.</b> It
///         is the same source <see cref="LinuxAppearance" /> reads <c>color-scheme</c> from and it
///         is read the same way, so everything that file records about spawning <c>gsettings</c>
///         applies here too — including that this is why the desktop appearance is read once and not
///         polled.
///     </para>
///     <para>
///         ⚠ <b>The setting is a <i>name</i> and not a colour, so this desktop needs the table macOS
///         went out of its way to avoid.</b> <c>MacOSAccent</c> is written
///         against <c>+[NSColor controlAccentColor]</c> precisely so that Apple's eight accent
///         colours stay Apple's rather than becoming eight constants in this repository. GNOME
///         offers no equivalent — <c>accent-color</c> answers <c>'teal'</c>, and the colour that
///         name stands for lives in libadwaita's stylesheet — so the table below is unavoidable
///         rather than a shortcut, and it is a table this repository now owns and will have to
///         follow. The values are libadwaita's own <c>AdwAccentColor</c> definitions.
///     </para>
///     <para>
///         ⚠ <b>White is the text colour for every one of the nine, and that is GNOME's answer
///         rather than a guess.</b> libadwaita defines a single <c>accent-fg-color</c> of
///         <c>#ffffff</c> and pairs it with all nine accents; the accents are chosen dark enough for
///         that to hold. This matters more than it looks: <c>PlatformInput.ApplyAccent</c> puts
///         <c>root.system-accent</c> on for the accent <i>and</i> its text or not at all, so a
///         reader that answered the accent alone would fill <c>AccentColor</c> and leave
///         <c>--accent</c> exactly where it was.
///     </para>
///     <para>
///         <b>A name this does not know is <see cref="SystemAccent.Unknown" /> and not the default
///         blue.</b> GNOME may add a tenth, and answering a colour for a name whose colour is not
///         known here would be indistinguishable at the destination from having read it —
///         <see cref="SystemAccent" />'s own remarks are about exactly that. An older GNOME with no
///         such key, a desktop with no <c>gsettings</c>, and a name from the future all land in the
///         same honest place.
///     </para>
///     <para>
///         Returned in sRGB, because <see cref="SystemAccent" /> is: the conversion to linear
///         happens once, at the <c>PlatformInput</c> boundary that knows what a
///         <c>SystemPalette</c> holds.
///     </para>
/// </remarks>
public static class LinuxAccent {
    /// <summary>What libadwaita draws on top of every one of its accents.</summary>
    static readonly Color4 OnAccent = new(1f, 1f, 1f, 1f);

    /// <summary>Reads the accent colour.</summary>
    /// <returns>
    ///     The accent and the colour drawn on it, or <see cref="SystemAccent.Unknown" /> where there
    ///     is no <c>gsettings</c>, no such key, or a name this does not have a colour for.
    /// </returns>
    [SupportedOSPlatform("linux")]
    public static SystemAccent Read() {
        if (!ExternalTool.Exists("gsettings")) {
            return SystemAccent.Unknown;
        }

        if (!ExternalTool.TryRead(
                "gsettings",
                ["get", "org.gnome.desktop.interface", "accent-color"],
                out var output
            )) {
            return SystemAccent.Unknown;
        }

        // `'teal'`, quotes included — gsettings prints the GVariant rather than the string, exactly
        // as it does for `color-scheme`.
        return FromName(Encoding.UTF8.GetString(output).Trim().Trim('\''));
    }

    /// <summary>The colour a GNOME accent name stands for.</summary>
    /// <remarks>
    ///     Separated from the read so that the table can be asserted on a machine that has no
    ///     <c>gsettings</c> to spawn — which is every machine this repository is developed on and
    ///     every container CI runs the Linux leg in.
    /// </remarks>
    /// <param name="name">The unquoted value of <c>accent-color</c>.</param>
    /// <returns>The accent and its text colour, or <see cref="SystemAccent.Unknown" />.</returns>
    internal static SystemAccent FromName(string name) =>
        name switch {
            "blue" => Accent(0x35, 0x84, 0xe4),
            "teal" => Accent(0x21, 0x90, 0xa4),
            "green" => Accent(0x3a, 0x94, 0x4a),
            "yellow" => Accent(0xc8, 0x88, 0x00),
            "orange" => Accent(0xed, 0x5b, 0x00),
            "red" => Accent(0xe6, 0x2d, 0x42),
            "pink" => Accent(0xd5, 0x61, 0x99),
            "purple" => Accent(0x91, 0x41, 0xac),
            "slate" => Accent(0x6f, 0x83, 0x96),
            _ => SystemAccent.Unknown
        };

    static SystemAccent Accent(int red, int green, int blue) =>
        new(new Color4(red / 255f, green / 255f, blue / 255f, 1f), OnAccent);
}
