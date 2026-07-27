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
driving extract → cull → prepare → sort. What is not built is **recording** — it needs the effect
system, which needs `ParameterCollection`.

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

A settled frame of 10 000 objects through extract → cull → sort **allocates nothing**, asserted by
test — the guard against a change that starts allocating per object per frame and surfaces months
later as a GC spike nobody can attribute.

Vixen keeps all three, with these changes:

- Extraction, culling, and command recording are **job-system parallel by default** rather than
  optionally so, and record into per-thread `CommandList`s.
- `RenderDataHolder` arrays become `NativeArray<T>` in SoA form, addressed by a dense `RenderObjectId`.
- Passes are submitted through the **render graph** ([05](05-graphics-rhi.md)), so barriers and
  transient memory are automatic.
- **GPU-driven culling** where capabilities allow: object bounds uploaded once, frustum + Hi-Z
  occlusion culling in compute, output an indirect draw buffer. The CPU path remains for GL/WebGL.

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

- **Clustering:** froxel grid (e.g. 16×9×24 with exponential depth slices), lights binned in compute,
  per-cluster light index list in a storage buffer. Falls back to tiled (2D) on GLES and to
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
| Static mesh | P1 | indexed, multiple submeshes, per-submesh material |
| Skinned mesh | P1 | GPU skinning, bone matrix palette in a storage buffer, dual-quaternion option |
| Blend shapes / morph targets | P2 | |
| GPU instancing | P1 | Stride's `InstancingRenderFeature` model; auto-batched by pipeline+material |
| LOD groups + cross-fade | P1 | screen-height-based, hysteresis to stop popping |
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
| Directional, point, spot, area (rect/disc/tube) | P1 | LTC-based area lights |
| Ambient / environment (IBL) | P1 | split-sum: prefiltered GGX cube + SH-9 irradiance |
| Light probes (SH, tetrahedral interpolation) | P1 | Stride has this (`LightProbes`); it is the pragmatic indirect-diffuse answer |
| Reflection probes (box/sphere projected, blended) | P1 | |
| Shadow maps: CSM (directional), cube (point), perspective (spot) | P1 | |
| Shadow filtering: PCF, PCSS, VSM option | P1 | PCF default, PCSS for soft area shadows |
| Shadow atlas + caching for static casters | P1 | re-render a cascade only when its content changed |
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

| Effect | Pri | Implementation note |
|---|---|---|
| Depth prepass / Z-prepass | P1 | |
| **TAA** | P1 | jittered projection, motion-vector reprojection, neighbourhood clamping, variance clipping. The default AA. |
| FXAA | P1 | cheap fallback / mobile |
| SMAA | P1 | 1×/T2× for the no-TAA case |
| MSAA (forward only) | P1 | 2/4/8×, with a custom depth resolve (Stride has `MSAADepthResolverShader`) |
| Upscaling hook | P2 | a `IUpscaler` interface so FSR/XeSS/DLSS can be plugged; ship FSR1 (spatial, no licence friction) in-box |
| SSAO / GTAO | P1 | GTAO with bent normals |
| SSR (screen-space reflections) | P1 | Stride's `LocalReflections`; hierarchical depth trace |
| Bloom + lens flare + light streak | P1 | dual-filter downsample/upsample chain |
| Depth of field | P1 | bokeh, near/far, physical aperture params |
| Motion blur | P2 | camera + per-object from motion vectors |
| Tonemap + colour grading | P1 | ACES/AgX/Reinhard/Filmic, 3D LUT, curves, white balance, split toning |
| Auto-exposure | P1 | histogram-based luminance in compute, with adaptation curve |
| Fog (linear/exp/height) | P1 | |
| Vignette, chromatic aberration, film grain, dithering | P1 | cheap, expected |
| Outline | P1 | editor selection needs it; Stride has it |
| Subsurface-scattering blur | P2 | |
| Sharpen (CAS) | P1 | |

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
| Video textures | P2 |
| VR/XR stereo (multiview, OpenXR) | P2 — `Silk.NET.OpenXR` exists; single-pass multiview in the RHI |

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

## Effect permutations

The problem Stride solves with `EffectSystem` + `.sdfx` mixins, and the thing that makes a
material/shader system usable at all.

- A material + a render stage + a set of feature flags (skinning on/off, instancing, shadow-receiving,
  light count bucket, fog, MSAA sample count, colour space) defines a **permutation key**.
- `EffectSystem` maps key → compiled `Effect` (pipeline + reflection), with three tiers of cache:
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
