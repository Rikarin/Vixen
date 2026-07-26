// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Mathematics;

/// <summary>
///     Scalar helpers the rest of the library and the engine share: the constants, the tolerance
///     comparisons, and the interpolation and clamping that would otherwise be rewritten slightly
///     differently in every subsystem.
/// </summary>
public static class MathUtil {
    /// <summary>π.</summary>
    public const float Pi = 3.14159265358979323846f;

    /// <summary>2π — a full turn.</summary>
    public const float TwoPi = 6.28318530717958647692f;

    /// <summary>π/2 — a quarter turn.</summary>
    public const float PiOverTwo = 1.57079632679489661923f;

    /// <summary>π/4 — an eighth of a turn.</summary>
    public const float PiOverFour = 0.78539816339744830962f;

    /// <summary>
    ///     The default tolerance for "close enough": about 8 significant decimal digits of headroom
    ///     below <see cref="float" />'s ~7. Small enough that it does not hide a real error, large
    ///     enough to absorb the rounding of a few chained operations.
    /// </summary>
    public const float ZeroTolerance = 1e-6f;

    /// <summary>Converts degrees to radians.</summary>
    /// <param name="degrees">The angle in degrees.</param>
    /// <returns>The angle in radians.</returns>
    public static float DegreesToRadians(float degrees) => degrees * (Pi / 180f);

    /// <summary>Converts radians to degrees.</summary>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The angle in degrees.</returns>
    public static float RadiansToDegrees(float radians) => radians * (180f / Pi);

    /// <summary>Whether a value is within <see cref="ZeroTolerance" /> of zero.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true" /> if it is effectively zero.</returns>
    public static bool IsZero(float value) => MathF.Abs(value) < ZeroTolerance;

    /// <summary>Whether a value is within <see cref="ZeroTolerance" /> of one.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true" /> if it is effectively one.</returns>
    public static bool IsOne(float value) => IsZero(value - 1f);

    /// <summary>
    ///     Whether two values are close enough to be treated as equal. The tolerance is scaled by
    ///     the larger magnitude, so it behaves absolutely near zero and relatively out where
    ///     <see cref="float" /> steps in units of thousands — a fixed epsilon is wrong at one end or
    ///     the other and there is no single value that is right at both.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if the values are within tolerance.</returns>
    /// <remarks>
    ///     This is never what <c>operator ==</c> does. Approximate comparison is spelled out at the
    ///     call site so that nobody has to guess which kind of equality a given <c>==</c> meant.
    /// </remarks>
    public static bool NearEqual(float left, float right, float tolerance = ZeroTolerance) {
        // IEEE ==, so two identical infinities are equal and a NaN is equal to nothing — including
        // another NaN, which float.Equals would have called equal.
        if (left == right) {
            return true;
        }

        // Without this, the scaling below says yes to every pair involving an infinity: the
        // difference is infinite, but so is the scaled tolerance, and infinity <= infinity holds.
        var difference = MathF.Abs(left - right);
        if (!float.IsFinite(difference)) {
            return false;
        }

        var scale = MathF.Max(1f, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
        return difference <= tolerance * scale;
    }

    /// <summary>Constrains a value to an interval.</summary>
    /// <param name="value">The value.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <returns>The constrained value.</returns>
    public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    /// <inheritdoc cref="Clamp(float,float,float)" />
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Constrains a value to <c>[0, 1]</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The constrained value.</returns>
    public static float Saturate(float value) => Clamp(value, 0f, 1f);

    /// <summary>Linearly interpolates between two values.</summary>
    /// <param name="from">The value at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The value at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant. Not clamped.</param>
    /// <returns>The interpolated value.</returns>
    /// <remarks>
    ///     Uses the <c>from + (to - from) * t</c> form, which is one FMA and is exact at
    ///     <c>t = 0</c>. It is <i>not</i> exact at <c>t = 1</c> for large magnitudes — the
    ///     alternative form is exact at 1 and not at 0, and animation and easing care far more about
    ///     the start.
    /// </remarks>
    public static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);

