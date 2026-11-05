---
title: The paint tile store
slug: editor/paint-tile-store
kind: guide
area: Editor
summary: How a paint layer's pixels are held as sparse tiles, what a stroke costs, how a host evaluates a stack without an editor, and what a .vxpaint pack holds.
api: [T:Vixen.Texturing.Painting.PaintImageSnapshot, T:Vixen.Texturing.Painting.PaintTileCache, T:Vixen.Texturing.Layers.LayerStackEvaluator, T:Vixen.Texturing.Layers.LayerStackPictures, T:Vixen.TextureGraph.TextureRect, T:Vixen.TextureGraph.TextureRegions, T:Vixen.TextureGraph.TextureTileWrite, T:Vixen.TextureGraph.TextureImageChange, T:Vixen.TextureGraph.TextureLocality]
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

Nothing in it composites. What `PaintComposite` does in the editor's pane is a second opinion that
reproduces one blend mode of sixteen; a host on this class has no such gap.

## The file

A `.vxpaint` at version 3 is a pack: for each channel, its usage, its background, and each tile that
differs from the background, in order of position, each deflated on its own. An unpainted channel is
a header, and one picture is always the same bytes. Versions 1 and 2 still open, and the next save
moves them on. A tile out of order is refused.

## Examples

A stamp, repaired and read back, in a host with no editor:

```csharp
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
