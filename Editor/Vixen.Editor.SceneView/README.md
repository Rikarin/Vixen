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

var views = layout.Update();   // once a frame, after the layout pass
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
- **Zoom is multiplicative.** A fixed step per notch takes forty notches to cross a level and then
  punches straight through what you were approaching.
- **Pan and fly are scaled by the distance.** Flying across a terrain and flying around a bolt are the
  same keys and want speeds three orders of magnitude apart.
- **Focus keeps the angle.** Focus that also reset the direction is the one people undo by hand every
  time: you lined the view up and then asked to see something *in* it.
- **The orthographic height is derived from the distance**, so pressing the projection key does not
  rescale the picture.

Bookmarks are the four numbers plus the projection, and the numpad views set the two angles and
deliberately do *not* force orthographic — a key that changed two things at once is one people stop
pressing.

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

**The drawing.** The gizmo says where its handles are and what is under the pointer; something has to
put triangles there. The same is true of the grid's lines and the selection outline — this assembly
produces geometry and state, and a debug-line renderer or a compositor node consumes it.

**Vertex snapping.** `SnapSettings.SnapToVertex` and its radius are in the model and are not honoured
yet: it needs the mesh under the pointer, which is the same readback picking does but for a position
rather than an id.

**Rubber-band selection.** The picking stage answers one pixel; a marquee wants a region, which is a
different copy and a different resolve.

**Camera flight input.** `EditorCamera.Fly` is there and nothing calls it: WASD belongs to the shell's
keymap over commands, not to a second binding system inside the viewport.

Licensed under Apache-2.0.
