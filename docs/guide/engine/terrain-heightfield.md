---
title: The terrain heightfield
slug: engine/terrain-heightfield
kind: guide
area: Engine
summary: One grid of 16-bit heights over an authored range, a stack of non-destructive edit layers, the paint channels and the hole mask.
api: [T:Vixen.Terrain.Terrain, T:Vixen.Terrain.TerrainDescription, T:Vixen.Terrain.TerrainRect, T:Vixen.Terrain.TerrainSamples, T:Vixen.Terrain.TerrainEditLayer, T:Vixen.Terrain.TerrainLayerKind, T:Vixen.Terrain.TerrainWeights, T:Vixen.Terrain.TerrainBlend, T:Vixen.Terrain.TerrainHoles, T:Vixen.Terrain.TerrainHeightmap, T:Vixen.Terrain.TerrainHeightmapFormat, T:Vixen.Terrain.TerrainResize, T:Vixen.Physics.Shapes.HeightFieldPlacement, T:Vixen.Terrain.TerrainMips, T:Vixen.Terrain.TerrainHeightmapPng, T:Vixen.Terrain.TerrainHeightmapImage, T:Vixen.Terrain.TerrainStore]
tags: [terrain, landscape, heightfield, layers]
since: 0.1
status: preview
related: [engine/terrain-brushes, engine/terrain-sculpting, rendering/terrain-rendering, editor/terrain-mode, engine/splines]
---

## What it is

`Terrain` is a heightfield and everything stored beside it: a base grid of 16-bit samples, a stack of
`TerrainEditLayer` deltas, the `TerrainWeights` paint channels, the `TerrainHoles` visibility mask, and
a per-tile cache of the composite the three of them add up to. `TerrainDescription` is its shape —
tile size, tile counts, metres per quad, the height range in metres — and every derived number a
create dialog needs. `TerrainRect` is a rectangle of samples, which is the unit almost everything here
speaks in.

**No device, no document, no editor.** One project reference, to `Vixen.Core.Mathematics`. Everything
is a function over arrays, so the tests need no world and run in milliseconds.

## What it is for

A landscape an artist sculpts and a game walks on. You want it when the ground is the level rather
than a mesh in it: a heightfield draws in one call at any resolution, collides as one Jolt shape per
tile, and can be edited in place without re-authoring anything.

You do not want it for a cliff face with an overhang, a cave, or anything with two surfaces above the
same point — a heightfield is a function of X and Z and cannot represent one. That is a mesh, and
`TerrainHoles` is how the two meet: punch the ground out and put the mesh in the gap.

## Using it

A terrain is its description and a fill height:

```csharp no-compile="a fragment; the description normally comes from a create form"
var terrain = new Terrain(
    new TerrainDescription {
        TileSamples = 128, TilesX = 4, TilesZ = 4,
        MetresPerQuad = 1f, MinHeight = -256f, MaxHeight = 256f
    }
);

var sculpt = terrain.AddLayer("Sculpt");
```

⚠ **The power of two is the sample count, not the quad count.** A tile of 128 samples spans 127
quads, and 129 samples — the round-sounding "128 quads" — is refused. Jolt's height field needs the
sample count to be a multiple of its block size *and* the block count to be a power of two, and it
reports a violation by returning nothing at all. Unreal states the same constraint from the other end,
as section sizes that are "a power of two value minus one" quads.

⚠ **Tile boundaries are shared samples, not adjacent ones**, and the storage is what makes that
structural. `TerrainSamples` is one grid covering the whole terrain and a tile is a *window* into it,
so a boundary sample has one home and cannot be written twice. Storing per-tile grids makes "tiles
share their boundary row" a rule every tool has to remember, and the seam it produces appears only
after somebody edits one side.

⚠ **The height range is authored, and it is not a stylistic choice.** Unreal's fixed −256…255.992
window is a compatibility artefact of a 1998 file format; a range the author sets means a 40 m rolling
landscape gets 0.6 mm of vertical precision instead of 8 mm for the same bytes.
`TerrainDescription.MetresPerStep` is what a create dialog puts on screen so that asking for a 20 km
range is a decision rather than a surprise.

## Edit layers

The stack is the storage model rather than a feature on top of one. Each layer holds signed deltas,
sparse in 64-square chunks, with a signed height alpha and a weight alpha; the composite is the base
plus every visible layer's deltas scaled by its alpha, clamped to the range.

```csharp no-compile="a fragment; the deltas normally come from a sculpt kernel"
var pads = terrain.AddLayer("Building pads");

pads.HeightAlpha = 0.5f;
pads.IsVisible = false;

terrain.InvalidateAll();
terrain.Resolve();
```

