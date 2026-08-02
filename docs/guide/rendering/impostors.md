---
title: Impostors
slug: rendering/impostors
kind: guide
area: Rendering
summary: A tree photographed from a hemi-octahedral grid of directions once, offline, and drawn as two triangles for ever after.
api: [T:Vixen.Rendering.ImpostorGrid, T:Vixen.Rendering.ImpostorAtlas, T:Vixen.Rendering.ImpostorCell, T:Vixen.Rendering.ImpostorSample, T:Vixen.Rendering.ImpostorView, T:Vixen.Editor.Assets.Terrain.TerrainAssetImporter, T:Vixen.Editor.Assets.Terrain.TerrainAssetImportSettings, T:Vixen.Editor.Assets.Terrain.HeightmapImporter, T:Vixen.Editor.Assets.Terrain.HeightmapImportSettings, T:Vixen.Shaders.Generated.ImpostorKeys, T:Vixen.Shaders.Generated.ImpostorConstants, R:Terrain/Impostor, T:Vixen.Rendering.ImpostorBake, T:Vixen.Rendering.ImpostorBakeCell, T:Vixen.Shaders.Generated.ImpostorFinishKeys, T:Vixen.Shaders.Generated.ImpostorFinishConstants, R:Terrain/ImpostorFinish, T:Vixen.Rendering.ImpostorCapturePass, T:Vixen.Rendering.ImpostorMesh, T:Vixen.Shaders.Generated.ImpostorCaptureKeys, T:Vixen.Shaders.Generated.ImpostorCaptureConstants, R:Terrain/ImpostorCapture]
tags: [impostors, billboards, foliage, lod, far-field, importers]
since: 0.1
status: preview
related: [rendering/foliage-rendering, engine/foliage, engine/grass, rendering/instance-culling]
---

## What it is

`ImpostorGrid` is the set of directions a mesh is photographed from, folded onto a hemi-octahedron.
`ImpostorAtlas` is where each of those photographs lives in one texture. `ImpostorView` is the camera
one cell is baked with. `Impostor.rvn` is what draws the result.

## What it is for

The far field. A tree at four hundred metres is a few pixels of silhouette, and drawing forty
thousand triangles for it is what a forest to the horizon costs. An impostor replays a photograph
instead — and because the photographs are orthographic, one atlas serves every distance.

You do not want it near the camera. An impostor has no parallax within itself and no silhouette
detail beyond its resolution; it is the *last* level of a LOD group, and the ones above it are
`MeshSimplifier`'s job over the mesh the foliage type already names.

## Using it

```csharp no-compile="a fragment; the bounds come from the mesh"
var grid = new ImpostorGrid(side: 9);
var atlas = new ImpostorAtlas(grid, cellSize: 128, padding: 4);

for (var z = 0; z < grid.Side; z++) {
    for (var x = 0; x < grid.Side; x++) {
        var view = ImpostorView.For(grid, new(x, z), centre, radius);
        // render the mesh with view.View × view.Projection into atlas.RectOf(new(x, z))
    }
}
```

⚠ **A *hemi*-octahedron, and it is a different fold rather than half of `OctahedralMap`'s.** Nobody looks at a tree from underneath, and a full-sphere
grid spends half its atlas on views a forest never shows — at the resolutions an impostor is worth
having, that is the difference between an 8×8 grid and a 12×12 one.

⚠ **The grid is odd-sided so a cell sits exactly overhead.** Straight down is where a top-down view
spends its whole time, and an even grid puts a seam there — four cells blended for the one direction
that ought to be a single photograph.

⚠ **A direction from below is folded onto its mirror, not clamped to the horizon.** A camera that
dips a degree under a hillside tree keeps the view it had; a clamp would slide it round the equator.

## The blend

⚠ **Three cells, not one.** Snapping to the nearest view makes an impostor rotate in visible steps as
the camera moves, and for a forest it is worse than for one object because every tree steps on a
different frame. The quad the direction lands in is split on its diagonal and the triangle it falls
in supplies three corners, weighted barycentrically — so the weights sum to one everywhere, including
across a cell boundary.

⚠ **Three rather than four.** Four views of a tree averaged together is a blur; three that share a
triangle are the smallest set whose weights are still continuous.

