---
title: Sub-object picking
slug: editor/sub-object-picking
kind: guide
area: Editor
summary: Which face, edge or vertex of one mesh the pointer is on, and why that is a different question from which entity.
api: [T:Vixen.Editor.SceneView.SubObjectPicker, T:Vixen.Editor.SceneView.SubObject, T:Vixen.Editor.SceneView.SubObjectKind, T:Vixen.Editor.SceneView.SubObjectFilter, T:Vixen.Editor.SceneView.MeshElements, T:Vixen.Editor.SceneView.MeshEdge, T:Vixen.Editor.SceneView.ISubObjectPicker]
tags: [editor, viewport, picking, blockout, mesh]
since: 0.1
status: preview
related: [editor/modes]
---

## What it is

`SubObjectPicker` answers which face, edge or vertex of one mesh is under a point in the viewport.
`MeshElements` is what it asks about: a mesh's shared positions, its unique edges and its triangles,
derived from the geometry the editor already draws. `SubObject` is the answer — a `SubObjectKind` and
an index into the table that kind names — and `SubObjectFilter` says which kinds may answer.

`ISubObjectPicker` is the scene-facing form of the same question, implemented by `ScenePicker` and
reachable from a pane as `SceneViewport.PickSubObject`.

## What it is for

The editor already has two picking answers and neither is this one. `ScenePicker` and the id buffer
both answer *which entity*, over a whole scene, with a payload the rest of the editor understands.
Half of a blockout toolset asks something else: which face of **this** mesh, within a tolerance
measured in pixels, with the innermost element winning — asked of one entity, answered with an index
only the caller and the mesh agree about.

You do not want it for selecting objects. It is a test against one mesh and it does not know the
scene is there.

**A drawing vertex is not a vertex, and that is the part worth knowing before using this.** `MeshData`
splits a corner wherever a normal or a texture coordinate had to be, so a cube's eight corners are
twenty-four entries and its twelve edges do not exist in it at all. A corner you can drag is one
thing. `MeshElements` is what turns the first into the second, by welding positions within a tolerance
relative to the mesh's own size — relative, and not exact, because a sphere's seam is `cos 0` against
`cos 2π` and differs in the last bits.

## Using it

Build the elements once, then query per pointer move.

```csharp no-compile="a fragment against a live camera and a live pane"
var elements = MeshElements.From(MeshPrimitives.Cube());
var picker = new SubObjectPicker();

var hit = picker.Under(elements, transform, camera, width, height, pointer, SubObjectFilter.Vertex);

if (hit.IsHit) {
    var corner = elements.Positions[hit.Index];
}
```

**The filter is what an element mode is.** Asking with `SubObjectFilter.Face` is face mode; asking
with `All` is "whatever is under the pointer", which is what a hover highlight wants. With several
kinds eligible the innermost wins — a vertex beats an edge and an edge beats a face — because the
corner of a face is also on two edges and inside the face, and a rule that took the largest would make
a vertex unclickable.

**A face is answered by a ray and a vertex by a projection.** A face has area, so the exact question
is which triangle the ray through the pointer meets first and no tolerance is needed. A vertex has no
area and an edge has no width, so the only thing that can be asked about them is how near the pointer
came *on screen*. That is why the tolerance is in render pixels and why how far away the mesh is
changes how much of it is within one.

**An instance, not a static.** Hover is one query per pointer move for as long as the pointer is over
the pane, so the projected positions live in buffers that grow to the largest mesh asked about and are
then reused. `ScenePicker` caches one `MeshElements` per shape kind for the same reason: a hundred
cubes are one table.

## Examples

From a pane, which is where a gesture comes from:

```csharp no-compile="a fragment; which entity is being edited is the caller's"
var hit = pane.PickSubObject(entity, point, SubObjectFilter.Edge);
```

Two limits, both deliberate and both stated rather than approximated:

**A face is a triangle.** The diagonal a triangulation puts across a cube's side is a real, selectable
edge through the middle of a wall. Face groups are what remove it — an n-gon face with an id every
operation propagates — and they arrive with the mesh kernel.

**Nothing is occluded.** The vertex on the far side of a cube is as selectable as the one facing you;
where two project to the same pixel the nearer one wins. Fixing it properly means asking what the
*picture* has at a pixel, which is the id buffer with an element id in it instead of an entity id.
Every cheap approximation in between is a depth bias that is wrong at a silhouette.

## See also

- [Editor modes](modes.md) — the blockout mode, whose element modes decide which filter to ask with.
- [docs/plan/24 § B4](https://github.com/Rikarin/Vixen/blob/master/docs/plan/24-blockout-tools.md) —
  the argument that this is the ray test with a different payload rather than a new subsystem.
