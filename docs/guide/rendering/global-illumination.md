---
title: The ambient split, and the nodes that fill it
slug: rendering/global-illumination
kind: guide
area: Rendering
summary: Why a lit frame is written to three targets with its diffuse ambient deliberately missing, what the screen probes, the surface cache and the traced reflections put into that hole, and the one node at the far end that adds it all back up.
api: [T:Vixen.Rendering.PostFx.ScreenProbeGatherAsset, T:Vixen.Rendering.PostFx.ReflectionsAsset, T:Vixen.Rendering.PostFx.AmbientCombineAsset, T:Vixen.Rendering.PostFx.AmbientCombineRenderer, T:Vixen.Rendering.Compositor.SurfaceCacheAsset, R:PostFx/AmbientCombine, T:Vixen.Shaders.Generated.AmbientCombineKeys, T:Vixen.Shaders.Generated.AmbientCombineConstants]
tags: [rendering, compositor, lighting, global-illumination, reflections]
since: 0.1
status: preview
related: [rendering/lit-path, rendering/standard-frame, rendering/render-quality, rendering/post-processing, rendering/physical-lighting]
---

## What it is

Four node kinds and one arrangement of render targets. The arrangement is the part worth
understanding first: with the split on, the shading pass stops writing one colour and writes three
planes, and the diffuse ambient it would have summed into the first one is **not written at all**.

| Target | What the shading pass puts in it |
|---|---|
| 0 — `SceneHdr` | Direct light, emissive, and the specular ambient |
| 1 — `SceneAlbedo` | Diffuse albedo in `rgb`, the material's own occlusion in `a` |
| 2 — `SceneNormals` | World normal in `xyz`, roughness in `a` |

That is `ForwardPlus.SplitOutputs` — a permutation on the forward pass, mirrored under the same key
by the visibility-buffer resolve so the two paths cannot disagree per pixel. The four nodes are what
fill the hole and close it:

| Node | What it does |
|---|---|
| `!ScreenProbeGather` | Place, trace, resolve, denoise, upsample — one node. Publishes screen irradiance over π |
| `!SurfaceCache` | The card atlas, kept captured, lit and bounced frame by frame — what a trace hit answers with |
| `!Reflections` | One traced reflection plane: radiance in `rgb`, **validity** in `a` |
| `!AmbientCombine` | Adds the frame back up |

The combine is one full-screen pass and one line of arithmetic:

```
combined = direct × sunVisibility + albedo × irradiance × occlusion, reflections blended over
```

`AmbientCombine.rvn` states every term's stand-in semantics beside it, and `AmbientCombineKeys` and
`AmbientCombineConstants` are the generated bindings the renderer writes those switches through.

## What it is for

A screen-space pass can only modulate a term that is still separate. An irradiance plane knows what
light arrives at a pixel; an occlusion plane knows how much of it survives the geometry around it;
neither can reach a number that a shading pass already added into one radiance value. Ambient is the
one term of the lit path that these passes have something to say about — so the frame is torn apart
at the pass that shades and put back together at the far end, and the tear is the whole reason the
combine exists as a node of its own rather than as a line inside a post-effect.

That is also why `!Reflections` needs the split even when nothing else does: the combine's weighted
blend is the only compositor the traced plane has. A frame with `reflections: Screen` and no split
would have a reflection buffer and no place to put it.

⚠ **The split is the material's decision as much as the document's.** The nodes here write and read
planes; whether the shading pass *produces* them is `ForwardPlus.SplitOutputs` in the material. A
document that names `!AmbientCombine` over a pass compiled without the split is a frame whose
`SceneAlbedo` and `SceneNormals` are whatever the clear left there, and the combine will faithfully
multiply by it.

⚠ **Nothing here is baked, and that is the trade.** Everything the probes gather, the cards remember
and the mirrors trace follows a scene that changes — a lamp switched on, a wall knocked down. For a
static scene that ships on a budget, a bake is cheaper and better. See
[Turning on dynamic global illumination](lit-path.md) for the layers underneath these: the distance
field every ray marches and the irradiance field the long rays terminate into.

