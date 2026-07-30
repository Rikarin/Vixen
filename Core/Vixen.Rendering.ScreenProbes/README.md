# Vixen.Rendering.ScreenProbes

The screen probe gather of [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md)
§ L3 — its geometry and arithmetic, written first and device-free. One probe per tile of screen, an
octahedral radiance map per probe, an atlas that holds them, and the resolve back to per-pixel
irradiance. The shipping gather is a set of compute passes; this is what those passes will be
compared against, texel by texel, the same arrangement `TracedIrradianceFiller` has with
`IrradianceFill` one subsystem over.

## The octahedral map is a contract, not a convenience

`OctahedralMap` folds the sphere into a square exactly the way the Raven library's
`Math.EncodeOctahedral` does — the tie at zero picks the positive hemisphere, the corners are the
south pole. A probe's map is written by one side and read by the other, so the direction a texel
stands for has to be one function evaluated twice; the convention tests pin it with hand-written
values rather than roundtrips, because a roundtrip passes with the axes swapped.

## Texel solid angles are exact, and that is the load-bearing part

A projection into spherical harmonics is an integral, so each texel carries the area of sphere it
stands for. The octahedral map is not equal-area, and a Jacobian-at-the-centre approximation is
wrong by whole percents at 8×8 — every one of them a probe that resolves that much dark, uniformly,
which is the kind of error that reads as a tuning problem forever.

Within one octant the decode is affine in the plane, so a straight texel edge maps to a great-circle
arc. The exact area is therefore: clip the texel against the eight octants, map each piece's
corners, sum the spherical triangle fans. The test that keeps it honest asserts the texels of every
resolution sum to 4π — at double precision, not within a shrug.

## Probes stand on pixels, and a probe with no surface says so

`ScreenProbeLayout` puts one probe per 16-pixel tile (a parameter, not a law), anchored at the
tile's centre and clamped into the viewport at the partial tiles. Every pixel reads the four probes
around it bilinearly; the lattice clamps at the border rather than extrapolating, and the weights
always sum to one.

A probe whose anchor shows the sky is **invalid, not black** — black would pull every neighbouring
pixel toward darkness through the filter, which is the screen-space version of the buried-probe leak
the irradiance field's validity exists for. Invalid probes drop out of the resolve and the weights
renormalise over what is left. A probe standing inside geometry is invalid by the field's sign,
before any ray is cast — the same rule as the field filler's.

The resolved probe stores what `SphericalHarmonicsL1.Irradiance` evaluates: irradiance over π, the
number a shading pass multiplies by albedo, clamped at zero on the way out — both conventions shared
with the irradiance field, and for the field's reasons.

## The reference gather is deliberately the naive one

`TracedScreenProbeGather` casts one deterministic ray per octahedral texel, at the texel's centre,
no jitter — so two gathers of one scene agree to the bit and a test asserts numbers rather than
tolerances around noise. Its closed forms are the family every layer of § L2 was held against: a
uniform sky comes back as itself through the whole chain, a linear sky resolves to
`a + ⅔·b·(n·ŷ)` within the quadrature's stated two per cent, and a probe on a lit floor still
answers the whole sky for the upward normal, because the L1 truncation of a hemisphere is exact at
the pole.

One of its tests documents the truncation's other face: beside an occluder, the away-facing answer
overshoots the sky. That is the positive mirror of the "an L1 field can return negative light"
finding in doc 19 § L2, and the test asserts a bound on it rather than pretending L1 is something
it is not.

## The device half, and where it lives

The shader side is `Raven/Library/ScreenProbes`: `ScreenProbeAtlas.rvn` for the addressing —
folding through `Math.DecodeOctahedral`, because one fold in the library means the G-buffer normals
and the probe maps cannot disagree about which corner is the south pole — and `ScreenProbeTrace.rvn`
for the kernel, one workgroup per probe and one invocation per texel, composing the same
`distanceField` slot `IrradianceFill` does. `AtlasConventionTests` here walks the shader's
arithmetic in C# and holds it against `OctahedralMap`, texel by texel, with text guards on the lines
that would drift silently.

