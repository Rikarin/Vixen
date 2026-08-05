---
title: Drawing foliage
slug: rendering/foliage-rendering
kind: guide
area: Rendering
summary: Cells culled as objects, instances culled within them, LOD decided per instance, and one indirect command per level.
api: [T:Vixen.Rendering.Terrain.FoliageRenderer, T:Vixen.Rendering.Terrain.FoliageDraw, T:Vixen.Rendering.Terrain.FoliageBatch, T:Vixen.Rendering.Terrain.FoliageCullPass, T:Vixen.Rendering.Terrain.FoliageCullInstanceRecord, T:Vixen.Rendering.Terrain.FoliageCullBatchRecord, T:Vixen.Rendering.Terrain.FoliageCullViewRecord, T:Vixen.Shaders.Generated.FoliageCullKeys, R:Terrain/FoliageCull, T:Vixen.Rendering.Terrain.FoliageOccluders, T:Vixen.Rendering.Terrain.FoliageDrawPass, T:Vixen.Rendering.Terrain.FoliageMesh, T:Vixen.Rendering.Terrain.FoliageVolumeComponent, T:Vixen.Rendering.Terrain.FoliageSceneEntry, T:Vixen.Shaders.Generated.FoliageKeys, T:Vixen.Shaders.Generated.FoliageConstants, T:Vixen.Shaders.Generated.FoliageLitKeys, T:Vixen.Shaders.Generated.FoliageLitConstants, T:Vixen.Shaders.Generated.FoliageLitCascadesElement, R:Terrain/Foliage, R:Terrain/FoliageLit, R:Terrain/FoliageVelocity, T:Vixen.Shaders.Generated.FoliageVelocityKeys, T:Vixen.Shaders.Generated.FoliageVelocityConstants, L:14017]
tags: [foliage, rendering, culling, lod, instancing]
since: 0.1
status: preview
related: [engine/foliage, engine/grass, rendering/instance-culling, rendering/grass-rendering, rendering/impostors, rendering/terrain-rendering, editor/foliage-mode]
---

## What it is

`FoliageRenderer` turns a `FoliageVolume` into indirect draws. Two stages: the cell as one object
against the frustum, then each surviving cell's instances against the frustum, their own cull distance
and their LOD level. `FoliageDraw` is the per-type draw templates; `FoliageBatch` is what one cell
contributed.

## What it is for

A forest that costs what is on screen rather than what is in the level. You want the second stage
whenever a cell holds more than a handful of instances — a 32 m cell of grass is fifty thousand of
them and the far half is behind a hill.

You do not want it for a few dozen props: two passes over an array to compact ten transforms costs
more than drawing all ten.

## Using it

```csharp no-compile="a fragment; the templates come from each type's LOD group"
renderer.Cull(volume, draws, frustum, camera.Position, densityScale: 1f);

foreach (var batch in renderer.Batches) {
    foreach (var command in batch.Commands) {
        // one indirect draw per level, at that level's own mesh
    }
}
```

⚠ **The LOD decision is per instance, and that is a deliberate divergence from
`LodRenderFeature`.** That feature is right for its case — a LOD group is several render objects and
it clears bits — and it cannot express "these four thousand trees in this cell are at level 1 and
those six hundred are at level 2", because its level is per object and here it is per instance. So a
cell draws three or four times instead of once.

⚠ **A level with no survivors still gets a command.** It is what lets a caller bind level N's mesh at
slot N instead of reading back which levels survived.

⚠ **A cell can survive the frustum and still draw nothing**, when every one of its instances is past
the cull distance. Issuing three empty commands for it would be the cost of the batch for none of the
benefit, so it does not become a batch — a third rejection worth knowing about when reading the
counters.

⚠ **One transform buffer for the whole frame, not one per cell.** A per-cell upload is a map and an
unmap per cell — a few thousand of them — to move a few kilobytes each. Every batch's run is a slice
of it, and the slices tile it exactly.

⚠ **The cross-fade weight rides in the per-instance parameters** and the existing dithered discard
reads it unchanged. Deciding the fade anywhere else would measure the distance a second time, and the
two would disagree by a frame — which pops.

## What is here and what is not

⚠ **This is the CPU half, and the compute shader is the other one.** The compaction, the binning and
the fade are `InstanceCuller`'s, which was built precisely so that both halves could be the same
arithmetic — the device form claims slots with an atomic add and is therefore unordered, which a seam
test sorts away rather than asserts through. What runs here is what a headless test and a machine with
no compute queue use, and it is the reference the dispatch is checked against.

