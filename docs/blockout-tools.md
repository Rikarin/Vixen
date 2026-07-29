<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Blockout tools

An in-viewport toolset for building the grey-box: a mesh kernel that survives being edited, a mode
that owns the viewport's input while it is active, sub-object selection, the fifteen verbs everybody
who has box-modelled reaches for, a snapping system that is one service rather than one per tool, and
a handoff that turns a block-out into an asset an artist can replace.

This document is the plan and the argument for it. It is a separate file for the same reason
[bindless-materials.md](bindless-materials.md) and [virtualized-geometry.md](virtualized-geometry.md)
are: it is larger than a row in a status table, several things depend on pieces of it, and the first
half of it is an argument rather than a schedule.

**Read [the row this overturns](#the-row-this-overturns) before the phases.** This document
contradicts a decision already recorded in [20 § Part G](plan/20-editor-parity.md#part-g--out-of-scope),
and a plan that quietly reversed one would be worth less than no plan.

---

## The row this overturns

[20 § Part G](plan/20-editor-parity.md#part-g--out-of-scope) lists, under things deliberately not
being built:

> **Mesh editing / modelling tools** — Unreal ships them and they are not what an engine is for.
> Import from a DCC.

Half of that is right and stays right. The half that is wrong is the word *modelling*.

**Blockout is not modelling; it is level design with geometry as the notation.** A designer building
a corridor is not authoring an asset, they are asserting that the player fits through a gap, that the
sightline from the balcony reaches the door, and that the jump is makeable. Every one of those is a
claim that is only true or false *in the running game*, and the loop that tests it is
edit → play → adjust, measured in seconds. Round-tripping a four-metre box through a DCC breaks that
loop, and a broken loop is not a slower workflow — it is a level nobody iterates on, because each
iteration costs more than living with the flaw.

That is why both reference engines converged here from opposite directions. Unreal shipped BSP
brushes in 1998, tried to retire them, and replaced them with Modeling Mode rather than with an
export button. Unity refused to build it, watched ProBuilder become the most-installed level-design
asset on its store, and then **bought ProBuilder and made it a first-party package**. Two independent
organisations, one conclusion: the geometry a designer blocks out with has to be editable where the
game runs.

So the line moves rather than disappearing, and here is where it goes:

| In | Out |
|---|---|
| Primitives with live parameters; push/pull on a grid | Sculpting, retopology, subdivision surfaces |
| Face / edge / vertex selection, loops and rings | Skinning, rigging, morph targets |
| Extrude, inset, bevel, loop cut, bridge, weld | UV *unwrapping* — seams, LSCM, packing |
| Boolean, plane cut, mirror, array | Normal-map baking, AO baking, lightmap UVs |
| Planar / box / world-space UVs, per-face material | Remesh, simplify, voxel merge |
| Snapping, measurement, alignment | A node-based parametric modeller |
| Bake to an asset an artist replaces | Being anybody's DCC |

The right-hand column is [20 § Part G](plan/20-editor-parity.md#part-g--out-of-scope)'s sentence,
still true, and the reason it is still true is that everything in it is *authoring for its own sake*.
Everything in the left-hand column exists to answer a question about the game.

⚠ **The test for whether a proposed tool belongs on the left is not "is it hard".** It is: *does a
level designer reach for it between two playtests?* Bevel is on the left because a chamfered edge is
how you stop a corridor reading as a tunnel; remesh is on the right because nobody has ever remeshed
between playtests.

---

## What the references actually ship

Surveyed rather than remembered, because "Unreal has modelling tools" is not a specification.

### Unreal Engine 5 — Modeling Mode

Ships in the box (`ModelingToolsEditorMode`), organised into ten categories. The full tool list, as
of [5.8's documentation](https://dev.epicgames.com/documentation/en-us/unreal-engine/modeling-tools-in-unreal-engine):

| Category | Tools |
|---|---|
| **Create** | Box, Sphere, Cylinder, Cone, Torus, Arrow, Rectangle, Disc, Stairs, Capsule, **CubeGrid**, Extrude Polygon, Extrude Path, Revolve Path, Revolve Spline, Draw Spline, Mesh Spline |
| **Select** | Delete, Extrude, Offset, Push Pull, Inset, Outset, Cut, Bevel, Insert Loop, Clean |
| **Transform** | Transform, Align, Merge, Duplicate, Edit Pivot, Bake Transform, Transfer, Convert, Split, Pattern, ISMEd |
| **Deform** | Vertex Sculpt, Dynamic Sculpt, Smooth, Offset, Warp, Lattice, Displace, Deform PolyGroups |
| **Model** | PolyGroup Edit, Subdivide, **Boolean**, PolyCut, **Plane Cut**, **Mirror**, Mesh Cut, Trim |
| **Mesh** | Tri Select, Triangle Edit, Fill Holes, Weld, Union, Jacket, Simplify, Remesh, Project |
| **Voxel** | Voxel Wrap, Blend, Offset, Boolean, Merge |
| **Bake** | Bake Textures, Bake All, Bake Vertex Colors, Bake RC |
| **UVs** | AutoUV, UV Unwrap, Project UVs, Edit UV Seams, Transform UVs, Layout UVs, UV Editor |
| **Attributes** | Inspect, LOD Manager, Normals, Tangents, Generate/Paint PolyGroups, Edit Attributes, Edit Materials, Paint Vertex Colors, Paint Maps, **Inspect/Simple Collision, Mesh To Collision** |

Two things in that list matter more than the length of it.

**PolyGroups are the idea worth stealing.** Unreal's meshes are triangles underneath, and a
*PolyGroup* is a named set of triangles that the tools treat as one face. Extrude acts on the group,
not on its triangulation; a cube's side stays one thing after a boolean has cut it into eleven
triangles. This is what lets a triangle-soup kernel behave like a polygon modeller, and it is why
Unreal can run booleans and still offer face-level editing afterwards.

**CubeGrid is the blockout tool proper**, and everything else in Create is a primitive.
[Its whole interaction](https://dev.epicgames.com/documentation/unreal-engine/cubegrid-tool-in-unreal-engine)
is a repositionable grid, a selected cell face, and a push/pull:

| | |
|---|---|
| Click | select a grid face; `Shift`+click extends to a rectangle of cells |
| `Ctrl`+drag, or `E` / `Q` | pull / push the selection by one cell |
| `Ctrl+E` / `Ctrl+Q` | double / halve the grid size |
| `Shift+E` / `Shift+Q` | slide the selection one cell forward / back without editing |
| `Z` | corner mode — push individual corners of the selection, `Z` again to apply |
| `R` | reposition the grid with a gizmo |

The grid size doubling and halving is the detail that makes it work: a designer blocks the building
at 4 m, the doorway at 1 m, and the step at 0.25 m, without ever typing a number.

### Unity — ProBuilder (first-party since 2018)

[The menu](https://docs.unity3d.com/Packages/com.unity.probuilder@6.0/manual/menu.html), by category:

- **Geometry** — Bevel Edges, Bridge Edges, Collapse Vertices, Conform Normals, Delete/Detach/Duplicate
  Faces, Extrude, Fill Hole, Flip Face Edge, Flip Normals, **Insert Edge Loop**, Merge Faces, Offset
  Elements, Smart Connect, Smart Subdivide, Split Vertices, Triangulate, **Weld Vertices**
- **Selection** — Grow, Shrink, Select Hole, **Select Loop**, **Select Ring**, Select by Material,
  by Smoothing Group, by Vertex Colour
- **Object** — Center Pivot, Conform/Flip Object Normals, Freeze Transform, Merge Objects,
  **Mirror Objects**, ProBuilderize, Set Collider, Set Trigger, Subdivide, Triangulate
- **Materials / Vertex Colours / UVs** — per-face material palette, vertex colour palette, a UV
  editor with auto (planar/box, world-space) and manual modes
- **Export** — Asset, OBJ, PLY, STL
- **Experimental** — Boolean (union / intersection / subtraction)

⚠ **Boolean is still marked experimental after seven years.** That is not neglect; it is the honest
status of a robust mesh boolean, and it is the single most important scheduling fact in this
document. Everything else on ProBuilder's list is a well-understood local operation on a mesh.
Boolean is a global one over floating-point geometry, and it is where every implementation of this
kind of toolset either spends a year or ships something that produces holes.

The rest of the Unity ecosystem exists because ProBuilder deliberately stops short:
[RealtimeCSG](https://realtimecsg.com/) does Quake-style plane-based CSG in real time and is what
people who miss BSP brushes use; Archimatix is a node-based parametric modeller; PolyBrush is
sculpting and paint. Only the first is blockout.

### Blender

Not a competitor, and the thing everyone compares the *feel* to. Three of its ideas are worth more
than the rest of its feature list combined, and none of them is a tool:

1. **Typed numeric entry during a transform.** `G X 5 ⏎` — grab, constrain to X, move five metres.
   No dialog, no field, no mouse precision. Nothing in Unreal or Unity has this and every level
   designer who has used Blender misses it within an hour.
2. **[Snapping is a mode over every transform](https://docs.blender.org/manual/en/latest/editors/3dview/controls/snapping.html),
   not a per-tool setting** — snap element (increment, vertex, edge, edge-centre, face, volume),
   snap base (closest / centre / median / active), and "align rotation to target" as an orthogonal
   toggle.
3. **The pivot and the working plane are first-class and movable.** The 3D cursor is a placement
   origin you put somewhere and then build from.

### The consensus

Strip the three lists to what all of them have and what a level designer uses between playtests, and
the intersection is remarkably small — about fifteen verbs, one selection model, one snapping
service, and a grid you can move. That intersection is [the inventory](#part-2--the-tool-inventory)
below. Everything in one list and not the others is a differentiator to consider later, not a gap.

---

## Where Vixen already is

More of this exists than "the viewport draws lines" suggests. Reconciled against the code, not
against the plan docs.

| Piece | State | Where |
|---|---|---|
| `MeshData` — parallel typed arrays, empty means absent | ✅ | [MeshData.cs](../Core/Vixen.Rendering/MeshData.cs) |
| `MeshPrimitives` — eight shapes, every one fitting the unit cube, CCW, no device | ✅ | [MeshPrimitives.cs](../Core/Vixen.Rendering/MeshPrimitives.cs) |
| `MeshShape` + `MeshShapes` — a primitive kind per entity, in the scene file | ✅ | [ShapeComponents.cs](../Editor/Vixen.Editor.SceneView/ShapeComponents.cs) |
| `SceneMeshes` — every shaped entity as one instance, grouped per shape | ✅ | [SceneMeshes.cs](../Editor/Vixen.Editor.SceneView/SceneMeshes.cs) |
| `MeshInstanceRenderer` — device-resident shapes, a per-entity transform, one draw per shape | ✅ | [MeshInstanceRenderer.cs](../Core/Vixen.Rendering/MeshInstanceRenderer.cs) |
| `MeshRenderer` — world-space triangles for the gizmo's solid handles, which are rebuilt per frame | ✅ | [MeshRenderer.cs](../Core/Vixen.Rendering/MeshRenderer.cs) |
| `TransformGizmo` — four modes, four spaces, two pivots, **recomputed from mouse-down** | ✅ | [TransformGizmo.cs](../Editor/Vixen.Editor.SceneView/TransformGizmo.cs) |
| `SnapSettings` — grid / angle / scale, absolute-vs-relative distinguished | ✅ | [GizmoTypes.cs](../Editor/Vixen.Editor.SceneView/GizmoTypes.cs) |
| `SnapSettings.SnapToVertex` / `SnapToSurface` | 🟡 | declared, **not honoured** |
| `SceneGrid` — 1-2-5 adaptive spacing, emphasis on round numbers, screen-height reach | ✅ | [SceneGrid.cs](../Editor/Vixen.Editor.SceneView/SceneGrid.cs) |
| `ScenePlacement` / `ISurfaceProbe` / `SurfaceHit` — a ray to a place to put something | ✅ | [ScenePlacement.cs](../Editor/Vixen.Editor.SceneView/ScenePlacement.cs) |
| `ScenePicker` — exact ray tests per primitive, in local space, pixel-sized markers | ✅ | [ScenePicker.cs](../Editor/Vixen.Editor.SceneView/ScenePicker.cs) |
| `PickingRenderer` / `PickingBuffer` — id buffer, one-pixel readback, ring-deep | 🟡 | written, driven by nothing |
| `CommandStack` — merging, transactions, clean-marking, randomised do/undo/redo tests | ✅ | [CommandStack.cs](../Editor/Vixen.Editor.Core/CommandStack.cs) |
| `SceneDocument` — undoable create/delete/rename, names outside the world, hidden set | ✅ | [SceneDocument.cs](../Editor/Vixen.Editor.SceneView/SceneDocument.cs) |
| `.vxscene` — YAML, GUID identities, byte-identical round trip, version-checked | ✅ | [SceneSerializer.cs](../Editor/Vixen.Editor.SceneView/SceneSerializer.cs) |
| **`ExactPredicates`** — filtered exact `Orient3D` / `InSphere` over `BigInteger` | ✅ | [ExactPredicates.cs](../Core/Vixen.Core.Mathematics/ExactPredicates.cs) |
| `PhysicsWorld.Raycast` — an `ISurfaceProbe` implementation waiting to be written | ✅ | [PhysicsWorld.Queries.cs](../Core/Vixen.Physics/PhysicsWorld.Queries.cs) |
| `ShapeDescription` — box, sphere, capsule, cylinder, **convex hull, mesh** | ✅ | [ShapeDescription.cs](../Core/Vixen.Physics/Shapes/ShapeDescription.cs) |
| `ModelImporter` — Assimp, one sub-asset per mesh, addressed by name | ✅ | [ModelImporter.cs](../Editor/Vixen.Editor.Assets/Models/ModelImporter.cs) |
| `GeometryBuffer` — many meshes in one vertex and one index buffer | ✅ | [GeometryBuffer.cs](../Core/Vixen.Rendering/GeometryBuffer.cs) |
| `Vixen.Navigation` — a managed voxel bake over level geometry, contour → `PolyMesh` | ✅ | Core/Vixen.Navigation |
| Any editable mesh at all | ⬜ | — |
| `IEditorMode` | ⬜ | proposed in [20 § A1](plan/20-editor-parity.md#a1--the-application-frame), not built |

**`ExactPredicates` is the most valuable line in that table and it did not get built for this.** It
was written for `DelaunayTetrahedralization`, and it is the exact thing a robust boolean needs: a
sign that is *the* sign, filtered so the exact path essentially never runs. Every mesh boolean that
produces holes produces them because a predicate near zero answered wrongly and the classification
became inconsistent. Having that already solved, tested and fast moves the boolean from "the phase
that might not land" to "the phase that is merely large".

**The gizmo being recomputed from mouse-down is the second.** [Numeric entry](#p0--the-seam) — the
Blender idea above — is, in that design, substituting a typed magnitude for the dragged one before
the same arithmetic runs. In an implementation that accumulated per-frame deltas it is not
expressible at all.

---

## What blocks it

Five things, of which one was genuinely blocking and is now fixed, one is still genuinely blocking, and
three are ordering constraints.

### B1. Every mesh in the viewport went through the CPU every frame ✅

**Fixed.** The argument is kept because it is the reason this was scheduled ahead of the rest of Phase
7 rather than with it, and because the shape of the answer is the shape the phases below now assume.

`SceneMeshes.Build` walked the scene, transformed each shape's vertices into world space, and appended
them to one list — once per frame, unconditionally. Its own remarks called this "the deliberate limit
of this path" and put the ceiling at the tens of thousands of vertices `MeshRenderer` is sized for,
"which is a block-out rather than a level".

That was a fair trade when a scene was a hundred primitives. It stopped being one here for a reason
the remark did not anticipate: **the cache was keyed by `PrimitiveKind`.** A hundred cubes were one
`MeshData`. A hundred *edited* meshes would have been a hundred, each rebuilt and re-transformed every
frame, with the pass linear in the scene's total vertex count and no sharing left in it.

⚠ **This is the one item here that could be mistaken for a performance concern and was not.** A drag
that redraws at four frames a second is not a slow tool, it is a tool nobody can aim.

**What was built** is the device-resident half of what [20's risk table](plan/20-editor-parity.md#risks)
calls the viewport wiring, and none of the material half:

| Piece | Where |
|---|---|
| `MeshInstanceRenderer` — each shape's geometry in a `GeometryBuffer`, written once; a per-entity instance ring; one draw per shape, and one more for its wireframe | [MeshInstanceRenderer.cs](../Core/Vixen.Rendering/MeshInstanceRenderer.cs) |
| `MeshInstanced.rvn` — the transform, the normal matrix and the outline's expansion in the vertex stage | [MeshInstanced.rvn](../Editor/Vixen.Editor.App/Shaders/MeshInstanced.rvn) |
| `SceneMeshes` — one `MeshInstance` per entity, grouped into a `ShapeBatch` per shape | [SceneMeshes.cs](../Editor/Vixen.Editor.SceneView/SceneMeshes.cs) |
| `ScenePresenter.Resolve` — where a `PrimitiveKind` becomes a range in a vertex buffer, on the frame the first entity wanting it appears | [ScenePresenter.cs](../Editor/Vixen.Editor.App/ScenePresenter.cs) |

A frame costs a hundred and sixty bytes an entity now — a transform, a normal matrix, a colour and four
style lanes — whether the entity is a cube or a corridor. Three things that were *copies of the
geometry* are style lanes on an instance: the selection outline, the wireframe view's edges and the
normal view's per-vertex colour. Selecting a whole floor used to double the frame's vertex count and
now costs one more instance per object.

⚠ **Two consequences to know before [P1](#p1--the-mesh-15-em).** The outline's width is in pixels, so
its expansion moved into the vertex stage — there is no vertex on the processor to expand any more —
and what can now be wrong without a test is the picture that expansion makes rather than the numbers it
is made of, which are asserted against `EditorCamera.WorldPerPixel` in both projections. And a
block-out mesh is one shape per *entity* rather than one per kind, which is a batch of one and an
allocation per mesh: the geometry buffer is per renderer today, so a four-pane layout holding four
copies of a level's geometry is the thing to fix when the shapes stop being eight primitives.

**What is still Phase 7's** is the material system. The viewport has one key direction, one ambient term
and a colour per instance, so [P5](#p5--surfaces-10-em) is gated exactly as it was, and so is the
picking stage.

### B2. There is no `IEditorMode`, and blockout is the second mode 🟡

[20 § A1](plan/20-editor-parity.md#a1--the-application-frame) already argues for this and already
says why: a mode is "a statement about what the viewport's input means right now", and retrofitting
one "is how editors end up with six mutually-exclusive booleans on the viewport". It proposes
shipping the interface with one mode (Select) so the seam is proven.

Blockout is what proves it. It needs first refusal on viewport input, its own toolbar, its own
context menu, and — the part that makes the seam necessary rather than nice — **its own claim on
keys that already mean something.** `1`/`2`/`3` for vertex/edge/face is the universal binding, and
[20 § B2](plan/20-editor-parity.md#b2--the-viewport) gives `1..9` to view-bookmark recall. Both are
right. A mode that owns its keys while active and releases them when it is not is the only
resolution that does not make one of them worse.

### B3. Nothing at run time can hold a mesh 🟡

`MeshShape` is an editor component, and its own remarks explain why: `Vixen.Engine` deliberately does
not reference `Vixen.Rendering`, so there is nowhere to name a mesh in a runtime component.
[20 § E1](plan/20-editor-parity.md#e1--the-three-panels-people-live-in-20-em) records the same gap
from the other end — dragging an asset into the scene is not built because "no runtime component
carries an `AssetId`, so there is nothing for an entity to hold a mesh or a texture *in*".

A blockout mesh has exactly this problem, and `MeshShape` has already established the answer: it is a
key of its own in the `.vxscene` rather than an entry in the registered-component list, because a
component no build declares is what a content compile refuses. Blockout geometry takes the same
bargain and inherits the same migration — the day the runtime grows a mesh component, both become
ordinary entries and the change is in the reader.

⚠ **What is genuinely blocked is only the last phase.** [P7](#p7--handoff-10-em) bakes a block-out
into an asset and points an entity at it, and *that* needs a component holding an `AssetId`.
Everything before it stores the geometry in the scene file, which is where a block-out belongs
anyway: it is level data, not a shared asset, and a designer who has to save six meshes to disk to
try a corridor has been given the DCC round-trip back under a different name.

### B4. Picking answers "which entity", and half the tools ask "which face" 🟡

Both answers exist as far as entities go — `ScenePicker`'s ray test, and `PickingRenderer`'s id
buffer, which nothing drives. Sub-object selection needs a third question with a different shape:
which face, edge or vertex of *this* mesh, within a screen-space tolerance, with the innermost
element winning, and with hover feedback fast enough to survive a mouse move.

That is the ray test with a different payload, not a new subsystem, and it is a ray test against one
mesh rather than against the scene. [P2](#p2--selection-10-em) does it on the CPU for that reason and
says what would move it to the id buffer later.

### B5. Snapping is declared and half-implemented 🟡

`SnapSettings.SnapToVertex` and `VertexRadius` are in the model and honoured by nothing; the SceneView
README says so, and [20 § E2](plan/20-editor-parity.md#e2--the-viewport-20-em) owes them. The reason
given — "it needs the mesh under the pointer" — stops being true the moment there *is* an editable
mesh under the pointer with an indexed vertex list. Blockout supplies the thing that unblocks the
feature it needs.

---

## Part 1 — The design

Six decisions, in the order they constrain each other.

### D1. The kernel is a separate runtime assembly, not part of the editor

`Core/Vixen.Geometry` — vertices, faces, edges and the operations over them — referencing
`Vixen.Core.Mathematics` and nothing else.

**Why `Core/` and not `Editor/`.** Three reasons and only the third is about the future. It is pure
arithmetic with no document, no selection and no device, so it belongs under the profile that is AOT-
compatible, trimmable, packable and API-checked, and `Vixen.Navigation` is the precedent — a managed
voxel bake with its own `PolyMesh`, in `Core/`, referencing no renderer. It is the only way the
operations are testable as functions rather than as gestures, which is the same bargain the whole of
`Vixen.Editor.SceneView` already makes. And a mesh operation is worth having at run time: procedural
level generation, destructible geometry and a runtime CSG are all things the same code answers, and
none of them are reachable from an editor assembly.

**It does not reference `Vixen.Rendering`.** `MeshData` lives there, and a geometry kernel that
depended on the render assembly to describe a triangle would be backwards. The kernel hands back its
own arrays; the six-line copy into `MeshData` lives in the editor assembly beside the code that
uploads it. `Vixen.Navigation` makes exactly this choice and it has cost it nothing.

The editor half is `Editor/Vixen.Editor.Blockout` — the mode, the tools, the gestures, the undo
commands and the drawing — with `Vixen.Editor.Blockout.Tests` beside it.

### D2. The mesh is faces over shared positions, and the two graphs are different

**Not a half-edge.** A half-edge structure cannot represent a non-manifold edge, and blockout
geometry is non-manifold constantly: a wall meeting a floor in a T, a boolean result, an imported
mesh with a stray internal face. A kernel that refuses those refuses the ordinary case. What is
stored instead is an indexed face set — faces as n-gon loops of corners — with an explicit edge
table where each edge names *up to* two faces and more is allowed and reported. That is close to
what `FDynamicMesh3` does, and the reporting is what lets a tool say "this operation needs a manifold
edge" instead of producing something wrong.

⚠ **The position graph and the shading graph are different graphs, and conflating them is the bug
this decision exists to prevent.** A cube's corner is one *position* and three *corners*, each with
its own normal, its own UV and possibly its own material. Vertex snapping, welding, edge loops and
"drag this corner" run on positions; normals, UVs, materials and smoothing run per corner. An
implementation with one vertex list either splits smooth shading every time it extrudes, or welds
UVs every time it merges — and both are discovered late, by an artist, in a mesh that is already in
a level. ProBuilder calls the position layer *shared vertices* and it is the single design choice
that makes its tools behave.

**Faces carry a group id.** Unreal's PolyGroups, and the reason to have them even in a polygon
kernel: a boolean returns triangles, and a face that was one wall before the cut has to still be one
wall afterwards or the next extrude acts on a sliver. Every operation propagates the id; a group is
what a tool selects and what a material is assigned to.

### D3. Every edit is a command, and a topology change stores the whole mesh

`CommandStack` already merges, transacts and marks clean, and its tests are randomised do/undo/redo.
Blockout commands go on it unchanged, with one rule:

- A **position** change — a gizmo drag on selected vertices — stores the before and after positions
  of the affected elements, and merges with the previous one if it is the same drag.
- A **topology** change — extrude, bevel, boolean, loop cut — stores **the whole mesh, before and
  after**.

The second looks wasteful and is the only honest answer. A boolean has no inverse to record; a bevel
with three segments touches every table in the structure; and an "undo" implemented as an inverse
operation is a second implementation of every tool, which will disagree with the first. A blockout
mesh is a few thousand vertices — tens of kilobytes — and `CommandStack.Capacity` defaults to 256,
so the worst case is a few megabytes of undo history for a mesh nobody has that big.

⚠ **State the bound rather than discovering it.** A designer who spends an hour on one mesh generates
the deep history this is measured against, and `Capacity` is settable for exactly this. A budget in
bytes rather than in entries is the change to make if it is ever hit — noted here so it is a decision
rather than a surprise.

### D4. Snapping is one service, above the gizmo

`SnapSettings` is a good model and it is attached to the gizmo. What the tools need is Blender's
arrangement: snapping as a *context* every transform consults, with three orthogonal parts.

| | |
|---|---|
| **Element** | increment, absolute grid, vertex, edge, edge centre, face, none |
| **Base** | the dragged element's own origin, the selection's centre, the active element, the point under the cursor when the drag began |
| **Modifiers** | align rotation to the target's normal; project along the view; ignore the mesh being edited |

`SnapSettings` grows into `SnapContext` — the existing four booleans and three steps stay, so nothing
that reads it today changes — and `TransformGizmo`, `ScenePlacement` and every blockout tool take the
same one. The alternative is what every editor that added snapping per tool has: a vertex snap that
works when you drag an object and not when you extrude a face, which reads as the feature being
broken.

⚠ **The base is the half everybody omits and it is the half that matters.** Snapping the *centre* of
what you dragged to a vertex is almost never what you meant; you meant the corner you grabbed. A
snap with no base concept can only offer the first.

### D5. The grid is a plane with a transform

`SceneGrid` today is a floor: adaptive 1-2-5 spacing, emphasis on round numbers, reach in screen
heights. All of that stays and becomes a *view* of a `WorkPlane` — an origin, a rotation and a step —
which defaults to the ground and can be moved.

This is CubeGrid's repositionable grid and Blender's 3D cursor, and it is worth its own decision
because it is what makes the three most common blockout gestures possible at all:

- **Set the plane to a face.** Select a wall, press a key, and the grid is on the wall. Everything
  placed, dragged and snapped afterwards is in the wall's plane. Nothing else makes building a
  doorway a two-minute job.
- **Double and halve the step.** Building at 4 m, then 1 m, then 0.25 m, without a settings panel.
  ⚠ Powers of two by default, because a grid you can halve indefinitely is one where every level is a
  sub-lattice of the last — a 0.25 m object is still on the 4 m grid's lines, and a 1/3 m one never
  will be again.
- **Offset the plane along its normal.** Building the second floor at 3 m without doing arithmetic.

The step is also what `SnapContext`'s increment element reads, so "the grid I can see" and "the grid
I snap to" are one number. They are two in more than one shipping editor and it is a bug people never
manage to describe.

### D6. Parametric first, editable second, and the demotion is explicit

A shape is created with live parameters — a stair with a rise, a run and a count; a box with three
extents; an arch with a radius and a segment count. Editing a parameter rebuilds it. This is
ProBuilder's model and it is right because the overwhelmingly common blockout edit is *"that corridor
should be a metre wider"*, which is one number, not a face selection.

The moment a face is edited, the shape becomes a plain mesh and its parameters are gone. That is a
one-way door and it is presented as one — a confirmation the first time, a badge on the entity
afterwards. The alternative, a parametric history that survives editing, is a node-based modeller
(Archimatix), which is [out of scope](#the-row-this-overturns) and is out of scope for the reason
that it is authoring for its own sake.

---

## Part 2 — The tool inventory

The verbs, with their bindings. `⋯` opens a settings popover; every binding is a registered command
and rebindable, and every one is claimed by the mode and released when it deactivates
([B2](#b2-there-is-no-ieditormode-and-blockout-is-the-second-mode-)).

### Selection

| Verb | Binding | Notes |
|---|---|---|
| Object / vertex / edge / face mode | `1` `2` `3` `4` | Mode-scoped. `Tab` enters and leaves the mesh |
| Extend / toggle | `Shift`+click | Same modifier as entity selection, for the same reason |
| Marquee | drag on empty | Shares [20 § E2](plan/20-editor-parity.md#e2--the-viewport-20-em)'s region resolve |
| Loop / ring | `Alt`+click / `Ctrl+Alt`+click | The edge table's whole reason for existing |
| Grow / shrink | `Ctrl+↑` / `Ctrl+↓` | |
| Select by group / material / plane | menu | "Every coplanar face" is the blockout-specific one |
| Invert, all, none | `Ctrl+I`, `Ctrl+A`, `Alt+A` | |

### Geometry

| Verb | Binding | Notes |
|---|---|---|
| **Extrude** | `E`, or `Ctrl`+drag the gizmo | Faces, edges and vertices. The one verb that must be perfect |
| **Inset / outset** | `I` | Per-face and as-a-region are different answers; both, `I` twice |
| **Bevel / chamfer** | `Ctrl+B` | Edges and vertices, with a segment count |
| **Loop cut** | `Ctrl+R` | Preview follows the pointer; scroll sets the count; slide before committing |
| **Knife / cut** | `K` | Free cut across faces, snapping to edges and midpoints |
| **Bridge** | `Ctrl+E` | Two edge loops or two faces |
| **Weld / merge** | `M ⋯` | By distance, to centre, to last, to the cursor |
| **Collapse / dissolve** | `X ⋯` | Dissolve removes an element and keeps the surface; delete makes a hole |
| **Subdivide** | menu | Faces and edges, with a count |
| **Fill hole** | `F` | Also "make face from selection", which is the same code |
| **Flip normals / flip face edge** | menu | Per-face and per-object |
| **Detach / separate** | `P` | To a new entity, or in place |
| **Merge objects** | menu | The inverse, and what makes a room one mesh before baking |
| **Offset elements** | `Ctrl+drag` | Move along the *normal* rather than along an axis |

### Creation

| Verb | Binding | Notes |
|---|---|---|
| **Shape tool** — box, cylinder, cone, sphere, capsule, torus, plane, **stairs, ramp, arch, pipe, door frame** | `Shift+A ⋯` | Drag a footprint on the work plane, then drag the height. Parameters stay live ([D6](#d6-parametric-first-editable-second-and-the-demotion-is-explicit)) |
| **Cube grid** | `G` | The whole of CubeGrid's interaction, including corner mode. Its own tool because it has its own selection model |
| **Poly shape** | `Shift+D` | Click a polygon on the work plane, then drag the height. How every irregular room gets made |
| **Duplicate** | `Ctrl+D` / `Alt`+drag | In place, and drag-to-place with snapping |
| **Mirror** | `Ctrl+M ⋯` | Across the work plane. Instance or copy |
| **Array / pattern** | menu ⋯ | Linear, radial, grid. Count and spacing, live |

### Placement and precision

This is the group that separates a toolset a professional will use from one they will try.

| Verb | Binding | Notes |
|---|---|---|
| **Numeric entry mid-drag** | type during any drag | Blender's `G X 5 ⏎`. Axis letters constrain, `-` negates, `Tab` moves between components, `Esc` cancels. Costs almost nothing given the gizmo's recompute-from-mouse-down design, and is the single most-missed feature by anyone coming from Blender |
| **Snap element / base / modifiers** | `Shift+Tab` toggles; `⋯` for the popover | [D4](#d4-snapping-is-one-service-above-the-gizmo) |
| **Work plane to face / to selection / to world** | `Shift+G` ⋯ | [D5](#d5-the-grid-is-a-plane-with-a-transform) |
| **Grid step double / halve** | `]` / `[` | Powers of two |
| **Edit pivot** | `Ctrl+.` | An entity's origin is where it rotates and where it snaps from; a wall's belongs at its corner |
| **Align and distribute** | `⋯` | Across a multi-selection, per axis, by min/centre/max. [20 § Part D](plan/20-editor-parity.md#transform) already owes this |
| **Measure** | `Shift+M` | Click two points and read the distance; angle from three. Snaps like everything else |
| **Dimensions during a drag** | always | The extent in metres, on screen, while resizing. Both reference editors make you read a details panel |
| **Reference volumes** | menu | A 1.8 m capsule, a door, a corridor, a vehicle — a *scale reference*, drawn and not shipped. The thing every level designer builds by hand on every project |

### Surfaces

| Verb | Binding | Notes |
|---|---|---|
| **Per-face material** | palette | Assign to a face selection. Needs the material system in the viewport |
| **Auto UV — planar / box / world-space** | `⋯` | **World-space is the default and that is the decision.** A blockout box scaled 8×3 must not stretch its texels, and a checker whose squares are a fixed number of metres everywhere is what makes proportion readable at a glance |
| **UV offset / rotate / scale / fit** | `⋯` | Per face. Not an unwrapper |
| **Smoothing groups** | `⋯` | Per face; a hard edge is the absence of one |
| **Vertex colours** | palette | Cheap per-face tinting for readability before there is art |
| **Blockout material** | default | The checker every editor makes you build. Grid at the work-plane step, a contrasting axis tint, and legible at grazing angles |

### Boolean and cutting

| Verb | Binding | Notes |
|---|---|---|
| **Union / subtract / intersect** | `⋯` | [P6](#p6--csg-20-em). Non-destructive first: the operands stay, the result is derived and re-evaluated |
| **Plane cut** | `⋯` | Cut by the work plane, keep one or both halves, cap the opening |
| **Trim** | `⋯` | Cut by another mesh's surface without a full boolean — cheaper, and what most "cut a doorway" actually wants |

### Handoff

| Verb | Notes |
|---|---|
| **Bake to mesh asset** | Writes a `.vxmesh` and points the entity at it. Where the block-out becomes something an artist replaces |
| **Generate collision** | Box per convex piece, or the mesh itself. `ShapeDescription` already has convex-hull and mesh kinds |
| **Export OBJ / glTF** | The artist opens the block-out in their DCC and models to it. **This is what makes "import from a DCC" and this document the same workflow rather than competing ones** |
| **Import back as editable** | ProBuilderize: an imported mesh becomes editable, which is how a designer adjusts art they were given |

---

## Part 3 — Phases

Effort in engineer-months, on [14](plan/14-roadmap.md)'s scale and benchmarked the same way. **Total
11.0 EM**, which is comparable to the whole of [20 § Part E](plan/20-editor-parity.md#part-e--milestones),
and that comparison is the honest framing rather than a reason not to start.

The ordering is the important part. **P0–P4 is 7.0 EM and is where the value is**; each of P5, P6 and
P7 is separable, and stopping after any phase leaves a tool somebody uses rather than a branch
somebody abandons. The cut line is drawn at each phase below.

### P0 — The seam (1.0 EM)

`IEditorMode` and the mode bar ([20 § A1](plan/20-editor-parity.md#a1--the-application-frame)),
shipped with two modes — Select, and a Blockout mode that so far only owns its keys. `SnapContext`
([D4](#d4-snapping-is-one-service-above-the-gizmo)) with element, base and modifiers, honouring
`SnapToVertex` and `SnapToSurface` against the primitives that already exist. `WorkPlane`
([D5](#d5-the-grid-is-a-plane-with-a-transform)) with `SceneGrid` drawing it, set-to-face, offset,
and step doubling. **Numeric entry during any gizmo drag.** Measure, dimensions-during-drag, and
reference volumes.

**Exit:** a designer can place, drag and rotate the *existing* primitives with vertex and surface
snapping, on a grid they moved onto a wall, typing exact distances, and read the result in metres —
with no editable mesh in the engine yet.

**If you stop here** you have shipped most of what [20 § E2](plan/20-editor-parity.md#e2--the-viewport-20-em)
owes for transforms, plus a precision story neither reference editor has. That is a real release.

### P1 — The mesh (1.5 EM)

`Core/Vixen.Geometry`: `EditMesh` ([D2](#d2-the-mesh-is-faces-over-shared-positions-and-the-two-graphs-are-different))
— faces, corners, shared positions, an edge table, face groups, attribute layers — with construction
from `MeshPrimitives`' eight shapes, triangulation out, validation (manifoldness reported, not
enforced; no orphaned corners; consistent winding), and a bounds. The `MeshData` adapter in the
editor. Per-entity storage and `.vxscene` serialisation as a key of its own
([B3](#b3-nothing-at-run-time-can-hold-a-mesh-)). One command type, both granularities
([D3](#d3-every-edit-is-a-command-and-a-topology-change-stores-the-whole-mesh)).

⚠ **The scene format is where this phase can go wrong quietly.** A mesh is the first thing in a
`.vxscene` that is not a handful of scalars, and the format's own promise is a byte-identical
round trip. A vertex list written at whatever precision `float.ToString` gives makes every scene a
merge conflict with itself — the same failure the format already documents for vectors — so the
round-trip test has to arrive with the writer rather than after it.

**Exit:** a cube in a scene is an `EditMesh`; it saves, reloads and re-saves to identical bytes;
moving one vertex is undoable; nothing looks different on screen.

### P2 — Selection (1.0 EM)

Vertex/edge/face modes, hover and selection highlight drawn through `SceneLines` and `MeshRenderer`'s
overlay pipelines (both exist), sub-object ray picking with innermost-wins and screen-space tolerance
([B4](#b4-picking-answers-which-entity-and-half-the-tools-ask-which-face-)), loops and rings, grow and
shrink, select-by-group / by-material / coplanar, and the marquee — which is
[20 § E2](plan/20-editor-parity.md#e2--the-viewport-20-em)'s region resolve and should be built once,
there, for both.

**Exit:** every element of a mesh can be selected by every gesture in the table above, and the
selection survives an undo of an edit that did not change topology.

### P3 — The verbs (2.0 EM)

Everything in [Geometry](#geometry). Extrude first and alone until it is right — faces, edges,
vertices, region-vs-individual, along the normal and along an axis, with snapping — because every
other verb is judged against how that one feels.

⚠ **Bevel is the one that looks small and is not.** A bevel with segments on an edge that meets three
other bevelled edges at a vertex is a miniature research problem, and the honest first version bevels
edges independently and reports where it could not resolve a corner, rather than producing a
self-intersecting one silently.

**Exit:** a room with a doorway, a window and a chamfered edge is built in the viewport, from a cube,
without leaving it; every operation is undoable and every one round-trips through the scene file.

**If you stop here** you have ProBuilder's geometry menu, which is what most people mean when they
say blockout.

### P4 — Creation (1.5 EM)

The shape tool with live parameters and the demotion rule ([D6](#d6-parametric-first-editable-second-and-the-demotion-is-explicit)),
including the level-design shapes — stairs, ramp, arch, pipe, door frame — which are the ones that
are tedious to build by hand and are why UE ships a Stairs tool. The cube-grid tool, whole, including
corner mode. Poly shape. Duplicate, mirror, array.

**Exit:** a two-storey building with stairs between the floors, blocked out in one session, in a
scene that opens in the shipped editor.

### P5 — Surfaces (1.0 EM)

Per-face materials, auto-UV in planar / box / **world-space** (default), UV transform per face,
smoothing groups, vertex colours, and the blockout checker material.

⚠ **This phase is gated on something outside it, and the gate did not move when
[B1](#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-) closed.** The viewport draws
untextured shading: one key direction, one ambient term, a colour per instance. Per-face materials and a
checker need the material system wired to the editor's viewport, which is
[14](plan/14-roadmap.md) Phase 7 — B1 took the device-resident geometry out of that dependency and left
the materials in it. P5 is therefore still the phase most likely to move, and it is placed after P4
rather than before it for exactly that reason.

**Exit:** a block-out reads as a space rather than as a grey mass; a scaled box's checker squares are
the same size as an unscaled one's.

### P6 — CSG (2.0 EM)

Boolean — union, subtract, intersect — over `ExactPredicates`, non-destructively: the operands
survive as hidden children, the result is derived, and changing an operand re-evaluates it. Plane
cut. Trim. Then the destructive "apply" that collapses it to a plain mesh.

**Plane-based, not point-based.** Each face carries its supporting plane as the exact intersection of
the planes that defined it, rather than as three floating-point points — the trick Quake's tooling
and RealtimeCSG both use — and classification asks `ExactPredicates.Orient3D` rather than a
tolerance. Coplanar faces are where every point-based boolean produces cracks, and they are what a
block-out is *made of*: every wall meets every floor coplanar with something.

⚠ **Budget this phase as though it were two, and read ProBuilder's seven-year "experimental" label as
data rather than as an anecdote.** The exit criterion is not "it works on the demo"; it is the
property suite below finding no hole across ten thousand randomised operand pairs.

**If you stop before this** you have every reference toolset's non-experimental feature set. That is
the argument for putting it last despite it being the most requested item.

### P7 — Handoff (1.0 EM)

Bake to a `.vxmesh` asset through the existing importer machinery, with the entity pointed at it —
the one part genuinely blocked on a runtime component carrying an `AssetId`
([B3](#b3-nothing-at-run-time-can-hold-a-mesh-)). Collision generation into `ShapeDescription`. OBJ
and glTF export. Import-back-as-editable.

**Exit:** a block-out becomes an asset, an artist opens it in a DCC, replaces it, and the level does
not change shape.

### Cost

| Phase | EM | Blocked on |
|---|---|---|
| P0 — The seam | 1.0 | — |
| P1 — The mesh | 1.5 | — |
| P2 — Selection | 1.0 | P1; shares the marquee with [E2](plan/20-editor-parity.md#e2--the-viewport-20-em) |
| P3 — The verbs | 2.0 | P1, P2 |
| P4 — Creation | 1.5 | P1, P3 |
| P5 — Surfaces | 1.0 | 🔴 the material system in the editor viewport |
| P6 — CSG | 2.0 | P1 |
| P7 — Handoff | 1.0 | 🟡 a runtime component holding an `AssetId` |
| | **11.0** | |

**And one cost that was not in the table and has been paid.**
[B1](#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-) — drawing meshes from
device-resident buffers instead of a per-frame CPU gather — was the precondition this document could not
ship past about P3 without. It is built, ahead of the rest of [14](plan/14-roadmap.md) Phase 7 rather
than as part of it, so no phase below is blocked on it and P1 inherits a viewport that draws a mesh per
entity for the price of a transform.

---

## Part 4 — Testing

The same bargain the rest of `Vixen.Editor.SceneView` makes, and it applies more strongly here than
anywhere else in the editor: **the kernel is pure functions over arrays, so almost all of this is a
unit test with no world, no renderer and no device.**

| Level | Mechanism |
|---|---|
| **Invariants after every operation** | Euler characteristic where the operation claims to preserve it; no orphaned corners; every edge naming faces that name it back; winding consistent within a group; no zero-area faces. One assertion helper, called by every operation's tests |
| **Property tests** | CsCheck, as `Vixen.Core.Mathematics` already does. Extrude by `d` then by `−d` returns the original mesh. Bevel by zero is the identity. Weld with a distance below the minimum edge length is the identity. Subdivide preserves volume. Loop cut preserves surface area |
| **Randomised do/undo/redo** | `Vixen.Editor.Core`'s existing suite over blockout commands: a random operation sequence, undone to empty and redone to the end, asserting mesh equality at every step. This is what catches a command that stored a reference where it needed a copy |
| **Boolean, adversarially** | Randomised operand pairs including the degenerate cases that are the *normal* cases here — coplanar faces, shared edges, identical operands, an operand entirely inside another, zero-volume intersections. The gate is no hole and no self-intersection, not no exception |
| **Round trip** | Every mesh built by every test saves to `.vxscene` and reloads to an identical mesh, and re-saves to identical bytes. The format's own standing promise |
| **Gestures** | `SceneViewport`'s existing pattern: synthetic pointer input against the real tool, asserting the mesh rather than the pixels. "Dragging the extrude handle fifteen pixels moves the face the right distance" is a unit test for the same reason the gizmo's version is |
| **Golden screenshots** | Only for what is *drawn*: selection highlight, hover, the work plane, the cube grid's preview, the checker material. The suite [20 § Part F](plan/20-editor-parity.md#part-f--testing) already gates |

⚠ **The invariant helper is the highest-value item in that table and it has to be written first.** A
mesh operation that corrupts the edge table produces geometry that looks correct and fails three
operations later, in a mesh a designer has spent an hour on, with no way to attribute it. Every
operation asserting the whole structure afterwards turns that into a failing test in the commit that
caused it.

---

## Risks

| Risk | Mitigation |
|---|---|
| ~~**The viewport cannot draw this many meshes**~~ ([B1](#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-)) | Closed. It was treated as a precondition rather than a parallel task, and the device-resident half of Phase 7's viewport wiring was built first: shapes live in a `GeometryBuffer` and a frame is one instance per entity. What remains of that dependency is the material system, which is P5's gate and not P2's |
| **Boolean absorbs the schedule** | It is last, it is the only phase with a research-shaped risk, and every phase before it exits with something shippable. `ExactPredicates` removes the part that usually causes the overrun |
| **This is 11 EM and reads as a second editor** | The cut line is real and stated per phase. P0 alone improves the existing transform tools; P0–P3 is the reference toolsets' core; P4 is where it becomes a level-design tool |
| **Scope creep into modelling** | [The table at the top](#the-row-this-overturns) is the test, and the test is "between two playtests", not "is it hard". A proposal that fails it is a DCC feature |
| **Two selection models in one viewport** | Entity selection and sub-object selection are genuinely different and the mode is what keeps them apart. This is why [P0](#p0--the-seam-10-em) builds `IEditorMode` before anything selects a face |
| **Undo memory** ([D3](#d3-every-edit-is-a-command-and-a-topology-change-stores-the-whole-mesh)) | Bounded and stated. A byte budget replaces the entry count if it is ever hit |
| **A designer builds a level out of blockout meshes and it ships** | ⚠ This *will* happen and it is not a failure — it is what happened at every studio that shipped ProBuilder geometry. It is a reason P7's collision generation and asset bake are in the plan rather than a reason to prevent it |

---

## Documents this changes

| Document | Change |
|---|---|
| [20 § Part G](plan/20-editor-parity.md#part-g--out-of-scope) | The "Mesh editing / modelling tools" row now points here, with the line redrawn rather than erased |
| [20 § A1](plan/20-editor-parity.md#a1--the-application-frame) | `IEditorMode`'s second mode is named, so the seam has a consumer rather than a hypothesis |
| [20 § E2](plan/20-editor-parity.md#e2--the-viewport-20-em) | Vertex snap, surface snap and the marquee are shared with [P0](#p0--the-seam-10-em) and [P2](#p2--selection-10-em) and should be built once |
| [02](plan/02-repository-layout.md) | Two assemblies: `Core/Vixen.Geometry` and `Editor/Vixen.Editor.Blockout`, each with its tests |
| [11 § `Vixen.Editor.SceneView`](plan/11-editor.md) | The "not in" list's vertex snapping and rubber-band selection are closed by [P0](#p0--the-seam-10-em) and [P2](#p2--selection-10-em) |
| [14](plan/14-roadmap.md) | Phase 7's viewport wiring gained a second dependant and split in two: the device-resident geometry is built ([B1](#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-)), and the material half is what [P5](#p5--surfaces-10-em) and the picking stage still wait on |

Licensed under Apache-2.0.
