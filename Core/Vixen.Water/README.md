# Vixen.Water

One definition of where the surface is.

A body is a spline and a profile; a sea state is a spectrum summed as Gerstner waves; a zone
rasterises both into a field of surface height, flow and the ground beneath. The evaluator over that
field is what the vertex stage, the buoyancy solver and a gameplay query all call — so a boat floats
at the height the water is drawn at.

Specified in [`docs/plan/35-water.md`](../../docs/plan/35-water.md).

```csharp
var river = new WaterBody(WaterBodyKind.River, spline, profilePerControlPoint) {
    ShoreFalloff = 1f,          // how far past the channel the surface fades out
    BedRamp = 2f                // how far in the bed reaches its full depth
};

var field = new WaterField(new() { Origin = origin, Extent = 256f, Resolution = 256 });
field.Rasterize([river, lake], terrain);

var query = new WaterQuery(field, WaterWaveSpectrum.Default);
var surface = query.Sample(new(x, z), waterTime);   // height, normal, flow, depth, coverage
```

## No device, because this is what a dedicated server runs

[§ D1]. A headless build has no device and still has to answer *how deep is the water here* for every
swimming character and every boat it simulates. So the whole assembly is arithmetic over arrays and
structs, references `Vixen.Core.Mathematics` and nothing that opens a device, and the physics join is
a separate assembly rather than a reference from here to Jolt.

## One evaluator, two hosts, and the seam is a test

[§ D2], which is the decision the rest of the document hangs off. The surface height at a position is
defined **once**, as arithmetic over the field's surface channel, the Gerstner sum at that position
and time attenuated by the local depth, and a ripple simulation's displacement where one covers it.
That arithmetic exists in exactly two places — `WaterEvaluator` and `Raven/Library/Water/Surface.rvn`
— and the two are held together by `WaterSurfaceSeamDeviceTests`, which dispatches the shader on a
real device and compares it against this, per component, over four thousand positions and times.

⚠ **The measured tolerance is 2 × 10⁻⁴ m, and doc 35 asked for exact.** That is § Risks' own stated
fallback, taken, with the reading written down rather than rounded off. The residue is a device's
freedom to contract a multiply and an add into one FMA, which shows up in the *phase* — the one
quantity here that grows without bound — so the drift is linear in it: a millionth of a metre at a
hundred radians and a twentieth of a millimetre at five thousand. What the structural half bought is
still most of the distance: `OpSin` is licensed 8192 ULP, which at those phases is not a rounding
difference but a different wave.

⚠ **Why that is worth a test rather than a convention.** Both references evaluate the surface twice
and neither pins them together, and the symptoms are the reason people believe water is hard: a boat
that hovers a hand's width above the crests in a swell, a character whose swimming state flickers at
the shoreline, a buoy that sinks when the frame rate drops. Unreal ships a per-body
`Max Wave Height Offset` to correct exactly this drift — a knob whose existence is a bug report.

Three consequences, each load-bearing:

- **Time is a water time.** Every entry point takes an explicit `waterTime` and there is no clock in
  here. A buoyancy solver reading the frame's total time and a shader reading a smoothed one *is* the
  drift, and it is invisible until the frame rate changes.
- **The evaluator is allocation-free**, because a hundred pontoons and forty swimming characters
  query it per fixed step. The tests assert ten thousand queries allocate zero bytes.
- **The wave count is quantised** to 8 / 16 / 32, so the loop is the same shape on both sides. A
  dynamic loop on the CPU and an unrolled one on the GPU is how two implementations of one function
  start to differ in the last bits.

## The trigonometry is a polynomial, deliberately

`WaterMath.SinCos`. Exact float agreement between a C# evaluator and a SPIR-V one is a real claim and
`sin`/`cos` do not support it — Vulkan allows `OpSin` 8192 ULP over the useful range, and a driver may
use whatever its special-function unit implements. So the wave sum calls a **stated polynomial in
stated operations**, and the Raven module spells out the same one. Two implementations of the same
finite sequence of multiplies and adds agree bit-for-bit on any IEEE-754 machine.

