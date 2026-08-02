<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 30 — Post-processing parity

⚠️ **Extends [06](06-rendering-pipeline.md).** Doc 06 names the frame's passes and lists depth of
field at P1 and motion blur at P2. This document is the audit behind those lines: every
post-processing effect Unreal Engine 5.8 and Unity HDRP 14 ship, what Vixen has against each, and
which we adopt.

**The claim this document has to earn.** A frame's look is authored in a `.vxcompositor` and nowhere
else, and every effect a shipping engine is expected to have is either a node kind a document can
name or a decision written down here about why it is not.

---

## Where the gap actually is

Thirteen post-effect renderers exist in `Vixen.Rendering.PostFx`. `PostEffectFactory` defines **five**
node kinds — `!Bloom`, `!Sky`, `!Tonemap`, `!DistanceFieldAo`, `!IndirectDiffuse`. The other eight
compile, reach both backends, and have no name a document can write. A frame can still reach them
through `!FullScreen` by spelling out the shader's bindings by hand, which is exactly what the node
kinds exist to stop.

So the headline number is misleading in both directions: we have more than we can use, and the
missing effects are mostly small.

---

## The table

`✅` shipped · `⚠️` partial · `❌` absent · `—` not applicable

| Effect | Unreal 5.8 | Unity HDRP 14 | Vixen today | Adopt |
|---|---|---|---|---|
| **Bloom** | Gaussian pyramid + convolution (FFT) | Pyramid, threshold, scatter, tint | ✅ pyramid, threshold, knee, filterRadius, levels; composited into the tonemap | ✅ add tint |
| **Bloom dirt mask** | texture, intensity, tint | texture, intensity | ❌ | ✅ |
| **Convolution bloom** | ✅ FFT kernel | ❌ | ❌ | ❌ |
| **Anamorphic bloom** | ❌ | ✅ | ❌ | ❌ |
| **Tonemap curve** | filmic: slope, toe, shoulder, black clip, white clip | None / Neutral / ACES / Custom / External | ⚠️ Reinhard, ACES, AgX; `operator: 3` documented as Uncharted, implemented as a clamp | ✅ implement the filmic curve |
| **Exposure (manual)** | EV compensation + physical camera | EV100 | ✅ `ev100` | ✅ add compensation |
| **Auto exposure** | histogram (64-bin, percentiles, mask) + basic | ✅ | ⚠️ log-average chain, speed up/down, min/max — UE's "Basic" | ✅ histogram later |
| **Local exposure** | ✅ bilateral / fusion | ❌ | ❌ | ❌ |
| **Colour grading model** | scene-referred CDL: sat/contrast/gamma/gain/offset × global/shadows/midtones/highlights | display-referred, several effects | ⚠️ contrast + saturation, **after** the curve | ✅ adopt UE's model, scene-referred |
| **Colour Adjustments** | (part of CDL) | post exposure, contrast, colour filter, hue shift, saturation | ⚠️ no colour filter, no hue shift | ✅ |
| **White Balance** | temperature + tint | temperature + tint | ⚠️ temperature only, as a lerp between two constants | ✅ |
| **Split Toning** | (part of CDL) | shadows, highlights, balance | ⚠️ no balance | ✅ add balance |
| **Lift Gamma Gain** | (part of CDL) | ✅ | ❌ | ❌ superseded by CDL |
| **Shadows Midtones Highlights** | (part of CDL) | ✅ | ❌ | ❌ superseded by CDL |
| **Channel Mixer** | ❌ | ✅ | ❌ | ❌ |
| **Colour Curves** | ❌ | ✅ 8 curves | ❌ | ❌ |
| **Grading LUT** | scene-referred, log-encoded | log-encoded (External mode) | ⚠️ display-referred, no `.cube` importer | ✅ importer; keep display-referred as a trim |
| **Depth of Field** | cinematic: sensor, focal length, f-stop, blade count, focus tracking | physical camera or manual ranges | ❌ | ✅ |
| **Physical camera** | sensor size, focal length, f-stop, shutter, ISO | ✅ | ⚠️ `Photometry.Ev100FromCamera` exists and nothing supplies it | ✅ |
| **Motion Blur** | ✅ | intensity, samples, min/max velocity, clamps | ✅ gather, shutter-driven | ✅ |
| **Motion vectors** | ✅ | ✅ | ✅ a stage, not a post-process | ✅ prerequisite |
| **Vignette** | intensity | colour, centre, intensity, smoothness, roundness, mask mode | ⚠️ intensity + smoothness | ✅ finish |
| **Film Grain** | intensity per tonal range, texel size, texture | type/texture, intensity, response | ⚠️ intensity, scale, luminance-weighted | ✅ expose as a node |
| **Chromatic Aberration** | intensity, start offset | spectral LUT, intensity, max samples | ⚠️ intensity | ✅ expose as a node |
| **Lens Distortion** | ❌ | intensity, x/y, centre, scale | ❌ | ✅ |
| **Lens Flare** | intensity, tint, bokeh size/shape, threshold | screen-space | ❌ | ❌ |
| **Panini Projection** | ✅ | distance, crop to fit | ❌ | ❌ |
| **Custom effects** | Post Process Materials, 4 blendable locations | custom passes | ✅ `!FullScreen` at any point in the graph | — already the general case |
| **FXAA / TAA / sharpen / fog / outline / SSAO** | ✅ | ✅ | ✅ shaders + renderers, **no node kind** | ✅ node kinds |

---

## Why we are not adopting the rest

