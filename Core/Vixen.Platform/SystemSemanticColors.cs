// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>The operating system's own semantic palette, as much of it as the platform can read.</summary>
/// <param name="Canvas">The background of application content — CSS <c>Canvas</c>.</param>
/// <param name="CanvasText">The text on it — CSS <c>CanvasText</c>; AppKit's <c>labelColor</c>.</param>
/// <param name="LinkText">The colour of a link — CSS <c>LinkText</c>.</param>
/// <param name="ButtonFace">The face of a push button — CSS <c>ButtonFace</c>.</param>
/// <param name="ButtonText">The text on a button — CSS <c>ButtonText</c>.</param>
/// <param name="ButtonBorder">The border of a button — CSS <c>ButtonBorder</c>; AppKit's <c>separatorColor</c>.</param>
/// <param name="Field">The background of an input field — CSS <c>Field</c>.</param>
/// <param name="FieldText">The text in it — CSS <c>FieldText</c>.</param>
/// <param name="Highlight">The background of a selected item — CSS <c>Highlight</c>.</param>
/// <param name="HighlightText">The text on a selected item — CSS <c>HighlightText</c>.</param>
/// <param name="GrayText">Disabled or secondary text — CSS <c>GrayText</c>.</param>
/// <remarks>
///     <para>
///         <b>The platform half of <c>SystemPalette</c>'s dynamic roles, named by the CSS system
///         colour each fills</b> — so that the mapping from a platform's vocabulary onto the sheet's
///         is made once, in the platform's reader, where the person writing it can see both names.
///         <c>PlatformInput.ApplySemanticColors</c> writes every role that is not <c>null</c> with
///         <c>SystemPalette.SetPlatform</c>, which survives the appearance and contrast changes that
///         <c>Reset</c> the default tables underneath.
///     </para>
///     <para>
///         ⚠ <b>Every role is nullable separately, and a partial read is the normal case rather than
///         a failure.</b> A desktop has a word for some of these and not others — Windows has no
///         mark colour, GNOME has no separator — and a role nobody supplied keeps following
///         <c>SystemPalette</c>'s own light, dark or high-contrast table, which is a browser's
///         answer and a reasonable one. What a platform must never do is invent a value for a role
///         it could not read: at the destination that is indistinguishable from having read it, and
///         it would stop following the appearance the way the table does.
///     </para>
///     <para>
///         ⚠ <b>sRGB, like <see cref="SystemAccent" /> and for the same reason.</b> Every platform
///         hands its colours over in sRGB — <c>NSColor</c> through <c>sRGBColorSpace</c>, a
///         <c>COLORREF</c> — while <c>SystemPalette</c> holds linear and its own remarks record that
///         handing it sRGB makes a palette that is visibly too bright with nothing reporting it. The
///         conversion happens once, at the <c>PlatformInput</c> boundary, rather than in each reader
///         where the second one written is the one that forgets.
///     </para>
///     <para>
///         ⚠ <b>Not <c>Mark</c>, <c>MarkText</c>, <c>AccentColor</c> or <c>AccentColorText</c>.</b>
///         The first two no desktop names. The accent pair is <see cref="SystemAccent" />, read on
///         its own terms because it drives a theme token and a class the sheet keys on, and a second
///         path to the same role would be two writers of one cell.
///     </para>
/// </remarks>
public readonly record struct SystemSemanticColors(
    Color4? Canvas = null,
    Color4? CanvasText = null,
    Color4? LinkText = null,
    Color4? ButtonFace = null,
    Color4? ButtonText = null,
    Color4? ButtonBorder = null,
    Color4? Field = null,
    Color4? FieldText = null,
    Color4? Highlight = null,
    Color4? HighlightText = null,
    Color4? GrayText = null
) {
    /// <summary>What a platform with no way to read its semantic palette reports.</summary>
    /// <remarks>
    ///     Named rather than left as <c>default</c> so that "I have no source for this" reads as a
    ///     decision at its call site instead of as a struct nobody filled in — the same reason
    ///     <see cref="SystemAccent.Unknown" /> is named.
    /// </remarks>
    public static SystemSemanticColors Unknown => default;

    /// <summary>Whether the platform read any role at all.</summary>
    public bool IsKnown =>
        Canvas is not null
        || CanvasText is not null
        || LinkText is not null
        || ButtonFace is not null
        || ButtonText is not null
        || ButtonBorder is not null
        || Field is not null
        || FieldText is not null
        || Highlight is not null
        || HighlightText is not null
        || GrayText is not null;
}
