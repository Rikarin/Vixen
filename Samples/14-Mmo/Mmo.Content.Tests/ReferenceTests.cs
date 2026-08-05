// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Ai;
using Vixen.Gameplay.Collections;
using Vixen.Gameplay.Crafting;
using Vixen.Gameplay.Economy;
using Vixen.Gameplay.Instances;
using Vixen.Gameplay.Interaction;
using Vixen.Gameplay.Items;
using Vixen.Gameplay.Loot;
using Vixen.Gameplay.Progression;
using Vixen.Gameplay.Quests;
using Vixen.Gameplay.Travel;
using Vixen.Samples.Mmo.Rules;
using Xunit;

namespace Vixen.Samples.Mmo.Content.Tests;

/// <summary>That an address one library writes down is an address another library has something at.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing in <c>Gameplay/</c> checks this, and structurally nothing can.</b> A loot
///         entry names an <em>item</em>, a vendor row names an item and a currency, a recipe names
///         items and a profession — and doc 28's spine allows only <c>Items</c> and <c>Combat</c> to
///         be depended on, so <c>Vixen.Gameplay.Loot</c> has no way to ask whether
///         <c>items/marchguard-plate</c> is anything. <c>LootLibrary</c> checks a nested <em>table</em>
///         because a table is its own; it cannot check an item.
///     </para>
///     <para>
///         ⚠ <b>And a <c>DefId</c> cannot report the difference.</b> It is a hash of an address, so
///         an id for nothing looks exactly like an id for something — a misspelt reference resolves
///         to a perfectly good number for a definition that does not exist. The failure is a lookup
///         that returns null in whatever code path first needs it, at whatever hour a player first
///         kills the thing that drops it.
///     </para>
///     <para>
///         <b>So this is the only place with every library at once, and therefore the only place the
///         check can live.</b> It belongs in the engine eventually — a <c>vixen content check</c>
///         that walks the whole catalog — and until then a game's own content test is where it goes,
///         which is worth copying out of this sample more than most of what is in it.
///     </para>
/// </remarks>
public sealed class ReferenceTests : IAsyncLifetime {
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
    public void EveryLootEntryDropsSomethingThatExists() {
        foreach (var table in LootLibrary.Compile(Catalog).All) {
            foreach (var entry in table.Entries) {
                if (entry.Item.IsSome) {
                    Resolves(entry.Definition.Item, $"{table.Definition.Address} drops");
                }
            }
        }
    }

    [Fact]
    public void EveryVendorSellsSomethingThatExistsForSomethingThatExists() {
        foreach (var vendor in EconomyLibrary.Compile(Catalog).Vendors) {
            foreach (var row in vendor.Stock) {
                Resolves(row.Definition.Item, $"{vendor.Definition.Address} sells");
                Resolves(row.Definition.Currency, $"{vendor.Definition.Address} charges");
            }
        }
    }

    [Fact]
    public void EveryRecipeTakesAndMakesSomethingThatExists() {
        foreach (var recipe in CraftingLibrary.Compile(Catalog).Recipes) {
            foreach (var input in recipe.Inputs) {
                Resolves(input.Address, $"{recipe.Definition.Address} takes");
            }

            foreach (var output in recipe.Outputs) {
                Resolves(output.Address, $"{recipe.Definition.Address} makes");
            }

            Resolves(recipe.Definition.Profession, $"{recipe.Definition.Address} is taught by");
        }
    }

    [Fact]
    public void EveryQuestPaysSomethingThatExists() {
        // Rewards are grants by address: items, currencies, reputations and the choice list. A quest
        // that pays a currency nobody minted hands out nothing and says nothing.
        foreach (var quest in QuestLibrary.Compile(Catalog).Quests) {
            var rewards = quest.Definition.Rewards;

            foreach (var grant in rewards.Items.Concat(rewards.Choices).Concat(rewards.Currencies).Concat(rewards.Reputation)) {
                Resolves(grant.Def, $"{quest.Definition.Address} pays");
            }
        }
    }

    [Fact]
    public void EveryAchievementUnlocksSomethingThatExists() {
        foreach (var achievement in CollectionLibrary.Compile(Catalog).Achievements) {
            foreach (var unlock in achievement.Definition.Unlocks) {
                Resolves(unlock, $"{achievement.Definition.Address} unlocks");
            }
        }
    }

