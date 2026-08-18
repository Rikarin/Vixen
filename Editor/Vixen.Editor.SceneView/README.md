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
    pane.OrbitAround = OrbitPivot.Selection;   // Blender's "orbit around selection"
    pane.Picker = new ScenePicker(scene);      // clicking selects; the ray test needs no device
    pane.Picking = new PickingBuffer(device);  // and this takes over when there is one
    pane.Surfaces = new SceneProbe(scene);     // what a drop, a vertex snap and a surface snap ask
    pane.Show &= ~SceneShow.Grid;              // per pane, because that is the point of four of them
    pane.Modes.Current = ViewMode.Wireframe;   // ditto
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

## ⚠ The scene was being presented upside down

`Viewport.FlipVertically` defaulted to on, and it had no business being on. Both backends resolve the
engine's +Y-up clip space where the API is — Vulkan with a negative-height viewport, OpenGL by
flipping the viewport origin — so a colour target's row zero is already the *top* of the view, and
`Conventions.md` puts the UV origin at the top-left. Sampling it as it stands is right; the flip
mirrored the whole pane about its horizon.

Almost nothing looked wrong. A grid is symmetric, a scene of markers is nearly so, and the corner
axis cross is an interface element that did not flip with it. What was noticed instead was everything
that *measures* the pane, because all of it — `TransformGizmo.HitTest`, `EditorCamera.PickingRay`,
`Viewport.Project` — measures the unmirrored image:

- the gizmo could not be clicked near the top or bottom of the pane and *could* near the middle,
  because the error is zero at the centreline and grows to the full height of the pane at the edges;
- hover lit up a handle the cursor was visibly not on — the same error, a little smaller;
- a vertical pan and a vertical orbit both went the wrong way;
- the grid and the origin lines were mirrored, which for a symmetric grid reads as "the grid is
  wrong" rather than as "the picture is upside down".

The property stays, for a host whose renderer really does hand over a bottom-up target, and it is
that host's job to say so. `ViewportTests` pins the default from one end and
`LineImageTests.AssertTheDiagonalFades` from the other.

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
- **Orbit is a turntable, on both axes.** The scene follows the pointer: dragging right spins what you
  are looking at to the right, and dragging up tips its top towards you and puts the eye beneath it.
  ⚠ The vertical used to carry the *camera* instead, so one diagonal drag turned the scene one way and
  the eye the other — and it survived review because the pane was being presented mirrored and the two
  wrongs cancelled on screen. Unity and Unreal orbit the other way on both axes; people who come from
  them want the whole gesture reversed, which is `InvertOrbitY` plus a negated horizontal, not a
  different rule.
- **`OrbitAround` turns the whole rig about a point that is not the pivot.** The camera can only orbit
  its own pivot, so orbiting the selection means reading the pivot's offset in the camera's basis
  before the turn and rebuilding it in the basis after — ⚠ the basis the turn *produced*, not the one
  it was asked for, or a drag held at the pitch limit slides the pivot a little further every frame.
- **`ZoomTowards` keeps a point still while the distance changes.** Blender's "zoom to mouse
  position", and the reason it is worth having is that approaching anything off-centre is otherwise
  zoom, pan, zoom, pan. ⚠ The pivot moves by the factor the distance *actually* changed by, which is
  not the one asked for once `MinimumDistance` has clamped it.
- **Flight is orbiting from where you are.** WASDQE moves the *pivot* along the camera's basis rather
  than switching to a second camera model, so leaving fly mode does not teleport the view and the
  orbit afterwards is about something in front of you.
- **Pan and fly are scaled by the distance.** Flying across a terrain and flying around a bolt are the
  same keys and want speeds three orders of magnitude apart.
- **Focus keeps the angle.** Focus that also reset the direction is the one people undo by hand every
  time: you lined the view up and then asked to see something *in* it.
- **The orthographic height is derived from the distance**, so pressing the projection key does not
  rescale the picture.
- **`TryProject` is `Project` with the lie taken out.** A perspective divide by a negative `w` answers
  for points behind the eye with a real pixel position, mirrored through the middle of the pane, and
  nothing downstream can tell it from a real one — which is how a gizmo behind the camera answers
  clicks in empty space. Orthographic is affine and needs none of it.

Bookmarks are the four numbers plus the projection, and the numpad views set the two angles and
deliberately do *not* force orthographic — a key that changed two things at once is one people stop
pressing.

**Nine numbered bookmark slots, not a list.** `Ctrl+1..9` saves and `1..9` recalls, which is the pair
both reference editors ship and which people arrive already knowing. A slot overwrites without asking
— the gesture is "put the view I am in on key three", and a prompt would make it two gestures — and a
recall of an empty slot is *disabled* rather than a no-op, because a key that does nothing and a key
that does nothing *yet* look identical while you are pressing it. An unbounded list of named views is
a different feature and belongs in the palette; the number row has nine keys.

### The gestures

Blender's middle-button set, plus Maya's Alt chords, plus the right button that flies. The two sets do
not collide because Blender's use no modifier where Maya's use Alt.

| Gesture | Does |
| --- | --- |
| Middle drag | Orbit |
| Shift + middle | Pan |
| Ctrl + middle | Dolly |
| Alt + left / middle / right | Orbit / pan / dolly — Maya's spelling of the same three |
| Left drag | Drive the gizmo, or start a selection |
| Right drag | Orbit, and hold it for WASDQE flight |
| Wheel | Zoom, at the pointer if `ZoomToCursor` |
| Numpad 1/3/7, 9 | Front, right, top; back |
| Numpad 2/4/6/8 | Orbit fifteen degrees |
| Numpad 5 | Toggle orthographic |

⚠ The middle button used to pan whatever was held with it — two branches written for one answer — so
there was no orbit on it at all, and the only orbit was on the right button, which is also the one
that captures WASDQE. Somebody arriving from Blender found that the button which turns the view slides
it, and that trying the other one started a flight.

⚠ **The keyboard orbits in degrees, through `Turn`.** A keyboard orbit expressed as a pointer delta
moves when the orbit speed is tuned and reverses when somebody sets "invert orbit Y" — and a key that
says "turn left" has no business being affected by a preference about the mouse. `ViewportLayout`'s
perspective preset was expressed that way and was the casualty of exactly that: it asked for a drag up
and to the left, and came up underneath the grid looking at the sky.

**Orbit around selection** is Blender's preference of the same name, and it is a preference because
both answers are right for different work: the view's own pivot keeps whatever is in the middle of the
pane in the middle of it, and the selection is what you want the moment you are working on something
that is not. The anchor is read from the *gizmo* rather than from the selection directly, so it
honours `TransformGizmo.Pivot` and a multiple selection swings around whatever the handles are sitting
on. Nothing selected has no anchor and falls back to the view, because a preference that stopped the
view turning until something was clicked would read as the middle button having broken.

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

- a snapped drag moves by a whole number of steps however slowly it was made, rather than by a
  rounding error per frame that adds up to somewhere between two of them;
- a drag that goes out and comes back ends exactly where it began;
- floating-point drift is impossible rather than merely small.

Hit-testing is in screen space, not by ray against solid handle geometry: an arm is a line a few pixels
wide, and a ray test against a cylinder that thin misses more often than it hits. Innermost handles are
tested first, because the plane quads overlap the arms they sit between.

### The target owns the undo entry

`EndManipulate` builds a `GizmoDrag` and asks the first target what it was; the target answers with a
`GizmoEdit` — the command, and the history it belongs on. The viewport executes and seals, so nothing
can forget to.

⚠ **It used to be three branches: a `Records` hook a host could set, a type test for a mesh element
drag, and the entity case underneath both.** Each was defensible and together they meant the viewport
held a list of the exceptions to its own rule, so a fourth kind of target would have been a fourth
branch. A target knows what document it came out of and what its edit is called; the viewport does
not, and asking it to was the mistake. An entity records a `TransformTargetsCommand` on the
viewport's document; a mesh selection records the positions it moved, because the entity did not move
and its corners did; a proxy shape records on the shape set's own file, because undoing it must not
depend on which tab has focus.

### What "it does not work from all angles" was

Four separate faults, all of which read to a user as the same one — the gizmo answering somewhere
other than where it is drawn.

