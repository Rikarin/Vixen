---
title: UV charting and seams
slug: engine/uv-charting
kind: guide
area: Engine
summary: Where to cut a mesh — a distortion-driven recursion whose chart count is an outcome of a quality target, and seams that are walks on the mesh's own edges.
api: [T:Vixen.Geometry.Uv.UvUnwrap, T:Vixen.Geometry.Uv.UvSettings, T:Vixen.Geometry.Uv.SeamCost, T:Vixen.Geometry.Uv.IChartDecomposition, T:Vixen.Geometry.Uv.UvReport]
tags: [geometry, uv, unwrap, charting, seams, decomposition]
since: 0.1
status: preview
related: [engine/uv-flattening, engine/uv-packing, engine/edit-meshes]
---

## What it is

`UvUnwrap.Charts` takes a mesh and answers with a chart per face — which is to say, where the seams
go. It does not flatten anything and it does not place anything in an atlas. Its output is exactly
what [`UvUnwrap.Flatten`](uv-flattening.md) takes, and `UvUnwrap.All` runs all three stages in one
call.

## What it is for

The first and hardest of the three problems. Cutting badly is not a quality setting — it is fifty
islands where a dozen would do, a seam across a character's face, and charts that ignore the model's
parts entirely.

It is separable because the charts do not have to be this library's. An artist's seams, the
remesher's patch layout and a file all describe the same value, so `Flatten` and `Pack` are equally
happy without ever calling this.

## Using it

```csharp no-compile="a fragment; `mesh` is an EditMesh"
var charts = UvUnwrap.Charts(mesh, new(), out var report);

// … or all three stages at once, which is what an importer wants.
var full = UvUnwrap.All(mesh, new(), new() { Resolution = 2048, Margin = 4 }, out var uvs);
```

`uvs` is one coordinate per `EditMesh.Corners` entry. Coordinates are per *corner* rather than per
position because a seam is one shared position whose two sides carry different coordinates — free in
the corner layer, and a vertex split in anything else.

## Chart count is an outcome, not a knob

⚠ **Nothing here is told how many charts to make, and that inversion is the whole design.** The
recursion is four steps:

1. **Decompose** the mesh into candidate regions. Material and face-group boundaries partition first
   and unconditionally — see `KeepGroups`.
2. **Flatten** each region and measure what it cost.
3. **Accept or recurse.** Under `DistortionThreshold`, keep it. Over, split it and try again, bounded
   by `MaxDepth`.
4. **Merge back.** Adjacent charts whose union still meets the threshold are merged, greedily,
   largest first.

⚠ **Step four is the half most tools do not have, and it is why they fragment.** Growing regions
until a stretch bound trips, with nothing that ever puts two back together, produces a chart count
that measures how often a bound tripped rather than how good the atlas is.

So the way to move the count is to move `DistortionThreshold`. Tighten it and charts multiply;
loosen it and the texture stretches. There is no chart-count setting to reach for, because a chart
count you asked for is a quality you did not.

## Two bounds, because two different things can fail to terminate

`MaxDepth` bounds the **distortion** recursion: a chart that will not come under the threshold is
eventually accepted as it is, and said so in `UvReport.Warnings`.

⚠ **A chart that cannot be laid flat at all is cut regardless of the depth.** An annulus, a closed
component with no boundary, a chart in two pieces and a bowtie pinch have *no* injective map to the
plane, so accepting one would ship a chart with no coordinates and a hole in the texture. That
recursion terminates on its own arithmetic — a split always produces strictly smaller parts and a
single face is always a disk — so it needs no depth bound and must not have one.

## A seam is a walk on the mesh's own edges

Every cut is a set of **existing edges**, found by search on the mesh graph under an edge cost.
Nothing is ever placed in space and snapped to the mesh afterwards, so there is no snapping stage and
there are no snapping artefacts.

`SeamCost` is where *"where would an artist cut"* is written down. Seven terms, each normalized so
that a caller can zero one out without rebalancing the rest:

| Term | Prefers a seam that… |
|---|---|
| `Concavity` | sits in a crease that folds inward, where a discontinuity does not read |
| `Visibility` | is occluded, estimated by ambient occlusion over the surface |
| `Feature` | follows a hard edge, where a normal-map discontinuity is invisible anyway |
| `Material` | runs where the texture already changes |
| `Symmetry` | lies on a mirror plane, so the two halves' seams agree exactly |
| `Length` | is short |
| `Existing` | was already there, when re-unwrapping a mesh that had coordinates |

⚠ **`Length` is the term everything else is traded against.** Raise it and the cutter takes the short
way round a feature it should have followed; drop it and seams wander through flat regions collecting
a fractional saving each time. It is the one to reach for first and the one to move least.

## Replacing the decomposition

`IChartDecomposition` is the only plug point this library has. Leaving `UvSettings.Decomposition`
null selects the built-in one — an approximate convex split over the dual graph weighted by dihedral
concavity and surface occlusion — and **the default path never calls an implementation of this**.

⚠ **It proposes and never decides.** Whatever comes back is still flattened, still measured, and
still has to pass `DistortionThreshold` before it is kept, so a bad decomposition costs chart quality
and can never cost validity. Returning `null` declines and falls back for that region alone, which
makes a proposer that only understands some shapes a useful proposer.

⚠ **Determinism is part of the contract rather than a quality of the implementation.** The output of
this call reaches every coordinate in the atlas, so an implementation that iterates a hash set,
consults a clock or restarts randomly breaks the byte-identical gate for the whole library.

## What it measured

On a corpus of eleven shapes chosen so that each one fails differently: **3.09 charts at an L²
stretch of 1.0059**, against **3.64 charts** with the merge-back pass switched off. The merge pass
takes 15 % off the recursion's count, concentrated entirely on the two shapes that fragment at all —
a closed torus goes 14 charts to 11, a dumbbell 9 to 6, and the nine shapes that already chart to
three or fewer are untouched.

⚠ **Those are not comparable with the published figures and it would be dishonest to line them up.**
The 10.4 / 51.6 / 74.3 charts reported for MeshTailor, xatlas and Blender's Smart UV Project are
averages over a garment dataset, not over eleven primitives.

## Examples

**Cut a mesh and see where the seams went.**

```csharp no-compile="a fragment; `mesh` is an EditMesh"
var charts = UvUnwrap.Charts(mesh, new(), out var report);

Report(report.ChartCount, report.SeamLength, report.SeamLengthNormalized);

// A seam is an edge whose two faces ended up in different charts.
for (var edge = 0; edge < mesh.Edges.Count; edge++) {
    var faces = mesh.FacesOf(edge);

    if (faces.Length == 2 && charts[faces[0]] != charts[faces[1]]) {
        Highlight(mesh.Edges[edge]);
    }
}
```

**Trade chart count against stretch**, which is the only control there is.

```csharp no-compile="a fragment; continues from above"
UvUnwrap.Charts(mesh, new() { DistortionThreshold = 1.02f }, out var tight);   // more charts
UvUnwrap.Charts(mesh, new() { DistortionThreshold = 1.60f }, out var loose);   // more stretch
```

**Follow the hard edges harder**, by leaning on one term of the cost.

```csharp no-compile="a fragment; continues from above"
var settings = new UvSettings {
    SeamCost = new() { Feature = 4f, Length = 0.5f }   // ⚠ Length is what Feature is bought from
};

UvUnwrap.Charts(mesh, settings, out var byFeature);
```

**Keep an artist's material boundaries and let nothing merge across them.**

```csharp no-compile="a fragment; `mesh` carries per-face groups"
// The default. Group boundaries partition first, and the merge-back pass may not undo one.
UvUnwrap.Charts(mesh, new() { KeepGroups = true }, out var kept);
```

## See also

- [UV flattening](uv-flattening.md) — what takes this stage's output.
- [UV packing](uv-packing.md) — the third stage, and a peer of a standalone packer.
- [docs/plan/42](https://github.com/rikarin/Vixen/blob/master/docs/plan/42-uv-unwrapping.md) — the
  design, and the references it is drawn from.
