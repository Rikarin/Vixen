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

**Status: both fillers are complete, the repair is dispatched with the same deferrals the CPU makes,
and every comparison between the two sides is clean.**
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

**And that comparison's one intermittent disagreement is closed — the border phase has its deferral,
as an order rather than as memory.** The leading explanation was right and the mechanism was sharper
than "a race the scheduler sometimes wins": the same-size reads of border texels are not floating-point
luck, they are structural. At the grid's outer face a border position clamps back inside, so the lookup
lands in a *face* neighbour with a local coordinate of exactly one, and the copy reaches that
neighbour's own border plane — every sync, at every grid-face edge texel, which is exactly the
`(4,4,0)`-shaped set observed. The CPU, deferring the whole class, read those texels *pre-pass* — a
stale zero on a field synced once; the dispatch read them whenever the scheduler ran that invocation.
One side deterministic-but-stale, the other side racy, and agreement was the common case only because
an unwritten border usually still held zero on the device too.

The fix is a third ordering, next to coarsest-first: **faces, then edges, then the corner**, as a
`Rank` permutation and three barriered dispatches per size class. It is sufficient for the same kind of
reason coarsest-first is: the read target always has strictly fewer border-plane coordinates than the
reader — a neighbour's texel is at four only on an axis where the reader's is too, and a same-size
brick matching on every such axis is the reader itself, which `Beyond` already refuses. So a rank never
reads its own dispatch, and never reads an unfinished one. The dilation's negated-validity sign could
not have done this — a copy needs the value, and a sign only marks one — which is why the border phase
never got it and why its deferral had to be an order instead.

`SyncBorders` commits in the same rank order now, because the CPU is the reference and the two must
take the same branches — and that also corrected the CPU: a grid-face edge texel now holds the field's
answer at its position on the *first* sync, not the previous sync's leftovers. The test that pins it is
the deterministic one this section used to owe: every border texel poisoned with a value no correct
sync can produce, owned probes filled from a ramp, one sync — under the old whole-class deferral the
poison provably survives in those edge texels, under the rank order nothing anywhere carries it. The
device comparison now runs on a poisoned pool too, so a dispatch that reads any border texel at the
wrong moment copies something the reference cannot contain, rather than a zero that happens to match.

**And something decides where to refine.** `IrradianceRefinementPolicy` takes *bands* — a margin and a
brick size — and applies them coarsest first around every renderer's bounds, which is what grades a
field rather than making it uniformly fine. `IrradianceFieldRenderer.Refinement` is the half that knows
what a renderer is; the policy itself takes boxes and is checked against closed forms with no scene in
sight.

Two things it is deliberately not: it reads **every** object rather than the visible ones, because
indirect light comes from geometry the camera cannot see and that is the whole reason a field exists
rather than a screen-space gather; and refinement alone only ever **adds** detail — the other
direction is `CoarsenTo`, below, which is what a streamed scene sets.

**Filler B projects, and nothing renders its cubes yet.** `CapturedIrradianceFiller` takes an
`IIrradianceCaptureSource` — a cube of radiance, a validity and a sun scalar — and writes the same
bricks filler A writes, through the same cursor and the same budget. The projection is the same integral
with cube texels standing in for rays, and § L2's third exit criterion is asserted: the two fillers
agree on a directional sky within two per cent, which is filler A's sixty-four-ray budget rather than
this one's 1536 texels.

**And now it renders them.** `IrradianceCubeCapture` records six 90° passes from a probe's position and
reads the colour and the depth back; `RenderedIrradianceCaptures` submits and waits, which is what makes
`TryCapture` a function rather than a promise. The six frusta come from `ShadowProjections.Cube`, the
same matrix a point light's shadow uses — and `CubeMapping.Direction` is *derived* from it by
unprojection, so the direction a texel was rendered from and the direction the projection integrates
over are one function evaluated twice rather than two conventions kept in step by hand.

With that, § 7's promise to WebGL2 is executed rather than argued: a target with no compute fills the
same bricks to the same numbers at build time. `AFieldBakedByRenderingHoldsTheSkyItWasBakedUnder` bakes a
field of sixty-four probes by rendering and reads back the closed form.

