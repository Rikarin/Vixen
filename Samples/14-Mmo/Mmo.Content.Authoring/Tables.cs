// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

/// <summary>The vocabulary the whole world is spelled out of.</summary>
/// <remarks>
///     <para>
///         <b>Everything below is a table because everything below is a designer's spreadsheet.</b>
///         A real MMO's content is authored in tooling over tables like these and exported; this
///         generator is that pipeline with the tooling left out, and the tables are the part worth
///         reading.
///     </para>
///     <para>
///         ⚠ <b>Original names throughout.</b> The sample is WoW-<em>shaped</em> — six zones, three
///         classes, tiers of gear, a reputation grind — and none of it is WoW's.
///     </para>
/// </remarks>
static class Tables {
    /// <summary>The zones, low to high, and the level band each covers.</summary>
    public static readonly Zone[] Zones = [
        new("greenmarch", "Greenmarch", 1, 8, "The starter valley: hedgerows, boars, and a forge."),
        new("thornwood", "Thornwood", 8, 14, "Old woodland gone wrong, and the road to the barrows."),
        new("ashfen", "Ashfen", 12, 17, "A drowned peat moor that still smoulders underneath."),
        new("kettlerock", "Kettlerock", 15, 20, "Scree, goats, and a mining company that stopped writing."),
        new("saltmere", "Saltmere", 17, 22, "A shallow inland sea and the things that came up it."),
        new("hollowmoor", "Hollowmoor", 20, 25, "Barrow country. Nothing here was buried shallow.")
    ];

    /// <summary>The three classes, and what each of them spends.</summary>
    public static readonly Class[] Classes = [
        new("vanguard", "Vanguard", "Rage", "Physical", "Plate", "Strength"),
        new("emberwright", "Emberwright", "Mana", "Fire", "Cloth", "Intellect"),
        new("marksman", "Marksman", "Focus", "Physical", "Leather", "Agility")
    ];

    /// <summary>Every slot a character can wear something in.</summary>
    public static readonly Slot[] Slots = [
        new("head", "Head", "Helm", 0.8f),
        new("shoulder", "Shoulder", "Pauldrons", 0.7f),
        new("chest", "Chest", "Hauberk", 1.0f),
        new("hands", "Hands", "Gauntlets", 0.6f),
        new("waist", "Waist", "Girdle", 0.6f),
        new("legs", "Legs", "Greaves", 0.9f),
        new("feet", "Feet", "Sabatons", 0.6f),
        new("back", "Back", "Cloak", 0.4f),
        new("finger", "Finger", "Band", 0.0f),
        new("trinket", "Trinket", "Charm", 0.0f)
    ];

    /// <summary>Armour classes, and which class wears which.</summary>
    public static readonly Armour[] Armours = [
        new("plate", "Plate", 1.6f, "vanguard"),
        new("leather", "Leather", 1.0f, "marksman"),
        new("cloth", "Cloth", 0.6f, "emberwright")
    ];

    /// <summary>Weapon kinds, and the class that wants one.</summary>
    public static readonly Weapon[] Weapons = [
        new("longsword", "Longsword", "MainHand", "vanguard", 2.4f, false),
        new("warhammer", "Warhammer", "MainHand", "vanguard", 3.1f, false),
        new("bulwark", "Bulwark", "OffHand", "vanguard", 0.0f, false),
        new("emberstaff", "Emberstaff", "MainHand", "emberwright", 2.8f, false),
        new("focus-orb", "Focus Orb", "OffHand", "emberwright", 0.0f, false),
        new("longrifle", "Longrifle", "MainHand", "marksman", 2.6f, true),
        new("carbine", "Carbine", "MainHand", "marksman", 1.9f, true),
        new("hunting-bow", "Hunting Bow", "MainHand", "marksman", 2.2f, false)
    ];

    /// <summary>The quality ladder. Five rungs, and the top one only drops in the Hollowmoor.</summary>
    public static readonly Rarity[] Rarities = [
        new("worn", "Worn", 0, 0, 0.6f),
        new("common", "Common", 1, 0, 1.0f),
        new("fine", "Fine", 2, 1, 1.35f),
        new("rare", "Rare", 3, 2, 1.8f),
        new("storied", "Storied", 4, 3, 2.5f)
    ];

