// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>A reproducible random stream, seeded by what the numbers are for.</summary>
/// <remarks>
///     <para>
///         <b>Reproducible from an event id is the requirement, and it is not a nicety.</b> Doc 28
///         § Loot: "the RNG is the kernel's deterministic stream seeded per drop event, so a drop is
///         reproducible from its event id — which is what makes <em>the log says you rolled a 3</em>
///         answerable". A support ticket about a drop, a report about a crit, a dispute about a
///         crafting quality: all of them are answerable only if the roll can be recomputed from
///         something that was written down.
///     </para>
///     <para>
///         <b>PCG-XSH-RR over a 64-bit LCG, and the choice is about being reimplementable.</b> The
///         whole algorithm is four integer operations with two published constants, so a tool in
///         another language can reproduce a roll — which is what an audit actually needs.
///         <see cref="System.Random" /> cannot do that: its algorithm is an implementation detail
///         that has already changed once between .NET versions, and a seeded stream whose meaning
///         depends on the runtime is not an audit trail.
///     </para>
///     <para>
///         ⚠ <b>Seeds are mixed, not combined with an operator.</b> <c>id ^ salt</c> and
///         <c>id + salt</c> both have inputs that cancel — <c>Vixen.Ai</c>'s <c>AgentRandom</c> shipped
///         with an XOR that made every agent in the world draw the same number, because the seed and
///         the entity were the same hash. <see cref="Mix" /> is SplatMix64's finaliser, which has no
///         such pair, and every constructor here goes through it.
///     </para>
/// </remarks>
public struct GameplayRandom {
    const ulong Multiplier = 6364136223846793005ul;
    const ulong Increment = 1442695040888963407ul;

    ulong state;

    /// <summary>Makes a stream from a seed.</summary>
    /// <param name="seed">The seed. Any value, including zero.</param>
    public GameplayRandom(ulong seed) {
        state = 0;
        Step();
        state += Mix(seed);
        Step();
    }

    /// <summary>Where the stream has got to. Enough to resume it exactly.</summary>
    /// <remarks>
    ///     What a durable roll stores when a sequence has to survive a restart — a pity counter's
    ///     stream, a persistent world event's. A fresh <see cref="GameplayRandom" /> built from
    ///     <see cref="Resume" /> continues rather than restarts.
    /// </remarks>
    public readonly ulong State => state;

    /// <summary>A stream for one event, so the same event always rolls the same way.</summary>
    /// <param name="eventId">What the roll is for — a drop event's id, an encounter's, a craft's.</param>
    /// <param name="salt">Which roll within that event: the first item, the second, the quality.</param>
    /// <returns>The stream.</returns>
    public static GameplayRandom For(ulong eventId, ulong salt = 0) => new(Mix(eventId) ^ Mix(salt + 0x9e3779b97f4a7c15ul));

    /// <summary>Continues a stream from a stored state.</summary>
    /// <param name="state">What <see cref="State" /> reported.</param>
    /// <returns>The stream, where it left off.</returns>
    public static GameplayRandom Resume(ulong state) {
        var random = default(GameplayRandom);
        random.state = state;

        return random;
    }

    /// <summary>The next 32 bits.</summary>
    /// <returns>The number.</returns>
    public uint NextUInt() {
        var previous = state;
        Step();

        // PCG-XSH-RR: xorshift the high bits down, then rotate by the top five.
        var xorshifted = (uint)(((previous >> 18) ^ previous) >> 27);
        var rotation = (int)(previous >> 59);

        return (xorshifted >> rotation) | (xorshifted << ((-rotation) & 31));
    }

    /// <summary>The next number in <c>[0, 1)</c>.</summary>
    /// <returns>The number.</returns>
    /// <remarks>
    ///     Twenty-four bits over 2²⁴, because a float has twenty-four bits of mantissa and dividing by
    ///     2³² instead produces values that round to exactly 1.0 — which turns
    ///     <c>NextFloat() &lt; chance</c> into a rare, unreproducible off-by-one at the top of every
    ///     table.
    /// </remarks>
    public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    /// <summary>The next number in <c>[0, bound)</c>.</summary>
    /// <param name="bound">One past the largest. Zero or less gives zero.</param>
    /// <returns>The number.</returns>
    /// <remarks>
    ///     Debiased by rejection rather than by a modulo, because a plain <c>% bound</c> makes the low
    ///     values likelier by up to one part in <c>2³² / bound</c> — invisible on a coin flip and
    ///     measurable on a loot table, which is exactly where this gets used.
    /// </remarks>
    public int NextInt(int bound) {
        if (bound <= 0) {
            return 0;
        }

        var limit = (uint)bound;
        var threshold = (uint)-(int)limit % limit;

        while (true) {
            var drawn = NextUInt();

            if (drawn >= threshold) {
                return (int)(drawn % limit);
            }
        }
    }

    /// <summary>The next number in <c>[minimum, bound)</c>.</summary>
    /// <param name="minimum">The smallest.</param>
    /// <param name="bound">One past the largest.</param>
    /// <returns>The number.</returns>
    public int NextInt(int minimum, int bound) => minimum + NextInt(bound - minimum);

    /// <summary>Whether something with this probability happened.</summary>
    /// <param name="probability">The chance, from zero to one.</param>
    /// <returns>Whether it did.</returns>
    /// <remarks>
    ///     Zero never happens and one always does, both exactly: <see cref="NextFloat" /> is strictly
    ///     below one, so <c>&lt; 1f</c> is always true and <c>&lt; 0f</c> never is. A designer writing
    ///     a guaranteed proc gets one.
    /// </remarks>
    public bool Chance(float probability) => NextFloat() < probability;

    /// <summary>Picks an index in proportion to a list of weights.</summary>
    /// <param name="weights">The weights. Negative ones count as zero.</param>
    /// <returns>The index, or −1 when every weight is zero.</returns>
    /// <remarks>
    ///     One pass to total and one to walk, rather than a prefix-sum array, because a loot table's
    ///     entry list is short and an allocation per drop is a per-kill allocation. The comparison is
    ///     strictly-less so that a zero-weight entry can never be picked, however the float
    ///     accumulation lands.
    /// </remarks>
    public int Pick(ReadOnlySpan<float> weights) {
        var total = 0f;

        foreach (var weight in weights) {
            if (weight > 0f) {
                total += weight;
            }
        }

        if (total <= 0f) {
            return -1;
        }

        var roll = NextFloat() * total;
        var running = 0f;

        for (var index = 0; index < weights.Length; index++) {
            if (weights[index] <= 0f) {
                continue;
            }

            running += weights[index];

            if (roll < running) {
                return index;
            }
        }

        // Only reachable when the accumulation lands a hair short of the total. Whatever the last
        // positive weight was is the right answer; returning −1 here would be a drop that silently
        // did not happen.
        for (var index = weights.Length - 1; index >= 0; index--) {
            if (weights[index] > 0f) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>SplitMix64's finaliser: an avalanche with no pair of inputs that cancels.</summary>
    /// <param name="value">The value to mix.</param>
    /// <returns>The mixed value.</returns>
    public static ulong Mix(ulong value) {
        value += 0x9e3779b97f4a7c15ul;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9ul;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebul;

        return value ^ (value >> 31);
    }

    void Step() => state = (state * Multiplier) + Increment;
}
