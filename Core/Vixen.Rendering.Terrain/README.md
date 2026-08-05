<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Rendering.Terrain

The device side of the terrain — [docs/plan/31 § T2](../../docs/plan/31-terrain-grass-and-trees.md).
The arithmetic is in [`Vixen.Terrain`](../Vixen.Terrain/README.md) and is device-free on purpose
([§ D1]), so what is here is the part that needs a graphics device and nothing that does not.

| Type | What it is |
|---|---|
| `TerrainGridPatch` | The index buffer every patch is drawn from, and the two divisions the vertex stage does |
| `TerrainNodeRecord` | The per-patch instance record, matching `TerrainNode` in `Terrain.rvn` byte for byte |
| `TerrainKeys` | Generated from `Raven/Library/Terrain/Terrain.reflect.json` — every binding index, by name |
| `TerrainSplat` | What the generated material compiles as: the 4/8/12/16 slot count, whether the height path is on, and the packed per-layer buffers |
| `FoliageRenderer` | § T5's two-stage cull: the cell against the frustum, then each instance against its own distance and level |
| `GrassRenderer` | § T6's ring: cells scattered as they enter range, blades culled every frame |
| `TerrainSurface` | The join both kernels leave out — a heightfield answering `IFoliageSurface` |
| `GrassDispatch` | § T6's device half: the cell records, the ring of buffers, the scatter dispatch and the indirect draws it produces |
| `FoliageCullPass` | § T5's device half: the instance table, the per-frame batch table, and the two dispatches that compact the survivors |
| `FoliageCullBatchRecord` | One cell of one type, as `FoliageCull.rvn` packs it — forty-eight bytes |
| `FoliageCullInstanceRecord` | One instance, in `FoliageStore`'s own thirty-two-byte layout |
| `FoliageCullViewRecord` | The six frustum planes and the viewpoint, as one record |

## The shader

`Raven/Library/Terrain/Terrain.rvn`. One instanced grid patch, morphed into its parent, sampling a
heightfield.

**There are no vertices.** A regular lattice's positions are two divisions of `SV_VertexID`, so
uploading 33² of them per frame would be sending the shader something it can count — the reflection
confirms it, with an empty `VertexInputs`. What is uploaded is the index buffer, once, and one
`TerrainNodeRecord` per patch.

**`SampleLevel`, not `Sample`.** A vertex stage has no derivatives, so `Sample` outside a fragment
stage never meant what it looked like and SPIR-V was quietly substituting level zero. [docs/plan/07]
records a terrain heightmap as the case that motivated adding it.

**The normal is differenced at the patch's own step**, not at one sample. A normal taken at full
resolution on a coarse patch is the normal of geometry that patch does not have, which makes the seam
between two levels visible in the lighting even though the positions agree.

## Two things kept honest without a GPU

**The morph.** `TerrainShaderParityTests` does what `GpuVisibilityGroupTests` does for the culling
shader: a source assertion that the expression is still there, plus a transliteration checked against
`TerrainLodTree.MorphIndex` over every index and every morph. A source assertion is weaker than an
execution and is chosen knowing it — it catches the failure that actually happens, which is somebody
editing or deleting the morph and opening every level boundary. The golden image catches the rest and
needs a device.

**The winding.** Every triangle is asserted to wind the same way in the XZ plane. A terrain wound
backwards is invisible from above and solid from below, which reads as nothing drawing at all rather
than as a winding problem.

## The generated splat material

`TerrainSplat.Of` reads the layer list and says what to compile. Two axes: the slot count, quantised
so a seventh layer does not compile a new shader, and whether *any* layer wants the height path.

⚠ **Which mode each layer uses is not a permutation.** That is per layer and the permutation is per
material, so eight layers with three modes between them is one shader — the mode and the contrast ride
a `float2` buffer the fragment stage reads.

⚠ **The height blend needs two passes over the layers.** It has to know the highest contender at a
fragment before it can weight any of them, and that is not known until every layer has been looked at.
`HeightBlend` off compiles the first pass out.

