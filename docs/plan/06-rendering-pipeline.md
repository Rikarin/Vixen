# 06 — Rendering Pipeline

Your brief: "Stride rendering pipeline with all of its features. Forward, deferred, antialiasing and
others." Stride's architecture here is genuinely excellent and under-appreciated — it is the part
worth taking most directly. This document maps Stride's model onto the Vixen RHI and lists the feature
set with modern additions.

## Stride's model, and why it is the right one

From `sources/engine/Stride.Rendering/Rendering/`, Stride's design is:

```
GraphicsCompositor          — the user-authored graph of what renders where (asset, editable)
 └── SceneRenderer[]        — Clear, SingleStage, ForceAspectRatio, RenderTexture, Delegate, …
      └── RenderView        — a camera + frustum + a set of enabled RenderStages
           └── RenderStage  — "Opaque", "Transparent", "ShadowMapCaster", "GBuffer", "Picking"
RenderSystem
 ├── RootRenderFeature[]    — MeshRenderFeature, SpriteRenderFeature, ParticleRenderFeature,
 │                            UIRenderFeature — one per *kind* of renderable
 │    └── SubRenderFeature[] — TransformRenderFeature, SkinningRenderFeature, InstancingRenderFeature,
 │                             MaterialRenderFeature, ForwardLightingRenderFeature, ShadowCasterRF
 ├── VisibilityGroup        — culling, RenderObject → RenderView visibility
 └── RenderObject / RenderNode / RenderViewFeature — the per-frame flattened work lists
```

The three ideas worth keeping verbatim:

1. **`RenderObject`/`RenderNode` separation.** Scene data is extracted once per frame into flat arrays
   (`RenderObject`), then per-view work is a `RenderNode` list referencing them. Extraction is
   parallel; nothing in the draw loop touches a scene graph or a managed object graph.
2. **Sub-render-features own their own data via `RenderDataHolder`.** Each feature declares typed
   per-object/per-view/per-node data arrays that grow in lockstep with the object count. Adding
   skinning does not modify `MeshRenderFeature`; it registers its own parallel array. This is
   structural extensibility without inheritance, and it is the reason Stride's renderer is
   extensible where most are not.
3. **`GraphicsCompositor` is an asset.** The frame's structure is data the user edits, not code. That
   is what makes "swap forward for deferred" a project setting rather than a fork.

### ✅ Status: all three are built

`Core/Vixen.Rendering` holds the spine: `RenderObjectStore` (flat, dense, stable ids),
`RenderDataHolder` (per-feature SoA arrays in native memory), `RootRenderFeature`/`SubRenderFeature`,
`RenderView`/`RenderStage`, `VisibilityGroup` (parallel CPU frustum culling) and `RenderSystem`
driving extract → cull → prepare → sort, then recording into per-thread command lists.

`MeshRenderFeature` is the first concrete renderable, with transform, material, forward-lighting,
skinning and instancing sub-features. None references another's data. Lighting, skinning and
instancing were each added after the mesh feature and changed nothing in it — instancing changed four
lines, to pass a draw-call argument it now has a source for. That is idea 2 cashing out rather than
being asserted, and a **skinned instanced mesh** is the case an inheritance hierarchy needs a class
for: here it is two independent flags on one object, contributed by two features that do not know
each other exists.

Sub-features say which shader variant their objects need through `IPermutationSubFeature`, because an
object is skinned when it has a skeleton and not when a material says so. `MaterialRenderFeature`
applies the contributions without knowing what contributed them, and resolves **per distinct
(material, flags) pair** — ten thousand objects over twenty materials, half of them skinned, is forty
resolutions and ten thousand dictionary lookups.

`Compositor/` is idea 3: `GraphicsCompositor` over a tree of `SceneRenderer`s — a sequence, a render
pass, a single stage from a single view, a shadow map, a delegate. Its **collect phase runs before
the render system**, so the frame's view list and every view's stage mask are *derived from the tree*
rather than set beside it: a stage nothing draws costs no culling, and a stage that is drawn cannot
have been forgotten in a mask.

**The compositor declares render-graph passes rather than opening render passes**, which is the
promise three bullets down finally kept. A node names its targets; the graph sizes them, aliases
them, places the barriers, derives the store actions and drops the passes nothing needed. `Reads` is
the load-bearing part: one line orders a pass after the one it samples, puts the barrier between
them, and keeps that producer from being culled — all three asserted, in both directions. The
frame's final target is *imported*, because a pass writing an import always survives, which is why
"the last pass" cannot disappear while an over-specified preset's unused passes cost nothing.

A document therefore owns its targets. `resources:` declares them with a `scale` of the frame rather
than a pixel size, so a half-resolution chain stays half resolution on a window nobody anticipated —
the half of "the frame is data" that naming host textures could not express.

And it is a file. `GraphicsCompositorAsset` is the same tree as a serialisable record graph with a
`[DataContract]` name per node type as its YAML tag, and `CompositorBuilder` turns one into a running
compositor. **The asset names resources; the host binds the names** — a texture handle belongs to a
device that did not exist when the file was written — so one authored document runs against a
swapchain, an offscreen buffer or a test's scratch texture unchanged. A test parses twenty lines of
YAML and draws a two-pass frame with a two-cascade shadow atlas in it, building no renderer tree in
C# at all. `Vixen.Rendering` does not reference `Vixen.Core.Yaml`: the model carries `[DataContract]`
so both generators run over it — the reflection one for the editor's YAML binder and the **binary
one** for the chunk a content build bakes — and a shipping runtime that loads a baked compositor
never links a parser. A test round-trips the document through `Serializer` and draws the same frame
out the far side.

Three things the implementation settled that the sketch above leaves open:

- **The phase order is a data dependency.** Culling needs everything extracted or a late object is
  tested against a stale bitset; preparation needs culling or it loses its point; sorting needs
  preparation because a feature's sort group may be what preparation resolved. Reordering any pair
  gives a frame that is quietly wrong rather than one that fails, which is why the order is stated in
  the code rather than left to the caller.