    [Fact]
    public void EveryInteractableYieldsATableThatExists() {
        foreach (var interactable in InteractionLibrary.Compile(Catalog).Interactables) {
            Resolves(interactable.Definition.Yields, $"{interactable.Definition.Address} yields");
        }
    }

    [Fact]
    public void EverySpecialisationHasATreeThatExists() {
        foreach (var specialisation in ProgressionLibrary.Compile(Catalog).Specialisations) {
            Resolves(specialisation.Definition.TalentTree, $"{specialisation.Definition.Address} allocates");
        }
    }

    [Fact]
    public void EveryTravelPointGoesSomewhereAndChargesSomethingThatExists() {
        foreach (var point in TravelLibrary.Compile(Catalog).Points) {
            Resolves(point.Definition.From, $"{point.Definition.Address} leaves");
            Resolves(point.Definition.To, $"{point.Definition.Address} arrives at");
            Resolves(point.Definition.Currency, $"{point.Definition.Address} charges");
        }
    }

    [Fact]
    public void EverySpawnTableSpawnsSomethingThatExists() {
        // ⚠ The one that was missing, and four dangling creature addresses sat in the tree because
        // of it — in a sample whose README claimed every library was exercised. A reference check is
        // only as good as its enumeration of reference sites, which is the argument for #42's
        // CollectReferences: a hand-maintained list will always be missing the field somebody added.
        foreach (var table in SpawnLibrary.Compile(Catalog).Tables) {
            foreach (var entry in table.Entries) {
                Resolves(entry.Address, $"{table.Definition.Address} spawns");
            }
        }
    }

    [Fact]
    public void EveryEncounterScriptExists() {
        // The other half of the same hole: a dungeon pointing at a behaviour tree nobody wrote.
        foreach (var instance in InstanceLibrary.Compile(Catalog).Instances) {
            foreach (var encounter in instance.Definition.Encounters) {
                Resolves(encounter.Script, $"{instance.Definition.Address}'s {encounter.Id} runs");
            }
        }
    }

    [Fact]
    public void EveryCreatureCastsAndDropsSomethingThatExists() {
        // CreatureLibrary already checks this and reports it as a content problem — asserted here
        // too because the check belongs to the *sample's* own definition type, and a test that only
        // trusted the library would not notice the library being deleted.
        var libraries = MmoLibraries.Load(
            content.Definitions.Select(entry => (entry.Address, entry.Bytes))
        );

        Assert.Empty(libraries.Creatures.Problems);
        Assert.True(libraries.Creatures.Count > 0, "No creatures at all, which is where this started.");
    }

    [Fact]
    public void EveryItemsAffixPoolExists() {
        foreach (var item in ItemLibrary.Compile(Catalog).All) {
            foreach (var pool in item.Definition.AffixPools) {
                Resolves(pool, $"{item.Definition.Address} rolls from");
            }
        }
    }

    [Fact]
    public void EveryEventChainsIntoAnEventThatExists() {
        foreach (var dynamic in QuestLibrary.Compile(Catalog).Events) {
            foreach (var next in dynamic.Definition.OnSuccess.Concat(dynamic.Definition.OnFailure)) {
                Resolves(next, $"{dynamic.Definition.Address} chains into");
            }
        }
    }

    [Fact]
    public void EveryCollectObjectiveNamesSomethingThatExists() {
        foreach (var quest in QuestLibrary.Compile(Catalog).Quests) {
            foreach (var stage in quest.Definition.Stages) {
                foreach (var objective in stage.Objectives) {
                    Resolves(objective.Target, $"{quest.Definition.Address} asks for");
                }
            }
        }
    }

    /// <summary>Asserts an address is at something, and says which reference is wrong when it is not.</summary>
    /// <param name="address">The address, or empty for a reference nobody wrote.</param>
    /// <param name="what">How to describe the reference in the failure.</param>
    /// <remarks>
    ///     ⚠ An empty address passes: most of these fields are optional, and "nobody set it" is a
    ///     different fact from "somebody set it to nonsense". A scene address is skipped too — a
    ///     scene is an engine asset and is not in this catalog at all, which is what
    ///     <c>CoverageTests.EverySceneAnythingNamesIsOnDisk</c> covers instead.
    /// </remarks>
    void Resolves(string address, string what) {
        if (address.Length == 0 || address.StartsWith("maps/", StringComparison.Ordinal)) {
            return;
        }

        Assert.True(Catalog.Find(DefId.From(address)) is not null, $"{what} '{address}', which is at nothing.");
    }
}
