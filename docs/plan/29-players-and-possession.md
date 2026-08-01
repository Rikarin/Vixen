<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 29 — Players and possession

⚠️ **Extends [04](04-ecs-and-scripting.md), [26](26-virtual-cameras.md) and [16](16-networking.md).**
Doc 04 gives a game a world, a transform and a `Behavior`; doc 26 gives it a camera that follows
something; doc 16 gives it prediction over an input type it never looks inside. What none of them
answers is **who the player is** — and every game built on the engine so far has answered it by hand,
differently, in a file called something like `AvatarController`.

**The claim this document has to earn.** A game that wants a player who walks, looks, jumps, dies,
respawns and is watched by a camera writes *no controller code at all* — it places a pawn, calls
`Players.Create`, and possesses. A game that wants a different body — a vehicle, a spectator, a
sniper drone — swaps the possessed entity and changes nothing else. And a game that wants that player
predicted over a network changes nothing about the controller, because the thing the network carries
is the same component the controller already writes.

---

## The argument

Every engine that has done this well has converged on the same decomposition, and Unreal states it
most plainly. `APlayerController` is not the player's body — it is the player's *persistence*: the
aim, the input stack, the network connection and the score. `APawn` is a body it can be attached to
and detached from. `Possess`/`UnPossess` is the edge between them, rewritten at runtime.

Four consequences follow, and each one removes a category of bug that a "player script on the
character" design produces forever:

| Because | You get |
|---|---|
| The controller outlives the pawn | Respawn is one edge write. Nothing has to copy the score, the aim, or the connection across the gap |
| Aim lives on the controller | A camera can keep looking the right way through a death, a vehicle entry and a pawn swap, because the thing that knows where the player is looking did not die |
| Controllers are interchangeable | An AI planner and a human write the same thing, so nothing downstream of the controller learns which is driving. Unreal spells this `AController`; here it is one component |
| Ownership starts at the controller | "May this client send this call" is answered once, at the top, rather than per feature |

