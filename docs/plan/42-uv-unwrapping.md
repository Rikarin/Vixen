<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 42 — UV unwrapping: seams, charts and packing

⚠️ **Amends [41](41-automatic-retopology.md) § D13 and extends [24](24-blockout-tools.md),
[40](40-ai-assisted-material-generation.md) and [08](08-asset-pipeline-and-addressables.md).** Doc 41
closed doc 40's retopology gap and left the other half of it standing on purpose: its § D13 derives an
atlas from the *patch layout the remesher already computed*, and says in as many words that this "is
not a general unwrapper — it unwraps meshes this remesher produced". That was the correct scope for a
remeshing document. It is not enough for the pipeline, because the meshes that most need UVs are
exactly the ones nobody remeshed: an imported model, a boolean result, a generated blob a texture is
about to be baked onto.

**The claim this document has to earn.** Any triangle mesh — imported, generated, booleaned, scanned —
gets a UV atlas: seams cut where an artist would cut them, charts flattened with bounded distortion,
islands packed tightly at a stated texel density with a margin measured in *texels rather than in UV
units*. It runs headless in the content build, it is deterministic, and each of its three stages is
usable on its own — so *"just repack these islands"*, which is what UV-Packer does and all it does,
is a supported entry point rather than a side effect.

⚠ **The licence position here is the opposite of doc 41's, and it changes the plan.** In quad
remeshing everything good was GPL and the one permissive implementation was the weakest. In
unwrapping, the two reference implementations — **xatlas and BFF are both MIT** — are permissive, and
the closed-off work is the recent *learned* seam models. So this document leans on published,
readable, permissively-licensed prior art far more than doc 41 could.

---

## Part 0 — It is three problems, and conflating them is the first mistake

| Stage | The question | Fails as |
|---|---|---|
| **Charting / seams** | *Where do we cut?* | Too many islands · seams across a face · seams that ignore the model's parts |
| **Flattening** | *How does a chart become flat?* | Stretch, angle distortion, **flipped triangles** |
| **Packing** | *Where does each island sit?* | Wasted atlas · bleeding at low mip levels · uneven texel density |

⚠ **They have different literatures, different failure modes and different runtimes, and a tool that
exposes only the fused verb cannot be debugged.** Blender's Smart UV Project and xatlas both do all
three behind one button; UV-Packer does only the third, and is popular *because* of that — an artist
who has cut seams by hand wants those seams kept and the islands rearranged.

