# Vixen.Water.Physics

Everything that joins the one water surface to a world with bodies in it. [docs/plan/35][35] § W7's
world-facing half — a component, a system, and the fixed step between them — and § D11's, which is one
number written onto a character.

Two systems, and they are the same seam with a different force:

| System | Reads | Writes |
|---|---|---|
| `BuoyancySystem` | `BuoyancyBody`, the pose out of Jolt | Forces on a rigid body, and `BuoyancyState` |
| `WaterImmersionSystem` | `CharacterMovement`, `LocalTransform` | `CharacterState.Immersion` — the whole of what `CharacterMoveMode.Swimming` was waiting for |

⚠ **`WaterImmersionSystem` lived in `Samples/13-ThirdPersonShooter` until it was moved here**, which
meant the fourth move mode was a feature no game could use without copying source. Its requirements
were always this assembly's exactly: the character components come from `Vixen.Physics` and the query
from `Vixen.Water`, and those two references are what this project *is*.

Twenty lines of arithmetic already exist in `Vixen.Water` — the exact spherical cap, the physical
damping terms, the flow drag, and `RestDisplacement`, all tested against an analytic answer. What is
here is everything that touches a world.

## The immersion is one number, and four rules about it are load-bearing

⚠ **`CharacterMoveMode.Swimming` was appended and must never be reordered.** The mode is a byte in a
component, so it is a byte in every saved scene and on the wire — renumbering it is a save-game and a
protocol break at once.

⚠ **The immersion is *state* rather than an argument to `CharacterMotion.Step`**, which is
[16](../../docs/plan/16-networking.md)'s requirement rather than a convenience: a predicted step is
re-simulated whenever a snapshot disagrees, so everything the rules read has to be part of what a
rollback restores.

⚠ **Water beats the ground.** A character wading out of its depth is still standing on the bed at the
moment it starts to swim, so the immersion test is asked before the ground test rather than after it.

⚠ **A zeroed component never swims**, which is what makes every scene saved before this load
unchanged: an immersion of zero is a character on dry land, and the fourth mode is unreachable rather
than mis-entered.

## Why it is its own assembly

[§ D1][35]. **The water kernel is what a dedicated server runs**, so nothing in it may open a device
or link a physics engine — a headless build still has to answer how deep the water is for every
swimming character and every boat it simulates. And a game with water and no rigid bodies must not
link Jolt to draw a lake.

`Vixen.Audio.Physics`' arrangement exactly, and for the same shape of reason: the kernel answers where
the surface is and knows nothing about a body, the physics world integrates forces and knows nothing
about a wave, and this is the join.

⚠ **Note what it does not reference: `Vixen.Rendering.Water`.** The zone fold lives there, and a
solver that reached for it would drag a graphics device into the path a dedicated server runs. So the
solver finds the water through `IWaterSurface`, which is a *kernel* interface — `WaterZoneSystem`
implements it on a client, and a headless build implements it from its own fold.

## A game references this package itself, and nothing will tell it to

`Vixen.App.Hosting` links `Vixen.Rendering.Water`, so a zone and a body reach every game with a
`!WaterSurface` node in its frame document. **It deliberately does not link this one**, because
linking it would put Jolt in every host that draws a lake — the exact thing the section above says
must not happen.

What that costs has to be stated rather than discovered:

> A `[ModuleInitializer]` cannot run in a process that never loads the assembly.

`BuoyancyBody` and `BuoyancyState` are declared to `SceneComponentRegistry` from an initializer this
assembly emits, so in a game that has not added the reference the declaration never happens and a
scene naming `!BuoyancyBody` fails to load with *"This build has no component called
'BuoyancyBody'"*. That is loud and it is the right failure; it does not name the package, and this
paragraph is where it is named.

⚠ **Swimming's version of that cost is quieter, and worth stating separately.**
`WaterImmersionSystem` declares no component, so a game that never references this package gets no
error at all: `CharacterState.Immersion` stays at zero, `CharacterMoveMode.Swimming` is never entered,
and a character walks along the bed of a lake that draws perfectly. Nothing is wrong as far as any
rule can tell — a character with no immersion writer is a character on dry land.

⚠ **The editor links it unconditionally, and not because an editor runs physics.** A component whose
assembly is not loaded is missing from Add ▸ and takes a scene naming it down on load — so a boat
could not be *authored* before it could be opted into. `EditorApplication.BuiltInSubsystems` is the
list that touches such an assembly, and it has to run before the scene file is read.

## The phase order is load-bearing, and both halves of it fail silently

