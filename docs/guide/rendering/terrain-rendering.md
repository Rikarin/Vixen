---
title: Drawing a terrain
slug: rendering/terrain-rendering
kind: guide
area: Rendering
summary: A quadtree with a vertex morph, one instanced grid patch, no vertex buffer, and one draw call however many patches it takes.
api: [T:Vixen.Terrain.TerrainLodRanges, T:Vixen.Terrain.TerrainLodNode, T:Vixen.Terrain.TerrainLodTree, T:Vixen.Rendering.Terrain.TerrainGridPatch, T:Vixen.Rendering.Terrain.TerrainNodeRecord, T:Vixen.Rendering.Terrain.TerrainRenderer, T:Vixen.Rendering.Terrain.TerrainShaders, T:Vixen.Rendering.Terrain.TerrainView, T:Vixen.Rendering.Terrain.TerrainComponent, T:Vixen.Rendering.Terrain.TerrainSplat, T:Vixen.Shaders.Generated.TerrainKeys, T:Vixen.Shaders.Generated.TerrainConstants, T:Vixen.Shaders.Generated.TerrainLitKeys, T:Vixen.Shaders.Generated.TerrainLitConstants, T:Vixen.Shaders.Generated.TerrainLitCascadesElement, T:Vixen.Shaders.Generated.TerrainCasterKeys, T:Vixen.Shaders.Generated.TerrainCasterConstants, R:Terrain/Terrain, R:Terrain/TerrainLit, R:Terrain/TerrainCaster, T:Vixen.Terrain.TerrainAtlas, T:Vixen.Terrain.TerrainAtlasTexel, T:Vixen.Rendering.Terrain.ITerrainTextures, T:Vixen.Rendering.Terrain.TerrainStreamer, T:Vixen.Rendering.Terrain.TerrainTilePages, T:Vixen.Rendering.Terrain.TerrainTileSource, T:Vixen.Rendering.Terrain.ITerrainTileSource, T:Vixen.Rendering.Terrain.TerrainTileHandler, T:Vixen.Engine.Renderer.AssetTerrainTextures, T:Vixen.Rendering.Terrain.TerrainNodeAsset, T:Vixen.Rendering.Terrain.TerrainFactory, T:Vixen.Rendering.Terrain.TerrainSceneRenderer, T:Vixen.Rendering.Terrain.TerrainSceneSource, T:Vixen.Rendering.Terrain.TerrainSceneEntry, T:Vixen.Rendering.Terrain.ITerrainAssetSource, T:Vixen.Rendering.Terrain.TerrainExtractionSystem, T:Vixen.Rendering.Terrain.TerrainVegetationQuality, T:Vixen.Engine.Renderer.AssetTerrainSource]
tags: [terrain, rendering, lod, cdlod, instancing]
since: 0.1
status: preview
related: [engine/terrain-heightfield, engine/terrain-painting, rendering/mesh-and-material, editor/terrain-mode, rendering/instance-culling]
---

## What it is

The device side of a terrain. `TerrainLodTree` descends a quadtree over the heightfield and selects
the nodes a view needs, each with a morph factor; `TerrainGridPatch` is the one lattice every node is
drawn from; `TerrainNodeRecord` is the sixteen bytes that place it; `TerrainRenderer` owns the
heightmap texture, the descriptor set and the draw. `TerrainComponent` is what a scene says about a
terrain — which one, how far level 0 reaches, whether it casts shadows.

## What it is for

Drawing four square kilometres of ground at sixty frames a second without popping. CDLOD (Strugar,
2010) is a quadtree over the heightfield with a vertex morph toward the parent grid; the morph is what
removes the pop, and the shared patch is what makes the whole terrain one instanced draw.

You do not want it for a small piece of ground that never changes — a mesh is simpler and costs less
to set up. The break-even is roughly where an artist would want to sculpt rather than model.

## Using it

```csharp no-compile="a fragment; the shaders come from the compiled library"
var renderer = new TerrainRenderer(device, terrain, shaders);

renderer.Upload(commands, view);
renderer.Record(commands, view);
```

⚠ **The shader takes no vertex buffer, and its reflection says so.** A regular lattice's positions are
two divisions of `SV_VertexID`, so uploading 33² of them per frame would be sending the shader
something it can count — `Terrain.reflect.json` has an empty `VertexInputs`. What is uploaded is the
index buffer, once, and one record per patch.