Three things the capture has to answer that a cube alone cannot. **Radiance is `Rgba32Float`**, because a
bake is the one place clamping is fatal — a probe beside a bright window whose texels stopped at one
carries that error into every bounce after it. **Validity is the solid-angle-weighted fraction of
directions whose hit is further than `MinimumDistance`**, which is the distance test rather than DDGI's
back-face count: a raster readback has no cheap winding, and "the geometry is where I am" is the question
that actually matters. **The sun is nine taps around its direction** rather than one, because a binary
per-probe shadow interpolates into steps at the probe spacing.

⚠ And the caller must rasterise two-sided. A probe standing in a room sees the room's *inside* faces;
culled, the room vanishes and every probe reports an open sky — the brightest possible wrong answer, and
the one validity cannot catch, because a probe that sees nothing looks exactly like a probe in the open.

⚠ It stalls the GPU once per probe. That is the right shape for a build step and the wrong one for
anything else; batching a budget's probes into one submit wants a ring of targets rather than the one
this reuses, and is not done.

**And the bounce runs.** `IrradianceBounceDeviceTests` draws each cube face through `RenderSystem` —
`MeshRenderFeature`, `MaterialRenderFeature`, `ForwardLightingRenderFeature`, the three a frame uses —
so a probe sees the scene as `ForwardPlus` shades it, with the field composed into that shading. Bake,
upload, bake again: the second pass shades the scene with the first pass's answer. A sunlit floor and a
wall the sun cannot reach (its *N*·*L* is zero, so every photon it holds came off the floor) went
0.254 → 0.331 → 0.324, against a flat 0.254 with the field switched off.

Contracting rather than monotone, and that is the scheme: each pass re-gathers the whole field from a
scene shaded with the previous one, so it is a Jacobi iteration that overshoots slightly and settles. The
assertions are the shape of the series — it grows, it stays, the change shrinks — because a cube capture
of a finite room projected into four coefficients has no closed form and pretending otherwise would be a
tolerance nobody could defend.

⚠ **It found that an L1 field can return negative light, and did.** Four coefficients cannot represent a
hemisphere sharply, so a probe lit entirely from one side evaluates *below zero* for a normal facing the
other way — the linear band subtracts more than the constant band has. A sunlit floor produces exactly
that: light arriving entirely from below, evaluating to **−0.047** for the floor's own upward normal.
Fed back as ambient, the second pass came out *dimmer* than the first. Nothing had caught it because
every earlier test used a near-uniform environment, where the linear band is small and this never
happens — a bounce is the first thing that ever asks a field about a direction its own light does not
come from.

The clamp is at the field-sampling boundary on both sides — `IrradianceField.Irradiance` in C#,
`IrradianceFieldProbes.Sample` in Raven — and deliberately *not* at the probe evaluation, which stays
raw arithmetic over a basis. Clamping there would also have made `IrradianceProbe.Irradiance` a lossy
readout of what a brick holds, which is what the addressing tests use it as; three of them said so
immediately.

Two more things had to be right about the *scene* before any bounce appeared, and each read as the
feedback being broken. **A flat floor cannot light itself** — every ray leaving it goes up and never
returns — so the first fixture produced one pass and then nothing. And **a field answers nothing outside
its own box**, so the wall, standing beyond it, received no indirect light however many passes ran.
Neither is a defect; both are properties a project has to get right, and they are now written down where
somebody will hit them.

**Writing it found the one convention that was not derived.** The engine's clip space is +Y up, which the
Vulkan backend expresses as a negative-height viewport — so a framebuffer's first row is *v = +1*, while
a `CubeImage`'s first row is *v = −1*. The readback flips. Without the flip the capture is mirrored
vertically, which leaves the constant band untouched and inverts exactly one of the three linear ones: a
probe lit from above reports light from below, and every test that integrates a uniform sky still passes.
That is why the fixture lights one quad in a direction with three different nonzero components rather
than using a uniform environment — the failure it caught was a sign on Y and nothing else.

Writing it also corrected something this document implies. **For an L1 payload, cube symmetry makes
uniform texel weights exact** — they sum to 4π so the constant band is right, and Σ(d·ŷ)² over a cube is
a third of the texel count by the same symmetry, so the linear band is too. A smooth sky, a linear sky
and a face-uniform sky are all blind to whether the projection weighted by solid angle at all. Only
content varying *within* a face is not, which is why the test that can tell lights a single texel. The
weighting stays because it is right and because an L2 band would have no such luck — but the claim that
it was load-bearing here was wrong, and four tests passed without it before one did not.

