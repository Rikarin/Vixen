// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Styling;

/// <summary>Which shape of easing a timing function is.</summary>
public enum TimingFunctionKind : byte {
    /// <summary>A cubic Bézier with two control points. Covers <c>linear</c> and every <c>ease*</c>.</summary>
    CubicBezier,

    /// <summary>A step function.</summary>
    Steps,

    /// <summary>A damped spring. Vixen's own.</summary>
    Spring
}

/// <summary>Where a step function jumps.</summary>
public enum StepPosition : byte {
    /// <summary>At the start of each interval.</summary>
    Start,

    /// <summary>At the end of each interval.</summary>
    End
}

/// <summary>Maps progress through a duration onto progress through a change.</summary>
/// <remarks>
///     <para>
///         The two are not the same thing and conflating them is what makes UI motion feel wrong.
///         Halfway through the time is not halfway through the movement — real things accelerate and
///         settle — and a timing function is the curve that says by how much.
///     </para>
///     <para>
///         Springs are Vixen's extension, and the case for them in a game UI is that they are the
///         only easing whose parameters are physical. A designer asking for "snappier" adjusts a
///         stiffness; the same request against a <c>cubic-bezier</c> is four numbers found by
///         guessing. They also compose properly with an interruption — a spring retargeted mid-flight
///         carries its velocity, where a Bézier restarts from a standstill and visibly stutters.
///     </para>
/// </remarks>
public readonly record struct TimingFunction {
    TimingFunction(
        TimingFunctionKind kind,
        float x1,
        float y1,
        float x2,
        float y2,
        int steps,
        StepPosition position,
        float mass,
        float stiffness,
        float damping
    ) {
        Kind = kind;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        Steps = steps;
        Position = position;
        Mass = mass;
        Stiffness = stiffness;
        Damping = damping;
    }

    /// <summary>What shape it is.</summary>
    public TimingFunctionKind Kind { get; }

    /// <summary>The first control point's x.</summary>
    public float X1 { get; }

    /// <summary>The first control point's y.</summary>
    public float Y1 { get; }

    /// <summary>The second control point's x.</summary>
    public float X2 { get; }

    /// <summary>The second control point's y.</summary>
    public float Y2 { get; }

    /// <summary>How many steps.</summary>
    public int Steps { get; }

    /// <summary>Where a step jumps.</summary>
    public StepPosition Position { get; }

    /// <summary>The spring's mass.</summary>
    public float Mass { get; }

    /// <summary>The spring's stiffness.</summary>
    public float Stiffness { get; }

    /// <summary>The spring's damping coefficient.</summary>
    public float Damping { get; }

    /// <summary>Constant speed. CSS's <c>linear</c>.</summary>
    public static TimingFunction Linear => Bezier(0f, 0f, 1f, 1f);

    /// <summary>CSS's <c>ease</c>.</summary>
    public static TimingFunction Ease => Bezier(0.25f, 0.1f, 0.25f, 1f);

    /// <summary>CSS's <c>ease-in</c>.</summary>
    public static TimingFunction EaseIn => Bezier(0.42f, 0f, 1f, 1f);

    /// <summary>CSS's <c>ease-out</c>.</summary>
    public static TimingFunction EaseOut => Bezier(0f, 0f, 0.58f, 1f);

    /// <summary>CSS's <c>ease-in-out</c>.</summary>
    public static TimingFunction EaseInOut => Bezier(0.42f, 0f, 0.58f, 1f);

    /// <summary>A cubic Bézier.</summary>
    /// <param name="x1">The first control point's x, in <c>[0, 1]</c>.</param>
    /// <param name="y1">The first control point's y, which may be outside it.</param>
    /// <param name="x2">The second control point's x, in <c>[0, 1]</c>.</param>
    /// <param name="y2">The second control point's y, which may be outside it.</param>
    /// <returns>The timing function.</returns>
    /// <remarks>
    ///     The <c>y</c> coordinates are deliberately unclamped: a control point above 1 makes the
    ///     curve overshoot and come back, which is how every "bounce" easing anyone has ever pasted
    ///     into a stylesheet works. The <c>x</c> coordinates are clamped because a curve that goes
    ///     backwards in time is not a function of it.
    /// </remarks>
    public static TimingFunction Bezier(float x1, float y1, float x2, float y2) => new(
        TimingFunctionKind.CubicBezier,
        Math.Clamp(x1, 0f, 1f),
        y1,
        Math.Clamp(x2, 0f, 1f),
        y2,
        0,
        StepPosition.End,
        0f,
        0f,
        0f
    );

    /// <summary>A step function.</summary>
    /// <param name="count">How many steps.</param>
    /// <param name="position">Where each one jumps.</param>
    /// <returns>The timing function.</returns>
    public static TimingFunction Step(int count, StepPosition position) => new(
        TimingFunctionKind.Steps,
        0f,
        0f,
        0f,
        0f,
        Math.Max(1, count),
        position,
        0f,
        0f,
        0f
    );

    /// <summary>A damped spring.</summary>
    /// <param name="mass">The mass. Larger is heavier and slower.</param>
    /// <param name="stiffness">The spring constant. Larger is snappier.</param>
    /// <param name="damping">The damping coefficient. Larger settles sooner and overshoots less.</param>
    /// <returns>The timing function.</returns>
    public static TimingFunction Spring(float mass, float stiffness, float damping) => new(
        TimingFunctionKind.Spring,
        0f,
        0f,
        0f,
        0f,
        0,
        StepPosition.End,
        MathF.Max(mass, 0.0001f),
        MathF.Max(stiffness, 0.0001f),
        MathF.Max(damping, 0f)
    );

    /// <summary>Maps elapsed progress onto progress through the change.</summary>
    /// <param name="progress">How far through the duration, in <c>[0, 1]</c>.</param>
    /// <returns>How far through the change, which may leave <c>[0, 1]</c>.</returns>
    public float Evaluate(float progress) {
        progress = Math.Clamp(progress, 0f, 1f);

        return Kind switch {
            TimingFunctionKind.Steps => EvaluateSteps(progress),
            TimingFunctionKind.Spring => EvaluateSpring(progress),
            _ => EvaluateBezier(progress)
        };
    }

    /// <summary>How long a spring takes to settle.</summary>
    /// <returns>The duration in seconds, or 0 for anything else.</returns>
    /// <remarks>
    ///     <para>
    ///         A spring has no duration of its own — it is a differential equation, and it approaches
    ///         its target without ever formally arriving. So one is derived: the time by which the
    ///         envelope of the oscillation has decayed to a thousandth, which is a millipixel on any
    ///         movement a UI makes and is therefore where "settled" honestly is.
    ///     </para>
    ///     <para>
    ///         This is what lets a spring be a <i>timing function</i> at all, sitting where CSS
    ///         expects one and driven by the same duration machinery as every other easing, rather
    ///         than needing its own integrator plumbed through the animator.
    ///     </para>
    /// </remarks>
    public float SettlingDuration() {
        if (Kind != TimingFunctionKind.Spring) {
            return 0f;
        }

        var decay = Damping / (2f * Mass);
        if (decay <= 0f) {
            // Undamped: it never settles. A second is long enough for anyone to notice and stop.
            return 1f;
        }

        // exp(-decay·t) = 1/1000  ⟹  t = ln(1000) / decay
        var undamped = MathF.Sqrt(Stiffness / Mass);
        var settling = MathF.Log(1000f) / decay;

        // An overdamped spring's slowest mode decays more slowly than the envelope suggests, so the
        // bound is taken from that mode instead.
        if (decay > undamped) {
            var slowest = decay - MathF.Sqrt((decay * decay) - (undamped * undamped));
            settling = MathF.Log(1000f) / MathF.Max(slowest, 0.0001f);
        }

        return Math.Clamp(settling, 0.001f, 10f);
    }

    float EvaluateSteps(float progress) {
        var step = Position == StepPosition.Start
            ? MathF.Floor(progress * Steps) + 1f
            : MathF.Floor(progress * Steps);

        return Math.Clamp(step / Steps, 0f, 1f);
    }

    /// <summary>The analytic solution of a damped harmonic oscillator released from rest at 0.</summary>
    /// <remarks>
    ///     Closed form rather than numerical integration, which matters for more than accuracy: a
    ///     value that depends only on the elapsed time cannot drift, so a spring evaluated at
    ///     variable frame rates gives the same answer as one evaluated at a fixed step, and a
    ///     dropped frame does not change where it ends up.
    /// </remarks>
    float EvaluateSpring(float progress) {
        var duration = SettlingDuration();
        var t = progress * duration;

        var undamped = MathF.Sqrt(Stiffness / Mass);
        var ratio = Damping / (2f * MathF.Sqrt(Stiffness * Mass));
        var decay = ratio * undamped;

        float displacement;

        if (ratio < 1f) {
            // Underdamped: it overshoots and rings. The case anyone writing `spring()` wants.
            var damped = undamped * MathF.Sqrt(1f - (ratio * ratio));
            displacement = MathF.Exp(-decay * t)
                * (MathF.Cos(damped * t) + (decay / damped * MathF.Sin(damped * t)));
        } else if (MathUtil.NearEqual(ratio, 1f)) {
            // Critically damped: the fastest approach with no overshoot at all.
            displacement = MathF.Exp(-undamped * t) * (1f + (undamped * t));
        } else {
            // Overdamped: two decaying exponentials, and it crawls in.
            var root = undamped * MathF.Sqrt((ratio * ratio) - 1f);
            var fast = -decay + root;
            var slow = -decay - root;
            displacement = ((slow * MathF.Exp(fast * t)) - (fast * MathF.Exp(slow * t))) / (slow - fast);
        }

        // The oscillator's displacement is what is *left* to travel, so progress is its complement.
        return 1f - displacement;
    }

    float EvaluateBezier(float x) {
        if (x <= 0f) {
            return 0f;
        }

        if (x >= 1f) {
            return 1f;
        }

        // The curve is parameterised by t, and CSS asks for y at a given *x*. So t has to be solved
        // for first — Newton-Raphson, falling back to bisection where the derivative is near zero,
        // which is exactly what happens on the flat run of an `ease-in` near the origin and would
        // otherwise send Newton off to infinity.
        var t = SolveForX(x);
        return CubicAt(t, Y1, Y2);
    }

    /// <summary>Finds the curve parameter at which the curve reaches a given x.</summary>
    /// <remarks>
    ///     <para>
    ///         Bisection to a tolerance on <b>t</b>, not on x, and the difference is the whole
    ///         correctness of this function. Terminating when <c>|x(t) − x|</c> is small pins nothing
    ///         wherever the curve is flat in x — and <c>cubic-bezier(0, y, 0, y)</c>, a perfectly
    ///         ordinary slow-start easing, is exactly that near the origin: <c>x(t) = t³</c>, so an
    ///         absolute error of 1e-6 in x is an error of 1e-2 in t, and the y that comes back is
    ///         visibly wrong for the first frames of every transition using it.
    ///     </para>
    ///     <para>
    ///         Found by a property test rather than by inspection, which is the case for it: the
    ///         curves it fails on are a thin slice of the parameter space and every hand-picked
    ///         easing passed.
    ///     </para>
    ///     <para>
    ///         Newton first because it converges in two or three steps on the curves anyone actually
    ///         writes, then bisection to finish, which is guaranteed because x is monotonic whenever
    ///         both control points lie in <c>[0, 1]</c> — which <see cref="Bezier" /> makes sure of.
    ///     </para>
    /// </remarks>
    float SolveForX(float x) {
        var t = x;

        for (var i = 0; i < 8; i++) {
            var error = CubicAt(t, X1, X2) - x;
            var slope = CubicSlopeAt(t, X1, X2);

            if (MathF.Abs(slope) < 1e-4f) {
                break;
            }

            var step = error / slope;
            t -= step;

            if (t is < 0f or > 1f) {
                break;
            }

            if (MathF.Abs(step) < 1e-7f) {
                return t;
            }
        }

        var low = 0f;
        var high = 1f;
        t = 0.5f;

        while (high - low > 1e-7f) {
            t = (low + high) * 0.5f;

            if (CubicAt(t, X1, X2) < x) {
                low = t;
            } else {
                high = t;
            }
        }

        return t;
    }

    // A cubic Bézier with the endpoints pinned at 0 and 1, which is what a CSS easing curve is.
    static float CubicAt(float t, float a, float b) {
        var inverse = 1f - t;
        return (3f * inverse * inverse * t * a) + (3f * inverse * t * t * b) + (t * t * t);
    }

    static float CubicSlopeAt(float t, float a, float b) {
        var inverse = 1f - t;
        return (3f * inverse * inverse * a)
            + (6f * inverse * t * (b - a))
            + (3f * t * t * (1f - b));
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch {
        TimingFunctionKind.Steps => string.Create(CultureInfo.InvariantCulture, $"steps({Steps}, {Position})"),
        TimingFunctionKind.Spring => string.Create(
            CultureInfo.InvariantCulture,
            $"spring({Mass}, {Stiffness}, {Damping})"
        ),
        _ => string.Create(CultureInfo.InvariantCulture, $"cubic-bezier({X1}, {Y1}, {X2}, {Y2})")
    };
}