⚠ **`SampleLevel`, not `Sample`.** A vertex stage has no derivatives, so `Sample` outside a fragment
stage never meant what it looked like and SPIR-V was quietly substituting level zero. A terrain
heightmap is the case that motivated adding the explicit-level form.

⚠ **The normal is differenced at the patch's own step**, not at one sample. A normal taken at full
resolution on a coarse patch is the normal of geometry that patch does not have, which makes the seam
between two levels visible in the lighting even though the positions agree.

⚠ **The diagonal alternates in a checker.** Splitting every quad the same way makes a lattice of
parallel diagonals, which on a heightfield reads as corduroy — most visible where the ground is nearly
flat, which is where an artist looks hardest.

⚠ **The winding is counter-clockwise seen from above.** Getting it backwards produces a terrain that
is invisible from above and solid from below, which reads as nothing drawing at all rather than as a
winding problem.

## The morph

`TerrainLodTree.MorphIndex` is the whole of it: an odd grid index slides onto its even neighbour as the
morph goes to one, so a patch fully morphed has exactly half its resolution and its shared edge lands
on its coarse neighbour's vertices.

⚠ **A morph ratio of 1 is not a setting, it is a crack at every transition**, so `TerrainLodRanges`
refuses it and says why: a band with no width leaves the finer node undegenerate exactly where the
coarser one takes over.

⚠ **A morphed vertex has to read the heightmap bilinearly.** It lands between samples for every morph
but zero and one, and snapping to the nearest sample would reintroduce — in the thing that *reads* the
morph — the pop the morph exists to remove.

⚠ **The grid size is a permutation rather than a uniform**, so the vertex stage's two integer
divisions fold at compile time. For a power of two that is a shift and a mask.

## Uploading

⚠ **Only what changed is re-uploaded, and the first frame is a special case.** A terrain built and
then resolved has no dirty tiles at all, so a renderer copying only dirty rows draws a heightmap of
zeros until somebody happens to sculpt — which reads as a flat terrain rather than as a missing
upload. After the first frame a stroke on one tile of sixteen moves a fraction of the bytes.

⚠ **One heightmap for the whole terrain, not one per tile.** Per-tile textures exist for *streaming* —
a tile is the unit of load — and drawing wants the opposite: a patch straddles no tile boundary except
by luck, so a per-tile heightmap makes every straddling patch either two draws or a shader sampling
two textures. A 4 km² terrain at one metre is 4097² samples, which is 33 MB in `R16UNorm`.

## The generated splat material

`TerrainSplat.Of` reads the terrain's layer list and says what to compile: how many layer slots the
loop runs, and whether any layer wants the height path.

⚠ **Nobody wires a graph.** Every Unreal project rebuilds the same `LandscapeLayerBlend` material and
every one of them rebuilds it slightly differently, which is why "why is my landscape black" is the
most-asked landscape question and why the mapping-scale mistake is in the official quick-start guide
as a troubleshooting step. A configuration that cannot be miswired does not need one.

⚠ **Two permutation axes and no more.** The layer *count* quantises to 4/8/12/16, so a terrain gaining
a seventh layer does not compile a new shader. What is deliberately not a permutation is which mode
each layer uses: that is per layer and the permutation is per material, so eight layers with three
modes between them is one shader rather than eight.

⚠ **The height blend costs a second pass and could not avoid one.** It has to know the *highest*
contender at a fragment before it can say how much any layer contributes, and that is not known until
every layer has been looked at. `HeightBlend` off compiles the first pass out entirely.

⚠ **An empty slot gets a positive tiling, not zero.** The shader divides world XZ by it, and although
the loop's early-out should mean an empty slot is never reached, a divisor of zero inside a branch a
compiler decided to flatten is a NaN across the whole terrain.

## What is kept honest without a GPU

The morph's arithmetic is checked two ways: a source assertion that the expression is still in
`Terrain.rvn`, and a transliteration of it compared against `TerrainLodTree.MorphIndex` over every
index and every morph.

⚠ **A source assertion is weaker than an execution and is chosen knowing it.** It catches the failure
that actually happens — somebody edits or deletes the morph and every level boundary opens — and it
does not catch a subtly different but similar-looking expression. A golden image catches that, and it
needs a device.

## The atlas

The heights and the weights are one texture each, holding a `TileSamples²` block per tile, with a mip
chain. `TerrainAtlas` is the layout and it is device-free, so the arithmetic the shader's `AtlasUv`
transliterates has a test that needs no GPU.