The host side is `Vixen.Rendering`: `ScreenProbeTexture` mirrors the atlas into one 2D texture —
radiance in the colour, validity in the alpha, so a readback can tell "nothing gathered" from
"gathered nothing but darkness" — and `ScreenProbeTraceFill` stages one job per valid probe and
dispatches. `ScreenProbeTraceDeviceTests` compares the dispatch against `TracedScreenProbeGather`
texel by texel, under a *linear* sky as well as a uniform one, because under a uniform sky every
texel is the same number and a mirrored decode is invisible.

The surface bias is applied while staging, so the shader receives an origin rather than a surface
and a rule — the same single place the reference applies it, which is what keeps the two comparable.
⚠ By default a probe with no surface is not dispatched, and on a device-owned atlas its patch is
therefore *undefined* — the composition the device comparisons run under. A frame sets
`ClearInvalid`, and the dispatch writes the invalid mark across such patches instead; see below.

## Long rays terminate in the irradiance field

Doc 19 § L3's trace order ends by amortising distant lighting in § L2's field, and both tracers do
it: a ray that runs out of budget samples the field at its end point and blends toward the field's
answer by the probe's validity — the sky is the fallback, not an addend, for the double-counting
reason `ForwardPlus` records. The Raven `IIrradianceSource` protocol grew a second member for it,
`Radiance(world, direction)`: the raw basis with no cosine lobe, because a termination point is not
a surface — nothing stands there to bias away from, and the ray wants what the light *is*, not what
a wall would receive from it. `SphericalHarmonicsL1.Radiance` is the C# half, and a linear sky
survives the round trip exactly, because a function of the first band's shape is what an L1
projection keeps whole. The kernel composes the same `irradiance` slot the shading passes compose —
`NoIrradiance`'s zero coverage blends every termination back to the sky, so a project without a
field traces exactly the rays it traced before.

## Adaptive probes stand where the lattice never sampled

Doc 19 § L3's "adaptive placement at disocclusions", device-free half. The layout reserves rows of
map slots *below* the grid's — an addition to the lattice, its addressing unmoved — and the atlas
places into them up to a capacity that is a budget, not a promise: when it runs out, the rest of
the screen keeps the lattice it had.

