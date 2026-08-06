---
title: Quad remeshing
slug: engine/quad-remeshing
kind: guide
area: Engine
summary: Triangles in, quads out — a retopologiser whose hard edges are layout boundaries rather than something snapped to, and which tells you how well it did.
api: [T:Vixen.Geometry.Remeshing.Remesher, T:Vixen.Geometry.Remeshing.RemeshSettings, T:Vixen.Geometry.Remeshing.ConditioningSettings, T:Vixen.Geometry.Remeshing.RemeshGuide, T:Vixen.Geometry.Remeshing.RemeshReport, T:Vixen.Geometry.Remeshing.ConditioningReport, T:Vixen.Geometry.Remeshing.RemeshStage, T:Vixen.Geometry.Remeshing.RemeshStageTiming, T:Vixen.Geometry.Remeshing.Singularity]
tags: [geometry, remesh, retopology, quad, mesh, blockout]
since: 0.1
status: preview
related: [engine/edit-meshes, engine/mesh-operations, engine/uv-packing]
---

## What it is

`Remesher.Remesh` takes a triangle mesh and returns an all-quad one. Seven stages run behind the one
call — condition, features, field, layout, quantize, extract, transfer — and a `RemeshReport` comes
back saying which stage took how long, how far the result strays from the source, where its irregular
vertices are, and anything that had to be forced.

## What it is for

Two cases, and they are the same code path.

A **generated mesh** — a photogrammetry scan, an SDF extraction, a model out of an image-to-3D tool —
arrives as marching-cubes soup with staircase noise, self-intersections and floating debris. It is not
editable and it is not riggable. The remesher makes it a quad cage.

A **boolean result** from a blockout is the other. Doc 24's `MeshBoolean` produces correct geometry
with a triangulation nobody would author, and the thing that matters about it is that its hard edges
stay exactly where they are. They do, because they are boundaries of the patch layout before the field
is ever solved rather than something the extraction is nudged toward afterwards.

## Using it

```csharp no-compile="a fragment; `triangles` is an EditMesh from an importer, a boolean or a shape"
var quads = Remesher.Remesh(triangles, new RemeshSettings { TargetQuads = 5000 }, out var report);

report.IsAllQuad;                  // asserted, not hoped
report.SingularitiesOnFeatures;    // zero on the interiors of the feature chains
report.MaxDeviation;               // a fraction of the diagonal, so it compares across models
```

`TargetQuads` is the only setting most callers touch. `TargetEdgeLength` says the same thing the other
way round; giving both is an error rather than a preference.

⚠ **It refuses rather than throws.** A mesh nothing can be done with comes back as an empty result with
the reason in `RemeshReport.Warnings` and the stage that gave up in `RemeshReport.Stages`. Every walk,
every repair and every solve inside it carries an explicit budget, so a pathological input is slow at
worst and never a hang.

## Hard edges are layout boundaries, and that is the whole design

Feature edges — dihedral angle over `FeatureAngle`, smoothing-group boundaries, face-group boundaries,
existing UV seams, and the artist's `Guides` — are chained into polylines *before* the cross field is
solved, and those polylines are boundaries of the patch decomposition. So a crease is a chain of output
edges by construction and `FeatureReproductionError` is measured at a tolerance of exact.

⚠ **The alternative is what produces good-but-wobbly hard surface.** Detect the edges, extract the
quads, then nudge extracted vertices toward what was detected: it is what the established tool does and
it is why that tool still ships its previous algorithm under a separate button.

Measured, as fractions of the bounding-box diagonal: `5.15e-5` on a box, `2.42e-5` on a plate with a
hole punched through it, `9.88e-5` on a cylinder. On a boolean of two boxes it is `8.6e-3`, and the
reason is in the report every time: on that partition the consistency system could not be satisfied
while every feature arc was held at one quad or more, so one was allowed to collapse.

## All-quad, and that is what makes it editable

⚠ **Quad-*dominant* is not good enough and the reason is downstream.** `MeshOperations` is built on the
assumption that a loop, a ring and a loop cut are statements about four-sided faces. A result with
triangles and pentagons in it has no rings to cut, and the mesh kernel's whole vocabulary stops working
on it. `NonQuadCount` is zero on every shape, and it is an assertion rather than an average.

Each patch is filled with a regular grid whose boundary vertices on a shared side are the *same*
vertices, by index. ⚠ **The seam is an equality and never a weld** — a tolerance weld there is how a
mesh acquires a crack that only appears under subdivision, on a model whose scale nobody thought about.

## Quantization is a flow, not an integer program

Each patch is a polygon whose sides need whole numbers of quads, two patches sharing a side must agree,
and a patch's opposite sides must agree for a grid to exist inside it. That system is solved as a
**minimum-deviation flow in a bi-directed graph** rather than as an integer program, for three reasons
and all three are constraints:

1. No commercial or copyleft solver may be taken, which rules out the usual two outright.
2. It is two hundred times faster at better energy, on the paper's own comparison.
3. ⚠ **A flow solver with an explicit tie-break is deterministic and auditable.** An integer program's
   answer depends on its solver's version and its internal timing, and a content build cannot have that.

There are two routers over the same formulation. The exact one is a successive-shortest-path search
with node potentials; the approximate one routes by fewest arcs and is for interactive preview.
Measured deviation energies, exact against approximate: 210 against 508 on a box, 132 against 255 on a
sphere, 283 against 592 on a cylinder, 214 against 728 on a flight of stairs.

⚠ **A patch side may quantize to zero and that is legitimate** — it is how a five-sided patch becomes
four-sided, and the extraction merges that side's two ends into one vertex. A patch whose whole width or
height comes to zero is a bug, and it is checked rather than assumed.

## Deterministic

Same input, same settings, byte-identical output, at one worker or sixteen. Four choices follow from
it: initialization derived from geometry rather than randomized, a deterministically computed graph
colouring, fixed iteration counts rather than convergence tolerances, and the flow solver above.

⚠ **This is also why it runs on CPU jobs and not the GPU.** Bit-exact float reduction across drivers
and vendors is not achievable, and this is an import-time cost rather than a frame cost.

## Examples

**Retopologise a boolean result and check the hard edges survived.** This is the blockout case, and the
number to read is `FeatureReproductionError`.

```csharp no-compile="a fragment; `wall` and `doorway` are EditMeshes"
var cut = MeshBoolean.Apply(wall, doorway, BooleanOperation.Difference);
var quads = Remesher.Remesh(cut!, new RemeshSettings { TargetQuads = 2000 }, out var report);

Report(report.FeatureReproductionError);   // a fraction of the diagonal
Report(report.Mesh.IsSolid);               // doc 24's report on the result, unchanged
```

**Read which stage was slow, and which one dropped something.** The stage boundaries are the debugging
surface; when a remesh looks wrong, *which stage* is the first question.

```csharp no-compile="a fragment; continues from above"
foreach (var stage in report.Stages) {
    Report(stage.Stage, stage.Elapsed, stage.Elements);
}

foreach (var warning in report.Warnings) {
    Report(warning);
}
```

**Refuse a result rather than ship it**, which is what makes this usable in an unattended content build.

```csharp no-compile="a fragment; continues from above"
var usable = report.IsAllQuad
    && report.SingularitiesOnFeatures == 0
    && report.MaxDeviation < 0.004f
    && report.Conditioning.Shrinkwrapped == false;
```

## See also

- [docs/plan/41](https://github.com/rikarin/Vixen/blob/master/docs/plan/41-automatic-retopology.md) —
  the design, the literature, and the licence table that decides it.
