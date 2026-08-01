---
title: Mesh surfaces
slug: engine/mesh-surfaces
kind: guide
area: Engine
summary: Where a face's texels come from and which normals it shades with — world-space projection and smoothing groups.
api: [T:Vixen.Geometry.MeshSurfaces, T:Vixen.Geometry.UvProjection]
tags: [geometry, blockout, uv, shading]
since: 0.1
status: preview
related: [engine/edit-meshes, engine/blockout-shapes, engine/mesh-operations, editor/face-materials]
---

## What it is

`MeshSurfaces` is the half of a mesh that is not its shape: the texture coordinates on its corners and
the smoothing groups on its faces. `UvProjection` is which of three ways coordinates are worked out —
world, box or planar.

It is pure arithmetic over the mesh's own arrays. Nothing here knows what a material is; that is the
editor's, in [face materials](../editor/face-materials.md).

## What it is for

Making a block-out readable. Grey on grey is a shape you cannot judge the size of, and a checker whose
squares are a fixed number of metres everywhere is what turns "how wide is that corridor" into
something you count. Smoothing groups are the other half: they are what stops a generated cylinder
coming out as a polygon.

You do not want it for unwrapping. Every unwrapper answers "where on this surface am I" and needs the
surface to have been laid out; a block-out has not been laid out and never will be, because it exists
to be replaced.

**World is the default and that is the decision.** A block-out box scaled 8×3 must not stretch its
texels, and the only projection whose squares are the same size on two objects of different scales is
the one that ignores the object entirely.

## Using it

```csharp no-compile="a fragment; the world matrix is the entity's and comes from the scene"
MeshSurfaces.Project(mesh, faces: null, UvProjection.World, scale: 1f, toWorld: transform);
MeshSurfaces.AutoSmooth(mesh);
```

⚠ **Coordinates are per *corner*, not per position.** A cube's corner is three corners in three faces
with three different coordinates, which is exactly why the drawing structure and the position graph
are different graphs — a projection that wrote per position could not map two faces of one box
differently, which is the whole point of a box projection.

⚠ **A face that is not named keeps the coordinates it had.** Mapping a wall and finding the floor
remapped is what makes a per-face tool useless.

⚠ **A hard edge is the absence of a smoothing group, so zero is a value rather than a sentinel.**
`AutoSmooth` numbers its groups from one and leaves a face with no neighbours inside the angle at
zero, because "smoothed with nobody" and "hard" are the same picture and the first would fill a saved
mesh with a group per face.

## Examples

Smoothing is a union-find over faces across shared edges, not a flag per edge:

```csharp no-compile="a fragment; the angle defaults to thirty degrees"
MeshSurfaces.AutoSmooth(mesh, angle: MeshSurfaces.DefaultSmoothingAngle);

var normals = MeshSurfaces.Normals(mesh);   // one per corner, honouring the groups
```

⚠ **It has to be transitive.** Every neighbour round a cylinder is within the angle, so the whole wall
is one surface — and a flag per edge would make a corner between two smooth faces depend on which of
them the normal was computed from first.

⚠ **Averaging is weighted by area rather than counted.** A cap's fan puts a great many tiny triangles
at the pole and one big quad beside it; an unweighted average there is a normal dominated by the
tessellation rather than by the shape, which is the classic pinched highlight at the top of a sphere.

A per-face transform turns about the face's own centre:

```csharp no-compile="a fragment; the amounts come from a settings popover"
MeshSurfaces.Transform(mesh, [face], offset: new(0.25f, 0f), rotation: MathF.PI * 0.5f);
MeshSurfaces.Fit(mesh, [face]);
```

⚠ **About its own centre rather than about the origin of the mapping.** Rotating a face's texture a
quarter turn about a point somewhere off in UV space moves it out of the frame as well as turning it,
which is what makes a rotate field feel broken — and is what a naive matrix multiply does.

## See also

- [Editable meshes](edit-meshes.md) — where the corner layers and the face groups live.
- [Block-out shapes](blockout-shapes.md) — what generates the faces these map.
- [Face materials](../editor/face-materials.md) — the editor verbs, and what a viewport does with them.
- [Mesh operations](mesh-operations.md) — the verbs that carry smoothing through an edit.