A tile's corner pixels are the detectors, being the points farthest from every anchor: a corner
whose surface stands farther than a tolerance from the plane of every valid probe it bilinearly
reads is on a surface the lattice never sampled, and gets a probe of its own, gathered by the same
trace order as everything else. Sampling asks the same question with the same number: the
position-aware `Irradiance` overload drops a tap whose *plane* rejects the pixel's surface exactly
as it drops an invalid one — blending it in is how light bleeds across a depth edge — and falls
back, in order, to the nearest adaptive probe whose plane accepts, then to the unfiltered lattice
(the bleed, chosen over a black hole, the dilation rule's own call), then to zero. Detection and
sampling sharing one tolerance is deliberate: disagreeing definitions of "a different surface"
place probes nothing reads.

The fallback's tests seed the maps by hand — grid probes holding one constant, the ledge probe
another — because under any gathered fixture of an empty world every probe sees the same sky and
the fallback is indistinguishable from the blend it replaced. The detection test is the gathered
one: a ledge straddled by two tiles produces exactly the eight corner probes the geometry predicts,
each resolving the uniform-sky closed form where it stands. **The device half is owed with the
denoiser's bilateral upsample** — the shipped upsample pass still reads the grid planes alone, so
adaptive probes change no picture until the pass that reads position arrives, and they were built
first because the lattice semantics had to exist to be read.

## The screen is asked first, and a screen hit is an occlusion

Doc 19 § L3's trace order opens with rays against the frame's own depth — geometry the distance
field may not hold: skinned meshes, foliage, anything too small or mobile for a signed distance
representation. `ScreenSpaceTrace` is the CPU half: a fixed count of equal steps along the ray, each
projected through the camera and compared against the depth buffer — behind a surface, within its
`Thickness`, is a hit, and a hit gives back **nothing**, exactly as a field hit does, because a
surface's own radiance is the § L4 surface cache. A sky texel occludes nothing; a ray that leaves
the viewport stops being the screen's to answer; and the field march runs over the whole ray
regardless, because a screen miss never proves the world empty.

The kernel runs the same march sample for sample, and the device comparison is sterner here than
anywhere else in the package: a screen hit is *binary*, so a last-bit disagreement in the decode
would flip a texel whole rather than nudge it — the comparison runs under an orthographic camera to
keep the projection affine, over a wall only the depth buffer can see, with a traceless reference
proving the wall stopped something. **The naive march is the point**: the HZB traversal that skips
empty space through the depth pyramid changes how fast the answer is found, not what it is, and it
lands against this baseline — wanting the pyramid's *other* reduction, since `HiZReduce` keeps the
farthest texel for occlusion culling and empty-space skipping wants the nearest. The step-versus-
thickness trade is real in the meantime: a fixed-step ray samples its budget divided by its step
count, and a wall thinner than a step slips between samples.

## The resolve is a dispatch, and its weights are the same table

`ScreenProbeResolve.rvn` projects each probe's map into L1 — one workgroup per probe, walking the
map in the exact order `ScreenProbeAtlas.Resolve` walks it, because a parallel reduction reorders a
float sum and the first version of anything here is the one with nothing between it and the
reference (making it wide is owed, with a baseline to hold it to). The solid angles arrive in a
buffer filled from `OctahedralMap.SolidAngles` — the same exact table, not a second derivation. The
output is four grid-sized planes in the irradiance pool's own colour-major packing, validity in the
constant plane's alpha, so whatever upsamples these probes interpolates coefficients exactly as the
field's sampler does.

## Placement reads the frame's own buffers, and its axes are pinned by hand

`ReconstructedScreenSurface` is what "probe placement from the real depth buffer" is: one frame's
depth and encoded normals on the host, answering `IScreenSurface` by exactly the arithmetic every
screen-space shader here uses — `Transform.UvDepthToWorld`, the UV-to-NDC map with no y negation,
the clip divide behind the same epsilon, the normal decode `SafeNormalize(xyz · 2 − 1)`. One
function evaluated twice, because a probe placed by this arithmetic is upsampled by that one. Zero
depth is the sky, because depth is reversed.

Its tests work the orthographic case by hand rather than round-tripping, for the octahedral map's
own reason: a round trip through a matrix and its inverse passes with the axes swapped, and the
top-left pixel landing at *negative* y is precisely the fact a swap would erase. The perspective
camera is then checked as an inversion — the reconstructed point projects back onto its own pixel
centre at its own depth, exactly.

## The frame draws it, and one node is the schedule

The upsample pass — `ScreenProbeUpsample.rvn` in the PostFx package, four validity-renormalised
taps with the lattice walk pinned against `ScreenProbeLayout.Bilinear` pixel by pixel — is drawn by
a real compositor, over device-resolved planes that travel as graph imports (a full-screen pass's
textures resolve through the graph and nothing else, the first drawn frame found).

`ScreenProbeGatherRenderer` in `Vixen.Rendering.PostFx` is doc 19's "node that schedules trace,
resolve and upsample as one graph". It owns none of the arithmetic — the tracer and resolver are
the host's, with their composed sources — and all of the ordering: it copies the depth and normal
targets back every frame, places probes from the copy a latency ago under **the matrix snapshotted
with that copy** (this frame's camera against last frame's depth reconstructs surfaces that exist
nowhere), runs trace and resolve in one compute pass, publishes the planes as imports, and builds
the upsample as a child. Its image test asks for three frames: the first is honestly dark — its
placement data had not come back yet — and the second is the uniform-sky closed form with nothing
done by hand. The probe lattice runs a frame behind the camera; the denoiser's reprojection will
meet that fact again.

One rule the node imposes rather than configures: `ScreenProbeTraceFill.ClearInvalid`. On an atlas
the dispatch owns, the patch of a probe nothing placed is undefined memory, and the resolve reads
validity out of it — so a probe with no surface still gets a job, and its sixty-four stores write
the invalid mark instead of tracing. The job struct grew a `valid` flag in what was padding; the
stride did not move.

## The denoiser opens with the frames already paid for

`ScreenProbeHistory` is temporal accumulation at probe level — the denoiser's first move, because
sixty-four rays per probe is a noisy estimate and the cheapest variance reduction is last frame's
answer. Each resolved probe blends with its own history as a running mean, `(h·w + c)/(w+1)`, with
a weight cap that ages the oldest frames out — the lag-versus-noise dial, and its tests hold the
recurrence to the digit rather than to a mood: a constant scene converges to itself exactly, a
flipped light follows `3/4, 3/5·…` precisely, a capped weight converges at the cap's rate.

History follows the *surface*, not the tile: this frame's surface is projected through last
frame's camera to find the probe that stood on it then — which is where the gather node's
one-frame-stale lattice finally gets its answer. Disocclusion is rejected by the plane test
placement and adaptive sampling already trust, and a rejection starts over at weight one — noisy
and honest; the spatial filter that will hide the restart is the denoiser's next move. The pan
test is the discriminator: a camera panned one tile blends each probe with its *neighbour's*
history, and the probe whose surface was off screen last frame starts from nothing.

**And the accumulation dispatches.** `ScreenProbeAccumulate.rvn` is the same arithmetic, one
invocation per probe, over two ping-ponged sets of six planes (`ScreenProbeHistoryTexture` — four
in the resolve's own packing so the upsample cannot tell accumulated planes from resolved ones,
plus the surface-and-weight and normal planes reprojection tests against). The device comparison
runs the CPU tests' scenarios — constant convergence, the flip, the pan that borrows the
neighbour's history — through the dispatch frame by frame, coefficients and weights alike, so a
drift is caught at the frame it starts. The gather node routes the upsample through the history's
back set when an `Accumulator` is present — the set the swap makes front by the time the pass
draws — and hands the driver the camera the surfaces were *placed* under, a frame older than the
node's own, because pairing this frame's camera with last frame's surfaces reconstructs history
that exists nowhere.

Owed from here, in the denoiser's own order: bilinear history taps (point reprojection is the
baseline), the spatial filter over probes, and the bilateral upsample that finally reads depth
and normal edges — which is also what turns the adaptive probes on.