    /// <summary>Creature families, the tag they grant, and where they live.</summary>
    public static readonly Family[] Families = [
        new("boar", "Boar", "Creature.Beast.Boar", "Faction.Wild", "greenmarch", "Physical"),
        new("wolf", "Wolf", "Creature.Beast.Wolf", "Faction.Wild", "greenmarch", "Physical"),
        new("bandit", "Bandit", "Creature.Humanoid.Bandit", "Faction.Bandits", "greenmarch", "Physical"),
        new("thornkin", "Thornkin", "Creature.Plant.Thornkin", "Faction.Thornwood", "thornwood", "Nature"),
        new("shade", "Shade", "Creature.Undead.Shade", "Faction.Barrow", "thornwood", "Shadow"),
        new("bogling", "Bogling", "Creature.Elemental.Bog", "Faction.Ashfen", "ashfen", "Nature"),
        new("emberling", "Emberling", "Creature.Elemental.Fire", "Faction.Ashfen", "ashfen", "Fire"),
        new("goat", "Crag Goat", "Creature.Beast.Goat", "Faction.Wild", "kettlerock", "Physical"),
        new("delver", "Delver", "Creature.Humanoid.Delver", "Faction.Company", "kettlerock", "Physical"),
        new("saltborn", "Saltborn", "Creature.Elemental.Water", "Faction.Saltmere", "saltmere", "Frost"),
        new("gullhag", "Gullhag", "Creature.Humanoid.Hag", "Faction.Saltmere", "saltmere", "Shadow"),
        new("barrow-knight", "Barrow Knight", "Creature.Undead.Knight", "Faction.Barrow", "hollowmoor", "Shadow"),
        new("grave-moth", "Grave Moth", "Creature.Beast.Moth", "Faction.Barrow", "hollowmoor", "Shadow"),
        new("cairn-wight", "Cairn Wight", "Creature.Undead.Wight", "Faction.Barrow", "hollowmoor", "Shadow")
    ];

    /// <summary>The professions, and whether they gather or make.</summary>
    public static readonly Profession[] Professions = [
        new("smithing", "Smithing", false, "Station.Forge"),
        new("leatherworking", "Leatherworking", false, "Station.Tannery"),
        new("weaving", "Weaving", false, "Station.Loom"),
        new("alchemy", "Alchemy", false, "Station.Alembic"),
        new("mining", "Mining", true, ""),
        new("herbalism", "Herbalism", true, "")
    ];

    /// <summary>The factions a player can grind, and the zone that cares.</summary>
    public static readonly Faction[] Factions = [
        new("marchwardens", "The Marchwardens", "greenmarch"),
        new("covenant", "The Thornwood Covenant", "thornwood"),
        new("fenwalkers", "The Fenwalkers", "ashfen"),
        new("kettle-company", "The Kettlerock Company", "kettlerock"),
        new("tidecallers", "The Tidecallers", "saltmere"),
        new("barrowwatch", "The Barrowwatch", "hollowmoor"),
        new("smiths-guild", "The Smiths' Guild", ""),
        new("wayfarers", "The Wayfarers", "")
    ];

    /// <summary>What a vendor sells, which is the whole of what makes it a different vendor.</summary>
    public static readonly VendorKind[] VendorKinds = [
        new("general", "General Goods", "consumable"),
        new("armourer", "Armourer", "armour"),
        new("weaponsmith", "Weaponsmith", "weapon"),
        new("reagents", "Reagent Trader", "reagent"),
        new("victualler", "Victualler", "food"),
        new("lapidary", "Lapidary", "gem"),
        new("quartermaster", "Quartermaster", "faction"),
        new("stablemaster", "Stablemaster", "mount")
    ];

