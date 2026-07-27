// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Ui.Styling;

/// <summary>A 128-bit summary of everything an element's ancestors are called.</summary>
/// <remarks>
///     <para>
///         The point is to answer <c>.sidebar .row</c> without walking up the tree. Matching a
///         descendant combinator right-to-left means climbing from the element to the root looking
///         for something that matches <c>.sidebar</c>, and in a deep tree that is most of the cost of
///         matching — paid for every rule that happens to end in <c>.row</c>, nearly all of which
///         will not match. Asking a bloom filter first turns "climb the tree and find nothing" into
///         two loads and a test.
///     </para>
///     <para>
///         A bloom filter can say "definitely not" and "probably yes", which is exactly the shape
///         needed: a false positive costs the tree walk that would have happened anyway, and a false
///         negative is impossible. Gecko and Servo do the same thing for the same reason.
///     </para>
///     <para>
///         Two hashes per name into 128 bits. Doc 09 specifies the width; the two-hash scheme is the
///         standard trade — one hash leaves too many collisions at the occupancies a real document
///         reaches, and four costs more to build than the walks it saves.
///     </para>
/// </remarks>
struct AncestorBloom {
    ulong low;
    ulong high;

    /// <summary>Records that an ancestor is called this.</summary>
    /// <param name="nameId">An interned tag, id or class name.</param>
    public void Add(int nameId) {
        var (first, second) = Bits(nameId);
        Set(first);
        Set(second);
    }

    /// <summary>Whether an ancestor could be called this.</summary>
    /// <param name="nameId">An interned tag, id or class name.</param>
    /// <returns>
    ///     <see langword="false" /> if certainly no ancestor is; <see langword="true" /> if one may
    ///     be, which still has to be confirmed by walking.
    /// </returns>
    public readonly bool MightContain(int nameId) {
        var (first, second) = Bits(nameId);
        return Test(first) && Test(second);
    }

    /// <summary>How many of the 128 bits are set. For diagnostics and tests.</summary>
    public readonly int PopulationCount => BitOperations.PopCount(low) + BitOperations.PopCount(high);

    void Set(int bit) {
        if (bit < 64) {
            low |= 1UL << bit;
        } else {
            high |= 1UL << (bit - 64);
        }
    }

    readonly bool Test(int bit) => bit < 64 ? (low & (1UL << bit)) != 0 : (high & (1UL << (bit - 64))) != 0;

    static (int First, int Second) Bits(int nameId) {
        // Interned ids are dense small integers assigned in first-seen order, which is close to the
        // worst possible input for a filter that just takes low bits: every name in a document would
        // land in the first few. Mixing first is what makes the occupancy even.
        var mixed = Mix((uint) nameId);
        return ((int) (mixed & 127), (int) ((mixed >> 7) & 127));
    }

    static uint Mix(uint value) {
        // MurmurHash3's finaliser. Cheap, and it spreads the low bits of a counter across the word,
        // which is the only property needed here.
        value ^= value >> 16;
        value *= 0x85EBCA6B;
        value ^= value >> 13;
        value *= 0xC2B2AE35;
        value ^= value >> 16;
        return value;
    }
}
