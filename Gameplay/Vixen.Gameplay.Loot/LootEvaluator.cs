// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;

namespace Vixen.Gameplay.Loot;

/// <summary>One thing that dropped.</summary>
/// <param name="Item">Which item.</param>
/// <param name="Count">How many.</param>
/// <param name="Seed">What its affixes roll from.</param>
/// <param name="Table">Which table it came out of, which for a nested drop is the inner one.</param>
/// <param name="Pity">Whether it dropped because the run of bad luck ran out.</param>
public readonly record struct LootDrop(DefId Item, int Count, uint Seed, DefId Table, bool Pity);

/// <summary>What one drop event produced.</summary>
public sealed class LootResult {
    internal LootResult(ulong eventId, ulong player, IReadOnlyList<LootDrop> drops) {
        EventId = eventId;
        Player = player;
        Drops = drops;
    }

    /// <summary>Nothing dropped.</summary>
    public static LootResult Empty { get; } = new(0, 0, []);

    /// <summary>What caused it. The one number a roll is reproducible from.</summary>
    public ulong EventId { get; }

    /// <summary>Whose roll it was, for a personal drop.</summary>
    public ulong Player { get; }

    /// <summary>What dropped, in the order it was rolled.</summary>
    public IReadOnlyList<LootDrop> Drops { get; }

    /// <summary>Whether anything dropped at all.</summary>
    public bool IsSome => Drops.Count > 0;

    /// <summary>Turns the drops into instances.</summary>
    /// <param name="items">Where item templates come from.</param>
    /// <returns>Them. A drop of an item this build does not know is skipped.</returns>
    public IReadOnlyList<ItemInstance> Materialise(ItemLibrary items) {
        ArgumentNullException.ThrowIfNull(items);

        var instances = new List<ItemInstance>(Drops.Count);

        foreach (var drop in Drops) {
            if (items.Find(drop.Item) is { } template) {
                instances.Add(template.Create(drop.Count, drop.Seed));
            }
        }

        return instances;
    }
}

/// <summary>Rolls a table. The same evaluator the editor's simulator runs.</summary>
/// <remarks>
///     <para>
///         <b>Reproducible from the event id, which is doc 28 § Loot's requirement and not a
///         nicety.</b> The stream is seeded from <c>(eventId, player)</c> and nothing else, so a
///         support ticket about a drop can be recomputed a year later — which is what makes "the log
///         says you rolled a 3" answerable.
///     </para>
///     <para>
///         ⚠ <b>Therefore the evaluation order is part of the contract.</b> Independent rows first,
///         in the order they were authored, then the weighted picks; a nested table is rolled where
///         its row sits. Reordering the phases, or sorting the rows, would change what every existing
///         event id produces.
///     </para>
///     <para>
///         ⚠ <b>A row whose conditions fail is absent, not skipped.</b> The remaining weights are
///         renormalised over what is left, which is what a designer writing "only on Heroic" means —
///         the alternative is a table that quietly drops nothing a fraction of the time.
///     </para>
/// </remarks>
public static class LootEvaluator {
    /// <summary>How deep a table may nest. Refused beyond, rather than recursing for ever.</summary>
    /// <remarks>
    ///     A cycle in a loot tree is a content bug — table A rolls B rolls A — and content is not
    ///     something a realm gets to trust. Eight is deeper than any authored tree and shallow enough
    ///     that the walk cannot run away.
    /// </remarks>
    public const int MaximumDepth = 8;

    /// <summary>Rolls a table once for one player.</summary>
    /// <param name="loot">Where tables come from.</param>
    /// <param name="table">Which table.</param>
    /// <param name="eventId">What caused the drop. The roll is reproducible from this.</param>
    /// <param name="player">Whose roll, for pity and for a personal drop. Zero is nobody in particular.</param>
    /// <param name="context">What is true of the kill, or null for nothing.</param>
    /// <param name="pity">Where a run of bad luck is remembered, or null for none.</param>
    /// <returns>What dropped.</returns>
    public static LootResult Roll(
        LootLibrary loot,
        LootTable table,
        ulong eventId,
        ulong player = 0,
        LootContext? context = null,
        IPityStore? pity = null
    ) {
        ArgumentNullException.ThrowIfNull(loot);
        ArgumentNullException.ThrowIfNull(table);

        var random = GameplayRandom.For(eventId, player);
        var drops = new List<LootDrop>();

        Evaluate(loot, table, context ?? LootContext.Empty, pity, player, ref random, drops, 0);

        return new(eventId, player, drops);
    }

