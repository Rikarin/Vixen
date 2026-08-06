# Vixen.Geometry

The mesh kernel the blockout tools edit. [docs/plan/24 § D1 and § D2](../../docs/plan/24-blockout-tools.md).

```csharp
var mesh = EditMesh.FromTriangles(positions, indices);   // a triangle soup becomes a mesh

mesh.MovePosition(0, corner);                            // the position graph
mesh.SetGroup(0, wall);                                  // what a tool selects

var report = mesh.Validate();                            // what is true of it
var triangles = mesh.Triangulate();                      // and back out to a renderer
```

## Under `Core/` and not `Editor/`

Three reasons and only the third is about the future. It is pure arithmetic with no document, no
selection and no device, so it belongs under the profile that is AOT-compatible, trimmable, packable
and API-checked. It is the only way the operations are testable as functions rather than as gestures.
And a mesh operation is worth having at run time: procedural level generation, destructible geometry
and a runtime CSG all want the same code, and none of them can reach an editor assembly.

⚠ **It references `Vixen.Core.Mathematics` and nothing else.** In particular not `Vixen.Rendering`: a
geometry kernel that needed the render assembly to describe a triangle would be backwards. The kernel
hands back its own arrays and the copy into `MeshData` lives in `Vixen.Editor.SceneView` beside the
code that uploads it — see `EditMeshes`. `Vixen.Navigation` makes exactly this choice with its own
`PolyMesh` and it has cost it nothing.

## Two graphs, not one

**The position graph and the shading graph are different graphs, and conflating them is the bug this
design exists to prevent.** A cube's corner is one *position* and three *corners*, each with its own
normal, its own texture coordinate and possibly its own material.

| Runs on positions | Runs on corners |
|---|---|
| vertex snapping, welding | normals |
| edge loops and rings | texture coordinates |
| "drag this corner" | materials, smoothing |

An implementation with one vertex list either splits smooth shading every time it extrudes, or welds
texture coordinates every time it merges — and both are discovered late, by an artist, in a mesh that
is already in a level. ProBuilder calls the position layer *shared vertices* and it is the single
design choice that makes its tools behave.

`FromTriangles` is the door in, and it welds: twenty-four drawing vertices become a cube's eight
corners. ⚠ **The tolerance is relative to the mesh's own size** — it is for floating-point noise in a
generator, not for cleaning up geometry, and a seam is `cos 0` against `cos 2π` differing in the last
bits. Welding by *distance* is a verb with a settings popover and belongs to the user.

## Not a half-edge

A half-edge structure cannot represent a non-manifold edge, and blockout geometry is non-manifold
constantly: a wall meeting a floor in a T, a boolean result, an imported mesh with a stray internal
face. A kernel that refuses those refuses the ordinary case.

What is stored instead is an indexed face set with an explicit edge table where each edge names *up
to* two faces **and more is allowed and reported**. `MeshReport` is the reporting:

| | |
|---|---|
| `NonManifold` | edges with more than two faces |
| `Boundary` | edges with one — the rim of an open surface |
| `Reversed` | edges whose two faces walk them the same way, so one is inside out |
| `Degenerate` | faces with no area |
| `Orphans` | positions no face uses |

⚠ **None of these is an error on its own.** A block-out under construction is boundary edges all the
way down; a wall built by mirroring is non-manifold where the halves meet. What *is* an error is a
mesh whose tables disagree — a corner naming no position, a face of two corners — and those are
refused at the door rather than reported.

⚠ **The degeneracy test is relative to the mesh's size, and the capsule is what found it.** An
absolute epsilon is a statement about how big a face has to be to count, and the triangles round a
hemisphere's pole are genuinely small — sixty-four of them were declared degenerate by a fixed
tolerance that a primitive built at a tenth of the scale would have failed entirely.

## Face groups

**Unreal's PolyGroups, and the reason to have them even in a polygon kernel.** A boolean returns
triangles, and a face that was one wall before the cut has to still be one wall afterwards or the
next extrude acts on a sliver. A group is what a tool selects and what a material is assigned to.

`FromTriangles` groups by **coplanar connected component**, so a cube's side is one group made of two
triangles. Connected *and* coplanar: two parallel walls facing the same way are two groups because no
edge joins them.

