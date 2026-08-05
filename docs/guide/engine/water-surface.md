---
title: Where the water surface is
slug: engine/water-surface
kind: guide
area: Engine
summary: One definition of the water surface — a body from a spline and a profile, a sea state from a spectrum, a field of height, flow and ground, and the evaluator that the renderer, the buoyancy solver and a gameplay query all call.
api: [T:Vixen.Water.WaterZone, T:Vixen.Water.WaterZoneState, T:Vixen.Water.WaterZoneUpdate, T:Vixen.Water.WaterInfoPrecision, T:Vixen.Water.WaterQuery, T:Vixen.Water.IWaterFieldSource, T:Vixen.Water.WaterEvaluator, T:Vixen.Water.WaterSample, T:Vixen.Water.WaterAttenuation, T:Vixen.Water.IWaterRipples, T:Vixen.Water.WaterBody, T:Vixen.Water.WaterBodyKind, T:Vixen.Water.WaterProfilePoint, T:Vixen.Water.WaterBodyContribution, T:Vixen.Water.WaterField, T:Vixen.Water.WaterFieldDescription, T:Vixen.Water.WaterFieldSample, T:Vixen.Water.IWaterGround, T:Vixen.Water.FlatWaterGround, T:Vixen.Water.WaterWaveSpectrum, T:Vixen.Water.WaterWaveCount, T:Vixen.Water.GerstnerWave, T:Vixen.Water.WaterMath, T:Vixen.Water.WaterSurfaceMesh, T:Vixen.Water.WaterFieldPyramid, T:Vixen.Water.WaterCarve, T:Vixen.Water.WaterCarveProfile, T:Vixen.Water.Buoyancy, T:Vixen.Water.BuoyancyPontoon, T:Vixen.Water.BuoyancySettings, T:Vixen.Water.BuoyancyForce, T:Vixen.Water.WaterRipples, T:Vixen.Water.WaterRippleSettings, T:Vixen.Water.WaterWavesAsset, T:Vixen.Rendering.Water.IWaterWaveSource, T:Vixen.Engine.Renderer.AssetWaterSource, T:Vixen.Water.WaterDisturbance, T:Vixen.Water.WaterDisturbanceKind, T:Vixen.Water.WaterDisturbances]
tags: [water, ocean, river, buoyancy, gerstner, terrain]
since: 0.1
status: preview
related: [engine/splines, engine/terrain-heightfield, engine/character-movement, engine/buoyancy]
---

## What it is

`Vixen.Water` is the kernel: pure arithmetic that answers **where the surface is at a position and a
time**. Four pieces stack up.

| Piece | What it is |
|---|---|
| `WaterBody` | A spline and a profile. A river is open, everything else closes |
| `WaterWaveSpectrum` | Wind, spread, a wavelength range and a seed — which *generates* `GerstnerWave`s |
| `WaterField` | Every body in a region rasterised into surface height, flow, ground and coverage |
| `WaterQuery` | The evaluator over the two, and the thing everything outside holds |
| `WaterZone` | A region's water: how big the window is, at what rate, and when it moves |

No device, no world and no renderer. That is deliberate: a dedicated server has no device and still
has to answer *how deep is the water here* for every swimming character and every boat it simulates.

## What it is for

⚠ **So that a boat floats at the height the water is drawn at.**

The surface is defined once and evaluated by two hosts — this, and `Raven/Library/Water/Surface.rvn`
— and the two are held together by a device test at a stated tolerance (see below; the design asked
for exact and the measurement is 2 × 10⁻⁴ m). Both of the engines
this design surveyed evaluate the surface twice and neither pins them together, and the symptoms are
the reason people believe water is hard: a boat that hovers a hand's width above the crests in a
swell, a character whose swimming state flickers at the shoreline, a buoy that sinks when the frame
rate drops. Unreal ships a per-body `Max Wave Height Offset` to correct exactly that drift — a knob
whose existence is a bug report.

## Using it

```csharp no-compile="the shape of a setup, not a compiling scene"
// A river: an open spline, with width, depth, velocity and audio intensity per control point.
var river = new WaterBody(WaterBodyKind.River, centreline, profilePerPoint) {
    ShoreFalloff = 1f,      // how far past the channel the surface fades out
    BedRamp = 2f            // how far in the bed reaches its full depth
};

// Every body in a region, resolved once into a field over the ground plane.
var field = new WaterField(new() { Origin = origin, Extent = 256f, Resolution = 256 });
field.Rasterize([river, lake], terrainAsGround);

// And the one thing everything else holds.
var query = new WaterQuery(field, WaterWaveSpectrum.Default);
var surface = query.Sample(new(x, z), waterTime);
```

`WaterSample` carries the height, the normal, the flow, the depth and the coverage — the five things
a vertex stage, a pontoon and a movement mode between them need.

