// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Movement.Tests;

/// <summary>A one-seat mount, a four-seat car, and a taxi nobody else may steer.</summary>
public static class Content {
    public const string Raptor = "vehicles/raptor";
    public const string Car = "vehicles/car";
    public const string Taxi = "vehicles/taxi";

    public static PlayerId Player(ulong who) => new(who);

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Skill.Riding")
            .Add(
                Raptor,
                new VehicleDefinition {
                    DisplayName = "Raptor",
                    Kind = VehicleKind.Mount,
                    Tag = "State.Mounted",
                    Seats = [new() { Id = "saddle", Role = SeatRole.Driver, Controls = true, Tag = "State.Driving" }]
                }
            )
            .Add(
                Car,
                new VehicleDefinition {
                    DisplayName = "Roller beetle",
                    Kind = VehicleKind.Ground,
                    Tag = "State.Mounted",
                    Seats = [
                        new() {
                            Id = "wheel",
                            Role = SeatRole.Driver,
                            Controls = true,
                            Tag = "State.Driving",
                            Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Skill.Riding" }]
                        },
                        new() { Id = "shotgun", Role = SeatRole.Gunner },
                        new() { Id = "back-left" },
                        new() { Id = "back-right" }
                    ]
                }
            )
            .Add(
                Taxi,
                new VehicleDefinition {
                    DisplayName = "Griffon",
                    Kind = VehicleKind.Flying,
                    Tag = "State.Mounted",
                    PassengersMaySteer = false,
                    Physics = new() { Ceiling = 500f },
                    Seats = [
                        new() { Id = "pilot", Role = SeatRole.Driver, Controls = true },
                        new() { Id = "seat" }
                    ]
                }
            )
            .Build();
}

sealed class Rider : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        value = 0f;

        return false;
    }
}