⚠ **`CompositeAt` is what the world *is*; `Composite` is a cache of it.** The cache is per tile and
invalidated by rectangle, so a stroke marks a tile and `Resolve` recomputes it once. Anything that
reads the ground *during* an edit — a pick, a second stamp of the same drag — has to go through
`CompositeAt`, because the cache is stale exactly then.

⚠ **Reordering changes the result even though addition commutes.** The deltas add in any order, but
the composite *clamps* to the height range, and a clamp is not commutative — a layer that pushes past
the ceiling loses what it pushed past, and which layer that is depends on the order. So `MoveLayer`
invalidates everything rather than being a no-op on a stack of sums.

⚠ **A reserved layer refuses the brush.** `TerrainLayerKind.Splines` and `Scatter` are regenerated
wholesale by whatever owns them, so a hand edit would be discarded the next time a spline moved —
silently, and an hour later. `AcceptsBrush` is what a tool checks, and the panel greys the row and
names the generator.

⚠ **`Collapse` is destructive and has no inverse worth computing.** It adds a layer's deltas into the
one below and drops it; the lower layer may already have held something at every sample. An undoable
collapse takes a `Clone` of the lower layer first — which is what `TerrainLayerCommands.Collapse`
does.

## Weights and holes

`TerrainWeights` is the paint channels, one byte per layer per sample, with the invariant that they
sum to 255 at every sample. Painting one layer redistributes the rest proportionally by largest
remainder, so the total is exact rather than nearly right.

```csharp no-compile="a fragment"
var grass = terrain.Weights.AddLayer("Grass");
var rock = terrain.Weights.AddLayer("Rock", TerrainBlend.Height);

terrain.Weights.Paint(rock, x: 40, z: 40, amount: 80);
```

⚠ **`Verify` names the offending sample and layer.** An invariant that reports "the weights are
wrong" is one nobody can act on; the failure is always a specific sample and the layer holding the
most of it, and saying so turns a bug report into a fix.

`TerrainHoles` is one bit per sample. A hole kills the up-to-four quads that reference it, in the
index buffer and in the collision shape both — which is why the hole tool rebuilds colliders even
though no height moved.

## Import, export and resize

```csharp no-compile="a fragment; the bytes come from a file the artist chose"
TerrainHeightmap.Import(terrain, layer, raw, new TerrainHeightmapFormat(1024, 1024));
```

⚠ **Import writes a layer, not the base.** A terrain imported from World Machine can then be sculpted
on top of without being destroyed, and re-imported without losing the sculpt.

⚠ **Resampling turned out not to be optional.** A terrain of four 128-sample tiles is 509 samples
across and heightmaps come out of World Machine at 512, 1024 and 2049 — they essentially never match.
The resample is bilinear and edge-to-edge; mapping by scale factor instead leaves a flat lip along two
sides of every imported terrain, which is subtle enough to ship.

`TerrainResize` rebuilds a terrain against a new description, carrying across everything that
overlaps — the base, every layer's deltas and flags, the weights and the holes.

⚠ **It copies by sample index, and changing the height range is the one thing that rescales.** A
change of `MetresPerQuad` makes the same landscape physically larger rather than resampling it. A
change of range preserves *metres*, and a delta scales by the ratio of the two ranges rather than
through `StoreHeight`: the absolute conversion adds the old minimum and subtracts the new one, which
turns every edit layer into a uniform offset of the whole terrain.

## The seam to physics

This assembly cannot name a `ShapeDescription` — one project reference, and it is not to
`Vixen.Physics`. What the two agree about is an array of floats and a sentinel, which is what a height
field *is*:

```csharp no-compile="a fragment; the caller owns the buffer and the sentinel"
terrain.Base.FillCollisionSamples(terrain, tileX, tileZ, heights, PhysicsShapes.NoCollisionHeight);
```

`HeightFieldPlacement` is the other side of it: how many samples a side, where the grid's corner is
and what one step of it spans. `PhysicsShapes.HeightField` takes the two together.

⚠ **A height field is static and `CanBeDynamic` says so.** Jolt has no inertia tensor for one, and a
terrain that could be given a rigid body is a terrain somebody will give a rigid body.

⚠ **Jolt wants at least two collision blocks per axis, and it does not document that.** The block size
is capped at half the grid for exactly this — an 8-sample tile with a block size of 8 returns a null
shape with no error anywhere.

## The file it is written as

`TerrainStore` is the `.vxterrain` format: the description, the base heightfield, the edit layers,
the paint channels and the holes. Binary, because a 4 km² terrain is tens of megabytes of samples and
a scene file is the one two people touch every day.