⚠ **A split of the layout, not of the texture.** A tile is the unit of load, and a CDLOD patch
straddles a tile boundary except by luck — a texture per tile would make every straddling patch
either two draws or a shader sampling two textures. One texture with a block per tile is both: one
thing to bind, and a block to upload, evict and mip on its own.

⚠ **The blocks duplicate their boundary samples rather than sharing them.** The packed heightfield is
`TilesX × TileQuads + 1` wide because adjacent tiles share the sample between them; the atlas gives
each tile all `TileSamples` of its own. That costs 1.6% at a 128-sample tile and buys a block whose
size is a power of two starting at a multiple of one.

⚠ **Which is what makes the mip chain legal.** A 2×2 reduction of the atlas never crosses a block
boundary. Reducing the packed grid instead would mix two tiles' texels at every level.

⚠ **A boundary sample belongs to the upper tile** — `x / TileQuads` sends sample 127 of a 128-sample
tiling to tile 1 — and the lower tile still holds it in its last column. Two answers to that question
is a terrain that reads one block and was written into another.

⚠ **Heights reduce by the maximum and weights by the average.** A maximum on a weight makes every
layer cover everything one level up, so a distant terrain is every texture at once; an average on a
height sinks a ridge. The two quantities want opposite reductions and neither is a default.

⚠ **The weights are taken with `SampleGrad`, from the packed coordinate's derivatives.** An atlas
coordinate jumps by a whole block at every tile boundary, so the hardware's own derivative there is
enormous and it picks the coarsest level it has — a dark line one pixel wide along every tile edge,
which reads as a crack in the mesh.

⚠ **A patch reads the level its step implies.** `log2(step)`, clamped to the chain a *tile* has
rather than the one the atlas's own size would allow. Reading level 0 on a coarse patch gives it a
height nothing between its own vertices ever had, and the surface swims as the camera moves.

## The layer textures

`ITerrainTextures` is what turns a layer's texture reference into something the splat loop can
sample. A `.vxlayer` names its albedo and its surface map as strings, because a layer is content and
a reference in content is a name; turning a name into a handle is the asset database's job, and a
renderer that did it would need one in a class whose job is a draw call.

⚠ **Null is a working renderer.** Every layer slot is bound a default at construction, so a terrain
with no source draws its weights in white — which is what a freshly created layer should look like,
and what a headless test sees.

⚠ **The defaults are not arbitrary.** White albedo makes an unassigned layer read as "no texture
yet" rather than as a hole in the world, and the surface default's alpha is 0.5 — a flat blend
height, which makes a height blend degrade to a weight blend rather than to a hard edge.

⚠ **The source is asked every frame and the sets are rebound only when an answer changes.** A source
answers nothing for a reference it has not loaded, so the frame a layer is assigned is the frame its
texture is not resident; asking once would show the default for ever and blocking would drop whatever
the load took. Rebinding is what costs, so the comparison is what avoids it.

⚠ **A resized set is a new set.** Growing the patch buffer creates descriptor sets that have never
been written, so the layer arrays are written into them as they are made — otherwise the first view
that selects more patches than the buffer held silently reverts every layer to its default.

## Streaming the tiles

`TerrainStreamer` decides which tiles a frame pays for. It owns a `StreamingGrid` over the terrain's
tiles and a `PageResidency` over `TerrainTilePages`, whose page is one tile's whole mip chain and
whose bytes come from an `ITerrainTileSource` — `TerrainTileSource` for a terrain that is already in
memory, which is every terrain the editor is sculpting.

```csharp no-compile="a fragment; the description is the terrain's own"
var streamer = new TerrainStreamer(in description, new TerrainTileSource(terrain));

renderer.Streaming = streamer;
renderer.StreamingSources.Add(new StreamingSource(player.Position, 512f));
```

⚠ **Set it before the first `Upload`.** The pinned tail is written on the frame that first uploads,
so a streamer attached afterwards finds every tile already copied in full — a streamer that saves
nothing and reports numbers saying it did.

⚠ **The coarse tail of every tile is pinned, which is why a tile that has not arrived is a *coarse*
tile rather than a hole.** `TerrainStreamer.LevelOf` floors the level the quadtree chose instead of
rejecting the node. Dropping it is the obvious implementation and its symptom is a hole in the
distance on the frame a camera turns, which reads as the terrain failing rather than as a tile
loading.