⚠ **Nothing in that file may be contracted into a fused multiply-add** — an FMA computes one rounding
where the written expression computes two. Vixen's own emitters do not contract and the
parenthesisation is load-bearing rather than style; what neither can reach is a translation layer
below the driver, which is where the measured residue above comes from.

⚠ **The range reduction is the part that has to be accurate.** π is split into four pieces, the first
three of which carry few enough significant bits that multiplying them by the half-cycle count is
*exact*. Two-part reduction degrades from a few hundred radians; four is good to the hundred thousand
a long session reaches, which is the range the tests state.

## Waves are a spectrum, and the FFT is deferred with arithmetic

[§ D7]. `WaterWaveSpectrum` holds wind, a directional spread, a wavelength range with a falloff, an
amplitude scale, a steepness and a seed, and *generates* the Gerstner list the evaluator reads. The
list is the runtime form; the spectrum is what an author edits.

⚠ **Deterministic from the seed on every host**, which is a stated exit criterion rather than a
nicety: a client and a server that disagree about the sea disagree about where a boat is. Every draw
goes through an integer hash and every trigonometric call through the polynomial above; the tests
assert the exact bits so the other CI legs can compare.

⚠ **The seed and the wave index are combined by multiply-and-add, not by XOR.** The mixer has a fixed
point at zero, so XOR makes every spectrum share one wave with every other along the seed-equals-index
diagonal. That is harmless in a scatter and is not harmless here: two seas that should differ agree
about their longest swell.

**Why Gerstner first.** A sum of sixteen waves is a *closed-form function of position and time*, so a
server can answer "where was the surface six ticks ago" without having simulated the intervening
frames. An FFT needs a per-frame dispatch chain and a CPU path that either reads back a texture or
runs a second transform, and neither can answer that question. When the FFT lands it is a second
model behind the same evaluator, and the seam test is what makes adding it safe.

**And it is the one asset kind water adds.** `WaterWavesAsset` is a `.vxwaves` — a name and a
spectrum — because a sea state is shared between every body in a region *and between levels*, which
is the only water thing that is. Everything else lives in the scene, where its merge is. The importer
is `Vixen.Editor.Assets`', the seam is `IWaterWaveSource`, and `AssetWaterSource` is the game's
answer; a zone that names one and does not get it keeps its inline spectrum and counts, because a
zone rendering a dead-flat sea reads as the renderer being broken rather than as an asset still
streaming.

## Depth is computed, never stored

[§ D3]. `WaterField` carries surface height, flow in two channels, the ground beneath, and coverage.
How deep the water is at a place is the first minus the third, worked out where it is used — storing
it would be a third number that can disagree with the two it came from, and the frame it disagrees on
is the one where the shoreline is in a different place for the material than for the wave attenuation.

⚠ **Bodies rasterise to the same field in either order**, and that is a stated property rather than a
happy accident. Priority decides which body is on top; bodies at one priority are averaged by their
coverage, which is commutative. A field that depended on the order a scene happened to walk its
entities in is one where moving an unrelated entity changes the shoreline by a texel — found months
later as "the water flickers near the bridge".

⚠ **The window's origin snaps to the *coarsest* consumer's grid.** Snapping to the field's own texel
is not enough once a ripple simulation samples it at a different rate: the two grids beat against each
other and produce a crawl along the shoreline that appears only while the camera moves. Floor rather
than round, so a window following a camera never steps backwards.

⚠ **And the snap grid has to be a whole number of the field's own texels**, which `WaterZone.Validate`
refuses otherwise. The samples include both edges, so the spacing is `Extent / (Resolution − 1)` — a
512 m window wants **257** texels for a round two metres, exactly as a terrain tile wants a power of
two *plus one*. It shipped as `Extent / Resolution` for a day, with the rasteriser using one spacing
and the snap using the other; the two beat against each other and produced precisely the crawl the
snap exists to prevent. The stability sweep is what found it.

