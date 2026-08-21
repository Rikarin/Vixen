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
related: [editor/terrain-mode, editor/play-mode-systems, engine/terrain-collision, engine/terrain-sculpting, engine/terrain-heightfield]
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

### The editor Vixen ships publishes one, and it is a switch over a session

⚠ **This section used to say the opposite, and the change is worth reading rather than skipping.**
Until 2026-08-21 the editor did not merely lack a physics world — it ran **no systems at all**:
`PlayModeController.ShouldTick` had no caller in the product, so pressing Play snapshotted the world,
maximised the viewport and stepped nothing. A `PhysicsScene` published then would have been a world
nothing called `Synchronize` on.

Both halves are closed now. `PlayModeController` steps a real `EngineLoop`, and `IPlaySystems` is how
something that owns a service adds the systems that need it — see
[what a play session runs](play-mode-systems.md). `Vixen.Editor.App` contributes a `PhysicsScene` over
the world being edited; `TerrainPhysicsModule`, named in `EditorModules.Standard`, publishes the
`ITerrainColliders` the toolset resolves and runs a `TerrainColliderSystem` over that scene.

⚠ **The published service is not a `TerrainColliders`, and that is the design.** `BindColliders`
resolves once and keeps the answer, and `PluginServices` has no removal — so the object published has
to outlive every session, while the collider system behind it exists only while one runs. What is
published is a switch: it forwards to the session's adapter, and when nothing is playing it counts
the stroke and rebuilds nothing, which is this page's own "null is a terrain with no collision, not
an error" wearing a counter.

⚠ **Physics belongs to play, not to editing.** Nothing simulates while the editor is editing — a body
falling under a gizmo drag is a scene that edits itself — and the session lifetime is also what keeps
the collider system's tile entities inside what a stop restores, rather than in somebody's scene file.

An embedding host that is *not* the editor still does what this page's first example does: construct
the adapter over its own system and publish it.

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