- **The first arm within tolerance won, not the nearest.** The arms cross on screen from most angles,
  and at every crossing the answer was whichever the loop reached first, which was X, always.
- **Nothing owned the middle.** The three arms all pass through the origin, so a click anywhere near
  it started an X drag — and translate offered no middle handle at all, so there was nothing else it
  *could* answer. There is one now: a ball that drags in the view plane, which is how anything gets
  moved that is not along an axis. Scale's has always been there and was never drawn. `HitTest` takes
  that circle before it looks at an arm at all, so the middle belongs to it whatever is drawn across
  it — which is why `ArmStart` can now be zero and the arms drawn right through the ball without a
  click changing hands.
- **An arm pointing at the eye was still offered.** It projects to a dot, so every pixel of it is
  within the grab radius of every other and it wins the middle of the gizmo — and then drags along a
  line that has no direction on screen, which moves the selection by whatever the ray's numerical
  error happens to be. `IsAxisVisible` is one dot product and is what both the hit test and the
  geometry ask, so a handle that is hidden is not grabbable and vice versa.
- **A gizmo behind the camera had grabbable arms.** See `TryProject` above.

Rotation rings are cut to the half facing the camera, in the picture and in the test. Three full
circles about one point cross each other twelve times, and at every crossing the front of one ring is
drawn over the back of another — so aiming at the green ring where the red one passes behind it was a
coin toss, and the space inside the gizmo that looks empty was criss-crossed by the far sides of all
three. ⚠ The run is *broken* rather than the point skipped: joining the last point before the horizon
to the first one after it draws a chord across the gizmo belonging to no handle.

⚠ **An edge-on ring is dragged along the screen, not in its own plane.** The ray and the plane are
nearly parallel, so the crossing point is hundreds of units away and moves by tens of them per pixel;
exactly parallel there is no crossing at all, and `OnPlane` answered with the translate drag's start
point — a field a rotation never wrote. That is a gizmo that spins wildly and then jumps, in the most
ordinary pose there is: a horizontal camera, turning something about Y. `Begin` notices it once, at
the grab, and the rest of that drag is arc length over radius. ⚠ The tangent is taken at the point of
the ring *nearest the eye* rather than at the point grabbed: at the two ends of an edge-on ring's
silhouette, turning moves it directly towards or away from the viewer, so no pointer motion there
means anything. It is also the physical answer — a hoop seen edge-on is turned by pushing the part
closest to you sideways.

### The heads are solid and the shafts are not

An arm is a line and its head is a shape, and the head used to be a wire outline too: four ribs from
the tip to a square around the shaft, and the square. From the one angle it was built for that reads
as an arrow; from every other it is four unrelated lines crossing near the end of a segment. It is
also the part of a gizmo people aim at — the head is the target and the shaft only says which way —
so it was exactly the wrong part to draw as a hint.

`GizmoGeometry.BuildSolid` is the second half: a cone on a translate arm, a cube on a scale one, a
ball in the middle either way, all from `MeshPrimitives` and all placed by a matrix. The geometry is
cached because it never changes;
what changes every frame is the matrix, because the gizmo is a constant size on screen and so its head
is a different size in world units at every distance. The cone's normals are the fiddly part — its tip
is a *row* of vertices with different normals, or it is lit as though a spotlight were on one side of
it — and that is solved and tested in `MeshPrimitives` rather than here.

- ⚠ **Drawn exactly when the arm is grabbable**, from the same `IsAxisVisible` call the shaft and the
  hit test ask. A head left behind on an arm that is a dot is a solid lump over the middle of the
  gizmo, hiding the handle that does answer there.
- ⚠ **Centred on its own middle, not on the tip.** Both primitives straddle their local origin, so a
  head placed at the end of the arm buries half of itself in the shaft and leaves the arm looking half
  a head short.
- ⚠ **Indices are offset by where the head's vertices started.** `MeshRenderer` deliberately does not
  do it — a caller building a frame knows where each mesh began and it does not — and an unoffset
  index names another head's vertex, which draws a triangle stretched between two arms.
- ⚠ **The frame is right-handed on purpose.** Either handedness produces a box the shape fits into,
  and the wrong one is a mirror: every triangle wound backwards. Nothing would show it today, because
  the pipeline is two-sided and the normals go through the inverse transpose and come out right
  regardless — which is precisely why it would be left in place to be discovered by whatever turns
  culling on next.

**The handles are an overlay, in both kinds.** `LineRenderer` has had a second pipeline differing only
in the depth test from the start, for exactly this; `MeshRenderer` did not, and now does. The need is
sharper for the solid half: a wire head behind a cube still shows a few pixels through it, and a solid
one is simply gone. `SceneLines` keeps three lists — the world's segments, the gizmo's segments, and
the gizmo's triangles — and `ScenePresenter` gives each its own renderer, because one renderer holds
one buffer and draws all of it with one pipeline.

⚠ **Which pass runs first is the whole of what covers what, since none of them tests depth.** Shafts,
then heads, so an opaque cone covers the end of the line running into it — and the ball in the middle
is appended last of all, so it covers the inner end of all three shafts. The arms are built from the
origin outwards and this is what hides that: the geometry runs through the ball and the picture does
not, which means an arm can neither leave a gap at the middle nor overshoot it, whatever the camera
angle. That is what the old `ArmStart` had to be tuned for and no longer is.

**Snapping is two different things and only one of them puts objects on the grid.** By default — and
this is what Blender, Unity and Unreal all do — a drag moves by a whole number of steps, so something
at 0.3 dragged one step lands at 1.3. `SnapContext.AbsoluteGrid` rounds the resulting *position*
instead, so everything dragged ends up on the same lattice however it started. The doc comment used to
claim the second and the code did the first.

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

⚠ **The strokes of an arm are shaded across it, which is what stops six flat lines reading as six
flat lines.** They already sit across the segment *on screen*, so which stroke a pixel is on is where
round a cylinder it would be: the middle one faces the eye, the outermost two face along the offset,
and everything between is the arc joining them. `GizmoGeometry.Shade` of that normal turns a ribbon
into a lit shaft, for one dot product per stroke and no extra geometry — and it is the same lighting
term the head on the end of the arm gets from `Mesh.rvn`, which is what makes the two read as one
object rather than as a solid cone stuck on a flat stick. The *colour* is shaded and the position is
not: a real cylinder bulges towards the eye and this deliberately does not, so an arm that looks round
is still an arm that is grabbed where it is drawn.

⚠ **A shaft stops at the middle of its own head rather than at the end of the arm.** The head is
opaque, convex and drawn after the lines, so ending the shaft inside it hides the join. Running it to
the tip does not: the strokes are offset across the segment by a few pixels and a cone is only a few
pixels wide near its point, so what gets drawn is a needle sticking out of the arrowhead — three of
them, at every camera angle. `HeadDepth` is the one number both halves ask, because a shaft that
outran its head and one that stopped short of it are the two ways this goes wrong.

### A handle is lit by the viewer, not by the world

`MeshRenderer` shades with one fixed key direction and a little ambient, which is all a solid shape
needs in order to read as a solid shape. Its default points nearly straight down, and that has one
failure that matters for a gizmo: a shape whose axis runs *along* the key is lit dead flat, because
every normal on it is at right angles to the light. That is the vertical arm, from every camera angle,
on every gizmo — the arm hardest to tell from a painted line is the one that never gets a gradient.

`GizmoGeometry.KeyLight` is a direction over the viewer's left shoulder, and `ScenePresenter` sets
`handles.LightDirection` from it once a frame. A key that follows the camera cannot land along an arm
for longer than the moment that arm points at the eye — which is the moment `IsAxisVisible` stops
drawing it anyway — and down-and-left of the line of sight is the direction a reader already assumes
when judging which way a shape bulges. A gizmo is not an object in the scene; it is a control drawn on
top of one, and it has no business being lit by the scene's key.

⚠ **Both halves of a handle have to be given that direction and they are given it separately** — the
heads and the ball through the push constant, the shafts through `Pen.Light` on the CPU. The same goes
for the ambient term. A frame that set one and not the other draws a cone lit from the side its own
arm is dark on, or a ball a shade lighter than the cones beside it.

