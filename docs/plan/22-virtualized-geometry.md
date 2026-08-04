<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Virtualized geometry

A Nanite-class geometry pipeline for Vixen: one authored mesh, a cluster DAG built offline,
streamed pages, hierarchical culling on the device, and a rasterizer that does not care how many
triangles there are.

This document is the plan and the argument for it. It exists as a separate file for the same reason
[bindless-materials.md](23-bindless-materials.md) does — several things across the engine are blocked on
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
| GPU visibility, one dispatch over every view | ✅ | [GpuVisibilityGroup.cs](../../Core/Vixen.Rendering/GpuVisibilityGroup.cs) |
| Hi-Z pyramid, min-reduced, per view | ✅ | [HiZRenderer.cs](../../Core/Vixen.Rendering/Compositor/HiZRenderer.cs), `HiZReduce.rvn` |
| **Two-phase occlusion** — `Main` then `Late`, answering the difference | ✅ | [GpuCulling.cs](../../Core/Vixen.Rendering/GpuCulling.cs) `CullPhase` |
| Draw arguments written by the device, bits never read back | ✅ | [GpuDrawArguments.cs](../../Core/Vixen.Rendering/GpuDrawArguments.cs) |
| A CPU mirror of the shader's arithmetic, randomised against the definition | ✅ | `GpuCulling.IsVisible` |
| Compositor nodes for all of it, assignments made by the builder | ✅ | `!GpuCulling`, `!HiZ` |
| Bindless table, descriptor indexing, Raven `Texture2D[]` | ✅ | [bindless-materials.md](23-bindless-materials.md) |
| **Material records** — the block as a record of one buffer, one bind per effect | ✅ | `MaterialRecords`, `[MaterialIndex]` |
| **Transform records** — the matrix out of the command buffer, index in `firstInstance` | ✅ | `UseTransformRecords` |
| **`GeometryBuffer`** — many meshes in one vertex and one index buffer | ✅ | [GeometryBuffer.cs](../../Core/Vixen.Rendering/GeometryBuffer.cs) |
| **`DrawIndexedIndirectCount`** — a draw whose count the host never learns | ✅ | all four backends |
| **Compaction** — survivors appended, one command per batch | ✅ | `GpuDrawArguments.Compact` |
| Discrete LOD with hysteresis and dither cross-fade | ✅ | [LodRenderFeature.cs](../../Core/Vixen.Rendering/Features/LodRenderFeature.cs) |
| Deferred/GBuffer *shaders* | ✅ | `Pipeline/GBuffer.rvn`, `Pipeline/Deferred.rvn` |
| **An incremental GPU scene** — object records rewritten only where they changed | ✅ | [PersistentUploadBuffer.cs](../../Core/Vixen.Rendering/PersistentUploadBuffer.cs) |
| **Geometry pages** — fixed-size, one quantization grid, roots in page zero | ✅ | [MeshletPageBuilder.cs](../../Core/Vixen.Rendering.VirtualGeometry/MeshletPageBuilder.cs) |
| **A residency service** — requests in, LRU out, one budget, not geometry-shaped | ✅ | [PageResidency.cs](../../Core/Vixen.Rendering/PageResidency.cs) |
| **The cluster traversal** — a permutation of `Culling.rvn`, with a CPU mirror | ✅ | [GpuClusterCulling.cs](../../Core/Vixen.Rendering/GpuClusterCulling.cs), `Culling.rvn` |
| **Workgroup-shared memory in Raven** — `groupshared`, `barrier()`, an atomic rooted in it | ✅ | B1 below |
| **64-bit integers and atomics in Raven** — `int64`/`uint64`, `Int64`/`Int64Atomics` reported apart | ✅ | B2 below |
| **The software raster** — compute scanline over the sub-pixel clusters, routed during the traversal | ✅ | [ClusterSoftwareRaster.rvn](../../Raven/Library/Pipeline/ClusterSoftwareRaster.rvn), phase 6 |
| **`SampleGrad` in Raven** — gradients the caller computed, in every stage | ✅ | B3 below |
| **The cluster DAG** — cluster, group, simplify with the group boundary locked, split, repeat | ✅ | [MeshletBuilder.cs](../../Core/Vixen.Rendering.VirtualGeometry/MeshletBuilder.cs) |
| **DAG validity as a build error** — monotonic error and boundary equality, per group | ✅ | `MeshletValidator`, `ModelCompiler.CompileMeshlets` |
| **A CPU reference cut**, and the fallback mesh cut from the same code | ✅ | `MeshletCut` |
| Meshlet generation in `ModelCompiler` | ✅ | [ModelCompiler.cs](../../Editor/Vixen.Editor.Assets/Models/ModelCompiler.cs), `generateMeshlets:` in the `.meta` |
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

Tracked as 🟡 in [07](07-raven-shader-pipeline.md) — "a storage class the language cannot
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
[07 § Workgroup-shared memory](07-raven-shader-pipeline.md).

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

The RHI now reports the device half of the same split: `GraphicsDeviceFeatures.HasInt64Atomics` is
`VK_KHR_shader_atomic_int64`'s `shaderBufferInt64Atomics` — the *bit*, not the extension, because a
device may offer the extension and decline the buffer atomic. ⚠ **MoltenVK on Apple silicon declines
it**, which is why phase 6's device test skips in this repository rather than passing or failing.

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

[`PersistentUploadBuffer<T>`](../../Core/Vixen.Rendering/PersistentUploadBuffer.cs), the sibling of
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

Built as [`Vixen.Rendering.VirtualGeometry`](../../Core/Vixen.Rendering.VirtualGeometry/README.md), called
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
  still draws at its coarsest level. [`MeshletPageBuilder`](../../Core/Vixen.Rendering.VirtualGeometry/MeshletPageBuilder.cs).
