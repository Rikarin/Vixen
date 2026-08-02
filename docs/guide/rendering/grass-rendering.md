---
title: Drawing grass
slug: rendering/grass-rendering
kind: guide
area: Rendering
summary: Cells scattered as they come into range and blades culled every frame — the CPU reference of a compute pass, and the seam test that holds the two together.
api: [T:Vixen.Rendering.Terrain.GrassRenderer, T:Vixen.Rendering.Terrain.GrassDraw, T:Vixen.Rendering.Terrain.GrassBatch, T:Vixen.Rendering.Terrain.TerrainSurface, T:Vixen.Rendering.Terrain.GrassDispatch, T:Vixen.Rendering.Terrain.GrassCellRecord, T:Vixen.Rendering.Terrain.GrassInstanceRecord, T:Vixen.Rendering.Terrain.GrassTerrainSource, T:Vixen.Shaders.Generated.GrassScatterKeys, T:Vixen.Shaders.Generated.GrassScatterConstants, T:Vixen.Shaders.Generated.GrassKeys, T:Vixen.Shaders.Generated.GrassConstants, R:Terrain/GrassScatter, R:Terrain/Grass, T:Vixen.Rendering.Terrain.GrassDrawPass, T:Vixen.Rendering.Terrain.GrassBladeMesh, T:Vixen.Rendering.Terrain.FoliageStreamer, T:Vixen.Rendering.Terrain.FoliageCellPages]
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

## The dispatch

`GrassDispatch` is the device half: it writes a `GrassCellRecord` per resident cell, owns the ring of
device buffers, and records the compute pass.

```csharp no-compile="a fragment; the terrain source is the renderer's own textures"
dispatch.Prepare(type, grid, [.. renderer.Residency.Resident], source, densityScale: 1f);
dispatch.Record(commands);
```

⚠ **A cell's run is filed under its *ring slot*, not its place in this frame's list.** A cell keeps
its buffer across frames — that is what the ring is — so filing under the loop index would move every
blade the moment a nearer cell arrived and pushed it down, which reads as the whole field jumping
whenever the camera moves.

⚠ **The counters are zeroed before the dispatch, not after.** A pass that cleared afterwards leaves
the buffer holding last frame's numbers for anything that reads it in between, and what reads it is
the indirect draw. The zeros are *copied* from a host buffer, because a command list can copy and
cannot fill.

⚠ **One counter per cell, not one for the dispatch.** A global head would serialise every invocation
of every cell on one cache line, and would make a cell's run depend on the order the workgroups
retired in.

⚠ **The instance buffer is device-local and never read back.** Staging a blade to the host would
throw away the whole point of scattering on the device.

⚠ **A dispatch that runs out of cells says so.** `Refused` is `GrassResidency.Refused`'s counterpart
one level down: a pass that quietly covered the first 256 of 300 resident cells is a field that stops
at a distance nobody chose.

⚠ **The instance record is forty-eight bytes** — `FoliageStore.InstanceBytes` plus an
`InstanceParameters` — so a seam test compares the two halves' *bytes* rather than an interpretation
of them. `GrassInstanceRecord.Of` packs a blade the CPU reference produced into exactly that.

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

## The draw

`GrassDispatch.RecordDraws` issues one indirect draw per resident cell and **binds nothing** — the
pipeline, the blade mesh and the material belong to a material and that class is a compute dispatch.
`GrassDrawPass` is what binds them: the pipeline built from `Grass.rvn`, set 2, the albedo, and a
`GrassBladeMesh`.

```csharp no-compile="a fragment; the shaders are Grass.rvn's two stages"
pass.Prepare(commands, dispatch, view, type.Wind, time);
pass.Record(commands, dispatch);
```

⚠ **The pass writes the blade's index count into the dispatch's indirect template.** A command whose
`IndexCount` is zero draws nothing however many instances survived the cull, and every host-side
counter still reads healthy — the scatter ran, the cells were resident, the draws were recorded. It
is the one failure in this path that is completely invisible from the host, which is why it is not
left to a caller.

⚠ **Two-sided.** A blade is a flat quad seen from both sides as its instance rotates; culling its
back faces makes half a field vanish depending on where a person stands, which reads as the scatter
being wrong rather than the raster state.

⚠ **The built-in blade is a fallback and has three segments, not one quad.** The vertex stage
displaces by height, so a two-triangle blade bends as a rigid card leaning over — which at any
strength above a breeze reads as the grass being knocked flat rather than swaying.

## Streaming the cells

`FoliageStreamer` and `FoliageCellPages` decide which cells `FoliageCullPass.Upload` writes into the
device buffer. Over a forest of two thousand cells that upload is fifty thousand records rewritten
whenever anything about the volume changes; a streamer makes it the cells a source can reach.

⚠ **A cell outside the streamer's window is uploaded rather than skipped.** The window is the
bounding box of the chunks that exist and is stale only just after somebody has painted beyond it —
so the safe direction is a tree that appears and is then culled normally, not a tree an artist has
just placed and cannot see.

⚠ **`FoliageStreamer.Changed` is what a host re-uploads from.** Without it there is no way to tell an
ordinary frame from one whose resident set moved, so a host re-uploads every frame — which is the
cost the streamer was added to remove, arriving through the other door.

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
