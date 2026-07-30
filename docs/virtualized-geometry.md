<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Virtualized geometry

A Nanite-class geometry pipeline for Vixen: one authored mesh, a cluster DAG built offline,
streamed pages, hierarchical culling on the device, and a rasterizer that does not care how many
triangles there are.

This document is the plan and the argument for it. It exists as a separate file for the same reason
[bindless-materials.md](bindless-materials.md) does — several things across the engine are blocked on
pieces of it, and those blocks are easier to read in one place than scattered across ten status
tables.

**Read the cost section before the phases.** The whole system is larger than everything remaining on
the roadmap put together, and the phases are ordered so that stopping early is a real option rather
than an abandoned branch.

## What this is

Nanite's claim is that triangle count stops being a thing you budget. It gets there by moving three
decisions off the CPU and out of the asset:

| | |
|---|---|
| **Level of detail** becomes per cluster, per view, per frame — not a chain of authored meshes swapped as a unit |
| **Culling** becomes hierarchical and device-side — the traversal rejects a subtree, not an object |
| **Rasterization** becomes indifferent to object count — a fixed number of dispatches draws the scene |

The parts that make it hard are not the ones that make it famous. Software rasterization is the
memorable trick; the cluster DAG and its error metric are what actually take the time, and they are
the parts that fail as visible cracks rather than as a slow frame.

## Where Vixen already is

More of this is built than a reading of "we have no Nanite" would suggest. The architectural piece
that is hardest to retrofit — two-phase occlusion against a Hi-Z pyramid, with the second phase
answering the *difference* — is done and device-verified.

| Piece | State | Where |
|---|---|---|
| GPU visibility, one dispatch over every view | ✅ | [GpuVisibilityGroup.cs](../Core/Vixen.Rendering/GpuVisibilityGroup.cs) |
| Hi-Z pyramid, min-reduced, per view | ✅ | [HiZRenderer.cs](../Core/Vixen.Rendering/Compositor/HiZRenderer.cs), `HiZReduce.rvn` |
| **Two-phase occlusion** — `Main` then `Late`, answering the difference | ✅ | [GpuCulling.cs](../Core/Vixen.Rendering/GpuCulling.cs) `CullPhase` |
| Draw arguments written by the device, bits never read back | ✅ | [GpuDrawArguments.cs](../Core/Vixen.Rendering/GpuDrawArguments.cs) |
| A CPU mirror of the shader's arithmetic, randomised against the definition | ✅ | `GpuCulling.IsVisible` |
| Compositor nodes for all of it, assignments made by the builder | ✅ | `!GpuCulling`, `!HiZ` |
| Bindless table, descriptor indexing, Raven `Texture2D[]` | ✅ | [bindless-materials.md](bindless-materials.md) |
| **Material records** — the block as a record of one buffer, one bind per effect | ✅ | `MaterialRecords`, `[MaterialIndex]` |
| **Transform records** — the matrix out of the command buffer, index in `firstInstance` | ✅ | `UseTransformRecords` |
| **`GeometryBuffer`** — many meshes in one vertex and one index buffer | ✅ | [GeometryBuffer.cs](../Core/Vixen.Rendering/GeometryBuffer.cs) |
| **`DrawIndexedIndirectCount`** — a draw whose count the host never learns | ✅ | all four backends |
| **Compaction** — survivors appended, one command per batch | ✅ | `GpuDrawArguments.Compact` |
| Discrete LOD with hysteresis and dither cross-fade | ✅ | [LodRenderFeature.cs](../Core/Vixen.Rendering/Features/LodRenderFeature.cs) |
| Deferred/GBuffer *shaders* | ✅ | `Pipeline/GBuffer.rvn`, `Pipeline/Deferred.rvn` |
| **An incremental GPU scene** — object records rewritten only where they changed | ✅ | [PersistentUploadBuffer.cs](../Core/Vixen.Rendering/PersistentUploadBuffer.cs) |
| **Geometry pages** — fixed-size, one quantization grid, roots in page zero | ✅ | [MeshletPageBuilder.cs](../Core/Vixen.Rendering.VirtualGeometry/MeshletPageBuilder.cs) |
| **A residency service** — requests in, LRU out, one budget, not geometry-shaped | ✅ | [PageResidency.cs](../Core/Vixen.Rendering/PageResidency.cs) |
| **The cluster traversal** — a permutation of `Culling.rvn`, with a CPU mirror | ✅ | [GpuClusterCulling.cs](../Core/Vixen.Rendering/GpuClusterCulling.cs), `Culling.rvn` |
| **Workgroup-shared memory in Raven** — `groupshared`, `barrier()`, an atomic rooted in it | ✅ | B1 below |
| **64-bit integers and atomics in Raven** — `int64`/`uint64`, `Int64`/`Int64Atomics` reported apart | ✅ | B2 below |
| **`SampleGrad` in Raven** — gradients the caller computed, in every stage | ✅ | B3 below |
| **The cluster DAG** — cluster, group, simplify with the group boundary locked, split, repeat | ✅ | [MeshletBuilder.cs](../Core/Vixen.Rendering.VirtualGeometry/MeshletBuilder.cs) |
| **DAG validity as a build error** — monotonic error and boundary equality, per group | ✅ | `MeshletValidator`, `ModelCompiler.CompileMeshlets` |
| **A CPU reference cut**, and the fallback mesh cut from the same code | ✅ | `MeshletCut` |
| Meshlet generation in `ModelCompiler` | ✅ | [ModelCompiler.cs](../Editor/Vixen.Editor.Assets/Models/ModelCompiler.cs), `generateMeshlets:` in the `.meta` |
| Deferred *pipeline* | ⬜ | Phase 10, cut-list #6 |
| Texture and shadow-page streaming on the same service | ⬜ | improvement 6; the service exists, the two other consumers do not |