⚠ **The bounding radius is the type's spacing scaled by the instance**, because this assembly has no
mesh and cannot ask one how big it is. A radius that is too small culls an instance while part of it is
on screen, so the approximation errs upwards.

## The device half

`FoliageCullPass` is the same arithmetic where fifty thousand trees can afford it, and
`FoliageCull.rvn` is what it dispatches. Neither is the definition — `InstanceCuller` is, and both
transliterate it, which `FoliageCullParityTests` holds them to.

⚠ **Two dispatches of one shader, because compaction needs a count before it needs a slot.** The
first phase counts each level's survivors; the second recomputes every verdict and claims a slot
within its level's run. That is `InstanceCuller`'s own two-pass shape, and it is there for the same
reason: one pass cannot make each level's survivors contiguous without knowing the earlier levels'
sizes.

⚠ **Recomputing is cheaper than remembering.** Storing the first phase's verdict would be four bytes
an instance of bandwidth each way, against a dot product and a hash — and the verdict is a pure
function of data neither phase writes, so the two cannot disagree.

⚠ **The survivors are indices, not transforms.** A compacted transform buffer is sixty-four bytes an
instance and this is four, and the draw needs an indirection either way because `firstInstance`
indexes *something*. It also makes the output directly comparable with `InstanceCuller.Survivors`,
which is what a seam test wants to compare.

⚠ **A batch writes inside its own run and never negotiates with another.** A batch's first instance
is where its survivors go as well as where its instances are, so the output buffer is exactly as long
as the input and nothing allocates per frame. Two batches' survivors are therefore not adjacent,
which costs nothing — they are different meshes and were never one draw.

⚠ **The device claims slots with an atomic and is therefore unordered.** A seam test sorts each
level's run before comparing; asserting through the order would be asserting something no GPU
promises.

⚠ **The first stage stays on the host.** A forest is a few thousand cells, and testing them beside
the code that already walks the chunks is cheaper than uploading their bounds so a dispatch can walk
them again. What the device is for is the fifty thousand instances inside them.

⚠ **The instances are uploaded when the volume changes and the batch table every frame.** A forest's
instances are megabytes and they do not move; its batch table is a hundred kilobytes and every field
in it is the view's.

⚠ **Occlusion is a permutation, and it is off by default.** The Hi-Z test removes a forest behind a
ridge before the ridge draws rather than by it, and it costs the projection of eight corners, four
texture loads and a branch — which is why a frame with no depth pass compiles a variant that carries
none of it. `FoliageOccluders` is what a host hands in; the default is "no pyramid", and it is not
the same as an empty one.

⚠ **The test runs last of the four rejections.** Eight matrix multiplies and four loads against six
dot products, so it only ever runs for an instance already in range, kept by the density scalar and
inside the frustum.

⚠ **Both phases run the same variant, always.** The placing phase recomputes the counting phase's
verdict, so a pair that disagreed about occlusion would place survivors the counts never accounted
for — which writes past the end of a level's run.

⚠ **The pyramid's own matrix, not this frame's.** A pyramid is a picture of a particular view;
testing against another view's matrix occludes by arithmetic that was never about it, which produces
trees vanishing when the camera turns.

⚠ **Four levels, and the stride is a constant both sides declare.** A stride that disagreed would not
fail — it would read level 2 of one cell out of level 0 of the next, which draws as the wrong mesh in
the right place.

## The draw

`FoliageDrawPass` is what consumes the cull: the pipeline built from `Foliage.rvn`, one descriptor
set over the cull's three buffers, and one `DrawIndexedIndirect` per level per batch out of the
commands the placing dispatch patched.

⚠ **The instance id is the survivor slot, and that is the whole indirection.** The cull patched each
command's `firstInstance` to its batch's run, so the vertex stage reads `survivors[SV_InstanceID]`
and then the thirty-two-byte instance it names. No transform is compacted, copied or re-uploaded
between the cull and the draw — a frame's cull re-aims the draws at a subset of a buffer that never
moved.

⚠ **The fade is the cull's, not remeasured.** `FoliageCullParameters.fade` was computed from the same
distance the cull binned by, and the draw stipples with the grass's own pattern — a tree's far LOD
dissolving over fading blades dissolves against one dither rather than two interfering ones.

⚠ **A type is drawn only once its mesh is real.** There is no honest stand-in for a tree the way the
grass has a built-in blade, so a type whose mesh has not loaded is skipped — its chunks are not even
uploaded — and `FoliageDrawPass.MissingMeshes` / `TerrainSceneRenderer.FoliageMeshesMissing` are
where the wait is visible. A number that falls over a level's first frames is content arriving; one
that stays up is a `.vxfoliage` whose mesh reference nothing can resolve.