- **Page pool**: a single large buffer, suballocated. This is `GeometryBuffer` with a residency policy
  instead of a load-time one, different allocator.
  [`MeshletPagePool`](../../Core/Vixen.Rendering/MeshletPagePool.cs).
- **Residency manager**: requests in, serviced on the CPU against async I/O, LRU eviction under a byte
  budget. [`PageResidency`](../../Core/Vixen.Rendering/PageResidency.cs), and it is not geometry-specific
  — see improvement 6.

**Exit:** a camera path over a scene exceeding the budget by 4×, holding the budget and showing no
cluster popping beyond the configured error, with a synthetic I/O delay injected. The
budget-respecting criterion is already the one [08](08-asset-pipeline-and-addressables.md)
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
- **A group refines once, not once per parent** — found two phases later, by the sample's shutdown
  log, and recorded here because it is a phase-3 defect: every parent of a group carries the whole
  child set and the shared error centre makes them all refine together, so each pushed the same
  children and the duplication compounded per level, invisible to the cut property, the coverage
  comparison and the one-parent-per-group test fixtures alike. `CullCluster.GroupLead` designates the
  one parent that pushes; the story is under phase 5, and `ClusterTraversalGroupTests` holds it.

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
  [`GpuClusterRaster`](../../Core/Vixen.Rendering/GpuClusterRaster.cs),
  [`ClusterRaster.rvn`](../../Raven/Library/Pipeline/ClusterRaster.rvn), placed by
  [`VisibilityBufferRenderer`](../../Core/Vixen.Rendering/Compositor/VisibilityBufferRenderer.cs).
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
lost-origin and the slot-for-page-number sabotages fail it.

**The golden image now exists as a fixture, and it passes — after finding four defects, of which the
shader was guilty of none.** `VirtualGeometryGoldenTests` draws one plane twice — once through
`MeshRenderFeature` and once through the traversal and the raster, at the same camera — and compares
which pixels each covers. Coverage rather than colour, because phase 4 owns where the draw lands and
phase 5 owns what it looks like; and a plane rather than a sphere, because a flat quad's silhouette is
LOD-invariant, so whichever cut the traversal chooses covers the same pixels and "within the LOD error
threshold" stops being a number nobody can write down.

What it found, in the order it found it — worth keeping because each is a class of defect, not an
instance:

- **The fixture's own winding.** The plane faced away from the camera and the traversal's normal cone
  correctly rejected a mesh whose back was turned. Both rasters in the fixture are two-sided, so the
  forward image looked entirely normal — a two-sided raster hides exactly the mistake the cone test
  exists to catch. `VirtualGeometryDeviceTests`' plane had the same winding mistake, discovered only
  when it gained the assertion below.
- **What looked like a host/device divergence in the traversal was the frame assembly.**
  `ClusterTraversalAcceptsTests` ran the scene through the host mirror and accepted clusters; the
  device accepted none — and the shader and the mirror were *both right*, because they were fed
  different views. `GraphicsCompositor.Use` clears a view's stage mask on first use each frame — so a
  stage removed from the tree stops being collected for — and only stage nodes put bits back. A
  virtualized frame has no stage nodes, so its view reached the device with an empty mask and the
  traversal rejected every instance at the stage test, before the frustum. The probe that found it was
  a patched shader counting how far each invocation got: 64 lanes entered, none survived the record
  checks, and a bit-packed dump of what the device actually read showed `view.stagesLow == 0` against
  the host's 1. `VisibilityBufferRenderer.Stages` is the fix — every stage by default, or-ed into the
  view *after* `Use`, narrowable per document — because a cluster draw is not a stage and a mask of
  none is a buffer that is silently, permanently empty.
- **The raster's count copy was never ordered after the traversal's writes.** `GpuClusterVisibility`
  leaves the visible list in `ShaderRead` and says in so many words that the reader of the count
  transitions it; `GpuClusterRaster.Prepare` — that reader — copied without transitioning. The copy is
  what turns the count into the indirect draw's instance count, and a copy that reads early reads last
  frame's count, which on every first frame is zero.
- **The depth prepass was being culled out of the frame, and this is the one that held out longest.**
  The graph declared an attachment as a pure write, so a pass that only *clears* a target had no
  consumer culling could see, and the pass *loading* that target tested its fragments against
  undefined memory — NaNs on this driver, which fail every comparison and discard every fragment. An
  indirect draw with a verified count, a forced full-screen triangle and a valid pipeline drew nothing,
  at any depth, until the graph's own Graphviz dump showed the `ClearDepth` box dashed. Loading is
  reading now, in `ColourAttachment` and `DepthAttachment` both, which also means loading a transient
  nothing ever wrote is refused by name instead of being a picture that differs by driver.
  `RenderGraphTests` holds all three directions, and the two culling tests fail with the fix removed.

**Met, and asserted at two strengths.** The golden comparison passes — the two paths agree on all but
a fraction of a percent of pixels, the slack being edge pixels the page quantization moves — and
`VirtualGeometryDeviceTests` now asserts `VisibleClusters > 0` through the document-driven path, the
cheap half of the same claim per frame. That assertion is the one whose absence let all of the above
ship: `TraversedOnDevice` is set when a dispatch is *prepared*, so it is true of a dispatch that culls
the entire scene.

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

### Phase 5 — Material resolve · ~2.5 EM · ✅ built

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

