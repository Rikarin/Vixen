---
title: Fog a shadow can fall through
slug: rendering/volumetric-fog
kind: guide
area: Rendering
summary: The froxel volume that turns the frame's fog from a function of distance into something the scene's own light shines through, what its far plane really controls, and why the composite is on the other side of TAA.
api: [T:Vixen.Rendering.PostFx.VolumetricFogAsset, T:Vixen.Rendering.PostFx.VolumetricFogRenderer, T:Vixen.Rendering.PostFx.FogAsset, T:Vixen.Rendering.PostFx.FogRenderer]
tags: [rendering, post-processing, compositor, fog]
since: 0.1
status: stable
related: [rendering/post-processing, rendering/standard-frame, rendering/render-quality, rendering/physical-lighting]
---

## What it is

Three compute dispatches that fill a 3D texture laid out along the camera's frustum, and one
permutation on the fog pass that reads it. Each cell of that texture — a *froxel*, a frustum-shaped
voxel — holds how much light the air in it scatters toward the camera and how much it stops.

| Node | What it does |
|---|---|
| `!VolumetricFog` | The three dispatches. Writes `FogMedia`, `FogScattered`, `FogVolume` |
| `!Fog` with `volume` set | Reads `FogVolume` and composites it, with the analytic falloff beyond |

The `!StandardFrame` expansion emits both, named `Volumetrics` and `Air`, when the tier asks for
them.

## What it is for

The fog every renderer starts with is a function of distance and altitude. That is enough for a
horizon that fades, and it is structurally unable to do the thing people actually want fog for: it
cannot know that a wall is between this patch of air and the sun. A valley under an analytic fog has
no beams in it, and no amount of tuning puts them there — the information is not in the model.

Marching a volume is what buys that. Every froxel asks what light reaches it, so a froxel behind a
wall is dark and the one beside it is bright, and the boundary between them is a shaft of light.

⚠ **It does not replace the analytic falloff, it stands in front of it.** The volume covers its own
range — sixty-four metres or so — and the world does not stop there. Beyond the volume's far plane
the same exponential falloff runs that always did, measured *from the far plane* rather than from the
camera, so the two do not fog the same metres twice.

## Using it

The simplest route is the tier. `!StandardFrame` emits the whole arrangement:

```yaml
!StandardFrame
  quality: High
```

`High` and `Epic` fill the volume; `Low` and `Medium` leave the analytic fog alone. See
[Choosing how hard the frame works](render-quality.md) for the three knobs — `post.volumetricFog`,
`post.volumetricSlices` and `post.volumetricFar` — and how a project overrides them.

Authored by hand, it is two nodes:

```yaml
- !VolumetricFog
  name: Volumetrics
  view: Camera
  output: FogVolume
  slices: 64
  far: 64
  density: 0.02
  sunDirection: [0.3, -0.8, 0.5]
  sunColour: [1.0, 0.9, 0.7]
  ambientColour: [0.35, 0.42, 0.55]

- !Fog
  name: Air
  source: SceneResolved
  depth: SceneDepth
  view: Camera
  output: SceneFogged
  volume: FogVolume
  volumeFar: 64
  volumeSlices: 64
```

⚠ **`volumeFar` and `volumeSlices` must be the numbers the dispatch used.** The composite finds a
pixel's slice by inverting the grid's depth distribution, so a far plane or a slice count that
disagrees reads the wrong slice for every pixel — a fog that is smooth, plausible, and wrong
everywhere. The standard frame takes both from the same tier for exactly this reason.

### The far plane is not a draw distance

The number worth understanding before any other. Raising `far` does **not** make the fog reach
further; the analytic term already covers everything past it. What it changes is how the slices are
*spent*.

The slices are distributed geometrically, so each is the same *ratio* deeper than the last. At
sixty-four metres and sixty-four slices the nearest slice is about a centimetre deep and the furthest
about four metres — which is the right shape, because a beam's edge is something you see near you and
a hundred metres of haze is something you see the average of. Stretch the same sixty-four slices over
a kilometre and the near slices become metres deep, which is coarse enough that a shadow edge
crossing the volume reads as a staircase.

### Where the passes run

The three dispatches sit between the shadow passes and the scene, and the composite sits after the
temporal accumulation. Those pull in opposite directions and both are deliberate:

- The dispatches need shadows and lights and **not** scene colour, so they belong before anything
  draws. Saying that as a declared edge is what puts the barriers in.
- The composite must be after TAA. The accumulator averages radiance and must only ever see the
  finished frame — fog added before it would be smeared by a reprojection meant for surfaces.

⚠ Splitting the feature across the accumulator is only safe because the volume does its temporal work
in its own space rather than the screen's. A volume inside TAA would be reprojected as though it were
a surface, which it is not.

## Examples

Ground mist in a valley — thick at the bottom, thinning with altitude:

```yaml
!VolumetricFog
  name: Volumetrics
  view: Camera
  density: 0.06
  heightFalloff: true
  fogHeight: 2.0
  heightFalloffRate: 0.25
  phaseG: 0.6
```

A back-lit haze, where the anisotropy is what makes looking toward the sun bright:

```yaml
!VolumetricFog
  name: Volumetrics
  view: Camera
  density: 0.015
  phaseG: 0.85
  sunColour: [2.0, 1.6, 1.1]
  ambientColour: [0.2, 0.26, 0.4]
```

⚠ **`ambientColour` of zero is not "no ambient", it is a valley that goes black whenever the sun is
behind the viewer.** A phase function is normalised over the sphere, so one directional light
contributes almost nothing outside its forward peak; what makes air *visible* from every angle is
what arrives from the whole sky. The same term is why deep water is blue rather than black.

### Tuning, in the order that pays

1. `density` — how much there is. Everything else is shape.
2. `far` — where the resolution is spent. See above.
3. `phaseG` — 0 is an even glow, 0.7 is air, 0.9 is a searchlight beam.
4. `ambientColour` — the floor the fog never goes below.
5. `slices` — raise it if a shadow edge crossing the volume bands.

## See also

- [Working the frame after it is drawn](post-processing.md) — the analytic fog this stands in front
  of, and the rest of the chain.
- [The frame, as one node](standard-frame.md) — where the two nodes land and why.
- [Choosing how hard the frame works](render-quality.md) — the tier knobs.
- [Light that behaves like light](physical-lighting.md) — the units the sun colour is in.
