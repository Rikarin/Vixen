// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Events;

/// <summary>Marsaglia's xorshift, as a struct with four bytes of state.</summary>
/// <remarks>
///     <para>
///         <b>Not <see cref="System.Random" />, for two reasons.</b> A shared <c>Random</c> is not
///         reproducible, and "the same event, played from the same state, picks the same variant" is
///         what makes a test of variation possible at all — otherwise every assertion about which
///         clip came out is a coin toss. A per-event <c>Random</c> would be reproducible and would
///         also be an allocation and an indirection per event, for three shifts' worth of work.
///     </para>
///     <para>
///         <b>Quality is not the concern here.</b> Xorshift fails serious statistical tests; what it
///         is being asked for is "a different footstep from the last one" and a few cents of pitch.
///         Anything a player could distinguish from a better generator would have to be audible, and
///         a period of 2³² − 1 is four billion footsteps.
///     </para>
///     <para>
///         Internal because a random number generator belongs in <c>Vixen.Core</c> if it belongs
///         anywhere public, and putting one in the audio assembly's surface would be the wrong place
///         for anybody to find it.
///     </para>
/// </remarks>
/// <param name="seed">Where to start. Zero is replaced, because zero is xorshift's fixed point.</param>
internal struct Xorshift32(uint seed) {
    // The golden-ratio constant, which is only here because it is a well-mixed non-zero number.
    const uint Fallback = 0x9E3779B9;

    uint state = seed == 0 ? Fallback : seed;

    /// <summary>The next thirty-two bits.</summary>
    /// <returns>A number, never zero.</returns>
    public uint Next() {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>The next number in 0..1, including zero and excluding one.</summary>
    /// <returns>The number.</returns>
    /// <remarks>
    ///     Twenty-four bits and not thirty-two, because a float has twenty-four bits of mantissa —
    ///     dividing the full range by 2³² rounds, and rounding up at the top is how a supposedly
    ///     half-open range returns exactly one and an index goes out of bounds once a fortnight.
    /// </remarks>
    public float NextUnit() => (Next() >> 8) * (1f / 16_777_216f);

    /// <summary>The next number in −1..1.</summary>
    /// <returns>The number.</returns>
    public float NextBipolar() => (NextUnit() * 2f) - 1f;

    /// <summary>The next index below a bound.</summary>
    /// <param name="bound">One past the largest wanted. Must be positive.</param>
    /// <returns>The index.</returns>
    public int NextIndex(int bound) => (int)(NextUnit() * bound);
}
