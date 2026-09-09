// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>The accent colour the user picked in their operating system's settings.</summary>
/// <param name="Color">
///     The accent itself, in <b>sRGB</b>, or <c>null</c> where the platform cannot say.
/// </param>
/// <param name="Text">
///     What the platform draws on top of the accent, in <b>sRGB</b>, or <c>null</c> where it has no
///     separate answer for that.
/// </param>
/// <remarks>
///     <para>
///         <b>One role rather than a palette, because one role is what every desktop can answer and
///         what the control theme already draws with.</b> <c>ControlTheme.vcss</c>'s <c>--accent</c>
///         is the focus ring, the switch, the spinner and the selection — thirty-one declarations in
///         one sheet — and until this type existed nothing anywhere read the operating system's
///         choice into it. The wider platform palette (AppKit's <c>labelColor</c> and the rest) is a
///         separate read with a separate destination; see <c>SystemPalette</c>.
///     </para>
///     <para>
///         ⚠ <b>sRGB, and that is a decision rather than an accident.</b> Every platform hands its
///         accent over in sRGB — <c>NSColor</c> resolved through <c>sRGBColorSpace</c>, DWM's
///         colourisation word, a GNOME hex string — while <c>SystemPalette</c> holds linear, and its
///         own remarks record that handing it an sRGB <see cref="Color4" /> makes a palette that is
///         visibly too bright with nothing anywhere reporting it. So the conversion happens once, in
///         <c>PlatformInput.ApplyAccent</c>, rather than in each platform's reader where the second
///         one to be written is the one that forgets.
///     </para>
///     <para>
///         ⚠ <b><c>null</c> is a real answer and is not "the user has no accent".</b> Every desktop
///         has an accent; what a platform can be missing is a way to read it. Reporting some default
///         blue would be indistinguishable at the destination from having read that blue off the
///         machine, and the destination has to be able to tell — a role nobody has supplied keeps
///         following <c>SystemPalette</c>'s own tables, and a role somebody has supplied survives
///         the next appearance change.
///     </para>
///     <para>
///         ⚠ <b><see cref="Text" /> is nullable separately, and a platform answering the accent
///         without it is the normal case.</b> macOS has <c>alternateSelectedControlTextColor</c>
///         and Windows has a text colour beside its accent; a hex string out of a GNOME setting has
///         nothing of the kind. Leaving it <c>null</c> keeps <c>AccentColorText</c> on the default
///         table, which is the pairing rule <see cref="SystemAccessibility" />'s neighbour
///         <c>SystemColor</c> is ordered to enforce: a background role and its text role are
///         guaranteed to contrast <i>within</i> a pair and guaranteed nothing across two.
///     </para>
/// </remarks>
public readonly record struct SystemAccent(Color4? Color = null, Color4? Text = null) {
    /// <summary>What a platform with no way to read an accent reports.</summary>
    /// <remarks>
    ///     Named rather than left as <c>default</c> so that "I have no source for this" reads as a
    ///     decision at its call site instead of as a field nobody filled in — the same reason
    ///     <see cref="SystemAccessibility.Unknown" /> is named.
    /// </remarks>
    public static SystemAccent Unknown => default;

    /// <summary>Whether the platform read an accent at all.</summary>
    public bool IsKnown => Color is not null;
}
