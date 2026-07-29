# 19 — Lighting and Global Illumination

> ⚠️ **Amends [06](06-rendering-pipeline.md).** Doc 06's Lighting table plans indirect diffuse as
> *baked lightmaps plus tetrahedrally interpolated SH probes* — the 2015 answer. This document
> replaces those rows with a fully dynamic path modelled on UE5's Lumen, and says exactly which of
> 06's rows are retired rather than deferred. Where the two disagree, this one is newer.

Brief: get to AAA-standard lighting, in the direction UE5 went, without carrying forward techniques
that are already dead.

## The thesis, stated once

**Lumen does not abolish lightmaps, probes or SH. It abolishes the offline solve.**

This is worth being precise about, because "Lumen replaces baked GI" is the marketing summary and it
leads to the wrong architecture. What is actually inside Lumen:

| Lumen component | What it is, structurally |
|---|---|
| Surface cache | A low-resolution lightmap over per-mesh cards — **re-lit every frame** instead of baked |
| Screen probes | Radiance probes that resolve to **3rd-order SH** before per-pixel integration |
| World-space radiance cache | A **clipmap of probes** in a 3D pool with an indirection lookup — the same structure as Unity's APV and UE's own Volumetric Lightmap |
| Voxel lighting | A directional radiance clipmap, so a ray hit can read scene lighting without re-shading |
| Direct lighting | **Shadow maps** (virtual ones), not traced shadows, by default |
| Fallbacks | Reflection probes and skylight SH, when a trace misses or leaves the cache |

Every data structure the "old" approach needs is a data structure Lumen also needs. What changed is
the *filler*: rays at runtime instead of a lightmapper offline. That single observation is what makes
this plan tractable solo, and it is the load-bearing decision in §3.

And empirically: **Epic kept the baked pipeline.** Lightmass, GPU Lightmass, lightmap UVs and the
Volumetric Lightmap all still ship in UE5, because Lumen does not run on mobile, on Switch, on most
standalone VR, or under its performance floor. Deleting the static path was never on Epic's table,
and it is not on ours — see §7, where Vixen's own committed targets make that impossible.

## 1 — What Lumen actually costs, and what it still cannot do

Worth writing down before committing, so the target is a real one rather than a logo.

**Cost.** Lumen's budget is roughly 4–8 ms/frame at *reduced internal resolution* on current-gen
console, and it is unusable without TSR or an equivalent temporal resolve — the raw gather is noisy
by construction. Anyone reimplementing this discovers that the tracing is the easy half and the
denoiser is the project.

**Standing limitations, in UE5 today:**

- Deforming geometry is not in the surface cache. A skeletal mesh does not contribute correct
  indirect light off-screen under software tracing.
- Material world-position offset is invisible to both the SDF and the cards, so anything that moves
  in the vertex shader lights as though it did not.
- Thin geometry leaks, bounded by SDF resolution.
- No caustics; specular GI is approximate; rough reflections read the cache rather than trace.
- Surface cache memory is a budget, and a large open world is a tuning exercise rather than a
  default.

So the honest target is **"the Lumen architecture, at a quality and performance bar set by one
engineer,"** not feature parity with a team that has iterated since 2020. Stated plainly here so the
roadmap is not measured against the wrong bar later.

## 2 — Where Vixen stands

Reconciled against the code, not against doc 06.

### Substrate — already built

