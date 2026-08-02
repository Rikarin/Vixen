---
title: Sculpting a heightfield
slug: engine/terrain-sculpting
kind: guide
area: Engine
summary: The seven sculpt kernels plus holes and paint, the stroke record that makes a drag undoable, and the ray that turns a pointer into a sample.
api: [T:Vixen.Terrain.TerrainSculpt, T:Vixen.Terrain.FlattenDirection, T:Vixen.Terrain.TerrainNoise, T:Vixen.Terrain.TerrainStroke, T:Vixen.Terrain.TerrainStrokeRedo, T:Vixen.Terrain.TerrainPick, T:Vixen.Terrain.TerrainHit]
tags: [terrain, sculpt, erosion, undo, picking]
since: 0.1
status: preview
related: [engine/terrain-heightfield, engine/terrain-brushes, editor/terrain-mode]
---

## What it is

`TerrainSculpt` is the kernels: sculpt, smooth, flatten, ramp, erode, hydro, noise, plus holes and
paint. Each takes a terrain, a layer, a brush and a stamp, changes the ground, and returns the samples
it wrote. `TerrainStroke` is the undo record a drag builds; `TerrainPick` is the ray that turns a
pointer into a place on the ground.

**Pure functions over a terrain and a stamp.** No document, no undo, no dirty tracking — the caller
owns those, because it is the caller that knows whether this stamp is one of four hundred in a drag.

## What it is for

Everything an artist does to the ground, as arithmetic with a right answer. You want these directly
when generating terrain from code — a level built from a seed, a test fixture, a tool that carves a
road from a spline. In the editor they are reached through `TerrainEdit`, which adds the stroke
lifecycle and the undo entry.

## Using it

```csharp no-compile="a fragment; the stamp comes from a brush stroke over the ground"
var stroke = new TerrainStroke(terrain, layer);

stroke.Record(brush, stamp);
TerrainSculpt.Sculpt(terrain, layer, brush, stamp, metres: 2f);

terrain.Resolve();
```

⚠ **Every kernel reads the composite and writes a layer**, and that pairing is the whole design.
Eroding a mountain on a layer above the base has to read what the world *is*, so the flow is right,
and write what this layer *adds*, so the base survives. Reading the layer instead gives erosion that
erases everything below it; writing the composite gives an edit the next invalidation discards.

⚠ **Smooth reads a snapshot, not the terrain it is writing.** Smoothing in place makes the result
depend on the order the samples are visited — the second sample averages a neighbour the first has
already moved — which is a directional smear showing up as a ridge running diagonally across every
smoothed area.

⚠ **Erosion is one pass per call, not a loop with a progress bar.** A stroke is many calls and an
artist holding the brush down is what "more erosion" means. It is also why erosion and hydro are in
the first sculpt phase rather than a later one: they are the two tools most responsible for a terrain
not looking like a heightfield.

⚠ **The hydraulic kernel is not mass-conserving and does not claim to be.** A true solver carries
sediment in water and needs a second field, a time step and a stability condition; this is a brush an
artist holds down. The moment somebody asks for the solver, it has failed.

⚠ **`FlattenDirection` is Unreal's setting and worth copying literally.** Cutting a building pad into
a hillside means `Lower`; filling a dip in a road means `Raise`. `Both` also fills the shoulder of a
cut pad, which is usually not what was meant — and a one-directional flatten to a plane taken at the
start of a stroke is exactly what a clay brush is.

`TerrainNoise` is value noise rather than gradient noise, for one reason: **the range of value noise
is exactly the range of its lattice**, so an amplitude declared as three metres never exceeds three
metres. A gradient-noise peak is a number you look up and hope for, and an artist sculpting near a
building cannot work with "three metres, except occasionally".

## The stroke record

Pointer down, drag, pointer up is one entry holding the layer it targeted, the union of the rectangles
it touched, and that rectangle's deltas before and after.

