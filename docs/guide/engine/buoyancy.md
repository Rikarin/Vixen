---
title: Floating things on water
slug: engine/buoyancy
kind: guide
area: Engine
summary: Pontoons over Jolt, evaluated at the fixed step's water time — a component, a system, and the three orderings that are silent when they are wrong.
api: [T:Vixen.Water.Physics.BuoyancyBody, T:Vixen.Water.Physics.BuoyancyState, T:Vixen.Water.Physics.BuoyancySystem, T:Vixen.Rendering.Water.WaterClockSystem, T:Vixen.Water.IWaterSurface]
tags: [water, buoyancy, physics, pontoon, boat, raft]
since: 0.1
status: preview
related: [engine/water-surface, engine/character-movement, engine/splines]
---

## What it is

The join between `Vixen.Water`'s arithmetic and a physics world. Put a `BuoyancyBody` on an entity
that already has a `Collider` and a `RigidBody`, add a `BuoyancySystem`, and it floats.

| Piece | What it is |
|---|---|
| `BuoyancyBody` | The pontoons, in the body's own frame, and how they float |
| `BuoyancyState` | What the last step did — wet count, mean submersion, lift, surface height |
| `BuoyancySystem` | The fixed-step pass that applies the forces |
| `WaterClockSystem` | The one thing that advances the water time, early enough for the above |

It is its own assembly. `docs/plan/35` § D1: **the water kernel is what a dedicated server runs**, so
nothing in it may link a physics engine — and a game with water and no rigid bodies must not link Jolt
to draw a lake.

## What it is for

A crate in a river, a raft a player stands on, a buoy that bobs. Anything whose interaction with water
is *displacement* rather than a movement mode — a swimming character is
[character movement](character-movement.md)'s `CharacterMoveMode.Swimming` and does not come through
here.

## Using it

```csharp no-compile="the shape of a setup, not a compiling scene"
// A raft: four pontoons at the corners, because one cannot lean.
world.Add(raft, BuoyancyBody.Raft(halfLength: 2.5f, halfWidth: 1.5f, radius: 0.7f));
world.Add<BuoyancyState>(raft);

// The clock first — see below — then the solver, pointed at whatever knows where the water is.
engine.Add(new WaterClockSystem(zones));
engine.Add(new BuoyancySystem(scene, zones));
```

`BuoyancyBody.Sphere` is a crate or a barrel; `BuoyancyBody.Raft` is anything that should pitch and
roll. A list of `BuoyancyPontoon` is the general case.

### ⚠ The pontoons are a displacement volume, not the collider

A barge is four large spheres and a canoe is six small ones along its keel. Matching them to the hull
mesh is how a boat ends up floating a hand's width too high; matching them to the collider is how it
ends up floating on its bounding box.

⚠ **Four and not one, for anything that should lean.** A force at a single point cannot produce a
torque about that point, so a hull with one pontoon bobs and never rolls — which reads as a boat on
rails and is invisible in any test that measures only a height.

### ⚠ Three orderings, and every one of them is silent when it is wrong

| Ordering | What breaks | What it looks like |
|---|---|---|
| After `PhysicsStepSystem` | Jolt clears accumulated forces at the step | The boat sinks with every counter non-zero |
| Before `PhysicsSyncSystem` | The body does not exist yet | The first step of every boat, lost |
| Clock in `PreRender` | `FixedUpdate` is earlier in the same frame | The boat is one frame of swell behind the water drawn under it |

The first two are the `[UpdateAfter]`/`[UpdateBefore]` attributes on `BuoyancySystem`, and a test
asserts they are there. The third is why `WaterClockSystem` exists as a separate system at all: the
zone fold *has* to be in `PreRender`, because a body is rasterised where `TransformSystem` has just
put it, so the clock cannot be advanced there.

⚠ **A host that forgets `WaterClockSystem` gets a still sea rather than a subtly wrong one.** That is
deliberate — a fallback advance inside the zone system would be a second writer, and one clock is the
whole point.

### ⚠ Jolt's own buoyancy is deliberately not used

It takes a **plane**, which is exactly the approximation a wave surface is not. Using it would also
put a second definition of the water surface inside the physics engine, where § D2's seam test cannot
reach it.

### ⚠ No ripples reach the solver, and that is what makes a boat predictable

The closed-form wave sum is a function of position and time, so a server can answer *where was the
surface six ticks ago* without having simulated the intervening frames. A ripple field is a simulation
whose state **is** its history and cannot answer that at all.

So a buoyant body needs no replication of its own: it rides `Vixen.Net.Physics`' existing rigid-body
path unchanged, because the force is a pure function of things both peers already have — the pontoons
(authored), the pose and velocity (replicated), the field and the spectrum (content), and the water
time (one clock, derived from the tick).

### ⚠ The pose is read from the simulation, not from `WorldTransform`

`TransformSystem` runs in `LateUpdate`, so in the fixed step that component holds *last frame's* pose
and a boat floated from it is floated where it was. `BuoyancySystem` therefore declares no transform
access at all.

## Examples

**Reading what happened**, which is the difference between "buoyancy is broken" and "two of four
pontoons are dry".

```csharp no-compile="a readout, not a compiling scene"
var state = world.Read<BuoyancyState>(raft);

if (!state.IsFloating) {
    // Outside every zone's window, or above the water. The state is written either way rather than
    // left stale — a readout showing four wet pontoons on a body in mid-air is worse than none.
}

Console.WriteLine($"{state.Wet}/{state.Total} wet, {state.Submerged:P0} under, {state.Lift:N0} N");
```

**An unset coefficient is one, not zero.** A chunk's column is zeroed memory, so a component added
from the inspector without the field filled in holds zero — and zero lift is a crate that sinks, which
reads as the whole system being unwired rather than as a field nobody typed. `BuoyancyBody.Settings`
is the seam where unset becomes the default.

## See also

- [Where the water surface is](water-surface.md) — the kernel this floats things on.
- [Character movement](character-movement.md) — swimming, which is a movement mode and not this.
- `docs/plan/35-water.md` § D10 — pontoons over Jolt at the fixed step's water time, and why.
