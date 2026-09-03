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
///     <para>
///         ⚠ <b>Four of CSS's five, and this used to be two.</b> <c>dashed</c> and <c>dotted</c>
///         were absent because there was no dash pattern anywhere in <c>Vixen.Ui</c> — the same
///         measurement <c>border-style</c>, <c>divide-&lt;style&gt;</c> and <c>outline-&lt;style&gt;</c>
///         were all recorded under, four families reading <c>partial</c> for one missing thing. There
///         is one now: <c>Dashes</c> distributes the marks and <c>DrawListBuilder</c> emits a
///         rectangle each.
///     </para>
///     <para>
///         ⚠ <b>A bar is the easy consumer of that pattern and it is worth saying why.</b> A
///         decoration is an axis-aligned rectangle with no corner radius, so breaking it up is
///         breaking up a length — no path, no stroke, no tessellation, and the software rasteriser
///         and the device draw the pieces because they are drawing the same quad they already drew.
///         A border's ring is the hard one, and it is hard for the corners rather than for the
///         pattern.
///     </para>
///     <para>
///         ⚠ <b><c>wavy</c> stays absent, and it is absent for a reason the dash pattern does not
///         touch.</b> A wave is a stroked path where every other decoration is a rectangle: it needs
///         the tessellator, a thickness that is a stroke width rather than a height, and an
///         amplitude and a period CSS does not state. A <c>decoration-wavy</c> that resolved and
///         painted a straight line is the inert family <c>UtilityConsumptionGateTests</c> exists to
///         keep out.
///     </para>
/// </remarks>
public enum TextDecorationStyle : byte {
    /// <summary>One bar. CSS's initial value.</summary>
    Solid,

    /// <summary>Two bars, separated by a gap of the same thickness.</summary>
    Double,

    /// <summary>One bar broken into marks three times the thickness, with gaps of twice it.</summary>
    Dashed,

    /// <summary>One bar broken into square marks one thickness long, with gaps of one.</summary>
    Dotted
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
