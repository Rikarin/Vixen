// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Gameplay;
using Vixen.Gameplay.Ai;
using Vixen.Gameplay.Chat;
using Vixen.Gameplay.Collections;
using Vixen.Gameplay.Combat;
using Vixen.Gameplay.Crafting;
using Vixen.Gameplay.Economy;
using Vixen.Gameplay.Exploration;
using Vixen.Gameplay.Housing;
using Vixen.Gameplay.Instances;
using Vixen.Gameplay.Interaction;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;
using Vixen.Gameplay.Movement;
using Vixen.Gameplay.Progression;
using Vixen.Gameplay.Pvp;
using Vixen.Gameplay.Quests;
using Vixen.Gameplay.Shooting;
using Vixen.Gameplay.Social;
using Vixen.Gameplay.Travel;

namespace Vixen.Samples.Mmo.Rules;

/// <summary>Every library compiled once, against one catalog, with every problem in one list.</summary>
/// <remarks>
///     <para>
///         <b>One object, because the alternative is nineteen fields on the realm and nineteen more
///         on the client.</b> Compiling is not cheap and it is not idempotent-free — a library holds
///         resolved tag ranges, and two copies compiled from two catalogs would answer differently.
///     </para>
///     <para>
///         ⚠ <b>The tags come first and the definitions second, and swapping them is a bug with a
///         very confusing shape.</b> Most tags reach the table because a definition mentions them; a
///         tag only <em>code</em> knows never does. <c>Event.Kill</c> is the one that bites —
///         <c>QuestModule</c> declares it, it is the verb a Kill objective counts, and no quest file
///         mentions it anywhere. Seed the catalog from <c>Composition.Tags</c> or every objective in
///         the game compiles into one nothing can ever advance.
///     </para>
///     <para>
///         ⚠ <b>Every library's <c>Problems</c> ends up in one list rather than nineteen.</b> A
///         library reports rather than throws, which is right for a running realm and useless unless
///         somebody looks — and nineteen places to look is nought places to look.
///     </para>
/// </remarks>
public sealed class MmoLibraries {
    MmoLibraries(GameplayComposition composition, DefinitionCatalog catalog) {
        Composition = composition;
        Catalog = catalog;

        Items = ItemLibrary.Compile(catalog);
        Loot = LootLibrary.Compile(catalog);
        Abilities = AbilityLibrary.Compile(catalog);
        Weapons = WeaponLibrary.Compile(catalog);
        Progression = ProgressionLibrary.Compile(catalog);
        Quests = QuestLibrary.Compile(catalog);
        Social = SocialLibrary.Compile(catalog);
        Chat = ChatLibrary.Compile(catalog);
        Economy = EconomyLibrary.Compile(catalog);
        Instances = InstanceLibrary.Compile(catalog);
        Pvp = PvpLibrary.Compile(catalog);
        Interaction = InteractionLibrary.Compile(catalog);
        Crafting = CraftingLibrary.Compile(catalog);
        Exploration = ExplorationLibrary.Compile(catalog);
        Travel = TravelLibrary.Compile(catalog);
        Movement = MovementLibrary.Compile(catalog);
        Spawns = SpawnLibrary.Compile(catalog);
        Housing = HousingLibrary.Compile(catalog);
        Collections = CollectionLibrary.Compile(catalog);

        // Last, because it reads every library above. Compiled exactly once: a library holds
        // resolved tag ranges, and two copies compiled from two catalogs answer differently.
        Problems = [.. Collect()];
    }

    /// <summary>What the game composed.</summary>
    public GameplayComposition Composition { get; }

    /// <summary>Every definition, at the address its path said.</summary>
    public DefinitionCatalog Catalog { get; }

    /// <summary>What the content build could not resolve, from every library, prefixed by which.</summary>
    public ImmutableArray<string> Problems { get; }

    /// <summary>Gear, reagents and the bag.</summary>
    public ItemLibrary Items { get; }

    /// <summary>Drop tables and their pity policies.</summary>
    public LootLibrary Loot { get; }