## The atlas

⚠ **Every cell is padded, and the padding is not optional.** A bilinear tap near the edge of a cell
reaches into its neighbour, which at four hundred metres is a tree wearing a stripe of the tree next
to it. The gutter is what the bake dilates into.

⚠ **The mip chain stops at the cell size.** A mip that mixes two cells is the same bleed the padding
exists to stop, arriving through a different door — so `MipLevels` is how many are *safe* rather than
how many fit. A 9×9 grid of 128-texel cells stops at eight levels, not the eleven its 1152-texel
resolution would allow.

## The bake camera

⚠ **Orthographic, and that is the whole reason an impostor works.** A perspective bake fixes the
distance the mesh was photographed from into the texture, so an impostor drawn nearer or further
shows the wrong parallax. Orthographic is direction-only, which is what a billboard replays.

⚠ **One radius for every cell, from the bounding sphere.** Fitting each view's own extent would pack
the atlas better and would make the impostor breathe as the blend moves between cells, because the
same vertex would be a different number of texels from the centre in each.

⚠ **The overhead cell has no side**, so its up vector falls back to a horizontal axis — or the bake
produces a NaN for the one view a top-down camera never leaves.

## The importers

The same page covers `TerrainAssetImporter` and `HeightmapImporter`, because they are what turn the
rest of this toolset's authored files into assets.

⚠ **Their real work is validation, not conversion.** A `.vxlayer`, `.vxfoliage`, `.vxgrass` and
`.vxspline` are already YAML in the engine's own dialect. What the generic native importer cannot do
is *read* them — and running each type's own `Validate()` turns "the grass never grew" from a bug
report into a message beside the file that caused it.

⚠ **A refusal is an error and a suspicion is a warning.** A spline with one control point cannot be
built; a foliage type with no mesh is an author part-way through, and failing a build over one is how
a toolset earns a reputation for getting in the way.

⚠ **A 16-bit PNG heightmap is refused rather than imported at eight bits.** The decoder this build
ships reads every PNG at eight bits a channel, and a heightmap through it is a terrain quantised to
256 heights — which does not look like a broken import, it looks like a faint terrace on every slope
and gets attributed to the generator. Raw `.r16` is lossless and is what the importer takes.

⚠ **A raw file carries no header**, so the dimensions and the endianness are settings. Zero means
"work it out from the length", which is right whenever the file is square — and heightmaps come out
of every terrain generator square.

## Examples

Reading which views a camera direction is made of:

```csharp no-compile="a fragment"
Span<ImpostorSample> samples = stackalloc ImpostorSample[3];

grid.Blend(Vector3.Normalize(camera.Position - tree), samples);
```

Where a cell's own view lands in the atlas:

```csharp no-compile="a fragment"
var uv = atlas.UvOf(samples[0].Cell, quadUv);
```

## The bake

`ImpostorBake` owns the atlas textures and the depth target, and records the whole bake: one render
pass, one viewport per cell, and a callback that draws.

⚠ **One render pass for the whole atlas, not one per cell.** A 9×9 grid is eighty-one cells; a pass
each would clear and store a 1152-texel target eighty-one times, which on a tiler is eighty-one
full-frame resolves to bake one tree. The clear happens once and the viewport moves.

⚠ **It does not know what a mesh is, and that is the seam.** The caller draws — it owns the pipeline,
the vertex and index buffers and the material — and what the bake supplies is the camera and the
rectangle. A baker that bound a mesh would need an asset database in a class whose job is a render
pass.

⚠ **`ImpostorAtlas.RectOf` already excludes the gutter.** Padding it again draws the tree into the
middle four-fifths of its cell, which is not wrong enough to look wrong — it is a silhouette a few per
cent small, uniformly, which reads as the impostor sitting at a slightly different distance than the
mesh it replaces.

⚠ **Depth is cleared to zero, which is *far*.** The engine's convention is reversed-Z; clearing to
one is the classic mistake and produces an atlas that depth-tests away entirely — eighty-one blank
cells and no error anywhere.

⚠ **The albedo is cleared to transparent black.** The alpha is the silhouette, so a cell cleared to
an opaque anything draws a square.

