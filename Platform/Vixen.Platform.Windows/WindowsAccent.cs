// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Windows;

/// <summary>The accent colour Windows is set to, read as the pair it draws a selection with.</summary>
/// <remarks>
///     <para>
///         <b><c>COLOR_HIGHLIGHT</c> and <c>COLOR_HIGHLIGHTTEXT</c>, and ⚠ <i>not</i>
///         <c>DWM\ColorizationColor</c>, which is what this was expected to read.</b> Three things
///         decide it, and the third is the one that settles it:
///     </para>
///     <para>
///         ⚠ <b>The colourisation word's high byte is an opacity and not an alpha.</b> DWM stores
///         <c>AARRGGBB</c> where the <c>AA</c> is how far the glass is blended, commonly <c>0xC4</c>
///         on a stock machine — so a reader that takes it as the accent's alpha publishes a
///         three-quarters-transparent accent, and <see cref="SystemAccent" /> has no way to tell
///         that from a colour a user chose. Every plausible mistake in that word is invisible at the
///         destination: the byte order differs between <c>DWM\AccentColor</c> (<c>AABBGGRR</c>) and
///         <c>DWM\ColorizationColor</c> (<c>AARRGGBB</c>), and a swapped pair is a window painted a
///         colour nobody picked.
///     </para>
///     <para>
///         ⚠ <b>DWM publishes no text colour at all, and half a read moves nothing.</b>
///         <c>PlatformInput.ApplyAccent</c> puts <c>root.system-accent</c> on for the accent
///         <i>and</i> its text or not at all, because a rule firing on half a read leaves the
///         theme's own text sitting on a colour the theme did not choose. A DWM-only reader would
///         therefore have filled <c>AccentColor</c> and left <c>--accent</c> exactly where it was:
///         a finished thing nothing draws with.
///     </para>
///     <para>
///         <b>And the role this fills is the selection.</b> <c>ControlTheme.vcss</c> spends
///         <c>--accent</c> on the focus ring, the switch, the spinner and the selection;
///         <c>COLOR_HIGHLIGHT</c> is by definition the colour Windows draws a selection in, and
///         <c>COLOR_HIGHLIGHTTEXT</c> is what it draws on top. That is the same pairing macOS is
///         read as — <c>controlAccentColor</c> with <c>alternateSelectedControlTextColor</c>, the
///         text on a selected row — so the two desktops answer the same question rather than two
///         adjacent ones, and the pair is guaranteed to contrast because Windows guarantees it.
///     </para>
///     <para>
///         ⚠ <b>The wrong answer this cannot see.</b> Whether <c>COLOR_HIGHLIGHT</c> tracks the
///         accent a user picks in Settings, or stays on the classic selection blue, is a property of
///         the Windows build and could not be measured on the machine this was written on. Both are
///         a colour the system chose rather than one this repository invented, which is why it is
///         acceptable to ship unmeasured where a mis-decoded DWM word would not be — but a Windows
///         runner whose accent has been moved off the default is where
///         <c>WindowsAccentTests</c> is worth the most.
///     </para>
///     <para>
///         Returned in sRGB, because <see cref="SystemAccent" /> is: the conversion to linear
///         happens once, at the <c>PlatformInput</c> boundary that knows what a
///         <c>SystemPalette</c> holds.
///     </para>
/// </remarks>
public static class WindowsAccent {
    /// <summary>Reads the accent colour.</summary>
    /// <returns>
    ///     The accent and the colour drawn on it, or <see cref="SystemAccent.Unknown" /> where the
    ///     answer cannot be believed.
    /// </returns>
    [SupportedOSPlatform("windows")]
    public static SystemAccent Read() {
        var accent = Win32.GetSysColor(Win32.ColorHighlight);
        var text = Win32.GetSysColor(Win32.ColorHighlightText);

        // ⚠ Verify the instrument, because `GetSysColor` has no failure channel: an index it does
        // not know answers zero, and so does a call that reached no window station. Two equal
        // answers are the shape both of those take — black text on black selection is not a scheme
        // Windows ships — so that reads as "could not be asked" rather than as a colour.
        return accent == text ? SystemAccent.Unknown : new SystemAccent(FromColorRef(accent), FromColorRef(text));
    }

    /// <summary>One <c>COLORREF</c>, opaque, in sRGB.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>COLORREF</c> is <c>0x00BBGGRR</c> — blue in the high byte of the three, which is
    ///     the reverse of the hex a human writes.</b> Reading it the other way round is the one
    ///     mistake here whose result is still a plausible colour, so it is a pure function with a
    ///     test of its own rather than four shifts inside a platform call nothing off Windows can
    ///     run.
    /// </remarks>
    /// <param name="colorRef">The <c>COLORREF</c>.</param>
    /// <returns>The colour, alpha 1.</returns>
    internal static Color4 FromColorRef(uint colorRef) =>
        new(
            (colorRef & 0xFF) / 255f,
            ((colorRef >> 8) & 0xFF) / 255f,
            ((colorRef >> 16) & 0xFF) / 255f,
            1f
        );
}
