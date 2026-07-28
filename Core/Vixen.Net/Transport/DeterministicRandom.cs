// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>
///     xoshiro256** seeded through splitmix64. Small, fast, and — the only property that matters
///     here — the same sequence from the same seed on every platform, for ever.
/// </summary>
/// <remarks>
///     <see cref="System.Random" /> is explicitly documented as free to change its algorithm between
///     releases, which makes "reproduce that packet loss pattern from the bug report" a promise the
///     BCL will not keep. Fourteen lines of state is a cheaper answer than a dependency.
/// </remarks>
struct DeterministicRandom {
    ulong s0, s1, s2, s3;

    public DeterministicRandom(ulong seed) {
        // splitmix64: spreads a seed of 0, or of 1, into four words that are not obviously related.
        s0 = SplitMix(ref seed);
        s1 = SplitMix(ref seed);
        s2 = SplitMix(ref seed);
        s3 = SplitMix(ref seed);
    }

    public ulong NextUInt64() {
        var result = ulong.RotateLeft(s1 * 5, 7) * 9;
        var t = s1 << 17;

        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = ulong.RotateLeft(s3, 45);

        return result;
    }

    /// <summary>A value in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A value in [-magnitude, +magnitude], for jitter.</summary>
    public long NextSigned(long magnitude) =>
        magnitude == 0 ? 0 : (long)((NextDouble() * 2.0 - 1.0) * magnitude);

    static ulong SplitMix(ref ulong state) {
        var z = state += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;

        return z ^ (z >> 31);
    }
}
