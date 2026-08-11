---
title: The post-processing node kinds
slug: rendering/post-processing
kind: guide
area: Rendering
summary: Every screen-space effect a compositor document can name, what each one reads, and the order they have to run in.
api: [T:Vixen.Rendering.PostFx.PostEffectFactory, T:Vixen.Rendering.PostFx.BloomAsset, T:Vixen.Rendering.PostFx.TonemapAsset, T:Vixen.Rendering.PostFx.SkyAsset, T:Vixen.Rendering.PostFx.FxaaAsset, T:Vixen.Rendering.PostFx.TemporalAntialiasingAsset, T:Vixen.Rendering.PostFx.SharpenAsset, T:Vixen.Rendering.PostFx.VignetteAsset, T:Vixen.Rendering.PostFx.FogAsset, T:Vixen.Rendering.PostFx.OutlineAsset, T:Vixen.Rendering.PostFx.SsaoAsset, T:Vixen.Rendering.PostFx.AutoExposureAsset, T:Vixen.Rendering.PostFx.DepthOfFieldAsset, T:Vixen.Rendering.PostFx.DepthOfFieldRenderer, R:PostFx/DepthOfField, T:Vixen.Rendering.PostFx.MotionBlurAsset, T:Vixen.Rendering.PostFx.MotionBlurRenderer, R:PostFx/MotionBlur, T:Vixen.Rendering.PostFx.LocalExposureAsset, T:Vixen.Rendering.PostFx.LocalExposureRenderer, R:PostFx/LocalExposure, T:Vixen.Rendering.PostFx.LensFlareAsset, T:Vixen.Rendering.PostFx.LensFlareRenderer, R:PostFx/LensFlare, R:PostFx/AutoExposure, T:Vixen.Rendering.PostFx.AutoExposureRenderer, R:Pipeline/MotionVectors, T:Vixen.Rendering.Features.MotionVectorRenderFeature, T:Vixen.Rendering.PostFx.DistanceFieldAoAsset, T:Vixen.Rendering.Compositor.IResizeTarget, T:Vixen.Rendering.PostFx.IndirectDiffuseAsset, T:Vixen.Rendering.ColorGrading, T:Vixen.Rendering.ColorGradingRange, T:Vixen.Editor.Assets.Textures.CubeLut, T:Vixen.Editor.Assets.Textures.CubeLutImporter, T:Vixen.Editor.Assets.Textures.CubeLutImportSettings, R:PostFx/Tonemap, R:PostFx/Vignette]
tags: [rendering, post-processing, compositor]
since: 0.1
status: stable
related: [rendering/physical-lighting, rendering/post-process-volumes, rendering/reading-the-frame, rendering/volumetric-fog]
---

## What it is

Seventeen screen-space effects, each a node a `.vxcompositor` names and configures. Register the
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
| `!DepthOfField` | colour, depth, a view's lens | defocused colour |
| `!MotionBlur` | colour, motion vectors, a view's shutter | smeared colour |
| `!LocalExposure` | colour, an exposure value | colour, re-exposed per region |
| `!LensFlare` | colour, a view's blade count | colour, with ghosts and a halo |
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
4. **`!MotionBlur` runs before `!Bloom`.** A smear is light landing on the sensor over an interval,
   so averaging it is averaging radiance — and the glow has to be built from the image the shutter
   actually recorded, not from a highlight that was only there for part of it.
5. **`!LocalExposure` and `!LensFlare` run before `!Bloom` too, and in that order.** Both are
   scene-referred: local exposure moves radiance around before the curve has to shape it, and a
   flare is light arriving at the sensor and has to be able to blow out. The flare is built from the
   locally exposed image rather than the other way round, because a ghost's brightness should follow
   what the sensor actually recorded.
4. **`!Bloom` is sampled by the tonemap, not composited by a pass.** See below.
5. **`!DepthOfField` runs before `!Bloom`.** Defocus is scene-referred — the lens spreading light
   across the sensor — and the glow has to be built from the image the lens actually focused, not
   from highlights that were never there.

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

