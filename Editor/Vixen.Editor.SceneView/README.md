# Vixen.Editor.SceneView

The viewport: what the camera is doing, what the handles do when you drag them, what is under the
pointer, and what happens when you press play.

Spec: [docs/plan/11](../../docs/plan/11-editor.md) § "`Vixen.Editor.SceneView`",
[docs/plan/17](../../docs/plan/17-app-heads-and-shipping.md) § play topologies.

```csharp
var layout = new ViewportLayout(panel, selection) { Arrangement = ViewportArrangement.Quad };
layout.TargetsFactory = () => EntityGizmoTarget.For(world, selection);

foreach (var pane in layout.Panes) {
    pane.Gizmo.Mode = GizmoMode.Translate;
    pane.Gizmo.Snap.SnapPosition = true;
    pane.Picking = new PickingBuffer(device);
}

var views = layout.Update(delta);   // once a frame, after the layout pass
```

## What joins which halves

`Viewport` (in `Vixen.Ui.Controls.Advanced`) says *where* and *how big* in render pixels and reports
the input inside it, and knows nothing about rendering. `RenderView` is what a frame is drawn from and
knows nothing about interfaces. Neither had to know about the other because `SceneViewport` is where
one drives the other — the same bargain `Vixen.Ui` makes with `Vixen.Platform`.

Everything below the viewport is arithmetic over interfaces with no device in it. The gizmo moves
`IGizmoTarget`s, placement asks an `ISurfaceProbe`, the grid returns a list of lines. That is what
makes "does dragging the X arm fifteen pixels move it the right distance" a unit test rather than a
screenshot somebody looks at, and it is why this assembly's test suite needs no GPU.

## The camera is four numbers

A pivot, a distance and two angles. Every navigation a scene view has is an operation on those: orbit
turns the angles, pan moves the pivot, zoom scales the distance, focus sets the pivot and solves the
distance. Storing a position and a rotation instead makes orbit the hard one, and orbit is the one
people use most.

- **Pitch is clamped just short of vertical.** At exactly ninety degrees the forward vector is parallel
  to the world up, the basis is undefined, and the horizon spins. Every scene view has this bug once.
- **Zoom is multiplicative, and it is per *notch*.** A fixed step per notch takes forty notches to
  cross a level and then punches straight through what you were approaching. The wheel arrives in
  pixels — that conversion belongs to the backend, which knows the device and the machine's settings
  — so `SceneViewport.Notches` divides before the camera sees it, and negates: pushing the wheel away
  from you moves in.
- **Orbit is a turntable.** Sideways carries the scene and vertically carries the camera: dragging
  right spins what you are looking at to the right, dragging up climbs over the top of it.
- **Flight is orbiting from where you are.** WASDQE moves the *pivot* along the camera's basis rather
  than switching to a second camera model, so leaving fly mode does not teleport the view and the
  orbit afterwards is about something in front of you.
- **Pan and fly are scaled by the distance.** Flying across a terrain and flying around a bolt are the
  same keys and want speeds three orders of magnitude apart.
- **Focus keeps the angle.** Focus that also reset the direction is the one people undo by hand every
  time: you lined the view up and then asked to see something *in* it.
- **The orthographic height is derived from the distance**, so pressing the projection key does not
  rescale the picture.

Bookmarks are the four numbers plus the projection, and the numpad views set the two angles and
deliberately do *not* force orthographic — a key that changed two things at once is one people stop
pressing.

### Flight is the one gesture the keymap cannot hold

Hold the right button and WASDQE flies; shift is four times faster. That is one gesture over six keys
that already mean something else — W, E and R are the gizmo modes, A is frame-all — and a keymap of
chords over commands can express neither half of it: it fires once on the press, and it does not know
a mouse button is down. So `SceneViewport` reads the keys itself, **only** while the button is held,
and **consumes** them so the shell's bindings cannot fire underneath. It stays one gesture rather
than a second binding system: `FlyKeys` is the whole of it, and it is settable.

- **The keys are positions.** `InputKey` names the physical key by its US-QWERTY legend, so the block
  under the left hand is the same one on AZERTY — where `Q` is the key printed `A`.
- **The direction is normalised**, so forwards-and-sideways is not forty per cent faster than either.
- **Releasing the button drops the held keys.** Most people let go of the mouse first, so the release
  of `W` arrives when the viewport is no longer listening — and a bit left set is a camera that sets
  off by itself the next time the button goes down. Losing the focus ends it for the same reason.
- **A frame longer than `MaximumFlyStep` is clamped.** A shader compile or a window dragged between
  displays is not travel, and integrating it puts the camera somewhere nobody was going.
- Only the focused pane flies, and nothing checks for that: keys reach the focus, and a four-pane
  layout has one.