⚠ **A handle under the pointer goes darker, where it used to go pale.** Both directions are visible
and the reason to pick this one is what the other end of the range already means: every pixel here is
a colour times a shading term, so brightness is the channel that says which way a surface faces. An
arm lightened towards white has lost its shading, and beside an unhovered one it reads as a
differently lit object rather than as this one being pointed at. Darkening rides the same channel in
the direction nothing else uses — nothing on a gizmo is darker than its own ambient. It is a scale and
not a blend, so hue and saturation come through untouched; mixing towards a dark colour would drag all
three arms towards that colour, and saying which axis is the whole job of an axis colour.

⚠ **The ambient term is a quarter, below `MeshRenderer`'s own default.** That default is for a shape
somebody is looking at, where the job is legibility; a handle is a shape somebody is *aiming* at,
where the job is to be unmistakably solid, and the difference is contrast — a quarter puts four times
the range between the lit and shadowed sides of a twenty-pixel cone. It goes no lower because the
ambient term is also what keeps a handle's dark side a colour rather than a silhouette, which for the
axis pointing away from the key is most of it.

⚠ **The Lambert term is wrapped rather than clamped, in `Mesh.rvn` and in `GizmoGeometry.Shade`
identically.** A plain `max(dot, 0)` takes every surface past ninety degrees from the key to exactly
the ambient term, so the whole far side of a cone is one flat colour and the shape stops being
readable precisely where it curves most. Remapping the dot product from −1…1 to 0…1 keeps the gradient
running round the back; squaring it puts the midpoint back where the clamped term had it, which is the
difference between a rounded shape and a flatly overlit one.

⚠ **The three axis colours are saturated well past what a flat swatch wants.** They are never drawn as
themselves — every pixel of a handle is the colour multiplied by a shade that bottoms out at the
ambient quarter — so a colour picked to look right flat is a colour whose shadowed half is mud. Picking
the lit end of the ramp and letting the shading walk it down is why the dark side of a head here is
still recognisably red.

### Two handles the hit test answered for and nothing drew

`HitTest` has always returned `Screen` for a circle outside the three rotation rings, and `Uniform`
for the middle of a scale gizmo. Neither was drawn, so a click out there turned the selection about
the view axis and a click in the middle scaled everything — both discoverable only by accident.
`GizmoGeometry` now draws both, from the same numbers (`ScreenRingScale`, `CentreRadius`) the test
reads, in grey rather than an axis colour because they belong to no axis.

⚠ **The middle one is a solid ball, and it has been a flat outlined square and then a cube.** Each
shape answered the last one's complaint. The square faced the camera because the handle belongs to no
axis and a *square* on the object's own axes is one you have to orbit to see square. The cube answered
that — it reads as a cube from every angle, so it could sit on the gizmo's own basis with its faces
perpendicular to the arms — and raised its own: a cube has an orientation and three flat faces, so it
draws as three brightnesses where the round heads beside it draw a gradient, and the handle that means
*all three axes* was the one shape on the gizmo that looked stuck on from somewhere else. A sphere has
no orientation to get wrong, which is the honest picture of that handle, and it is the shape a light
gradient reads best on: every normal is on the visible half, so the shading runs its whole range
across twenty-odd pixels. That it no longer needs the basis at all is the tell that the basis was
never what the handle was about.

⚠ **It is drawn at exactly the radius that grabs it, which no shape before it could be.** The test
answers for a circle of `CentreRadius` pixels. The old square's half-side was exactly that, so its
four corners stuck out to `√2 ×` it and did not answer clicks; the cube that followed had to be
divided by `√3` to pull its corners back inside, which left it drawn at a bit over half the region it
stood for. A sphere's every point is the same distance out, so it is the one shape whose silhouette
*is* the circle — what is drawn and what is grabbed are the same disc, with no ring of pixels that
looks like the handle and is not, and none that answers for it and looks like nothing. It is the same
rule `Tolerance` follows for the arms, and breaking it fails the same way: at the edges of a handle,
which reads as the tool being unreliable rather than as a number being wrong.

⚠ **The arms are built through it and it is drawn over them.** `ArmStart` is zero, so every shaft is a
segment from the origin outwards and the inner `CentreRadius` pixels of all three are covered by the
ball, which is appended last. Nothing has to fit: an arm cannot leave a gap or overshoot when it
starts inside the shape that hides it, which is what the old non-zero `ArmStart` had to be tuned
against. What makes zero safe for the *test* is the ordering in `HitTest` — the centre circle is taken
before any arm is looked at, so the middle keeps its clicks whatever is built across it. Move that
check below the arms and the gap has to come back.

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

## The floor grid

The spacing is chosen, not fixed: the 1-2-5 step that puts the lines roughly `TargetSpacing` pixels
apart, which is the rule a chart's axis ticks follow and for the same reason. A one-metre grid is a
grey haze from two hundred metres up and three lines from half a metre away.

Everything else about it is decided from the **world coordinate**, and the four things that were
decided from a loop index or a line count instead are the four ways it looked wrong:

- **The emphasised lines were every tenth *line*, not every tenth round number.** The lines are laid
  out from the pivot, so the emphasis marched sideways one line at a time as the view was panned — a
  grid that is subtly, continuously wrong and that nobody can point at. ⚠ `MajorColour` is now
  brighter as well as more opaque, because the two used to differ only in alpha and the distance fade
  below took that over: an emphasised line at the rim came out fainter than an ordinary one under the
  pivot, so the emphasis said "near" rather than "round".
- **The finer level was a tenth of the coarse one**, which is four or five pixels apart at every
  distance — permanently too dense to read and permanently drawn. So the fade computed from it never
  left a tenth, and the level it controlled was an invisible haze costing two hundred segments a
  frame. It is now the previous step of the sequence, half or two fifths of the coarse spacing, which
  is legible at one end of a bracket and not at the other — which is the only reason a fade has
  anything to do.
- **The finer level used the coarse level's line count**, so it covered a tenth of the reach: a small
  dense square patch sitting in the middle of the grid.
- **The reach was a fixed count of lines**, which is a fixed reach in *screen* terms — right looking
  down and far too short looking along the ground, where the whole budget is spent in the first few
  metres and the floor stops just past your feet. It is now in screen-heights at the pivot's depth,
  the one unit that is the same at every zoom, with a hard ceiling on the segment count because a
  camera at the horizon can see for ever.

⚠ **Every line is emitted as two halves meeting under the pivot**, faded to nothing at the ends. A
colour at each end can only fade one way along a segment, and what a grid has to do is be solid where
you are looking and gone at the rim — three colours across one line. `LineVertex` has carried a colour
per vertex from the start for exactly this; the grid was the caller that was not using it, so its far
edge was a hard rectangle drawn across the scene.

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

### …and until there is a target for it, a ray

⚠ All of the above is true and none of it was reachable: `SceneViewport.Pick` returned `false` unless
a `PickingBuffer` had been set, nothing set one, and so **a click in the viewport selected nothing at
all** — the only way to select an entity was the hierarchy panel, and clicking empty space did not
even deselect. `ScenePicker` is the answer that needs no device: `Picking` is still checked first, and
`Picker` is what answers when there is none.

- ⚠ **The ray goes into each shape's local space rather than the vertices coming out of it.** A cube
  is twenty-four vertices and a torus six hundred, and transforming them per entity per click is the
  whole cost of the test; inverting one matrix is not. The parameter along the ray survives the
  transform so long as the direction is *not* renormalised on the way in — which is what makes
  distances from differently scaled entities comparable, and what makes clicking the near cube not
  select the far one behind it.
- **Exact, not bounds.** The corner of a sphere's bounding box is empty, and answering for it is what
  makes clicking beside a ball select the ball.
- **An entity with no shape is a cross, and a cross has no area to hit.** What is tested is a small
  sphere about its origin, sized in *render pixels* rather than world units — a light two hundred
  metres away is a handful of pixels and has to stay clickable, which is the same reason a gizmo is a
  constant size on screen. Measured at the marker rather than at the camera's pivot, because those
  differ whenever the thing being clicked is not what the camera is orbiting.
- **A zero scale is skipped rather than thrown over.** An entity can be scaled to nothing and back,
  and a picker that threw would take the editor with it.

**Shift or control extends the selection, and toggles.** The same modifier that adds something is the
one that takes it back out, because two gestures for the two halves of one idea is what makes people
click an already-selected object and wonder why nothing happened. A miss clears the selection and an
*additive* miss does not — that is the end of a rubber-band that grabbed nothing.

