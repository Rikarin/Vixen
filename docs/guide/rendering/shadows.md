---
title: Making everything cast a shadow
slug: rendering/shadows
kind: guide
area: Rendering
summary: The caster stage, the atlas a shading pass reads it from, and the two joins that make one mesh drawable by two shaders.
api: [T:Vixen.Rendering.Compositor.ShadowMapAsset, T:Vixen.Rendering.Compositor.ScenePublishAsset, T:Vixen.Rendering.VertexSchema, T:Vixen.Rendering.VertexChannel, T:Vixen.Rendering.Compositor.PunctualShadowAsset, T:Vixen.Rendering.Compositor.PunctualShadowTileData, T:Vixen.Rendering.IPunctualLightSource, T:Vixen.Rendering.Ecs.StaticShadowCaster]
tags: [rendering, compositor, lighting, shadows]
since: 0.1
status: stable
related: [rendering/lit-path, rendering/physical-lighting]
---

## What it is

A cascaded shadow map, described in a `.vxcompositor` rather than assembled in C#. Four pieces,
each owned by whoever knows the fact it carries:

| Piece | Says |
|---|---|
| a stage with `shader: ShadowCaster` | how casters are drawn — depth only, front faces, biased |
| `GraphicsOptions.CasterStages` | which objects are *in* that stage |
| `MeshRenderable.CastsShadows` | which of those objects opt back out of it |
| `!ShadowMap` | where the cascades are fitted and what they are drawn into |
| `sceneTextures:` on the shading pass | how the atlas reaches set 0 |

## What it is for

Direct light that stops at the first thing it hits. Everything in the level and the character
casting into one atlas, four cascades deep, fitted to the camera the frame is actually drawn
through.

You do not want it for a frame with no directional light — the node fits its cascades to a sun, and
with none it falls back to a constant direction that has nothing to do with what the frame is lit
by. And you do not want a caster stage at all if the only thing in the scene is a skybox: it is a
second traversal of the whole level per cascade.

## Using it

The stage first, beside the one the camera draws:

```yaml
stages:
  - name: Opaque

  - name: Shadow
    shader: ShadowCaster
    cull: Front
    depthBias: 1.5
    depthBiasSlope: 2.5
    depthClamp: true
```

`cull: Front` is doing most of the work that the biases would otherwise have to. Recording the far
side of a caster puts the stored depth *behind* the surface being tested, which is the cheapest cure
there is for a closed mesh shadowing itself.

Then the atlas, the node and the publish. The atlas's size is the node's own arithmetic —
`cascadeCount` tiles of `resolution` across — written out because a graph resource has to declare an
extent and the node has no way to declare one for it:

```yaml
resources:
  - name: ShadowAtlas
    format: Depth32Float
    usage: DepthStencilTarget, Sampled
    width: 8192
    height: 2048

game: !Sequence
  name: Frame
  children:
    - !ShadowMap
      name: Sun
      stage: Shadow
      atlas: ShadowAtlas
      view: Camera
      cascadeCount: 4
      resolution: 2048
      shadowDistance: 90.0

    - !RenderPass
      name: Main
      colourTargets: [SceneHdr]
      depthTarget: SceneDepth
      sceneTextures:
        - binding: shadowMap
          resource: ShadowAtlas
      children:
        - !SingleStage
          name: Opaque
          view: Camera
          stage: Opaque
```

⚠ `view: Camera` is not decoration. Left empty, the node fits every cascade to its own fallback
camera — down −Z from the origin — so the shadows are correct for a view nobody is looking through
and absent from the one that exists.

⚠ The *consuming* pass publishes the atlas, not the producing one. A graph resource's barrier
belongs to whoever declared it read, so the shadow node hands over its matrices, its biases and its
sampler and cannot hand over the texture.

Finally, the objects. A frame document decides where a stage is drawn; it cannot decide what an
object is extracted as, so without this line the level is invisible to the shadow pass however
carefully the document is written:

```csharp no-compile="one line of a Game.OnConfigure, which owns the AppConfig"
config.Graphics.CasterStages.Add("Shadow");
```

The camera's own view keeps the `Opaque` mask alone — the shadow node makes its own views, one per
cascade, and adding the caster stage to the camera would draw the level twice into the frame the
player sees.

That line is also what makes the per-object opt-out mean anything. `MeshRenderable.CastsShadows` is
authored on the entity, and a render object carries a stage mask and nothing else a shadow pass
consults — so "casts no shadow" can only be spelled as "not in the caster stages", and extraction has
to be told which stages those are. Naming them here does both: `AppGraphics` unions `CasterStages`
and `StaticCasterStages` into `MeshExtractionSystem.CasterStages`, and a drawable with the flag clear
is stamped with its mask less those bits.

