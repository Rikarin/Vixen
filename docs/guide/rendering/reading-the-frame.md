---
title: A pass that reads the frame so far
slug: rendering/reading-the-frame
kind: guide
area: Rendering
summary: Two ways for a pass to consume its own output — a snapshot of a target inside one frame, and a pair of targets alternating across them — and why both are resources the render graph owns rather than barriers somebody remembers.
api: [T:Vixen.Rendering.Compositor.TextureCopyAsset, T:Vixen.Rendering.Compositor.TextureCopyRenderer, T:Vixen.Graphics.RenderGraph.PingPongTextures, T:Vixen.Graphics.RenderGraph.PingPongPair, T:Vixen.Rendering.Water.WaterRenderer, T:Vixen.Rendering.Water.WaterAsset, T:Vixen.Rendering.Water.WaterRendererFactory, T:Vixen.Rendering.Water.WaterZoneComponent, T:Vixen.Rendering.Water.WaterBodyComponent, T:Vixen.Rendering.Water.WaterZoneSystem, T:Vixen.Rendering.Water.WaterInfoTexture, T:Vixen.Rendering.Water.IWaterSplineSource, T:Vixen.Rendering.Water.WaterSurfaceAsset, T:Vixen.Rendering.Water.WaterMeshRenderer, T:Vixen.Rendering.Water.WaterSurfacePass, T:Vixen.Rendering.Water.WaterNodeRecord, T:Vixen.Rendering.Water.WaterMeshShaders, T:Vixen.Rendering.Water.WaterMeshView, T:Vixen.Rendering.Water.WaterMeshSettings, T:Vixen.Rendering.Water.UnderwaterShape, T:Vixen.Rendering.Water.UnderwaterAsset, T:Vixen.Rendering.Water.UnderwaterRenderer, R:Water/Water, R:Water/WaterMesh, R:Water/Underwater, T:Vixen.Shaders.Generated.WaterKeys, T:Vixen.Shaders.Generated.WaterMeshKeys, T:Vixen.Shaders.Generated.UnderwaterKeys, T:Vixen.Rendering.Water.WaterRippleSimulation, R:Water/Ripples, T:Vixen.Shaders.Generated.RipplesKeys, T:Vixen.Rendering.Water.WaterTiles, R:Water/WaterTiles, T:Vixen.Shaders.Generated.WaterTilesKeys, T:Vixen.Shaders.Generated.WaterConstants, T:Vixen.Shaders.Generated.WaterMeshPerFrameConstants, T:Vixen.Shaders.Generated.WaterMeshPerMaterialConstants, T:Vixen.Shaders.Generated.WaterTilesConstants, T:Vixen.Shaders.Generated.RipplesConstants, T:Vixen.Shaders.Generated.UnderwaterConstants, T:Vixen.Rendering.Water.WaterDebugDraw, T:Vixen.Rendering.Water.WaterMeshRenderer.WaterZoneDraw]
tags: [rendering, compositor, render-graph, water]
since: 0.1
status: stable
related: [rendering/post-processing, rendering/post-process-volumes, rendering/capturing-a-frame]
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
    # … the lit pass …
    - !WaterSurface { surface: WaterSurface, normal: WaterNormal, sceneDepth: SceneDepth, view: Camera }
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

⚠ **All three nodes, in that order, or there is no wet pixel.** `!WaterSurface` is what draws the
geometry — `WaterMeshRenderer` over `WaterSurfacePass`, one instanced draw of the terrain's own grid
patch per zone plus a second for the far skirt. Without it `!Water` reads a cleared mask, finds no
coverage anywhere and passes the frame through unchanged, which is a water stack that is wired, tested
and invisible.

⚠ **A frame that draws no water at all is usually the depth buffer.** The surface's depth state is
`Greater` with no write and its attachment is `LoadAction.Load`, so a document that puts `!WaterSurface`
before anything has written depth tests every fragment against undefined memory — and fails all of
them, with no validation error anywhere. That is the silent no-draw; it belongs after the opaque pass
for the reason below, and the horizon fixture clears depth to the far plane before it because a
fixture has no opaque pass.

