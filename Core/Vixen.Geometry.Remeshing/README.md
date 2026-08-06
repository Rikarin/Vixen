# Vixen.Geometry.Remeshing

A quad remesher. [docs/plan/41](../../docs/plan/41-automatic-retopology.md).

```csharp
var quads = Remesher.Remesh(triangles, new RemeshSettings { TargetQuads = 5000 }, out var report);

report.IsAllQuad;                  // asserted, not hoped
report.SingularitiesOnFeatures;    // zero, or the layout was wrong
report.MaxDeviation;               // as a fraction of the diagonal, so it compares across models
```

## Seven stages, and every one is an inspectable artefact

```
  source triangles
        │
   ①  Condition        weld · orient · de-speck · repair · isotropic pre-remesh
   ②  Features        dihedral · creases · groups · UV seams · guide curves
   ③  Field           4-RoSy cross field, hierarchical, feature-constrained
   ④  Layout          separatrix tracing · motorcycle graph · patch decomposition
   ⑤  Quantize        min-deviation-flow over the bi-directed patch graph
   ⑥  Extract         per-patch grids, stitched · relax · validate
   ⑦  Transfer        normals · UVs · colours · materials · skin weights · baked maps
```

⚠ **The stage boundaries are the debugging surface.** When a remesh looks wrong, *which stage* is the
first question, and a monolith cannot answer it.

## Features are boundaries by construction, not something snapped to

Feature edges — dihedral angle, explicit creases, face-group boundaries, existing UV seams, and the
artist's guides — are chained into polylines *before* the field is solved, and those polylines are
boundaries of the patch layout. So a hard edge is a chain of output edges by construction, and
`FeatureReproductionError` is measured at a tolerance of exact rather than of close.

The alternative — detect edges, extract, then nudge extracted vertices toward what was detected — is
what produces good-but-wobbly hard surface, and it is why the established tool still ships its
previous algorithm under a button.

## Conditioning is a stage with a report

⚠ **A remesher that assumes a clean manifold is a remesher that does not run on the input this
library exists for.** Generated meshes arrive with staircase noise at the voxel frequency,
self-intersections, floating debris and non-manifold edges. Seven steps, each reporting what it
changed, and a caller can refuse to continue on the report.

Non-manifold edges are repaired by **cutting, not merging**: cutting keeps the geometry and costs a
seam, merging invents a surface that was never there.

## All-quad, and that is what makes it editable

⚠ **Quad-*dominant* is not good enough and the reason is downstream.** `MeshOperations` is built on
the assumption that a loop, a ring and a loop cut are statements about four-sided faces. A result
with triangles and pentagons in it has no rings to cut, and the mesh kernel's whole vocabulary stops
working on it. `NonQuadCount` is asserted zero.

## Deterministic, and it is a gate

Same input, same settings, byte-identical output, at any thread count on any platform. Four choices
follow from it: initialization derived from geometry rather than randomized, a deterministically
computed graph colouring so parallel updates have no ordering effect, **fixed iteration counts rather
than convergence tolerances**, and a flow solver with an explicit tie-break rather than an integer
program whose answer depends on its solver's version.

⚠ **This is also why it runs on CPU jobs and not the GPU** — bit-exact float reduction across drivers
and vendors is not achievable, and this is an import-time cost rather than a frame cost. Recorded as
a decision, because "why is this not on the GPU" is the first question anyone reading the code has.

## One packer, not two

The atlas comes out of the patch layout — a quantized quad patch *is* a rectangle, so the layout is
already a chart decomposition with zero in-chart distortion. But the merging and the packing are
calls into [`Vixen.Geometry.Uv`](../Vixen.Geometry.Uv/), so there is one margin rule in the engine
rather than two.

## See also

- [`Vixen.Geometry`](../Vixen.Geometry/) — the mesh kernel, and the fourteen verbs the all-quad
  guarantee exists to keep working.
- [`Vixen.Geometry.Uv`](../Vixen.Geometry.Uv/) — the packer, and the unwrapper for meshes whose
  topology is the point and must not be touched.
- [docs/plan/41](../../docs/plan/41-automatic-retopology.md) — the design, the literature, and the
  licence table that decides it.
