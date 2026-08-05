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
using Vixen.Samples.Mmo.Rules;
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
    public void ALibraryIsExercisedAtTheScaleTheSampleClaims(string library, Func<DefinitionCatalog, int> count, int floor) {
        // ⚠ A floor rather than `> 0`, and the difference is not pedantry. The `> 0` version passed
        // for months while the world had no creatures in it at all: one authored file per library is
        // "the mechanism compiles", and the README claims something much stronger than that.
        var measured = count(Catalog);

        Assert.True(measured >= floor, $"{library} has {measured} authored and the sample claims at least {floor}.");
    }

    [Fact]
    public void TheCompositionTakesEveryLibrary() {
        // Twenty-one modules: doc 28's twenty libraries plus the kernel they all depend on.
        Assert.Equal(22, AuthoredContent.Composition!.Modules.Count);
    }

    [Fact]
    public void EveryLibraryWithoutDefinitionsIsExercisedSomewhereElse() {
        // ⚠ Three libraries author nothing and are not gaps in the coverage: Inventory is containers
        // a game sizes at runtime (the bag item carries the number), the auction house and mail are
        // Economy's runtime half, and matchmaking lives in Live/ rather than Gameplay/. They are the
        // composition's business and the realm's, which is task #36.
        Assert.Contains(AuthoredContent.Composition!.Modules, module => module.Name == "Inventory");

        // The bag is where Inventory's one authored number lives, so it is the thing to check.
        var bag = ItemLibrary.Compile(Catalog).Find(DefId.From("items/bags/wardens-pack"));

        Assert.NotNull(bag);
    }

    [Fact]
    public void EverySceneAnythingNamesIsOnDisk() {
        // ⚠ Nothing in Gameplay/ checks this, and it cannot: a scene is an engine asset and the
        // gameplay libraries do not know what one is. So an instance pointing at a map nobody built
        // compiles clean and fails when somebody tries to enter it.
        // The address of a scene is its path, exactly as a definition's is — see AuthoredContent.
        var root = Path.Combine(AppContext.BaseDirectory, "Assets");
        var scenes = Directory.EnumerateFiles(root, "*.vxscene", SearchOption.AllDirectories)
            .Select(path => Path.ChangeExtension(Path.GetRelativePath(root, path), null).Replace('\\', '/').ToLowerInvariant())
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

    public static TheoryData<string, Func<DefinitionCatalog, int>, int> Authored => new() {
        { "Items", catalog => ItemLibrary.Compile(catalog).Count, 300 },
        { "Loot", catalog => LootLibrary.Compile(catalog).Count, 40 },
        { "Combat", catalog => AbilityLibrary.Compile(catalog).Count, 100 },
        { "Shooting", catalog => WeaponLibrary.Compile(catalog).Count, 6 },
        { "Progression: trees", catalog => ProgressionLibrary.Compile(catalog).Trees.Count(), 3 },
        { "Progression: professions", catalog => ProgressionLibrary.Compile(catalog).Professions.Count(), 6 },
        { "Progression: reputations", catalog => ProgressionLibrary.Compile(catalog).Reputations.Count(), 8 },
        { "Quests", catalog => QuestLibrary.Compile(catalog).Quests.Count(), 50 },
        { "Quests: events", catalog => QuestLibrary.Compile(catalog).Events.Count(), 6 },
        { "Social", catalog => SocialLibrary.Compile(catalog).Policies.Count(), 4 },
        { "Chat", catalog => ChatLibrary.Compile(catalog).Channels.Count(), 8 },
        { "Economy: currencies", catalog => EconomyLibrary.Compile(catalog).Currencies.Count(), 6 },
        { "Economy: vendors", catalog => EconomyLibrary.Compile(catalog).Vendors.Count(), 20 },
        { "Instances", catalog => InstanceLibrary.Compile(catalog).Instances.Count(), 4 },
        { "Pvp", catalog => PvpLibrary.Compile(catalog).Maps.Count(), 4 },
        { "Interaction", catalog => InteractionLibrary.Compile(catalog).Interactables.Count(), 30 },
        { "Crafting", catalog => CraftingLibrary.Compile(catalog).Recipes.Count(), 40 },
        { "Exploration", catalog => ExplorationLibrary.Compile(catalog).Maps.Count(), 6 },
        { "Travel", catalog => TravelLibrary.Compile(catalog).Points.Count(), 12 },
        { "Movement", catalog => MovementLibrary.Compile(catalog).Vehicles.Count(), 12 },
        { "Ai", catalog => SpawnLibrary.Compile(catalog).Tables.Count(), 25 },
        { "Housing: furniture", catalog => HousingLibrary.Compile(catalog).Furniture.Count(), 25 },
        { "Collections", catalog => CollectionLibrary.Compile(catalog).Collectibles.Length, 30 },
        { "Collections: achievements", catalog => CollectionLibrary.Compile(catalog).Achievements.Length, 18 }
    };
}

/// <summary>That every address <c>Mmo.Shared</c> spells out is an address something is at.</summary>
/// <remarks>
///     ⚠ <b>A misspelt address is not a compile error and never will be.</b> <c>DefId.From</c> hashes
///     whatever it is handed, so <c>"maps/thornwod"</c> is a perfectly good id for nothing at all —
///     and the failure is a lookup that returns null in a code path nobody exercised. This is the
///     only place that can catch it, which is why <c>MmoAddresses.All</c> exists at all.
/// </remarks>
public sealed class AddressTests : IAsyncLifetime {
    AuthoredContent content = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => content = await AuthoredContent.LoadAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void EveryAddressCodeNamesResolves() {
        foreach (var address in MmoAddresses.All) {
            Assert.True(content.Catalog.Find(DefId.From(address)) is not null, $"'{address}' is at nothing.");
        }
    }
}