public class VehicleTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly MovementLibrary library;

    public VehicleTests() => library = MovementLibrary.Compile(catalog);

    VehicleInstance Instance(string address) => new(library.Find(DefId.From(address))!);

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void AMountIsASingleSeatVehicle() {
        // ⚠ Doc 28's point: it collapses two systems people usually write twice.
        var raptor = library.Find(DefId.From(Content.Raptor))!;

        Assert.Equal(VehicleKind.Mount, raptor.Kind);
        Assert.Single(raptor.Seats.ToArray());
        Assert.Equal(0, raptor.DriverSeat);
    }

    [Fact]
    public void GettingOnGrantsTheVehicleAndTheSeatTags() {
        var raptor = Instance(Content.Raptor);
        var tags = new GameplayTagSet();

        Assert.Equal(SeatRefusal.None, raptor.Mount(Content.Player(1), 0, null, tags));
        Assert.True(tags.Contains(catalog.Tags.Resolve("State.Mounted")));
        Assert.True(tags.Contains(catalog.Tags.Resolve("State.Driving")));
        Assert.Equal(Content.Player(1), raptor.Driver);

        raptor.Dismount(Content.Player(1), tags);

        Assert.False(tags.Contains(catalog.Tags.Resolve("State.Mounted")));
        Assert.False(raptor.IsDriven);
    }

    [Fact]
    public void AnOccupiedSeatIsRefusedAndSoIsBeingAboardTwice() {
        var car = Instance(Content.Car);

        car.Mount(Content.Player(1), 1);

        Assert.Equal(SeatRefusal.Occupied, car.Mount(Content.Player(2), 1));
        Assert.Equal(SeatRefusal.AlreadyAboard, car.Mount(Content.Player(1), 2));
        Assert.Equal(SeatRefusal.Unknown, car.Mount(Content.Player(2), 9));
    }

    [Fact]
    public void ASeatRequirementIsChecked() {
        var car = Instance(Content.Car);
        var rider = new Rider();

        Assert.Equal(SeatRefusal.Requirements, car.Mount(Content.Player(1), 0, rider));
        Assert.Equal(SeatRefusal.None, car.Mount(Content.Player(1), 1, rider));

        car.Dismount(Content.Player(1));
        rider.Tags.Add(catalog.Tags.Resolve("Skill.Riding"));

        Assert.Equal(SeatRefusal.None, car.Mount(Content.Player(1), 0, rider));
    }

    [Fact]
    public void GettingInTakesTheFirstSeatThatWillHaveThem() {
        var car = Instance(Content.Car);
        var rider = new Rider();

        // No riding skill, so the wheel refuses and the gunner's seat takes them.
        Assert.Equal(1, car.MountAny(Content.Player(1), rider));
        Assert.Equal(2, car.MountAny(Content.Player(2), rider));
    }

    [Fact]
    public void TheDriverLeavingDoesNotEjectAnybody() {
        // ⚠ It becomes driverless; the passengers stay where they are.
        var car = Instance(Content.Car);
        var rider = new Rider();

        rider.Tags.Add(catalog.Tags.Resolve("Skill.Riding"));
        car.Mount(Content.Player(1), 0, rider);
        car.Mount(Content.Player(2), 2);

        Assert.Equal(SeatRefusal.None, car.Dismount(Content.Player(1)));
        Assert.False(car.IsDriven);
        Assert.Equal(1, car.Count);
        Assert.True(car.Carries(Content.Player(2)));
    }

    [Fact]
    public void APassengerMayTakeTheWheelWhenThePolicyAllowsIt() {
        var car = Instance(Content.Car);
        var rider = new Rider();

        rider.Tags.Add(catalog.Tags.Resolve("Skill.Riding"));
        car.Mount(Content.Player(1), 2, rider);

        Assert.Equal(SeatRefusal.None, car.MoveTo(Content.Player(1), 0, rider));
        Assert.Equal(Content.Player(1), car.Driver);
    }

    [Fact]
    public void APassengerMayNotTakeTheWheelWhenThePolicyForbidsIt() {
        // ⚠ Both answers ship: a taxi nobody may steer and a raft anybody may.
        var taxi = Instance(Content.Taxi);

        taxi.Mount(Content.Player(1), 1);

        Assert.Equal(SeatRefusal.Forbidden, taxi.MoveTo(Content.Player(1), 0));
        Assert.Equal(SeatRefusal.Forbidden, taxi.Mount(Content.Player(2), 0));
    }

    [Fact]
    public void TheFirstAboardATaxiMayStillBeItsPilot() {
        var taxi = Instance(Content.Taxi);

        Assert.Equal(SeatRefusal.None, taxi.Mount(Content.Player(1), 0));
        Assert.Equal(Content.Player(1), taxi.Driver);
    }

    [Fact]
    public void MovingSeatsIsCheckedBeforeAnybodyMoves() {
        // ⚠ Getting off and then failing to get back on is how somebody ends up in the road.
        var car = Instance(Content.Car);
        var rider = new Rider();

        car.Mount(Content.Player(1), 2);
        car.Mount(Content.Player(2), 3);

        Assert.Equal(SeatRefusal.Occupied, car.MoveTo(Content.Player(1), 3));
        Assert.Equal(SeatRefusal.Requirements, car.MoveTo(Content.Player(1), 0, rider));
        Assert.Equal(2, car.SeatOf(Content.Player(1)));
        Assert.Equal(2, car.Count);
    }

    [Fact]
    public void MovingSeatsSwapsTheTags() {
        var car = Instance(Content.Car);
        var rider = new Rider();
        var tags = new GameplayTagSet();

        rider.Tags.Add(catalog.Tags.Resolve("Skill.Riding"));
        car.Mount(Content.Player(1), 0, rider, tags);

        Assert.True(tags.Contains(catalog.Tags.Resolve("State.Driving")));

        car.MoveTo(Content.Player(1), 2, rider, tags);

        Assert.False(tags.Contains(catalog.Tags.Resolve("State.Driving")));
        Assert.True(tags.Contains(catalog.Tags.Resolve("State.Mounted")));
    }

    [Fact]
    public void EjectingEmptiesIt() {
        var car = Instance(Content.Car);
        var changes = 0;

        car.Changed += _ => changes++;
        car.Mount(Content.Player(1), 1);
        car.Mount(Content.Player(2), 2);

        Assert.Equal(2, car.Eject());
        Assert.True(car.IsEmpty);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void SomebodyNotAboardCannotGetOffOrMove() {
        var car = Instance(Content.Car);

        Assert.Equal(SeatRefusal.NotAboard, car.Dismount(Content.Player(1)));
        Assert.Equal(SeatRefusal.NotAboard, car.MoveTo(Content.Player(1), 0));
        Assert.Equal(-1, car.SeatOf(Content.Player(1)));
    }

    [Fact]
    public void AVehicleWithNoSteeringSeatOrTwoIsAProblem() {
        var problems = MovementLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add("vehicles/none", new VehicleDefinition { Seats = [new() { Id = "a" }] })
                    .Add(
                        "vehicles/two",
                        new VehicleDefinition {
                            Seats = [new() { Id = "a", Controls = true }, new() { Id = "b", Controls = true }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("nobody can drive it", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("both be authoritative", StringComparison.Ordinal));
    }

    [Fact]
    public void AFlyerWithNoCeilingIsAProblem() {
        var problems = MovementLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "vehicles/odd",
                        new VehicleDefinition {
                            Kind = VehicleKind.Flying,
                            Seats = [new() { Id = "a", Controls = true }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("flies to the skybox", StringComparison.Ordinal));
    }

    [Fact]
    public void AMountWithSeveralSeatsIsWorthSayingSoAbout() {
        var problems = MovementLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "vehicles/odd",
                        new VehicleDefinition {
                            Kind = VehicleKind.Mount,
                            Seats = [new() { Id = "a", Controls = true }, new() { Id = "b" }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("whose model is a creature", StringComparison.Ordinal));
    }
}
