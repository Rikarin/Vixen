// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Core.Collections;

/// <summary>
///     A growable set of bits over 64-bit words: archetype masks, render group masks, dirty flags.
/// </summary>
/// <remarks>
///     A <c>bool[]</c> would be eight times the memory and would test one flag per instruction. This
///     tests sixty-four, which is what makes "which of these ten thousand nodes changed" a scan the
///     cache can keep up with.
/// </remarks>
public sealed class BitSet {
    const int BitsPerWord = 64;
    const int WordShift = 6;

    ulong[] words;

    /// <summary>How many bits the set currently has room for.</summary>
    public int Capacity => words.Length * BitsPerWord;

    /// <summary>The backing words, for bulk operations and for uploading a mask to the GPU.</summary>
    public ReadOnlySpan<ulong> Words => words;

    /// <summary>Creates a set with room for <paramref name="bitCapacity" /> bits, all clear.</summary>
    /// <param name="bitCapacity">How many bits to make room for.</param>
    public BitSet(int bitCapacity = BitsPerWord) {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCapacity);
        words = new ulong[WordCount(bitCapacity)];
    }

    /// <summary>Reads or writes one bit. Reading past the end is <see langword="false" />.</summary>
    /// <param name="index">The bit index.</param>
    /// <returns>Whether the bit is set.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is negative.</exception>
    public bool this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            var word = index >> WordShift;

            // Reading past the end is false rather than an error: a mask is conceptually infinite
            // and zero everywhere it has not been written, and forcing callers to size it first
            // turns every query into two.
            return word < words.Length && (words[word] & (1UL << index)) != 0;
        }

        set {
            if (value) {
                Set(index);
            } else {
                Clear(index);
            }
        }
    }

    /// <summary>Sets a bit, growing the set if needed.</summary>
    /// <param name="index">The bit index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is negative.</exception>
    public void Set(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        EnsureCapacity(index + 1);

        // The shift is already modulo 64 on every architecture the CLR targets, so no mask here.
        words[index >> WordShift] |= 1UL << index;
    }

    /// <summary>Clears a bit. Clearing past the end does nothing and does not grow the set.</summary>
    /// <param name="index">The bit index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is negative.</exception>
    public void Clear(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var word = index >> WordShift;

        if (word < words.Length) {
            words[word] &= ~(1UL << index);
        }
    }

    /// <summary>Clears every bit, keeping the capacity.</summary>
    public void Clear() => Array.Clear(words);

    /// <summary>How many bits are set.</summary>
    /// <returns>The population count.</returns>
    public int PopCount() {
        var total = 0;
        foreach (var word in words) {
            total += BitOperations.PopCount(word);
        }

        return total;
    }

    /// <summary>Whether no bit is set.</summary>
    /// <returns><see langword="true" /> if the set is empty.</returns>
    public bool IsEmpty() {
        foreach (var word in words) {
            if (word != 0) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether every bit set in <paramref name="other" /> is also set here.</summary>
    /// <param name="other">The subset to test for.</param>
    /// <returns><see langword="true" /> if this is a superset.</returns>
    /// <remarks>The query an archetype match is: does this archetype have all the components asked for.</remarks>
    public bool Contains(BitSet other) {
        ArgumentNullException.ThrowIfNull(other);

        for (var i = 0; i < other.words.Length; i++) {
            var required = other.words[i];
            var present = i < words.Length ? words[i] : 0UL;

            if ((present & required) != required) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether any bit is set in both sets.</summary>
    /// <param name="other">The set to test against.</param>
    /// <returns><see langword="true" /> if they overlap.</returns>
    public bool Intersects(BitSet other) {
        ArgumentNullException.ThrowIfNull(other);

        var shared = Math.Min(words.Length, other.words.Length);
        for (var i = 0; i < shared; i++) {
            if ((words[i] & other.words[i]) != 0) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets every bit that is set in <paramref name="other" />.</summary>
    /// <param name="other">The set to merge in.</param>
    public void UnionWith(BitSet other) {
        ArgumentNullException.ThrowIfNull(other);
        EnsureCapacity(other.Capacity);

        for (var i = 0; i < other.words.Length; i++) {
            words[i] |= other.words[i];
        }
    }

    /// <summary>Clears every bit that is not set in <paramref name="other" />.</summary>
    /// <param name="other">The set to intersect with.</param>
    public void IntersectWith(BitSet other) {
        ArgumentNullException.ThrowIfNull(other);

        for (var i = 0; i < words.Length; i++) {
            words[i] &= i < other.words.Length ? other.words[i] : 0UL;
        }
    }

    /// <summary>Clears every bit that is set in <paramref name="other" />.</summary>
    /// <param name="other">The set to subtract.</param>
    public void ExceptWith(BitSet other) {
        ArgumentNullException.ThrowIfNull(other);

        var shared = Math.Min(words.Length, other.words.Length);
        for (var i = 0; i < shared; i++) {
            words[i] &= ~other.words[i];
        }
    }

    /// <summary>
    ///     Enumerates the indices of the set bits, ascending. Skips whole empty words, so a sparse
    ///     set of a million bits costs about as much as the number of bits actually set.
    /// </summary>
    /// <returns>An enumerator.</returns>
    public Enumerator GetEnumerator() => new(words);

    void EnsureCapacity(int bitCapacity) {
        var required = WordCount(bitCapacity);
        if (required > words.Length) {
            Array.Resize(ref words, Math.Max(required, words.Length * 2));
        }
    }

    static int WordCount(int bitCapacity) => (bitCapacity + BitsPerWord - 1) / BitsPerWord;

    /// <summary>Walks the set bits, one word at a time.</summary>
    public struct Enumerator {
        readonly ulong[] words;
        int wordIndex;
        ulong remaining;

        internal Enumerator(ulong[] words) {
            this.words = words;
            wordIndex = -1;
            remaining = 0;
            Current = -1;
        }

        /// <summary>The index of the current set bit.</summary>
        public int Current { get; private set; }

        /// <summary>Advances to the next set bit.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() {
            while (remaining == 0) {
                if (++wordIndex >= words.Length) {
                    return false;
                }

                remaining = words[wordIndex];
            }

            // Take the lowest set bit and clear it, so each word costs one instruction per bit set
            // in it rather than sixty-four tests.
            Current = (wordIndex << WordShift) + BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            return true;
        }
    }
}
