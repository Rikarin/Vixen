---
title: Multisampling
slug: rendering/msaa
kind: guide
area: Rendering
summary: What a document says to draw a pass at 4× and where the samples go afterwards — the resolve pair, the sample counts that all have to agree, the two textures that are deliberately not interchangeable, and why depth resolves by a named rule rather than by averaging.
api: [T:Vixen.Rendering.Compositor.ResolveTargetAsset, T:Vixen.Graphics.DepthResolveMode]
tags: [rendering, compositor, antialiasing, render-graph]
since: 0.1
status: preview
related: [rendering/post-processing, rendering/smaa, rendering/choosing-a-frame, rendering/reading-the-frame]
---

## What it is

The hardware antialiasing: a pass draws into a texture that keeps several samples per pixel, and at
the end of the pass those samples are averaged into an ordinary one-sample texture. Two lines of a
`.vxcompositor` say it — `sampleCount` on the pass and its targets, and a `resolveTargets` entry
naming where the samples go:

```yaml
resources:
  - name: SceneSamples
    format: Rgba16Float
    usage: ColourTarget
    sampleCount: 4
  - name: SceneColour
    format: Rgba16Float
    usage: ColourTarget, Sampled
game: !RenderPass
  name: Main
  colourTargets: [SceneSamples]
  sampleCount: 4
  resolveTargets:
    - target: SceneSamples
      into: SceneColour
```

`resolveTargets` is a list of `ResolveTargetAsset` — a `target`, which is a name from this pass's
`colourTargets`, and an `into`, which is the single-sampled resource the average lands in. Pairs are
matched by name rather than by position, so adding a colour target above one of these does not move
somebody else's resolve onto its neighbour.

## What it is for

Unlike every antialiasing node in [the post chain](post-processing.md), multisampling runs the
rasteriser more than once per pixel rather than reconstructing an edge from a finished image. It sees
geometric edges exactly and interior shading not at all — which is the trade: it is the only filter
here that cannot blur a texture or smear a thin highlight, and the only one that does nothing about
specular aliasing.

⚠ **`!StandardFrame` has no `samples:` knob, on purpose.** Its expansion is the ambient split, which
is not the classic forward path: multisampling it would mean multisampled albedo, normals, specular
*and* depth, and each of those wants a resolve that is not an average. Multisampling is available to
a document that writes its own passes; the standard frame's answer to aliasing is
[SMAA](smaa.md) or temporal antialiasing, chosen with `antialiasing:`.

## Using it

**A multisampled texture is not sampleable, and its resolve is not an attachment.** The two resources
in the example above have deliberately different `usage`: the 4× one is `ColourTarget` and nothing
else, because a multisampled image cannot be read through an ordinary sampler; the resolve carries
`Sampled` and is what every later pass reads by name. A frame that points its post chain at
`SceneSamples` gets a validation error. A frame that leaves `resolveTargets` out entirely gets no
error at all — just a target it drew correctly and nobody can read.

**Every attachment of one pass has the same sample count.** Raising it on the colour targets and
leaving the depth target at one is the usual way to get this wrong, and the render graph refuses the
pass rather than letting the validation layer report it as a framebuffer problem:

> Pass 'Main' attaches 'SceneSamples' at 4× and 'SceneDepth' at 1×. Every attachment of one pass has
> the same sample count — raising it on the colour targets and leaving the depth target behind is the
> usual way here.

Three more conditions are checked where the pair is declared, because a release driver reports none
of them: the target must actually be multisampled, the resolve must be single-sampled, and the two
must agree on format and on size. The last is the one worth the check — a resolve between two
differently sized attachments is undefined rather than a scale, so it reads as a picture that is
subtly cropped rather than as an error.

Below the compositor the same pair is one optional argument on the render graph's pass builder:

```csharp no-compile="a fragment; the builder and both textures are the pass's own"
builder.ColourAttachment(samples, LoadAction.Clear, clear, resolve: colour);
```

Naming a resolve makes the store a `StoreAction.Resolve` whatever `store` says, and declares the
resolve as a *write* of the pass. Both halves matter: the resolve is the write the next pass reads,
which lets the multisampled texture be aliased and discarded — and without the declaration the
resolve has no producer, every reader of it fails validation, and the pass that filled it is culled
for writing something nobody wanted.

### Resolving depth

**A depth resolve is not an average, and there is deliberately no option to make it one.** The mean
of four colours is a colour; the mean of four depths is a surface that is behind nothing and in front
of nothing, so everything that reads the resolved buffer — reprojection, screen-space tracing, fog,
a later pass's depth test — reads a plane no geometry occupies. Colour can be described by
`StoreAction.Resolve` and a view alone because every backend combines colour the same way. Depth
needs the rule named, which is what `DepthResolveMode` is:

| Mode | Keeps |
|---|---|
| `Max` | The largest depth value — **the sample nearest the camera**, and the default |
| `Min` | The smallest — the sample farthest away |
| `SampleZero` | Sample zero, ignoring the rest |

