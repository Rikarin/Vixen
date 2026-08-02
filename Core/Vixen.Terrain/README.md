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
| `TerrainSculpt` | Sculpt · smooth · flatten · ramp · erode · hydro · noise · holes · paint |
| `TerrainStroke` | One drag as one undoable command, holding the rect it touched before and after |
| `TerrainBrush` | A radius in metres, a strength, a falloff fraction and curve, a shape, a spacing and a rotation mode |
| `BrushFalloff` | The four curves — smooth, linear, spherical, tip — as arithmetic on one number |
| `BrushStroke` | A drag, accumulated one pointer move at a time into evenly spaced stamps |
| `IBrushMask` | Where a masked brush reads its weights. A function from the unit square, so this assembly needs no image type |
| `TerrainPick` | A ray against the composited heightfield, and the bilinear height under a point |
| `TerrainResize` | Rebuilding a terrain against a new shape, carrying across everything that overlaps |

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
