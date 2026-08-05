---
title: Fog a shadow can fall through
slug: rendering/volumetric-fog
kind: guide
area: Rendering
summary: The froxel volume that turns the frame's fog from a function of distance into something the scene's own light shines through, what its far plane really controls, and why the composite is on the other side of TAA.
api: [T:Vixen.Rendering.PostFx.VolumetricFogAsset, T:Vixen.Rendering.PostFx.VolumetricFogRenderer, T:Vixen.Rendering.PostFx.FogAsset, T:Vixen.Rendering.PostFx.FogRenderer, T:Vixen.Shaders.Generated.VolumetricFogKeys, T:Vixen.Shaders.Generated.VolumetricFogConstants, T:Vixen.Shaders.Generated.VolumetricFogInjectKeys, T:Vixen.Shaders.Generated.VolumetricFogInjectConstants, R:PostFx/VolumetricFog, R:PostFx/VolumetricFogInject, T:Vixen.Shaders.Generated.VolumetricFogCascadesElement]
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

`High` and `Epic` fill the volume and shadow it; `Low` and `Medium` leave the analytic fog alone. See
[Choosing how hard the frame works](render-quality.md) for the four knobs — `post.volumetricFog`,
`post.volumetricShadows`, `post.volumetricSlices` and `post.volumetricFar` — and how a project
overrides them.

⚠ `post.volumetricShadows` is the one to reach for when the feature costs too much before dropping
`post.volumetricFog` altogether — but understand what you are keeping. Off, the volume is still
filled and still composited, and what goes is the beams. That is a defensible trade only where the
slice count is already low; otherwise it is paying for the marching and not for the reason to march.

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

### Shadowed in-scatter, which is the point

Fog that no shadow falls through is a glow. It is brighter toward the sun and it thins with altitude,
and both of those the analytic falloff already did for a fraction of the price. A *beam* is the
absence of light behind a caster, and nothing derivable from a pixel's distance and height can
produce one — so the lighting pass asks, per froxel, whether the sun reaches it.

**It turns itself on.** There is no knob for the atlas. The pass looks for two things and uses them
if the frame has both:

| What it needs        | Who provides it in the standard frame                                  |
| -------------------- | ---------------------------------------------------------------------- |
| A cascade atlas      | The `Sun` node — the `shadows: Cascades` line                           |
| The cascade matrices | The scene pass publishes them under its own name (`ForwardPlus`)        |

Either alone is useless: an atlas with no matrices cannot be projected into, and matrices with no
atlas have nothing to sample. A frame missing either fills the volume unshadowed, which is a real
answer rather than a failure — the height gradient and the phase peak are still worth marching.

⚠ **A hand-authored frame that renames things must say so.** `shadowAtlas:` and `scenePass:` on the
node are how; the defaults match what the standard frame emits. A name the frame never declared is
indistinguishable from a frame with no sun, so the fog goes quietly unshadowed.

The cascade *selection* is shared with the ground's lit path rather than restated — containment
rather than depth, the two-column atlas fold, the blend across the last tenth of a cascade, the fade
at the shadow distance. A froxel that picked its cascade by a second copy of that rule would shadow
the air on one side of the terrain boundary and the ground on the other.

#### What it costs

The tap is a 3×3 filter, and a froxel near a cascade edge blends two cascades — so up to eighteen
depth comparisons per froxel, against one grid of 160 × 90 × slices. At sixty-four slices that is
about 0.9 M froxels, and it is the dominant cost of the whole feature by a wide margin: the injection
and the march are each one cheap pass over the same grid.

The levers, cheapest first:

- **`slices`.** Cost is linear in it and so is the shadow work. Dropping 64 → 32 halves it.
- **`far`.** Does not change the cost at all — it changes where the resolution is spent. Free.
- **Grid width and height** are fixed at 160 × 90 and deliberately not tiered. See below.

#### Why the grid is not wider

The original argument was that a shaft's edge comes from a *slice* boundary crossing a shadow edge —
the depth axis — so lateral resolution buys sharpness the trilinear read gives straight back. That was
asserted before the volume reprojected anything, and reprojection is exactly the thing that could have
made lateral resolution matter more. It does not. Measured:

A froxel at view depth `d` is `d · 2·tan(fovX/2) / 160` wide and `d · ((far/near)^(1/slices) − 1)`
deep. At the High tier — 91° horizontal, 64 slices from 0.5 m to 64 m — that is **0.013 d wide and
0.079 d deep: the depth axis is six times coarser**. At Epic (128 slices to 96 m) it is still 3.3×;
at Medium (32 slices to 48 m), 12×. Whichever axis the camera's motion drags the history along, the
one that was already several times coarser is the one that shows.

And reprojection made width *more* expensive, not less. The scattering volume is now a pair, so the
frame holds four volumes rather than three: at rgba16f, 160 × 90 × 64 is 7.4 MB each and about 30 MB
in total. Doubling to 320 × 180 makes that 118 MB — for a sharpening along the two axes that were
already the fine ones.

⚠ What reprojection *did* introduce is a lateral cost that did not exist before: every frame the
history is resampled at a non-integer grid offset, so fast lateral camera motion smears the volume
along the screen axes at roughly one froxel per frame of accumulation. It is bounded — the blend
weight decays it — and it is worst exactly when the eye's own acuity is lowest. If you find a scene
where it reads, `feedback` is the lever, not the grid width.

⚠ A froxel has no surface normal, so the slope bias and the normal offset that a wall's shadow needs
both vanish here — the air is biased by the constant term alone. This is not a simplification: a
volume of air has no face to be oblique to and nothing to lift a sample off. The constant bias is the
scene pass's own, deliberately, so the shaft and the shadow it belongs to meet.

