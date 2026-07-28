// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Fuzz;

/// <summary>The generator the mutations come out of.</summary>
/// <remarks>
///     <para>
///         SplitMix64: eight lines, no state beyond a counter, and the same sequence on every
///         platform and every runtime version. That last part is what matters here — <c>Random</c>
///         is explicitly allowed to change its algorithm between releases, and a fuzz finding that
///         cannot be replayed on the machine it was reported from is a rumour rather than a bug.
///     </para>
///     <para>
///         Its own rather than <c>Vixen.Net</c>'s, which is internal and correctly so. A fuzzer
///         reaching into the internals of what it fuzzes is a fuzzer that will one day fail to
///         compile for a reason that has nothing to do with the code under test.
///     </para>
/// </remarks>
public sealed class FuzzRandom {
    ulong state;

    /// <summary>Creates a generator.</summary>
    /// <param name="seed">Where to start. The same seed gives the same sequence, always.</param>
    public FuzzRandom(ulong seed) => state = seed;

    /// <summary>The next value.</summary>
    /// <returns>Sixty-four bits.</returns>
    public ulong Next() {
        state += 0x9E3779B97F4A7C15ul;

        var value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9ul;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBul;

        return value ^ (value >> 31);
    }

    /// <summary>The next value below a bound.</summary>
    /// <param name="bound">The exclusive upper bound. Must be positive.</param>
    /// <returns>Something in <c>[0, bound)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound" /> is not positive.</exception>
    /// <remarks>
    ///     Modulo, and the bias that comes with it is fine: these numbers pick a mutation and an
    ///     offset, and a fuzzer that visits one byte a fraction of a percent more often than
    ///     another has not been made worse in any way anybody could measure.
    /// </remarks>
    public int Below(int bound) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bound);

        return (int)(Next() % (ulong)bound);
    }
}