⚠ **What streams is the upload, not the host bytes.** Both are true and only one is obvious: a
terrain being edited has an edit stack, so it is in memory by definition. The saving is the block
copies — the first frame of a 128×128-tile terrain is sixteen thousand of them without a streamer and
a few dozen with one. Getting the bytes off the heap needs a tile-addressable file, which is what
`ITerrainTileSource` is the seam for.

`TerrainTilePages.Drain` is the hand-over: a load comes back on a pool thread and a copy into a
texture needs the frame's command list, so the renderer drains what has arrived rather than the pool
recording anything.

## Examples

Selecting the patches a view needs, which is the half that runs without a device:

```csharp no-compile="a fragment; the ranges are a project setting"
var tree = new TerrainLodTree(terrain.Description, TerrainLodRanges.Default);
var nodes = new List<TerrainLodNode>();

tree.Select(view, nodes);
```

Each node becomes sixteen bytes:

```csharp no-compile="a fragment; `node` is one the selector produced"
var record = TerrainNodeRecord.Of(node, gridQuads: TerrainLodTree.DefaultGridQuads);
```

⚠ **`Origin + gridIndex × Step` is the sample the heightmap is read at**, and the far corner of the
patch is its far sample. That identity is what makes one lattice serve every level.

The whole frame is two calls and one draw:

```csharp no-compile="a fragment; the command list is the frame's"
renderer.Upload(commands, view);
renderer.Record(commands, view);
```

## Using it from a game

A game never constructs a `TerrainRenderer`. The scene carries the component, the frame document
names the node, and the two meet in the compositor:

```csharp no-compile="a fragment; OnConfigure runs before the host exists"
// In Game.OnConfigure — the compositor is built before OnInitialise runs.
config.Graphics.Factories.Add(new PostEffectFactory());
config.Graphics.Factories.Add(new TerrainFactory());
```

Registering `TerrainFactory` is the whole installation: the host recognises it and wires the world
renderer's `TerrainSceneSource` to it, `WorldRenderer.Register` adds the `TerrainExtractionSystem`
that walks `TerrainComponent` entities into that source every frame, and `WorldRenderer.Mount`
supplies the `AssetTerrainSource` that turns the component's reference into a heightfield — started
on first ask, answered "not yet" until the bytes land.

The scene's half is one component on one entity — `TerrainComponent.Of("Maps/Valley.vxterrain")`,
placed by the entity's transform — and the document's half is one node. In a `!StandardFrame` it
splices at the `afterOpaque` seam, which is exactly where opaque ground belongs: after the Main
pass, sharing its depth (reverse-Z, `Greater`), before the velocity and particle passes.

```yaml
game: !StandardFrame
  extensions:
    afterOpaque:
      - !Terrain {}
```

A hand-authored document places the same node itself and names its own targets — `output:`,
`depth:` and `view:` default to the standard frame's `SceneHdr`, `SceneDepth` and `Camera`. The
node draws every terrain the world carries; a world with none draws nothing quietly, while a target
or view nothing bound refuses with a `CompositorBindingException` naming the node and the name.

The node's nullable scalars — `grassDensityScale:`, `grassResidentCells:` and their siblings — are
the quality waterfall's seam: a written value is the document deciding, null falls through to
`TerrainFactory.Vegetation`, which is where a host lays down the numbers its resolved tier chose,
and the defaults are the engine table's High tier.

## Frame-lit shading

The node picks between two shaders, and **what the frame provides is the switch — there is no
toggle**. `Terrain` is the preview: a hard-coded sun over the splat, the variant the editor's
viewport embeds, and what any frame that publishes no lighting gets. `TerrainLit` is the same
geometry — one base shader, so the two cannot place a vertex differently — lit on the scene pass's
own terms, and the node chooses it exactly when the frame provides what it needs:

- a `SceneConstants` with a scene camera (`TerrainFactory` wires the builder's own instance);
- the cascade constants `ShadowMapRenderer.Publish` writes under the scene pass's name
  (`ForwardPlus.cascades[0].viewProjection` and its siblings — `scenePass:` renames the prefix);
- the cascade atlas as a declared resource (`shadowAtlas:`, canonically `ShadowAtlas`), which the
  node also declares a read on so the graph fences the shadow passes before the ground samples them.

What the lit ground then does, per fragment: the frame's sun direction and radiance, Lambert over
the splat albedo; the cascade shadow term — containment-based selection, edge blend, distance fade,
the bias **added** because under reverse-Z toward the light is numerically up; the sky's spherical
harmonics for ambient; and, when the frame publishes its culled cluster buffers
(`ForwardPlus.lightBuffer` and `ForwardPlus.clusters`), every clustered lamp that reaches the
fragment. `GrassLit` takes the sun, the cascades and the sky on the same terms, and lights blades by
their rotated up axis — a field is the ground, slightly furred, not thousands of tiny walls.

⚠ **No per-object light fallback, deliberately.** A frame that culls no lights gets a sun-and-sky
terrain: the eight-light per-object list is chosen per object, and a terrain is the biggest object
in any frame — the exact shape that list reorders worst on.

⚠ **The split planes follow the frame.** When the document declares `SceneAlbedo` and `SceneNormals`
(`albedo:` / `normals:` rename them), the node binds them as its second and third targets, loaded,
and the lit shaders write raw albedo and the **raw signed world normal** — `SceneNormals`' own
convention, where zero is the sky — while withholding diffuse ambient, which the `!AmbientCombine`
at the other end of the frame rebuilds from real irradiance and real occlusion. Screen-space GI, AO
and the combine then see the ground exactly as they see everything else.

## Shadow casting

A terrain with `TerrainComponent.CastShadows` — on by `TerrainComponent.Of`, off in a zeroed
component — draws into the sun's cascade atlas, so it self-shadows its own valleys and throws its
hills across everything else the frame shades. There is nothing to install beyond what lit shading
already asked for: `TerrainFactory`'s document transform inserts a caster node wherever a document
holds a `!Terrain` node and a `!ShadowMap` writing the atlas it samples, directly **after** the
shadow node — position is the point, because the graph runs passes in declaration order and a
caster pass declared where the terrain node builds (after the Main pass) would write depths the
frame had already sampled. Register the factory after `PostEffectFactory`, as the samples do: the
transform has to see the expanded frame, not the `!StandardFrame` preset that stands for it.

What the caster pass does: loads the atlas — never clears, the mesh casters' depths are in it and
reverse-Z `Greater` merges the terrain depth-correctly — then draws one tile per cascade under the
shadow node's own viewports, with the cascade's *unfolded* matrix read straight off that node
(`ShadowMapRenderer.Cascades`; the published, tile-folded form is for lookups). It rasterises on
the caster stage's conventions: back faces culled, zero raster bias — the frame's biases are added
in the sampling, in metres — and depth clamp where the device has it.

Three deliberate shapes worth knowing:

- **Coverage is the whole terrain at one coarse level, not the camera's node set.** A hill behind
  the camera still casts into the view, so the caster tiles the entire heightfield uniformly —
  at most 8×8 patches, the level rising with the terrain's size, floored to the streamer's pinned
  tail so every read is resident. Uniform also means no morph and no cracks by construction; a
  cascade's texels cannot see the boundary the morph exists for.
- **Holes cast no shadow.** The caster keeps a fragment stage for exactly one line: the hole mask's
  `discard`, because a cave mouth throwing the shadow of a solid hillside reads as a bug standing
  in front of the cave. That is the pass's whole fragment cost.
- **A terrain casts from its second frame.** The caster pass records before the surface's own
  upload pass runs, so a heightfield born this frame is skipped until its atlas has been copied
  once — one frame of latency per new terrain, paid once.

⚠ **The virtual shadow map does not receive terrain casters yet.** A frame running `shadows:
Virtual` A/Bs the map with the cascades — the map answers where it has a drawn page, the cascades
everywhere else — so terrain shadows appear wherever the cascades answer and are absent from drawn
pages, the mirror of terrain *receiving* (which is cascades-only on such frames too). Marking
terrain into the page passes is a tracked follow-up.

⚠ **Still owed:** grass as a caster (blades barely resolve at cascade texel sizes, and the scatter
is a per-camera compute whose output the shadow pass has no residency contract with — casting
terrain without grass is the visible 95 %), `LodBias` (carried, not yet consumed), motion vectors,
punctual shadow atlas sampling for the lamps, terrain in the virtual shadow map's pages as above,
and per-layer surface roughness reaching the lit BRDF — the splat stays diffuse until the surface
textures teach it otherwise.

## See also

- [The terrain heightfield](../engine/terrain-heightfield.md) — the samples this draws.
- [Meshes and materials](mesh-and-material.md) — the render feature vocabulary this fits into.
- [Culling and streaming instances](instance-culling.md) — the same residency seam, and what the foliage on this will use.
- [Painting a terrain](../engine/terrain-painting.md) — the weights and layer list this compiles from.
- [docs/plan/31 § D3](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  a quadtree with a morph rather than a clipmap, and the argument between them.