The parts of Unreal's version that are **not** worth taking are as instructive. `APlayerCameraManager`
is a whole class for blending between view targets — [26](26-virtual-cameras.md) already does that
better, with priorities instead of imperative `SetViewTarget` calls, so the camera half of this
document is a component write and no new machinery. `AGameModeBase` is a god object that owns
spawning, class substitution, login and match state; here that decomposes into a spawner
([16](16-networking.md)'s, already built) and a possession call. And `ACharacter`'s movement is welded
to its pawn class, which is what makes Unreal's character movement famously hard to replace; here it
is a separate component read by a separate system, in a separate assembly.

### The constraint that decides the shape

`Vixen.Physics` references `Vixen.Engine`. `Vixen.Net.Engine` is the only assembly permitted to see
both `Vixen.Net` and `Vixen.Engine` ([02](02-repository-layout.md), enforced by `CheckArchitecture`).
So a single `PlayerController` type that reads input, drives a Jolt character and sends a move RPC
**cannot be written** — the gate refuses it before a reviewer has to.

That is not an obstacle to work around. It is the layering telling us where the seams are, and the
seams it names are exactly Unreal's: intent is separate from movement, and movement is separate from
the wire.

---

## The shape

```
Vixen.Engine.Players       PlayerController ──writes──▶ MoveIntent
   input → intent                                          │
   possession, aim                                         │ read by
   no physics, no net                                      ▼
Vixen.Physics.Characters                            CharacterMovement ──▶ CharacterController.Update
   intent → velocity                                       ▲
                                                           │ server applies
Vixen.Net.Engine.Players            PlayerMoveInput : IPredictedInput<T>
   the wire, prediction, spawning
```

**`MoveIntent` is the pivot, and it is the whole design.** A small `[Component] [DataContract]`
struct: two move axes, an absolute yaw and pitch, and a button bitfield. It is the only thing the
three layers agree on, and it buys four things at once:

- **Layer legality.** Neither side needs the other's assembly, so the gate is satisfied by
  construction rather than by an exception.
- **Prediction, nearly for free.** `PredictedStep<T>` must be a pure function of the world and the
  input ([16](16-networking.md)). "Write the intent, run the movement" is exactly that, so the *same*
  `CharacterMovementSystem` runs on the server, on the owning client, and inside a rollback replay.
  `MispredictionCount` becomes a test rather than a hope.
- **Doc 04's controller polymorphism, without a hierarchy.** An AI planner writes `MoveIntent` and
  nothing downstream can tell. That is [28](28-gameplay-framework.md) § AI's `IAgentAction` seam,
  available before that document starts.
- **One format, not two.** The quantized intent *is* the predicted-input payload. There is no second
  struct that has to be kept in agreement with the first — the failure doc 16 spends a page on.

**Absolute yaw, not a yaw delta.** A delta is what an input device produces and it is the wrong thing
to put in a component: two machines integrating deltas diverge, and a server handed a delta has
nothing to refuse. What crosses every boundary here is where the player *is* looking, which the server
is free to reject outright. `Samples/08-Multiplayer`'s hand-rolled `Steer(x, z, facing)` had already
worked this out; this is the same decision made once, in a type.

### The components

| Type | Attributes | Why |
|---|---|---|
| `PlayerController` | `[Component] [DataContract]` | The seat: local slot, owning `PlayerId`, camera channel, whether it accepts input |
| `ControlRotation` | `[Component] [DataContract]` | Yaw, pitch and the pitch clamps. **On the controller**, which is the point |
| `MoveIntent` | `[Component] [DataContract]` | The pivot. Held by the controller *and* by the pawn |
| `Possessing` | `[Component]`, **no `[DataContract]`** | Controller → pawn |
| `PossessedBy` | `[Component]`, **no `[DataContract]`** | Pawn → controller |
| `ViewTarget` | `[Component]`, **no `[DataContract]`** | Controller → the shot that watches it |

⚠ **Three of them deliberately lack `[DataContract]`, and the reason is already written down twice in
this repository.** An entity handle names a slot in the world that issued it; `CameraTargets` and
`PhysicsBody` are both on that line. So a scene places a *pawn* and a *shot*, and something running in
the world wires up who is driving what. There is no denylist and no opt-out attribute — the conjunction
`[Component] [DataContract]` is what admits a type to a scene file, and omitting one of the pair is the
whole mechanism.

⚠ **`PlayerController.Owner` is a `uint`, not a `PlayerId`.** `PlayerId` lives in `Vixen.Net.Sessions`
and `Vixen.Engine` may not reference it. `NetworkSpawn.Owner` is already a `uint` documented as "who
owns it, as `PlayerId`", for the same reason and with the same trade: a lost type name against a
reference that would invert the layering. Zero means the local machine, which is also what it means
there.

### Possession is an operation, not a field

`Players` is the only supported way to change the edge, in the shape `Hierarchy` already established
for the transform tree and for the same reason: two components describe one relationship, both can be
written directly, and everything that does will eventually produce a pawn that thinks it is possessed
by a controller that has forgotten it.

```csharp
var controller = Players.Create(world);
Players.Possess(world, controller, pawn);
Players.BindCamera(world, controller, shot);

// later, the pawn dies
world.Destroy(pawn);            // the controller keeps its aim, its slot and its score
Players.Possess(world, controller, Players.Spawn(...));
```

`Possess` unbinds both sides first, so possessing an already-possessed pawn steals it rather than
producing two controllers that each believe they have it — which is what a game means by
"possess" and is the only behaviour that has no silent failure mode.

### Two systems, and where they run

| System | Phase | Does |
|---|---|---|
| `PlayerInputSystem` | `Input`, after `InputUpdateSystem` | Samples each controller's input source into its `ControlRotation` and `MoveIntent` |
| `PossessionSystem` | `Input`, after `PlayerInputSystem` | Repairs dead edges, forwards intent to the pawn, retargets the shot |

**Both in `Input`, and that is not a shortcut.** `SystemPhase`'s own documentation puts input before
"anything that reacts to it", and the reactor here is `CharacterMovementSystem` in `FixedUpdate` —
the next phase. A phase boundary is a hard sync where command buffers play back, so a pawn that gains
its `MoveIntent` during `Input` has it before the first fixed step of the same frame. Putting
possession in `EarlyUpdate` — the tempting place, because it is structural — would forward *last*
frame's intent, and the symptom would be one frame of input latency that no profiler attributes to a
phase choice.

**The camera needs no new machinery at all.** `PossessionSystem` writes `CameraTargets.Both(pawn)`
onto the controller's `ViewTarget` shot, unconditionally, every frame. Doc 26's director then blends
because the answer changed. Unreal needs `SetViewTarget`, a blend curve and a camera manager to do
that; here it is one component write, it self-heals if anything else clobbers it, and there are at
most four of them in a split-screen game.

### The input seam

`IPlayerInputSource` — one method, `Sample(ref ControlRotation, ref MoveIntent, float deltaTime)` —
held in a side table on `PlayerInputSystem` rather than in a component, which is the shape
`InputUpdateSystem(InputService)` already uses and which keeps the managed `InputActions` object out
of a chunk.

The engine ships `ActionPlayerInput` over `Vixen.Input`, resolving named actions from an
`InputActionMap`. ⚠ **This is the one place the engine trades away the property `Vixen.Input`'s README
is proudest of** — that a renamed action is a compiler error rather than a string that resolves to
null. An engine cannot reference a game's generated accessor, so the default binds by name; it
resolves once, at construction, and **throws naming the map and the missing action** rather than
reading zero forever. A game that wants the compile-time property back implements
`IPlayerInputSource` over its own generated accessor, which is about eight lines, and the guide page
shows it.

Look input is two cases wearing one action. A mouse produces a delta that is already frame-rate
independent; a stick produces a rate that must be multiplied by the frame time. `ActionPlayerInput`
reads `InputAction.ActiveBinding` to tell which device won this frame — the input system already
decides that, and asking it is cheaper and more correct than a second action or a heuristic on the
magnitude.

---

## What P1 found

Three things the existing code asserted that its own behaviour did not, each caught by a test written
against it. They are recorded here rather than quietly fixed, because each is the kind of claim a
later reader would otherwise trust again.

| Claim | Reality |
|---|---|
| `CharacterController.Position` is "the bottom-centre of its shape" | It is the **centre**. `CharacterControllerTests` already pinned a capsule settling at `halfHeight + radius` above the floor, so the comment and the test had disagreed since the day both were written. `CharacterMovement.ShapeOffset` is what puts an entity's origin back at the feet |
| `Vixen.Engine`'s README: "`PhysicsBody` carries `[Component]` and not `[DataContract]`" | It carries **neither**, and its own doc comment says so at length. The example that makes the point is `CameraTargets` |
| `CharacterVirtual` reports the velocity a sweep achieved | It does not — it leaves `LinearVelocity` as it was given. A character walking into a wall holds the full walk speed, which is harmless; one jumping into a ceiling holds its full upward speed and hangs there until gravity eats it, which is not |

The third needed a rule rather than a fix. The achieved velocity is measured from the displacement,
and then **each component keeps its asked-for sign and the smaller magnitude** — because a sweep can
only ever take velocity away. Taking the displacement wholesale gets the wall and the ceiling right
and the stairs catastrophically wrong: a 0.4 m step-up in one 60 Hz step is a measured 24 m/s, and a
character that believed it would be launched off every staircase in the game.

A fourth was a design error of this document's own making: growing a capsule about a fixed centre
drives its bottom into the floor, so `TrySetShape` refuses it and a crouched character can never stand
up — on flat ground, with nothing above it, looking exactly like the ceiling check misfiring.
`CrouchShapeOffset` is the second offset that makes the feet rather than the centre the fixed point.

---

## What P2 found

**Split screen simulates and does not render, and the blocker is precise.** Two players get two
directors, two sets of shots and two cameras, and every one of them updates independently — the
channel was already load-bearing in doc 26 and now reaches a player. But `CameraExtractionSystem`
fills exactly one `RenderView`, from the lowest `Camera.Order` in the world, and a `RenderView` has no
viewport rectangle — its own remarks say "one number rather than a field of view and a viewport".
Showing two players at once therefore needs a view per player, a rect on each, and a compositor with a
node per view. That is [06](06-rendering-pipeline.md)'s work and it is not smuggled in here.

What *is* done is the honest half: `PlayerCameras` sets each camera's `Order` from its channel, so
seat zero is the one on screen and switching which player is watched is a component write rather than
a teardown.

**The third-person pitch is negated, and it is not a sign slip.** `ControlRotation.Pitch` is positive
looking *up*; `OrbitBody.Pitch` is positive riding *above* the target and looking down. A player
raising their aim drops the camera and looks up past the character's shoulder, which is what every
third-person game does — copying the sign across would exactly invert it, and it is the kind of error
that is obvious in play and invisible in review. `AimingUpDropsTheThirdPersonCameraRatherThanRaisingIt`
is the test that holds it.

---

## What P3 found

Two ways to write a prediction that reports perfect agreement while the two machines drift apart.
Both were found by a test that failed for the right reason, and both are the kind of defect that
cannot be seen in a review because the symptom is a number reading zero.

**A hand-driven tick must advance the world's change version.** `SystemRunner` does it at every phase
boundary and nothing else does, so a replayed tick — which has no frame loop — stamps every write with
the same version as the tick before it. From the second tick onwards `WithChanged` matches nothing:
`NetworkTransformCaptureSystem` silently stops publishing, `PredictionHistory` records the same bytes
for ever, and `MispredictionCount` sits at zero. `PredictedPlayerMovement` calls
`World.AdvanceVersion()` first thing, and a game writing its own step has to.

**The step has to publish `NetworkTransform` inside the tick.** Physics writes `LocalTransform`;
a snapshot and the history both speak `NetworkTransform`; nothing carries one to the other within a
tick. Publishing in the frame loop instead records the *previous* tick's pose. The test that pins this
is the one beside the headline number — `ThePredictedStatePublishesWhatTheCharacterDid` — and without
it `MispredictionCountIsZeroOverALosslessRun` passes vacuously, which is exactly what it did the first
time it was run.

**And a rollback has to reach into Jolt.** A `CharacterController`'s position is native state that no
snapshot restores. `PhysicsScene` now adopts a written `LocalTransform` — which incidentally fixes
teleporting a character at all, something that quietly did nothing before — so a replay starts from the
server's state rather than from the guess it was correcting. Without it the correction never
converges, which is the failure `ClientPrediction`'s own remarks describe from the other end.

### And what the tidy-up after it found

**`PredictionSmoother.Advance` built a fresh `List<uint>` on every call.** It runs every frame for as
long as any object is being smoothed, which on a connection that is working at all is most of a
session — so it was a steady-state allocation in exactly the place
[12](12-build-ci-and-testing.md)'s zero-collection criterion is measured. It had been there since the
smoother was written and nothing had run it, because nothing was wired to it. The allocation tests
across the player path are what found it, and they are the reason to have written them.

---

## Where this stops

- **No per-character slope or step limits.** `CharacterControllerSettings` has both with sensible
  values, and Jolt fixes them at creation — so exposing them on the component means detecting the edit
  and recreating the controller. Real work for a knob nobody has asked for; deferred rather than
  half-done.
- **No swimming.** It needs water volumes, which do not exist. A mode that could never be entered
  would be a promise in an enum, so `CharacterMoveMode` has three members and not four.
- **No `APlayerState` equivalent.** Score, name and ping belong to `Vixen.Net.Sessions`'
  `NetworkPlayer` on one side and to [28](28-gameplay-framework.md)'s durable state on the other.
  A third home for them in `Vixen.Engine` would be the beginning of a god object.
- **No HUD, no spectator, no input-mode stack.** Unreal's `AHUD` is a `Vixen.Ui` question.
  Spectating is possessing a pawn with no movement. An input-mode stack — game versus menu versus
  console — is `InputActionMap.Enable`/`Disable`, which already exists.
- **Local multiplayer is slots, not seats.** `PlayerController.Slot` plus `GamepadSlot` plus
  `VirtualCamera.Channel` is the whole of it. There is no `ULocalPlayer` object because there is
  nothing left for one to hold.

---

## Milestones

| # | Milestone | Deliverable | EM |
|---|---|---|---|
| **P0** ✅ | **The seat** | Components, `Player`, `PlayerInputSystem`, `PossessionSystem`, `ActionPlayerInput` — `Vixen.Engine` | 0.5 |
| **P1** ✅ | **The body** | `CharacterMovement`, movement modes, jump with coyote time, crouch through `TrySetShape` — `Vixen.Physics` | 0.75 |
| **P2** ✅ | **The view** | First- and third-person rigs assembled from doc 26's stages; split-screen across channels | 0.25 |
| **P3** ✅ | **The wire** | `PlayerMoveInput : IPredictedInput<T>`, `PlayerSpawner`, the predicted step — `Vixen.Net.Engine` | 0.75 |
| **P4** ✅ | **The proof** | `Samples/13-ThirdPersonShooter` — a **project** rather than a sample: `.vxproj`, `Assets/`, `VixenApp.Run<T>`, and a headless run that asserts the player is `Walking` on collision the level authored. Nine engine failures found and fixed, seven of which produced a working program with a wrong answer | 0.75 |
| | **Total** | | **3.0** |

### What P4 found

⚠ **An end-to-end project is a test, and nothing else in this repository was running it.** Every
subsystem here had tests and every one of them passed; what nothing exercised was the join — a
`.vxproj` on the real SDK, its own components in its own level, its own frame, and a game that loads
all three by address. Nine failures came out of that gap, and the shape they share is the point:

**Seven of the nine produced a working program with a wrong answer.** A frame that drew, unlit,
because the compositor silently fell back to the host's built-in one. A level that loaded with no
models, because a model's distance field collided with its own mesh's address and the model was
dropped from the catalog. A mesh chunk stamped with an editor type, so every game that loaded one got
"nothing registered in this process claims it" about content the build had just declared good. None of
these fails a build; all of them fail a player.

**Three were about a module initializer that never ran.** The registrations that make a name mean a
type are `[ModuleInitializer]`s, and the CLR runs one at the first access to a type in the module — so
"the assembly is referenced" and "the assembly has registered anything" are different facts. The build
tool never touched the game's assembly, or the engine's; a game never touched `Vixen.Rendering.PostFx`
before the host built its frame. Each looked like a missing type and was a missing *touch*.

**One was a sidecar recording a decision nobody made.** A `.meta` naming `!RawImporter` pinned the
file to it for ever, so a format that got a compiler later kept shipping as bytes — in exactly the
projects that already had the file, and not in new ones.

The lesson for the next milestone that ships a subsystem: the test that finds these is a project, and
it has to be built and *started*, because starting is where six of the nine appeared.

P3 is gated on a determinism test from P1, not on a date: a movement step that is not a pure function
of world and input mispredicts on every snapshot over a perfect connection, and it looks like jitter
rather than like a bug.

---

## Testing

| Area | Test |
|---|---|
| Possession | Randomised possess/unpossess/destroy sequences never leave a half-linked pair, in the shape `HierarchyTests` already uses |
| Lifetime | **The controller outlives the pawn.** Destroy the pawn: the edge clears, the aim and the slot survive, re-possession restores everything |
| Stealing | Possessing an already-possessed pawn leaves exactly one controller holding it |
| Aim | Pitch clamps at both ends; yaw wraps rather than growing without bound; the clamp agrees with `PovAim`'s |
| Forwarding | The pawn's `MoveIntent` equals the controller's after one frame, including the frame it is first added on |
| Camera | Possession retargets the bound shot; a pawn swap retargets it again; a shot with no `CameraTargets` yet gains one |
| Split-screen | Two controllers, two `GamepadSlot`s, two camera channels, one world — neither sees the other's input |
| Determinism (P1) | The same intent sequence produces a byte-identical transform |
| Prediction (P3) | `MispredictionCount == 0` over a lossless local transport with the default controller. **One number for the whole networked path** |

---

## Risks and open questions

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| P-R1 | **A shipped default controller becomes the API**, and then it cannot be changed — [28](28-gameplay-framework.md) G-Q5's objection, reaching one layer down | High | The engine ships the *mechanism* — possession, aim, intent — and one movement component with human defaults, the way `CharacterControllerSettings` already ships a described human. ⚠ **This row originally said the camera rigs ship in the sample and not the library, and P2 found that wrong**: a first-person camera and a third-person orbit are both steered by `ControlRotation`, and the write that carries it into `PovAim` and `OrbitBody` is `PossessionSystem`'s — so neither rig can be assembled from outside `Vixen.Engine` at all. What ships is two factories whose every tunable is an argument; what belongs in `Samples/05-PlatformerGame` is the *tuning*, which is what a preset would have frozen |
| P-R2 | **`MoveIntent`'s layout is both a scene format and a wire format**, so widening it is two breaks at once | Medium | Its size is pinned by a test and it is in the release table's component-size column. Games needing more put it in their own component; the button field reserves its high byte for exactly that |
| P-R3 | **String-keyed default input** loses the property that a renamed action is a compiler error | Medium | Resolved once at construction and throws naming both; the generated-accessor path is documented as the one a shipping game takes |
| P-R4 | **Intent forwarding is a copy**, so a system that writes the pawn's intent directly is silently overwritten each frame | Low | The pawn's `MoveIntent` is documented as derived, the way `WorldTransform` is. A game driving a pawn without a controller simply has no `PossessedBy` and is not visited |

| # | Open question | Recommendation |
|---|---|---|
| P-Q1 | Is a controller an entity or a `Behavior`? | **An entity.** Prediction replays a *world*; a `Behavior` is not world state and would be invisible to a rollback. `Behavior` stays available for game-specific logic on top |
| P-Q2 | Do vehicles share `MoveIntent`? | **No.** Keep it character-shaped; [28](28-gameplay-framework.md)'s `IVehicle` gets its own intent component written by the same producer. Doc 16's "one input type per session" is satisfied by a discriminator in the wire struct, not by overloading the component |
| P-Q3 | Should the pawn adopt the controller's yaw automatically? | **Only if it asks.** Unreal's `bUseControllerRotationYaw` is right: a strafing shooter wants it and a character that turns toward its movement does not. P1's `CharacterMovement` carries the flag |
| P-Q4 | One `MoveIntent` on the controller and a copy on the pawn, or a lookup through the edge? | **The copy.** A lookup makes every movement chunk sweep a random access through two component reads, and the copy is four entities' worth of work at the top of the frame |