## Using it

The tier route emits the whole arrangement. Two knobs on `!StandardFrame` decide it:

```yaml
!StandardFrame
  quality: High
  gi: Probes             # Off | Ambient | Probes
  reflections: Screen    # Off | Probe | Screen
```

| `gi:` | What is emitted |
|---|---|
| `Off` | No split at all. One colour target, ambient shaded in the material, none of these nodes |
| `Ambient` | The split, the occlusion pair (`!DistanceFieldAo` and `!Ssao`) and the combine — shaded ambient, honestly darkened |
| `Probes` | All of it: the clipmap, the irradiance field, `!SurfaceCache`, `!ScreenProbeGather`, the occlusion pair and the combine |

`reflections: Screen` adds `!Reflections` and, on its own, turns the split on for the same reason —
the combine is where a traced plane lands.

What the tier moves, from [Choosing how hard the frame works](render-quality.md):

| Knob | Low | Medium | High | Epic |
|---|---|---|---|---|
| `gi.probeTileSize` — pixels per probe | 32 | 16 | 16 | 8 |
| `gi.screenTraces` — march the depth buffer first | off | off | on | on |
| `gi.irradianceBudget` — bricks refilled per frame | 2 | 4 | 8 | 16 |
| `reflections.screenSteps` | 16 | 24 | 32 | 64 |
| `reflections.roughnessThreshold` | 0.5 | 0.5 | 0.5 | 0.5 |

⚠ **`screenTraces` is a choice, not a quality slider**, which is why it is a boolean and off on the
cheap tiers rather than a count. The probes' origins are a placement one `latency` old and the rays
march *this* frame's depth: identical for a still scene, sheared by one frame of motion for a moving
one. The denoiser owns that shear, and turning the stage on is deciding you want what the depth
buffer can see at the price of what the reprojection then has to hide.

### The half a document cannot say

Each of these nodes owns placement and numbers. What it deliberately does not own is anything that
outlives a frame — a card atlas, readbacks in flight, a probe history, an effect system, a device.
Those come from the host, through `CompositorBuilder`:

| Node | What the host supplies |
|---|---|
| `!ScreenProbeGather` | `ScreenProbeTracer`, `ScreenProbeResolver`, `ScreenProbeAccumulator`, `ScreenProbeFilter`, and `TracePyramid` for the screen march |
| `!SurfaceCache` | `SurfaceCache` and its capture, radiosity and two fills |
| `!Reflections` | `Effects` for the kernel's variants, `TracePyramid`, and the composed sources on the node's own trace |

```csharp no-compile="the builder belongs to the host that drives the compositor; see SceneRenderHost"
builder.ScreenProbeTracer = tracer;
builder.SurfaceCache = store;
```

⚠ **A node built with none of that supplied does nothing rather than throwing.** That is the same
answer every field node in [the lit path](lit-path.md) gives, and it is what makes one document
portable across builds that pay for the machinery and builds that do not. It is also why a frame
that produces no indirect light at all is worth checking against the host before the document: the
file can only state the first half of the fact.

⚠ `TracePyramid` is **taken, not read**. `!ScreenProbeGather` with `screenTraces: true` and
`!Reflections` are both takers, and the builder counts them so one nearest chain under both marching
nodes gets a descriptor ring deep enough. A host does not have to remember how many nodes it wired.

### Every plane past the first three is optional

The combine's `direct:`, `albedo:` and `normals:` are the split pass's own targets and are required.
Everything after them is optional, and absence has a *stated* answer rather than an undefined one:

| Seat | Left empty |
|---|---|
| `irradiance:` | Contributes no ambient at all |
| `occlusion:` | Occlusion and sun visibility both read as one — fully open, fully lit |
| `contactOcclusion:` | Reads as one |
| `reflections:` | Blends none in |
| `depth:` | The AO planes are read with a plain linear tap instead of a bilateral upsample |