## What is photographed

`ImpostorBake.Record` takes the draw as a delegate, because a bake renders the mesh with whatever
material the caller has — and for a long time nobody passed one, so the bake had no caller at all.
`ImpostorCapturePass` is the pipeline that fills it, over `ImpostorCapture.rvn`.

```csharp no-compile="a fragment; the shaders are ImpostorCapture.rvn's two stages"
using var capture = new ImpostorCapturePass(device, vertex, fragment, atlas.Grid.CellCount);

capture.Bake(commands, bake, new ImpostorMesh(vertices, indices, indexCount, centre, radius));
```

⚠ **Two render targets, which is why this is its own shader and not the block-out one.** An impostor
with no normals is a flat cut-out: the far field still receives the sun, and a billboard that cannot
say which way it faces shades as a card that changes with the time of day and never with its own
shape. Raven gives a fragment stage that returns a struct one `SV_Target` per field, which is what
makes this two targets rather than two passes over the same triangles.

⚠ **The albedo is a constant, and that is a stated simplification.** A tree is bark and leaves — two
materials over one mesh — and a faithful bake draws it once per material through the material system
the level uses. What this produces is the correct *silhouette* with a flat colour, and the silhouette
is what a forest four hundred metres away reads.

⚠ **Two-sided, and here that is unarguable.** A leaf card is a single quad and half a tree's cards
face away from any given cell; culling them photographs a tree with holes in it, and the holes then
blend into the far field for ever.

⚠ **One constant block per cell, at an aligned offset.** Each cell is a different camera. One block
rewritten per draw would bake every cell with whichever camera was written last — sixty-four
photographs of a tree from one angle, in an atlas whose whole purpose is that they differ.

⚠ **A vertex with no normal is given `+Y` rather than a zero.** A zero normalises to a NaN, and a NaN
in the normal atlas survives the dilation and the whole mip chain: one bad vertex turns a whole
impostor black at a distance, a very long way from the vertex that caused it.

⚠ **What is still owed is the orchestration**, not the bake: loading a foliage type's mesh out of the
project, running this over it and writing the atlas back as a texture asset. That is a content-build
step, and the content build has no device.

## Finishing it

`ImpostorFinish.rvn` is two phases of one shader, and `ImpostorBake.Finish` records them for both
atlases.

⚠ **Dilate first, then reduce, and the order is the whole point.** Reducing an undilated level
averages the silhouette's edge with transparent black, so the fringe the dilation exists to remove is
baked into every level below — and each level halves it into a wider band. Dilating afterwards would
fix level 0 and nothing else.

⚠ **The dilation copies the colour and not the alpha.** The gutter has to stay transparent; what the
fringe comes from is the *colour* a zero-alpha texel contributes to a bilinear blend. Black next to a
leaf darkens the leaf's edge, and the leaf's own colour next to it does not.

⚠ **The reduce is alpha-weighted, which a box filter is not.** Averaging a leaf with the transparent
texel beside it gives a colour half way to whatever the gutter holds; weighting by coverage gives the
leaf's colour at half coverage, which is what a smaller version of the same silhouette looks like.
The alpha itself is the plain average, because that *is* the coverage.

⚠ **Every tap is clamped to its own cell.** The atlas is a grid of independent photographs, and a
filter that walked across a boundary would put one view of the tree into another — at four hundred
metres, a tree wearing a stripe of itself seen from a different angle.

⚠ **A dispatch per level, not one with a loop.** A level cannot be read until the whole of the level
above it is written, and a workgroup can only wait for itself.

⚠ **A view per level, because a storage image is a view of one level.** A single view of the chain
makes every dispatch write level 0 — a chain of identical levels, invisible until something minifies.

⚠ **Finishing is separate from constructing, because a bake without it is still a bake.** The atlas
is legible with one level and an empty gutter; a caller that only wants the photographs should not
have to compile two compute variants to get them.

## See also

- [Drawing foliage](foliage-rendering.md) — the LOD levels an impostor is the last of.
- [Foliage instances](../engine/foliage.md) — the types that carry one.
- [docs/plan/31 § T7](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the phase this is, and doc 06's impostors row it closes.
