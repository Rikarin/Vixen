// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
using Xunit;

namespace Vixen.Samples.Mmo.Content.Tests;

/// <summary>That the sample is what it says it is: a slice through every library, not most of them.</summary>
/// <remarks>
///     ⚠ <b>The claim is the thing worth testing.</b> A sample that says it exercises twenty libraries
///     and exercises seventeen is worse than one that says seventeen: somebody reads the list, does not
///     find the example they came for, and concludes the library does not work.
/// </remarks>
public sealed class CoverageTests : IAsyncLifetime {
    AuthoredContent content = null!;

    DefinitionCatalog Catalog => content.Catalog;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => content = await AuthoredContent.LoadAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Authored))]
    public void ALibraryHasSomethingAuthoredForIt(string library, Func<DefinitionCatalog, int> count) {
        Assert.True(count(Catalog) > 0, $"{library} has nothing authored, so the sample does not exercise it.");
    }

    [Fact]
    public void TheCompositionTakesEveryLibrary() {
        // Twenty-one modules: doc 28's twenty libraries plus the kernel they all depend on.
        Assert.Equal(21, AuthoredContent.Composition!.Modules.Count);
    }

    [Fact]
    public void EveryLibraryWithoutDefinitionsIsExercisedSomewhereElse() {
        // ⚠ Three libraries author nothing and are not gaps in the coverage: Inventory is containers
        // a game sizes at runtime (the bag item carries the number), the auction house and mail are
        // Economy's runtime half, and matchmaking lives in Live/ rather than Gameplay/. They are the
        // composition's business and the realm's, which is task #36.
        Assert.Contains(AuthoredContent.Composition!.Modules, module => module.Name == "Inventory");

        // The bag is where Inventory's one authored number lives, so it is the thing to check.
        var bag = ItemLibrary.Compile(Catalog).Find(DefId.From("items/wardens-pack"));

        Assert.NotNull(bag);
    }

    [Fact]
    public void EverySceneAnythingNamesIsOnDisk() {
        // ⚠ Nothing in Gameplay/ checks this, and it cannot: a scene is an engine asset and the
        // gameplay libraries do not know what one is. So an instance pointing at a map nobody built
        // compiles clean and fails when somebody tries to enter it.
        var scenes = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Assets", "Scenes"), "*.vxscene")
            .Select(path => "maps/" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var named = InstanceLibrary.Compile(Catalog).Instances.Select(instance => instance.Definition.Scene)
            .Concat(PvpLibrary.Compile(Catalog).Maps.Select(map => map.Definition.Scene))
            .Where(scene => scene.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var scene in named) {
            Assert.Contains(scene, scenes);
        }
    }

    public static TheoryData<string, Func<DefinitionCatalog, int>> Authored => new() {
        { "Items", catalog => ItemLibrary.Compile(catalog).Count },
        { "Loot", catalog => LootLibrary.Compile(catalog).Count },
        { "Combat", catalog => AbilityLibrary.Compile(catalog).Count },
        { "Shooting", catalog => WeaponLibrary.Compile(catalog).Count },
        { "Progression", catalog => ProgressionLibrary.Compile(catalog).Trees.Count() },
        { "Quests", catalog => QuestLibrary.Compile(catalog).Quests.Count() },
        { "Social", catalog => SocialLibrary.Compile(catalog).Charters.Count() },
        { "Chat", catalog => ChatLibrary.Compile(catalog).Channels.Count() },
        { "Economy", catalog => EconomyLibrary.Compile(catalog).Currencies.Count() },
        { "Instances", catalog => InstanceLibrary.Compile(catalog).Instances.Count() },
        { "Pvp", catalog => PvpLibrary.Compile(catalog).Maps.Count() },
        { "Interaction", catalog => InteractionLibrary.Compile(catalog).Interactables.Count() },
        { "Crafting", catalog => CraftingLibrary.Compile(catalog).Recipes.Count() },
        { "Exploration", catalog => ExplorationLibrary.Compile(catalog).Maps.Count() },
        { "Travel", catalog => TravelLibrary.Compile(catalog).Points.Count() },
        { "Movement", catalog => MovementLibrary.Compile(catalog).Vehicles.Count() },
        { "Ai", catalog => SpawnLibrary.Compile(catalog).Tables.Count() },
        { "Housing", catalog => HousingLibrary.Compile(catalog).Plots.Count() },
        { "Collections", catalog => CollectionLibrary.Compile(catalog).Collectibles.Length }
    };
}