| Prerequisite | Where | Note |
|---|---|---|
| Compute, indirect dispatch, storage buffers | [`ICommandList`](../../Core/Vixen.Graphics/ICommandList.cs), `ComputePipelineDescription` | `DispatchIndirect` present |
| Bindless, capability-gated | [`GraphicsDeviceFeatures.HasBindless`](../../Core/Vixen.Graphics/GraphicsDeviceFeatures.cs) | with a documented non-bindless path |
| Render graph, transient aliasing | [`Vixen.Graphics.RenderGraph`](../../Core/Vixen.Graphics.RenderGraph/) | |
| 3D textures | Vulkan, GL and WebGPU conversions all present | the probe pool's storage |
| **Motion vectors, history, variance clipping, jitter** | [`TemporalAntialiasingRenderer`](../../Core/Vixen.Rendering.PostFx/TemporalAntialiasingRenderer.cs) | the prerequisite people discover late |
| HiZ pyramid, GPU culling | `HiZPyramid`, `GpuCulling` | screen tracing reads the HZB |
| Bent-normal AO | `AmbientOcclusionRenderer` | already writes the average unoccluded direction |
| SH projection / evaluation / blending | [`SphericalHarmonics`](../../Core/Vixen.Core.Imaging/SphericalHarmonics.cs) | L2, tested against closed forms |
| A bake that is a build step, with `.meta` parameters and per-target overrides | `NavMeshImporter` | the precedent this plan's SDF bake copies exactly |

That is more of Lumen's floor than expected. The temporal machinery in particular is not a
nice-to-have — a screen-probe gather without reprojected history has nothing to filter against.

### Absent

Mesh SDF baking · global SDF clipmap · mesh cards · surface cache · surface-cache direct lighting ·
surface-cache radiosity · screen probe gather · world-space radiance cache · acceleration structures
in the RHI · virtual shadow maps.

Six or seven subsystems. No partial credit anywhere in that list.

## 3 — The architecture: one sampler, two fillers

The decision everything else follows from.

```
                      ┌──────────────────────────────┐
   runtime SDF rays ─▶│   Irradiance field           │
   (HasCompute)       │   3D pool + indirection       │─▶ one sample path in the shader
                      │   4³ bricks, borders, L1 SH   │    (integer math + trilinear)
   offline capture ─▶ │                              │
   (no compute)       └──────────────────────────────┘
```

**The storage and sampling layer is written once and does not know what filled it.** A brick is a
brick whether a ray tracer wrote it this frame or a cube capture wrote it at build time. Concretely:

- **Storage.** 4³ probes per brick in a 5³ physical footprint — the extra texel duplicates the
  neighbour so hardware trilinear works across a brick boundary without a seam. This is UE's
  Volumetric Lightmap detail, and it is the one everybody rediscovers the hard way.
- **Indirection.** A 3D index texture mapping world cell → brick slot in the pool. Sampling is:
  world position → cell → index fetch → UVW → trilinear. Integer arithmetic and two fetches. No
  Delaunay, no predicates, **no repeat of the tetrahedral failure recorded in doc 06**.
- **Payload.** L1 SH per probe (4 coefficients per channel), plus a validity scalar and a
  directional-light shadowing scalar. L1 not L2: half the pool, and it is what both Unity and Epic
  ship as default.
- **Refinement.** Bricks subdivide near geometry, up to 3 levels, from renderer bounds — which the
  `VisibilityGroup` already has.

Why this matters more than it looks: it is what lets §7's platform commitment survive. Shaders do not
branch on which filler ran. A phone gets the same lighting model as a desktop, at a different update
rate and a different quality, through the same code.

## 4 — What is retired, not deferred

Struck from doc 06 and from the roadmap. These are not "P2, later" — they are decisions reversed.

| Retired | Why |
|---|---|
| **Texture lightmaps and the whole GI bake tool** | The single biggest saving. Kills a lightmap UV unwrapper, a chart packer, seam fixing, an atlas allocator, and a UV channel in the mesh format. This tool is never written |
| **Tetrahedral light-probe interpolation** | Attempted, found wrong by its own tests, withdrawn. §3 is the replacement, and it cannot fail the same way |
| **Indirect-lighting-cache-style per-object probe sampling** | Subsumed by the irradiance field |
| **Baked static shadow data** | Replaced by the per-probe shadowing scalar, plus SDF shadows in L1 |

**Explicitly *not* retired**, because Lumen keeps every one of them: shadow maps, reflection probes,
skylight/IBL SH, clustered light binning, TAA. Doc 06's ✅ rows stand.

## 5 — The phases

Each ships something visible on its own. Effort in **EM**, matching [14](14-roadmap.md)'s convention.

### L1 — Distance fields *(2.5 EM)*

The tracing substrate. Neither the irradiance field nor Lumen exists without it, and it is the only
phase with no dependency on any other.