The mechanism is worth knowing because it looks like a bug in a capture. A descriptor set is written
wholly or not at all, so the renderer binds *something* into every slot — the direct plane, which is
bound already — and a `use…` uniform at zero is what actually makes the term read as off. So a frame
where `occlusion:` is empty has the direct plane bound to the occlusion binding, and none of its
texels are part of the answer.

⚠ **Sun visibility multiplies DIRECT and occlusion multiplies AMBIENT.** That is
`!DistanceFieldAo`'s channel rule, kept here: its plane carries occlusion in `r` and sun visibility
in `g` precisely so a consumer can send them to different terms. Pre-combining them into one
darkness is the mistake that makes a scene look dirty rather than grounded.

⚠ **Where a shadow map already shadows the sun, set `sunShadow: false` on the AO node.** The
double-application guard is that knob, not arithmetic in the combine — a cone traced toward the sun
on top of a cascade lookup shadows the sun twice, softer and wronger. With it off the `g` channel
stays one and nothing is applied twice.

⚠ **One AO march, not two.** The albedo plane's alpha carries the material's *own* occlusion, and
the combine multiplies that and the AO planes into the same ambient. A material compiled with its
own clipmap march writes the room's occlusion into that alpha, and the result is the room occluded
squared. Pick the node or the material, not both.

⚠ **`!IndirectDiffuse` and `!ScreenProbeGather` publish the same kind of plane, and running both is
the same skylight added twice.** They are alternatives, not layers: the gather has real traces
behind it where the screen pass has only the probe volumes. Name one in `irradiance:`.

### Reflections are blended by reflectance, not by validity

The reflection plane's alpha means *the trace answered here*. It says nothing about how much of that
radiance a surface actually sends to the eye — so the combine weighs the blend by the surface's own
specular reflectance, `Ibl.EnvironmentDfg` against the normals plane's roughness and the view angle,
at a dielectric `f0`.

⚠ **Which is why `view:` is not decoration on this node.** A dielectric reflects about four per cent
head-on and nearly everything at a grazing angle, and the view ray comes out of the camera's
unprojection. Absent a camera the shader weighs every surface at normal incidence — a dimmer
reflection, never a runaway one — and that is the deliberate fallback rather than trusting an
identity matrix, which would invent grazing angles everywhere and make the frame a mirror.

⚠ **A metal reflects here as though it were paint.** The split carries a normal and a roughness and
no metalness, so `f0` is fixed and dielectric: too dim, and dimmer the more metallic the surface.
This is a floor, not an estimate, and the honest fix is a specular-ambient term of its own for the
reflection to replace.

### The bilateral upsample lives in the combine

The AO pair usually runs at half resolution, and the combine is the one place it meets the
full-resolution frame — so the upsample is here rather than in a pass of its own. Naming `depth:`
and `view:` turns it on: each AO plane is read as its four nearest texels weighted by the probe
upsample's plane test, with `planeTolerance` in metres deciding how far off a pixel's plane a
reduced texel may stand and still count.

Without it, a linear tap at a depth edge averages the two surfaces that meet there, and the symptom
is occlusion standing a texel or two off the corner that earned it. With a depth bound but no
camera, every tap's surface is reconstructed at a place that exists nowhere, the plane test rejects
everything, and it falls back quietly to the linear read it was meant to replace.

### Where the combine goes

⚠ **Before `!TemporalAntialiasing`, necessarily.** Half-lit radiance — direct with the ambient still
owed — is not a darker version of the finished frame, it is a *different image*, and an accumulator
handed it would blend that history into the finished frame at every disocclusion. The combine is
where the frame becomes whole, so it is the earliest point the accumulator is allowed to see.

