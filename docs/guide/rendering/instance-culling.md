---
title: Culling and streaming instances
slug: rendering/instance-culling
kind: guide
area: Rendering
summary: Per-instance culling and LOD binning inside a batch, per-instance parameters beside the transforms, and a grid that keeps pages resident around whoever is moving.
api: [T:Vixen.Rendering.InstanceCuller, T:Vixen.Rendering.InstanceBounds, T:Vixen.Rendering.InstanceCullSettings, T:Vixen.Rendering.InstanceLodRun, T:Vixen.Rendering.Features.InstanceParameters, T:Vixen.Rendering.StreamingGrid, T:Vixen.Rendering.StreamingSource]
tags: [rendering, instancing, culling, lod, streaming]
since: 0.1
status: preview
related: [rendering/mesh-and-material, rendering/terrain-rendering]
---

## What it is

`InstanceCuller` takes the instances of one batch and produces the ones a view can see, binned by LOD
level, compacted so each level's run is contiguous. `InstanceParameters` is the per-instance data that
is not a transform — a tint, a wind phase, a scale, a fade — uploaded in a parallel buffer.
`StreamingGrid` keeps a grid of pages resident around whatever is moving through it.

## What it is for

Fifty thousand trees. Instancing draws a batch as one call, and a batch culled as one object is either
entirely drawn or entirely absent — which for a forest covering a level means always entirely drawn.
Culling *within* the batch is what makes the count the frame pays for the count on screen.

You do not want it for a handful of instances: two passes over an array to compact ten transforms
costs more than drawing all ten.

## Using it

```csharp no-compile="a fragment; the bounds come from whatever placed the instances"
var runs = culler.Cull(bounds, transforms, view, settings, out var visible);

foreach (var run in runs) {
    commands.DrawIndexed(indexCount, run.Count, firstInstance: run.First);
}
```

⚠ **Count, then place — two passes rather than one.** A single pass that appended as it went would
either need a per-level list or a stable sort afterwards; counting first makes each level's offset
known before anything is written, so the second pass writes straight to its final slot.

⚠ **Density is hashed from the position, not sampled from a generator.** A density scalar below one
has to drop the *same* instances every frame, or the ones that survive flicker. A hash of the position
is stable under any traversal order and under any change to how many instances came before it.

⚠ **LOD is per instance and the runs are what a draw takes.** A batch binned per level is several
indirect draws over one buffer rather than several buffers, which is what makes the compaction worth
doing at all.

## Per-instance parameters

`InstanceParameters` rides beside the transforms rather than inside them:

⚠ **A parallel buffer and a second permutation key.** A shader that does not declare
`Vixen.InstanceParameters` gets the transform-only variant and pays nothing; one that does gets a
second binding. `Neutral` — scale one, fade one — is what an instance with nothing to say uploads.

⚠ **The two buffers must not drift.** An instance appended to one and not the other shifts every
instance after it, which draws as a forest where the trees have each other's tints. There is a guard
for exactly that.

## The streaming grid

```csharp no-compile="a fragment; residency is the shared PageResidency"
grid.Update(sources, residency);
```

⚠ **Distance to the cell, not to its centre.** A source standing just inside a cell's edge is zero
away from it and half a cell from its centre; using the centre makes the ring of resident cells
lopsided in whichever direction the source happens to be leaning.

⚠ **`Lead` is how far ahead of what is needed the grid requests.** A page requested when it is needed
arrives after it was needed, which is a hole in the ground for as long as the load takes.

⚠ **It never evicts.** Eviction is `PageResidency`'s, which already has an LRU and a budget, and a
grid that also evicted would be a second policy disagreeing with the first about what is worth
keeping. `PageKey(Source, Index)` is deliberately source-agnostic so that terrain tiles, foliage cells
and meshlet pages all sit in the same budget.

## Examples

Culling a cell of trees and drawing the survivors, binned:

```csharp no-compile="a fragment; the arrays are one cell's instances"
var settings = new InstanceCullSettings { Density = 0.5f, LodDistances = distances };
var runs = culler.Cull(bounds, transforms, view, settings, out var visible);
```

⚠ **`Density` below one drops the *same* instances every frame**, because the choice is a hash of the
position rather than a draw from a generator. A density slider that reshuffled would make a forest
shimmer.

Per-instance parameters, for the instances that have something to say:

```csharp no-compile="a fragment"
feature.Add(transform, InstanceParameters.Neutral with { WindPhase = phase, Fade = fade });
```

Keeping pages resident around a player:

```csharp no-compile="a fragment; residency is shared with every other page source"
var grid = new StreamingGrid(cellSize: 128f, lead: 1);

grid.Update([new StreamingSource(player.Position, radius: 512f)], residency);
```

## See also

- [Meshes and materials](mesh-and-material.md) — the batch this culls within.
- [Drawing a terrain](terrain-rendering.md) — the other consumer of the residency seam.
- [docs/plan/31 § D9](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the cell as the batch, and why the second cull belongs on the GPU.
