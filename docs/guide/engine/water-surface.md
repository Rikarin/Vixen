---
title: Where the water surface is
slug: engine/water-surface
kind: guide
area: Engine
summary: One definition of the water surface — a body from a spline and a profile, a sea state from a spectrum, a field of height, flow and ground, and the evaluator that the renderer, the buoyancy solver and a gameplay query all call.
api: [T:Vixen.Water.WaterZone, T:Vixen.Water.WaterZoneState, T:Vixen.Water.WaterZoneUpdate, T:Vixen.Water.WaterInfoPrecision, T:Vixen.Water.WaterQuery, T:Vixen.Water.IWaterFieldSource, T:Vixen.Water.WaterEvaluator, T:Vixen.Water.WaterSample, T:Vixen.Water.WaterAttenuation, T:Vixen.Water.IWaterRipples, T:Vixen.Water.WaterBody, T:Vixen.Water.WaterBodyKind, T:Vixen.Water.WaterProfilePoint, T:Vixen.Water.WaterBodyContribution, T:Vixen.Water.WaterField, T:Vixen.Water.WaterFieldDescription, T:Vixen.Water.WaterFieldSample, T:Vixen.Water.IWaterGround, T:Vixen.Water.FlatWaterGround, T:Vixen.Water.WaterWaveSpectrum, T:Vixen.Water.WaterWaveCount, T:Vixen.Water.GerstnerWave, T:Vixen.Water.WaterMath]
tags: [water, ocean, river, buoyancy, gerstner, terrain]
since: 0.1
status: preview
related: [engine/splines, engine/terrain-heightfield, engine/character-movement]
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
