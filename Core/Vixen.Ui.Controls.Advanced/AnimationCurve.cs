// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core.Curves;

namespace Vixen.Ui.Controls.Advanced;

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
    /// <summary>Above this many keys the projection of <see cref="Fill" /> goes to the pool.</summary>
    /// <remarks>
    ///     Sixty-four is far past any hand-authored curve and still a 1.5 KB frame. A curve with more
    ///     keys than this came out of a bake, and a bake does not evaluate through this class.
    /// </remarks>
    const int StackKeys = 64;

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
    /// <remarks>
    ///     ⚠ <b>The arithmetic is <see cref="CurveEvaluation" />'s and not this control's</b>, because
    ///     the bake that turns an authored clip into sampled channels has to agree with what the
    ///     editor drew. Two copies of the Hermite convention is two places for it to drift, and the
    ///     drift is invisible until a build looks different from the curve somebody keyed.
    /// </remarks>
    public float Evaluate(float time) {
        if (keys.Count == 0) {
            return 0f;
        }

        if (keys.Count <= StackKeys) {
            Span<CurveSample> stack = stackalloc CurveSample[StackKeys];
            Fill(stack);

            return CurveEvaluation.Evaluate(stack[..keys.Count], time);
        }

        var rented = ArrayPool<CurveSample>.Shared.Rent(keys.Count);

        try {
            Fill(rented);
            return CurveEvaluation.Evaluate(rented.AsSpan(0, keys.Count), time);
        } finally {
            ArrayPool<CurveSample>.Shared.Return(rented);
        }
    }

    /// <summary>The two slopes that actually govern a segment, after the modes have had their say.</summary>
    /// <param name="index">The segment's first key.</param>
    /// <returns>The outgoing slope of the first and the incoming slope of the second.</returns>
    public (float Outgoing, float Incoming) Slopes(int index) {
        if (keys.Count <= StackKeys) {
            Span<CurveSample> stack = stackalloc CurveSample[StackKeys];
            Fill(stack);

            return CurveEvaluation.Slopes(stack[..keys.Count], index);
        }

        var rented = ArrayPool<CurveSample>.Shared.Rent(keys.Count);

        try {
            Fill(rented);
            return CurveEvaluation.Slopes(rented.AsSpan(0, keys.Count), index);
        } finally {
            ArrayPool<CurveSample>.Shared.Return(rented);
        }
    }

    /// <summary>Projects the keys into the evaluator's form.</summary>
    /// <remarks>
    ///     A curve editor's keys are a mutable list of class instances and the evaluator wants a span
    ///     of values, so somewhere the two have to meet. Doing it here keeps the copy off the
    ///     evaluator — a bake has its keys in a span already and should not pay for this one's shape —
    ///     and the stack path covers every curve a person has ever drawn by hand.
    /// </remarks>
    void Fill(Span<CurveSample> buffer) {
        for (var index = 0; index < keys.Count; index++) {
            var key = keys[index];
            buffer[index] = new(key.Time, key.Value, key.InTangent, key.OutTangent, key.Mode);
        }
    }

    void Sort() {
        keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        Changed?.Invoke(this);
    }
}
