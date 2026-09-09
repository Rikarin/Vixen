// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.MacOS;

/// <summary>The accent colour macOS is set to, read out of AppKit's semantic colours.</summary>
/// <remarks>
///     <para>
///         <b><c>+[NSColor controlAccentColor]</c>, and <i>not</i> <c>AppleAccentColor</c> in
///         <c>NSUserDefaults</c>.</b> The defaults key is an index rather than a colour — one of
///         eight, with the key absent altogether when the user has never moved off the default —
///         so a reader built on it needs a hard-coded table of Apple's eight accent colours plus a
///         ninth answer for "absent", and every one of those nine is a value Apple owns and this
///         repository would be guessing at. The semantic colour is the same choice resolved by the
///         system that made it.
///     </para>
///     <para>
///         ⚠ <b>Absence is the common case on the defaults path and is the reason it is not taken.</b>
///         Measured on a stock macOS 15 machine: <c>AppleAccentColor</c> and
///         <c>AppleHighlightColor</c> are both missing while <c>AppleInterfaceStyle</c> reads
///         <c>Dark</c>. A reader over the defaults would therefore answer "no accent" on almost
///         every Mac; <c>controlAccentColor</c> answers the blue that machine is actually drawing.
///     </para>
///     <para>
///         ⚠ <b>AppKit, from a process with no <c>NSApplication</c>, and that is measured rather
///         than assumed.</b> Three files in this repository recorded that the platform's palette
///         could not be read because "an SDL process has no <c>NSApplication</c> and <c>NSColor</c>
///         wants one". It is false — a semantic colour is a <i>class</i> method resolved against
///         <c>NSAppearance.currentDrawingAppearance</c>, which has a sensible value with no
///         application object in sight, and it works on a secondary thread.
///         <c>MacOSSemanticColorTests</c> is the tripwire under that correction and this reader is
///         its first production caller. What genuinely does need an <c>NSApplication</c> is
///         <c>NSApp.effectiveAppearance</c>, which is why <see cref="MacOSAppearance" /> reads a
///         default instead.
///     </para>
///     <para>
///         ⚠ <b>The accent does not follow the appearance and the text on it does.</b>
///         <c>controlAccentColor</c> is the user's choice and reads the same under Aqua and Dark
///         Aqua — asserted, because two identical readings are also what a broken prototype
///         returns — so this needs no appearance set and is correct whichever one is current.
///         <c>alternateSelectedControlTextColor</c> is the white AppKit draws on a selected row and
///         is what pairs with it.
///     </para>
///     <para>
///         Returned in sRGB, because <see cref="SystemAccent" /> is: the conversion to linear
///         happens once, at the <c>PlatformInput</c> boundary that knows what a
///         <c>SystemPalette</c> holds.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacOSAccent {
    /// <summary>Reads the accent colour.</summary>
    /// <returns>
    ///     The accent and the colour drawn on it, or <see cref="SystemAccent.Unknown" /> where the
    ///     Objective-C runtime, AppKit or the colour could not be reached.
    /// </returns>
    public static SystemAccent Read() {
        if (!ObjC.Load()) {
            return SystemAccent.Unknown;
        }

        var accent = Component("controlAccentColor");

        if (accent is null) {
            return SystemAccent.Unknown;
        }

        return new SystemAccent(accent, Component("alternateSelectedControlTextColor"));
    }

    /// <summary>One <c>+[NSColor …]</c> class colour, resolved into sRGB.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>colorUsingColorSpace:</c> is the step that turns a dynamic catalogue colour into
    ///         components at all: the colour AppKit hands back has no <c>redComponent</c> until it
    ///         has been resolved against a colour space, and asking one for it raises.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every message here is guarded by <c>respondsToSelector:</c> or by a nil check,
    ///         because the failure mode of neither is a wrong colour.</b> An unimplemented selector
    ///         raises, and an Objective-C exception unwinding into managed code takes the process
    ///         with it — which for a colour nobody asked for would be an application that will not
    ///         start on an older macOS.
    ///     </para>
    /// </remarks>
    static Color4? Component(string colour) {
        var nsColor = ObjC.GetClass("NSColor");

        if (nsColor == 0 || !ObjC.SendBool(nsColor, ObjC.Selector("respondsToSelector:"), ObjC.Selector(colour))) {
            return null;
        }

        var dynamic = ObjC.Send(nsColor, ObjC.Selector(colour));

        if (dynamic == 0) {
            return null;
        }

        var space = ObjC.Send(ObjC.GetClass("NSColorSpace"), ObjC.Selector("sRGBColorSpace"));

        if (space == 0) {
            return null;
        }

        var srgb = ObjC.Send(dynamic, ObjC.Selector("colorUsingColorSpace:"), space);

        if (srgb == 0) {
            return null;
        }

        return new Color4(
            (float)ObjC.SendDouble(srgb, ObjC.Selector("redComponent")),
            (float)ObjC.SendDouble(srgb, ObjC.Selector("greenComponent")),
            (float)ObjC.SendDouble(srgb, ObjC.Selector("blueComponent")),
            (float)ObjC.SendDouble(srgb, ObjC.Selector("alphaComponent"))
        );
    }
}