- **Culling parallelises over objects, not views**, and the batch size is a multiple of 64. A frame
  has a handful of views and tens of thousands of objects, so splitting by view leaves most threads
  idle — and since a `ulong` holds 64 objects' bits, whole-word batches are what make the parallel
  path need no lock and no atomic anywhere.
- **The sort key puts grouping above depth in one 64-bit comparison.** That is what makes a
  front-to-back sort also a state-change-minimising one. Sorting purely by depth makes a scene
  *slower the better it is culled*, because the draw order stops correlating with pipeline state; a
  transparent stage leaves grouping out entirely, because reordering blended draws changes the image.

- **A pipeline is decided by four things, and the key names all four**: the effect, the stage (blend,
  depth, raster), the output (attachment formats and sample count) and the vertex layout. State
  belongs to the stage and formats to the pass, because a stage is drawn into many passes — "Opaque"
  means depth-written wherever it is drawn. The output holds *formats, not textures*, which is what
  lets the swapchain hand out a new image every frame and the render graph alias transient targets
  without invalidating a single pipeline.
- **Three per-draw data problems, three mechanisms, and none of them is a descriptor set per draw.**
  Lighting's fixed-size block uses a dynamic descriptor offset; skinning's variable-length palette
  uses a push constant holding a base index; instancing uses the draw call's own `firstInstance`,
  which the API adds into `gl_InstanceIndex` before the shader runs and which therefore costs no
  binding at all. Reaching for one mechanism for all three would have meant padding every bone
  palette up to `minStorageBufferOffsetAlignment` and picking a maximum bone count in advance.
- **A pass's own bindings need a lifetime the RHI does not have**, and
  `Vixen.Graphics.DescriptorAllocator` is it. A pass sampling the shadow atlas cannot own a set,
  because the atlas is a graph resource whose handle does not exist until the graph compiles and
  which may alias different memory next frame. So sets are written after the graph resolves, recycled
  through a ring exactly `FramesInFlight` deep — shorter is a use-after-free most drivers execute in
  silence — and shared within a frame by anything asking for the same writes, which is the difference
  between a set per pass and a set per distinct combination. This is what lets a compositor node bind
  what it declared instead of handing the host a callback.

A settled frame of 10 000 objects through extract → cull → sort **allocates nothing**, asserted by
test — the guard against a change that starts allocating per object per frame and surfaces months
later as a GC spike nobody can attribute.

Vixen keeps all three, with these changes:

- Extraction, culling, and command recording are **job-system parallel by default** rather than
  optionally so, and record into per-thread `CommandList`s.
- `RenderDataHolder` arrays become `NativeArray<T>` in SoA form, addressed by a dense `RenderObjectId`.
- ✅ Passes are submitted through the **render graph** ([05](05-graphics-rhi.md)), so barriers and
  transient memory are automatic. The compositor declares; the graph compiles.
- **GPU-driven culling** where capabilities allow: object bounds uploaded once, frustum + Hi-Z
  occlusion culling in compute, output an indirect draw buffer. The CPU path remains for GL/WebGL.
  ✅ **Both culls are here.** `IVisibilityGroup` is the seam, `GpuVisibilityGroup` packs the scene
  into two storage buffers and dispatches `Library/Pipeline/Culling.rvn`, and
  `RenderSystem.Visibility` is where a host chooses. One invocation owns one 32-object word, so the
  pass needs no atomic; one dispatch covers every view, which is why the counts travel in the view
  record rather than in a uniform block. Occlusion is the `Occlusion` permutation of the same shader
  over a `HiZPyramid` — last frame's depth min-reduced by `Library/Pipeline/HiZReduce.rvn`, built by
  the `HiZRenderer` compositor node, which exists because declaring the depth *read* is what orders
  the dispatch after the pass that filled it. Minimum because depth is reversed, 3×3 because a
  floored mip chain leaves a trailing row, and per view because only a view whose matrix was seen in
  the frame the pyramid was built in may be projected with it. It falls back to the CPU whenever it
  cannot run — no pipeline, a variant still compiling, a pyramid not yet built — which is what "the
  CPU path remains" is, made automatic.
  ✅ **And the indirect draw buffer.** `GpuVisibilityGroup.ReadBack = false` submits and waits for
  nothing: `Compositor/GpuCullingRenderer` records the cull and `Library/Pipeline/DrawArguments.rvn`
  in the frame's own list — the only ordering an RHI with no fences can express — and
  `MeshRenderFeature` draws through `DrawIndexedIndirect` at each object's own slot. It zeroes
  instance counts rather than compacting; the host's bitset then holds what *could* be seen, and the
  device removes the rest. With the readback on, everything is as before: the bits are this frame's
  and the work list is exact.
  ✅ **And in two phases**, which is what removes the frame of staleness one-phase occlusion culling
  cannot avoid. The `Late` permutation of the same shader reads the visibility word before it writes
  it and answers with the *difference* — visible against a pyramid rebuilt from the main pass's own
  depth, and not already drawn — into the same buffer, so the late draws are the same draws reading
  an argument buffer whose contents changed. A frame with no pyramid still dispatches it and gets an
  empty difference, because skipping it would leave the main pass's bits for the late draws to find.
  ✅ **And it is a compositor document**: `!GpuCulling` and `!HiZ` are node kinds with `readBack`,
  `indirectDraws` and `phase` as their flags, and `CompositorBuilder` makes the assignments a file
  cannot — the render system's visibility group, the arguments every drawing feature reads, and the
  descriptor-ring depths that two nodes of a kind in one frame imply. The resources stay
  host-supplied, so one document runs on a target with no compute and gets the CPU path.
  Still open: **compaction**, blocked on an indirect draw whose count comes from the device *and* on
  bindless materials, since each object binds its own vertex buffer and material set — see
  `Vixen.Rendering/README.md § Culling`.

## Frame structure