## Show flags are a bitset per pane

Unreal's Show flags and Unity's scene-view toggles, and both exist for the same reason: a viewport
that draws everything is unreadable the moment a scene has lights, parents and bounds in it, and each
of those is something somebody needs to look at on its own for ten minutes and never again.
`SceneShow` is the bitset and `SceneLines`/`SceneMeshes` are what read it.

- **Per pane, not per editor.** The point of four panes is that they disagree — a wireframe top view
  beside a shaded perspective one is the whole reason somebody asked for four — so it lives beside
  the camera and the view mode.
- ⚠ **A flag that is off costs nothing rather than costing a walk that emits no vertices.** For the
  parent links, the only source that is a test per entity, that is the difference between skipping a
  branch and skipping the pass.
- ⚠ **Only flags with something behind them are there.** Doc 20's checklist also names colliders,
  audio sources and navigation; there is no collider component, no audio-source component and no
  navigation mesh to draw, so a tick for any of them would be a control that does nothing — doc 20's
  own second bar failed by the menu meant to satisfy it. They arrive with the subsystems.
- ⚠ **The grid is `SceneShow.Grid` and *not* `SceneGrid.Enabled`.** The grid keeps its own switch for
  a host with no show flags, and the editor writes exactly one of the two. Two writers to one setting
  is how a menu tick and a panel toggle come to disagree.
- ⚠ **`Components` is contributed gizmos and `Gizmos` is the transform handles**, and they are two
  flags on purpose. Turning the handles off is somebody saying "stop putting an arrow over the thing I
  am looking at"; it is not them asking for every trigger volume in the level to disappear. The two
  are one word apart and sharing a switch would have been wrong every time either was used.

## Anything can draw a gizmo for its own component

`ComponentGizmo` is a registry contribution — a component type, and a `GizmoDrawer` handed the boxed
value, where the entity is, and whether it is selected. `[DrawGizmo]` is the same thing declared, read
out of a plugin's or a project script's assembly.

- ⚠ **`LightShapes` is what this replaces the shape of.** It is a walk over the scene testing for one
  component type and switching on its kind — this mechanism, written once, in the assembly that
  happens to know about lights. A plugin's component had no way to be drawn at all, which is doc 36's
  F2 in the one place a level designer would notice it.
- ⚠ **`GizmoPlacement` is five vectors rather than the `Transform` they came from.** `Transform` is a
  `ref struct`, so it cannot be a delegate parameter anybody keeps, and it needs a `World` and an
  `Entity` to exist — a test for a gizmo would otherwise have to build a world to call one.
- ⚠ **On `SceneViewport` rather than on `SceneLines`, and that is about who can reach what.**
  `ComponentGizmos` needs the component bridges, which the *application* assembles; the `SceneLines`
  that draws with it belongs to the presenter, which the *executable* builds and which deliberately
  cannot see the application. The pane is the one object both ends already hold.
- ⚠ **The component arrives boxed, which is the tooling path's price rather than a mistake.** One
  allocation per entity per frame per gizmo is what a runtime `Type` costs — the trade
  `Vixen.Core.Reflection` names for the inspector. A gizmo that has to be free is a built-in that can
  be generic.

## A mode can draw under the pointer

`SceneViewport.Cursor` is an `Action<GizmoDraw>?` a mode sets while it is armed and clears when it
leaves. `SceneLines.Build` calls it, which is what makes it appear at all: `Build` is what both
`ScenePresenter` and the compositor-driven `FramePresenter` call, so a hover cursor cannot vanish
when the view mode switches. `Vixen.Editor.Terrain`'s `TerrainCursor` is the first one — the brush's
reach and its plateau, conformed to the ground.

- ⚠ **Pushed rather than pulled, for `ComponentGizmos`' reason one shelf up.** `Vixen.Editor.Terrain`
  references this assembly and not the reverse, so `SceneLines` cannot ask a terrain what its brush is
  over. The pane is the one object the mode and the presenter both hold.
- ⚠ **Into `overlay`, which is the opposite of a contributed gizmo.** A gizmo says where a thing *is*
  and has to be occluded to say it. A cursor says where the next click will land and is conformed to
  the surface it is lying on, so it is coplanar with the geometry it would be depth-tested against —
  in the depth-tested channel it z-fights its way in and out as the camera moves, and against an exact
  compare it disappears altogether.
- ⚠ **No show flag**, for `SceneMeasure`'s reason: it exists only while a mode is armed, and a second
  switch hiding it is how somebody picks up the brush, sees nothing and concludes the tool is broken.
- ⚠ **`OnCrossed` is why a cursor can be taken away.** `Entered` and `Exited` are never fed in from
  outside — the document works them out and delivers them `RoutingStrategy.Direct`, and
  `UiElement.Invoke` matches handlers on the strategy they registered with, so the bubble listener
  that hears every move never hears a crossing. Without the second registration a mode draws a ring
  that stays behind, at the last place inside the pane, for the whole time somebody is using a panel.

## A plugin can float a panel over a pane

`SceneOverlay` is Unity's `[Overlay]`: a title, a corner, and a builder handed a host element and the
pane. `ViewportChrome` hosts them beside the toolbar, the stats readout and the rubber-band, which
were the only things that could be over a pane before.

- ⚠ **Built once per pane, not once.** Two panes are two cameras, two view modes and two active tools,
  so an overlay showing any of that has to be two elements reading two viewports — the same failure
  `ViewportChrome` already describes for showing one toolbar over four panes.
- ⚠ **A corner rather than coordinates.** Panes are split, resized and rearranged; a panel placed at a
  pixel offset is under the toolbar in one layout and off the edge in another.

## Rubber-band selection

A press in empty space starts a band **every time**, because a click and a band begin identically and
which one it is cannot be known until the pointer has moved. The release is where they part: a band
that never reached `Marquee.MinimumSize` is answered by the picker, and one that did is a region
query.

- **Two corners, not an origin and a size.** A drag goes in any direction, and the consumer that
  forgets to cope with a negative width is the hit test rather than the drawing — so a band dragged up
  and to the left selects nothing while looking exactly like one that works.
- ⚠ **Either dimension passes the threshold, not both.** Dragging along a row of objects is a band a
  few pixels tall and several hundred wide.
- **Touching, not containing**, which is what both reference editors do by default: a band that only
  took what it fully enclosed cannot select anything larger than the pane, so the gesture stops
  working precisely where a scene gets big.
- ⚠ **Corners behind the eye are dropped rather than projected.** A perspective divide answers for
  them with a real pixel position mirrored through the middle of the pane, which would stretch the
  object's screen rectangle across the whole viewport and put it in every band anybody drags.
- ⚠ **An empty band clears the selection and an additive one does not** — the same rule a miss
  follows, and for the same reason.
- The band is drawn by `MarqueeOverlay`, an element in `Viewport.Overlay` rather than geometry in the
  render target: it is in layout pixels, in the same draw list as every panel, and it is
  `pointer-events: none` because it covers the very pixels the drag is happening over.

## The grid is a plane you can move

`WorkPlane` is doc 24's D5: an origin, a rotation and a step. `SceneGrid` is a *view* of it — the
adaptive 1-2-5 spacing, the emphasis on round numbers and the reach in screen heights all still
happen, in the plane's own two directions rather than in world X and Z. A default plane is the
identity, so a grid nobody moved is the grid that was always here.

- **Set it to a face** and the grid is on the wall; everything placed, dragged and snapped afterwards
  is in the wall's plane.
- **Offset along its own normal**, which is the second floor at three metres without doing arithmetic
  — and one wall-thickness further along when the plane is on a wall.
- **`]` and `[` double and halve the step**, from whatever the grid is currently drawing.

⚠ **The adaptive spacing stays and a chosen step overrides it, and it has to be both.** A grid that
only adapted could never be the four metres a level is blocked out at; one that only obeyed would be a
grey haze from two hundred metres up. Until somebody presses `]` there is no step — the spacing is a
function of the camera — which is why `Coarsen` has to be told what was on screen.

⚠ **Powers of two, so every level is a sub-lattice of the last.** A 0.25 m object is still on the 4 m
grid's lines; a step of a third would never be on one again.