    /// <summary>What the three classes can do.</summary>
    public AbilityLibrary Abilities { get; }

    /// <summary>The rifle.</summary>
    public WeaponLibrary Weapons { get; }

    /// <summary>Curves, trees, professions, reputations.</summary>
    public ProgressionLibrary Progression { get; }

    /// <summary>The chain, the daily, and the two events.</summary>
    public QuestLibrary Quests { get; }

    /// <summary>Group policies and the guild charter.</summary>
    public SocialLibrary Social { get; }

    /// <summary>Five channels.</summary>
    public ChatLibrary Chat { get; }

    /// <summary>Three currencies and two vendors.</summary>
    public EconomyLibrary Economy { get; }

    /// <summary>The Barrowdeep.</summary>
    public InstanceLibrary Instances { get; }

    /// <summary>Ravensford and the arena.</summary>
    public PvpLibrary Pvp { get; }

    /// <summary>Ore, herbs and the forge.</summary>
    public InteractionLibrary Interaction { get; }

    /// <summary>Three recipes, one per source.</summary>
    public CraftingLibrary Crafting { get; }

    /// <summary>The two charts and their fog.</summary>
    public ExplorationLibrary Exploration { get; }

    /// <summary>Waypoints, the flight path and the hearthstone.</summary>
    public TravelLibrary Travel { get; }

    /// <summary>Two mounts and the payload waggon.</summary>
    public MovementLibrary Movement { get; }

    /// <summary>Two camps and their leashes.</summary>
    public SpawnLibrary Spawns { get; }

    /// <summary>The freehold and its furniture.</summary>
    public HousingLibrary Housing { get; }

    /// <summary>Mounts, pets, looks, titles and the achievements over them.</summary>
    public CollectionLibrary Collections { get; }

    /// <summary>Composes, seeds the tags, reads the definitions and compiles everything.</summary>
    /// <param name="definitions">The catalog's contents, as <c>(address, artefact bytes)</c>.</param>
    /// <returns>The libraries.</returns>
    public static MmoLibraries Load(IEnumerable<(string Address, ReadOnlyMemory<byte> Bytes)> definitions) {
        ArgumentNullException.ThrowIfNull(definitions);

        var composition = MmoModules.Compose();
        var builder = new DefinitionCatalogBuilder();

        // Tags first. See the remarks on the type — this is not an ordering preference.
        foreach (var tag in composition.Tags) {
            builder.AddTag(tag);
        }

        foreach (var (address, bytes) in definitions) {
            DefinitionSerialization.Add(builder, address, bytes.Span);
        }

        return new(composition, builder.Build());
    }

    IEnumerable<string> Collect() =>
        Prefix("Items", Items.Problems)
            .Concat(Prefix("Loot", Loot.Problems))
            .Concat(Prefix("Combat", Abilities.Problems))
            .Concat(Prefix("Shooting", Weapons.Problems))
            .Concat(Prefix("Progression", Progression.Problems))
            .Concat(Prefix("Quests", Quests.Problems))
            .Concat(Prefix("Social", Social.Problems))
            .Concat(Prefix("Chat", Chat.Problems))
            .Concat(Prefix("Economy", Economy.Problems))
            .Concat(Prefix("Instances", Instances.Problems))
            .Concat(Prefix("Pvp", Pvp.Problems))
            .Concat(Prefix("Interaction", Interaction.Problems))
            .Concat(Prefix("Crafting", Crafting.Problems))
            .Concat(Prefix("Exploration", Exploration.Problems))
            .Concat(Prefix("Travel", Travel.Problems))
            .Concat(Prefix("Movement", Movement.Problems))
            .Concat(Prefix("Ai", Spawns.Problems))
            .Concat(Prefix("Housing", Housing.Problems))
            .Concat(Prefix("Collections", Collections.Problems));

    static IEnumerable<string> Prefix(string library, IReadOnlyList<string> problems) =>
        problems.Select(problem => $"{library}: {problem}");
}
