---
title: Drawing a terrain
slug: rendering/terrain-rendering
kind: guide
area: Rendering
summary: A quadtree with a vertex morph, one instanced grid patch, no vertex buffer, and one draw call however many patches it takes.
api: [T:Vixen.Terrain.TerrainLodRanges, T:Vixen.Terrain.TerrainLodNode, T:Vixen.Terrain.TerrainLodTree, T:Vixen.Rendering.Terrain.TerrainGridPatch, T:Vixen.Rendering.Terrain.TerrainNodeRecord, T:Vixen.Rendering.Terrain.TerrainRenderer, T:Vixen.Rendering.Terrain.TerrainShaders, T:Vixen.Rendering.Terrain.TerrainView, T:Vixen.Rendering.Terrain.TerrainComponent, T:Vixen.Shaders.Generated.TerrainKeys, T:Vixen.Shaders.Generated.TerrainConstants, R:Terrain/Terrain]
tags: [terrain, rendering, lod, cdlod, instancing]
since: 0.1
status: preview
related: [engine/terrain-heightfield, rendering/mesh-and-material, editor/terrain-mode, rendering/instance-culling]
---

## What it is

The device side of a terrain. `TerrainLodTree` descends a quadtree over the heightfield and selects
the nodes a view needs, each with a morph factor; `TerrainGridPatch` is the one lattice every node is
drawn from; `TerrainNodeRecord` is the sixteen bytes that place it; `TerrainRenderer` owns the
heightmap texture, the descriptor set and the draw. `TerrainComponent` is what a scene says about a
terrain — which one, how far level 0 reaches, whether it casts shadows.

## What it is for

Drawing four square kilometres of ground at sixty frames a second without popping. CDLOD (Strugar,
2010) is a quadtree over the heightfield with a vertex morph toward the parent grid; the morph is what
removes the pop, and the shared patch is what makes the whole terrain one instanced draw.

You do not want it for a small piece of ground that never changes — a mesh is simpler and costs less
to set up. The break-even is roughly where an artist would want to sculpt rather than model.

## Using it

```csharp no-compile="a fragment; the shaders come from the compiled library"
var renderer = new TerrainRenderer(device, terrain, shaders);

renderer.Upload(commands, view);
renderer.Record(commands, view);
```

⚠ **The shader takes no vertex buffer, and its reflection says so.** A regular lattice's positions are
two divisions of `SV_VertexID`, so uploading 33² of them per frame would be sending the shader
something it can count — `Terrain.reflect.json` has an empty `VertexInputs`. What is uploaded is the
index buffer, once, and one record per patch.

⚠ **`SampleLevel`, not `Sample`.** A vertex stage has no derivatives, so `Sample` outside a fragment
stage never meant what it looked like and SPIR-V was quietly substituting level zero. A terrain
heightmap is the case that motivated adding the explicit-level form.

⚠ **The normal is differenced at the patch's own step**, not at one sample. A normal taken at full
resolution on a coarse patch is the normal of geometry that patch does not have, which makes the seam
between two levels visible in the lighting even though the positions agree.

⚠ **The diagonal alternates in a checker.** Splitting every quad the same way makes a lattice of
parallel diagonals, which on a heightfield reads as corduroy — most visible where the ground is nearly
flat, which is where an artist looks hardest.

⚠ **The winding is counter-clockwise seen from above.** Getting it backwards produces a terrain that
is invisible from above and solid from below, which reads as nothing drawing at all rather than as a
winding problem.

## The morph

`TerrainLodTree.MorphIndex` is the whole of it: an odd grid index slides onto its even neighbour as the
morph goes to one, so a patch fully morphed has exactly half its resolution and its shared edge lands
on its coarse neighbour's vertices.

⚠ **A morph ratio of 1 is not a setting, it is a crack at every transition**, so `TerrainLodRanges`
refuses it and says why: a band with no width leaves the finer node undegenerate exactly where the
coarser one takes over.

⚠ **A morphed vertex has to read the heightmap bilinearly.** It lands between samples for every morph
but zero and one, and snapping to the nearest sample would reintroduce — in the thing that *reads* the
morph — the pop the morph exists to remove.

⚠ **The grid size is a permutation rather than a uniform**, so the vertex stage's two integer
divisions fold at compile time. For a power of two that is a shift and a mask.

## Uploading

⚠ **Only what changed is re-uploaded, and the first frame is a special case.** A terrain built and
then resolved has no dirty tiles at all, so a renderer copying only dirty rows draws a heightmap of
zeros until somebody happens to sculpt — which reads as a flat terrain rather than as a missing
upload. After the first frame a stroke on one tile of sixteen moves a fraction of the bytes.

⚠ **One heightmap for the whole terrain, not one per tile.** Per-tile textures exist for *streaming* —
a tile is the unit of load — and drawing wants the opposite: a patch straddles no tile boundary except
by luck, so a per-tile heightmap makes every straddling patch either two draws or a shader sampling
two textures. A 4 km² terrain at one metre is 4097² samples, which is 33 MB in `R16UNorm`.

## What is kept honest without a GPU

The morph's arithmetic is checked two ways: a source assertion that the expression is still in
`Terrain.rvn`, and a transliteration of it compared against `TerrainLodTree.MorphIndex` over every
index and every morph.

⚠ **A source assertion is weaker than an execution and is chosen knowing it.** It catches the failure
that actually happens — somebody edits or deletes the morph and every level boundary opens — and it
does not catch a subtly different but similar-looking expression. A golden image catches that, and it
needs a device.

## Examples

Selecting the patches a view needs, which is the half that runs without a device:

```csharp no-compile="a fragment; the ranges are a project setting"
var tree = new TerrainLodTree(terrain.Description, TerrainLodRanges.Default);
var nodes = new List<TerrainLodNode>();

tree.Select(view, nodes);
```

Each node becomes sixteen bytes:

```csharp no-compile="a fragment; `node` is one the selector produced"
var record = TerrainNodeRecord.Of(node, gridQuads: TerrainLodTree.DefaultGridQuads);
```

⚠ **`Origin + gridIndex × Step` is the sample the heightmap is read at**, and the far corner of the
patch is its far sample. That identity is what makes one lattice serve every level.

The whole frame is two calls and one draw:

```csharp no-compile="a fragment; the command list is the frame's"
renderer.Upload(commands, view);
renderer.Record(commands, view);
```

## See also

- [The terrain heightfield](../engine/terrain-heightfield.md) — the samples this draws.
- [Meshes and materials](mesh-and-material.md) — the render feature vocabulary this fits into.
- [Culling and streaming instances](instance-culling.md) — the same residency seam, and what the foliage on this will use.
- [docs/plan/31 § D3](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  a quadtree with a morph rather than a clipmap, and the argument between them.
