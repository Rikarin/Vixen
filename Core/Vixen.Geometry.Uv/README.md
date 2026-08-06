# Vixen.Geometry.Uv

UV unwrapping, as three problems rather than one button.
[docs/plan/42](../../docs/plan/42-uv-unwrapping.md).

```csharp
var charts = UvUnwrap.Charts(mesh, settings);              // where do we cut?
var islands = UvUnwrap.Flatten(mesh, charts, settings);    // how does a chart become flat?
var placed = UvUnwrap.Pack(islands, new() { Resolution = 2048, Margin = 4 });   // where does each sit?

var report = UvUnwrap.All(mesh, settings, out var uvs);    // and the common case
```

## Three problems, and conflating them is the first mistake

| Stage | The question | Fails as |
|---|---|---|
| **Charting** | *Where do we cut?* | Too many islands · seams across a face · seams that ignore the model's parts |
| **Flattening** | *How does a chart become flat?* | Stretch, angle distortion, **flipped triangles** |
| **Packing** | *Where does each island sit?* | Wasted atlas · bleeding at low mip levels · uneven texel density |

They have different literatures, different failure modes and different runtimes. ⚠ **A tool that
exposes only the fused verb cannot be debugged**, and — more practically — cannot serve the artist
who already cut their seams by hand and wants the islands rearranged. `Pack` takes *islands*, not a
mesh, and that is what makes it a peer of a standalone packer rather than an internal detail.

## Margin is in texels, so the packer is told the resolution

`PackSettings.Resolution` is required. ⚠ **A margin expressed as a fraction of UV space is a bug with
a two-year fuse**: it looks right at the resolution it was tuned at, and the same asset at half
resolution bleeds across islands at mip 3, in a build nobody associates with the packing change.
Bleeding is then misdiagnosed as a sampler problem roughly always.

Spacing is distributed *evenly across every chart* rather than applied per island as it is placed.
Uneven gaps read as carelessness in an atlas even when nothing bleeds.

## Chart count is an outcome, not a knob

The recursion splits only what fails `DistortionThreshold`, and a merge-back pass puts adjacent
charts together again wherever their union still passes. Growing regions until a stretch bound trips,
with nothing that ever puts two back together, is exactly why the established tools fragment — fifty
charts where a dozen would do.

## Coordinates are per corner

A seam is one shared position whose two sides carry different coordinates. That is free in
`EditMesh`'s corner layer and a vertex split in any per-vertex structure, so the arithmetic happens
where a seam costs nothing and the split happens at the very end, beside the code that uploads it.

⚠ **`MeshData` is deliberately out of reach.** It lives in `Vixen.Rendering`, one layer up. See
`UvLayeringTests`.

## What is measured

`UvReport` carries eleven fields, five of them taken from MeshTailor's metric set — chart count,
compactness as 4πA/P², convexity as A/A(hull), normalized seam length, and boundary jaggedness. ⚠ The
normalization is over **√area rather than area**, because the published definition is not
dimensionless: halve a model's scale and the figure doubles.

⚠ **`FlippedTriangles` must be zero.** It is a correctness field wearing a metric's clothes: a
flipped triangle is a region of the atlas where the mapping is not invertible, so a bake writes to
the wrong texel and sampling reads from it. A chart that cannot reach zero is subdivided rather than
shipped.

And packing efficiency is reported twice, before and after margin. Raw used-area-over-atlas-area
flatters a packer that leaves no room to bleed into; the gap between the two is what a margin setting
actually costs.

## The sparse solver is written here, because there was not one

`Solving/` is a compressed-sparse-row matrix, a preconditioned conjugate gradient with Jacobi and
incomplete-Cholesky(0), and a CGLS least-squares path that never forms `AᵀA`. Doc 42 § B1: nothing
sparse existed anywhere in the repository and no numerics package is referenced, which is why U1 is a
phase of its own. § D5 explains why it is conjugate gradient and not a sparse Cholesky — the
factorization is asymptotically better in a local–global loop and is a large piece of numerical
software to own, and CG **warm-started from the previous iterate** converges in very few steps
precisely because consecutive solves are close. `Solve` therefore takes the previous answer *in* the
array it writes the next one to.

All of it is `internal`. Nothing a caller of the three stages holds is a matrix.

⚠ **IC(0) does not exist for every SPD matrix.** A pivot can go non-positive — Kershaw's 4×4 is the
standard counterexample and it is in the tests. That is detected, falls back to Jacobi, and is
*reported*, because the failure is otherwise a NaN that arrives as a coordinate.

⚠ **A fixed budget still needs a floor at the limit of the arithmetic, and that is not the tolerance
§ D5 forbids.** Measured while writing it: a 3×2 least-squares system converged at iteration 4, sat
still until iteration 40, and then ran away to `1e+10` by iteration 80, because `beta = next / rho`
with both operands at the underflow floor is not a number. A budget of 64 — `SolverIterations`'
default — lands inside that. The floor sits where a `double` stops carrying information about the
residual, so which side of it a platform lands on cannot change the answer; a *quality* threshold
sits where the iteration is still making progress, and that one would. `ExhaustionFloorTests` is what
holds that distinction to account.

## The flattener is a ladder, and most charts stop on the first rung

`Flatten` takes a mesh and a chart per face. LSCM is one sparse least-squares solve and is exact on
anything developable; ARAP's local–global loop is paid for only when the first rung missed
`DistortionThreshold`; and a third pass works on the folded neighbourhood alone. The local–global
loop's matrix is a cotangent Laplacian that does not change between iterations, which is exactly the
case `ConjugateGradient`'s warm start exists for — the matrix, its incomplete Cholesky and both
solution arrays are built once, above the loop.