⚠ **The tool the request named is the third stage.** UV-Packer (3d-io) takes islands that are already
unwrapped and rearranges them to fill the sheet, distributing spacing evenly across all charts, and its
selling point is throughput — thousands of islands and millions of polygons. That is a real product and
a real need, and this document treats it as a **first-class entry point**
([D1](#d1-three-stages-three-artefacts-and-each-one-callable-alone)) rather than as the tail of a
pipeline. But it is not an unwrapper, and supplying only it would leave a generated mesh exactly where
doc 40 § D6 left it.

---

## Part 1 — The references

### MeshTailor, read in full

*Cutting Seams via Generative Mesh Traversal.* Xueqi Ma, Xingguang Yan, Congyue Zhang, Hui Huang
(Shenzhen University · Simon Fraser University). [arXiv:2603.27309v1](https://arxiv.org/abs/2603.27309),
28 March 2026. Paper text under CC BY-NC-ND 4.0.

The method formulates seam placement as **autoregressive traversal of the mesh graph**:

| Piece | What it is |
|---|---|
| `ChainingSeams` | Turns an unordered seam set into a deterministic vertex-walk sequence, ordered **loops-first, balance-first, large-patch-first** — global structural cuts before local refinement |
| Dual-stream encoder | GraphSAGE over connectivity, plus a frozen point-cloud encoder over 2,048 sampled points, fused by cross-attention |
| Mesh-native pointer network | A transformer that, at each step, picks the next vertex — ⚠ **masked to the 1-ring of the current vertex**, so every seam is edge-aligned by construction |
| Training | Negative log-likelihood against seams extracted from professional UV layouts |

Reported against GarmentCodeData: **10.4 charts against xatlas's 51.6 and Blender's 74.3**, at
distortion of 1.097 against xatlas's 1.064 — an order-of-magnitude reduction in fragmentation for
roughly nothing in distortion — with better island compactness and markedly smoother boundaries, and a
100-participant user study preferring it on all three axes it asked about.

**Three things in it are worth taking, and one of them is not the network.**

⚠ **1 · The central insight is a statement about representation, not about learning.** The paper's own
comparison says SeamGPT predicts *coordinates* that must then be snapped to mesh edges, and that
snapping is where the artefacts come from; the fix is to make the output space *be* the mesh graph. That
is the same lesson doc 41 § D4 arrived at from the other direction — a feature line that is reproduced
by construction beats one that is approximated and then snapped — and it applies just as forcefully to
a hand-written tracer. [D4](#d4-a-seam-is-a-walk-on-the-mesh-graph-and-that-is-true-without-a-network)
takes it.

⚠ **2 · The metric set is the best-specified one in the literature, and it becomes our gate.** Chart
count, island compactness as 4πA/P², convexity as A/A(hull), normalized seam length, and *boundary
jaggedness* as a discrete-curvature proxy on resampled UV boundary loops. Nobody else writes down the
last two, and they are exactly what separates "low distortion" from "an artist would accept this".
[Part 4](#part-4--what-the-report-says) adopts all five.

⚠ **3 · The canonical ordering is a usable heuristic on its own.** *Loops first, balance first, large
patches first* is a good deterministic priority for a tracer, and it needs no model to apply.

**And the network itself falls under doc 41 § D19, cleanly.** It is trained on **300 K part-level
samples from TexVerse and 110 K from GarmentCodeData**, with ground truth extracted from professional
UV layouts through Blender's seam operator — which is the artist-authored corpus doc 41 § D19 said was
the thing we actually want and cannot license. No code or weights release is stated, and the paper is
CC BY-NC-ND. So: **an optional plugin-tier proposer at most** ([D14](#d14-a-learned-seam-proposer-is-a-plugin-that-proposes-and-never-decides)),
never the path.

⚠ **One correction to the paper, offered as a checkable observation.** Normalized seam length is defined
as total seam length divided by surface *area*, which is not dimensionless — halve a model's scale and
the figure doubles. [Part 4](#part-4--what-the-report-says) reports length over √area beside it, so the
number compares across models.

### PartUV, which is the closer template

*PartUV: Part-Based UV Unwrapping of 3D Meshes*, SIGGRAPH Asia 2025
([arXiv:2511.16659](https://arxiv.org/abs/2511.16659)). A **top-down recursive** pipeline: a part
hierarchy from PartField, then two geometric charting strategies per node, flatten with ABF, and
compare against a user-specified distortion threshold τ — **under threshold, accept; over, recurse into
the children**. It handles non-manifold and degenerate input deliberately, is parallelized, and
completes in tens of seconds.

⚠ **It is explicitly aimed at AI-generated meshes** — "noisy, bumpy and poorly conditioned" — and it
reports that Nuvo and OptCuts, the optimization-based competition, take thirty minutes to several
hours. That runtime gap is the whole argument for the recursive-threshold shape over joint
cut-and-distortion optimization.

⚠ **The one part we cannot take is the top of it.** PartField is a learned decomposition;
[D3](#d3-charting-is-a-distortion-driven-recursion-over-a-decomposition-that-is-pluggable) keeps the
recursion and makes the decomposition pluggable, with a classical concavity-driven default.

### The rest of the field

**Charting.** xatlas and Microsoft's UVAtlas both grow regions greedily against a stretch metric and
flatten with LSCM — fast, robust, and **fragmented**: 51.6 charts where MeshTailor gets 10.4. Seamster
(2002) established the other idea worth having, that a seam should be *inconspicuous* — routed through
concave, occluded regions where a texture discontinuity does not read. OptCuts and Autocuts optimize
cuts and distortion jointly and give consolidated charts with distorted, semantically arbitrary
boundaries, at a runtime nobody can put in a content build.

**Flattening.** LSCM and ABF++ are conformal, fast, and **do not guarantee injectivity**; ARAP's
local/global iteration improves rigidity and can still fold; symmetric-Dirichlet with SLIM's
local/global solve is the strong academic baseline; Progressive Parameterizations reports far fewer
iterations than SLIM, AKVF and CM; BFF flattens through boundary data with cone singularities and is
**MIT**.

**Packing.** Rectangle bin packing over island bounding boxes is what Blender ships and it wastes
whatever the bounding box wastes. Irregular packing is NP-hard and is attacked three ways — no-fit
polygons (unique NFP per pair *per rotation*, so (m·n)² of them, which is why high rotational freedom
kills it), raster/bitmask overlap tests, and metaheuristics. UVPackmaster's published shape is the
practical compromise: an O(N³)-ish core capped at about 1024 islands with an O(N log N) pass sweeping
the remaining tiny ones around the edges. *Learning Based 2D Irregular Shape Packing* (TOG 2023) groups
islands into near-rectangular **super patches** to reduce the problem to bin packing, for a 5–10 %
improvement over xatlas and NFP baselines — and ⚠ **the grouping idea survives without the learning.**

### The licence table

| Artefact | Licence | Link it? | Read it? |
|---|---|---|---|
| **xatlas** | **MIT** | ✅ Yes | ✅ Yes |
| **BFF** | **MIT** | ✅ Yes | ✅ Yes |
| libigl (core) | MPL2 | ✅ Yes | ✅ Yes |
| MeshTailor | Paper CC BY-NC-ND; no stated code/weights | ⛔ | ✅ Yes |
| PartUV / PartField | Learned component, terms not established here | ⛔ Assume no | ✅ Yes |
| Papers generally | — | — | ✅ **Algorithms are not licensed** |

⚠ **This is still a clean-room C# implementation**, for the same reason doc 41 gave: a native
dependency is a native dependency whatever its licence, and ADR-015 already keeps import-time C++ out
of the runtime. But unlike doc 41, we are reading permissively-licensed reference code rather than
inferring from papers alone, and xatlas in particular is the behaviour to match and beat.

---

## Part 2 — What blocks it

### B1. There is no sparse linear solver anywhere in the repository ⛔

LSCM, ABF, ARAP, SLIM and BFF are all, underneath, sparse symmetric positive-definite systems. A grep
across `Core/` for a sparse matrix, a Cholesky factorization, a conjugate-gradient iteration or a least-squares
solve returns nothing, and `Directory.Packages.props` has no numerics package among its 52 entries.

⚠ **This is the single largest unbudgeted item in the document**, and it is the reason U1 exists as a
phase of its own. [D5](#d5-flattening-is-a-ladder-and-the-solver-under-it-is-conjugate-gradient-with-a-warm-start)
says what gets built and why it is a preconditioned conjugate gradient rather than a sparse Cholesky —
which is a real engineering trade, not a shortcut.

### B2. `GlyphAtlas` is a packer, and it is the wrong one 🟡

`Core/Vixen.Ui.Text/Rasterizing/GlyphAtlas.cs` is a shelf packer, and its own remarks are honest about
what that costs — it opens rows as tall as their first glyph and notes a skyline packer would waste
less. It is a good precedent for *how to write one here*, and it is unusable for this: glyphs are
axis-aligned rectangles arriving one at a time, UV islands are irregular polygons known all at once.
⚠ **Different problem, and the shelf packer's rectangle assumption is exactly the one
[D7](#d7-packing-is-a-ladder-too-and-the-honest-metric-is-efficiency-after-margin) has to beat.**

### B3. The engine has exactly one UV channel ⛔ *for a second set, which we are not adding*

`SurfaceVertex.TexCoord` is one `Vector2` and `MeshData.TexCoords` is one array. A second UV set would
touch the vertex layout, the RHI input description, the compiled mesh format and every shader that
reads a texture.

⚠ **And we do not need one**, which is worth stating because in most engines the unwrapper exists *for*
the second channel. Doc 19 replaced baked lightmaps with a Lumen-shaped dynamic path, and its surface
cache is built on **six-axis cards** — `CardGenerator` projects, it does not unwrap. **So nothing in
Vixen's lighting wants a lightmap UV**, and this document's output goes to UV0, for texturing and
baking. A second channel is out of scope with a reason rather than by omission.

### B4. Margin is in texels and packing happens in UV units 🟡

An island packed with a margin expressed as a fraction of UV space has a *different* pixel gap at 512²
than at 4096². Bleeding at low mip levels is the symptom, it appears late, and it is misdiagnosed as a
sampler problem roughly always. ⚠ **The packer therefore has to know the atlas resolution**, which
sounds obvious and is the single most commonly wrong thing in packing implementations
([D8](#d8-margin-is-in-texels-so-the-packer-takes-the-resolution)).

### B5. AI-generated input is degenerate, and doc 41 already solved that ✅

Non-manifold edges, zero-area triangles, floating debris and duplicate faces break every
parameterization in the list. PartUV calls out dedicated handling of exactly this as one of its
contributions. ⚠ **Doc 41 § D3's conditioning stage is that handling, already specified and already
budgeted** — so this document *reuses* it rather than restating it, and an unwrap of a raw generated
mesh runs conditioning first.

### B6. Determinism, again 🟡

The same gate as doc 41 § D14, for the same reason — the content hash, and a golden that must not move.
Iterative solvers with convergence-tolerance stops, randomized restarts and metaheuristic packers
(simulated annealing, genetic algorithms) are all excluded by it, and that exclusion shapes
[D5](#d5-flattening-is-a-ladder-and-the-solver-under-it-is-conjugate-gradient-with-a-warm-start) and
[D7](#d7-packing-is-a-ladder-too-and-the-honest-metric-is-efficiency-after-margin).

---

## Part 3 — The design

### D1. Three stages, three artefacts, and each one callable alone

```
  triangles ──▶ ① Chart ──▶ ② Flatten ──▶ ③ Pack ──▶ UVs
                    │            │            │
              chart ids     per-chart      island
              + seam set    2-D coords     transforms
```

Every arrow is a public entry point, and every intermediate is a value a caller can hold, inspect and
hand back:

| Entry point | Who wants it |
|---|---|
| `UvUnwrap.Charts(mesh, settings)` | Someone who wants the seams and will flatten elsewhere |
| `UvUnwrap.Flatten(mesh, charts)` | Someone whose charts came from doc 41's patch layout, or from an artist |
| **`UvUnwrap.Pack(islands, settings)`** | ⚠ **UV-Packer's job, and the request's** — islands in, transforms out, nothing re-cut |
| `UvUnwrap.All(mesh, settings)` | The importer, and the common case |

⚠ **`Pack` taking *islands* rather than a mesh is what makes it a peer of UV-Packer rather than an
internal detail.** An artist who cut seams by hand in Blender and wants them respected is the exact
case that a fused-verb unwrapper cannot serve, and it is most of why UV-Packer exists.

### D2. `Core/Vixen.Geometry.Uv`, and doc 41's atlas becomes its client

A new assembly beside doc 41's, referencing `Vixen.Geometry`, `Vixen.Core.Mathematics` and
`Vixen.Core.Threading`. The layering test asserts it, as `RemeshingLayeringTests` does.

⚠ **This amends doc 41 § D13.** That section derives an atlas from the remesher's patch layout, and
correctly — a quantized quad patch *is* a rectangle, and throwing that away to re-cut it with a general
charter would be worse in every respect. What changes is the second half: **doc 41's super-chart
merging and rectangle packing become calls into `Vixen.Geometry.Uv`'s packer** rather than code of its
own. The remesher keeps its privileged charting; it stops owning a second packer.

Direction of reference: **`Vixen.Geometry.Remeshing` → `Vixen.Geometry.Uv`**, never the reverse. The
unwrapper must run on meshes nobody remeshed, which is the entire point.

### D3. Charting is a distortion-driven recursion over a decomposition that is pluggable

PartUV's shape, with its learned top swapped for something we own:

1. **Decompose** into candidate regions. The default is classical and concavity-driven — approximate
   convex decomposition over the dual graph, weighted by dihedral concavity and the shape diameter
   function, which is the same family Seamster drew on to find inconspicuous cuts. Material and
   face-group boundaries (`MeshFace.Group`) partition first and unconditionally.
2. **Flatten** the region and measure distortion.
3. **Accept or recurse.** Under the threshold τ, keep it; over, split and repeat.
4. **Merge back.** Adjacent charts whose union still meets τ are merged, greedily, largest first.

⚠ **Chart count is an outcome of a quality target, not a knob**, and that inversion is what produces
few large charts instead of many small ones. xatlas's fragmentation is the direct consequence of
growing regions until a stretch bound trips, with nothing that ever puts two back together — step 4 is
the cheap half of the fix and step 3's top-down direction is the expensive half.

⚠ **The decomposition is an interface**, so a learned part field can be dropped in behind it under
[D14](#d14-a-learned-seam-proposer-is-a-plugin-that-proposes-and-never-decides)'s rules — and the
default path never calls it.

### D4. A seam is a walk on the mesh graph, and that is true without a network

MeshTailor's representational point, taken directly: seam candidates are **paths of existing edges**,
found by shortest-path search on the dual/primal graph under an edge cost, never by placing a curve in
space and snapping. There is no snapping stage, so there are no snapping artefacts.

The edge cost is where "where would an artist cut" is actually encoded:

| Term | Prefers a seam that… |
|---|---|
| **Concavity** | sits in a crease that folds inward — Seamster's inconspicuousness |
| **Visibility** | is occluded, estimated by ambient occlusion over the surface |
| **Feature alignment** | follows a hard edge or a crease, where a normal-map discontinuity is invisible anyway |
| **Material boundary** | runs where the texture already changes |
| **Symmetry** | lies on the mirror plane, so the two halves' seams agree exactly |
| **Length** | is short, which is the term everything else is traded against |
| **Existing seams** | was already there, when re-unwrapping a mesh that had UVs |

Chains are ordered **loops first, balance first, large patches first** — MeshTailor's canonical
ordering, used as a deterministic priority.

### D5. Flattening is a ladder, and the solver under it is conjugate gradient with a warm start

Three rungs, tried in order, each one only paid for when the one below fails its bound:

1. **LSCM** — one sparse least-squares solve. Fast, conformal, no injectivity guarantee.
2. **ARAP / symmetric-Dirichlet, local–global** — initialize from LSCM, iterate. Penalizes stretch *and*
   compression, which conformal energies do not.
3. **Bijective repair** — for charts that still fold, a progressive/injectivity-preserving pass on the
   flipped neighbourhood only, and if that fails, **split the chart and recurse**, which is
   [D3](#d3-charting-is-a-distortion-driven-recursion-over-a-decomposition-that-is-pluggable)'s loop
   doing its job.

⚠ **A flipped triangle is a correctness failure, not a quality one.** It is a region of the atlas where
the mapping is not invertible: bakes write to the wrong texel and sampling reads from it. The count is
asserted zero, per chart, and a chart that cannot reach zero is subdivided rather than shipped.

**The solver, which B1 says has to be written.** A preconditioned **conjugate gradient** over a
compressed-sparse-row matrix, with Jacobi and incomplete-Cholesky preconditioners, and a **fixed
iteration budget** rather than a residual tolerance.

⚠ **The textbook argument is for a sparse Cholesky and it is worth saying why we are not writing
one.** In a local–global iteration the system matrix is constant and only the right-hand side moves, so
a factorization computed once and back-substituted per iteration is asymptotically better. But a
supernodal sparse Cholesky with a fill-reducing ordering is a large, subtle piece of numerical software,
and CG **warm-started from the previous iterate** converges in very few iterations precisely because
consecutive solves are close. That trades an asymptotic win for a large reduction in what has to be
built and maintained, and ⚠ **the fixed iteration budget is also what makes the solve deterministic** —
a residual test is a floating-point comparison whose outcome can differ across platforms, which is
exactly the class of bug [B6](#b6-determinism-again-) exists to prevent. If profiling later says the
factorization is worth it, that is a contained change behind one interface.

⚠ **The cotangent Laplacian and the conjugate gradient disagree on the first obtuse triangle, and
this section did not say so.** Added by U2, which had to decide. A cotangent goes negative past 90°,
the Laplacian is positive semi-definite only when every weight is non-negative, and CG is valid only
on a definite one. The failure is silent in the worst way: an indefinite system does not throw and does
not diverge, it converges to a **saddle**, and the chart comes back folded with nothing anywhere naming
the triangle. **The decision is to raise every weight to a small positive floor relative to the chart's
largest**, which makes the matrix positive definite by construction rather than usually, and to report
both the count and the *most negative cotangent* — the count alone does not discriminate, because an
ordinary quad grid over a hemisphere produces one negative weight per quad and so does a strip of 170°
slivers. Clamping to zero was rejected because a zero weight removes the edge and can disconnect a
vertex, which `ConjugateGradient` then masks out and leaves wherever the initialization put it;
uniform weights were rejected because they mix two discretizations and make the obtuse triangle the
stiffest thing in the chart; **mean-value coordinates are the principled fix and are ruled out by the
solver rather than by taste — their matrix is not symmetric**, and every line of
[B1](#b1-there-is-no-sparse-linear-solver-anywhere-in-the-repository-)'s CG assumes it is. Intrinsic
Delaunay flipping loses nothing and is the upgrade a large reported cotangent would justify; it is a
phase of its own.

⚠ **Rung 1 is harder to fold than "no injectivity guarantee" suggests, and rung 3 has to be tested
anyway.** U2 could not construct an input a *free-boundary* LSCM folds: not a sphere with a hairline
slit, not a hyperbolic fan, not a strip of 170° slivers, not a forty-to-one ribbon. The guarantee
really is absent and the failure is rare, which is a reason to keep the rung ordering and **not** a
reason to skip covering the third rung — a rung reached by nothing is a rung nobody has run, so the
repair pass is tested against a fold injected on purpose.

⚠ **A chart that is not a disk is refused before any solve runs, and the test is the Euler
characteristic.** An annulus, a handle, a chart in two pieces and a bowtie have *no* injective map to
the plane, so producing coordinates for one is producing a fold with extra steps. `χ = 2 − 2g − b` is
one only for a disk, which catches genus and boundary in one number — a boundary-loop count passes a
torus with a single hole. It is blind to a pinch, where two fans meet at one vertex and give
`5 − 6 + 2 = 1`, so that is checked separately.

### D6. Distortion is measured four ways, because the failures are different

Angular (conformal) and area (authalic) distortion, the L² and L^∞ stretch of Sander et al., and the
flipped-triangle count. ⚠ **Reporting one number hides the failure that matters**: a conformal map can
have perfect angles and a 40× area ratio between two ends of a chart, which shows up as a texture that
is sharp on the shoulder and mush on the hand, and an angle-only metric calls it excellent.

### D7. Packing is a ladder too, and the honest metric is efficiency *after* margin

| Rung | Method | For |
|---|---|---|
| **Rectangle** | Skyline over island bounding boxes, with 90° rotation | The fast path, and the fallback |
| **Irregular** | Rasterized island masks at a chosen texel scale; placement by scanline against an occupancy bitmap; a fixed rotation set | The quality path |
| **Super-patch** | Group near-rectangular neighbours into composite rectangles, then pack those | The TOG 2023 idea, without the learning |
| **The tail** | Thousands of tiny islands swept into leftover gaps by area, descending | UVPackmaster's shape: the expensive core is capped, the tail is O(N log N) |

⚠ **Rasterized masks rather than no-fit polygons, and the reason is rotation.** NFP needs a unique
polygon per pair *per rotation* — (m·n)² of them — so a packer that wants sixteen orientations of a
thousand islands is computing a quarter of a billion polygons. A bitmask overlap test at texel
resolution is trivially parallel, trivially deterministic, and gets *more* accurate as the atlas gets
bigger, which is the direction the problem actually goes.

⚠ **No simulated annealing, no genetic algorithm, no random restarts.** They are the standard answers in
the irregular-packing literature and every one of them is excluded by
[B6](#b6-determinism-again-). The ordering is by descending area with an explicit index tie-break, and
the same islands pack the same way every time.

**And the metric is efficiency after margin.** Raw used-area-over-atlas-area flatters a packer that
leaves no room to bleed into. The report gives both, and the gap between them is what a margin setting
actually costs.

⚠️ **Corrected once U4 was measured, and the correction reverses which of the two ranks a packer.**
`EffectiveEfficiency` counts an island *and its reserved band* against the sheet, so it answers "how
much of the atlas is spoken for" — and a bounding-box packer, whose band is drawn around a box rather
than around a silhouette, therefore scores **higher on it while delivering less texture**. Measured on
422 irregular islands at 2048² with a four-texel margin:

| Rung | `PackingEfficiency` — texture delivered | `EffectiveEfficiency` — atlas consumed |
|---|---|---|
| Rectangle | 32.99 % | **85.08 %** |
| Irregular | **52.96 %** | 68.30 % |

So the sentence above is right that raw efficiency flatters a *zero-margin* packer, and wrong to
conclude from that that the after-margin figure is the one to rank on. **`PackingEfficiency` is what
ranks packers**; `EffectiveEfficiency` says what the margin cost, which is the fifteen points between
the two columns of the winning row. [Exit criterion 3](#exit-criteria-measured) is restated on that
basis.

### D8. Margin is in texels, so the packer takes the resolution

`PackSettings.Resolution` is required, and `Margin` is an integer count of texels. The packer converts
once. ⚠ **A margin in UV units is a bug with a two-year fuse**: it looks right at the resolution it was
tuned at, and the same asset at half resolution bleeds across islands at mip 3 in a build nobody
associates with the packing change. Doc 41 § D12's bake writes into this atlas, and a bake that bleeds
is a bake that is wrong.

Spacing is also distributed *evenly across all charts* rather than applied per island as it is placed —
which is UV-Packer's stated behaviour and is the visibly better one, because uneven gaps read as
carelessness in the atlas even when nothing bleeds.

### D9. Texel density is a constraint, not an observation

Islands are scaled to a uniform texels-per-metre by default, with a per-material override and a
per-chart multiplier for regions that deserve more. ⚠ **The default has to be uniform, because
non-uniform density is invisible in the atlas and glaring in the game** — the classic symptom being a
character's face at half the resolution of their boots. The report gives the achieved density's min,
max and variance, so "did the packer quietly rescale something" is answerable.

### D10. Stacking and mirroring are opt-in, and doc 41 makes them detectable

Symmetric islands can be *deliberately overlapped* so both halves share one region of texture, halving
the atlas cost. It is off by default — it forbids asymmetric detail, and discovering that after
texturing is expensive.

⚠ **Doc 41 § D11's exact symmetry is what makes detection reliable.** A mesh remeshed with symmetry on
has vertex *k* and its mirror as exact negations, so matching islands is an equality rather than a
tolerance search. On an arbitrary mesh the match is approximate and is offered rather than applied.

### D11. UDIM is a tiling of the packer, not a second implementation

When the islands do not fit at the requested density, the packer either scales down or spills into the
next tile, and which one is the caller's choice. Tiles are integer offsets in UV space, so the
atlas-relative machinery is untouched. ⚠ **The one real constraint is that an island may not straddle a
tile boundary**, which is a placement rule and not a new packer.

### D12. Determinism, and the report

Same gate as doc 41 § D14: same input and settings, byte-identical UVs, any thread count, any platform.
Enforced by the fixed CG budget, the deterministic packing order, and no metaheuristics anywhere.

### D13. Where it is invoked from

| Surface | Shape |
|---|---|
| **Importer** | `ModelImportSettings.Unwrap` — generate when the source has no UVs, or always, or never |
| **CLI** | `vixen unwrap in.glb out.glb --resolution 2048 --margin 4 --density 512` and `vixen uv pack …` for the third stage alone |
| **Doc 41** | Its § D13 atlas calls this packer; its § D12 bake writes into the atlas this produces |
| **Doc 40** | The retexture panel's UV-less refusal ends here rather than at "remesh it first" — a mesh whose topology is fine but whose UVs are missing needs *this*, not a remesh |
| **Doc 24** | `BlockoutHandoff`'s bake and export, so a blockout leaves with real UVs instead of box projection |
| **Editor** | A UV panel: islands, distortion as a heat map, seam display, and the three verbs separately |

### D14. A learned seam proposer is a plugin that proposes and never decides

MeshTailor-class models predict seam chains that are edge-aligned by construction, which is the right
output space, and their chart counts are genuinely better than anything deterministic on the table.
Under doc 41 §§ D18–D19's rules, unchanged:

1. **A plugin**, with its own ONNX Runtime. `Core/` gains nothing.
2. **It proposes and never decides.** Its chains become candidate cuts with a strong prior in
   [D4](#d4-a-seam-is-a-walk-on-the-mesh-graph-and-that-is-true-without-a-network)'s cost; the
   deterministic charter still runs, and τ still has the last word. A bad prediction costs chart
   quality, never validity.
3. **The proposal is in the content hash**, model version included.
4. **No third-party training data.** ⚠ And here that rule bites hardest: MeshTailor's ground truth is
   *professional UV layouts* from TexVerse and GarmentCodeData — precisely the artist-authored corpus
   doc 41 § D19 identified as the thing we want and cannot license. There is no synthetic substitute
   for "where would a human have cut this", so a Vixen-trained seam model is **not** a thing this
   document expects to exist.

⚠ **Which makes this the honest position: if a permissively-licensed seam model with released weights
appears, the plugin seam is here to take it. We are not going to train one.**

---

## Part 4 — What the report says

`UvReport`, adopting MeshTailor's five and adding what a renderer needs:

| Field | Why |
|---|---|
| `ChartCount` | The headline. Fewer is better at equal distortion |
| `Compactness` — 4πA/P² | Round islands pack and filter better than tendrils |
| `Convexity` — A/A(hull) | The other half of shape quality |
| `SeamLength`, `SeamLengthNormalized` | Total, and ⚠ over √area rather than over area, so it is scale-free |
| `BoundaryJaggedness` | The metric that separates "low distortion" from "an artist would accept it" |
| `AngularDistortion`, `AreaDistortion`, `StretchL2`, `StretchLInf` | [D6](#d6-distortion-is-measured-four-ways-because-the-failures-are-different) |
| `FlippedTriangles` | **Must be zero.** A correctness field wearing a metric's clothes |
| `PackingEfficiency`, `EffectiveEfficiency` | Before and after margin |
| `TexelDensity` — min, mean, max, variance | [D9](#d9-texel-density-is-a-constraint-not-an-observation) |
| `Stages` — timings | Which of the three was slow |

---

## Part 5 — Phases

### U0 — The spike · 0.5 EM

LSCM plus a skyline packer on three inputs — an imported character, a boolean-heavy blockout export and
a raw generated mesh. **What it answers:** whether a hand-written CG solve holds its timing at a few
hundred thousand triangles, which is [B1](#b1-there-is-no-sparse-linear-solver-anywhere-in-the-repository-)'s
open question and the one that can reorder everything after it.

### U1 — The assembly and the sparse solver · 1.0 EM

`Core/Vixen.Geometry.Uv`, the layering test, CSR storage, preconditioned CG with Jacobi and incomplete
Cholesky, and the determinism harness around it. ⚠ **Everything else is blocked on this**, which is why
it is not folded into U2.

### U2 — Flattening · 1.25 EM ✅

LSCM, then ARAP/symmetric-Dirichlet local–global, the bijectivity check, the repair pass, and the four
distortion measures. Tested per-chart against known-hard inputs.

Landed. The local step is ARAP's closest rotation rather than the symmetric Dirichlet's, and the
difference is a line search: a barrier against inversion needs one, a line search is a floating-point
comparison deciding how many steps to take, and that is [B6](#b6-determinism-again-)'s excluded class.
The barrier's job is done instead by the flip count refusing the chart. ⚠ **A latent blocker turned up
on the way**: `EditMesh.Normal` delegated to `Vector3.Normalize`, which carries an absolute
`MathUtil.ZeroTolerance` of `1e-6` — so a face whose Newell sum was smaller reported *no* normal, and
`Triangulate` reads a missing normal as "no plane to ear-clip in" and fans instead. One surface
therefore triangulated two different ways at two model scales, which is two different unwraps of it.
Fixed in `EditMesh` by dividing by its own length, which is what that line's own comment already said
it did.

### U3 — Charting and seams · 1.75 EM

Concavity decomposition, the recursive τ loop, the merge-back pass, graph-walk seam tracing on the
seven-term cost, and the canonical ordering. ⚠ **The phase that decides whether the output looks
professional**, and the one to measure against xatlas's 51.6 charts.

### U4 — Packing · 1.5 EM

The four rungs, texel-margin conversion, even spacing, and the efficiency pair. ⚠ **Blocked only on
U1**, not on U2 or U3 — packing takes islands, and islands can come from doc 41, from an artist or from
a file. **This is UV-Packer parity, and it can land before the unwrapper does.**

**— cut line: an unwrapper end to end, and a standalone packer. 6.0 EM —**

### U5 — The report and the determinism gate · 0.75 EM

`UvReport` in full, the debug dumps per stage, and byte-identical output across thread counts and
platforms.

### U6 — Density, stacking and UDIM · 0.75 EM

Uniform texel density, per-material override, symmetric stacking, multi-tile spill.

### U7 — The surfaces · 0.75 EM

The importer setting, both CLI verbs, the editor UV panel with its distortion heat map, and the doc 40,
41 and 24 wiring.

### U8 — The learned seam proposer · 1.0 EM · optional, and unlikely

[D14](#d14-a-learned-seam-proposer-is-a-plugin-that-proposes-and-never-decides). ⚠ **Only worth opening
if somebody releases permissive weights** — we are not training one, for the reason D14's fourth rule
gives.

### Cost

| Phase | EM | Blocked on |
|---|---|---|
| U0 — The spike | 0.5 | Nothing |
| U1 — Assembly and solver | 1.0 | U0 |
| U2 — Flattening | 1.25 | U1 |
| U3 — Charting and seams | 1.75 | U2 |
| U4 — Packing | 1.5 | **U1 only** — parallel with U2 and U3 |
| — | **6.0** | **the cut line — an unwrapper, and a standalone packer** |
| U5 — Report and determinism | 0.75 | U3, U4 |
| U6 — Density, stacking, UDIM | 0.75 | U4 |
| U7 — The surfaces | 0.75 | U5 |
| | **8.25** | |
| U8 — Learned seam proposer | 1.0 | U3. ⚠ Optional, and gated on somebody else's licence |

⚠ **U4 running parallel to U2–U3 is the schedule's one real lever**, and it happens to deliver the
thing the request named first.

---

## Part 6 — Where this lands against the alternatives

| | xatlas | Blender Smart UV | UV-Packer | This |
|---|---|---|---|---|
| Charts on the same input | 51.6 | 74.3 | *(does not chart)* | **τ-driven, merged back** — the target is MeshTailor's order of magnitude |
| Seam quality | Growth-bound artefact | Growth-bound artefact | — | Seven-term cost: concavity, occlusion, features, materials, symmetry |
| Flattening | LSCM | ABF/LSCM | — | Ladder to ARAP, **zero flipped triangles asserted** |
| Packing | Rectangle | Rectangle | **Irregular, fast, even spacing** | Four rungs, and even spacing taken from UV-Packer |
| Margin | UV units | UV units | Exact spacing | **Texels, resolution required** |
| Texel density | — | — | — | A constraint, and reported |
| Report | — | — | — | Eleven fields, five of them MeshTailor's |
| Determinism | Not claimed | Not claimed | Not claimed | **Gated in CI** |
| In-process | Native lib | In Blender | External binary + addon | `Core/`, headless, one call |

And the honest rows:

⚠ **xatlas is fast, robust, MIT and has been hammered on by thousands of projects.** Matching its
robustness is a bigger job than beating its chart count, and the corpus in
[the exit criteria](#exit-criteria-measured) exists because that is the claim most likely to be wrong.

⚠ **MeshTailor's chart counts are not obviously reachable without its data.** Ten charts where xatlas
gives fifty-two came from learning what professional layouts look like. A τ-driven recursion with a
merge-back pass should land far below xatlas and above MeshTailor, and the document is not going to
pretend otherwise until U3 is measured.

---

## Exit criteria (measured)

1. **Against xatlas, on its own terms.** A 500-mesh corpus — imported, CAD, generated, blockout exports
   — with **fewer charts and no worse L² stretch than xatlas on at least 80 % of it**, and never more
   than 1.25× its stretch on any of it.

   🟡 **Not yet measurable — xatlas cannot be run here — so U2 left a baseline instead**, on a fixed set
   of shapes chosen so each fails differently. Through the full ladder: sphere cut open 1.0402 L² /
   1.3830 L^∞ / 1.2579 angular / 1.2219 area; torus slit both ways 1.0277 / 2.2551 / 1.1981 / 1.1689;
   hemisphere 1.0251 / 1.1759 / 1.1882 / 1.1786; saddle 1.0222 / 1.4682 / 1.1631 / 1.1544; slit
   cylinder and a flat 40:1 strip exactly 1 on all four. ⚠ **Both rungs are quoted because one column
   says the wrong thing about both**: on the sphere, LSCM alone measures **1.04 angular against ARAP's
   1.26** and **1.72 area against ARAP's 1.22**. The conformal map wins on the metric it optimizes.
2. **Correctness.** Zero flipped triangles on 100 % of the corpus, or an explicit refusal naming the
   chart. No exceptions, no hangs. ✅ Met on U2's corpus: every disk in it flattens with zero flips, and
   every non-disk — annulus, closed surface, genus-one-with-a-hole, disconnected, pinched — is refused
   by name before a solve runs.
3. **Packing.** ⚠️ **Restated, because as written this criterion was won by the packer it was meant to
   rule out.** It named `EffectiveEfficiency`, which counts an island *and its margin band* against the
   sheet — and a bounding-box packer's band is drawn around a box, so it consumes **more** of the atlas
   while delivering **less** texture ([D7](#d7-packing-is-a-ladder-too-and-the-honest-metric-is-efficiency-after-margin)
   has the measurements). The discriminating field is `PackingEfficiency`. So: **`PackingEfficiency` at a
   4-texel margin on 2048² beats a bounding-box packer on the same islands by at least 10 points.**
   ✅ Met at **19.97 points** — 52.96 % against 32.99 % on 422 irregular islands.

   ⚠ **And the 80 % figure is dropped rather than restated, because it was never a property of the
   packer.** Delivered texture is bounded by how much of the sheet the *islands themselves* can cover
   once every one of them is separated by four texels; on a corpus of several hundred concave islands
   that ceiling is nowhere near 80 %, whatever the placement. The reachable improvement that remains is
   real and is named: the skyline is a max envelope, so caves under overhangs are unreachable, and
   recovering them needs free-interval or MaxRects-style hole tracking rather than tuning. Three cheaper
   alternatives were measured and rejected — waste-first scoring made it *worse* at 45.86 %, and both
   `Level = y` and spatially-spread descent candidates changed nothing.
4. **Margin.** The same asset packed at 512², 1024², 2048² and 4096² has **the same texel gap** at every
   resolution, verified by rasterizing the atlas and measuring. ✅ Met exactly: 4 texels at all four
   resolutions, island-to-island and island-to-edge, and the gap equals the margin asked for across
   {2, 6, 12} × {512, 1024} as well.
5. **Density.** Uniform mode holds texel density within 2 % across every chart.
6. **Determinism.** Ten runs × {1, 4, 16} threads × three platforms, byte-identical.
7. **The packer alone.** Islands unwrapped in Blender, imported, repacked: seams untouched, island
   shapes untouched, better efficiency. ⚠ **This is the UV-Packer comparison, and it is the one the
   request asked for.**
8. **The AI case, end to end.** A generated mesh → doc 41 conditioning → remesh → this unwrap → doc 41's
   bake, producing a textured asset with no manual step.

---

## What this does not become

1. **A second UV channel.** [B3](#b3-the-engine-has-exactly-one-uv-channel--for-a-second-set-which-we-are-not-adding).
   Doc 19's surface cache uses cards, so nothing in the lighting wants a lightmap UV.
2. **An interactive UV editor.** A panel that *shows* islands, distortion and seams, and runs the three
   verbs. Not a drag-a-vertex-in-UV-space tool — that is doc 20's surface and a different document.
3. **A native dependency.** Clean-room C#, with xatlas and BFF attributed in `NOTICE` where their
   published behaviour is the reference.
4. **A metaheuristic packer.** [D7](#d7-packing-is-a-ladder-too-and-the-honest-metric-is-efficiency-after-margin) —
   annealing and genetic search are the literature's standard answers and every one is excluded by
   determinism.
5. **A trained seam model.** [D14](#d14-a-learned-seam-proposer-is-a-plugin-that-proposes-and-never-decides)'s
   fourth rule. The plugin seam exists to accept somebody else's permissive weights, not to justify
   building a corpus we already decided we cannot license.
6. **A runtime feature.** Import time and edit time. Nothing in a shipped game unwraps anything.

---

## See also

- [41 — Automatic retopology](41-automatic-retopology.md) — § D3's conditioning is reused wholesale,
  § D13's atlas becomes this packer's client, § D12's bake writes into this atlas, and §§ D18–D19's
  rules govern [D14](#d14-a-learned-seam-proposer-is-a-plugin-that-proposes-and-never-decides).
- [40 — AI-assisted generation](40-ai-assisted-material-generation.md) — § D6's *"a mesh with no UVs
  cannot be retextured"* ends here, and for meshes whose topology never needed touching this is the
  answer rather than a remesh.
- [24 — Blockout tools](24-blockout-tools.md) — `MeshSurfaces`' box and planar projection is what a
  blockout has today; `BlockoutHandoff` is what gains a real atlas.
- [19 — Lighting and GI](19-lighting-and-global-illumination.md) — why there is no lightmap UV to
  generate, which is the constraint that keeps this to one channel.
- Ma, X., Yan, X., Zhang, C., Huang, H. *MeshTailor: Cutting Seams via Generative Mesh Traversal.*
  arXiv:2603.27309, March 2026. [arXiv](https://arxiv.org/abs/2603.27309) — the metric set in
  [Part 4](#part-4--what-the-report-says) and the representational argument in
  [D4](#d4-a-seam-is-a-walk-on-the-mesh-graph-and-that-is-true-without-a-network).
- *PartUV: Part-Based UV Unwrapping of 3D Meshes.* SIGGRAPH Asia 2025.
  [arXiv:2511.16659](https://arxiv.org/abs/2511.16659) ·
  [doi:10.1145/3757377.3763843](https://doi.org/10.1145/3757377.3763843) — the recursive
  distortion-threshold shape [D3](#d3-charting-is-a-distortion-driven-recursion-over-a-decomposition-that-is-pluggable)
  takes.
- [xatlas](https://github.com/jpcy/xatlas) — MIT. The baseline to beat, and readable.
- Sawhney, R., Crane, K. *Boundary First Flattening.* TOG 2018.
  [doi:10.1145/3132705](https://doi.org/10.1145/3132705) ·
  [geometrycollective.github.io/boundary-first-flattening](https://geometrycollective.github.io/boundary-first-flattening/)
  — MIT.
- Lévy, B. et al. *Least Squares Conformal Maps.* SIGGRAPH 2002 · Sheffer, A. et al. *ABF++* ·
  Liu, L. et al. *A Local/Global Approach to Mesh Parameterization*, SGP 2008 · Rabinovich, M. et al.
  *Scalable Locally Injective Mappings* (SLIM), TOG 2017 · Liu, Y. et al. *Progressive
  Parameterizations*, SIGGRAPH 2018 — the flattening ladder in
  [D5](#d5-flattening-is-a-ladder-and-the-solver-under-it-is-conjugate-gradient-with-a-warm-start).
- Sheffer, A., Hart, J. *Seamster: Inconspicuous Low-Distortion Texture Seam Layout.* VIS 2002 — the
  occlusion and concavity terms in [D4](#d4-a-seam-is-a-walk-on-the-mesh-graph-and-that-is-true-without-a-network).
- Poranne, R. et al. *Autocuts*, SIGGRAPH Asia 2017 · Li, M. et al. *OptCuts*, SIGGRAPH Asia 2018 —
  joint cut-and-distortion optimization, and the runtime that rules it out of a content build.
- *Learning Based 2D Irregular Shape Packing.* TOG 2023.
  [doi:10.1145/3618348](https://doi.org/10.1145/3618348) — the super-patch grouping in
  [D7](#d7-packing-is-a-ladder-too-and-the-honest-metric-is-efficiency-after-margin), used without the
  learning.
- [UV-Packer](https://www.uv-packer.com/) (3d-io) — the standalone packer this document takes even
  spacing and a first-class `Pack` entry point from.
- [`Vixen.Ui.Text/Rasterizing/GlyphAtlas.cs`](../../Core/Vixen.Ui.Text/Rasterizing/GlyphAtlas.cs) — the
  shelf packer already in the repository, and its own remarks on what shelf packing costs.