⚠ **And the stability sweep could not have failed as first written.** Bilinear interpolation of a
*linear* field is exact on any grid, so a planar test beach cannot show a sampling crawl however badly
the window is placed — the test was measuring nothing. The bed needs curvature, and the sweep now
ships with a negative control: an unsnapped window over the same bed wobbles a hundred times more.

## The zone

`WaterZone` is what an author states and `WaterZoneState` is what holds a field, moves it and says when
it was last right. The window is re-rasterised only when the view has crossed `ScrollThreshold` of the
extent or something has changed — a hundred frames apart at a walk — and `RasterCount` is the reading
that says so.

⚠ **The field is filled by the same code the surface query reads, and that is § D2 rather than a
shortcut.** § D3 describes the info texture as a top-down *render*, with every body drawing into it
through a material. That is faster and it is a second rasterisation of the same bodies — one on the
device for the picture, one on the host for the boat — with nothing holding the two together. Filling
it from `WaterField.Rasterize` means the shoreline the material shades and the depth the buoyancy
solver reads are the same numbers by construction.

⚠ **Which needed the distance query to be fast, and it was not.** Measuring a point against the curve
by scanning for a nearest sample and ternary-searching between its neighbours is ninety spline
evaluations; a 256² field against one body is sixty-five thousand of those, and it took **four
seconds** — a hitch, not an amortised cost. `WaterBody` flattens its curve once in the constructor and
projects onto the polyline instead, which is six multiplies a segment. The same field is now
milliseconds, and the answer is *more* accurate than the search it replaced, because a projection onto
a segment is exact.

⚠ **A shoreline band narrower than a few texels cannot be resolved however smooth the arithmetic is.**
That is a real constraint on an author and the reason the panel shows metres per texel: an eight-metre
falloff at one metre a texel is a ramp, and a two-metre falloff at the same rate is two texels, which
is neither a ramp nor a cut.

## The surface mesh is the terrain's quadtree

[§ D4]. `WaterSurfaceMesh` selects patches through `PatchSelector` — the *same* descent
`TerrainLodTree` uses, extracted rather than copied — so the no-crack property, the morph and their
two tests are written once. The only difference between the two consumers is what the vertex stage
samples for height.

⚠ **Water makes a crack worse than a terrain does, not better.** A crack in a terrain shows a sliver
of skybox for a frame; a crack in a flat specular surface shows a bright line that reads as a
rendering artefact from four hundred metres. Which is why `WaterSurfaceMeshTests` is written before
the renderer rather than after the first screenshot.

**The finest node's vertex spacing is the field's texel spacing, by construction.** The root spans the
window's `Resolution − 1` quads rounded up to whole patches, so a 512 m window at 257 texels gives
256 quads at two metres — one vertex per texel at level zero. Any other choice makes the surface
either carry detail the field cannot supply or throw away detail it can.

⚠ **A node's box is grown by the sea state's maximum amplitude.** A node bounded by its rest height is
one culled away while a crest is still in front of the camera, and the symptom is a strip of missing
sea that appears only when the wind rises. `WaterFieldPyramid` is what makes asking cheap: a reduction
of the field's coverage and surface range, so "is any of this 128-metre square wet, and how tall does
it get" is nine lookups whatever the node's size, rather than a scan.

⚠ **The far mesh and the edge fade are one decision.** The skirt to the horizon has no field under it
and so no waves; meeting the window's full-height waves directly puts a step the height of a crest
along a straight line at the horizon, in every frame. So the amplitude ramps to zero across
`EdgeFade` and the two agree exactly where they meet — **which is the one place the drawn surface is
deliberately not the queried one.** The fade is in the mesh rather than in the evaluator, because in
the evaluator it would make every buoyancy query depend on where the *camera* is: a raft that rides
differently depending on where somebody is looking, which is § D2's seam broken in the worst way.
Here the disagreement is confined to a band the view is half a window from.