⚠ **Record before you apply, and `Record` is what makes the wrong order inexpressible.** A record
holds what the ground *was*, so it has to be taken before the kernel runs — and a caller who fetches
the rectangle from the kernel's return value can only take it afterwards, which records what the
kernel wrote and produces an undo that restores the stroke it was supposed to remove. `Record`
computes the footprint itself. `Extend` takes a rectangle and stays public for the ramp, whose
footprint is not a stamp.

⚠ **The recorded rectangle is the one the kernel *read*, not the one it wrote.** Smoothing and erosion
read a sample beyond their footprint, so a record sized to the write restores a rectangle whose border
still holds post-stroke values — and the next smooth over the same place pulls them back in. `Extend`
grows by `TerrainSculpt.NeighbourMargin` for that reason.

⚠ **The before-image is captured lazily and never re-captured.** A drag crossing the same ground forty
times records it once, holding the value it had before the first crossing. It is also what lets a
two-point tool preview itself: undo the stroke, extend over the new region, reapply.

`Capture` takes the after-image at pointer-up rather than accumulating it, because it is only ever
needed once and building it as the stroke ran would double the record's cost for a stroke nobody
undoes — which is almost all of them.

## Picking

```csharp no-compile="a fragment; the ray comes from a camera and a pixel"
if (TerrainPick.Cast(terrain, ray.Origin, ray.Direction, out var hit)) {
    var stamp = new BrushStamp(hit.Ground);
}
```

⚠ **`HeightAt` reads `CompositeAt`, the definition, and not `Composite`, the cache.** A pick happens
in the middle of a drag, which is exactly when the cache is stale — a stamp invalidates the tiles it
touched and `Resolve` runs once a frame — so reading the cache aims every stamp of a stroke at the
ground the stroke started from. That reads as a brush that digs a hole and then stops following the
surface down it.

⚠ **It intersects the bilinear surface, not the triangles that are drawn.** Which two triangles a quad
is split into depends on the LOD level the patch was selected at, so "the triangle under the pointer"
would give a different answer at two distances from the camera. The bilinear surface passes through
all four corner samples, so it agrees with the mesh wherever the mesh has a vertex and differs inside
a quad by at most the sag of the diagonal.

⚠ **A ray that starts underground hits at its own origin.** Marching until it comes out again aims the
brush at whatever is on the far side of the hill the camera is inside.

⚠ **Holes are ignored.** A hole is a bit on the visibility mask and the heights beneath it are still
there; a pick that refused to answer over one would make the hole tool unable to take a hole back out.

## Examples

A whole drag, which is what the editor does forty times a second:

```csharp no-compile="a fragment; the positions come from a pointer over the ground"
var stroke = new TerrainStroke(terrain, layer);
var path = new BrushStroke(brush);
var stamps = new List<BrushStamp>();

path.MoveTo(where, stamps);

foreach (var stamp in stamps) {
    stroke.Record(brush, stamp);
    TerrainSculpt.Erode(terrain, layer, brush, stamp, talus: 0.5f, rate: 0.5f);
}

terrain.Resolve();
```

⚠ **`Record` before the kernel, every time.** It is the one ordering here that is easy to get wrong
and impossible to notice until somebody presses undo.

Cutting a building pad into a hillside, which is what the direction is for:

```csharp no-compile="a fragment; the target is a height the designer chose"
TerrainSculpt.Flatten(terrain, layer, brush, stamp, target: 12f, direction: FlattenDirection.Lower);
```

Ridged noise, which is the toggle that turns hills into mountains:

```csharp no-compile="a fragment"
var settings = new TerrainNoise(Octaves: 5, Frequency: 0.02f, Ridged: true);

TerrainSculpt.Noise(terrain, layer, brush, stamp, amplitude: 30f, settings);
```

## See also

- [The terrain heightfield](terrain-heightfield.md) — the layers these write and the composite they read.
- [Terrain brushes](terrain-brushes.md) — where a stamp and its weights come from.
- [Sculpt mode](../editor/terrain-mode.md) — the editor half: the drag, the undo entry, the colliders.
- [docs/plan/31 § D11](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  why a stroke is one command and it stores a rect.