⚠ **Group ids are numbered from zero in face order** rather than by whatever the union-find rooted
them at. A group id is written to a file and shown in an inspector, and ids that jumped from 0 to 7 to
23 would be a mesh nobody could read.

## Selection and the walks under it

`MeshTopology` is what a gesture is made of: the edge loop through an edge, the ring across it, every
face coplanar with and joined to one, a group, a shell, a boundary loop. `MeshSelection` is what a
mode holds — **one set and a kind**, not three sets, because a selection kept per kind is three that
drift out of agreement and the first operation to read the wrong one is a bug nobody can reproduce.

⚠ **A loop runs on through positions where four edges meet and stops where a different number do.**
Any other rule has to guess, and a loop that guesses occasionally selects a path nobody can describe —
which is worse than one that stops short, because stopping short is visible.

⚠ **Coarse to fine takes everything, fine to coarse takes only what is fully covered.** Three faces as
vertices is every corner of them; those vertices back to faces is the three faces again. A rule that
took partially covered faces would grow the selection every time somebody switched modes twice.

## The verbs

`MeshOperations` is doc 24's Geometry table: extrude, inset, bevel, loop cut, subdivide, bridge, fill
hole, flip, weld, merge by distance, dissolve, delete, detach and append.

⚠ **Every one of them rebuilds the face table and leaves the positions alone.** A position index is
what a selection holds, what an undo entry records and what a drag in flight is writing to — D3 turns
on one meaning the same thing from one frame to the next. Faces renumber freely, which is exactly why
a topology change drops an element selection. What is left behind is an orphan; `Compact` removes them
and hands back the map, and it is run between gestures rather than inside one.

⚠ **A region and a set of individual faces are different answers and both are wanted.** Extruding four
faces as a region gives one box; individually gives four boxes with walls between them. What decides
it is what counts as the rim, and that single rule is the whole difference.

⚠ **A partial subdivision splits its neighbours' edges too.** Otherwise the shared edge is split on
one side and whole on the other — a T-junction, which `Validate` reports as two boundary edges and
which draws as a crack the first time anything moves. The neighbour becomes an n-gon, which is most of
the argument for having n-gons at all.

⚠ **The winding of a cap cannot come from the edge table.** An edge is stored low-to-high, which says
nothing about which way round a face walks it, so a boundary loop taken from the stored order gives a
fill that faces inwards about half the time. `BoundaryLoop` orients itself from the rim's own face.

## The shapes, and the surfaces

**`MeshShapes` is doc 24's P4 Creation table.** Twelve kinds behind one `ShapeParameters` — a
`ShapeKind` and six numbers — and the last five are the reason it exists: stairs, ramp, arch, pipe and
door frame are the shapes that are tedious to build by hand and are why Unreal ships a Stairs tool.

⚠ **A shape carries its size in the geometry rather than in the transform.** `MeshPrimitives` builds
everything to fit the unit cube so a thousand entities can share one upload; a parametric shape is one
mesh per entity anyway, and a wall built as a unit cube scaled `8 3 0.2` has a non-uniform transform —
so every bevel on it is wider on one axis than another and every texel on it is stretched.

⚠ **Centred in X and Z and sitting on the origin in Y**, because everything a block-out tool makes is
placed on the work plane and a shape whose origin is its centre arrives half-buried in the floor.

⚠ **Six fields for twelve shapes rather than a record per kind.** A discriminated union is tidier in
the type system and worse everywhere else it has to exist: a tagged variant in the scene format, a
drawer per shape in the inspector, and a case added to all of them every time somebody adds a stair.
What the fields *mean* is per kind and is documented on each.

**`MeshSurfaces` is P5's arithmetic half**: world, box and planar UV projection; per-face offset,
rotate, scale and fit; smoothing groups and the corner normals they imply. World is the default, and
doc 24 gives the reason in one sentence — a block-out box scaled 8×3 must not stretch its texels.

⚠ **Smoothing is a second per-face number, and every operation has to carry it.** A verb that carried
a face's group and dropped its smoothing gives back a mesh that is materialled correctly and faceted,
which reads as a shading bug in the renderer rather than as the extrude that caused it. It is
`MeshLoop`'s third field for exactly that reason.

