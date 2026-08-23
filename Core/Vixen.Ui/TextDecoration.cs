// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>Which lines <c>text-decoration-line</c> asks for.</summary>
/// <remarks>
///     Flags, because CSS takes a space-separated list and <c>underline overline</c> is two lines on
///     one run rather than a fifth value.
/// </remarks>
[Flags]
public enum TextDecorationLine : byte {
    /// <summary>None. CSS's initial value, and what <c>no-underline</c> writes.</summary>
    None = 0,

    /// <summary>A line below the baseline, at the position the face asks for.</summary>
    Underline = 1,

    /// <summary>A line at the top of the ascent.</summary>
    Overline = 2,

    /// <summary>A line across the glyphs, at the face's strikeout position.</summary>
    LineThrough = 4
}

/// <summary>How <c>text-decoration-style</c> draws that line.</summary>
/// <remarks>
///     ⚠ <b>Two of CSS's five, and the other three are absent rather than approximated.</b>
///     <c>dotted</c>, <c>dashed</c> and <c>wavy</c> need a stroke this engine cannot draw: there is
///     no dash pattern anywhere in <c>Vixen.Ui</c> — <c>border-style</c> is emitted by nothing and
///     read by nothing for exactly that reason, which is why <c>divide-dashed</c> is not a class
///     either. A <c>decoration-dashed</c> that resolved cleanly and painted a solid line is the
///     inert family <c>UtilityConsumptionGateTests</c> exists to keep out, and registering one to
///     round the table out would be the same mistake with a nicer name.
/// </remarks>
public enum TextDecorationStyle : byte {
    /// <summary>One bar. CSS's initial value.</summary>
    Solid,

    /// <summary>Two bars, separated by a gap of the same thickness.</summary>
    Double
}

/// <summary>An element's resolved <c>text-decoration</c>, ready to draw.</summary>
/// <remarks>
///     <para>
///         <b>Resolved except for the two <c>auto</c>s, which only a run can settle.</b> A thickness
///         and an underline position are the <i>face's</i> to state — see
///         <see cref="Text.DecorationMetrics" /> — and a line may be composed of runs in more than
///         one face, so the value that reaches here is either a length the author wrote or
///         <see cref="float.NaN" /> meaning "ask the font". NaN rather than zero, for
///         <c>TextRun.Leading</c>'s reason: zero is a thickness somebody might mean.
///     </para>
///     <para>
///         ⚠ <b><see cref="Color" /> is nullable and null means <c>currentColor</c>.</b> CSS's
///         initial value for <c>text-decoration-color</c> is <c>currentColor</c>, and resolving it to
///         the text colour here rather than at the point of drawing would mean an element that sets
///         only <c>text-decoration-line</c> carries a colour it never asked for — which the animator
///         would then happily interpolate away from.
///     </para>
/// </remarks>
/// <param name="Lines">Which lines to draw.</param>
/// <param name="Style">Whether each is one bar or two.</param>
/// <param name="Color">What colour, or null for the text's own.</param>
/// <param name="Thickness">How thick, in pixels, or NaN for the face's.</param>
/// <param name="Offset">How much further down the underline sits, in pixels. Zero for <c>auto</c>.</param>
/// <remarks>
///     ⚠ <b><c>default(TextDecoration)</c> is not "a decoration with the defaults" — it is no
///     decoration at all, and its <see cref="Thickness" /> is zero rather than NaN.</b> A record
///     struct's parameter defaults belong to its <i>constructor</i>; the zero-initialised value has
///     never run one. That is harmless because <see cref="Lines" /> is then
///     <see cref="TextDecorationLine.None" /> and <see cref="IsNone" /> catches it first — but it is
///     why <c>TextRun.Bar</c> takes its decoration as a required argument rather than an optional
///     one. An optional there would read as "ask the font" and would silently mean "draw nothing".
/// </remarks>
public readonly record struct TextDecoration(
    TextDecorationLine Lines,
    TextDecorationStyle Style = TextDecorationStyle.Solid,
    Color4? Color = null,
    float Thickness = float.NaN,
    float Offset = 0f
) {
    /// <summary>Nothing to draw. The overwhelmingly common case, and the one that must cost nothing.</summary>
    public bool IsNone => Lines == TextDecorationLine.None;
}

/// <summary>One decoration bar, placed against a baseline.</summary>
/// <param name="Top">How far below the baseline its top edge sits. Negative is above.</param>
/// <param name="Thickness">How tall it is. Always positive.</param>
public readonly record struct DecorationBar(float Top, float Thickness);
