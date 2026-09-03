# Vixen.Physics

Rigid-body physics on [Jolt](https://github.com/jrouwe/JoltPhysics) 2.22.0. Bodies, shapes,
constraints, a character controller, scene queries and triggers, plus the ECS bridge that steps it
once per fixed step and hands the renderer an interpolated transform.

Spec: [docs/plan/14-roadmap.md](../../docs/plan/14-roadmap.md) § Phase 8. Dependency register:
[docs/plan/01](../../docs/plan/01-technology-decisions.md) § ADR — `JoltPhysicsSharp` 2.22.0.

## The shape of it

```
PhysicsWorld                       — the whole engine-facing surface of Jolt
 ├── PhysicsShapes                 — shape registry: interned descriptions → one native shape each
 ├── bodies                        — BodyHandle, create/destroy/move/force/query
 ├── constraints                   — ConstraintHandle: fixed, point, hinge, slider, distance, cone
 ├── characters                    — CharacterController, one per player or NPC that walks
 ├── queries                       — Raycast, RaycastAll, Overlap*, ShapeCast, CheckPoint
 └── events                        — ContactEvent, TriggerEvent, per step

PhysicsScene                       — the ECS bridge
 ├── Synchronize(dt)               — components → bodies
 ├── Step(dt)                      — the simulation, and events translated into entity terms
 └── Writeback()                   — bodies → LocalTransform, velocities, interpolation state
```

**Nothing above `PhysicsWorld` names a Jolt type.** That is what makes the binding replaceable, and
what lets the API be shaped for Vixen's conventions — right-handed Y-up mathematics, handles rather
than native objects, and a fixed step it does not own.

## Standing something up

```csharp
using var world = new PhysicsWorld();

var ground = world.Shapes.Box(new Vector3(50f, 1f, 50f));
var crate  = world.Shapes.Box(0.5f);

world.CreateBody(BodyDescription.Static(ground, new(0f, -1f, 0f)));
var box = world.CreateBody(BodyDescription.Dynamic(crate, new(0f, 5f, 0f)) with { Mass = 20f });

world.OptimizeBroadPhase();          // once, after the level is in place

for (var step = 0; step < 240; step++) {
    world.Step(1f / 60f);

    foreach (var contact in world.Contacts) { /* thuds, damage, decals */ }
}
```

`Step` takes a delta and does not accumulate. The accumulator is
`Vixen.Engine.Frames.FixedStepAccumulator`, and there is exactly one of it — a physics world that ran
its own steps would drift from the simulation phase it is part of, and a replay would stop
reproducing.

## In the ECS

```csharp
using var loop  = new EngineLoop();
using var scene = new PhysicsScene(loop.World);

loop.AddPhysics(scene);              // three fixed-step passes + render-time interpolation

var crate = loop.World.Create(LocalTransform.At(new(0f, 5f, 0f)));
loop.World.Add(crate, Collider.Of(scene.Shapes.Box(0.5f)));
loop.World.Add(crate, RigidBody.Dynamic());
loop.World.Add<PhysicsInterpolation>(crate);
```

| Component | What it is |
|---|---|
| `Collider` | The volume, its layer and its material. On its own it is a **static** body. |
| `RigidBody` | Makes it move — dynamic or kinematic — plus mass, damping, locked axes. |
| `LinearVelocity`, `AngularVelocity` | Mirrored both ways: read after the step, written in before it. |
| `PhysicsInterpolation` | Opt in to being drawn between the last two steps rather than on them. |
| `PhysicsTeleport` | A tag: "I moved this transform, push it into the body." |
| `PhysicsBody` | Written by the bridge. The handle, and what the body was built from. |

**Why `PhysicsTeleport` exists.** The bridge writes `LocalTransform` every step for every dynamic
body, so "the transform changed since I last looked" is true every step and says nothing about who
changed it. The tag is how game code says it was them; the bridge acts on it and takes it off again.
A kinematic body needs none of this — its transform is authored by definition and the bridge drives
the body towards it every step.

**And the tag also collapses the smoothing.** `PhysicsInterpolation` holds the last two simulated
poses, and a teleport makes those two the two ends of the jump — so a body with both components was
drawn *crossing the level* over the following fixed step, and on a frame exactly one step long,
where `alpha` is zero, it was drawn at the position it had just left. `PhysicsScene.Arrive` puts both
poses on the destination, so the body arrives and then goes on being smoothed from there.

⚠ **Never a distance.** A body genuinely moving at 200 m/s covers the same gap in a step, so any
threshold big enough to catch a teleport is big enough to un-smooth a projectile — the wall-clock
threshold in another costume. The tag is the caller saying so, and it is the only thing that actually
tells the two apart. `NetworkRigidBodyCorrectionSystem`'s hard snap adds it and its *soft* correction
deliberately does not, so a steered body stays smoothed.

**A character is told by the adopt instead.** It has no tag by design (see below), so
`StepCharacters` collapses the smoothing on the step where `Adopt` returns true — which is already a
provenance question rather than a geometric one, because `PhysicsInterpolation.DrawnPosition` is what
proves the transform was written by somebody other than the smoothing. A walking character is adopted
zero times, so a walk is never mistaken for a teleport. Position only: rotation is never adopted.

**A character needs no tag, and that is a claim with a scar on it.** Writing a character's
`LocalTransform` *is* the teleport: `PhysicsScene.Adopt` takes anything disagreeing with the
controller and snaps the controller to it, which is what makes a respawn, a checkpoint load and a
rollback reach a `CharacterController` at all. The premise underneath — that nothing else writes a
character's transform between two steps — was false for as long as it was written down.
`PhysicsInterpolationSystem` writes it every frame, and on a frame one fixed step long it writes the
*previous* step's pose, so every other step was adopted away and every character in the engine walked
at exactly half its `WalkSpeed`. The smoothing now records what it wrote in
`PhysicsInterpolation.DrawnPosition` and the bridge ignores a transform still sitting on it;
`PhysicsScene.CharacterAdoptionCount` is the number to watch, because a walking character is adopted
zero times and that one was adopted sixty times a second.

**A body's entity should be a root.** Physics works in world space and the bridge reads and writes
`LocalTransform`, so an entity with a `Parent` has its local transform treated as though it were a
world one — physics and the transform hierarchy then write the same component meaning different
things. Attach things to a body with a joint, or hang children *under* the body rather than making
the body a child.

## Layers

Thirty-two layers, a symmetric collision matrix, and a two-way split of the broad phase into static
and moving.

```csharp
var layers = PhysicsLayers.Define()
    .Add("Level",   PhysicsBroadPhase.Static)
    .Add("Props")
    .Add("Player")
    .Add("Trigger")
    .Separate("Trigger", "Trigger")
    .Build();

using var world = new PhysicsWorld(new() { Layers = layers });
```

The broad-phase class is a *performance* classification and not a collision one: Jolt keeps one
bounding-volume tree per broad-phase layer and never tests a pair drawn from the same static tree, so
putting the level on a `Static` layer is what stops a hundred thousand immobile triangles from being
considered against each other every step. What collides with what is the matrix.

## Queries

```csharp
if (world.Raycast(eye, forward, 100f, out var hit, QueryFilter.Excluding(self))) { … }

foreach (var overlap in world.OverlapSphere(blast, 5f, QueryFilter.On(damageableLayers))) { … }

if (world.ShapeCast(bullet, muzzle, aim, velocity * dt, out var impact)) { … }
```

`RaycastAll` and the overlap queries return a span into a buffer the world reuses, valid until the
next query — the same contract `DebugDraw.Lines` has, and what keeps a query that finds forty hits
from allocating a forty-element array.

`QueryFilter` stores its layer mask and its ignored body **inverted**, so that `default` means "every
layer, no exclusion" rather than "hit nothing, and skip body zero". Every query takes the filter as
an optional parameter, and the alternative failure is silent.

## Characters

`CharacterController` is Jolt's `CharacterVirtual`: a shape swept through the world every step and
slid along whatever it hits. It has no body in the simulation, so nothing pushes it and it does not
fall over — which is what a character wants and what a dynamic capsule famously does not give.

**Gravity is the caller's.** `Velocity` is whatever was last set and `Update` moves by it, because a
character's vertical motion is a gameplay decision — coyote time, variable jump height, a ladder — and
a controller that applied gravity itself would have to be fought for every one of them.

```csharp
character.Velocity = character.IsGrounded
    ? new(input.X * speed, MathF.Max(character.Velocity.Y, 0f), input.Z * speed)
    : new(input.X * speed, character.Velocity.Y + (gravity * dt), input.Z * speed);

world.Step(dt);
character.Update(dt);       // after the step: it sweeps against the world as it is now
```

⚠ **`Position` is the shape's *centre***, not its base — `CharacterControllerTests` pins a capsule
settling at `halfHeight + radius` above the floor. The doc comment used to say "bottom-centre" and was
simply wrong; `CharacterMovement.ShapeOffset` is what puts an entity's origin back at the character's
feet.

## Character movement

The loop above is what every game writes around the controller, so `Vixen.Physics` writes it once.
An entity with a `CharacterMovement` and a `MoveIntent` walks:

```csharp
var walker = world.Create(LocalTransform.At(spawn));

world.Add(walker, CharacterMovement.Default with {
    Shape = scene.Shapes.Capsule(0.6f, 0.3f),
    CrouchShape = scene.Shapes.Capsule(0.3f, 0.3f)
});

world.Add(walker, default(MoveIntent));     // whatever writes this is not physics' business
```

`PhysicsScene` gives it a `CharacterController` and a `CharacterState`, and
`CharacterMovementSystem` — the fourth fixed-step pass, after the world step — moves it.
[29](../../docs/plan/29-players-and-possession.md) is the design; the short version is that
`MoveIntent` is the seam, so a person, an AI planner and a replay are indistinguishable from here.

**The rules are a pure function and that is the requirement rather than the style.**
`CharacterMotion.Step` reads only its arguments and writes only the state it is handed — no clock, no
random source, no field of its own — because doc 16's prediction replays the same tick whenever a
snapshot disagrees, and a step that is not reproducible makes the *correction* wrong. It also means
the whole rule set is tested with no Jolt at all.

| | |
|---|---|
| Modes | `Walking`, `Falling`, `Flying`, `Swimming`. ⚠ This row used to say swimming was absent "because water volumes are, and a mode that could never be entered is a promise in an enum" — they exist ([35 § D11](../../docs/plan/35-water.md)) and the promise was kept |
| Jump | Coyote time, jump buffering, and variable height as a **clamp** — a multiplier applied once a step makes the apex depend on the step rate |
| Crouch | `TrySetShape`, so standing up under a low ceiling is a refusal that needs no special case anywhere |
| Speed | Linear acceleration towards an exact top speed. An exponential approach never quite reaches the number in the inspector |
| Platforms | Velocity is stored relative to the ground, so standing still on a lift is a zero and is carried |
| Slope and step | Per character, and **live** — see below |

⚠ **The achieved velocity is measured from the displacement, not read back.** `CharacterVirtual`
leaves `LinearVelocity` as it was given — `CharacterSceneTests` pins that by walking into a wall — so
a character that trusted it would jump into a ceiling and hang there holding its full upward speed. A
sweep can only ever take velocity away, so each component keeps its asked-for sign and the smaller
magnitude; taking the displacement wholesale instead would read a 0.4 m stair step-up as 24 m/s and
launch the character off it.

⚠ **Slope and step are per character, and this section used to say they could not be — wrongly, and
about the binding rather than about the cost.** It read: "they are fixed at creation — exposing them
on the component means recreating the controller on an edit, which is real work for a knob nobody has
asked for yet." Neither half was true of Jolt. `CharacterBase` carries a `MaxSlopeAngle` **setter**,
which recomputes the cosine the ground test actually uses; and the step height is a field of the
`ExtendedUpdateSettings` handed to *every* `ExtendedUpdate`, so it was per-step all along and was
fixed only by this project holding the struct in a `readonly` field. Nothing is recreated, no contact
state is lost, and `CharacterSceneTests` asserts the controller is the same object across an edit.

`CharacterMovement.MaxSlopeAngle` and `.StepHeight` are the authored half; `CharacterController` has
both live. The bridge compares against **what it last pushed** rather than against what the
controller holds — the contract `CharacterBody.BuiltShape` already gives a hand-driven `TrySetShape`,
so a game that tunes a controller through `TryGetCharacter` keeps its tuning and only a *component*
edit wins.

⚠ **Zero takes the default for both.** A component is a struct in a zeroed column, so a scene naming
`WalkSpeed` and nothing else would otherwise hand out a character that slides off flat ground and
catches on every 5 cm lip — the guard `CharacterMotion.WadeScale` already made for `WadeSpeedScale`,
promoted to `CharacterMovement.ResolveMaxSlopeAngle` / `.ResolveStepHeight` now that two callers need
it. The price is that stair walking cannot be turned *off* through the component; it is off at
`CharacterControllerSettings.StepHeight`, or on the controller itself.

## Determinism

`PhysicsDeterminismTests` is the Phase 8 exit gate and it compares **bits**, not tolerances. Two
settings hold it up:

- `PhysicsWorldSettings.Deterministic` — on by default, fixes the island splitter's order.
- `PhysicsWorldSettings.ThreadCount` — Jolt is deterministic for a given thread count and **not**
  across different ones. A replay, a rollback or a lockstep peer has to agree on the number.

This is not a nicety: Phase 9's lag compensation rewinds collider history and re-runs shape casts
against it, and a tolerance here would mean divergence is found by a desync months later instead of
by that file.

## Debug drawing

`PhysicsDebugDrawSystem` is off by default and draws into `Vixen.Engine.Diagnostics.DebugDraw`.

It draws what [13](../../docs/plan/13-diagnostics.md) § Overlays specifies — collider wireframes,
contact points, constraints and sleeping state — plus optional bounds and per-body axes.

Colour carries the state — grey asleep, dark green static, blue kinematic, bright green awake, yellow
sensor — because "the crate has gone to sleep" and "the crate is static" look identical in every other
respect.

A constraint is drawn as a cross at each of its two anchors, the axis it turns about or slides along,
and a segment joining the anchors. That segment has no length while the constraint is satisfied, so
seeing one at all means the solver is losing — which is the first thing worth knowing about a joint
that looks wrong. `PhysicsWorld.GetConstraintAnchors` is public for the same reason an editor gizmo
needs it.

Wireframes come from Vixen's own shape descriptions, not from Jolt's debug renderer: every shape in
the registry is already described exactly, so a box is its twelve edges and a capsule is its rings. A
mesh or a convex hull is drawn as its bounding box — wireframing a hundred-thousand-triangle level
would produce more lines than the debug renderer can hold and would tell nobody anything.

## Platforms

`JoltPhysics.Native` ships the compiled library for `win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `osx` (universal) and `android-arm`, `android-arm64`, `android-x64`.

**There is no iOS slice, and that is a gap in the Phase 8 exit criteria.** iOS is NativeAOT-only, where
a dynamic library would not be loadable in any case; closing it means a static `libjoltc.a` pinned and
restored the way MoltenVK already is in `Vixen.Platform.iOS`, and linked into the app. Nothing in this
project assumes otherwise — the managed side is `IsAotCompatible` and has no dynamic loading of its
own — but until that library exists, `Samples/05` cannot run on iOS.

## Known gaps

- **iOS**, above.
- **Per-pair collision suppression.** A joint's two bodies still collide with one another. Jolt does
  this with a `CollisionGroup` and a shared `GroupFilterTable` rather than a flag on the constraint,
  which is a body-level facility this project does not expose yet. Layers cover the common case.
- **Vehicles, ragdolls and soft bodies.** Jolt has all three and the binding exposes them; none is in
  Phase 8's scope. `Vixen.Animation`'s ragdoll integration lands with the animation work.
- **Double precision.** `Foundation.Init(doublePrecision: false)`. Large-world support is a separate
  decision that touches the mathematics types, not just this project.

## Two bugs in the binding, worked around here

Both are pinned by tests, so if a later `JoltPhysicsSharp` fixes them the workaround can go and the
test will say so.

1. **`BodyCreationSettings.MotionQuality` does not reach the native settings object.** A body asked
   for continuous detection at creation gets discrete, and reading the value back gives garbage.
   `PhysicsWorld.CreateBody` sets it through the body interface afterwards instead.
2. **`BodyInterface.GetTransformedShape` returns an identity transform**, so its "world space" bounds
   are the shape's local ones sitting at the origin. `PhysicsWorld.GetBounds` goes through a body lock.

A third is not a bug but is worth the same warning: `ExtendedUpdateSettings`'s parameterless
constructor zeroes the struct rather than filling in Jolt's defaults, and a zero
`WalkStairsStepForwardTest` makes the stair sweep test forward by nothing — which does not fail
loudly, it just walks the character into the step and leaves it there. `CharacterController` sets
every field.

## Testing

`Vixen.Physics.Tests` disables xunit's parallelism, on purpose. Jolt's initialisation is
process-global — one allocator, one factory, one registry of shape types — so two test collections
running at once take that global up and down underneath one another, and what comes out is a native
abort with no managed stack rather than a failed assertion.
