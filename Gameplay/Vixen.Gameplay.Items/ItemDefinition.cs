// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Items;

/// <summary>When an item stops being tradeable.</summary>
/// <remarks>
///     <b>A closed list, unlike rarity and slot, because these four are the whole of the mechanism.</b>
///     Binding exists to take an item out of the economy at a defined moment, and there are only four
///     moments an item can reach: never, arriving, being worn, being used. A game that wants a fifth
///     wants a rule about one of these rather than a new one.
/// </remarks>
public enum ItemBinding : byte {
    /// <summary>Tradeable for ever.</summary>
    None = 0,

    /// <summary>Bound as soon as it is picked up.</summary>
    OnPickup = 1,

    /// <summary>Tradeable until somebody equips it.</summary>
    OnEquip = 2,

    /// <summary>Tradeable until somebody uses it.</summary>
    OnUse = 3,

    /// <summary>Already bound. What an instance carries once the moment has passed.</summary>
    Bound = 4
}

/// <summary>One stat an item or an affix grants, as a definition holds it.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///     <see cref="ModifierDefinition" />.
/// </remarks>
[DataContract("ItemStat")]
public sealed class ItemStatDefinition {
    /// <summary>Which stat — <c>Power</c>.</summary>
    public string Attribute { get; set; } = string.Empty;

    /// <summary>Which bucket, and therefore which stage of the evaluation.</summary>
    public ModifierOp Op { get; set; }

    /// <summary>The value, or the low end of the roll when <see cref="Maximum" /> is above it.</summary>
    public float Value { get; set; }

    /// <summary>The high end of the roll. Below or equal to <see cref="Value" /> means no roll.</summary>
    /// <remarks>
    ///     A range on the stat rather than on the affix, because a single affix routinely rolls two
    ///     stats with different spreads — <em>of the Bear</em> giving a wide health range and a narrow
    ///     armour one — and one roll per affix is what makes that authorable.
    /// </remarks>
    public float Maximum { get; set; }
}

/// <summary>A rarity tier: what it is called, how it sorts, and how many affixes it buys.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A definition rather than an enum, which is a deviation from doc 28's sketch and the
///         reason is sorting.</b> The sketch authors <c>rarity: Legendary</c>, and the obvious
///         readings are a closed C# enum — which fixes every game to one ladder — or a
///         <see cref="GameplayTag" />, which is open but orders <em>alphabetically</em>, so a bag
///         sorted by rarity would put Common above Legendary for ever. A rarity needs a number, and a
///         number a designer sets is a definition.
///     </para>
///     <para>
///         It still carries a tag, because <em>every legendary drops a token</em> is a tag query like
///         every other rule.
///     </para>
/// </remarks>
[DataContract("ItemRarityDefinition")]
public sealed record ItemRarityDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Where it sits on the ladder. Higher is rarer; the numbers are a game's own.</summary>
    public int Order { get; set; }

    /// <summary>How many affixes an item of this rarity rolls.</summary>
    public int Affixes { get; set; }

    /// <summary>What an item of this rarity is, for a rule to match — <c>Item.Rarity.Legendary</c>.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }
    }
}

/// <summary>One rollable modifier an item can carry.</summary>
[DataContract("AffixDefinition")]
public sealed record AffixDefinition : Definition {
    /// <summary>What it is called in the UI — <c>of Power</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How likely it is against the other affixes in its pool.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>The lowest item level it appears on.</summary>
    public int MinimumItemLevel { get; set; }

    /// <summary>The highest item level it appears on. Zero is no ceiling.</summary>
    public int MaximumItemLevel { get; set; }

    /// <summary>What it does, with its ranges.</summary>
    public List<ItemStatDefinition> Stats { get; set; } = [];

    /// <summary>What this affix is — <c>Affix.Suffix.OfPower</c>.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Tag prefixes the item must match for this to be rollable on it.</summary>
    /// <remarks>
    ///     <c>Item.Weapon</c> on a weapon-only affix. Empty means any item in the pool, which is what
    ///     most affixes are — the pool is already the coarse filter and this is the fine one.
    /// </remarks>
    public List<string> RequiredItemTags { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in Tags) {
            tags.Add(tag);
        }

        foreach (var tag in RequiredItemTags) {
            tags.Add(tag);
        }
    }
}

/// <summary>A set of affixes an item can roll from.</summary>
/// <remarks>
///     A pool rather than a list on the item, because the same forty weapon affixes appear on three
///     hundred weapons and authoring them per item is three hundred places to make the same balance
///     change.
/// </remarks>
[DataContract("AffixPoolDefinition")]
public sealed record AffixPoolDefinition : Definition {
    /// <summary>The addresses of the affixes in it.</summary>
    public List<string> Affixes { get; set; } = [];
}

/// <summary>An item, as a designer wrote it.</summary>
/// <remarks>
///     <para>
///         Doc 28 § Items' own example, near enough verbatim: a display name, a rarity, a slot, an
///         item level, tags, stats, effects, an icon and a prefab. What it does <em>not</em> carry is
///         anything per-copy — that is <see cref="ItemInstance" />, and the split is the whole point.
///     </para>
///     <para>
///         ⚠ <b><see cref="Slot" /> is a tag and <see cref="Rarity" /> is an address</b>, and the two
///         being different shapes is deliberate. A slot is asked about hierarchically — <em>any
///         one-handed weapon</em> — and never sorted; a rarity is sorted and never asked about
///         hierarchically. Each is the shape its questions want.
///     </para>
/// </remarks>
[DataContract("ItemDefinition")]
public sealed record ItemDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The address of its <see cref="ItemRarityDefinition" />.</summary>
    public string Rarity { get; set; } = string.Empty;

    /// <summary>Which equipment slot it goes in — <c>Item.Slot.MainHand</c>. Empty is not equippable.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>How good it is, which is what affix ranges scale against.</summary>
    public int ItemLevel { get; set; }

    /// <summary>How many fit in one container slot. One is unstackable.</summary>
    public int MaximumStack { get; set; } = 1;

    /// <summary>How much wear it takes before it stops working. Zero is indestructible.</summary>
    public int MaximumDurability { get; set; }

    /// <summary>How many sockets it has.</summary>
    public int Sockets { get; set; }

    /// <summary>When it stops being tradeable.</summary>
    public ItemBinding Binding { get; set; }

    /// <summary>What it is — <c>Item.Weapon.Sword</c>, <c>Item.Source.Raid</c>.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>What it grants when equipped, before affixes.</summary>
    public List<ItemStatDefinition> Stats { get; set; } = [];

    /// <summary>The addresses of the affix pools it rolls from.</summary>
    public List<string> AffixPools { get; set; } = [];

    /// <summary>The address of its icon.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>The address of the prefab it spawns as in the world.</summary>
    public string Prefab { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in Tags) {
            tags.Add(tag);
        }

        if (Slot.Length > 0) {
            tags.Add(Slot);
        }
    }
}
