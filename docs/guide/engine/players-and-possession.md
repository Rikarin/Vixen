---
title: Players and possession
slug: engine/players-and-possession
kind: guide
area: Engine
summary: A controller that outlives the body it drives, and the one component that carries a player's intent.
api: [T:Vixen.Engine.Players.Player, T:Vixen.Engine.Players.PlayerController, T:Vixen.Engine.Players.MoveIntent, T:Vixen.Engine.Players.ControlRotation, T:Vixen.Engine.Players.Possessing, T:Vixen.Engine.Players.PossessedBy, T:Vixen.Engine.Players.ViewTarget, T:Vixen.Engine.Players.MoveButtons, T:Vixen.Engine.Players.IPlayerInputSource, T:Vixen.Engine.Players.ActionPlayerInput, T:Vixen.Engine.Players.PlayerInputSystem, T:Vixen.Engine.Players.PossessionSystem, T:Vixen.Engine.Players.PlayerCameras, T:Vixen.Engine.Players.PlayerCamera]
tags: [players, input, possession, cameras]
since: 0.1
status: stable
related: [ecs/components, engine/world-serialisation, engine/character-movement]
---

## What it is

A **player** is an entity carrying a `PlayerController`: a seat at the machine, an aim, and nothing
that describes a thing in the world. A **pawn** is any entity it is currently driving. `Player` is
the operation set that links the two, and `MoveIntent` is what the link carries.

The three pieces are separate because they have different lifetimes. A pawn dies. A controller does
not — it keeps its slot, its connection, its camera channel and, most importantly, where the player
was looking.

## What it is for

Anything a person drives: a character, a vehicle, a spectator camera, a cursor in a strategy game.
The reason to use it rather than a script on the character is respawning. With a controller, a
respawn is `Player.Possess(world, controller, newPawn)` — one call, and the aim, the score and the
camera follow by themselves. Without one, every game rediscovers that those five things have to be
copied across the gap, and each rediscovery misses a different one.

It is also what an AI shares with a human. Nothing downstream of `MoveIntent` knows which is driving,
so a possessed NPC and a possessed player run the same movement code.

You do not want it for something nobody drives — a door, a turret on a timer, a projectile. Those
write their own components, and a pawn nothing possesses is never visited.

## Using it

Three components and one call:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;

public static class Spawning {
    public static Entity NewPlayer(World world) {
        var pawn = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var controller = Player.Create(world);

        Player.Possess(world, controller, pawn);
        return controller;
    }
}
```

`Player.Create` gives the entity a `PlayerController`, a `ControlRotation` and a `MoveIntent`.
`Possess` writes the `Possessing`/`PossessedBy` pair, releasing whatever either side was already
attached to — so possessing a pawn somebody else holds *steals* it rather than sharing it.

Two systems make it move. `PlayerInputSystem` samples each player's input source into their aim and
their intent; `PossessionSystem` copies that intent onto the pawn, points the camera at it, and lets
go of pawns that no longer exist. Both run in `SystemPhase.Input`, before the fixed step that reads
the result.

```csharp compile
using Vixen.Engine.Frames;
using Vixen.Engine.Players;

public static class PlayerLoop {
    public static void Register(EngineLoop loop, PlayerInputSystem input) {
        loop.Add(input);
        loop.Add(new PossessionSystem());
    }
}
```

**Where intent comes from is an interface.** `IPlayerInputSource` has one method, so a device, a
planner, a replay and a test are the same shape:

```csharp compile
using Vixen.Engine.Players;

public sealed class WalkForward : IPlayerInputSource {
    public void Sample(ref ControlRotation rotation, ref MoveIntent intent, float deltaTime) {
        rotation.Turn(0.5f * deltaTime, 0f);
        intent.Move = new(0f, 1f);
        intent.Yaw = rotation.Yaw;
        intent.Pitch = rotation.Pitch;
    }
}
```

`ActionPlayerInput` is the one the engine ships, over a `Vixen.Input` action map. It wants a `Move`
and a `Look` value action and binds any of `Jump`, `Crouch`, `Sprint`, `Fire`, `AltFire`, `Aim`,
`Interact` and `Reload` that the map happens to have. A missing `Move` or `Look` throws at
construction naming the map and the action, rather than reading zero on the frame the player presses
it.

> **A shipping game should implement `IPlayerInputSource` over its own generated accessor.**
> `ActionPlayerInput` binds by name, which is the one thing `Vixen.Input` otherwise makes impossible:
> a renamed action becomes a run-time surprise instead of a compiler error. The engine cannot
> reference a game's generated class, so the default is the compromise; the interface is the way out
> of it, and it is the eight lines above.

**The camera is one call.** `PlayerCameras` assembles the two rigs a player steers:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;

public static class Views {
    public static PlayerCamera Eyes(World world, Entity controller) =>
        PlayerCameras.FirstPerson(world, controller, eyeHeight: 1.7f);

    public static PlayerCamera OverTheShoulder(World world, Entity controller) =>
        PlayerCameras.ThirdPerson(world, controller, distance: 4f, shoulderHeight: 1.4f);
}
```

