// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Curves;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.TextureGraph;

/// <summary>
///     The one-row tables <c>Curve</c> and <c>GradientMap</c> read: a spline or a colour ramp,
///     sampled on the CPU by the editor's own evaluator and handed to the plan as an image.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is what stops a second opinion about what a curve or a gradient means, and doc
///         48 § D3's ban is not what it is dodging.</b> That rule forbids a CPU <em>twin</em> of a
///         kernel — two transcriptions of one operation, whose parity test proves only that somebody
///         copied carefully. This is the opposite arrangement: there is exactly one implementation
///         of a Hermite spline in this repository and exactly one of a gradient's stop list, and
///         both are already somewhere else. Baking a table out of them means a kernel that reads a
///         table can never disagree with the control an artist dragged.
///     </para>
///     <para>
///         <b>The two representations, established rather than invented.</b>
///         <c>Core/Vixen.Core/Curves/CurveEvaluation.cs</c> holds a curve as
///         <see cref="CurveSample" />s — a time, a value and two <em>slopes</em> — with
///         <c>AnimationCurve</c> (the control <c>CurveEditor</c> edits) and the animation bake as its
///         two existing callers, and its own remark says why it is a static function over a span:
///         both callers project their own key type into it. This is a third caller doing exactly
///         that. <c>Vixen.Ui.Controls.Advanced</c>'s <c>Gradient</c> holds a ramp as two lists of
///         stops and decides which of three spaces they are mixed in; <see cref="FromRamp" /> takes
///         its <c>Evaluate</c> as a delegate, which is also what keeps this assembly from
///         referencing a UI control.
///     </para>
///     <para>
///         ⚠ <b>Eight bits per entry, which is a claim worth checking rather than assuming.</b> The
///         table is quantised and the kernel interpolates between entries, so a curve is
///         reconstructed to within about half a step of an 8-bit output — and for an input that is
///         already 8-bit the identity table is <em>exact</em>, because entry <c>k</c> holds
///         <c>k</c> and the interpolation weight is zero. A 16-bit table would be the change to make
///         if a height field ever came through a curve; it is a format on this file and nothing else
///         would move.
///     </para>
/// </remarks>
static class TextureRamp {
    /// <summary>How many entries a table holds, which is its width in texels.</summary>
    /// <remarks>
    ///     256 because the input it indexes is very often an 8-bit image, and a table shorter than
    ///     the input's own resolution is a staircase the interpolation cannot hide.
    /// </remarks>
    public const int Entries = 256;

    /// <summary>Bakes a colour ramp into a row of RGBA8 texels.</summary>
    /// <param name="evaluate">
    ///     The ramp, as a function of position from zero to one.
    ///     <c>Vixen.Ui.Controls.Advanced.Gradient.Evaluate</c> has exactly this signature and is
    ///     passed directly.
    /// </param>
    /// <returns><see cref="Entries" />×1 texels, tightly packed, RGBA.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="evaluate" /> is null.</exception>
    public static byte[] FromRamp(Func<float, Color4> evaluate) {
        ArgumentNullException.ThrowIfNull(evaluate);

        var pixels = new byte[Entries * 4];

        for (var entry = 0; entry < Entries; entry++) {
            var color = evaluate(entry / (float)(Entries - 1));

            pixels[entry * 4] = Quantise(color.R);
            pixels[(entry * 4) + 1] = Quantise(color.G);
            pixels[(entry * 4) + 2] = Quantise(color.B);
            pixels[(entry * 4) + 3] = Quantise(color.A);
        }

        return pixels;
    }

    /// <summary>Bakes four per-channel curves into one row, a curve to a lane.</summary>
    /// <param name="red">The red channel's keys, in time order.</param>
    /// <param name="green">The green channel's.</param>
    /// <param name="blue">The blue channel's.</param>
    /// <param name="alpha">The alpha channel's.</param>
    /// <returns><see cref="Entries" />×1 texels, tightly packed, RGBA.</returns>
    /// <remarks>
    ///     One row rather than four, so the kernel takes one <c>Load</c> per interpolation end
    ///     instead of four — and because four curves an artist draws are four independent functions
    ///     of one input, which is what a per-channel curve is.
    /// </remarks>
    public static byte[] FromCurves(
        ReadOnlySpan<CurveSample> red,
        ReadOnlySpan<CurveSample> green,
        ReadOnlySpan<CurveSample> blue,
        ReadOnlySpan<CurveSample> alpha
    ) {
        var pixels = new byte[Entries * 4];

        for (var entry = 0; entry < Entries; entry++) {
            var at = entry / (float)(Entries - 1);

            pixels[entry * 4] = Quantise(CurveEvaluation.Evaluate(red, at));
            pixels[(entry * 4) + 1] = Quantise(CurveEvaluation.Evaluate(green, at));
            pixels[(entry * 4) + 2] = Quantise(CurveEvaluation.Evaluate(blue, at));
            pixels[(entry * 4) + 3] = Quantise(CurveEvaluation.Evaluate(alpha, at));
        }

        return pixels;
    }

    /// <summary>The straight line from zero to one, as two keys.</summary>
    /// <returns>The keys a channel left alone carries.</returns>
    /// <remarks>
    ///     ⚠ <b>Not a convenience.</b> A curve node with one channel curved and three left alone is
    ///     the ordinary case, and the three left alone have to be an identity <em>through the same
    ///     evaluator</em> — an omitted channel baked as zero is a curve node that silently drops
    ///     colour, which is the failure this returns instead of inviting.
    /// </remarks>
    public static CurveSample[] Straight() => [
        new(0f, 0f, 1f, 1f, TangentMode.Linear),
        new(1f, 1f, 1f, 1f, TangentMode.Linear)
    ];

    /// <summary>A value in zero to one as a byte, clamped.</summary>
    static byte Quantise(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
