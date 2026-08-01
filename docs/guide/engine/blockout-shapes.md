---
title: Block-out shapes
slug: engine/blockout-shapes
kind: guide
area: Engine
summary: Boxes, stairs, arches and the rest, generated from a handful of numbers that stay live afterwards.
api: [T:Vixen.Geometry.MeshShapes, T:Vixen.Geometry.ShapeKind, T:Vixen.Geometry.ShapeParameters]
tags: [geometry, blockout, mesh, level-design]
since: 0.1
status: preview
related: [engine/edit-meshes, engine/mesh-operations, engine/mesh-surfaces, editor/shape-tool]
---

## What it is

`MeshShapes` builds an [`EditMesh`](edit-meshes.md) from a `ShapeParameters` — a `ShapeKind` and six
numbers. Twelve kinds: box, plane, cylinder, cone, sphere, capsule and torus, plus the five that are
tedious to build by hand and are why Unreal ships a Stairs tool — stairs, ramp, arch, pipe and door
frame.

`MeshShapes.Sweep` is the routine three of those are made of, and it is also the poly-shape tool: a
closed outline, swept along a line, capped at both ends.

## What it is for

Making the thing rather than modelling it. Half of a block-out pass is a wall, a floor, a flight of
steps and a doorway, and every one of those is a shape somebody wants at a size rather than a mesh
somebody wants to extrude into existence.

You do not want it for a prop an artist will make. A shape here exists to be replaced, which is the
whole reason it is generated from numbers instead of authored.

**Quads, wherever a quad is what the shape is made of.** `EditMeshes.From(PrimitiveKind)` welds a
triangle soup back into a mesh and cannot give back the four-sided faces the soup was built from — and
an edge loop, an edge ring and a loop cut are all statements about four-sided faces. A cylinder made
editable through the renderer's primitive has no rings to cut; one built here does.

## Using it

```csharp no-compile="a fragment; the mesh goes into a scene document, which is the editor's"
var stairs = MeshShapes.Create(
    new ShapeParameters { Kind = ShapeKind.Stairs, Size = new(1.2f, 3f, 5f), Steps = 16 }
);
```

⚠ **`Size` is the whole extent in world units, and the geometry carries it.** `MeshPrimitives` builds
everything to fit the unit cube so that a transform's scale is the size, which is right when a
thousand entities share one upload. It is wrong here: a wall built as a unit cube scaled `8 3 0.2`
has a non-uniform transform, so every bevel it is given afterwards is wider on one axis than another
and every texel on it is stretched.

⚠ **Centred in X and Z, and sitting *on* the origin in Y.** Everything a block-out tool makes is
placed on the work plane, and a shape whose origin is its centre arrives half-buried in the floor.

⚠ **Six fields for twelve shapes, and what each means is per kind.** `Sides` is a cylinder's sides and
an arch's segments; `Steps` is a staircase's steps and a sphere's rings; `Thickness` is the only one
that is a length rather than a ratio, and it is the wall left above an opening. A record per kind
would be tidier in the type system and would grow a case in the scene format, a drawer in the
inspector and a branch in the reader every time somebody added a stair.

⚠ **Out-of-range parameters are clamped rather than refused.** The caller is a number field somebody
is scrubbing through zero: an inspector that threw on a negative side count would throw once per
frame, and one that refused the edit would make the field impossible to type a two-digit number into.

## Examples

The poly-shape tool, which is `Sweep` with an outline a designer clicked:

```csharp no-compile="a fragment; the footprint comes from clicks on the work plane"
Span<Vector2> footprint = [new(0f, 0f), new(4f, 0f), new(4f, 2f), new(2f, 2f), new(2f, 5f), new(0f, 5f)];

var room = MeshShapes.Sweep(footprint, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, new(0f, 3f, 0f));
```

⚠ **The outline must be anticlockwise in the plane its two axes span.** A clockwise one produces a
solid whose faces all point inwards — which validates as closed, draws as a hole under back-face
culling, and is the single most common way a generator goes wrong. `BlockoutCreate.Poly` reverses a
clockwise footprint before it gets here, because which way round a designer drags a room is not
something they should have to know.

**Face groups are assigned by what a face *is*, not by which way it points.** A staircase's treads are
`MeshShapes.GroupTop` and its risers `GroupFront` whatever angle the flight is at, so "select every
tread" is one click and a material given to the treads survives the flight being made steeper.
`EditMesh.Regroup` is the other answer and is the right one for a mesh that arrived from somewhere
else; a generator knows better than a tolerance can.

## See also

- [Editable meshes](edit-meshes.md) — the structure everything here produces.
- [Mesh operations](mesh-operations.md) — the verbs that change one afterwards.
- [Mesh surfaces](mesh-surfaces.md) — how the faces these generate are mapped and shaded.
- [The shape tool](../editor/shape-tool.md) — the gesture and the live parameters, in the editor.