## Not yet, and named so the absence is a decision

- **Adaptive probes on the device.** The CPU half above is whole; the atlas mirror does not yet
  carry the adaptive rows up, the trace dispatch does not fill them, and the upsample pass cannot
  read them until it reads position — which is the bilateral upsample, which is the denoiser's.
- **Importance sampling.** The shipping gather aims rays where the BRDF and last frame's lighting
  say they matter. It changes which texel a ray serves, not what a texel means, so it belongs to the
  version that has a BRDF to sample against.
- **The HZB traversal.** The screen trace exists and is the naive march; the hierarchical one that
  skips empty space through the depth pyramid is owed against it, together with the pyramid's
  nearest-texel reduction and a linear-depth thickness for perspective cameras.
- **The denoiser past its opening.** Temporal accumulation exists above, on the CPU; the device
  half, the spatial filter over probes, and the bilateral upsample are the project doc 19 § L3
  warns about, in that order.
- **Resizing.** The gather node sizes its lattice on the first build and refuses a resized frame —
  rebuilding textures frames still reference is a use-after-free with latency, so until resizing is
  a deliberate step, a host that resizes recreates the node.

**Nothing here creates or calls a graphics device.** The assembly references
`Vixen.Rendering.DistanceFields` for the reference's marching and `Vixen.Rendering.IrradianceFields`
for `IRadianceSource` — the same question a field probe asks, asked by a screen probe — so the same
qualified line the irradiance field's README draws applies here too.
