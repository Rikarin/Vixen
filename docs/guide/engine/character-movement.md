---
title: Character movement
slug: engine/character-movement
kind: guide
area: Physics
summary: Turning a player's intent into a character that walks, jumps, crouches and slides along walls.
api: [T:Vixen.Physics.Characters.CharacterMovement, T:Vixen.Physics.Characters.CharacterState, T:Vixen.Physics.Characters.CharacterBody, T:Vixen.Physics.Characters.CharacterMoveMode, T:Vixen.Physics.Characters.CharacterMotion, T:Vixen.Physics.Ecs.CharacterMovementSystem]
tags: [physics, characters, players, movement]
since: 0.1
status: stable
related: [engine/players-and-possession, engine/networked-players]
---

## What it is

`CharacterMovement` is how a character walks: its speeds, its gravity, its jump, and the two shapes it
takes standing and crouched. `CharacterState` is where it currently is in that motion.
`CharacterMotion` is the rule between them, and `CharacterMovementSystem` runs it against a real
`CharacterController` once per fixed step.

Together they are the half of a player that moves. The other half — who is driving — is
[players and possession](engine/players-and-possession), and the two meet at one component:
`MoveIntent`.

## What it is for

Anything that walks: a player, an NPC, a possessed vehicle's driver on foot. It exists because the
loop every game writes around a raw `CharacterController` — apply gravity when airborne, zero it when
grounded, accelerate towards the input — is the same loop every time, and the parts that are *not*
obvious (coyote time, jump buffering, what a sweep does to the velocity it was given) are the parts
everyone gets wrong once.

You do not want it for something that is not a character. A crate is a `RigidBody`; a lift is a
kinematic body; a bullet is a raycast. A character controller is specifically a shape that is swept
and slid rather than solved, which is why nothing pushes it and it does not fall over.

## Using it

Two components and a shape:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;

public static class Walkers {
    public static Entity Spawn(PhysicsScene scene, LocalTransform at) {
        var walker = scene.Entities.Create(at);

        scene.Entities.Add(
            walker,
            CharacterMovement.Default with {
                Shape = scene.Shapes.Capsule(0.6f, 0.3f),
                CrouchShape = scene.Shapes.Capsule(0.3f, 0.3f)
            }
        );

        // Whatever writes this is not physics' business — a player controller, a planner, a replay.
        scene.Entities.Add(walker, default(MoveIntent));
        return walker;
    }
}
```

`PhysicsScene` attaches the `CharacterController` and the `CharacterState` on the next step, so
nothing has to remember to. `AddPhysics` already registers `CharacterMovementSystem`, so a character
placed at any point starts moving.

⚠ **`CharacterMovement.Default` has no shape and cannot have one** — a `ShapeId` names a volume only a
live `PhysicsShapes` can issue. A character with no shape is skipped and retried every step rather
than throwing, so filling it in later works.

⚠ **The entity's origin is the character's feet; the controller's position is its shape's centre.**
`ShapeOffset` is the difference, and `CrouchShapeOffset` is the same for the crouched volume. They are
also what lets a character stand up: growing a capsule about a fixed centre drives its bottom into the
floor and the swap is refused, so the bridge moves the controller by the difference between the two
offsets and the feet stay where they are.

## Examples

**Tuning a jump by height rather than by speed.** The stored value is a speed, so the two can never
disagree:

```csharp compile
using Vixen.Physics.Characters;

public static class Jumps {
    public static CharacterMovement ClearingALedge(CharacterMovement movement, float height) =>
        movement with { JumpSpeed = CharacterMovement.JumpSpeedForHeight(height, movement.Gravity) };
}
```

**Reading what a character is doing**, for an animation graph or a footstep system:

```csharp compile
using Vixen.Ecs;
using Vixen.Physics.Characters;

public static class Locomotion {
    static readonly QueryDescription Characters =
        new QueryDescription().WithAll<CharacterMovement, CharacterState>();

    public static void Blend(World world) {
        foreach (var chunk in world.Chunks(Characters)) {
            var states = chunk.ReadValues<CharacterState>();

            for (var index = 0; index < chunk.Count; index++) {
                var state = states[index];
                var planar = new Vixen.Core.Mathematics.Vector2(state.Velocity.X, state.Velocity.Z);

                // Airborne, crouched and how fast — everything a locomotion blend needs, and none of
                // it asks who is driving.
                _ = (state.Mode == CharacterMoveMode.Falling, state.IsCrouching, planar.Length());
            }
        }
    }
}
```

**A flying character.** The mode is entered and left by whatever granted it, never by the ground — so
a drone that lands can still take off:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Physics.Characters;

public static class Flight {
    public static void Grant(World world, Entity character, bool flying) =>
        world.Get<CharacterState>(character).Mode =
            flying ? CharacterMoveMode.Flying : CharacterMoveMode.Falling;
}
```

While flying the look pitch steers, so a climb needs no second axis nobody has bound.

**Testing movement with no physics at all.** `CharacterMotion` is a pure function, so a rule can be
checked without a world, a shape or a native library:

```csharp compile
using Vixen.Engine.Players;
using Vixen.Physics.Characters;

public static class Falling {
    public static float SpeedAfterASecond(CharacterMovement movement) {
        var state = default(CharacterState);

        for (var step = 0; step < 60; step++) {
            CharacterMotion.Step(movement, ref state, default, CharacterGround.Airborne, 1f / 60f);
        }

        return state.Velocity.Y;
    }
}
```

That property is not a convenience. Doc 16's prediction replays the same tick whenever a snapshot
disagrees, and a step that reads a clock or an unseeded random source does not merely predict badly —
it makes the correction itself wrong, and the symptom is a player who twitches on a connection with no
loss at all.

## See also

- [Players and possession](engine/players-and-possession) — what writes the `MoveIntent` this reads,
  and why the two live in different assemblies.
- [Components](ecs/components) — why `CharacterBody` carries neither `[Component]` nor
  `[DataContract]`, and what that keeps out of a scene file.

The design record is `docs/plan/29-players-and-possession.md`, whose P1 this is.