## Carving is a reserved edit layer, and the machinery existed

[§ D5]. `WaterCarve` writes into the terrain's reserved `Water` layer, alongside Splines and Scatter,
on [31 § D4]'s contract and with **no change to it** — which is the evidence doc 35 claims it is: the
feature that most obviously wants non-destructive terrain deformation was not in scope when the
mechanism was designed, and needed nothing added to it.

**The bed a body carves is the bed the field rasterises, by construction.** Both read
`WaterBody.Sample` — the surface height minus the coverage-weighted bed depth — so the shoreline the
terrain is cut to and the shoreline the water is drawn at cannot disagree. That is § D2's argument
applied to the ground rather than to the surface, and it is why `WaterCarve` takes a `WaterBody`
rather than a `TerrainSplineProfile`.

⚠ **A carve only ever cuts, and a raise only ever raises.** The bed is where a body *wants* the
ground, not where it insists on it: ground already deeper than the bed is a trench somebody dug on
purpose, and a lake whose surface sits above a valley floor would otherwise fill the valley in.

⚠ **`Regenerate` resolves every body at each sample and combines by min and max — it does not carve
them in turn.** Carving in turn is order-dependent twice over: a later body's clear erases an
earlier one's bed wherever the two rects overlap, and even without that the last writer wins. The
symptom is a lake that gets shallower when an unrelated body is moved.

⚠ **The carve profile has three numbers where Unreal's brush has twenty.** The channel depth, the ramp
and the outward falloff are the *body's*, not the carve's. The terracing, the two octaves of curl and
the shape blur are a procedural shoreline generator living inside a water body, and every one of them
is a terrain brush an author runs on a layer above — which survives the body being moved, because it
is in a different layer. That is the gate § D5 says to check the decision against, and it is a test.

## Buoyancy is pontoons, and the rest is measured rather than tuned

[§ D10]. `Buoyancy.Solve` takes a body's pontoons and its placement, asks the evaluator where the
surface is over each, and produces a force at each pontoon's own world position — which is what makes
a body pitch when one end is lifted rather than bob without rolling.

**The submerged fraction is the exact spherical cap, not a linear ramp on the depth.** A linear
approximation is wrong by a third at half submersion, which is precisely where a floating body rests —
so a crate tuned against it sits at a waterline the arithmetic never predicted. ⚠ It is saturated:
the expression is exactly one at full submersion only in exact arithmetic, and a fraction above one is
lift above Archimedes. The property test found that, which is what a property test is for.

⚠ **It reads the simulation's water time and never a frame time.** A force computed from an
interpolated render-time surface changes when the frame rate does, and in a networked game that is a
client and a server disagreeing about where a boat is.

⚠ **Jolt's own buoyancy impulse is deliberately not used.** It takes a *plane*, which is exactly the
approximation a wave surface is not — and using it would put a second definition of the water surface
inside the physics engine, where the seam test cannot reach it.

⚠ **A current is a drag towards the flow, not a push in its direction.** A constant push accelerates a
raft for ever and it ends the river faster than the water. The test measures a raft reaching about the
river's own speed and staying there.

The convergence test ships with its negative control: with the damping removed the same body is still
moving half a metre a second after five seconds, so the settling is the damping's doing rather than
the integrator's.

⚠ **And it asserts the *waterline*, not the displaced volume.** At equilibrium the lift equals the
weight *by definition*, so "displaced volume equals `RestDisplacement`" is true for any coefficient
and a test asserting only that still passes with the term deleted. The crate carries a coefficient of
1.6 so that the resting waterline is a number only the right arithmetic produces, and the mutation was
checked.

**The join is not here, and § D1 is why**: it puts the physics half in its own assembly rather than a
reference from here to Jolt. That assembly is
[`Vixen.Water.Physics`](../Vixen.Water.Physics/README.md), and it is built —
`BuoyancyBody`/`BuoyancyState`, `BuoyancySystem` applying the force to Jolt in `FixedUpdate` between
the physics sync and the step, `BuoyancyDebugDraw`, and `BuoyancyDebugSystem` joining that draw to
`water.showBuoyancy`.

