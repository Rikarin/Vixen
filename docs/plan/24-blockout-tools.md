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
[bindless-materials.md](23-bindless-materials.md) and [virtualized-geometry.md](22-virtualized-geometry.md)
are: it is larger than a row in a status table, several things depend on pieces of it, and the first
half of it is an argument rather than a schedule.

**Read [the row this overturns](#the-row-this-overturns) before the phases.** This document
contradicts a decision already recorded in [20 § Part G](20-editor-parity.md#part-g--out-of-scope),
and a plan that quietly reversed one would be worth less than no plan.

---

## The row this overturns

[20 § Part G](20-editor-parity.md#part-g--out-of-scope) lists, under things deliberately not
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

The right-hand column is [20 § Part G](20-editor-parity.md#part-g--out-of-scope)'s sentence,
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
| `MeshData` — parallel typed arrays, empty means absent | ✅ | [MeshData.cs](../../Core/Vixen.Rendering/MeshData.cs) |
| `MeshPrimitives` — eight shapes, every one fitting the unit cube, CCW, no device | ✅ | [MeshPrimitives.cs](../../Core/Vixen.Rendering/MeshPrimitives.cs) |
| `PrimitiveShape` + `PrimitiveShapes` — a primitive kind per entity, as a runtime component a scene names | ✅ | [MeshComponents.cs](../../Core/Vixen.Rendering/Ecs/MeshComponents.cs) |
| `SceneMeshes` — every shaped entity as one instance, grouped per shape | ✅ | [SceneMeshes.cs](../../Editor/Vixen.Editor.SceneView/SceneMeshes.cs) |
| `MeshInstanceRenderer` — device-resident shapes, a per-entity transform, one draw per shape | ✅ | [MeshInstanceRenderer.cs](../../Core/Vixen.Rendering/MeshInstanceRenderer.cs) |
| `MeshRenderer` — world-space triangles for the gizmo's solid handles, which are rebuilt per frame | ✅ | [MeshRenderer.cs](../../Core/Vixen.Rendering/MeshRenderer.cs) |
| `TransformGizmo` — four modes, four spaces, two pivots, **recomputed from mouse-down** | ✅ | [TransformGizmo.cs](../../Editor/Vixen.Editor.SceneView/TransformGizmo.cs) |
| `SnapContext` — element, base and modifiers over the grid / angle / scale steps; one per editor | ✅ | [SnapContext.cs](../../Editor/Vixen.Editor.SceneView/SnapContext.cs) |
| Vertex, edge, edge-centre and surface snapping, honoured *and* reachable | ✅ | [B5](#b5-snapping-is-declared-and-half-implemented-) |
| `SceneGrid` — 1-2-5 adaptive spacing, emphasis on round numbers, screen-height reach | ✅ | [SceneGrid.cs](../../Editor/Vixen.Editor.SceneView/SceneGrid.cs) |
| `ScenePlacement` / `ISurfaceProbe` / `SurfaceHit` — a ray to a place to put something | ✅ | [ScenePlacement.cs](../../Editor/Vixen.Editor.SceneView/ScenePlacement.cs) |
| `ScenePicker` — exact ray tests per primitive, in local space, pixel-sized markers | ✅ | [ScenePicker.cs](../../Editor/Vixen.Editor.SceneView/ScenePicker.cs) |
| `SubObjectPicker` + `MeshElements` — which face, edge or vertex of *one* mesh, innermost wins | ✅ | [SubObjectPicker.cs](../../Editor/Vixen.Editor.SceneView/SubObjectPicker.cs) |
| `PickingRenderer` / `PickingBuffer` — id buffer, one-pixel readback, ring-deep | 🟡 | written, driven by nothing |
| `CommandStack` — merging, transactions, clean-marking, randomised do/undo/redo tests | ✅ | [CommandStack.cs](../../Editor/Vixen.Editor.Core/CommandStack.cs) |
| `SceneDocument` — undoable create/delete/rename, names outside the world, hidden set | ✅ | [SceneDocument.cs](../../Editor/Vixen.Editor.SceneView/SceneDocument.cs) |
| `.vxscene` — YAML, GUID identities, byte-identical round trip, version-checked | ✅ | [SceneSerializer.cs](../../Editor/Vixen.Editor.SceneView/SceneSerializer.cs) |
| **`ExactPredicates`** — filtered exact `Orient3D` / `InSphere` over `BigInteger` | ✅ | [ExactPredicates.cs](../../Core/Vixen.Core.Mathematics/ExactPredicates.cs) |
| `PhysicsWorld.Raycast` — an `ISurfaceProbe` implementation waiting to be written | ✅ | [PhysicsWorld.Queries.cs](../../Core/Vixen.Physics/PhysicsWorld.Queries.cs) |
| `ShapeDescription` — box, sphere, capsule, cylinder, **convex hull, mesh** | ✅ | [ShapeDescription.cs](../../Core/Vixen.Physics/Shapes/ShapeDescription.cs) |
| `ModelImporter` — Assimp, one sub-asset per mesh, addressed by name | ✅ | [ModelImporter.cs](../../Editor/Vixen.Editor.Assets/Models/ModelImporter.cs) |
| `GeometryBuffer` — many meshes in one vertex and one index buffer | ✅ | [GeometryBuffer.cs](../../Core/Vixen.Rendering/GeometryBuffer.cs) |
| `Vixen.Navigation` — a managed voxel bake over level geometry, contour → `PolyMesh` | ✅ | Core/Vixen.Navigation |
| `EditMesh` — faces over shared positions, an edge table that reports, face groups | ✅ | [Core/Vixen.Geometry](../../Core/Vixen.Geometry/README.md) |
| `MeshTopology` + `MeshSelection` — loops, rings, coplanar regions, and one set with a kind | ✅ | [MeshTopology.cs](../../Core/Vixen.Geometry/MeshTopology.cs) |
| `MeshOperations` — the geometry verbs, over the tables, with ear clipping under them | ✅ | [MeshOperations.cs](../../Core/Vixen.Geometry/MeshOperations.cs) |
| `MeshEdit` + `MeshGizmoTarget` — which mesh is being edited, and its selection as one thing to drag | ✅ | [MeshEdit.cs](../../Editor/Vixen.Editor.SceneView/MeshEdit.cs) |
| An edited mesh drawn as one shape per *entity*, retired a ring depth after it is replaced | ✅ | [SceneMeshes.cs](../../Editor/Vixen.Editor.SceneView/SceneMeshes.cs) |
| `IEditorMode` + `EditorModes` — the mode bar, Select, and Blockout owning its keys | ✅ | [Modes/](../../Editor/Vixen.Editor.Ui/Modes/IEditorMode.cs), [Vixen.Editor.Blockout](../../Editor/Vixen.Editor.Blockout/README.md) |

**`ExactPredicates` is the most valuable line in that table and it did not get built for this.** It
was written for `DelaunayTetrahedralization`, and it is the exact thing a robust boolean needs: a
sign that is *the* sign, filtered so the exact path essentially never runs. Every mesh boolean that
produces holes produces them because a predicate near zero answered wrongly and the classification
became inconsistent. Having that already solved, tested and fast moves the boolean from "the phase
that might not land" to "the phase that is merely large".

**The gizmo being recomputed from mouse-down is the second.** [Numeric entry](#p0--the-seam-10-em) — the
Blender idea above — is, in that design, substituting a typed magnitude for the dragged one before
the same arithmetic runs. In an implementation that accumulated per-frame deltas it is not
expressible at all.

---

## What blocks it

Five things, and all five are now built: the per-frame CPU gather, the mode seam, the runtime mesh
component, the sub-object query and the snapping service. What is left of P0 is the work rather than
the blockers — the work plane, numeric entry, measurement and the reference volumes.

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

**What was built** is the device-resident half of what [20's risk table](20-editor-parity.md#risks)
calls the viewport wiring, and none of the material half:

| Piece | Where |
|---|---|
| `MeshInstanceRenderer` — each shape's geometry in a `GeometryBuffer`, written once; a per-entity instance ring; one draw per shape, and one more for its wireframe | [MeshInstanceRenderer.cs](../../Core/Vixen.Rendering/MeshInstanceRenderer.cs) |
| `MeshInstanced.rvn` — the transform, the normal matrix and the outline's expansion in the vertex stage | [MeshInstanced.rvn](../../Editor/Vixen.Editor.App/Shaders/MeshInstanced.rvn) |
| `SceneMeshes` — one `MeshInstance` per entity, grouped into a `ShapeBatch` per shape | [SceneMeshes.cs](../../Editor/Vixen.Editor.SceneView/SceneMeshes.cs) |
| `ScenePresenter.Resolve` — where a `PrimitiveKind` becomes a range in a vertex buffer, on the frame the first entity wanting it appears | [ScenePresenter.cs](../../Editor/Vixen.Editor.App/ScenePresenter.cs) |

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

### B2. There is no `IEditorMode`, and blockout is the second mode ✅

**Built.** The argument is kept because it is what the implementation answers, and because the shape
of the answer is what the phases below now assume.

[20 § A1](20-editor-parity.md#a1--the-application-frame) already argues for this and already
says why: a mode is "a statement about what the viewport's input means right now", and retrofitting
one "is how editors end up with six mutually-exclusive booleans on the viewport". It proposes
shipping the interface with one mode (Select) so the seam is proven.

Blockout is what proves it. It needs first refusal on viewport input, its own toolbar, its own
context menu, and — the part that makes the seam necessary rather than nice — **its own claim on
keys that already mean something.** `1`/`2`/`3` for vertex/edge/face is the universal binding, and
[20 § B2](20-editor-parity.md#b2--the-viewport) gives `1..9` to view-bookmark recall. Both are
right. A mode that owns its keys while active and releases them when it is not is the only
resolution that does not make one of them worse.

**What was built**, and the one thing worth knowing is that the key claim needed no new mechanism:

| Piece | Where |
|---|---|
| `IEditorMode` — id, title, icon, context, panel, toolbar, a register/unregister pair, an activation pair, and first refusal on pointer and key input | [IEditorMode.cs](../../Editor/Vixen.Editor.Ui/Modes/IEditorMode.cs) |
| `EditorModes` — the registry behind the mode bar: one `Add` gives a mode a button, a radio entry in the palette, a context in the keymap and a claim on input | [EditorModes.cs](../../Editor/Vixen.Editor.Ui/Modes/EditorModes.cs) |
| `SelectMode` — the neutral mode, which claims nothing, so that a viewport in it is the viewport as it was | [SelectMode.cs](../../Editor/Vixen.Editor.Ui/Modes/SelectMode.cs) |
| The mode bar between the menu bar and the toolbar, hidden while nothing has registered a mode | [EditorShell.cs](../../Editor/Vixen.Editor.Ui/EditorShell.cs) |
| `IViewportInput` — the pane's end of the seam, because `Vixen.Editor.SceneView` does not reference the shell | [ViewportInput.cs](../../Editor/Vixen.Editor.SceneView/ViewportInput.cs) |
| `BlockoutMode` + `BlockoutElement` — the four element modes on `1`–`4`, `Tab` in and out of the mesh | [Editor/Vixen.Editor.Blockout](../../Editor/Vixen.Editor.Blockout/README.md) |
| `PluginContext.AddMode` — the extension point [20 § A1](20-editor-parity.md#a1--the-application-frame) says joins the other eight | [PluginContext.cs](../../Editor/Vixen.Editor.Plugin/PluginContext.cs) |

⚠ **The key conflict was resolved with the machinery that was already there, and that is the finding
worth recording.** `EditorCommand.Context` and `KeyMap`'s per-context chord table are how the
outliner and the content browser already share Delete — so a blockout command declaring
`Context = "blockout"` binds `2` under that context while `scene.bookmark-go-2` keeps it globally,
and `KeyMap.CommandFor` resolves the context's binding first and the global one second. Neither
command moved, neither gave up its key, and no new arbitration was written. What the application
supplies is the one fact only it knows: that a press in the scene pane means the active mode's
context rather than the outliner's.

⚠ **First refusal is refusal over what a gesture *starts*.** A pointer event arriving while the gizmo
is dragging or a rubber-band is open goes to the pane whatever the mode says, because a mode that
could take the release of a drag it did not begin would leave the gizmo holding the object. Keys are
the opposite and are offered mid-drag, because [P0](#p0--the-seam-10-em)'s numeric entry — `G X 5 ⏎`
— is only meaningful while a drag is in flight.

**What is still P0's** is everything else in that phase: `SnapContext`, `WorkPlane`, numeric entry
itself, measure, dimensions-during-drag and the reference volumes. The Blockout mode owns its keys
and declines every pointer event, which is what makes entering it safe before there is a mesh.

### B3. Nothing at run time can hold a mesh ✅

**Resolved, and the reasoning below is kept because it is what the resolution answers.** The claim was
that `Vixen.Engine` deliberately does not reference `Vixen.Rendering`, so there is nowhere to name a
mesh in a runtime component — and that is still true. What was wrong was the conclusion: the reference
that was needed runs the *other* way. `Vixen.Rendering` now references `Vixen.Ecs` and `Vixen.Engine`
and declares `MeshRenderable`, `PrimitiveShape` and `Light` itself, which is what `Vixen.Physics`,
`Vixen.Audio`, `Vixen.Animation` and `Vixen.Navigation` had all been doing the whole time.
[20 § E1](20-editor-parity.md#e1--the-three-panels-people-live-in-20-em) records the same gap from
the other end — dragging an asset into the scene, because "no runtime component carries an `AssetId`, so
there is nothing for an entity to hold a mesh or a texture *in*" — and it closes the same way.

The old answer was that a shape is a key of its own in the `.vxscene` rather than an entry in the
registered-component list, because a component no build declares is what a content compile refuses; and
that blockout geometry would take the same bargain until the runtime grew a mesh component, at which
point both would become ordinary entries and the change would be in the reader. That is exactly what
happened, and the migration cost what it was predicted to cost — `SceneSerializer` still reads the two
legacy keys and no longer writes them.

**How it closed:** `Vixen.Rendering` references `Vixen.Ecs` and
`Vixen.Engine` and owns `Ecs/MeshComponents.cs`, so `MeshRenderable` and `PrimitiveShape` are runtime
components a compiled scene names — as is `Light`, which moved out of the editor at the same time. The
loading half is closed too: a catalog entry carries its `vx:` reference, `ContentCatalog.TryGetAddress`
resolves one into an address, and `AssetManager.LoadAsync<T>(reference)` turns an `AssetId` into an
asset.

Drawing is now built for a primitive: `SurfaceGeometry` packs a `MeshData` into the vertex the shading
stages declare, `GeometryResidency` keeps one slice per mesh shared by every entity drawing it, and
`MeshExtractionSystem` turns a `PrimitiveShape` entity into a render object with a valid draw, world
matrix and bounds. What is left for a *mesh asset* is the load — the resolution is done and the open
question is what the extraction does while an asynchronous load is in flight — and a material asset
resolved to a `Material`, which is P5's blocker too.

⚠ **What was genuinely blocked was only the last phase, and it is now unblocked.**
[P7](#p7--handoff-10-em) bakes a block-out into an asset and points an entity at it, and *that* needed a
component holding an `AssetId`: `MeshRenderable.Mesh` is one.
Everything before it stores the geometry in the scene file, which is where a block-out belongs
anyway: it is level data, not a shared asset, and a designer who has to save six meshes to disk to
try a corridor has been given the DCC round-trip back under a different name.

### B4. Picking answers "which entity", and half the tools ask "which face" ✅

**Built, and the prediction held: it was the ray test with a different payload.** The argument is kept
because it is what the implementation answers.

Both answers exist as far as entities go — `ScenePicker`'s ray test, and `PickingRenderer`'s id
buffer, which nothing drives. Sub-object selection needs a third question with a different shape:
which face, edge or vertex of *this* mesh, within a screen-space tolerance, with the innermost
element winning, and with hover feedback fast enough to survive a mouse move.

That is the ray test with a different payload, not a new subsystem, and it is a ray test against one
mesh rather than against the scene. [P2](#p2--selection-10-em-) does it on the CPU for that reason and
says what would move it to the id buffer later.

| Piece | Where |
|---|---|
| `SubObjectPicker` — the query, and the innermost-wins rule over it | [SubObjectPicker.cs](../../Editor/Vixen.Editor.SceneView/SubObjectPicker.cs) |
| `MeshElements` — a mesh's shared positions, unique edges and triangles, derived from what is drawn | [MeshElements.cs](../../Editor/Vixen.Editor.SceneView/MeshElements.cs) |
| `ISubObjectPicker` on `ScenePicker`, with one element table per shape kind | [ScenePicker.cs](../../Editor/Vixen.Editor.SceneView/ScenePicker.cs) |
| `SceneViewport.SubObjects` and `PickSubObject` — the question from the pane the gesture will come from | [SceneViewport.cs](../../Editor/Vixen.Editor.SceneView/SceneViewport.cs) |

⚠ **The half that was not in the description is that a drawing vertex is not a vertex.** `MeshData`
splits a corner wherever a normal or a texture coordinate had to be, so a cube's eight corners are
twenty-four entries and its twelve edges do not exist in it at all. What a pointer names is
[D2](#d2-the-mesh-is-faces-over-shared-positions-and-the-two-graphs-are-different)'s *position* graph,
which is why `MeshElements` welds — and why the welding is by a tolerance rather than by equality: a
sphere's seam is `cos 0` against `cos 2π` and differs in the last bits, so exact welding leaves a line
of doubled positions down every curved primitive.

⚠ **A face is a triangle until [P1](#p1--the-mesh-15-em), and the artefact is visible rather than
theoretical.** The diagonal a triangulation puts across a cube's side is a real, selectable edge
through the middle of a wall, and a test asserts it rather than working around it. Face groups are
what remove it, which is the argument [D2](#d2-the-mesh-is-faces-over-shared-positions-and-the-two-graphs-are-different)
already makes for having them in a polygon kernel.

⚠ **Nothing is occluded, and it is stated rather than approximated.** The vertex on the far side of a
cube is as selectable as the one facing you; where the two project to the same pixel the nearer wins.
Fixing it properly is `PickingRenderer`'s id buffer with an element id in it rather than an entity id
— the move this section always said it defers — and every cheap approximation in between is a depth
bias that is wrong at a silhouette.

**What is left for [P2](#p2--selection-10-em-)** is the half this was never going to answer: the
gestures and the drawing. Hover and selection highlight through `SceneLines` and `MeshRenderer`'s
overlays, loops and rings, grow and shrink, select-by-group and coplanar, and the marquee. The
question is answerable; nothing asks it yet.

### B5. Snapping is declared and half-implemented ✅

**Closed, and the prediction was right about the cause and half right about the symptom.** The
argument is kept because it is what the work answers.

`SnapSettings.SnapToVertex` and `VertexRadius` are in the model and honoured by nothing; the SceneView
README says so, and [20 § E2](20-editor-parity.md#e2--the-viewport-20-em) owes them. The reason
given — "it needs the mesh under the pointer" — stops being true the moment there *is* an editable
mesh under the pointer with an indexed vertex list. Blockout supplies the thing that unblocks the
feature it needs.

⚠ **By the time this was picked up, "honoured by nothing" had become "reachable from nothing", which
is the same bug wearing different clothes.** `SceneProbe.TryNearestVertex` and
`SceneViewport.SnapPoint` had been built, so a drag *did* land on a vertex — but no command anywhere
turned `SnapToVertex` on. `scene.toggle-snap` moves the increment, the angle and the scale together
and says nothing about the elements that need geometry, so the feature was complete, tested and
unreachable. Finding that is the argument for the doc's own habit of writing down what a row means
rather than only whether it is ticked.

**What was built** is [D4](#d4-snapping-is-one-service-above-the-gizmo) whole, because half of it is
what makes the other half worth having:

| Piece | Where |
|---|---|
| `SnapContext` — `SnapSettings` grown into a service: `SnapElements`, `SnapBase`, `SnapModifiers` | [SnapContext.cs](../../Editor/Vixen.Editor.SceneView/SnapContext.cs) |
| `ISceneProbe.TrySnap` — one query, over `MeshElements`, with the precedence in it | [SceneProbe.cs](../../Editor/Vixen.Editor.SceneView/SceneProbe.cs) |
| `TransformGizmo.SnapOrigin` and the align-on-landing | [TransformGizmo.cs](../../Editor/Vixen.Editor.SceneView/TransformGizmo.cs) |
| One context per editor, handed to every pane's gizmo and every pane's placement | [EditorApplication.cs](../../Editor/Vixen.Editor.App/EditorApplication.cs) |
| Eleven commands and the Snap dropdown beside the toggle | [ViewportCommands.cs](../../Editor/Vixen.Editor.App/ViewportCommands.cs) |

⚠ **The four booleans are views over the element set rather than second state.** D4 promised "nothing
that reads it today changes", and the way to keep that promise without two writers for one fact is
that `SnapPosition`, `AbsoluteGrid`, `SnapToVertex` and `SnapToSurface` get and set bits of
`Elements`. A toolbar toggle and a settings panel cannot disagree about whether snapping is on
because there is nothing for them to disagree about.

⚠ **Edge and edge-centre came free from [B4](#b4-picking-answers-which-entity-and-half-the-tools-ask-which-face-),
and could not have been written without it.** They are elements of the *position* graph — a cube has
twelve edges and `MeshData` has none — so the welded `MeshElements` the sub-object picker needed is
the same table a snap lands on. That is B5's own sentence about "an indexed vertex list" coming true
one phase earlier than it expected.

⚠ **The base is the half that was missing and the half D4 says matters.** A snap used to move the
gizmo's origin onto the point; `SnapBase.Pointer` moves *the corner you grabbed* onto it, which costs
nothing because a drag already records where the ray met the handle when it began.

**What is still P0's** is the rest of that phase: `WorkPlane`, numeric entry, measure,
dimensions-during-drag and the reference volumes. `Shift+Tab` for the snap popover is a binding rather
than a mechanism and goes with them.

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

**Built — see [B5](#b5-snapping-is-declared-and-half-implemented-).** The argument below is what it
answers, and the one thing it did not predict is that the base would be free: a drag already records
where the ray met the handle when it began, so `SnapBase.Pointer` is that number read rather than a
number to start keeping.

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

**Built — see [P0](#p0--the-seam-10-em-).** The argument below is what it answers. The one thing it
did not anticipate is that "all of that stays" and "one number" pull in opposite directions: the
adaptive sequence had to stay as the *default* and the chosen step had to override it, because a grid
that only adapted can never be the number a level is blocked out at.

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
| Marquee | drag on empty | Shares [20 § E2](20-editor-parity.md#e2--the-viewport-20-em)'s region resolve |
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
| **Align and distribute** | `⋯` | Across a multi-selection, per axis, by min/centre/max. [20 § Part D](20-editor-parity.md#transform) already owes this |
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

Effort in engineer-months, on [14](14-roadmap.md)'s scale and benchmarked the same way. **Total
11.0 EM**, which is comparable to the whole of [20 § Part E](20-editor-parity.md#part-e--milestones),
and that comparison is the honest framing rather than a reason not to start.

The ordering is the important part. **P0–P4 is 7.0 EM and is where the value is**; each of P5, P6 and
P7 is separable, and stopping after any phase leaves a tool somebody uses rather than a branch
somebody abandons. The cut line is drawn at each phase below.

### P0 — The seam (1.0 EM) ✅

`IEditorMode` and the mode bar ([20 § A1](20-editor-parity.md#a1--the-application-frame)),
shipped with two modes — Select, and a Blockout mode that so far only owns its keys — ✅
[built](#b2-there-is-no-ieditormode-and-blockout-is-the-second-mode-). `SnapContext`
([D4](#d4-snapping-is-one-service-above-the-gizmo)) with element, base and modifiers, honouring
`SnapToVertex` and `SnapToSurface` against the primitives that already exist — ✅
[built](#b5-snapping-is-declared-and-half-implemented-), with edge and edge-centre besides. `WorkPlane`
([D5](#d5-the-grid-is-a-plane-with-a-transform)) with `SceneGrid` drawing it, set-to-face, offset,
and step doubling — ✅. **Numeric entry during any gizmo drag** — ✅. Measure,
dimensions-during-drag, and reference volumes — ✅.

| Piece | Where |
|---|---|
| `WorkPlane` — an origin, a rotation and a step; `SceneGrid` lays its lines out in the plane's basis | [WorkPlane.cs](../../Editor/Vixen.Editor.SceneView/WorkPlane.cs) |
| `NumericEntry` + `TransformGizmo.Typed` — `G X 5 ⏎`, axis letters, `Tab`, `-`, backspace, `Esc` | [NumericEntry.cs](../../Editor/Vixen.Editor.SceneView/NumericEntry.cs) |
| `TransformGizmo.Dragged` and the mid-pane readout — metres, degrees or a factor, while the drag runs | [ViewportChrome.cs](../../Editor/Vixen.Editor.App/ViewportChrome.cs) |
| `SceneMeasure` — two points a distance, three an angle, snapped like everything else | [SceneMeasure.cs](../../Editor/Vixen.Editor.SceneView/SceneMeasure.cs) |
| `ReferenceVolumes` — a person, a door, a corridor and a car, drawn and not shipped | [ReferenceVolumes.cs](../../Editor/Vixen.Editor.SceneView/ReferenceVolumes.cs) |
| Eleven more commands, two dropdowns and three Scene submenus | [ViewportCommands.cs](../../Editor/Vixen.Editor.App/ViewportCommands.cs) |

⚠ **The adaptive spacing stays and the chosen step overrides it, which is what "all of that stays"
had to mean.** `SceneGrid`'s 1-2-5 sequence is a legibility device and is right until somebody says
otherwise; `]` and `[` are them saying otherwise, and from that moment the grid draws the number they
chose however far away the camera is. Both halves are needed: a grid that only adapted could never be
the number a level is blocked out at, and one that only obeyed would be a grey haze from two hundred
metres up.

⚠ **`SnapContext.GridStep` reads the plane rather than being pushed at.** That is D5's last sentence
made structural: there is one number, asked for on demand, and the second one that could disagree with
it does not exist.

⚠ **Numeric entry cost what D5 predicted, and the prediction is worth recording as a win.** "Costs
almost nothing given the gizmo's recompute-from-mouse-down design" — every frame of a drag was already
the pose at the grab plus a magnitude, so typing substitutes the magnitude and the same arithmetic
runs. What it did need was a rule the doc does not state: a typed number beats a snap, because it is
the most specific thing anybody has said about where the object lands.

**Exit:** a designer can place, drag and rotate the *existing* primitives with vertex and surface
snapping, on a grid they moved onto a wall, typing exact distances, and read the result in metres —
with no editable mesh in the engine yet. **Met.**

**If you stop here** you have shipped most of what [20 § E2](20-editor-parity.md#e2--the-viewport-20-em)
owes for transforms, plus a precision story neither reference editor has. That is a real release.

### P1 — The mesh (1.5 EM) ✅

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

| Piece | Where |
|---|---|
| `EditMesh` — faces over shared positions, an edge table that reports, face groups, corner layers | [EditMesh.cs](../../Core/Vixen.Geometry/EditMesh.cs) |
| `MeshReport` — manifoldness, boundary, winding, slivers and orphans, as facts rather than a verdict | [MeshReport.cs](../../Core/Vixen.Geometry/MeshReport.cs) |
| `EditMeshes` — the copies the kernel deliberately does not make: into `MeshData`, and into the file | [EditMeshes.cs](../../Editor/Vixen.Editor.SceneView/EditMeshes.cs) |
| `SceneDocument.MeshOf` / `SetMesh`, and a `mesh:` key of its own in `.vxscene` | [SceneDocument.cs](../../Editor/Vixen.Editor.SceneView/SceneDocument.cs) |
| `EditMeshCommand` — one type, both granularities | [EditMeshCommand.cs](../../Editor/Vixen.Editor.SceneView/EditMeshCommand.cs) |

⚠ **The round-trip warning above was right about the risk and wrong about the work.** The format
already writes a `Vector3` at round-trip precision, so a mesh made of them inherits the answer — what
the phase actually had to decide was the *shape* of the record, and flat lists won: a face written as
its own mapping would triple the line count of every mesh and make a one-vertex move a diff across the
whole block. Corner counts rather than start offsets, so a file that has lost a line is a short mesh
rather than one whose last face reads off the end.

⚠ **An absolute epsilon for "this face has no area" is wrong and the capsule found it.** Newell's sum
is twice a face's area, so a fixed tolerance is a statement about how big a face has to be to count —
and the triangles round a hemisphere's pole are genuinely small. Sixty-four of them were declared
degenerate; a primitive built at a tenth of the scale would have had all of them declared so. The test
is relative to the mesh's bounds now, which is the same lesson the weld tolerance already taught.

⚠ **Groups are what make a cube's side one wall, and they arrive with the mesh rather than later.**
`FromTriangles` groups by coplanar connected component, so the diagonal a triangulation puts across a
side is still a real edge and the two halves are still two faces — but they are one group, which is
what an extrude will act on.

**Exit:** a cube in a scene is an `EditMesh`; it saves, reloads and re-saves to identical bytes;
moving one vertex is undoable; nothing looks different on screen. **Met** — and the last clause is met
by the entity keeping its `PrimitiveShape`, which is what draws. Drawing *from* an edited mesh is
[B1](#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-)'s own noted follow-up — "a
block-out mesh is one shape per *entity* rather than one per kind" — and it is what
[P2](#p2--selection-10-em-) needs before a moved vertex is visible.

### P2 — Selection (1.0 EM) ✅

Vertex/edge/face modes, hover and selection highlight drawn through `SceneLines` and `MeshRenderer`'s
overlay pipelines (both exist), ~~sub-object ray picking with innermost-wins and screen-space
tolerance~~ — ✅ [built](#b4-picking-answers-which-entity-and-half-the-tools-ask-which-face-), so what
is left here is the gestures and the drawing over it — loops and rings, grow and
shrink, select-by-group / by-material / coplanar, and the marquee — which is
[20 § E2](20-editor-parity.md#e2--the-viewport-20-em)'s region resolve and should be built once,
there, for both.

| Piece | Where |
|---|---|
| `MeshTopology` — loops, rings, coplanar regions, groups, shells and boundary loops, as walks over the edge table | [MeshTopology.cs](../../Core/Vixen.Geometry/MeshTopology.cs) |
| `MeshSelection` — one set and a kind, with the conversions between the three and grow / shrink / invert | [MeshSelection.cs](../../Core/Vixen.Geometry/MeshSelection.cs) |
| `MeshEdit` — which mesh, which mode, what is selected, what is hovered; one per editor | [MeshEdit.cs](../../Editor/Vixen.Editor.SceneView/MeshEdit.cs) |
| `MeshGizmoTarget` — the selection as one thing the transform gizmo drags | [MeshGizmoTarget.cs](../../Editor/Vixen.Editor.SceneView/MeshGizmoTarget.cs) |
| `SubObjectPicker.Within` and `SceneViewport.EndSelect` — E2's one band, answering two questions | [SubObjectPicker.cs](../../Editor/Vixen.Editor.SceneView/SubObjectPicker.cs) |
| `SceneLines.Elements` — the cage, the vertex handles and the filled face highlight | [SceneLines.cs](../../Editor/Vixen.Editor.SceneView/SceneLines.cs) |
| `SceneShape.Of(entity, version)` — B1's follow-up: one shape per *entity*, so a moved vertex moves on screen | [SceneMeshes.cs](../../Editor/Vixen.Editor.SceneView/SceneMeshes.cs) |
| `BlockoutSelection` and ten commands — loop, ring, grow, shrink, group, coplanar, linked, all, none, invert | [BlockoutSelection.cs](../../Editor/Vixen.Editor.Blockout/BlockoutSelection.cs) |

⚠ **The drawing half was the phase's real work and B1's follow-up is where it lived.** P1 met its exit
by leaving the entity drawing its `PrimitiveShape`; an element mode that could not show a moved vertex
would have been a mode nobody could use. A `SceneShape` now carries an entity and a revision, so an
edited mesh is one upload of its own — and the presenter retires the previous revision after the
renderer's ring depth rather than idling the device, because idling per frame of a drag is exactly the
four-frames-a-second tool B1 called a blocker.

⚠ **The demotion moved from "the first edit" to "entering the mode", and D6 is still satisfied.** A
designer who presses `3` and sees nothing change concludes the mode is broken. The door is one-way
because it throws away *live parameters* and a `PrimitiveShape` has none — a kind and a material, both
of which survive — so the confirmation D6 asks for arrives with [P4](#p4--creation-15-em), which is
what creates the parameters it protects. **That is P4's to build and it is owed.**

⚠ **A position change keeps the selection and a topology change drops it, and the test is the table
sizes.** The distinction is P2's exit criterion and it needs nothing from the document beyond a
version per entity: same sizes means the mesh was dragged and every index still names what the
designer chose; different sizes means the numbering moved underneath it.

⚠ **The band is E2's region resolve, asked a second question rather than written a second time.** One
`Marquee`, one projection, and the element mode decides whether the answer is entities or elements —
and an element is taken when it is *wholly* inside, because a touch rule takes every edge leaving the
region as well.

**Exit:** every element of a mesh can be selected by every gesture in the table above, and the
selection survives an undo of an edit that did not change topology. **Met.**

### P3 — The verbs (2.0 EM) ✅

Everything in [Geometry](#geometry). Extrude first and alone until it is right — faces, edges,
vertices, region-vs-individual, along the normal and along an axis, with snapping — because every
other verb is judged against how that one feels.

⚠ **Bevel is the one that looks small and is not.** A bevel with segments on an edge that meets three
other bevelled edges at a vertex is a miniature research problem, and the honest first version bevels
edges independently and reports where it could not resolve a corner, rather than producing a
self-intersecting one silently.

| Piece | Where |
|---|---|
| `MeshOperations` — extrude, inset, bevel, loop cut, subdivide, bridge, fill, flip, weld, dissolve, delete, detach, append | [MeshOperations.cs](../../Core/Vixen.Geometry/MeshOperations.cs) |
| Ear clipping, which replaced the fan the kernel shipped with | [EditMesh.cs](../../Core/Vixen.Geometry/EditMesh.cs) |
| `BlockoutGeometry` — the same verbs against a scene: one undo entry each, and the result selected | [BlockoutGeometry.cs](../../Editor/Vixen.Editor.Blockout/BlockoutGeometry.cs) |
| Fourteen commands, the bindings the inventory names, four on the mode's strip and all of them in a menu | [BlockoutMode.cs](../../Editor/Vixen.Editor.Blockout/BlockoutMode.cs) |

⚠ **Every verb renumbers the faces and leaves the positions alone, and that is not an
optimisation.** A position index is what a selection holds, what an undo entry records and what a drag
in flight is writing to — D3 turns on one meaning the same thing from one frame to the next. So an
operation rebuilds the face table wholesale and leaves orphaned positions behind; `Compact` removes
them and hands back the map, and it is run *between* gestures because that is the only moment at which
nothing holds an index.

⚠ **A partial subdivision has to split its neighbours' edges too, and finding that out was the
phase's one real surprise.** Subdividing one face of a box left the shared edges split on one side and
whole on the other — a T-junction, which `Validate` reported as twelve boundary edges and which would
have drawn as a crack the first time anything moved. The neighbours become n-gons instead, which is
what every modelling tool does and is most of the argument for an n-gon kernel.

⚠ **The winding of a cap cannot come from the edge table.** An edge is stored low-to-high, which says
nothing about which way round a face walks it — so a boundary loop walked from the stored order gives
a fill that faces inwards about half the time. `BoundaryLoop` orients itself from the rim's own face
now, and `Dissolve` looks for its shared edge in either direction for the same reason.

**Exit:** a room with a doorway, a window and a chamfered edge is built in the viewport, from a cube,
without leaving it; every operation is undoable and every one round-trips through the scene file.
**Met** — and it is a test rather than a claim: `A_room_with_a_doorway_and_a_chamfered_edge_is_built_from_a_cube`
hollows a box, insets and pushes a doorway and a window through two of its walls, chamfers an edge, and
asserts the result round-trips through `SceneMeshData` unchanged.

⚠ **What is not here: the knife, and it is the one row of the table left undone.** A free cut across
faces snapping to edges and midpoints is an interactive tool with a path, a preview and its own
modality — the kernel primitive it needs is "split this face between these two points", and the
gesture around it is nearer to [P6](#p6--csg-20-em)'s plane cut than to the rest of this phase. It is
owed and it is called out rather than quietly dropped.

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
[14](14-roadmap.md) Phase 7 — B1 took the device-resident geometry out of that dependency and left
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

Bake to a `.vxmesh` asset through the existing importer machinery, with the entity pointed at it.
This was the one part genuinely blocked on a runtime component carrying an `AssetId`; that component
exists now — `MeshRenderable.Mesh` — so what is left here is the bake itself
([B3](#b3-nothing-at-run-time-can-hold-a-mesh-)). Collision generation into `ShapeDescription`. OBJ and
glTF export. Import-back-as-editable.

**Exit:** a block-out becomes an asset, an artist opens it in a DCC, replaces it, and the level does
not change shape.

### Cost

| Phase | EM | Blocked on |
|---|---|---|
| P0 — The seam ✅ | 1.0 | — |
| P1 — The mesh ✅ | 1.5 | — |
| ~~P2 — Selection~~ | 1.0 | ✅ Done. The drawing arrived with it, which is B1's own follow-up; the marquee is [E2](20-editor-parity.md#e2--the-viewport-20-em)'s one band answering a second question |
| ~~P3 — The verbs~~ | 2.0 | ✅ Done, less the knife — see [P3](#p3--the-verbs-20-em-) |
| P4 — Creation | 1.5 | P1, P3 |
| P5 — Surfaces | 1.0 | 🔴 the material system in the editor viewport |
| P6 — CSG | 2.0 | P1 |
| P7 — Handoff | 1.0 | mesh *drawing* — the extraction system over `GeometryBuffer` |
| | **11.0** | |

**And one cost that was not in the table and has been paid.**
[B1](#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-) — drawing meshes from
device-resident buffers instead of a per-frame CPU gather — was the precondition this document could not
ship past about P3 without. It is built, ahead of the rest of [14](14-roadmap.md) Phase 7 rather
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
| **Golden screenshots** | Only for what is *drawn*: selection highlight, hover, the work plane, the cube grid's preview, the checker material. The suite [20 § Part F](20-editor-parity.md#part-f--testing) already gates |

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
| **Two selection models in one viewport** | Closed as far as the seam goes. Entity selection and sub-object selection are genuinely different and the mode is what keeps them apart, which is why [P0](#p0--the-seam-10-em) built `IEditorMode` before anything selects a face — and the mode's context is now what arbitrates the keys as well |
| **Undo memory** ([D3](#d3-every-edit-is-a-command-and-a-topology-change-stores-the-whole-mesh)) | Bounded and stated. A byte budget replaces the entry count if it is ever hit |
| **A designer builds a level out of blockout meshes and it ships** | ⚠ This *will* happen and it is not a failure — it is what happened at every studio that shipped ProBuilder geometry. It is a reason P7's collision generation and asset bake are in the plan rather than a reason to prevent it |

---

## Documents this changes

| Document | Change |
|---|---|
| [20 § Part G](20-editor-parity.md#part-g--out-of-scope) | The "Mesh editing / modelling tools" row now points here, with the line redrawn rather than erased |
| [20 § A1](20-editor-parity.md#a1--the-application-frame) | `IEditorMode`'s second mode is named *and built*, so the seam has a consumer rather than a hypothesis. The mode bar is no longer owed |
| [20 § E2](20-editor-parity.md#e2--the-viewport-20-em) | Vertex snap and surface snap are built — see [B5](#b5-snapping-is-declared-and-half-implemented-) — and the marquee **was** built once: [P2](#p2--selection-10-em-) added a second question to `SceneViewport.EndSelect` rather than a second band |
| [02](02-repository-layout.md) | Two assemblies: `Core/Vixen.Geometry` and `Editor/Vixen.Editor.Blockout`, each with its tests. Both are built ([B2](#b2-there-is-no-ieditormode-and-blockout-is-the-second-mode-), [P1](#p1--the-mesh-15-em-)) |
| [11 § `Vixen.Editor.SceneView`](11-editor.md) | The "not in" list's vertex snapping and rubber-band selection are closed by [P0](#p0--the-seam-10-em) and [P2](#p2--selection-10-em-), and the assembly gained a third question beside "which entity" — see [B4](#b4-picking-answers-which-entity-and-half-the-tools-ask-which-face-) |
| [14](14-roadmap.md) | Phase 7's viewport wiring gained a second dependant and split in two: the device-resident geometry is built ([B1](#b1-every-mesh-in-the-viewport-went-through-the-cpu-every-frame-)), and the material half is what [P5](#p5--surfaces-10-em) and the picking stage still wait on |

Licensed under Apache-2.0.
