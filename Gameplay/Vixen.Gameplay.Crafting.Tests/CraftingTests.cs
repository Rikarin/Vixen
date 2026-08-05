// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Crafting.Tests;

/// <summary>A known bar, a taught sword, and two things found by experiment.</summary>
public static class Content {
    public const string Bar = "recipes/bar";
    public const string Sword = "recipes/sword";
    public const string Alloy = "recipes/alloy";
    public const string Elixir = "recipes/elixir";
    public const string Smithing = "Profession.Smithing";
    public const string Forge = "Interactable.Station.Forge";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Interactable.Station.Forge.Enchanted")
            .Add(
                Bar,
                new RecipeDefinition {
                    DisplayName = "Copper bar",
                    Profession = Smithing,
                    Station = Forge,
                    Inputs = [new() { Item = "items/ore", Count = 2 }],
                    Outputs = [new() { Item = "items/bar" }],
                    SkillRequired = 0,
                    SkillCap = 100,
                    SkillGain = 4
                }
            )
            .Add(
                Sword,
                new RecipeDefinition {
                    DisplayName = "Copper sword",
                    Profession = Smithing,
                    Station = Forge,
                    Source = RecipeSource.Taught,
                    Inputs = [new() { Item = "items/bar", Count = 3 }, new() { Item = "items/leather" }],
                    Outputs = [new() { Item = "items/sword" }],
                    SkillRequired = 25,
                    SkillCap = 75,
                    SkillGain = 2,
                    QualityChance = 0.25f
                }
            )
            .Add(
                Alloy,
                new RecipeDefinition {
                    DisplayName = "Bronze alloy",
                    Profession = Smithing,
                    Source = RecipeSource.Discovered,
                    Inputs = [new() { Item = "items/ore" }, new() { Item = "items/tin" }],
                    Outputs = [new() { Item = "items/bronze" }]
                }
            )
            .Add(
                Elixir,
                new RecipeDefinition {
                    DisplayName = "Green elixir",
                    Source = RecipeSource.Discovered,
                    Inputs = [new() { Item = "items/herb", Count = 3 }],
                    Outputs = [new() { Item = "items/elixir" }]
                }
            )
            .Build();
}

/// <summary>Somebody with a skill number and a station tag.</summary>
sealed class Smith(int skill) : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        if (subject == AttributeId.From(Content.Smithing)) {
            value = skill;

            return true;
        }

        value = 0f;

        return false;
    }
}

