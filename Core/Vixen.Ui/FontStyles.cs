// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>Upright, italic, or the slanted-roman compromise.</summary>
/// <remarks>
///     ⚠ <b><see cref="Oblique" /> is a distinct value and not a synonym for <see cref="Italic" />.</b>
///     An italic is a differently drawn face — a single-storey <i>a</i>, cursive joins — and an
///     oblique is the upright one sheared. CSS lets a stylesheet ask for either and lets a family
///     supply either, so the matching rules below have to know they are two things that substitute
///     for each other asymmetrically: asking for oblique and getting italic is better than the
///     reverse, because a family that ships an italic meant it.
/// </remarks>
public enum FontStyle : byte {
    /// <summary>Upright.</summary>
    Normal,

    /// <summary>A separately drawn cursive face.</summary>
    Italic,

    /// <summary>The upright face, sheared.</summary>
    Oblique
}

/// <summary>How wide the face is drawn, as CSS's nine steps.</summary>
/// <remarks>
///     The numbers are the percentages CSS gives them, so a comparison is arithmetic rather than a
///     table — and <c>font-stretch</c> also accepts a raw percentage, which lands between two of these
///     without needing a new case.
/// </remarks>
public enum FontStretch : byte {
    /// <summary>50%.</summary>
    UltraCondensed = 50,

    /// <summary>62.5%.</summary>
    ExtraCondensed = 62,

    /// <summary>75%.</summary>
    Condensed = 75,

    /// <summary>87.5%.</summary>
    SemiCondensed = 87,

    /// <summary>100%.</summary>
    Normal = 100,

    /// <summary>112.5%.</summary>
    SemiExpanded = 112,

    /// <summary>125%.</summary>
    Expanded = 125,

    /// <summary>150%.</summary>
    ExtraExpanded = 150,

    /// <summary>200%.</summary>
    UltraExpanded = 200
}

/// <summary>What a stylesheet asked a family for.</summary>
/// <param name="Weight">100–900, where 400 is regular and 700 is bold.</param>
/// <param name="Style">Upright, italic or oblique.</param>
/// <param name="Stretch">The width.</param>
public readonly record struct FontQuery(int Weight = 400, FontStyle Style = FontStyle.Normal, FontStretch Stretch = FontStretch.Normal) {
    /// <summary>What an element with no font declarations asks for.</summary>
    /// <remarks>
    ///     Identical to <c>default</c> by construction — 400, normal, normal are CSS's initial values
    ///     and the record's own defaults — and named anyway, because <c>Resolve(css, default)</c> reads
    ///     as "no opinion" and this reads as "the initial values", which is what it means.
    /// </remarks>
    public static FontQuery Default => new();
}