Owed: no known defects — the border phase's deferral, above, was the last one. `Deferred`, which has
the same ambient term and has not been given the slot, stays blocked rather than pending, since the
pass itself is Phase 10 and unbuilt. Then two optimisations: the repair runs over every brick every
frame, because a brick the budget did not refill still has neighbours that were, and narrowing it to the
dirty bricks and their neighbours is real work nobody has done; and filler B stalls the GPU once per
probe, which wants a ring of targets rather than the one the capture reuses. Neither has a scene large
enough to measure against, which is why neither has been done.

And coarsening is in, closing the ratchet this paragraph used to record. `TryMerge` is `Split`'s
exact inverse — refusing mixed groups and merges that add no coverage, subsampling the merged
probes rather than discarding them, because a parent probe stands at exactly every other child
probe's position and a copy of something real beats a flicker to black. The policy's `CoarsenTo`
merges back whatever no band still claims, never undoing its own frame's refinement, with
`CoarsenMargin` as the hysteresis against a box teetering on a brick boundary; a scene whose
geometry leaves entirely gives every slot back, and a test walks exactly that. What building it
found: a merge that covers not one cell more is a rename that doubles a brick's nominal size
without end — stale candidates once escalated a corner octet into a brick thirty-two thousand
cells across — so both the merge and the policy's snapshot now refuse staleness explicitly.

Composing the slot into the forward pass also turned up a defect that has nothing to do with the field.
**The non-clustered forward variant cannot bind set 3**: `ForwardLightingRenderFeature` declares the
per-draw light block as a *dynamic* uniform, deliberately and for good reasons, and the shader's
reflection describes it as a plain one — incompatible layouts, and a GPU fault at the draw. Nothing had
found it because the only device test drawing `ForwardPlus` uses the clustered variant, which never
statically uses set 3 and therefore need not bind it.

**And a frame the dispatch lit.** Filler A had been checked by reading the pool back, and the shading
models had been checked against a field the CPU filled. Two halves, each verified against the other's
absence: nothing had ever run `IrradianceFieldRenderer.DeviceFiller` — not the `PassKind.Compute`
branch, not the pool created as a storage image, not the upload that carries the index volume and
nothing else. `IrradianceShadingDeviceTests` now draws the same quad under the same closed form with
each filler in turn, and halving the dispatch's sky halves the pixel, which is what says the light came
from the compute shader rather than from anything else in the frame.

It found a defect on the first run, and not in the field. **A pass composition could not compile against
the whole shader library**, which is the only configuration an application has. `RVN2073` asks the
*compilation* rather than the shader, so a compute or post-process variant sharing a source set with
`ForwardPlus` must name a filler for `surface`, `shading` and all ten of `CompositeSurface`'s links —
slots it cannot reach and has nothing to say about. `MaterialCompiler.PassComposition` named the two
typed ones alone. Every post pass in the engine was affected; none had noticed, because every test that
compiles a pass narrows its effect provider to that pass's own packages, and the narrow set is the
configuration nothing ships in.

The fix derives the pass path's defaults from the material path's own inventory rather than writing
them down twice, so the two agree by construction; what remains is a completeness check over the
library's declared slots for *each* path, where before there was one for the material path and, for the
pass path, only a cross-check of fillers it happened to name. **A list that has to agree with another
list is a list that drifts**, and an assertion that is missing rather than failing is invisible for as
long as it exists.

**The view bias, which completes § G3's four.** Validity, dilation and the normal bias were in; this is
the fourth. `IrradianceField.ViewBias` moves a shading lookup toward the camera as well as along the
normal, in the same probe spacings, and the two offsets are summed into one fetch. The justification is
not "a bit more bias": **the space between a visible surface and the eye looking at it is empty by
construction**, since something opaque in it would be what got shaded instead — so it is a direction
that is always safe to step in, which the normal cannot promise. And it covers what the normal cannot:
seen edge-on, a normal is nearly perpendicular to the view ray, so a step along it barely leaves the
surface, and that is the geometry where a floor's lookup slides under the wall beside it.

`IIrradianceSource.Sample` therefore takes a view direction, required rather than optional — a consumer
with no camera has to answer that deliberately, because a zero nobody chose is a leak mitigation
quietly doing half its job. `ForwardPlus` already had the vector. `IndirectDiffuse` derives it by
reconstructing the same pixel at the near plane, rather than binding a camera position beside the
inverse view-projection it already has: two facts about one camera are two things a caller can set half
of, and the half that goes missing does not fail.