### Lamps in the air

The sun is not the only thing a medium scatters. A street light in mist has a cone under it, and that
cone is the same in-scatter the sun's beam is — a phase function applied to whatever radiance reaches
the froxel. So the lighting pass also walks the frame's punctual lights, per froxel.

**It turns itself on too, on exactly the shadowing rule.** The pass looks for two buffers and uses
them if the frame published both:

| What it needs         | Who provides it                                                       |
| --------------------- | --------------------------------------------------------------------- |
| The scene light list  | The forward lighting feature, as `ForwardPlus.lightBuffer`             |
| The culled cluster lists | The scene pass's own `sceneBuffers:` line, as `ForwardPlus.clusters` |

⚠ **The standard frame publishes neither, so this is off unless you build a frame that culls.** The
standard frame lights its lamps per object; the cluster cull is a node a hand-authored document adds.
That is not a defect to work around — an object-sized light list is chosen per object, and a fog
volume covers the whole frustum, which is the shape that choice serves worst. A frame that wants lamps
in its air runs the cull, and then the fog reads the same lists the walls do.

Two things are worth knowing before you turn it on:

- **The lamps are unshadowed in the medium.** The punctual shadow atlas is a composed feature of the
  scene pass, and this is a compute dispatch that composes nothing — so a lamp behind a wall lights
  the air on both sides of it. The ground's lit path carries the same debt. The sun, which is what a
  shaft is made of, is the shadowed one.
- **They arrive one frame late.** The buffers are published while the scene pass *executes*, and the
  dispatches are built before it runs. So the first frame after a cut has fog the sun lights alone.

There is no cosine here. A surface receives light across a tilted face; a froxel has no face, so what
weights a lamp's radiance is the phase function — the same one the sun's term uses, with the same
`phaseG`. A lamp is therefore brightest in the air when you are looking almost into it.

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

### Its own temporal work

The grid is coarse — 160 × 90 by a few dozen slices — and a shadow edge crossing it lands on a
different sample the moment the camera moves a fraction of a froxel. What the eye reads from that is
not softness; it is crawl. So the lighting pass keeps a history:

- Each frame the sample point inside every froxel is offset by a Halton (2, 3, 5) step, and the
  injection and the lighting pass are handed **the same** offset. Three axes, not two: the depth axis
  is the one a shaft's edge lives on.
- The froxel's world point is pushed through the previous frame's view-projection and read back as a
  *grid* position — tile from `xy/w`, slice from the grid's own logarithmic distribution applied to
  `w`. Only `w` and `xy/w` are used, never `z`, so none of this depends on reverse-Z or on where the
  far plane is.
- The two are blended at `feedback`, default 0.9 — about ten frames to answer a light that moved.

Only the in-scatter is averaged. The extinction is what the medium *is*, written each frame from a
closed form, so blending it would make a cellar's cleared air bleed out into the valley over several
frames. And the march does not jitter: it runs *after* the blend, so an offset there would be a wobble
in the finished picture rather than detail paid for over several frames.

⚠ **A document that names `scattered:` itself switches this off**, and switches the jitter off with
it. The pair has to be memory the node owns — a resource the graph declares lives for one frame, and
next frame's history would be aliased over something in this one. Jitter without a history is the same
coarse grid asking a different wrong question every frame, which is the crawl rather than a cheaper way
out of it, so the two go together.

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

### A room with different air in it

Three fields on a post-process volume's `settings` override the node where the volume applies:

| Field | What it decides |
|---|---|
| `volumetricDensity` | How thick the medium is here |
| `volumetricAlbedo` | How much of it scatters rather than absorbs |
| `volumetricPhaseG` | Its anisotropy |

```yaml
# A cellar with no mist in it, under a level-wide volume that fills the valley.
settings:
  volumetricDensity: 0.0
```

⚠ **`0` and "unset" are different answers, and this is the field where the difference bites.** Leaving
`volumetricDensity` unset means *this volume has no opinion about fog* — whatever is underneath it
stands, so a volume that only darkens the grade leaves the mist alone. Setting it to `0` means *there
is no fog here*, which is an opinion and a strong one. If the two were flattened, every volume in the
level that cared about anything else would silently delete the fog.

See [Making a room look different](post-process-volumes.md) for how volumes overlap, blend and
resolve by priority — the volumetric fields follow exactly those rules and need no shape, weight or
priority of their own.

### Tuning, in the order that pays

1. `density` — how much there is. Everything else is shape.
2. `far` — where the resolution is spent. See above.
3. `phaseG` — 0 is an even glow, 0.7 is air, 0.9 is a searchlight beam.
4. `ambientColour` — the floor the fog never goes below. ⚠ Zero is not "no ambient", it is a valley
   that goes black whenever the sun is behind the viewer: a phase function is normalised over the
   sphere, so one directional light contributes almost nothing outside its forward peak. The
   shadowing multiplies the sun's term only, for the same reason — shaded air still sees the sky.
5. `slices` — raise it if a shadow edge crossing the volume bands, and lower it first if the feature
   costs too much. It is the one knob that moves the shadow work.

## See also

- [Working the frame after it is drawn](post-processing.md) — the analytic fog this stands in front
  of, and the rest of the chain.
- [The frame, as one node](standard-frame.md) — where the two nodes land and why.
- [Choosing how hard the frame works](render-quality.md) — the tier knobs.
- [Light that behaves like light](physical-lighting.md) — the units the sun colour is in.