Each creates a real `Camera` with a `CameraDirector` on the player's own channel, a `VirtualCamera`
shot on the same channel, and binds the shot. From then on `PossessionSystem` points the shot at
whatever the player is driving and feeds it their aim, every frame — so a death, a respawn and a
vehicle entry all need no camera code at all.

These two are in the engine rather than in a sample because **they are the rigs that cannot be built
from outside it**: both are steered by `ControlRotation`, and the write that carries it into `PovAim`
and `OrbitBody` is `PossessionSystem`'s. Everything a game tunes is an argument here and a component
afterwards, so a third rig is the same three component adds with different values.

You can also do it by hand, which is what those two calls are:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Players;

public static class OwnRig {
    public static void Watch(World world, Entity controller, Entity pawn) {
        var shot = world.Create(VirtualCamera.Default, FollowBody.Behind(distance: 6f, height: 2f));

        Player.BindCamera(world, controller, shot);
        Player.Possess(world, controller, pawn);
    }
}
```

⚠ `FollowBody.Behind` swings round as the **target** turns, which is right for a camera watching a car
and wrong for one a player is steering — that is why `ThirdPerson` uses `OrbitBody` instead.

## Examples

**Respawning.** The controller is what survives, so nothing has to be carried across:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;

public static class Deaths {
    public static Entity Respawn(World world, Entity controller, LocalTransform at) {
        var body = Hierarchy.CreateTransform(world, at);

        // The aim, the slot, the camera channel and the bound shot are all still there.
        Player.Possess(world, controller, body);
        return body;
    }
}
```

Destroying a pawn without unpossessing is fine. `PossessionSystem` clears the dangling edge on its
next pass and counts it in `ReleasedCount` — a number that climbing every frame means a game is
leaking possessions.

**Split screen.** Two seats, two gamepad slots, two camera channels, one world:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;

public static class SplitScreen {
    public static (PlayerCamera One, PlayerCamera Two) TwoSeats(World world) {
        var one = Player.Create(world);
        var two = Player.Create(world, slot: 1);

        // The camera channel comes from the slot, so neither director can see the other's shots and
        // neither player can lose their camera to the other's trigger volume.
        return (PlayerCameras.ThirdPerson(world, one), PlayerCameras.ThirdPerson(world, two));
    }
}
```

⚠ **That simulates, and only seat zero is drawn.** Each player gets their own director, shots and
camera, and all of it updates independently — but `CameraExtractionSystem` fills one `RenderView` from
the lowest `Camera.Order` in the world, and a `RenderView` has no viewport rectangle. `PlayerCameras`
sets each camera's order from its channel, so seat zero is on screen and swapping which player is
watched is an order write. Showing both at once needs a view per player and a rect on each, which is
the rendering pipeline's work rather than this subsystem's.

**Reading the intent.** Movement code sees a component, not a controller:

```csharp compile
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;

public static class Walking {
    static readonly QueryDescription Walkers = new QueryDescription().WithAll<MoveIntent, LocalTransform>();

    public static void Step(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(Walkers)) {
            var intents = chunk.ReadValues<MoveIntent>();
            var transforms = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                var speed = intents[index].IsHeld(MoveButtons.Sprint) ? 8f : 4f;
                transforms[index].Position += intents[index].WorldDirection() * speed * deltaTime;
            }
        }
    }
}
```

`WorldDirection` uses the yaw and never the pitch, so a character walking forward while looking at
the sky walks along the ground.

**Stopping input without losing the player.** A cutscene sets one field:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;

public static class Cutscenes {
    public static void Playing(World world, Entity controller, bool playing) =>
        world.Get<PlayerController>(controller).AcceptsInput = !playing;
}
```

The intent is cleared rather than frozen, so a held sprint does not survive the cutscene — and the
aim is untouched, because that is the thing the controller exists to preserve.

## See also

- [Components](ecs/components) — why `Possessing`, `PossessedBy` and `ViewTarget` carry
  `[Component]` without `[DataContract]`, and what that keeps out of a scene file.
- [Entity queries](ecs/queries) — how a movement system reads `MoveIntent` a column at a time.

The design record is `docs/plan/29-players-and-possession.md`, which carries the argument for the
decomposition and what P1 to P4 add: character movement, the assembled camera rigs, the networked and
predicted half, and the sample. The shots and the director a `ViewTarget` points into are
`docs/plan/26-virtual-cameras.md`.
