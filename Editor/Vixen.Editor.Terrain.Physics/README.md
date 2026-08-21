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

`EditorApplication` holds no `PhysicsScene` and nothing under `Editor/` even references
`Vixen.Physics` — its play mode steps a system graph, but not one with physics in it. So a sculpt
stroke in the shipped editor rebuilds no collision **because there is none to rebuild**, which is a different statement
from the seam being unfed. A host that has a physics world publishes the service and the toolset
picks it up.

⚠ **The missing piece used to be a tier below a physics scene — the editor ran no systems at
all — and that tier landed on 2026-08-21.** `PlayModeController.ShouldTick`, the method that decides
whether the game loop advances this frame, had no caller in the product; its only callers were its
own tests, so a `PhysicsScene` published here would have been a physics world nothing calls
`Synchronize` on. Play mode now steps a real `EngineLoop` over the world being edited and
`PlayModeController.Loop` is the seam physics attaches to — see docs/plan/11 § *Play mode runs a
system graph*. What remains is narrower: no assembly under `Editor/` references `Vixen.Physics`, and
nothing in a project declares which systems its scene wants, so the shipped editor still constructs
no scene. A host that has one adds the physics systems to `Loop` and publishes this adapter beside
them.

## Tests

`Editor/Vixen.Editor.Terrain.Physics.Tests` asserts against a **dropped rigid body**, not a call
count: a crate settles on the ground, a stroke raises it five metres, and the rest height follows.
The negative control leaves `TerrainEdit.Colliders` null and shows the crate resting at 0.4815 m
before and after the same stroke — which is what the tree did for a year, silently.