## Gizmos: recomputed, never accumulated

The gizmo records what each target held on mouse-down and applies **the whole of the drag** to that,
every frame. Three consequences, and all three are bugs in the implementation that adds each frame's
delta:

- snapping lands *on* the grid rather than near it, however slowly the drag was made;
- a drag that goes out and comes back ends exactly where it began;
- floating-point drift is impossible rather than merely small.

Hit-testing is in screen space, not by ray against solid handle geometry: an arm is a line a few pixels
wide, and a ray test against a cylinder that thin misses more often than it hits. Innermost handles are
tested first, because the plane quads overlap the arms they sit between.

### It has to be attached every frame, not on the press that grabs it

`GizmoGeometry` draws what the gizmo is attached to. A gizmo pointed at the selection only by
`BeginManipulate` therefore has no handles to draw until something has already been clicked in the
viewport — and the click that would attach them is the click that hit-tests against an empty gizmo.
A selection made in the hierarchy panel got no handles at all, which is indistinguishable from
handles that cannot be dragged. `SceneViewport.Update` attaches once a frame, and skips it mid-drag
because the targets a drag started on are the ones the rest of it has to be applied to.

The same shape of gap sat on the pointer: a move with nothing held carries `PointerButton.None`, so a
handler that checked for the primary button before doing anything never asked what was under the
cursor — no handle ever lit up, and the only way to find out whether a press would grab an arm was to
press and see what moved. Escape now cancels a drag from the viewport too, immediately rather than
through the stack, and is *not* consumed when there is no drag: it is also the key that closes a
dialog and cancels a rename.

### Thick lines are several thin ones

`LineRenderer` draws one-pixel lines and deliberately will not offer anything else — `lineWidth`
above one is an optional Vulkan feature most tiled GPUs lack, so a renderer that offered it would draw
a different picture on different machines. Its own remarks say a thick line belongs to whoever wants
one, and `GizmoGeometry` is that: every segment is emitted `TransformGizmo.Thickness` times, each
shifted a pixel further along `segment × view` — the one offset that is "across the line on screen"
in world space, so the strokes stay a fixed number of pixels apart from every angle instead of
collapsing into one line at some of them. A segment pointing straight at the camera has no such
perpendicular and falls back to the camera's own right; without that, the arm nearest the eye quietly
goes back to a hairline.

⚠ **`GrabRadius` is floored at half the thickness.** A gizmo drawn thicker than it is tested has a rim
of pixels that ignores clicks, which fails at the edges of an arm and works in the middle of it — and
reads as the tool being unreliable rather than as a number being wrong. `Tolerance` is the floored
value and is what every hit test uses.

### Two handles the hit test answered for and nothing drew

`HitTest` has always returned `Screen` for a circle outside the three rotation rings, and `Uniform`
for a square in the middle of a scale gizmo. Neither was drawn, so a click out there turned the
selection about the view axis and a click in the middle scaled everything — both discoverable only by
accident. `GizmoGeometry` now draws both, from the same numbers (`ScreenRingScale`, `CentreRadius`)
the test reads, in grey rather than an axis colour because they belong to no axis.

⚠ **A plane handle seen edge-on is not offered.** Its quad projects to a sliver lying along the third
arm and would take that arm's clicks, and dragging in a plane you are looking along the edge of is not
something anyone can aim. Every editor hides these handles; here it has to affect the *test* too,
because a handle that is hidden and still grabbable is worse than one that is neither.

**Spaces**: world, local, parent and screen. Several objects selected forces world — "local" has no
answer when the selection disagrees about which way local is, and picking the primary's would drag
nineteen objects along an axis that is not theirs. `Parent` is the one people ask for once they have a
rotated parent: dragging "along X" then changes the number in the inspector by the amount dragged.

**Pivots**: the last-clicked object's origin, or the middle of everything. A rotation is about the
gizmo's origin, so a group turns as a group and a single object spins in place.

A drag becomes one `TransformTargetsCommand` on mouse-up, holding position, rotation and scale together
per target — a rotate about a group's centre both moves and turns each object, and three commands for
one drag would be three undo steps that only make sense applied together. Escape rolls the drag back
immediately rather than through the stack, so the viewport is redrawn from the model the instant the key
is pressed.

## Picking is a render stage

Raycasting works for a box and stops working for a skinned mesh, an instanced forest, an alpha-cut leaf
and anything whose shader moved its vertices. Drawing object ids with the same vertex path as the
picture means what you click is what you see, by construction, for every kind of geometry there will
ever be.

- **`R32_UInt`, not a colour format.** An id packed into eight-bit channels runs out and is subject to
  whatever the swapchain's colour space does to it. Zero means nothing, so an object's id is its index
  plus one — otherwise "the sky" and "the first object" are the same answer.