⚠ **The surface tests depth and never writes it.** The composite unprojects the *scene* depth to find
what is behind the water; a surface that wrote depth would put itself there and the water would be
integrated against itself — clear at every depth, with nothing in a capture to say why. And the far
skirt is drawn *first*, because with depth writes off nothing arbitrates between two fragments at one
pixel except which came last.

**The pass runs over the tiles that have water in them, not over the screen.** `tiled: true` — which is
the default for a document — puts a compute dispatch ahead of the draw. `WaterTiles.rvn` classifies the
coverage mask into one flag per 8×8 tile, and `!Water` becomes `Draw(6, tiles)`: two triangles per
tile, one instance per tile, and a dry tile collapsing to a degenerate rectangle in the vertex stage.
`WaterTiles` is the C# half of that arithmetic, and the tile size is a constant in both files because
three things — the host, the classifier and the draw — have to agree about which tile an instance is.

⚠ **A tiled pass loads its target and leaves a dry tile alone**, where the untiled one writes every
pixel of the frame: the scene colour back, with a zero mask in alpha. Those are the same picture only
where the output already holds what `behind:` is a copy of — which in a document is free, because the
`!Copy` above filled `behind:` from that very target. Wire a `WaterRenderer` by hand against a target
holding something else and the dry pixels keep that something else, which is why `Tiled` is off on the
node and on in the document.

⚠ **A flag per tile and not a compacted list, so the draw is instanced rather than indirect.** The
shape this came from feeds an indirect draw, which needs the count on the device; `ICommandList` has
`DrawIndexedIndirect` and no non-indexed `DrawIndirect`, so indirect here means either a three-entry
index buffer for a triangle that has no vertex buffer, or reading the count back to the host — a stall
a frame long, every frame, to avoid a pass over tiles that are mostly empty. What a flag costs instead
is one discarded rectangle per dry tile.

⚠ **The tile buffer is bound even when the tiling is off**, because a descriptor set is written wholly
or not at all — a shader's bindings come from its declarations and not from the variant it was compiled
into. Untiled, the node imports a zeroed word of its own rather than declaring a transient nothing
writes, which the render graph refuses by name.

**Underwater is two features that look like one**, and § D9 warns twice that getting the order wrong
is architectural. The volume half is `UnderwaterShape`, doc 32's `IPostProcessShape`, supplied by
`WaterZoneSystem` — per zone rather than per body, because the field has already resolved a river
mouth into one place. It grades the whole frame.

The other half is `!Underwater`, and it exists because **a fold produces one weight and a waterline is
a curve**. It solves the intersection of the surface with the near plane per pixel, against the local
surface *plane* read off the same `WaterQuery` the volume fold and the buoyancy solver read.

⚠ **It goes after `!Water` and after a second `!Copy`.** What it grades is the finished frame
*including* the water surface, so the copy it reads has to be taken after the surface was composited —
a document reusing the copy `!Water` read would grade the frame as it was before the water was in it,
which at the waterline is a band of unlit lake.

⚠ **The surface mask does a different job in this node.** In `!Water` it says "there is water here";
in `!Underwater` it says "the ray leaves the water here", which is what bounds the fog path. Without
that, a diver looking up is exactly as dark as one looking down at the bed — the failure that reads as
"underwater is just a blue filter".

**The ripple field is the ping-pong's first consumer.** `WaterRippleSimulation` is one dispatch a
step over a `PingPongTextures` pair, with the displacement and its rate in one texture's `rg`.

⚠ **`Rgba16Float` and not `Rg16Float`, which is portability rather than waste.** Vulkan's *required*
storage-image formats are a short list and two-channel half is not on it — a device without
`StorageImageExtendedFormats` refuses the module outright, which is what the seam fixture found on the
first machine it ran on.

⚠ **The uniform block is filled at declaration and not inside the pass body.** A graph executes long
after it is declared and the injection queue is cleared at the bottom of `Record`, so a body that
described itself would describe an empty queue every time — a device field that is perfectly flat,
perfectly stable, and wrong by exactly the reference's own amplitude.

⚠ **The descriptor ring is sized by `StepsPerFrame` and not by frames in flight alone.** A set names
the half of the pair this step reads and the halves swap, so a set rewritten while a submitted command
list still references it is the race the ping-pong exists to avoid — arriving through the descriptor
rather than through the texture. One step a frame is the assumption; an accumulator catching up after
a hitch takes several.

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