```csharp no-compile="a fragment of scene set-up, against a world"
// Glass, foliage cards, a skybox shell: drawn, and never in a cascade.
world.Add(entity, MeshRenderables.Default(mesh) with { CastsShadows = false });
```

⚠ **A host that names no caster stage ignores the flag rather than obeying it.** A zeroed
`MeshRenderable` reads `false`, and so does a scene file that omits the field — so the opt-in is what
keeps an unwired project drawing the shadows it drew yesterday instead of losing all of them at once.
`PrimitiveShape` carries no such field and is always a caster; giving it one would change a
component's layout, which every saved scene is a copy of.

⚠ **The flag is read once, when the entity is first extracted**, exactly as `StaticShadowCaster` is,
and for a sharper reason than symmetry: a static caster's shadow is already in the cached atlas, so
re-stamping its mask on a live scene would stop it being *drawn* without removing what is already
*there*. Toggling it on a settled entity takes effect through `MeshExtractionSystem.Resettle`, which
drops every render object and residency claim so the next extraction stamps them afresh — and for a
static caster, a bump of `StaticVersion` beside it to redraw the cache.

Terrain is the one caster that does not go through the stage. The ground is not an extracted
object — its patches live in a node's own buffers — so `TerrainComponent.CastShadows` is consumed
by a caster node the terrain factory's transform splices directly after the shadow node, which
loads the atlas and merges the terrain's depths into every cascade's tile under the same
conventions this page describes: reverse-Z, back faces, zero raster bias, holes discarded. See
[terrain rendering](terrain-rendering.md) for its shape and its virtual-shadow-map caveat.

## Examples

**One mesh, two shaders, two vertex layouts.** A vertex layout is a join of two facts and only one
of them belongs to the mesh: the buffer decides which attributes exist and how they are packed, and
the *shader* decides which of them it reads and at which location. `ForwardPlus` declares six
streams, so its `position` is location 6; `ShadowCaster` declares one, so its `position` is location
1 and it has three attributes where the forward pass has four.

So a renderer describes its vertices once, by name, and lets each effect supply its own numbers:

```csharp no-compile="the two halves of one wiring, shown out of the vertex struct and the renderer that own them"
public static VertexSchema Schema { get; } = new(
    SizeInBytes,
    new VertexChannel("position", VertexFormat.Float32X3, 0),
    new VertexChannel("normal", VertexFormat.Float32X3, 12),
    new VertexChannel("tangent", VertexFormat.Float32X4, 24),
    new VertexChannel("texcoord", VertexFormat.Float32X2, 40)
);

describer.VertexSchemas.Add(SurfaceVertex.Schema);
```

The names are the shaders' parameter names and they have to be — the match is by name, so
`texcoord` here and `uv` in a stage is an attribute the pipeline refuses to bind.

**What a stage owes the shader it imposes.** `ShadowCaster` declares an opacity map, a sampler and a
bone palette whatever its `AlphaTested` and `Skinned` permutations say, and no material in any
project has a name for any of them: they belong to a pass no material has heard of. A per-material
set is written wholly or not at all, so without somewhere for those to come from the whole caster
stage draws nothing.

`RenderStage.Parameters` is that somewhere — consulted *after* the material, so an alpha-tested
caster still cuts out against the material's own opacity map:

```csharp no-compile="a fragment against a built compositor, whose stages a document made"
if (builder.Stages.TryGetValue("Shadow", out var caster)) {
    caster.Parameters.Set(ParameterKeys.New<TextureViewHandle>("ShadowCaster.opacityMap"), white);
    caster.Parameters.Set(ParameterKeys.New<SamplerHandle>("ShadowCaster.opacitySampler"), sampler);
    caster.Parameters.Set(ParameterKeys.New<BufferHandle>("ShadowCaster.bones"), bindPose);
}
```

White rather than undefined: the alpha-tested variant samples it, and white means "this texel is
solid" — the answer that makes a caster with no cut-out map cast its whole silhouette.

### ⚠ The lamps are a separate atlas, and it is four things rather than three

A spot or a point light is shadowed by `!PunctualShadows`, not by the cascades. Nothing has to be
fitted or stabilised — a spot's shadow frustum *is* its cone and a point's is six of them — so the
node is short. What is long is the wiring, because a punctual shadow is composed rather than
declared:

```yaml
resources:
  - name: PunctualShadowAtlas
    format: Depth32Float
    usage: DepthStencilTarget, Sampled
    width: 4096            # tilesPerSide × resolution, the node's own arithmetic
    height: 4096

game: !Sequence
  name: Frame
  children:
    - !PunctualShadows
      name: Lamps
      stage: Shadow        # the same caster stage the cascades use
      atlas: PunctualShadowAtlas
      resolution: 1024
      tilesPerSide: 4
      passes:
        - ForwardPlus.PunctualShadowAtlas
```

