---
title: Face materials
slug: editor/face-materials
kind: guide
area: Editor
summary: Per-face materials, UV projection, smoothing groups and the block-out checker the viewport draws by default.
api: [T:Vixen.Editor.Blockout.BlockoutSurfaces, T:Vixen.Editor.SceneView.MaterialCommand, T:Vixen.Editor.Core.Scenes.SceneFaceMaterialData]
tags: [editor, blockout, materials, uv, viewport]
since: 0.1
status: preview
related: [editor/shape-tool, editor/element-selection, editor/mesh-editing, engine/mesh-surfaces]
---

## What it is

`BlockoutSurfaces` is doc 24's Surfaces table run against a scene: assign a material to a face
selection, project its texture coordinates, transform them, and set its smoothing. `MaterialCommand`
is one assignment on the undo stack; `SceneFaceMaterialData` is what a `.vxscene` carries for it.

The arithmetic is [`MeshSurfaces`](../engine/mesh-surfaces.md)'. What is here is the half that needs a
scene: which asset, which entity, and which entry in the history.

## What it is for

Making a block-out read as a space rather than as a grey mass. Brick on the walls, metal on the
gantry, and a checker on everything nobody has dressed yet.

You do not want it for an authored mesh's material slots. Those are the model compiler's, and a
`MeshRenderable` names one material for the whole mesh.

**A material is assigned to a face's *group*, not to the face.** That is what face groups are for: a
wall's twelve faces after two bevels are still one wall, and an assignment remembered per face index
is one that the next loop cut renumbers out from under.

## Using it

```csharp no-compile="a fragment; the reference comes from a palette or the content browser"
BlockoutSurfaces.Assign(editing, brick);
BlockoutSurfaces.Project(editing, UvProjection.World);
BlockoutSurfaces.AutoSmooth(editing);
```

⚠ **Assigning a material does not demote a parametric shape and everything else here does.** The
assignment lives on the document beside the mesh, so regenerating the geometry from its parameters
leaves it exactly where it was — which lets a designer dress a corridor and still make it a metre
wider. A projection writes into the mesh's own corner layer, so a shape that stayed parametric would
lose it the next time anybody nudged a number.

⚠ **An empty selection means the whole object here and means nothing in `BlockoutGeometry`.** "Project
the UVs" has a sensible whole-object reading and "extrude" does not.

⚠ **`Regroup` is the explicit step between "these faces" and "this material".** A generator's groups
are the ones a designer wants nine times in ten — a staircase's treads, a doorway's reveal — and
splitting silently on assignment would make every material assignment a change to the mesh's
structure.

## Examples

Two materials on one mesh are two draws, and the viewport does the splitting itself:

```csharp no-compile="a fragment; the collector is the presenter's"
scene.SetMaterial(entity, MeshShapes.GroupTop, brick);

meshes.Build(scene);   // one batch per materialled group; one for a mesh with none
```

⚠ **One piece per group only when a group actually names a material.** A block-out is nearly all one
material, and splitting every wall into six pieces because a box has six groups would be six uploads
and six draws for one picture.

The checker is what an *unmaterialled* surface draws with, and its squares are the work plane's step:

```csharp no-compile="a fragment; the presenter keeps this in step with the grid"
meshes.Checker = viewport.Grid.Plane.Effective(spacing);
```

⚠ **World space, computed in the shader from the fragment's position.** A block-out box scaled 8×3
must not stretch its texels, and what makes proportion readable at a glance is a square that is the
same number of metres everywhere — so the checker is a function of the world position and the world
normal, and nothing about the object reaches it. That also means it needs no UV layout and no texture:
two lanes on the instance the shader's own README had been calling reserved.

⚠ **Filtered by the screen-space derivative, which is what "legible at grazing angles" costs.** A
checker sampled per pixel with no filtering becomes a shimmering moiré the moment a cell is smaller
than a pixel, which on a floor is most of the floor. Fading the contrast out as the cell shrinks below
a couple of pixels is what a mip chain does for a texture, in four instructions.

⚠ **The axis tint is small on purpose.** It is there so that a wall and a floor read as different
planes at a glance; a strong one makes every screenshot look like a debug view, which is what makes
people turn the whole thing off.

## See also

- [Mesh surfaces](../engine/mesh-surfaces.md) — the projections and the smoothing rule.
- [The shape tool](shape-tool.md) — what generates the groups these are assigned to.
- [Element selection](element-selection.md) — how a face selection is made in the first place.
- [Editable meshes](../engine/edit-meshes.md) — where a face's group and smoothing group live.