### ⚠ Time is a water time, not a frame time

There is no clock in the kernel and there will not be one. The fixed step and the render both pass a
value derived from the same source, the render interpolating within the step.

A buoyancy solver reading the frame's total time and a shader reading a smoothed one *is* the drift,
and it is invisible until the frame rate changes — at which point a force computed from an
interpolated render-time surface changes with it, and in a networked game that is a client and a
server disagreeing about where a boat is.

### ⚠ The closed form is a different question from the simulated one

```csharp no-compile="two calls, and the difference is the point"
query.Sample(position, waterTime);       // waves + any ripple simulation
query.ClosedForm(position, waterTime);   // waves alone
```

The wave sum is a closed-form function of position and time, so the surface six ticks ago costs
exactly what the surface now costs. A rollback needs that; a ripple simulation cannot provide it,
because it is a height field advanced a step at a time. So the network path asks for the closed form
alone, and the signature is what enforces the separation rather than a comment asking for it.

It is also why the wave model is Gerstner rather than FFT in the first version. An FFT ocean is
better at open sea and needs a per-frame dispatch chain and a CPU path that either reads back a
texture or runs a second transform — neither of which can answer *where was the surface six ticks
ago*. When it lands it is a second model behind the same evaluator.

### The zone, and when its window moves

```csharp no-compile="the shape of a zone, not a compiling scene"
var state = new WaterZoneState(WaterZone.Default with { Extent = 512f, Resolution = 257, CoarsestTexel = 4f });

state.SetBodies([river, lake]);
state.Update(cameraOnTheGroundPlane, terrainAsGround);   // rasterises only when it has to

var query = state.Query(spectrum);   // reads the field the zone is holding *now*
```

The query holds the *state* rather than the field object — `WaterZoneState` is its
`IWaterFieldSource` — so it stays live across a scroll **and** across a reshape to a new resolution,
which builds a new field the old arrays could not become. A query built from a snapshot would answer
for a window that no longer exists, which is a boat that keeps floating where the water used to be.

The window is re-rasterised only when the view has crossed `ScrollThreshold` of the extent or
something has changed — a hundred frames apart at a walk. `RasterCount` is the reading that says the
threshold is working: a number tracking the frame count is a zone paying its whole cost every frame,
and one that stops rising while the view moves is a window left behind.

⚠ **257 texels and not 256, for the terrain quadtree's reason.** The samples include both edges, so
the spacing is `Extent / (Resolution − 1)` — 512 m over 257 samples is two metres exactly, where 256
would be 2.0078. `WaterZone.Validate` refuses a `CoarsestTexel` that is not a whole number of those,
because a window snapped to a grid that is a fraction of a texel is the shoreline crawl the snap
exists to prevent.

⚠ **A shoreline band narrower than a few texels cannot be resolved**, however smooth the arithmetic
is. That is what the metres-per-texel readout is for: an eight-metre falloff at one metre a texel is a
ramp, and a two-metre falloff at the same rate is two texels, which is neither a ramp nor a cut.

### The surface mesh, and the three things it shares with the terrain

```csharp no-compile="the shape of a mesh, not a compiling scene"
var mesh = new WaterSurfaceMesh(state.Window, TerrainLodRanges.Default) { FarDistance = 8000f };
var reduced = new WaterFieldPyramid(state.Window.Resolution);

reduced.Build(state.Field!);
mesh.Update(reduced, query.MaximumAmplitude);   // ⚠ the amplitude, or crests get culled away

var patches = new List<TerrainLodNode>();

mesh.SelectFar(frustum, restHeight, patches);            // the skirt first — see below
mesh.Select(camera, frustum, patches);                   // then the window
```

The descent is `PatchSelector`, which is `TerrainLodTree`'s with the terrain taken out of it — so the
morph, the no-crack property and the continuity property are one implementation with two consumers
rather than Unreal's two of each. The finest node's vertex spacing *is* the field's texel spacing, by
construction: a 512 m window at 257 texels gives 256 quads at two metres, one vertex per texel.

⚠ **A node's bounding box is grown by the sea state's maximum amplitude.** A node bounded by its rest
height is one culled away while a crest is still in front of the camera, and the symptom is a strip of
missing sea that appears only when the wind rises. `WaterFieldPyramid` is what makes the question
cheap — a reduction of coverage and surface range, so "is any of this 128-metre square wet, and how
tall does it get" is nine lookups whatever the node's size.

⚠ **The far mesh is selected first and drawn first**, and the order is load-bearing: the surface tests
depth without writing it, so nothing arbitrates between two fragments at one pixel except which came
last — and the near mesh is the one with a field under it.