⚠ **A resize is a camera cut for the gather.** Its lattice is sized on the first build and a resized
frame is refused rather than rebuilt mid-flight, because releasing textures that frames still
reference is a use-after-free with latency. The host idles the device and drives the reset through
the compositor's own resize; probe history, placement and readback rings all start over.

⚠ **`!SurfaceCache`'s `passes:` list is replaced, not added to.** Empty leaves the default, which is
the screen-probe trace alone — the pass whose hit branch composes the slot. A document that names
its own consumers means *those*, and dropping the default is how a frame ends up writing a cache
slot nothing reads every time the atlas turns over.

## Examples

The whole chain, hand-authored — the shape sample 13's frame uses:

```yaml
- !SurfaceCache
  name: Cache

- !ScreenProbeGather
  name: Gather
  depth: SceneDepth
  normals: SceneNormals
  output: ProbeIrradiance
  view: Camera
  tileSize: 16
  screenTraces: true
  planeTolerance: 0.02

- !Reflections
  name: Mirrors
  depth: SceneDepth
  normals: SceneNormals
  colour: SceneHdr
  target: Reflections
  view: Camera
  roughnessThreshold: 0.45
  screenSteps: 32

- !DistanceFieldAo
  name: Occlusion
  depth: SceneDepth
  normals: SceneNormals
  source: GlobalDistanceField
  output: AmbientOcclusion
  sunShadow: false
  view: Camera

- !Ssao
  name: ContactOcclusion
  depth: SceneDepth
  normals: SceneNormals
  view: Camera
  output: ScreenOcclusion
  scale: 0.5

- !AmbientCombine
  name: Combine
  direct: SceneHdr
  albedo: SceneAlbedo
  normals: SceneNormals
  irradiance: ProbeIrradiance
  occlusion: AmbientOcclusion
  contactOcclusion: ScreenOcclusion
  reflections: Reflections
  depth: SceneDepth
  view: Camera
  output: SceneCombined
```

⚠ `colour: SceneHdr` on `!Reflections` is deliberate scheduling rather than a default worth
copying blind. At that point in the frame `SceneHdr` holds the sky, the level and the particles, so
a screen hit reflects this frame's own lit opaques. Which colour a reflection samples — last frame's
lit buffer, this frame's opaque pass — is a decision a document makes by *where it puts the node*.

The occlusion-only frame, which is what `gi: Ambient` expands to. No probes, no cache, no traces —
the split exists purely so two occlusion planes have a term to darken:

```yaml
- !AmbientCombine
  name: Combine
  direct: SceneHdr
  albedo: SceneAlbedo
  normals: SceneNormals
  occlusion: AmbientOcclusion
  contactOcclusion: ScreenOcclusion
  depth: SceneDepth
  view: Camera
  output: SceneCombined
```

`irradiance:` and `reflections:` are empty and the frame is correct: no ambient rebuilt from a plane,
nothing blended over, and the shading pass's specular ambient still in `SceneHdr` where it always was.

### `intensity` is not an exposure knob

`intensity:` multiplies the rebuilt ambient alone, and one is the honest number. The meter and the
tonemap already own the frame's brightness — see
[Working the frame after it is drawn](post-processing.md) — and a value here that re-grades the
shade is a second exposure wearing a lighting term's clothes. It exists because artists ask for it,
and because a number that is not one is then visible in a capture rather than folded into a field
where it would corrupt what the next bounce reads.

## See also

- [Turning on dynamic global illumination](lit-path.md) — the clipmap and the irradiance field
  underneath all of this, and the host slots they need to do more than nothing.
- [The frame, as one node](standard-frame.md) — where `gi:` and `reflections:` put every node above.
- [Choosing how hard the frame works](render-quality.md) — the tier knobs, and how a project
  overrides them.
- [Working the frame after it is drawn](post-processing.md) — the chain the combine's output feeds.
- [Light that behaves like light](physical-lighting.md) — the units every plane here is measured in.
