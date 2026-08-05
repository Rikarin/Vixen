# Vixen.Gameplay.Movement

Mounts and vehicles as one type with seats. **The seat model only** — the transform half waits on doc
16.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Movement, part of **G7**.

## State

**Built: vehicles with seats, roles, requirements and tags; mount, dismount, seat changes and eject.
17 tests.**

⚠ **Not built, and deliberately not worked around: anything to do with a position.** Doc 28 says
mounts and vehicles are where doc 16's owed **parent-relative replication** stops being optional,
*"because a passenger replicating world coordinates fights the vehicle's own"*. That is item 69 in
[`docs/overview.md`](../../docs/overview.md) and it is still ⬜. Building the networked half now would
mean building a workaround for something that is supposed to land, so nothing here touches a
transform.

| | |
|---|---|
| `VehicleDefinition` · `SeatDefinition` · `VehiclePhysicsDefinition` · `VehicleKind` · `SeatRole` | What a designer authors. |
| `Vehicle` · `Seat` · `MovementLibrary` | Compiled once, with a `Problems` list. |
| `VehicleInstance` · `SeatChange` · `SeatRefusal` | One vehicle in the world. |
| `MovementModule` | One definition type and two tags. |

## The four things worth knowing before reading the code

### A mount is a single-seat vehicle whose model is a creature

Doc 28's point exactly, and it *"collapses two systems people usually write twice"*. `VehicleKind`
changes which physics config a game reaches for and changes nothing in this library.

### Exactly one seat steers, and the compiler says so

A vehicle with none is one nobody can drive. A vehicle with two is one where two clients both think
they are authoritative over the same rigid body — which is a networking bug that would present as
jitter and be diagnosed for a week. Both are reported.

### The driver leaving does not eject anybody

It becomes driverless and the passengers stay. Whether one of them may take the wheel is
`PassengersMaySteer`, a policy, because **both answers ship**: a taxi nobody may steer and a raft
anybody may steer are equally real, and a library that picked one would pick it for every vehicle in
every game.

### Moving seats is checked before anybody moves

Getting off and then failing to get back on is how somebody ends up standing in the road at sixty
miles an hour.

## What is owed

- **The transform**, above. When doc 16 #69 lands, what this library needs from it is that a
  passenger's position is expressed relative to the vehicle's body rather than in world space.
- **Control mapping.** `SeatRole.Gunner` is authored and nothing reads it; what a gunner may fire is
  `Vixen.Gameplay.Shooting`'s and the wiring is a game's.
- **Swimming and gliding**, which doc 28 lists under movement and which are player states rather than
  vehicles — an effect granting a tag, on the kernel, and arguably not this library at all.
