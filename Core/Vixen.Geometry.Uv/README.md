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
