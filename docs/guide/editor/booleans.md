---
title: Booleans and handoff
slug: editor/booleans
kind: guide
area: Editor
summary: Non-destructive union, subtract and intersect whose operands stay editable — and what turns a block-out into an asset.
api: [T:Vixen.Editor.Blockout.BlockoutBoolean, T:Vixen.Editor.Blockout.BlockoutHandoff, T:Vixen.Editor.SceneView.SceneCsg, T:Vixen.Editor.SceneView.CsgNode, T:Vixen.Editor.SceneView.BooleanCommand, T:Vixen.Editor.SceneView.MeshExport, T:Vixen.Editor.SceneView.ExportPiece, T:Vixen.Editor.SceneView.IMeshBaker, T:Vixen.Editor.App.ProjectMeshBaker]
tags: [editor, blockout, csg, export, assets]
since: 0.1
status: preview
related: [editor/shape-tool, editor/element-selection, editor/face-materials, engine/mesh-booleans]
---

## What it is

`BlockoutBoolean` is doc 24's P6 against a scene: union, subtract, intersect, plane cut, trim, and the
apply that ends a derivation. `SceneCsg` and `CsgNode` are how a derived entity is held and rebuilt;
`BooleanCommand` is one on the undo stack. `BlockoutHandoff` is P7 — bake, export, import back and
collision — over `MeshExport` and an `IMeshBaker`, of which `ProjectMeshBaker` is the editor's.

## What it is for

Cutting a doorway by putting a box where the doorway goes, and then still being able to move the box.
And, when the shape is settled, turning the whole thing into an asset an artist opens.

You do not want a boolean for a hole you could inset and push. Two faces and a drag is one undo entry
and no derivation; a boolean is for the cut that is not a face.

**Non-destructive first, which is the whole point of the phase.** A subtract makes a new entity whose
geometry is derived from its operands; the operands become its hidden children and stay editable. "The
doorway should be twenty centimetres wider" is then dragging a box that is still there.

## Using it

```csharp no-compile="a fragment; the selection is the scene document's"
var result = BlockoutBoolean.Subtract(scene);   // two operands or more, in selection order

BlockoutBoolean.Collapse(scene, result);        // the destructive apply, when the shape is settled
```

⚠ **The operands are the result's *children* rather than a list of references.** A reference would be
a second way for one entity to name another, with its own lifetime rules — what happens when an
operand is deleted, what a duplicate does about them, what the outliner draws. Children answer all of
that for free.

⚠ **Order matters and it is the outliner's.** A difference is not commutative: the first thing
selected is the one being cut, and reordering the rows is how you swap which is which.

⚠ **Re-evaluation is pulled rather than pushed.** `SceneCsg.Refresh` compares one integer per node — a
hash of its operands' mesh versions *and transforms* — so a frame that changed nothing costs a
comparison per boolean. The transform is in the hash because moving a cutter changes the result
without changing a single vertex of it.

⚠ **Editing a face of a derived mesh collapses the derivation first.** The result is a function of its
operands, so an edit to it is one the next re-evaluation would overwrite without saying so. It is the
same one-way door a parametric shape has, and `MeshEdit.Demote` is both.

⚠ **The cuts are destructive and the booleans are not.** A plane cut's operand is a plane, which is
not an entity and has nowhere to survive as one; a trim's operand is an entity and is still
destructive, because throwing the cutter away is what a trim is for.

## Examples

The bake writes the file the artist will open, and points the entity at that:

```csharp no-compile="a fragment; the baker is the application's, because it owns the asset database"
BlockoutHandoff.Bake(scene, baker);                  // geometry out, MeshRenderable in
BlockoutHandoff.Editable(scene, meshes);             // and back again
```

⚠ **The bake and the export are one file.** The plan asks for a bake "through the existing importer
machinery", and that machinery reads OBJ — so writing the artist's file and pointing the entity at it
makes the thing in the level and the thing on the artist's disk the same bytes, rather than two
artefacts to keep in step. A mesh format of the editor's own would have been a second compiler.

⚠ **Geometry is baked in the entity's own space and exported in the world's.** An asset is something
the entity is an *instance* of, so a bake centred on the world would give a mesh that arrives offset
by wherever in the level it was standing. `MeshExport.Pieces` takes the centre as an argument and the
two callers differ only in that.

⚠ **What comes back from `Editable` is welded, not the mesh the artist authored.** A `MeshData` is one
vertex per corner and triangles only; the positions weld back into a graph and coplanar neighbours are
regrouped, but two triangles that were a quad are two triangles.

Collision is boxes, one per connected shell:

```csharp no-compile="a fragment; registering them needs a live PhysicsWorld"
BlockoutHandoff.Collision(scene, entity, boxes);
```

⚠ **Boxes rather than `ShapeDescription`s, and that is a layering decision.** A hull or a mesh is
registered *by its data* with a physics world, and the description holds the index it hands back — so
the thing that can turn these into shapes is the host that has a world.

## See also

- [Mesh booleans](../engine/mesh-booleans.md) — the classification, and why it is exact.
- [The shape tool](shape-tool.md) — what the operands usually are, and the other one-way door.
- [Face materials](face-materials.md) — a cut's reveal is a face group like any other.
- [Editable meshes](../engine/edit-meshes.md) — what `Validate` reports about a boolean's result.
