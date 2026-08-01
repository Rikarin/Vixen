---
title: Element selection
slug: editor/element-selection
kind: guide
area: Editor
summary: Vertex, edge and face modes over one mesh — what a click takes, what a loop walks, and what survives an edit.
api: [T:Vixen.Editor.SceneView.MeshEdit, T:Vixen.Editor.SceneView.MeshGizmoTarget, T:Vixen.Editor.Blockout.BlockoutSelection, T:Vixen.Editor.Blockout.BlockoutGeometry]
tags: [editor, blockout, selection, mesh, viewport]
since: 0.1
status: preview
related: [editor/modes, editor/sub-object-picking, editor/mesh-editing, editor/shape-tool, editor/face-materials, engine/mesh-operations]
---

## What it is

`MeshEdit` is the state of editing one mesh: which entity, which element mode, what is selected in it
and which element the pointer is over. `MeshGizmoTarget` is that selection as something the transform
gizmo can drag. `BlockoutSelection` is the selection verbs — loop, ring, grow, shrink, by group,
coplanar, linked, all, none, invert. `BlockoutGeometry` is the geometry verbs — extrude, inset, bevel,
loop cut and the rest of doc 24's table — each one entry in the undo history.

The walks themselves are the kernel's, in `MeshTopology`, and the set is `MeshSelection`. What is here
is the half that needs a scene.

## What it is for

Selecting the wall rather than the building. Half of a blockout pass is "this face, that edge, those
four corners", and the whole of what the geometry verbs act on is whatever a mode said was selected.

You do not want it for choosing entities. `SceneDocument.Selection` is what the outliner, the
inspector and the object gizmo read; this is one level down and inside one mesh, and the two are
deliberately different sets with different lifetimes.

**One target, following the entity selection.** Exactly one entity selected is the mesh being edited;
anything else is none. The element indices of two meshes are two numbering schemes, so a selection
spanning both is one that no single operation can act on — which is the line every reference toolset
draws.

## Using it

Entering an element mode is what makes a parametric shape editable, and it is undoable:

```csharp no-compile="a fragment; the scene and its selection are the application's"
var editing = new MeshEdit(scene);

editing.Enter(MeshElementKind.Face);      // free: a parametric shape already has a cage
editing.Clicked(hit, additive: false);    // what a click in the viewport does
editing.Demote();                         // the D6 door, at the first edit, on the undo stack
```

⚠ **Entering a mode demotes nothing; the first *edit* does.** A shape built by the shape tool has a
real mesh from the moment it is created, so the cage is there and every element of it selects while
its parameters are still live. `MeshEdit.Demote` is doc 24's one-way door, it asks once a session, and
what says so afterwards is `SceneDocument.IsPlainMesh` — see [the shape tool](shape-tool.md).

⚠ **A position change keeps the selection and a topology change drops it.** Undoing a drag puts the
corners back and leaves every index meaning what it did; an extrude renumbers the tables, so an index
kept across one names a different element. `Reconcile` is what tells the two apart, from the table
sizes, and it costs one integer comparison when nothing has happened.

## Examples

Dragging elements goes through the gizmo, and the entity does not move:

```csharp no-compile="a fragment; the gizmo attaches these itself, once per frame"
var target = new MeshGizmoTarget(scene, editing);

target.Position += offset;   // the corners move; the entity's transform does not
```

⚠ **One target rather than one per position.** The gizmo recomputes every target from where it was at
mouse-down, so a hundred selected vertices as a hundred targets would rotate each of them about its
own centre — which is not what rotating a face means.

The selection verbs are functions, and every one of them declines quietly when it does not apply:

```csharp no-compile="a fragment; each of these is behind a registered command in `BlockoutMode`"
BlockoutSelection.Loop(editing);       // through the edge chosen last
BlockoutSelection.Coplanar(editing);   // the whole flat wall, however many faces it is
BlockoutSelection.Grow(editing);       // one step out, across shared edges
```

The geometry verbs are the same shape, with a document and a command behind each:

```csharp no-compile="a fragment; the distance comes from a drag or from the mode's step"
BlockoutGeometry.Extrude(editing, BlockoutGeometry.Local(editing, offset).Y);
BlockoutGeometry.Bevel(editing, width, segments, out var unresolved);
```

⚠ **What is selected afterwards is what the verb made.** Extruding a face and then moving it is one
gesture in every modelling tool there is; a verb that left the original selection would make the second
half act on the geometry the first half left behind.

⚠ **Amounts are in the mesh's own space.** An extrude of one metre on an entity scaled to a half is two
units in the mesh — `Local` is the conversion, and skipping it is invisible until somebody extrudes a
wall that had been scaled.

⚠ **Which way a loop cut runs is decided by the edge you picked.** The cut goes across the ring that
edge is part of, so picking a different edge gives the other direction. Asked for in *face* mode there
is no edge you picked — only a converted list — so it takes the lowest-numbered one, which is at least
the same answer every time. The hover preview that would let the pointer choose is not built.

⚠ **A verb can leave you in a different element mode, and the mode bar follows it.** Weld leaves a
vertex, bevel leaves faces. `MeshEdit.ElementChanged` is what keeps the segmented control, the keys
and the selection saying the same thing — without it the tool reads as having stopped responding until
you click another mode and come back.

⚠ **A loop asked for in face mode converts to edges rather than declining.** "Select loop" is a
statement about edges whatever mode you are in, and a key that did nothing in three of the four modes
is a key people conclude is broken.

## See also

- [Sub-object picking](sub-object-picking.md) — which element the pointer is on, which is what a click
  turns into a selection.
- [Editable meshes](../engine/edit-meshes.md) — the kernel's selection set and topology walks.
- [Mesh operations](../engine/mesh-operations.md) — the verbs `BlockoutGeometry` records on the undo
  stack.
- [Editor modes](modes.md) — where the element modes live and how they claim `1`–`4`.
- [The shape tool](shape-tool.md) — what makes the geometry these verbs edit, and the demotion rule.
- [Face materials](face-materials.md) — dressing a face selection rather than moving it.