- **One pixel is copied, not the target.** A full-resolution readback is sixteen megabytes a frame for
  a 4K viewport to answer a question about one pixel.
- **Read back several frames later.** Mapping a buffer the GPU may still be writing means waiting for
  the GPU, and doing that in the frame the click happened turns a click into a stall. The ring is as
  deep as the number of frames in flight, and a pick is resolved when its slot comes round again.
- **Nothing is drawn on a frame nobody clicked**, so a viewport sitting still costs nothing.

Two passes, and they have to be two: a copy cannot be recorded inside a render pass. The graph derives
the order and the barrier from the transfer pass declaring that it reads what the draw pass wrote — and
the transfer pass declares a side effect, because the readback buffer is not one of the graph's
resources and the whole chain would otherwise be culled.

The drawing half is a `RenderPassRenderer` this owns rather than a pass it declares by hand:
`CompositorFrame.Context` is internal to `Vixen.Rendering`, rightly, and composition is the seam
`SceneRenderer.BuildChild` exists for.

## View modes are compositors

Doc 06 made the compositor data precisely so that "show me the normals" is a different tree rather than
a branch inside the renderer. `ViewModes` collects them: a host registers the trees it has, and a mode
with none falls back to shaded rather than showing nothing.

Wireframe and overdraw are the exception and are here as stage state, because they are the same geometry
with a different rasterizer and a different blend. ⚠ That mutates the stage, and a stage belongs to the
render system rather than to a view — so a four-pane layout with independent render modes needs a stage
per pane. `ViewportLayout` gives each pane a whole `SceneViewport` for this reason.

## The document

`SceneDocument` is what `Vixen.Editor.Core`'s README promised would arrive here, and the reason it is
here rather than there is the reference: a scene *is* an ECS world, and `Vixen.Editor.Core` does not
reference `Vixen.Ecs` — deliberately, so the command stack and the asset database stay testable
without one.

**The editor names entities and the runtime does not.** There is no name component: a name is thirty
bytes per entity in every chunk of a shipping build, serving a panel that does not exist at run time.
The map lives on the document, which also makes renaming an ordinary undoable edit rather than a
structural change to the world.

**Creating and destroying entities are undoable, and the handle survives.** `Create` and `Delete` go
on the stack; `Add` stays for a host building a scene from a file or a template, where filling the
undo history with entries nobody made would be wrong.

Five things have to come back, and only the first was ever the hard part:

| | How |
|---|---|
| The handle | `World.TryRecreate`, which refuses if anything took the slot |
| The components | A scratch world holding a mirror, copied both ways by `CopyComponentsFrom` |
| The name | `TryGetName` before, `Assign` after — an entity never named must not come back named |
| The stable id | `TryGetId` / `Adopt`, so references in a saved file still point at it |
| Its place among its siblings | `PreviousSiblingOf` before, `Hierarchy.SetParentAfter` after |

⚠ **A delete takes the whole subtree.** A child left behind holds a `Parent` naming a dead entity,
and every walk over the hierarchy then throws.

⚠ **The components cannot be a list of boxes.** They are unconstrained structs stored by type in
chunks, so the only thing that can hold an arbitrary one without knowing what it is, is a chunk — and
the only thing that makes chunks is a world. Hence the scratch world.

⚠ **The hierarchy components are re-established, not copied back.** `Parent`, `Sibling` and `Child`
are handles into a list whose other ends the delete also rewrote — the surviving parent's `Child`
pointer in particular. Restoring the raw values would give a list that is internally consistent and
detached from the one it belongs to.

⚠ **An undo can refuse.** If something took one of the freed slots, every handle is unrecoverable and
the command throws rather than half-restoring — checked for all of them before any of them moves.
In the editor this needs a play-mode restore or a second document to reach.

Reparenting is still not undoable, but only for want of a command: `SetParentAfter` is the primitive
it was waiting on.

⚠ **Saving throws without an `ISceneWriter`.** `EditorDocument.Save` marks the document clean
afterwards, so a `SaveCore` that wrote nothing would leave it claiming to match a file that does not
exist — and the next crash would take the work with it. `SceneFileWriter` is the implementation the
editor uses.

## The file

`.vxscene` is the authoring format: YAML through the same binder a material and a settings asset go
through. A content build compiles it into a `SceneAsset`, so nothing about it is shaped for load
speed and everything is shaped for being read by a person and merged by git.

**The format itself lives in `Vixen.Editor.Core`, and this assembly is one of its two readers.** The
other is `SceneImporter`, which compiles one; a viewport and an importer should not have to reference
each other, and two bindings of one format are two things to keep in step — which is how a file comes
to mean one thing when it is saved and another when it is built. What lives here is the half that
turns a file into a live document and back.