public class CraftingTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly CraftingLibrary library;

    public CraftingTests() => library = CraftingLibrary.Compile(catalog);

    Recipe Recipe(string address) => library.Find(DefId.From(address))!;

    GameplayTagSet Station(string name) {
        var tags = new GameplayTagSet();

        tags.Add(catalog.Tags.Resolve(name));

        return tags;
    }

    static Dictionary<uint, int> Holding(params (string Item, int Count)[] items) =>
        items.ToDictionary(entry => DefId.From(entry.Item).Value, entry => entry.Count);

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void ARecipeEverybodyKnowsNeedsNoTeaching() {
        var crafter = new Crafter(library, new Smith(0));

        Assert.True(crafter.Knows(Recipe(Content.Bar)));
        Assert.False(crafter.Knows(Recipe(Content.Sword)));
        Assert.Equal(0, crafter.Learned);
    }

    [Fact]
    public void ATaughtRecipeHasToBeTaught() {
        var crafter = new Crafter(library, new Smith(50));
        var holdings = Holding(("items/bar", 3), ("items/leather", 1));

        Assert.Equal(
            CraftingRefusal.NotLearned,
            crafter.CanCraft(Recipe(Content.Sword), Station(Content.Forge), holdings)
        );

        Assert.True(crafter.Learn(Recipe(Content.Sword)));
        Assert.False(crafter.Learn(Recipe(Content.Sword)));
        Assert.Equal(
            CraftingRefusal.None,
            crafter.CanCraft(Recipe(Content.Sword), Station(Content.Forge), holdings)
        );
    }

    [Fact]
    public void AStationIsATagQuerySoASubtypeSatisfiesIt() {
        // ⚠ A forge and an enchanted forge both satisfy one recipe, because the station is a prefix.
        var crafter = new Crafter(library, new Smith(0));
        var holdings = Holding(("items/ore", 2));

        Assert.Equal(CraftingRefusal.None, crafter.CanCraft(Recipe(Content.Bar), Station(Content.Forge), holdings));
        Assert.Equal(
            CraftingRefusal.None,
            crafter.CanCraft(Recipe(Content.Bar), Station("Interactable.Station.Forge.Enchanted"), holdings)
        );
        Assert.Equal(CraftingRefusal.WrongStation, crafter.CanCraft(Recipe(Content.Bar), null, holdings));
    }

    [Fact]
    public void ARecipeWithNoStationIsMadeAnywhere() {
        var crafter = new Crafter(library, new Smith(0));

        crafter.Learn(Recipe(Content.Elixir));

        Assert.Equal(
            CraftingRefusal.None,
            crafter.CanCraft(Recipe(Content.Elixir), null, Holding(("items/herb", 3)))
        );
    }

    [Fact]
    public void MissingIngredientsAreRefused() {
        var crafter = new Crafter(library, new Smith(0));

        Assert.Equal(
            CraftingRefusal.Missing,
            crafter.CanCraft(Recipe(Content.Bar), Station(Content.Forge), Holding(("items/ore", 1)))
        );
    }

    [Fact]
    public void TooLittleSkillIsRefused() {
        var crafter = new Crafter(library, new Smith(10));

        crafter.Learn(Recipe(Content.Sword));

        Assert.Equal(
            CraftingRefusal.Requirements,
            crafter.CanCraft(Recipe(Content.Sword), Station(Content.Forge), Holding(("items/bar", 3), ("items/leather", 1)))
        );
    }

    [Fact]
    public void CraftingReportsWhatToMoveAndMovesNothing() {
        var crafter = new Crafter(library, new Smith(0));
        var holdings = Holding(("items/ore", 2));

        Assert.Equal(
            CraftingRefusal.None,
            crafter.Craft(Recipe(Content.Bar), Station(Content.Forge), holdings, 1ul, out var result)
        );

        Assert.Equal(DefId.From("items/ore"), result.Consumed[0].Item);
        Assert.Equal(2, result.Consumed[0].Count);
        Assert.Equal(DefId.From("items/bar"), result.Produced[0].Item);

        // The holdings this was handed are untouched: what moves is the caller's containers' job.
        Assert.Equal(2, holdings[DefId.From("items/ore").Value]);
    }

    [Fact]
    public void TheQualityRollIsReproducibleFromTheAttempt() {
        // ⚠ "The log says it came out ordinary" has to be answerable — the loot library's property,
        // one layer over.
        var crafter = new Crafter(library, new Smith(50));

        crafter.Learn(Recipe(Content.Sword));

        var holdings = Holding(("items/bar", 3), ("items/leather", 1));
        var qualities = new List<bool>();

        for (var attempt = 0ul; attempt < 200ul; attempt++) {
            crafter.Craft(Recipe(Content.Sword), Station(Content.Forge), holdings, attempt, out var first);
            crafter.Craft(Recipe(Content.Sword), Station(Content.Forge), holdings, attempt, out var again);

            Assert.Equal(first.Quality, again.Quality);
            qualities.Add(first.Quality);
        }

        // A quarter chance over two hundred tries, so both outcomes happen and neither dominates.
        Assert.InRange(qualities.Count(quality => quality), 30, 70);
    }

    [Fact]
    public void SkillGainFallsAwayAcrossTheBandRatherThanOffACliff() {
        // ⚠ A cliff makes the last point before it the only thing worth making.
        var bar = Recipe(Content.Bar);

        Assert.Equal(4, bar.GainAt(0));
        Assert.Equal(3, bar.GainAt(25));
        Assert.Equal(2, bar.GainAt(50));
        Assert.Equal(1, bar.GainAt(75));
        Assert.Equal(0, bar.GainAt(100));
        Assert.Equal(0, bar.GainAt(500));
    }

    [Fact]
    public void DiscoveryIsAnExactMatchAndNotASuperset() {
        // ⚠ Matching a subset would mean throwing everything in the pot discovers every recipe at
        // once, which is a button rather than experimentation.
        var crafter = new Crafter(library, new Smith(0));

        Assert.False(crafter.TryDiscover([new(DefId.From("items/ore"), "items/ore", 1)], out _));
        Assert.False(
            crafter.TryDiscover(
                [
                    new(DefId.From("items/ore"), "items/ore", 1),
                    new(DefId.From("items/tin"), "items/tin", 1),
                    new(DefId.From("items/herb"), "items/herb", 3)
                ],
                out _
            )
        );

        Assert.True(
            crafter.TryDiscover(
                [new(DefId.From("items/tin"), "items/tin", 1), new(DefId.From("items/ore"), "items/ore", 1)],
                out var found
            )
        );

        Assert.Equal(Content.Alloy, found!.Definition.Address);
    }

    [Fact]
    public void TheOrderIngredientsWentInDoesNotMatter() {
        var forwards = library.Discover(
            [new(DefId.From("items/ore"), "items/ore", 1), new(DefId.From("items/tin"), "items/tin", 1)]
        );

        var backwards = library.Discover(
            [new(DefId.From("items/tin"), "items/tin", 1), new(DefId.From("items/ore"), "items/ore", 1)]
        );

        Assert.NotNull(forwards);
        Assert.Same(forwards, backwards);
    }

    [Fact]
    public void TheCountsHaveToMatchToo() {
        Assert.NotNull(library.Discover([new(DefId.From("items/herb"), "items/herb", 3)]));
        Assert.Null(library.Discover([new(DefId.From("items/herb"), "items/herb", 2)]));
    }

    [Fact]
    public void DiscoveringSomethingTeachesItOnce() {
        var crafter = new Crafter(library, new Smith(0));
        var learned = new List<Recipe>();

        crafter.Discovered += recipe => learned.Add(recipe);

        var ingredients = new[] { new RecipeItem(DefId.From("items/herb"), "items/herb", 3) };

        Assert.True(crafter.TryDiscover(ingredients, out _));
        Assert.False(crafter.TryDiscover(ingredients, out var again));
        Assert.NotNull(again);
        Assert.Single(learned);
    }

    [Fact]
    public void ARecipeThatTakesOrMakesNothingIsAProblem() {
        var problems = CraftingLibrary.Compile(
                new DefinitionCatalogBuilder().Add("recipes/air", new RecipeDefinition()).Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("out of air", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("only destroys", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoDiscoverableRecipesFromOneSetOfIngredientsIsAProblem() {
        var problems = CraftingLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "recipes/a",
                        new RecipeDefinition {
                            Source = RecipeSource.Discovered,
                            Inputs = [new() { Item = "items/ore" }],
                            Outputs = [new() { Item = "items/a" }]
                        }
                    )
                    .Add(
                        "recipes/b",
                        new RecipeDefinition {
                            Source = RecipeSource.Discovered,
                            Inputs = [new() { Item = "items/ore" }],
                            Outputs = [new() { Item = "items/b" }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("only one of them", StringComparison.Ordinal));
    }

    [Fact]
    public void ARecipeThatNeverTeachesAnythingIsAProblem() {
        var problems = CraftingLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "recipes/odd",
                        new RecipeDefinition {
                            SkillRequired = 100,
                            SkillCap = 50,
                            Inputs = [new() { Item = "items/ore" }],
                            Outputs = [new() { Item = "items/bar" }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("never teaches", StringComparison.Ordinal));
    }
}
