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
  Delaunay, no predicates, and **no exact-predicate problem to have** — which is the point, and is
  now a claim about robustness rather than about a failure, since doc 06's tetrahedral row has
  since been fixed with `ExactPredicates` and reads 🟡 rather than ⛔.
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
| **Tetrahedral light-probe interpolation** | Retired as *this document's* answer, not as code: it was attempted, found wrong by its own tests, and has since been fixed — `ExactPredicates` + `DelaunayTetrahedralization` + `LightProbeVolume` are built and doc 06's row reads 🟡. §3 is still what the plan commits to, because a lattice inside a brick needs no predicates to be right and no triangulation to sample |
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

**Status: built, and it has now drawn.** The bake, the clipmap (which scrolls rather than
recomposites), the CPU tracer, the importer stage, the volume textures, the compositor node, the Raven
module and `DistanceFieldAo` all exist and are gated. `DistanceFieldAoImageTests` runs the pass through
a real compositor on a real device and reads the picture back: with `NoDistanceField` behind the slot
every pixel comes back `(1, 1, 0)` — fully open, fully lit, nothing in blue — which is the answer that
is knowable exactly and as far from a shader that did not run as a frame can be.

It found what a first execution always finds here. **A full-screen pass had no way to fill a compose
slot at all**, so `DistanceFieldAo` could not be built by a compositor under *any* composition: the key
carried none, the compiler refused the unbound slot, the effect system recorded a miss, and the node
drew nothing while looking exactly like a pass nobody scheduled. `FullScreenRenderer.Composition` is
the fix, and `DistanceFieldAoRenderer.Source` is what sets it — defaulting to `NoDistanceField`,
because a project with no clipmap has no field to trace. That is the sixth time something real running
has found something the layer below had agreed with itself about, after the unbound material slot, the
binding-name confusion, three errors in the first rendered scene, and the scroll's float associativity.

**And a frame now traces an actual clipmap.** `ATracedFrameSeesWhatTheFieldHolds` puts a ball above
the reconstructed plane, composites the clipmap, copies it up and reads the picture back: black under
the ball, lit at the corners. Every part of L1 at once, on a device.

Getting there found three more defects of the same kind, all of them structural and none of them
visible to anything that was not a whole frame:

- **A full-screen pass could not bind set 0.** A mesh feature binds it for a geometry pass and
  `RenderPassRenderer` only puts it in the context for children to find; a full-screen pass has
  neither. So a post effect whose shader declares anything per-frame — which is exactly what a
  composed clipmap is — declared a set nothing bound. `FullScreenRenderer.SceneConstants` is the fix.
- **`GlobalDistanceFieldRenderer` was in the wrong phase.** It overrode `Record`, which runs *inside*
  a render pass, and it records a buffer-to-texture copy — which is illegal there. It could never have
  run in a real frame. It now declares its own `PassKind.Transfer` pass in `Build`, marked as having a
  side effect because the volumes are not graph resources and a pass writing no graph resource is a
  pass the graph culls.
- **Neither GPU mirror transitioned its own textures.** Same root cause as the above: the volumes are
  named into a descriptor set rather than read through the graph, so nothing else in the frame knows
  they exist. A texture never moved out of `UNDEFINED` is a validation error at the copy, and one left
  in `TRANSFER_DST` is one at the draw that samples it. Both were true, of the distance-field mirror
  and of the irradiance one, and both now barrier around their own copies.

That is the pattern this document has recorded five times now, and it is worth stating as a rule
rather than as a list: **a layer checked only against its own mirror is a layer that has not been
checked.** Every one of these passed every test it had.

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
supersedes doc 06's light-probe row, and the point at which Vixen has GI at all. Superseded rather
than closed: that row's CPU half was repaired after this document was written, so the two are now
alternative answers rather than a replacement for a hole.

**Exit:** a closed box lit from outside stays dark (the leak test); moving a light updates indirect
within a bounded frame count; the same scene through filler A and filler B agree within a stated
tolerance.

**Status: filler A is complete on both sides, repair included, and the two agree; filler B is not
started.**
[`Vixen.Rendering.IrradianceFields`](../../Core/Vixen.Rendering.IrradianceFields/README.md) holds the
payload (`SphericalHarmonicsL1` plus validity and a sun-shadow scalar), the pool (4³ probes in a 5³
footprint, fixed capacity, cleared on release), the indirection grid, and the border sync. The seam is
closed and the closure is checked the way a bake is: a field filled from a linear function of world
position is reproduced *exactly* across a brick boundary, and the same field with its borders left
alone is badly wrong — because a layout detail that looks like padding needs a test that fails when
you remove it.

