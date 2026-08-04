// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Curves;

namespace Vixen.Ai;

/// <summary>The shape a consideration's input is put through.</summary>
/// <remarks>
///     doc 37 § D8's six, and the list is closed for the same reason the blackboard's six types are:
///     an editor draws a dropdown over it, a file names one of them, and a seventh that only one game
///     wants is a <see cref="DelegateResponseCurve" /> rather than an entry here.
/// </remarks>
public enum ResponseCurveKind : byte {
    /// <summary><c>m(x − c) + b</c>. "More is better", proportionally.</summary>
    Linear,

    /// <summary><c>m(x − c)^k + b</c>. <c>k &gt; 1</c> rises late, <c>k &lt; 1</c> rises early.</summary>
    Polynomial,

    /// <summary><c>m / (1 + e^(−k(x − c))) + b</c>. A threshold: "urgent below half health".</summary>
    Logistic,

    /// <summary>The inverse of the logistic. Diminishing returns.</summary>
    Logit,

    /// <summary><c>m·e^(−(x − c)² / 2k²) + b</c>. A sweet spot: "ten metres is the right range".</summary>
    Gaussian,

    /// <summary>Authored keys, sampled. For the shapes no formula has.</summary>
    Sampled
}

/// <summary>Input in, score out.</summary>
/// <remarks>
///     doc 37 § Part 4's seam. Two implementations ship and they differ in shape rather than in
///     numbers: <see cref="ResponseCurve" /> is four parameters a file can hold and an editor can
///     draw, and <see cref="DelegateResponseCurve" /> is a lambda for the shape that is a piece of
///     game logic rather than a curve.
/// </remarks>
public interface IResponseCurve {
    /// <summary>Puts an input through the shape.</summary>
    /// <param name="x">The normalised input, in <c>[0,1]</c>.</param>
    /// <returns>The score, in <c>[0,1]</c>.</returns>
    float Evaluate(float x);
}

/// <summary>One input, one shape, four parameters.</summary>
/// <remarks>
///     <para>
///         The Infinite Axis shape, and it is small enough to state completely: <c>m</c> is slope,
///         <c>k</c> is exponent or width, <c>b</c> is vertical shift and <c>c</c> is horizontal shift.
///         Every curve in doc 37 § D8's table is those four at different values.
///     </para>
///     <para>
///         ⚠ <b>The result is clamped to <c>[0,1]</c>, and that is what makes the zero rule mean
///         something.</b> A curve that returned 1.4 would let one consideration outvote a veto, and a
///         negative one would flip the sign of a geometric mean — so the clamp is part of the
///         contract rather than defensive arithmetic.
///     </para>
///     <para>
///         ⚠ <b>A record class rather than a record struct.</b> A record struct's <c>new()</c> is its
///         <i>zero</i> value, so a curve built with an object initialiser would get a slope of zero —
///         a consideration that always scores <c>b</c>. The same trap cost P2 a working layout and P3
///         very nearly cost a sight radius.
///     </para>
/// </remarks>
public sealed record ResponseCurve : IResponseCurve {
    /// <summary>Which shape.</summary>
    public ResponseCurveKind Kind { get; init; } = ResponseCurveKind.Linear;

    /// <summary>Slope, or height for the bell.</summary>
    public float Slope { get; init; } = 1f;

    /// <summary>Exponent, steepness, or width — whichever the shape has.</summary>
    public float Exponent { get; init; } = 1f;

    /// <summary>Vertical shift.</summary>
    public float Shift { get; init; }

    /// <summary>Horizontal shift.</summary>
    public float Centre { get; init; }

    /// <summary>The keys, for <see cref="ResponseCurveKind.Sampled" />. Assumed to be in time order.</summary>
    public CurveSample[]? Keys { get; init; }

    /// <summary>Straight through: the input is already the score.</summary>
    public static ResponseCurve Identity { get; } = new();

    /// <summary>A constant. What a consideration that is only there for its weight uses.</summary>
    /// <param name="value">The score.</param>
    /// <returns>The curve.</returns>
    public static ResponseCurve Constant(float value) => new() { Slope = 0f, Shift = value };

    /// <summary>A threshold at a point, rising or falling.</summary>
    /// <param name="centre">Where it turns over.</param>
    /// <param name="steepness">How sharply. Negative falls instead of rising.</param>
    /// <returns>The curve.</returns>
    public static ResponseCurve Threshold(float centre, float steepness = 12f) => new() {
        Kind = ResponseCurveKind.Logistic,
        Exponent = steepness,
        Centre = centre
    };

    /// <summary>A bell around a point.</summary>
    /// <param name="centre">Where it peaks.</param>
    /// <param name="width">Its standard deviation.</param>
    /// <returns>The curve.</returns>
    public static ResponseCurve Bell(float centre, float width = 0.15f) => new() {
        Kind = ResponseCurveKind.Gaussian,
        Exponent = width,
        Centre = centre
    };

    /// <inheritdoc />
    public float Evaluate(float x) {
        var input = Math.Clamp(x, 0f, 1f);

        var value = Kind switch {
            ResponseCurveKind.Linear => (Slope * (input - Centre)) + Shift,
            ResponseCurveKind.Polynomial => (Slope * MathF.Pow(MathF.Max(0f, input - Centre), Exponent)) + Shift,
            ResponseCurveKind.Logistic => (Slope / (1f + MathF.Exp(-Exponent * (input - Centre)))) + Shift,
            ResponseCurveKind.Logit => Logit(input),
            ResponseCurveKind.Gaussian => Gaussian(input),
            ResponseCurveKind.Sampled => Keys is { Length: > 0 } keys ? CurveEvaluation.Evaluate(keys, input) : input,
            _ => input
        };

        // NaN is not a score. A logit at exactly zero or one, a polynomial with a fractional exponent
        // over a negative base, a Gaussian with a zero width — all reachable from an editor, and all
        // of them would poison a geometric mean rather than produce a wrong-looking agent.
        return float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
    }

    float Logit(float input) {
        // Pinched away from the asymptotes rather than clamped after the fact, because ln(0) is −∞ and
        // the clamp above would turn the whole curve into a step at the ends.
        var pinched = Math.Clamp(input - Centre, 1e-4f, 1f - 1e-4f);

        return (Slope * (MathF.Log(pinched / (1f - pinched)) / (2f * MathF.Max(1e-3f, Exponent)))) + 0.5f + Shift;
    }

    float Gaussian(float input) {
        var width = MathF.Max(1e-3f, Exponent);
        var offset = input - Centre;

        return (Slope * MathF.Exp(-(offset * offset) / (2f * width * width))) + Shift;
    }
}

/// <summary>A curve that is a lambda.</summary>
/// <param name="shape">What it does.</param>
/// <remarks>
///     The second implementation of the seam, and it differs in shape rather than in numbers: it has
///     no parameters at all, so nothing can draw it and nothing can serialise it. That is the trade —
///     a game whose "how good is this" is a lookup into its own tables writes it here, and gives up
///     the editor's preview for it.
/// </remarks>
public sealed class DelegateResponseCurve(Func<float, float> shape) : IResponseCurve {
    readonly Func<float, float> shape = shape ?? throw new ArgumentNullException(nameof(shape));

    /// <inheritdoc />
    public float Evaluate(float x) {
        var value = shape(Math.Clamp(x, 0f, 1f));

        return float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
    }
}