```
1. Sync           wait frame fence; reset per-frame pools; drain main-thread queue
2. Extract        [parallel] scene → RenderObject SoA arrays; only dirty objects re-extracted
                  (driven by ECS change versions — see 04)
3. Prepare views  build RenderView list: main camera, shadow cascades, reflection probes, UI
4. Cull           [parallel or GPU] frustum → per-view visibility bitsets; Hi-Z occlusion;
                  LOD selection; distance/shadow-distance fade
5. Prepare        [parallel] per sub-feature: fill transform/skinning/material/light data;
                  resolve effect permutations; allocate constant-buffer ranges from the upload ring
6. Sort           per stage: front-to-back (opaque, by pipeline then depth), back-to-front
                  (transparent, by depth), state-change-minimising (UI)
7. Record         [parallel] per stage per thread → CommandList; render graph inserts barriers
8. Submit         queue submissions in graph order; async compute overlapped where available
9. Present        swapchain present; GPU timestamp readback for the previous frame
```

Steps 2–7 are all jobs with declared dependencies; the frame is a DAG, not a sequence of `for` loops.
The main thread's own work is steps 1, 3, 8, 9 and is budgeted at **< 1 ms**.

## Pipelines

Three shipped `GraphicsCompositor` presets, all built from the same features:

### Forward+ (clustered) — the default

The right default in 2026. Depth prepass → light clustering in compute → single opaque forward pass
with clustered light lookup → transparent pass → post FX.