Dilation and the normal bias are in, and running them turned the leak criterion above into something
sharper than it was written as. **The pass count is not the knob** — a repair never overwrites a valid
probe, so each face of a wall fills inward from its own side and the two meet without mixing, and the
closed-box test passes at one, two and eight passes alike. The knob is **how thick a wall is in
probes**: three works, exactly one leaks at full strength in a single pass, and thinner than the probe
spacing is worse still because every probe is then valid and there is nothing to repair or to notice.
Both failures have tests asserting that they *do* leak, so the day refinement fixes them the tests say
which one it fixed. That makes refinement a leak fix rather than a memory optimisation, which is not
how § 3 currently reads.

**Filler A now has a CPU reference**, the way the distance-field tracer had one before its shader port:
sixty-four Fibonacci directions per probe marched through an `IDistanceField`, cosine-projected into
L1, validity from the field's sign, a sun-shadow ray, hysteresis, and a resumable budget. The exit
criterion above is asserted end to end against it — a field filled inside a closed box, dilated and
synced, is dark at every interior point, because each ray hit the inside of the shell before it
reached anything bright.

It also corrected this section. **The backface vote cannot fire against an exact field**: sphere
tracing stops where the field crosses zero on the way down and the gradient there always opposes the
ray, so the sign answers every time. The vote earns its place against a *sampled* field, where an
over-reported step lands past a thin wall and the surface it then finds is seen from behind. Both are
implemented; § L2's bullet should say which one answers when, rather than naming the vote alone.

**Refinement is in**, as a brick size stored beside the slot in every cell the brick covers — Epic's
arrangement, and the reason a lookup never searches or climbs. `Allocate` covers a region at a size and
`Refine` splits what overlaps another until it is fine enough; a split discards the parent's probes
rather than interpolating them down, because interpolated children look converged and a filler would
then be blending toward the truth from a lie.

Two things fell out of it that this section did not anticipate. **There is no field-wide probe lattice
once bricks differ in size** — "the probe next door" becomes a question about world positions, and
dilation and the filler's walk both had to be rewritten in those terms. And **border sync has an
order: coarsest first.** A fine brick borrowing from a coarse neighbour interpolates that neighbour's
field at a position that can fall in the coarse brick's own border plane, so the coarse brick has to
be finished first; the reverse never happens. The seam test on a refined field is what found it, and
the obvious way to make a pass order-independent — compute everything, then write everything — is
exactly what breaks it.

**The GPU side is in**: `Raven/Library/IrradianceFields` for the lookup and
`Vixen.Rendering.Lighting.IrradianceFieldTexture` for what feeds it. Four pool volumes rather than six
— validity rides in the constant term's alpha and the sun's shadow in the red channel's — packed
colour-major so one fetch gives one colour's three coefficients, which is what the evaluation wants.
The index volume is point sampled and therefore always half-precision; it holds integers, and `Upload`
refuses a pool past 2048 texels an axis rather than storing an origin that rounds. Two indirection
fetches per pixel, the first only to learn the brick size the normal bias is measured in.

The convention is pinned the way L1's was, and the same way round: a test walks the shader's
addressing in C# and asserts it reaches the texels the field's own sampler reads — on a refined field
as well as a uniform one, because the divide by the brick size is a step a uniform field never
exercises.

**And L2 has drawn.** `IndirectDiffuse` is the consumer: a screen-space pass composing
`IIrradianceSource`, with `IrradianceFieldRenderer` filling a budget of bricks a frame, dilating,
syncing borders and copying the field up in a transfer pass. `IndirectDiffuseImageTests` runs it on a
device — an empty world under a uniform sky of radiance *L* comes back as a flat frame of *L*, which
is the same closed form the projection and the filler are each held against, now reached through the
pool, the index volume and the shader's basis. *L* is deliberately neither a half nor a one, because
the g-buffer clears to halves and the shader writes a one into alpha, and a radiance equal to either
would pass for a picture that had merely copied something through.

**And the shading models read it.** `ForwardPlus` now composes `IIrradianceSource` and has a
`UseIrradianceField` permutation, so the field's answer *is* the ambient diffuse where it has one — the
screen-space pass is a debug view and a compositing input rather than the way light reaches a surface.
`IrradianceShadingDeviceTests` draws a Lambertian quad of albedo *C* in a field filled from a uniform
sky of radiance *L* and asserts the pixel is *C* × *L*, per channel, with every other source of light
switched off and a companion frame with only the flag flipped coming back dark.