## Grass: a ring, and a seam with no file behind it

`GrassRenderer` is the CPU form of `GrassScatter.rvn` — [§ T6]. A cell entering range is scattered
into one of a fixed number of pooled buffers and a cell leaving hands its buffer back, so a field
costs a fixed amount of memory whatever the size of the level; re-entry re-scatters to the *identical*
blades, because the hash depends on the cell coordinate and the candidate slot and on nothing else.

⚠ **The scatter is on entry and the cull is every frame, and keeping those apart is the shape of the
whole feature.** Scattering per frame probes the surface for every blade of every cell every frame,
which is the cost the ring exists to pay once.

⚠ **Grass is derived, so there is no file to check a device run against.** If the dispatch and the
reference disagree the field simply differs between a machine with a compute queue and one without,
and nobody can point at a wrong number. `GrassScatterParityTests` closes that with a transliteration
— every stream, the candidate position and the density curve, at zero drift — and a source assertion
that the arithmetic is still in the shader.

⚠ **`(float)uint.MaxValue` is 4294967296, not 4294967295.** A `float` has twenty-four bits of
mantissa. A shader written with the true maximum agrees to six decimal places and disagrees in the
last bits of every draw, which is a field that is *almost* the same — so the constant is named on both
sides rather than written twice.

## Both culls patch `firstInstance`, and that is a device capability

`GrassScatter.rvn` writes `command.firstInstance = cell.first` and `FoliageCull.rvn` writes
`command.firstInstance = batch.firstInstance + runBase`. Both are the base of a run inside one shared
buffer, and neither can be zero for more than the first draw — which is how a cell reaches its own
blades and a batch its own level with no descriptor of its own, because Vulkan adds `firstInstance`
into `gl_InstanceIndex` before the vertex stage runs.

⚠ **That is a *permission*, not a free draw argument, and the permission is
`GraphicsDeviceFeatures.HasDrawIndirectFirstInstance`.** A direct draw may always name a first
instance. An *indirect* one may not: without `drawIndirectFirstInstance`,
VUID-vkCmdDrawIndexedIndirect-firstInstance-00530 requires every command in the buffer to carry zero
there.

⚠ **Nothing reports getting this wrong.** The offending number is written by a compute pass into a
device buffer; the validation layers read the draw call, which is legal. So there is no message to
grep for, and the symptom is not a blank screen — it is every cell drawing ring slot zero's blades,
a full field of plausible vegetation standing in the wrong places, which reads as a scatter bug.

So `TerrainSceneRenderer` asks before it builds anything: a device without the capability grows no
grass and no foliage, and says so through `VegetationUnsupported`. Off rather than folded in some
other way — a base from a uniform in all six vegetation shaders plus a second buffer for the one
number the foliage cull computes on the device, for a target that does not exist. `VP_KHR_roadmap_2022`
and `VP_ANDROID_15_minimums` both require the capability and MoltenVK reports it; only the older
`VP_ANDROID_baseline_2022` leaves it out, which is why the check is there at all.

**Writing a third indirect pass?** `TerrainShaderParityTests` sweeps every `.rvn` for a non-zero
write to `firstInstance` and fails unless the Vulkan backend still asks for the bit — but it cannot
tell whether *your* pass gated on the capability. That part is yours.

## What is owed

The render feature itself: the per-tile height and weight textures with their mips, the upload of
selected nodes, the generated splat material's 4/8/12/16 permutation, and `TerrainComponent`. Until
those land nothing draws — what is here is the geometry contract and the pieces that can be checked
without a device.

And the device halves of both instancing paths: the compute shader that mirrors `InstanceCuller` for
foliage, and the host that binds `GrassScatter.rvn` and `Grass.rvn` — the dispatch, the cell records
and the ring of device buffers. Both were built CPU-first on purpose, because a cull and a scatter
fail silently in both directions.