- **Offline:** voxelize each mesh into a sparse SDF (32–64³ typical), as an importer stage beside
  `ModelImporter`, with bake parameters in the `.meta` and per-target overrides — the `NavMeshImporter`
  pattern verbatim, including the byte-identical-rebake test.
- **Runtime:** composite instances into a camera-centred **global SDF clipmap** (4 levels) in 3D
  textures, updated incrementally as the camera moves.
- **Ships on its own:** distance-field soft shadows, and large-scale DFAO that beats the current
  screen-space AO wherever the occluder is off-screen.

An SDF is a function, so the bake checks against closed forms on the CPU with no device — the same
discipline `EnvironmentBaker` and `SphericalHarmonics` already follow.

**Exit:** a sphere's baked field matches its analytic distance to tolerance; the clipmap's traced
occlusion matches a CPU reference on a fixture scene; two bakes are byte-identical.

### L2 — The irradiance field *(2.0 EM)*

§3's structure, both fillers.

- Pool, indirection texture, brick refinement, borders, L1 payload, validity.
- **Filler A** (`HasCompute`): N bricks per frame round-robin, rays traced against L1's clipmap,
  cosine-projected into SH, blended against the previous value with a hysteresis term.
- **Filler B** (no compute): the offline cube-capture bootstrap — render a small cube per probe with
  the existing pipeline, project with `SphericalHarmonics`, iterate 2–3 passes feeding the previous
  result back as ambient. Reuses `EnvironmentBaker` almost verbatim. Not a lightmapper.
- Leak mitigation lands here, because it is where the leaks are: per-probe validity from backface
  hits, dilation into invalid probes, normal bias, view bias.

**Ships on its own:** dynamic indirect diffuse everywhere, on every target. This is the phase that
closes doc 06's withdrawn light-probe row, and the point at which Vixen has GI at all.

**Exit:** a closed box lit from outside stays dark (the leak test); moving a light updates indirect
within a bounded frame count; the same scene through filler A and filler B agree within a stated
tolerance.

### L3 — Screen probe gather *(3.0 EM)*

The Lumen final gather. The largest quality jump and the largest risk.

- Probes on a ~16 px screen grid, plus adaptive placement at disocclusions.
- Per probe: an octahedral radiance map (8×8), importance-sampled against the BRDF and against the
  previous frame's lighting.
- Trace order: screen traces against the HZB first, then L1's mesh/global SDF, then **terminate long
  rays in L2's field** so distant lighting is amortised rather than re-traced per probe.
- Spatial filter, temporal reprojection through the existing motion vectors, resolve to SH,
  bilateral-upsample to per-pixel irradiance.

**Budget the denoiser, not the tracer.** Un-denoised, this looks worse than L2 alone.

**Exit:** a reference-path-traced fixture matched within a stated error at a stated ray count; no
ghosting on the standard camera-cut and fast-pan tests.

### L4 — Surface cache and radiosity *(3.5 EM)*

Multi-bounce for geometry that is off-screen. The most Epic-specific chunk, and the most deferrable.

- Card generation offline (up to 6 orthographic captures per mesh, clustered from the geometry).
- Runtime capture into albedo/normal/depth/emissive atlases, budget-limited per frame.
- Direct lighting evaluated **on the cache**, then a radiosity pass over the cards themselves —
  which is where the infinite-bounce look comes from, cheaply, because it is a low-resolution 2D
  problem.

**Exit:** a Cornell-box fixture converges to a reference within a stated error; the second bounce is
visible and measurable rather than asserted.

### L5 — Reflections through the same tracer *(1.5 EM)*

Traced reflections reusing L1/L4, with rough reflections reading L2 and L4 instead of tracing, and
the existing reflection probes as the miss fallback. Retires doc 06's "⚠ blended against the sky"
caveat as a side effect.

### L6 — Hardware ray tracing *(2.0 EM)*

Acceleration structures in the RHI (`GraphicsDeviceFeatures.HasRayTracing`, capability-gated like
everything else), as an alternative tracer behind L1's interface. Nothing above it changes.

