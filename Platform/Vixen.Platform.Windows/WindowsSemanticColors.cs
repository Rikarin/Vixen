// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>The palette Windows is drawing with, read only while a high-contrast scheme is on.</summary>
/// <remarks>
///     <para>
///         <b>The classic system colours — <c>COLOR_WINDOW</c>, <c>COLOR_WINDOWTEXT</c>,
///         <c>COLOR_BTNFACE</c> and the rest — are the one place Windows publishes a whole
///         semantic palette, and ⚠ they are only the palette the user is looking at while a
///         high-contrast scheme is on.</b> Dark mode does not touch them: on a Windows 10 or 11 set
///         to dark with no high-contrast scheme, <c>GetSysColor(COLOR_WINDOW)</c> still answers
///         white and <c>COLOR_WINDOWTEXT</c> black, because the classic table predates the
///         Personalize key and nothing rewrites it when the app theme flips. A reader that supplied
///         those unconditionally would therefore put a white <c>Canvas</c> under a dark document —
///         actively worse than the browser-default dark table <c>SystemPalette</c> already carries,
///         and quieter, since a light field on a dark window reads as a choice rather than a bug.
///     </para>
///     <para>
///         <b>Under a high-contrast scheme the table is exactly right, and it is the read that
///         matters most.</b> A high-contrast scheme is a palette the user picked — Aquatic, Desert,
///         Dusk, Night sky, or their own edit of one — and <c>SystemPalette.HighContrast</c> is a
///         fixed guess at it that cannot know which. This is the same gate Chromium's forced-colours
///         path uses: system colours from <c>GetSysColor</c> while <c>HCF_HIGHCONTRASTON</c> is
///         set, its own tables otherwise. And it composes with what <c>PlatformInput</c> already
///         does: <c>ApplyAccessibility</c> switches the document to the forced table on the same
///         flag, <c>Repalette</c> resets to it, and a role supplied here survives that reset.
///     </para>
///     <para>
///         ⚠ <b>The dark-mode half is not unwritten but blocked at this layer, and the block is
///         worth naming so nobody widens the gate to close it.</b> What follows the app theme is
///         WinRT's <c>UISettings.GetColorValue(UIColorType.Foreground)</c> and its
///         <c>Background</c>, which are a COM activation away and not a <c>user32</c> call; nothing
///         in this assembly activates a WinRT class today. Until that exists, a Windows in dark mode
///         answers <see cref="SystemSemanticColors.Unknown" /> and the document follows the
///         appearance through <c>SystemPalette.Dark</c>, which is what a browser does there too.
///     </para>
///     <para>
///         <b>The mapping.</b> <c>Canvas</c>/<c>CanvasText</c> and <c>Field</c>/<c>FieldText</c> are
///         both <c>COLOR_WINDOW</c>/<c>COLOR_WINDOWTEXT</c>, because Windows has one "window"
///         colour and draws edit controls in it; <c>ButtonBorder</c> is <c>COLOR_BTNTEXT</c>, which
///         is what a high-contrast scheme draws a button's edge in and what Chromium maps it to;
///         <c>LinkText</c> is <c>COLOR_HOTLIGHT</c>; the selection pair is the pair
///         <see cref="WindowsAccent" /> reads, because under high contrast the selection is the
///         accent. Returned in sRGB, because <see cref="SystemSemanticColors" /> is.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsSemanticColors {
    /// <summary>Reads the palette.</summary>
    /// <returns>
    ///     Every role the classic table names, or <see cref="SystemSemanticColors.Unknown" /> when no
    ///     high-contrast scheme is on or the answer cannot be believed.
    /// </returns>
    public static SystemSemanticColors Read() {
        if (WindowsAccessibility.HighContrast() is not true) {
            return SystemSemanticColors.Unknown;
        }

        var window = Win32.GetSysColor(Win32.ColorWindow);
        var windowText = Win32.GetSysColor(Win32.ColorWindowText);

        // ⚠ Verify the instrument, on `WindowsAccent`'s terms: `GetSysColor` has no failure channel
        // and answers zero for a call that reached no window station, so a canvas equal to its
        // text — a scheme no one can read — is "could not be asked" rather than a palette.
        if (window == windowText) {
            return SystemSemanticColors.Unknown;
        }

        var highlight = Win32.GetSysColor(Win32.ColorHighlight);
        var highlightText = Win32.GetSysColor(Win32.ColorHighlightText);

        return new SystemSemanticColors(
            Canvas: WindowsAccent.FromColorRef(window),
            CanvasText: WindowsAccent.FromColorRef(windowText),
            LinkText: WindowsAccent.FromColorRef(Win32.GetSysColor(Win32.ColorHotlight)),
            ButtonFace: WindowsAccent.FromColorRef(Win32.GetSysColor(Win32.ColorButtonFace)),
            ButtonText: WindowsAccent.FromColorRef(Win32.GetSysColor(Win32.ColorButtonText)),
            ButtonBorder: WindowsAccent.FromColorRef(Win32.GetSysColor(Win32.ColorButtonText)),
            Field: WindowsAccent.FromColorRef(window),
            FieldText: WindowsAccent.FromColorRef(windowText),
            Highlight: highlight == highlightText ? null : WindowsAccent.FromColorRef(highlight),
            HighlightText: highlight == highlightText ? null : WindowsAccent.FromColorRef(highlightText),
            GrayText: WindowsAccent.FromColorRef(Win32.GetSysColor(Win32.ColorGrayText))
        );
    }
}
