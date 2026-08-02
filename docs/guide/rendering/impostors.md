---
title: Impostors
slug: rendering/impostors
kind: guide
area: Rendering
summary: A tree photographed from a hemi-octahedral grid of directions once, offline, and drawn as two triangles for ever after.
api: [T:Vixen.Rendering.ImpostorGrid, T:Vixen.Rendering.ImpostorAtlas, T:Vixen.Rendering.ImpostorCell, T:Vixen.Rendering.ImpostorSample, T:Vixen.Rendering.ImpostorView, T:Vixen.Editor.Assets.Terrain.TerrainAssetImporter, T:Vixen.Editor.Assets.Terrain.TerrainAssetImportSettings, T:Vixen.Editor.Assets.Terrain.HeightmapImporter, T:Vixen.Editor.Assets.Terrain.HeightmapImportSettings, T:Vixen.Shaders.Generated.ImpostorKeys, T:Vixen.Shaders.Generated.ImpostorConstants, R:Terrain/Impostor, T:Vixen.Rendering.ImpostorBake, T:Vixen.Rendering.ImpostorBakeCell]
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

## What is owed

**The dilation and the mip build.** The gutter is left for a dilation pass to fill and the chain is
capped at `MipLevels`; neither runs yet, so an atlas straight out of the bake has a hard edge at each
cell's border and one level.

## See also

- [Drawing foliage](foliage-rendering.md) — the LOD levels an impostor is the last of.
- [Foliage instances](../engine/foliage.md) — the types that carry one.
- [docs/plan/31 § T7](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the phase this is, and doc 06's impostors row it closes.