⚠ **That derivation found a defect in two shaders, one of them outside this track.** Depth is reversed
— near is one, far is zero, a depth attachment clears to zero — and both `IndirectDiffuse` and
`DistanceFieldAo` tested for "nothing was drawn here" with `deviceDepth >= 1`. That is the near plane.
The branch fired on the surfaces closest to the camera and never on the sky, which then got a field
lookup, or a march, from a far-plane position and came back lit or shadowed. `Fog.rvn` next door has
always had it right, so the convention was never in doubt — only this reading of it. Nothing caught it
because every device test of either pass clears its stand-in depth to a half, where both spellings
behave identically; each now has a frame at zero, and each of those frames fails on the old spelling.

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

**Status: started — the geometry and arithmetic of the gather exist, device-free, and nothing else
does.** [`Vixen.Rendering.ScreenProbes`](../../Core/Vixen.Rendering.ScreenProbes/README.md) holds
the octahedral mapping (the same fold as the Raven library's G-buffer normals, pinned by hand-written
convention tests rather than roundtrips), the probe lattice and its atlas addressing, the atlas with
per-probe validity, and a reference gather in the L1/L2 mould: one deterministic ray per texel
through an `IDistanceField`, radiance from `IRadianceSource`, resolved to L1 with a validity rule
copied from the field's filler. A uniform sky comes back as itself through the whole chain — anchor
lookup, trace, map, projection, bilinear resolve — and a linear sky matches its closed form within
the stated two per cent.

Two things the first pass decided. **Texel solid angles are computed exactly** — a texel clipped
against the eight octants is a set of great-circle polygons, and their areas sum to 4π at double
precision — because the octahedral map is not equal-area and a Jacobian shortcut is a projection
that is uniformly dark by whole percents, the error that reads as a tuning problem forever. And **a
probe whose pixel shows the sky is invalid, not black**, with the bilinear weights renormalising
over what remains — the screen-space restatement of the buried-probe rule, for the same leak.

One finding, the truncation's other face: beside an occluder the away-facing answer **overshoots**
the sky, for exactly the reason the bounce found L1 answering below zero — four coefficients cannot
hold a one-sided distribution. The test asserts a bound on the overshoot instead of pretending
otherwise; the spatial filter will meet this number again.

**And the trace dispatches, agreeing with the reference to a ten-thousandth.**
`Raven/Library/ScreenProbes` is a new package: `ScreenProbeAtlas.rvn` addresses the map by folding
through `Math.DecodeOctahedral` — one fold in the library, so the G-buffer normals and the probe
maps cannot disagree about which corner is the south pole — and `ScreenProbeTrace.rvn` is the
kernel, one workgroup per probe and one invocation per texel, composing the same `distanceField`
slot `IrradianceFill` composes, sky-or-black like it, for the same § L4 reason.
`AtlasConventionTests` walks the shader's arithmetic in C# against `OctahedralMap` texel by texel
and guards the drift-prone lines by text, L2's arrangement exactly. `ScreenProbeTexture` mirrors
the atlas into one 2D texture — radiance in colour, validity in alpha, so a readback tells "nothing
gathered" from "gathered nothing but darkness" — and `ScreenProbeTraceFill` stages one job per
valid probe, with the surface bias applied at staging so the shader receives an origin rather than
a surface and a rule.

Two details the first dispatch decided. **The device comparison runs under a linear sky as well as
a uniform one**, because a uniform sky is blind to the decode — every texel is the same number
whatever direction it stands for, so a mirrored or transposed octahedral fold passes; a sky varying
with a direction's y gives every texel its own answer, and the dispatch reproduced all of them.
And **a probe with no surface is not dispatched, so on a device-owned atlas its patch is
undefined** — validity rides in the alpha of texels something wrote, and clearing or skipping the
unwritten patches belongs to the consuming pass, where it is owed.

And one finding this section already owned, replayed on schedule: **a slot is filled where it is
declared, not where it is used** — the new package's `distanceField` needed its line in
`MaterialCompiler.OptionalSlots`, and until it had one every whole-library compilation in the
golden suite refused with `RVN2073`, exactly as § L2 recorded when `IrradianceFill` was the shader
doing the declaring. The failure was loud and eight tests wide, which is what that inventory's own
comment promises.

