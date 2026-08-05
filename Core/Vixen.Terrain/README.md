<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Terrain

The terrain kernel — [docs/plan/31](../../docs/plan/31-terrain-grass-and-trees.md) § D1–D12. The
heightfield, its edit layers, the paint channels, the holes, the sculpt kernels, the brush and the
stroke record.

**No device, no document, no editor.** One project reference, to `Vixen.Core.Mathematics`, for the
reason `Vixen.Geometry` has the same one: a kernel that needed the render assembly to describe a
height sample would be backwards. Everything here is a function over arrays, so the tests need no
world and run in milliseconds.

## What is here

| Type | What it is |
|---|---|
| `TerrainDescription` | The shape: tile size in samples, tile counts, metres per quad, height range — and every derived number a create dialog shows |
| `TerrainSamples` | One 16-bit grid covering the whole terrain. Tiles are *windows* into it |
| `Terrain` | The base, the layer stack, the composite and its per-tile cache, the weights and the holes |
| `TerrainEditLayer` | One non-destructive container of signed deltas, sparse in 64-square chunks |
| `TerrainWeights` | The paint channels, and the sum-to-one invariant with a checker that names the offender |
| `TerrainHoles` | One bit per sample. A hole kills the up-to-four quads that reference it |
| `TerrainSculpt` | Sculpt · smooth · flatten · ramp · erode · hydro · noise · holes |
| `TerrainPaint` | The four paint tools over one target layer, all of them through the invariant |
| `TerrainLayerDescription` | What a `.vxlayer` holds: textures by name, tiling in metres, blend mode, physics material |
| `TerrainWeightStroke` | A paint drag as one undoable command — every layer's weights, because painting one moves them all |
| `TerrainWeightmap` | One layer's coverage as an 8-bit mask, in and out |
| `TerrainStroke` | One drag as one undoable command, holding the rect it touched before and after |
| `TerrainBrush` | A radius in metres, a strength, a falloff fraction and curve, a shape, a spacing and a rotation mode |
| `BrushFalloff` | The four curves — smooth, linear, spherical, tip — as arithmetic on one number |
| `BrushStroke` | A drag, accumulated one pointer move at a time into evenly spaced stamps |
| `IBrushMask` | Where a masked brush reads its weights. A function from the unit square, so this assembly needs no image type |
| `TerrainPick` | A ray against the composited heightfield, and the bilinear height under a point |
| `TerrainResize` | Rebuilding a terrain against a new shape, carrying across everything that overlaps |
| `TerrainSpline` | Roads: deform into the reserved Splines layer, paint along the width, place meshes along the length |
| `TerrainSplineProfile` | A half-width, a cosine shoulder per side, a strength and a depth |
| `TerrainMips` | The per-tile height mip chain, reduced by the *maximum* so a ridge survives |
| `PatchSelector` | The CDLOD descent itself, over anything that can answer `IPatchSource` |
| `IPatchSource` | The two questions a descent asks a square of ground: how tall is it, and is there anything there |


## One quadtree, not two

`PatchSelector` is `TerrainLodTree`'s descent with the terrain taken out of it, and water is its
second consumer — [`docs/plan/35 § D4`](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source).
Unreal has a landscape LOD system and a water LOD system, with two morphs, two sets of bias cvars and
two ways to get a crack.

The extraction is worth stating because of what it turned out to be: the selection, the morph, the
no-crack property and the continuity property are all functions of a node's extent and the view's
distance, and **none of them is a function of what the height came from**. What is left in
`TerrainLodTree` is exactly the part that is about a terrain — where its samples are, how tall it is,
and how a morphed grid index becomes a heightmap lookup.

⚠ **`IPatchSource.Covers` must be conservative.** Pruning happens before a node's children are
visited, so a source that answers from the node's centre removes a shoreline running through a
corner — and the water ends in a straight edge halfway across a tile. Over-reporting costs a patch
that draws nothing; under-reporting is a hole.

⚠ **Implement it as a `readonly struct`.** `Select` takes it as a generic parameter rather than as an
interface reference, so a struct source is called through a constrained call and a selection allocates
nothing — which matters because this runs once per frame per terrain and per zone.


## The mip chain reduces by the maximum

⚠ **An averaged mip sinks a ridge.** Four samples of which one is a peak average to a quarter of it, so
a mountain gets shorter every level and the silhouette a distant patch draws is not the mountain's. A
maximum keeps the ridge and raises the valleys, which errs towards geometry being *above* where it
should be — the direction that hides a crack rather than opening one, and the direction the collision
approximation is already conservative in.

⚠ **A tile is a power of two *plus one* samples, so a level is not half its parent.** 129 → 65 → 33
keeps the boundary sample on the boundary; halving the count instead drops the last row, and the seam
it opens is one texel wide and permanent. Each tile reduces its own copy of the shared row, so two
tiles agree by construction.

## Roads are the reserved layer, and that is the whole of their non-destructiveness

`Terrain.ReservedLayer(kind)` is the accessor all three generators share, and it refuses
`TerrainLayerKind.Manual`: an author's layers are many and named by the author, so there is no such
thing as *the* manual layer. There is one layer per generator and not one per thing generated — two
roads crossing have to agree about the height at the junction, and two layers would give the answer to
whichever composited last.