⚠ **Auto-smoothing is a union-find over faces, not a flag per edge.** Smoothing has to be transitive
round a cylinder — every neighbour is within the angle, so the whole wall is one surface — and a
per-edge flag would make a corner between two smooth faces depend on which of them the normal was
computed from first.

## The boolean, and the two things that make it work

**`MeshBoolean` is doc 24's P6.** Union, difference and intersection over a BSP, plus a plane cut and
a trim. The algorithm is Naylor–Amanatides–Thibault and is the least interesting part; what decides
whether a boolean works is the classification.

⚠ **A plane is the three points that defined it, and they come from the original operand.**
`ExactPredicates.Orient3D` answers which side of a plane-through-three-points a fourth point is on,
exactly — so recording the plane that way makes every original vertex against every original plane a
question over inputs that were never arithmetic. A normal and an offset derived in floating point
would be a fourth number that disagrees with the three it came from, which is the disagreement that
opens a crack between a wall and the floor it is flush with.

⚠ **A vertex made by a split remembers the plane it was made on.** Its position is arithmetic and so
inexact; its membership is a record. Asked which side of that plane it is on it answers "on it"
without any arithmetic at all, for ever, through every later split — which is what keeps the two faces
either side of a cut agreeing about it. A point on a segment also lies on every plane *both* its
endpoints lie on, so membership propagates through a split rather than being rediscovered.

⚠ **What is honestly not exact.** A vertex where three planes meet, classified against a fourth plane
it was not made on, is a floating-point question — the fully plane-based answer is a four-plane
determinant and there is no such predicate here. What is exact is the coplanar case, the shared-edge
case and the identical-operand case, which are what a block-out is made of.

⚠ **`MeshOperations.Stitch` exists because a cut makes T-junctions by construction.** A plane splits
every face it crosses and no face it merely touches, so the face beside a cut face keeps an edge whose
middle is now somebody else's corner. csg.js and most of its descendants ignore this because a
renderer that only sees triangles gets away with it; an edge table cannot. It is the general form of
the fix `Subdivide` already needed.

⚠ **A non-manifold edge in a result is an answer.** Two solids that touch along an edge and nowhere
else have a union with one in it, and the property suite found the case within ten thousand pairs. The
gate is no boundary edge and no reversed face — no hole and no self-intersection — rather than
`IsClosed`, which is the same argument D2 makes about reporting these instead of refusing them.

**`MeshCollision` is P7's half of the same mesh**: a box per connected shell, or the triangle soup.
A shell rather than a convex piece, because convex decomposition is a research problem with
approximate answers and a shell is exact and already in the edge table.

## What is not here yet

**Triangulation is ear clipping now**, falling back to a fan for a loop no triangulation is right
for. Every face still produces exactly `Count − 2` triangles whatever route it took, because that is
what lets the face table and the triangle list be walked together.

**And it gives the same indices for a model in metres and the same model in millimetres**, which is
not free and is why the flattening drops the axis the face most nearly faces along rather than
building a basis in its plane: two coordinates *copied* out of the position round nowhere, where two
dot products round twice per corner. Ear clipping then asks `ExactPredicates.Orient2D` rather than a
cross product, and requires a candidate corner to turn by more than its own coordinates' last bits
before believing which way — a staircase's nose corners are collinear by construction, and nothing
representable puts them exactly on the line. Doc 41 § D14 wants byte-identical remesher output and
doc 08 caches on a content hash, so a triangulation that moved under a unit conversion would re-page
every meshlet of an asset that had one.

**The layers are named and typed rather than a dictionary**: per-corner normals and texture
coordinates, and a per-face group. A general layer system is what a DCC needs and is not what a
blockout kernel needs yet; adding one is a change to this file rather than to everything that reads it.

**No knife.** A free cut across faces is doc 24's P3 row that is still owed — the kernel primitive is
"split this face between these two points" and the gesture round it is nearer to `MeshBoolean.PlaneCut`
than to anything here.

## Tests

Unit tests over the tables, and property tests with CsCheck as doc 24's Part 4 asks for and as
`Vixen.Core.Mathematics` already does. A box at any size anywhere comes out solid; triangulating and
rebuilding gives the same mesh; a copy is equal to its original and independent of it. Every one of
them ends with the invariant helper rather than only with its own property.
