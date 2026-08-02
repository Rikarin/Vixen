---
title: Grass
slug: engine/grass
kind: guide
area: Engine
summary: A field scattered from a rule and never saved — the density curve, the hash both halves share, and the ring of buffers that makes it cost a fixed amount of memory.
api: [T:Vixen.Foliage.GrassType, T:Vixen.Foliage.GrassWind, T:Vixen.Foliage.GrassBlade, T:Vixen.Foliage.GrassScatter, T:Vixen.Foliage.GrassScatter.Refusal, T:Vixen.Foliage.GrassResidency, T:Vixen.Foliage.GrassSlot, T:Vixen.Foliage.GrassResidencyChange]
tags: [grass, foliage, vegetation, scatter, streaming]
since: 0.1
status: preview
related: [engine/foliage, engine/terrain-painting, rendering/grass-rendering, rendering/foliage-rendering]
---

## What it is

`GrassType` is what a `.vxgrass` holds: a mesh, the terrain layer it reads, a curve from that layer's
painted weight to how dense the field is, and how the blades vary. `GrassScatter` turns one cell of
that rule into blades. `GrassResidency` decides which cells are close enough to hold any.

**Nothing here is loaded and nothing here is saved.** A level names a grass type and a layer, and a
million blades follow from that.

## What it is for

Grass, and anything else you would place ten thousand of and never name individually — small rocks,
leaf litter, wildflowers. The dividing line against [foliage instances](foliage.md) is **density ×
identity**: something a designer moves, deletes, and expects to find tomorrow is stored; something
that follows from a rule is derived.

⚠ **The two toolsets differ because the things differ.** The grass tools change a *rule* — which
layer, how dense, what mesh — and the foliage tools change *instances*. Offering a gizmo on a blade
of grass would offer it on something regenerated every time a cell enters range.

## Using it

```csharp no-compile="a fragment; the surface is whatever the host can probe"
var meadow = GrassType.Of("Meadow") with {
    Mesh = "Meshes/grass",
    Layer = "Grass",
    Density = 16f,
    MinWeight = 0.2f,
    MaxWeight = 0.8f
};

var blades = new List<GrassBlade>();

GrassScatter.Scatter(meadow, cell, grid, surface, blades);
```

⚠ **`Density` is the *candidate* density and never the placed one.** It fixes the grid a cell is
scattered over — which on the device is the dispatch extent, and therefore cannot depend on anything
sampled — and what the painted weight decides is what fraction of those candidates survive. A density
that varied with the weight would mean a dispatch whose size depends on a texture read.

⚠ **The layer is a name, not an index**, for [`FoliageType.LayerFilter`](foliage.md)'s reason:
removing the second of six terrain layers shifts the rest down.

⚠ **A type with no layer grows everywhere, not nowhere.** An unbound type is "all of it", which is
what somebody dragging a new grass type onto a terrain expects to see.

## The curve

`DensityAt` is a smoothstep from `MinWeight` to `MaxWeight`.

⚠ **A smoothstep and not a linear ramp, because a ramp has a corner at each end.** The foot of a
linear ramp is a straight line across the ground wherever the painted weight crosses the threshold —
a visible edge that follows nothing in the terrain. A smoothstep's derivative is zero at both ends,
so the field feathers out.

⚠ **Written out rather than handed to `smoothstep`.** GLSL leaves `smoothstep(e, e, x)` undefined,
and a type whose weight range an artist collapsed to a point is an ordinary thing to author — so both
sides carry the guard and the polynomial.

## The hash

⚠ **Deterministic from the cell coordinate and the candidate slot, never from an iteration order.**
That is what makes a cell re-entering range produce the grass it had, what makes two machines agree,
and what makes the CPU reference comparable to the dispatch at all — the seam test is impossible
against a counter.

⚠ **The cast of a negative coordinate is a reinterpretation of its bits, and it has to be on both
sides.** C#'s `(uint)(-1)` and GLSL's `uint(-1)` are both `0xFFFFFFFF`, which is what makes the four
cells around the origin agree. A hash that took an absolute value would produce a different field on
exactly the quadrant nobody tests.

⚠ **No spacing check, and its absence is the difference from a foliage stroke.** A minimum spacing is
a query against what is already placed, which makes a candidate's fate depend on the order the others
were tested in — and sixty-five thousand parallel invocations do not have an order. The grid *is* the
spacing: a candidate cannot leave its own slot, which is why `Jitter` reaches half a step and not a
whole one.

## The ring

`GrassResidency` gives an entering cell one of a fixed number of pooled slots and takes it back when
the cell leaves, so a field costs a fixed amount of memory whatever the size of the level.

⚠ **Eviction happens further out than creation, and the gap is not decoration.** A camera standing on
a boundary where the two ranges are equal re-scatters that cell every frame — the whole cost of the
feature paid for nothing, and a visible flicker where the verdicts alternate.

⚠ **Distance is measured to the nearest point of the cell, not to its centre.** A 32 m cell whose
near edge is under the camera has its centre 22 m away, so a centre test with a 20 m range would
leave the ground the camera is standing on bare.

⚠ **A ring too small for its range drops the far cells.** The wanted set is filled nearest first, so
what a capacity that ran out loses is the horizon rather than a hole underfoot — and `Refused` says
it happened, because the alternative is a level that quietly stops having grass at a distance nobody
chose.

⚠ **A full ring reclaims from the furthest resident.** Every cell can be inside the eviction range
and still be the wrong set: a view that swung round has a whole new neighbourhood nearer than the one
behind it, and without this the grass arrives seconds after the camera did.

## It costs nothing in any file

A `GrassType` never enters a `FoliageVolume`, so the ordinary way to fail this is the other one: to
mark a *foliage* type `FoliageStorage.Derived`, paint with it, and have the store write it anyway.
`FoliageStore.Persisted` is the one place that reads the flag, and a chunk of a derived type is not
written and not counted.

## Examples

Reading why a cell grew nothing, which is what a panel reports:

```csharp no-compile="a fragment"
var why = GrassScatter.Consider(type, cell, grid, surface, index, 1f, out var blade);

if (why == GrassScatter.Refusal.Density) {
    // the layer is painted, but not enough of it for this candidate
}
```

Thinning a whole field for a scalability setting:

```csharp no-compile="a fragment"
GrassScatter.Scatter(type, cell, grid, surface, blades, densityScale: 0.5f);
```

⚠ **The scalar multiplies the threshold and every candidate keeps its own draw**, so lowering the
setting removes a subset of the blades that were there and moves none of the rest. A slider that
reshuffled would look like a different level.

Bringing the ring up to date, which is one call a frame:

```csharp no-compile="a fragment"
var change = residency.Update(camera.Position, range: 40f);

foreach (var created in change.Created) {
    // scatter into slot `created.Slot`
}

foreach (var evicted in change.Evicted) {
    // hand the buffer back
}
```

⚠ **A difference, not a set** — [`FoliageCollision`](foliage.md)'s reason, and stronger here: what a
caller does with the answer is upload one buffer and return another, and a set would make it diff a
few thousand cells every frame to find the two that moved.

## See also

- [Foliage instances](foliage.md) — the stored half, and why the two are separate.
- [Drawing grass](../rendering/grass-rendering.md) — the ring of blade buffers, the cull and the wind.
- [Painting layers](terrain-painting.md) — the weights a grass type reads.
- [docs/plan/31 § D8](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  grass is derived, trees are stored, and the distinction is the density.
