---
title: Splines
slug: engine/splines
kind: guide
area: Engine
summary: A cubic Hermite curve with an arc-length table, so "sixty metres along" and "how far is that point" are both cheap questions.
api: [T:Vixen.Core.Mathematics.Spline, T:Vixen.Core.Mathematics.SplinePoint, T:Vixen.Core.Mathematics.SplineFrame]
tags: [mathematics, spline, curve, camera, terrain]
since: 0.1
status: preview
related: [engine/terrain-heightfield]
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

## See also

- [The terrain heightfield](terrain-heightfield.md) — one of the four consumers this was pulled out for.
- [docs/plan/31 § B5](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  why the curve landed in Core rather than in whichever subsystem asked first.