    /// <summary>Finds the interpolant that <see cref="Lerp(float,float,float)" /> would need.</summary>
    /// <param name="from">The value that maps to 0.</param>
    /// <param name="to">The value that maps to 1.</param>
    /// <param name="value">The value to locate.</param>
    /// <returns>The interpolant, or 0 if the interval is empty.</returns>
    public static float InverseLerp(float from, float to, float value) {
        var range = to - from;
        return IsZero(range) ? 0f : (value - from) / range;
    }

    /// <summary>Interpolates with a curve that is flat at both ends (3t² − 2t³).</summary>
    /// <param name="amount">The interpolant, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The eased interpolant.</returns>
    public static float SmoothStep(float amount) {
        var t = Saturate(amount);
        return t * t * (3f - (2f * t));
    }

    /// <summary>
    ///     Interpolates with a curve whose first <i>and</i> second derivatives vanish at both ends
    ///     (6t⁵ − 15t⁴ + 10t³). Worth the extra multiplies wherever the result drives acceleration,
    ///     because <see cref="SmoothStep" /> has a visible jerk at its endpoints.
    /// </summary>
    /// <param name="amount">The interpolant, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The eased interpolant.</returns>
    public static float SmootherStep(float amount) {
        var t = Saturate(amount);
        return t * t * t * ((t * ((t * 6f) - 15f)) + 10f);
    }

    /// <summary>Wraps an angle into <c>(−π, π]</c>.</summary>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The equivalent angle in the principal range.</returns>
    public static float WrapAngle(float radians) {
        var wrapped = MathF.IEEERemainder(radians, TwoPi);
        return wrapped <= -Pi ? wrapped + TwoPi : wrapped;
    }

    /// <summary>
    ///     The shortest signed angle from one angle to another, in <c>(−π, π]</c>. Interpolating a
    ///     heading without this goes the long way round whenever the pair straddles ±π.
    /// </summary>
    /// <param name="from">The angle to measure from, in radians.</param>
    /// <param name="to">The angle to measure to, in radians.</param>
    /// <returns>The signed difference in radians.</returns>
    public static float DeltaAngle(float from, float to) => WrapAngle(to - from);

    /// <summary>Whether a value is a power of two. Zero is not.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true" /> if exactly one bit is set.</returns>
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    /// <summary>The smallest power of two that is at least <paramref name="value" />.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The rounded-up power of two; 1 for any value below 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The result would not fit in an <see cref="int" />.</exception>
    public static int NextPowerOfTwo(int value) {
        if (value <= 1) {
            return 1;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1 << 30);
        return 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(value - 1)));
    }

    /// <summary>Rounds up to a multiple of <paramref name="alignment" />.</summary>
    /// <param name="value">The value to round. Must not be negative.</param>
    /// <param name="alignment">The multiple. Must be positive.</param>
    /// <returns>The rounded value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static int AlignUp(int value, int alignment) {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        return ((value + alignment - 1) / alignment) * alignment;
    }

    /// <summary>
    ///     Wraps a value into <c>[0, length)</c>, unlike <c>%</c>, which reflects around zero for
    ///     negative input and is almost never what a looping animation or a tiled coordinate wants.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="length">The length of the interval. Must be positive.</param>
    /// <returns>The wrapped value.</returns>
    public static float Repeat(float value, float length) {
        if (length <= 0f) {
            return 0f;
        }

        var wrapped = value - (MathF.Floor(value / length) * length);

        // Rounding can land exactly on `length` for values just under a multiple of it, which would
        // break the half-open interval this promises.
        return wrapped >= length ? 0f : wrapped;
    }

    /// <summary>Bounces a value back and forth within <c>[0, length]</c>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="length">The length of the interval. Must be positive.</param>
    /// <returns>The reflected value.</returns>
    public static float PingPong(float value, float length) {
        if (length <= 0f) {
            return 0f;
        }

        var wrapped = Repeat(value, length * 2f);
        return length - MathF.Abs(wrapped - length);
    }
}