- **Clustering:** ✅ froxel grid (16×9×24 with exponential depth slices), lights binned in compute,
  per-cluster light index list in a storage buffer. `ComputeRenderer` is the compute pass as a
  compositor node, and the edge that made it possible is the one it declares: compute *writes* the
  cluster buffer and the shading pass *reads* it, so the graph orders them and places the barrier.
  The buffer is declared rather than imported, so a cull nothing consumes is dropped with its
  dispatch, and the node binds what it declared out of the per-frame descriptor allocator rather than
  through a host callback — and it fills its own uniform block from `ConstantBinding`, without which
  the culler's camera, planes and light count had no way in at all. Clustered lighting then costs
  **nothing per object** — no selection, no
  per-draw block, no descriptor per draw. The grid is right-handed like the rest of the engine, which
  it was not: `Transform.ViewRay` pointed down +Z while `Matrix4x4.LookAt` looks down −Z, so every
  cluster's box was mirrored in z from the lights tested against it and every list came back empty —
  a handedness mistake gives an empty result rather than a wrong-looking one. `ClusterGrid.DepthOf`
  is now the single place the two conventions meet, on both sides, and a test holds the fragment's
  own cluster against the box the culler built for it. The pass is also **dispatched on a device** and
  its buffer read back, against that same oracle over all 3456 clusters — reverting the handedness
  fails it with `expected [0], got []`. Falls back to tiled (2D) on GLES and to
  per-object light lists (Stride's `ForwardLightingRenderFeature` approach, max N lights per draw) on
  WebGL2 where compute is absent.
- **Why default:** MSAA works, transparency works, material variety is unconstrained, memory
  bandwidth is far below deferred on mobile. Mobile is a first-class target here, and deferred on
  mobile is a bandwidth catastrophe.

### Deferred

GBuffer → light accumulation → forward pass for transparents/forward-only materials → post FX.

- **GBuffer layout** (4 RTs + depth, all in the render graph so aliasing is automatic):
  | RT | Format | Contents |
  |---|---|---|
  | 0 | `R8G8B8A8_UNorm_sRGB` | base colour RGB, occlusion A |
  | 1 | `R10G10B10A2_UNorm` | octahedral-encoded normal (RG10), roughness (B10), shading model ID (A2) |
  | 2 | `R8G8B8A8_UNorm` | metallic, specular, clearcoat, clearcoat-roughness |
  | 3 | `R16G16_Float` | motion vectors |
  | depth | `D32_Float` | reverse-Z |
- **Shading-model ID in the GBuffer** is how deferred supports more than one BSDF (Stride's material
  system permits many); the lighting pass branches on it in a `switch` over a small set. Materials
  whose model is not GBuffer-representable (SSS, hair, clearcoat with anisotropy) are automatically
  routed to the forward pass — a per-material capability check made at material-compile time, with a
  build warning naming the material.
- Kept because: high light counts on desktop, decal support, and screen-space techniques (SSR/SSAO/SSGI)
  that want a full GBuffer.

### Mobile forward

Single pass, no prepass (tile-based GPUs hate the extra geometry pass), per-object light lists,
subpass-friendly (`VK_KHR_dynamic_rendering` with `localRead` or real subpasses on 1.1), MSAA 4×
resolved in-tile, minimal post FX (tonemap + FXAA fused into the resolve).

## Feature inventory

Everything below is either a `RootRenderFeature`, a `SubRenderFeature`, or a render-graph pass.
Priority column: **P1** = required for the 1.0 renderer, **P2** = post-1.0.

### Geometry and materials

| Feature | Pri | Notes |
|---|---|---|
| Static mesh | ✅ | `MeshRenderFeature` + `TransformRenderFeature` + `MaterialRenderFeature`. A mesh with three materials is three render objects sharing one pair of buffers, so each sorts into its own place — one object with a submesh list would have to pick one sort key for three pipelines and be drawn at the wrong depth for two of them |
| Skinned mesh | ✅ | `SkinningRenderFeature`: palettes packed back to back in one storage buffer, the base index pushed as a constant — no dynamic offset, so no padding to `minStorageBufferOffsetAlignment` and no maximum bone count. The palette is `inverseBindPose * boneWorld` already multiplied: one multiply per bone per frame rather than one per vertex. Dual-quaternion option still P2 |
| Blend shapes / morph targets | P2 | |
| GPU instancing | ✅ | `InstancingRenderFeature`. **The instance offset is a draw-call argument, not a binding** — `firstInstance` is added into `gl_InstanceIndex` before the shader runs, so a batch reaches its own run of one shared buffer with no descriptor, no dynamic offset and no fixed maximum. A batch is culled as one object, so batching by locality is the caller's call |
| LOD groups | ✅ | `LodRenderFeature`. A group is several render objects and this clears the bits of the levels a view is not showing — after culling, because an object outside the frustum has no screen size, and before sorting, because sorting builds the list a level must be absent from. Per view: a shadow cascade leaves `ScreenHeightScale` at zero and sees every level, since a shadow from a different mesh than its caster stops matching it. Hysteresis is asserted in both directions |
| LOD cross-fade | ✅ | Both levels visible for the transition, each pushed a weight. **Dither, not blend** — two translucent copies of one object write depth twice and sort against each other, where a dithered discard by weight makes the two levels' surviving pixels tile the silhouette exactly once, which is why the weights summing to one is asserted. Off by default: a fade doubles the draws for every object crossing a threshold |
| Impostors / billboards | P2 | |
| Sprites, sprite sheets, 9-slice | P1 | shares the UI batcher |
| Decals (deferred + forward clustered) | P2 | |
| Terrain (clipmap, virtual texture) | P2 | |
| Procedural primitives | P1 | cube/sphere/plane/capsule/torus/cone — needed for the editor and samples |
| Wireframe / unlit / debug materials | P1 | editor requirement |
| Mesh shaders / meshlet culling | P2 | capability-gated; the modern GPU-driven path |

### Lighting

| Feature | Pri | Notes |
|---|---|---|
| Directional, point, spot | ✅ | `ForwardLightingRenderFeature`: per-object lists, one dynamic-offset uniform block per draw. Lights are selected against **objects, not the view frustum** — a lamp behind the camera lights what is in front of it, so frustum-culling lights would darken exactly what is on screen. Range is measured to the sphere's surface, and the ranking is the falloff the fragment will evaluate, so "the eight brightest" means the same on both sides |
| Area lights (rect/disc/tube) | ✅ | Sphere, tube and rectangle, through Karis's **representative point** rather than LTC: shade the point on the shape nearest the reflection ray and widen the lobe by the angle the shape subtends. A rectangle also takes its own cosine, which is what makes it a panel rather than a glowing slab. ⚠ **Not LTC, which this row asked for.** LTC replaces two approximations — a highlight with the right size and the wrong shape, and a diffuse term that treats a near light as a point on it — with a closed-form polygon integral, at the cost of a fitted 64×64 table that comes from an offline optimisation this repository cannot run. Adding it is adding that table and a second `Resolve`; nothing is in its way. The five kinds share one 80-byte record and one loop, so clustering and the per-object light list needed no second path |
| Ambient / environment (IBL) | ✅ | Both halves, and the producers for them: `EnvironmentBaker` prefilters a cube per roughness by GGX importance sampling and `SphericalHarmonics` projects it into nine coefficients, on the CPU where a bake belongs and where closed forms can check it. Two defects fell out — the pass sampled the reflection at mip zero whatever the roughness said, so `Ibl.SpecularLod` and `environmentMipCount` were both dead; and the diffuse term fed it a *radiance* sample where irradiance belongs, which is where the missing `1/π` in `Ibl.Diffuse` was hiding |
| Light probes (SH, tetrahedral interpolation) | P1 | Stride has this (`LightProbes`); it is the pragmatic indirect-diffuse answer. ⚠ **Attempted and withdrawn.** Bowyer–Watson over the probe positions is fifteen lines of idea and a wall of robustness: an oversized enclosing tetrahedron makes every circumsphere swallow the domain (four probes produced no cells at all), a grid of probes is *cospherical* so a strict in-sphere test finds no cavity, and even with both fixed a near-degenerate cell's circumsphere is large enough to eat the mesh. Doing it properly means exact predicates. The SH side it would feed — projection, linear blending, evaluation — is built and tested |
| Reflection probes (box/sphere projected, blended) | ✅ | Parallax-corrected against a box or a sphere, faded against the environment over the probe's own blend distance, and selected by priority then volume so a cupboard inside a room wins inside the cupboard. Selected **per object**, and it costs an `int`: the cubes are one binding with a count bound for the frame, the volumes are an array beside them, and `ForwardLightingRenderFeature` writes the index and the weight into the padding std140 already left after the light count. `SceneLighting` fills that array from the same selector in the same order — the array's length off the shader's own plan, the spare slots taking the sky's cube, since the shader samples the slot before it weighs it. ⚠ Blended against the **sky** rather than against a second probe |
| Shadow maps: CSM (directional) | ✅ | `ShadowMapRenderer` — **a cascade is a view**: four `RenderView`s over one stage, culled and sorted by machinery that knows nothing about shadows, into four tiles of one atlas in one pass. Crawl is fixed at its two sources: a *sphere* fit (so turning does not resize the cascade) and texel snapping (so sub-texel movement gives a bit-identical matrix). What a shading pass reads is published into set 0 by the node itself — the matrix with its atlas tile folded in, the texel size, the biases and the sampler. Selection is **per fragment**: the block holds a `ShadowCascade[CascadeCount]` — a matrix and the distance it is valid to, together — and a fragment picks the nearest one that still covers it from its own view depth. It read one matrix and one distance until then, so everything past the nearest slice projected outside its tile and came back unshadowed, which reads as a shadow distance far shorter than the setting. The last cascade's end is ramped by `Lighting.CascadeFade` rather than stopping at a line across the ground |
| Shadow maps: cube (point), perspective (spot) | ✅ | `PunctualShadowRenderer`. Short where cascades are long, and the reason is worth stating: a punctual light *already is* a volume, so nothing has to be invented from the camera and nothing has to be stabilised. Six 90° frusta tile the sphere exactly — asserted over ten thousand directions, because a seam in a shadow cube is light through a wall along one line. A point light is six tiles and a spot is one, and a light that does not fit is dropped **whole** and counted |
| Shadow filtering: PCF, PCSS, VSM option | P1 | PCF default, PCSS for soft area shadows |
| Shadow atlas + caching for static casters | ✅ | Directional cascades. Two things had to be true together: the projection has to stop moving, which `ShadowMapRenderer.Slack` buys by cutting the cascade wider than its slice and keeping it while it still covers one — trading resolution for stability, since the same texels then cover 1.5625× the area at 25%; and the static casters have to be separable, which a second `RenderStage` already is, so no filtering machinery was needed. The cache is redrawn only when a cascade re-fits or the host bumps `StaticVersion`, and `StaticRebuilds` is what makes "it caches" checkable. Punctual lights are still redrawn every frame |
| Contact shadows (screen-space ray-marched) | P2 | |
| Baked lightmaps + GI bake | P2 | large; a separate lightmapper tool |
| SSGI / RTXGI-style probes | P2 | |
| Volumetric fog / lighting (froxel) | P2 | shares the clustering grid |
| Light shafts | P2 | Stride has it |
| Emissive as light source | P2 | |

### PBR / BSDF (written in Raven — see [07](07-raven-shader-pipeline.md))

The material model follows Stride's composable feature architecture (`IMaterialDiffuseModelFeature`,
`IMaterialSpecularModelFeature`, …), which is closer to Disney/Filament's principled model than
Unity's fixed lit shader and is the correct shape for a shader-graph-backed system.

