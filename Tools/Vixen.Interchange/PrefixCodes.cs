// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Interchange;

/// <summary>Length-limited canonical Huffman codes, which JPEG and lossless WebP both write.</summary>
/// <remarks>
///     <para>
///         <b>The limit is applied the way JPEG's Annex K.2 does it</b>: build an unlimited code,
///         then repeatedly take two codes from the deepest level, move one up a level and turn a
///         shorter code into two codes one level deeper. That keeps the code complete, so a
///         decoder that refuses an incomplete tree — lossless WebP's does — accepts it.
///     </para>
///     <para>
///         ⚠ <b>Codes are canonical as deflate defines them</b>: shorter codes first, and within a
///         length by symbol. Both formats derive the codes from the lengths alone, so any other
///         assignment writes a file whose every symbol decodes as a different one.
///     </para>
/// </remarks>
static class PrefixCodes {
    /// <summary>Code lengths for symbol counts, none longer than <paramref name="limit" />.</summary>
    /// <param name="counts">How often each symbol occurs; zero for one that never does.</param>
    /// <param name="limit">The longest code allowed.</param>
    /// <returns>A length per symbol, zero for an unused one. A lone used symbol gets length one.</returns>
    public static int[] Lengths(long[] counts, int limit) {
        var lengths = new int[counts.Length];
        List<int> used = [];

        for (var symbol = 0; symbol < counts.Length; symbol++) {
            if (counts[symbol] > 0) {
                used.Add(symbol);
            }
        }

        if (used.Count == 0) {
            return lengths;
        }

        if (used.Count == 1) {
            lengths[used[0]] = 1;
            return lengths;
        }

        // Plain Huffman over the used symbols. Ties go to the lower node index, so a run is repeatable.
        var parents = new int[(used.Count * 2) - 1];
        var queue = new PriorityQueue<int, (long, int)>();

        for (var at = 0; at < used.Count; at++) {
            queue.Enqueue(at, (counts[used[at]], at));
        }

        var next = used.Count;

        while (queue.Count > 1) {
            queue.TryDequeue(out var first, out var a);
            queue.TryDequeue(out var second, out var b);
            parents[first] = next;
            parents[second] = next;
            queue.Enqueue(next, (a.Item1 + b.Item1, next));
            next++;
        }

        parents[next - 1] = -1;
        var depth = new int[next];

        for (var node = next - 2; node >= 0; node--) {
            depth[node] = depth[parents[node]] + 1;
        }

        // How many codes of each length, then Annex K.2's adjustment down to the limit.
        var deepest = depth.Take(used.Count).Max();
        var bits = new int[Math.Max(deepest, limit) + 1];

        for (var at = 0; at < used.Count; at++) {
            bits[depth[at]]++;
        }

        for (var length = bits.Length - 1; length > limit; length--) {
            while (bits[length] > 0) {
                var shorter = length - 2;

                while (bits[shorter] == 0) {
                    shorter--;
                }

                bits[length] -= 2;
                bits[length - 1]++;
                bits[shorter + 1] += 2;
                bits[shorter]--;
            }
        }

        // The most frequent symbols get the shortest codes.
        var order = used.OrderByDescending(symbol => counts[symbol]).ThenBy(static symbol => symbol).ToList();
        var taken = 0;

        for (var length = 1; length <= limit; length++) {
            for (var count = 0; count < bits[length]; count++) {
                lengths[order[taken++]] = length;
            }
        }

        return lengths;
    }

    /// <summary>The canonical codes for some lengths, most significant bit first.</summary>
    public static int[] Codes(ReadOnlySpan<int> lengths) {
        var longest = 0;

        foreach (var length in lengths) {
            longest = Math.Max(longest, length);
        }

        var perLength = new int[longest + 2];

        foreach (var length in lengths) {
            if (length > 0) {
                perLength[length]++;
            }
        }

        var first = new int[longest + 2];
        var code = 0;

        for (var length = 1; length <= longest; length++) {
            code = (code + perLength[length - 1]) << 1;
            first[length] = code;
        }

        var codes = new int[lengths.Length];

        for (var symbol = 0; symbol < lengths.Length; symbol++) {
            if (lengths[symbol] > 0) {
                codes[symbol] = first[lengths[symbol]]++;
            }
        }

        return codes;
    }

    /// <summary>A code's bits in the other order, for a format that reads least significant first.</summary>
    public static int Reverse(int code, int length) {
        var reversed = 0;

        for (var bit = 0; bit < length; bit++) {
            reversed = (reversed << 1) | ((code >> bit) & 1);
        }

        return reversed;
    }
}
