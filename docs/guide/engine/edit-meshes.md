---
title: Editable meshes
slug: engine/edit-meshes
kind: guide
area: Engine
summary: Faces over shared positions, an edge table that reports rather than refuses, and face groups.
api: [T:Vixen.Geometry.EditMesh, T:Vixen.Geometry.MeshFace, T:Vixen.Geometry.MeshGroupSource, T:Vixen.Geometry.MeshEdge, T:Vixen.Geometry.MeshReport, T:Vixen.Geometry.MeshTopology, T:Vixen.Geometry.MeshSelection, T:Vixen.Geometry.MeshElementKind]
tags: [geometry, mesh, blockout, modelling]
since: 0.1
status: preview
related: [ecs/components, editor/mesh-editing, editor/element-selection, engine/mesh-operations, engine/blockout-shapes, engine/mesh-surfaces, engine/mesh-booleans]
---

## What it is

`EditMesh` is a mesh you can change: faces as n-gon loops of corners, corners over shared positions,
an explicit edge table beside them, and a group id per face. `MeshFace` is one face, `MeshEdge` is one
edge, and `MeshReport` is everything true of a mesh that a tool might need to know before acting on
it.

It is arithmetic over arrays — no document, no selection, no device — which is what makes every
operation a function that can be checked against its own invariants rather than a gesture that has to
be driven.

## What it is for

Level design with geometry as the notation. A designer building a corridor is asserting that the
player fits through the gap and that the sightline reaches the door, and every one of those is only
true or false in the running game — so the geometry has to be editable where the game runs, not
round-tripped through a DCC.

You do not want it for an imported asset you are only drawing. `MeshData` is the drawing structure and
is what a renderer uploads; this is the structure you edit, and going through it to draw something
costs a conversion for nothing.

**The position graph and the shading graph are different graphs.** A cube's corner is one *position*
and three *corners*, each with its own normal and texture coordinate. Snapping, welding, edge loops
and "drag this corner" run on positions; normals, texture coordinates and materials run on corners. A
structure with one vertex list either splits smooth shading every time it extrudes or welds texture
coordinates every time it merges.

## Using it

A triangle soup is the door in, and it welds:

```csharp no-compile="a fragment; the arrays come from whatever produced the geometry"
var mesh = EditMesh.FromTriangles(positions, indices);

mesh.MovePosition(0, corner);       // the position graph — not a topology change
mesh.SetGroup(0, wall);             // what a tool selects
```

**Non-manifoldness is reported, not refused.** A half-edge structure cannot represent an edge with
three faces, and blockout geometry is non-manifold constantly — a wall meeting a floor in a T, a
boolean result, an imported mesh with a stray internal face. A kernel that refuses those refuses the
ordinary case.

```csharp no-compile="a fragment against a mesh somebody is editing"
var report = mesh.Validate();

if (!report.IsManifold) {
    // "this operation needs a manifold edge" — a sentence a designer can act on
}
```

⚠ **None of what a report lists is an error on its own.** A block-out under construction is boundary
edges all the way down. What *is* an error is a mesh whose tables disagree — a corner naming no
position, a face of two corners — and those are refused when they are added rather than reported
afterwards.

**Faces carry a group id**, which is what makes a cube's side one wall rather than two triangles.
`FromTriangles` groups by coplanar connected component; connected *and* coplanar, so two parallel
walls facing the same way are two groups because no edge joins them. A boolean returns triangles, and
a face that was one wall before the cut has to still be one wall afterwards or the next extrude acts
on a sliver.

**`GroupSource` says where those ids came from, and two stages of the content pipeline need to
know.** `MeshGroupSource.Coplanarity` is the guess `Regroup` — and therefore `FromTriangles` — makes
about shape. `MeshGroupSource.Assigned` is a statement somebody made: `SetGroup`, a shape out of
`MeshShapes`, or a reader carrying a file's declared material across.

⚠ **A retopology reads a group boundary as a crease and an unwrap makes it a chart boundary, and
both are only right for `Assigned`.** On a faceted surface — anything out of a generator, a sculpt or
a marching-cubes extraction — hardly any two neighbouring triangles are within half a degree of
coplanar, so the coplanarity guess is close to one group per triangle. Measured on a 25 439-triangle
image-to-3D mesh, read as material that gave 24 197 charts and a patch layout that refused outright.
Both stages check `GroupSource` before they read a boundary; a mesh you build face by face and mean
the groups on should say so.

## Examples

The edge table is what loops, rings and bevels are selections of, and it is rebuilt only when the
topology changes — dragging a corner does not disturb it:

```csharp no-compile="a fragment; the edge index comes from whatever the pointer named"
foreach (var edge in mesh.Edges) {
    var from = mesh.Positions[edge.A];
    var to = mesh.Positions[edge.B];
}

var touching = mesh.FacesOf(index);   // one for a boundary, two for a manifold edge, more for a seam
```

Undo is two granularities and the reason is that one of them has no inverse. A position change records
the positions it touched; a topology change records the whole mesh, because a boolean has no inverse
to record and an undo implemented as an inverse operation is a second implementation of every tool
that will disagree with the first.

```csharp no-compile="a fragment; `EditMeshCommand` is the editor's and lives beside the document"
var was = new EditMesh(mesh);   // deep, or the recorded state changes under the next edit
```

Two limits worth knowing. Triangulation is a fan from each face's first corner — exact for a convex
face, wrong for a concave one, and every face a primitive produces is convex. And the attribute layers
are named and typed rather than dictionary-keyed, because the set a blockout kernel needs is closed.

## See also

- [docs/plan/24 § D2](https://github.com/Rikarin/Vixen/blob/master/docs/plan/24-blockout-tools.md) —
  why this is an indexed face set with an edge table and not a half-edge structure.
- `MeshData` — the drawing structure this converts to and from.
