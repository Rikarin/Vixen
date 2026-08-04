---
title: A pass that reads the frame so far
slug: rendering/reading-the-frame
kind: guide
area: Rendering
summary: Two ways for a pass to consume its own output — a snapshot of a target inside one frame, and a pair of targets alternating across them — and why both are resources the render graph owns rather than barriers somebody remembers.
api: [T:Vixen.Rendering.Compositor.TextureCopyAsset, T:Vixen.Rendering.Compositor.TextureCopyRenderer, T:Vixen.Graphics.RenderGraph.PingPongTextures, T:Vixen.Graphics.RenderGraph.PingPongPair, T:Vixen.Rendering.Water.WaterRenderer, T:Vixen.Rendering.Water.WaterAsset, T:Vixen.Rendering.Water.WaterRendererFactory, T:Vixen.Rendering.Water.WaterZoneComponent, T:Vixen.Rendering.Water.WaterBodyComponent, T:Vixen.Rendering.Water.WaterZoneSystem, T:Vixen.Rendering.Water.WaterInfoTexture, T:Vixen.Rendering.Water.IWaterSplineSource]
tags: [rendering, compositor, render-graph, water]
since: 0.1
status: stable
related: [rendering/post-processing, rendering/post-process-volumes]
---

## What it is

Two small pieces with one problem between them: a pass that has to read what it is about to write.

| Piece | Where the previous value is | What it is |
|---|---|---|
| `!Copy` | Earlier in the same frame | A transfer pass that snapshots one named target into another |
| `PingPongTextures` | The previous frame | Two persistent textures the graph is handed each frame, swapped per step |

Both exist because **sampling a target a pass is also writing is undefined** — not slow, not
approximate, undefined — so the read has to come from a second resource, and that resource has to be
one the render graph knows the lifetime of.

## What it is for

`!Copy` is what refraction needed. A shading model that integrates absorption over the distance to
whatever is behind it reads the scene colour and then contributes to it; the compositor could always
express the *pass*, and what it could not express is the copy that makes the pass legal. `!Water` is
that pass — see below.

`PingPongTextures` is what a simulation needs. A height field advanced by a step that reads frame N
and writes frame N + 1 has two targets and a rotation between them, and the dependency crosses a frame
boundary — so it is invisible in either frame's pass list unless something states it.

⚠ **Neither is water's alone**, though both were built for it
(`docs/plan/35-water.md` § B1 and § B5). Anything that reads the frame so far and then contributes to
it wants the first: a distortion pass, a heat haze, a UI that blurs what is behind it. Anything
iterative wants the second.

## Using it

### The copy

```yaml
resources:
  - name: SceneColour
    format: Rgba16Float
    usage: ColourTarget, Sampled, CopySource
  - name: SceneColourCopy
    format: Rgba16Float
    usage: Sampled, CopyDestination

game: !Sequence
  children:
    # … the lit pass writes SceneColour …
    - !Copy
      name: SceneColourSnapshot
      source: SceneColour
      destination: SceneColourCopy
    # … and a pass that samples SceneColourCopy may now write SceneColour.
```

⚠ **Both resources have to declare the usage**, and neither flag is in a resource's default. Missing
one is a validation error on a debug driver and silently nothing on a release one, so the build
refuses it by name instead — naming the resource and the flag it is short of.

⚠ **A mismatch is refused rather than resolved.** A copy moves texels; it does not convert formats and
it does not rescale. A half-resolution destination would copy correctly into its top-left quarter and
every pixel of it would be a plausible colour, so what reaches the screen is a refraction that is
subtly, consistently wrong. Where a resample is what was wanted, that is a full-screen node.

**A copy nothing reads is culled, with its destination's memory.** That is what makes a document that
carries a water node cost nothing in a scene with no water.

### The ping-pong

```csharp no-compile="the shape of a step, not a compiling simulation"
// Once, when the simulation is created. ⚠ Both textures need the whole usage: the two halves
// alternate, so there is no half of a ping-pong that is only ever read.
ripples = new PingPongTextures(
    device,
    new TextureDescription(
        PixelFormat.Rgba16Float, 256, 256,
        TextureUsage.Storage | TextureUsage.Sampled | TextureUsage.ColourTarget,
        Name: "Ripples"
    )
);

// Per frame.
if (!ripples.HasHistory) {
    ripples.Clear(graph);
}

var pair = ripples.Import(graph);

graph.AddPass("ripple step", pass => {
    pass.Kind = PassKind.Compute;
    pass.Reads(pair.Read);
    pass.Writes(pair.Write, ResourceState.ShaderWrite);
    pass.Execute(context => context.CommandList.Dispatch(16, 16));
});

graph.Execute(commandList);
ripples.Advance();
```

