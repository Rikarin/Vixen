<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Terrain, grass and trees

An outdoor toolset: a sculpted heightfield stored as a stack of non-destructive edit layers, a splat
material generated from the layers a terrain declares rather than hand-wired per project, two editor
modes that own the viewport while they are active, grass scattered on the GPU from the terrain's own
weights and never stored, trees stored as instances and culled per cell, and a spline that deforms
the ground it runs over.

This document extends [06 § Geometry and materials](06-rendering-pipeline.md) and
[20 § B6](20-editor-parity.md#b6--world-building), and it is a separate file for the reason
[24](24-blockout-tools.md) and [22](22-virtualized-geometry.md) are: it is larger than a row in a
status table, four other subsystems own a piece of it, and the first third of it is an argument
rather than a schedule.

**Read [the rows this overturns](#the-rows-this-overturns) before the phases.** This document
contradicts three decisions already recorded, one of them twice.

---

## The rows this overturns

Three rows, and they do not all move the same distance.

[20 § Part G](20-editor-parity.md#part-g--out-of-scope) lists, under things deliberately not being
built:

> **Terrain and foliage tools** — A whole subsystem — heightfields, layers, sculpting, procedural
> scatter, LOD, and a renderer for each — behind a mode. Post-1.0, and the `IEditorMode` seam in A1
> is what it will attach to.

Every clause of that is accurate, including the size. What has changed is not the estimate but the
*denominator*: when it was written, `IEditorMode` was a hypothesis with one implementation, the
viewport gathered meshes through the CPU every frame, there was no GPU culling, no bindless table,
no instancing feature, no page residency, no distance fields, and no `SampleLevel` in a vertex
stage. All eight of those now exist and were built for other reasons.
[Where Vixen already is](#where-vixen-already-is) is the whole of the argument for reopening this —
the subsystem did not get smaller, the thing it stands on got much larger.

[20 § B6](20-editor-parity.md#b6--world-building) carries the same decision as a panel row —
**Terrain / foliage · ⛔ · Post-1.0** — and it becomes [Part 2](#part-2--the-authoring-surface).

[06 § Geometry and materials](06-rendering-pipeline.md) carries two:

| Row | What happens to it |
|---|---|
| **Terrain (clipmap, virtual texture) · P2** | Promoted, and **half of it is rejected on the merits**. A geometry clipmap is the wrong structure here and the reasons are in [D3](#d3-a-quadtree-with-a-morph-not-a-clipmap); a virtual texture is the right structure and is *deferred* rather than dropped, in [D7](#d7-no-virtual-texture-in-the-first-pass-and-the-loop-is-why) |
| **Impostors / billboards · P2** | Promoted, because it is not a general feature that terrain happens to want — it is the last two hundred metres of a forest, it has no other consumer, and building it anywhere else would mean guessing at its requirements |

And one thing that is not a row at all.
[docs/guide/editor/modes.md](../guide/editor/modes.md) already uses `TerrainPlugin` and `SculptMode`
as the worked example of the plugin contract. That was written as a plausible-sounding hypothetical.
It is a good example precisely because terrain is the obvious second consumer of the mode seam, and
this document is what makes the sample compile.

### Where the line goes

| In | Out |
|---|---|
| A sculpted heightfield, tiled, with edit layers | Voxel terrain, overhangs, caves, arbitrary topology |
| Sculpt · smooth · flatten · ramp · erosion · hydro · noise · holes | A node-based procedural terrain generator |
| Weight layers, painted, with weight / height / alpha blending | Hand-wiring a splat material per project |
| Grass and detail meshes scattered from the terrain's own weights | Per-blade physics, grass that reacts to a character |
| Trees and props as painted persistent instances, on any surface | Being a vegetation modeller — import from SpeedTree or a DCC |
| An offline growth simulation that bakes to instances | A live ecosystem simulation at run time |
| Splines that deform the ground and place meshes along it | A road network, a river system, a city generator |
| Heightfield collision, and tree collision within a radius | Ten thousand live tree colliders |
| Impostors for the far field, baked from the mesh | Authoring impostor sheets by hand |
| Import and export of 16-bit heightmaps and weightmaps | A terrain format anybody else has to read |

⚠ **The test for the left-hand column is the same one [24](24-blockout-tools.md) uses, reworded:
does an environment artist reach for it between two lighting builds?** Erosion is on the left because
a ridge that has not been eroded reads as a cone; a biome graph is on the right because it is a
content-generation product, not a tool, and every engine that has shipped one has shipped it as a
plugin.

---

## What the references actually ship

Surveyed rather than remembered, because "Unreal has terrain" is not a specification.

### Unreal Engine 5 — Landscape

[The Landscape system](https://dev.epicgames.com/documentation/unreal-engine/landscape-outdoor-terrain-in-unreal-engine)
is three editor modes over one data structure.

**The data structure**, from
[the technical guide](https://dev.epicgames.com/documentation/unreal-engine/landscape-technical-guide-in-unreal-engine):
a landscape is a grid of *components*, "Unreal Engine's base unit for rendering, visibility
calculations, and collision", each holding its heights in a single texture whose vertex dimensions
are a power of two. A component splits into 1 or 4 (2×2) *subsections*, which is the LOD unit;
below that are *quads*. Heights are 16-bit, mapped to −256 … 255.992 and scaled by the actor's Z
scale — a fixed ratio of 1/512. Epic recommends no more than **1024 components** per landscape, and
publishes a table of eight recommended configurations from 127² up to 8129² vertices. Import formats
are 16-bit PNG, `r8`, `r16` and a raw form with a JSON sidecar.

**The three modes**:

| Mode | What it does |
|---|---|
| [Manage](https://dev.epicgames.com/documentation/unreal-engine/landscape-manage-mode-in-unreal-engine) | Create, import from file, resize, add and delete components, move to a streaming proxy, and edit splines |
| [Sculpt](https://dev.epicgames.com/documentation/unreal-engine/landscape-sculpt-mode-in-unreal-engine) | Sculpt, Smooth, Flatten, Ramp, Erosion, Hydro, Noise, Visibility, Mirror, plus Region Select and Copy |
| [Paint](https://dev.epicgames.com/documentation/unreal-engine/landscape-paint-mode-in-unreal-engine) | Paint, Smooth, Flatten and Noise, over the *target layers* the material declares |

**Layers, and the two things that word means.** A *target layer* is a paint channel: it needs a
**Layer Info Object** asset, it is either weight-blended (painting one reduces the others, and the
set sums to 255 at each vertex) or non-weight-blended (independent — the snow-over-everything case),
and the material declares it through `LandscapeLayerBlend`, which offers weight, height and alpha
blending. An *edit layer* is something else entirely.

**[Edit layers](https://dev.epicgames.com/documentation/unreal-engine/landscape-edit-layers-in-unreal-engine)
are the best idea in the system**, and it is worth being precise about why. Each is an independent,
non-destructive container of height, paint or visibility data; they stack, reorder by drag, carry a
heightmap alpha and a weightmap alpha separately (a negative height alpha subtracts), lock, hide and
collapse. Two are reserved and automatically managed: a **Splines** layer, which is where spline
deformation goes, and a **Patches** layer. The default limit is eight. The consequence is the point:
a spline moved after a mountain was sculpted does not have to un-sculpt anything, because the
mountain and the road never wrote to the same bytes.

**[Splines](https://dev.epicgames.com/documentation/unreal-engine/landscape-splines-in-unreal-engine)**
are control points and segments with a half-width and a cosine-blended side falloff, left and right
independently; they deform the ground, paint a blend mask along their width, and scatter meshes along
their length in randomised order. And — the sentence that matters — "landscape splines do not affect
the heightmap until a Spline edit layer is added to the edit layer stack".

**[Runtime Virtual Texturing](https://dev.epicgames.com/documentation/unreal-engine/runtime-virtual-texturing-in-unreal-engine)**
is how the landscape material stops being re-evaluated per pixel per frame and starts being a
shading cache: the landscape writes base colour, normal, roughness into an RVT through an output
expression, and decals, splines and meshes *read* from it to blend into the ground. Eight material
layouts, page table plus GPU feedback, `r.VT.MaxUploadsPerFrame` to keep page uploads from spiking a
frame, and low mips optionally pre-baked into a streaming virtual texture. Only static components
render into it, because it is a cache and expects stable content.

**[Nanite landscape](https://dev.epicgames.com/documentation/en-us/unreal-engine/using-nanite-with-landscapes-in-unreal-engine)**
is opt-in per landscape and is *built*, not derived: enabling it and pressing Build produces a Nanite
mesh whose topology is non-uniform, concentrating vertices where the terrain needs them. It improves
virtual-shadow-map cost and gives fine-grained streaming, at the cost of holding both
representations in memory, and it **must be rebuilt after sculpting** — an unbuilt landscape renders
the old shape.

### Unreal Engine 5 — grass and foliage, which are three systems

This is the part most summaries get wrong. Unreal has three placement mechanisms and they are not
alternatives; they are answers to different questions.

**1. [Landscape grass](https://dev.epicgames.com/documentation/unreal-engine/grass-quick-start-in-unreal-engine)
— derived, never stored.** A `LandscapeGrassType` asset holds an array of grass varieties (mesh,
density, grid offset, random rotation, align to surface, scaling, cull distance); a **Grass Output**
node in the landscape material binds a grass type to a layer's weight. Instances are generated from
the weightmap as the camera approaches and discarded behind it. Nothing about a blade of grass is in
the level file.

**2. [Foliage mode](https://dev.epicgames.com/documentation/unreal-engine/foliage-mode-in-unreal-engine)
— painted, stored.** Paint, Reapply, Single, Fill, Erase and Lasso, over a palette of foliage types.
A type carries density, radius, scale range, align-to-normal, cull distances, scalability
(`foliage.DensityScale`), and a **filter** for which surfaces accept it — landscapes, static meshes,
BSP, other foliage, translucent. Static-mesh foliage is hardware-instanced and batched
automatically; **actor foliage** places real actors and is explicitly documented as a performance
risk. Reapply is the tool worth stealing: it re-runs a *subset* of the type's settings over
instances that already exist.

**3. [The procedural foliage tool](https://dev.epicgames.com/documentation/unreal-engine/procedural-foliage-tool-in-unreal-engine)
— simulated offline, baked to (2).** A Procedural Foliage Spawner scaled to a region, driven by
per-type ecology: initial seed density, average spread distance, shade radius, max age, num steps
(read as simulated years), can-grow-in-shade, spawns-in-shade, overlap priority. A blocking volume
carves clearings. Resimulate re-runs it; the output is ordinary instances.

**4. [Nanite foliage](https://dev.epicgames.com/documentation/unreal-engine/nanite-foliage)
— experimental, and three separate ideas.** *Assemblies* micro-instance repeated parts (a branch, a
leaf) up to 65 000 per assembly, encoded into the hierarchy without duplicating geometry — a
demonstrated tree goes from 3.5 GB to 29 MB. *Voxels* replace triangles with near-pixel-sized
aggregates at distance, storing a normal *distribution* rather than a normal and sampling it
stochastically, which is what removes the billboard LOD entirely. *Skinning* replaces
world-position-offset wind with a bone hierarchy, because WPO forces conservative cluster bounds and
per-material dispatch binning; a hundred thousand bones update in about 0.1 ms, and animation is
disabled below a screen-size threshold.

### Unity — Terrain

[Unity's terrain](https://docs.unity3d.com/Manual/terrain-UsingTerrains.html) is one component over
a `TerrainData` asset, with a tool strip: Raise/Lower, Set Height, Smooth, Stamp, Paint Holes, Paint
Texture, Paint Trees, Paint Details. Its settings are the ones worth copying because they are
honest about being a memory budget — heightmap resolution, control-texture (splat) resolution,
base-texture resolution, detail resolution, pixel error, basemap distance, draw instanced. **Terrain
Layers** are reusable assets (textures, tiling, offset) shared across terrains, which Unreal's Layer
Info Objects are not. Trees and details are separate systems: trees are prototypes with real LODs
and colliders, details are grass billboards or detail meshes drawn in patches. A Terrain Collider
takes the heightmap directly. Neighbouring terrains are stitched explicitly.

### The consensus

Both engines, from very different starting points, ship the same four things:

1. **A heightfield with a component/tile grid**, where the tile is simultaneously the LOD unit, the
   culling unit and the streaming unit.
2. **Weight layers with a blend mode**, painted, summing to one, with a reusable per-layer texture
   set.
3. **Two placement models for vegetation, not one** — *derived* scatter that is recomputed from the
   surface and never saved, and *stored* instances that an artist placed and expects to find again.
   Grass is the first, trees are the second, and conflating them produces either a level file with a
   million entries in it or a forest that moves when the density slider does.
4. **Collision straight off the heightfield**, never a baked mesh.

And they disagree about exactly one thing worth having an opinion on: whether the terrain material
is authored (Unreal — every project rewires the same `LandscapeLayerBlend` graph) or configured
(Unity — layers are a list). [D6](#d6-the-splat-material-is-generated-from-the-layer-list) takes
Unity's side and says why.

---

## Where Vixen already is

Reconciled against the code, not against the plan docs. This table is the argument for the
schedule below being 16 EM rather than 30.

| Piece | State | Where |
|---|---|---|
| `IEditorMode` + `EditorModes` — a mode owns viewport input, claims keys by context, releases them | ✅ | [24 § B2](24-blockout-tools.md), `Editor/Vixen.Editor.Ui` |
| `PluginContext.AddMode` — and its documented example is literally `TerrainPlugin` | ✅ | [guide/editor/modes.md](../guide/editor/modes.md) |
| `InstancingRenderFeature` — one storage buffer, `firstInstance` as the offset, no maximum count | ✅ | [InstancingRenderFeature.cs](../../Core/Vixen.Rendering/Features/InstancingRenderFeature.cs) |
| `LodRenderFeature` + cross-fade — per view, after culling, before sorting, dithered not blended | ✅ | [LodRenderFeature.cs](../../Core/Vixen.Rendering/Features/LodRenderFeature.cs) |
| `GpuVisibilityGroup` — Hi-Z occlusion, indirect args, same `IVisibilityGroup` contract as the CPU path | ✅ | [IVisibilityGroup.cs](../../Core/Vixen.Rendering/IVisibilityGroup.cs) |
| `DrawIndexedIndirectCount` behind its own capability — one command per batch, count read from a buffer | ✅ | [23](23-bindless-materials.md) |
| `BindlessTable` — a texture is an index, so N layer textures cost one binding | ✅ | [23](23-bindless-materials.md) |
| **`PageResidency` — deliberately not geometry-shaped**: `PageKey(Source, Index)`, source never interpreted | ✅ | [PageResidency.cs](../../Core/Vixen.Rendering/PageResidency.cs) |
| `HiZPyramid`, `GpuCulling`, `GpuDrawArguments`, `GpuVisibilityTiles` | ✅ | Core/Vixen.Rendering |
| **`SampleLevel` in Raven, added so "a vertex stage sampling a heightmap can say which mip it means"** | ✅ | [07](07-raven-shader-pipeline.md) |
| **`Displacement.Wind`** — two frequencies, height-weighted stiffness, world-space offset | ✅ | [Displacement.rvn](../../Raven/Library/Geometry/Displacement.rvn) |
| `Displacement.AlongNormal`, `ParallaxOcclusion` | ✅ | same file |
| `MeshSimplifier` + `MeshletBuilder` + `MeshletPageBuilder` — a DAG, simplified, paged | ✅ | Core/Vixen.Rendering.VirtualGeometry |
| `SurfaceGeometry` / `GeometryBuffer` — many meshes in one vertex and one index buffer | ✅ | Core/Vixen.Rendering |
| `SnapContext`, `ScenePlacement`, `ISurfaceProbe`, `SurfaceHit` — a ray to a place to put something | ✅ | [24 § P0](24-blockout-tools.md) |
| `CommandStack` — merging, transactions, randomised do/undo/redo tests | ✅ | Editor/Vixen.Editor.Core |
| `ImportPipeline` + `ImporterRegistry` + `.meta` sidecars | ✅ | Editor/Vixen.Editor.Assets |
| `Vixen.Core.Imaging` — `TextureData`, mip chains, BCn/ASTC/ETC2, KTX2 | ✅ | Core/Vixen.Core.Imaging |
| `Vixen.Physics` on Jolt — bodies, shapes, queries, layers, ECS bridge | ✅ | Core/Vixen.Physics |
| `Vixen.Navigation` — a voxel bake over level geometry, and a consumer of whatever the ground becomes | ✅ | Core/Vixen.Navigation |
| Distance fields, surface cache, screen probes, traced reflections — the whole doc 19 chain | ✅ | Core/Vixen.Rendering.* |
| `[Inspector]` members over `[DataContract]` types — a settings panel with no dialog code | ✅ | Editor/Vixen.Editor.Inspector |
| `TerrainComponent`, anything heightfield, anything foliage | ⛔ | Does not exist. `Heightfield` in `Vixen.Navigation` is a navmesh voxel field and is unrelated |

**Eight of the nine things this needs from the renderer are built.** That is the whole reason the
row moves.

---

## What blocks it

Seven, and only two of them are large.

### B1. ~~There is no heightfield collider~~ ✅

`ShapeKind` is Sphere, Box, Capsule, Cylinder, ConvexHull, Mesh, Plane
([ShapeDescription.cs](../../Core/Vixen.Physics/Shapes/ShapeDescription.cs)). A terrain could be
given a `Mesh` shape and it would be wrong in the way that matters: a mesh shape is a BVH over
triangles, which costs memory proportional to the terrain and rebuilds whenever a brush touches it.
Jolt has a height-field shape natively; what is missing is `ShapeKind.HeightField`, the description
fields, the ECS bridge and the binding call. **Small, and on the critical path** — a terrain nothing
can stand on is a demo.


✅ **Built.** `ShapeKind.HeightField`, `HeightFieldPlacement`, `PhysicsShapes.HeightField(…)` and the
Jolt binding, in `Core/Vixen.Physics`. Three things came out of it that this document did not know:

- **The ECS bridge needed no change at all.** `Collider` holds a `ShapeId` and never asks what kind
  it is, so a terrain tile is a collider on the day the shape exists. That is the payoff of the
  existing design and it removes a line from [T0](#t0--unblockers--10-em---built).
- **The sample count must be a power of two**, for the reason in
  [D2](#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit)'s second warning.
- **Collision is quantised, and by a stated amount.** Jolt compresses eight bits per sample against
  its *block's* range, not the field's — so a flat tile is exact and a block spanning nine metres
  quantises to 3.5 cm. Both halves are asserted, because a loose tolerance on the second would also
  pass a mapping error of metres.

### B2. ~~Instancing culls a batch as one object~~ ✅

`InstancingRenderFeature`'s own remarks say it: "a forest drawn as one object with ten thousand
transforms is culled as one object, so its bounds have to enclose the whole forest — which also
means it is all-or-nothing. Batching by locality rather than by mesh is what keeps that from being a
regression, and it is the caller's decision because only the caller knows the scene's shape."

Foliage *is* that caller, and it does know the scene's shape — it is a grid. So this is not a defect
to fix but a contract to satisfy, and [D9](#d9-the-cell-is-the-batch-and-the-second-cull-is-on-the-gpu)
is how. What is genuinely missing is the second stage: a per-instance GPU cull that writes a
compacted instance list and an indirect draw. Every piece of that exists (`GpuCulling`,
`GpuDrawArguments`, `DrawIndexedIndirectCount`); none of it is wired to instances rather than to
render objects.


✅ **Both halves are built.** `InstanceCuller` in `Core/Vixen.Rendering` culls
per instance against the frustum and a cull distance, bins survivors into a contiguous ascending run
per LOD level, decides each survivor's fade, and fills one `DrawCommand` per level.

**The CPU reference first, deliberately** — [22 § improvement 4](22-virtualized-geometry.md), and the
shape `GpuCulling` already has. A per-instance cull fails silently in both directions: too few and the
forest has holes, too many and nothing looks wrong at all, it is merely slow.

✅ **The compute shader and its dispatch landed with [T5](#t5--foliage-instances--20-em)**, against
this as its oracle — `FoliageCull.rvn` and `FoliageCullPass`, compared survivor for survivor, level
for level and fade for fade.

⚠ **What is still owed is the Hi-Z test.** Neither half does occlusion: both do frustum and distance,
so a forest behind a ridge is culled by the ridge's own draw rather than before it. `GpuCulling`
already has the pyramid, and pointing it at instances is what closes this.

One thing the reference settled that the design did not state: **density scaling hashes the
instance's position, not its index.** A prefix or a stride satisfies "keep half of them" and fails the
property that matters — that lowering the setting keeps a *subset* of what a higher one kept, so the
quality slider thins the field instead of rearranging it. It is asserted both ways.

### B3. ~~There is no per-instance data beyond a transform~~ ✅

An instance in the buffer is a matrix. Foliage needs at least: a colour or tint variation, a wind
phase offset, an age or scale factor, and a per-instance LOD/fade weight. All four fit in one
`float4` beside the matrix, and the vertex shader already reaches its own record through
`gl_InstanceIndex`. ⚠ **This is a vertex-format and record-layout change in `Vixen.Rendering`**, and
it is the same class of change [24 § P5](24-blockout-tools.md) recorded as owed for vertex colours —
so the two should land together rather than twice.


✅ **Built.** `InstanceParameters` — tint, wind phase, scale, fade — in a buffer parallel to the
transforms, with `Vixen.InstanceParameters` as a second permutation so a crate field pays nothing.

⚠ **Parallel turned out to mean *literally* parallel.** A batch that supplies no parameters still
advances the second buffer, by `InstanceParameters.Neutral` per instance, because the shader reaches
both through one `gl_InstanceIndex` and two buffers that could drift apart would need a per-draw delta
to reconcile. The cost is sixteen bytes per instance whether used or not; the alternative was a class
of bug whose symptom is one forest wearing another tree's wind. `Neutral` is deliberately not
`default`: scale and fade are 1, so a forgotten record draws something rather than nothing of no size.

### B4. ~~`MeshData` has one UV set and no colour channel~~ ✅

`Positions`, `Normals`, `Tangents`, `TexCoords`, `Indices`, `BoneIndices`, `BoneWeights`. A tree
imported from a DCC carries its wind weights in vertex colour and its lightmap or detail coordinates
in a second UV; without either, wind falls back to the height-above-pivot heuristic
`Displacement.Wind` already implements, which is good enough for grass and visibly wrong for a large
tree whose branches should lag its trunk. **Not a blocker for the first four phases**; it is a
blocker for trees looking right, and it is doc 24's owed item, not a new one.


✅ **Built.** `MeshData.Colors` and `MeshData.TexCoords1`, filled by `ModelReader` from Assimp's
channel 0 and UV set 1, and absent when the file has neither — asserted, because an array of zeros
would read as "colours, all black".

⚠ **Carried, not yet drawn**, and that is the honest state. `SurfaceVertex` has no colour attribute,
so the channel reaches `ModelCompiler` and stops. Widening the shared vertex format costs sixteen
bytes on every mesh in the engine to serve the ones that use it, so the consumer is a foliage vertex
layout of its own in [T5](#t5--foliage-instances--20-em) — and the importer half had to land first,
because an importer that discarded the data leaves nothing to consume.

### B5. ~~There is no spline~~ ✅

Nothing in `Core` is a spline. [26](26-virtual-cameras.md) already recorded this as "the largest
owed item and the one most worth doing", because a dolly track needs one. Terrain needs the same
thing: control points, tangents, a segment evaluator, an arc-length parameterisation, serialisation
and viewport editing. **One asset, two consumers**, and [T8](#t8--splines--15-em---built) is where it gets
built — which retires doc 26's item at the same time.


🟡 **The curve is built; the asset around it is not.** `Spline`, `SplinePoint` and `SplineFrame` in
`Vixen.Core.Mathematics`: cubic Hermite, two tangents per point so a corner is expressible, an
orthonormal frame with roll, an arc-length table with a binary search, nearest-point, and Catmull-Rom
auto-tangents.

**In `Vixen.Core.Mathematics` rather than a project of its own**, because a curve is arithmetic and
both consumers — [26](26-virtual-cameras.md)'s dolly and this document's roads — already reference it,
so it costs no new project reference in either direction.

✅ **Serialisation as an asset and viewport editing landed with
[T8](#t8--splines--15-em---built)** — `SplineAsset`, `ISplineSource` and `SplineEdit`. What was closed
first is the part both consumers were actually blocked on: neither could start because there was no
curve to read.

⚠ **One decision is deliberately deferred rather than owed: `SplineAsset` has no descriptor**, so the
YAML binder cannot read one. Giving it one means `Vixen.Core.Mathematics` — the assembly holding
`Vector3` — taking a reference on `Vixen.Core.Reflection`, which is a change to the whole dependency
graph rather than to splines. The importer validates the file by hand instead.

### B6. There is no world streaming 🟡

`.vxworld` is a settings sidecar. `SceneManager` does additive loading into one world, which
[20 § B6](20-editor-parity.md#b6--world-building) correctly calls multi-scene editing — and it is
not distance-based streaming. There is no grid, no streaming source, no cell, no HLOD.

⚠ **This is the one thing terrain forces that terrain cannot pay for.** A 4 km² terrain at 1 m
resolution is 16 million height samples — 32 MB of heights, plus weights, plus every instance on it.
The mitigation is structural rather than aspirational: [D2](#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit)
makes the *tile* the unit of everything, including load, so the terrain is streamable by
construction and streaming it is a policy the day a streaming system exists. What this document
does **not** do is invent one — see [Risks](#risks).


🟡 **`StreamingGrid` is built, and it is deliberately less than the heading asks for.** It is the
policy half of [D13](#d13-streaming-rides-pageresidency): streaming sources with a radius, a grid of
cells over XZ, distance measured to the cell rather than to its centre, a lead ring that is requested
but not yet in use, and only the cells a source can reach visited. `PageKey.Source` keeps a terrain's
heights, its weights and a mesh's clusters apart in one pool, which makes this
[22 § improvement 6](22-virtualized-geometry.md)'s second real customer.

**It never evicts.** A cell that stops being wanted is simply not touched, and `PageResidency`'s LRU
reclaims the room when something else needs it — evicting on the way out would empty the pool whenever
a source turned round and refill it on the way back.

⚠ **This still does not make a scene stream, and the gap is exactly as this section described it.**
Terrain *bytes* now stream. A scene with ten thousand tree instances still loads all ten thousand,
because they are entities and a grid of blobs does not know what an entity is. That remains a document
of its own, and it is in [Risks](#risks) rather than quietly closed here.

### B7. ~~There is no brush, and there are about to be three~~ ✅

Sculpt strength over a falloff, paint weight over a falloff, and foliage density over a falloff are
the same function applied to different targets. Unreal implements them three times. [D12](#d12-the-brush-is-one-service)
says once, and it is the same argument [24 § D4](24-blockout-tools.md) made for snapping.


✅ **Built.** `Core/Vixen.Terrain`: `TerrainBrush` (shape, falloff curve, radius in metres, strength,
spacing, rotation mode), the four falloff curves as arithmetic on one number, `BrushStamp`,
`BrushStroke`, `IBrushMask` and `BrushFootprint`. No device, no document, one project reference.

Two properties are asserted rather than assumed, and both are the kind that a hand-picked example
would miss: **every falloff starts at one, ends at zero and never rises** — over every radius, every
strength and all four curves, as a property test — and **a stroke is spaced by distance, so the same
drag stamps the same whatever rate the pointer events arrive at**, with the leftover distance carried
across the join between segments. The random rotation is a hash of the stamp index rather than a
shared generator, so a stroke can be undone and redone to the same result.

---

## Part 1 — The design

### D1. Two runtime assemblies and one editor assembly, and the kernel touches no device

```
Core/Vixen.Terrain/              heights, edit layers, weights, brush kernels, tile mesh build,
                                 hole masks, heightmap import/export — pure functions over arrays
Core/Vixen.Foliage/              instance storage, cell grid, the scatter kernel, the growth
                                 simulation, the CPU reference for both
Core/Vixen.Rendering.Terrain/    TerrainRenderFeature, FoliageRenderFeature, the GPU scatter pass,
                                 the impostor bake — the only one that knows what a device is
Editor/Vixen.Editor.Terrain/     TerrainMode, FoliageMode, the tools, the panels, the commands
```

The split is [24 § D1](24-blockout-tools.md)'s, for its reason and one more. Its reason: a game that
ships a terrain needs the sculpting arithmetic at run time (a crater, a landslide, a moddable map),
and an editor assembly cannot be a runtime dependency. The additional one: **the kernel is where
almost all the tests are**, and a test that needs neither a device nor a world runs in milliseconds
and can be property-tested by the thousand.

`Vixen.Foliage` is separate from `Vixen.Terrain` because foliage is not a terrain feature. A foliage
type paints onto anything with a surface — a blockout mesh, an imported cliff, a rooftop — which is
what Unreal's surface filters are for, and folding it into the terrain assembly would make a rooftop
of moss depend on a heightfield.

### D2. The terrain is an asset and the tile is the unit

A `.vxterrain` asset holds the terrain; a `TerrainComponent` in a scene names it, with a transform.
This is Unity's split, not Unreal's, and there are two independent reasons.

**A `.vxscene` is the file two people touch every day.** That sentence is already
[20 § B6](20-editor-parity.md#b6--world-building)'s argument for putting world settings in a sidecar,
and it applies far harder here: a heightfield is tens of megabytes of binary and merging it is not a
thing. **And a terrain is reusable** — a test map, a lighting scene and the shipping level want the
same ground.

The tile is **a power-of-two number of *samples*** — 128 or 256, so 127 or 255 quads, with 128 the
default — and is the unit of *everything*:

| The tile is the unit of | Because |
|---|---|
| Storage | Each tile's heights and weights are one chunk in the asset, loaded independently |
| The quadtree root | [D3](#d3-a-quadtree-with-a-morph-not-a-clipmap) subdivides within a tile, never across |
| Culling | One bounding box, and its min/max height are maintained by the brush that changed it |
| Collision | One Jolt height-field shape, rebuilt per tile when that tile's heights change |
| Streaming | One `PageKey(Source: terrain, Index: tile)` — [D13](#d13-streaming-rides-pageresidency) |
| Undo | A stroke stores the rect it touched, clipped per tile — [D11](#d11-a-stroke-is-one-command-and-it-stores-a-rect) |
| Editing granularity | A brush marks tiles dirty; nothing outside them is re-uploaded, re-collided or re-scattered |

Heights are **16-bit unsigned**, mapped over a per-terrain height range in metres. Unreal's fixed
1/512 ratio and −256…255.992 window is a compatibility artefact of a 1998 file format; a range the
author sets means a 40 m rolling landscape gets 0.6 mm of vertical precision instead of 8 mm, for
the same bytes. The range is a property of the asset and changing it rescales, with the dialog
saying so.

⚠ **Tile boundaries are shared vertices, not adjacent ones.** A tile owns `n` × `n` samples spanning
`n − 1` quads, and its last row is the neighbour's first. Storing them twice is how a terrain grows a
seam that appears only after somebody edits one side, and it is one of the two classic bugs in this
subsystem. The other is [D3](#d3-a-quadtree-with-a-morph-not-a-clipmap)'s.

⚠ **The power of two is the sample count, and an earlier draft of this document had it on the quad
count.** It is not a stylistic choice — [B1](#b1-there-is-no-heightfield-collider-) is now built, and
Jolt's height field requires the sample count to be a multiple of its block size *and* the resulting
block count to be a power of two, which together mean a power-of-two sample count. 129 samples — the
round-sounding "128 quads" — is rejected. Unreal states the same constraint from the other end, as
section sizes that are "a power of two value minus one" quads; two engines arriving at 127 by
different routes is the constraint, not a convention. `PhysicsShapes.HeightFieldBlockSize` carries
the measured form, including a second bound Jolt does not document: **at least two blocks per axis**,
so the block size is capped at half the grid and a 2×2 tile is impossible.

### D3. A quadtree with a morph, not a clipmap

[06](06-rendering-pipeline.md)'s row said *clipmap*, and this rejects that half.

A **geometry clipmap** (Losasso and Hoppe, 2004) is a set of nested rings centred on the viewer,
each twice the extent and half the resolution of the one inside it, scrolled toroidally as the
camera moves. It is an excellent structure, and Vixen already runs one — the distance-field clipmap
in `Vixen.Rendering.DistanceFields` is "camera-snapped and scrolls". That is the case it is right
for: a volume with no authored topology, no editing, and no unit anyone names.

A terrain is the opposite of all three, and the ring is the problem. **A clipmap ring is centred on
the camera, so it aligns with nothing** — not the tile, not the collision shape, not the streaming
page, not the undo rect, not the region the artist just sculpted. Every one of those is a rectangle
in terrain space and the ring is a rectangle in camera space, so every interaction between them is a
scatter/gather. It also makes editing awkward in a way that is easy to miss until it is expensive:
the rings scroll and the data does not, so the mapping from a texel of a ring's vertex texture to a
sample of the terrain changes every frame the camera moves.

**CDLOD** (Strugar, 2010 — *Continuous Distance-Dependent Level of Detail for Rendering Heightmaps*)
is a quadtree over the heightfield. Each node is drawn as the *same* instanced grid patch, scaled
and placed; a node is selected when the camera is within its LOD range; and the vertex shader
**morphs** each vertex towards its parent-level position across the outer part of the node's range,
so a node has fully degenerated into its parent's silhouette by the time it is replaced. That morph
is the whole trick: transitions are continuous, there is no popping, and — the part that matters
more — **there are no cracks and therefore no skirts**, because at a boundary between two levels the
finer node has already morphed its shared edge onto the coarser node's vertices.

It maps onto what Vixen has, piece for piece:

| CDLOD wants | Vixen has |
|---|---|
| One mesh drawn many times with per-node parameters | `InstancingRenderFeature` — the offset is `firstInstance`, no maximum count |
| Per-node origin, scale, morph range, height page | An instance record; [B3](#b3-there-is-no-per-instance-data-beyond-a-transform-)'s extra `float4`, twice |
| Heights sampled in the vertex stage at a chosen mip | `SampleLevel`, added for exactly this |
| Nodes culled before they are drawn | `GpuVisibilityGroup` — frustum and Hi-Z, indirect args out |
| The mip a node reads | The tile's mip chain, which is `MipChain` in `Vixen.Core.Imaging` |

So a terrain frame is: select nodes on the CPU (a quadtree descent, a few hundred nodes, jobified),
upload one record each, cull on the GPU, and draw one indirect instanced call of a 33×33 grid patch.
**One draw call for the terrain**, and the grid patch is 2 KB of vertices shared by every node in
every terrain in the world.

⚠ **The morph must be computed from the node's own range, not from a global distance.** The
temptingly simple version — morph by camera distance over one global band — makes two adjacent nodes
at different levels disagree about how far morphed the shared edge is, which produces exactly the
crack the morph was there to prevent. The morph parameter is `(d − start) / (end − start)` in the
*node's* range, and the two levels agree at the boundary because the finer node's `end` is where the
coarser node's selection begins. This is the second classic bug, and it is a golden test.

### D4. Edit layers are the storage model, not a feature on top of it

Unreal added edit layers in 4.24, five years after Landscape shipped, and they are still per-landscape
opt-in. Building them second is what makes them opt-in: everything written against the flat
heightmap has to keep working.

So the composite is derived from the start. A terrain holds an ordered stack of layers; each layer
holds a sparse set of tile-sized height deltas and weight deltas — sparse because a layer that only
touched three tiles stores three tiles. A layer carries a height alpha (signed, so negative
subtracts), a weight alpha, a visibility flag and a lock. The **composite** heights are the base
plus each visible layer's delta times its alpha, evaluated per tile and cached; a brush writes into
the *selected* layer and invalidates the composite for the tiles it touched.

| Layer kind | Written by | Editable by hand |
|---|---|---|
| Base | The create dialog, or a heightmap import | Yes |
| Sculpt / paint | The brush | Yes |
| **Splines** | [T8](#t8--splines--15-em---built)'s solver, on every spline change | No — it is regenerated |
| **Scatter** | [T9](#t9--growth-simulation--10-em---built)'s simulation | No — it is resimulated |

The reserved layers are Unreal's idea and they are right for Unreal's reason: a road re-routed after
the mountain was sculpted must not have to un-sculpt the mountain. What Vixen adds is that a
reserved layer is *regenerated wholesale* rather than incrementally patched, which is only tractable
because it is a separate layer — the generator can clear its own deltas and write them again without
knowing what anyone else did.

Default limit: eight layers, as Unreal. It is a memory bound, and it is a setting.

⚠ **Collapse is destructive and the panel says so.** Collapsing a layer into the one below adds the
deltas and drops the layer; it cannot be undone except through the undo stack, and past the end of
that it is gone. That is the correct semantic and the wrong one to discover.

### D5. Weights sum to one, and the layer that broke it is named

Weight-blended layers at a sample sum to 1.0 (stored as four `u8` channels per texture summing to
255). Painting a layer raises it and lowers the others *proportionally*, which is Unreal's rule and
the only one that does not produce a layer that can never be removed. Non-weight-blended layers —
the snow case — are stored in their own channel and excluded from the sum.

Four channels per texture, so four weight-blended layers per weightmap; a terrain with nine layers
allocates three weightmaps per tile and the material samples three. **Bindless makes the count free**
at the binding level, and the loop is bounded at compile time by the layer count the material was
generated for.

The sum-to-one invariant is asserted after every paint operation in the kernel's tests, and the
assertion reports **which layer** the excess is on, because a weight-sum drift is a rounding bug that
shows up as a barely-visible tint and is otherwise unattributable.

### D6. The splat material is generated from the layer list

A `TerrainLayer` (`.vxlayer`) is Unity's reusable asset: albedo, normal, an ORM or per-channel
roughness/AO/height, tiling and offset in metres, a blend mode (weight, height, alpha), a height-blend
contrast, and a physics material for the ground it represents. A terrain asset holds an ordered list
of them.

The terrain's material is **generated** from that list. It is a Raven effect with the layer count as
a permutation constant, a bounded loop sampling each layer's textures through bindless indices,
blending by the declared mode, and writing an ordinary `MaterialSurface`. Nobody wires a graph.

The reason is not convenience. **Every Unreal project rebuilds the same `LandscapeLayerBlend`
material**, and every one of them rebuilds it slightly differently — which is why "why is my
landscape black" is the single most-asked landscape question, why the answer is usually a missing
layer info object or an unassigned coordinate node, and why the mapping-scale mistake is in the
official quick-start guide as a troubleshooting step. A configuration that cannot be miswired does
not need a troubleshooting step.

⚠ **The escape hatch stays open and is a different thing.** A project that wants a custom terrain
shader writes one and assigns it; the generated material is the default, not the mechanism. What is
*not* offered is editing the generated one, because the next layer added would silently regenerate
over it.

The layer count is the permutation axis, quantised to 4 / 8 / 12 / 16 so a terrain gaining a
seventh layer does not compile a new shader. Above 16 layers the material is virtual-textured or the
answer is "you want two terrains", and [D7](#d7-no-virtual-texture-in-the-first-pass-and-the-loop-is-why)
is where that goes.

### D7. No virtual texture in the first pass, and the loop is why

[06](06-rendering-pipeline.md)'s row said *virtual texture* and it is right about the destination.
It is wrong about the order, and here is the arithmetic.

An eight-layer terrain samples at most eight albedo, eight normal and eight ORM textures per pixel —
except it does not, because at any given sample most weights are zero and the loop skips them. In
practice a pixel touches two or three layers. Twenty-four bounded samples worst case, four to nine
typical, all through one bindless array with no per-layer descriptor: that is a normal material, not
a pathology.

**A runtime virtual texture solves a different problem than the one this has**, and it solves it by
adding a page table, a feedback buffer, an eviction policy, an upload budget, a compression format
choice and an entire second failure mode (a page that has not arrived). Unreal needs it because
Landscape materials are frequently forty layers of hand-wired blending with procedural noise in
them, and because RVT is *also* how decals and splines blend into the ground.

So the decision is: **build the direct sampler first, and build the RVT when a real terrain measures
badly or when spline/decal blending demands it.** When it is built, it rides `PageResidency` rather
than inventing a second residency manager — which is [22 § improvement 6](22-virtualized-geometry.md)'s
promise, made concrete: `PageKey.Source` distinguishes a terrain texture page from a meshlet page and
`PageResidency` "never interprets it". That is one budget, one eviction policy and one profiler view
for geometry, terrain, foliage and shadows, where Unreal has four.

### D8. Grass is derived, trees are stored, and the distinction is the density

This is the fourth item of [the consensus](#the-consensus) and it is the design decision most likely
to be got wrong by someone building this from a feature list.

**Grass and small details are scattered on the GPU from the terrain's own weights and never
persisted.** A cell entering range dispatches a compute shader that, per candidate position, hashes
the cell and index to a jittered position, samples the composite weight of the layer the grass type
is bound to, tests it against the type's density curve, samples the height and normal, rejects on
slope, and appends a transform to a per-cell instance buffer. The buffer lives in a ring of pooled
allocations and is dropped when the cell leaves range. **Nothing about a blade of grass is in any
file** — the level names a grass type and a layer, and a million blades follow from that.

**Trees, rocks and props are instances an artist placed, stored per cell in the scene.** They have
identity: a designer moves one, deletes one, and expects to find the result tomorrow. They carry
collision, they are lighting-relevant, and they are what a quest marker gets attached to.

The dividing line is **density × identity**: something you would place ten thousand of and never
name individually is derived; something you would place a hundred of and might name is stored. A
foliage type declares which it is, and the tools differ accordingly — the grass tools change a
*rule* (which layer, how dense, what mesh), the foliage tools change *instances*.

⚠ **Derived scatter must be deterministic from position, not from an iteration order.** The hash is
of the cell coordinate and the candidate index, so the same cell scattered on two machines, or by
the CPU reference and the GPU pass, produces the same grass. A counter-based or append-order-based
identity would make grass flicker as cells re-enter range, and would make the CPU/GPU seam test in
[Part 4](#part-4--testing) impossible to write — which is the same seam test
[19 § L4](19-lighting-and-global-illumination.md) used to hold the surface cache's two halves
together, measured at exactly zero drift.

### D9. The cell is the batch, and the second cull is on the GPU

[B2](#b2-instancing-culls-a-batch-as-one-object-) is a contract, and the cell satisfies it: a foliage
cell is a fixed-size square in world space (default 32 m, per type-group), it holds every instance of
one mesh within it, its bounds are tight, and it is what the instancing feature sees as one object.
A forest is a few thousand cells, each one culled by the existing frustum and Hi-Z path.

That is enough for trees and not enough for grass, because a 32 m cell of grass is fifty thousand
instances and the far half of it is behind a hill. So a second stage: a compute pass over the
surviving cells' instances that tests each instance against the frustum, the Hi-Z pyramid and its
own cull distance, writes the survivors compacted into a draw-order buffer, and fills a
`DrawIndexedIndirect` command. `GpuCulling`, `HiZPyramid`, `GpuDrawArguments` and
`DrawIndexedIndirectCount` all exist; what is new is that the granularity is an instance rather than
a render object.

The **LOD decision moves into that pass** for foliage, and this is a deliberate divergence from
`LodRenderFeature`. That feature is right for its case — a LOD group is several render objects and it
clears bits — and it cannot express "these four thousand trees in this cell are at LOD 1 and those
six hundred are at LOD 2", because the level is per object and here it is per instance. So the
compute pass bins each instance into its level's own indirect command and the cell draws three or
four times instead of once. The cross-fade weight rides in [B3](#b3-there-is-no-per-instance-data-beyond-a-transform-)'s
`float4` and the existing dithered discard reads it unchanged.

### D10. Collision is the heightfield, and trees are collided within a radius

The terrain's collider is one Jolt height-field shape per tile ([B1](#b1-there-is-no-heightfield-collider-)),
rebuilt for the tiles a stroke dirtied and nothing else. Holes are supported by the shape's own
masked-sample form.

Trees are the interesting case, because ten thousand static bodies is not a scene, it is a broadphase
problem. So: **a foliage type declares a collision shape and an activation radius, and instances
within that radius of a physics-relevant entity get a body.** The set is maintained incrementally by
the same cell grid the renderer uses, bodies are pooled, and the radius defaults to something
comfortably past the character controller's reach. A type with no collision shape never allocates
anything.

⚠ **This is a visible behavioural difference and it is stated rather than hidden**: a projectile
fired at a tree four hundred metres away passes through it. The alternative is a broadphase that
degrades for the whole game, and the mitigation available to a project that needs it is to raise the
radius for that type.

Grass never collides.

### D11. A stroke is one command and it stores a rect

The undo model is [24 § D3](24-blockout-tools.md)'s, specialised. A brush stroke — pointer down,
drag, pointer up — is one `CommandStack` entry holding: the layer it targeted, the union of the
tile-clipped rects it touched, and the before and after bytes of exactly those rects. A 256×256 rect
of 16-bit heights is 128 KB before and after; a typical stroke is a fraction of one tile.

Merging is off. Two strokes are two undos, which is what an artist means by "undo that", and it is
what every paint application does. What *does* merge is the intra-stroke updates — a drag is one
command being extended, not forty commands.

The stroke is applied to the **composite** for display as it happens and committed to the **layer**
at pointer-up, which is what makes a stroke feel immediate and still land in the right container.

### D12. The brush is one service

`TerrainBrush` carries: a shape (circle, a texture alpha, a tiling pattern), a falloff (smooth,
linear, spherical, tip), a radius in metres, a strength, a spacing (stamps per metre of travel), a
flow, and a rotation mode (fixed, random per stamp, aligned to stroke). It answers one question —
*for this world-space sample, what is the weight of this stamp* — and it does not know whether the
answer will scale a height, a layer weight or a scatter probability.

Three consumers, one implementation, one settings panel section, one set of tests. This is
[24 § D4](24-blockout-tools.md)'s argument for `SnapContext` applied unchanged, and the failure mode
it avoids is the one Unreal has: the sculpt brush, the paint brush and the foliage brush there have
different falloff curves, so a soft edge sculpted at strength 0.3 and a soft edge painted at strength
0.3 are different shapes.

### D13. Streaming rides `PageResidency`

A tile is a page. `PageKey(Source: the terrain's residency id, Index: tile index)`, requested by the
node selector one LOD level ahead of what it needs, evicted by the same LRU-with-a-budget that
already serves meshlet pages. A tile that has not arrived is drawn at whatever coarser mip *has*
arrived — which CDLOD makes trivial, because a coarser mip is a valid node at a higher level and the
morph already blends between levels.

This is the mechanism [B6](#b6-there-is-no-world-streaming-) is answered with, and the honest scope
is: it makes terrain *data* streamable, which is most of the bytes. It does not make the scene
streamable, and a scene with ten thousand tree instances still loads them all. Foliage cells are
addressable the same way and the hook is left; the policy that would drive it is a world-streaming
system, and that is not this document.

---

## Part 2 — The authoring surface

[20 § B6](20-editor-parity.md#b6--world-building)'s ⛔ row, made concrete. This is the half of the
work that decides whether the subsystem is used.

### Two modes, not three, and not one

Unreal has Landscape and Foliage as separate modes and it is right, for a reason that is easy to
mistake for history: **they filter different things.** Sculpt and paint require a terrain and act on
its texels. Foliage paints onto *any* surface — a terrain, a blockout mesh, an imported cliff, a
roof — and its filter set is the feature, not an accident. One mode that did both would need the
target-surface question answered twice with different answers.

So:

| Mode | Context key claim | Requires |
|---|---|---|
| `TerrainMode` | `terrain` — `1`–`7` select tools, `[`/`]` size, `-`/`=` strength, `Shift` inverts | A selected `TerrainComponent` |
| `FoliageMode` | `foliage` — `1`–`6` select tools, `[`/`]` size, `Shift` erases | Nothing |

Both go through `EditorModes.Add`, both declare an `EditorCommand.Context`, and both therefore
release `1`–`9` back to the view bookmarks when they are not active — which is
[the mode seam's whole point](../guide/editor/modes.md) and needs no new machinery.

⚠ **`TerrainMode` with no terrain selected shows the create panel rather than an empty toolbar.**
Entering a mode that does nothing and says nothing is the state every one of these tools puts a new
user in.

### The terrain panel

One panel, four sections, over `[DataContract]` types with `[Inspector]` members — no dialog code,
which is [20 § B6](20-editor-parity.md#b6--world-building)'s bargain for world settings.

**Create / Manage.** New terrain, import heightmap, export heightmap, resize, add and remove tiles.
The create form takes tile size (63 / 127 / 255 quads), tile count in X and Z, metres per quad,
height range in metres, and a base height — and **shows the derived numbers as it is filled in**:
world extent in metres, total vertex count, height storage in MB, weightmap storage per layer in MB,
and the number of Jolt shapes. The `(derived)` readout convention is
[20 § B6](20-editor-parity.md#b6--world-building)'s from the lighting panel, and it belongs here more
than there: this is the dialog where a person accidentally asks for 8 GB.

Import accepts 16-bit PNG and raw `r16`, with a size and endianness form for the raw case, through
`Vixen.Core.Imaging`. Export writes 16-bit PNG. Weightmaps import and export per layer as 8-bit
grayscale. ⚠ **Import is a layer operation, not a replace** — it writes the selected edit layer, so
a terrain heightmap imported from World Machine can be sculpted on top of without being destroyed,
and re-imported without losing the sculpt.

**Edit layers.** The stack, top to bottom, drag to reorder. Per row: name, visibility, lock, height
alpha and weight alpha as sliders that update the viewport live, and a context menu with rename,
duplicate, clear, collapse-down and delete. Reserved layers (Splines, Scatter) render with their
generator's icon and refuse the brush with a tooltip that says which tool owns them.

**Target layers.** The paint channels the terrain's layer list declares, each with its weight
coverage as a small histogram, an assign/create control for the `.vxlayer` asset, and a
weight-blended / non-weight-blended toggle. Selecting one makes it the paint target.

**Brush.** [D12](#d12-the-brush-is-one-service)'s settings, shared by every tool in both modes, with
the tool-specific parameters appended below a rule.

### The sculpt tools

Seven, and the list is the intersection of the two references plus one.

| Tool | What it does | Notable settings |
|---|---|---|
| **Sculpt** | Raises; `Shift` lowers | Strength; *clay* mode, which accumulates against a plane rather than along the normal |
| **Smooth** | Averages within the brush | Radius of the filter, separately from the brush radius |
| **Flatten** | Pulls towards a target height | Target picked from the first sample of the stroke; eccentricity (flatten to a plane fitted to the brush, not to a level plane) |
| **Ramp** | Two picked points, a width and a side falloff, linear between them | Width, falloff, and whether it may raise, lower or both |
| **Erosion** | Thermal — material above the talus angle slides downhill | Talus angle, iterations, sediment carried |
| **Hydro** | Hydraulic — rain, flow, dissolve, deposit, evaporate | Rain amount, evaporation, solubility, iterations |
| **Noise** | Adds fractal noise | Octaves, lacunarity, gain, and a *ridged* toggle |

Plus **Holes**, which paints a visibility mask rather than a height — punched from the index buffer
at tile build and from the collision shape at the same time.

Erosion and hydro are iterative and **run on the job system over the dirtied rect**, not per stamp:
a stroke marks the rect, and the simulation steps while the button is held, which is what makes
erosion feel like a brush rather than a batch job. They are the two tools most responsible for a
terrain not looking like a heightmap, which is why they are in the first sculpt phase rather than a
later one.

⚠ **Erosion respects the layer it is writing to and the composite it reads from.** Eroding a
mountain on a layer above the base reads the composite (so the flow is right) and writes the delta
(so the base survives). Getting this backwards produces erosion that erases everything below it, and
it is the reason [D4](#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it) puts the
composite in the kernel rather than in the renderer.

### The paint tools

Four, over the selected target layer: **Paint** (raise the layer's weight, `Ctrl+Shift` to lower it
while the others rise proportionally), **Smooth**, **Flatten** (set to the brush strength), and
**Noise**.

The target-layer list is the panel section above; the tools are the strip. This is Unreal's layout
and it is correct: the layer being painted changes far more often than the tool.

### The foliage tools

| Tool | What it does |
|---|---|
| **Paint** | Adds instances of every selected type at the brush's density; `Shift` erases |
| **Single** | One instance at the cursor, of the selected type or cycling through them |
| **Fill** | Fills the surface under the cursor to its edges |
| **Erase** | Removes, filtered to the selected types |
| **Reapply** | Re-runs a chosen *subset* of a type's settings over instances that already exist |
| **Select / Lasso** | Selects instances, so the transform gizmo can move, rotate and scale them individually |

**Reapply is the one to get right.** It is Unreal's tool and it is what turns foliage from
place-and-regret into an editable thing: changing a type's scale range afterwards should be able to
re-roll the scale of existing trees without moving them, and re-rolling *everything* is not the same
operation. So the settings panel grows a checkbox per property while Reapply is active, which is
exactly how Unreal does it and is worth copying literally.

**Filters** decide what accepts a paint stroke: terrain, static meshes, blockout meshes, other
foliage, and a per-type surface-normal range. A stroke ray-tests through `ISurfaceProbe`, which is
already the seam `ScenePlacement` uses to answer "where does this go" — so painting onto a blockout
wall works on the day blockout meshes are probeable, with no foliage-specific code.

### The palette

A `FoliageType` (`.vxfoliage`) is an asset: the mesh or the LOD group, the material overrides,
density, radius (minimum spacing), scale range, align-to-normal and its strength, random pitch and
yaw ranges, slope range, altitude range, **a terrain-layer filter** (only spawn where this weight
exceeds a threshold), start and end cull distance, shadow casting, collision shape and activation
radius, and the derived/stored flag from [D8](#d8-grass-is-derived-trees-are-stored-and-the-distinction-is-the-density).

A `GrassType` (`.vxgrass`) is the derived counterpart, and it is smaller because most of the above
does not apply: mesh, the layer it reads, a density curve against that weight, jitter, scale range,
random yaw, align-to-surface, cull distance, and a wind profile.

The palette panel is a grid of thumbnails with per-type density multipliers and a checkbox, matching
both references — because a foliage palette is one of the few interfaces every artist already knows.

### What the scene sees

```csharp
[Component]
public partial struct TerrainComponent {
    public AssetHandle<Terrain> Terrain;
    public int LodBias;
    public bool CastShadows;
}
```

Nine tenths of the terrain is in the asset, which is [D2](#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit).
Foliage cells are a `FoliageVolume` component naming the cell grid and the instance chunks, stored
beside the scene rather than inside it for the same merge-conflict reason.

---

## Part 3 — Phases

Effort in engineer-months, on [14](14-roadmap.md)'s scale. **Total 16.0 EM**, which is larger than
[24](24-blockout-tools.md)'s eleven and is the honest number rather than a reason not to start.

**The cut line is after [T6](#t6--grass--15-em---built), at 12.5 EM.** Everything before it is a terrain an
artist can build a level on; everything after is polish, reach and the far field. Each phase below
states what stopping there leaves.

### T0 — Unblockers · 1.0 EM · ✅ built

`ShapeKind.HeightField` and its Jolt binding
([B1](#b1-there-is-no-heightfield-collider-)) — **the ECS bridge turned out not to need touching**,
because `Collider` holds an opaque `ShapeId`. The per-instance parameters beside the transform in
`InstancingRenderFeature`, with `MeshData.Colors` and a second UV set landing at the same time
([B3](#b3-there-is-no-per-instance-data-beyond-a-transform-), [B4](#b4-meshdata-has-one-uv-set-and-no-colour-channel-))
— which also closes [24 § P5](24-blockout-tools.md)'s owed vertex colours. `TerrainBrush` in
`Core/Vixen.Terrain` with all four falloffs and all three shapes, and its property tests
([D12](#d12-the-brush-is-one-service)).

**Exit:** a Jolt height-field body can be created from an array and raycast against; an instanced
draw carries per-instance data a shader reads; the brush's falloff never rises between its plateau
and its edge, for every radius and strength. **Met.**

⚠ **The exit criterion moved, and the original was not checkable.** It said the brush "answers
weights that integrate to a stated value over its own footprint" — which is true of a great many
wrong brushes, and false of a right one whenever the falloff fraction changes. What replaced it is
monotonicity plus the two endpoints, as a property over every radius and strength, because that is
what every consumer assumes and none of them checks.

**If you stop here** you have closed two owed items from doc 24, given the physics layer the one
shape it is missing, and put the spline both this document and [26](26-virtual-cameras.md) were
waiting on into `Vixen.Core.Mathematics`. Nothing is wasted.

### T1 — The heightfield kernel · 2.0 EM · ✅ built

`Core/Vixen.Terrain`: tiles with shared boundary samples, 16-bit heights over an authored range, the
edit-layer stack with sparse per-tile deltas and signed alphas, composite evaluation with per-tile
caching and invalidation, the seven sculpt kernels, the hole mask, weight storage with the
sum-to-one invariant, min/max height maintenance per tile, mip chain generation, and 16-bit
heightmap import and export.

No renderer, no editor, no device. This is where the property tests live and it is the phase whose
test suite is worth more than its code.

**Exit:** a terrain can be created, sculpted, layered, collapsed, saved, reloaded byte-identically,
and asked for its composite — entirely in a unit test.

✅ **Built.** `TerrainDescription`, `TerrainSamples`, `Terrain`, `TerrainEditLayer`,
`TerrainWeights`, `TerrainHoles`, `TerrainSculpt` and `TerrainStroke` — the shape and its derived
readout, the global sample grid, the layer stack with signed alphas and collapse, the composite with
per-tile caching and invalidation, the sum-to-one invariant with a checker that names the offender,
the seven sculpt kernels plus holes and paint, the stroke record, and raw 16-bit heightmap import and
export — and, since T7's pass over the owed list, **the mip chain**: `TerrainMips`, reduced by the
*maximum* rather than the average.

⚠ **An averaged mip sinks a ridge.** Four samples of which one is a peak average to a quarter of it,
so a mountain gets shorter every level and the silhouette a distant patch draws is not the mountain's.
A maximum keeps the ridge and raises the valleys, which errs towards geometry being *above* where it
should be — the direction that hides a crack rather than opening one.

⚠ **A tile is a power of two <em>plus one</em> samples, so a level is not half its parent.**
129 → 65 → 33 keeps the boundary sample on the boundary; halving the count instead drops the last row,
and the seam it opens is one texel wide and permanent.

⚠ **Heightmap I/O split, and the split is [D1](#d1-two-runtime-assemblies-and-one-editor-assembly-and-the-kernel-touches-no-device)
holding.** Raw `r16` is bytes and needs no reference, so it is here; 16-bit PNG needs
`Vixen.Core.Imaging` and belongs with the importer, which already depends on it. **Resampling turned
out not to be optional**: a terrain of four 128-sample tiles is 509 samples across and heightmaps come
out of World Machine at 512, 1024 and 2049, so they essentially never match. It is bilinear and
edge-to-edge — mapping by scale factor instead leaves a flat lip along two sides of every imported
terrain, which is subtle enough to ship.

Three things came out of building it that the design did not have:

- **The global grid is what makes [D2](#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit)'s seam
  warning structural rather than a rule.** Storing one grid and making a tile a *window* into it means
  a boundary sample has one home and cannot be written twice. What is left of the seam is the
  *caches*: a stroke on a boundary has to dirty both tiles, which is its own test.
- **`TileOf` had to pick a side, and the doc comment picked the wrong one.** A boundary sample
  answers as the upper tile's sample zero, which makes ownership the half-open range
  `[T·quads, (T+1)·quads)` and partitions the grid cleanly. The comment said "lower" and the
  implementation said upper; the implementation was right.
- **Recording an undo after applying the kernel is expressible, and produces an undo that restores
  the stroke.** `TerrainStroke.Record(brush, stamp)` computes the footprint itself so the wrong order
  cannot be written. This was found by a test that did it wrong.

**If you stop here** you have a terrain library with no way to see it. That is a real thing to have
built and a bad place to stop.

### T2 — The renderer · 2.0 EM · ✅ built

`Core/Vixen.Rendering.Terrain`: the shared grid patch, the quadtree node selector (jobified,
`RenderView`-aware), the instance record, the CDLOD morph in a Raven vertex stage through
`SampleLevel`, per-tile height and weight textures with their mip chains, the generated splat
material with its 4/8/12/16 permutation, the render feature, and the `TerrainComponent`.

`GpuVisibilityGroup` culls the nodes; the draw is one indirect instanced call.

**Exit:** a sculpted terrain renders, lit, at 60 fps over a 4 km² extent, with the no-crack golden
test passing at every level boundary and the morph asserted continuous.

🟡 **The half that needs no device is built, and it is the half that decides whether the other half
needs skirts.** `TerrainLodRanges`, `TerrainLodNode` and `TerrainLodTree` in `Core/Vixen.Terrain` —
the quadtree descent with frustum and distance selection, the per-level morph bands, the vertex
morph, and the bilinear read a morphed vertex needs. ✅ **Built.** `Raven/Library/Terrain/Terrain.rvn`, and `Core/Vixen.Rendering.Terrain` with
`TerrainGridPatch`, `TerrainNodeRecord`, `TerrainRenderer` and `TerrainComponent`. A terrain is one
indexed instanced draw over the patches the quadtree chose, one descriptor set, and no vertex buffer.

⚠ **An atlas of per-tile blocks: the split of the *layout* this section asked for, without the split
of the texture it did not.** Per-tile textures exist for *streaming* — a tile is the unit of load,
which is [D13](#d13-streaming-rides-pageresidency)'s whole argument. Drawing wants the opposite: a
patch straddles no tile boundary only by luck, so a texture per tile makes every straddling patch
either two draws or a shader sampling two textures. One texture holding a `TileSamples²` block per
tile is both, and `TerrainAtlas` is the layout.

⚠ **The blocks duplicate their boundary samples rather than sharing them, and that is what buys the
mip chain.** The packed heightfield is `TilesX × TileQuads + 1` wide because adjacent tiles share the
sample between them; giving each tile all `TileSamples` of its own costs 1.6% at a 128-sample tile and
makes every block a power of two starting at a multiple of one — so a 2×2 reduction of the atlas never
crosses a boundary. Reducing the packed grid would mix two tiles at every level, which is
[D2](#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit)'s seam arriving through the mip chain.

⚠ **Heights reduce by the maximum and weights by the average, and this is the one place both
appear.** A maximum on a weight makes every layer cover everything one level up, so a distant terrain
is every texture at once; an average on a height sinks a ridge, because four samples of which one is a
peak average to a quarter of it.

⚠ **The weights are sampled with `SampleGrad`, from the *packed* coordinate's derivatives.** An atlas
coordinate jumps by a whole block at every tile boundary, so the hardware's own derivative there is
enormous and it picks the coarsest level it has — a dark line one pixel wide along every tile edge, on
every terrain, which reads as a crack in the mesh rather than as a sampling bug.

⚠ **And a patch reads the level its step implies**, clamped to a *tile's* chain rather than the
atlas's own size: an atlas of thirty-two 128-texel tiles is 4096 wide and would allow thirteen levels,
of which only eight keep a block at a texel or more.

⚠ **Only what changed is re-uploaded, and the first frame is a special case that had to be
written.** A terrain built and then resolved has no dirty tiles at all, so a renderer that copied
only dirty rows drew a heightmap of zeros until somebody happened to sculpt — which reads as a flat
terrain rather than as a missing upload. After the first frame a stroke on one tile of sixteen moves
a fraction of the bytes, which is asserted.

⚠ **The shader takes no vertex buffer, and its reflection says so.** A regular lattice's positions
are two divisions of `SV_VertexID`, so uploading 33² of them per frame would be sending the shader
something it can count; `Terrain.reflect.json` has an empty `VertexInputs`. What is uploaded is the
index buffer, once, and one sixteen-byte record per patch.

⚠ **The morph is kept honest without a device, by the route `GpuCulling` already established.** Its
remarks name the gap a CPU mirror leaves — "what it cannot say is whether the shader still contains
that arithmetic" — and answer it with a source assertion in `GpuVisibilityGroupTests`.
`TerrainShaderParityTests` does both halves: the expression is still there, and a transliteration of
it equals `TerrainLodTree.MorphIndex` over every index and every morph. **A source assertion is
weaker than an execution and is chosen knowing it.** It catches the failure that happens — somebody
edits or deletes the morph and every level boundary opens — and the golden image catches the rest.

⚠ **This ordering is [§ Part 4]'s instruction, followed literally**: "the no-crack test must be
written before the renderer, not after it". Both properties it names are functions of the morph, so
both are unit tests and both existed before any pixel did. Three assertions carry it — a fully
morphed patch's shared edge lands exactly on its coarse neighbour's vertices, a fully morphed patch
has exactly half its resolution, and the selected patches tile the terrain **exactly once** (a gap is
a hole in the ground, an overlap is z-fighting, and both are counting arguments rather than pictures).

Two things the design did not state and the arithmetic forced:

- **A morph ratio of 1 is not a setting, it is a crack at every transition**, so `Validate` refuses
  it and says why. A band with no width leaves the finer node undegenerate exactly where the coarser
  one takes over.
- **A morphed vertex has to read the heightmap bilinearly.** It lands between samples for every morph
  but zero and one, and snapping to the nearest sample would reintroduce — in the thing that *reads*
  the morph — the pop the morph exists to remove.

**If you stop here** you have a terrain the engine can display and nothing can edit in place —
which is enough to import a World Machine heightmap and ship a level on it.

### T3 — Sculpt mode · 2.0 EM · ✅ built

`Editor/Vixen.Editor.Terrain`: `TerrainMode`, its context and key claims, the create/manage panel
with its derived readout, the edit-layer stack panel, the seven tools plus holes, the brush section,
the stroke command with its rect ([D11](#d11-a-stroke-is-one-command-and-it-stores-a-rect)), live
composite update during a drag, and per-tile collider rebuild on commit.

**Exit:** an artist creates a terrain, sculpts a valley, erodes a ridge, adds an edit layer, flattens
a building pad on it, hides the layer, shows it, undoes eight strokes and redoes them — and can walk
on the result in play mode.

✅ **Built, and the exit criterion is one test.** `TerrainMode` with its eight tools in the `terrain`
context, `TerrainEdit` with the stroke lifecycle, `TerrainStrokeCommand` and `TerrainHoleCommand`,
`TerrainLayerCommands` for the whole stack panel, and `TerrainCreateSettings`, `TerrainBrushSettings`
and `TerrainToolSettings` as the panel's three sections. The exit sentence runs end to end through a
real `CommandStack`, clause by clause, and finishes by dropping a ray on the building pad.

⚠ **The mode seam took no new machinery the second time, which is the claim [24 § B2](24-blockout-tools.md)
was built to make.** `1`–`8` are the tools while the terrain context has the focus and view-bookmark
recall everywhere else, because these commands declare a context and the bookmarks declare none.
Nothing in `Vixen.Editor.Ui` changed. Blockout's seam was a hypothesis with one consumer; it now has
two, and the second one cost a `Context` string.

⚠ **A pointer had to become a sample, and that turned out to belong in the kernel.** `TerrainPick`
casts a ray at the composited heightfield — box clip, march at half a quad, bisect — so the half of a
brush that is arithmetic is a unit test rather than something only a running editor can exercise. It
reads `CompositeAt`, the *definition*, rather than `Composite`, the cache: a pick happens mid-drag,
which is exactly when the cache is stale, and reading the cache aims every stamp of a stroke at the
ground the stroke started from.

⚠ **It intersects the bilinear surface rather than the triangles that are drawn**, and it has to.
Which two triangles a quad is split into depends on the level the patch was selected at and on
`TerrainGridPatch`'s alternating diagonal, so "the triangle under the pointer" would give a different
answer at two distances from the camera. The bilinear surface agrees with the mesh wherever the mesh
has a vertex.

⚠ **Holes needed their own stroke type, and the design did not say so.**
[D11](#d11-a-stroke-is-one-command-and-it-stores-a-rect) describes one stroke record; the seven sculpt
tools write signed deltas into an edit layer, but a hole is one bit on `TerrainHoles`, which lives on
the terrain rather than on a layer and has no alpha, no stack and no composite. `TerrainHoleStroke`
is the parallel record — same lazy before-image, same reason — and the two are separate because
recording a bit in a delta record would restore the wrong container's contents.

⚠ **The ramp previews by undoing itself, which is what makes a two-point tool live.** A ramp is one
shape between two points rather than stamps that accumulate, so each move of the second point undoes
the stroke and redraws it. This works only because the record captures its before-image *lazily*: an
undo puts the layer back to exactly what that image holds, so re-extending over new ground captures
the original values there too.

Four things came out of building it that the design did not have:

- **A locked or generated layer had to refuse the brush with a sentence rather than an exception.**
  "The layer Splines is managed by its generator" is an ordinary thing to try, and a mode that threw
  would take the frame down with the scene unsaved. What is not ordinary is a brush that silently
  does nothing, which is the version that gets reported as the tool being broken.
- **Undoing a layer removal needed `Terrain.InsertLayer`.** An undo built on `AddLayer` puts back a
  layer with the right name and none of its deltas — which passes any test that counts layers and
  loses an hour of sculpting. Collapse needed `TerrainEditLayer.Clone` for the same reason from the
  other end: it destroys *both* layers and the addition has no inverse worth computing.
- **`TerrainDescription.TilesOf` had to exist**, because three separate callers were about to write
  the same "which tiles does this rectangle touch" loop and one of them — the collider rebuild —
  would have used `TileOf` and left a strip of collision disagreeing with the ground beside it.
- **Flatten grew a direction, and it is Unreal's setting rather than an invention.** Cutting a pad
  into a hillside means *lower*; filling a dip in a road means *raise*. It also turns out to be what
  the clay brush is: a one-directional flatten to a plane taken at the start of the stroke, which is
  why holding the brush down builds a mesa instead of sharpening a spike.

⚠ **Resizing is `TerrainResize`, and it copies by sample index rather than by world position.**
Changing `MetresPerQuad` makes the same landscape physically larger rather than resampling it onto a
finer grid — resampling is what the heightmap importer does and it cannot preserve an edit layer's
deltas, because a delta between two samples is not a delta at either of them. Changing the *height
range* does rescale, which is [D2](#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit)'s
requirement, and a delta scales by the ratio of the two ranges rather than through `StoreHeight` —
putting a delta through the absolute conversion turns every edit layer into a uniform offset of the
whole terrain.

✅ **The panel's chrome is built.** `EditorTerrainPanels` in `Vixen.Editor.App` registers four
panels over the settings objects `Vixen.Editor.Terrain` already owns — create, edit layers, target
layers, brush and tool — with no dialog code, which is [20 § B6]'s bargain for world settings applied
to a toolset. The terrain and foliage ones are *mode* panels: `IEditorMode.Panel` names them, so
entering the mode opens the panel and leaving it closes it.

⚠ **The create form's derived numbers are on screen while it is being filled in**, not behind a
recompute button. `TerrainFacts` is extent, samples, height storage, weightmap storage per layer,
collision shapes and vertical precision — and this is the dialog where a person accidentally asks for
eight gigabytes. Its refusal is shown beside the numbers rather than only when Create is pressed: a
form whose only feedback is a button that does nothing is the shape of dialog people describe as "the
editor is broken".

⚠ **A reserved layer says which tool owns it rather than simply refusing.** Splines and Scatter are
regenerated wholesale, so a brush stroke into one would be erased the next time anything regenerated
it — and "nothing happened" is the worst possible way to learn that.

⚠ **Empty states say what to do.** No terrain, no palette, no growth run: each draws a row rather
than nothing, because every one of these panels is first met by somebody with none of the three.

**Owed within T3:** 16-bit PNG import and export, which belongs with the importer that already
depends on `Vixen.Core.Imaging` (raw `r16` is wired); and the `.vxterrain` asset itself, which
`TerrainMode.Created` hands out rather than writes.

**If you stop here you have shipped the thing this document is for.** Everything after is coverage.

### T4 — Layers and paint mode · 2.0 EM · ✅ built

The `.vxlayer` asset and its importer, the target-layer panel, the four paint tools, weightmap
allocation and growth as layers are added, the three blend modes wired through the generated
material, weightmap import and export, and the layer's physics material reaching the collider so a
footstep sound knows it is on gravel.

**Exit:** a terrain with six layers, painted, height-blended where it should be, with the sum-to-one
invariant asserted after ten thousand randomised strokes.

✅ **Built, and the exit criterion is two tests.** `TerrainLayerDescription` is what a `.vxlayer`
holds and `TerrainWeights` carries one per channel; `TerrainPaint` is the four tools;
`TerrainWeightStroke` is the undo record; `TerrainWeightmap` is the 8-bit import and export;
`TerrainSplat` in `Vixen.Rendering.Terrain` is the generated material's permutation and its packed
per-layer buffers; and `TerrainCategory`, `TerrainPaintTool`, `TerrainPaintCommand` and
`TerrainLayerSettings` are the editor's half. The ten thousand randomised strokes are a kernel test,
where a stroke costs microseconds; the six-layer session is an editor one, driven through the mode's
own strip and a real `CommandStack`.

⚠ **"Blend mode" means two different things and the design used the word for both.**
`TerrainBlend` is a *storage* question — whether a layer takes part in the sum-to-one budget — and
`TerrainLayerBlend` is a *shading* one: given the weight the storage produced, how the material
combines this layer's albedo. A layer is routinely weight-blended in storage and height-blended in
shading, so they had to become two names.

⚠ **A paint stroke's undo record holds every layer, not the one that was painted.** Painting one
layer lowers all the others proportionally, so restoring a single channel leaves the rest holding
what the redistribution gave them — every touched sample sums above 255, and the drift is reported
three operations later with no way back to its cause. `TerrainWeightStroke` records the whole row.

⚠ **And restoring it needed a new kernel operation.** Setting six layers back one at a time
redistributes six times, so the first five are moved again by the sixth and the undo lands *near*
where the stroke started rather than on it. `TerrainWeights.Restore` writes a whole sample in one
assignment — it is the only spelling that is exact, and it is safe precisely because the row it is
handed summed to the total when it was read.

⚠ **The digits had to become slot commands.** [§ Part 2] says `1`–`n` select tools, and there are now
two tool sets sharing them. Binding "Sculpt" and "Paint Layer" both to `1` in the `terrain` context
puts two commands on one chord and the keymap resolves that to whichever registered last — silently.
`terrain.tool-N` means what the design sentence means, "the third tool", and the named commands keep
the words an artist searches the palette for.

⚠ **A paint stroke rebuilds no colliders, and that is not an omission.** No height moved, so the
shape is the shape it was; what changed is which *material* each quad is, and that is read from the
weights when it is asked rather than baked in. Rebuilding would be a Jolt height field built to hold
the heights it already has, once per stroke. The first version did it anyway and a test caught it.

⚠ **The height blend needs two passes over the layers and could not avoid it.** A height blend has to
know the *highest* contender at a fragment before it can say how much any layer contributes, and that
is not known until every layer has been looked at. `HeightBlend` is one permutation for the whole
material rather than one per layer, so a terrain that blends only by weight compiles the first pass
out entirely — and eight layers with three modes between them is still one shader.

Three things the design did not have:

- **Removing a target layer is not invertible from the layer alone.** Its weight goes to the others
  in proportion, so putting the channel back leaves them holding what they were given.
  `TargetLayerCommand` records every channel and restores all of them, which for a few hundred
  thousand samples is the honest cost of an undoable removal.
- **A weightmap import cannot trust its file.** A mask painted in an external tool has no idea the
  other layers exist, so writing it verbatim breaks the sum at every sample it touches. The import
  goes through the same redistribution painting it by hand would have.
- **The panel's coverage histogram is one number.** A per-layer histogram over four million samples
  is a bar nobody reads; what the section is actually for is "this layer is at zero and I do not know
  why", which is the state you get into by painting over your base layer, and a percentage answers it.

✅ **The `.vxlayer` importer is built** — `TerrainAssetImporter` in `Vixen.Editor.Assets`, which
claims all four of this document's extensions and whose real work is validation rather than
conversion: they are already YAML in the engine's own dialect, and what the generic native importer
cannot do is *read* them.

**Owed within T4:** the layer *textures* reaching the device, which needs a texture source seam the
renderer does not have (the weightmaps, the scales and the blend buffer are uploaded).

**If you stop here** you have Unity's terrain, minus vegetation.

### T5 — Foliage instances · 2.0 EM · ✅ built

`Core/Vixen.Foliage`: the cell grid, instance chunks, the `.vxfoliage` asset, the placement rules
(radius rejection, slope, altitude, layer filter, normal alignment), and serialisation beside the
scene. `Vixen.Rendering.Terrain`: the per-instance GPU cull and compaction pass, per-instance LOD
binning, indirect draws per level, and cross-fade through the existing dithered weight
([D9](#d9-the-cell-is-the-batch-and-the-second-cull-is-on-the-gpu)). `Editor/Vixen.Editor.Terrain`:
`FoliageMode`, the six tools, the palette, the filters, and instance selection through the transform
gizmo. Collision within an activation radius ([D10](#d10-collision-is-the-heightfield-and-trees-are-collided-within-a-radius)).

**Exit:** fifty thousand painted trees over a terrain, culled per instance, LOD-binned, at frame
budget — and one of them selected, moved, and still there after a reload.

✅ **Built, and the exit criterion is two tests.** `Core/Vixen.Foliage` with `FoliageType`,
`FoliageCellGrid`, `FoliageChunk`, `FoliageVolume`, `FoliageScatter`, `FoliageStore` and
`FoliageCollision`; `FoliageRenderer` in `Vixen.Rendering.Terrain`; and `FoliageMode`, `FoliageEdit`
and the two commands in the editor. The culling half of the sentence is asserted where a frustum can
be pointed at it, and the authoring half — painted, selected, moved, reloaded — where a stroke can be
driven with world points.

⚠ **The mode requires nothing, which is what makes it a separate mode.** Sculpt and paint need a
terrain and act on its texels; foliage paints onto *any* surface, and one mode that did both would
have to answer "what is the target surface" twice with different answers. `IFoliageSurface` is
`ISurfaceProbe`'s question with the painted weight added, so the filters work without foliage-specific
code — painting onto a blockout wall works on the day blockout meshes are probeable.

⚠ **The digits are slots for the third time, and the seam still cost nothing.** Blockout claims 1–4,
terrain 1–8, foliage 1–6, and view-bookmark recall keeps all nine everywhere none of them has the
focus. Nothing in `Vixen.Editor.Ui` changed for any of the three.

⚠ **A foliage stroke's undo record is instances, not a rectangle.** Sculpt and paint both write a
grid, so their records are a rect of values; a foliage stroke writes a list, so what it holds is what
it added and what it took away. And **redo re-adds rather than re-scattering**: the scatter is
deterministic, so re-running it would produce the same trees *only if nothing else changed in
between* — which an undo stack does not promise.

⚠ **An address is not a reference, and that is the bug class of this whole phase.** `FoliageAddress`
is valid until its chunk changes, because removing an instance shifts the ones after it.
`FoliageVolume.Remove` sorts descending so a caller cannot get it wrong — and the trap is a *loop*
that removes as it goes, which Reapply's filter pass did until a test caught it.

Three things the design did not have:

- **`Extend` required a surface for every tool.** Erase does not need one, so `Shift`-erase silently
  did nothing wherever nothing answered — which is exactly where an artist is most likely to be
  cleaning up. The surface is now looked at only by the tools that place things.
- **A cell can survive the frustum and still draw nothing**, when every one of its instances is past
  the cull distance. Issuing three empty commands for it would be the cost of the batch for none of
  the benefit, so it does not become a batch — a third rejection the design named only two of.
- **Two draws from one hash have to be re-hashed, not sliced.** Slicing the yaw and the scale out of
  one hash's bits gives them correlated low bits, which shows up as every large tree facing the same
  way — a pattern an artist sees immediately and cannot describe.

✅ **The compute shader is built, and it was T5's last owed item.** `FoliageCull.rvn` and
`FoliageCullPass` in `Vixen.Rendering.Terrain`, held against `InstanceCuller` by
`FoliageCullParityTests` — [§ B2] built the reference precisely so both halves could be the same
arithmetic, and the seam test compares survivors, levels, runs and fades over four thousand instances
at zero drift.

⚠ **Two dispatches of one shader, because compaction needs a count before it needs a slot.** The
first phase counts each level's survivors; the second recomputes every verdict and claims a slot
within its level's run. That is `InstanceCuller`'s own two-pass shape and it is there for the same
reason. **Recomputing is cheaper than remembering**: storing the verdict would be four bytes an
instance of bandwidth each way against a dot product and a hash, and a pure function of data neither
phase writes cannot disagree with itself.

⚠ **The survivors are indices, not transforms.** Four bytes rather than sixty-four, the draw needs an
indirection either way because `firstInstance` indexes *something*, and it makes the device's output
directly comparable with `InstanceCuller.Survivors` — which is what a seam test wants to compare.

⚠ **The first stage stays on the host.** A forest is a few thousand cells and testing them beside the
code that already walks the chunks is cheaper than uploading their bounds so a dispatch can walk them
again. And the instances upload when the volume changes rather than per frame: they are megabytes and
they do not move.

The other two of T5's owed items — removing a palette entry, and the `.vxfoliage` importer — were
closed earlier.

**If you stop here** you have terrain and trees. This is the second natural stopping point.

### T6 — Grass · 1.5 EM · ✅ built

The `.vxgrass` asset, the CPU scatter reference in `Core/Vixen.Foliage`, the GPU scatter compute pass
keyed on the same hash, the per-cell ring of instance buffers with range-based creation and eviction,
density scalability as a runtime scalar, and wind through `Displacement.Wind` with a per-instance
phase from [T0](#t0--unblockers--10-em---built)'s `float4`.

**Exit:** grass follows the layer it is bound to, appears and disappears with range without popping,
scatters identically on the CPU and the GPU (the seam test, at zero drift), and costs nothing in any
file.

✅ **Built, and all four criteria are tests.** `GrassType`, `GrassWind`, `GrassScatter` and
`GrassResidency` in `Core/Vixen.Foliage`; `GrassRenderer` and `TerrainSurface` in
`Vixen.Rendering.Terrain`; `GrassScatter.rvn` and `Grass.rvn` in the shader library, with
`Displacement.WindPhased` beside the wind it extends.

⚠ **`Density` is the *candidate* density and never the placed one, and that is the shape of the whole
feature.** The device runs one invocation per candidate slot, so the dispatch extent cannot depend on
anything sampled — what the painted weight decides is what *fraction* of a fixed grid survives. A
density that varied with the weight would be a dispatch whose size depends on a texture read.

⚠ **The grid is the spacing, and there is no spacing check.** That is the one rule
`FoliageScatter` has that this deliberately does not: a minimum spacing is a query against what is
already placed, which makes a candidate's fate depend on the order the others were tested in — and
sixty-five thousand parallel invocations have no order. A candidate cannot leave its own slot, which
is why `Jitter` reaches half a step rather than a whole one.

⚠ **The seam test is a transliteration *and* a source assertion, and it needed both.** The
transliteration says the arithmetic is right — every stream, the candidate position and the density
curve, compared over thousands of candidates at zero drift. The source assertion says it is still
*there*, which is the failure that actually happens. Grass has no file to compare a device run
against, so if the two halves disagree the field simply differs between a machine with a compute
queue and one without.

Four things the design did not have:

- **`(float)uint.MaxValue` is not 4294967295.** A `float` has twenty-four bits of mantissa and rounds
  it up to 4294967296, so a shader written with the true maximum agrees to six decimal places and
  disagrees in the last bits of every draw — a field that is *almost* the same, which is the hardest
  drift to see and the easiest to introduce. The constant is named on both sides now.
- **A collapsed weight range is undefined in GLSL.** `smoothstep(e, e, x)` has no answer, and an
  artist dragging both ends of a range onto one number is ordinary — so both halves write the
  polynomial out with the same guard rather than calling the intrinsic.
- **The ring needs to reclaim from the furthest resident, not only to evict by range.** Every cell can
  be inside the eviction range and still be the wrong set: a view that swung round has a whole new
  neighbourhood nearer than the one behind it, and hysteresis alone makes the grass arrive seconds
  after the camera did.
- **A short-range field must not scatter into a long-range field's cells.** One ring serves every
  field and is held open to the largest cull distance, so residency is per cell and scatter is per
  cell *per field* — otherwise the near field pays the far field's bill.

**Owed within T6:** ~~the host that binds `GrassScatter.rvn`~~ — **built**: `GrassDispatch` writes the
cell records, owns the ring of device buffers and records the pass, bound by keys generated from a
published `GrassScatter.reflect.json`.

⚠ **A cell's run is filed under its *ring slot*, not its place in this frame's list.** A cell keeps
its buffer across frames — that is what the ring is — so filing under the loop index would move every
blade the moment a nearer cell arrived and pushed it down, which reads as the whole field jumping
whenever the camera moves.

⚠ **The counters are zeroed before the dispatch, not after.** A pass that cleared afterwards leaves
the buffer holding last frame's numbers for anything that reads it in between, and what reads it is
the indirect draw. The zeros are *copied* from a host buffer, because a command list can copy and
cannot fill.

✅ **The draw half is built too.** `GrassScatter.rvn` grew an `Arguments` permutation — one group per
cell, turning each cell's final count into an indirect command — and `GrassDispatch` owns the
argument buffer, writes the blade mesh's template into it and issues one `DrawIndexedIndirect` per
resident cell.

⚠ **It cannot be folded into the scatter.** The invocation that claims slot zero does not know how
many will follow it, and an indirect draw needs the *final* count — so the argument write has to be
after every candidate of the cell has retired, which is a second dispatch by definition.

⚠ **The count is clamped to the run's capacity, and this is not belt and braces.** `atomicAdd` hands
a slot to every candidate that passed the density test, including the ones that then returned because
the run was full — so on a cell whose weight is painted to one everywhere, `counts` is the number of
*candidates*, which is larger than the run. An unclamped instance count draws off the end of the
cell's blades and into the next cell's.

⚠ **One draw per cell rather than one for the field**, because a cell's blades are at its own ring
slot's offset and the slots a frame is using are not contiguous. A single multi-draw would need the
commands packed in slot order, which would mean rewriting them whenever any cell was evicted.

⚠ **The commands are indexed by the frame's list and the blades by the ring slot.** `CommandOf` takes
one and `RunOf` the other; confusing the two draws the right number of blades from the wrong place.

What is still owed here is a grass panel, which is deliberately not a mode, because [§ D8] says the grass tools change a *rule* and that
is a settings object beside the terrain panel rather than a fifth viewport mode; and **the hole mask
on the device side of the scatter**, which is the one rejection the two halves do not share — `TerrainSurface`
answers a miss over a missing quad and the dispatch does not, so a blade stands in the mouth of a
cave. The mask is not bound to `Terrain.rvn` either, because the drawn surface drops the *quad*, so
this lands with the per-tile texture work T2 already owes. It is stated in the shader's own header
rather than left for somebody to find.

**If you stop here** you have the whole of the consensus feature set. **This is the cut line.**

### T7 — Impostors and the far field · 1.0 EM · ✅ built

An octahedral impostor bake — a grid of views around the mesh, rendered to an albedo/normal/depth
atlas — as the last LOD of a foliage type, generated by the asset pipeline from the mesh it already
has. Uses `MeshSimplifier` for the intermediate levels rather than a second simplifier.

Closes [06](06-rendering-pipeline.md)'s **Impostors / billboards** row for its only real consumer.

**Exit:** a forest to the horizon with a measured draw-call and triangle count that does not grow
with distance.

✅ **Built, bake included.** `ImpostorGrid`, `ImpostorAtlas` and `ImpostorView` are the fold, the
atlas layout, the per-cell orthographic camera and the three-cell blend; `Impostor.rvn` draws the
result; and `ImpostorBake` records it — one render pass over the whole atlas with a viewport per
cell, and a callback that draws.

⚠ **One render pass, not one per cell.** A 9×9 grid is eighty-one cells, and a pass each would clear
and store a 1152-texel target eighty-one times to bake one tree — which on a tiler is eighty-one
full-frame resolves. The clear happens once and the viewport moves.

⚠ **The bake does not know what a mesh is, and that is the seam.** The caller owns the pipeline, the
buffers and the material; what the bake supplies is the camera and the rectangle. A baker that bound
a mesh would need an asset database in a class whose job is a render pass.

⚠ **`ImpostorAtlas.RectOf` already excludes the gutter, and padding it again is the mistake a test
caught here.** A double inset draws the tree into the middle four-fifths of its cell, which is not
wrong enough to look wrong — it is a silhouette a few per cent small, uniformly, which reads as the
impostor sitting at a slightly different distance than the mesh it replaces.

**Owed within T7:** the dilation into the gutter and the mip build. The chain is capped at
`MipLevels` and the gutter is left for a dilation pass, so an atlas straight out of the bake has a
hard edge at each cell's border and one level.

⚠ **A *hemi*-octahedron, and it is a different fold rather than half of `OctahedralMap`'s.** Nobody
looks at a tree from underneath, and a full-sphere grid spends half its atlas on views a forest never
shows — at the resolutions an impostor is worth having, that is the difference between an 8×8 grid
and a 12×12 one. The full-sphere fold exists, is used for probe radiance, and is not this.

⚠ **Three cells blended, not one.** Snapping to the nearest view makes an impostor rotate in visible
steps, and for a forest it is worse than for one object because every tree steps on a different frame.
The three that share the direction's triangle sum to one everywhere, which is what makes the blend
continuous across a cell boundary.

⚠ **Orthographic, and that is the whole reason an impostor works.** A perspective bake fixes the
distance the mesh was photographed from into the texture, so an impostor drawn nearer or further shows
the wrong parallax.

⚠ **The intermediate LOD levels are `MeshSimplifier`'s and are not duplicated here.** T7's brief says
so and the seam is the model importer's: an impostor is the *last* level of a foliage type's LOD
group, and the ones above it are the simplifier's job over the mesh the type already names.

Three things the design did not have:

- **The grid has to be odd-sided.** Straight down is where a top-down view spends its whole time, and
  an even grid puts a seam exactly there — four cells blended for the one direction that ought to be a
  single photograph.
- **The atlas's mip chain has to stop at the cell size.** A mip that mixes two cells is the bleed the
  gutter exists to stop, arriving through a different door — so `MipLevels` is how many are *safe*
  rather than how many fit, which for a 9×9 grid of 128-texel cells is eight rather than eleven.
- **The overhead cell has no side.** Its camera's up vector falls back to a horizontal axis, or the
  bake produces a NaN for the one view a top-down camera never leaves.

### T8 — Splines · 1.5 EM · ✅ built

**A spline asset, in `Core`, with two consumers.** Control points with tangents, segment evaluation,
arc-length parameterisation, serialisation, and viewport editing (add, insert, delete, tangent
handles, join, split) built on the gizmo and `SnapContext` that already exist. Then the terrain half:
a reserved Splines edit layer, deformation by half-width and cosine side falloff (left and right
independent), layer painting along the width, and mesh placement along the length with randomised
selection.

⚠ **This retires [26](26-virtual-cameras.md)'s largest owed item.** Its dolly track was blocked on
"a spline *asset*: authored, serialised, editable in the viewport, shared with whatever else needs a
path" — and inventing one there "would make it the second spline in the engine the moment anything
else needs one". This is that moment, so it is built once, here, and the camera stage that reads it
becomes the small thing doc 26 said it was.

**Exit:** a road across a terrain, deforming it non-destructively, painted with a gravel layer along
its width, with meshes placed along it — and a camera dolly following the same asset type.

✅ **Built, and doc 26's owed item is closed.** `SplineAsset` and `ISplineSource` in
`Vixen.Core.Mathematics`; `TerrainSpline` with its profile and its mesh placement in `Vixen.Terrain`;
`TrackedDollyBody` and `DollyMode` in `Vixen.Engine`; `SplineEdit` and `SplineCommand` in
`Vixen.Editor.SceneView`, on the gizmo's `IGizmoTarget` vocabulary and `SnapContext`.

⚠ **Two types, and the split is the point.** `Spline` is immutable and precomputes an arc-length
table; `SplineAsset` is mutable and precomputes nothing. An editor moves a control point on every
frame of a drag, and rebuilding a length table sixty times a second for a curve nobody is measuring is
what makes an editor feel heavy.

⚠ **The undo record is the whole point list, which is the opposite of [D11](#d11-a-stroke-is-one-command-and-it-stores-a-rect)
and for the same reason.** A heightfield stroke stores a rect because a terrain is megabytes; a spline
is three kilobytes, and a per-point record would have to reason about every index shifting when a
point is inserted. Same argument, different answer.

Four things the design did not have:

- **A road's width is measured across the ground, not through the air.** `Spline.DistanceTo` is 3-D —
  right for a camera, and for a road it means a centreline can only deform ground it is already level
  with. A causeway drawn twenty metres above a valley floor, which is exactly how an author draws one,
  touched nothing at all. Cutting and filling is the whole point of a spline that deforms.
- **Clearing the road's own rect is not enough when a road *moves*.** The new rect no longer covers
  the old one, so the old road stayed. `Regenerate` — empty the layer, lay every road down again, and
  invalidate the chunks the layer had already allocated — is what an editor calls; `Deform` is for
  adding a road to a layer that is otherwise already right.
- **Inserting a point has to be compared by arc length, not by parameter.** Splitting a segment
  reparameterises both halves — that is what makes the shape survive — so a test written against the
  parameter range fails on correct output.
- **A component holding a string is a managed one**, so the dolly reads its own component one entity
  at a time where every other body stage walks a contiguous column. That is the price of naming an
  asset rather than holding a handle to it, and it is worth paying for the reason every other asset
  reference in a scene is a name.

**Owed within T8:** the spline *overlay* — drawing the curve, its points and its tangent handles in
the viewport, which is `SceneLines` work and is what the spline panel names as absent; and mesh
placement reaching the scene, which needs the entity spawning `PlaceAlong` deliberately does not do.

### T9 — Growth simulation · 1.0 EM · ✅ built

The offline ecology of Unreal's procedural foliage tool, in `Core/Vixen.Foliage`: seeded age
simulation over a volume, spread distance, shade radius and shade tolerance, overlap priority,
blocking volumes, a fixed step count, and a stated random seed. Writes to a reserved Scatter edit
layer's instance set, which [D4](#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it)
makes re-runnable without touching hand-placed instances.

**Exit:** a forest that reads as a forest — clumped, shaded out under canopy, cleared where the
volume says — from four sliders, resimulating deterministically.

✅ **Built.** `FoliageEcology` on `FoliageType`, `FoliageBlocker`, and `FoliageGrowth` with its
settings and its per-reason result.

⚠ **The reserved Scatter layer is a *volume* of its own, because that is what this kernel has.** The
simulation regenerates wholesale, so it cannot share a container with hand-placed instances — the
destination is cleared and refilled and the scene's own volume is never touched. Same mechanism, this
assembly's vocabulary.

⚠ **A plant's identity is hashed at birth and each step resolves in hash order.** Which of two
overlapping seeds wins must not depend on which parent was walked first, or the same seed grows a
different forest whenever anything upstream reorders the working set.

Three things the design did not have:

- **The canopy has to grow with the plant.** Shading from the mature radius produces one tree per
  shade radius, evenly spaced, everywhere — which is precisely the pattern that makes a procedural
  forest read as procedural, and it would pass every other test.
- **Shade and spread pull in opposite directions, and at forest tolerances shade wins.** A forest
  under competition is *more* evenly spaced than chance, not less. "Clumped" as an unqualified exit
  criterion is a test that fails on correct output; what is asserted is spread's contribution against
  a sowing of the same species, measured at the spread scale.
- **Nearest-neighbour distance measures the spacing radius, not the clumping.** A hard minimum
  spacing dominates that statistic and strengthens as the field fills, so a spread forest scores as
  *more* evenly spaced than the sowing it grew from — true at two metres and silent about ten. The
  variance-to-mean ratio over spread-sized cells is what actually answers the question.

✅ **The panel is built** — `TerrainGrowthSettings` and a Grow button, in `Vixen.Editor.App`. The
seed is a field rather than a hidden number, because "the same rules, a different forest" is what a
procedural forest is for and a generator that reseeded itself would make an author who liked what
they saw unable to get it back. The plant cap is reported when it bites, because a simulation that
quietly stopped sowing reads as a rule that stopped working.

**Owed within T9:** blocking volumes as *scene* objects, which needs a component and a bounds query
this kernel deliberately cannot name.

### Cost

| Phase | EM | Blocked on |
|---|---|---|
| ~~T0 — Unblockers~~ ✅ | 1.0 | Built, plus the spline from T8 and the per-instance cull's reference half from T5 |
| ~~T1 — The heightfield kernel~~ ✅ | 2.0 | Built, mip chain and heightmap I/O included |
| ~~T2 — The renderer~~ ✅ | 2.0 | Built, per-tile atlas and mips included. Owed within it: the layer textures the splat loop reads |
| T3 — Sculpt mode ✅ | 2.0 | T1, T2 |
| T4 — Layers and paint mode ✅ | 2.0 | T3 |
| T5 — Foliage instances ✅ | 2.0 | Built, compute shader included. ⚠ **`InstanceCuller` landed early, in T0**, as the CPU reference and `FoliageCull.rvn`'s oracle. Owed within it: the Hi-Z test, which the reference does not do |
| T6 — Grass ✅ | 1.5 | Built, scatter dispatch and indirect draw included. Owed within it: the hole mask on the device side, and a grass *panel*, which § D8 says is a rule rather than a mode |
| — | **12.5** | **the cut line** |
| T7 — Impostors ✅ | 1.0 | Built, bake included. Owed within it: the dilation into the gutter and the mip build |
| T8 — Splines ✅ | 1.5 | Built, and [26](26-virtual-cameras.md)'s owed dolly track with it. Owed within it: the `.vxspline` importer, the viewport overlay, and mesh placement reaching the scene |
| T9 — Growth simulation ✅ | 1.0 | Built, panel included. Owed within it: blocking volumes as scene objects |
| | **16.0** | |

T1 and T0 are fully parallel; T5 needs nothing from T3 or T4 except the terrain-layer filter, so a
second person can build foliage while the first builds sculpting. That is the schedule's one real
parallelism and it is worth taking.

---

## Improvements over the references

Eight, in the shape [22 § Improvements over UE5](22-virtualized-geometry.md) uses — each one a place
where building second is an advantage rather than an apology.

### 1. Edit layers are the storage model, not a retrofit

Unreal shipped Landscape in 2012 and edit layers in 2019, which is why they are opt-in per landscape
and why several tools interact with them awkwardly. Building the composite into the kernel from the
first commit means no tool has a flat-heightmap path to keep working, and the reserved-layer
mechanism that splines and scatter need is the same mechanism the artist's own layers use.
See [D4](#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it).

### 2. The splat material is configured, not wired

"Why is my landscape black" is the most-asked Landscape question and it has a dozen causes, all of
them a graph somebody wired differently. A generated material with the layer count as its permutation
axis has none of them. See [D6](#d6-the-splat-material-is-generated-from-the-layer-list).

### 3. One residency manager, four kinds of page

`PageResidency` was written source-agnostic on purpose — `PageKey.Source` is "assigned by whoever
registered it; the residency service never interprets it" — and terrain tiles are its second
customer. Unreal runs Nanite streaming, virtual-texture streaming, landscape streaming and the
shadow page pool as four systems with four budgets and four eviction policies.
See [D13](#d13-streaming-rides-pageresidency).

### 4. One brush, three consumers

Unreal's sculpt, paint and foliage brushes are three implementations with three falloff curves, so
the same strength means three different shapes. See [D12](#d12-the-brush-is-one-service).

### 5. A CPU reference for the scatter, so determinism is a test

The surface cache's CPU/GPU seam test measured exactly zero drift and that pattern transfers
directly: the scatter is a hash of position, so the same cell scattered by the reference and by the
compute shader must be bit-identical. Unreal's grass has no oracle, which is why "the grass moved"
is a bug report rather than a failing test. See [D8](#d8-grass-is-derived-trees-are-stored-and-the-distinction-is-the-density).

### 6. Editing does not invalidate the render representation

Nanite landscape must be rebuilt after sculpting, and an unbuilt one draws the old shape. CDLOD over
the live heightfield has no build step: a brush writes texels, the tile's mips regenerate for the
dirty rect, and the next frame is correct. **This is the reason terrain is not built on the meshlet
path**, and it is worth stating plainly because "use Nanite for everything" is the obvious-looking
answer. See [D3](#d3-a-quadtree-with-a-morph-not-a-clipmap).

### 7. The height range is the author's, not a 1998 constant

Unreal's fixed 1/512 ratio over −256…255.992 spends the same sixteen bits whether the terrain is a
dune field or the Himalayas. An authored range gives a 40 m landscape thirteen times the vertical
precision for the same bytes. See [D2](#d2-the-terrain-is-an-asset-and-the-tile-is-the-unit).

### 8. Two placement models, named as such

Both references have derived scatter and stored instances, and neither says so in its documentation —
which is why every project eventually paints ten thousand grass instances by hand and then wonders
why the level file is 400 MB. Making it a declared property of a foliage type, with different tools
for each, is a documentation fix expressed as a design.

---

## What is deliberately not built

| Not built | Why, and what it would take |
|---|---|
| **Voxel terrain, caves, overhangs** | A heightfield is a function of two variables and every one of the cheap things above depends on that — the collider, the quadtree, the scatter, the weight layers. Overhangs are meshes placed on the terrain, which is what both references do and what every shipped game does |
| **A runtime virtual texture** | Deferred, not rejected, with the arithmetic in [D7](#d7-no-virtual-texture-in-the-first-pass-and-the-loop-is-why). It rides `PageResidency` when it lands |
| **A node-based terrain generator** | A content-generation product. World Machine, Gaea and Houdini exist, they export 16-bit heightmaps, and [T3](#t3--sculpt-mode--20-em--built)'s import writes to an edit layer so their output can be sculpted on top of without being destroyed |
| **A biome system** | A rule engine over layers and foliage types, which is a game's design and not an engine's. Every piece it would need is exposed |
| **Live ecosystem simulation** | [T9](#t9--growth-simulation--10-em---built) runs offline and bakes, which is the reference behaviour and the right one — a forest that grows while the player watches is a game mechanic with a bespoke budget |
| **Nanite-class foliage — assemblies, voxels, skinned wind** | Genuinely the future and genuinely three separate large features on top of [22](22-virtualized-geometry.md), which is itself not finished. Unreal ships it marked experimental. The impostor path in [T7](#t7--impostors-and-the-far-field--10-em---mostly-built) is the 90 % answer at 5 % of the cost, and nothing in this design forecloses the other one |
| **Grass that reacts to a character** | A displacement texture written by moving entities and sampled by the scatter's vertex stage. Small, delightful, and it needs the grass path to exist first — owed rather than cut |
| **Terrain-to-mesh export** | A bake for an external tool. [24 § P7](24-blockout-tools.md) already writes OBJ and the tile mesh builder makes this a small addition when somebody asks |
| **Landscape patches / blueprint brushes** | Unreal's mechanism for a mesh that deforms the terrain under it. It wants the reserved-layer machinery [D4](#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it) builds, so it becomes cheap the day it is wanted — but no first-party need for it exists yet |
| **Terrain neighbours and stitching** | Unity needs this because a terrain is capped in size; a tiled terrain does not have the problem. Two *separate* terrains meeting is a level-design decision and a seam either way |

---

## Part 4 — Testing

The kernel is pure functions over arrays, so the great majority of this needs no world, no renderer
and no device — the same bargain [24 § Part 4](24-blockout-tools.md) makes and for the same reason.

| Level | Mechanism |
|---|---|
| **Invariants after every operation** | Weight-blended layers sum to 1 within one ULP of the `u8` quantisation, and the assertion names the offending layer; a tile's stored min/max bracket its actual samples; shared boundary samples are equal between neighbours; a hole mask never disagrees between the mesh and the collider. One helper, called by every operation's tests |
| **Property tests** (CsCheck, as `Vixen.Core.Mathematics` does) | Sculpt by `+d` then `−d` is the identity. Smooth is mean-preserving and monotonically reduces total variation. Flatten to `h` leaves every sample in the brush at `h` at full strength. Erosion never increases total mass. Noise with the same seed is the same noise. Painting layer A to 1 and then layer B to 1 leaves A at 0 |
| **Composite equivalence** | A stack of layers collapsed equals the composite of the stack, for randomised stacks with randomised alphas — including negative ones. This is the test that catches an alpha applied in the wrong order |
| **Randomised do/undo/redo** | `Vixen.Editor.Core`'s existing suite over terrain and foliage commands: a random stroke sequence, undone to empty and redone to the end, asserting height, weight and instance equality at every step |
| **No cracks** | The morph is evaluated at every vertex of a boundary between two adjacent nodes at different levels, and the two must produce identical world positions. Pure arithmetic, no device, and it is the single highest-value test in the renderer |
| **Morph continuity** | Vertex position as a function of camera distance is continuous across a level transition — sampled densely and asserted, because a discontinuity here is a visible pop that a screenshot taken at the wrong moment will not catch |
| **Scatter determinism, CPU vs GPU** | [19 § L4](19-lighting-and-global-illumination.md)'s seam test, transferred: the CPU reference and the compute shader scatter the same cell and the instance sets are compared as sets. Stated tolerance: exact |
| **Collision agreement** | A raycast against the height-field shape and a raycast against the built tile mesh agree within a stated bound, over randomised terrains and randomised rays. This is what catches an off-by-half-a-texel in the sample-to-world mapping, which is otherwise discovered as a character standing slightly in the ground |
| **Round trip** | Every terrain built by every test saves and reloads to an identical terrain and re-saves to identical bytes — the format's standing promise, and it matters more here than anywhere because the payload is binary |
| **Gestures** | `SceneViewport`'s pattern: synthetic pointer input against the real tool, asserting the heightfield rather than the pixels. "Dragging the sculpt brush across two tiles raises both and dirties exactly two colliders" is a unit test |
| **Budget assertions** | A 4 km² terrain: node count per frame, draw calls, and the upload bytes a stroke causes. These are the numbers that regress silently |
| **Golden screenshots** | The suite [20 § Part F](20-editor-parity.md#part-f--testing) already gates: a lit terrain at three camera distances (which is where a crack or a pop shows), each blend mode, holes, a grass field, and an impostor transition |

⚠ **The no-crack test must be written before the renderer, not after it.** It is arithmetic over the
morph function and needs nothing else to exist. A crack found by eye is found in a screenshot at one
camera position, attributed to the wrong thing, and worked around with a skirt — which is how
terrain renderers acquire skirts they do not need and then keep them for ever.

---

## Risks

| Risk | Mitigation |
|---|---|
| **16 EM, and it reads as a second engine** | The cut line is real and stated per phase. T0 alone closes two owed items from doc 24; T0–T3 (7 EM) is a terrain an artist builds a level on; T0–T6 (12.5 EM) is the whole consensus feature set. Nothing after T6 blocks anything |
| **There is no world streaming, and terrain is the feature that needs it** ([B6](#b6-there-is-no-world-streaming-)) | Structural rather than aspirational: the tile is the unit of load and `PageResidency` already serves pages, so terrain *data* streams. Scene-level streaming — cells of instances, a streaming source, a grid — is named here as the dependency it is and is a document of its own. ⚠ **The honest failure mode is a project that builds a 16 km² world and discovers the instances do not stream.** The create dialog's derived readout is the early warning, and it should say so in words rather than only in megabytes |
| **The generated splat material's permutations multiply against everything else** | Quantised to four layer counts, and the layer loop is bounded rather than unrolled per layer. The permutation axis is one integer, not one flag per layer — which is the mistake that would produce 2¹⁶ variants |
| **Foliage instance memory** | An instance is a transform plus a `float4` — 80 bytes, so a million instances is 80 MB and that is a real budget. Stated in the palette panel as a derived readout per type, in the same style as the create dialog, and the derived/stored distinction in [D8](#d8-grass-is-derived-trees-are-stored-and-the-distinction-is-the-density) is what keeps grass out of that number entirely |
| **Erosion is a research rabbit hole** | Thermal and hydraulic, both textbook, both bounded by an iteration count, both running on the job system over a rect. The moment somebody proposes a fluid solver it has failed [the left-column test](#where-the-line-goes) |
| **Sculpting and the GI chain disagree** | Distance fields, the surface cache and the irradiance field all cache what the world looks like, and a sculpt invalidates it. A terrain tile edit must dirty the corresponding distance-field bricks and surface-cache cards — the same problem an edited blockout mesh already has, and the answer belongs with doc 19's invalidation rather than being invented here. ⚠ **It is listed as a risk because it is easy to not notice**: the terrain will look right and the bounce light will be from the old shape |
| **Two selection models, again** | Instance selection in `FoliageMode` is the third selection kind in the viewport, after entities and sub-objects. It is the mode that keeps them apart, which is the answer [24](24-blockout-tools.md) already validated — and the reason instance selection lives in a mode rather than in the picker |
| **A project ships without ever using edit layers** | Likely, and fine. The default terrain has one layer and behaves exactly like a flat heightfield; the machinery costs nothing until a second layer exists |

---

## Documents this changes

| Document | Change |
|---|---|
| [20 § Part G](20-editor-parity.md#part-g--out-of-scope) | The "Terrain and foliage tools" row now points here. The estimate was right; what changed is the eight pieces of infrastructure it would have had to build itself, which now exist |
| [20 § B6](20-editor-parity.md#b6--world-building) | The **Terrain / foliage** panel row becomes [Part 2](#part-2--the-authoring-surface) rather than ⛔ |
| [20 § A1](20-editor-parity.md#a1--the-application-frame) | `IEditorMode` gains its third and fourth consumers, and the example in [guide/editor/modes.md](../guide/editor/modes.md) — which is literally `TerrainPlugin` / `SculptMode` — stops being hypothetical |
| [06 § Geometry and materials](06-rendering-pipeline.md) | **Terrain** is promoted, with *clipmap* rejected on the merits ([D3](#d3-a-quadtree-with-a-morph-not-a-clipmap)) and *virtual texture* deferred with arithmetic ([D7](#d7-no-virtual-texture-in-the-first-pass-and-the-loop-is-why)). **Impostors / billboards** is promoted and given its consumer ([T7](#t7--impostors-and-the-far-field--10-em---mostly-built)) |
| [26 § What is deliberately not built](26-virtual-cameras.md) | The dolly track's blocker — "wants a spline *asset*… the largest owed item and the one most worth doing" — is [T8](#t8--splines--15-em---built). One asset, two consumers, built once |
| [24 § P5](24-blockout-tools.md) | Vertex colours, recorded there as owed against `MeshData`, land in [T0](#t0--unblockers--10-em---built) alongside the per-instance data that needs the same change |
| [02](02-repository-layout.md) | Four assemblies with their tests: `Core/Vixen.Terrain`, `Core/Vixen.Foliage`, `Core/Vixen.Rendering.Terrain`, `Editor/Vixen.Editor.Terrain` |
| [08](08-asset-pipeline-and-addressables.md) | Four asset kinds and their importers: `.vxterrain`, `.vxlayer`, `.vxfoliage`, `.vxgrass`, plus 16-bit PNG and raw `r16` heightmap import through `Vixen.Core.Imaging` |
| [22 § improvement 6](22-virtualized-geometry.md) | "One residency manager for geometry, textures and shadow pages" gains its second real customer, and `PageKey.Source`'s deliberate opacity gets to pay off |
| [19](19-lighting-and-global-illumination.md) | Terrain and foliage are new sources of GI invalidation — a sculpted tile dirties distance-field bricks and surface-cache cards. Listed as a risk above; the mechanism belongs to doc 19 |
| `Core/Vixen.Physics` | `ShapeKind.HeightField` — one shape kind, one binding call, one ECS bridge. Jolt has the shape; `ShapeDescription` does not name it |

Licensed under Apache-2.0.
