---
title: Nodes over the world
slug: ai/world-nodes
kind: guide
area: AI
summary: The behaviour-tree nodes that walk, turn, patrol, animate and make noise — and what each of them needs on the entity.
api: [T:Vixen.Ai.Nodes.MoveToTask, T:Vixen.Ai.Nodes.MoveDirectlyTowardTask, T:Vixen.Ai.Nodes.PatrolTask, T:Vixen.Ai.Nodes.RotateTowardTask, T:Vixen.Ai.Nodes.DoesPathExistDecorator, T:Vixen.Ai.Nodes.PathTest, T:Vixen.Ai.Nodes.AgentTarget, T:Vixen.Ai.Nodes.PlayAnimationTask, T:Vixen.Ai.Nodes.PlaySoundTask, T:Vixen.Ai.Nodes.DefaultFocusService, T:Vixen.Ai.Nodes.WorldNodes, T:Vixen.Ai.Nodes.Ecs.AiFocus, T:Vixen.Ai.Nodes.Ecs.PatrolRoute, T:Vixen.Ai.Nodes.Ecs.PatrolMode]
tags: [ai, behaviour-trees, navigation, animation, audio]
since: 0.1
status: stable
related: [ai/behaviour-trees, ai/perception, ai/authoring-a-tree, ai/environment-queries]
---

## What it is

`Vixen.Ai.Nodes` is the set of behaviour-tree nodes that **touch the rest of the engine**: walking a
navmesh, turning, patrolling a route, asking whether a path exists, playing an animation state and
making a sound.

It is a separate assembly with the widest reference list in `Core/` — navigation, animation and audio
at once — and that is safe for exactly one reason: **nothing depends on it.** A game links it if it
wants the nodes, and a game that wants behaviour trees over its own movement code does not.

## What it is for

The half of a behaviour tree that is not decision-making. A `Selector` and a `Blackboard` decorator
are the same in every game ever shipped; "walk over there" is the same in most of them, and this is
where that lives so that a project does not write `MoveToTask` for the fourth time.

You do *not* want these for anything with a rule in it — a task that casts an ability, applies a
status effect or checks a gameplay tag names a game's definitions and belongs to the game.

## Using it

Each node reads and writes components the entity already carries, and **fails rather than adding
them**. A tree step happens inside a chunk walk, and a structural change there invalidates every span
the walk is holding — so a guard that can walk is authored with a `NavigationAgent`, and one that
cannot is a bug worth seeing.

| Node | Slot | What the entity needs |
|---|---|---|
| `MoveTo` | task | `NavigationAgent`, `NavigationDestination`, `LocalTransform` |
| `MoveDirectlyToward` | task | `LocalTransform` |
| `Patrol` | task | `PatrolRoute`, `NavigationDestination` |
| `RotateToward` | task | `LocalTransform`; `AiFocus` if no key is given |
| `DoesPathExist` | decorator | `LocalTransform`, and a `NavMeshQuery` given to the resolver |
| `PlayAnimation` | task | `AnimatorComponent` with the named layer and state |
| `PlaySound` | task | `AudioSource` and `AudioClipRef` |
| `DefaultFocus` | service | `AiFocus` |

```csharp compile
using Vixen.Ai;
using Vixen.Ai.Nodes;
using Vixen.Ai.Nodes.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation.Ecs;

public static class Guards {
    public static Entity Spawn(World world, Vector3 at) {
        ArgumentNullException.ThrowIfNull(world);

        var guard = world.Create(LocalTransform.At(at), NavigationAgent.Default(), new NavigationDestination());

        world.Add(guard, new NavigationState { Position = at });
        world.Add(guard, new AiFocus());
        world.Add(
            guard,
            PatrolRoute.Of(
                PatrolMode.Loop,
                new Vector3(5f, 0f, 5f),
                new Vector3(32f, 0f, 5f),
                new Vector3(32f, 0f, 32f)
            )
        );

        return guard;
    }
}
```

### One key, two types

A movement node takes one key and does not care whether it holds a `Vector3` or an `Entity`.

