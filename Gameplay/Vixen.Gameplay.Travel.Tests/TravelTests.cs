// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Travel.Tests;

/// <summary>A free portal, a paid waypoint, a taxi and an instance door.</summary>
public static class Content {
    public const string Portal = "travel/portal";
    public const string Waypoint = "travel/camp-waypoint";
    public const string Taxi = "travel/taxi";
    public const string Door = "travel/crypt-door";
    public const string Queensdale = "maps/queensdale";
    public const string Divinity = "maps/divinity";
    public const string Crypt = "maps/crypt";
    public const string Gold = "currency/gold";
    public const string Unlock = "Discovered.Queensdale.Camp";

    public static PlayerId Player(ulong who) => new(who);

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Level.Thirty")
            .Add(
                Portal,
                new TravelPointDefinition {
                    DisplayName = "Asura gate",
                    Kind = TravelKind.Portal,
                    From = Queensdale,
                    To = Divinity
                }
            )
            .Add(
                Waypoint,
                new TravelPointDefinition {
                    DisplayName = "Camp waypoint",
                    Kind = TravelKind.Waypoint,
                    To = Queensdale,
                    UnlockedBy = Unlock,
                    Currency = Gold,
                    Cost = 150
                }
            )
            .Add(
                Taxi,
                new TravelPointDefinition {
                    DisplayName = "Griffon to Divinity",
                    Kind = TravelKind.Taxi,
                    From = Queensdale,
                    To = Divinity,
                    Currency = Gold,
                    Cost = 20,
                    Seconds = 45f
                }
            )
            .Add(
                Door,
                new TravelPointDefinition {
                    DisplayName = "The Crypt",
                    Kind = TravelKind.InstanceEntrance,
                    From = Queensdale,
                    To = Crypt,
                    Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Level.Thirty" }]
                }
            )
            .Build();
}

sealed class Traveller : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        value = 0f;

        return false;
    }
}

public class TravelTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly TravelLibrary library;
    readonly Traveller player = new();

    public TravelTests() => library = TravelLibrary.Compile(catalog);

    TravelPoint Point(string address) => library.Find(DefId.From(address))!;

    static Dictionary<uint, long> Purse(long gold) =>
        new() { [DefId.From(Content.Gold).Value] = gold };

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void APortalFromTheWrongMapIsRefused() {
        Assert.Equal(
            TravelRefusal.WrongPlace,
            Travelling.CanUse(Point(Content.Portal), DefId.From(Content.Divinity), player)
        );

        Assert.Equal(
            TravelRefusal.None,
            Travelling.CanUse(Point(Content.Portal), DefId.From(Content.Queensdale), player)
        );
    }

    [Fact]
    public void SomewhereYouAlreadyAreIsRefused() =>
        Assert.Equal(
            TravelRefusal.AlreadyThere,
            Travelling.CanUse(Point(Content.Waypoint), DefId.From(Content.Queensdale), player)
        );

    [Fact]
    public void AWaypointNobodyHasFoundIsLocked() {
        // ⚠ The unlock is a tag, which is how this library asks about a discovery without referencing
        // Vixen.Gameplay.Exploration — and it means a quest or a purchase can unlock one too.
        Assert.Equal(
            TravelRefusal.Locked,
            Travelling.CanUse(Point(Content.Waypoint), DefId.From(Content.Divinity), player)
        );

        player.Tags.Add(catalog.Tags.Resolve(Content.Unlock));

        Assert.Equal(
            TravelRefusal.None,
            Travelling.CanUse(Point(Content.Waypoint), DefId.From(Content.Divinity), player)
        );
    }

    [Fact]
    public void TheUnlockIsCheckedBeforeTheRequirements() {
        // ⚠ "You have not found this yet" is the answer a player needs; "you are not level thirty" is
        // noise when they cannot see the waypoint at all.
        var locked = TravelLibrary.Compile(
            new DefinitionCatalogBuilder()
                .AddTag("Level.Thirty")
                .Add(
                    "travel/both",
                    new TravelPointDefinition {
                        Kind = TravelKind.Waypoint,
                        To = Content.Divinity,
                        UnlockedBy = Content.Unlock,
                        Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Level.Thirty" }]
                    }
                )
                .Build()
        );

        Assert.Equal(
            TravelRefusal.Locked,
            Travelling.CanUse(locked.Find(DefId.From("travel/both"))!, DefId.From(Content.Queensdale), player)
        );
    }

    [Fact]
    public void ARequirementIsChecked() {
        Assert.Equal(
            TravelRefusal.Requirements,
            Travelling.CanUse(Point(Content.Door), DefId.From(Content.Queensdale), player)
        );

        player.Tags.Add(catalog.Tags.Resolve("Level.Thirty"));

        Assert.Equal(
            TravelRefusal.None,
            Travelling.CanUse(Point(Content.Door), DefId.From(Content.Queensdale), player)
        );
    }

    [Fact]
    public void AnOrderCarriesTheFareAndDoesNotTakeIt() {
        // ⚠ A fare taken here and a transfer that then fails is a player who paid to stay put.
        var purse = Purse(500);

        Assert.Equal(
            TravelRefusal.None,
            Travelling.Order(Point(Content.Taxi), Content.Player(1), DefId.From(Content.Queensdale), player, purse, out var order)
        );

        Assert.Equal(DefId.From(Content.Divinity), order.To);
        Assert.Equal(20, order.Cost);
        Assert.Equal(45f, order.Seconds);
        Assert.Equal(500, purse[DefId.From(Content.Gold).Value]);
    }

    [Fact]
    public void SomebodyWhoCannotPayIsRefused() =>
        Assert.Equal(
            TravelRefusal.Cost,
            Travelling.Order(Point(Content.Taxi), Content.Player(1), DefId.From(Content.Queensdale), player, Purse(5), out _)
        );

    [Fact]
    public void AFreePointCostsNothingAndNeedsNoPurse() {
        Assert.Equal(
            TravelRefusal.None,
            Travelling.Order(Point(Content.Portal), Content.Player(1), DefId.From(Content.Queensdale), player, null, out var order)
        );

        Assert.Equal(0, order.Cost);
        Assert.False(order.Currency.IsSome);
    }

    [Fact]
    public void EverythingUsableFromHereIsListed() {
        player.Tags.Add(catalog.Tags.Resolve("Level.Thirty"));

        var here = library.AvailableFrom(DefId.From(Content.Queensdale), player).ToArray();

        // The portal, the taxi and the crypt door. Not the waypoint: it goes where they already are,
        // and they have not found it.
        Assert.Equal(3, here.Length);
        Assert.DoesNotContain(here, point => point.Id == DefId.From(Content.Waypoint));
    }

    [Fact]
    public void APointThatGoesNowhereIsAProblem() {
        var problems = TravelLibrary.Compile(
                new DefinitionCatalogBuilder().Add("travel/odd", new TravelPointDefinition()).Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("goes nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void APriceInNothingIsAProblem() {
        var problems = TravelLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("travel/odd", new TravelPointDefinition { To = Content.Divinity, Cost = 100 })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("costs 100 of nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void AWaypointNothingUnlocksIsAProblem() {
        var problems = TravelLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("travel/odd", new TravelPointDefinition { Kind = TravelKind.Waypoint, To = Content.Divinity })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("it is a portal", StringComparison.Ordinal));
    }

    [Fact]
    public void APointThatGoesFromAMapToItselfIsAProblem() {
        var problems = TravelLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("travel/odd", new TravelPointDefinition { From = Content.Divinity, To = Content.Divinity })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("to itself", StringComparison.Ordinal));
    }
}
