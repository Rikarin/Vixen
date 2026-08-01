---
title: Networked players
slug: engine/networked-players
kind: guide
area: Networking
summary: Giving a connection a body, predicting its movement, and proving the two ends agree.
api: [T:Vixen.Net.Engine.Players.PlayerMoveInput, T:Vixen.Net.Engine.Players.PlayerPawn, T:Vixen.Net.Engine.Players.PlayerSpawner, T:Vixen.Net.Engine.Players.LocalPlayerSystem, T:Vixen.Net.Physics.PredictedPlayerMovement]
tags: [networking, players, prediction, physics]
since: 0.1
status: stable
related: [engine/players-and-possession, engine/character-movement]
---

## What it is

Four pieces that turn a local player into a networked one. `PlayerSpawner` is the server's:
a connection joins, gets a controller and an owned pawn, and the two are possessed. `PlayerPawn` is
the tag that lets a client work out which of the things it owns is its body.
`LocalPlayerSystem` is the client noticing. `PlayerMoveInput` is `MoveIntent` on the wire, and
`PredictedPlayerMovement` is the one tick of simulation a rollback replays.

None of it is a second movement implementation. The step writes the input and runs the same
`CharacterMotion` rules [character movement](engine/character-movement) already had.

## What it is for

A game where the server decides where players are and the players still feel responsive. That is the
whole of it: the client predicts its own movement, the server simulates the same decoded inputs, and
when they agree — which is almost always — the prediction costs nothing.

You do not want this for something the server alone moves. A crate, a lift and an NPC are replicated
transforms, not predicted ones; doc 16's warning is worth repeating, that a game predicting movement
but not the interactions movement causes feels *less* consistent than one predicting nothing.

## Using it

On the server, one call per connection:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Net.Engine;
using Vixen.Net.Engine.Players;
using Vixen.Net.Sessions;

public static class Match {
    public static Entity Joined(World world, PlayerSpawner players, PlayerId player) =>
        players.Join(world, player, "gameplay/prefabs/avatar", LocalTransform.At(new(0f, 1f, 0f)));

    public static void Died(World world, PlayerSpawner players, PlayerId player) =>
        players.Respawn(world, player, "gameplay/prefabs/avatar", LocalTransform.At(new(0f, 1f, 0f)));

    public static void Left(World world, PlayerSpawner players, PlayerId player) =>
        players.Leave(world, player);
}
```

`Join` spawns the pawn **owned by that player**, and that one argument is what makes the client's copy
an autonomous proxy: it is what `PredictedOwnershipSystem` reads to decide what to predict, and what
`[ServerRpc(RequireOwnership = true)]` checks. Unreal reaches the same place by making the player
controller the `NetConnection` owner and letting ownership flow down; here it is said once, at the
spawn.

`Respawn` keeps the controller, so the aim, the seat and the camera survive a death with no code.

On the client, say who you are and where your controller is:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Net.Engine.Players;
using Vixen.Net.Sessions;

public static class Session {
    public static LocalPlayerSystem Connected(World world, PlayerId me) {
        var controller = Player.Create(world, owner: me.Value);

        // The camera exists before any body does, so the first frame after a spawn arrives is
        // already looking at the right place.
        PlayerCameras.ThirdPerson(world, controller);

        return new LocalPlayerSystem { Local = me, Controller = controller };
    }
}
```

The client is never told which entity is its pawn. An entity carrying `PlayerPawn`, owned by this
connection and possessed by nothing, *is* the pawn — an inference over state that is already
replicated, re-evaluated every frame. A message saying "you are pawn 47" can arrive before the spawn
it names, after the pawn has died, or twice; a query has none of those cases.

## Examples

**Predicting.** The step, the log and the predictor:

```csharp compile
using Vixen.Net.Engine.Players;
using Vixen.Net.Physics;
using Vixen.Net.Prediction;
using Vixen.Net.Replication;
using Vixen.Physics.Ecs;

public static class Predicting {
    public static ClientPrediction<PlayerMoveInput> Build(PhysicsScene scene, ReplicationRegistry registry) {
        var movement = new PredictedPlayerMovement(scene, 1f / 60f);
        var log = new InputLog<PlayerMoveInput>();

        return new ClientPrediction<PlayerMoveInput>(registry, log, movement.AsStep());
    }
}
```

Each tick the client calls `Step` with its input; when a snapshot arrives it calls `Reconcile`. The
input log is the same object the client sends from, because a replay needs exactly the inputs that
were used the first time.

**Rounding the local intent.** The one line that is easy to leave out and expensive to leave out:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Net.Engine.Players;

public static class Sending {
    public static PlayerMoveInput Take(World world, Entity controller) {
        ref var intent = ref world.Get<MoveIntent>(controller);

        // What goes on the wire is quantized, and the server computes from the decoded numbers. A
        // client predicting with full precision disagrees by the rounding on *every* tick, on a
        // perfect connection — and it looks like jitter rather than like a bug.
        intent = PlayerMoveInput.Round(intent);

        return PlayerMoveInput.From(intent);
    }
}
```

**What the input costs.** Seven bytes: two axes at eight bits, two angles at ten, and sixteen of
buttons. `InputLog<T>` sends the newest and the few before it every tick, so this is the one payload
that goes out more often than a snapshot.

## See also

- [Players and possession](engine/players-and-possession) — the controller, the aim and the camera
  this puts on a wire.
- [Character movement](engine/character-movement) — the rules the predicted step runs, and why they
  are a pure function.

⚠ **Two traps worth knowing about, both of which produce a green number measuring nothing.** A
hand-driven tick must call `World.AdvanceVersion()`, or every `WithChanged` filter in the engine
stops matching from the second tick onwards. And the predicted step must publish `NetworkTransform`
from `LocalTransform` inside the tick, or the recorded history never changes and every reconciliation
agrees while the two machines drift apart. `PredictedPlayerMovement` does both; a game writing its own
step has to.

The design record is `docs/plan/29-players-and-possession.md`, whose P3 this is.
