---
title: Collision under the sculpt brush
slug: editor/terrain-sculpt-collision
kind: guide
area: Editor
summary: The adapter that joins the sculpt tools' `ITerrainColliders` seam to `TerrainColliderSystem`, so a stroke rebuilds the Jolt height field it moved — and the one number that says the wiring is wrong.
api: [T:Vixen.Editor.Terrain.Physics.TerrainColliders]
tags: [editor, terrain, sculpt, collision, physics]
since: 0.1
status: preview
related: [editor/terrain-mode, engine/terrain-collision, engine/terrain-sculpting, engine/terrain-heightfield]
---

## What it is

`TerrainColliders` implements `Vixen.Editor.Terrain.ITerrainColliders` over
`Vixen.Terrain.Physics.TerrainColliderSystem`. `TerrainEdit.Commit` calls the seam after every stroke
that moved a height; this is what turns that call into a rebuilt Jolt height field for the tiles the
stroke touched.

It lives in its own assembly, `Vixen.Editor.Terrain.Physics`, with exactly two references — the
toolset and the collider system. That is not tidiness: the layer rules fail a `Core/` project that
references an `Editor/` one, so `TerrainColliderSystem` cannot implement the interface itself, and
`Vixen.Editor.Terrain` deliberately links no physics. The join has nowhere else to go.

## What it is for

A host that both **edits a terrain** and **simulates one**: an editor with a physics world, a tool
that sculpts at runtime, a level-generation harness that wants collision it can raycast the moment a
stroke lands.

A game that only *plays* a terrain needs none of this — `TerrainColliderSystem` follows
`Terrain.RevisionOf` on its own, and [terrain collision](../engine/terrain-collision.md) is the page
for that. What this adds is *when*: the frame the artist let go of the mouse, rather than the one
after it.

## Using it

```csharp no-compile="a fragment; `physics` and `placements` are the host's"
var system = new TerrainColliderSystem(physics, placements);

terrainMode.Editing.Colliders = new TerrainColliders(system);
```

`TerrainModule` will do that assignment for you if the host publishes the interface as a plugin
service:

```csharp no-compile="a fragment; `plugins` is the host's PluginServices"
plugins.Add<ITerrainColliders>(new TerrainColliders(system));
```

The module reads it in its per-frame follow, so publishing it after the toolset loaded is fine — a
host acquires a physics world when it has a reason to, which is not necessarily before its plugins
activated.

### ⚠ The editor Vixen ships publishes none, and that is not an oversight

`EditorApplication` holds no `PhysicsScene`; nothing under `Editor/` does, and no assembly under
`Editor/` even references `Vixen.Physics`. Play mode steps a system graph, but not one with physics
in it, so there is still no simulation for a stroke to keep in step with. A sculpt stroke in the
shipped editor therefore rebuilds no collision **because there is none to rebuild** — not because the
seam is unfed. Publishing the service is what an embedding host does when that changes.

⚠ **What was missing was a whole tier below a `PhysicsScene`, and that tier now exists.** Until
2026-08-21 the editor did not merely lack a physics world — it ran **no systems at all**.
`PlayModeController.ShouldTick`, the method that decides whether the game loop advances this frame,
had no caller in the product; its only callers were its own tests. Pressing Play snapshotted the
world, maximised the viewport and showed a notification, and nothing then stepped, so a
`PhysicsScene` published here would have been a physics world nothing ever called `Synchronize` on.

Play mode now steps a real `EngineLoop` over the world being edited, and `PlayModeController.Loop`
is the seam a host adds to. What is still missing is narrower and is not this subsystem's either:
`Vixen.Editor.App` does not reference `Vixen.Physics`, so the shipped editor still constructs no
`PhysicsScene` — and nothing in a project *declares* which systems its scene wants, so a session runs
the engine's default graph (behaviours, coroutines, transforms) and says on entry what it is not
running. An embedding host that owns a physics world can add its four systems to `Loop` and publish
`ITerrainColliders` beside them, which is what the per-frame `BindColliders` resolution above was
built to accept. See [docs/plan/11 § Play mode runs a system
graph](https://github.com/Rikarin/Vixen/blob/master/docs/plan/11-editor.md#play-mode-runs-a-system-graph).

⚠ **Note what `Vixen.Editor.App` already references and what it does not.** It references
`Core/Vixen.Water.Physics` — for `BuoyancyBody`'s icon and its Add Component entry, so the *type* can
be placed on an entity. That is the editor knowing a physics component exists, not the editor running
physics, and it is the shape the terrain case would take too.

### ⚠ `Missed` is the number that says the wiring is wrong

```csharp no-compile="a fragment; `colliders` is the adapter above"
if (colliders.Missed > 0) { /* the terrain being sculpted is in no placement list */ }
```

Both `TerrainColliderSystem.Rebuild` overloads return `bool` where `ITerrainColliders` returns
`void`, and `false` means *this system has never heard of this terrain*. A forwarding wrapper that
discarded that value would report success for every stroke while rebuilding nothing, and would say so
in no log. `Missed` counts those, and a rebuild that found nothing asks
`TerrainColliderSystem.Sync` — which is the only thing that can make a terrain known — so a stroke
that lands before the first frame builds the terrain rather than being lost.

Zero is the working state. Anything else is an `ITerrainPlacements` that does not list the terrain the
brush is pointed at.

## Examples

### Push and poll do not fight

```csharp no-compile="a fragment; `system` and `edit` are the host's"
edit.Colliders = new TerrainColliders(system);

edit.Begin(new(20f, 20f));
edit.Commit();          // the tiles under the brush are rebuilt now

system.Sync();          // and the poll finds nothing stale
```

`TerrainColliderSystem` stamps each tile with `Terrain.RevisionOf` as it builds it and skips a tile
whose stamp still matches, so a pushed rebuild is not done a second time on the next frame. That
matters more than it sounds: `PhysicsShapes` never releases a shape, so a tile rebuilt every frame
grows the intern table for the life of the world. `TerrainColliderSystem.Rebuilds` is the counter
that makes it visible.

### A stroke on a tile seam rebuilds both sides

Nothing here has to arrange that. `Rebuild(terrain, rect)` walks `TerrainDescription.TilesOf`, which
answers with **both** tiles for a sample on a boundary — a stroke along a tile edge that rebuilt one
side would leave a strip of collision disagreeing with the ground beside it by whatever the stroke
moved, which is a lip the player trips on, on a seam nothing draws.

### Paint strokes rebuild nothing, on purpose

`TerrainEdit.Commit` only calls the seam for the sculpt category. A paint stroke changes which
*material* a quad is, which is read from the weights when it is asked rather than baked into the
shape — so a rebuild would be a Jolt height field built to hold the heights it already has, once per
stroke, for nothing.

## See also

- [Terrain collision](../engine/terrain-collision.md) — the runtime half: one height-field shape per
  tile, and the poll that follows sculpting without a caller.
- [Sculpt and paint mode](terrain-mode.md) — `ITerrainColliders`, the seam this feeds, and what calls
  it.
- [Sculpting terrain](../engine/terrain-sculpting.md) — what moves the ground the colliders follow.