⚠ **One albedo for the pass, white by default — this increment's honest ceiling.** A `.vxfoliage`
names a mesh and nothing about its material; until the material seam exists (the impostor bake needs
the same answer) a forest draws flat-shaded in its tint, which reads as "no material yet" rather than
as foliage that is broken.

## Using it from a game

A scene stands its painted foliage up with `FoliageVolumeComponent`: the `.vxfol` the editor saved
beside the scene, and the `.vxfoliage` palette entries in the order the volume's chunks index —

```csharp no-compile="a fragment; the references are the project's"
world.Add(entity, FoliageVolumeComponent.Of("Levels/Forest.vxfol", "Foliage/Pine.vxfoliage", "Foliage/Rock.vxfoliage"));
```

— plus the same `!Terrain` node in the frame document the ground already needs; the node owns one
`FoliageCullPass`/`FoliageDrawPass` pair per volume. The extraction bridge resolves the palette
first and the instances after it, because the store drops chunks past the palette; a palette still
loading waits quietly (`TerrainExtractionSystem.Waiting`), and one whose type refuses itself drops
the whole volume loudly (`RefusedFoliage`) — the order is what the instances index, so dropping one
entry would re-dress every stand after it.

⚠ **The palette's order is load-bearing.** Append to it; never sort it.

⚠ **A type's mesh reference must be a `vx:` reference** — `vx:<model>#<mesh>`, the same scalar a
`MeshRenderable` carries — because the runtime resolves it through the scene's own mesh source, one
load per pine however many volumes place it.

The document's quality knobs are `foliageDensityScale:`, `foliageCullDistanceScale:` and
`foliageCellBudget:` on the tier record — density feeds the cull's hashed keep, the distance scale
multiplies every type's authored cull distances (and deliberately not its LOD thresholds), and the
budget bounds how many cells stay uploaded. A volume that fits the budget uploads whole and streams
nothing; a bigger one uploads the cells around the camera through `FoliageStreamer`.

⚠ **The foliage follows the terrain's lighting mode.** Under a lit frame the instances draw with
`FoliageLit` — the frame's sun, the cascade term sampled per *fragment* (a tree spans many cascade
texels where a grass blade spans a fraction of one), the sky's harmonics for ambient, and under a
split frame the raw signed mesh normal to `SceneNormals`. The mesh's normal, not the ground's — a
trunk is a cylinder the sun goes behind, where a blade is optically the meadow it covers.

⚠ **`FoliageType.CastShadows` is carried, not yet consumed** — shadow casting for foliage is the
caster increment's, and the flag is readable off the volume's palette when it lands. Hi-Z occlusion
is plumbed the same way: `FoliageCullPass.Prepare` takes `FoliageOccluders`, and the node hands in
none until the frame publishes a depth pyramid for the ground stack.

## Examples

Three levels, taking over at sixty and a hundred and fifty metres:

```csharp no-compile="a fragment; the index counts come from the mesh's LOD group"
var draws = new FoliageDraw(
    type,
    [near, middle, far],
    [60f, 150f]
);
```

Reading what a frame cost:

```csharp no-compile="a fragment; the counters are what a profiler shows"
var drawn = renderer.Cull(volume, draws, frustum, camera.Position);

// renderer.CellsConsidered   — every cell holding a drawn type
// renderer.CellsDrawn        — the ones the frustum kept
// renderer.InstancesConsidered / drawn — what the second stage did
// renderer.Draws             — indirect commands issued, empty ones included
```

⚠ **`Draws` counts the empty commands, because they are issued.** A profiler reading it is reading
what the queue was handed.

Scaling the whole thing down for a scalability setting:

```csharp no-compile="a fragment"
renderer.Cull(volume, draws, frustum, camera.Position, densityScale: 0.5f);
```

⚠ **A density scalar drops the *same* instances every frame**, because the choice is a hash of the
position rather than a draw from a generator. A slider that reshuffled would make a forest shimmer.

## See also

- [Foliage instances](../engine/foliage.md) — the cells and instances this draws.
- [Drawing grass](grass-rendering.md) — the derived half, scattered rather than loaded.
- [Impostors](impostors.md) — the last LOD level, and the far field it makes affordable.
- [Culling and streaming instances](instance-culling.md) — the compaction and the LOD binning themselves.
- [docs/plan/31 § D9](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the cell as the batch, and why the second cull belongs on the GPU.