**Long rays terminate in L2's field, on both sides.** The trace order's last stage, and the arrow
this section always drew from § L3 into § L2, executed: a ray that runs out of budget samples the
field at its end and blends toward the field's answer by probe validity — sky as the fallback, not
an addend, for the double-counting reason the forward pass recorded. The Raven `IIrradianceSource`
grew `Radiance(world, direction)` for it — the raw basis, no cosine lobe, because a termination
point is not a surface and the ray wants what the light *is* — with `SphericalHarmonicsL1.Radiance`
as the C# half, and a linear sky surviving the round trip exactly because an L1-shaped function is
what an L1 projection keeps whole. The kernel composes the same `irradiance` slot the shading
passes compose, so `NoIrradiance`'s zero coverage makes a fieldless project trace exactly the rays
it traced before; the device comparison runs the reference's termination through
`IrradianceField.TrySample` and the dispatch's through `IrradianceFieldProbes.Radiance`, inside the
field and beyond its box, and the two agree either way.

**And the resolve is a dispatch.** `ScreenProbeResolve.rvn` projects each probe's map into L1 — one
workgroup per probe, walking the map in the exact order `ScreenProbeAtlas.Resolve` walks it,
because a parallel reduction reorders a float sum and the first version is the one with nothing
between it and the reference; widening it is an owed optimisation with a baseline. Its solid angles
are uploaded from `OctahedralMap.SolidAngles` — the same exact table, not a second derivation — and
its output is four grid-sized planes in the pool's own colour-major packing, validity in the
constant plane's alpha, shaped for the upsample pass that does not exist yet. The comparison seeds
the atlas under a linear sky so the projection genuinely integrates, and holds all four
coefficients of every probe, invalid one included, to a ten-thousandth.

**The upsample exists, and a frame has not drawn it — stated in that order deliberately.**
`ScreenProbeUpsample.rvn` is `IndirectDiffuse`'s screen-space sibling: four validity-renormalised
taps of the resolved planes per pixel, the blend evaluated once because the projection is linear,
the sky rejected by the reversed-depth zero test that bit twice before. Its lattice walk is
emulated in C# and held against `ScreenProbeLayout.Bilinear` for every pixel of a clamping
viewport — the pixel-centre and lattice-origin halves are this pass's half-texel, wrong-silently on
either side alone — and a compile test pins the reflected binding names to what
`ScreenProbeTexture.Apply` writes. **And a frame has drawn it.** The image test runs the pass through a real compositor over
device-resolved planes: a uniform sky crosses the atlas, the solid-angle table, the resolve
dispatch, the four planes, the lattice walk and the graph's scheduling, and comes back as a flat
frame of itself — with the sky's own pixels dark under the reversed-depth test. The first frame
found what first frames find here, twice: a full-screen pass's textures resolve through the graph
and nothing else, so the resolved planes travel as imports already in their own state rather than
as parameter writes; and a pass that composes nothing still names every slot its source set
declares, which is the RVN2073 rule caught this time by the pass's own composition being empty.
Bilinear only until the denoiser brings the bilateral weights.

