// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Events;

/// <summary>How an event chooses which of its variants to play.</summary>
public enum VariantSelection {
    /// <summary>Every variant plays once before any plays twice, in a fresh order each round.</summary>
    /// <remarks>
    ///     The default, and the one to want. Plain random gives runs — five variants drawn ten times
    ///     will produce a pair back to back about nine times in ten, and a pair is exactly what a
    ///     listener hears as "the sound is broken". A bag cannot, and it also guarantees the rarely
    ///     drawn variant is actually heard, which is what the sound designer recorded it for.
    /// </remarks>
    Shuffle = 0,

    /// <summary>Weighted random, which may repeat.</summary>
    /// <remarks>The only mode that honours a weight of zero as "hardly ever", rather than "once a round".</remarks>
    Random = 1,

    /// <summary>Weighted random, never the same one twice running.</summary>
    /// <remarks>
    ///     Weights and a memory of one. Cheaper to reason about than <see cref="Shuffle" /> when the
    ///     variants are not equally likely — a common shape is one usual sound and three rare ones,
    ///     and a bag would play each rare one as often as the usual one.
    /// </remarks>
    RandomNoRepeat = 2,

    /// <summary>In the order they are written, round and round.</summary>
    /// <remarks>
    ///     For a sequence that means something — a three-part reload, a stepped UI tick. Weights are
    ///     ignored, because an order is not a distribution.
    /// </remarks>
    Sequential = 3
}

/// <summary>Picks the next variant, without allocating and without asking a clock.</summary>
/// <remarks>
///     <para>
///         Split out from <see cref="AudioEvent" /> because "which one plays next" is the part with
///         the interesting behaviour and no dependencies — it is a function of a mode, some weights
///         and a seed, so it can be tested by calling <see cref="Next" /> a thousand times and
///         counting, with no mixer, no device and no clip anywhere near it.
///     </para>
///     <para>
///         <b>Seeded, so a run is reproducible.</b> The same selector built the same way produces the
///         same sequence on every machine and every platform, which is what lets a test assert the
///         order rather than assert a histogram — and what would let a replay or a lockstep
///         simulation keep two machines' audio in step, if one ever wanted that.
///     </para>
///     <para>
///         <b>Game thread only.</b> Nothing here is synchronised: an event is played from gameplay
///         code, and the audio thread never sees a selector.
///     </para>
/// </remarks>
public sealed class VariantSelector {
    readonly float[] weights;
    readonly int[] bag;
    Xorshift32 random;
    int bagCursor;
    int cursor;
    int last = -1;

    /// <summary>How it chooses.</summary>
    public VariantSelection Mode { get; }

    /// <summary>How many variants there are to choose between.</summary>
    public int Count => weights.Length;

    /// <summary>What <see cref="Next" /> returned last, or −1 before the first call.</summary>
    public int Last => last;

    /// <summary>A selector over some weighted variants.</summary>
    /// <param name="weights">
    ///     One per variant. Negatives are treated as zero; all-zero is treated as all-equal, because a
    ///     set of variants that can never be chosen is a content mistake and silence is a bad way to
    ///     report it.
    /// </param>
    /// <param name="mode">How to choose.</param>
    /// <param name="seed">Where the sequence starts.</param>
    public VariantSelector(ReadOnlySpan<float> weights, VariantSelection mode, uint seed = 0) {
        Mode = mode;
        random = new(seed);
        this.weights = new float[weights.Length];

        var total = 0f;

        for (var i = 0; i < weights.Length; i++) {
            this.weights[i] = MathF.Max(weights[i], 0f);
            total += this.weights[i];
        }

        if (total <= 0f) {
            Array.Fill(this.weights, 1f);
        }

        // Only shuffling needs the bag, and only shuffling pays for it.
        bag = mode is VariantSelection.Shuffle ? new int[weights.Length] : [];

        for (var i = 0; i < bag.Length; i++) {
            bag[i] = i;
        }

        // Empty rather than full, so the first call shuffles: a bag left in written order would make
        // the first round of every event in the game play its variants in the order of the file.
        bagCursor = bag.Length;
    }

    /// <summary>Chooses the next variant.</summary>
    /// <returns>Its index, or −1 if there are none.</returns>
    public int Next() {
        if (weights.Length == 0) {
            return -1;
        }

        if (weights.Length == 1) {
            last = 0;
            return 0;
        }

        last = Mode switch {
            VariantSelection.Sequential => Step(),
            VariantSelection.Shuffle => Draw(),
            VariantSelection.RandomNoRepeat => Weighted(last),
            _ => Weighted(-1)
        };

        return last;
    }

    /// <summary>Forgets everything: the bag, the cursor, and what played last.</summary>
    /// <remarks>The seed is not reset, so a reset selector continues the same random sequence.</remarks>
    public void Reset() {
        bagCursor = bag.Length;
        cursor = 0;
        last = -1;
    }

    int Step() {
        var index = cursor;
        cursor = (cursor + 1) % weights.Length;
        return index;
    }

    int Draw() {
        if (bagCursor >= bag.Length) {
            Shuffle();
        }

        return bag[bagCursor++];
    }

    void Shuffle() {
        for (var i = bag.Length - 1; i > 0; i--) {
            var j = random.NextIndex(i + 1);
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }

        // The bag boundary is where shuffling still repeats: the last of one round and the first of
        // the next are independent draws, so about one round in N opens with the sound that just
        // played — and a repeat across a boundary is as audible as any other. Swapping it away costs
        // one comparison a round. The bag holds distinct indices, so anything else will do.
        if (bag[0] == last) {
            var other = 1 + random.NextIndex(bag.Length - 1);
            (bag[0], bag[other]) = (bag[other], bag[0]);
        }

        bagCursor = 0;
    }

    /// <summary>Draws by weight, optionally refusing one index.</summary>
    /// <param name="excluded">An index that must not come out, or −1 for no restriction.</param>
    /// <returns>The chosen index.</returns>
    /// <remarks>
    ///     A linear walk and not a binary search over a cumulative table, because the exclusion
    ///     changes the total — a table would have to be rebuilt or corrected for every draw, and a
    ///     variant list is a handful of entries. The walk is exact and unbiased; a retry loop, which
    ///     is the usual way to do this, is neither when one weight dominates.
    /// </remarks>
    int Weighted(int excluded) {
        var total = 0f;

        for (var i = 0; i < weights.Length; i++) {
            if (i != excluded) {
                total += weights[i];
            }
        }

        // Every eligible variant has a weight of zero — which only happens when the one variant with
        // any weight is the one just played. Refusing to repeat is the stronger of the two
        // instructions, so something else plays.
        if (total <= 0f) {
            return excluded == 0 ? 1 : 0;
        }

        var target = random.NextUnit() * total;

        for (var i = 0; i < weights.Length; i++) {
            if (i == excluded) {
                continue;
            }

            target -= weights[i];

            if (target <= 0f) {
                return i;
            }
        }

        // Only reachable through floating-point drift in the subtraction above, and the last eligible
        // index is the one the loop was about to return anyway.
        return excluded == weights.Length - 1 ? weights.Length - 2 : weights.Length - 1;
    }
}
