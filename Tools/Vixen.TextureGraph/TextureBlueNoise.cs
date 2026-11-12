// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph;

/// <summary>The one blue-noise tile <c>Source/Blue Noise</c> reads, made by void and cluster.</summary>
/// <remarks>
///     <para>
///         <b>Blue noise is a rank order with no low frequencies</b>: every threshold of it is a set of points as
///         evenly spread as a set of that many can be, with no clumps and no holes. A filtered white noise has the
///         spectrum and not the order, which is why Patina #167 asked for a real algorithm. Ulichney's void and
///         cluster is that algorithm: points go in at the largest void and come out of the tightest cluster, as
///         measured by a Gaussian energy on the torus, so the tile wraps without a seam.
///     </para>
///     <para>
///         ⚠ <b>One tile, made once, and a seed moves it rather than remaking it.</b> The tile is
///         <see cref="Side" /> square and takes some milliseconds; a compilation runs on every edit, so it is made
///         on first use and kept. <c>BlueNoise.rvn</c> turns a seed into a shift on the torus and one of the
///         square's eight symmetries, each of which keeps every distance between points and so keeps the noise
///         blue. Two seeds are two different pictures of the same quality.
///     </para>
///     <para>
///         Deterministic: the starting pattern comes from a fixed xorshift and every tie goes to the lowest
///         index, so the bytes are the same on every machine, which the result cache relies on.
///     </para>
/// </remarks>
static class TextureBlueNoise {
    /// <summary>The tile's side, in texels.</summary>
    public const int Side = 64;

    /// <summary>The Gaussian's deviation, in texels: Ulichney's 1.5.</summary>
    const double Sigma = 1.5;

    static readonly Lazy<float[]> Made = new(Make);

    /// <summary>Each texel's rank over the texel count, in <c>[0, 1)</c>, top row first.</summary>
    public static IReadOnlyList<float> Ranks => Made.Value;

    /// <summary>The tile as the bytes of an <see cref="TextureFormat.R32Float" /> image.</summary>
    /// <returns>A new array of <see cref="Side" /> × <see cref="Side" /> × 4 bytes.</returns>
    public static byte[] Bytes() {
        var ranks = Made.Value;
        var bytes = new byte[ranks.Length * sizeof(float)];

        Buffer.BlockCopy(ranks, 0, bytes, 0, bytes.Length);

        return bytes;
    }

    static float[] Make() {
        const int count = Side * Side;

        var kernel = Kernel();
        var pattern = new bool[count];
        var state = 0x9E3779B9u;

        // A tenth of the texels, at random: the starting pattern void and cluster settles.
        var placed = 0;

        while (placed < count / 10) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            var at = (int)(state % count);

            if (!pattern[at]) {
                pattern[at] = true;
                placed++;
            }
        }

        var energy = Energy(pattern, kernel);

        // Settle: move the tightest point into the largest void until the move would put it back.
        for (var guard = 0; guard < count; guard++) {
            var cluster = Extreme(pattern, energy, true, true);

            Toggle(pattern, energy, kernel, cluster);

            var gap = Extreme(pattern, energy, false, false);

            if (gap == cluster) {
                Toggle(pattern, energy, kernel, cluster);

                break;
            }

            Toggle(pattern, energy, kernel, gap);
        }

        var ranks = new int[count];
        var ones = pattern.Count(static set => set);

        // Phase one: the starting points, ranked by taking the tightest out first.
        var working = (bool[])pattern.Clone();
        var workingEnergy = (double[])energy.Clone();

        for (var rank = ones - 1; rank >= 0; rank--) {
            var cluster = Extreme(working, workingEnergy, true, true);

            Toggle(working, workingEnergy, kernel, cluster);
            ranks[cluster] = rank;
        }

        // Phase two: fill the largest void until half the tile is points.
        for (var rank = ones; rank < count / 2; rank++) {
            var gap = Extreme(pattern, energy, false, false);

            Toggle(pattern, energy, kernel, gap);
            ranks[gap] = rank;
        }

        // Phase three: the minority is now the gaps, so the tightest cluster of gaps is the next to fill.
        var inverted = new bool[count];

        for (var index = 0; index < count; index++) {
            inverted[index] = !pattern[index];
        }

        var invertedEnergy = Energy(inverted, kernel);

        for (var rank = count / 2; rank < count; rank++) {
            var cluster = Extreme(inverted, invertedEnergy, true, true);

            Toggle(inverted, invertedEnergy, kernel, cluster);
            ranks[cluster] = rank;
        }

        var values = new float[count];

        for (var index = 0; index < count; index++) {
            values[index] = ranks[index] / (float)count;
        }

        return values;
    }

    /// <summary>The toroidal Gaussian, indexed by the offset between two texels.</summary>
    static double[] Kernel() {
        var kernel = new double[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var dx = Math.Min(x, Side - x);
                var dy = Math.Min(y, Side - y);

                kernel[y * Side + x] = Math.Exp(-(dx * dx + dy * dy) / (2 * Sigma * Sigma));
            }
        }

        return kernel;
    }

    static double[] Energy(bool[] pattern, double[] kernel) {
        var energy = new double[pattern.Length];

        for (var index = 0; index < pattern.Length; index++) {
            if (pattern[index]) {
                Spread(energy, kernel, index, 1);
            }
        }

        return energy;
    }

    static void Toggle(bool[] pattern, double[] energy, double[] kernel, int index) {
        pattern[index] = !pattern[index];
        Spread(energy, kernel, index, pattern[index] ? 1 : -1);
    }

    static void Spread(double[] energy, double[] kernel, int index, int sign) {
        var (cy, cx) = Math.DivRem(index, Side);

        for (var y = 0; y < Side; y++) {
            var row = (y - cy + Side) % Side * Side;

            for (var x = 0; x < Side; x++) {
                energy[y * Side + x] += sign * kernel[row + (x - cx + Side) % Side];
            }
        }
    }

    /// <summary>The highest-energy point among the set texels, or the lowest-energy one among the clear.</summary>
    static int Extreme(bool[] pattern, double[] energy, bool among, bool highest) {
        var best = -1;

        for (var index = 0; index < pattern.Length; index++) {
            if (pattern[index] != among) {
                continue;
            }

            if (best < 0 || (highest ? energy[index] > energy[best] : energy[index] < energy[best])) {
                best = index;
            }
        }

        return best;
    }
}
