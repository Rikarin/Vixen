---
title: Painting a terrain
slug: engine/terrain-painting
kind: guide
area: Engine
summary: The four paint tools, the layer weights that sum to one, the reusable ground a layer names, and the undo record that has to hold all of them.
api: [T:Vixen.Terrain.TerrainPaint, T:Vixen.Terrain.TerrainWeightStroke, T:Vixen.Terrain.TerrainWeightRedo, T:Vixen.Terrain.TerrainWeightmap, T:Vixen.Terrain.TerrainLayerDescription, T:Vixen.Terrain.TerrainLayerBlend]
tags: [terrain, painting, layers, weights, splat]
since: 0.1
status: preview
related: [engine/terrain-heightfield, engine/terrain-brushes, engine/terrain-sculpting, rendering/terrain-rendering, editor/terrain-mode]
---

## What it is

`TerrainPaint` is the four tools an artist paints ground with: paint, smooth, flatten and noise, each
over one target layer. `TerrainLayerDescription` is what a `.vxlayer` holds — the textures, the tiling
in metres, the blend mode, the physics material. `TerrainWeightStroke` is the undo record a drag
builds, and `TerrainWeightmap` reads and writes one layer's coverage as a grayscale image.

## What it is for

Deciding where the grass stops and the rock starts. Everything here writes
`TerrainWeights`, so the sum-to-one invariant is maintained by construction rather than by each tool
remembering to.

You do not want these for a ground that never changes — one material on the whole terrain needs no
layers at all, and the generated splat material compiles a loop of one.

## Using it

```csharp no-compile="a fragment; the stamp comes from a brush stroke over the ground"
var stroke = new TerrainWeightStroke(terrain);

stroke.Record(brush, stamp);
TerrainPaint.Paint(terrain, layer: 1, brush, stamp, amount: 64);
```

⚠ **Every kernel here goes through `TerrainWeights.Paint` rather than writing a channel.** That method
is where the invariant lives — raise one layer and the rest come down in proportion, by largest
remainder — and a kernel that wrote the byte itself would be a second implementation of the rule,
disagreeing with the first by a unit or two per sample. Which is exactly the drift `Verify` reports
and nobody can explain.

⚠ **The undo record holds *every* layer at every sample, not the one that was painted.** Painting one
lowers all the others, so restoring a single channel leaves the rest holding what the redistribution
gave them — the sum comes out above 255 at every touched sample, and the drift surfaces three
operations later.

⚠ **And restoring it needs `TerrainWeights.Restore`, not six `SetWeight` calls.** Setting six layers
back one at a time redistributes six times, so the first five are moved again by the sixth and the
undo lands *near* where the stroke started rather than on it.

⚠ **Smooth reads a snapshot.** Worse here than for heights: a paint write moves every layer at the
sample, so smoothing in place would average against weights the redistribution had already changed
twice.

## The layer asset

⚠ **Textures are named, not handled.** The kernel has no device and no asset database, and a
`.vxlayer` is read by a world that has not run yet — the same choice `TerrainComponent.Terrain` makes.

⚠ **The tiling is in metres of world, not repeats per terrain.** Repeats-per-terrain is the spelling
that makes a layer stop being reusable, and getting it wrong is the mistake in Unreal's own
quick-start guide's troubleshooting section.

⚠ **"Blend mode" means two different things.** `TerrainBlend` is a *storage* question — whether the
layer takes part in the sum-to-one budget, which is the snow case — and `TerrainLayerBlend` is a
*shading* one: weight, height or alpha. A layer is routinely weight-blended in storage and
height-blended in shading.

⚠ **A height blend with no surface texture is refused rather than degraded.** There is nowhere to read
the height from, so it would silently become a weight blend — the class of failure reported as "the
height blending does not work".

## Import and export

```csharp no-compile="a fragment; the mask came from an image editor"
TerrainWeightmap.Import(terrain, layer: 2, mask, width: 1024, height: 1024);
```

⚠ **An import restores the invariant rather than trusting the file.** A mask painted elsewhere has no
idea the other layers exist, so it goes through the same redistribution painting it by hand would
have — and it resamples edge to edge, because a terrain of four 128-sample tiles is 509 across and
image editors make 512s.

## Examples

The whole of a paint drag, which is what the editor does:

```csharp no-compile="a fragment; the positions come from a pointer over the ground"
var stroke = new TerrainWeightStroke(terrain);
var path = new BrushStroke(brush);
var stamps = new List<BrushStamp>();

path.MoveTo(where, stamps);

foreach (var stamp in stamps) {
    stroke.Record(brush, stamp);
    TerrainPaint.Smooth(terrain, layer, brush, stamp);
}
```

A layer that lies over the others rather than taking from them:

```csharp no-compile="a fragment"
var snow = terrain.Weights.AddLayer(
    TerrainLayerDescription.Of("Snow") with { Albedo = "Textures/snow", TilingMetres = 6f },
    TerrainBlend.NonWeight
);
```

⚠ **A non-weight-blended layer is excluded from the sum by design**, which is what lets snow cover
whatever is underneath instead of replacing it — and it is why the material normalises by the total
it actually accumulated rather than dividing by one.

Asking what ground a place is, which is what a footstep does:

```csharp no-compile="a fragment"
var ground = terrain.Weights.GroundAt(x, z);
```

## See also

- [The terrain heightfield](terrain-heightfield.md) — where the weights live, and the invariant itself.
- [Terrain brushes](terrain-brushes.md) — the stamp and the falloff all four tools share.
- [Drawing a terrain](../rendering/terrain-rendering.md) — the generated material these feed.
- [docs/plan/31 § D5](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  why the weights sum to one, and why the layer that broke it is named.
