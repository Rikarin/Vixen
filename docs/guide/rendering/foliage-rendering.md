---
title: Drawing foliage
slug: rendering/foliage-rendering
kind: guide
area: Rendering
summary: Cells culled as objects, instances culled within them, LOD decided per instance, and one indirect command per level.
api: [T:Vixen.Rendering.Terrain.FoliageRenderer, T:Vixen.Rendering.Terrain.FoliageDraw, T:Vixen.Rendering.Terrain.FoliageBatch]
tags: [foliage, rendering, culling, lod, instancing]
since: 0.1
status: preview
related: [engine/foliage, engine/grass, rendering/instance-culling, rendering/grass-rendering, rendering/impostors, rendering/terrain-rendering, editor/foliage-mode]
---

## What it is

`FoliageRenderer` turns a `FoliageVolume` into indirect draws. Two stages: the cell as one object
against the frustum, then each surviving cell's instances against the frustum, their own cull distance
and their LOD level. `FoliageDraw` is the per-type draw templates; `FoliageBatch` is what one cell
contributed.

## What it is for

A forest that costs what is on screen rather than what is in the level. You want the second stage
whenever a cell holds more than a handful of instances — a 32 m cell of grass is fifty thousand of
them and the far half is behind a hill.

You do not want it for a few dozen props: two passes over an array to compact ten transforms costs
more than drawing all ten.

## Using it

```csharp no-compile="a fragment; the templates come from each type's LOD group"
renderer.Cull(volume, draws, frustum, camera.Position, densityScale: 1f);

foreach (var batch in renderer.Batches) {
    foreach (var command in batch.Commands) {
        // one indirect draw per level, at that level's own mesh
    }
}
```

⚠ **The LOD decision is per instance, and that is a deliberate divergence from
`LodRenderFeature`.** That feature is right for its case — a LOD group is several render objects and
it clears bits — and it cannot express "these four thousand trees in this cell are at level 1 and
those six hundred are at level 2", because its level is per object and here it is per instance. So a
cell draws three or four times instead of once.

⚠ **A level with no survivors still gets a command.** It is what lets a caller bind level N's mesh at
slot N instead of reading back which levels survived.

⚠ **A cell can survive the frustum and still draw nothing**, when every one of its instances is past
the cull distance. Issuing three empty commands for it would be the cost of the batch for none of the
benefit, so it does not become a batch — a third rejection worth knowing about when reading the
counters.

⚠ **One transform buffer for the whole frame, not one per cell.** A per-cell upload is a map and an
unmap per cell — a few thousand of them — to move a few kilobytes each. Every batch's run is a slice
of it, and the slices tile it exactly.

⚠ **The cross-fade weight rides in the per-instance parameters** and the existing dithered discard
reads it unchanged. Deciding the fade anywhere else would measure the distance a second time, and the
two would disagree by a frame — which pops.

## What is here and what is not

⚠ **This is the CPU half, and the compute shader is the other one.** The compaction, the binning and
the fade are `InstanceCuller`'s, which was built precisely so that both halves could be the same
arithmetic — the device form claims slots with an atomic add and is therefore unordered, which a seam
test sorts away rather than asserts through. What runs here is what a headless test and a machine with
no compute queue use, and it is the reference the dispatch is checked against.

⚠ **The bounding radius is the type's spacing scaled by the instance**, because this assembly has no
mesh and cannot ask one how big it is. A radius that is too small culls an instance while part of it is
on screen, so the approximation errs upwards.

## Examples

Three levels, taking over at sixty and a hundred and fifty metres:

```csharp no-compile="a fragment; the index counts come from the mesh's LOD group"
var draws = new FoliageDraw(
    type,
    [near, middle, far],
    [60f, 150f]
);
```

Reading what a frame cost:

```csharp no-compile="a fragment; the counters are what a profiler shows"
var drawn = renderer.Cull(volume, draws, frustum, camera.Position);

// renderer.CellsConsidered   — every cell holding a drawn type
// renderer.CellsDrawn        — the ones the frustum kept
// renderer.InstancesConsidered / drawn — what the second stage did
// renderer.Draws             — indirect commands issued, empty ones included
```

⚠ **`Draws` counts the empty commands, because they are issued.** A profiler reading it is reading
what the queue was handed.

Scaling the whole thing down for a scalability setting:

```csharp no-compile="a fragment"
renderer.Cull(volume, draws, frustum, camera.Position, densityScale: 0.5f);
```

⚠ **A density scalar drops the *same* instances every frame**, because the choice is a hash of the
position rather than a draw from a generator. A slider that reshuffled would make a forest shimmer.

## See also

- [Foliage instances](../engine/foliage.md) — the cells and instances this draws.
- [Drawing grass](grass-rendering.md) — the derived half, scattered rather than loaded.
- [Impostors](impostors.md) — the last LOD level, and the far field it makes affordable.
- [Culling and streaming instances](instance-culling.md) — the compaction and the LOD binning themselves.
- [docs/plan/31 § D9](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the cell as the batch, and why the second cull belongs on the GPU.
