// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Items;

/// <summary>One stat an affix rolls, resolved.</summary>
/// <param name="Attribute">Which stat.</param>
/// <param name="Op">Which modifier bucket.</param>
/// <param name="Minimum">What a roll of zero gives.</param>
/// <param name="Maximum">What a roll of one gives.</param>
public readonly record struct AffixStat(AttributeId Attribute, ModifierOp Op, float Minimum, float Maximum) {
    /// <summary>The value at a roll.</summary>
    /// <param name="roll">Where in the range, from zero to one.</param>
    /// <returns>The value.</returns>
    public float At(float roll) => Minimum + ((Maximum - Minimum) * Math.Clamp(roll, 0f, 1f));
}

/// <summary>A rarity tier with its names resolved.</summary>
public sealed class ItemRarityTemplate {
    internal ItemRarityTemplate(ItemRarityDefinition definition, GameplayTag tag) {
        Definition = definition;
        Tag = tag;
    }

    /// <summary>What it was compiled from.</summary>
    public ItemRarityDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>Where it sits on the ladder.</summary>
    public int Order => Definition.Order;

    /// <summary>How many affixes an item of this rarity rolls.</summary>
    public int Affixes => Math.Max(0, Definition.Affixes);

    /// <summary>What an item of this rarity is, for a rule to match.</summary>
    public GameplayTag Tag { get; }
}

/// <summary>An affix with its names resolved and its ranges compiled.</summary>
public sealed class AffixTemplate {
    readonly AffixStat[] stats;
    readonly GameplayTag[] tags;
    readonly GameplayTagRange[] requiredItemTags;

    internal AffixTemplate(
        AffixDefinition definition,
        AffixStat[] stats,
        GameplayTag[] tags,
        GameplayTagRange[] requiredItemTags
    ) {
        Definition = definition;
        this.stats = stats;
        this.tags = tags;
        this.requiredItemTags = requiredItemTags;
    }

    /// <summary>What it was compiled from.</summary>
    public AffixDefinition Definition { get; }

    /// <summary>Its id — what a <see cref="RolledAffix" /> names.</summary>
    public DefId Id => Definition.Id;

    /// <summary>How likely it is against the others in its pool. Never negative.</summary>
    public float Weight => MathF.Max(0f, Definition.Weight);

    /// <summary>What it does, with its ranges.</summary>
    public ReadOnlySpan<AffixStat> Stats => stats;

    /// <summary>What this affix is.</summary>
    public ReadOnlySpan<GameplayTag> Tags => tags;

    /// <summary>Whether it can roll on an item at all.</summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether the item's level and tags allow it.</returns>
    /// <remarks>
    ///     Evaluated at roll time rather than baked per item, because the pool is shared by three
    ///     hundred items and a per-item list would be three hundred copies of the same forty entries.
    /// </remarks>
    public bool AppliesTo(ItemTemplate item) {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ItemLevel < Definition.MinimumItemLevel) {
            return false;
        }

        if (Definition.MaximumItemLevel > 0 && item.ItemLevel > Definition.MaximumItemLevel) {
            return false;
        }

        foreach (var range in requiredItemTags) {
            if (!item.HasTagUnder(range)) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>An item with its names resolved: the form a roll, a tooltip and a stat block run on.</summary>
public sealed class ItemTemplate {
    readonly GameplayTag[] tags;
    readonly Modifier[] stats;
    readonly AffixTemplate[] affixes;

    internal ItemTemplate(
        ItemDefinition definition,
        GameplayTag[] tags,
        GameplayTag slot,
        ItemRarityTemplate? rarity,
        Modifier[] stats,
        AffixTemplate[] affixes
    ) {
        Definition = definition;
        this.tags = tags;
        Slot = slot;
        Rarity = rarity;
        this.stats = stats;
        this.affixes = affixes;
    }

    /// <summary>What it was compiled from.</summary>
    public ItemDefinition Definition { get; }

    /// <summary>Its id — what an <see cref="ItemInstance" /> names.</summary>
    public DefId Id => Definition.Id;

    /// <summary>Which equipment slot it goes in, or <see cref="GameplayTag.None" />.</summary>
    public GameplayTag Slot { get; }

    /// <summary>Its rarity, or null when the definition named none.</summary>
    public ItemRarityTemplate? Rarity { get; }

    /// <summary>How good it is.</summary>
    public int ItemLevel => Definition.ItemLevel;

    /// <summary>How many fit in one container slot. Never below one.</summary>
    public int MaximumStack => Math.Max(1, Definition.MaximumStack);

    /// <summary>Whether more than one fits in a slot.</summary>
    public bool IsStackable => MaximumStack > 1;

    /// <summary>How much wear it takes. Zero is indestructible.</summary>
    public int MaximumDurability => Math.Max(0, Definition.MaximumDurability);

    /// <summary>How many sockets it has.</summary>
    public int Sockets => Math.Max(0, Definition.Sockets);

    /// <summary>When it stops being tradeable.</summary>
    public ItemBinding Binding => Definition.Binding;

    /// <summary>What it is.</summary>
    public ReadOnlySpan<GameplayTag> Tags => tags;

    /// <summary>What it grants before affixes, with no source stamped on.</summary>
    public ReadOnlySpan<Modifier> Stats => stats;

    /// <summary>
    ///     Every affix it could roll, in a canonical order, before the per-item level and tag filter.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Sorted by address, not by the order the pools listed them.</b> A roll picks from this
    ///     span by weight, so its order is part of what the seed means — and an order that depended on
    ///     how a designer sorted a YAML list would make every existing item re-roll the day somebody
    ///     tidied it.
    /// </remarks>
    public ReadOnlySpan<AffixTemplate> AffixPool => affixes;

    /// <summary>How many affixes an instance of this rolls.</summary>
    public int AffixCount => Rarity?.Affixes ?? 0;

    /// <summary>Whether any of the item's own tags falls under a prefix.</summary>
    /// <param name="range">The prefix.</param>
    /// <returns>Whether it matches.</returns>
    public bool HasTagUnder(GameplayTagRange range) {
        if (Slot.IsSome && range.Contains(Slot)) {
            return true;
        }

        foreach (var tag in tags) {
            if (range.Contains(tag)) {
                return true;
            }
        }

        return Rarity is { Tag.IsSome: true } && range.Contains(Rarity.Tag);
    }

    /// <summary>A fresh instance of this item: full durability, unrolled unless it has affixes.</summary>
    /// <param name="stack">How many.</param>
    /// <param name="seed">What its affixes roll from. Zero rolls nothing.</param>
    /// <returns>The instance.</returns>
    public ItemInstance Create(int stack = 1, uint seed = 0) =>
        new() {
            Definition = Id,
            Seed = AffixCount > 0 ? seed : 0u,
            Stack = (ushort)Math.Clamp(stack, 0, Math.Min(MaximumStack, ushort.MaxValue)),
            Durability = (ushort)Math.Min(MaximumDurability, ushort.MaxValue),
            Binding = Binding
        };
}
