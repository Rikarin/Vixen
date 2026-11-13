---
title: The paint tile store
slug: editor/paint-tile-store
kind: guide
area: Editor
summary: How a paint layer's pixels are held as sparse tiles, what a stroke costs, how a host evaluates a stack without an editor, and what a .vxpaint pack holds.
api: [T:Vixen.Texturing.Painting.PaintImageSnapshot, T:Vixen.Texturing.Painting.PaintMeshSurface, T:Vixen.Texturing.Painting.PaintTransfer, T:Vixen.Texturing.Painting.PaintView, T:Vixen.Texturing.Painting.PaintSymmetryGroup, T:Vixen.Texturing.Painting.PaintIsometry, T:Vixen.Texturing.Painting.PaintSizeSpace, T:Vixen.Texturing.Painting.PaintScatterAxis, T:Vixen.Texturing.Painting.PaintStampBlend, T:Vixen.Texturing.Painting.PaintPen, T:Vixen.Texturing.Painting.PaintPenAngle, T:Vixen.Texturing.Painting.IPaintPlacement, T:Vixen.Texturing.Painting.PaintStampTexels, T:Vixen.Texturing.Painting.PaintOcclusion, T:Vixen.Texturing.Painting.PaintFillMode, T:Vixen.Texturing.Painting.PaintFillOutline, T:Vixen.Texturing.Painting.IPaintSource, T:Vixen.Texturing.Painting.IPaintStencil, T:Vixen.Texturing.Painting.PaintAtlasSource, T:Vixen.Texturing.Painting.PaintSmudge, T:Vixen.Texturing.Painting.PaintFrame, T:Vixen.Texturing.Painting.PaintProjectedPicture, T:Vixen.Texturing.Painting.PaintTexelMap`1, T:Vixen.Texturing.Painting.PaintTileCache, T:Vixen.Texturing.Layers.LayerStackEvaluator, T:Vixen.Texturing.Layers.LayerStackPictures, T:Vixen.TextureGraph.TextureRect, T:Vixen.TextureGraph.TextureRegions, T:Vixen.TextureGraph.TextureTileWrite, T:Vixen.TextureGraph.TextureImageChange, T:Vixen.TextureGraph.TextureLocality]
tags: [editor, painting, material-authoring, texture-graph]
since: 0.1
status: preview
related: [editor/texture-graph-evaluation, editor/texture-graph-compiling]
---

## What it is

A `PaintImage` is a table of 256² tiles. A tile nobody wrote to does not exist and reads as the image's
background, so a 4K layer nobody painted holds one number.

`Snapshot` copies the table and marks every tile shared, and the next write to a shared tile clones it
first. `Restore` puts a table back. An undo entry is two tables, so it holds the tiles its stroke
changed, and a drag with mirrors cannot leave any of its paint behind, since nothing is undone texel
by texel.

## What it is for

Painting at 4K on a stack of twenty layers, with the composite under the brush being the plan's and not
an imitation of it. A stroke costs its tiles rather than its image, and an undo entry costs the tiles
it changed.

## What a stroke costs

A stroke reports the tiles it wrote through `TilesChangedSince`, and `PaintTileCache` uploads only
those. `TexturePlanEvaluator.EvaluateIncremental` takes a `TextureRegions` that says which external
images moved and over which rectangles. An op listed in `TextureLocality` then runs over those
rectangles alone, through `ICommandList.DispatchBase`, with the invocation ids a whole dispatch would
have given it. No kernel changes for this.

Only a plan's outputs stay whole pictures. They are repaired in the texture the last evaluation left.
The images between ops are read only over the rectangles they were written, so they rent pooled
textures, and a stack of twenty layers needs two or three of those.

An op that reads its neighbours, such as a blur, is not in the list. It runs whole and asks for its
inputs whole, so a stroke under one costs the chain above the stroke. The result is the same.
`LayerStackEvaluatorDeviceTests` compares a repaired picture to a clean evaluation byte for byte, for
strokes, undos and redos.

⚠ A host must keep a canvas for as long as it has strokes no file holds, so a `PaintCanvasStore` under
a long session needs a budget it cannot reach.

## Using it

`LayerStackEvaluator` holds a stack's plan, its uploads and a `PaintTileCache`. `Prepare` compiles only
when the structure changed, and `Evaluate` repairs what moved. `EvaluateClean` shares nothing with any
earlier evaluation, which is what an export owes and what a repair is checked against. A host fills
externals that are not painted pixels through a `LayerStackPictures`.

Nothing in it composites: what is under the brush is what the plan produced. The editor's paint panes
are a host on this class, and a host with no device shows the painted layer's own pixels and says so.
`TextureBake.RepairOf` names the rectangles of an output a repair rewrote, for a host keeping a copy.

## The file

A `.vxpaint` at version 3 is a pack: for each channel, its usage, its background, and each tile that
differs from the background, in order of position, each deflated on its own. An unpainted channel is
a header, and one picture is always the same bytes. Versions 1 and 2 still open, and the next save
moves them on. A tile out of order is refused.

## Examples

A stamp, repaired and read back, in a host with no editor:

```csharp no-compile="needs a device and a compiled stack"
using var run = new LayerStackEvaluator(evaluator, canvases, stackPath);

run.Prepare(stack, stack.Sets[0], library.Registry, library.SubGraphs);
run.Evaluate().Dispose();

var dirty = stroke.MoveTo(new Vector2(512f, 512f));

using var bake = run.Evaluate();
var map = run.Outputs.First(output => output.Usage == "baseColor").Image;
var texels = bake.ReadTexels(map, [new(0, 0, 256, 256)]);
```

## See also

- [Evaluating a texture graph](texture-graph-evaluation.md)
- [Compiling a texture graph](texture-graph-compiling.md)

## A stamp on a mesh

`PaintMeshSurface` lays a `PaintProjection` over an atlas. Its `Coverage` says which texels are surface, and a
`PaintStroke` given one of its placements measures each texel by where it is on the mesh rather than in the atlas, which
is what makes a seam nothing special: both sides of one are the same place.

`Through(view, symmetry)` is a brush seen through a camera. A stamp's centre is a position on the picture, `PaintView`
turns it into a ray, each triangle near the hit is clipped to the square the stamp makes on the picture, and a texel
weighs what the brush weighs where it projects. So the disc on the screen stays a disc on any island, and a separate
shell under the brush takes paint with the rest. A face turned away is skipped, depth is a limit, and within it
a texel the camera cannot see is left alone: `PaintOcclusion` tests it against the depth of the nearest surface along the
lines of sight round it, cast once per cell of the picture for the whole stroke, with a tolerance for something lying a
hair in front of it. Within a cell of a silhouette the far side is still let through; `PaintOcclusion.None` is the old
behaviour.

`AcrossSeams()` is a brush in the atlas. It paints the disc as it always did, then floods from the triangle under the
stamp across edges the mesh really shares, carrying the stamp on to the other islands by distance along the surface. It
does not reach a surface it is only near, such as the far side of a thin wall.

Both take a `PaintSymmetryGroup`: a mirror through a plane, or two to 64 turns round an axis, each a `PaintIsometry`,
through whatever point the host names, so a model off its own origin mirrors onto itself. Through a camera, each copy is
the camera moved with it, so a copy is never hidden behind the model. In the atlas, a copy finds the stamp's place on the
surface, moves it, starts from the nearest surface there, and weighs each texel in the copy's turned frame, so a
mirrored alpha stays mirrored. A copy whose place is off the mesh paints nothing.

`PictureRadius` and `AtlasRadius` turn a radius in texels or in the model's units (`PaintSizeSpace`) into the stroke's
own, measured on the triangle under where it starts; the stroke keeps that radius, as a projector keeps the one it
measures at pointer-down.

A brush's `Aspect` and `AspectAngle` make an elliptical stamp of the same area. Through a camera that is the author's,
stretched in the picture, and the square a stamp is clipped to reaches its long semi-axis. `FlowJitter` takes from a
stamp's flow on a hash stream of its own, `PaintScatterAxis.Across` scatters only across the path, and
`PaintStampBlend.Maximum` keeps the strongest stamp at a texel rather than laying each over the last, so a stroke at a
quarter flow is a quarter however slowly it is dragged.

Either way a stroke dilates each island it reached separately, into the gutter of `PaintCoverage`, and reports one
rectangle per island, since the union of two is everything between them.

A pen is a `PaintPen`: its pressure, how far it leans and which way on the picture, and its barrel's turn. `MoveTo`
and `Dab` take one, and each stamp a move earns takes its share of the way from the last pen, its angles the short way
round. `TiltSize` takes the radius down as the pen stands up, and `PaintPenAngle` turns the whole stamp, its stretch
with its alpha, by the barrel or by the lean. A move given a pressure alone is an upright, unturned pen.

`Fill(mode, view)` is a polygon fill: each stamp is a click, given to `PaintStroke.Dab` so that two clicks are two places
rather than a path, and it fills the piece of the mesh under the click whole, at a weight of one under the stroke's
opacity. `PaintFillMode` says how much: the triangle, the flat polygon it is part of (found, as the triangles joined to
it on the same island in the same plane, since a mesh here is triangles), every triangle of the mesh it came from
(`PaintProjection.PartOf`, one part per `MeshData` it was opened from), or its whole island, the UV shell.

A fill can also take an outline instead of clicks: `Fill(mode, view, symmetry, outline)` with a `PaintFillOutline` (a
rectangle, or a lasso closed from its last corner to its first) fills, from one stamp, every triangle the outline holds,
each grown as the mode says. A triangle is held when its middle, or a point halfway from there to a corner, falls inside,
and through a camera only where the camera sees that point unless the outline is `Through`. A symmetry copies a fill as
it copies a brush: through a camera each copy is the camera moved, and in the atlas each triangle chosen also takes the
one its middle, moved, lands on.

A stroke's colour can come from somewhere other than one colour. An `IPaintSource` says what each texel mixes towards,
and an `IPaintStencil` how much of the brush gets through where each texel lands on the stroke's picture; every
placement reports that place (`PaintStampTexels.Pictures`), through a camera where the texel projects and in the atlas
its own centre. `PaintAtlasSource` lays a picture over the atlas texel for texel at an offset, which is a material's map
painted as it is or the composite read from elsewhere, a clone. `PaintProjectedPicture` is a picture fixed on the screen
by a `PaintFrame` (moved, scaled, turned, perhaps tiled): as a source each texel takes its colour, and as a stencil its
brightness masks the brush. `PaintSmudge` reads the canvas being painted, at the step the brush just took in the atlas,
so each stamp drags what lies behind it; it is read for the whole stamp before any of it is written, and moves what is
there rather than mixing from what the texel held before the stroke. It keeps what it has laid unrounded until the
stroke ends, since a step of a fraction of a texel moves a smooth picture by less than one level of eight bits: rounded at
every stamp, a fine spacing smeared nothing and a coarse one smeared in steps along its edge. A dilated texel takes the colour of the texel it
grew from as it takes its reach.

The per-texel numbers of a stroke live in `PaintTexelMap`, a tiled sparse array, because a hash table of millions of
entries is a cache miss per lookup.

## Paint on a new layout

`PaintTransfer.Move` lays a canvas out on another mesh's atlas by where each texel sits on the surface: each texel of the
new atlas finds its point on the new mesh, the nearest point on the old mesh facing the same way, and reads the old atlas
there, so repacked islands and a remeshed model are one case. Surface the old mesh never had reads as the background.
`PaintTransfer.SameLayout` says first whether the two atlases are laid out alike, in which case the paint already fits
and moving it would only resample it.