The existing two-phase structure is not merely *similar* to Nanite's — it is the same algorithm at a
coarser granularity. Cluster culling is that traversal one level deeper. This is the single most
important fact in this document, because it means the plan extends a working system rather than
standing up a parallel one.

## What blocked it

**All three are built.** An earlier draft of this document listed five, and the bindless plan closed
two of them while it was being written — `GeometryBuffer` gave every mesh one shared vertex and index
buffer, `DrawIndexedIndirectCount` gave a draw a count the host never learns, and material and
transform records took the last per-object binds out of the run. Compaction, which those were blocking,
is built. The remaining three were all Raven's, and they have landed together; what follows is what
each was and what it turned into, because the reasons stay useful and the phases below still refer to
them.

### B1. Workgroup-shared memory ✅

Tracked as 🟡 in [07](plan/07-raven-shader-pipeline.md) — "a storage class the language cannot
declare". Atomics landed without it, which was enough for a cull that gives one invocation one word
and needs no cooperation at all.

Hierarchical traversal needs cooperation. A workgroup pops a node, tests its children, and pushes the
survivors — a queue with a local head, which is shared memory or it is a global atomic per child and
a dispatch that spends its life in memory traffic. The same gap blocked GPU sorting (#50) and a
compaction with one counter per workgroup.

**Built.** `groupshared var tile: float[64]` is a shader member that is deliberately not a binding —
no descriptor, no `(set, binding)`, nothing the host writes — and not a local either: one copy per
workgroup rather than one per invocation. `barrier()` and `memoryBarrierShared()` came with it, and an
atomic may now root in either a writable resource or a `groupshared` variable, which is the rule the
atomics always meant. Only a compute stage may reach any of it (`RVN3012`), decided by reachability
rather than by where the declaration sits. See
[07 § Workgroup-shared memory](plan/07-raven-shader-pipeline.md).

### B2. 64-bit atomics ✅

Raven's atomics were scalar `int`/`uint` — 32-bit, "the targets' limit rather than a choice" for
floats, but 64-bit integers are a separate matter: optional on Vulkan
(`VK_KHR_shader_atomic_int64`), SM6.6 on D3D12, and absent from WebGPU entirely.

A single-pass software rasterizer wants `atomicMax` on a 64-bit word packing depth above ID. With 32
bits you get depth *or* a usable ID, not both, and the alternative is two passes over the same
triangles — a depth pass by `atomicMin` and an ID pass testing equality — which costs roughly what
it sounds like.

**Built.** `int64` and `uint64` are scalar types resolved by name rather than by keyword, with every
atomic declared at both widths and nothing widening into 64 bits implicitly — `uint64(x)` is written
out, because a silent widening would let tie-breaking decide the width of an operation whose width is
the entire point. A shader that uses one reports **two** capabilities, `Int64` and `Int64Atomics`,
because a device may offer the type without offering atomics on it. This blocked **phase 6 only**, and
phase 6 remains optional and gated on a measurement.

### B3. `SampleGrad` ✅

`SampleLevel` had landed; `Sample` takes its level from quad derivatives. A visibility-buffer resolve
has neither — the pixel next door may be a different triangle of a different material, so the quad's
derivatives are meaningless at every silhouette and every material boundary.

The fix is analytic: barycentric gradients from the triangle's screen-space plane, propagated through
the UV interpolation and handed to the sample. `SampleLevel` alone will not do, because one LOD
throws away anisotropy and that is visible as blur on every floor and every wall at a grazing angle.

**Built**, on all three texture types, as SPIR-V's `Grad` image operand and GLSL's `textureGrad` —
legal in every stage, because a stated gradient needs no quad to derive one from. It blocked **phase
5**, and it was the only prerequisite the Forward+ resolve adds that a GBuffer resolve would not also
need.

### And two things that stopped being blockers earlier

**A shared geometry buffer.** `GeometryBuffer` is built — many meshes in one vertex buffer and one
index buffer, one buffer per vertex layout because `vertexOffset` is multiplied by the *pipeline's*
stride, device-local with staged `Write`/`Flush`. The page pool of phase 2 is this with a different
allocation policy, not a new thing.

**A draw whose count comes from the device.** `DrawIndexedIndirectCount` is built across Vulkan, GL,
WebGPU and Null, behind `GraphicsDeviceFeatures.HasDrawIndirectCount`, with zeroed instance counts as
the fallback. The hardware-raster path of phase 4 draws the surviving cluster list with it directly.

### And one thing that was never a blocker

**Mesh shaders.** `GraphicsDeviceFeatures.HasMeshShaders` exists and Raven cannot emit them yet.
That is fine — Nanite does not use them either. The cluster pipeline is compute plus ordinary draws,
which is why it reaches hardware mesh shaders never did.

## The shape of the system

```
BUILD (offline, ModelCompiler)
  mesh → clusters (~128 tri) → group → simplify group → split → repeat
       → cluster DAG + per-cluster bounds, error, material, bone range
       → pack into 128 KB pages, root page marked resident
       → fallback mesh (a cut through the DAG at a fixed budget)

FRAME
  1  GPU scene update      dirty instances only, not the whole array
  2  instance cull         ← what GpuVisibilityGroup already does
  3  cluster traverse      persistent workgroups over the DAG, per view
                           cut by screen-space error; reject by frustum + Hi-Z
                           → visible cluster list + page requests
  4  raster                HW: one instanced indirect draw → visibility buffer
                           SW: compute, 64-bit atomicMax → visibility buffer   [opt]
  5  material resolve      bin visbuffer into per-material tiles
                           → run existing Forward+ shading per tile
  6  Hi-Z rebuild          feed phase 2 of the next pass — already built
  7  streaming             service page requests, evict by LRU under budget
```

Steps 2 and 6 exist. Step 1 is a rework of something that exists. Steps 3–5 and 7 are new.

## Phases

Each phase ships something usable on its own. The exit criteria follow the repository's habit of
verifying by sabotage — a test that does not fail when the feature is removed is not a test of the
feature.

---

### Phase 0 — Unblockers · ~1 EM · ✅ built

Nothing here is virtualized geometry. All of it is owed to something else already. **Two of the five
items this phase used to hold were built with the bindless plan** — the geometry arena is
`GeometryBuffer` and the device-supplied draw count is `DrawIndexedIndirectCount`, along with the
material records that were the third.

| | Work | Owed to | |
|---|---|---|---|
| 0.1 | **Workgroup-shared memory in Raven** — the storage class, the barrier intrinsics, the atomic root that is not a local | B1, GPU sort (#50), a compaction with one counter per workgroup rather than one per dispatch | ✅ |
| 0.2 | **`SampleGrad` in Raven** — explicit gradients, so a sample outside a fragment quad filters correctly | B3, phase 5 | ✅ |
| 0.3 | **Incremental GPU scene** — the object records were repacked and re-uploaded every frame. Now a persistent buffer, rewritten only where it changed | everything; it was the cost floor at 100k instances | ✅ |
| 0.4 | **64-bit integers and atomics in Raven** — `int64`/`uint64`, every atomic at both widths, `Int64` and `Int64Atomics` reported separately | B2, phase 6 | ✅ |

**Exit:** a 100k-instance scene where a frame that moves one object uploads one object's worth of
bytes, asserted by counting the upload. Deleting the dirty tracking makes the assertion fail rather
than making the frame slower. Both are `GpuVisibilityGroupTests`, and the second was checked by
doing it.

#### What 0.3 turned out to be

[`PersistentUploadBuffer<T>`](../Core/Vixen.Rendering/PersistentUploadBuffer.cs), the sibling of
`UploadBuffer<T>`: same ring of one region per frame in flight, and the opposite policy about what
is in it. `UploadBuffer` is refilled from scratch, which is right for a skeleton's matrices or a
frame's light list — data the host recomputes anyway. Object records are the same bytes they were
last frame for all but a handful of a hundred thousand, and uploading all of them costs three
megabytes a frame to say so.

Three decisions worth keeping:

- **Dirtiness is decided by comparison, not by cooperation.** The plan said "driven by the ECS
  change versions", and the ECS does expose them — but nothing bridges the ECS to
  `RenderObjectStore` yet, and `RenderObjectStore` hands out a `ref` that any feature may write
  through. A flag those writers had to set would be *silently* wrong when one of them forgot, which
  is bounds a frame culled against and a diagnostic nowhere. Comparing the packed bytes cannot miss
  a change, and it reads exactly the bounds and stage mask the culling loop reads anyway. The linear
  pass stays; what leaves the host is the difference.
- **A dirty set per region, not one.** A persistent buffer cannot simply rewrite this frame's
  region: that region is `FramesInFlight` frames stale, and what it is missing is every change
  since. So a change marks the record in every region and each flushes its own set when its turn
  comes — one moved object costs one record per frame for three frames rather than the whole buffer
  once.
- **A region starts entirely dirty.** Not conservatism, and it is the case a comparison alone gets
  wrong: a record the host has never set is zeroed in its copy and undefined on the device, so
  comparing the two finds no difference and skips the one write that mattered. A bit cleared only by
  an actual write makes "never written" and "differs" the same state.

What it does *not* do is remove the per-frame walk over the object array — that needs the store to
own mutation rather than hand out a `ref`, which is a separate change with a wider blast radius and
no visible payoff until something profiles it.

---

### Phase 1 — The cluster DAG · ~3 EM · ✅ built

Offline, in `ModelCompiler`. This is the phase that decides whether the result has cracks.

Built as [`Vixen.Rendering.VirtualGeometry`](../Core/Vixen.Rendering.VirtualGeometry/README.md), called
from `ModelCompiler.CompileMeshlets` and written as a `Meshlets` sub-asset per mesh. Three things the
plan below did not say, found in the building:

- **The error has to be measured, not taken from the quadric.** A quadric is the distance to the
  *planes* of the triangles that met at a vertex, which on a smooth surface is about a third of the
  distance to the surface itself — so a cut chosen for a one-pixel budget pops by three. The
  simplifier measures the removed point against the triangles that replaced it and adds that to what
  the point already carried.
- **Locking a group's boundary vertices is not enough.** A collapse of an *interior* edge can delete
  the one triangle carrying a boundary edge, and the edge goes with it although neither endpoint
  moved. Both rules are needed.
- **The per-cluster lock is a quality failure, not a validity one.** The exit criterion below asks
  for a validation that fails on it; locking more than necessary never cracks, so it cannot. What it
  costs is measured instead: with the group lock every level meets the ratio it was given exactly,
  and with the per-cluster lock no level ever does.

- **Cluster** the mesh into ~128-triangle groups by a locality partition (METIS-style edge-cut on the
  triangle adjacency graph). Record per cluster: bounds, a normal cone for backface rejection, the
  material index, and — see improvement 1 — the bone index range.
- **Group** neighbouring clusters (~8–32), simplify each group *as a unit* with its shared boundary
  edges locked, and split the simplified result back into clusters. Repeat until one cluster remains.
  Locking the group boundary rather than each cluster's is the whole trick: it lets interior detail
  collapse while guaranteeing that any cut through the DAG meets along edges that were never moved.
- **Error metric** per cluster: the object-space deviation introduced by the simplification that
  produced it, stored so it can be projected to screen space at runtime. Parent error must be ≥ every
  child's, or a cut can pick a parent on one side of a seam and a child on the other.
- **Fallback mesh**: a cut through the DAG at a fixed triangle budget, emitted as an ordinary mesh.
  This is what runs on WebGL2, in collision, in the physics cook, and anywhere else the virtualized
  path does not reach. It is generated, not authored.

**Exit:** a build-time validation pass asserts monotonic error across every DAG edge and locked
boundary equality between every parent and its children, and it *fails* on a mesh deliberately built
with the per-cluster lock instead of the per-group one. Plus a CPU reference cut over a sphere at
twenty distances whose silhouette error stays under the requested pixel threshold.

**Met**, with the third clause answered as above and one criterion added that is stronger than any of
them: over twenty thresholds, **every cut of a closed mesh is itself closed** — a sphere has no
boundary, so a cut that took a parent on one side of a group and a child on the other leaves an edge
with one triangle on it, and that is a crack detected as a number rather than looked for in a picture.
Removing the group-boundary lock fails it. `MeshletCutTests`, `MeshletValidatorTests`.

---

### Phase 2 — Pages and residency · ~2 EM · ✅ built

- **Page format**: fixed-size (128 KB) pages holding clusters with their vertex and index data,
  position-quantized, materials as indices. Root page always resident, so a never-streamed object
  still draws at its coarsest level. [`MeshletPageBuilder`](../Core/Vixen.Rendering.VirtualGeometry/MeshletPageBuilder.cs).
- **Page pool**: a single large buffer, suballocated. This is `GeometryBuffer` with a residency policy
  instead of a load-time one, different allocator.
  [`MeshletPagePool`](../Core/Vixen.Rendering/MeshletPagePool.cs).
- **Residency manager**: requests in, serviced on the CPU against async I/O, LRU eviction under a byte
  budget. [`PageResidency`](../Core/Vixen.Rendering/PageResidency.cs), and it is not geometry-specific
  — see improvement 6.

**Exit:** a camera path over a scene exceeding the budget by 4×, holding the budget and showing no
cluster popping beyond the configured error, with a synthetic I/O delay injected. The
budget-respecting criterion is already the one [08](plan/08-asset-pipeline-and-addressables.md)
states for streaming generally.

**Met**, as `MeshletStreamingTests`, with "no popping beyond the configured error" given the strongest
form it has here: **every frame of the path draws a closed surface**. A sphere has no boundary, so a
cut that drew a cluster and not its missing neighbour leaves an edge with one triangle on it — the
same crack detector phase 1 uses, applied to a cut chosen under a residency constraint rather than
under a threshold alone.

Four things the plan above did not say, three of them found in the building:

- **Only the geometry pages; the cluster records stay resident.** A traversal has to test a cluster —
  its bounds, its cone, its error — before it can know whether it wants its geometry, so the
  hierarchy cannot itself be paged. It is also sixty-odd bytes against a cluster's two kilobytes,
  which makes the split obviously right rather than a compromise.
- **One quantization grid for the mesh, not one per cluster.** This is the decision that cracks the
  mesh if it is made the obvious way, and the obvious way is the one that spends the bits well:
  quantize each cluster against its own bound. A vertex on a locked boundary is referenced by a
  cluster on each side, the two have different bounds, and the same position rounds to two different
  numbers — throwing away, in the last step before the device sees it, exactly the bit-identical
  boundary phase 1 collapses onto existing vertices to guarantee. Sixteen bits across the *mesh's*
  longest extent instead, which also makes every cluster's local coordinates fit sixteen bits by
  construction: the coarsest cluster there can be spans the whole grid.
- **Degradation is a coarser threshold, not a patched cut.** When a page has not arrived, the
  tempting fix is local — swap the missing cluster and its siblings for their group's parents, repeat.
  It is wrong in a way that looks right: a group's other children may not be in the cut at all,
  because the cut took *their* children, so swapping in the parents leaves those finer clusters
  underneath and the surface is covered twice in one place and once in another. Raising the threshold
  cannot do that, because every threshold's cut is an antichain by construction.
- **A sink may be full, and saying so is not an error.** The pool stages through host memory it
  reclaims when the frame's copies are recorded, so a frame that streams more than one flush cycle
  can carry has to be told to stop. `IPageStore.Place` answers whether it took the bytes, the service
  treats a refusal as it treats a budget it cannot meet, and the page loses a frame — which costs
  nothing, because the request is demand-driven and the next frame asks again.
- **The pages are two artefacts, and one artefact would have defeated the phase.** A
  `MeshletPageSet` carrying its own bytes is a single blob whose deserialisation reads every page,
  which is the one thing paging exists to avoid — so `ModelImporter` writes the records with
  `WithoutData()` and the geometry beside them, and `StreamMeshletPageSource` seeks into the second.
  Building the pages at *import* rather than at load is the other half of the same point: finding a
  mesh's extent and snapping every vertex to a grid is work proportional to the whole mesh, which is
  exactly the work streaming exists so a frame never does. A build that shipped only the DAG would
  have moved it to load time with an identical picture to show for it.

---

### Phase 3 — Hierarchical cluster culling · ~2.5 EM · ✅ built

The centre of the system, and the phase that reuses the most.

- **Traversal**: workgroups over the DAG. Pop a node, project its error, and either accept its
  cluster (error under threshold) or push its children. Frustum, normal cone and Hi-Z rejection
  happen at every level, so a rejected subtree costs one test.
- **A permutation of `Culling.rvn`, not a new shader.** See **improvement 3**.
- **Output**: the visible cluster list, plus page requests for clusters whose data was not resident —
  which is what makes streaming demand-driven rather than predictive.

**Exit:** the existing randomised CPU-mirror test extended to the cluster hierarchy — a
`GpuCulling.IsVisible` equivalent for the traversal, compared against a brute-force cut over random
DAGs. Sabotage: removing the normal-cone test, or projecting error with the wrong view, both fail it.

**Met** as `GpuClusterCullingTests`: `GpuClusterCulling.Traverse` against `Cut` over randomised DAG
shapes and thresholds, with both named sabotages failing it and one criterion added that is stronger
than either — **every path from a root to a leaf holds exactly one visible cluster**, which is the
property that makes a cut a cut rather than a set that happens to match an oracle. Two on a path is
the surface drawn twice; none is a hole.

Four things the plan above did not say, and the first is the one that would have shipped as cracks:

- **An error is projected at the *group's* bound, not at the cluster's.** A group's simplification
  produces several parents, every one of which replaces *all* of the group's children — so all of them
  have to reach the same refinement decision. They share an error already, because phase 1 gives a
  group one; what they also have to share is the **distance** it is projected at, and their own bounds
  are in different places. A per-cluster distance makes one parent refine while its sibling does not,
  and the surface is covered twice in one place and once in another. Found by the randomised
  comparison, which is exactly the class of defect it exists for, and `CullCluster.ErrorCenter` is what
  fixes it.
- **One entry point, not two.** Raven refuses a second compute stage in one shader (`RVN2050`), which
  turns out to be the constraint agreeing with improvement 3 rather than fighting it: the branch on
  the permutation is folded before lowering, so the object variant carries no queue, no barrier and no
  shared memory at all, and the cluster variant carries no per-word loop. A test asserts exactly that,
  because it is the half that would rot.
- **`Occluded` takes a sphere now.** That is improvement 3 made concrete rather than asserted: the
  object cull and the cluster traversal ask the same question of the same pyramid with the same
  matrix, and what differed between them was only which sphere. The host's `IsOccluded` and
  `ScreenBounds` were refactored the same way, so there is one occlusion semantic on each side rather
  than two on each.
- **The queue overflows by drawing coarser, not by dropping.** A workgroup's front is a fixed
  `groupshared` array — 4 KB of the 16 a device has to offer — and a group whose front does not fit
  stops refining and accepts what it has. Dropping the node would be a hole, and a shader cannot grow
  an array.

**Stopping here is defensible**, and what is left to reach a frame is host plumbing rather than
another idea: a render feature that fills the instance records from the scene, dispatches the
traversal, and hands the visible list to `DrawIndexedIndirectCount`. The decision the frame turns on
is built and checked.

**Stopping here is defensible.** With phases 0–3 you have continuous LOD, hierarchical device-side
culling, streamed geometry and no authored LOD chains, drawn by ordinary hardware raster through the
existing Forward+ pipeline. That is most of what users notice about Nanite, at roughly a quarter of
the total cost. See *Where to stop*.

---

### Phase 4 — Hardware-raster visibility buffer · ~2 EM · ✅ built

- One instanced indirect draw over the visible cluster list — the count is the traversal's output and
  the host never learns it, which is the call `GpuDrawArguments.Compact` already makes.
  [`GpuClusterRaster`](../Core/Vixen.Rendering/GpuClusterRaster.cs),
  [`ClusterRaster.rvn`](../Raven/Library/Pipeline/ClusterRaster.rvn), placed by
  [`VisibilityBufferRenderer`](../Core/Vixen.Rendering/Compositor/VisibilityBufferRenderer.cs).
- Output one `uint`: the visible-list slot and the triangle index, with ordinary depth test and depth
  write. No atomics, no 64-bit anything, no compute — which is why this is the portable baseline and
  phase 6 is the accelerator.

**Exit:** a golden-image comparison against the same scene drawn through `MeshRenderFeature`,
matching within the LOD error threshold.

**Not met in that form, and the substitute is stated rather than implied.** A golden image needs a
rasterizer, so it is a device test, and what is asserted here instead is the whole decode path on the
host: from a visible word through the instance, the geometry record, the slot table and the page bytes
to a world position, compared **exactly** against `MeshletPageSet.GetPositions` over every corner of
every cluster of a sphere — `ClusterRasterTests`. Exactly, not nearly, because the decode is an integer
addition and one multiply and two readers of the same bytes have to reach the same float. Both the
lost-origin and the slot-for-page-number sabotages fail it. The golden image is still owed and is
phase 5's natural companion, since a resolve is what makes the buffer into a picture.

Four things the plan above did not say:

- **`DrawIndexedIndirect`, not `DrawIndexedIndirectCount`.** The latter is for a *list* of argument
  structures whose length the device decides, which is what a compacted per-object draw needs. Here
  there is one structure and the device decides its `instanceCount` — so the whole of "draw what the
  traversal chose" is a four-byte copy from the visible list's count word into the argument buffer.
  That removes the `HasDrawIndirectCount` gate, which is worth having: the visibility buffer is
  reachable on every target the RHI supports rather than on the ones with the newer feature.
- **One `uint` per pixel, not `RG32_UINT`.** Twenty-five bits of visible-list slot and seven of
  triangle, packed. Half the bandwidth of a full-screen target every resolve pass reads, no new pixel
  format in three backends, and the bit budget is not tight — a cluster holds 128 triangles by
  construction and 33M drawn clusters is far past what a frame reaches.
- **The slot is stored biased by one, so the target can clear to zero.** A clear colour is four floats
  in every API the RHI wraps, so an integer target cannot be cleared to all ones — and zero has to mean
  "nothing covered this pixel" rather than "the frame's first cluster, its first triangle". An
  increment where a pixel is written, a decrement where it is read.
- **`firstInstance` is *not* how per-cluster data is reached, and the visible word had to change.** The
  packed word carried the instance's *cluster base*, which reaches the cluster record and not the
  instance's transform — so a raster could find the geometry and not where to put it. It carries the
  instance *index* now, which reaches both. And the residency bitset became a **slot table**: the
  traversal only asks the yes-or-no question, but the raster needs the slot, and two tables that have
  to agree about whether a page is present is how a cluster comes to be drawn out of a slot holding
  another page's bytes.

---

### Phase 5 — Material resolve · ~2.5 EM

This is where the plan deliberately diverges from Unreal. See **improvement 2** — the resolve bins
into per-material tiles and runs the *existing* Forward+ shading, rather than resolving into a
GBuffer and forcing the engine deferred.

- A compute pass classifies visibility-buffer tiles by material, producing an indirect dispatch list
  per material.
- Attribute reconstruction: fetch the triangle's three vertices from the page pool, recover
  barycentrics from the pixel, interpolate, and derive gradients analytically from the triangle plane
  rather than from quad derivatives, which do not exist here.
- Shading is `ForwardPlus.rvn` unchanged, reached through the bindless material record — which is
  built, so this is a lookup rather than a project.

**A second entry contract, not a second material system.** A material's shader receives interpolated
stage inputs from the rasterizer today; in the resolve it receives them from a fetch-and-interpolate
prologue. That is a permutation, and the composition system already has the shape for it — but every
virtualized material compiles a resolve variant alongside its forward one, which roughly doubles that
slice of the effect bundle. A build-time cost, not an architectural one.

⚠ **The vertex-side transform must agree bit-for-bit between raster and resolve.** Skinning and any
world-position offset run twice — once to rasterize, once to reconstruct — and a disagreement lands
attributes on the wrong surface. Worth an assertion rather than a comment.

⚠ **This is where MSAA goes.** A visibility buffer breaks it: per-sample visibility is four times the
buffer and a per-sample resolve, which is the trap deferred falls into. MSAA is one of the four
reasons [06](plan/06-rendering-pipeline.md) gives for Forward+ being the default — but it is P1 and
unbuilt, and TAA is shipped and owns its history, which is what Nanite leans on for the same reason.
So the cost here is a feature that does not exist yet, and a phase-5 decision should come with
marking MSAA as classic-path-only rather than leaving it a general promise.

**Exit:** the material tree's existing composition tests pass through the visibility path with no
shader source changes — the composed shading models produce the same image whether reached from a
normal draw or a resolve.

---

### Phase 6 — Software raster · ~3 EM · **optional, capability-gated**

B2 is built, so the language is no longer what stands in the way — but the gate was never the
language. Only worth doing once profiling shows sub-pixel triangles dominating, which is the regime it
is for: hardware raster wastes roughly 4× on triangles smaller than a quad. The capability gate is
real and now reportable: a shader using the packed word asks for `Int64` and `Int64Atomics`, and the
host picks the hardware-raster variant on a device that has neither.

Compute-based scanline raster over small clusters, 64-bit `atomicMax` packing depth above ID.
Clusters route to hardware or software by projected triangle size, decided during traversal.

**Exit:** identical output to phase 4 on the same scene, asserted per pixel, with the routing
threshold swept.

---

### Phase 7 — Shadows · ~2.5 EM

Virtual shadow maps, because a Nanite-class scene defeats cascades: the geometry is detailed enough
that cascade resolution becomes the visible limit. VSM pages are culled by the same traversal with a
different view record, and share the residency manager from phase 2.

Realistically a separate project. Named because a plan that stops at phase 6 has shadows that no
longer match the geometry drawing them.

---

## Improvements over UE5

The user asked for these specifically. Each is a place where Vixen's constraints or existing code
make a different choice available — not a claim that Epic missed something obvious, since several of
these are cheap here precisely *because* Nanite already proved the shape.

### 1. Skinned geometry designed in, not retrofitted

Nanite shipped rigid-only and gained skinning several versions later, after the cluster record and
the error metric had settled around a static object-space bound. Retrofitting cost Epic real work.

Vixen can pay for it once, at the start: each cluster carries a **bone index range** (available free
after reordering influences during the build) and a bound computed as the union over the skeleton's
motion envelope, which the animation importer can already produce. Traversal expands the bound by the
range's current motion, and everything downstream is unchanged.

The cost is a few bytes per cluster and a bound that is looser for a deforming mesh than for a rigid
one. The saving is not having two cluster formats.

### 2. Forward+ compatible resolve, instead of forced deferred

**This is the most consequential deviation.** Nanite's visibility buffer resolves into a GBuffer, and
that is a large part of why Unreal is deferred-first — with the forward path a separate, less
capable branch maintained for VR and mobile.

Vixen's default is Forward+ clustered, chosen deliberately: [06](plan/06-rendering-pipeline.md)
records that "bandwidth is far below deferred on mobile" and that mobile is first-class. Resolving
into a GBuffer would either abandon that or duplicate the shading path.

Binning the visibility buffer into per-material tiles and running the existing clustered forward
shading in those tiles keeps one shading path, one material tree, and mobile bandwidth. It also
avoids UE's material-depth full-screen passes: a material covering 1% of the screen dispatches over
1% of the tiles rather than rasterizing a depth-tested full-screen quad.

The honest cost: tile binning has a worst case UE's approach does not — a screen where every tile
holds every material degenerates to the same work with extra bookkeeping. Materials are spatially
coherent in practice, which is the assumption being made explicit rather than hidden.

### 3. One culling shader, one occlusion semantic

Unreal maintains Nanite cluster culling and GPU Scene instance culling as separate systems with
separate two-pass implementations.

Vixen already has `Culling.rvn` with an `Occlusion` permutation and a `CullPhase`. Cluster culling
should be a **permutation of that shader** over the same phase structure, because objects and
clusters are the same hierarchy at different depths, and because two implementations of "visible
against last frame's pyramid" is two places for the definition to drift.

✅ **Built that way**, and the language pushed it further than intended: Raven refuses a second compute
entry point in one shader, so the two dispatch shapes are one entry point branching on the
permutation — which means the object variant provably carries none of the traversal's shared memory,
and a test says so. The occlusion test now takes a **sphere** rather than an object, on both sides, so
"visible against last frame's pyramid" is one function with two callers rather than two functions.
`GpuClusterCulling` is a sibling of `GpuCulling` rather than a copy: the frustum test, the rounding
slack, the stage-mask intersection and the whole occlusion test are called from it.

### 4. A CPU reference for the parts that fail silently

The repository's established idiom — `GpuCulling.IsVisible` transliterating the shader, compared
against the definition over randomised input, plus a source assertion that the shader still contains
that arithmetic — applies unusually well here.

Nanite's worst failures are silent: an error metric that is non-monotonic at one DAG edge produces a
crack at one distance on one mesh. A CPU reference cut and a CPU reference rasterizer that agree with
the device bit-for-bit turn that class of bug into a unit test. Unreal does not have this, and
debugging Nanite artefacts is correspondingly unpleasant.

**The cut half is built.** `MeshletCut.SelectByError` is the linear scan the traversal of phase 3 will
do hierarchically, and `PixelError` is the projection it will mirror. The fallback mesh is cut by the
same code at a budget rather than a threshold, so the path that has to be crack-free is the path that
runs in every build.

### 5. DAG validity as a build error

Following from 4: assert monotonic error and locked-boundary equality across every edge **at import
time**. Nanite's crack-freedom is a property the builder is careful to maintain; making it a checked
invariant costs a validation pass over a structure already in memory, and converts the engine's most
notorious artefact class into a failed build.

**Built.** `MeshletValidator` recomputes the boundary sets from the positions rather than asking the
builder what it did, and a mesh that fails produces no clusters and an import error naming the group —
because a builder that is self-consistently wrong is the only interesting case, and shipping the
asset anyway is how the crack reaches a player.

### 6. One residency manager for geometry, textures and shadow pages

Unreal runs Nanite streaming, virtual texture streaming and the VSM page pool as three systems with
three budgets and three eviction policies.

Vixen has **none of them yet**, which is an advantage exactly once. A single page-residency service —
request buffer in, LRU eviction under one budget, one set of counters — serves geometry pages here,
texture mip tails in [08](plan/08-asset-pipeline-and-addressables.md), and VSM pages in phase 7. One
budget to tune and one place to profile.

Build it in phase 2 with all three consumers in view, or it will be geometry-shaped and the other two
will grow their own.

✅ **Built as [`PageResidency`](../Core/Vixen.Rendering/PageResidency.cs)**, and the seam that keeps it
honest is `IPageStore`: the service owns the request queue, the byte budget, the eviction order and
the counters, and knows nothing about where the bytes go or how they are read. What proves that is
not the interface but the test — `PageResidencyTests` drives the whole of it against a store that is
a dictionary, with no device and no geometry anywhere in it. The two other consumers are still
unbuilt; what has been avoided is their having to bring their own budget.

### 7. A portable baseline that does not need 64-bit atomics

Nanite is effectively SM6/Vulkan-1.2-class only. Making the **hardware-raster** visibility buffer the
baseline (phase 4) and software raster the capability-gated accelerator (phase 6) puts virtualized
geometry on GLES 3.1, WebGPU and MoltenVK — none of which offer 64-bit image atomics.

The fallback mesh from phase 1 covers WebGL2, which has no compute at all.

✅ **Built, and it turned out to need one thing less than the plan assumed.** `ClusterRaster.rvn` uses
no atomics, no 64-bit types and no compute, as intended — and it also needs neither
`HasDrawIndirectCount` nor `HasMultiDrawIndirect`, because the frame is *one* instanced
`DrawIndexedIndirect` whose instance count is a four-byte copy out of the traversal's own count word.
So the portability claim is stronger than it was written: the gate is plain indirect drawing, which
every backend the RHI wraps has.

### 8. Raster cost visible in the asset, not discovered in a profile

Unreal's programmable raster makes masked materials and world-position offset work under Nanite, at a
runtime cost that surprises people who did not know which lever they pulled.

Vixen's material compiler already knows at build time whether a material discards or perturbs
position — it is a property of the composed feature tree. Bake that into the cluster's flags, route
those clusters to a distinct raster permutation, and surface the count in the asset. The cost stops
being a mystery in a frame capture.

---

## Cost, and where to stop

| Phase | EM | Cumulative |
|---|---|---|
| 0 — Unblockers ✅ | ~1 | 1 |
| 1 — Cluster DAG ✅ | ~3 | 4 |
| 2 — Pages and residency ✅ | ~2 | 6 |
| 3 — Hierarchical culling ✅ | ~2.5 | 8.5 |
| 4 — HW-raster visibility buffer ✅ | ~2 | 10.5 |
| 5 — Material resolve | ~2.5 | 13 |
| 6 — SW raster (optional) | ~3 | 16 |
| 7 — Virtual shadow maps | ~2.5 | 18.5 |

[overview.md](overview.md) puts the *entire remaining roadmap* at ~8–11 EM. This system is still
roughly twice what is left of the engine. That is not an argument against it — it is an argument for
reading the phase boundaries as real decision points rather than as a burndown.

**Two honest stopping points**, where an earlier draft had three. The first has mostly happened: the
bindless plan closed the geometry buffer, the device-supplied draw count and the material records,
which is why phase 0 is now an afternoon's worth of Raven work plus one upload rewrite rather than a
project.

- **After phase 3 (~8.5 EM).** Continuous LOD, no authored LOD chains, hierarchical device-side
  culling, streamed geometry — drawn by ordinary hardware raster through the existing Forward+ path.
  **This was the recommendation and it has been passed.** It captures what people actually notice about
  Nanite (import a film-resolution mesh, it just works) without touching the shading architecture, and
  every phase in it is independently useful. Discrete LOD and `LodRenderFeature` keep working alongside
  it for meshes that opt out.

  Phase 4 has since been built, which moves the line rather than erasing it: what exists now is a
  visibility buffer with nothing that reads it, so the *drawable* configuration is still this one —
  virtualized meshes through the classic path — until phase 5 gives the buffer a resolve. That is the
  honest reading of where the system is, and it is why the next decision is phase 5 or nothing rather
  than phase 6.

- **After phase 5 (~13 EM).** Object count stops mattering to the CPU entirely. Worth it only if
  profiling shows draw submission, not shading or triangles, as the limit — and note that compaction
  already took a large bite out of exactly that cost, so measure against the *current* frame rather
  than against the one this document was first written for.

Phase 6 should be gated on a measurement, not a plan. Phase 7 is its own project.

## What is deliberately not planned

**Nanite-style GBuffer resolve.** Improvement 2 explains the alternative. If the deferred pipeline
([06 § Deferred](plan/06-rendering-pipeline.md)) lands first, a GBuffer resolve becomes a second
resolve permutation and costs little — but it should not be the only one.

**Mesh shaders.** `HasMeshShaders` stays a capability nothing uses. The cluster path is compute plus
indirect draws, which reaches strictly more hardware for the same result.

**Displacement and tessellation on clusters.** Unreal's Nanite tessellation is experimental and
interacts badly with the error metric, since displaced geometry invalidates the bound the cut was
chosen against. Not until the base system is stable.

**Translucency.** The visibility buffer stores one surface per pixel. Translucent geometry stays on
the classic path, exactly as it does in Unreal, and the fallback mesh is what it draws.
