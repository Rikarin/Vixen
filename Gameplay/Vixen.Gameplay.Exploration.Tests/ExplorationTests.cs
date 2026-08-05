// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Exploration.Tests;

/// <summary>One map with four points, one of which deliberately does not count.</summary>
public static class Content {
    public const string Queensdale = "maps/queensdale";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Level.Ten")
            .Add(
                Queensdale,
                new MapDefinition {
                    DisplayName = "Queensdale",
                    Columns = 16,
                    Rows = 8,
                    Tag = "Completion.Queensdale",
                    Points = [
                        new() { Id = "ascalon", Kind = PointKind.Landmark, Tag = "Discovered.Queensdale.Ascalon" },
                        new() { Id = "falls", Kind = PointKind.Vista, Tag = "Discovered.Queensdale.Falls" },
                        new() {
                            Id = "camp",
                            Kind = PointKind.Waypoint,
                            Tag = "Discovered.Queensdale.Camp",
                            Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Level.Ten" }]
                        },
                        new() { Id = "cache", Kind = PointKind.Cache, Tag = "Discovered.Queensdale.Cache", Counts = false }
                    ]
                }
            )
            .Build();
}

sealed class Levelled : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        value = 0f;

        return false;
    }
}

public class ExplorationTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly ExplorationLibrary library;
    readonly ExplorationRecord record;

    public ExplorationTests() {
        library = ExplorationLibrary.Compile(catalog);
        record = new(library);
    }

    MapChart Map => library.Find(DefId.From(Content.Queensdale))!;

    PointOfInterest Point(string id) => Map.Find(id)!;

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void OnlyThePointsMarkedAsCountingCountTowardsCompletion() {
        // ⚠ Opt-in, because a patch that adds a point to a finished map must not un-complete it.
        Assert.Equal(3, Map.Counting);
        Assert.Equal(4, Map.Points.Length);
    }

    [Fact]
    public void FindingSomethingGrantsItsTagAndMovesTheNumber() {
        Assert.Equal(0f, record.CompletionOf(Map));
        Assert.True(record.Discover(Map, Point("ascalon")));
        Assert.True(record.HasFound(Map, Point("ascalon")));
        Assert.True(record.Tags.Contains(catalog.Tags.Resolve("Discovered.Queensdale.Ascalon")));
        Assert.Equal(1f / 3f, record.CompletionOf(Map), 5);
    }

    [Fact]
    public void FindingSomethingTwiceIsNotFindingItTwice() {
        var found = 0;

        record.Found += (_, _) => found++;

        Assert.True(record.Discover(Map, Point("ascalon")));
        Assert.False(record.Discover(Map, Point("ascalon")));
        Assert.Equal(1, found);
    }

    [Fact]
    public void APointThatDoesNotCountStillGrantsItsTag() {
        Assert.True(record.Discover(Map, Point("cache")));
        Assert.True(record.Tags.Contains(catalog.Tags.Resolve("Discovered.Queensdale.Cache")));
        Assert.Equal(0f, record.CompletionOf(Map));
    }

    [Fact]
    public void ARequirementOnAPointIsChecked() {
        var player = new Levelled();

        Assert.False(record.Discover(Map, Point("camp"), player));

        player.Tags.Add(catalog.Tags.Resolve("Level.Ten"));

        Assert.True(record.Discover(Map, Point("camp"), player));
    }

    [Fact]
    public void CompletionIsAnnouncedOnceOnTheDiscoveryThatFinishesIt() {
        var completed = 0;

        record.Completed += _ => completed++;

        record.Discover(Map, Point("ascalon"));
        record.Discover(Map, Point("falls"));

        Assert.Equal(0, completed);

        record.Discover(Map, Point("camp"));

        Assert.Equal(1, completed);
        Assert.True(record.IsComplete(Map));
        Assert.True(record.Tags.Contains(catalog.Tags.Resolve("Completion.Queensdale")));

        // The one that does not count does not announce it again.
        record.Discover(Map, Point("cache"));

        Assert.Equal(1, completed);
    }

    [Fact]
    public void AMapWithNothingCountingIsAlreadyComplete() {
        var bare = ExplorationLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add("maps/bare", new MapDefinition { Points = [new() { Id = "a", Counts = false }] })
                .Build()
        );

        var chart = bare.Find(DefId.From("maps/bare"))!;

        Assert.Equal(1f, new ExplorationRecord(bare).CompletionOf(chart));
    }

    [Fact]
    public void FogStartsDownAndIsLiftedInASquare() {
        Assert.Equal(0f, record.RevealedOn(Map));
        Assert.False(record.IsRevealed(Map, 4, 4));

        Assert.Equal(9, record.Reveal(Map, 4, 4));
        Assert.True(record.IsRevealed(Map, 4, 4));
        Assert.True(record.IsRevealed(Map, 3, 3));
        Assert.True(record.IsRevealed(Map, 5, 5));
        Assert.False(record.IsRevealed(Map, 6, 4));
        Assert.Equal(9f / 128f, record.RevealedOn(Map), 5);
    }

    [Fact]
    public void RevealingTheSameGroundTwiceLiftsNothingNew() {
        Assert.Equal(9, record.Reveal(Map, 4, 4));
        Assert.Equal(0, record.Reveal(Map, 4, 4));
        Assert.Equal(3, record.Reveal(Map, 5, 4));
    }

    [Fact]
    public void RevealingAtTheEdgeIsClipped() {
        Assert.Equal(4, record.Reveal(Map, 0, 0));
        Assert.Equal(4, record.Reveal(Map, 15, 7));
        Assert.False(record.IsRevealed(Map, -1, 0));
        Assert.False(record.IsRevealed(Map, 16, 0));
    }

    [Fact]
    public void FogSurvivesASaveAndALoad() {
        record.Reveal(Map, 4, 4, radius: 2);

        var saved = record.FogOf(Map).ToArray();
        var restored = new ExplorationRecord(library);

        Assert.True(restored.RestoreFog(Map, saved));
        Assert.True(restored.IsRevealed(Map, 4, 4));
        Assert.Equal(record.RevealedOn(Map), restored.RevealedOn(Map));
        Assert.False(restored.RestoreFog(Map, new ulong[99]));
    }

    [Fact]
    public void AMapNobodyHasVisitedHasNoFogAtAll() {
        Assert.Empty(record.FogOf(Map).ToArray());
        Assert.Equal(0, record.Visited);

        record.Reveal(Map, 0, 0);

        Assert.Equal(1, record.Visited);
    }

    [Fact]
    public void AWaypointWithNoTagIsAProblem() {
        var problems = ExplorationLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("maps/odd", new MapDefinition { Points = [new() { Id = "wp", Kind = PointKind.Waypoint }] })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("nothing can be unlocked", StringComparison.Ordinal));
    }

    [Fact]
    public void AFogGridTooBigForASaveIsAProblem() {
        var problems = ExplorationLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("maps/huge", new MapDefinition { Columns = 4096, Rows = 4096 })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("will not fit in a save", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoPointsWithOneIdIsAProblem() {
        var problems = ExplorationLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("maps/odd", new MapDefinition { Points = [new() { Id = "a" }, new() { Id = "a" }] })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("two points called 'a'", StringComparison.Ordinal));
    }
}