**And the gather runs as one schedule, placed from the frame's own depth.**
`ReconstructedScreenSurface` answers `IScreenSurface` from a frame's depth and normal buffers by
the shaders' own arithmetic — `Transform.UvDepthToWorld` spelled in C#, the reversed-depth zero as
the sky test — with its axes pinned by a hand-worked orthographic case, because a reconstruction
that negated y would pass every round trip through its own inverse. `ScreenProbeGatherRenderer` is
the node this section owed: it copies the depth and normals back each frame, places probes from
the copy a latency ago under the camera matrix snapshotted beside it (this frame's camera against
last frame's depth reconstructs surfaces that exist nowhere), runs trace and resolve in one
compute pass, publishes the resolved planes as graph imports and builds the upsample as a child.
Its image test asks a compositor for three frames: the first is honestly dark, because its
placement data had not come back yet, and the second is the uniform-sky flat frame with nothing
seeded by hand. Two decisions the schedule forced. **The probe lattice runs a frame behind the
camera** — placement is a readback, and the denoiser's temporal reprojection will meet that
staleness again with a name. And **an unplaced probe's patch is cleared by the trace dispatch
itself** (`ClearInvalid`, a `valid` flag in the job's padding): on an atlas the dispatch owns, a
patch nothing writes is undefined memory, and the resolve reads validity out of it.

**And the trace order opens with the screen.** `ScreenSpaceTrace` marches the frame's own depth —
geometry the distance field may not hold — before any field ray: a fixed count of equal steps, each
projected and tested behind-within-thickness, a hit giving back nothing for the § L4 reason a field
hit does, a sky texel occluding nothing, and an off-screen ray falling through to the field because
a screen miss never proves the world empty. The kernel runs the same march sample for sample, and
its device comparison is the package's sternest: a screen hit is binary, so a last-bit decode
disagreement flips a texel whole — the wall only the depth buffer can see stopped the same rays on
both sides, texel for texel, with a traceless reference proving it stopped any. Wired through the
gather node behind `ScreenTraces`, **off by default and stated why**: the probes stand where a
frame ago's placement put them and the rays march this frame's depth — identical for a still
scene, sheared by motion for a moving one, which is the denoiser's reprojection problem named
before it exists. One frame drawn with it on shows the L1 overshoot from § L3's own finding, now
end to end: probes standing on the one surface a frame drew blacken a cone *behind* themselves,
and the away-facing answer lands above the sky, not below it.

**Adaptive placement exists device-free, and is stated as half a feature on purpose.** The layout
reserves adaptive map rows below the grid — an addition to the lattice, its addressing unmoved,
the capacity a budget rather than a promise. Tile corners are the detectors, being the pixels
farthest from every anchor: a corner farther than a tolerance from the plane of every probe it
bilinearly reads stands on a surface the lattice never sampled, and gets a probe gathered by the
same trace order. The position-aware sampling overload drops plane-mismatched taps exactly as it
drops invalid ones — one tolerance shared by detection and sampling, because disagreeing
definitions of "a different surface" place probes nothing reads — and falls back to the nearest
accepting adaptive probe, then the unfiltered lattice, then zero. The ledge fixture produces
exactly the eight corner probes its geometry predicts. What is deliberately absent: the device
half changes no picture until the upsample reads position, and that is the bilateral upsample,
which is the denoiser's opening move.

**And the denoiser has its opening move.** `ScreenProbeHistory` accumulates resolved probes
across frames as a capped running mean — the cheapest variance reduction being the frames already
paid for — with history following the <i>surface</i> rather than the tile: this frame's surface
reprojects through last frame's camera to the probe that stood on it then, which is where the
gather node's one-frame-stale lattice gets its answer. Disocclusion rejects by the plane test
placement and adaptive sampling already use, and a rejection starts over at weight one — noisy
and honest until the spatial filter exists to hide the restart. The tests hold the recurrences to
the digit: a constant converges to itself exactly, a flip follows the mean's own sequence, the cap
converges at the cap's rate, and a camera panned one tile blends each probe with its neighbour's
history while the newly revealed column starts from nothing.

**And the accumulation dispatches, agreeing with the CPU frame by frame.**
`ScreenProbeAccumulate.rvn` runs the same capped running mean per probe over two ping-ponged sets
of six planes — four in the resolve's own packing, so the upsample reads accumulated probes
without knowing the difference, plus surface-and-weight and normal planes for the reprojection
and its plane test. The device comparison replays the CPU recurrences' scenarios — constant
convergence, the flip, the pan that borrows the neighbour's history — through the dispatch,
coefficients and weights compared after every frame so a drift is caught where it starts. The
gather node routes the upsample through the history when an `Accumulator` is set, publishing the
back set the swap will front, and feeds the driver the camera its surfaces were placed under — a
frame older than its own, matching the surfaces exactly. A constant scene through the whole
accumulated chain draws the same flat frame the raw resolve draws, which is the closed form's way
of saying the plumbing added nothing and lost nothing.

**The spatial filter runs on both sides**: a cross of lattice neighbours blended at a stated
strength, gated by history and by the shared plane test, renormalising over what survives —
writing into separate filtered planes rather than into the history, because filtering what the
next frame blends against is a recursive blur whose width nothing set. A uniform field is
unchanged exactly, a lone spike spreads by precisely its kernel share, and a depth edge stops the
bleed to the bit; the dispatch is compared against the CPU probe for probe over a scene holding
both closed forms at once, and the gather node runs it after the accumulation with the upsample
reading the filtered planes — still without knowing what kind of planes they are.

**And the upsample is bilateral, behind a tolerance.** Above zero, each tap tests the pixel's
reconstructed world position against its probe's stored plane — the history's surface and normal
planes — dropping mismatches exactly as invalid probes drop, with the plain blend as the
everyone-rejected fallback: the CPU overload's own order, minus adaptive probes, so a lattice
without them behaves identically on both sides. Zero is bit-for-bit the bilinear pass. A flat
frame proves what a flat frame can — planes bound and read without changing the closed form, the
fallback engaged rather than black — and the discriminating depth-edge picture is owed with the
first fixture that draws real geometry.

With that, the denoiser's chain runs end to end on the device — accumulate, filter, bilateral
upsample — every stage held to a CPU reference.

**And the exit criteria are asserted rather than aspired to.** `ExitCriteriaTests` is where this
section's exit sentence becomes numbers. The reference is a 4096-sample cosine-weighted Monte
Carlo estimate, deterministic by Hammersley because a reference that changes between runs referees
nothing; the chain at its own sixty-four rays per probe matches it within a stated budget with
named parts — under an unshadowed linear sky, where quadrature and truncation are exact, within
one per cent; under a ball's occlusion cone, which no L1 projection holds, within five per cent
RMS and ten at the worst pixel, both measured before they were stated. The camera-cut test
accumulates five frames and cuts to a different camera on a different plane: every probe rejects
its past outright and answers the new frame alone at weight one, no fraction of the old light
blended in. The fast-pan test pans a tile per frame over per-column constants: every probe answers
exactly its own world column's number — a smear would be a blend of two columns, and there is no
tolerance wide enough to call that no ghosting — with reprojection proven to have actually run.
The bilateral edge has its picture too: a synthetic two-plane G-buffer whose step the bilinear
taps bleed across by exactly the lattice weight (pinned first, because a discriminator that cannot
show the failure proves nothing), and whose sides come back pure under the plane test. And the
gather node's resize is a deliberate step: a changed frame is refused loudly until the host idles
and calls `Reset`, which starts the temporal chain over, because a resize is a camera cut.

Remaining, all of it quality and performance rather than exit criteria: the HZB traversal (the
naive march is the baseline, and the pyramid wants its nearest-texel reduction beside culling's
farthest), the adaptive probes' device half (the bilateral pass that would read them now exists),
importance sampling, bilinear history taps, screen-trace radiance once § L4's surface cache gives
hits something to return, and composing the output into a shipped preset — a project decision,
the same slot `IndirectDiffuse` fills today.

### L4 — Surface cache and radiosity *(3.5 EM)*

Multi-bounce for geometry that is off-screen. The most Epic-specific chunk, and the most deferrable.

- Card generation offline (up to 6 orthographic captures per mesh, clustered from the geometry).
- Runtime capture into albedo/normal/depth/emissive atlases, budget-limited per frame.
- Direct lighting evaluated **on the cache**, then a radiosity pass over the cards themselves —
  which is where the infinite-bounce look comes from, cheaply, because it is a low-resolution 2D
  problem.

**Exit:** a Cornell-box fixture converges to a reference within a stated error; the second bounce is
visible and measurable rather than asserted.

**Status: the arithmetic is whole, the exit criterion is asserted, and the device halves have
landed.** [`Vixen.Rendering.SurfaceCache`](../../Core/Vixen.Rendering.SurfaceCache/README.md)
holds the cards (a box and an axis, the in-plane frame cyclic whatever the sign), the generator
(triangles vote by dominant normal axis, ties to the smaller index because float noise must not
pick a card's shape), the shelf atlas with exact-size reuse (§ 6's "atlas allocation and
residency", CPU half — running out is a quality reduction, not an error), the traced capture the
rasterising runtime capture will be compared against, direct sun on the cache behind a shadow ray,
and the radiosity gather that reads last pass's answer through a double buffer — skylight reaches
cards through the gather's misses, with no ambient term to double-count.

The Cornell box runs as written: five walls and an emissive panel as cards, converged where the
largest change drops under a thousandth, held against a five-bounce path tracer **within a stated
five per cent** (measured at 1.2 before being stated). The second bounce is measured, not
asserted: emissive-only, the floor's red-to-green ratio is the white panel's — one; converged, it
rises past 1.1, colour that took two bounces to arrive. Two convergences agree to the bit, the
property every dispatch comparison leans on. What building the reference found: a path tracer
whose per-bounce directions are one shifted sequence is a one-dimensional lattice threading a
four-dimensional domain, and it biased the two-bounce estimate by a fifth — the reference now
takes two fresh Halton primes per bounce.

**And the seam every tracer left open is closed.** `SurfaceCacheRadiance` wraps any
`IRadianceSource`: a hit inside a resident card answers with the card's outgoing radiance —
direct, emissive, every bounce — and everything else falls through, so the L2 fillers and the L3
gather inherit multi-bounce light without changing a line. A screen probe on the Cornell floor
holds the panel in its up-texel and red toward the red wall; over the black world it stays dark.

**And the device halves are no longer owed — each one landed against the reference that was built
first.** Sampling narrows through `SurfaceCardIndex` — a uniform grid where a query is one cell and
a test holds the indexed answer against the linear scan rewritten in full. The Raven `SurfaceCache`
package carries the card arithmetic once for every kernel, and `ISurfaceCacheSource` turns "what a
ray that hits a surface sees" into a compose slot: `NoSurfaceCache` answers the black every tracer
always answered, `SurfaceCacheSource` answers with the card atlas — `TryRadiance`'s walk, test for
test — and `ScreenProbeTrace` composes it at its hit branch, so the probes inherit multi-bounce
light on the device exactly as the CPU gather already did. `SurfaceCacheLight` and
`SurfaceCacheGather` dispatch the two radiosity passes and are compared texel by texel against
`CardRadiosity`: the open-sky facts under `NoDistanceField` (pure arithmetic), and the seam fact
handing **one** `GlobalDistanceField` object to both sides — the CPU reference marches the very
grids the clipmap uploads — with the drift measured at exactly zero on the machine that measured
it, stated at 1e-4 for devices whose filters carry fewer weight bits. The runtime capture,
`SurfaceCardCapture`, rasterises a card in `IrradianceCubeCapture`'s mould — the projection derived
from the card so framebuffer texel (x, y) *is* card texel (x, y), three passes over the one
attachment every scene pipeline already targets — and is compared against the traced reference on
one scene captured both ways: validity texel for texel, materials to float precision, depth to the
march's own arrival hair.

Still owed, named so the absence is a decision: the one-pass MRT capture (the three-pass form is
its baseline and referee), a compositor node scheduling capture → light → gather in a live frame
the way the irradiance-field renderer schedules its fills, and a device-side spatial index — the
kernels' sampler still scans the card buffer linearly, which is honest at fixture scale and slow at
scene scale.

### L5 — Reflections through the same tracer *(1.5 EM)*

Traced reflections reusing L1/L4, with rough reflections reading L2 and L4 instead of tracing, and
the existing reflection probes as the miss fallback. Retires doc 06's "⚠ blended against the sky"
caveat as a side effect.

**Status: the reference half is built and its closed forms hold; the device half is the owed
remainder.** [`Vixen.Rendering.Reflections`](../../Core/Vixen.Rendering.Reflections/README.md) is
the layer that reuses everything below it, which is this section's whole argument: a mirror ray
marches L1's tracer, a hit answers through `IRadianceSource` — hand it L4's
`SurfaceCacheRadiance` and a reflection carries emissive, direct light and every bounce, asserted
by a test that reads the store's own outgoing convention back off a cached wall — and a miss asks
`IReflectionFallback`, the seam doc 06's probes plug into as the far field they are actually good
at. Rough surfaces, at and above a stated threshold, read L2's field about the mirror direction
instead of tracing — the discriminating test puts a wall where the mirror ray would hit and shows
it appearing below the threshold and *not* appearing at it, because rough is a different read, not
a darker mirror. The view convention is incident and pinned (the other convention reflects the
camera and looks plausible everywhere), and the mirror bias carries the test that shows what
removing it does: every reflection becomes the reflector's own colour.

Owed: the device half — a per-pixel Raven kernel through the same composed slots the kernels
already share (`IDistanceFieldSource`, `ISurfaceCacheSource`), with this reference as its
texel-by-texel referee and the probe fallback handed through the interface's device analogue,
which is the piece that actually retires doc 06's caveat in a frame — plus the blend band around
the hard threshold, and screen traces first in the trace order, for the screen probes' reason.

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
