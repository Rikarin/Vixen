---
title: Mounts and vehicles
slug: gameplay/movement
kind: guide
area: Gameplay
summary: One type with seats — a mount is a single-seat vehicle whose model is a creature. The seat model only; the transform half waits on parent-relative replication.
api: [T:Vixen.Gameplay.Movement.VehicleDefinition, T:Vixen.Gameplay.Movement.SeatDefinition, T:Vixen.Gameplay.Movement.VehiclePhysicsDefinition, T:Vixen.Gameplay.Movement.VehicleKind, T:Vixen.Gameplay.Movement.SeatRole, T:Vixen.Gameplay.Movement.Vehicle, T:Vixen.Gameplay.Movement.Seat, T:Vixen.Gameplay.Movement.MovementLibrary, T:Vixen.Gameplay.Movement.VehicleInstance, T:Vixen.Gameplay.Movement.SeatChange, T:Vixen.Gameplay.Movement.SeatRefusal, T:Vixen.Gameplay.Movement.MovementModule]
tags: [gameplay, movement, mount, vehicle, mmo]
since: 0.1
status: preview
related: [gameplay/tags, gameplay/requirements, gameplay/shooting, gameplay/travel]
---

## What it is

`Vixen.Gameplay.Movement` models mounts and vehicles as **one type with seats**. A vehicle is a
compiled `Vehicle` — kind, seats, roles, requirements, tags — and one of them in the world is a
`VehicleInstance` that tracks who is sitting where.

⚠ **Nothing here touches a transform, and that is deliberate rather than unfinished.** Doc 28 puts
mounts and vehicles at the point where **parent-relative replication** stops being optional, *"because
a passenger replicating world coordinates fights the vehicle's own"* — and that is still owed. Building
the networked half now would mean building a workaround for something that is meant to land, so this
library is the seat model and nothing else. Where a vehicle *is* remains the scene's answer.

## What it is for

### A mount is a single-seat vehicle whose model is a creature

Doc 28's point exactly, and it collapses two systems people usually write twice. `VehicleKind` changes
which physics configuration a game reaches for and changes nothing at all in this library.

### Exactly one seat steers, and the compiler says so

A vehicle with no driving seat is one nobody can drive. A vehicle with two is one where two clients
both believe they are authoritative over the same rigid body — a networking bug that presents as
jitter and gets diagnosed for a week. Both are reported in `MovementLibrary.Problems`.

### The driver leaving does not eject anybody

The vehicle becomes driverless and the passengers stay. Whether one of them may take the wheel is
`PassengersMaySteer`, which is **a policy rather than a rule, because both answers ship**: a taxi
nobody may steer and a raft anybody may steer are equally real, and a library that picked one would
pick it for every vehicle in every game.

### Moving seats is checked before anybody moves

⚠ Getting off and then failing to get back on is how somebody ends up standing in the road at sixty
miles an hour. `MoveTo` is one operation for that reason — it is not `Dismount` followed by `Mount`.

## Using it

Mount, and let the vehicle grant the tags the seat carries:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Movement;

static class Riding {
    public static bool Ride(VehicleInstance vehicle, PlayerId player, IRequirementContext context, GameplayTagSet tags) {
        // The requirement context is what a seat's requirements are evaluated against — "may only be
        // gunned by somebody with the artillery training" is authored, not coded.
        var refusal = vehicle.Mount(player, vehicle.Vehicle.DriverSeat, context, tags);

        return refusal == SeatRefusal.None;
    }
}
```

⚠ **Pass the same tag set to `Dismount` that you passed to `Mount`.** The seat's tags are granted into
the set the caller owns, and the library has no reference to it afterwards — a dismount that forgets
leaves the rider tagged as mounted for the rest of the session.

`MountAny` is the "get in" button: it takes the first seat the player qualifies for and returns its
index, or −1.

## Examples

The whole seat lifecycle, with the refusals a client greys buttons out on:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Movement;

static class Taxi {
    public static int Board(VehicleInstance vehicle, PlayerId player) {
        // −1 when nothing is free or nothing they qualify for is free. The two are not distinguished
        // here on purpose: a client that could tell them apart could probe a requirement.
        return vehicle.MountAny(player);
    }

    public static SeatRefusal Shuffle(VehicleInstance vehicle, PlayerId player, int seat) {
        // One operation, checked before anybody moves — see "moving seats is checked before anybody
        // moves". Dismount-then-Mount is the version that strands people.
        return vehicle.MoveTo(player, seat);
    }

    public static bool StillDriven(VehicleInstance vehicle, PlayerId driver) {
        vehicle.Dismount(driver);

        // The passengers stay. Whether one of them may now steer is the vehicle's own policy.
        return vehicle.IsDriven || (!vehicle.IsEmpty && vehicle.Vehicle.PassengersMaySteer);
    }
}
```

Watching who moved, for the replication layer that will eventually care:

```csharp compile
using System;
using Vixen.Gameplay.Movement;

static class Watching {
    public static void Attach(VehicleInstance vehicle, Action<SeatChange> onChange) {
        // SeatChange carries the player, the seat index and whether that seat drives — which is the
        // one bit anything downstream branches on.
        vehicle.Changed += onChange;
    }
}
```

## See also

- [Gameplay tags](tags.md) — what a seat grants.
- [Requirements](requirements.md) — what gates a seat.
- [Travel](travel.md) — getting somewhere, as opposed to riding something.
- [Shooting](shooting.md) — where a `SeatRole.Gunner`'s weapon would come from.