✅ **Built, through `compose` rather than through a mixin resolver.** A pass declares two slots —
`surface: IMaterialSurface` for what a point on the surface is, `shading: IShadingModel` for what it
does with light — and each feature is a shader implementing one of them, resolved when the effect is
compiled. So a material with no clear coat contains no clear-coat code, rather than a branch that is
always false. `MaterialCompiler` (`Vixen.Rendering.Materials`) turns an authored tree into the
composition that selects those shaders and the parameters that feed them, and the composition is part
of the `EffectKey` — two materials differing only in features are two variants, which a key carrying
only permutations could not express. Details, including the one constraint the whole shape is built
around, are in [Vixen.Rendering's README](../../Core/Vixen.Rendering/README.md#materials).

✅ **The forward pass declares which set each of its bindings is in**, which it did not — so every one
of them defaulted to set 2, the material's, including the light list, the camera and the scene's
environment. `ForwardLightingRenderFeature` writes the per-object block and binds it at set 3, so the
shader and the feature that fills it disagreed about which set it was in and nothing said so. Now:
set 0 the scene, set 1 the shared view block, set 2 the material (1888 bytes to **32**), set 3 the
object. `world` is a push constant, because that is what `TransformRenderFeature` already does with it
— which also leaves the per-draw block with exactly one owner. `ForwardPlusLayoutTests` holds the
offsets against the checked-in reflection, so the two cannot drift apart again quietly.

✅ **And a material binds its own resources**, which was the last thing a host had to do by hand. A
material knows it has a texture called `albedo`; which binding index that is belongs to the compiled
shader, so until the binding plan reached the runtime somebody had to write the number down and hand
over a finished descriptor set. `MaterialRenderFeature` now writes it: the uniform block through
`EffectConstants`, every texture, sampler and storage buffer looked up in `Effect.Bindings` by the
shader's own name for it. The same fix that made a compositor node's bindings authorable, applied to
the other half of the same gap.

Per variant rather than per material, because a permutation can fold a texture out of the shader and a
set written for the variant that has it does not fit the layout of the one that does not — which is
also what keeps a depth prepass binding nothing. Every binding or none, because a set short of an entry
is a validation error on one backend and a sampled black texture on another. Through the frame
allocator, because a value that changes must not be rewritten under a frame still reading it.

| Layer | Options |
|---|---|
| Diffuse | Lambert, Oren–Nayar, Burley (Disney), energy-conserving variants |
| Specular | Cook–Torrance microfacet with pluggable NDF (GGX, Beckmann), visibility (Smith-correlated, Schlick, Implicit), Fresnel (Schlick, Schlick-with-f90, Complex/Gulbrandsen for metals) |
| Multi-scatter | Energy compensation for GGX (Fdez-Agüera / Turquin), on by default — the difference between "looks like 2015" and "looks right" |
| Clearcoat | second GGX lobe with its own normal map, IOR 1.5 default |
| Anisotropy | tangent-space aligned GGX |
| Sheen | Charlie / Ashikhmin for cloth |
| Hair | Kajiya–Kay (cheap) and Marschner R/TT/TRT (quality) |
| Subsurface | pre-integrated skin LUT + Burley separable SSS blur (Stride has both) |
| Transmission / thin-film | refraction with rough transmission, thin-walled option |
| Displacement | vertex displacement + parallax occlusion mapping |
| Layering | Stride's `IMaterialLayers` — N materials blended by mask, resolved at shader-compile time |
| Cel / stylised | Stride's `CelShading` — proves the model handles non-PBR |
| Workflows | metallic-roughness (primary), specular-glossiness (import compatibility) |

Colour management, stated once and enforced: **linear working space, sRGB textures decoded on sample,
HDR render targets (`R16G16B16A16_Float` or `R11G11B10_Float`), ACES-fitted or AgX tonemap, sRGB or
Rec.2020-PQ encode at present.** An `OutputColorSpace` on the swapchain description covers SDR/HDR10/
scRGB displays.

### Post-processing (`Vixen.Rendering.PostFx`)

Stride's `Images/` directory is essentially the complete list, and the set is right:

✅ **`Vixen.Rendering.PostFx` is where they live**, as of the effects marked below. The project exists
because the *set* is content-shaped — added, removed and reordered per project — where the compositor
and the render graph are the engine's spine; a game that ships no outline should not link one. Its
`PostEffectRenderer` holds what every effect has in common and a subclass answers four questions:
which shader, which permutations, which textures on which bindings, and its own parameters.

Adding it needed one thing from `Vixen.Rendering`: `SceneRenderer`'s three phase methods are
`protected internal`, so a composite node *outside* that assembly could not drive a child — which
would have made "a post effect is a node over a full-screen pass" a sentence only the engine could
write, and a game's own effect impossible. `BuildChild` and its two siblings are that seam, and
deliberately the only thing that widens.

Bloom and tonemap moved across too, which is what made the seam necessary rather than merely nice: a
document naming `!Bloom` has to be built by something, and the builder that reads documents cannot
reference the project the node now lives in. `ISceneRendererFactory` is that something — whoever
defines a node kind supplies the factory that builds it, so a game's own effect is a node kind on the
same terms as a shipped one.

Every entry below is a `FullScreenRenderer` or a node built out of several. ✅ **The full-screen pass
is the edge every one of them was waiting on**: everything else in the compositor draws *objects*, and
a post effect has none. It draws three vertices generated from `SV_VertexID`, so there is no vertex
buffer to bind and no quad's diagonal seam across the middle of the screen; it fills its own uniform
block from an `Effect`'s parameter table, which is what lets a post effect be configured by name
rather than by generated code; and the two caches behind it — `SamplerCache` and `EffectConstants` —
are shared, because a chain that made a sampler per pass would reach a driver's limit rather than
merely waste one.

| Effect | Pri | Implementation note |
|---|---|---|
| Depth prepass / Z-prepass | ✅ | `RenderStage.ShaderName` is what makes it a prepass rather than a second shading pass: one stage draws the objects with `DepthOnly.rvn` while another draws them with their materials, off one extraction and one cull. The per-material set is bound only where the resolved effect declares one, so a depth-only pipeline is not handed a layout it does not have. Every object in the prepass resolves to the same variant, so the stage's sort collapses to pure front-to-back — which is what makes early-Z reject the most |
| **TAA** | ✅ | `TemporalAntialiasingRenderer` in `Vixen.Rendering.PostFx`. It owns its history and alternates two textures, because a pass cannot read the target it writes — and they are *imports* rather than graph resources, since a transient dies at the end of the frame and a history that dies every frame is a history of nothing. The jitter sequence is exposed rather than applied: what it offsets is the projection, which belongs to the view |
| FXAA | ✅ | `FxaaRenderer`. Needs no history, no motion vectors and no depth, which is why it is the fallback wherever the others cannot go |
| SMAA | P1 | 1×/T2× for the no-TAA case |
| MSAA (forward only) | P1 | 2/4/8×, with a custom depth resolve (Stride has `MSAADepthResolverShader`) |
| Upscaling hook | P2 | a `IUpscaler` interface so FSR/XeSS/DLSS can be plugged; ship FSR1 (spatial, no licence friction) in-box |
| SSAO / GTAO | ✅ (SSAO) | `AmbientOcclusionRenderer` over `Ssao.rvn`, at half resolution by default — occlusion from a hemisphere is low frequency almost everywhere, so the cost halves twice and only contact edges notice. The march steps in the *depth buffer's* texel grid rather than its own half-size target's, which is the one thing about running it at a fraction that can be silently wrong. Bent normals are a permutation the shader has and nothing yet consumes; the full GTAO horizon integral is still to come |
| SSR (screen-space reflections) | P1 | Stride's `LocalReflections`; hierarchical depth trace |
| Bloom + lens flare + light streak | ✅ (bloom) | `BloomRenderer`, in `Vixen.Rendering.PostFx`: Jimenez's 13-tap downsample and 9-tap tent upsample, one shader in three permuted modes. The pyramid is **declared**, so nine textures and nine passes vanish when nothing reads the result. Each pass steps in its *source's* texel grid — taking it from the target makes a bloom that is subtly too soft and that no screenshot answers. Lens flare and light streak still to come |
| Depth of field | P1 | bokeh, near/far, physical aperture params |
| Motion blur | P2 | camera + per-object from motion vectors |
| Tonemap + colour grading | ✅ (the pass and the LUT) | `TonemapRenderer`, with the 3D grading table bound and `UseLut` folding the sample out of the variant when there is none. ACES, AgX, Reinhard and Uncharted curves, exposure, white point, contrast, saturation, white balance and split toning are the shader's; what is still missing is the table as an *asset* — something that imports a `.cube` and hands over a texture |
| Auto-exposure | P1 | histogram-based luminance in compute, with adaptation curve |
| Fog (linear/exp/height) | ✅ | `FogRenderer`. A post-process because fog depends on distance, which the depth buffer already holds for every pixel — putting it in every material would mean every material carrying its parameters and evaluating it whether it is on or not |
| Vignette, chromatic aberration, film grain, dithering | ✅ (three of four) | `VignetteRenderer`: one pass, three permutations, because they are one look and each is one or two taps. Grain moves with a frame index — grain that does not is a texture stuck to the screen, which is worse than none. Dithering is not in it |
| Outline | ✅ | `OutlineRenderer`, from depth and normal discontinuities. Screen space rather than geometry: the alternative needs adjacency the importer would have to build, and scales with the scene rather than the screen |
| Subsurface-scattering blur | P2 | |
| Sharpen (CAS) | ✅ | `SharpenRenderer`, contrast-adaptive, to put back what antialiasing and upscaling took out |

Each effect is an `ImageEffect` (Stride's `ImageEffectShader` model: a Raven shader + declared inputs
+ a parameter block), so the chain is data-driven and user-extensible, and each declares a
compute and a fullscreen-triangle variant so WebGL2 has a path.

### Other renderables

| Feature | Pri |
|---|---|
| UI rendering (world-space and screen-space) | P1 — the `Vixen.Ui` bridge |
| VFX / particles | P1 — see below |
| Skybox (cubemap, procedural physical sky) | P1 |
| Trails / ribbons | P2 |
| Line/gizmo/debug renderer | P1 — editor dependency |
| Text in 3D (MSDF) | P1 |
| Video textures | P2 — ✅ `Vixen.Video`: WebM in, Opus for the sound, the picture on the sound's clock, three `R8` planes and the coefficients a shader converts them by. ✅ `Vixen.Video.Rendering` draws them — one pipeline, a quad in any rectangle, `VideoRenderFeature` in a `ByGroup` stage, `VideoSurfaceUploader` for the ECS path — and `VideoRenderTarget` converts one into an ordinary colour texture, which is what makes a video nameable by `UiRenderer.RegisterImage` and by anything else that binds one view. **Screen-space only**: a video lit as a texture on a mesh is a material, and that is what is owed |
| VR/XR stereo (multiview, OpenXR) | P2, **not in 1.0** — `Vixen.Xr` + `Vixen.Xr.OpenXR` exist and are tested against a simulated headset: session, per-eye asymmetric projections, runtime-owned swapchains, actions. Nothing renders into the eye buffers yet and single-pass multiview is unwritten, so treat it as a parked spike rather than a feature. See [14](14-roadmap.md) |

## VFX pipeline

Your brief: "Visual Effect pipeline similar to the Stride. node based editor similar to unity." That
is Stride's `Vixen.Particles`-equivalent runtime with Unity VFX Graph's authoring UX — a good
combination.

**Runtime (`Vixen.Vfx`).** A particle *system* is a compiled graph:

```
Spawners  → burst, rate, distance-based, on-event
Initializers → position (shape: point/sphere/box/cone/mesh/spline), velocity, size, colour,
               lifetime, rotation, custom attributes
Updaters  → gravity, drag, force fields, curl noise, collision (depth-buffer or Jolt),
            attribute-over-lifetime curves/gradients, sub-emitters, trails
Renderers → billboard (camera/velocity/fixed-axis aligned), mesh, ribbon, light
```

- **Storage is SoA `NativeArray` per attribute**, with attributes allocated only if the graph uses
  them — the same "declare your own data" idea as `RenderDataHolder`.
- **CPU simulation** for small counts, editor-authoring, and WebGL2. **GPU simulation** (compute
  simulate + indirect draw + GPU sort) for large counts, capability-gated. The graph compiles to both
  — the same node graph emits a C# job body and a Raven compute shader. This dual-target compilation
  is the interesting engineering and needs to be designed in from the start rather than retrofitted.
- Deterministic RNG per particle (hash of index + seed), so CPU and GPU paths agree and effects are
  reproducible for tests and replays.
- Sorting: none (additive), by depth (CPU radix or GPU bitonic), or by age.

**Authoring** is `Vixen.Editor.VfxGraph` — see [11](11-editor.md).

### 🟡 Status: both targets are emitted; only one of them runs

`Vixen.Vfx` has the storage, the compiled graph, the CPU simulation, billboard geometry and
`ParticleRenderFeature`. `VfxShaderEmitter` closes the other half of the dual target on paper: the same
compiled graph becomes a Raven compute shader, and the tests compile it and hand both targets to
`glslangValidator` and `spirv-val`. What is not built is the dispatch — nothing uploads a particle
buffer, runs the kernel or reads it back — so the exit criterion's CPU/GPU agreement test is waiting on
a device rather than on the translation.

Three things settled while writing it, recorded because they are the reasons rather than the results:

- **The sweep order inverts between the targets, and that is fine.** The CPU runs one operation across
  every particle to keep the opcode dispatch out of the inner loop; a compute invocation runs the whole
  graph on one particle, because it has no inner loop and every intermediate can stay in a register.
  Both are correct because no operation reads another particle — which is worth stating, since it is the
  property that makes the dual target cheap.
- **The graph is unrolled into the shader, not uploaded and interpreted.** One shader per graph rather
  than one shader for every graph, and no branch on the hot path of the processor that likes them least.
- **Spawning and reaping stay on the CPU, for now.** Spawning is bookkeeping with one right home.
  Reaping was blocked and is not any more: writing the emitter is what put `atomicAdd` into Raven
  ([07](07-raven-shader-pipeline.md) § Atomics), and GPU compaction is every survivor taking the next
  slot from a shared counter. It waits on the dispatch rather than on the language. When it lands the
  two backends will leave the survivors in *different orders*, which is fine and is written down
  anyway: nothing promises an order, and a particle's randomness follows its identifier rather than
  its slot.

## Effect permutations

The problem Stride solves with `EffectSystem` + `.sdfx` mixins, and the thing that makes a
material/shader system usable at all.

- A material + a render stage + a set of feature flags (skinning on/off, instancing, shadow-receiving,
  light count bucket, fog, MSAA sample count, colour space) defines a **permutation key**.
- `EffectSystem` maps key → compiled `Effect`, with three tiers of cache:
  1. In-memory dictionary (frame-to-frame).
  2. On-disk bytecode cache keyed by `(RavenSourceHash, PermutationKey, Backend)`.
  3. Build-time pre-generation: the content build enumerates permutations reachable from the project's
     materials and compositors and bakes them into a bundle. Shipping builds must have **zero**
     runtime shader compilation.
- Development builds compile on demand, asynchronously, rendering with a placeholder (magenta/checker)
  material for the frames until ready — never a hitch, never a stall.
- Mobile/console iteration uses the **remote shader compiler** (`Tools/Vixen.ShaderCompilerService`,
  Stride's `EffectCompilerServer` pattern): the device requests a permutation over TCP, the dev
  machine compiles and returns it, the device caches it. This is what makes on-device shader iteration
  tolerable and it is worth building early.

### ✅ Status: all three tiers are built, and the exit criterion is a test

`EffectKey`, `Effect` and `EffectSystem` are in `Core/Vixen.Shaders`, with `ParameterCollection`
beside them. Three things the implementation settled:

- **`IEffectProvider` is what makes "zero runtime shader compilation" structural.** A tier is a
  provider, asked in order; a shipping build supplies only the one backed by the baked bundle and
  never references the compiler, so it *cannot* compile a shader — not because a flag forbids it, but
  because the code was never linked in. The remote compiler becomes a provider rather than a special
  case, and so does the on-disk cache.
- **A miss is recorded rather than hidden.** `EffectSystem.Misses` exists so the "no runtime
  compilation in shipping" claim in the table below can be a *test*: run a playthrough against the
  bundle alone and assert the list is empty. Half of that assertion was already specified here; this
  is the other half.
- **`Effect` holds bytecode and layout, not a pipeline.** A pipeline also depends on the vertex
  layout, the render pass and the blend and depth state, none of which a shader knows — so one effect
  backs many pipelines, and keying pipelines by effect alone is a cache that hands back an object
  drawn with the wrong blend mode.

`ParameterCollection` keeps values and permutations apart rather than distinguishing by key type at
every use, which is what makes "what is this frame's effect key" a field rather than a filter. Only
the permutations Raven reports as `UsedPermutationKeys` reach the key — the difference between a
tractable cache and 2ⁿ entries where a handful are distinct — and the values are sorted by name, so
the same settings in a different order are the same key rather than a cache that never hits.

#### What the three tiers turned out to need

**A form for a variant that the compiler is not needed to read.** Raven already writes `.rvnfx` —
bytecode, reflection, permutation key, source hash — and it is unusable at run time, because
`CompiledEffectReader` lives in the compiler assembly and reading one links the parser, the lowerer
and both backends. So `Vixen.Shaders` has **`EffectData`**: the same content, device-independent,
carried by the engine's own serializer. The disk cache, the baked bundle and the answer that comes
back over TCP are all that one record, and translating a `.rvnfx` into it happens once, on the build
side, in `Tools/Vixen.ShaderCompiler` — the only project that references both halves.

**The tiers answer with bytes, not with effects.** `IEffectProvider` gives an `Effect`, which is a
thing on a device; the tiers underneath implement **`IEffectSource`** and give an `EffectData`. That
is what lets them compose: a disk cache that missed can ask the dev machine and *write down what came
back*, which it could not do if the answer were already a set of device handles. One
`EffectSourceProvider` at the top turns whatever the stack produced into an effect, and a shipping
build's stack is one deep.

**The disk cache is keyed by (key, target) with the source hash checked rather than named.** Doc's
`(RavenSourceHash, PermutationKey, Backend)` has a direction problem: a reader has to be able to
*find* an entry, and a runtime asking for a variant does not know what the shader source hashed to —
the compiler that knew is the thing this tier exists to avoid running. So the hash rides inside the
record and `Expect` is what a host that does know sets. Every failure to read is a miss and never an
exception: a cache is an optimisation and its failure mode has to be "slower".

**Pre-generation is a fixed point, not a cross product.** Raven reports which keys a compilation
*read*, and the answer depends on the values — a flag guarded by another flag is unread until the
outer one is on. Compiling once with the defaults undercounts; the cross product of everything
declared overcounts by orders of magnitude. `PermutationClosure` compiles the defaults, enumerates
over what was read, and starts again if any of those compilations read something new. The set only
grows and is bounded by what the shader declares, so it terminates having compiled exactly the
variants that exist — three declared keys and two that matter is two shaders, measured rather than
claimed.

That enumeration also found a constraint worth writing down: **a shader whose used-key set depends on
its values has variants no draw can ask for.** The engine builds its key from the `UsedPermutationKeys`
in the reflection checked in beside the shader, and that reflection came from one compilation. If
`Inner` is only read inside `if (Outer)`, the generated key list does not contain `Inner` and nothing
can select those variants. The closure reports it as `Dependent`; the fix is in the shader, which
should read the inner key unconditionally.

**Numbers need a domain and booleans do not.** A `bool` has two values and enumerating them is
complete; an `int` does not, and which values matter is project knowledge — a light-count bucket is 4,
16 and 64 because of what the scenes look like. Unsupplied, a numeric key contributes its declared
default alone, and the variants nobody asked for show up as named misses rather than as a silently
short bundle.

**Where the manifest comes from is a playthrough.** No static analysis of a scene knows which shading
model a script switches to on level three. `EffectSystem.Requests` records every key anything asked
for — before the in-memory tier, so a key resolved once and cached is still in the list — and that is
an `EffectManifest`: JSON, because it is a build input people read, review in a diff and merge when
two branches each add a material. Play, dump, build, and the next run compiles nothing.

`vixen content build` is where that lands: `ProjectSettings/Shaders.effects.json` in, `shaders.effects`
beside the catalog out, with `--shader-target` picking the backend. No manifest is not a failure — a
project runs against a compiler in development, and the build says how to make one rather than
refusing to finish.

#### Compiling without stalling

The other half of the development story, and the half that decides whether anyone leaves the
compiler in the loop: `EffectSystem.Placeholder`. Setting it makes a miss return immediately with
something to draw and queue the real compile; `Pump()` produces them, called by the host off the
render thread and bounded by a count if a frame should only pay for so much. The system owns no
thread of its own, because how much CPU to spend compiling and against what else is a scheduling
decision the job system exists for — and a pump is testable without a clock.

**The placeholder is never cached, and that is the whole subtlety.** The dictionary holds what a key
resolved to; a placeholder is what it resolved to *for now*. Caching it makes the temporary answer
permanent, which is a magenta object that never becomes anything with nothing logged anywhere. The
matching half is that whatever *kept* the answer has to know it was provisional —
`MaterialRenderFeature` resolves a variant once and keeps it, and the real effect arrives some frames
later with nothing to announce it. `Effect.IsPlaceholder` is what it checks, and it re-resolves
exactly the variants still holding one.

`Raven/Library/Pipeline/Placeholder.rvn` is the shipped one: a screen-space magenta checker importing
nothing and binding one matrix. Screen space rather than UV space because it has to draw for a mesh
with no texture coordinates — the geometry most likely to be missing its shader — and one uniform
because it stands in for *any* shader, so its bindings must be ones every draw can already satisfy.

## Testing

| Area | Test |
|---|---|
| Extraction | Change-version test: a static scene extracts 0 objects on frame 2 |
| Culling | Frustum/occlusion results compared against a brute-force CPU oracle over randomised scenes; GPU culling output compared to CPU culling output |
| Sorting | Golden order for randomised scenes; front-to-back monotonicity assertions |
| Render graph | See [05](05-graphics-rhi.md) |
| Shading | BRDF unit tests: white furnace test (energy conservation within tolerance across roughness), reciprocity, Fresnel limits at grazing angles. These run as **CPU ports of the Raven functions**, cross-checked against the compiled shader via a compute readback — catching the class of bug where the shader and the reference diverge |
| Golden image | ~40 fixtures on lavapipe with perceptual diff (see [05](05-graphics-rhi.md)); one fixture per BSDF layer, per post effect, per pipeline preset |
| Permutations | The build-time enumerator's output is asserted to be a superset of what a playthrough of `Samples/05` requests at runtime — i.e. "no runtime compilation in shipping" is a *test*, not a hope |
| Performance | CPU-side frame benchmark: 10 000 objects through extract→cull→sort→record on the Null backend, with allocation assertions |