    /// <summary>Rolls a table for a group, honouring the distribution.</summary>
    /// <param name="loot">Where tables come from.</param>
    /// <param name="table">Which table.</param>
    /// <param name="eventId">What caused the drop.</param>
    /// <param name="distribution">How it is shared out.</param>
    /// <param name="participants">Who was there.</param>
    /// <param name="context">What is true of the kill, or null.</param>
    /// <param name="pity">Where a run of bad luck is remembered, or null.</param>
    /// <returns>One result per participant for a personal drop, one shared result otherwise.</returns>
    /// <remarks>
    ///     <b>Policies on the drop, not different code paths.</b> Personal rolls the same table once
    ///     per participant; the other three roll it once and differ only in who may take what out of
    ///     the window afterwards — which is a flow, not an evaluation, and belongs to whatever owns
    ///     the window.
    /// </remarks>
    public static IReadOnlyList<LootResult> Roll(
        LootLibrary loot,
        LootTable table,
        ulong eventId,
        LootDistribution distribution,
        IReadOnlyList<ulong> participants,
        LootContext? context = null,
        IPityStore? pity = null
    ) {
        ArgumentNullException.ThrowIfNull(participants);

        if (distribution != LootDistribution.Personal) {
            return [Roll(loot, table, eventId, 0, context, pity)];
        }

        var results = new LootResult[participants.Count];

        for (var index = 0; index < participants.Count; index++) {
            results[index] = Roll(loot, table, eventId, participants[index], context, pity);
        }

        return results;
    }

    static void Evaluate(
        LootLibrary loot,
        LootTable table,
        LootContext context,
        IPityStore? pity,
        ulong player,
        ref GameplayRandom random,
        List<LootDrop> drops,
        int depth
    ) {
        if (depth >= MaximumDepth) {
            return;
        }

        Span<int> weighted = stackalloc int[Math.Min(table.Entries.Length, 256)];
        Span<float> weights = stackalloc float[weighted.Length];
        var count = 0;

        var attempts = pity is not null && table.HasPity ? pity.AttemptsOf(new(player, table.Id)) : 0;
        var pityRolled = false;
        var pityHit = false;

        for (var index = 0; index < table.Entries.Length; index++) {
            var entry = table.Entries[index];

            if (!entry.Conditions.IsMetBy(context)) {
                continue;
            }

            if (entry.IsWeighted && count < weighted.Length) {
                weighted[count] = index;
                weights[count] = entry.Weight;
                count++;
            }

            if (!entry.IsIndependent) {
                continue;
            }

            var chance = entry.Chance;
            var usesPity = entry.UsesPity && table.Pity is { } policy;

            if (usesPity) {
                pityRolled = true;
                chance = PityChance(entry.Chance, table.Pity!, attempts);
            }

            if (!random.Chance(chance)) {
                continue;
            }

            if (usesPity) {
                pityHit = true;
            }

            Drop(loot, table, entry, context, pity, player, ref random, drops, depth, usesPity && attempts > 0);
        }

        if (pityRolled && pity is not null) {
            pity.Record(new(player, table.Id), pityHit);
        }

        for (var roll = 0; roll < table.Rolls && count > 0; roll++) {
            var picked = random.Pick(weights[..count]);

            if (picked < 0) {
                break;
            }

            Drop(loot, table, table.Entries[weighted[picked]], context, pity, player, ref random, drops, depth, false);
        }
    }

    static void Drop(
        LootLibrary loot,
        LootTable table,
        LootEntry entry,
        LootContext context,
        IPityStore? pity,
        ulong player,
        ref GameplayRandom random,
        List<LootDrop> drops,
        int depth,
        bool fromPity
    ) {
        if (entry.Table.IsSome) {
            if (loot.Find(entry.Table) is { } nested) {
                Evaluate(loot, nested, context, pity, player, ref random, drops, depth + 1);
            }

            return;
        }

        // The count first and the seed second, always, so that changing an entry's range does not
        // shift the seed of everything after it in the same event.
        var count = entry.Minimum == entry.Maximum
            ? entry.Minimum
            : random.NextInt(entry.Minimum, entry.Maximum + 1);

        drops.Add(new(entry.Item, count, random.NextUInt(), table.Id, fromPity));
    }

    /// <summary>What a row's chance is after a run of bad luck.</summary>
    /// <param name="chance">Its authored chance.</param>
    /// <param name="policy">The table's pity policy.</param>
    /// <param name="attempts">How many attempts have failed in a row.</param>
    /// <returns>The chance, from zero to one.</returns>
    /// <remarks>
    ///     Public because the editor's simulator draws this curve, and because a game showing a
    ///     player their pity progress must show the same number the realm rolls against.
    /// </remarks>
    public static float PityChance(float chance, PityPolicyDefinition policy, int attempts) {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.GuaranteedAt > 0 && attempts >= policy.GuaranteedAt) {
            return 1f;
        }

        var ramped = Math.Max(0, attempts - policy.AttemptsBefore) * policy.RampPerAttempt;

        return Math.Clamp(chance + ramped, 0f, 1f);
    }
}
