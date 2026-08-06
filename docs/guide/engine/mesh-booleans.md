---
title: Mesh booleans
slug: engine/mesh-booleans
kind: guide
area: Engine
summary: Union, difference and intersection over solids, classified exactly so that coplanar faces do not open cracks.
api: [T:Vixen.Geometry.MeshBoolean, T:Vixen.Geometry.BooleanOperation, T:Vixen.Geometry.MeshCollision]
tags: [geometry, blockout, csg, boolean]
since: 0.1
status: preview
related: [engine/edit-meshes, engine/mesh-operations, engine/blockout-shapes, editor/booleans, engine/quad-remeshing]
---

## What it is

`MeshBoolean` combines two solids — union, difference or intersection — and cuts one with a plane or
with another's surface. `BooleanOperation` names which. `MeshCollision` is the other end of the same
mesh: the boxes and the triangle soup a physics world can be given.

## What it is for

Cutting a doorway through a wall by putting a box where the doorway goes. It is the most requested
tool in every block-out toolset and the one every toolset ships last, because a boolean that is nearly
right produces geometry that looks correct and is not.

You do not want it for a bevel or an inset. Those are [mesh operations](mesh-operations.md), they know
which faces they were given, and they are exact by construction.

**Every mesh boolean that produces holes produces them for one reason:** a classification near zero
answered wrongly, and the two sides of a cut stopped agreeing about which side they were on. That is
not a tolerance to tune. It is a question with an exact answer.

## Using it

```csharp no-compile="a fragment; the transform is where the second solid sits in the first's space"
var wall = MeshBoolean.Apply(solid, cutter, BooleanOperation.Difference, transform);
var half = MeshBoolean.PlaneCut(solid, new Plane(Vector3.UnitY, -2f));
```

⚠ **Null rather than an empty mesh when the result is nothing.** Subtracting a solid from itself is a
legitimate thing to ask for and its answer is that there is no solid — which a caller has to be able
to tell from "the operation failed", because one deletes an entity and the other must not.

⚠ **A plane is three points, and they are the ones that defined it.** A face's supporting plane is
recorded as three corners of the *original* operand and never recomputed, so classifying any original
vertex against any original plane is one call to `ExactPredicates.Orient3D` over inputs that were
never arithmetic. A normal and an offset derived in floating point would be a fourth number that
disagrees with the three it came from — which is exactly the disagreement that opens a crack between a
wall and the floor it is flush with.

⚠ **A vertex made by a split *remembers* the plane it was made on.** Its position is arithmetic and so
is inexact; its membership is not. Asked which side of that plane it is on, it answers "on it" from
the record rather than from a subtraction, for ever, through every later split — so the two faces
either side of a cut can never be told apart by it. A point on a segment also lies on every plane
*both* its endpoints lie on, so membership propagates through a split.

⚠ **What that does not buy: a split vertex against a plane it was not made on.** Three planes meeting
at a point is a position computed in floating point. The fully plane-based answer is a four-plane
determinant, which is a predicate this engine does not have. What is exact is every original vertex
against every original plane, and every derived vertex against the planes it was derived on — which
between them are the cases a block-out is made of.

## Examples

Face groups travel, so the reveal a cut exposed is selectable as a group of its own:

```csharp no-compile="a fragment; the cutter's groups are shifted so they cannot collide"
var made = MeshBoolean.Apply(wall, doorway, BooleanOperation.Difference);

// made.Faces where Group >= the wall's highest are the reveal.
```

A trim removes the material and leaves the opening bare, which a subtract does not:

```csharp no-compile="a fragment; doc 24 calls this what most 'cut a doorway' actually wants"
var trimmed = MeshBoolean.Trim(wall, cutter);
```

⚠ **A boolean produces T-junctions by construction and they are repaired.** A plane splits every face
it crosses and no face it merely touches, so the face beside a cut face keeps an edge whose middle is
now somebody else's corner. Every BSP boolean has this and most of them ignore it, because a renderer
that only sees triangles mostly gets away with it — an editable mesh does not.
`MeshOperations.Stitch` is the repair and runs on every result.

⚠ **A non-manifold edge in a result is an answer rather than a defect.** Two solids that touch along
an edge and nowhere else have a union with a non-manifold edge in it. `EditMesh.Validate` reports it,
for the reason it reports every other one.

## See also

- [Editable meshes](edit-meshes.md) — the structure, and what `Validate` reports.
- [Mesh operations](mesh-operations.md) — the verbs that are exact by construction, including `Stitch`.
- [Booleans in the editor](../editor/booleans.md) — non-destructive operands, and the handoff.
- [Block-out shapes](blockout-shapes.md) — what the operands usually are.
