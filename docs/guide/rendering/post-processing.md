---
title: The post-processing node kinds
slug: rendering/post-processing
kind: guide
area: Rendering
summary: Every screen-space effect a compositor document can name, what each one reads, and the order they have to run in.
api: [T:Vixen.Rendering.PostFx.PostEffectFactory, T:Vixen.Rendering.PostFx.BloomAsset, T:Vixen.Rendering.PostFx.TonemapAsset, T:Vixen.Rendering.PostFx.SkyAsset, T:Vixen.Rendering.PostFx.FxaaAsset, T:Vixen.Rendering.PostFx.TemporalAntialiasingAsset, T:Vixen.Rendering.PostFx.SharpenAsset, T:Vixen.Rendering.PostFx.VignetteAsset, T:Vixen.Rendering.PostFx.FogAsset, T:Vixen.Rendering.PostFx.OutlineAsset, T:Vixen.Rendering.PostFx.SsaoAsset, T:Vixen.Rendering.PostFx.AutoExposureAsset, T:Vixen.Rendering.PostFx.DistanceFieldAoAsset, T:Vixen.Rendering.PostFx.IndirectDiffuseAsset, R:PostFx/Tonemap, R:PostFx/Vignette]
tags: [rendering, post-processing, compositor]
since: 0.1
status: stable
related: [rendering/physical-lighting]
---

## What it is

Thirteen screen-space effects, each a node a `.vxcompositor` names and configures. Register the
factory once and a document can say `!Bloom`:

```csharp no-compile="the builder is the host's; see SceneRenderHost"
builder.Factories.Add(new PostEffectFactory());
```

| Node | Reads | Publishes |
|---|---|---|
| `!Sky` | the host's environment cube | fills an existing colour target |
| `!Ssao` | depth, normals, a view | occlusion |
| `!DistanceFieldAo` | depth, normals, a distance field | occlusion, and a sun shadow |
| `!IndirectDiffuse` | depth, normals, an irradiance field | bounced light |
| `!AutoExposure` | scene colour | a one-element buffer, on the device |
| `!TemporalAntialiasing` | colour, motion vectors, depth | a resolved image, and next frame's history |
| `!Fog` | colour, depth, a view | fogged colour |
| `!Bloom` | colour | the pyramid above its threshold |
| `!Tonemap` | colour, a pyramid, a table, an exposure buffer | display-referred colour |
| `!Outline` | colour, depth, normals, a mask | colour with edges drawn |
| `!Vignette` | colour | colour, with the lens's imperfections |
| `!Fxaa` | colour | an antialiased image |
| `!Sharpen` | colour | a sharpened image |

## What it is for

A frame's look, authored in a document rather than assembled in C#. Every one of these shaders
existed before it had a node kind, and reaching them meant writing a `!FullScreen` with the shader's
binding indices spelled out by hand — so no project ever did, and the effects were dead weight.

You do not want a node for an effect that is one pass of a chain somebody else composites. `!Bloom` is
a node because the shape of its nine passes follows from one number; a single blur is not.

## Using it

⚠ **The order is explicit, so it can be wrong.** Unreal and Unity both hide this inside an uber pass;
a compositor document does not. Four rules, and every one of them has a symptom rather than an error:

1. **`!TemporalAntialiasing` runs before `!Tonemap`.** It blends this frame with the last, and
   blending display-referred values blends two different curves' outputs — a scene that changes
   exposure ghosts.
2. **`!Vignette` runs after `!Tonemap`.** Grain is a fixed amount added to a number, so on
   scene-referred light it is invisible in shadow and enormous in highlights.
3. **`!Fxaa` runs last, on display-referred colour.** It finds edges by luminance contrast, and
   contrast in scene light is unbounded — every threshold in the shader would be meaningless.
4. **`!Bloom` is sampled by the tonemap, not composited by a pass.** See below.

A minimal end of frame:

```yaml
- !Bloom
  name: Glow
  source: SceneHdr
  output: BloomPyramid
  threshold: 3000.0
  knee: 1500.0

- !Tonemap
  name: Tonemap
  source: SceneHdr
  bloom: BloomPyramid
  output: SceneColour
  operator: 1
  ev100: 13.0

- !Vignette
  name: Lens
  source: SceneColour
  output: Lensed
  grainIntensity: 0.04

- !Fxaa
  name: Fxaa
  source: Lensed
  output: Display
```

⚠ **A node that unprojects a depth buffer needs a `view:`.** `!Ssao` and `!Fog` reconstruct positions
from depth, and without a camera they use an identity matrix — which unprojects every pixel to the
same place and produces occlusion or fog that is smooth, plausible and completely wrong. `!Sky` has
the same requirement for the same reason.

## Examples

**Three effects, one node.** `!Vignette` carries vignette, chromatic aberration and film grain,
because they are one shader — always applied together, at the very end, each a few instructions. Three
full-screen passes would cost three times the bandwidth to save nothing. Each is behind its own
permutation, so a document that wants only grain compiles only grain:

```yaml
- !Vignette
  name: Lens
  source: SceneColour
  output: Display
  useVignette: false
  useChromaticAberration: false
  grainIntensity: 0.06
```

**Auto-exposure publishes a buffer, not a texture.** The reduction produces the number on the device
and the tonemap consumes it there, so a `!Tonemap` picks it up by naming the resource rather than by
naming the node:

```yaml
- !AutoExposure
  name: Exposure
  source: SceneHdr
  brightenRate: 3.0
  darkenRate: 1.0
```

A host that read it back to set `exposure` would pay a stall and a frame of latency for a value it
never looks at, which is the whole reason that pass is compute.

⚠ Auto-exposure and `ev100` are alternatives, not layers. A scene lit in lux and lumens usually wants
a fixed exposure value — see [lighting a scene in lux and lumens](physical-lighting.md).

**The one node with no shipped producer.** `!TemporalAntialiasing` needs a motion-vector texture and
**nothing in the engine writes one yet**; `docs/plan/30` tracks it. The node exists and is correct;
a frame that names it has to supply the texture itself.

## See also

- [Lighting a scene in lux and lumens](physical-lighting.md) — what `ev100` and the grade are in.
- `docs/plan/30-post-processing-parity.md` — the audit against Unreal and HDRP, and what is
  deliberately not here.