The third kind, `TerrainLayerKind.Water`, is [`docs/plan/35-water.md`](../../docs/plan/35-water.md)
§ B4 and needed no change to the contract at all — which is the strongest evidence [§ D4] got the
storage model right: the feature that most obviously wants non-destructive terrain deformation was not
in scope when the mechanism was designed.

`TerrainSpline` writes into a `TerrainLayerKind.Splines` edit layer — [§ D4] — so moving a road,
narrowing it or deleting it re-runs into the same layer and the author's own sculpting underneath is
untouched. A road written into the base heightfield is a road that can never be moved.

⚠ **`Regenerate`, not `Deform`, is what an editor calls.** `Deform` clears its own rect, which is
enough to add a road to a layer that is otherwise correct and *not* enough when a road moves out of
that rect — the old one stays behind. `Regenerate` empties the layer, lays every road down again, and
invalidates the chunks the layer had already allocated so the cached composite does not keep the old
road either.

⚠ **A road's width is measured across the ground, not through the air.** `Spline.DistanceTo` is 3-D,
which is right for a camera; used here it means a centreline can only deform ground it is already
level with, so a causeway drawn twenty metres above a valley floor touches nothing at all.
`TerrainSpline.Nearest` is the horizontal search, and cutting and filling is the whole point.

⚠ **Every sample within reach is visited once**, from the curve's own bounding box — which covers the
*curve* and not only the control points, because a Hermite segment leaves the hull of its endpoints
whenever the tangents are long. Walking the curve and stamping a brush instead double-counts wherever
two stamps overlap, so the road comes out deeper round its corners than along its straights.

## Six things worth knowing

**The sample count is the power of two, not the quad count.** A tile of 128 samples spans 127 quads,
and 129 samples is rejected. Jolt's height field needs the sample count to be a multiple of its block
size *and* the block count to be a power of two, and it reports a violation by returning nothing at
all. Unreal states the same constraint from the other end, as sections that are "a power of two value
minus one" quads.

**Tile boundaries cannot be duplicated, because there is only one copy.** The terrain is one grid and
a tile is a window into it, so a boundary sample has one home. Storing per-tile grids would make
"tiles share their boundary row" a rule every tool has to remember, and the seam it produces appears
only after somebody edits one side.

**The composite is derived, and the cache is checked against the definition.** `Terrain.CompositeAt`
is what the world *is*; `Terrain.Composite` is a cache of it, per tile, invalidated by rect. The
load-bearing test walks every sample and compares the two — a stale cache looks perfectly reasonable,
it is just old.

**A paint undo holds every layer, and restoring it is one assignment.** Painting one layer lowers the
rest proportionally, so a record of the target channel alone restores a state whose sum is wrong — and
putting six layers back one at a time redistributes six times, so the first five are moved again by
the sixth. `TerrainWeights.Restore` writes a whole sample at once and is the only spelling that lands
back on what was read.

**A kernel reads the composite and writes a layer.** Reading the layer gives erosion that erases
everything below it; writing the composite gives an edit the next invalidation discards. Flattening a
building pad on a layer above a mountain flattens the mountain and leaves the mountain layer intact.

**Record before you apply.** `TerrainStroke.Record(brush, stamp)` computes the footprint itself, so
the wrong order is not expressible. `Extend` takes a rect and stays public for the ramp, whose
footprint is not a stamp — and its remarks say what happens if you hand it the kernel's return value.

**One brush, three consumers.** Sculpt strength, paint weight and foliage density over a falloff are
the same function applied to different targets, so a soft edge sculpted at strength 0.3 and a soft
edge painted at strength 0.3 are the same shape. Unreal implements them three times and they are not.
`Falloff` is the fraction of the radius that falls off, not where the falloff starts; a stroke is
spaced by distance rather than by pointer event; and its random rotation is a hash of the stamp index,
so a stroke can be undone and redone to the same result.

**A pick reads the definition, not the cache.** `TerrainPick.HeightAt` goes through
`Terrain.CompositeAt` rather than `Terrain.Composite`, because a pick happens in the middle of a drag
— which is exactly when the cache is stale. Reading the cache aims every stamp of a stroke at the
ground the stroke started from, so a brush digs a hole and then stops following the surface down it.

**And it intersects the bilinear surface, not the triangles that are drawn.** Which two triangles a
quad is split into depends on the LOD level the patch was selected at, so "the triangle under the
pointer" would give a different answer at two distances from the camera. The bilinear surface passes
through all four corner samples, so it agrees with the mesh wherever the mesh has a vertex.

**Resizing copies by sample index; changing the height range is the one thing that rescales.** Sample
(x, z) becomes sample (x, z), so a change of `MetresPerQuad` makes the same landscape physically
larger rather than resampling it — that is `TerrainHeightmap.Import`'s job and it cannot preserve an
edit layer's deltas anyway, because a delta between two samples is not a delta at either of them. A
range change preserves *metres*, and a delta scales by the ratio of the two ranges rather than through
`StoreHeight`: the absolute conversion adds the old minimum and subtracts the new one, which turns
every edit layer into a uniform offset of the whole terrain.

## The seam to physics

`Vixen.Terrain` cannot name a `ShapeDescription` — one project reference, and it is not to
`Vixen.Physics`. What the two agree about is an array of floats and a sentinel, which is what a
height field *is*: `TerrainSamples.FillCollisionSamples` fills a tile's samples in metres with holes
written as the caller's no-collision value. See [§ D10](../../docs/plan/31-terrain-grass-and-trees.md).
