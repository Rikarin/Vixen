// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Movement;

/// <summary>What sort of thing it is.</summary>
/// <remarks>
///     ⚠ <b>A mount is a single-seat vehicle whose model is a creature, and that is doc 28's whole
///     point here:</b> it <em>"collapses two systems people usually write twice"</em>. The kind
///     changes what physics config a game reaches for; it changes nothing in this library.
/// </remarks>
public enum VehicleKind {
    /// <summary>One seat, and it is an animal.</summary>
    Mount,

    /// <summary>Wheels.</summary>
    Ground,

    /// <summary>Wings.</summary>
    Flying,

    /// <summary>On the water.</summary>
    Boat,

    /// <summary>Under it.</summary>
    Submarine
}

/// <summary>What somebody in a seat may do.</summary>
public enum SeatRole {
    /// <summary>Steer it.</summary>
    Driver,

    /// <summary>Ride in it.</summary>
    Passenger,

    /// <summary>Ride in it and shoot.</summary>
    Gunner
}

/// <summary>Why a mount or a dismount was refused.</summary>
public enum SeatRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>There is no such vehicle or seat.</summary>
    Unknown,

    /// <summary>Somebody is in it.</summary>
    Occupied,

    /// <summary>They are already aboard.</summary>
    AlreadyAboard,

    /// <summary>They are not aboard.</summary>
    NotAboard,

    /// <summary>A requirement is not met.</summary>
    Requirements,

    /// <summary>The seat is not one they may take.</summary>
    Forbidden
}

/// <summary>One place somebody can sit.</summary>
[DataContract("VehicleSeat")]
public sealed class SeatDefinition {
    /// <summary>What it is called within its vehicle.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What somebody in it may do.</summary>
    public SeatRole Role { get; set; } = SeatRole.Passenger;

    /// <summary>Whether whoever is in it steers.</summary>
    public bool Controls { get; set; }

    /// <summary>What sitting in it grants — <c>State.Mounted</c>.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>What has to be true to sit in it.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];
}

/// <summary>What the physics wants. A few numbers, not a physics engine.</summary>
/// <remarks>
///     ⚠ <b>Deliberately thin.</b> A vehicle's real configuration is <c>Vixen.Net.Physics</c>'s rigid
///     body, and doc 28 says the networking is that library's existing authority rather than anything
///     new. What is here is what a designer sets per vehicle and a game hands to the body.
/// </remarks>
[DataContract("VehiclePhysics")]
public sealed class VehiclePhysicsDefinition {
    /// <summary>How fast it goes, in metres a second.</summary>
    public float MaximumSpeed { get; set; } = 10f;

    /// <summary>How fast it gets there.</summary>
    public float Acceleration { get; set; } = 5f;

    /// <summary>How fast it turns, in degrees a second.</summary>
    public float TurnRate { get; set; } = 120f;

    /// <summary>How high it may go, or zero for something that stays on the ground.</summary>
    public float Ceiling { get; set; }
}

/// <summary>A mount, a car, a glider, a boat or a submarine — one type.</summary>
[DataContract("VehicleDefinition")]
public sealed record VehicleDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What sort it is.</summary>
    public VehicleKind Kind { get; set; }

    /// <summary>What being in it at all is — <c>State.Mounted</c>. Empty for one nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Its seats. The first that controls is the driver's.</summary>
    public List<SeatDefinition> Seats { get; set; } = [];

    /// <summary>What the physics wants.</summary>
    public VehiclePhysicsDefinition Physics { get; set; } = new();

    /// <summary>Whether a passenger may take the driver's seat when it is empty.</summary>
    /// <remarks>
    ///     ⚠ <b>A policy, because both answers ship.</b> A taxi nobody may take the wheel of and a
    ///     raft anybody may steer are both real, and a library that picked one would be picking it for
    ///     every vehicle in every game.
    /// </remarks>
    public bool PassengersMaySteer { get; set; } = true;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var seat in Seats) {
            if (seat.Tag.Length > 0) {
                tags.Add(seat.Tag);
            }

            foreach (var requirement in seat.Requires) {
                if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                    tags.Add(requirement.Subject);
                }
            }
        }
    }
}

/// <summary>A seat with its names resolved.</summary>
public sealed class Seat {
    internal Seat(SeatDefinition definition, int index, GameplayTag tag, RequirementSet requirements) {
        Definition = definition;
        Index = index;
        Tag = tag;
        Requirements = requirements;
    }

