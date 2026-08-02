---
title: Drawing grass
slug: rendering/grass-rendering
kind: guide
area: Rendering
summary: Cells scattered as they come into range and blades culled every frame — the CPU reference of a compute pass, and the seam test that holds the two together.
api: [T:Vixen.Rendering.Terrain.GrassRenderer, T:Vixen.Rendering.Terrain.GrassDraw, T:Vixen.Rendering.Terrain.GrassBatch, T:Vixen.Rendering.Terrain.TerrainSurface]
tags: [grass, rendering, culling, instancing, wind]
since: 0.1
status: preview
related: [engine/grass, engine/foliage, rendering/foliage-rendering, rendering/terrain-rendering]
---

## What it is

`GrassRenderer` holds a ring of blade buffers, scatters a cell into one when it comes into range,
drops it when it goes, and every frame culls what the ring holds into indirect draws. `TerrainSurface`
is what answers "what is the ground here" when the ground is a heightfield.

`GrassScatter.rvn` and `Grass.rvn` are the device forms of the same two things — a compute dispatch
that scatters and a draw that sways.

## What it is for

A field that costs what is on screen and nothing at all on disk. You want it whenever the things
being drawn are too many to have identity: grass, leaf litter, small stones.

You do not want it for anything a designer places by hand. That is [foliage](../engine/foliage.md),
which has an undo record and a file.

## Using it

```csharp no-compile="a fragment; the templates come from the mesh's LOD group"
var fields = new[] { new GrassDraw(meadow, [blade], []) };

renderer.Scatter(fields, new TerrainSurface(terrain), camera.Position);
renderer.Cull(fields, frustum, camera.Position, densityScale: 1f);
```

⚠ **The scatter happens on entry and the cull happens every frame, and keeping those apart is the
whole shape of the feature.** Scattering per frame would probe the surface for every blade of every
cell every frame — the cost the ring exists to pay once. Culling on entry would draw the far half of
every cell for as long as it stayed resident.

⚠ **Residency is per cell and scatter is per cell per field.** One ring, because the cell is the unit
of everything foliage does and a ring per field would probe the same ground several times; but a
field with a 20 m cull distance does not scatter into a cell held resident by a field with an 80 m
one, or the short field pays for the long field's range.

⚠ **More fields than the ring holds is refused rather than truncated.** Which field the tail is
depends on the order somebody happened to list them in, and a field that silently stopped drawing is
not a thing anybody debugs quickly.

## The seam

⚠ **This is the CPU half, and `GrassScatter.rvn` is the other one.** Grass is derived, so there is no
file to compare a device run against: if the two disagree, the field simply differs between a machine
with a compute queue and one without, and nobody can point at a wrong number.

`GrassScatterParityTests` closes that two ways. A **transliteration** computes what the shader
computes, in C#, and compares it to the kernel over thousands of candidates at zero drift — every
stream, the candidate position, the density curve. A **source assertion** says the arithmetic is
still there, which is the failure that actually happens: somebody edits the mixing constants and
every cell scatters a different field.

⚠ **The divisor is the interesting constant.** The host writes `mixed / (float)uint.MaxValue`, and
that cast is not 4294967295 — a `float` has twenty-four bits of mantissa and rounds it up to
4294967296. A shader written with the true maximum agrees to six decimal places and disagrees in the
last bits of every draw, which is a field that is *almost* the same.

What neither catches is a subtly different but similar-looking expression, and the golden screenshot
is what catches that — it needs a GPU.

⚠ **Holes are outside the comparison, and that is stated rather than hidden.** `TerrainSurface`
answers a miss over a missing quad, so the reference grows nothing there and the dispatch grows a
blade standing in a cave mouth. The hole mask is not bound to `Terrain.rvn` either — the drawn
surface drops the *quad* — so closing it lands with the per-tile texture work.

## Wind

⚠ **Through `Displacement.WindPhased`, not a second implementation of it.** Foliage, water and a
material's own displacement all sway; one of them growing its own copy of two sines is how the grass
and the trees in one scene come to move at different speeds in the same gust.

⚠ **The per-instance phase is what stops a clump moving as one object.** The shader's own phase is
`dot(worldPosition.xz, direction)`, which separates two blades a hundred metres apart and does
nothing at all for two that are ten centimetres apart. The offset comes from the blade's own hash and
rides in `InstanceParameters.WindPhase`.

⚠ **It is added to the phase, not to the offset.** Adding it afterwards would displace a blade that
is not moving, which detaches it from its root.

## The surface

`TerrainSurface` is the join `Vixen.Foliage` and `Vixen.Terrain` deliberately leave out of both: the
foliage kernel cannot name a terrain — a roof of moss would then depend on a heightfield — and the
terrain kernel has never heard of a blade of grass.

⚠ **Outside the terrain is a miss, and the explicit bounds test is not redundant.**
`TerrainPick.HeightAt` clamps rather than refusing, which is right for a brush that has to aim
somewhere; used unguarded here it answers for every position in the world, so a field would stretch
to the horizon at the height of the terrain's border.

⚠ **A layer whose name is not in the stack answers a weight of zero.** Nothing grows, which is a
field somebody notices and fixes; answering one would carpet a whole terrain because of a typo.

## Examples

Reading what a frame cost:

```csharp no-compile="a fragment; the counters are what a profiler shows"
// renderer.BladesResident              — what the ring is holding
// renderer.BladesScattered / Dropped   — what the last Scatter moved
// renderer.CellsConsidered / CellsDrawn
// renderer.BladesConsidered / BladesDrawn
// renderer.Draws                       — indirect commands, empty ones included
```

⚠ **A resident cell can survive the frustum and still draw nothing**, when every blade in it is past
the cull distance — [foliage's](foliage-rendering.md) third rejection, and it is more common here
because a cell is held resident to the *largest* field's range.

Dropping everything, which is what a teleport does:

```csharp no-compile="a fragment"
renderer.Reset();
```

## See also

- [Grass](../engine/grass.md) — the type, the scatter and the ring this drives.
- [Drawing foliage](foliage-rendering.md) — the stored half, and the culler both share.
- [Drawing the terrain](terrain-rendering.md) — the heightfield the surface reads.
- [docs/plan/31 § T6](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the phase this is, and the cut line it sits on.