**A host registers it now**, which for a long time nothing did: every `new BuoyancySystem(…)` in the
tree was a test. `Samples/13-ThirdPersonShooter`'s `Arena` adds it beside the sibling
`WaterImmersionSystem` it already added, and `Arena.vxscene` carries the raft it works on — four
pontoons authored *in the file*, so the path that once lost `BuoyancyPontoon`'s `[DataContract]` is
the path a shipped scene walks.

⚠ **Authoring that raft found the other half of the same class of defect, one registry over.**
`Collider` and `RigidBody` were declared to `SceneComponentRegistry` by `Vixen.Physics` and absent
from `TypeRegistry`, because that project named the engine's component generator and not the
reflection one — so they were scene components no scene file could ever name. Nothing had noticed
because no scene in the repository had authored either.

The networked predicted body is still owed — neither component is `[Replicated]`.

## Ripples are a sliding window, and every number in it is a bound

[§ D12]. `WaterRipples` is a height field and its velocity, advanced by an explicit wave-equation step,
with entities injecting into it — and the evaluator adds it, so a second boat rides the first one's
wake and the buoyancy solver feels it.

⚠ **This runs on the CPU even when a GPU simulation is running, and that is stated behaviour rather
than duplication.** Buoyancy sampling a GPU texture cannot see the current frame's step without a
stall. So the same injections are simulated here at a lower resolution, and the two are compared at a
**stated tolerance that is not exact** — unlike everything else in this assembly. That asymmetry is
why the ripple contribution is a separate argument to the evaluator: the closed-form part is exact and
answerable at any past time, the simulated part is neither, and the network path asks for the first
alone by passing no ripples at all.

⚠ **An injection goes into the velocity, not the height.** Writing the height holds the surface at a
shape until something else moves it, so a boat sitting still would carve a permanent hole in the lake;
a velocity impulse propagates away as a ring, which is what a splash is.

⚠ **The Courant limit is refused rather than discovered.** Past `c ≤ Δx ⁄ (Δt√2)` an explicit wave
equation does not look wrong — it grows without bound in a few dozen steps and every consumer
downstream reads a NaN. `WaterRippleSettings.Validate` is where that becomes a message.

⚠ **The edge is damped to nothing rather than clamped**, or the boundary is a mirror and a wake
returns to meet itself a second later — which reads as the lake having invisible walls exactly as far
away as the window is wide. The test measures that against a mirrored control.

⚠ **The window scrolls by shifting its contents, unlike `WaterField`, which forgets them.** A field is
a function of bodies and ground that can be recomputed; a simulation's state *is* its history. The
shift is by whole texels, which is why the origin snaps: a window moving by a fraction of a texel
resamples its own state every step, and a resampling filter applied repeatedly is a low-pass filter —
the wake would blur away while walking and be sharp again the moment the camera stopped.

**The injection budget is spent in arrival order and the overflow is a number.** A scene over budget
has *arbitrary* ripples rather than merely fewer, and a budget nobody can see is one nobody raises
before shipping.

**What is not here is the device half.** The compute dispatch over W0's ping-pong pair, the CPU/GPU
seam test at its stated non-exact tolerance, and the wake and splash hooks for `Vixen.Vfx` are W8's
other half and are not built.

## What is not here

The renderer (`Vixen.Rendering.Water`), the editor (`Vixen.Editor.Water`), the Raven modules, the
buoyancy solver's Jolt join, and the ripple simulation. Each is a later phase of the same document,
and each reads this.

[§ D1]: ../../docs/plan/35-water.md#d1-three-assemblies-and-the-kernel-touches-no-device
[§ D2]: ../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test
[§ D3]: ../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render
[§ D7]: ../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic
