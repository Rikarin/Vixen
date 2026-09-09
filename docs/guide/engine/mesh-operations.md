---
title: Mesh operations
slug: engine/mesh-operations
kind: guide
area: Engine
summary: Extrude, inset, bevel, loop cut, knife, bridge, weld, dissolve — the modelling verbs, as functions over a mesh.
api: [T:Vixen.Geometry.MeshOperations, T:Vixen.Geometry.MeshLoop, T:Vixen.Geometry.KnifeCut]
tags: [geometry, mesh, blockout, modelling]
since: 0.1
status: preview
related: [engine/edit-meshes, editor/element-selection, engine/blockout-shapes, engine/mesh-surfaces, engine/mesh-booleans, engine/quad-remeshing]
---

## What it is

`MeshOperations` is the geometry verbs: extrude, inset, bevel, loop cut, knife, subdivide, bridge,
fill hole, flip, weld, merge by distance, dissolve, delete, detach and append. Each takes an `EditMesh` and a set
of element indices, changes the mesh, and returns the faces it made. `MeshLoop` is the small record
each of them assembles the new face table out of.

## What it is for

Building a room out of a cube without leaving the viewport. A designer's blockout pass is almost
entirely these verbs applied to a face selection, and every one of them is arithmetic with a right
answer — which is what makes them testable against a cube instead of against a gesture.

You do not want them for a mesh you are only drawing. `MeshData` is the drawing structure; going
through the kernel to change one costs two conversions and gains nothing.

**Nothing here knows about a scene, a selection or an undo stack.** Which faces are selected is the
editor's, and so is recording the change — see `BlockoutGeometry`, which is the same verbs with a
document and a command behind each.

## Using it

Every verb takes what to act on and returns what it made:

```csharp no-compile="a fragment; the face indices come from whatever the pointer named"
var made = MeshOperations.Extrude(mesh, faces, distance: 2f);

MeshOperations.Inset(mesh, made, amount: 0.2f);
MeshOperations.Bevel(mesh, edges, width: 0.1f, segments: 3, out var unresolved);
```

⚠ **The face table is renumbered and the positions are not.** A position index is what a selection
holds, what an undo entry records and what a drag in flight is writing to; renumbering those under a
running gesture is the defect doc 24's D3 exists to prevent. Faces move freely, which is exactly why a
topology change drops an element selection.

⚠ **Positions no face uses are left behind rather than compacted.** `EditMesh.Validate` reports them
as orphans, and `Compact` is what removes them — run between gestures, when nothing holds an index,
because it renumbers the position table and hands back the map.

### The knife

`Knife` is doc 24 § P3's last row, and its primitive is *split this face between these two points on
its boundary*. `KnifeCut` is one of those; a stroke is a list of them and goes in one call:

```csharp no-compile="a fragment; the points come from a gesture that snapped them"
MeshOperations.Knife(mesh, [new KnifeCut(face, entry, exit), new KnifeCut(next, exit, further)]);
```

⚠ **A stroke is one call because each cut renumbers the table the next would index into.** Cutting
face 0 and then face 3 is cutting whatever face 3 has *become* after a rebuild, which is usually a
face the stroke never crossed. Everything is resolved before anything is written, so a stroke whose
last face refuses does not leave the ones before it half cut.

⚠ **It ends in `Stitch`, because a cut leaves T-junctions by construction.** A point part-way along an
edge is a corner on the face that was cut and the middle of a whole edge on the face beside it: the
two stop sharing an edge, `Validate` calls both halves boundaries, and the surface draws with a crack
that opens and closes as the camera moves. Nothing about the geometry is wrong, which is why it
survives every check that is not that one.

⚠ **Two faces meeting at one point get one position.** A stroke leaves a face by the edge the next
face enters by; inserting a position apiece would put two coincident corners on one edge, which
`Stitch` cannot repair because each is already a corner of the face that made it. Every count-shaped
assertion stays green over that, and the seam only closes when somebody welds by distance.

⚠ **A chord that separates nothing is refused rather than made degenerate.** Both ends on one edge,
both at one corner, or two adjacent corners each describe a cut that divides the face into itself and
a line — and a face table with a two-corner face in it is one every later verb walks wrongly.

⚠ **A region and a set of individual faces are different answers, and both are wanted.** Extruding
four faces as a region gives one box; individually gives four boxes with walls between them. What
decides it is what counts as the rim: an edge between two selected faces is interior when they are one
region and a rim when they are not.

## Examples

Bevel is the verb that looks small and is not:

```csharp no-compile="a fragment; the caller is expected to surface the count"
var made = MeshOperations.Bevel(mesh, edges, width, segments, out var unresolved);

if (unresolved > 0) {
    // "seven corners were left square" — a sentence a designer can act on
}
```

⚠ **A bevel on an edge that meets three other bevelled edges at a vertex is a miniature research
problem.** The honest first version bevels edges independently and reports where it could not resolve a
corner, rather than producing a self-intersecting one silently.

Two rules worth knowing about the shapes these produce. A partial subdivision splits its neighbours'
edges too — so they become n-gons rather than leaving a T-junction, which would draw as a crack the
first time anything moved. And a loop cut runs only through quads, because "the opposite edge" is a
phrase about a four-sided face.

```csharp no-compile="a fragment; the edge is one of the ring the cut crosses"
MeshOperations.LoopCut(mesh, edge, cuts: 3, slide: 0.5f);
```

## See also

- [Editable meshes](edit-meshes.md) — the structure these change, and why it is not a half-edge.
- [Element selection](../editor/element-selection.md) — what supplies the indices, in the editor.
- [docs/plan/24 § P3](https://github.com/Rikarin/Vixen/blob/master/docs/plan/24-blockout-tools.md) —
  the verb inventory these implement, and the bindings each of them has.
