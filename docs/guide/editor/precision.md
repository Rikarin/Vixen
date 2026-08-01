---
title: Building to a number
slug: editor/precision
kind: guide
area: Editor
summary: The work plane you build in, typing an exact distance mid-drag, the tape measure, and the scale references.
api: [T:Vixen.Editor.SceneView.WorkPlane, T:Vixen.Editor.SceneView.NumericEntry, T:Vixen.Editor.SceneView.TypedTransform, T:Vixen.Editor.SceneView.SceneMeasure, T:Vixen.Editor.SceneView.ReferenceVolume, T:Vixen.Editor.SceneView.ReferenceVolumes, T:Vixen.Editor.SceneView.ReferenceVolumeSet]
tags: [editor, viewport, blockout, grid, measurement]
since: 0.1
status: preview
related: [editor/snapping]
---

## What it is

Four things that share one job: making a viewport somewhere you build to a number rather than by eye.

`WorkPlane` is where you are building — an origin, a rotation and a step. The floor grid is a *view*
of it, so putting the plane on a wall puts the grid on the wall. `NumericEntry` reads an exact
distance, angle or factor typed partway through a drag and hands the gizmo a `TypedTransform`.
`SceneMeasure` is a tape measure. `ReferenceVolumes` are the four sizes every level designer draws by
hand on every project, drawn in the pane and never in the scene.

## What it is for

A grey box has no scale. A corridor is four metres wide or eight and there is nothing in an empty
scene to tell you which; a level designer asserting that the player fits through a gap, that the jump
is makeable and that the sightline reaches is asserting things that are only true or false in metres.

Each of these answers one of those without leaving the viewport. You do not want any of them for
authoring an asset — that is a DCC's job — but you reach for all four between two playtests.

## Using it

**The plane is what everything else is measured against.** It defaults to the ground through the world
origin, which is exactly what a floor grid always was.

```csharp no-compile="a fragment against a live pane — the plane is the editor's, shared by every one"
plane.SetTo(hit.Point, hit.Normal);   // the grid is now on the wall you pointed at
plane.Offset(3f);                     // the second floor, along the plane's own normal
plane.Coarsen(grid.Spacing(camera, height));   // ] — doubles from whatever is on screen
```

**The step is one number, and that is the point.** Until somebody chooses one, the grid picks its
spacing from how far away the camera is — the 1-2-5 sequence a chart's axis ticks follow, which is
right until it is not. `]` and `[` choose one, and from then on the grid draws it however far away the
camera moves, and `SnapContext.GridStep` reads it. Powers of two from wherever it is, so every level
is a sub-lattice of the last: a 0.25 m object is still on the 4 m grid's lines, and a step of a third
would never be on one again.

**Typing happens during a drag and nowhere else.** You are already dragging; you type, and the drag
becomes exact.

| | |
|---|---|
| digits, `.` | the magnitude |
| `X` `Y` `Z` | constrain to that axis, overriding the handle you grabbed. Again to release |
| `-` | flip the sign, wherever you are in the number |
| `Tab` | the next component; `Shift+Tab` the previous |
| Backspace | back out — the last character removed puts the drag back on the pointer |
| `Enter` | commit, which is ending the drag |
| `Esc` | cancel the drag and the typing with it |

⚠ **A typed number beats a snap.** Rounding it to the grid or pulling it onto a corner afterwards
would answer a question the user did not ask.

⚠ **An axis letter is only taken once something has been typed.** `X` on its own during a drag is a
key some other tool may want; it becomes a constraint once the user has said, by typing a digit, that
they mean an exact transform.

## Examples

The tape measure snaps like everything else, which is the whole of why it is worth having: a distance
between two points the pointer happened to land on is a number nobody can act on, and between two
corners it is the width of the doorway.

```csharp no-compile="a fragment; the points arrive already snapped by whoever is driving the gesture"
measure.Add(corner);
measure.Add(other);

var text = measure.Describe();   // "2.40 m", or "2.40 m  90.0°" once there are three points
```

Two points are a distance and three are an angle at the middle one. A fourth starts a new measurement
rather than extending the old one — the gesture after reading a measurement is measuring the next
thing, and a tool that had to be cleared first is one people clear by turning it off and on again.

A scale reference is a box of a known size put where you are building:

```csharp no-compile="a fragment; where it goes is the pane's work plane under the pointer"
references.Add(ReferenceVolumes.Person, at);
```

⚠ **Drawn and not shipped.** They are lines in the viewport rather than entities in the scene: nothing
to select, nothing to save, and nothing to accidentally leave in a level and find in a build. That is
the whole difference between this and the cube everybody scales to 1.8 and then forgets about. Each
volume knows where its box sits relative to the point it is placed at, because a person stands on the
floor and a corridor is a hole you are inside.

## See also

- [Snapping](snapping.md) — the context whose increment step the work plane supplies.
- [docs/plan/24 § P0](https://github.com/Rikarin/Vixen/blob/master/docs/plan/24-blockout-tools.md) —
  the phase these four make up, and why it is worth shipping before there is an editable mesh.
