---
title: Swimming
slug: engine/swimming
kind: guide
area: Engine
summary: One number written every fixed step — how much of a character's capsule is under water — and the movement mode that whole feature is made of.
api: [T:Vixen.Water.Physics.WaterImmersionSystem, T:Vixen.Physics.Characters.CharacterMoveMode, T:Vixen.Water.IWaterSurface]
tags: [water, swimming, characters, movement, immersion]
since: 0.2
status: preview
related: [engine/buoyancy, engine/water-surface, engine/character-movement]
---

## What it is

`WaterImmersionSystem` writes `CharacterState.Immersion` — the fraction of a character's capsule below
the water surface, 0 to 1 — once per fixed step, for every character in the world.

That is the whole of swimming's new machinery. Everything downstream already exists:

| Piece | Where it is | What it does with the number |
|---|---|---|
| `CharacterMovement.SwimThreshold` / `WadeThreshold` | `Vixen.Physics` | The two thresholds, with a gap, that decide the mode |
| `CharacterMotion` | `Vixen.Physics` | Wading speed scale, the swim restoring force, the drag |
| `CharacterMoveMode.Swimming` | `Vixen.Physics` | The fourth mode, entered when immersion crosses the upper threshold |
| `WaterImmersionSystem` | `Vixen.Water.Physics` | **Writes the number.** Without it none of the above can ever run |

It lives in `Vixen.Water.Physics` — the same assembly as [buoyancy](buoyancy.md), and for the same
reason. `docs/plan/35` § D1: the water kernel is what a dedicated server runs, so nothing in it may
link a physics engine, and `Vixen.Physics` may not reach into the water stack. A character's immersion
needs `CharacterMovement` from one side and a water query from the other, and that pair of references
is exactly what this assembly is.

## What it is for

A character that walks into a lake and starts swimming, and a character that swims to a shore and
walks out. A crate bobbing beside them is [buoyancy](buoyancy.md) and does not come through here: that
turns immersion into a lift on a rigid body, this turns it into a movement mode on a character
controller.

## Using it

### ⚠ A game has to reference the package itself, and nothing will tell it to

`Vixen.App.Hosting` links `Vixen.Rendering.Water`, so a zone and a body reach any game with a
`!WaterSurface` node in its frame document — and a lake draws perfectly with no reference to this
package at all. What it will not do is make anybody swim. `CharacterState.Immersion` stays at zero,
`CharacterMoveMode.Swimming` is never entered, and a character walks along the bed of a lake that
looks entirely correct in a screenshot.

There is no error, because nothing is wrong: a character with no immersion writer is a character on
dry land as far as every rule can tell. Add the package:

```xml
<ProjectReference Include="…/Vixen.Water.Physics/Vixen.Water.Physics.csproj" />
```

### Constructing it

It takes an `IWaterSurface` — the *kernel* interface, not the zone fold. On a client that is the
`WaterZoneSystem` the host already built; on a dedicated server it is whatever folded the zones there.
That is the seam that keeps a graphics device out of a headless build.

```csharp no-compile="a game's wiring, not a compiling scene"
// `graphics.Water` is the WaterZoneSystem AppGraphics builds for every game.
var immersion = new WaterImmersionSystem(graphics.Water);

loop.Add(immersion);
```

Its phase and ordering are attributes and a game never sets them by hand:
`[UpdateInGroup(SystemPhase.FixedUpdate)]` and `[UpdateBefore(typeof(CharacterMovementSystem))]`.

### ⚠ The ordering is silent when it is wrong

`CharacterMovementSystem` runs after `PhysicsStepSystem` and reads the immersion to pick the mode.
Written any later, the number it reads is one step old — which at a shoreline is a character that
starts swimming a step after it should have and starts wading a step after it should have. Nothing
fails; the character is simply always slightly behind the water it is standing in.

The clock has the same shape. There is one water time and `WaterClockSystem` is its only writer;
this system reads it off the surface rather than off `GameTime`, so a swimmer bobs on the same swell
that is drawn under them.

### The thresholds have a gap, and it is not an accident

A character chest-deep in water with a 30 cm swell crosses any *single* threshold about twice a
second. `SwimThreshold` (0.8 by default) is what it takes to start swimming and `WadeThreshold` (0.6)
is what it takes to stop, and the gap between them should be at least the local wave amplitude. Set
them equal and the symptom is an animation state machine that stutters between wade and swim rather
than anything that looks like a physics bug.

## Examples

**Reading what happened**, which is the difference between "swimming is broken" and "nobody has walked
into the lake yet".

```csharp no-compile="a readout, not a compiling scene"
var state = world.Read<CharacterState>(player);

Console.WriteLine($"{state.Immersion:P0} under, mode {state.Mode}");

// And across the whole world, the number that says the system ran at all.
Console.WriteLine($"{immersion.Swimming} characters are out of their depth");
```

⚠ **`Swimming` at zero with a character visibly in a lake is the diagnostic worth knowing.** A zone
whose spline never resolved answers every query with dry land — see `WaterZoneSystem.UnresolvedBodies`
— and the water still draws. The character walks along the bed and nothing reports a problem.

**The capsule is twice the shape offset, and this is why it is measured that way.** A character's
origin is at its feet; `CharacterMovement.ShapeOffset` lifts the capsule's centre off them, so the
crown is the same distance again above the centre. A writer that passed the offset itself would read a
saturated 1.0 for anything more than shoulder-deep — a plausible-looking answer that is wrong at every
depth below it.

## See also

- [Floating things on water](buoyancy.md) — the same join, turning immersion into a lift instead.
- [Where the water surface is](water-surface.md) — the kernel this asks, and the zone fold that answers.
- [Character movement](character-movement.md) — the other three modes, and the state this writes into.
- `docs/plan/35-water.md` § D11 — swimming as a fourth move mode, and why immersion is the only new number.
