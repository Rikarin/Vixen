---
title: Snapping
slug: editor/snapping
kind: guide
area: Editor
summary: What a transform lands on, which part of it lands there, and why that is one service rather than one per tool.
api: [T:Vixen.Editor.SceneView.SnapContext, T:Vixen.Editor.SceneView.SnapElements, T:Vixen.Editor.SceneView.SnapBase, T:Vixen.Editor.SceneView.SnapModifiers, T:Vixen.Editor.SceneView.SnapHit]
tags: [editor, viewport, snapping, gizmo, blockout]
since: 0.1
status: preview
related: [editor/sub-object-picking]
---

## What it is

`SnapContext` is what every transform in the editor rounds to. It has three orthogonal parts:
`SnapElements` says what a position may land on, `SnapBase` says which part of what is being dragged
lands there, and `SnapModifiers` is everything true of a snap without being either. `SnapHit` is one
answer — a point, sometimes a normal, and which kind of element gave it.

## What it is for

Snapping attached to a gizmo is a vertex snap that works when you drag an object and not when you
extrude a face, which reads as the feature being broken rather than as two features. One context is
what makes a drop and a drag onto the same ramp agree about whether the thing landing on it stands
up. The editor gives the same instance to every pane's gizmo, every pane's placement, and every tool
that comes later.

**The base is the half most editors omit and the half that matters.** Snapping the *centre* of what
you dragged onto a vertex is almost never what you meant; you meant the corner you grabbed. A snap
with no base concept can only offer the first, which is why a great many vertex snaps are a feature
people try once.

## Using it

Turn elements on; they compose.

```csharp no-compile="a fragment against a live editor — the context is the application's"
snap.SnapToVertex = true;
snap.Elements |= SnapElements.Face;

snap.Base = SnapBase.Pointer;
snap.Toggle(SnapModifiers.AlignToTarget, false);
```

**Holding several elements is strictly better than holding one.** A vertex snap only answers when
there is a corner within reach, so falling through to the surface when there is not makes the pair a
better drag rather than a mode switch. The order is smallest first — vertex, edge centre, edge,
surface — which is the same innermost-wins rule [sub-object picking](sub-object-picking.md) uses.

**The four booleans are views over the element set.** `SnapPosition`, `AbsoluteGrid`, `SnapToVertex`
and `SnapToSurface` get and set bits of `Elements` rather than being second state, so a toolbar toggle
and a settings panel cannot disagree about whether snapping is on.

**Only a surface snap carries a normal.** A vertex is a point and an edge is a line; neither says
which way anything faces, so `SnapModifiers.AlignToTarget` has nothing to align to and the drag is a
move. That is not a gap to be filled by averaging the faces round a corner — a cube's corner would
stand things up diagonally.

**`ProjectFromView` decides where the search happens, not how far it reaches.** On, the nearest
element to the *pointer* within `VertexRadius` pixels: the gesture is "put it on that corner", and
which corner is meant is decided by where the pointer is. Off, the nearest element to the *base*,
within the same radius converted to metres there — which is what you want when the handle being held
is a long way from the geometry the object should land on.

## Examples

The whole query is one call, and the precedence lives inside it rather than in each caller:

```csharp no-compile="a fragment; the exclusion list is whatever the caller is dragging"
if (probe.TrySnap(ray, pointer, camera, width, height, snap, origin, ignore, out var hit)) {
    gizmo.SnapTo = hit;
}
```

`IgnoreSelf` is read by the *caller* rather than by the probe, because what "self" is belongs to
whoever is dragging: a viewport knows it is the selection, and a placement about to create something
has nothing to leave out.

A snap is still constrained by the handle being dragged. Landing on a corner with the X arm held moves
along X to that corner's X and leaves the other two alone — "snap to that corner" and "keep it on this
axis" compose rather than the last one written winning.

## See also

- [Sub-object picking](sub-object-picking.md) — `MeshElements`, the welded positions and edges a
  vertex or edge snap actually lands on.
- [docs/plan/24 § D4](https://github.com/Rikarin/Vixen/blob/master/docs/plan/24-blockout-tools.md) —
  the argument for one service above the gizmo rather than one setting per tool.