⚠ **`SnapContext.GridStep` reads the plane rather than being pushed at.** "The grid I can see" and
"the grid I snap to" are one number, asked for on demand — the second number that could disagree with
it does not exist.

## Typing an exact transform mid-drag

`NumericEntry` is Blender's `G X 5 ⏎`, which doc 24 calls the single most-missed feature by anybody
coming from Blender and which neither reference editor has. It cost almost nothing, and the reason is
the gizmo's design: every frame of a drag is already *the pose at the grab* plus *a magnitude derived
from the pointer*, so typing substitutes the magnitude and the same arithmetic runs. An implementation
that accumulated per-frame deltas could not express it at all.

- ⚠ **A typed number beats a snap.** Rounding it to the grid or pulling it onto a corner afterwards
  would answer a question the user did not ask.
- ⚠ **An axis letter overrides the handle that was grabbed**, because pressing one is a more specific
  statement than which arrow your pointer happened to be over. Pressing it again releases.
- ⚠ **An axis letter is only *taken* once a digit has been typed.** `X` on its own during a drag is a
  key some other tool may want.
- ⚠ **Backspacing the last character out backs out of the entry**, so the drag goes back to following
  the pointer rather than sitting frozen at zero.
- ⚠ **`Number0` follows `Number9` and `Keypad0` follows `Keypad9`** — the order the keys are in, not
  the order the digits are. A range test from zero types every digit one place out.

`TransformGizmo.Dragged` is the other half of the same idea: the drag says how far it has gone, in
metres or degrees or as a factor, and `ViewportChrome` draws it above the middle of the pane. Doc 24's
objection to both reference editors is precise — "both make you read a details panel" — and a number
in the corner of a four-pane layout is a details panel with fewer steps.

## Measuring, and things of a known size

`SceneMeasure` is two points a distance and three an angle at the middle one; a fourth starts again,
because the gesture after reading a measurement is measuring the next thing. ⚠ **It snaps like
everything else**, which is the whole of why it is worth having: between two points the pointer landed
on it is a number nobody can act on, and between two corners it is the width of the doorway.

`ReferenceVolumes` are the four sizes every level designer draws by hand on every project — a person,
a door, a corridor and a car. ⚠ **Drawn and not shipped**: lines in the pane rather than entities, so
there is nothing to select, nothing to save and nothing to leave in a level by accident. Each knows
where its box sits relative to the point it is placed at, because a person stands on the floor and a
corridor is a hole you are inside.

## Snapping is one service

`SnapContext` is doc 24's D4: **what you land on, what of yours lands on it, and everything true of a
snap without being either.** One instance per editor, handed to every pane's gizmo and every pane's
`ScenePlacement`, so a drop and a drag onto the same ramp cannot disagree about whether the thing
landing on it stands up.

| | |
|---|---|
| `SnapElements` | increment, absolute grid, vertex, edge, edge centre, face |
| `SnapBase` | the gizmo's origin, the selection's centre, the active element, **the point you grabbed** |
| `SnapModifiers` | align rotation to the target, search from the view, ignore what is being dragged |

⚠ **The blocker was never the viewport, and that is the lesson worth keeping.** `SnapToVertex` and
`SnapToSurface` *were* honoured here, fully tested, and nothing anywhere turned them on:
`scene.toggle-snap` moves the increment, the angle and the scale and says nothing about the elements
that need geometry. The feature was complete, tested and unreachable — this repository's commonest
defect wearing a snapping hat. Grep for who *sets* a flag, not for who reads it.

⚠ **The four booleans that used to be here are views over `Elements` rather than second state.**
`SnapPosition`, `AbsoluteGrid`, `SnapToVertex` and `SnapToSurface` get and set bits, so a toolbar
toggle and a settings panel cannot disagree about whether snapping is on — there is nothing for them
to disagree about.

⚠ **The base is the half everybody omits and the half that matters.** Snapping the *centre* of what
you dragged to a vertex is almost never what you meant; you meant the corner you grabbed, which is
`SnapBase.Pointer`. It costs nothing: a drag already records where the ray met the handle when it
began.

`ISceneProbe.TrySnap` is the one query behind all of it — over `MeshElements`, so edge and edge-centre
snapping exist at all — and the precedence lives in it: vertex, edge centre, edge, surface.

- **Smallest first, and the set composes.** Holding vertex and surface at once is strictly better than
  either: a vertex snap only answers when there is a corner within reach, so falling through to the
  surface is a better drag rather than a mode switch.
- ⚠ **Only a surface snap carries a normal.** A vertex is a point and an edge is a line; neither says
  which way anything faces, so `AlignToTarget` has nothing to align to and the drag is a move. That is
  not a gap to fill by averaging the faces round a corner — a cube's corner would stand things up
  diagonally.
- ⚠ **Nearest *on screen* by default, and nearest to the base when `ProjectFromView` is off.** The
  gesture is usually "put it on that corner" and which corner is meant is decided by where the pointer
  is; the other reading is what you want when the handle being held is a long way from the geometry
  the object should land on. The reach is the same either way — the pixel radius is converted to
  metres at the base — so trying both does not also mean re-tuning a number.
- ⚠ **The exclusion is the caller's, not the probe's.** What "self" is belongs to whoever is dragging:
  a pane knows it is the selection and a placement about to create something has nothing to leave out.
  That is why `ISceneProbe` exists beside `ISurfaceProbe` rather than replacing it.
- ⚠ **Still constrained by the handle being dragged.** A snap on the X arm moves along X to the
  snapped point's X; a snap on a plane handle moves in that plane. "Snap to that corner" and "keep it
  on this axis" compose rather than the last one written winning.
- ⚠ **`TransformGizmo.SnapTo` is a *point*, not a probe.** Answering "which vertex is nearest" needs
  the scene, the camera and the pane's size in render pixels — three things a gizmo has no business
  holding, two of which would have to be threaded through `Drag` for the one mode that uses them.
- ⚠ **A ray's parameter does not survive a transform, because `Ray` normalises.** Both this and
  `ScenePicker` take the local hit *point* back out through the matrix and measure the world distance
  from it. Without that, a shape scaled fourfold answers with a quarter of the distance — so "the near
  cube rather than the far one" was decided by scale rather than by depth, and a shape's distance was
  not comparable with a marker's at all.

## Shapes are instanced, and that was a blocker rather than an optimisation

`SceneMeshes` walked the scene once a frame, transformed every vertex of every entity into world space
and appended them to one list. `docs/plan/24-blockout-tools.md` § B1 is the argument for why that had to change
before anything else in that document could be built, and the sentence worth keeping is the one about
what the failure looks like: **a drag that redraws at four frames a second is not a slow tool, it is a
tool nobody can aim.**

What made it a blocker rather than a cost was the cache: it was keyed by `PrimitiveKind`, so a hundred
cubes were one `MeshData` and a hundred *edited* meshes would have been a hundred rebuilds a frame,
with no sharing left in the pass. So the collector now emits one `MeshInstance` per entity — a
transform, a normal matrix, a colour and four style lanes, a hundred and sixty bytes — grouped into
`ShapeBatch`es that share a shape, and `MeshInstanceRenderer` holds each shape's geometry in a
`GeometryBuffer` that is written once.

- **A `ShapeBatch` names a `PrimitiveKind`, not a device handle.** This assembly still has no device
  in it: `ScenePresenter.Resolve` is where a kind becomes a range in a vertex buffer, on the frame the
  first entity wanting it appears. The day a block-out mesh is a mesh of its own rather than a
  parameter, that is the only thing that changes.
- ⚠ **A batch's instances have to be contiguous**, because a draw names a first instance and a count.
  Grouping is why the collector buckets per shape instead of appending in tree order, and it is the
  only invariant the collector owes the renderer.
- ⚠ **The outline, the wireframe and the normal view were all copies of the geometry and are now style
  lanes.** Selecting everything in a scene used to double the frame's vertex count. It now costs one
  more instance per selected entity, which is the case the outline is actually used in.
- ⚠ **One inverse per entity, not per vertex.** The normal matrix is the transform's inverse transpose
  and is computed here, because a shader language has no inverse to ask with — and because without it
  a cube scaled `2 1 1` has normals that are no longer perpendicular to their faces, so the shading
  slides across the object as it is scaled and reads as the light moving.
