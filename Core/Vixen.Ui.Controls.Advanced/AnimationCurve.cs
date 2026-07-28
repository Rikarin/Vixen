// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls.Advanced;

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

/// <summary>One key of a curve.</summary>
/// <remarks>
///     ⚠ <b>Tangents are slopes, not control points.</b> A slope survives the key being dragged in
///     time — the shape either side stays what it was — where a control point stored as a position
///     has to be moved with it, and every implementation that stores positions eventually forgets to
///     in one of the six places a key can move.
/// </remarks>
public sealed class CurveKey {
    /// <summary>Creates a key.</summary>
    /// <param name="time">When.</param>
    /// <param name="value">What.</param>
    /// <param name="mode">How its tangents behave.</param>
    public CurveKey(float time, float value, TangentMode mode = TangentMode.Auto) {
        Time = time;
        Value = value;
        Mode = mode;
    }

    /// <summary>When.</summary>
    public float Time { get; set; }

    /// <summary>What.</summary>
    public float Value { get; set; }

    /// <summary>The slope coming in, in value per unit of time.</summary>
    public float InTangent { get; set; }

    /// <summary>The slope going out.</summary>
    public float OutTangent { get; set; }

    /// <summary>How the two behave.</summary>
    public TangentMode Mode { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"({Time}, {Value})";
}

/// <summary>A value over time, as keys with tangents between them.</summary>
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
/// </remarks>
public sealed class AnimationCurve {
    readonly List<CurveKey> keys = [];

    /// <summary>Creates an empty curve.</summary>
    public AnimationCurve() {
    }

    /// <summary>Creates a curve from some keys.</summary>
    /// <param name="source">The keys, in any order.</param>
    public AnimationCurve(params ReadOnlySpan<CurveKey> source) {
        foreach (var key in source) {
            keys.Add(key);
        }

        Sort();
    }

    /// <summary>The keys, in time order.</summary>
    public IReadOnlyList<CurveKey> Keys => keys;

    /// <summary>Raised after anything changes.</summary>
    public event Action<AnimationCurve>? Changed;

    /// <summary>A straight line from (0,0) to (1,1).</summary>
    public static AnimationCurve Linear() =>
        new(new CurveKey(0f, 0f, TangentMode.Linear), new CurveKey(1f, 1f, TangentMode.Linear));

    /// <summary>A curve that starts slowly.</summary>
    public static AnimationCurve EaseIn() =>
        new(
            new CurveKey(0f, 0f, TangentMode.Free) { OutTangent = 0f },
            new CurveKey(1f, 1f, TangentMode.Free) { InTangent = 2f }
        );

    /// <summary>A curve that ends slowly.</summary>
    public static AnimationCurve EaseOut() =>
        new(
            new CurveKey(0f, 0f, TangentMode.Free) { OutTangent = 2f },
            new CurveKey(1f, 1f, TangentMode.Free) { InTangent = 0f }
        );

    /// <summary>A curve that starts and ends slowly. The default for almost everything.</summary>
    public static AnimationCurve EaseInOut() =>
        new(
            new CurveKey(0f, 0f, TangentMode.Free) { OutTangent = 0f },
            new CurveKey(1f, 1f, TangentMode.Free) { InTangent = 0f }
        );

    /// <summary>A single step in the middle.</summary>
    public static AnimationCurve Step() =>
        new(new CurveKey(0f, 0f, TangentMode.Constant), new CurveKey(1f, 1f, TangentMode.Constant));

    /// <summary>Adds a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Where it landed in time order.</returns>
    public int Add(CurveKey key) {
        ArgumentNullException.ThrowIfNull(key);

        keys.Add(key);
        Sort();

        return keys.IndexOf(key);
    }

    /// <summary>Adds a key at a time and value.</summary>
    /// <param name="time">When.</param>
    /// <param name="value">What.</param>
    /// <returns>The key.</returns>
    public CurveKey Add(float time, float value) {
        var key = new CurveKey(time, value);
        Add(key);

        return key;
    }

    /// <summary>Removes a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(CurveKey key) {
        if (!keys.Remove(key)) {
            return false;
        }

        Sort();
        return true;
    }

    /// <summary>Moves a key and puts it back in time order.</summary>
    /// <param name="key">The key.</param>
    /// <param name="time">Its new time.</param>
    /// <param name="value">Its new value.</param>
    /// <remarks>
    ///     ⚠ <b>The list is re-sorted, so a key dragged past its neighbour changes places with
    ///     it.</b> The alternative — clamping a key between its neighbours — is what makes a curve
    ///     editor feel stuck, and the reordering is exactly what the user means by dragging one key
    ///     over another.
    /// </remarks>
    public void Move(CurveKey key, float time, float value) {
        ArgumentNullException.ThrowIfNull(key);

        key.Time = time;
        key.Value = value;

        Sort();
    }

    /// <summary>Tells subscribers something changed, for an edit made through a key directly.</summary>
    public void Touch() => Changed?.Invoke(this);

    /// <summary>The value at a time.</summary>
    /// <param name="time">When.</param>
    /// <returns>The value.</returns>
    public float Evaluate(float time) {
        if (keys.Count == 0) {
            return 0f;
        }

        if (keys.Count == 1 || time <= keys[0].Time) {
            return keys[0].Value;
        }

        if (time >= keys[^1].Time) {
            return keys[^1].Value;
        }

        var index = 0;

        while (index < keys.Count - 2 && keys[index + 1].Time <= time) {
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

        var (outgoing, incoming) = Slopes(index);

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
    /// <param name="index">The segment's first key.</param>
    /// <returns>The outgoing slope of the first and the incoming slope of the second.</returns>
    public (float Outgoing, float Incoming) Slopes(int index) {
        var from = keys[index];
        var to = keys[index + 1];

        return (
            from.Mode switch {
                TangentMode.Auto => AutoSlope(index),
                TangentMode.Linear => Straight(from, to),
                _ => from.OutTangent
            },
            to.Mode switch {
                TangentMode.Auto => AutoSlope(index + 1),
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
    float AutoSlope(int index) {
        var key = keys[index];

        if (keys.Count < 2) {
            return 0f;
        }

        if (index == 0) {
            return Straight(key, keys[1]);
        }

        if (index == keys.Count - 1) {
            return Straight(keys[^2], key);
        }

        return (Straight(keys[index - 1], key) + Straight(key, keys[index + 1])) * 0.5f;
    }

    static float Straight(CurveKey from, CurveKey to) {
        var span = to.Time - from.Time;
        return span <= 0f ? 0f : (to.Value - from.Value) / span;
    }

    void Sort() {
        keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        Changed?.Invoke(this);
    }
}