**Convolution bloom.** An FFT against a kernel texture is the physically real answer and it needs a
kernel somebody authored, an FFT implementation and a resolution-dependent buffer region. The pyramid
is what both Unity pipelines ship and it is indistinguishable at gameplay framing.

**Anamorphic bloom.** A non-square kernel is cheap, but it only means anything with an anamorphic
lens model on the camera, which we do not have and which nothing has asked for.

**Local exposure.** UE5's bilateral/fusion tone compression is genuinely good and genuinely
research-grade. It belongs after auto-exposure has a histogram, not before.

**Lift Gamma Gain, Shadows Midtones Highlights, Channel Mixer, Colour Curves.** All four are ways of
authoring a colour transform, and UE's CDL decomposition — saturation, contrast, gamma, gain, offset
per tonal range — expresses the first two exactly and the other two well enough. Adopting four
overlapping models because Unity ships four is how a grading stack becomes unexplainable. ⚠ Colour
Curves is the one real loss: hue-vs-hue and hue-vs-sat cannot be written as a CDL. That is what a
grading LUT is for, which is why the importer is adopted.

**Lens flare.** Image-based flares need authored bokeh shapes and ghost tables, and screen-space
flares need a threshold pass of their own. High content cost, no engine argument.

**Panini projection.** A cylindrical reprojection that matters above about 90° of field of view.
Nothing in the engine's samples goes near that, and a game that needs it needs it as a projection
matrix rather than as a post-process.

**Post Process Materials.** Nothing to build. UE offers four hard-coded insertion points; a
`.vxcompositor` runs an arbitrary shader wherever the node is written, which is the general case of
the same idea.

---

## The two findings behind the adoptions

### Grading belongs before the curve

UE grades in **scene-referred linear space, before tone mapping**, and calls display-referred lookup
tables legacy. HDRP bakes its whole grading chain into a log-encoded LUT, which is the same claim.
`Tonemap.rvn` currently runs:

```
exposure → white balance → curve → contrast, saturation, split toning → LUT → transfer
                                   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
```

White balance is scene-referred and agrees with both references. Everything else is after the curve,
which neither reference does — and the LUT is sampled through `saturate()`, so it is display-referred
by construction and cannot hold a grade that touches values above white.

The shader's own comment defends the order: a curve authored on display-referred values behaves
predictably, and grading in HDR means the same curve behaves differently depending on scene
brightness. That argument is sound **for a LUT or a curve** and is not the argument either engine
makes for contrast and saturation. The resolution is to split the two: grade scene-referred, and keep
the LUT where it is as a final display-referred trim.

### The physical camera is one set of numbers, used twice

UE derives field of view, exposure *and* defocus from sensor size, focal length, f-stop, shutter and
ISO. `Photometry.Ev100FromCamera` already takes an f-number, a shutter time and an ISO, and nothing
in the engine supplies them — so the exposure half is built and unreachable, and depth of field would
otherwise arrive with an aperture of its own. One component feeding both is the difference between a
physically based camera and a blur slider next to an exposure slider.

---

## Order of work

| Step | Item | Size | State |
|---|---|---|---|
| 1 | Node kinds for the eight renderers that already exist | S | ✅ |
| 2 | Move grading before the curve | S | ✅ |
| 3 | Colour filter, hue shift, split-toning balance | S | ✅ |
| 4 | White balance: CIE temperature **and** tint, from `Photometry.FromTemperature` | S | ✅ |
| 5 | Filmic tonemap curve | S | ✅ Hable's, and `operator: 3` stops lying |
| 6 | Finish vignette: colour, centre, roundness | S | ✅ |
| 7 | `.cube` importer → `Texture3D` | S | ✅ and `AssetTextureSource` builds a 3D texture now |
| 8 | Physical camera; exposure reads it | M | ✅ folded into `Camera` itself — one component, and `fieldOfView` is a view onto `focalLength` |
| 12 | CDL grading per tonal range | M | ✅ |
| 13 | Bloom tint and dirt mask; lens distortion | S | ✅ |
| 9 | Depth of field, physical mode | L | ✅ gather-based, physical only — no manual mode by design |
| 10 | Motion-vector pass | M | ✅ a stage with a shader override, as the shadow pass already is |
| 11 | Motion blur | M | ✅ directional gather, shutter off the camera, no intensity |
| 14 | Histogram auto-exposure | M | ⬜ the log-average chain is UE's "Basic" |

### What the last three need, and why they are not with the rest

Everything above is a full-screen pass and a node kind: a shader reading textures the frame already
has, and a record describing it. **Steps 10 and 11 are not that.** A motion vector is the difference
between where a vertex is and where it was, so producing one means every shading and depth shader
gaining a previous-frame clip position, `TransformRenderFeature` keeping a second matrix per object
and a second ring to hold it, the frame declaring another colour target, and the geometry pass writing
it. That is the material path rather than the post-processing one, and it is owed to `Taa.rvn` — which
declares a motion-vector input and has never had one — as much as to motion blur.

Step 14 is self-contained but is a rewrite rather than an addition: a 64-bin histogram, a percentile
reduction and a metering mask replace the log-average chain rather than extending it, and the value it
adds over the chain is stability in scenes with a bright sky or a dark doorway, which this engine has
no test scene for yet.

⚠ **Ordering in a document is explicit and can therefore be wrong.** UE and Unity both hide the
order inside an uber pass. A `.vxcompositor` does not, so the rules have to be written next to the
node kinds: TAA runs before the tonemap, grain and vignette after the curve, FXAA last on LDR, and
bloom is sampled by the tonemap rather than composited by a pass of its own.
