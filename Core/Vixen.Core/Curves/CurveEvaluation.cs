// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Curves;

/// <summary>How a key's two tangents behave when it is moved.</summary>
public enum TangentMode : byte {
    /// <summary>Worked out from the neighbours, so the curve stays smooth without being aimed.</summary>
    Auto,

    /// <summary>Whatever the user dragged them to, kept in line with each other.</summary>
    Free,

    /// <summary>Ditto, but the two sides move independently — a corner.</summary>
    Broken,

    /// <summary>Straight lines to the neighbours.</summary>
    Linear,

    /// <summary>The value holds until the next key and then jumps. A step.</summary>
    Constant
}

/// <summary>One key, as the evaluator reads it.</summary>
/// <param name="Time">When.</param>
/// <param name="Value">What.</param>
/// <param name="InTangent">The slope coming in, in value per unit of time.</param>
/// <param name="OutTangent">The slope going out.</param>
/// <param name="Mode">How the two behave.</param>
/// <remarks>
///     ⚠ <b>Tangents are slopes, not control points.</b> A slope survives the key being dragged in
///     time — the shape either side stays what it was — where a control point stored as a position
///     has to be moved with it, and every implementation that stores positions eventually forgets to
///     in one of the six places a key can move.
/// </remarks>
public readonly record struct CurveSample(
    float Time,
    float Value,
    float InTangent,
    float OutTangent,
    TangentMode Mode
);

/// <summary>Sampling a keyed curve. The one implementation of the tangent convention.</summary>
/// <remarks>
///     <para>
///         <b>Cubic Hermite between neighbours</b>, which is what every animation system in this
///         shape uses and what makes a tangent mean what an artist expects: the slope at the key.
///         Bézier control points are the same curve wearing different numbers, and the conversion is
///         a third of the interval — but slopes are what survive a key being dragged sideways.
///     </para>
///     <para>
///         ⚠ <b>Outside the first and last key the curve holds rather than extrapolating.</b> A
///         cubic extrapolated past its last key runs off to infinity within a second or two, and an
///         animation that was sampled one frame past its end would send whatever it drives into the
///         next county. Clamping is the boring answer and the right one.
///     </para>
///     <para>
///         <b>Why this is here and not beside either of its callers.</b> Two things sample these
///         curves: the editor's <c>AnimationCurve</c> control, which is mutable and raises events
///         because a person is dragging its keys, and the bake that turns an authored clip into the
///         sampled channels a runtime plays. They cannot share a key <i>type</i> — one is a class
///         with setters and the other is a serialised record — but they must share the arithmetic,
///         because a curve that reads one way in the editor and another in the build is a bug nobody
///         can see until it ships. So the shape is a static function over a span, and both callers
///         project their own keys into it.
///     </para>
///     <para>
///         ⚠ <b>Keys are assumed to be in time order.</b> Both callers sort on edit rather than on
///         read — an editor because it re-sorts when a key is dragged past its neighbour, a bake
///         because the times came out of a <c>SortedSet</c>. Sorting here would turn every sample
///         into an allocation and hide the caller that forgot.
///     </para>
/// </remarks>
public static class CurveEvaluation {
    /// <summary>The value at a time.</summary>
    /// <param name="keys">The keys, in time order.</param>
    /// <param name="time">When.</param>
    /// <returns>The value, or zero when there are no keys.</returns>
    public static float Evaluate(ReadOnlySpan<CurveSample> keys, float time) {
        if (keys.Length == 0) {
            return 0f;
        }

        if (keys.Length == 1 || time <= keys[0].Time) {
            return keys[0].Value;
        }

        if (time >= keys[^1].Time) {
            return keys[^1].Value;
        }

        var index = 0;

        while (index < keys.Length - 2 && keys[index + 1].Time <= time) {
            index++;
        }

        var from = keys[index];
        var to = keys[index + 1];
        var span = to.Time - from.Time;

        if (span <= 0f) {
            return to.Value;
        }

        if (from.Mode == TangentMode.Constant) {
            return from.Value;
        }

        var t = (time - from.Time) / span;

        if (from.Mode == TangentMode.Linear && to.Mode == TangentMode.Linear) {
            return from.Value + ((to.Value - from.Value) * t);
        }

        var (outgoing, incoming) = Slopes(keys, index);

        // Hermite: h00 p0 + h10 m0 + h01 p1 + h11 m1, with the tangents scaled by the interval
        // because they are slopes in the curve's own units rather than in t.
        var t2 = t * t;
        var t3 = t2 * t;

        return (((2f * t3) - (3f * t2) + 1f) * from.Value)
            + ((t3 - (2f * t2) + t) * outgoing * span)
            + (((-2f * t3) + (3f * t2)) * to.Value)
            + ((t3 - t2) * incoming * span);
    }

    /// <summary>The two slopes that actually govern a segment, after the modes have had their say.</summary>
    /// <param name="keys">The keys, in time order.</param>
    /// <param name="index">The segment's first key.</param>
    /// <returns>The outgoing slope of the first and the incoming slope of the second.</returns>
    public static (float Outgoing, float Incoming) Slopes(ReadOnlySpan<CurveSample> keys, int index) {
        var from = keys[index];
        var to = keys[index + 1];

        return (
            from.Mode switch {
                TangentMode.Auto => AutoSlope(keys, index),
                TangentMode.Linear => Straight(from, to),
                _ => from.OutTangent
            },
            to.Mode switch {
                TangentMode.Auto => AutoSlope(keys, index + 1),
                TangentMode.Linear => Straight(from, to),
                _ => to.InTangent
            }
        );
    }

    /// <summary>The slope an automatic key takes: the average of the two chords either side of it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the chord to the far neighbour.</b> Averaging the two makes a key at the top of a
    ///     hump take a slope of zero, which is what "smooth" means to anybody drawing one — and the
    ///     far-neighbour version makes the curve overshoot past every local extreme.
    /// </remarks>
    static float AutoSlope(ReadOnlySpan<CurveSample> keys, int index) {
        var key = keys[index];

        if (keys.Length < 2) {
            return 0f;
        }

        if (index == 0) {
            return Straight(key, keys[1]);
        }

        if (index == keys.Length - 1) {
            return Straight(keys[^2], key);
        }

        return (Straight(keys[index - 1], key) + Straight(key, keys[index + 1])) * 0.5f;
    }

    static float Straight(CurveSample from, CurveSample to) {
        var span = to.Time - from.Time;
        return span <= 0f ? 0f : (to.Value - from.Value) / span;
    }
}
