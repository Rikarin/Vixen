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

## What is owed

The render feature itself: the per-tile height and weight textures with their mips, the upload of
selected nodes, the generated splat material's 4/8/12/16 permutation, and `TerrainComponent`. Until
those land nothing draws — what is here is the geometry contract and the pieces that can be checked
without a device.
