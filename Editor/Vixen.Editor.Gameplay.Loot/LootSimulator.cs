// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Gameplay;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;

namespace Vixen.Editor.Gameplay.Loot;

/// <summary>What one item did over a simulation.</summary>
/// <param name="Item">Which item.</param>
/// <param name="Name">What a designer calls it, or its id when this build has no such item.</param>
/// <param name="Drops">How many rows dropped it.</param>
/// <param name="Events">How many events dropped at least one.</param>
/// <param name="Total">How many of it dropped, stacks summed.</param>
/// <param name="Smallest">The smallest stack that dropped.</param>
/// <param name="Largest">The largest.</param>
public readonly record struct LootItemStatistics(
    DefId Item,
    string Name,
    int Drops,
    int Events,
    long Total,
    int Smallest,
    int Largest
) {
    /// <summary>How often an event dropped at least one, as a fraction.</summary>
    /// <param name="events">How many events were simulated.</param>
    /// <returns>The rate.</returns>
    public double RateOver(int events) => events <= 0 ? 0 : Events / (double)events;

    /// <summary>How many dropped per event on average, stacks counted.</summary>
    /// <param name="events">How many events were simulated.</param>
    /// <returns>The average.</returns>
    public double PerEvent(int events) => events <= 0 ? 0 : Total / (double)events;
}

/// <summary>What a run of bad luck looked like over a simulation.</summary>
/// <param name="Hits">How many times a pity row dropped.</param>
/// <param name="Misses">How many attempts did not.</param>
/// <param name="LongestDrought">The longest run of consecutive misses.</param>
/// <param name="MeanAttempts">How many misses a hit followed on average.</param>
/// <param name="Guarantee">What the policy promised, or zero for no guarantee.</param>
public readonly record struct LootPityStatistics(
    int Hits,
    int Misses,
    int LongestDrought,
    double MeanAttempts,
    int Guarantee
) {
    /// <summary>Whether the policy kept its promise.</summary>
    /// <remarks>
    ///     ⚠ <b>The one number in a preview that is a bug report rather than a balance figure.</b> A
    ///     drought longer than the guarantee is either a content mistake — a ramp that never
    ///     starts — or an evaluator that stopped honouring the policy, and neither is something a
    ///     designer should have to notice by reading a table of rates.
    /// </remarks>
    public bool GuaranteeHeld => Guarantee <= 0 || LongestDrought <= Guarantee;
}

/// <summary>What a simulation found.</summary>
public sealed class LootSimulation {
    internal LootSimulation(
        int events,
        int emptyEvents,
        IReadOnlyList<LootItemStatistics> items,
        LootPityStatistics? pity
    ) {
        Events = events;
        EmptyEvents = emptyEvents;
        Items = items;
        Pity = pity;
    }

    /// <summary>How many events were rolled.</summary>
    public int Events { get; }

    /// <summary>How many of them dropped nothing at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The number a designer most needs and least expects to see.</b> A table whose weighted
    ///     rows are all conditional drops nothing on an ordinary kill, and that is invisible in the
    ///     authored file.
    /// </remarks>
    public int EmptyEvents { get; }

    /// <summary>What each item did, most frequent first.</summary>
    public IReadOnlyList<LootItemStatistics> Items { get; }

    /// <summary>What pity did, or null when the table has none.</summary>
    public LootPityStatistics? Pity { get; }
}

/// <summary>Rolls a table many times and reports what happened.</summary>
/// <remarks>
///     <para>
///         <b>It runs <see cref="LootEvaluator" />, not an approximation of it</b> — which is doc 28
///         § Loot's actual requirement ("simulated in the editor with the real evaluator") and the
///         reason the evaluator is a library rather than a script. A simulator with its own
///         arithmetic is a second set of odds, and the one a designer balances against is the one
///         that is wrong.
///     </para>
///     <para>
///         ⚠ <b>Consecutive event ids from a first id the caller chose.</b> A simulation is
///         reproducible for the same reason a drop is, so rerunning after an edit shows what the edit
///         did rather than what the weather did.
///     </para>
///     <para>
///         ⚠ <b>A fresh pity store per run.</b> A simulation that inherited a live player's bad luck
///         would report a rate nobody else will ever see.
///     </para>
/// </remarks>
public static class LootSimulator {
    /// <summary>How many events a preview runs by default.</summary>
    /// <remarks>
    ///     Enough that a one-per-cent row shows a stable rate, and few enough to finish inside a UI
    ///     frame budget on a table of any realistic size.
    /// </remarks>
    public const int DefaultEvents = 10000;

