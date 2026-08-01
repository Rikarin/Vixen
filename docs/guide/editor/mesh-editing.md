---
title: Editable meshes in a scene
slug: editor/mesh-editing
kind: guide
area: Editor
summary: How an entity comes to carry an editable mesh, how it is saved, and how an edit is undone.
api: [T:Vixen.Editor.SceneView.EditMeshes, T:Vixen.Editor.SceneView.EditMeshCommand, T:Vixen.Editor.Core.Scenes.SceneMeshData]
tags: [editor, mesh, blockout, scene, undo]
since: 0.1
status: preview
related: [engine/edit-meshes]
---

## What it is

Three things that put `Vixen.Geometry`'s kernel into a scene. `EditMeshes` is the two copies the
kernel deliberately does not make — into `MeshData` for a renderer, and into the scene file's flat
lists. `SceneMeshData` is what a `.vxscene` carries. `EditMeshCommand` is one undoable edit.

## What it is for

The kernel is arithmetic over arrays and knows nothing about documents, files or undo. Everything that
makes an edited mesh part of a *project* lives here, which is what keeps the kernel testable as
functions and reusable at run time.

You do not want any of this for an imported mesh asset. This is for geometry that belongs to one
entity in one scene — a block-out is level data rather than a shared asset, and a designer who had to
save six meshes to disk to try a corridor has been given the DCC round-trip back under a different
name.

## Using it

An entity gets a mesh from the document, and gives it up the same way:

```csharp no-compile="a fragment against a live scene document"
scene.SetMesh(entity, EditMeshes.From(PrimitiveKind.Cube));

var mesh = scene.MeshOf(entity);   // the mesh itself, not a copy — editing is what it is for
```

**The trip out is not the trip in reversed.** Going in, twenty-four drawing vertices weld to eight
positions. Coming out, eight positions expand to one vertex per corner again — because a normal
belongs to a corner, and a cube drawn from eight shared vertices is a cube lit as a very lumpy sphere.

⚠ **A converted mesh is flat shaded**, which is right for a block-out and wrong for a converted
sphere. Smoothing groups are what fix it.

## Examples

Undo has two granularities and they are not interchangeable:

```csharp no-compile="a fragment; both are built *after* the edit has been applied"
scene.Stack.Execute(EditMeshCommand.Moved(scene, entity, positions, was));   // a drag
scene.Stack.Execute(EditMeshCommand.Rebuilt(scene, entity, before, "Extrude"));   // a topology change
```

A position change records the positions it touched and merges with the drag before it, so a drag over
three hundred frames is one entry in the history — and it undoes to where the *first* of them started
rather than to one frame ago. A topology change records the whole mesh, before and after, because a
boolean has no inverse to record and an undo implemented as an inverse operation is a second
implementation of every tool that will disagree with the first.

⚠ **The recorded meshes are deep copies.** A command holding the live object would record a "before"
that changes under it — an undo that puts things back where they already are, which is exactly what a
randomised do/undo/redo suite exists to catch.

The file's shape is four flat lists — positions, a position index per corner, a corner count per face,
and a group per face. Positions go through the registered `Vector3` converter, which writes at
round-trip precision, so a scene saved, opened and saved again is the same bytes. ⚠ **Corner counts
rather than start offsets**, so a file that has lost a line in a bad merge is a short mesh rather than
one whose last face reads off the end of the corner list — and the faces that do add up still load.

## See also

- [Editable meshes](../engine/edit-meshes.md) — the kernel itself, and why it is not a half-edge.
- [docs/plan/24 § P1](https://github.com/Rikarin/Vixen/blob/master/docs/plan/24-blockout-tools.md) —
  the phase, and its warning about where a scene format goes wrong quietly.