✅ **Assertion written, now that there is a second transform to disagree.** Until skinning landed this
warning was vacuous: both passes decoded the same bytes and placed them by the same instance, and there
was nothing else to get wrong. The blend is now shared outright — both call `Skinning.BlendMatrix` — but
the four palette reads cannot be, because indexing a palette has to happen in the shader that declares
it or the whole 16 KB is copied at every call. So the fetch is duplicated, and
`SkinnedClusterTests.The_raster_and_the_resolve_skin_by_the_same_arithmetic` compares the two `Skin`
functions character for character. A tolerance there would be a tolerance for one of them fetching bone
1 where the other fetches bone 2, which is the entire failure mode.

⚠ **This is where MSAA goes.** A visibility buffer breaks it: per-sample visibility is four times the
buffer and a per-sample resolve, which is the trap deferred falls into. MSAA is one of the four
reasons [06](06-rendering-pipeline.md) gives for Forward+ being the default — but it is P1 and
unbuilt, and TAA is shipped and owns its history, which is what Nanite leans on for the same reason.
So the cost here is a feature that does not exist yet, and a phase-5 decision should come with
marking MSAA as classic-path-only rather than leaving it a general promise.

✅ **Marked.** [06](plan/06-rendering-pipeline.md)'s antialiasing table now reads *MSAA (classic path
only)* with the reason, rather than leaving a general promise a virtualized frame could not keep.

**Exit:** the material tree's existing composition tests pass through the visibility path with no
shader source changes — the composed shading models produce the same image whether reached from a
normal draw or a resolve.

**Met for the composition, and the remainder is named below.** All four shading models compose into
[`VisibilityResolve.rvn`](../../Raven/Library/Pipeline/VisibilityResolve.rvn) through the same two slots
`ForwardPlus` composes them into, reach both backends, and are distinguishable from the default — which
is the criterion, and nothing in `Material/` was changed to serve the resolve except the one thing the
resolve genuinely needs, below. `LibraryTreeTests` holds it.

The reconstruction is where the substance is, and it is checked against the *definition* of
perspective-correct interpolation rather than against a second solver: a world-linear attribute comes
back as the same linear function of the world point the pixel sees, over randomised triangles, cameras
and interior points. [`Barycentrics.rvn`](../../Raven/Library/Geometry/Barycentrics.rvn) and
[`ClusterAttributes`](../../Core/Vixen.Rendering/ClusterAttributes.cs), tested in `ClusterAttributeTests`.

Five things the plan above did not say:

- **Both silent failures are one line each, and only one of them shows in a picture.** Dropping the
  perspective correction is the classic affine error — plausible image, bending lines, a texture that
  swims across a floor. Correcting the *weights* but not their *derivatives* leaves the picture right
  and the mip selection wrong, which reads as a texture slightly too sharp at grazing angles and is
  invisible in a still. The second is a quotient-rule term, and the finite-difference oracle is the only
  assertion that catches it. Both sabotages verified.
- **The tolerance has to scale with the attribute's range, not its magnitude.** A triangle whose near
  corner is nine times nearer than its far one amplifies float32 error through the solve's division, so a
  fixed absolute tolerance is a test of the depth ratio rather than of the arithmetic. Found by the
  property test failing on a random seed after passing on twenty others — which is the property test
  doing its job twice.
- **A permutation on the *material tree*, not on the pass.** `MaterialTextures.UseAnalyticGradients`
  swaps `Sample` for `SampleGrad`, and it has to live where the sampling is rather than where the pass
  is: a compute stage has no quad, so the implicit form is undefined there and no runtime branch can
  help. `MaterialData` carries `uvDdx`/`uvDdy` for it, and every feature calls one `SampleSurface`
  instead of choosing. **This is the first consumer of B3**, which was built for exactly this.
- **The tangent is derived, and the resolve is better placed for it than a fragment stage.** A page
  vertex carries a position, a normal and a coordinate; a tangent is what those three imply, and
  deriving it needs the screen derivatives of two of them — which the analytic gradients already are. A
  fragment stage would have to interpolate a stored tangent and pay a channel for it.
- **The clustered punctual loop, the shadow cascades and the ambient term are now literally one
  implementation**, in [`ClusteredShading.rvn`](../../Raven/Library/Pipeline/ClusteredShading.rvn), which
  `ForwardPlus` and `VisibilityResolve` both derive from. What made the extraction possible is that
  lighting never needed the interpolators: those functions read a world position, a view-space position
  and an object index, and nothing else about how the pixel was found. Passing those three down as a
  `ShadingPoint` costs a forward fragment nothing and lets a compute stage supply them from a triangle it
  reconstructed.

**And the host is complete.** [`GpuVisibilityTiles`](../../Core/Vixen.Rendering/GpuVisibilityTiles.cs)
bins, with the counters doubling as the indirect dispatch arguments, and
[`GpuClusterResolve`](../../Core/Vixen.Rendering/GpuClusterResolve.cs) dispatches one indirect command
per material over that material's own bin — both recorded by `VisibilityBufferRenderer` in one pass, so
the ordering between them is not something a compositor document can get wrong.

**And the two links to the outside world exist.** The system was complete from import to shaded pixel
and had never run: `new RenderSystem()` appeared only in test projects, and the three artefacts a build
writes per mesh were read by nothing. `VirtualGeometryContent` reads them into a registered, streaming
mesh; `VirtualGeometrySystem` joins the six device objects that have to point at each other; and
`SceneRenderHost` turns a document, a device and a scene into a recorded frame.
`VirtualGeometryDeviceTests` runs all of it on real Vulkan.

**Four defects surfaced the first time a frame ran, and none of them could have surfaced earlier:**

- **The cluster passes' effect keys carried no composition**, so the raster, the binning and the
  traversal each failed `RVN2073` against an effect system serving the whole library — which is every
  application. Every test that had resolved one narrowed the source set to that pass's own packages.
  `MaterialCompiler.PassComposition()` is what they name now, and
  `ComposeSlotInventoryTests.APassCompositionBindsEverySlotTheLibraryDeclares` had been asserting that
  this existed while nothing used it.