    /// <summary>Rolls a table many times.</summary>
    /// <param name="loot">Where tables come from.</param>
    /// <param name="table">Which table.</param>
    /// <param name="items">Where item names come from, or null to report ids.</param>
    /// <param name="events">How many events to roll.</param>
    /// <param name="firstEventId">The first event id, so a run is reproducible.</param>
    /// <param name="context">What is true of the kill, or null.</param>
    /// <param name="player">Whose rolls, for pity.</param>
    /// <returns>What happened.</returns>
    public static LootSimulation Run(
        LootLibrary loot,
        LootTable table,
        ItemLibrary? items = null,
        int events = DefaultEvents,
        ulong firstEventId = 1,
        LootContext? context = null,
        ulong player = 1
    ) {
        ArgumentNullException.ThrowIfNull(loot);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegative(events);

        var pity = table.HasPity ? new CountingPityStore() : null;
        var counters = new Dictionary<uint, Counter>();
        var seenThisEvent = new HashSet<uint>();
        var empty = 0;

        for (var index = 0; index < events; index++) {
            var result = LootEvaluator.Roll(loot, table, firstEventId + (ulong)index, player, context, pity);

            if (result.Drops.Count == 0) {
                empty++;

                continue;
            }

            seenThisEvent.Clear();

            foreach (var drop in result.Drops) {
                ref var counter = ref CollectionsMarshal.GetValueRefOrAddDefault(counters, drop.Item.Value, out var existed);

                if (!existed) {
                    counter.Smallest = int.MaxValue;
                }

                counter.Drops++;
                counter.Total += drop.Count;
                counter.Smallest = Math.Min(counter.Smallest, drop.Count);
                counter.Largest = Math.Max(counter.Largest, drop.Count);

                if (seenThisEvent.Add(drop.Item.Value)) {
                    counter.Events++;
                }
            }
        }

        var statistics = new List<LootItemStatistics>(counters.Count);

        foreach (var (id, counter) in counters) {
            var item = new DefId(id);

            statistics.Add(
                new(
                    item,
                    items?.Find(item)?.Definition.DisplayName is { Length: > 0 } name ? name : item.ToString(),
                    counter.Drops,
                    counter.Events,
                    counter.Total,
                    counter.Smallest == int.MaxValue ? 0 : counter.Smallest,
                    counter.Largest
                )
            );
        }

        // Most frequent first, ties by name, so a rerun of the same table lists its rows in the same
        // order and a designer comparing two runs is comparing rows rather than positions.
        statistics.Sort(
            static (left, right) => right.Events != left.Events
                ? right.Events.CompareTo(left.Events)
                : string.CompareOrdinal(left.Name, right.Name)
        );

        return new(events, empty, statistics, pity?.Summarise(table.Pity?.GuaranteedAt ?? 0));
    }

    struct Counter {
        public int Drops;
        public int Events;
        public long Total;
        public int Smallest;
        public int Largest;
    }

    /// <summary>A pity store that also counts what it was told.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted here rather than inferred from the drops, because the two are not the same
    ///     question.</b> Whether a pity row dropped is visible in the result; whether the evaluator
    ///     considered it an <em>attempt</em> is not — a row excluded by its conditions is not a miss,
    ///     and counting it as one would report a drought the policy never promised anything about.
    ///     The store is the only thing that knows, so the store is what counts.
    /// </remarks>
    sealed class CountingPityStore : IPityStore {
        readonly MemoryPityStore inner = new();

        int hits;
        int misses;
        int longest;
        long attemptsAtHits;

        public int AttemptsOf(PityKey key) => inner.AttemptsOf(key);

        public void Record(PityKey key, bool hit) {
            var before = inner.AttemptsOf(key);

            if (hit) {
                hits++;
                attemptsAtHits += before;
            } else {
                misses++;
                longest = Math.Max(longest, before + 1);
            }

            inner.Record(key, hit);
        }

        public LootPityStatistics Summarise(int guarantee) =>
            new(hits, misses, longest, hits == 0 ? 0 : attemptsAtHits / (double)hits, guarantee);
    }
}
