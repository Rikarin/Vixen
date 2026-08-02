---
title: The shape tool
slug: editor/shape-tool
kind: guide
area: Editor
summary: Making block-out geometry — live parameters, the cube grid, the poly shape, and duplicate, mirror and array.
api: [T:Vixen.Editor.Blockout.BlockoutCreate, T:Vixen.Editor.Blockout.ShapeDrag, T:Vixen.Editor.Blockout.ShapeStage, T:Vixen.Editor.Blockout.BlockoutCubeGrid, T:Vixen.Editor.Blockout.GridBox, T:Vixen.Editor.SceneView.ShapeCommand, T:Vixen.Editor.SceneView.SceneClone, T:Vixen.Editor.Core.Scenes.SceneShapeData]
tags: [editor, blockout, level-design, viewport]
since: 0.1
status: preview
related: [editor/modes, editor/element-selection, editor/mesh-editing, editor/face-materials, engine/blockout-shapes, editor/booleans]
---

## What it is

`BlockoutCreate` is doc 24's Creation table: shapes with live parameters, the poly shape, and
duplicate, mirror and array. `ShapeDrag` is the two-stage gesture behind it — drag a footprint on the
work plane, then drag the height. `BlockoutCubeGrid` and `GridBox` are the cube-grid tool, which
counts in whole cells. `ShapeCommand` is a parameter change on the undo stack, `SceneClone` is the
component-wise subtree copy a duplicate is made of, and `SceneShapeData` is what a `.vxscene` carries.

## What it is for

Building the level rather than modelling it. The overwhelmingly common block-out edit is *"that
corridor should be a metre wider"*, which is one number — so a shape keeps the numbers it was made
from and editing one rebuilds it.

You do not want it for a mesh an artist authored. A shape here is generated, and everything about it
is arranged so that it can be thrown away and replaced.

**A parametric entity has both a mesh and its parameters.** The mesh is what draws, picks, selects and
gets edited; the parameters are what an inspector shows and what rebuilds the mesh when one changes.
That pairing is why entering an element mode on a shape costs it nothing.

## Using it

```csharp no-compile="a fragment; the scene is the application's open document"
var wall = BlockoutCreate.Shape(scene, ShapeKind.DoorFrame, at: new(0f, 0f, 5f));

BlockoutCreate.Resize(scene, wall, scene.ShapeOf(wall)!.Value with { Inner = 0.35f });
```

⚠ **The one-way door is the first *edit*, not the first mode change.** Pressing `4` to look at a
shape's faces throws nothing away; extruding one of them does, and `MeshEdit.Demote` is where the
confirmation is asked. It is asked once per session and never again — a dialog on every wall is one
people learn to dismiss without reading. What tells them afterwards is
`SceneDocument.IsPlainMesh`, which is the badge and is derived rather than recorded.

⚠ **Consecutive parameter changes merge into one history entry.** Dragging a width field is one
decision made over forty frames, and forty entries for it is the shape of every "undo did not undo
what I did" report. A *demotion* never merges, because it is a different kind of act.

⚠ **Sizes are in world units and the transform stays uniform.** Nothing here writes a non-uniform
scale, which is what keeps a later bevel the same width on every axis and a later projection
unstretched.

## Examples

The gesture, which is world points in and an entity out:

```csharp no-compile="a fragment; the pane turns a pointer into a point on the work plane"
drag.Begin(corner);        // press
drag.Drag(opposite);       // move — the entity exists from here
drag.Settle();             // release; now ShapeStage.Height
drag.Raise(3f);            // move again, no button held
drag.Commit();             // click
```

⚠ **The entity exists from the moment the footprint does.** Drawing a preview and creating something
at the end would be two representations of one shape and a preview that can disagree with the result.
`Cancel` undoes back past the create, so `Escape` leaves nothing behind.

⚠ **A completed drag is two history entries, not one, and that is a known papercut.** The create is
one and the sizing that follows merges into a second, so the first `Ctrl+Z` after a drag leaves a
small box rather than nothing. Folding them would mean holding a transaction open across the frames
of a gesture, and `CommandStack.Undo` refuses inside one — so a `Ctrl+Z` mid-drag would throw instead
of doing nothing. It is stated here rather than left to be discovered.

The cube grid counts in cells, and stays parametric while it does:

```csharp no-compile="a fragment; the plane is the application's own WorkPlane"
var floor = BlockoutCubeGrid.Create(scene, new GridBox(-4, 0, -5, 8, 1, 10), plane);

BlockoutCubeGrid.Push(scene, floor, axis: 1, positive: true, cells: 3, plane);
```

⚠ **Integers, and that is the point rather than an implementation choice.** A box whose extents are
cell counts cannot drift off the grid, cannot be a quarter of a cell wide after nine pushes, and lines
up with the box beside it exactly. It makes ordinary `ShapeKind.Box` shapes, so every other verb works
on one unchanged.

⚠ **Corner mode is where a cube-grid box stops being one.** Pulling one corner down a cell makes a
ramp or a wedge, which is not a box's three extents — so `BlockoutCubeGrid.Corner` demotes, and the
cell quantisation is what survives.

⚠ **A poly shape is a plain mesh from birth.** Its parameters would be a polygon of arbitrary length
and a height, which is not six numbers — so it would need a record of its own in the scene format and
a gesture of its own to edit. What a designer does to one afterwards is move its corners.

⚠ **Mirror copies and never instances.** An instance is a link that survives editing, which is a
second kind of entity reference and a rule for what happens when one side is edited. A copy is what a
block-out pass actually uses, because the two halves stop being symmetrical about ten minutes later.

## See also

- [Block-out shapes](../engine/blockout-shapes.md) — the generators, and what each parameter means.
- [Element selection](element-selection.md) — the modes and the verbs that edit what this made.
- [Face materials](face-materials.md) — dressing a shape without losing its parameters.
- [Editor modes](modes.md) — where the creation verbs are registered and how they claim their keys.
