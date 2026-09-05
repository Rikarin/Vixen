// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The font metrics CSS 2.1 §10.8's <i>strut</i> is made of, in the container's own units.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This store still has no font, and that is the point of this type rather than an
///         exception to it.</b> §10.8 begins every line box with an imaginary zero-width inline box
///         carrying the block container's own font and line height — which is why an empty line is
///         still a line tall, why a short image never makes a line shorter than the text beside it
///         would be, and why <c>line-height</c> on a container does anything at all. Every one of
///         those is <i>arithmetic over five numbers</i>; only producing the five needs a font. So
///         the numbers are a computed value the layer that owns the fonts writes down, exactly as
///         it already writes down a resolved <c>font-size</c>, and <c>Vixen.Ui.Layout</c> stays
///         geometry.
///     </para>
///     <para>
///         ⚠ <b>All-zero means "no strut", and that is the initial value.</b> A tree that never sets
///         one lays out exactly as it did before this type existed: a line is as tall as the boxes
///         on it, and the five font-relative <see cref="VerticalAlign" /> values fall back to
///         <see cref="VerticalAlign.Baseline" /> — see <see cref="LayoutTree" />'s
///         <c>EffectiveVerticalAlign</c>, which is where that fallback is decided. Refusing them
///         where there is no strut is the same refusal as before and for the same reason: a
///         <c>middle</c> rounded to <c>baseline</c> is half an x-height out and reads as a rendering
///         quirk rather than as a missing feature.
///     </para>
///     <para>
///         ⚠ <b><see cref="Ascent" />/<see cref="Descent" /> and
///         <see cref="TextAscent" />/<see cref="TextDescent" /> are two different boxes and
///         collapsing them is wrong the moment <c>line-height</c> is not <c>normal</c>.</b> The
///         first pair is the strut's <i>line box</i> — the font's metrics grown (or shrunk) by half
///         the leading — and it is what a line box is at least as tall as. The second is the font's
///         <i>content area</i>, which is what <c>text-top</c> and <c>text-bottom</c> are defined
///         against. With <c>line-height: 2</c> they differ by half a font size on each side.
///     </para>
/// </remarks>
/// <param name="Ascent">How far the strut's line box reaches above the baseline. Positive.</param>
/// <param name="Descent">How far it reaches below. Positive, unlike the font table's sign.</param>
/// <param name="TextAscent">The top of the font's content area, above the baseline. Positive.</param>
/// <param name="TextDescent">The bottom of it, below the baseline. Positive.</param>
/// <param name="XHeight">The font's x-height, which <c>vertical-align: middle</c> takes half of.</param>
/// <param name="SubOffset">How far below the baseline <c>vertical-align: sub</c> lowers a box.</param>
/// <param name="SuperOffset">How far above it <c>vertical-align: super</c> raises one.</param>
public readonly record struct StrutMetrics(
    float Ascent,
    float Descent,
    float TextAscent,
    float TextDescent,
    float XHeight,
    float SubOffset,
    float SuperOffset
) {
    /// <summary>Whether a font ever wrote these numbers down.</summary>
    /// <remarks>
    ///     ⚠ <b>The two <c>vertical-align</c> families ask this question differently, which is why it
    ///     is a property rather than a branch at one call site.</b> A line box asks it to decide
    ///     whether to start at the strut's height or at zero; the five font-relative alignments ask
    ///     it to decide whether they are honoured at all. A tree whose container declares a
    ///     <c>line-height</c> and nothing else still answers <c>true</c> here, because a line height
    ///     with no font behind it is a perfectly good strut of exactly that height.
    /// </remarks>
    public bool HasFont => this != default;
}