- ⚠ **`SceneMeshes.Segments` stays where it was even though a smoother sphere is now free.** What it
  decides is also what `ScenePicker` and `SceneProbe` test against, and those still walk triangles:
  changing it changes what a click hits.
- ✅ **The surfaces are shaded by their material.** This bullet used to say materials were what was
  missing. `SceneMeshes.Surfaces` resolves an entity's material reference to a `MaterialSurface` — a
  base colour, a metalness, a roughness and what it emits — and all of it reaches the instance as two
  more vertex attributes, so two entities of different materials sharing a shape stay one draw. The
  shader is a metal-roughness BRDF: GGX, a height-correlated Smith visibility and Schlick, one key
  light and a constant term standing in for the environment.
- ⚠ **What that reduction drops is textures.** A base-colour map multiplies a tint and there is
  nowhere here to sample one from, so a material whose tint is white and whose map is a brick comes
  out white. Normal maps, clear coat, sheen, anisotropy and subsurface are passed over the same way —
  silently, because a material with a clear coat is still a material whose base colour the viewport
  should draw, and refusing it would make "not implemented" look identical to "not assigned". A
  per-face material and the blockout checker still need the viewport driven by `RenderSystem` through
  a `GraphicsCompositor`, which is Phase 7's wiring and is where the picking stage is still blocked.
- ⚠ **An entity with no material is drawn exactly as it was before any of this.**
  `MaterialSurface.Default` is a fully rough dielectric, which comes out at one directional term to
  within a rounding — deliberately, because the alternative is every block-out level in existence
  changing appearance on the day this landed.

## A selection outline without a stencil

The textbook outline is a stencil pass and a post effect over it: a second render pass, a stencil
format this target does not have, and a shader. What this path does instead is an inverted hull built
from the geometry that is already there — the object's own vertices pushed outwards across the view by
a width in **pixels**, exactly rather than approximately.

- The expansion is along the part of the normal lying **across** the view, scaled by how many world
  units a pixel is at that vertex, so the rim is the width it was asked for in pixels at every
  distance and in both projections.
- ⚠ **A vertex whose normal points at the eye is not expanded at all.** It is not on the silhouette,
  and expanding it pushes the front face outwards through its own surface — an orange bloom over the
  middle of the selection.
- ⚠ **The hull is pushed away from the eye by a bias in pixels.** Moved only across the view it would
  be at the same depth as the object at every pixel the object covers, which the rasterizer settles
  differently per triangle: an outline that flickers in patches.
- ⚠ **The hull's normals face the light rather than the surface.** One renderer, one ambient term for
  the whole draw — so the only way to have a flat rim beside shaded surfaces is a lambert term of one
  everywhere, which is what the third style lane asks for.
- It is collected only when surfaces are: in a wireframe view there is nothing for a rim to be the rim
  *of*, and an expanded hull with no object over it is a solid blob.
- ⚠ **The expansion moved into the vertex stage when the shapes became instanced, and it had to.** It
  needs the camera at every vertex, and there is no vertex on the processor any more. What crosses the
  boundary instead is the numbers the measurement is made of, in `MeshInstanceView` —
  `EditorCamera.PixelScale` is the part of `WorldPerPixel` that does not depend on the point, and the
  shader multiplies it by the depth along the view axis.
- ⚠ **What that costs is a test.** The picture the expansion makes can only be asserted on with a
  device and a golden image; that the *numbers* it is made of are the camera's own is asserted, in both
  projections, against `EditorCamera.WorldPerPixel`. That is the half of it that can drift silently.

### ⚠ None of it is in a composed pane, and the tint is not either

Everything above is the **tool renderer's**, and so is `SceneMeshes.SelectedColour`, the amber tint
that replaced the rim. A pane drawn through a `GraphicsCompositor` binds neither: it is `ForwardPlus`
over ECS-extracted objects, and the editor's instanced mesh shader is not in that frame. So a composed
pane drew a selected object pixel-for-pixel like an unselected one while the transform gizmo sat on it
saying otherwise — with the gizmo off, selecting a crate changed **eleven pixels out of half a
million**, and all eleven were a parent-link line changing hue behind the cube's edge.

**`SelectionCage` is what says it there**, and it is lines, which is the one thing #151's `Tools` pass
records over a composition. `SceneLines` emits it, so it reaches both presenters by construction —
#144's rule about the terrain cursor, applied to the thing it was really needed for.

- ⚠ **An overlay rather than a change to the surface, and in that pane it is not a compromise.** A
  composed pane exists to show the frame a game would draw; tinting the selected object amber is the
  one edit that destroys what the pane is for. The tint stays right in the tool pane, which is a
  diagram. A picture wants its annotation drawn over it.
- ⚠ **Brackets rather than a box, because `SceneShow.Bounds` is a box.** Drawn together they read as
  one thick doubled box — this was rendered before it was believed. `SelectionCage.Corner` is a
  quarter of each edge, so the middle half of every edge is empty and a cage is `2 × Corner` of a wire
  box's length.
- ⚠ **And the bounds box no longer turns amber for a selected entity**, which it did from a build in
  which nothing else round one was. One question, one answer each: the box is what extent a thing has,
  the cage is which thing is selected.
- The standoff is the hull's own rule kept — four pixels through `EditorCamera.WorldPerPixel` at the
  object — divided by each axis's scale on the way into local space, so a crate scaled fourfold on X
  does not carry four times the gap on X.
- ⚠ **Not behind a show flag**, unlike everything else `SceneLines` collects. A flag names a class of
  thing the scene has; whether the click just made landed is not something a pane may be configured to
  stop reporting.
- ⚠ **The rim is still owed for the tool pane's own sake**, and this does not close it. What is closed
  is that a composed pane says nothing at all. A faithful rim — a stencil the target does not have, or
  a second draw of the extracted mesh where `SceneRenderHost.Load` puts the debug overlay — remains
  available and does not conflict: the cage is entity-level and a rim is surface-level.

## View modes are compositors

Doc 06 made the compositor data precisely so that "show me the normals" is a different tree rather than
a branch inside the renderer. `ViewModes` collects them: a host registers the trees it has, and a mode
with none falls back to shaded rather than showing nothing.

✅ **And they are wired now.** `EditorWorldRenderer` builds one document with a subtree per mode and
hands them to every pane's `ViewModes`; a pane is compositor-driven exactly when a tree is registered
for the mode it is in, and every other mode is still the tool renderer's. Switching mode is
`GraphicsCompositor.Game` being pointed at a different subtree — `Build`, `Collect` and `Degradations`
all walk from `Game` down, so there is no rebuild and no second render system. The host asks
`Modes.Registered.Contains(Modes.Current)` and **not** `Resolve`, because `Resolve` falls back to the
shaded tree for every mode: read that way, Albedo would compose and draw the shaded picture.

### ⚠ A stage belongs to the mode, and the pipeline cache is why

`ViewModes.ApplyTo` says a four-pane layout with independent render modes needs a stage per pane. That
is true only of the arrangement it describes — mutating one shared stage — and that arrangement does
not work at all:

`PipelineKey` is `(Effect, Stage.Index, VertexLayout, Output)` (`Core/Vixen.Rendering/PipelineCache.cs`),
`PipelineCache` never evicts, and nothing in the tree calls `Clear`. So a stage's rasterizer, blend and
depth state are read **exactly once** — by `EffectPipelineDescriber.Describe`, on the first draw that
misses the cache — and baked into a pipeline the key can no longer tell apart from any other state on
that stage. ⚠ Mutating a stage that has already drawn changes the mode and not the picture.

So wireframe gets a **stage of its own**, configured before it has ever drawn, which is the one
legitimate call of `ApplyTo`. Stage per *mode* is also strictly better than stage per pane: two panes
in wireframe share it and both draw wireframe, and the count is the nine modes rather than the pane
count.

⚠ Which makes the extraction mask the **union** of every mode's stage, set before the first `Extract`.
A mask is copied into a render object as it is created and a settled entity is never extracted again,
so a mask carrying only the shaded bit is a pane that draws until somebody picks Wireframe and then
draws nothing — while still reporting its objects, its lights, zero waiting and zero dropped.

