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

## Singularities go on the corners, and not on the lines between them

⚠ **"Zero singularities on features" means zero on the *interiors* of the feature chains, and a cube
is why the distinction is not pedantry.** A singularity on a hard edge is a visible pinch and the
placement pass repels them from one; a singularity at a box's corner is exactly where an artist puts
one, and it is where the surface is genuinely not developable. On a cube every vertex is a feature
corner and every edge is a feature edge, and the Euler characteristic says eight quarter turns have
to exist somewhere — so reading the criterion as "off every feature vertex" makes the simplest
hard-surface shape unsatisfiable.

The placement pass is **monotone**: each of its three corrections scores the field before and after,
on how many singularities sit on feature lines and on how much turning there is in total, and puts
the field back unless it improved. It reaches zero on a cube and on a boolean of two boxes; on a
flight of stairs and on a box with a cylindrical bore it improves the count without reaching zero,
and the report says so rather than the code claiming otherwise.

## All-quad, and that is what makes it editable

⚠ **Quad-*dominant* is not good enough and the reason is downstream.** `MeshOperations` is built on
the assumption that a loop, a ring and a loop cut are statements about four-sided faces. A result
with triangles and pentagons in it has no rings to cut, and the mesh kernel's whole vocabulary stops
working on it. `NonQuadCount` is asserted zero.

## Quantization is a flow, not an integer program

Patch sides become arcs, the consistency constraints become conservation at the nodes, and "as close
as possible to the size the density field asked for" becomes the deviation cost. Two routers over the
one formulation: the exact one is a successive-shortest-path search with node potentials, the
approximate one routes by fewest arcs and is for interactive preview. Measured energies, exact against
approximate — 210 against 508 on a box, 132 against 255 on a sphere, 283 against 592 on a cylinder,
214 against 728 on a flight of stairs.

⚠ **A patch side may quantize to zero and that is legitimate** — it is how a five-sided patch becomes
four-sided, and the extraction merges that side's two ends into one output vertex. A patch whose whole
width or height comes to zero is a bug, and it is checked.

## What is measured today, and what is not

Every result is **100 % quads** on every fixture, and every feature polyline is reproduced at the order
the exit criterion asks for on straight hard surface — `5.15e-5` of the diagonal on a box, `2.42e-5`
on a plate with a hole, `9.88e-5` on a cylinder.

⚠ **`MeshReport.IsSolid` holds on a closed smooth surface and not yet on hard surface, and the quad
budget is overshot.** Both come from one place: the patches the separatrix tracing produces are longer
round than they are wide, which overshoots a quad budget *quadratically* — a patch's count is a
product of two side lengths — and leaves a handful of patches whose four sides do not line up. Those
are refused rather than emitted as a folded grid, so what is missing is holes rather than corruption,
and `RemeshReport.Warnings` counts them and says why. Compacting the partition is layout work, and it
is recorded here as owed rather than described as done.

## The output carries the input, or it is useless

Stage seven is what makes a remesh a pipeline rather than a demo. Normals, texture coordinates and
face groups are written back into the mesh; **vertex colours and skinning weights come back beside it**
in a `TransferResult`, because `EditMesh` has a normal layer and a coordinate layer and nothing else,
and growing it a colour channel and a bone channel would put a renderer's and a rig's vocabulary into
geometry types that have never heard of either.

⚠ **Face groups are decided by the majority of covered *area*, never by the nearest face.**
Nearest-face assignment shreds along a material boundary — every other quad flips — and the sawtooth
seam it leaves reads as a UV bug and gets debugged as one. Measured on a plane split on a straight
line with a fourteen-quad target: the area rule gives **14** boundary edges, which is the floor for a
straight cut, and the nearest-face rule gives **17**.

⚠ **Skinning weights are clamped to the target's influence limit, and the clamp is not optional.** A
target with room for four handed five silently loses one, and which one depends on the order the
interpolation accumulated them in. Sorted by descending weight with the bone index as the tie-break,
the survivor is a function of the input.

## The bake is a rasterizer this assembly had to grow

There is no CPU mesh-to-texture rasterizer in this repository. The two things that bake a mesh into a
texture are GPU-only and project along an *axis* rather than through a parameterization; the half-space
coverage rule that is reusable lives in `Vixen.Rendering`, one layer up, which the layering test
forbids referencing — **and it is pixel-centre only, which silently loses the outermost row of texels
of every chart in an atlas.** So `AtlasRaster` is conservative coverage by separating axes, written
here, and `MapBaker` casts along the output's interpolated normal both ways and takes the nearer hit.

⚠ **Content is rasterized in one pass and dilated in a second**, and the gutter only ever writes where
coverage is false — so one chart's dilation cannot overwrite the chart abutting it in the atlas.

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