### Total, honestly

**~14.5 EM.** That is three times Phase 5's entire renderer and twice the UI framework. It is the
correct number for this target and it should be read as a multi-milestone track, not a phase.

**Cut line, decided in advance:** L1 + L2 is **4.5 EM** and delivers dynamic GI on every platform,
with no bake tool and no dead techniques carried forward. If only one thing is built, it is that.
L3 is the AAA jump. L4–L6 are what make it Lumen rather than Lumen-shaped.

## 6 — RHI work required

| Need | Status | Phase |
|---|---|---|
| 3D texture render/storage targets, `imageStore` into 3D | dimension plumbing exists; UAV-to-3D path needs checking | L1 |
| Sparse/partially-resident 3D textures | `HasSparseResources` declared; unimplemented | L2, optional — a fixed pool works |
| Texture atlas allocation and residency | none | L4 |
| Acceleration structures, ray queries | **absent entirely** — no concept in the RHI | L6 |
| Async compute for cache updates | `HasAsyncCompute` declared | L2+, optional |

Only the last is a new RHI *concept*. Everything before it is plumbing over abstractions that exist.

## 7 — The platform constraint, and why §3 exists

[10](10-platforms.md) commits to six targets, including WebGL2 (retired as a risk by an executed
spike), GLES 3.2 Android, and iOS through MoltenVK. And `GraphicsDeviceFeatures` documents
`HasCompute` as **false on WebGL2**, with the note that the absence *cascades*.

Doc 10's own rule is *"always a runtime capability query with a fallback, never `#if PLATFORM`"*, and
the feature record's is *"capability-gated with a documented fallback, never a hard requirement."*
A Lumen-only lighting path breaks both, for indirect diffuse specifically.

| Target | Filler | Gather | Result |
|---|---|---|---|
| Windows, Linux, macOS | runtime rays | L3 screen probes | full path |
| Android (Vulkan 1.1+) | runtime rays, reduced rate | L2 field only | dynamic GI, no screen gather |
| iOS / MoltenVK | runtime rays, reduced rate | L2 field only | as Android |
| Web / WebGL2 | offline capture | L2 field only | static GI, same shader |

One sampler, four configurations, no shader branching on platform. This table is the reason §3 is
the architecture rather than an implementation detail.

## 8 — Risks

| # | Risk | Mitigation |
|---|---|---|
| G1 | **The denoiser is the project.** L3 without good filtering looks worse than L2 alone | L2 ships standalone and is never regressed by L3; L3 is behind a setting from its first commit |
| G2 | SDF memory across a large scene | Sparse bricks; per-target resolution overrides in `.meta`, as the navmesh already does |
| G3 | Leaks through thin geometry — the defect users actually report | Validity + dilation + normal/view bias land **in L2**, not as polish; the closed-box test is an exit criterion, not a nice-to-have |
| G4 | Scope. 14.5 EM is larger than most whole phases | The L1+L2 cut line is decided in advance, above, not under pressure later |
| G5 | Reference implementations are licence-hostile — Unity's APV is Unity Companion, UE's is the Unreal EULA, neither Apache-2.0 compatible | Implement from published papers and talks; credit and re-derive, exactly as `Vixen.Navigation` did for Recast/Detour |

## 9 — What this changes elsewhere

- **[06](06-rendering-pipeline.md) Lighting table** — delete the *Baked lightmaps + GI bake* row;
  replace *Light probes (SH, tetrahedral interpolation)* with L2; replace *SSGI / RTXGI-style probes*
  with L3; add rows for L1's distance-field shadows and AO.
- **[14](14-roadmap.md) Phase 10** — currently *"Deferred, advanced rendering, Web"* at 2.5 EM. L1+L2
  belongs there and the estimate moves accordingly; L3–L6 are a post-1.0 track.
- **[05](05-graphics-rhi.md)** — add acceleration structures to the capability register as declared
  and unimplemented, so L6 has somewhere to land.
- **[../overview.md](../overview.md)** — the withdrawn light-probe entry becomes "superseded by 19"
  rather than "owed".
