---
title: Splines
slug: engine/splines
kind: guide
area: Engine
summary: A cubic Hermite curve with an arc-length table, the authored asset over it, and its two consumers — roads that deform a terrain and camera dollies that follow a track.
api: [T:Vixen.Core.Mathematics.Spline, T:Vixen.Core.Mathematics.SplinePoint, T:Vixen.Core.Mathematics.SplineFrame, T:Vixen.Core.Mathematics.SplineAsset, T:Vixen.Core.Mathematics.ISplineSource, T:Vixen.Terrain.TerrainSpline, T:Vixen.Terrain.TerrainSplineProfile, T:Vixen.Terrain.TerrainSplineMesh, T:Vixen.Engine.Cameras.TrackedDollyBody, T:Vixen.Engine.Cameras.DollyMode, T:Vixen.Editor.SceneView.SplineEdit, T:Vixen.Editor.SceneView.SplineCommand, T:Vixen.Editor.SceneView.SplineHandle, T:Vixen.Editor.SceneView.SplineElement, T:Vixen.Editor.Terrain.TerrainSplineSettings]
tags: [mathematics, spline, curve, camera, terrain, roads]
since: 0.1
status: preview
related: [engine/terrain-heightfield, engine/terrain-painting, editor/terrain-mode, engine/foliage]
---

## What it is

`Spline` is a cubic Hermite curve through a list of `SplinePoint` control points, each carrying a
position, an in and out tangent and a roll. It evaluates by parameter, evaluates by *distance* through
a precomputed arc-length table, produces a `SplineFrame` — position, tangent, normal, binormal — at
any point, and answers "how far along is the point nearest this one".

One project reference, to nothing: it is arithmetic over `Vector3`.

## What it is for

Anything that follows a path. A camera rail, a road cut into a terrain, a river, a fence placed at
even intervals, a patrol route. It is in `Vixen.Core.Mathematics` rather than in any of those because
all four wanted it and none of them should own it.

You do not want it for a curve that has to interpolate *and* stay within a hull — that is a B-spline,
and it does not pass through its control points, which is the property an author placing points
expects.

## Using it

```csharp no-compile="a fragment; the points come from whatever placed them"
var spline = new Spline(points);

var at = spline.EvaluateAtDistance(60f);
var frame = spline.FrameAt(spline.ParameterAtDistance(60f), Vector3.UnitY);
```

⚠ **A Hermite tangent is three times a Bézier handle.** `m₀ = 3(P₁ − P₀)` for the two to describe the
same curve. Getting it wrong produces a curve that looks plausible and is measurably the wrong length
— a quarter circle comes out at 1.44 instead of π/2.

⚠ **Evaluating by distance needs a table, and the table is why.** Arc length has no closed form for a
cubic, so "sixty metres along" is a search through `SamplesPerSegment` samples per segment with a
linear interpolation between two of them. A caller that stepped the parameter uniformly instead would
move fast through tight curves and slowly through straight runs, which on a camera rail is the whole
of what a person notices.

⚠ **`SmoothTangents` is what turns positions into a curve.** A list of positions with zero tangents is
a polyline; the Catmull-Rom rule — each tangent from the chord between its neighbours — is what makes
it smooth, and it is what an author placing points means.

⚠ **A degenerate chord gives a zero tangent, not a NaN.** `Vector3.Normalize` returns `Zero` for a
zero-length input rather than a NaN, so a check for finiteness never fires and the guard has to be a
length test.

⚠ **The frame's normal comes from a world up, not from parallel transport.** It is the cheap answer
and it fails at the poles — a spline that goes vertical has no defined roll about the world up. What
`SplinePoint.Roll` is for is saying what happens there, by hand.

## Examples

Placing fence posts every three metres, which is the question the arc-length table exists for:

```csharp no-compile="a fragment; the spline came from placed points"
for (var distance = 0f; distance < spline.Length; distance += 3f) {
    var at = spline.EvaluateAtDistance(distance);
    var frame = spline.FrameAt(spline.ParameterAtDistance(distance), Vector3.UnitY);

    Place(at, frame.Tangent, frame.Normal);
}
```

Turning a list of positions into a curve rather than a polyline:

```csharp no-compile="a fragment"
var points = Spline.SmoothTangents(positions, closed: false);
var spline = new Spline(points);
```

Finding where on a road a point is, which is what a terrain carve needs:

```csharp no-compile="a fragment"
var distance = spline.DistanceTo(sample, out var t);
```

⚠ **`DistanceTo` searches the sampled table, not the analytic curve.** It is exact to the sampling
rate and is what a brush needs; a caller wanting more can refine around the parameter it returns.

## The authored asset

`SplineAsset` is what a `.vxspline` holds and what an editor mutates.

⚠ **Two types, and the split is the point.** `Spline` is immutable and precomputes an arc-length
table; the asset is mutable and precomputes nothing. An editor moves a control point on every frame of
a drag, and rebuilding a length table sixty times a second for a curve nobody is measuring is what
makes an editor feel heavy. Ask for `Build()` when the answer is needed.

⚠ **An asset with one point is legal and is not a curve.** An author places the first point of a road
before they place the second, and an asset that refused to exist until it had two would have to be
built from a dialog rather than from the viewport. `CanBuild` is the question a consumer asks.

⚠ **`InsertOn` preserves the shape, which is why it is not `Insert` with an evaluated position.**
Dropping a point on and leaving the tangents alone reparameterises both halves, so the road moves —
and the author's next act is to drag it back to where it already was. The segment is converted to its
Bézier form, subdivided by de Casteljau, and converted back, which changes three points' tangents and
no positions.

⚠ **A cut keeps the point in both halves**, because splitting a road at a junction and moving one half
should leave the other ending where it did. A closed path **opens** rather than splitting: a ring cut
once is one path.

⚠ **Joining merges a coincident end.** Two control points at the same place make a segment of zero
length, which has no direction — so a road joined without the merge has a frame that flips at the seam
and a mesh placement that stacks everything it puts there.

## Editing it in the viewport

```csharp no-compile="a fragment; the world point comes from a pane's ray"
edit.Pick(where);
edit.Begin();
edit.Move(delta);

if (edit.Commit() is { } command) {
    document.Stack.Execute(command);
}
```

⚠ **Tangent handles are selectable in their own right.** The only way to author a corner is to move
one of them without the other, so a handle set that held only positions could express a smooth road
and nothing else. A tangent of zero length sits exactly on its point and **wins the tie** — otherwise
the handle would be unreachable precisely once the author had used it to make a corner.

⚠ **The undo record is the whole point list.** A heightfield stroke records a rect because a terrain
is megabytes; a spline is a hundred points and about three kilobytes. Two moves merge into one entry;
an edit that changed the *count* never merges, because inserting a point and then moving it are two
things an author did.

⚠ **Deleting walks descending**, so the indices below a removal do not shift under it — the trap
`FoliageVolume.Remove` guards against, one subsystem over.

## Roads

```csharp no-compile="a fragment"
var layer = TerrainSpline.LayerOf(terrain);

TerrainSpline.Regenerate(terrain, layer, [(road.Build(), TerrainSplineProfile.Road)]);
```

⚠ **Non-destructive because of *where* it goes.** The deformation is written into a reserved
`TerrainLayerKind.Splines` layer — [§ D4](terrain-heightfield.md)'s mechanism — so moving the road,
changing its width or deleting it re-runs into the same layer and the author's own sculpting
underneath is untouched. A road written into the base heightfield can never be moved.

⚠ **`Regenerate`, not `Deform`, is what an editor calls.** `Deform` clears its own rect, which is
right for adding a road to a layer that is otherwise correct and *not enough* when a road moves out of
that rect — the old one stays. `Regenerate` empties the layer, lays every road down again, and
invalidates the chunks the layer had already allocated so the cached composite does not keep the old
road either.

⚠ **Distance is measured across the ground, not through the air.** `Spline.DistanceTo` is 3-D, which
is what a camera wants; used for a road it means a centreline can only deform ground it is already
level with — so a causeway drawn twenty metres above a valley floor, which is exactly how an author
draws one, touches nothing at all. `TerrainSpline.Nearest` is the horizontal one.

⚠ **Left and right fall off independently.** A road cut into a hillside has a cutting on the uphill
side and an embankment on the downhill one, and they are different widths.