⚠ **The two rungs are not ordered by quality and the numbers say so.** On a slit sphere LSCM measures
1.04 angular against ARAP's 1.26, and 1.72 area against ARAP's 1.22. A conformal map wins on the
metric it optimizes and loses forty per cent on the one an artist sees, which is § D6's argument from
the inside.

| | angular | area | L² | L^∞ |
|---|---|---|---|---|
| sphere cut open | 1.2579 | 1.2219 | 1.0402 | 1.3830 |
| torus, slit both ways | 1.1981 | 1.1689 | 1.0277 | 2.2551 |
| hemisphere | 1.1882 | 1.1786 | 1.0251 | 1.1759 |
| saddle | 1.1631 | 1.1544 | 1.0222 | 1.4682 |
| cylinder, slit | 1.0000 | 1.0000 | 1.0000 | 1.0000 |
| flat 40:1 strip | 1.0000 | 1.0000 | 1.0000 | 1.0000 |

⚠ **A chart that is not a disk is refused before any solve runs.** An annulus, a handle, a chart in
two pieces and a bowtie all have *no* injective map to the plane, so producing coordinates for one
would be producing a fold with extra steps. The test is the Euler characteristic — `χ = 2 − 2g − b`,
one only for a disk — plus a separate check for the pinch χ cannot see, because two triangles meeting
at a single vertex give `5 − 6 + 2 = 1`.

⚠ **A cotangent goes negative on an obtuse triangle, and a Laplacian with a negative weight is not
guaranteed positive definite.** Conjugate gradient on an indefinite matrix does not throw and does not
diverge — it converges to a saddle and the chart comes back folded. Every weight is therefore raised
to a small positive floor relative to the chart's largest, which makes the matrix positive definite by
construction, and the count *and the worst cotangent* go in the report. Clamping to zero would remove
the edge and can disconnect a vertex; uniform weights mix two discretizations; mean-value coordinates
are the principled fix and are ruled out because their matrix is not symmetric. Intrinsic Delaunay
flipping is the upgrade a large reported cotangent would justify. `CotangentWeights` has the whole
argument.

## The packer, and what it measures

`Pack` is four rungs over one occupancy bitmap. The rectangle rung is a skyline over bounding boxes;
the irregular rung is the same skyline against each island's *rasterized underside*, so a concave
island settles onto a bump instead of resting a box on it; the super-patch rung groups
near-rectangular neighbours into one composite first; and everything past `CoreLimit` takes a tail
sweep that scans the skyline's steps rather than every column.

⚠ **Rasterized masks rather than no-fit polygons, and the reason is rotation.** An NFP needs a unique
polygon per *pair* per *orientation* — sixteen orientations of a thousand islands is a quarter of a
billion polygons. A bitmask overlap test is a word-wise `AND`, and it gets *more* accurate as the
atlas grows.

⚠ **The margin is grown into the mask once and never applied again.** The grid holds grown shapes and
a candidate is tested with its raw one, so the empty gap between two islands — and between an island
and the sheet's edge — is exactly `Margin`. Not half of it each, and not twice it because both of them
padded.

Measured on 422 irregular islands at 2048² with a four-texel margin:

| Rung | Texture delivered | Atlas consumed |
|---|---|---|
| Rectangle | 32.99 % | 85.08 % |
| **Irregular** | **52.96 %** | 68.30 % |

⚠ **`EffectiveEfficiency` cannot carry that comparison, and the reason is worth writing down.** It is
island *plus its margin band* over atlas area, so a bounding-box packer — whose band is drawn around a
box — consumes more of the sheet while delivering less of it. The figure that ranks packers is
`PackingEfficiency`; the effective one says what the margin cost, which is the 15 points between the
two columns.

## Deterministic

Same input, same settings, byte-identical coordinates, at any thread count on any platform. That
rules out the standard answers in the irregular-packing literature — no simulated annealing, no
genetic search, no random restarts — and it is why every solver here runs a **fixed iteration budget
rather than a residual tolerance**. A residual test is a floating-point comparison whose outcome can
differ across platforms.

Only the sparse multiply is parallel, and the dot products deliberately are not: doc 41 § D14 rules
out a floating-point reduction in a nondeterministic order, and a dot product split across threads is
exactly one. Each row of a multiply sums in ascending column order and writes only its own element,
so neither the worker count nor the batch size moves a bit.

⚠ **A fixed budget still needs a floor at the limit of the arithmetic, and that is not the tolerance
§ D5 forbids.** Measured while writing this: a 3×2 least-squares system converged at iteration 4, sat
still until iteration 40, and then ran away to `1e+10` by iteration 80, because `beta = next / rho`
with both operands at the underflow floor is not a number. A budget of 64 — `SolverIterations`'
default — lands inside that. The floor sits where a `double` stops carrying information about the
residual, so which side of it a platform lands on cannot change the answer; a *quality* threshold
sits where the iteration is still making progress, and that one would.

## See also

- [`Vixen.Geometry`](../Vixen.Geometry/) — the mesh kernel this unwraps.
- [`Vixen.Geometry.Remeshing`](../Vixen.Geometry.Remeshing/) — one of its callers, and the only
  assembly in `Core/` allowed to depend on this.
- [docs/plan/42](../../docs/plan/42-uv-unwrapping.md) — the design, and the references it is drawn
  from.