⚠ **The layers are stored and the composite is not.** A composite is a cache; the layer stack is the
definition, and writing both would be writing a number twice and guaranteeing they disagree the first
time somebody edits the file. Reading recomposites, which is the same code the editor runs.

⚠ **Only the chunks a layer has touched.** An edit layer over a 4 km² terrain that somebody sculpted
one hill into is sixteen million zeroes and a hundred thousand numbers — storing the zeroes is what
makes a stack of layers unaffordable.

⚠ **Holes are coordinates, not a mask.** A terrain with three holes in it is the normal case, and a
bitmask over sixteen million samples is two megabytes to say "three".

⚠ **A version, first, and it is checked.** A heightfield read with the wrong field order is not a
parse error — it is a terrain that loads and looks like static, and the person seeing it has no reason
to suspect the format.

## Importing and exporting heights

Raw `.r16` is lossless and carries no header, so its size and endianness are settings.
`TerrainHeightmapPng` is the other half: sixteen-bit greyscale PNG, which is what every terrain
generator writes.

⚠ **Sixteen bits, and eight is refused rather than widened.** `Vixen.Core.Imaging`'s decoder reads
eight bits a channel, which is right for a texture and wrong for a heightfield: a terrain quantised to
256 heights is a faint terrace on every slope, and it gets blamed on the generator.

⚠ **Greyscale only, and a colour PNG is refused rather than averaged.** There is no defensible way to
turn three channels into one height — a luminance weighting is a photographic convention and a
heightfield is not a photograph.

⚠ **Every filter type decodes and one is encoded.** A file this writes is one we control; a file it
reads came from World Machine, Gaea or Photoshop, and each picks filters per row.

⚠ **PNG is big-endian and always has been**, which makes it the one heightmap import that cannot be
asked about endianness — and the one place a raw `.r16` always must be.

⚠ **A PNG's size overrides the import settings.** That is the whole reason to prefer it over raw:
somebody who filled the form in for a `.r16` and then imported a `.hmpng` must not get whichever
answer the form happened to hold.

## Examples

A terrain sized so that a create dialog would not have to warn about it — four tiles of 128 samples
at a metre a quad is 508 m square, 1.0 MB of heights and sixteen collision shapes:

```csharp no-compile="a fragment; the numbers are what a create form collects"
var description = new TerrainDescription {
    TileSamples = 128, TilesX = 4, TilesZ = 4,
    MetresPerQuad = 1f, MinHeight = -40f, MaxHeight = 40f
};

// 508 × 508 m, 1.0 MB of heights, 0.6 mm per step.
var terrain = new Terrain(description, height: 0f);
```

A layer hidden and shown again, which changes nothing on disk and everything on screen:

```csharp no-compile="a fragment; `pads` is a layer of the terrain above"
pads.IsVisible = false;
terrain.InvalidateAll();
terrain.Resolve();
```

⚠ **Invalidate, then resolve.** The composite is a cache and nothing recomputes it on read — a
visibility change that skipped the invalidation leaves the layer contributing until something else
happens to dirty the same tiles.

Growing a terrain by a ring of tiles, keeping everything on it:

```csharp no-compile="a fragment"
var larger = TerrainResize.WithTiles(terrain, tilesX: 6, tilesZ: 6, fill: 0f);
```

## Mip levels

⚠ **Reduced by the *maximum*, not the average.** An averaged mip sinks a ridge: four samples of which
one is a peak average to a quarter of it, so a mountain gets shorter every level and the silhouette a
distant patch draws is not the mountain's. A maximum keeps the ridge and raises the valleys, which
errs towards geometry being *above* where it should be — the direction that hides a crack rather than
opening one.

⚠ **A tile is a power of two *plus one* samples, so a level is not half its parent.** 129 → 65 → 33
keeps the boundary sample on the boundary; halving the count instead drops the last row, and the seam
it opens is one texel wide and permanent. Each tile reduces its own copy of the shared row, so two
tiles agree by construction.

⚠ **Level 0 is read from the composite cache, so the caller has to have resolved it.** Reading the
definition instead walks the layer stack once per sample per level — a hundred and fifty thousand
walks for a 129-sample tile, to answer a question the cache already holds.

## See also

- [Terrain brushes](terrain-brushes.md) — the one brush every sculpt, paint and scatter tool stamps with.
- [Sculpting a heightfield](terrain-sculpting.md) — the kernels that write these layers, and the stroke record.
- [Drawing a terrain](../rendering/terrain-rendering.md) — the quadtree, the morph and the one draw.
- [Splines](splines.md) — the curve a road or a river carved into this one follows.
- [docs/plan/31 § D2](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  why the terrain is an asset and the tile is the unit of everything.
