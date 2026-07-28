// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>
///     Random numbers a compute shader can reproduce exactly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Stateless, not a stream.</b> A generator with state has to be carried per particle and
///         advanced in the order the particles are visited, which on the GPU is no order at all. So
///         there is no state: a value is a pure function of <i>what it is for</i> — the particle's
///         own identifier, the effect's seed, and a per-use salt — and any thread can compute any
///         particle's value at any time without having computed anybody else's first. That is what
///         makes the CPU and GPU paths comparable rather than merely similar.
///     </para>
///     <para>
///         <b>Integer operations only, and that is a hard rule.</b> Every step here is a 32-bit
///         multiply, xor or shift, all of which are exact and identical on every processor that runs
///         them. The moment a hash reaches for a float multiply, a sine or a table it becomes a thing
///         that agrees to within a tolerance, and a tolerance is not what a golden-image test or a
///         replay wants. The single conversion to float happens at the very end, from a 24-bit
///         mantissa's worth of bits, which is exact in both directions.
///     </para>
///     <para>
///         The mixing function is the one from Chris Wellons' <c>lowbias32</c>, chosen because it is
///         three multiplies and three xor-shifts, has no anomalies in the avalanche tests, and is
///         short enough to inline into a shader without a second thought.
///     </para>
/// </remarks>
public static class VfxRandom {
    /// <summary>Salt for the first random value an operation asks for.</summary>
    /// <remarks>
    ///     <para>
    ///         Two different uses of randomness on the same particle must not agree with each other.
    ///         Without a salt, "random size" and "random lifetime" would be the same number, and every
    ///         big particle would live longest — a correlation that looks like art direction and is
    ///         not.
    ///     </para>
    ///     <para>
    ///         The salt is the <i>operation's</i> index in the compiled graph rather than a counter
    ///         the caller advances, so it stays a pure function of the graph. Reordering the graph
    ///         changes the noise, which is correct: it is a different effect.
    ///     </para>
    /// </remarks>
    public const uint FirstSalt = 0;

    /// <summary>Mixes a value into a well-distributed 32-bit hash.</summary>
    /// <param name="value">The value to mix.</param>
    /// <returns>The hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(uint value) {
        // Offset first, because a mixer built from xor-shifts and multiplies has a fixed point at
        // zero: every step of it maps 0 to 0, so particle zero of seed zero would draw zero for ever
        // and the first particle of an effect would be the one that looked wrong. The constant is the
        // golden ratio's reciprocal, which is the usual choice for having no short bit patterns.
        value += 0x9e3779b9;

        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;

        return value;
    }

    /// <summary>The hash for one particle's one use of randomness.</summary>
    /// <param name="particle">The particle's identifier — stable for its whole life.</param>
    /// <param name="seed">The effect instance's seed.</param>
    /// <param name="salt">Which use this is. See <see cref="FirstSalt" />.</param>
    /// <returns>The hash.</returns>
    /// <remarks>
    ///     The three are combined by hashing each in turn rather than by adding them, so that
    ///     particle 3 of seed 5 and particle 5 of seed 3 are unrelated. Adding them first would make
    ///     a diagonal stripe of the identifier space share every value.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(uint particle, uint seed, uint salt) => Hash(Hash(Hash(particle) ^ seed) ^ salt);

    /// <summary>A uniform value in [0, 1).</summary>
    /// <param name="particle">The particle's identifier.</param>
    /// <param name="seed">The effect instance's seed.</param>
    /// <param name="salt">Which use this is.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    ///     Twenty-four bits over 2^24, which is every float in [0, 1) with an exact representation and
    ///     nothing else. Dividing by <see cref="uint.MaxValue" /> instead would ask the hardware to
    ///     round a 32-bit integer into a 24-bit mantissa, and the rounding mode is the one thing about
    ///     float arithmetic a shader compiler is allowed to differ on.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Value(uint particle, uint seed, uint salt) => ToFloat(Hash(particle, seed, salt));

    /// <summary>A uniform value in [<paramref name="minimum" />, <paramref name="maximum" />).</summary>
    /// <param name="particle">The particle's identifier.</param>
    /// <param name="seed">The effect instance's seed.</param>
    /// <param name="salt">Which use this is.</param>
    /// <param name="minimum">The low end.</param>
    /// <param name="maximum">The high end.</param>
    /// <returns>The value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Range(uint particle, uint seed, uint salt, float minimum, float maximum) =>
        minimum + ((maximum - minimum) * Value(particle, seed, salt));

    /// <summary>Three uniform values in [0, 1), from three consecutive salts.</summary>
    /// <param name="particle">The particle's identifier.</param>
    /// <param name="seed">The effect instance's seed.</param>
    /// <param name="salt">The first of the three salts to use.</param>
    /// <returns>The values.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Value3(uint particle, uint seed, uint salt) => new(
        Value(particle, seed, salt),
        Value(particle, seed, salt + 1),
        Value(particle, seed, salt + 2)
    );

    /// <summary>A direction uniformly distributed over the unit sphere.</summary>
    /// <param name="particle">The particle's identifier.</param>
    /// <param name="seed">The effect instance's seed.</param>
    /// <param name="salt">The first of the two salts to use.</param>
    /// <returns>A unit vector.</returns>
    /// <remarks>
    ///     <b>Uniform over the sphere, not over the angles.</b> Picking two angles uniformly and
    ///     converting spherically clusters points at the poles, which on a burst of a thousand
    ///     particles is a visible pinch at the top and bottom. Sampling <c>z</c> uniformly in
    ///     [-1, 1] and the azimuth uniformly is the standard fix and costs one sine and one cosine —
    ///     the only transcendentals in this file, and they are on the *direction* rather than on the
    ///     hash, so the two paths still agree bit for bit on the numbers that feed them.
    /// </remarks>
    public static Vector3 Direction(uint particle, uint seed, uint salt) {
        var z = Range(particle, seed, salt, -1f, 1f);
        var azimuth = Range(particle, seed, salt + 1, 0f, MathF.Tau);
        var radius = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));

        return new(radius * MathF.Cos(azimuth), radius * MathF.Sin(azimuth), z);
    }

    /// <summary>The top 24 bits of a hash, as a float in [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float ToFloat(uint hash) => (hash >> 8) * (1f / (1 << 24));
}
