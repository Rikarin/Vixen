# Vixen.Editor.Terrain.Physics

Feeds the sculpt tools' collision seam.

- `Editor/Vixen.Editor.Terrain/ITerrainColliders` is what `TerrainEdit.Commit` calls after every
  stroke that moved a height.
- `Core/Vixen.Terrain.Physics/TerrainColliderSystem` is what rebuilds a tile's Jolt height field.
- Neither may reference the other. `build/Build.ArchitectureRules.cs` fails a `Core/` project that
  references an `Editor/` one, and the toolset deliberately links no physics.

So this is the assembly between them: `Vixen.Terrain.Physics`' own arrangement one layer up, and
`Vixen.Water.Physics`' before that.

It is now two things rather than one. `TerrainColliders` is the adapter — the seam's implementation,
for any host that has a physics world. `TerrainPhysicsModule` is what makes the **shipped editor** one
of those hosts: it is named in `EditorModules.Standard`, publishes the `ITerrainColliders` the sculpt
tools resolve, and contributes an `IPlaySystems` that runs a `TerrainColliderSystem` over the
`PhysicsScene` the editor application stands up when Play is pressed.

```csharp
var system = new TerrainColliderSystem(physics, placements);

terrainMode.Editing.Colliders = new TerrainColliders(system);
```

Or publish it and let `TerrainModule` do the assignment:

```csharp
plugins.Add<ITerrainColliders>(new TerrainColliders(system));
```

## ⚠ The "three lines" claim was nearly right, and the missing line is the point

`docs/plan/31` § D10 and `docs/guide/engine/terrain-collision.md` both said `Rebuild(terrain, rect)`
"has `ITerrainColliders`' signature so an editor-side adapter is three lines". It does not, quite:
both overloads return `bool` where the interface returns `void`.

That discarded value is the whole hazard. `false` means **this system has never heard of this
terrain**, so a forwarding wrapper that threw it away would report success for every stroke while
rebuilding nothing — the same defect this assembly exists to end, one layer in. `Missed` counts them
and `TerrainColliderSystem.Sync` answers them.

## ⚠ Push and poll do not fight

`TerrainColliderSystem` stamps each tile with `Terrain.RevisionOf` as it builds it, and its per-frame
pass skips a tile whose stamp still matches. A stroke pushed through here is therefore not rebuilt
again on the next frame — which matters, because `PhysicsShapes` never releases a shape. What the
push adds over the poll is only *when*: the frame the artist let go of the mouse.

## The editor Vixen ships now publishes one, and it is a switch

`TerrainModule.BindColliders` resolves `ITerrainColliders` in its per-frame follow and **keeps the
first answer**, and `PluginServices` has no removal. The physics world, meanwhile, exists only while a
play session does — physics belongs to play, not to editing, because a body that falls while somebody
drags a gizmo is a scene that edits itself, and because the tile entities a collider system creates
have to be *inside* what a stop restores rather than something that lands in a person's scene file.

Two lifetimes, so two objects:

| Lives for | What it is |
|---|---|
| the editor | `PlayColliders`, published once — forwards to whatever is simulating, counts strokes that had nothing to rebuild in |
| one session | `TerrainColliderSystem` and a `TerrainColliders` over it, created on Play, dropped on Stop |

⚠ **`Idle` is the editing-half counter and `Missed` is the wiring one.** A stroke while nothing plays
increments `Idle`, which is `ITerrainColliders`' own "a terrain with no collision, not an error". A
stroke *while a session runs* that increments `Missed` is an `ITerrainPlacements` that does not list
the ground being sculpted — which has no other symptom.

⚠ **This module builds no simulation of its own.** The `PhysicsScene` comes from
`PlaySession.TryGet`; a session without one runs no collider system at all. A second physics world
over one scene is a state in which nothing collides with anything and nothing is raised.

⚠ **And nothing here catches what `TerrainColliderSystem.Rebuild(terrain, tileX, tileZ)` throws.** An
out-of-range tile index used to corrupt a *different* tile in silence; it throws now, and a wrapper
that swallowed it would put the silence back with an extra step in front of it.

## Tests

`Editor/Vixen.Editor.Terrain.Physics.Tests` asserts against a **dropped rigid body**, not a call
count: a crate settles on the ground, a stroke raises it five metres, and the rest height follows.
The negative control leaves `TerrainEdit.Colliders` null and shows the crate resting at 0.4815 m
before and after the same stroke — which is what the tree did for a year, silently.