```csharp no-compile="a fragment; the compilation and the parameters are the caller's"
// And the composition, which is what puts a shadow lookup in the variant at all.
slots[MaterialCompiler.ForwardPunctualShadowSlot] = MaterialCompiler.PunctualShadowShader;
```

```yaml
      # And the texture, from the pass that reads it — under the compose slot's name, because a
      # slot's bindings are named for what fills it.
      sceneTextures:
        - binding: PunctualShadowAtlas.atlas
          resource: PunctualShadowAtlas
```

⚠ **And a fourth thing that is not configuration: the node needs the scene's light list.**
`CompositorBuilder.Lights` is where it comes from, and it is the *same list instance* the lighting
feature owns rather than a copy — the node writes each light's tile index back into the entry it came
from, and the feature reads that back when it flattens the lights to the GPU. Two lists is two sets
of indices, one of which addresses an atlas packed from the other.

⚠ **There is no permutation, and that is deliberate.** The neutral filler `NoPunctualShadows`
declares no bindings and compiles to `1f`, so composition alone is the switch — where ambient
occlusion needs both a slot and a permutation because the march is the expensive half. What that
buys is one thing to get wrong instead of two; what it costs is that naming the slot without naming
the pass in `passes:` is a set written short, which is every draw in the pass refused rather than a
shadow that does not appear.

⚠ **Sixteen tiles is two point lights, or one point light and ten spots.** A light that does not fit
is dropped whole — all six faces or none, because a point light with four faces rendered leaks along
the other two — and `PunctualShadowRenderer.DroppedLights` is what turns "some shadows disappeared in
the big fight" into a number.

That ratio is the sizing decision, and it is worth doing before choosing a resolution rather than
after: eighteen shadowed point lights are 108 tiles, so eleven tiles a side whatever each one is —
and at 512 that atlas is 127 MB. The same eighteen lights as *spots*, which is what a floodlight on a
post physically is, are eighteen tiles and can afford 1024 each in a quarter of the memory. Reaching
for a point light where a spot would do is a six-fold cost taken without being told, which is why
`ShadowProjections.TileCount` is a number in the API rather than an implementation detail.

### Keeping the lamps' tiles between frames

Add `cached: true` and a lamp that has not moved, over casters that have not moved, keeps the texels
it already has:

```yaml
    - !PunctualShadows
      name: Lamps
      stage: Shadow
      atlas: PunctualShadowAtlas
      resolution: 256
      tilesPerSide: 11
      cached: true
      passes:
        - ForwardPlus.PunctualShadowAtlas
```

**A tile is kept when four things hold, and redrawn otherwise.** The slot has been drawn at least
once; it holds this lamp's projection inputs — kind, position, direction, range, outer angle, near
plane — compared *bit for bit*; the casters whose bounds meet the tile's frustum are the same
objects in the same places, world matrices included; and `CasterVersion` has not been bumped since.
Nothing else is a claim the host has to make: a lamp that moves, a caster that moves, one that
appears or vanishes, and one that turns on the spot all invalidate the tile they are in, and only
that tile.

`CasterVersion` is the escape hatch for changes with no effect on bounds or transform — a skinned
mesh animating inside a fixed sphere, an alpha cutout edited, a mesh swapped underneath a render
object. Bumping it redraws everything once.

⚠ **The node owns the atlas when this is on.** Depth that survives a frame cannot live in the render
graph's pool, which exists to recycle memory whose lifetime ends inside one — so the node creates the
texture, publishes it under the name the document declared, and only the tiles that went stale get a
pass. The declaration stays, and its extent is still checked against `tilesPerSide × resolution`,
because that disagreement is worth reporting whichever texture is drawn into.

⚠ **Where this differs from `ShadowMapRenderer`'s static cache, and why.** A cascade is fitted to the
camera, so its projection moves whenever the player does; only its static half is worth keeping, and
putting the two halves back together costs a whole-atlas copy every frame. A punctual light carries
its own frustum and does not know the camera exists, so there is no static/dynamic split here, no
second stage to fill, and no copy.

**What it is worth.** Sample 13's eighteen point lights are 108 tiles. Walking its level, the frame
recorded 8 306 draws a frame uncached and 1 297 cached — 4 to 8 tiles redrawn instead of 108. Standing
still it is 7 647 against 436. What the cache removes is the command recording; the GPU cost of 256²
tiles of boxes was already below what a timestamp on this machine can resolve.