⚠ Wireframe is registered only where the device reports `HasWireframe`. `FillMode.Wireframe` needs
`fillModeNonSolid`, which is optional in Vulkan, and a pipeline built without it is silently filled
solid. Absent, the pane keeps the tool renderer's wireframe below, which is drawn as segments and
works everywhere.

✅ **Every pane composes**, where at most one used to. The limit was the render view:
`EditorWorldRenderer` held one `RenderView`, one `GraphicsCompositor` and therefore one set of imports
and one reference size, so two panes declaring in the same frame both drew the second one's camera. It
holds a view, a colour, a depth and a sub-frame **per pane** now — four of each, because
`ViewportArrangement.Quad` is four and the document is built in a constructor, so the slots have to
exist before anybody splits the panel.

⚠ **The build may not be split, and the reason is `RenderView.Index`.** `RenderSystem.SetViews` assigns
it, runs once per `GraphicsCompositor.Collect` and clears the list first — and the work list a pass
records is looked up by that index at *execute* time, which is after every pane has built. A build per
pane would therefore leave all four panes recording whichever view took index 0 in the last collect:
four cameras, one visible set, and every counter in the frame healthy. So `EditorHost.Record` resizes
every pane, runs the frame's prologue **once** — `WorldRenderer.Draw` opens with the per-frame
descriptor pool's boundary, and a second call between two panes hands the second pane sets the first
pane's passes are still going to bind — then uploads and prepares each pane, and composes all of them
with one `EditorWorldRenderer.Compose`.

⚠ **A reference size is the frame's where an extent is the pane's.** `Compositor.Resize` takes one size
and four panes have four, so the linear target between the shading pass and the grade is sized
explicitly per pane by `EditorWorldRenderer.Size`, and the reference size is written once from the
*largest* pane. A resource declared with no size is `Scale` of `FrameSize`, and a colour of one size
attached beside a depth of another is a framebuffer the driver refuses rather than a picture that is
merely wrong.

⚠ **The first pane's names are unsuffixed and that is load-bearing rather than tidy.** A project's own
`.vxcompositor` names `Camera`, `SceneColour` and `SceneDepth` and knows nothing about panes, so an
authored frame composes pane 0 and the other three keep the tool presenter — which draws. The same is
true of a pane past the four slots.

### …and for the modes no tree is authored for, `ViewShading`

Two trees are authored — shaded and wireframe — and the other seven modes are still drawn by the tool
renderer, so this table is still the live one for them. What it draws is `SceneMeshes` through
`MeshInstanceRenderer`: device-resident shapes, one instance per entity, one key light, and a material
per entity reduced to a `MaterialSurface`. `ViewShading` is the table of what *that* path can honestly
express, and seven of the nine modes are expressible with no new module and no new pipeline:

| Mode | How |
|---|---|
| Shaded | The default. |
| Shaded Wireframe | The same, plus a second batch of the same instances drawn from the shape's edge index range. |
| Wireframe | Only that batch. Segments rather than `FillMode.Wireframe`, which needs `fillModeNonSolid` — optional in Vulkan and absent on most tiled GPUs, so a view mode that drew nothing on a phone. |
| Unlit, Albedo | The ambient term at one, which the shader arranges to be the base colour exactly — the environment stand-in is weighted by metalness, so a dielectric's ambient is its albedo and nothing added. |
| Normal | A style lane, and the shader remaps the world normal into a colour ⚠ from −1..1 rather than clamping: half of every normal is negative, so clamping would paint three of a cube's six faces black. The selection's colour is ignored in this mode, because painting the selected object orange in a view whose content *is* the normal makes the one object being looked at the one the view cannot answer for. |
| Roughness | ✅ A greyscale written at *collect* time rather than a style lane, which is the asymmetry with Normal: a normal varies per vertex and only the shader can know it, where a roughness is one number per entity that the collector has in hand. ⚠ The instance is also given the neutral surface — shading a roughness view by the roughness is the number multiplied by a picture of itself. |

⚠ **The other two are registered and greyed rather than absent.** Light complexity needs the clustered
light list; overdraw needs an additive pipeline with the depth test off. `ViewModes.Resolve` falls back
to shaded for an unregistered mode — right for a compositor that has not been authored, wrong for a
menu line, because a line that draws the picture of the line above it reads as the editor ignoring the
click.

✅ **Roughness was a third of them and enabled itself.** Its excuse was that the tool renderer had no
material to read a roughness off, and it has one now. Nothing in `ViewportCommands` changed, because
the enablement is `ViewShading.IsSupported`'s answer rather than a list written out twice.

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

**A shape and a light used to be keys of their own rather than entries in that list, and both said they
were temporary.** They were the *editor's* components, because the runtime had nowhere to name a
`PrimitiveKind` or a `LightKind` — `Vixen.Engine` deliberately does not reference `Vixen.Rendering`. The
resolution was that the reference needed to run the other way: `Vixen.Rendering` references `Vixen.Ecs`
and `Vixen.Engine` and declares `Light`, `PrimitiveShape` and `MeshRenderable` in its own `Ecs/` folder,
exactly as `Vixen.Physics` and `Vixen.Audio` do. All three are ordinary entries in the list now, as a
`Camera` always was.

⚠ **`shape:` and `light:` are still read, and are never written.** Every scene authored before that
carries them, and the YAML binder ignores keys it does not know — so removing the properties would not
fail to open those files, it would open them and quietly drop the geometry and the lighting. A file
rewrites itself into the new form on its first save. What is left is cosmetic: `OmitDefaults` is a
whole-document setting and is off for this format, so a newly saved scene carries `shape: ''` and
`light: null` and means nothing by either.

⚠ **A `Color3` needs a scalar converter, the same as a `Vector3`.** Nothing in
`Vixen.Core.Mathematics` carries the reflection generator, so every type of its that the format
names has to be registered in `SceneScalars` — the symptom of forgetting is "Color3 has no
descriptor", thrown from the serializer when somebody saves, which is to say after the work is done
rather than when the field was added.

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

**`SceneSerializer.Instantiate` is `Load` for a subtree that is not the document's own.** It creates
one file entity and everything under it, and takes a map instead of adopting the file's identities.
Reading a scene *is* adoption — that is what makes a save, load and save cycle a no-op in the diff —
and a **prefab instance** is the case that must not: two instances of one prefab in one scene would
otherwise both claim the template's ids, and every reference between entities would name whichever
was reached last. The map records where each entity came from, which is also exactly what an override
comparison needs. See
[`Vixen.Editor.AssetEditors`](../Vixen.Editor.AssetEditors/README.md) for the prefab editor built on
it, and for `PrefabFileWriter`, which refuses a document that is not a single subtree at the save
rather than at somebody else's build.

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

## Three picking questions, not one

| | |
|---|---|
| Which entity is under this ray | `ScenePicker`, on the processor, exactly, per primitive |
| Which entity is at this pixel | `PickingRenderer` + `PickingBuffer`, the id buffer, driven by nothing yet |
| Which face, edge or vertex of *this* mesh is under the pointer | `SubObjectPicker` over `MeshElements` |

The third is doc 24's B4 and it is deliberately a separate interface — `ISubObjectPicker`, which
`ScenePicker` also implements. It is asked of one entity, it answers with an index into a table only
the caller and the mesh agree about, and it needs a tolerance in pixels because a vertex has no area.
A stub that answered "which entity" cannot sensibly answer it, and every test in this assembly that
has one would have had to.

⚠ **A drawing vertex is not a vertex.** `MeshData` splits a corner wherever a normal or a texture
coordinate had to be, so a cube's eight corners are twenty-four entries and its twelve edges are not
in it at all. `MeshElements` derives the other graph — shared positions, unique edges, triangles — by
welding within a tolerance relative to the mesh's own size, because a sphere's seam is `cos 0` against
`cos 2π` and exact welding leaves a line of doubled positions down every curved primitive.

⚠ **`MeshElements` is not `EditMesh` and must not grow into it.** Doc 24's P1 builds the authored
structure — n-gon faces, an edge table that reports non-manifold edges, attribute layers, face groups
— in `Core/Vixen.Geometry`. What is here is the smallest thing that lets a pointer name an element of
geometry the editor *already draws*. Two consequences follow and both are asserted rather than worked
around: a face is a triangle, so the diagonal across a cube's side is a selectable edge; and nothing
is occluded, so the far corner of a cube is as selectable as the near one. The second is what the id
buffer closes, with an element id in it instead of an entity id.