- **The raster's index buffer and argument template were device-local and written from the host** — the
  third instance of that in this system, and the recording backend accepts it every time. Both are
  staged through host memory now, as the mesh records already were.
- **`VisibilityBufferRenderer` collected no view**, so a virtualized document produced a frame with no
  views in it: the traversal had nothing to choose a cut for and every pass ran and drew nothing. A
  virtualized document has no `SingleStage` in it — a cluster draw is not a stage — so this node is the
  only place a view can enter the frame, and it had no way to name one.
- **The bone palette buffer did not exist in a frame with nothing skinned**, and the raster declares the
  binding whether or not any instance reaches it. The frame's palette is seeded with its identity
  unconditionally now.

✅ **The application exists**: [`Samples/12-VirtualGeometry`](../../Samples/12-VirtualGeometry/README.md),
a `Vixen.App.Game` whose frame is `SceneRenderHost.Draw` — the document decides where the passes go,
the host decides what exists, and the visibility buffer reaches a swapchain as a debug view of the
cut. It runs the same document `VirtualGeometryDeviceTests` runs headless, plus a present pass of its
own, and `--vixen-frames N` makes it a CI check rather than only a demo.

**And the sample's shutdown log found a phase-3 defect on its first run** — the traversal accepting
1 376 clusters of a 442-cluster mesh. Every parent of a group carries the group's whole child set, and
the shared `errorCenter` — the thing that stops cracks — guarantees all of them refine at the same
moment: each pushed the same children, and the duplication compounded per level. On the host an
87-cluster sphere yielded a visible list of 1 016 entries, 34 distinct. **No existing test could see
it**: the cut property ("every path holds exactly one visible cluster") is true of a list with
duplicates, the golden image compares coverage that duplicates cover perfectly, and
`GpuClusterCullingTests`' hand-built DAGs have one parent per group — their own comment says so. The
consequence was not only waste: the duplicates multiply until they overflow the visible list's
per-instance capacity, and an overflowed list is a hole. `CullCluster.GroupLead` is the fix — one
parent per group, designated at flatten time, alone pushes and requests; the rest accept themselves
when the group cannot refine and otherwise contribute nothing — and `ClusterTraversalGroupTests`
holds it over a real multi-parent DAG, failing with the designation removed.

**And a document can now place the whole path**, which it could not while its sibling could:
`GpuCullingAsset` has had a node since phase 3 and the traversal one level down the same hierarchy could
only be assembled in code. `ClusterCulling` and `VisibilityBuffer` are the two nodes, on exactly the
terms the culling node already had — a document decides placement and the names, a host supplies the
device memory, and the same file builds on a project with no virtualized geometry into nodes that draw
nothing. Two nodes and not three, because the traversal has to precede the draw its answer feeds and the
draw has to share the classic geometry's depth, whereas the draw, the binning and the shading have an
order no file should be able to disagree with.

Three more things the plan did not say, all found by building it:

- **The variant key is the material's composition plus one permutation, spelled literally.**
  `EffectKey.From` wants generated `ParameterKey`s and the gradient key has none — it is declared on
  `MaterialTextures` and *inherited* into every sampling feature, which the reflection does not publish.
  Two names and two values is a small enough surface to write out, and the test asserts the composition
  reaches the key: without it every material resolves to the same default variant, which compiles,
  dispatches, and shades something grey.
- **`EffectSetWriter` could not bind a storage image at all.** It mapped `SampledTexture` and
  `StorageTexture` to the same write, so the resolve's output target was bound as a sampled texture. The
  null device's validation puts it better than this could: *no driver checks this and the shader reads
  whichever it was compiled for*. Fixed with a `DescriptorWrite.StorageImage` beside `Texture`; the
  resolve is the first set in the engine that both contains one and is filled through that helper.
- **Two buffers were device-local and written from the host**, which is not an error anywhere until a
  device says so — the binning's argument template, and every one of the traversal's mesh-record buffers.
  The template only ever feeds a copy, so it is host-upload now. The mesh records are read by the
  traversal for every cluster it tests, so they stay device-local and are staged through host memory with
  the copies recorded at the head of the frame, which is what `MeshletPagePool` already does for pages.

- **The resolve was reading last frame's records, and only skinning made it visible.** Three of the
  buffers both passes read are ringed per frame in flight — the instance records, the slot table, and
  now the palette. The raster bound each at this frame's descriptor offset; the resolve fills its set
  through `EffectSetWriter`, which binds a storage buffer *whole* and has nowhere to put an offset. So
  on every frame the ring was not at region zero the two passes disagreed about where every instance
  was — invisible in a static scene, because the regions hold identical bytes, and exactly the
  wrong-surface failure the warning above describes as soon as anything moves. Both now bind whole and
  add a base index, which is `TransformRenderFeature.BaseIndex`'s arrangement and the one that survives
  a writer with no offsets.

So the whole path exists: import → pages → residency → traversal → raster → bin → shade, and a resolved
pixel gets the same lights with the same shadows as a forward-drawn one because it is running the same
code.

**Three things the extraction turned up, none of which a compilation would have said:**

- **Inheritance merged a base's bindings and streams and not its permutations.** `ForwardPlus` came out
  reporting two of its eleven — the nine on the base vanished — which looks like a shader with no
  variants: the generated keys lose the names and a host asking for `UseShadows` gets the default with
  no diagnostic anywhere. `MergeInterface` merges them now, for inheritance only; a *composed* feature's
  stay its own, or one shader's variant space would be the product of every feature a material brings.