⚠ **`EdgeFade` is the one place the drawn surface is deliberately not the queried one.** The skirt has
no field under it and so no waves; meeting the window's full-height waves directly puts a step the
height of a crest along a straight line at the horizon, in every frame. The amplitude ramps to zero
across the band instead. It is in the mesh and not in the evaluator, because in the evaluator it would
make a buoyancy query depend on where the *camera* is — a raft that rides differently depending on
where somebody is looking.

### ⚠ Depth is computed, never stored

`WaterField` carries surface height, flow, ground and coverage. How deep the water is at a place is
the first minus the third, worked out where it is used. Storing it would be a third number that can
disagree with the two it came from, and the frame it disagrees on is the one where the shoreline is
in a different place for the material than for the wave attenuation.

Attenuation is in the evaluator for the same family of reasons: a wave whose amplitude does not fall
off as the ground rises produces a crest that intersects the beach, and a project that fixes the
visible half in a material then discovers that buoyancy is still using the unattenuated height and a
boat in the shallows is bobbing through the sand.

### ⚠ Trigonometry goes through a polynomial

`WaterMath.SinCos`, and not `MathF.Sin`. Vulkan allows `OpSin` 8192 ULP over the useful range and a
driver may use whatever its special-function unit implements — at the phases a long session reaches
that is not a rounding difference, it is a different wave. Both sides call the same stated polynomial
over the same stated four-part range reduction instead.

⚠ **The seam's measured tolerance is 2 × 10⁻⁴ m, and the design asked for exact.** The residue is a
device's freedom to contract a multiply and an add into one FMA even where the source does not — the
parenthesisation in `WaterMath` is load-bearing, and it does not reach a translation layer below the
driver. It shows up in the phase, so it is linear in it: a millionth of a metre at a hundred radians,
a twentieth of a millimetre at five thousand. A tenth of a millimetre on a surface whose crests are
metres, and it does not accumulate over a session because the surface is a closed form rather than a
simulation.

### Carving, buoyancy and ripples

Three more things read the same field and the same evaluator, which is § D2's whole return.

```csharp no-compile="the three seams, not a compiling scene"
// The bed a body wants, cut into the terrain's reserved Water layer. Regenerated wholesale, so
// moving a river restores the old bank and cuts the new one in one operation.
WaterCarve.Regenerate(terrain, WaterCarve.LayerOf(terrain), [(river, WaterCarveProfile.Default)]);

// A crate floating. The force comes back at each pontoon's own world position, which is what makes
// a body pitch when one end is lifted.
Buoyancy.Solve(in evaluator, pontoons, in placement, velocity, gravity, in settings, waterTime, forces);

// And a wake, which the evaluator adds — so a second boat rides the first one's.
ripples.Inject(hullPosition, 1.5f, -3f);
ripples.Step(fixedStep);
var surface = query.Sample(position, waterTime, ripples);
```

⚠ **A carve only ever cuts.** The bed is where a body *wants* the ground, not where it insists on it:
ground already deeper is a trench somebody dug on purpose, and a lake whose surface sits above a
valley floor would otherwise fill the valley in. `WaterCarve.Regenerate` resolves every body at each
sample and combines by min and max rather than carving them in turn, because carving in turn gives a
different answer depending on the order a scene walked its entities in.

⚠ **The submerged fraction is the exact spherical cap.** A linear ramp on the depth is wrong by a
third at half submersion, which is precisely where a floating body rests — so a crate tuned against
one sits at a waterline the arithmetic never predicted. `Buoyancy.RestDisplacement` is the analytic
answer the convergence test measures the solver against.

⚠ **A ripple injection goes into the velocity, not the height**, or a boat sitting still carves a
permanent hole in the lake. And `WaterRippleSettings.Validate` refuses a speed above the Courant
limit rather than letting it be discovered: past it an explicit wave equation does not look wrong, it
grows without bound in a few dozen steps and everything downstream reads a NaN.

⚠ **The ripple window shifts its contents when it scrolls, unlike the info field, which forgets
them.** A field can be recomputed from bodies and ground; a simulation's state *is* its history, and
throwing it away when the camera walks would delete every wake in the scene.

### The one asset kind: a sea state in a file

`.vxwaves` is what § D6 admits, and the reason it is admitted is sharing. Everything else water puts
in a scene is per-body or per-zone, so it travels with the entity and merges where the entity does. A
sea state is neither: it is shared between every body in a region **and between levels**, so a
coastline authored across four streamed sublevels would otherwise hold four copies of one wind and
drift out of step the first time somebody edited three of them.

| Piece | Where |
|---|---|
| `WaterWavesAsset` | the kernel — a name and a `WaterWaveSpectrum`, no device, no world |
| `WaterWavesImporter` | `Vixen.Editor.Assets` — validates, then writes the serialized record |
| `IWaterWaveSource` | the seam `WaterZoneSystem.Waves` is set to |
| `AssetWaterSource` | `Vixen.Engine.Renderer` — the game's answer, and the splines' too |