## Live parameters beside the mesh

Doc 24's D6. A shape made by the shape tool carries **both**: the `ShapeParameters` it was made from
and the `EditMesh` they generated. `SceneDocument.SetShape` writes both; `SceneDocument.SetShape(…,
null)` — the demotion — removes the parameters and leaves the geometry exactly where it is.

⚠ **Both, rather than deriving the mesh on demand.** Deriving it would mean the picker, the drawing
and every selection walk each asking a generator for geometry they then have to cache, and every one
of them switching source at the moment of demotion. With both, demotion is one dictionary removal —
which is what makes the one-way door one-way with nothing to clean up, and what lets an element mode
be entered on a live shape for free.

⚠ **A parametric entity writes its parameters and not its mesh.** The geometry is a function of the
numbers, so a file carrying both would carry two answers to one question — and "make the corridor a
metre wider" would be a rewritten mesh in the diff instead of a changed number.

⚠ **`ShapeCommand` records no geometry at all**, where `EditMeshCommand` has to record whole meshes.
Putting the parameters back *is* putting the mesh back, exactly, for six numbers.

⚠ **The badge is derived.** `IsPlainMesh` is "has a mesh and no parameters", which needs no flag to
save, migrate or keep true through an undo — and puts the same badge on a mesh that arrived from an
import, which is in exactly the same position.

## A material per face group

`SceneDocument.MaterialsOf` is doc 24's P5 per-face material, and the assignment is to a **group**
rather than to a face: a wall's twelve faces after two bevels are still one wall, and an assignment
remembered per face index is one the next loop cut renumbers away.

⚠ **On the document rather than on the mesh**, because an `AssetReference` means nothing to
`Vixen.Geometry` — which references `Vixen.Core.Mathematics` and nothing else. The kernel owns which
faces are in which group; what a group *is* is the editor's.

⚠ **Two materials on one mesh are two draws.** A material is per instance in `MeshInstanced`, so
`SceneShape` carries a group and `EditMeshes.ToMeshData` cuts the pieces — but only when a group
actually names a material, because a block-out is nearly all one material and splitting every wall
into six pieces because a box has six groups would be six uploads for one picture.

## Cloning a subtree

`SceneClone` is the component-wise copy [doc 20's E1](../../docs/plan/20-editor-parity.md) files its
clipboard as blocked on. The scene component registry is the filter, which is the same rule saving
applies — so a duplicate is what you would get by saving the entity and reading it back, and nothing
is copied that a build could not compile.

⚠ **The geometry goes through the same commands a verb would use** rather than being written into the
document directly, which is what makes an undo of a duplicate put the tables back as well as the
entity. ⚠ **Children are copied in reverse**, for the reason the serializer gives at length:
`Hierarchy.Link` puts a new child at the head of the intrusive list.

⚠ **Behaviours are not copied yet and that is stated rather than silent.** A `Behavior` is an object
with authored fields rather than a value in a column, so cloning one is `SceneBehaviorRegistry`'s to
answer and is E1's rather than doc 24's P4. A duplicated block-out wall has no behaviours to lose.

## First refusal on a pane's input

`SceneViewport.Input` is an `IViewportInput`, and it is how doc 20's `IEditorMode` reaches a pane
without this assembly ever hearing about the shell. Null is the default and is the editor as it
shipped; the editor sets one adapter over the mode registry and shares it across every pane.

⚠ **Refusal is over what a gesture *starts*, not over one already running.** A pointer event arriving
while the gizmo is dragging or a rubber-band is open goes to the pane whatever the owner says —
otherwise a mode entered mid-drag could take the release of a drag it did not begin, and the gizmo
would be left holding the object with no event ever arriving to let go.

⚠ **Keys are the other way round and are offered during a drag.** Doc 24's numeric entry —
`G X 5 ⏎` — is only meaningful while a drag is in flight, so a hook that stood down for the duration
of one could not carry the feature it exists for. What still comes first is Escape, which is the
drag's own way out and has to stay reachable from inside any mode.

## Not in

~~**Solid rings and plane quads.**~~ ~~Solid handles.~~ Both are in. A rotation ring is a tube swept
round the circle — cut to the camera-facing half exactly as the polyline was, because solid geometry
makes the crossings worse rather than better — and a plane handle is a filled translucent square with
its outline still over it. The fill is what the hit test has always answered for: `InQuad` is a
point-in-polygon test over the whole square, not a distance to its border, so the outline understated
what answers a click by the whole of its middle.

~~**A selection outline.**~~ In, as an inverted hull rather than as a stencil pass — see above for why
that is exact rather than an approximation here, and for the three things it gets wrong if any of them
is skipped.

~~**Vertex snapping.**~~ In, through `SceneProbe` rather than through a readback, together with
surface snapping. The readback is still the right answer for geometry a shader moved; a scene of
primitives is a scene the processor can answer about exactly.

~~**Rubber-band selection.**~~ In, as a screen-space region query rather than as a region readback —
`IScenePicker.Within`, over the same projected bounds a click tests against.

**The picking *stage*.** ~~Clicking to select in the viewport.~~ That works, through a ray test.
`PickingRenderer` is written and tested and nothing drives it, and the reason has moved rather than
gone away: it is a `SceneRenderer` over a `RenderStage`, which needs the viewport driven by
`RenderSystem` through a `GraphicsCompositor`. The editor's viewport is `SceneMeshes` through
`MeshInstanceRenderer`, which has device-resident geometry and a per-entity transform but neither a
compositor nor a material system. So this is blocked on the same material-system wiring the view modes
are — doc 20's Risks table says that work should be scheduled *before* the viewport milestone, and it
was not — and it stays blocked until it is, at which point the stage connects to a real target and
`ScenePicker` becomes the fallback rather than the answer.

**Auto-depth.** `EditorCamera.OnPivotPlane` is the depth a zoom-at-the-pointer assumes when it has not
been told one, and it is right when the grid is what you are looking at. Blender samples the depth
buffer instead. That wants a readback the picking stage already knows how to do, and it wants
something in the depth buffer to sample.

~~**Meshes.**~~ In, and twice over: solid shapes went in as world-space triangles through
`MeshRenderer`, and are now device-resident geometry drawn once per entity through
`MeshInstanceRenderer` — see above for why the second step was a blocker rather than a tidy-up.

~~**A material on them.**~~ In, as a reduction rather than as the material system. An entity's material
reference resolves to a `MaterialSurface` — base colour, metalness, roughness, emission — which reaches
the instance as two vertex attributes and is shaded by a metal-roughness BRDF in `MeshInstanced.rvn`.
What is still out is anything that needs a *texture*: a base-colour map, a normal map, a per-face
material and the blockout checker are the same Phase 7 compositor wiring the picking stage waits on,
because sampling one needs a descriptor set and this pipeline has none.

Licensed under Apache-2.0.

## Mesh assets

`SceneMeshes` collects an entity's `MeshRenderable` before its `PrimitiveShape`, and batches by
`SceneShape` — a key that names either a built-in kind or a mesh reference. That is what makes a
hundred instances of one rock a single instanced draw, exactly as a hundred cubes already were.

⚠ **The key's discriminator is the reference, not the kind.** `PrimitiveKind.Cube` is zero, so a
defaulted key would mean "every entity is a cube" — a viewport full of cubes where the meshes should
be, which reads as the feature not working rather than as a key that collided.

**The mesh wins over the shape**, which is the rule `MeshExtractionSystem` makes an archetype fact
with `WithNone<MeshRenderable>`. Here it is a branch, because the editor walks a document's entity
list rather than a query — and an entity that looks different in the viewport from how it looks in
the game is the one defect a viewport must not have. An unloaded mesh therefore draws *nothing*
rather than falling back to its shape, and `Waiting` says how many.

Geometry comes from `IMeshSource`. The editor's implementation is `ProjectMeshSource`, which reads
the chunks the last import wrote out of the project's own artefact store — not a content build, which
is something an author runs when they ship rather than every time they open a scene. It is
invalidated when an import finishes: a chunk is content-addressed, so a re-imported mesh is a new id
under the same reference and nothing about the cached geometry would ever say it is stale.