- **A composed model's parameters attach to the shader that declares the slot**, so moving
  `compose val shading` to the base moved `SubsurfaceShading.wrap` out of the pass's binding list, where
  `MaterialRenderFeature` looks for it. Each pass keeps its own slots and reaches the shared loop by
  overriding one hook — one forwarding line, and a material's parameters stay where a host already knows
  to find them.
- **A ternary reads both operands.** Building the `ShadingPoint` with
  `UseObjectRecords ? objectIndex : -1` declares that stream as a fragment input in *every* variant,
  while the vertex stage writes it only under the permutation — and a fragment input the vertex stage
  does not write is a pipeline the driver refuses outright. The reads are inside their own permutations
  now, matching the guards the writers use. Caught by the golden device tests, which is what they are
  for.

⚠ **The resolve composes no irradiance source**, so it gets sky ambient and not field ambient. A compose
slot has to be bound wherever it is *reached*, and the forward pass gets away with declaring one only
because its own reach is inside `if (UseIrradianceField)` — folded off by default — and because
`MaterialCompiler` names `NoIrradiance` for every material. Giving the resolve a field means giving the
library a default binding for the slot, which is a change to how compositions are defaulted rather than
a change to that file.

✅ **Done, and the change was to Raven rather than to either shader.** A compose slot may now name its
own default — `compose val irradiance: IIrradianceSource = NoIrradiance` — used in any compilation that
binds nothing. The resolve declares the slot and overrides `SampleIrradiance`, so it takes indirect
diffuse from the same place a forward draw does, and `GpuClusterResolve.IrradianceField` is the same
choice `UseIrradianceField` is on the forward path. Neither is on in a shipped frame today: indirect
diffuse arrives through `PostFx/IndirectDiffuse.rvn`. What changed is that the two paths now have the
same choice rather than different answers to it.

Three things about the diagnosis above turned out to be wrong or beside the point, and the difference
matters because it is what made the fix a language feature:

- **Reachability had nothing to do with it.** `ReportComposeIssues` runs over every slot of every type
  in the compilation, folded or not, reached or not. The forward pass survives because
  `MaterialCompiler` names a filler for it, and *only* because of that — the `if (UseIrradianceField)`
  guard was never load-bearing.
- **So the obligation was on the whole compilation**, which is why the alternative to declaring the slot
  was so unattractive: declaring one here would have obliged every compilation of the library to bind
  it, including the ones compiling something else entirely.
- **And `MaterialCompiler.OptionalSlots` was the mechanism the plan was asking to change.** It is a
  hand-kept list of the slots the library declares that a material does not fill, whose own comment
  records the failure mode — a slot added to the library and not added there shows up as `RVN2073` the
  first time anything compiles a material, breaking every material in the project rather than the shader
  that declared the slot. It happened twice; the `ScreenProbes` package landing without its line refused
  every whole-library compilation in the golden suite. Every entry now also names its default in the
  `.rvn`, so the list stops being load-bearing and becomes what it should always have been: how a
  *project* names a real field where the library's default is the neutral one.

A slot's initializer is a bare identifier naming a shader and not an expression, so `RVN2072` changed
from "a compose slot cannot have an initializer" to "its initializer has to name a shader". Binding it
as a value would have reported the shader as a type used as a value, which is true of the syntax and
wrong about what it means.

---

### Phase 6 — Software raster · ~3 EM · ✅ built, **optional and capability-gated**

B2 is built, so the language is no longer what stands in the way — but the gate was never the
language. Only worth doing once profiling shows sub-pixel triangles dominating, which is the regime it
is for: hardware raster wastes roughly 4× on triangles smaller than a quad. The capability gate is
real and now reportable: a shader using the packed word asks for `Int64` and `Int64Atomics`, and the
host picks the hardware-raster variant on a device that has neither.

Compute-based scanline raster over small clusters, 64-bit `atomicMax` packing depth above ID.
Clusters route to hardware or software by projected triangle size, decided during traversal.

**Exit:** identical output to phase 4 on the same scene, asserted per pixel, with the routing
threshold swept.

Built as [`ClusterSoftwareRaster.rvn`](../../Raven/Library/Pipeline/ClusterSoftwareRaster.rvn) and
[`GpuClusterSoftwareRaster`](../../Core/Vixen.Rendering/GpuClusterSoftwareRaster.cs), with
[`SoftwareRaster`](../../Core/Vixen.Rendering/SoftwareRaster.cs) as the CPU reference improvement 4 asks
for, and it stays **off unless a host sets a threshold** —
`VirtualGeometryRenderFeature.SoftwareThreshold`, defaulting to zero. That is this document's own
instruction taken literally: where the crossover between a compute scanline raster and a quad-shading
fixed-function one falls is a property of the hardware, and a default that guessed would be a frame that
is slower for a reason nothing reports.

**The exit criterion is met on the host and unrun on a device, and the difference is stated rather
than blurred.** `VirtualGeometryGoldenTests.The_software_raster_draws_what_the_hardware_raster_draws`
is the per-pixel comparison across a swept threshold, and it **skips** on the only Vulkan device this
repository can reach: MoltenVK on Apple silicon reports `shaderBufferInt64Atomics = false`, which is
the capability gate working rather than failing. What runs is the half a host can assert —
`SoftwareRasterTests` and `ClusterRoutingTests` — and the fixture is written and waiting for hardware
that offers the atomic.

What it compares, when it does run, is **coverage and the triangle index** rather than the whole
identity word. The slot cannot be compared across two runs — the two rasters fill opposite ends of one
list and the order within an end is whichever atomic won — but the triangle is the same number for the
same surface however it was drawn, so agreement at every pixel of a frame is the claim the criterion
means.

Six things the plan above did not say:

- **The routing needs somewhere to put the clusters it diverts, and the hardware raster is a draw over
  a *prefix*.** One instanced `DrawIndexedIndirect` covers `visible[0]` entries starting at the front,
  so the clusters it must not draw cannot be among them. They go at the **back of the same buffer**:
  three counter words instead of one, hardware ascending from the header, software descending from the
  end. That costs no second binding and — the reason it was chosen over a second list — **no change to
  what a pixel means**. A pixel names an entry, an entry is an entry wherever it sits, and the binning
  and the resolve read one buffer and ask one question. A second list would have needed a bit of the
  identity word to say which, and a branch in every reader.
- **Two counters cannot bound two ends, and the third is not a duplicate.** A hardware append knows its
  own index and can only *read* the software count, which is stale-low — so a bound computed from the
  pair is optimistic exactly where the two regions are about to meet, and two clusters written to one
  word is a cluster drawn out of another's page. One exact reservation taken before either append makes
  the sum provably at most the capacity. It also moves the meaning of `VisibleOverflowed`: the
  reservation is what a frame's cut is judged against, not either raster's count.
- **The near plane is the contract, not a safety check.** The software raster does no clipping — a
  corner behind the eye projects to a position that is not wrong so much as meaningless — so the
  routing requires the cluster's *whole bound* to clear the near plane before it will divert it. That
  is what makes `w > 0` true of every corner of every triangle the path will ever see. Removing it
  fails no test in this repository and fails as geometry smeared across the screen at the one camera
  angle where a cluster straddles the plane, which is why `ClusterRoutingTests` asserts it by name.
- **A zero error scale had to be excluded, and it is the case that would have routed a whole scene
  wrongly.** `RenderView.ScreenHeightScale` is zero for a shadow cascade and a probe face on purpose,
  and that propagates as a zero error scale — under which every cluster reads as *infinitely small* and
  the entire scene goes to a raster meant for specks.
- **A compute pass cannot write the depth attachment, so the two rasters resolve in two steps.** The
  software pass atomically maxes into a packed depth-above-identity buffer of its own; a merge then
  asks, per pixel, whether what it found is nearer than what the hardware draw left behind. So the
  ordering between the two comes out of a real depth comparison rather than out of which ran last, and
  equal loses — which gives the fixed-function depth the last word on the pixels it drew. **The merge
  clears the buffer as it reads it**, which is the whole per-frame cost of clearing sixteen megabytes at
  1080p; only the first frame after an allocation needs a copy.
- **The pass is absent rather than dormant on a device without the atomic.** It declares a read of the
  depth target and a write of the identity buffer, and a graph that declared those would oblige every
  document to give it a sampled depth image and a storage identity image — on hardware that can never
  run the dispatch that wanted them. `VisibilityBufferRenderer` therefore tests
  `GpuClusterSoftwareRaster.Supported` before adding the pass at all.

And one thing the phase inherited rather than introduced: **twenty-five bits of slot is now a bound on
the list rather than on the accepted count**, because the software entries are at the far end of it. A
buffer longer than thirty-three million words would pack a slot that wraps into a triangle index, so
`GpuClusterVisibility` caps the allocation there — thirty-two thousand virtualized instances and a
hundred and thirty megabytes of list, which is far past any real frame and is a checked ceiling rather
than an assumption.

---

### Phase 7 — Shadows · ~2.5 EM · 🟡 built, with the caster path named as owed

Virtual shadow maps, because a Nanite-class scene defeats cascades: the geometry is detailed enough
that cascade resolution becomes the visible limit. VSM pages are culled by the same traversal with a
different view record, and share the residency manager from phase 2.

Realistically a separate project. Named because a plan that stops at phase 6 has shadows that no
longer match the geometry drawing them.

**Built as a directional clipmap and a map per spot**, and the map itself is whole: the address space
([`VirtualShadowMap`](../../Core/Vixen.Rendering/VirtualShadowMap.cs)), the marking pass that turns the
frame's own depth into page requests
([`VirtualShadowMark.rvn`](../../Raven/Library/Pipeline/VirtualShadowMark.rvn)), the physical pages and
their table ([`VirtualShadowPages`](../../Core/Vixen.Rendering/VirtualShadowPages.cs)), the atlas and the
dispatch ([`VirtualShadowAtlas`](../../Core/Vixen.Rendering/VirtualShadowAtlas.cs)), the node that orders
the four things ([`VirtualShadowRenderer`](../../Core/Vixen.Rendering/Compositor/VirtualShadowRenderer.cs))
and the lookup composed into the shading
([`VirtualShadows.rvn`](../../Raven/Library/VirtualShadows/VirtualShadows.rvn)). A document places it as
`!VirtualShadow`.

⚠ **The one sentence above that is not yet true is "culled by the same traversal".** The traversal
appends every view's cut to *one* visible list with no view tag on an entry — see `Cull.PackVisible`, which
packs an instance and a cluster and nothing else — so a per-page cut would need a list per view, which is
a change to phase 3's output rather than to anything in phase 7. Until it lands, a virtualized mesh casts
through the **fallback mesh** phase 1 generates for exactly this case: "what runs anywhere else the
virtualized path does not reach". The shadow's *resolution* is what phase 7 is for and that is fixed;
what is owed is the caster's level of detail matching the receiver's.

**Exit:** none was stated, and the honest reading of what is asserted is: the address space against its
own definition (`VirtualShadowMapTests` — the level selection's rounding direction, every page of a level
round-tripping through its projection, a page's window composing with its level to the identity, the
sixteen maps' page runs being disjoint) and the page lifecycle as a policy (`VirtualShadowPageTests`).
There is no device test: the picture a virtual shadow map draws is a picture, and the assertions that
would mean something about it — a shadow at a silhouette that a cascade blurs — are the ones a golden
image is worst at.

Six things the plan above did not say:

- **The snap is to a whole page, not to a texel, and that is the entire caching story.** A cascade snaps
  its centre to a texel so the sampling grid does not slide under stationary geometry. A clipmap level
  snaps to a *page* so that every page's world footprint is bit-identical from one frame to the next —
  which is what lets a page already drawn stay drawn. Snapping to a texel would leave the boundaries
  sliding, invalidate every page every frame, and produce a virtual shadow map with a picture nobody can
  tell from the working one and none of the point of it.
- **A page appears in the table when it is *drawn*, not when it is allocated.** A slot just handed over
  holds whatever the last page left in it, so publishing on allocation is a lookup reading an unrelated
  part of the world's depth: a shadow of something that is not there, in the right place, at a plausible
  depth. Absent-until-drawn makes the failure "unshadowed for a frame" instead, which is the direction a
  shadow is allowed to be wrong in — and it is what makes the fall-through below load-bearing rather than
  decorative.
- **The lookup answers a *sample*, not a number**, and the second field is what makes the feature
  additive. A map covers what its levels reach and what has been drawn so far, so "I have nothing for this
  point" is a frequent and different outcome from "fully lit". `ClusteredShading.Shadow` falls through to
  the cascades where the map did not answer, so a project turns phase 7 on and gets better shadows where
  its pages are rather than a hole where they are not.
- **A compose slot rather than bindings on the pass**, which is `PunctualShadows.rvn`'s arrangement and
  its reason verbatim: set 0 is written wholly or not at all, so declaring the atlas, the table and the
  level records on `ClusteredShading` would be three resources every existing host suddenly owed — and a
  host that does not fill them does not lose its shadows, it loses every draw in the pass.
- **A pass per page rather than one pass with a viewport per page**, and the clear is why: a
  `LoadAction.Clear` clears the whole attachment, so one pass would throw away every cached page in the
  atlas — the one thing this system exists not to do. It is also why the atlas is the node's own texture
  and not a graph transient: a transient is discarded at the end of the pass that wrote it, which is a
  cache that never holds anything.
- **Improvement 6 has its second consumer, and the shadow pages are the case that tests the seam
  hardest.** There is nothing to load — a shadow page's content is *rendered* — so `IPageStore.LoadAsync`
  returns immediately with nothing and `Place` allocates rather than copies. Everything the service
  actually contributes (the request queue, the byte budget, the eviction order, the counters) is exactly
  as meaningful for a page that is drawn as for one that is read, and nothing shadow-shaped had to be
  added to `PageResidency` to make it fit. `VirtualShadowPageTests` drives all of it with no device in
  the file.

Still owed, in the order it matters: clusters casting through the traversal (needs per-view visible
lists); point lights, which are six maps rather than one and want a cube address space; and per-caster
invalidation, which today is per-level — a light that turns or a level that moved invalidates all of its
pages, and a *moved object* invalidates nothing at all, so a dynamic caster's shadow is only correct
because the page it is in keeps being re-marked.

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

**✅ Built, through the pages, the raster and the resolve.** A skinned mesh's page vertex carries four
bone indices and four weights, a byte each, after its normal and coordinate; the raster blends them
through `Skinning.BlendMatrix` — the same function `ShadowCaster` uses — before placing the vertex, and
the resolve does it again on the same bytes by the same call. `VirtualGeometryRenderFeature.SetBones`
takes the same matrices `SkinningRenderFeature` does.

Five things it turned out to involve, none of which the paragraph above implies:

- **It cannot be a permutation, and that is the one real difference from the classic path.** A shadow
  pass picks its skinned variant per draw because there is a draw per object to pick it at. One
  indirect draw covers every cluster of every mesh in the scene, so "is this vertex skinned" has to be
  a value a mesh record carries and a branch every vertex takes. That also keeps a static mesh's page
  vertex at sixteen bytes rather than charging every rock in the project eight bytes of zeros:
  `RasterMesh.influenceOffset` is per mesh, with a sentinel for no skeleton.
- **A byte an index is exactly the palette, not a compromise.** `Skinning.MaxBones` is 256 because 256
  `mat4`s are exactly the 16 KB of uniform range Vulkan guarantees. A skeleton past that is a build
  error naming the offending bone, because the alternative — clamping — is a limb attached to the wrong
  joint on one character, which reads as a modelling bug.
- **The bound is expanded per instance rather than per cluster**, which is looser than the paragraph
  above by roughly the ratio of a cluster to a character. `CullInstance.motionRadius` bounds how far
  *any* point of the mesh can be moved by this frame's palette — an affine bound, `|(A − I)c + t|` plus
  the radius times the norm of `A − I`, maximised over the palette — and the traversal adds it to every
  cluster radius. It is exactly zero for a rest pose, so a skeleton standing still is culled as tightly
  as a rock. The per-cluster form needs per-bone extents on the device and a loop per cluster test, and
  nothing that reads this changes when it lands.
- **The record grew past a multiple of sixteen and had to say so.** `CullInstance` was 48 bytes and
  exactly a multiple of the alignment a `float3` member gives a struct, so nothing had ever needed
  declaring. Two more words made it 56 on the host and 64 on the device — and a stride that disagrees
  does not fail, it reads instance one out of the middle of instance zero. `CullObject` had carried an
  explicit padding word and the reason for it since phase 3; this is that reason arriving.
- ⚠ **`Skinning.BlendMatrix` had never been compiled**, because every caller of it reaches it inside
  `if (Skinned)` and that permutation is off in the shipped set. The first shader to reach it
  unconditionally produced invalid SPIR-V twice over — see below.

**Two Raven bugs, and both were in shipped code that no shader had ever reached.**

- `matrix * scalar` lowers to `MatrixMultiply` whatever is on the other side of the operator, and the
  SPIR-V emitter's shaped-product table had no case for it — so the operator fell past every case in
  the arithmetic switch into its default, which is `>=`. The emitted instruction was
  `OpFOrdGreaterThanEqual` **with a `mat4` result type**.
- `matrix + matrix` became `OpFAdd` on a matrix, which SPIR-V does not have: its arithmetic takes
  scalars and vectors only. Matrix operands are decomposed into columns now.

Both are fixed in `SpirvEmitter`. What they mean is that **GPU skinning has never produced valid SPIR-V
on any path** — the shadow pass and the GBuffer pass would have failed the validator the first time
anyone compiled their skinned variant. The library's own tests compile every shipped shader and did not
catch it, because a permutation folds the code away before it is lowered.

**And one thing that was the library's convention already**: a bare `Buffer<mat4>` is rejected by the
validator, because a matrix in a storage buffer has to state its stride and its majorness and a struct
member is the only place SPIR-V has to put them. `Transform.rvn` had made that decision and written
down the reason; `BoneMatrix` is the same wrapper for the palette.

### 2. Forward+ compatible resolve, instead of forced deferred

**This is the most consequential deviation.** Nanite's visibility buffer resolves into a GBuffer, and
that is a large part of why Unreal is deferred-first — with the forward path a separate, less
capable branch maintained for VR and mobile.

Vixen's default is Forward+ clustered, chosen deliberately: [06](06-rendering-pipeline.md)
records that "bandwidth is far below deferred on mobile" and that mobile is first-class. Resolving
into a GBuffer would either abandon that or duplicate the shading path.

Binning the visibility buffer into per-material tiles and running the existing clustered forward
shading in those tiles keeps one shading path, one material tree, and mobile bandwidth. It also
avoids UE's material-depth full-screen passes: a material covering 1% of the screen dispatches over
1% of the tiles rather than rasterizing a depth-tested full-screen quad.

The honest cost: tile binning has a worst case UE's approach does not — a screen where every tile
holds every material degenerates to the same work with extra bookkeeping. Materials are spatially
coherent in practice, which is the assumption being made explicit rather than hidden.

✅ **Built**, as [`VisibilityTiles.rvn`](../../Raven/Library/Pipeline/VisibilityTiles.rvn) and
[`GpuVisibilityTiles`](../../Core/Vixen.Rendering/GpuVisibilityTiles.cs) for the binning, and
[`GpuClusterResolve`](../../Core/Vixen.Rendering/GpuClusterResolve.cs) for the dispatch that consumes it
— one indirect command per material, over that material's own bin. The worst case is reportable rather
than merely acknowledged: `Overflowed` says a material wanted more tiles than its list holds, which is a
hole and not a slow frame.

**And answered rather than only reported.** A diagnostic that fires every frame and changes nothing is a
hole that documents itself, so the capacity is a uniform the host raises rather than a constant the
shader was compiled with: a frame that overflowed grows the lists to hold what it wanted, and the growth
is capped at the screen's own tile count — a material's list holds tiles that exist, so at that capacity
overflow is impossible and the growth has finished. Within a frame the overflow is still a dropped tile,
because a list is a device buffer and a frame cannot make one.

The report is still worth having with the policy behind it. The counts come back from a dispatch nothing
waited for, so the growth is a frame late by construction: `Overflowed` is how a host learns that a frame
*was* wrong rather than that frames *will be*, and `Growths` rising every frame is a scene whose
materials are genuinely scattered — the case the whole binning assumption is about, where the honest
answer is a smaller tile rather than a bigger list.

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
texture mip tails in [08](08-asset-pipeline-and-addressables.md), and VSM pages in phase 7. One
budget to tune and one place to profile.

Build it in phase 2 with all three consumers in view, or it will be geometry-shaped and the other two
will grow their own.

✅ **Built as [`PageResidency`](../../Core/Vixen.Rendering/PageResidency.cs)**, and the seam that keeps it
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

And phase 6 landing did not weaken it, which is the half worth stating: the software raster is a
*second* pass a device may not have, and a device without it draws the same picture rather than a
degraded one — the routing threshold is forced to zero and the compositor adds no pass at all. That is
not a hypothetical: MoltenVK is the case, on the machine this was built on.

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
| 5 — Material resolve ✅ | ~2.5 | 13 |
| 6 — SW raster (optional) ✅ | ~3 | 16 |
| 7 — Virtual shadow maps 🟡 | ~2.5 | 18.5 |

[overview.md](../overview.md) puts the *entire remaining roadmap* at ~8–11 EM. This system is still
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

Phase 6 is built and is gated on a measurement rather than on a plan — the threshold defaults to zero,
so a project that has not profiled its frame draws exactly what phase 4 drew. Phase 7 is its own
project.

Phase 7 has since landed as a map without its cluster casters — see the phase — which moves that line
too: what a project gets today is the sun's shadow at the resolution each pixel needs, cast by the
fallback meshes, and what is left is the caster's level of detail matching the receiver's.

## What is deliberately not planned

**Nanite-style GBuffer resolve.** Improvement 2 explains the alternative. If the deferred pipeline
([06 § Deferred](06-rendering-pipeline.md)) lands first, a GBuffer resolve becomes a second
resolve permutation and costs little — but it should not be the only one.

**Mesh shaders.** `HasMeshShaders` stays a capability nothing uses. The cluster path is compute plus
indirect draws, which reaches strictly more hardware for the same result.

**Displacement and tessellation on clusters.** Unreal's Nanite tessellation is experimental and
interacts badly with the error metric, since displaced geometry invalidates the bound the cut was
chosen against. Not until the base system is stable.

**Translucency.** The visibility buffer stores one surface per pixel. Translucent geometry stays on
the classic path, exactly as it does in Unreal, and the fallback mesh is what it draws.