⚠ **A cosine shoulder, not a linear ramp**, whose crease would catch the light along the whole length
of the road.

⚠ **Every sample within reach is visited once**, from the curve's own bounding box. Walking the curve
and stamping a brush at intervals double-counts wherever two stamps overlap — which on a tight bend is
most of the inside of the corner, so the road ends up deeper round its corners than along its
straights. The box covers the *curve* and not only the control points, because a Hermite segment
leaves the hull of its endpoints whenever the tangents are long.

## Painting and placing along one

`PaintAlong` lays a layer down the width, through `TerrainWeights.Paint` so the sum-to-one invariant
is maintained in one place. `PlaceAlong` spaces meshes along the length.

⚠ **Spaced by distance, not by parameter** — parameter spacing bunches everything up in the tight
segments and strings it out in the wide ones, which is exactly wrong for a fence.

⚠ **The choice of mesh is hashed from the index**, so re-running after moving one control point does
not re-roll the whole fence.

⚠ **It returns placements rather than writing them.** The terrain kernel has no scene and no asset
database; what a caller does with the list is the caller's.

## The road profile in the panel

`TerrainSplineSettings` is what the Splines panel edits, and `ToProfile` is where it meets
`TerrainSpline`.

⚠ **The two side falloffs are separate, and that is not symmetry pedantry.** A road cut into a
hillside has a cutting on the uphill side and an embankment on the downhill one, and one number for
both makes every mountain road look like it was laid on a plain.

⚠ **`Reach` is the wider side, not the average.** It is what an invalidated rect is sized from, and a
rect sized to the mean leaves the wide side's last metres unrebuilt — which draws as a seam that only
appears on one side of the road.

⚠ **The panel regenerates rather than deforms.** Deforming clears only the rect it is about to write,
so a road that moved leaves its old cutting behind for ever. The layer is reserved precisely so that
emptying it and laying every road down again is safe.

⚠ **Curve authoring is not on the panel and the panel says so.** `SplineEdit` is the viewport half
and it is not on the gizmo yet; a panel that silently had no way to author a curve would read as a
feature that does not work rather than as one that is not finished.

## Camera dollies

```csharp no-compile="a fragment; the source is the host's asset table"
system.Splines = tracks;
world.Add(camera, TrackedDollyBody.Following("Track"));
```

**This is [docs/plan/26]'s largest owed item.** That document declined to invent a spline for its
dolly track; the stage it said would be small once one existed is `TrackedDollyBody` and the hundred
lines behind it.

⚠ **`Position` is a distance in metres, not a parameter.** A camera moving at a constant parameter
rate speeds up through the wide-open segments of its own track and crawls through the tight ones — the
classic bug in every dolly ever written.

⚠ **An open track clamps and a closed one wraps.** A dolly clamped at the end of a loop stops at the
seam it was drawn from, which reads as the track being broken there.

⚠ **The offset is in the track's own frame**, so a track banked by `SplinePoint.Roll` carries the
camera round with it — which is the whole reason a spline point has a roll.

⚠ **A camera whose track cannot be resolved holds its position.** Falling back to the origin would
send it through the level the first frame after somebody renamed an asset.

⚠ **Auto-dolly writes the position back**, so an author reading the component sees where the camera
actually is and a mode switched to `Manual` mid-shot carries on from there. Nearest-point has two
answers on a track that doubles back and takes the closer; the mitigation is a track that does not
cross itself, which is Cinemachine's answer too.

⚠ **The dolly reads its component one entity at a time**, because a track is a *name* and a component
holding a string is a managed one. Every other body stage walks a contiguous column; this is the price
of naming an asset rather than holding a handle to it.

## See also

- [The terrain heightfield](terrain-heightfield.md) — the edit layers a road is written into.
- [Painting layers](terrain-painting.md) — the weights `PaintAlong` writes through.
- [Sculpt and paint mode](../editor/terrain-mode.md) — the modes a road shares its terrain with.
- [docs/plan/31 § B5](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  why the curve landed in Core rather than in whichever subsystem asked first.
- [docs/plan/31 § T8](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the asset, its two consumers, and doc 26's owed item it retires.
