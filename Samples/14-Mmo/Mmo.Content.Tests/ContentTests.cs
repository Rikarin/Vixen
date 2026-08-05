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

/// <summary>
///     Every library the sample claims to exercise, compiled against the authored tree, with its
///     <c>Problems</c> read.
/// </summary>
/// <remarks>
///     <para>
///         <b>Each library reports content problems rather than throwing</b>, which is right for a
///         running game and useless unless somebody looks. This is the somebody. A misspelt tag, a
///         reference to an address that is not there, a talent tree whose prerequisite names a node
///         that was renamed — all of them compile to a library that works and quietly does less than
///         the designer meant.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is on an empty list, and that is the only useful shape.</b>
///         Asserting a count means the next real problem is absorbed by an off-by-one somebody
///         updates without reading.
///     </para>
/// </remarks>
public sealed class ContentTests : IAsyncLifetime {
    AuthoredContent content = null!;

    DefinitionCatalog Catalog => content.Catalog;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => content = await AuthoredContent.LoadAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void EveryFileImports() {
        Assert.True(content.Problems.Length == 0, string.Join("\n", content.Problems));
        Assert.True(content.Files > 0, "Found no content — the Assets glob or the copy is wrong.");
        Assert.Equal(content.Files, Catalog.Count);
    }

    [Fact]
    public void EveryDefinitionIsAtTheAddressItsPathSays() {
        // The whole cross-reference scheme rests on this: `items/rarity-fine` in a YAML file resolves
        // because Assets/Items/rarity-fine.vxdef is at that address and nowhere else.
        var rarity = Catalog.Find(DefId.From("items/rarity-fine"));

        Assert.NotNull(rarity);
        Assert.Equal("items/rarity-fine", rarity.Address);
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void ALibraryCompilesClean(string name, Func<DefinitionCatalog, IReadOnlyList<string>> compile) {
        var problems = compile(Catalog);

        Assert.True(problems.Count == 0, $"{name}:\n  " + string.Join("\n  ", problems));
    }

    public static TheoryData<string, Func<DefinitionCatalog, IReadOnlyList<string>>> Libraries => new() {
        { "Items", catalog => ItemLibrary.Compile(catalog).Problems },
        { "Loot", catalog => LootLibrary.Compile(catalog).Problems },
        { "Combat", catalog => AbilityLibrary.Compile(catalog).Problems },
        { "Shooting", catalog => WeaponLibrary.Compile(catalog).Problems },
        { "Progression", catalog => ProgressionLibrary.Compile(catalog).Problems },
        { "Quests", catalog => QuestLibrary.Compile(catalog).Problems },
        { "Social", catalog => SocialLibrary.Compile(catalog).Problems },
        { "Chat", catalog => ChatLibrary.Compile(catalog).Problems },
        { "Economy", catalog => EconomyLibrary.Compile(catalog).Problems },
        { "Instances", catalog => InstanceLibrary.Compile(catalog).Problems },
        { "Pvp", catalog => PvpLibrary.Compile(catalog).Problems },
        { "Interaction", catalog => InteractionLibrary.Compile(catalog).Problems },
        { "Crafting", catalog => CraftingLibrary.Compile(catalog).Problems },
        { "Exploration", catalog => ExplorationLibrary.Compile(catalog).Problems },
        { "Travel", catalog => TravelLibrary.Compile(catalog).Problems },
        { "Movement", catalog => MovementLibrary.Compile(catalog).Problems },
        { "Ai", catalog => SpawnLibrary.Compile(catalog).Problems },
        { "Housing", catalog => HousingLibrary.Compile(catalog).Problems },
        { "Collections", catalog => CollectionLibrary.Compile(catalog).Problems }
    };
}
