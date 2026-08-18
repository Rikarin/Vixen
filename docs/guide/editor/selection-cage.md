---
title: Showing what is selected
slug: editor/selection-cage
kind: guide
area: Editor
summary: The corner brackets a viewport draws round a selected object, and why a composed pane cannot say it with colour.
api: [T:Vixen.Editor.SceneView.SelectionCage]
tags: [editor, viewport, selection, compositor, gizmo]
since: 0.1
status: preview
related: [editor/modes, editor/frame-panel]
---

## What it is

`SelectionCage` draws eight corner brackets round an object's extent, in the object's own axes,
standing off it by a width in render pixels. It is handed a `BoundingBox`, a world matrix, a camera
and a colour, and it writes twenty-four segments into a `GizmoDraw` — the same line list the grid, the
entity markers and every contributed gizmo go into.

`SceneLines` calls it once per selected entity, and that is the whole of the wiring: whatever
`SceneLines` emits appears in both of the editor's viewport paths.

## What it is for

**A viewport has to say which object you just clicked, and colour cannot always say it.**

The editor draws a pane one of two ways. `ScenePresenter` shades the editor's own block-out shapes
with the tool renderer, and there a selected surface is simply tinted amber —
`SceneMeshes.SelectedColour`. `FramePresenter` draws the pane through a `GraphicsCompositor` instead:
`ForwardPlus` over the same ECS-extracted objects a game would draw, with the editor's tools recorded
over the top in one `Tools` pass.

Nothing the tool renderer does to a surface exists in that second pane. The amber tint is not there,
and neither was the inverted-hull rim that preceded it — that rim lived in the editor's own instanced
mesh shader, which a compositor frame never binds. So a composed pane drew a selected object
pixel-for-pixel like an unselected one, while the transform gizmo sat on it insisting it was selected.
Measured: with the gizmo switched off, selecting a crate changed eleven pixels out of half a million.

**In that pane an overlay is not a compromise, it is the only correct answer.** A composed pane exists
to show the frame a game would draw. Painting the selected object amber is precisely the edit that
destroys what the pane is for — the material you selected the object to look at is the thing you can
no longer see. A tint is right in a diagram; a picture wants its annotation drawn over it.

You do not want `SelectionCage` for saying an object has an *extent*. `SceneShow.Bounds` is that, and
the two are deliberately different drawings — see below.

## Using it

It takes geometry, not a scene, so a contributed gizmo can use it for whatever it considers selected.

```csharp no-compile="a fragment against a live camera and a live line list"
SelectionCage.Draw(
    draw,
    MeshPrimitives.Create(PrimitiveKind.Cube).Bounds,
    world.Read<WorldTransform>(entity).Value,
    viewport.Camera,
    height,
    new Color4(1f, 0.62f, 0.15f, 1f)
);
```

**Brackets rather than a box, because a box already exists.** `SceneShow.Bounds` draws twelve
continuous edges round *every* shaped entity as a diagnostic about extent. A selection drawn as a
second wire box, in the same place, is two drawings a person cannot tell apart — and rendering them
together proves it: they read as one thick doubled box. `SelectionCage.Corner` is a quarter, so each
bracket takes a quarter of its edge and the middle half of every edge is empty. A cage is exactly
`2 × Corner` of a wire box's total length, and it *is* a wire box at `Corner = 0.5`.

**The bounds box no longer turns amber.** It used to, from a build in which nothing else round a
selected object was. Now the two questions get one answer each: the box says what extent an object
has, in the neutral colour extent is drawn in; the cage says which object is selected.

**The standoff is in pixels, and it is divided by the object's scale on the way in.** A cage exactly
on the extent z-fights the silhouette it is annotating, and a gap in world units is invisible on a
building and swallows a bolt. `SelectionCage.Standoff` is four render pixels, resolved through
`EditorCamera.WorldPerPixel` at the object — the inverted hull's own rule, kept. Because the gap is a
world distance and the extent is in the object's own space, each axis divides by that axis's scale:
without it a crate scaled fourfold on X carries four times the gap on X, and a bracket further from
one face than from the next reads as a bug in the bracket rather than as a scale on the object.

**It is depth-tested, and it is not behind a show flag.** The near corners show and the far ones are
hidden by the object's own surface, which is what makes it read as a cage in the scene rather than a
decal on the glass. And every other thing `SceneLines` draws asks `SceneViewport.Show` first; this
does not, because a flag names a class of thing the scene has whether or not anybody asked to see it,
and whether the click just made landed is not something a viewport may be configured to stop
reporting.

**An entity with no geometry gets no cage.** A light, a camera and an empty have no extent, and a box
round one would be a box round an arbitrary constant. They are already answered: `SceneLines` draws
their marker cross in the selection's colour and at 1.6 times the size, and that reaches a composed
pane the same way this does.

## Examples

An entity's extent comes from whichever of three places it has geometry, and the order matches what
the viewport actually draws: an `EditMesh` being edited wins over a mesh asset, and a mesh asset wins
over a `PrimitiveShape`. The asset's size is resolved through `SceneViewport.Meshes`, the same
`IMeshSource` the scene's mesh components already read:

```csharp no-compile="a fragment; the application owns both objects"
pane.Meshes = SceneGeometry;
```

That is on the *viewport* rather than on `SceneLines` for a reason worth repeating, because it is the
same reason this page exists. Two presenters draw a pane and each holds a `SceneLines` of its own, so
anything hung on one of those appears in one pane's worth of the editor and silently not in the
other.

A mesh that has not loaded yet is not caged at a fallback size. `IMeshSource` is ask-don't-wait, so
the miss is the ask and the cage appears on the frame the geometry does — which is the frame the
object appears on. A guessed extent would draw a box of the wrong size round nothing at all for as
long as the disk took, and then jump.

## See also

- [The frame panel](frame-panel.md) — what a `GraphicsCompositor` document is, and what a pane driven
  by one draws.
- [Editor modes](modes.md) — what else the viewport is drawing while a mode is active.
- `Editor/Vixen.Editor.SceneView/README.md` — the viewport's own notes, including the inverted-hull
  outline this replaced in the composed pane and did not replace in the tool pane.