**A component is a tagged entry in the entity's `components` list** — `- !Camera` and the keys under
it, the same polymorphism a `.meta` uses for its importer, and the same name the compiled scene and
the binary serializer use for that component. Saving reads back exactly the components
`SceneComponentRegistry` knows, in name order so an open-and-save is not a diff; a file naming a type
nothing registered is **refused on load rather than dropped**, because an entity opened without its
component is one that gets saved without it.

**An entity is named by a GUID, not by its handle.** An `Entity` is a slot and a version in one
world; loading the same scene twice reissues every one of them. `EntityId` is the identity that
survives, and it is what a reference between entities, a prefab override and a multi-user session
all have to be expressed in.

⚠ **A GUID rather than a counter, and the reason is git.** A local counter reads better in a diff and
has one unreadable failure: two branches each add an entity, each picks the next id, and the merge
takes both hunks cleanly — leaving two entities claiming one id, which no tool reports. Same trade
doc 08 already made for assets.

**Children are nested rather than each naming a parent.** Moving a subtree is one moved block instead
of *n* scattered edits. It also means a parent exists before anything that hangs from it, for free.

**A vector is one scalar**: `position: 1 2 3`, not a mapping with three keys — fifteen lines per
entity is a diff nobody can scan. Written with round-trip precision, because a scene that is opened
and saved has to produce the same bytes; a format that quietly rounded would make every scene a merge
conflict with itself.

⚠ **Children are restored in reverse.** `Hierarchy.Link` puts a new child at the *head* of the
intrusive list — O(1), which is why the list is intrusive — so creating the file's children in order
leaves the world holding them backwards, and the scene flips its sibling order on every
open-and-save. Not visibly wrong, and enough to make every scene conflict with itself. The
same-bytes round-trip test is what holds this honest if `Link` ever changes.

⚠ **A version field that is written and checked.** A file from a newer editor is refused rather than
bound as far as it goes: a scene half-read is a scene saved back with the other half gone, which is
the one failure a version field exists to prevent.

## Play mode, both topologies

**In-process** is `WorldSnapshot` plus `PlayModeController`. A snapshot is a walk over the archetypes
copying rows, which is what doc 11 means when it calls cheap world cloning a design constraint on the
ECS rather than an afterthought.

Two things that are easy to get wrong and are not:

- **The restore clears first.** Play mode's hazard is state that outlives it, and restoring on top of
  what is there would keep every entity a script spawned.
- **Every entity gets a new handle, so the selection is translated.** `Restore` returns the table
  rather than being a `void`; an untranslated selection highlights whatever landed in those slots,
  which looks like a rendering fault and is not one. An entity play mode *spawned* has no translation
  and is dropped rather than kept.

⚠ **An `Entity` copied verbatim into another world names the wrong thing.** `World.CopyComponentsFrom`
says so and leaves the fix-up to the caller. What is fixed up here is the hierarchy, because those are
the handle-valued components the engine itself declares; a game component holding an `Entity` needs its
own pass over the same table.

Doc 11 asks that a play-stop which leaks *fail* rather than degrade silently, so the tracked-object
count is compared across the session and `Leaks` is what grew. Only growth counts: a session that
disposed something the editor had before it started is a different bug.

**Out-of-process** is `PlayerSessions`. Networking is what requires it — testing a server-authoritative
game needs a server and several clients — and it doubles as the way to check release-configuration
behaviour and to isolate a game that hangs, which is why a hung player is killed rather than waited for.
Ports are assigned by the set rather than by each launch, because two clients on the inspector's default
port present as "the remote inspector does not work with more than one client".

## Not in

**Solid handles.** `GizmoGeometry` and `SceneLines` turn the gizmo, the grid and an entity marker into
line segments that `Vixen.Rendering`'s `LineRenderer` draws — so the viewport shows them now. Cones,
rings and a selection outline want a mesh path the editor does not have, and lines are what an editor
draws for a scene of empties anyway.

**Vertex snapping.** `SnapSettings.SnapToVertex` and its radius are in the model and are not honoured
yet: it needs the mesh under the pointer, which is the same readback picking does but for a position
rather than an id.

**Rubber-band selection.** The picking stage answers one pixel; a marquee wants a region, which is a
different copy and a different resolve.

**Meshes.** `Vixen.Editor.App` renders the scene into an offscreen target and hands it to the
interface, so the viewport is live. What goes in it is lines: there is no material system wired to an
editor viewport and no model importer feeding one, so a mesh pass is a second `SceneRenderer` in the
same target when there is something to put in it.

Licensed under Apache-2.0.