⚠ **The name becomes a value in exactly one place.** The fold substitutes a resolved spectrum into
the `WaterZoneComponent` every consumer reads, so the vertex stage and the underwater shape cannot
disagree about what sea this is — a frame where they did is a boat riding a different swell from the
one drawn under it.

⚠ **A sea state that has not loaded falls back to the zone's inline spectrum, which is the opposite
of a body's missing spline.** A body with no curve has no shape to draw and is not rendered at all; a
zone with no spectrum has a perfectly good window, and rendering it dead flat reads as the water
stack being broken rather than as one asset still streaming. `WaterZoneSystem.UnresolvedWaves` — and
`stat water`'s `no waves` row — are the only evidence, which is why they exist.

⚠ **A `.vxwaves` and a `.vxspline` both ship as serialized records, not as text.** A game does not
carry the YAML dialect; that is the editor's format. This was the bug in the spline half:
`SplineAsset.Points` was a getter-only `IReadOnlyList`, both serialisers skip a member they cannot
write to, and every curve round-tripped to a name, a closed flag and *no points* — with no error
anywhere, because everything downstream asks `CanBuild` and draws nothing when the answer is no.

### Wakes and splashes are one event with two consumers

A `WaterDisturbance` says something disturbed the water here, this hard, this wide. A ripple field
turns it into an injection; `Vixen.Vfx` turns it into a burst of spray. `WaterDisturbances` is the
bounded queue between them.

⚠ **One event and not two producers**, which is § D2's rule applied once more: two producers is a wake
whose spray is not where the ripple is, and the frame they stop agreeing on is the frame something
changed in only one of them.

⚠ **Draining does not empty.** A queue that emptied itself on the first read would give whichever
system was added second nothing at all — a wake with no spray, or spray with no wake, depending on an
ordering nobody chose. The step that produced them is what clears it.

⚠ **A strength is a rate, not a displacement.** A source that pushed the height down would carve a
permanent dent in the lake; one that pushes the rate down makes a depression that springs back past
its own start.

⚠ **In the kernel, so a dedicated server produces the same events and drops them.** A headless build
has no particles and still simulates the boat that would have made them; putting the event where the
renderer is would mean the two builds disagreed about how the hull was moving.

## Examples

**A sea state is a spectrum, and the same seed is the same sea everywhere.**

```csharp no-compile="values, not a compiling scene"
var spectrum = WaterWaveSpectrum.Default with {
    WindDirection = 0.7f,
    WindSpeed = 14f,          // amplitude scales with the square of it
    DirectionalSpread = 0.6f, // ⚠ zero is corrugated iron, not a sea
    MinimumWavelength = 4f,
    MaximumWavelength = 90f,
    Count = WaterWaveCount.ThirtyTwo,
    Seed = 42u
};

query.SetSpectrum(spectrum);
var tallest = query.MaximumAmplitude;   // what the LOD metric and the bounds are sized from
```

The wave count is **quantised to 8 / 16 / 32** because it is a shader permutation: a sea state gaining
one wave must not compile a shader, and a dynamic loop on one host against an unrolled one on the
other is how two implementations of one function start to differ in the last bits.

`MaximumAmplitude` is a bound — the sum of every amplitude, the case where every crest lines up —
rather than a measurement. A measured maximum is a smaller, prettier number that is wrong about once
a frame, and the frame it is wrong on is the one where the bounding box does not contain the surface.

**Bodies resolve to the same field in either order.** Priority decides which body is on top; bodies at
one priority are averaged by their coverage, which is commutative. A field that depended on the order
a scene happened to walk its entities in is one where moving an unrelated entity changes the shoreline
by a texel, found months later as "the water flickers near the bridge".

**An island is the same mechanism with the sign flipped** — a body kind whose coverage subtracts and
whose ground rises, rather than a second actor type.

**Immersion is the only new number a swimming character needs.**

```csharp no-compile="one call; every rule about wading and swimming is a threshold on it"
var immersion = query.Immersion(new(x, z), capsuleBottom, capsuleHeight, waterTime);
```

⚠ It is monotone in the capsule's height by construction, and the tests say so. A non-monotone
immersion is a character that becomes *less* submerged by sinking, and no amount of hysteresis on a
movement mode's thresholds can save an input that goes backwards.

## See also

- [Splines](splines.md) — the curve a body is authored as, and the same one a road uses.
- [The terrain heightfield](terrain-heightfield.md) — what a zone asks for its ground channel, and
  where the reserved `Water` edit layer carves.
- [Character movement](character-movement.md) — where the immersion threshold will land.
- `docs/plan/35-water.md` — the design, the two references it was surveyed against, and what is
  deliberately not built.
