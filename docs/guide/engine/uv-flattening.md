---
title: UV flattening
slug: engine/uv-flattening
kind: guide
area: Engine
summary: A chart in, a flat island out — a three-rung ladder that measures what it cost you four ways and refuses anything it would have to fold.
api: [T:Vixen.Geometry.Uv.UvUnwrap, T:Vixen.Geometry.Uv.UvSettings, T:Vixen.Geometry.Uv.UvIsland, T:Vixen.Geometry.Uv.UvDistortion, T:Vixen.Geometry.Uv.UvReport]
tags: [geometry, uv, unwrap, flatten, lscm, arap, distortion]
since: 0.1
status: preview
related: [engine/uv-charting, engine/uv-packing, engine/edit-meshes]
---

## What it is

`UvUnwrap.Flatten` takes a mesh and a chart per face and lays each chart flat. It does not decide
where to cut and it does not place anything in an atlas — charts go in, one `UvIsland` per chart comes
out, and the islands are what [`UvUnwrap.Pack`](uv-packing.md) takes.

## What it is for

The middle of the three stages, and the one that decides whether a texture stretches. It is separable
because the charts do not have to be this library's: an artist's seams, the remesher's patch layout
and a file all describe the same thing — a chart per face — and all three are welcome here.

## Using it

```csharp no-compile="a fragment; `charts` came from the charter, from doc 41's patch layout or from an artist"
var islands = UvUnwrap.Flatten(mesh, charts, new(), out var report);

if (!report.IsInjective) {
    // Cannot happen: a chart that would fold produces no island at all. See below.
}

var placements = UvUnwrap.Pack(islands, new() { Resolution = 2048, Margin = 4 });
```

`chartOfFace` is one entry per face. A negative entry leaves that face out entirely, which is how you
flatten part of a mesh.

## Three rungs, and you only pay for the one you need

| Rung | What it is | When it runs |
|---|---|---|
| **LSCM** | One sparse least-squares solve. Conformal: angles are preserved, areas are not | Always |
| **ARAP** | A local–global loop that fits the closest *rotation* to every triangle | When the first missed `DistortionThreshold`, or folded |
| **Repair** | The same loop over the folded neighbourhood alone, growing | When the second still folded |

A developable chart — a cylinder wall, a flat panel, a lofted ribbon — stops at the first rung and is
exact there. A sphere or a torus goes up to the second.

⚠ **The two rungs are not ordered by quality, and the numbers say so.** A conformal map is *better* at
angles than an as-rigid-as-possible one, by construction, because that is what it optimizes. What it
is blind to is area, and that is the failure people actually see. Measured here on a slit sphere:

| | angular | area | L² stretch | L^∞ stretch |
|---|---|---|---|---|
| LSCM alone | **1.04** | 1.72 | 1.17 | 1.61 |
| Through ARAP | 1.26 | **1.22** | **1.04** | **1.38** |

The conformal column looks better on the metric a conformal map optimizes and forty per cent worse on
the one that shows up as a texture that is sharp on the shoulder and mush on the hand.

## Four measures, because one number hides the failure that matters

`UvReport.Distortion` is a `UvDistortion`:

- **`Angular`** — how far angles moved. One is conformal.
- **`Area`** — how far the area ratio moved from uniform. One is authalic.
- **`StretchL2`** — Sander's average stretch, weighted by surface area.
- **`StretchLInf`** — Sander's *worst triangle*, which an average will not show you.

All four are normalized so that **one is a perfectly isometric map and larger is worse**, and all four
are invariant to how big the model is. That is what makes `UvSettings.DistortionThreshold` a statement
about shape rather than about units.

## `Flipped` is not a metric

⚠ **`UvDistortion.Flipped` is a correctness field wearing a metric's clothes and it is always zero.**
A flipped triangle is a region of the atlas where the mapping is not invertible: a bake writes to the
wrong texel and sampling reads from it. There is no threshold for it and no trading it against the
other four.

So a chart the ladder cannot bring to zero **produces no island at all**. It is named in
`UvReport.Warnings` with the reason, and the answer to it is a smaller chart — which is the charter's
recursion, not the flattener's.

The test is [`ExactPredicates.Orient2D`](/docs/api/vixen.core.mathematics/exactpredicates) on the
coordinates that ship, not a `float` cross product: three points that are exactly collinear can give a
naive cross product of `16` in one argument order and `-67108864` in another, and a triangle that is
exactly degenerate in the parameterization is both the case that matters and the case the naive test
gets wrong.

## What is refused before any solve runs

A chart that is not a topological **disk** has no injective map to the plane, so producing coordinates
for one would be producing a fold with extra steps. The test is the Euler characteristic —
`χ = V − E + F`, which is `2 − 2g − b` and is one only for a disk — plus a separate check for the
pinch it cannot see.

| Chart | Refused as | Why |
|---|---|---|
| A tube, both ends open | not a disk | Two boundary loops, `χ = 0` |
| A torus with one hole | not a disk | One boundary loop and `χ = −1`. ⚠ A loop count passes this |
| Anything closed | closed | No boundary at all, so no cut has been made |
| Faces sharing no vertex | disconnected | Two charts wearing one id |
| Two fans meeting at a point | pinched | `χ = 1`, and not a surface |

## Determinism

Same mesh, same charts, same settings, byte-identical coordinates — at one worker, four or sixteen, and
at any batch size. Charts are the unit of parallelism and nothing inside one is threaded, so there is
no floating-point reduction whose order a thread count could change.

Renumbering the mesh's vertices does not move the map either. The two vertices LSCM pins are chosen by
**graph distance with a positional tie-break**, so an importer, a weld or a boolean that renumbers the
mesh gets the same answer rather than the same answer up to a similarity.

## Settings that matter

`UvSettings.DistortionThreshold` is what the first rung has to beat before the second is paid for. One
is isometric; the default is a little above it.

`FlattenIterations` and `SolverIterations` are **counts and not tolerances**, and that is a determinism
decision rather than a performance one: a residual test is a floating-point comparison whose outcome
can differ across platforms.

## Examples

**Flatten one part of a mesh and leave the rest alone.** A negative chart id excludes the face, so a
selection is a chart assignment with `-1` everywhere else.

```csharp no-compile="a fragment; `selected` is the caller's own set"
var charts = new int[mesh.FaceCount];

for (var face = 0; face < mesh.FaceCount; face++) {
    charts[face] = selected.Contains(face) ? 0 : -1;
}

var islands = UvUnwrap.Flatten(mesh, charts, new());
```

**Read what a chart cost, per measure rather than as one number.**

```csharp no-compile="a fragment; continues from above"
UvUnwrap.Flatten(mesh, charts, new(), out var report);

var distortion = report.Distortion;

// Angles moved this much, areas this much, and the worst single triangle this much. A chart that
// looks fine on the first and terrible on the second is the classic conformal failure.
Report(distortion.Angular, distortion.Area, distortion.StretchL2, distortion.StretchLInf);
```

**Find out which chart was refused, and why.** A refused chart produces no island; the reason is in
the warnings.

```csharp no-compile="a fragment; continues from above"
foreach (var warning in report.Warnings) {
    Log(warning);   // "Chart 7 has 2 boundary loops and Euler characteristic 0, so it is not a disk…"
}
```

## See also

- [UV packing](uv-packing.md) — where the islands go.
- [docs/plan/42](https://github.com/rikarin/Vixen/blob/master/docs/plan/42-uv-unwrapping.md) — the
  design, and the references it is drawn from.
