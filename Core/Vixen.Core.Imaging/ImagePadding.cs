// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging;

/// <summary>What fills the texels of a picture that no island of its layout covers.</summary>
public enum PaddingMode {
    /// <summary>Nothing: whatever was there stays.</summary>
    None = 0,

    /// <summary>The islands grow until every texel is filled.</summary>
    Infinite = 1,

    /// <summary>The islands grow by the distance, and the rest is zero in every lane, which is transparent.</summary>
    Transparent = 2,

    /// <summary>The islands grow by the distance, and the rest is one colour.</summary>
    Colour = 3,

    /// <summary>The islands grow by the distance, and the rest is a smooth blur of what is around it.</summary>
    Diffusion = 4
}

/// <summary>
///     Fills the background of a picture from its islands, so that a mip or a bilinear tap at a
///     chart's edge reads the chart and not whatever the background happened to hold.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             It is <c>MapBaker.Dilate</c>'s idea, on a <see cref="FloatImage" /> rather than on a
///             bake's buffers
///         </b>
///         , and the two rules that make it right are the same. The coverage is
///         read and never written, so one island's padding cannot overwrite the island beside it.
///         And each round commits after it finishes, so a texel filled early in a round cannot be
///         read by one later in the same round: where two islands' fronts meet in one round,
///         writing in place would give the meeting line to whichever front the order visited first,
///         a lopsided seam that shows at low mips.
///     </para>
///     <para>
///         ⚠ <b>Only the frontier is visited.</b> A round reads the texels the last round filled
///         and their empty neighbours, rather than scanning the picture, so growing until
///         everything is filled costs one pass over the picture however far it has to reach. A
///         scan per round would be a 4K picture times the width of its largest empty area.
///     </para>
///     <para>
///         A filled texel takes the mean of its filled four-neighbours, every lane alike. That
///         averages a normal without renormalising it, which for padding is what a mip does to it
///         anyway, and averages an id, which is wrong for an id map and is why a baker's id map
///         is dilated by the baker and not here.
///     </para>
/// </remarks>
public static class ImagePadding {
    /// <summary>Fills the background of a picture in place.</summary>
    /// <param name="image">The picture, changed in place.</param>
    /// <param name="coverage">Which texels belong to an island: width × height of them, row zero first.</param>
    /// <param name="mode">What fills the background.</param>
    /// <param name="distance">How many texels the islands grow by, for every mode but <see cref="PaddingMode.Infinite" />.</param>
    /// <param name="background">For <see cref="PaddingMode.Colour" />, one value per lane; a missing lane is zero.</param>
    /// <returns>How many texels the islands grew into.</returns>
    /// <exception cref="ArgumentException">The coverage is not the picture's size, or the distance is negative.</exception>
    public static int Pad(
        FloatImage image,
        ReadOnlySpan<bool> coverage,
        PaddingMode mode,
        int distance,
        ReadOnlySpan<float> background = default
    ) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(distance);

        if (coverage.Length != image.Width * image.Height) {
            throw new ArgumentException(
                $"The coverage has {coverage.Length} texels and the picture {image.Width * image.Height}.",
                nameof(coverage)
            );
        }

        if (mode == PaddingMode.None) {
            return 0;
        }

        var filled = coverage.ToArray();

        // An empty layout has nothing to grow from: every mode leaves the picture as it was, rather than
        // painting the whole of it one colour because no island happened to be found.
        if (Array.IndexOf(filled, true) < 0) {
            return 0;
        }

        var grown = Dilate(image, filled, mode == PaddingMode.Infinite ? int.MaxValue : distance);

        switch (mode) {
            case PaddingMode.Transparent:
                Fill(image, filled, []);
                break;
            case PaddingMode.Colour:
                Fill(image, filled, background);
                break;
            case PaddingMode.Diffusion:
                Diffuse(image, filled);
                break;
        }

