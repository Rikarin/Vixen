// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Items;

/// <summary>What an item does to the wearer's stats: the block computed on equip.</summary>
/// <remarks>
///     <para>
///         Doc 28 § Items: "the stat block is computed on equip". Not stored on the instance, not
///         replicated, not saved — derived from the definition and the seed at the one moment
///         somebody needs it, which is the whole reason an instance is sixteen bytes.
///     </para>
///     <para>
///         <b>What comes out is <see cref="Modifier" />s</b>, so an equipped item is exactly as much a
///         modifier source as a buff is. The kernel already knows how to remove those exactly, which
///         is what makes unequipping a sword arithmetic rather than a subtraction.
///     </para>
/// </remarks>
public static class ItemStats {
    /// <summary>Computes what one instance grants.</summary>
    /// <param name="library">Where the templates come from.</param>
    /// <param name="instance">The copy being equipped.</param>
    /// <param name="source">What the modifiers are removable by — usually the slot it went into.</param>
    /// <param name="into">Where to put them.</param>
    /// <returns>How many were produced.</returns>
    public static int Compute(
        ItemLibrary library,
        in ItemInstance instance,
        ModifierSource source,
        ICollection<Modifier> into
    ) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(into);

        var item = instance.IsSome ? library.Find(instance.Definition) : null;

        if (item is null) {
            return 0;
        }

        var produced = 0;

        foreach (ref readonly var stat in item.Stats) {
            into.Add(stat with { Source = source });
            produced++;
        }

        Span<RolledAffix> rolled = stackalloc RolledAffix[ItemAffixes.Maximum];

        foreach (ref readonly var affix in rolled[..ItemAffixes.Roll(item, instance.Seed, rolled)]) {
            if (library.FindAffix(affix.Affix) is not { } template) {
                continue;
            }

            foreach (ref readonly var stat in template.Stats) {
                into.Add(new(stat.Attribute, stat.Op, stat.At(affix.Roll), source));
                produced++;
            }
        }

        return produced;
    }

    /// <summary>
    ///     What a broken item grants, which is nothing — the one rule durability has that is not the
    ///     game's.
    /// </summary>
    /// <param name="library">Where the templates come from.</param>
    /// <param name="instance">The copy.</param>
    /// <returns>Whether its stats should count at all.</returns>
    /// <remarks>
    ///     ⚠ <b>Zero durability is broken; zero <em>maximum</em> durability is indestructible.</b> The
    ///     two are one field apart and reading them the wrong way round makes every stack of ore in
    ///     the game broken. What breaking <em>costs</em> — nothing, half, everything — is a game's
    ///     rule; that a broken item grants nothing is the shipped default, and a game overrides it by
    ///     not calling this.
    /// </remarks>
    public static bool IsFunctional(ItemLibrary library, in ItemInstance instance) {
        ArgumentNullException.ThrowIfNull(library);

        var item = instance.IsSome ? library.Find(instance.Definition) : null;

        return item is not null && (item.MaximumDurability == 0 || instance.Durability > 0);
    }
}