    /// <summary>What it was compiled from.</summary>
    public SeatDefinition Definition { get; }

    /// <summary>Which of its vehicle's seats it is.</summary>
    public int Index { get; }

    /// <summary>What it is called within its vehicle.</summary>
    public string Id => Definition.Id;

    /// <summary>What somebody in it may do.</summary>
    public SeatRole Role => Definition.Role;

    /// <summary>Whether whoever is in it steers.</summary>
    public bool Controls => Definition.Controls;

    /// <summary>What sitting in it grants.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What has to be true to sit in it.</summary>
    public RequirementSet Requirements { get; }
}

/// <summary>A vehicle with its seats compiled.</summary>
public sealed class Vehicle {
    readonly Seat[] seats;

    internal Vehicle(VehicleDefinition definition, GameplayTag tag, Seat[] seats) {
        Definition = definition;
        Tag = tag;
        this.seats = seats;
        DriverSeat = Array.FindIndex(seats, seat => seat.Controls);
    }

    /// <summary>What it was compiled from.</summary>
    public VehicleDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What sort it is.</summary>
    public VehicleKind Kind => Definition.Kind;

    /// <summary>What being in it is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>Its seats.</summary>
    public ReadOnlySpan<Seat> Seats => seats;

    /// <summary>Which seat steers, or −1.</summary>
    public int DriverSeat { get; }

    /// <summary>Whether a passenger may take the wheel when it is empty.</summary>
    public bool PassengersMaySteer => Definition.PassengersMaySteer;

    /// <summary>What the physics wants.</summary>
    public VehiclePhysicsDefinition Physics => Definition.Physics;

    /// <summary>Finds a seat.</summary>
    /// <param name="id">Its id within the vehicle.</param>
    /// <returns>Its index, or −1.</returns>
    public int IndexOf(string? id) => Array.FindIndex(seats, seat => string.Equals(seat.Id, id, StringComparison.Ordinal));
}

/// <summary>Every vehicle a build knows, compiled once.</summary>
public sealed class MovementLibrary {
    readonly Dictionary<uint, Vehicle> vehicles;
    readonly string[] problems;

    MovementLibrary(Dictionary<uint, Vehicle> vehicles, string[] problems) {
        this.vehicles = vehicles;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static MovementLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every vehicle, in address order.</summary>
    public IEnumerable<Vehicle> Vehicles =>
        vehicles.Values.OrderBy(vehicle => vehicle.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static MovementLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var vehicles = new Dictionary<uint, Vehicle>();

        foreach (var definition in catalog.OfType<VehicleDefinition>()) {
            var controlling = definition.Seats.Count(seat => seat.Controls);

            // ⚠ Exactly one. A vehicle with none is one nobody can drive; a vehicle with two is one
            // where two clients both think they are authoritative over the same rigid body.
            if (controlling == 0) {
                problems.Add($"'{definition.Address}' has no seat that steers, so nobody can drive it.");
            } else if (controlling > 1) {
                problems.Add(
                    $"'{definition.Address}' has {controlling} seats that steer, and two clients cannot "
                    + "both be authoritative over one body."
                );
            }

            if (definition.Kind == VehicleKind.Mount && definition.Seats.Count > 1) {
                problems.Add(
                    $"'{definition.Address}' is a mount with {definition.Seats.Count} seats — which is a "
                    + "vehicle whose model is a creature, and is worth saying so."
                );
            }

            if (definition.Kind == VehicleKind.Flying && definition.Physics.Ceiling <= 0f) {
                problems.Add($"'{definition.Address}' flies and has no ceiling, so it flies to the skybox.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seats = new Seat[definition.Seats.Count];

            for (var index = 0; index < seats.Length; index++) {
                var seat = definition.Seats[index];

                if (seat.Id.Length > 0 && !seen.Add(seat.Id)) {
                    problems.Add($"'{definition.Address}' has two seats called '{seat.Id}'.");
                }

                seats[index] = new(seat, index, tags.Resolve(seat.Tag), RequirementSet.Compile(seat.Requires, tags));
            }

            vehicles.Add(definition.Id.Value, new(definition, tags.Resolve(definition.Tag), seats));
        }

        return new(vehicles, [.. problems]);
    }

    /// <summary>Finds a vehicle.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Vehicle? Find(DefId id) => vehicles.GetValueOrDefault(id.Value);
}