Four things that decided the shape:

- **It replaces the sky's ambient rather than adding to it**, weighted by the probe's validity. The
  field's rays already hit the sky, so its answer contains what the sky contributes; adding them counts
  the sky twice and the second count is the brighter, because it is unoccluded. Where the field has no
  answer the sky is the fallback, which is the whole reason a *coverage* number had to exist.
- **`IIrradianceSource` became one method returning three numbers.** It had an accessor per number and
  no coverage at all, so a consumer wanting two of them paid for the two indirection fetches and four
  filtered ones twice — which `IndirectDiffuse` did, while its own comment said it did not.
- **The π nearly got counted twice.** The field stores irradiance divided by π, which is what a shading
  pass multiplies by albedo; `Ibl.Diffuse` takes plain irradiance and divides by π itself. Handing one
  to the other unscaled is a scene lit 3.14 times too dim — dark enough to read as a tuning problem and
  bright enough not to read as a bug. Only a per-channel closed form catches it.
- **Which shader fills the slot is a project's decision, not a material's.** Whether the scene has a
  field is true of every material in it at once, so `MaterialCompiler.Compile` takes the override as a
  parameter rather than the descriptor carrying it — two materials disagreeing would be two effects
  where the project meant one.

The blast radius was the known one and it was survivable: `("ForwardPlus", "irradiance", …)` in
`OptionalSlots` means every material names `NoIrradiance` by default and declares no bindings for it, so
a project without a field draws exactly the frame it drew before.

**Filler A dispatches, and agrees with the reference to a ten-thousandth.** `IrradianceFill` — one
workgroup per brick, one invocation per probe, the bricks to do in a buffer indexed by the group — is
driven by `IrradianceFieldFill`, and `IrradianceFillDeviceTests` runs it on a device and reads the pool
back. Every one of the sixty-four probes of every brick is the probe `TracedIrradianceFiller` writes
for the same position. `IrradianceFieldTexture.PoolIsWritten` is the storage half: set, the pool is a
storage image the dispatch owns and the mirror uploads only the index volume, because allocation and
refinement stay a CPU decision and only the probes move; `IrradianceFieldRenderer.DeviceFiller` is the
one property that switches between the two, and asking for both fillers at once is refused rather than
resolved.

What the node turned out to need, all of it found by trying:

- **It cannot be a `ComputeRenderer`.** That node binds resources by graph name, and the fill writes
  four storage images the graph does not own. `HiZPyramid` is the shape — a hand-rolled node writing
  its own descriptors and its own barriers.
- **Its binding indices come from the compiled effect, not from the generated constants.** Reflection
  describes *one* variant — the checked-in one was produced with `GlobalDistanceField` filling the slot
  — and a different implementation behind `distanceField` may declare resources that renumber
  everything after them. The names are safe; the numbers are the compilation's. Set 2 is filled by
  hand from `Effect.BindingOf` rather than through `EffectSetWriter`, for one reason: the job buffer is
  a ring, and what has to be bound is *this frame's region of it* — a name-driven write has nowhere to
  put an offset.
- **The composed source may declare no set 0 at all.** `NoDistanceField` does, which is why it is the
  composition the test runs under: a node that assumed a set 0 would bind a set nothing filled.
- **A struct that agrees with a comment agrees with nothing.** `IrradianceFillJob` is std430 — three
  `float3`-shaped members at 0, 16 and 32, stride 48 — and sequential layout would make it 36, reading
  every job after the first out of the middle of the one before it. `IrradianceFillJobTests` asserts
  the offsets against `IrradianceFill.reflect.json` rather than against the number written here.
- **A pool readback is what makes a device-authored field checkable at all.** With `PoolIsWritten` set,
  the CPU field beside it is no longer what the shader reads, so every closed form L2 was built against
  has nothing to test. `RecordReadback`/`TryRead` are the way back, and they are also what separates
  "the dispatch wrote nothing" from "the dispatch wrote elsewhere" — two failures that draw the same
  unlit frame. The earlier attempt that produced garbage was diagnosable only by guessing.