    /// <summary>Gem colours, and the stat each one carries.</summary>
    public static readonly (string Id, string Name, string Attribute)[] Gems = [
        ("ember", "Ember", "Strength"),
        ("tide", "Tide", "Intellect"),
        ("thorn", "Thorn", "Agility"),
        ("barrow", "Barrow", "Stamina"),
        ("cairn", "Cairn", "Armour"),
        ("kettle", "Kettle", "CritChance")
    ];

    /// <summary>What an affix is called, and what it gives.</summary>
    public static readonly (string Id, string Name, string Attribute, float Value)[] Affixes = [
        ("of-the-bear", "of the Bear", "Stamina", 4f),
        ("of-the-boar", "of the Boar", "Strength", 4f),
        ("of-the-owl", "of the Owl", "Intellect", 4f),
        ("of-the-hawk", "of the Hawk", "Agility", 4f),
        ("of-the-wolf", "of the Wolf", "CritChance", 0.01f),
        ("of-the-tide", "of the Tide", "MaximumHealth", 20f),
        ("of-the-forge", "of the Forge", "Armour", 8f),
        ("of-the-ember", "of the Ember", "SpellPower", 5f),
        ("of-the-hunt", "of the Hunt", "AttackPower", 5f),
        ("of-warding", "of Warding", "FireTaken", -0.02f)
    ];

    /// <summary>A zone.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Low">The level it starts at.</param>
    /// <param name="High">The level it runs out at.</param>
    /// <param name="Blurb">One line, for the map.</param>
    public readonly record struct Zone(string Id, string Name, int Low, int High, string Blurb);

    /// <summary>A class.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Resource">What it spends.</param>
    /// <param name="School">What most of its damage is.</param>
    /// <param name="Armour">What it wears.</param>
    /// <param name="Attribute">What its damage scales off.</param>
    public readonly record struct Class(string Id, string Name, string Resource, string School, string Armour, string Attribute);

    /// <summary>An equipment slot.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Tag">The slot tag's last segment.</param>
    /// <param name="Noun">What a piece in it is called.</param>
    /// <param name="ArmourShare">How much of a set's armour it carries. Zero for jewellery.</param>
    public readonly record struct Slot(string Id, string Tag, string Noun, float ArmourShare);

    /// <summary>An armour class.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Scale">How much armour it carries against the others.</param>
    /// <param name="Class">Which class wears it.</param>
    public readonly record struct Armour(string Id, string Name, float Scale, string Class);

    /// <summary>A weapon kind.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Slot">Which hand.</param>
    /// <param name="Class">Who wants one.</param>
    /// <param name="Speed">Seconds a swing, or zero for something that does not swing.</param>
    /// <param name="Ranged">Whether it has ballistics, and therefore a <c>WeaponDefinition</c> too.</param>
    public readonly record struct Weapon(string Id, string Name, string Slot, string Class, float Speed, bool Ranged);

    /// <summary>A quality rung.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Order">Where it sits.</param>
    /// <param name="Affixes">How many affixes a piece of it rolls.</param>
    /// <param name="Scale">How much better its numbers are.</param>
    public readonly record struct Rarity(string Id, string Name, int Order, int Affixes, float Scale);

    /// <summary>A creature family.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What one is called.</param>
    /// <param name="Tag">What it grants, which is what a Kill objective counts.</param>
    /// <param name="Faction">Whose side it is on.</param>
    /// <param name="Zone">Where it lives.</param>
    /// <param name="School">What it hits with.</param>
    public readonly record struct Family(string Id, string Name, string Tag, string Faction, string Zone, string School);

    /// <summary>A profession.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Gathering">Whether it picks things up rather than making them.</param>
    /// <param name="Station">What it is made at, or empty.</param>
    public readonly record struct Profession(string Id, string Name, bool Gathering, string Station);

    /// <summary>A faction.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Zone">Which zone cares, or empty for one that spans them.</param>
    public readonly record struct Faction(string Id, string Name, string Zone);

    /// <summary>A kind of shop.</summary>
    /// <param name="Id">Its address segment.</param>
    /// <param name="Name">What the sign says.</param>
    /// <param name="Sells">Which of the item tables it stocks from.</param>
    public readonly record struct VendorKind(string Id, string Name, string Sells);
}