⚠ **`Max` is "nearest" because the engine is reversed-Z.** The near plane maps to depth 1 and the far
plane to 0, so the inequality runs the opposite way from the convention most references assume.
Getting this backwards is expensive precisely because it is cheap to do: both choices resolve without
an error and render a picture that looks right, and only the passes reading the resolved depth go
quietly wrong. `Max` is the right default for almost everything — an edge pixel covered partly by a
near surface and partly by a far one belongs to the near one, because that is the surface the
resolved colour beside it is mostly showing.

`SampleZero` is a fallback rather than a choice: it is the only mode every Vulkan implementation is
required to support, and sample zero sits at a fixed offset inside the pixel that is not the centre
for any sample count above one — so a depth buffer resolved that way is biased by up to half a pixel
in a direction that depends on the pattern.

A document says it through the same `resolveTargets` list colour uses, naming the depth target; the
rule goes on the pass, because a pass has at most one depth attachment:

```yaml
game: !RenderPass
  name: Main
  colourTargets: [SceneSamples]
  depthTarget: SceneDepthSamples
  sampleCount: 4
  depthResolveMode: Max
  resolveTargets:
    - target: SceneSamples
      into: SceneColour
    - target: SceneDepthSamples
      into: SceneDepth
```

Below the compositor it is the same optional argument colour has, on the same builder:

```csharp no-compile="a fragment; the builder and both textures are the pass's own"
builder.DepthAttachment(depthSamples, resolve: depth, resolveMode: DepthResolveMode.Max);
```

Everything colour's resolve guarantees holds here too — the store becomes a `StoreAction.Resolve`
whatever `store` says, the target is declared as a write so it has a producer, and the four
conditions on the pair are checked where it is named. Two rules are specific to depth:

- **A read-only depth attachment cannot resolve.** Read-only means the pass only tests depth; a
  resolve is a write of the target beside it. Asking for both is refused rather than silently doing
  neither.
- **Stencil is not resolved alongside depth.** Vulkan requires the two modes to agree when both
  resolve, and there is no meaningful "nearest" for a stencil value, so the depth resolve stands
  alone and the stencil samples are dropped.

### ⚠ The rule is a request, and some devices decline it

**Every implementation must offer `SampleZero`. `Max` and `Min` are optional, and asking a device
for one it does not offer is invalid usage rather than a slow path** —
VUID-VkRenderingInfo-pDepthAttachment-06102 — so what the resolved buffer holds afterwards is the
driver's business and everything downstream of it reads a lie.

So the mode a pass names is clamped against the device before it is submitted. Ask
`GraphicsDeviceFeatures` which rules it has:

```csharp no-compile="a fragment; the device is the caller's"
if (device.Features.SupportsDepthResolveMode(DepthResolveMode.Max)) {
    // The nearest sample. Otherwise sample zero is what the resolve will hold.
}
```

`ClampDepthResolveMode` is what the backends call, and it returns `SampleZero` for anything the
device declined. Nothing throws and nothing warns — a frame authored with `depthResolveMode: Max`
runs everywhere, and on a device that cannot honour it the resolved depth is biased by up to half a
pixel instead of being undefined.

⚠ **This is not implied by the sample count.** lavapipe renders 4× depth attachments quite happily
and advertises `SampleZero` alone, which is why the two questions are asked separately.

⚠ **This is a Vulkan capability, not a universal one.** It is `VK_KHR_depth_stencil_resolve`, which
the engine already enables as a prerequisite of dynamic rendering. WebGPU has no equivalent — a
render pass depth attachment there carries no resolve target at all — so the WebGPU backend refuses a
depth resolve loudly instead of dropping it, because a dropped one leaves the target holding last
frame's contents and reads as a picture that is almost right.

## Examples

A pass whose colour survives and whose depth does not is the common shape — the depth buffer is
multisampled because it has to be, not because anything downstream wants the samples:

```yaml
resources:
  - name: SceneSamples
    format: Rgba16Float
    usage: ColourTarget
    sampleCount: 4
  - name: SceneDepthSamples
    format: Depth32Float
    usage: DepthStencilTarget
    sampleCount: 4
  - name: SceneHdr
    format: Rgba16Float
    usage: ColourTarget, Sampled
game: !RenderPass
  name: Main
  colourTargets: [SceneSamples]
  depthTarget: SceneDepthSamples
  sampleCount: 4
  resolveTargets:
    - target: SceneSamples
      into: SceneHdr
```

Everything after this reads `SceneHdr`. `SceneSamples` and `SceneDepthSamples` are named nowhere else
in the document, which is the point: their contents exist for the duration of one pass.

## See also

- [The post-processing node kinds](post-processing.md) — the reconstruction filters, which solve a
  different half of the problem and compose with this one.
- [SMAA](smaa.md) — the analytic filter, and what it costs against what it is worth.
- [Choosing a frame](choosing-a-frame.md) — which antialiasing a project should be asking for.
- [Reading the frame](reading-the-frame.md) — what a `.vxcompositor`'s resources and passes mean.
