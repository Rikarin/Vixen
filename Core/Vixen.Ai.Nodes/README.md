# Vixen.Ai.Nodes

The behaviour-tree nodes that touch the rest of the engine. Walk a navmesh, walk in a straight line,
patrol a route, turn to face something, ask whether a path exists, play an animation state, play a
sound, and keep a focus pointed at something.

Spec: [docs/plan/37 § P4](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md).

## State

**Built and tested — 30 tests**, and P4's exit criterion is one of them: a guard patrols a baked
navmesh, notices a player, chases it and gives up, with no window and asserting positions. Every
part of it is authored — the tree is a `.vxbt` compiled through `BehaviorTreeContentCompiler`, the
route is a component, the sight is a `PerceptionConfig` — so the only thing the test does per frame
is move the player and step three systems.

## The widest reference list in `Core/`, and why that is fine here

These nodes are where a decision meets the engine, so somebody has to depend on navigation,
animation and audio at once. What makes that safe is the other half: **nothing depends on this**. A
game links it if it wants the nodes and does not if it does not, and `NodeLayeringTests` asserts
both directions — the list it may reference, and that no loaded assembly references it.

| | |
|---|---|
| `Movement/MoveToTask` | Walks to a key's position or entity over the navmesh, through `NavigationDestination`. |
| `Movement/MoveDirectlyTowardTask` | Straight line, ignoring navigation. For fliers, swimmers and levels with no mesh yet. |
| `Movement/PatrolTask` | Walks the route on the entity's own `PatrolRoute`. Forward, ping-pong or loop. |
| `Movement/RotateTowardTask` | Turns to face a key, or the focus. Yaw only. |
| `Movement/DoesPathExistDecorator` | By nav raycast, by a budgeted search, or by the whole search. |
| `Movement/AgentTarget` | One key, two types: a `Vector3` is a place and an `Entity` is a thing to follow. |
| `Presentation/PlayAnimationTask` | Plays a state on a layer and waits for it to play through. |
| `Presentation/PlaySoundTask` | Plays a clip on the agent's own `AudioSource`. |
| `Presentation/DefaultFocusService` | Keeps `AiFocus` pointed at a key, and clears it when the key is unset. |
| `Ecs/AiFocus` · `PatrolRoute` | What the agent is looking at, and the corridor it walks. |
| `WorldNodes` | The declarations, and how a `.vxbt` builds them. |

## The five things worth knowing before reading the code

### A key is one key, and the node does not care what type it is

`MoveTo(target)` means "go to that thing". Whether the key holds a `Vector3` or an `Entity` is a fact
about how the key got written — and it is exactly the pair perception's default binding produces. An
entity target follows its entity; a position target does not. That is the difference between chasing
somebody and going to where they were, and it is one node rather than two with the same name.

### The route is the level's data and the task is the asset's

One `.vxbt` runs every guard in the game; the corridor each of them walks is a `PatrolRoute`
component placed in the level editor. Putting the points on the task would mean a behaviour tree per
patrol route, which is the thing an authored tree exists to avoid.

⚠ **Only `PatrolMode.Forward` ever succeeds.** A loop and a ping-pong have no end, so they stay
`Running` for ever and are *meant* to be interrupted — by a decorator observing a perception key,
which is the whole shape of a patrolling guard.

### A move task keeps its issued destination, and that is not an optimisation

Writing `NavigationDestination` every tick bumps its version every tick, which is a full path search
per agent per frame — the exact cost `NavPathQueue`'s budget exists to bound, paid unconditionally.
The target has to have actually moved before the search is worth repeating.

⚠ **Aborting stops the agent where it stands.** An agent that kept walking to a destination its tree
has forgotten about is the classic behaviour-tree bug: a guard that chases you while playing its idle.

### There is no hierarchical path query, and `Budgeted` is what stands in for one

Unreal offers raycast, hierarchical and full. A hierarchical query answers "are these two places in
the same connected region" off a coarse graph built beside the mesh; Vixen bakes no such graph, and
inventing one for a decorator would be a second navigation structure to keep in step with the first.
A search stopped at a node budget answers the same question with the same shape of cost, and is wrong
only in the direction that makes an agent give up rather than walk into a dead end.

### Two of the nodes need something a schema cannot name

`DoesPathExist` needs a live `NavMeshQuery` over a baked mesh and `PlaySound` needs an `AudioClip`
a content build produced. Neither is a string in a file, so `WorldNodes.Register` takes the query and
sounds are registered by name the way sensors already are. A tree authored before the level is baked
still compiles — the decorator is reported and the branch reads as the dead end it is.

## Reading

- [The guide page](../../docs/guide/ai/world-nodes.md) — configuring each node, and what it needs.
- [Vixen.Ai](../Vixen.Ai/README.md) — the tree these nodes are leaves of.
- [Vixen.Ai.Perception](../Vixen.Ai.Perception/README.md) — where the keys they read come from.