⚠ **An entity target follows its entity; a position target does not.** That is the difference between
chasing somebody and going to where they were — and it is exactly the pair
[perception's](perception.md) default binding writes, so `MoveTo(target)` chases while
`MoveTo(seen)` searches.

### The route is a component, not a setting

One `.vxbt` runs every guard in the game; the corridor each of them walks is a `PatrolRoute` placed
in the level editor. Putting the points on the task would mean a behaviour tree per patrol route.

⚠ **Only `PatrolMode.Forward` ever succeeds.** A loop and a ping-pong have no end, so they stay
`Running` for ever and are meant to be interrupted — by a decorator observing a perception key, which
is the whole shape of a patrolling guard.

⚠ **A patrol starts from the nearest point, not the first.** A guard that respawns mid-route otherwise
walks back to the start of it through whatever is in the way.

### Aborting is where movement nodes earn their keep

`MoveTo` and `Patrol` both stop the agent when they are aborted. An agent that kept walking to a
destination its tree has forgotten about is the classic behaviour-tree bug — a guard that chases you
while playing its idle — and it happens whenever a higher-priority branch takes over.

### Asking whether a path exists

`DoesPathExist` has three settings, cheapest first:

| Test | What it does |
|---|---|
| `Raycast` | walks a straight line across the surface. Says no to anything round a corner. |
| `Budgeted` | a search stopped after a node budget. Says no when it runs out. |
| `Full` | the whole search. Exact, and the most expensive thing a decorator can do. |

⚠ **There is no hierarchical test, and `Budgeted` is what stands in for one.** Unreal's hierarchical
query reads a coarse graph baked beside the mesh; Vixen bakes no such graph, and a second navigation
structure kept in step with the first is a bad trade for one decorator. A budgeted search is wrong
only in the direction that makes an agent give up rather than walk into a dead end.

⚠ **It re-tests when its key changes, not when the world does.** A door closing writes no blackboard
key, so a branch already running keeps running until its `MoveTo` reports that the crowd failed. The
decorator answers "is it worth starting"; the task answers "did it work".

### The focus

`AiFocus` is one place everything downstream reads — a rotation task, an aim offset, a head-look
constraint and a dialogue camera all want "what is this character looking at". `DefaultFocus` keeps
it pointed at a key, and `RotateToward` with no key falls through to it.

⚠ **The service clears the focus when the key is unset, and that is the half people leave out.** A
focus nobody cleared is a guard that keeps staring at where an enemy was after it has forgotten
about it.

## Examples

Registering the nodes so a `.vxbt` can name them — the query and the sounds are the two things a
schema cannot carry:

```csharp no-compile="a fragment; the mesh and the clips come from the level and the content build"
var resolver = new BehaviorTreeResolver();

WorldNodes.Register(resolver, new NavMeshQuery(level.NavMesh), new Dictionary<string, AudioClip> {
    ["footstep"] = clips.Load("guard/footstep"),
    ["alerted"] = clips.Load("guard/alerted")
});
```

A guard that patrols until it sees something, chases while the sighting is fresh, and goes back to
its route when it is not — which is P4's exit criterion as a file:

```yaml
version: 1
name: Guard
keys:
  - { name: target, type: Entity }
  - { name: seen, type: Vector3 }
  - { name: age, type: Float }
root:
  name: Brain
  type: Selector
  services:
    - type: DefaultFocus
      interval: 0.2
      fields: { Key: target }
  children:
    - name: Chase
      type: MoveTo
      fields: { Key: target, Acceptance: "2", Repath: "1" }
      decorators:
        - type: Blackboard
          fields: { Key: age, Test: Less, Value: "0.5", Aborts: Both }
    - name: Search
      type: MoveTo
      fields: { Key: seen, Acceptance: "1.5", Repath: "1" }
      decorators:
        - type: Blackboard
          fields: { Key: target, Test: IsSet, Aborts: LowerPriority }
    - { name: Walk, type: Patrol, fields: { Acceptance: "1.5" } }
```

⚠ **"Gives up" is one decorator over one float.** No timer, no second branch holding a remembered
position, and nothing in the tree that knows what a sense is: the age of the stimulus crosses half a
second, the key's observers fire, and the branch is aborted.

## See also

- [Perception](perception.md) — where `target`, `seen` and `age` come from.
- [Behaviour trees](behaviour-trees.md) — what an abort is, and why the decorator above interrupts.
- [Authoring a behaviour tree](authoring-a-tree.md) — the editor these nodes appear in, and the
  factories that let an assembly contribute nodes.
