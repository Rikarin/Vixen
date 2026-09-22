// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>AppKit's semantic colours, read as the CSS system colours they fill.</summary>
/// <remarks>
///     <para>
///         <b>The read #838 was open against for three batches, on the claim that it could not be
///         made.</b> "An SDL process has no <c>NSApplication</c> and <c>NSColor</c> wants one" was
///         written in three files and is false: a semantic colour is a <i>class</i> method resolved
///         against <c>NSAppearance.currentDrawingAppearance</c>, which has a sensible value with no
///         application object in sight, follows the <em>system</em> appearance, and works on a
///         secondary thread. <c>MacOSSemanticColorTests</c> is the tripwire under that measurement
///         and <see cref="MacOSAccent" /> was its first production caller; this is the rest of the
///         palette through the same <see cref="MacOSAccent.Component" />.
///     </para>
///     <para>
///         <b>The mapping, AppKit name to CSS role.</b> <c>labelColor</c> is <c>CanvasText</c> and
///         <c>windowBackgroundColor</c> is <c>Canvas</c>; <c>textColor</c> on
///         <c>textBackgroundColor</c> is the field pair; <c>controlTextColor</c> on
///         <c>controlColor</c> is the button pair, with <c>separatorColor</c> for its border;
///         <c>selectedContentBackgroundColor</c> with <c>alternateSelectedControlTextColor</c> is the
///         selection pair — the same white <see cref="MacOSAccent" /> pairs with the accent, because
///         on a Mac a selected row <i>is</i> drawn in the accent; <c>disabledControlTextColor</c> is
///         <c>GrayText</c> and <c>linkColor</c> is <c>LinkText</c>. Each is nullable on its own, so a
///         macOS old enough to lack one — <c>selectedContentBackgroundColor</c> is 10.14 — supplies
///         the rest and leaves that role to the table.
///     </para>
///     <para>
///         ⚠ <b><c>labelColor</c> is 84.7% opaque, and that is kept.</b> AppKit draws its labels
///         slightly soft over whatever is under them; a reader that rounded the alpha to one would
///         put pure black text where the platform draws near-black, invisible in a screenshot and
///         obvious beside a native window. The alpha reaches <c>SystemPalette</c> as read.
///     </para>
///     <para>
///         Returned in sRGB, because <see cref="SystemSemanticColors" /> is: the conversion to
///         linear happens once, at the <c>PlatformInput</c> boundary that knows what a
///         <c>SystemPalette</c> holds.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacOSSemanticColors {
    /// <summary>Reads the palette.</summary>
    /// <returns>
    ///     Every role AppKit answers for, or <see cref="SystemSemanticColors.Unknown" /> where the
    ///     Objective-C runtime or AppKit could not be reached.
    /// </returns>
    public static SystemSemanticColors Read() {
        if (!ObjC.Load()) {
            return SystemSemanticColors.Unknown;
        }

        return new SystemSemanticColors(
            Canvas: MacOSAccent.Component("windowBackgroundColor"),
            CanvasText: MacOSAccent.Component("labelColor"),
            LinkText: MacOSAccent.Component("linkColor"),
            ButtonFace: MacOSAccent.Component("controlColor"),
            ButtonText: MacOSAccent.Component("controlTextColor"),
            ButtonBorder: MacOSAccent.Component("separatorColor"),
            Field: MacOSAccent.Component("textBackgroundColor"),
            FieldText: MacOSAccent.Component("textColor"),
            Highlight: MacOSAccent.Component("selectedContentBackgroundColor"),
            HighlightText: MacOSAccent.Component("alternateSelectedControlTextColor"),
            GrayText: MacOSAccent.Component("disabledControlTextColor")
        );
    }
}
