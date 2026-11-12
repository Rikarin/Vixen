// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Curves;
using Vixen.Core.Mathematics;

namespace Vixen.TextureGraph;

/// <summary>
///     The one-row tables <c>Curve</c> and <c>GradientMap</c> read: a spline or a colour ramp,
///     sampled on the CPU by the editor's own evaluator and handed to the plan as an image.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This is what stops a second opinion about what a curve or a gradient means, and doc
///             48 § D3's ban is not what it is dodging.
///         </b>
///         That rule forbids a CPU <em>twin</em> of a
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
///     <para>
///         <b>A ramp or a curve can be written in the setting that would otherwise name an asset</b>,
///         which is <see cref="FromText" />, and a compilation reads it. A host with no gradient asset
///         to point at gets a table that is in the document, so the file is all a second machine or a
///         second host needs: the two Patina processes that bake a graph and <c>vixen texture bake</c>
///         mean the same by it because none of them is asked to resolve it. Stops are mixed in the
///         values they were written in, and no colour space is chosen.
///     </para>
/// </remarks>
public static class TextureRamp {
    /// <summary>How many entries a table holds, which is its width in texels.</summary>
    /// <remarks>
    ///     256 because the input it indexes is very often an 8-bit image, and a table shorter than
    ///     the input's own resolution is a staircase the interpolation cannot hide.
    /// </remarks>
    public const int Entries = 256;

    /// <summary>What starts a ramp written inline: <c>ramp:0 000000ff,1 ffffffff</c>.</summary>
    public const string RampScheme = "ramp:";

    /// <summary>What starts a curve written inline: <c>curve:r=0 0,1 1;g=0 0,0.5 1,1 0</c>.</summary>
    public const string CurveScheme = "curve:";

    const int Channels = 4;
    const int HexDigits = 8;
    const float ByteMaximum = 255f;

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
            pixels[entry * 4 + 1] = Quantise(color.G);
            pixels[entry * 4 + 2] = Quantise(color.B);
            pixels[entry * 4 + 3] = Quantise(color.A);
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
            pixels[entry * 4 + 1] = Quantise(CurveEvaluation.Evaluate(green, at));
            pixels[entry * 4 + 2] = Quantise(CurveEvaluation.Evaluate(blue, at));
            pixels[entry * 4 + 3] = Quantise(CurveEvaluation.Evaluate(alpha, at));
        }

        return pixels;
    }

    /// <summary>The straight line from zero to one, as two keys.</summary>
    /// <returns>The keys a channel left alone carries.</returns>
    /// <remarks>
    ///     ⚠ <b>Not a convenience.</b> A curve node with one channel curved and three left alone is
    ///     the ordinary case, and the three left alone have to be an identity
    ///     <em>
    ///         through the same
    ///         evaluator
    ///     </em>
    ///     — an omitted channel baked as zero is a curve node that silently drops
    ///     colour, which is the failure this returns instead of inviting.
    /// </remarks>
    public static CurveSample[] Straight() => [
        new(0f, 0f, 1f, 1f, TangentMode.Linear),
        new(1f, 1f, 1f, 1f, TangentMode.Linear)
    ];

    /// <summary>Bakes a ramp or a curve written inline.</summary>
    /// <param name="reference">
    ///     What a <c>Ramp</c> or <c>Curve</c> setting holds. A ramp lists stops as a position and eight
    ///     hex digits of RGBA. A curve lists keys per channel, as a time and a value, with automatic
    ///     tangents; a channel left out is <see cref="Straight" />.
    /// </param>
    /// <returns>
    ///     <see cref="Entries" />×1 texels, tightly packed, RGBA, or <see langword="null" /> when the
    ///     reference names something else, which a host resolves.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is a ramp or a curve, written wrongly.</exception>
    public static byte[]? FromText(string reference) {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.StartsWith(RampScheme, StringComparison.Ordinal)) {
            var stops = ParseStops(reference[RampScheme.Length..]);

            return FromRamp(position => Mix(stops, position));
        }

        if (reference.StartsWith(CurveScheme, StringComparison.Ordinal)) {
            var channels = ParseChannels(reference[CurveScheme.Length..]);
            var straight = Straight();

            return FromCurves(
                channels.GetValueOrDefault("r", straight),
                channels.GetValueOrDefault("g", straight),
                channels.GetValueOrDefault("b", straight),
                channels.GetValueOrDefault("a", straight)
            );
        }

        return null;
    }

    /// <summary>A value in zero to one as a byte, clamped.</summary>
    static byte Quantise(float value) => (byte)Math.Clamp((int)MathF.Round(value * ByteMaximum), 0, (int)ByteMaximum);

    /// <summary>A ramp at one position, mixing the neighbouring stops in the values they were written in.</summary>
    static Color4 Mix(List<(float Position, Color4 Colour)> stops, float position) {
        if (position <= stops[0].Position) {
            return stops[0].Colour;
        }

        for (var i = 1; i < stops.Count; i++) {
            var (end, to) = stops[i];

            if (position <= end) {
                var (start, from) = stops[i - 1];
                var t = end > start ? (position - start) / (end - start) : 1f;

                return new(
                    from.R + (to.R - from.R) * t,
                    from.G + (to.G - from.G) * t,
                    from.B + (to.B - from.B) * t,
                    from.A + (to.A - from.A) * t
                );
            }
        }

        return stops[^1].Colour;
    }

    static List<(float Position, Color4 Colour)> ParseStops(string text) {
        List<(float Position, Color4 Colour)> stops = [];

        foreach (var stop in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            var parts = stop.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2 || parts[1].Length != HexDigits) {
                throw new InvalidDataException($"a ramp stop is a position and eight hex digits, not '{stop}'");
            }

            stops.Add((ParseNumber(parts[0]), ParseColour(parts[1])));
        }

        return stops.Count > 0
            ? [..stops.OrderBy(static stop => stop.Position)]
            : throw new InvalidDataException("a ramp needs a stop");
    }

    static Color4 ParseColour(string hex) {
        var lanes = new float[Channels];

        for (var i = 0; i < Channels; i++) {
            if (!byte.TryParse(
                    hex.AsSpan(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var lane
                )) {
                throw new InvalidDataException($"'{hex}' is not hex");
            }

            lanes[i] = lane / ByteMaximum;
        }

        return new(lanes[0], lanes[1], lanes[2], lanes[3]);
    }

    static Dictionary<string, CurveSample[]> ParseChannels(string text) {
        Dictionary<string, CurveSample[]> channels = new(StringComparer.Ordinal);

        foreach (var channel in text.Split(
                     ';',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                 )) {
            var split = channel.IndexOf('=', StringComparison.Ordinal);

            if (split < 1) {
                throw new InvalidDataException($"a curve channel is a name and its keys, not '{channel}'");
            }

            channels[channel[..split]] = ParseKeys(channel[(split + 1)..]);
        }

        return channels;
    }

    static CurveSample[] ParseKeys(string text) {
        List<CurveSample> keys = [];

        foreach (var key in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            var parts = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2) {
                throw new InvalidDataException($"a curve key is two numbers, not '{key}'");
            }

            keys.Add(new(ParseNumber(parts[0]), ParseNumber(parts[1]), 0f, 0f, TangentMode.Auto));
        }

        return keys.Count > 0
            ? [..keys.OrderBy(static key => key.Time)]
            : throw new InvalidDataException("a curve channel needs a key");
    }

    static float ParseNumber(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
            ? number
            : throw new InvalidDataException($"'{text}' is not a number");
}
