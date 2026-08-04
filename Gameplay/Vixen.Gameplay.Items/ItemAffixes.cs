// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Items;

/// <summary>Turns an instance's seed back into the affixes it rolled.</summary>
/// <remarks>
///     <para>
///         <b>Regenerated, never stored.</b> Doc 28 § Items: rolled affixes are
///         <c>(affixDefId, roll)</c> pairs regenerated from the seed. That is what keeps an instance
///         at sixteen bytes, and it is also what makes a client's tooltip and a realm's damage
///         calculation agree while the wire carries a definition id and a seed.
///     </para>
///     <para>
///         ⚠ <b>Therefore the roll is a pure function of (template, seed) and nothing else</b> — no
///         clock, no player, no ambient random. Everything it consumes is either the seed or content
///         both ends already agreed on, and <c>TheSameSeedRollsTheSameAffixes</c> is the test that
///         keeps it that way.
///     </para>
///     <para>
///         ⚠ <b>An item whose pool changed re-rolls, and there is no way around that.</b> The pool a
///         seed picks from is part of what the seed means, so adding an affix to a pool changes what
///         every existing instance of every item using it has rolled. That is a content decision with
///         a visible consequence — a player's sword changes — and the honest place to say so is here
///         and in <c>ContentDiff</c>, not in a migration that pretends otherwise.
///     </para>
/// </remarks>
public static class ItemAffixes {
    /// <summary>How many affixes one instance can carry. A bound, so a roll can use the stack.</summary>
    public const int Maximum = 16;

    /// <summary>Rolls an instance's affixes.</summary>
    /// <param name="item">What it is a copy of.</param>
    /// <param name="seed">Its roll seed. Zero rolls nothing.</param>
    /// <param name="into">Where to put them, at most <see cref="Maximum" />.</param>
    /// <returns>How many were rolled.</returns>
    public static int Roll(ItemTemplate item, uint seed, Span<RolledAffix> into) {
        ArgumentNullException.ThrowIfNull(item);

        var wanted = Math.Min(item.AffixCount, Math.Min(into.Length, Maximum));

        if (seed == 0 || wanted <= 0 || item.AffixPool.Length == 0) {
            return 0;
        }

        // The candidate list is the pool minus what this item's level and tags exclude. Built here
        // rather than cached per item because the pool is shared and the filter is a few comparisons.
        Span<int> candidates = stackalloc int[Math.Min(item.AffixPool.Length, 256)];
        Span<float> weights = stackalloc float[candidates.Length];
        var count = 0;

        for (var index = 0; index < item.AffixPool.Length && count < candidates.Length; index++) {
            var affix = item.AffixPool[index];

            if (affix.Weight <= 0f || !affix.AppliesTo(item)) {
                continue;
            }

            candidates[count] = index;
            weights[count] = affix.Weight;
            count++;
        }

        var random = GameplayRandom.For(seed);
        var rolled = 0;

        while (rolled < wanted && count > 0) {
            var picked = random.Pick(weights[..count]);

            if (picked < 0) {
                break;
            }

            into[rolled++] = new(item.AffixPool[candidates[picked]].Id, random.NextFloat());

            // Without replacement: an item with two of the same affix is a bug that reads as
            // generosity. Swapping the last entry down keeps the remaining order stable, which the
            // seed depends on.
            count--;
            candidates[picked] = candidates[count];
            weights[picked] = weights[count];
        }

        return rolled;
    }

    /// <summary>Rolls an instance's affixes into a new array. For an editor and a test, not a frame.</summary>
    /// <param name="item">What it is a copy of.</param>
    /// <param name="seed">Its roll seed.</param>
    /// <returns>Them.</returns>
    public static RolledAffix[] Roll(ItemTemplate item, uint seed) {
        Span<RolledAffix> buffer = stackalloc RolledAffix[Maximum];
        var count = Roll(item, seed, buffer);

        return count == 0 ? [] : buffer[..count].ToArray();
    }
}