⚠ **`Advance` is called after the graph has executed, not after it has been declared.** Both halves
are imported by index, so swapping mid-declaration gives two passes in one frame two different
opinions about which texture is the input — and the second would be reading what it had just written.

⚠ **The first read is undefined until something has written it, and the graph cannot catch that.** An
import counts as produced as far as read validation is concerned — it has to, since the whole point of
importing is that a previous frame filled it — so a first-frame read passes validation and samples
whatever the allocation held. On most drivers that is zeroes, which is exactly what a settled height
field looks like, and so the bug survives to the first machine whose driver hands back something else.
`HasHistory` is how a caller knows; `Clear` is the decision made once rather than per consumer.

⚠ **Clearing is a render pass**, because there is no clear-texture operation on `ICommandList` and
deliberately so — every backend spells one differently and half of them implement it as this. A pair
declared for storage alone is refused by name rather than left dirty.

### The water pass, which is the copy's first consumer

```yaml
game: !Sequence
  children:
    # … the lit pass, then the water surface pass writing WaterSurface and WaterNormal …
    - !Copy   { source: SceneColour, destination: SceneColourCopy }
    - !Water  { behind: SceneColourCopy, output: SceneColour, view: Camera }
```

`!Water` integrates absorption and scattering over the depth of water between the surface and whatever
is behind it, and composites it once. ⚠ **Naming the output in `behind:` is refused by name at build
time** — that is the undefined case above, and a document that made the mistake would otherwise render
on one driver and not another.

⚠ **Its alpha is the waterline mask, not an opacity.** A camera straddling the surface needs two
treatments in one frame divided by a curve a post-process volume's single per-frame weight cannot
express, and the pass already knows per pixel which side it is on.

What supplies the water's own planes is a scene: `WaterZoneComponent` and `WaterBodyComponent` on
ordinary entities, folded by `WaterZoneSystem` into the fields the kernel owns and uploaded by
`WaterInfoTexture`. ⚠ **Two diagnostics rather than one** — `ZonelessBodies` is a body no zone's window
reached and `UnresolvedBodies` is a spline that has not loaded, and the fixes are different enough that
one number for both would send an author to the wrong place.

## Examples

**Both halves are imported every step**, whether or not the step touches them. A graph told about only
the one it uses cannot place the barrier between this step's write and the next step's read, and the
untouched half's entry state next frame becomes a guess rather than a fact.

That is what makes the tracking worth a type. The pair remembers the state each texture was left in,
so the next frame's import says where it actually is:

| Frame | What the graph is told | What it emits |
|---|---|---|
| 1 | Enters `Undefined`, must leave `ShaderRead` | `Undefined → ShaderWrite`, then `ShaderWrite → ShaderRead` |
| 2, reading it | Enters `ShaderRead`, must leave `ShaderRead` | Nothing — it is already where the read wants it |
| 2, writing the other half | Enters `ShaderRead` | `ShaderRead → ShaderWrite` |

⚠ **`Undefined` is not a neutral placeholder.** It tells the driver the previous contents may be
discarded, and on hardware with compressed render targets they will be — so an import that had not
tracked its own state throws the whole ping-pong away, silently, on the frame it first mattered.

**On OpenGL** `glBindImageTexture` is not implemented, so a simulation there is a full-screen fragment
pass into a colour target rather than a compute pass into a storage image. That is why the usage is
the caller's to state: the pair does not know which of the two the step is.

## See also

- [The post-processing node kinds](post-processing.md) — the other node kinds a compositor document
  is built from.
- [Where a look applies](post-process-volumes.md) — the other half of the same round of
  generalisations.
- `docs/plan/35-water.md` § B1 and § B5 — why both of these were owed, and by what.
- `docs/plan/05-graphics-rhi.md` — why barrier correctness is derived rather than hand-written.