### The two halves temporal antialiasing needs from outside itself

`!TemporalAntialiasing` cannot work alone, and both of the things it needs are now shipped — this
paragraph used to say neither was.

**Motion vectors.** `MotionVectorRenderFeature` and `Pipeline/MotionVectors.rvn` write them, and
`WorldRenderer.Motion` is where the feature lives; a frame supplies the texture by declaring a pass
over the `Motion` stage. Sample 13's `Velocity` pass is the worked example — after the main pass and
sharing its depth read-only, so the velocity written for a pixel belongs to whatever ended up visible
there.

**The camera's sub-pixel offset.** The resolve averages samples taken at different points inside the
pixel, and taking them is the camera's job. `AppGraphics` sets `CameraExtractionSystem.JitterTarget`
to the frame's size whenever the tree has a `!TemporalAntialiasing` in it, and
`CameraMath.SubpixelJitter` is the sequence — eight Halton offsets that repeat, so a still camera
converges to an exact answer instead of chasing offsets it has never seen.

⚠ **A host driving a `RenderView` by hand gets neither.** The editor's viewport sets
`RenderView.ViewProjection` directly, so it never advances `PreviousViewProjection` and never carries
an offset; a temporal resolve there blurs rather than supersamples. That is the case the pass's own
remarks describe — *a frame that gets blurrier and no sharper* — and it is what every frame in this
engine did until the jitter was wired.

⚠ **Do not offset the camera in a tree with no temporal resolve in it.** A jittered camera that
nothing accumulates is a frame that shakes by half a pixel and buys nothing for it, which is why the
switch is the presence of the node rather than a setting.

## Defocus comes off the lens, and only off the lens

`!DepthOfField` has no manual mode, and that is the decision worth knowing about. Unreal and HDRP
both offer one beside the physical mode; this does not, because an aperture that sets the exposure
and a blur radius typed next to it are two answers to one question.

```yaml
- !DepthOfField
  name: Defocus
  source: SceneHdr
  depth: SceneDepth
  view: Camera
  samples: 16
```