⚠ **And one trap that made the whole thing worthless until it was found.** A frame that culls on the
device without a readback hands the host the *conservative* set — every object that could be visible,
because the real answer never leaves the GPU. Sample 13 runs that way, so each of its 108 tiles has
the whole level in its CPU work list, and a cache that trusted the list redrew every tile whenever
anything anywhere moved. The node re-tests each entry against the tile's own frustum for exactly this
reason; `TilesDisturbed` against `TilesMoved` is what tells the two failures apart when a cache
refuses to save anything.

### Keeping the sun's level geometry between frames

The cascades have a cache too, and it is a different arrangement for a reason worth reading before
turning it on. A cascade is *fitted to the camera*, so its projection moves whenever the player does
and only the half of its content that never moves is worth keeping. Three things have to agree, and
any one of them alone makes the frame slower rather than faster:

```yaml
    - !ShadowMap
      name: Sun
      stage: Shadow          # the movers
      staticStage: ShadowStatic   # the level
      slack: 0.25
      atlas: ShadowAtlas
      view: Camera
      cascadeCount: 4
      resolution: 2048
```

```csharp no-compile="two lines of a Game.OnConfigure, which owns the AppConfig"
// The host: which stages a static entity is extracted into *instead* of the ordinary caster ones.
config.Graphics.CasterStages.Add("Shadow");
config.Graphics.StaticCasterStages.Add("ShadowStatic");
```

```csharp no-compile="a fragment against a world the level was loaded into"
// The scene: which entities are making the claim.
world.Add<StaticShadowCaster>(entity);
```

**The invalidation rule.** The cached half is redrawn when it has never been drawn, when any cascade
re-fitted this frame, or when the host bumped `StaticVersion` since it was last drawn. Everything else
keeps it: the frame copies the cache into the working atlas and draws only `stage:` on top.
`TilesDrawn` is `cascadeCount` on a kept frame and twice that on a rebuilt one, `StaticRebuilds` is
the cumulative count, and `StaticRefits` against `StaticInvalidations` says which of the two causes a
rebuild had — they have opposite fixes.

⚠ **`slack:` is not optional, whatever the default says.** A cascade cut exactly to its slice re-fits
the moment the camera moves a texel, and a re-fit invalidates the whole cache — so a static stage with
no slack is every frame the uncached one drew *plus* a whole-atlas copy, with every counter reading
healthy. Cutting 25% wider keeps a cascade while it still covers its slice, and costs resolution: the
same texels over `(1 + slack)²` of the area, 1.5625× at 0.25. The node reports the mistake through
`Degraded`.

⚠ **`StaticShadowCaster` is a claim, and nothing checks it.** An entity that carries it and then moves
keeps the shadow it was first drawn with — the shadow stays where the object used to be. It is also
read *once*, when the entity is first extracted, because that is when a stage mask is stamped; adding
or removing it afterwards does not restamp what already drew.

⚠ **An object belongs in one caster stage or the other, never both.** In both, the level is rasterised
into the cache *and* over it every frame. That is why `StaticCasterStages` replaces `CasterStages` for
a tagged entity rather than adding to it.

⚠ **Everything else that draws casters needs a stage of its own with all of them in it.** A punctual
tile is cached whole and a virtual shadow page is a page of the *world*, so neither has a static and a
dynamic half — and giving either of them one half is a picture rather than an error. `!PunctualShadows`
drawing half the level lights the other half through its own walls. `!VirtualShadow` is worse and much
quieter: it *outranks* the cascades wherever it has a drawn page, so a level missing from its pages
stops casting a sun shadow over most of the screen while the cascades behind it stay perfectly correct
and every counter reads healthy. Sample 13 declares one `ShadowAll` stage and points both nodes at it.

⚠ **A stage that imposes a shader owes that shader's bindings, and every such stage owes them.** A
caster's opacity map, its sampler and its bone palette come from `RenderStage.Parameters`, and a stage
whose collection nobody filled writes no per-material set at all — the pipeline is still bound and the
draw still recorded, with the set empty. That is a validation message with the layers on and a fault
inside `vkQueueSubmit` without them; `MaterialRenderFeature.UnboundCount` and `Unbound` name the
shader and the stage. Splitting one caster stage into three means filling three.

⚠ **The cache's texture is the node's, not the document's.** Every `!Resource` a document declares is
transient, and the graph's pool exists to recycle exactly the memory a cache must keep — so the node
creates it from `CompositorBuilder`'s device. What the document must remember is `CopyDestination` in
the *working* atlas's `usage:`, because the cache is copied into it every frame.

## See also

- [Turning on dynamic global illumination](lit-path.md) — the indirect half of the same frame.
- [Lighting a scene in lux and lumens](physical-lighting.md) — what the sun casting them is measured in.
- `docs/plan/19-lighting-and-global-illumination.md` — why the cascades are views.