The reference is the CPU filler rather than the closed form, and that is the stronger check: a uniform
sky constrains only the constant coefficient, so a transposition or a sign error among the three linear
ones is invisible to it. Sixty-four Fibonacci directions do not sum to exactly zero, so the reference's
linear coefficients are small nonzero numbers a wrong shader has no way of reproducing.

Adding the fill shader also found that **a slot has to be filled where it is *declared*, not where it
is used**: `IrradianceFill` declares `distanceField`, so every material in every project needed a
filler for a slot no material can reach. `MaterialCompiler.PassSlots` and `PassComposition` are the
pass-side counterpart of `OptionalSlots`, because a full-screen pass composing one typed slot cannot
compile beside a shader declaring another unless it names both.

**The dilation and the border sync are dispatches too, and the fill drives them.** `IrradianceRepair`
is one shader in three permutations — gather, promote, borders — and `IrradianceFieldFill` owns an
`IrradianceFieldRepair` and runs it after every fill, in the order the CPU insists on. One call rather
than two, because a fill without its repair leaves a rind of unlit probes and a seam at every brick
boundary, and both read as lighting bugs rather than as a missing line.

Three things that only showed up in the writing:

- **A device has nowhere to put a deferred write list.** `Dilate` applies its repairs after the whole
  sweep so a repair cannot feed the probe beside it in the same pass; on a GPU that ordering is the
  scheduler's. The answer is a **sign**: a repair is written with its validity negated, which is a value
  no filler produces and which every reader already rejects — `validity <= 0` was already the test for
  "do not borrow from this". A four-instruction promote pass flips it back. No scratch memory, no
  ping-pong pool.
- **`MathF.Round` breaks ties to even and GLSL's `round` does not**, and on this data ties are
  structural rather than rare: a fine brick's probes sit exactly halfway between a coarse neighbour's,
  so *every* lookup across a refinement boundary lands on one. Both sides now spell the tie-break out as
  `floor(x + ½)`, which is the fix that removes an unspecified dependency rather than teaching one side
  to imitate the other's quirk. Found by the refined case of the comparison test and by nothing else.
- **The pool cannot be sampled while it is being written.** An image is in one layout at a time, so
  every read in the repair is an explicit `Load` and the trilinear a fine brick needs across a size
  boundary is written out by hand — which is also what makes it match `IrradianceBrickPool.Sample`
  rather than approximately agree with it.

`IrradianceRepairDeviceTests` seeds the pool from the reference filler tracing an analytic sphere,
dispatches the repair, and compares **all one hundred and twenty-five texels** of every brick — the
sixty-four a fill writes and the sixty-one it does not. On a refined field as well as a uniform one,
because the coarsest-first ordering exists for the refined case and a uniform field never reaches it.
Seeding a pool and then dispatching into it is not a test-only shape: it is filler B handing a field to
filler A, and it is why the mirror carries both usages.

**And something decides where to refine.** `IrradianceRefinementPolicy` takes *bands* — a margin and a
brick size — and applies them coarsest first around every renderer's bounds, which is what grades a
field rather than making it uniformly fine. `IrradianceFieldRenderer.Refinement` is the half that knows
what a renderer is; the policy itself takes boxes and is checked against closed forms with no scene in
sight.

Two things it is deliberately not: it reads **every** object rather than the visible ones, because
indirect light comes from geometry the camera cannot see and that is the whole reason a field exists
rather than a screen-space gather; and it only ever **adds** detail, so a scene that streams geometry
through a region ratchets that region toward its finest and never gives the slots back. Coarsening
needs the pool to take slots back and a policy for when, and neither exists.

Owed: filler B at all; the view bias; and `Deferred`, which has the same ambient term and has not been
given the slot. Plus one optimisation that is now visible — the repair runs over every
brick every frame, because a brick the budget did not refill still has neighbours that were, and
narrowing it to the dirty bricks and their neighbours is real work nobody has done.

Composing the slot into the forward pass also turned up a defect that has nothing to do with the field.
**The non-clustered forward variant cannot bind set 3**: `ForwardLightingRenderFeature` declares the
per-draw light block as a *dynamic* uniform, deliberately and for good reasons, and the shader's
reflection describes it as a plain one — incompatible layouts, and a GPU fault at the draw. Nothing had
found it because the only device test drawing `ForwardPlus` uses the clustered variant, which never
statically uses set 3 and therefore need not bind it.

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
- **[../overview.md](../overview.md)** — the light-probe entry keeps its own status and gains a note
  that § L2 is not built on it
  rather than "owed".
