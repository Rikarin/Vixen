// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.Mmo.Authoring;

/// <summary>Writes <c>Samples/14-Mmo/Assets</c> from the tables.</summary>
/// <remarks>
///     <para>
///         <b>Run by hand; the output is committed.</b> <c>Tools/Vixen.UnicodeTableGen</c> is the
///         precedent: this repository already generates artefacts it checks in, because the thing
///         that reads them is a content build rather than a compiler, and CI has no generator to run.
///     </para>
///     <para>
///         ⚠ <b>The order is a dependency order and it is not incidental.</b> A loot table cannot
///         name an item before the item exists and a creature cannot name a loot table before the
///         table does. The content test would catch a mistake here anyway — that is what
///         <c>ReferenceTests</c> is for — but a generator that emitted a dangling address would be a
///         generator whose output nobody could trust to be self-consistent.
///     </para>
/// </remarks>
public static class Program {
    /// <summary>Writes it.</summary>
    /// <param name="args">The Assets directory, or nothing for the one beside this project.</param>
    /// <returns>Zero.</returns>
    public static int Main(string[] args) {
        var root = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets");

        root = Path.GetFullPath(root);

        var world = new World(root);

        var rarities = world.Rarities();
        var pools = world.Affixes();
        var items = world.Items(rarities, pools);
        var currencies = world.Currencies();
        var effects = world.Effects();
        var (abilities, creatureAbilities) = world.Abilities(effects);

        world.Ballistics(items.Ranged);

        var loot = world.Loot(items);
        var creatures = world.Creatures(loot, creatureAbilities);

        world.Spawns(creatures);

        var (professions, reputations) = world.Progression();

        world.Recipes(items, professions);
        world.Vendors(items, currencies);
        world.Maps();
        world.Scenes();
        world.Interactables(loot);
        world.Travel(currencies);
        world.Vehicles();
        world.Quests(items, currencies, reputations);
        world.Events(creatures, currencies);
        world.Talents(effects);
        world.Collections();
        world.Rest(creatures);

        Console.WriteLine($"wrote {world.Count:N0} definitions to {root}");

        foreach (var prefix in new[] {
            "items/", "abilities/", "creatures/", "quests/", "loot/", "vendors/", "effects/",
            "crafting/", "collectibles/", "achievements/", "spawns/", "world/", "travel/",
            "vehicles/", "maps/", "progression/", "chat/", "social/", "housing/", "pvp/",
            "instances/", "currencies/", "weapons/", "events/"
        }) {
            Console.WriteLine($"  {prefix,-16} {world.CountOf(prefix),5:N0}");
        }

        return 0;
    }
}