`BuoyancySystem` runs in `FixedUpdate`, **after `PhysicsSyncSystem` and before `PhysicsStepSystem`**.

- After the step, Jolt has already cleared the accumulated forces, so every force is thrown away. The
  symptom is a boat that sinks with the system visibly running and every counter non-zero.
- Before the sync, the force lands on a body the sync has not created yet — the first step of every
  boat, lost.

Neither looks like a bug in the ordering. The attributes are asserted by a test for that reason.

`WaterImmersionSystem` is in the same phase and is ordered **before `CharacterMovementSystem`**, which
is what reads the number it writes. One system later and the character reads a step-old immersion —
which at a shoreline is a character that starts swimming a step after it should have and starts wading
a step after it should have. Nothing fails; it is simply always slightly behind the water it is in.

## One water clock, and it is why `WaterClockSystem` exists

`WaterZoneSystem` folds in `PreRender`, because a body has to be rasterised where `TransformSystem`
has just put it. `FixedUpdate` is **earlier in the same frame**. So a clock advanced during the fold
hands a solver last frame's water time while the vertex stage draws this frame's, and a boat sits
exactly one frame of swell behind the water underneath it — constant, small, and invisible until the
frame rate changes.

That is the drift [§ D2][35]'s whole seam test exists to prevent, arriving through the back door of a
phase order. `WaterClockSystem` is in `EarlyUpdate` and is the only thing that writes the clock.

## The pose comes out of the simulation, not out of a component

`WorldTransform` is written by `TransformSystem` in `LateUpdate`, so in this phase it holds *last
frame's* pose. A boat floated from it is floated where it was. The simulation has the answer to hand,
so that is what is read — which is also why this system declares no transform access at all.

## Jolt's own buoyancy is deliberately not used

[§ D10][35]. It takes a **plane**, which is exactly the approximation a wave surface is not — and
using it would put a second definition of the water surface inside the physics engine, where § D2's
seam test cannot reach it.

## No ripples, and the omission is the design

The closed-form wave sum is a function of position and time, so a server can answer *where was the
surface six ticks ago* without having simulated the intervening frames. A ripple field is a simulation
whose state **is** its history and cannot answer that at all — [§ D12][35]'s asymmetry, which is why
the ripple contribution is a separate argument to `WaterEvaluator` rather than part of it.

So a buoyant body needs no replication of its own. It rides `Vixen.Net.Physics`' existing rigid-body
path unchanged, because the force is a pure function of things both peers already have: the pontoons
(authored), the pose and velocity (replicated), the field and the spectrum (content), and the water
time (one clock, derived from the tick). A thousand ticks run bit-identically twice, and that test is
what would fail if somebody helpfully threaded a wake through the solver.

## The pontoons are a displacement volume, not a collider

A barge is four large spheres and a canoe is six small ones along its keel. Matching them to the hull
mesh is how a boat ends up floating a hand's width too high; matching them to the collider is how it
ends up floating on its bounding box.

⚠ **Four and not one**, for anything that should lean. A single pontoon bobs and never rolls, because
a force at one point cannot produce a torque about it — the corners are what tell the solver about the
attitude. `BuoyancyBody.Raft` is the shape that matters.

⚠ **And `BuoyancyPontoon` needs its own `[DataContract]`, which it shipped without.** A declared scene
component is not enough on its own: the pontoon list is `BuoyancyBody`'s only load-bearing field, and
without a contract on the element type it could not be written in a file at all. A body with no
pontoons floats nowhere and is *not an error*, so an authored boat loaded, looked complete in the
inspector, and sank. `WaterSceneRunsTests` — a pond, a `.vxwaves`, a spline resolved by name and a
dinghy, folded and stepped through Jolt — is the fixture that found it, and the reason it exists.

## What is not here

⚠ **The `water.showBuoyancy` wiring, which is not the same as the lines.** The lines are written and
tested — `BuoyancyDebugDraw` draws the pontoon spheres, the submerged fractions and the force arrows —
and the verb is registered in `Vixen.Rendering.Water`. What nothing does is join them: the draw reads
its own `Enabled` deliberately, no host copies `WaterDebug.ShowBuoyancy` into it, and no host calls
`Draw`. So the toggle sets a bool that nothing consumes, in the editor's Water menu as well as the
console — where the other five water verbs are driven per frame by `WaterPresenter`.

And the boat that *steers* with these forces, which is
[28 § Vixen.Gameplay.Movement](../../docs/plan/28-gameplay-framework.md)'s.

[35]: ../../docs/plan/35-water.md