        return grown;
    }

    /// <summary>Grows the filled texels outward by a number of rounds, in place.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="filled">Which texels hold a value; grown in place.</param>
    /// <param name="rounds">How many rounds, or <see cref="int.MaxValue" /> for until nothing grows.</param>
    /// <returns>How many texels were filled.</returns>
    static int Dilate(FloatImage image, bool[] filled, int rounds) {
        var (width, height, lanes) = (image.Width, image.Height, image.Channels);
        var samples = image.Samples;
        var queued = new bool[filled.Length];
        List<int> frontier = [];

        // The first frontier: every empty texel beside an island.
        for (var at = 0; at < filled.Length; at++) {
            if (!filled[at] && Beside(filled, at, width, height)) {
                frontier.Add(at);
                queued[at] = true;
            }
        }

        var total = 0;
        var values = new float[lanes];
        Span<int> offsets = stackalloc int[4];

        for (var round = 0; round < rounds && frontier.Count > 0; round++) {
            // ⚠ Values first, from what was filled before this round; the flags after, all at once.
            var computed = new float[frontier.Count * lanes];

            for (var index = 0; index < frontier.Count; index++) {
                Mean(samples, filled, frontier[index], width, height, lanes, values);
                values.CopyTo(computed, index * lanes);
            }

            List<int> next = [];

            for (var index = 0; index < frontier.Count; index++) {
                var at = frontier[index];
                Array.Copy(computed, index * lanes, samples, at * lanes, lanes);
                filled[at] = true;
            }

            foreach (var at in frontier) {
                Neighbours(at, width, height, offsets);

                foreach (var neighbour in offsets) {
                    if (neighbour >= 0 && !filled[neighbour] && !queued[neighbour]) {
                        queued[neighbour] = true;
                        next.Add(neighbour);
                    }
                }
            }

            total += frontier.Count;
            frontier = next;
        }

        return total;
    }

    static bool Beside(bool[] filled, int at, int width, int height) {
        var (x, y) = (at % width, at / width);
        return (x > 0 && filled[at - 1])
            || (x + 1 < width && filled[at + 1])
            || (y > 0 && filled[at - width])
            || (y + 1 < height && filled[at + width]);
    }

    /// <summary>A texel's four neighbours, left, right, up and down, with -1 for one off the edge.</summary>
    static void Neighbours(int at, int width, int height, Span<int> into) {
        var (x, y) = (at % width, at / width);
        into[0] = x > 0 ? at - 1 : -1;
        into[1] = x + 1 < width ? at + 1 : -1;
        into[2] = y > 0 ? at - width : -1;
        into[3] = y + 1 < height ? at + width : -1;
    }

    /// <summary>The mean of a texel's filled four-neighbours, in a fixed order.</summary>
    static void Mean(float[] samples, bool[] filled, int at, int width, int height, int lanes, float[] into) {
        Array.Clear(into);
        var found = 0;
        Span<int> offsets = stackalloc int[4];
        Neighbours(at, width, height, offsets);

        foreach (var neighbour in offsets) {
            if (neighbour < 0 || !filled[neighbour]) {
                continue;
            }

            for (var lane = 0; lane < lanes; lane++) {
                into[lane] += samples[neighbour * lanes + lane];
            }

            found++;
        }

        for (var lane = 0; lane < lanes; lane++) {
            into[lane] /= found;
        }
    }

    static void Fill(FloatImage image, bool[] filled, ReadOnlySpan<float> background) {
        var lanes = image.Channels;

        for (var at = 0; at < filled.Length; at++) {
            if (filled[at]) {
                continue;
            }

            for (var lane = 0; lane < lanes; lane++) {
                image.Samples[at * lanes + lane] = lane < background.Length ? background[lane] : 0f;
            }
        }
    }

    /// <summary>
    ///     Fills what is still empty by pull-push: a pyramid of weighted means down to one texel,
    ///     then each empty texel takes the level below it, interpolated, on the way back up.
    /// </summary>
    /// <remarks>
    ///     The result has no edges of its own: every empty texel is a blend of the islands near it,
    ///     weighted towards the nearer ones by how many levels down it had to go to find them.
    /// </remarks>
    static void Diffuse(FloatImage image, bool[] filled) {
        var lanes = image.Channels;
        List<(int Width, int Height, float[] Values, float[] Weights)> levels = [];

        var weights = new float[filled.Length];

        for (var at = 0; at < filled.Length; at++) {
            weights[at] = filled[at] ? 1f : 0f;
        }

        var values = new float[image.Samples.Length];

        for (var at = 0; at < filled.Length; at++) {
            for (var lane = 0; lane < lanes; lane++) {
                values[at * lanes + lane] = filled[at] ? image.Samples[at * lanes + lane] : 0f;
            }
        }

        levels.Add((image.Width, image.Height, values, weights));

        // Pull: each level is the weighted mean of the 2×2 above it, and its weight their sum.
        while (levels[^1].Width > 1 || levels[^1].Height > 1) {
            var (width, height, above, aboveWeights) = levels[^1];
            var (w, h) = (Math.Max(1, (width + 1) / 2), Math.Max(1, (height + 1) / 2));
            var sums = new float[w * h * lanes];
            var total = new float[w * h];

            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var from = y * width + x;
                    var to = y / 2 * w + x / 2;
                    var weight = aboveWeights[from];
                    total[to] += weight;

                    for (var lane = 0; lane < lanes; lane++) {
                        sums[to * lanes + lane] += above[from * lanes + lane] * weight;
                    }
                }
            }

            for (var at = 0; at < total.Length; at++) {
                for (var lane = 0; lane < lanes; lane++) {
                    sums[at * lanes + lane] = total[at] > 0f ? sums[at * lanes + lane] / total[at] : 0f;
                }

                total[at] = Math.Min(total[at], 1f);
            }

            levels.Add((w, h, sums, total));
        }

        // Push: an empty texel takes the coarser level, sampled bilinearly at its own centre.
        for (var level = levels.Count - 2; level >= 0; level--) {
            var (width, height, here, hereWeights) = levels[level];
            var (cw, ch, coarse, _) = levels[level + 1];

            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var at = y * width + x;

                    if (hereWeights[at] >= 1f) {
                        continue;
                    }

                    var u = Math.Clamp((x + 0.5f) / 2f - 0.5f, 0f, cw - 1);
                    var v = Math.Clamp((y + 0.5f) / 2f - 0.5f, 0f, ch - 1);
                    var (left, top) = ((int)u, (int)v);
                    var (right, bottom) = (Math.Min(left + 1, cw - 1), Math.Min(top + 1, ch - 1));
                    var (s, t) = (u - left, v - top);
                    var keep = hereWeights[at];

                    for (var lane = 0; lane < lanes; lane++) {
                        var upper = float.Lerp(
                            coarse[(top * cw + left) * lanes + lane],
                            coarse[(top * cw + right) * lanes + lane],
                            s
                        );
                        var lower = float.Lerp(
                            coarse[(bottom * cw + left) * lanes + lane],
                            coarse[(bottom * cw + right) * lanes + lane],
                            s
                        );
                        var pulled = float.Lerp(upper, lower, t);
                        here[at * lanes + lane] = here[at * lanes + lane] * keep + pulled * (1f - keep);
                    }

                    hereWeights[at] = 1f;
                }
            }
        }

        var finest = levels[0].Values;

        for (var at = 0; at < filled.Length; at++) {
            if (!filled[at]) {
                Array.Copy(finest, at * lanes, image.Samples, at * lanes, lanes);
            }
        }
    }
}
