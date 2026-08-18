# Vixen.Editor.Terrain.Physics

Feeds the sculpt tools' collision seam.

- `Editor/Vixen.Editor.Terrain/ITerrainColliders` is what `TerrainEdit.Commit` calls after every
  stroke that moved a height.
- `Core/Vixen.Terrain.Physics/TerrainColliderSystem` is what rebuilds a tile's Jolt height field.
- Neither may reference the other. `build/Build.ArchitectureRules.cs` fails a `Core/` project that
  references an `Editor/` one, and the toolset deliberately links no physics.

So this is the assembly between them: **one type, two references**, which is
`Vixen.Terrain.Physics`' own arrangement one layer up, and `Vixen.Water.Physics`' before that.

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

## ⚠ The editor Vixen ships publishes no `ITerrainColliders`

`EditorApplication` holds no `PhysicsScene` and nothing under `Editor/` does — its play mode is a
`WorldSnapshot` capture and restore rather than a system graph. So a sculpt stroke in the shipped
editor rebuilds no collision **because there is none to rebuild**, which is a different statement
from the seam being unfed. A host that has a physics world publishes the service and the toolset
picks it up.

## Tests

`Editor/Vixen.Editor.Terrain.Physics.Tests` asserts against a **dropped rigid body**, not a call
count: a crate settles on the ground, a stroke raises it five metres, and the rest height follows.
The negative control leaves `TerrainEdit.Colliders` null and shows the crate resting at 0.4815 m
before and after the same stroke — which is what the tree did for a year, silently.