Every number it uses — focal length, aperture, focus distance, blade count, sensor width — comes off
that view's `Camera`, which is the physical one and the only one. See
[lighting a scene in lux and lumens](physical-lighting.md#the-camera-is-the-other-end-of-the-same-arithmetic).

⚠ **A camera with no lens leaves the frame sharp**, as does one focused at infinity. That is the
honest answer rather than a guessed default: a soft frame in a project that never asked for depth of
field is worse than none.

⚠ **And the default camera barely blurs even when you do focus it.** `Camera.Perspective` frames 60°,
which is a 20.8 mm ultra-wide, and depth of field falls with the square of the focal length — at
f/2.8 it is sharp from 2.6 m to 180 m. Reach for `Camera.WithLens(50f)` or `85f` and the same
aperture holds a metre or half of one. See
[two conventions, and both are named](physical-lighting.md#two-conventions-and-both-are-named).

⚠ **It is a gather, so a blurred foreground does not spill over a sharp background.** Each pixel
collects from its neighbours weighted by *their* blur, which handles a sharp subject on a soft
background correctly and cannot fully handle the reverse — the spill stops at the silhouette. The fix
is a separate near field composited over the far one, which is two more passes; this is one, and says
so in the shader.

## Two meters, and which one a frame wants

`!AutoExposure` ships both of Unreal's, and they answer different questions.

**The chain** — the default — halves the frame to one texel and takes the geometric mean of its log
luminance. That is a good number for a scene of roughly uniform brightness. It is a bad one for the
two cases exposure exists for: a dark room with a bright window, and a bright street with a dark
doorway. The mean sits between the two populations and exposes for neither.

**The histogram** — `useHistogram: true` — bins the frame's luminance into 64 bins and takes the mean
of the bins between two percentiles. A percentile is a rank rather than a sum, so it can throw the
window and the doorway away entirely, which is what a spot meter does:

```yaml
- !AutoExposure
  name: Meter
  source: SceneHdr
  useHistogram: true
  lowPercentile: 0.5
  highPercentile: 0.95
  meteringPower: 1.0
```

⚠ **Bin 0 is the floor, not a bin.** Everything at or below `minimumLogLuminance` lands in it and the
resolve skips it — because a frame with a large area of true black would otherwise have its median
dragged into the floor and expose the whole scene for the black, which is the average with extra
steps.

⚠ **`meteringPower` is centre weighting, not a mask.** Unreal takes a texture; this takes one number,
because a mask needs authoring per scene and the case anybody reaches for is "stop the sky at the top
of the frame underexposing the subject in the middle of it". Zero meters evenly.

⚠ **It is three dispatches whatever the frame's size is** — a clear, a build and a resolve — against
the chain's one per halving. The clear cannot be folded into the build: a build invocation cannot
clear "its" bin, because a bin belongs to a luminance rather than to a pixel and every invocation is
racing every other one for all of them.

## Local exposure, which is the one a single number cannot do

A frame with a sunlit window and an unlit interior has ten or twelve stops between the two, and a
tone curve has about six to spend. Whatever the meter picks, one of them is white or black. That is
what a camera does; it is not what an eye does, because an eye adapts locally.

`!LocalExposure` blurs the log luminance into a *base* — the slow, large-scale brightness of each
region — compresses that, and leaves the *detail* alone:

```yaml
- !LocalExposure
  name: Adapt
  source: SceneHdr
  output: SceneAdapted
  ev100: 13.0
  highlightContrast: 0.35
  shadowContrast: 0.25
  edgeRange: 1.5
```

⚠ **`ev100` has to agree with the tonemap's**, or name the same `view:` both do. The pivot is the
luminance that stays exactly where it is, and it belongs wherever the meter says middle grey is. Set
it somewhere else and the node is a global exposure change wearing a local one's clothes.

⚠ **`edgeRange` is what decides whether there are halos.** The blur is bilateral: a tap counts less
the further its luminance is from the centre's, in stops. Too wide and the window bleeds across its
frame so the wall beside it is compressed as though it were bright — the dark ring people call the
HDR halo. Too narrow and the base follows every edge, leaving no large-scale brightness to compress.

⚠ **Compressing the image rather than the base is the classic mistake**, and it is what produces the
flat grey look everybody recognises from bad HDR photography: it compresses the texture along with
the brightness. Here the base moves and the detail does not.

## Lens flare, which is not bloom

Bloom is light scattered a short way from where it landed. A flare is light that reflected off the
back of one lens element and the front of another, so it lands somewhere else entirely — and where it
lands is not arbitrary. A ghost of a highlight sits on the line from that highlight *through the
centre of the frame*, which is why the whole effect is one vector and a list of scale factors.

```yaml
- !LensFlare
  name: Flare
  source: SceneHdr
  view: Camera
  output: SceneFlared
  threshold: 40000.0
  ghosts: 4
  useStarburst: true
```

⚠ **`threshold` is in the source's units.** In a physically lit frame that is cd/m², where nothing is
near one — so the default of one flares the floor. The same argument `!Bloom`'s threshold makes.

**`view:` gives the starburst the camera's blade count**, which is the same diaphragm that shapes the
bokeh in `!DepthOfField`. One lens, two effects; a number typed here and a different one on the
camera would be one lens with two diaphragms.

**The chromatic offset is not decoration.** A lens is corrected for one wavelength, and a ghost forms
at surfaces coated for transmission rather than reflection — so a real ghost fringes at its edge.
Sampling the three channels at three radii along the vector to the centre is what produces it, and a
ghost without it reads as a flat coloured blob.

## Motion vectors, which are not a post-process

`!MotionBlur` and `!TemporalAntialiasing` both read a texture saying where each pixel was last frame,
and nothing in a post chain can produce one: the answer needs the geometry, not the image. A frame
gets one by drawing the scene a second time with a stage that overrides the shader — exactly the way
a shadow map already does:

```yaml
resources:
  # ⚠ A signed float format. A vector points either way and a fast pan moves a pixel a long way, so
  # an unsigned format folds half the screen's motion onto the other half.
  - name: SceneMotion
    format: Rg16Float
    usage: ColourTarget, Sampled

stages:
  # ⚠ TestOnly. It runs after the shading pass and shares its depth, so a fragment that lost the
  # test is behind something else and has no business writing that pixel's velocity.
  - name: Motion
    shader: MotionVectors
    depth: TestOnly
```

```yaml
- !RenderPass
  name: Velocity
  colourTargets: [SceneMotion]
  depthTarget: SceneDepth
  depthLoad: Load
  readOnlyDepth: true
  children:
    - !SingleStage
      name: MotionVectors
      view: Camera
      stage: Motion
```

⚠ **And the host has to extract into it**, the same line a shadow stage needs, because a document
decides where a stage is drawn and cannot decide what an object is extracted as:

```csharp no-compile="config is the host's; see GraphicsOptions"
config.Graphics.CasterStages.Add("Motion");
```

Miss that line and the pass draws nothing, which is a target of zeroes, a motion blur that is a copy
and a temporal resolve with its first defence silently removed — with every counter in the run
reporting that the pass ran.

**What the pass costs and what it buys.** Writing motion out of the shading pass would be nearly
free, since those vertices are already being transformed — but it would mean every material shader
gaining a second clip position and a second output, the visibility-buffer resolve gaining the same,
and every existing variant recompiling. Unreal draws a velocity pass for the same reason. What this
costs is one more pass over the geometry with the cheapest shader in the library.

⚠ **A skinned mesh reports its root's motion, not its limbs'.** The palette is this frame's, so the
inside of a running character's silhouette is approximate. Doing better means holding a second bone
palette for a frame — a page of streaming per skinned mesh — and the error is confined to the inside
of a silhouette that is already moving. The silhouette itself, which is what a temporal resolve
rejects history across, is correct.

## Grading, and which side of the curve it is on

The tonemap carries two grades, and they are on opposite sides of the curve on purpose.

**`ColorGrading` is scene-referred and runs before it.** Saturation, contrast, gamma, gain and offset
— the ASC colour decision list — applied globally and then over three luminance ranges. It is
Unreal's decomposition, and it replaces four separate Unity effects: Lift/Gamma/Gain,
Shadows/Midtones/Highlights, Channel Mixer and Colour Curves are all ways of authoring a colour
transform, and adopting four overlapping models is how a grading stack becomes unexplainable.

```csharp no-compile="the renderer is the compositor's; Grading is null for no grade at all"
tonemap.Grading = ColorGrading.Neutral with {
    Shadows = ColorGradingRange.Neutral with { Gain = new(0.9f, 0.95f, 1.2f) },
    HighlightsMin = 0.6f
};
```

⚠ **`ColorGradingRange.Neutral`, not `default`.** A zeroed range is a saturation of zero and a gain of
zero, which is a black greyscale image — the same trap `Camera.Perspective` and `ControlRotation`
exist to avoid.

⚠ **The three ranges are a partition**: their weights sum to one at every luminance, so setting all
three to the same values is the same picture as setting `Global` to them. Without that property four
controls fight over the same pixels.

⚠ **The constants are scene-referred**, so `Gamma` is a power on unbounded radiance and 1 means "leave
it alone" rather than "display gamma", and contrast pivots around 0.18 in log space rather than 0.5
linearly. A range copied from a display-referred tool is roughly right and not exactly right — the
trade for a grade under which SDR and HDR output agree.

**A `.cube` table is display-referred and runs after it.** `CubeLutImporter` reads the format every
colour suite exports — Resolve, Baselight, Nuke, Photoshop — into the `Texture3D` the tonemapper has
sampled since it was written, and which no project could previously author:

```yaml
- !Tonemap
  name: Tonemap
  source: SceneHdr
  lut: Assets/Looks/evening.cube
  output: SceneColour
```

⚠ It ships with **no mips and no compression**, and neither is an oversight. A mip of a colour
transform is a *different* colour transform — averaging two entries of a grade is not the grade
halfway between them — and block compression works because neighbouring texels in a picture are
similar and the eye forgives the error, neither of which holds for a table every pixel of the frame
indexes through.

⚠ The table is what expresses the one thing the decision list cannot: hue-versus-hue and
hue-versus-saturation are not a CDL, and no combination of five per-channel operations is one.

## When the window resizes

Most of the nodes on this page need nothing from you when the frame changes size. A post effect
declares its output as a graph transient sized from `frame.Size` on every build, so a resized frame
simply declares a differently sized texture; `!Bloom`, `!AutoExposure` and `!LensFlare` rebuild their
whole mip chain from the new size the same way. The ones that keep a device texture between frames —
`!TemporalAntialiasing`'s history pair, `!Reflections`' output and its Hi-Z chain, `!Fog`'s froxel
volumes — compare the extent they allocated against the one they were handed, reallocate when it
moved, and drop whatever they had accumulated. Destroying a texture inside `Build` is safe because
the RHI's `Destroy` retires rather than frees: the memory comes back when the frame that referenced
it has finished, not when the call returns.

`!ScreenProbeGather` is the exception, and the reason `IResizeTarget` exists. Its lattice is not a
texture but a *shape*: an atlas layout, a mirror, a history and a CPU reconstruction surface are all
constructed against one viewport, and a probe's patch in the atlas is addressed by a grid derived
from it. Rebuilding that from inside `Build` would swap objects that this frame's descriptor sets
already name, so the node refuses a frame of the wrong size instead — loudly, with the size it laid
out and the size it was given.

The resize is therefore a step outside a frame:

```csharp no-compile="the host owns the device; see SceneRenderHost"
// What AppGraphics does after it rebuilds the swapchain.
host.FrameSize = swapChain.Size;
```

`SceneRenderHost.FrameSize` forwards to `GraphicsCompositor.Resize`, which walks the frame and calls
`Reset()` on every node implementing `IResizeTarget`, having first idled the device exactly once —
and not at all when no node in the tree wants one, so a window drag through a hundred sizes costs a
hundred nothing-happened walks rather than a hundred device stalls. A size equal to the current one
is not a resize: a surface reporting `Suboptimal` asks for a swapchain rebuild every frame, and
resetting on those would restart every temporal chain in the frame for ever.

⚠ **A reset is a camera cut.** Probe history, placement and readback rings all start over, because
the alternative is reprojecting through a lattice that no longer exists — a frame that draws and is
quietly wrong, which is worse than a frame that is visibly one frame behind.

**A node of your own taking part.** Implement `IResizeTarget` only if you keep state whose *shape*
other objects were built against. If you can compare a cached `Int2` and reallocate, do that instead
— it needs no wiring and no idle.

```csharp no-compile="illustrative; SceneRenderer's phases are driven by the compositor"
public sealed class MyGather : SceneRenderer, IResizeTarget {
    Lattice? lattice;

    public void Reset() {
        lattice?.Dispose();
        lattice = null;      // The next Build lays a new one at the frame's size.
    }
}
```

`Reset()` is called with the device idle and outside any frame, and it is called on a node that has
never built — the first size a host writes is a change from the compositor's default — so "nothing
to forget" has to be free rather than an error. It is called on disabled nodes too, for the reason
[post-process volumes](post-process-volumes.md) visits them: a node switched off across the resize
and back on afterwards would otherwise refuse the first build that reached it.

## See also

- [Fog a shadow can fall through](volumetric-fog.md) — the froxel volume that stands in front of the
  analytic falloff above.
- [Making a room look different](post-process-volumes.md) — where a look applies, rather than which
  effects exist.
- [Lighting a scene in lux and lumens](physical-lighting.md) — what `ev100` and the grade are in.
- `docs/plan/30-post-processing-parity.md` — the audit against Unreal and HDRP, and what is
  deliberately not here.
