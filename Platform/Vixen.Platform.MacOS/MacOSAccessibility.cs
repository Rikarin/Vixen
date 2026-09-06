// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>Which accessibility settings macOS is running with.</summary>
/// <remarks>
///     <para>
///         <b><c>com.apple.universalaccess</c> in <c>NSUserDefaults</c>, and not
///         <c>NSWorkspace.accessibilityDisplayShouldReduceMotion</c>.</b> The two agree, and only one
///         of them is reachable from a process that never made an <c>NSApplication</c> — which is
///         every SDL application, this engine's included. It is the same choice
///         <see cref="MacOSAppearance" /> makes and for the same reason: Foundation answers, AppKit
///         wants a main thread and an application object.
///     </para>
///     <para>
///         ⚠ <b>An absent key is <c>false</c>, not unknown.</b> These defaults are written when the
///         setting is turned on and the domain simply has no entry until then — so reaching the
///         domain and finding nothing in it is a genuine "the user has not asked for this", the same
///         way <c>AppleInterfaceStyle</c>'s absence is genuinely light. What is unknown is failing to
///         reach the Objective-C runtime at all.
///     </para>
///     <para>
///         ⚠ <b><c>increaseContrast</c> and not <c>reduceTransparency</c>.</b> The two sit next to
///         each other on the same settings pane and only the first is what <c>forced-colors</c>
///         describes — macOS's Reduce Transparency changes materials, not the palette, and reporting
///         it as a forced-colours mode would make a stylesheet throw away its own colours because
///         somebody turned off a blur.
///     </para>
///     <para>
///         ⚠ <b><see cref="SystemAccessibility.TextScale" /> is left <c>null</c> here and there is
///         nothing to write instead.</b> macOS has no system-wide text scale: Dynamic Type is a UIKit
///         API and the Mac's equivalents are per-application (a font size in each app's own
///         preferences) or a display-resolution change, neither of which is a multiplier an
///         application can read. So "no source" is the honest answer, and it is the same
///         <c>null</c>-is-not-<c>1.0</c> reading the two flags below already carry — Windows and
///         GNOME both answer this and macOS does not, which is a difference between platforms rather
///         than a hole in this file.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacOSAccessibility {
    const string Domain = "com.apple.universalaccess";

    /// <summary>Reads the current settings.</summary>
    /// <returns>The settings, or <see cref="SystemAccessibility.Unknown" /> where the Objective-C
    /// runtime or the defaults domain could not be reached.</returns>
    public static SystemAccessibility Read() {
        if (!ObjC.Load()) {
            return SystemAccessibility.Unknown;
        }

        var defaults = ObjC.Send(ObjC.GetClass("NSUserDefaults"), ObjC.Selector("standardUserDefaults"));

        if (defaults == 0) {
            return SystemAccessibility.Unknown;
        }

        var domain = ObjC.Send(defaults, ObjC.Selector("persistentDomainForName:"), ObjC.String(Domain));

        // ⚠ A nil domain is a machine on which nobody has ever opened the accessibility pane, which
        // is most of them, and is not a failure — every setting in it is off.
        if (domain == 0) {
            return new SystemAccessibility(false, false);
        }

        return new SystemAccessibility(Flag(domain, "reduceMotion"), Flag(domain, "increaseContrast"));
    }

    static bool Flag(nint domain, string key) {
        var value = ObjC.Send(domain, ObjC.Selector("objectForKey:"), ObjC.String(key));

        return value != 0 && ObjC.SendBool(value, ObjC.Selector("boolValue"));
    }
}
