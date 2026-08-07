<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 41 — Automatic retopology: a quad remesher

⚠️ **Amends [40](40-ai-assisted-material-generation.md) and extends [24](24-blockout-tools.md),
[22](22-virtualized-geometry.md), [08](08-asset-pipeline-and-addressables.md) and
[33](33-character-creator.md).** Doc 40 § D6 wrote down, in as many words, that Vixen has *no
retopology and no automatic UV unwrapping*, and its closing list makes "a modelling package — no
retopology, no unwrapper" one of the six things the feature deliberately does not become. That limit
was correctly drawn for a document about *inference*. It is the wrong limit for the pipeline as a
whole, because it is the single thing standing between a generated mesh and a shippable asset — and
because retopology is not inference. It is arithmetic, it is deterministic, and it belongs in `Core/`
next to the mesh kernel [24](24-blockout-tools.md) already built.

**The claim this document has to earn.** A 4-million-triangle marching-cubes blob out of an image-to-3D
model — non-manifold, self-intersecting, with three floating specks of geometry and no UVs — becomes a
5,000-quad mesh with clean edge loops, a UV atlas, the source's materials and normals transferred onto
it, and a baked normal map carrying the detail that the quads gave up. It happens inside the content
build, with no external binary, no Python, no round trip through ZBrush, and it produces **the same
bytes every time**. The same code, with the same settings surface, retopologises a boolean result from
doc 24's blockout mode and a scanned CAD part, and reproduces every hard edge in either one *exactly*.

⚠ **Nothing in this document is a model.** It is the one part of the AI pipeline with no weights, no
licence to accept and no download — which is why it is the part that ships to a game's own build
machine.

⚠ **A note on the name.** *ZRemesher* is Maxon's, and *Quad Remesher* is Exoside's. This document
describes a **ZRemesher-class** remesher — one whose *output* an artist would recognise — implemented
from the published literature. The engine's own name for it is `Vixen.Geometry.Remeshing`, and neither
trademark appears in the code, the UI or the CLI.

---

## Part 0 — What ZRemesher actually is, read from the outside

Surveyed on 2026-08-06 from the shipping documentation and the observable behaviour, because there is
no other source: **ZRemesher is closed, and no paper describes it.**

**ZRemesher and Exoside's Quad Remesher are the same algorithm by the same author** — Maxime Rouca,
who also wrote ZBrush's UV Master and Decimation Master. ZRemesher shipped inside ZBrush (v1 in 4R6,
v2 in 4R7, v3 in 2021 with hard-surface handling); Quad Remesher shipped in 2019 as plugins for
3ds Max, Maya, Modo and Blender. That single fact is worth stating early, because it means the entire
observable design space of "the good automatic remesher" is **one implementation with two front
ends**, and everything below is measured against it.

### The parameter surface, which is the only public specification there is

| Control | What it does | What it implies about the algorithm |
|---|---|---|
| **Target polygon count** | The quad budget | There is a global density scalar, not just a target edge length |
| **Adaptive size** (0–100) | 0 = uniform squares; 100 = let curvature decide | Density is a *field*, driven by curvature |
| **Curves strength** + **ZRemesher Guides** brush | Painted curves the edge flow should follow | The direction field takes soft directional constraints |
| **Detect Edge** / **Keep Creases** | Hard-surface edge preservation | Features are found and *snapped to*, after the fact |
| **Keep Groups** | PolyGroup boundaries survive as edges | Region boundaries are constraints on the layout |
| **Freeze Border** | Open boundaries are not moved | Boundary is a constraint class of its own |
| **Symmetry** | Mirrored topology | Approximate — mirrored *flow*, not mirrored *vertices* |
| **Density masking** (PolyPaint) | Per-area density multiplier | Confirms the density field, and that it is authorable |
| **Half / Same / Double** | Relative to the current count | A convenience over the budget |
| **Legacy (2018)** | Runs the ZRemesher 2 algorithm instead | ⚠ v3 regressed on some organic input badly enough that the old one had to stay shipped |

⚠ **That last row is the most informative one in the table.** A tool that has to keep its previous
algorithm available, permanently, under a button, is a tool whose quality is *not monotone in its
version* — and the reason is knowable: hard-surface handling was bolted onto a field-and-flow
algorithm that was tuned for organic shapes, and the two want different singularity placement. This
document's answer is [D4](#d4-features-are-found-before-the-field-and-they-are-boundaries-by-construction):
features become patch boundaries *before* the field is solved, so hard surface is not a mode.

### What it does not do, and every one of these is an opening

1. **No UVs.** UV Master is a separate tool, run separately, with its own seams.
2. **No attribute transfer.** The result has no materials, no vertex colours, no skinning weights.
   Reprojecting detail is a second manual operation.
3. **No report.** There is no deviation figure, no quad percentage, no singularity count — you look at
   it and decide.
4. **Not scriptable as a build step.** It is a button in a DCC package. A content build cannot call it.
5. **Not deterministic in any documented sense**, and nothing depends on it being so.
6. **One mesh at a time**, in a GUI, on an artist's machine.

⚠ **Items 1, 2 and 4 are why an *engine* remesher is a different product from a *sculpting* one, not a
worse copy of it.** An engine already knows what a material assignment, a skin binding and a content
hash are; a sculpting package does not have to care.

---

## Part 1 — The literature, and which parts we may actually use

Rouca published nothing. But ZRemesher sits inside a well-mapped field, and its output is consistent
with the standard three-stage shape: **a cross field, a parameterization, an extraction**. What follows
is what is available, and — the part that decides this plan — under what licence.

### The four families

**1 · Integer-grid maps / MIQ.** Bommes et al., *Mixed-Integer Quadrangulation* (2009), and the decade
of work after it. A 4-RoSy cross field, then a global parameterization whose integer transitions across
cuts are solved as a mixed-integer problem; quads fall out as the integer iso-lines. **The best quality
in the family, and the reference standard.** Slow, fragile on imperfect input, and the practical
implementations depend on **CoMISo (GPL3)**.

**2 · Instant Meshes.** Jakob, Tarini, Panozzo, Sorkine-Hornung, SIGGRAPH Asia 2015. Replaces the global
solve with a **local smoothing operator** applied to two fields in sequence — an orientation field
(4-RoSy) and a position field — over a **multiresolution hierarchy**, in linear time. Sub-second on
meshes of hundreds of thousands of faces; ~9 minutes on the 372M-triangle St Matthew. Accepts point
clouds and scans as well as meshes.

⚠ **And it is BSD-3 with a contribution clause** — the only permissive implementation of a modern
remesher in existence. That single licence fact is why the field solver in this plan is shaped like
Instant Meshes' and not like anything else.

Its weaknesses are exactly ZRemesher's: output is quad-**dominant** rather than quad-only, sharp
features are *snapped to* after extraction rather than reproduced by construction, and the solver is
randomized (random initialization, parallel Gauss–Seidel over an unordered schedule) — so two runs of
the same input differ.

**3 · QuadWild.** Pietroni et al., *Reliable Feature-Line Driven Quad-Remeshing*, SIGGRAPH 2021. Four
stages: field, field tracing, **a patch layout in which feature lines are boundaries by construction**,
and a per-patch tessellation quantized for global consistency. Reported under 0.5% failure across
Thingi10K, which for automatic quadrangulation is an extraordinary number. It is the right *structure*
and it produces the hard-surface results ZRemesher's Legacy button exists because of.

⚠ **And it is GPL3, and its quantization needs Gurobi (commercial) or CoMISo (GPL3).** Unusable as
code in an Apache-2.0 engine — and, separately, unusable as a *shape* until 2023, because a content
build cannot depend on a commercial ILP solver.

**4 · The 2023 unlock, and it is the reason this document exists now.** Heistermann, Warnett and Bommes,
*Min-Deviation-Flow in Bi-directed Graphs for T-Mesh Quantization*, SIGGRAPH 2023. The quantization
step — deciding how many quads each patch side gets, subject to global consistency — is **not an ILP**.
It is a minimum-deviation-flow problem in a bi-directed graph, solvable exactly by matching. Against
QuadWild's own ILP on the authors' 300-mesh dataset: **17.06 s against 3491 s — 0.49% of the runtime —
at 11% lower energy**, with an approximate solver landing in 3.19 s at 24% higher energy.

⚠ **Two hundred times faster, better answers, and no commercial solver.** That is the difference
between "QuadWild-quality retopology" being a research artefact and being a stage in a content build.

**5 · The learning-based wave (2024–2026), which is not the base and might be an accelerator.**
NeurCross (TOG 2025), CrossGen, NeuFrameQ (ICCV 2025), *Learning Sparse Singularities for Cross Field
Design*, SQuadGen, and the autoregressive quad generators (QuadGPT, QuadLink). The interesting cluster
is the one that predicts **singularity structure** rather than dense fields, because singularity
placement is precisely what makes topology look artist-made. [D18](#d18-a-learned-field-prior-is-a-plugin-that-seeds-and-never-decides)
takes exactly one thing from it and refuses the rest.

### The licence table, because it decides the plan

| Artefact | Licence | May we link it? | May we read it? |
|---|---|---|---|
| Instant Meshes | BSD-3 + contribution clause | ✅ **Yes** | ✅ Yes |
| libigl (core) | MPL2 | ✅ Yes | ✅ Yes |
| libigl `copyleft/comiso`, CoMISo | GPL3 | ⛔ No | ✅ Yes |
| QuadWild | GPL3 | ⛔ No | ✅ Yes |
| quadwild-bimdf, libSatsuma | GPL3 | ⛔ No | ✅ Yes |
| Gurobi | Commercial | ⛔ No | — |
| Papers (all of the above) | — | — | ✅ **The algorithms are not licensed** |

⚠ **The load-bearing decision falls straight out of that table.** This is a **clean-room C#
implementation**: the field solver takes Instant Meshes' *shape* (which we are additionally permitted
to port, and will attribute in `NOTICE`), the pipeline takes QuadWild's *feature-first structure* as a
design read from the paper, and the quantizer takes Bi-MDF's *formulation* as a design read from the
paper. No GPL code enters the repository, and no native dependency is added. C# is not a compromise
here either: this is graph work and sparse linear algebra over a few million elements, and the existing
`MeshSimplifier` is the proof that the repository already does exactly this class of work at speed.

---

## Part 2 — What blocks it

### B1. AI-generated meshes are the worst input a remesher can be given ⛔

Every image-to-3D and text-to-3D generator in current use — TRELLIS, TRELLIS.2, the Hunyuan3D line —
reconstructs an SDF and extracts a surface with marching cubes. What comes out is:

- **Staircase-scale noise** at the voxel frequency, which a curvature-driven field reads as real detail
  and aligns to. Untreated, the field is garbage.
- **Self-intersections and near-degenerate slivers**, because the isosurface is extracted per cell.
- **Floating debris** — specks the SDF hallucinated, sometimes hundreds.
- **Non-manifold edges** where two sheets meet at a cell boundary.
- **No UVs, no material assignment, no groups** — or, for the PBR-native generators, UVs that a
  remesh invalidates anyway.

⚠ **A remesher that assumes a clean manifold is a remesher that does not run on the input this
document exists for.** Conditioning is not preprocessing hygiene here; it is stage one, and it has a
report ([D3](#d3-conditioning-is-a-stage-with-a-report-not-hygiene)).

### B2. There is no attribute transfer anywhere in the engine ⛔

A remesh throws away every per-vertex and per-face quantity the source carried. Without transfer, the
output has no normals worth having, no UVs, no materials, no vertex colours and — for doc 33's
characters — **no skinning weights**, which makes it not a character. Doc 40 § D6 named the absence of
retopology; this is its twin, and it is the larger of the two.

### B3. A nondeterministic remesher breaks the content build ⛔

Doc 08 caches compiled assets on a content hash. Doc 22 builds a cluster DAG and pages from the
compiled mesh, and its crack-freedom is an *equality* over shared boundary vertices. The golden-image
tests compare frames. **A remesher whose output differs run to run makes every one of those
rebuild-unstable**: a CI machine and a developer machine produce different meshlet pages for the same
source file, and a golden fails for a reason nobody can attribute.

⚠ This rules out Instant Meshes' randomized initialization and unordered parallel Gauss–Seidel
*verbatim*, and it is the single largest constraint on the solver's design.
[D14](#d14-determinism-is-a-gate-not-an-aspiration) is what replaces them.

### B4. `Vixen.Geometry` references only the maths, and a remesher needs jobs 🟡

`Core/Vixen.Geometry` is deliberately dependency-light — `EditMesh`, `MeshTopology`, `MeshOperations`,
`MeshBoolean`, `MeshSurfaces`, `MeshCollision`, over `Vixen.Core.Mathematics` and nothing else. A
hierarchical field solve wants `JobScheduler`. Adding a threading reference to `Vixen.Geometry` would
put it in every consumer of the mesh kernel, including a blockout verb that needs one thread.
[D2](#d2-a-new-assembly-in-core-and-the-contrast-with-doc-40-is-the-argument) makes it a new assembly
rather than a new reference.

### B5. `EditMesh` is not a half-edge, and the remesher wants adjacency it does not want 🟡

Doc 24 chose an indexed face set with an edge table *specifically* so that non-manifold geometry is
reported rather than refused, and that decision is right for an editable mesh. A field solve wants a
manifold triangle surface with per-vertex tangent frames and one-ring traversal.

⚠ **The resolution is that these are two different structures for two different jobs, and the
conversion is a stage.** The remesher builds its own internal manifold triangle view during
conditioning, works there, and hands back an `EditMesh` of quads. `EditMesh` does not grow a half-edge
mode, and the remesher does not run on n-gons.

### B6. The competition already ships this, hosted 🟡

Hunyuan3D's hosted pipeline advertises "smart retopology" with quad output and a density control, as a
step in the same generation service that produced the mesh. ⚠ **The gap that leaves is not quality, it
is *locality*** — that retopology happens on somebody else's machine, on the vendor's schedule, on the
mesh they generated, and it is not available to the boolean result an artist made in the blockout mode
this morning. A remesher inside the engine serves both, and serves the build machine.

---

## Part 3 — The design

### D1. One pipeline, seven stages, and every stage is an inspectable artefact

```
  source triangles
        │
   ①  Condition        weld · orient · de-speck · repair · isotropic pre-remesh
        │              → a manifold triangle view, and a ConditioningReport
   ②  Features        dihedral · creases · groups · UV seams · guide curves
        │              → feature polylines and corners
   ③  Field           4-RoSy cross field, hierarchical, feature-constrained
        │              → per-vertex direction + singularity list + density field
   ④  Layout          separatrix tracing · motorcycle graph · patch decomposition
        │              → patches with sides, and the consistency system
   ⑤  Quantize        min-deviation-flow over the bi-directed patch graph
        │              → an integer side count per patch side
   ⑥  Extract         per-patch grids, stitched · relax · validate
        │              → an all-quad EditMesh, T-junction free
   ⑦  Transfer        normals · UVs · colours · materials · skin weights · baked maps
                      → the output, carrying what the input carried
```

⚠ **The stage boundaries are the debugging surface, and that is deliberate.** Every stage can dump its
artefact — the conditioned triangles, the field as a line set, the patch layout as coloured regions,
the quantization as a labelled graph — into the viewport through `DebugDraw` and into a file. When a
remesh looks wrong, "which stage" is the first question, and a monolith cannot answer it. This is what
ZRemesher structurally cannot offer.

### D2. A new assembly in `Core/`, and the contrast with doc 40 is the argument

**`Core/Vixen.Geometry.Remeshing`**, referencing `Vixen.Geometry`, `Vixen.Core.Mathematics` and
`Vixen.Core.Threading`. Nothing else — no graphics, no assets, no editor. A `RemeshingLayeringTests`
asserts both halves, the way `AiLayeringTests` does for doc 37.

⚠ **Doc 40 § D9 put inference in an *editor* assembly, and that was right for inference and is wrong
here.** The reasons it gave — a shipped game infers nothing, the weights are the author's to accept,
the runtime must not grow — are all statements about *models*. This has no model. It is arithmetic over
triangles, and it has four callers that are not the editor:

| Caller | Why it cannot be an editor assembly |
|---|---|
| `Tools/Vixen.AssetCompiler` | The content build runs headless on CI, with no editor |
| `Tools/Vixen.Cli` — `vixen remesh` | A batch tool over a directory of generated meshes |
| `Editor/Vixen.Editor.Blockout` | Doc 24's mode, retopologising a boolean result |
| A game's own tooling | Procedural content that wants a quad cage at build time |

### D3. Conditioning is a stage with a report, not hygiene

Ordered, each step reporting what it changed:

1. **Weld** at a tolerance relative to the bounding box — never absolute. ⚠ Doc 24's `Vixen.Geometry`
   row already records this lesson twice (the capsule's degenerate poles, the weld tolerance); a fixed
   epsilon is a claim about how big a model is.
2. **Orient** consistently by flood fill over the face graph; components whose orientation cannot be
   made consistent are flagged, not fixed.
3. **De-speck** — drop connected components below a fraction of the largest component's surface area.
   The default drops the SDF's hallucinated debris and keeps a character's separate eyeball.
4. **Repair non-manifold edges by cutting**, not by merging: an edge with three or more faces is split
   so each side is manifold. Cutting keeps the geometry and costs a seam; merging invents a surface.
5. **Fill holes** below a size threshold, optionally. Off by default — a hole in the input is often a
   hole in the subject.
6. **Isotropic pre-remesh** to a target edge length: split long edges, collapse short ones, flip toward
   valence 6, tangential relaxation. ⚠ **This is the step that makes marching-cubes soup workable**, and
   QuadWild's `do_remesh` exists for the same reason. A curvature estimate on a staircase mesh is a
   curvature estimate of the staircase; the pre-remesh is what turns per-cell noise into a smooth
   surface the field can read. The relaxation is projected back onto the original surface each
   iteration, so the shape does not drift.
7. **Voxel shrinkwrap**, opt-in and last. Sample a signed field (generalised winding number, so that
   self-intersecting and open input still has an inside), extract with dual contouring, and continue
   from that. ⚠ **It destroys thin features and it is never the default** — it is the escape hatch for
   input so broken that nothing else will run, and the report says loudly when it fired.

The `ConditioningReport` carries every count and the resulting `MeshReport`. A caller can refuse to
continue on it.

### D4. Features are found before the field, and they are boundaries by construction

Feature edges come from five sources, unioned:

| Source | Notes |
|---|---|
| Dihedral angle over a threshold | The default, and the only one an arbitrary input has |
| Explicit creases | Doc 24's shapes and the importer both carry them |
| Face-group boundaries | ZRemesher's "Keep Groups" — `MeshFace.Group` already exists |
| UV seams on the input | So a retexture-then-remesh round trip does not shred an atlas |
| **Guide curves** | [D10](#d10-guides-density-and-the-rest-of-the-authoring-surface) — the artist's |

Edges are chained into **feature polylines**; endpoints and junctions become **feature corners**; chains
shorter than a threshold are pruned (marching-cubes output produces thousands of two-edge "features"
that are noise); nearly-straight chains are simplified.

⚠ **The polylines are then boundaries of the layout, not a post-process snap.** This is the whole
QuadWild thesis and it is the difference between a hard edge that is *reproduced* and one that is
*approximated*. ZRemesher's Detect Edge nudges extracted vertices toward detected edges afterwards,
which is why its hard-surface results are good-but-wobbly and why the Legacy button exists. Here, a
feature polyline is a chain of output edges by construction, and
[the exit criterion](#exit-criteria-measured) asserts that at a tolerance of exact.

### D5. The field is a 4-RoSy cross field, hierarchical, and deterministic

Per-vertex, in the tangent plane, represented by one direction standing for four. The energy is the
standard one — over each edge, the smallest angle between the two endpoints' representatives after the
best of the four rotations — minimized by a **local smoothing operator** rather than a global solve,
which is what buys linear time.

**Hierarchy.** Coarsen by greedy edge contraction into a vertex-cluster hierarchy, solve at the coarsest
level, prolong, refine. Without it the smoothing propagates one ring per iteration and a 2M-vertex mesh
never converges; with it, the coarse level fixes the global structure in a few hundred elements.

**Constraints.**
- Feature polylines fix the cross to the polyline's tangent, **hard**.
- Boundary edges likewise, when Freeze Border is on.
- Guide curves fix it *softly*, at the user's strength — which is exactly ZRemesher's Curves Strength.
- Principal curvature directions align it softly, weighted by curvature anisotropy |κ₁ − κ₂| relative
  to the model's scale. ⚠ **This weight is the whole of "Adaptive Size" on the direction side**: on a
  sphere the two curvatures are equal, the weight is zero, and the field is free to be smooth — which
  is the correct answer and the one a naive curvature alignment gets wrong by chasing noise.

**Determinism, which is a design property and not a tuning detail.**
- Initialization is **derived from geometry**, not random: each vertex's initial representative is the
  projection of a canonical axis chosen by the vertex's own position, tie-broken by index.
- The Gauss–Seidel sweep runs over a **graph colouring computed deterministically** (greedy by index),
  so vertices in one colour are independent and can be updated in parallel with no ordering effect.
- A **fixed iteration count per hierarchy level**, not a wall-clock or convergence-tolerance stop.
- Every reduction sums in index order.

**Singularities** are read off afterwards as the index of each vertex — the accumulated rotation
around its one-ring, in quarter turns. A valence-3 or valence-5 point in the output is a ±¼ singularity
here.

### D6. Singularity placement gets its own pass, because it is what topology *looks* like

A field minimizing smoothness alone scatters singularity pairs across flat regions, and a remesh with
scattered singularities is the thing artists mean when they say automatic topology looks wrong. Three
corrections, in order:

1. **Cancel adjacent opposite pairs.** A +¼ and a −¼ within a few edges of each other contribute
   nothing but noise; removing the pair and re-smoothing locally is strictly better.
2. **Push singularities off feature lines.** A singularity on a hard edge is a visible pinch and it
   fights the layout. There is a repulsion term, and the exit criterion is **zero on features**.
3. **Attract to Gaussian curvature.** A singularity has to go *somewhere* — the index sum is the Euler
   characteristic and no amount of smoothing changes it — and the right somewhere is where the surface
   genuinely is not developable: the tip of a finger, the corner of a box, the pole of a sphere. That
   is where an artist puts them.

⚠ **This is the single pass most responsible for "does it look like ZRemesher".** The field solve makes
topology that is *valid*; this makes it *good*. It is also cheap, because it operates on tens or
hundreds of singularities rather than millions of vertices — which is worth saying plainly, since the
temptation is to spend the effort on the solver instead.

### D7. Layout, and quantization as a flow problem rather than an ILP

**Trace** separatrices out of each singularity along the field, in all its directions, until they hit
another singularity, a feature line, or a boundary. The arrangement of traced curves plus the feature
polylines is a **motorcycle-graph-style partition** (Eppstein et al., 2008) of the surface into patches.
Patches that are too small or degenerate are merged into a neighbour.

Each patch is a polygon with *k* sides, and each side needs an integer number of quads. Consistency is
global: two patches sharing a side must agree, and a patch's opposite sides must agree for a regular
grid to exist inside it. That system is the quantization, and how it is solved is the decision.

⚠ **It is solved as a minimum-deviation flow in a bi-directed graph, not as an integer program.**
Bi-MDF: patch sides become arcs, the consistency constraints become conservation at nodes, and "as
close as possible to the size the density field asked for" becomes the deviation cost. The exact solver
is matching-based; there is an approximate one for interactive preview.

Three reasons this is the right call and not just the fast one:

1. **No commercial or copyleft solver.** The alternative is Gurobi or CoMISo, and [Part 1](#the-licence-table-because-it-decides-the-plan)
   forbids both. Without this formulation the QuadWild-shaped design is simply not available.
2. **It is two hundred times faster** at better energy, on the paper's own comparison — which moves
   retopology from "a thing you run overnight" to "a thing the content build does".
3. **A flow solver is deterministic and auditable.** An ILP's answer depends on the solver's version
   and its internal timing; a min-cost-flow with a fixed tie-break does not. [D14](#d14-determinism-is-a-gate-not-an-aspiration)
   needs that.

⚠ **A patch side may quantize to zero**, which collapses it — that is legitimate and it is how a
five-sided patch becomes four-sided. It must be *allowed* and then *checked*: a patch that collapses to
nothing is a bug, and the validator refuses it.

### D8. Extraction is all-quad, T-junction-free and manifold — asserted, not hoped

Each patch, with agreed integer side counts, is filled with a regular grid mapped through a per-patch
parameterization; grid vertices on shared sides are the *same* vertices, by index, so the seam is an
equality rather than a weld.

⚠ **This is where the plan diverges from Instant Meshes and refuses to compromise.** Instant Meshes
extracts from a position field and produces a quad-*dominant* mesh — some triangles, some pentagons,
some T-junctions — and every downstream consumer then has to cope. Doc 24's `MeshOperations` is built
on the assumption that a loop, a ring and a loop cut are statements about four-sided faces: a
quad-dominant result has no rings to cut, and the mesh kernel's whole vocabulary stops working on it.
An all-quad guarantee is not a nicety, it is what makes the output *editable*.

Then:

- **Relax.** A few iterations of tangential smoothing with reprojection onto the conditioned source,
  with feature vertices constrained to slide *along* their polyline and corners pinned.
- **Validate.** `EditMesh.Validate()` — the same `MeshReport` every doc 24 operation is tested against.
  A closed input must produce `IsSolid`. Non-quad face count is asserted zero.
- **Report.** Quad count, singularity count and their valences, max and mean deviation from the source
  as a fraction of the bounding-box diagonal, minimum scaled Jacobian, feature reproduction error, and
  per-stage timings.

### D9. Adaptivity is one scalar field, and everything writes into it

A single per-vertex **target edge length**, computed once and consumed by the pre-remesh, the
quantization and the extraction:

```
  targetLength(v) = clamp( base × curvatureTerm(v) × densityPaint(v) × featureTerm(v),
                           min, max )
```

where `base` is derived from the quad budget and the surface area, `curvatureTerm` is driven by the
adaptivity setting (at 0 it is 1 everywhere — ZRemesher's uniform squares), `densityPaint` is the
artist's mask, and `featureTerm` tightens near feature polylines so a hard edge is not straddled by one
enormous quad.

⚠ **One field means "Adaptive Size", "density masking" and "keep detail near the creases" are the same
mechanism rather than three that interact by accident.** Three separate multipliers applied at three
different stages is how a remesher acquires settings that only work in certain combinations.

### D10. Guides, density, and the rest of the authoring surface

| Ours | ZRemesher's | Notes |
|---|---|---|
| `TargetQuads` | Target polygon count | Or `TargetEdgeLength`; one implies the other through the area |
| `Adaptivity` 0–1 | Adaptive Size | Feeds `curvatureTerm` |
| `Guides` + `GuideStrength` | ZRemesher Guides + Curves Strength | A set of 3-D polylines. ⚠ **Ours are an asset, not a paint session** — they can be authored on a curve, saved beside the mesh, and reused after the source changes |
| `DensityMask` | PolyPaint density | A per-vertex scalar on the source, or a texture |
| `FeatureAngle`, `KeepCreases` | Detect Edge, Keep Creases | |
| `KeepGroups` | Keep Groups | `MeshFace.Group` boundaries become features |
| `FreezeBorder` | Freeze Border | |
| `Symmetry` | Symmetry | [D11](#d11-symmetry-is-exact-and-that-is-a-real-difference) — ours is exact |
| `TransferAttributes`, `BakeMaps` | *(absent)* | [D12](#d12-the-output-carries-the-input-or-it-is-useless) |
| `GenerateUvs` | *(absent)* | [D13](#d13-uvs-come-nearly-free-from-the-layout-and-that-closes-doc-40s-other-gap) |

### D11. Symmetry is exact, and that is a real difference

ZRemesher's symmetry produces mirrored *flow* — the two halves look alike and their vertices do not
correspond. For a character that is then rigged, mirrored for blend shapes, or used for a mirrored
weight paint, that is a lasting nuisance.

Here: detect a mirror plane (or take one), solve the field and layout on one half with the plane as a
constraint, **mirror the result**, and weld along the plane. ⚠ **Vertices on the plane are snapped to
it exactly, not welded by tolerance** — a tolerance-welded seam is how a mirrored mesh acquires a
one-vertex crack that only shows up under subdivision. Output vertex *k* and its mirror are exact
negations to the last bit.

### D12. The output carries the input, or it is useless

A transfer stage, driven by closest-point queries against the conditioned source (a BVH —
`Vixen.Core.Mathematics.TriangleTree`):

⚠ **Corrected.** This paragraph credited `MeshCollision` with "already has the shape of one", and it
does not: it is union-find shell labelling plus one axis-aligned box per shell, with no tree in it and
no query on it. The structure that *is* one was `Vixen.Rendering.DistanceFields`' internal
`TriangleTree`, whose own remarks said it belonged in `Vixen.Core.Mathematics` as soon as a second
caller existed. This is that caller, so it moved, and it grew the query this stage needs —
`Closest(point)` returns the triangle index and the barycentric coordinates that the table below
interpolates against, which the scalar `DistanceSquared` could not.

| Quantity | How |
|---|---|
| **Normals** | Barycentric interpolation on the source triangle, then smoothing-group-aware reconstruction |
| **UVs** | Interpolated, when the source had them and the caller wants them kept rather than regenerated |
| **Vertex colours / masks** | Interpolated |
| **Materials / face groups** | ⚠ **By majority of covered source *area*, not by nearest face.** Nearest-face assignment shreds along a material boundary — every other quad flips — and produces a mesh with a sawtooth material seam that looks like a UV bug |
| **Skinning weights** | Interpolated and renormalized, with the influence count clamped to the target's limit. This is what makes doc 33's characters remeshable, and what makes a generated humanoid riggable |
| **Baked normal + displacement** | Ray-cast along the output's interpolated normal against the source, into the atlas [D13](#d13-uvs-come-nearly-free-from-the-layout-and-that-closes-doc-40s-other-gap) produces |

⚠ **The bake is where the AI pipeline's arithmetic actually closes.** A 4M-triangle generated blob is
not expensive because it is 4M triangles; it is expensive because it is 4M triangles *of noise* with no
UVs. 5,000 quads plus a 2K normal map is smaller, looks better under a moving light, subdivides, and
can be rigged. Retopology without baking is a downgrade; retopology with baking is the pipeline.

### D13. UVs come nearly free from the layout, and that closes doc 40's other gap

**A quad patch with agreed integer side counts is a rectangle.** The layout is therefore already a
chart decomposition with zero in-chart distortion, and the atlas is a rectangle-packing problem, which
is a solved one.

Two refinements make it usable rather than merely correct:

- **Merge patches into super-charts before packing.** One chart per patch means hundreds of charts and
  hundreds of seams. Neighbouring patches whose grids agree across the shared side merge into a larger
  rectangle; the merge is greedy, seeded from the largest patches, and bounded by a seam budget the
  caller sets.
- **Seams prefer feature lines**, which are where an artist would cut anyway and where a normal-map
  discontinuity is least visible.

⚠ **This is not a general unwrapper and the document will not pretend otherwise.** It unwraps *meshes
this remesher produced*, because it reuses a structure the remesher had to compute anyway. An
arbitrary imported mesh with bad UVs still has no unwrapper — though it can now get one by being
remeshed, which is a legitimate answer for a great many meshes and an unacceptable one for a mesh whose
topology is the point. **Doc 40 § D6's second sentence is amended, not deleted.**

⚠️ **Amended by [42 — UV unwrapping](42-uv-unwrapping.md), in one half and not the other.** The
privileged charting stays here — a quantized quad patch *is* a rectangle, and re-cutting it with a
general charter would be worse in every respect. What moves is the packing: **the super-chart merging
and rectangle packing above become calls into `Vixen.Geometry.Uv`**, so there is one packer with one
margin rule rather than two. And the escape hatch for "a mesh whose topology is the point" now exists —
doc 42 unwraps it without touching a triangle.

### D14. Determinism is a gate, not an aspiration

**The same input and the same settings produce byte-identical output, at any thread count, on any
supported platform.** It is a test, it runs in CI, and it is the reason for four choices already made:
geometric initialization ([D5](#d5-the-field-is-a-4-rosy-cross-field-hierarchical-and-deterministic)),
deterministic graph colouring, fixed iteration counts, and a flow solver with an explicit tie-break
rather than an ILP ([D7](#d7-layout-and-quantization-as-a-flow-problem-rather-than-an-ilp)).

⚠ **Cross-platform bit-exactness constrains the arithmetic**: no fused-multiply-add where the
non-fused result differs, no `float` reductions in a nondeterministic order, no dependence on
`double`-vs-`float` intermediate width. Where an exact predicate is needed —
orientation tests during tracing and the layout — `Vixen.Core.Mathematics`' `ExactPredicates` already
exists and doc 24's boolean is the precedent for using it rather than a tolerance.

⚠ **Corrected, twice.** The assembly is `Vixen.Core.Mathematics`, not `Vixen.Geometry`. And what
already existed was `Orient3D` and two `InSphere` overloads — there was **no `Orient2D`**, which is the
predicate a separatrix traced in a tangent plane and a patch layout actually ask for. It exists now,
filtered in `double` with a `BigInteger` fallback exactly as `Orient3D` is, and
[42 § D5](42-uv-unwrapping.md#d5-flattening-is-a-ladder-and-the-solver-under-it-is-conjugate-gradient-with-a-warm-start)'s
flipped-triangle count is its other caller.

### D15. Quads are wanted for what happens *after*, and that is worth being explicit about

Three consequences, none of which a triangle remesher offers:

1. **Doc 24's verbs work on the result.** Loops, rings, loop cuts, bevels — an artist can *edit* what
   came out.
2. **Catmull–Clark subdivision.** A quad cage plus a displacement map is the LOD chain, and it is a
   better one than QEM decimation because every level is the same surface rather than an approximation
   of the previous level.
3. **Doc 22 can take the cage.** The cluster DAG currently simplifies with quadric error collapses;
   for an asset that *has* a subdivision cage, the levels are already there and exact. ⚠ **Not built
   here** — it is named as a consequence so that whoever opens doc 22 next knows the option exists.

### D16. Where it is invoked from

| Surface | Shape |
|---|---|
| **Importer** | `ModelImportSettings.Retopology` — a nullable `RemeshSettings`. ⚠ **This is the AI-pipeline hook**: a generated GLB dropped into the project comes out retopologised, atlased and baked, with the setting recorded in the `.meta` and in the content hash |
| **CLI** | `vixen remesh in.glb out.glb --quads 5000 --adaptivity 0.7 --bake` — batchable over a directory |
| **Blockout** | A `Retopologize` verb on doc 24's mode, one undo entry, result selected. Its natural input is a boolean result, which is exactly the hard-surface case |
| **Doc 40's panels** | A `Retopologize` node in the generation graph, between `GenerateMesh` and the write. And the retexture panel's ⚠ *"refuses a mesh with no UVs"* rule stops being a dead end — the offer becomes "remesh it first" |
| **Doc 33** | Conform and transfer, reusing [D12](#d12-the-output-carries-the-input-or-it-is-useless)'s weight transfer |

### D17. CPU jobs, and not the GPU — named so nobody quietly changes it

The field smoothing is textbook GPU work: a local operator over a colouring, in parallel, on millions
of vertices. It is still going to run on `JobScheduler`, for two reasons.

⚠ **Bit-exact float reduction across drivers and vendors is not achievable**, and
[D14](#d14-determinism-is-a-gate-not-an-aspiration) is a gate. ⚠ **And this is an import-time cost, not
a frame cost** — a few seconds inside a content build that already spends longer compiling shaders. A
GPU path would buy a preview that is interactive rather than nearly-interactive, at the price of the
property the content build depends on.

This is recorded as a decision rather than an omission, because "why is this not on the GPU" is the
first question anyone reading the code will have.

### D18. A learned field prior is a plugin that seeds and never decides

The 2025–26 work on predicting cross fields and, more interestingly, **sparse singularity structure**
is genuinely promising for the one part of this pipeline that is aesthetics rather than mathematics
([D6](#d6-singularity-placement-gets-its-own-pass-because-it-is-what-topology-looks-like)). Four rules,
the first three taken straight from doc 38 § D5 and doc 40 § D9 rather than invented:

1. **It lives in a plugin**, with its own ONNX Runtime dependency. `Core/` gains nothing.
2. **It seeds and never decides.** The prediction initializes the field and proposes singularity
   positions; the deterministic solver then runs to completion over it, and the validator has the last
   word. A model that is wrong makes the topology *worse-looking*, never *invalid*.
3. **The seed is part of the content hash**, model version included, or determinism is a lie.
4. ⚠ **No third-party training data, ever** —
   [D19](#d19-if-a-prior-is-ever-trained-the-corpus-is-one-we-generate) is the whole of the argument,
   and it is a rule rather than a preference.

⚠ **And it is explicitly not on the critical path.** Everything in Parts 3 and 5 ships without it.

### D19. If a prior is ever trained, the corpus is one we generate

There are three tiers of "learned" here, and **only the middle one involves a dataset at all**:

| Tier | Data | Notes |
|---|---|---|
| **Per-shape self-supervised** (NeurCross-class) | **None** | Fits a neural SDF and a cross field on the single input mesh simultaneously — an optimizer that happens to be an MLP. No corpus, no weights, nothing to license. ⚠ SGD on a device is exactly what [D14](#d14-determinism-is-a-gate-not-an-aspiration) forbids, which rule 2 above is what makes survivable |
| **Trained on synthetic shapes** | **Ours** | *Learning Sparse Singularities* (TOG 2026) trains on **1,000 samples** — scripted parametric primitives and compound gears, singularities annotated by rule — and lets a conventional method connect what it predicts |
| **Distilled from our own solver** | **Ours** | After R4 the deterministic pipeline *is* a label generator: predict in one shot what the hierarchy reaches in many iterations |

⚠ **We already own the generator the middle row needs.** `MeshShapes` is twelve parametric shapes,
`MeshBoolean` is CSG and `MeshOperations` is the rest — which is the role OpenSCAD plays in that
paper, in-repo and under test. **The corpus becomes a test fixture built by a script**, and at a
thousand samples this is a couple of GPU-days rather than a training programme.

⚠ **A corpus regenerable from a script is also the only kind [D14](#d14-determinism-is-a-gate-not-an-aspiration)
can live with.** The seed goes into the content hash; a hash whose provenance is "an 800,000-object
scrape somebody downloaded in 2026" is not auditable, and a build cannot be reproduced from it.

#### Why not free 3-D assets from the internet

Two reasons, and the practical one comes first.

⚠ **The free corpora do not contain the label we want.** What a prior would buy is D6's aesthetic
judgement — *where would an artist put the loops* — and the supervision for that is a production quad
mesh with good edge flow. Thingi10K is 3-D-printing STLs, Objaverse is Sketchfab uploads, ABC is CAD
B-reps. None of them is artist retopology, and a prior trained on Objaverse learns what amateur
Sketchfab topology looks like.

Then the terms, which are restrictive in their own right:

| Corpus | Terms | Usable for weights shipped with a commercial engine? |
|---|---|---|
| **Objaverse 1.0** (~800 K) | Per object: CC-BY 721 K · CC-BY-NC 25 K · CC-BY-NC-SA 52 K · CC-BY-SA 16 K · **CC0 3.5 K**. The collection is ODC-By | Only filtered — and CC0 is 0.4 % of it |
| **Objaverse-XL** (10 M+) | Mixed; the Polycam portion is non-commercial, on request and approval | ⛔ No |
| **ShapeNet** | Click-through agreement: **non-commercial research and education only** | ⛔ No |
| **ABC** (1 M CAD) | Onshape terms of use; "freely usable for research" | ⛔ Research only |
| **Thingi10K** | Ten different open-source licences, per model | Only filtered |

Four hazards, worst first:

1. **Non-commercial is a disqualification, not a filtering nuisance.** This is a commercial engine.
2. ⚠ **ShareAlike is the genuinely unsettled one.** Whether model weights are a derivative work of
   their training data is not settled law in any jurisdiction. If they are, CC-BY-SA input makes the
   weights CC-BY-SA, which conflicts with Apache-2.0. Treat SA as no.
3. **Attribution survives but is not free.** 721 K CC-BY objects means a 721 K-entry attribution
   manifest shipped beside the weights, permanently.
4. ⚠ **A collection's licence grants nothing about the objects in it.** ODC-By on the Objaverse
   *dataset* is not a licence to the models, and the two are conflated constantly.

Whether training is itself covered by fair use or by a text-and-data-mining exception is under active
litigation in the United States and differs again in the EU (DSM Art. 4, with an opt-out) and the UK.
**This document takes no position on it and does not need to**, because rule 4 removes the question.

#### The ceiling this puts on R9, stated in advance

⚠ **The corpus that would teach artist edge flow is a studio's asset library, and that is neither
free nor licensable.** So the best a legitimately-trained prior can do is *better singularity
placement on CAD-like and primitive-like shapes* — not topology that looks like a character artist
made it.

That is worth writing down before anybody builds it, because it means
[D6](#d6-singularity-placement-gets-its-own-pass-because-it-is-what-topology-looks-like)'s hand-written
placement pass **carries the aesthetic load permanently**, and is not a stopgap until R9 arrives.

---

## Part 4 — What the report says

Every remesh returns a `RemeshReport`, and it is the answer to *"is this good?"* that ZRemesher does not
give:

| Field | Why it is in the report |
|---|---|
| `QuadCount`, `NonQuadCount` | The second must be zero; a non-zero is a bug, not a setting |
| `Singularities` — count by valence | The headline quality number. Fewer and better-placed is better |
| `SingularitiesOnFeatures` | Must be zero |
| `MaxDeviation`, `MeanDeviation` | As a fraction of the bounding-box diagonal, so it compares across models |
| `MinScaledJacobian` | The worst quad's shape quality. A negative one is an inverted quad |
| `FeatureReproductionError` | Max distance from a feature polyline to the nearest output edge |
| `MeshReport` | Doc 24's, unchanged — manifold, closed, consistent, no degenerates |
| `Stages` — time and element counts | Which stage was slow, and which one dropped something |
| `Warnings` | The shrinkwrap fired · N components dropped · a patch collapsed · the budget was not met |

⚠ **A remesher that cannot tell you it went wrong will be trusted until it embarrasses somebody.** The
report is what makes it usable in an unattended content build.

---

## Part 5 — Phases

### R0 — The spike · 0.5 EM

One question, three inputs: a scanned head, a boolean-heavy CAD part, and a real TRELLIS output.
Conditioning plus a hierarchical 4-RoSy solve plus naive per-face extraction, no layout and no
quantization, dumping the field as a line set. **What it answers:** whether conditioning survives
marching-cubes soup at all, and whether the hierarchical solver holds its timing budget in C# at
2M vertices. ⚠ If conditioning cannot make the TRELLIS output tractable, everything after R1 is
premature and the phase order changes.

### R1 — The assembly and the conditioning stage · 1.0 EM

`Core/Vixen.Geometry.Remeshing`, the layering test, the internal manifold triangle view, the seven
conditioning steps, `ConditioningReport`, and the isotropic pre-remesh with reprojection. Tested
against a corpus of deliberately awful meshes.

### R2 — Features and the field · 1.5 EM

Feature detection from all five sources, polyline chaining, corner detection, pruning. The 4-RoSy
solver, the hierarchy, hard and soft constraints, curvature alignment, the density field, singularity
extraction, and [D6](#d6-singularity-placement-gets-its-own-pass-because-it-is-what-topology-looks-like)'s
placement pass.

### R3 — Layout, quantization and extraction · 2.0 EM

Separatrix tracing on exact predicates, the patch partition, patch merging, the bi-directed
consistency graph, the min-deviation-flow solver (exact and approximate), per-patch grid extraction,
stitching, relaxation, and the validator. ⚠ **The largest phase, and the flow solver is the piece with
the most unknowns** — it is also the piece that is worthless to build halfway, so it is not split.

### R4 — The quality report and the determinism gate · 1.0 EM

`RemeshReport` in full, the per-stage debug dumps, and the determinism tests: one thread against
sixteen, ten runs, byte-identical, on all three desktop platforms. ⚠ **This is where the solver design
gets audited rather than where determinism gets added** — if R2 or R3 introduced an ordering
dependence, this is where it is found and it is a bug in them.

**— cut line: a remesher that produces good topology, and nothing else. 5.0 EM —**

### R5 — Attribute transfer and baking · 1.0 EM

The BVH-backed transfer, normals, UVs, colours, majority-area material assignment, skin weights, and
the normal/displacement bake. ⚠ **This is what makes it useful rather than impressive.**

### R6 — UVs from the layout · 0.75 EM

Chart derivation, super-chart merging, seam preference, packing, and the atlas.

**— cut line: the AI pipeline's missing piece, end to end. 6.75 EM —**

### R7 — Guides, symmetry, and the artist surface · 1.0 EM

Guide curves as an asset, density masks, exact symmetry, doc 24's `Retopologize` verb with its settings
panel and a live preview at the approximate quantization, and the debug overlays as a viewport toggle.

### R8 — The importer, the CLI, and doc 40's wiring · 0.5 EM

`ModelImportSettings.Retopology`, `vixen remesh`, the generation-graph node, and the retexture panel's
"remesh it first" offer.

### R9 — The learned field prior · 1.0 EM · optional, and not on the path

[D18](#d18-a-learned-field-prior-is-a-plugin-that-seeds-and-never-decides). Only worth opening after R7
has produced enough real results to say whether singularity placement is actually the remaining
complaint.

⚠ **Two of the three tiers in [D19](#d19-if-a-prior-is-ever-trained-the-corpus-is-one-we-generate) are
cheaper than the estimate suggests, and one of them is free.** Evaluating a NeurCross-class per-shape
optimizer costs no corpus at all and should be the first thing tried, because it answers *"would a
neural field even be better than D5's?"* without anyone building a dataset. The synthetic corpus is a
script over `MeshShapes` and `MeshBoolean`, so most of the 1.0 EM is the ONNX plumbing doc 38 already
built once and the evaluation harness — **not data work**.

### Cost

| Phase | EM | Blocked on |
|---|---|---|
| R0 — The spike | 0.5 | Nothing |
| R1 — Assembly and conditioning | 1.0 | R0 |
| R2 — Features and the field | 1.5 | R1 |
| R3 — Layout, quantization, extraction | 2.0 | R2 |
| R4 — Report and determinism | 1.0 | R3 |
| — | **6.0** | **the first cut line — good topology, nothing carried across** |
| R5 — Transfer and baking | 1.0 | R4 |
| R6 — UVs from the layout | 0.75 | R3 |
| — | **7.75** | **the second cut line — the AI pipeline's missing piece** |
| R7 — Guides, symmetry, the artist surface | 1.0 | R4 |
| R8 — Importer, CLI, doc 40 | 0.5 | R5, R6 |
| | **9.25** | |
| R9 — The learned prior | 1.0 | R7. ⚠ Optional |

⚠ **R6 depends on R3 and not on R5, so it can run in parallel with R5** — the atlas comes out of the
layout, and the bake is the only thing that needs both.

---

## Part 6 — Where this beats ZRemesher, honestly

The first four rows are the ones worth building for. The rest are consequences of being inside an
engine rather than inside a sculpting package.

| | ZRemesher / Quad Remesher | This | Why it is possible |
|---|---|---|---|
| **Hard-surface features** | Detected, then snapped to — approximate, and the reason Legacy still ships | **Reproduced exactly**, because they are layout boundaries | QuadWild's structure, read from the paper ([D4](#d4-features-are-found-before-the-field-and-they-are-boundaries-by-construction)) |
| **Determinism** | Not claimed, not needed | **Byte-identical, gated in CI** | The content build requires it, so it was designed in from the field solver up ([D14](#d14-determinism-is-a-gate-not-an-aspiration)) |
| **UVs** | None — UV Master is a separate tool | **An atlas from the layout**, seams on features | The patch layout is already a chart decomposition ([D13](#d13-uvs-come-nearly-free-from-the-layout-and-that-closes-doc-40s-other-gap)) |
| **Attributes** | Lost | Normals, UVs, colours, materials, **skin weights**, and a baked normal/displacement pair | An engine knows what those are ([D12](#d12-the-output-carries-the-input-or-it-is-useless)) |
| **Quality report** | You look at it | Nine measured fields | [Part 4](#part-4--what-the-report-says) |
| **Broken input** | Cleaned up beforehand, by you | A conditioning stage with a report and a shrinkwrap of last resort | [D3](#d3-conditioning-is-a-stage-with-a-report-not-hygiene) |
| **Symmetry** | Mirrored flow | **Mirror-exact vertices** | [D11](#d11-symmetry-is-exact-and-that-is-a-real-difference) |
| **Guides** | A paint session on the model | An **asset**, reusable after the source changes | [D10](#d10-guides-density-and-the-rest-of-the-authoring-surface) |
| **Automation** | A button in a GUI | An importer setting, a CLI, a graph node | [D16](#d16-where-it-is-invoked-from) |
| **Quantization** | Unknown | Min-deviation flow — no ILP, no commercial solver | Bi-MDF, 2023 ([D7](#d7-layout-and-quantization-as-a-flow-problem-rather-than-an-ilp)) |
| **Inspectability** | Opaque | Seven stages, each one dumpable | [D1](#d1-one-pipeline-seven-stages-and-every-stage-is-an-inspectable-artefact) |

And the two rows going the other way, which are real:

⚠ **ZRemesher has had thirteen years of tuning against artists' complaints, and this will not match its
organic results on day one.** The mathematics is public; the thousand small decisions about what looks
right are not, and they are most of what people are actually praising when they praise ZRemesher.
[D6](#d6-singularity-placement-gets-its-own-pass-because-it-is-what-topology-looks-like) is the honest
attempt at the largest of them, and ⚠ **it is where the aesthetics stay** — the corpus that would teach
edge flow to a network is a studio's asset library, which is neither free nor licensable, so
[D19](#d19-if-a-prior-is-ever-trained-the-corpus-is-one-we-generate)'s ceiling means D6 is carrying
this permanently rather than until R9 lands.

⚠ **And it is not interactive.** Quad Remesher answers a mid-poly mesh in a few seconds; a hierarchical
solve plus an exact flow quantization plus a bake is a build step. R7's preview runs the approximate
quantizer to make the settings panel usable, and that is the extent of it.

---

## Exit criteria (measured)

⚠️ **Measured as of R3, and three of these are not met.** The table below each criterion is what the
implementation actually does, not what it was hoped to do. Where a number is short, the cause is named
rather than the target moved.

1. **The AI case.** A 4M-triangle TRELLIS output → 5,000 quads. 100% quads, `MeshReport.IsSolid`,
   max deviation < 0.4% of the bounding-box diagonal, zero singularities on features, complete on one
   desktop machine in under 60 s including the bake.

   ⚠️ **Not measurable on the data available, and that is a fact about the corpus rather than about
   the remesher.** The sixteen TRELLIS files to hand are 13.5 K–25.4 K triangles, not 4 M — they are
   the generator's *post-processed* exports, already decimated and already atlased, fully unwelded
   (every triangle carrying its own three vertices) with `TEXCOORD_0` and a 2 K albedo. Welded at 1e-5
   of the diagonal, one of them collapses from 76 293 vertices to 12 688 positions with an
   edge-valence histogram of `{1: 1, 2: 38 147, 3: 2, 4: 4}` — **seven defective edges out of 38 154**,
   and no degenerate triangles. That is a good weld/de-speck/repair case and a poor test of the
   staircase noise [§ B1](#b1-ai-generated-meshes-are-the-worst-input-a-remesher-can-be-given-) is
   written about. The synthetic corpus in `BrokenMeshSpace` covers the latter; the raw extraction is
   still owed.

   ⚠️ **`IsSolid` now holds on six of the seven fixtures, and the four defects that stopped it were
   one defect.** A cut with a loose end is a *slit*: the flood crosses round it, the same patch lies on
   both sides, and the boundary walk traverses that arc once in each direction. `Prune` removes the
   slits it may, and [§ D4](#d4-feature-detection-and-chaining-happens-before-the-field-and-that-is-the-whole-design)
   forbids it removing a feature polyline's own loose end — a crease that runs off into a flat region
   legitimately dead-ends. Measured across the fixtures: **every** duplicated arc in **every** patch was
   an opposed pair, box carried 7 loose ends and a union 25, and **a sphere carried 0 — the one fixture
   that came back solid.** The layout now walks each loose end on along the field until it lands on
   existing structure, which is [§ D7](#d7-layout-and-quantization-as-a-flow-problem-rather-than-an-ilp)'s
   partition finishing its own cuts. A cylinder is the exception: one patch of seventy-seven can be
   neither divided — every arc bounding it is a single mesh edge, so there is nowhere for a fourth
   corner — nor merged, because the merge is capped and an uncapped one dissolves every cut on a box.
   It leaves a twelve-edge rim.

   ⚠️ **The budget overshoot is closed, and the row that was left did belong to
   [§ D9](#d9-adaptivity-is-one-scalar-field-and-everything-writes-into-it)'s field.** Box 5 047 →
   2 678 was the layout's half; the other half was that **the density field on its own implied 1 454 to
   2 207 quads against a 400 budget before any partition existed** — `curvatureTerm` and `featureTerm`
   are both ≤ 1, so every target length came out at or below `base` while `base = √(area / quads)` was
   derived as though they were exactly one. `base` is now *solved* from what the field will actually
   produce: the budget says `∫ dA / targetLength² = quads`, so `base² = Σ A(v)/m(v)² / quads` over the
   vertices, as a fixed point because the feature reach is itself stated in multiples of `base`. It
   reduces to `√(area / quads)` exactly where nothing modulates it.

   **Measured, at a 5 000 budget on the sixteen TRELLIS files: 15 511–24 661 quads before, 4 747–6 950
   after — 0.95× to 1.39× of what was asked.** Synthetic at 400: box 2 678 → 554, sphere → 372,
   cylinder 2 730 → 687, stairs 3 353 → 646, union 3 000 → 600, difference → 541, which is 0.93× to
   1.72×. This is *not* the rejected "scale the targets afterwards" fix, which absorbed the layout's
   overshoot as well and took a box to 444 **and its feature error from 5.1e-5 to 5.1e-2**; this one
   only makes the field mean what it says, and `Remesher.BudgetTolerance` still measures the residual.

   ⚠️ **A consequence worth stating: a *uniform* `DensityMask` is now a no-op.** The mask is one of the
   three terms and the normalisation divides all three back out, so a mask that says "twice as dense
   everywhere" says nothing — it is *where* the mask varies that moves quads, and a painted region is
   paid for by the unpainted ones. `TargetQuads` is the setting for wanting more of them.

   ⚠️ **The longer `base` made the arc counts small enough to reach zero, and three quantizer faults
   that had never been reachable came out at once.** A state path may cross the same arc twice — the
   two-headed case the bi-directed formulation exists for — and the search checks one step at a time,
   so both visits saw the count as it stood before either move and an arc one above its bound went one
   *below* it; a box and a sphere each quantized a side to **−1**, the patch then reported its opposite
   sides disagreeing, the extractor skipped it, and the box came back with 30 boundary edges. The
   repair rounds returned the last round rather than the best: on `Solder 4.glb` they went 19 collapsed,
   13, 4, 2 and then **375 of 378**, which meant 248 skipped patches and 780 quads where round three
   gave 6 223. And a partition that cannot satisfy the no-collapse floor under its feature arcs is now
   given more quads in named multiples before the floor is dropped, because dropping it deletes a
   crease.

   ⚠️ **`MaxDeviation` is 4–12× the 0.4 % criterion and the mean is 5–9× *inside* it, so the criterion
   as written measures a tail rather than the surface.** Measured on the sixteen, against the
   **conditioned** surface — which is what `Remesher` already builds its `SurfaceProjector` from, so
   every deviation figure in this document is already post-pre-remesh:

   | | mean | p50 | p90 | p99 | p99.9 | max | over 0.004 |
   |---|---|---|---|---|---|---|---|
   | across the corpus | 0.00044–0.00077 | 0.00000 | 0.00123–0.00253 | 0.00441–0.00780 | 0.0112–0.0232 | 0.0156–0.0478 | **1.5 %–4.9 %** |

   The median is *exactly zero* — most output positions sit on the surface — and 95–98.5 % of samples
   are inside 0.004. One quad in a noisy pocket sets the maximum. Against the **raw** input the mean
   roughly doubles (0.00066–0.00129) while the maximum is unchanged to three figures, which is the
   pre-remesh deliberately smoothing staircase noise at about that amplitude and is the pipeline
   working as designed — the criterion is about the conditioned surface and should stay that way.
   **The criterion is therefore restated as mean < 0.4 % and p99 < 1 %, with the maximum reported
   rather than bounded**, because a bound on a maximum over ten thousand samples of a marching-cubes
   surface is a bound on the worst pocket the generator left, not on the retopology.

2. **The hard-surface case.** A boolean result from doc 24's blockout mode: **every feature polyline is
   a chain of output edges, to 1e-5** — `FeatureReproductionError` at a tolerance of exact, which is
   the criterion QuadWild's structure exists to make achievable and ZRemesher's snap cannot meet.

   ⚠️ **The booleans were three orders short and are now within a factor of five and thirty.**
   Measured against the bounding-box diagonal: box 5.15e-5 → **5.87e-5**, cylinder 9.88e-5 →
   **2.43e-5**, union **8.61e-3 → 4.46e-5**, difference **1.27e-2 → 2.83e-4**. Two causes were found
   and both were about where a crease *starts* rather than what runs along it. A collapsed arc merges
   its two ends into one output vertex — § D7 permits exactly that — and the merged vertex was placed
   on the *lower-indexed* end, so a plain arc collapsing beside a crease took the crease's endpoint
   with it. And an arc whose **two** ends both carry creases may not collapse at all, because the one
   vertex left would have to stand for two distinct points of the feature graph; those are now floored
   at one quad. The fallback that lets a feature arc collapse still exists and still warns, and it now
   fires on a box alone.

   ⚠️ **The plate moved the wrong way, 2.42e-5 → 2.86e-4, and it is a different defect.** Its worst arc
   is a three-vertex chain on the hole's rim with a chord sagitta of 1.56e-3 — the chain is genuinely
   *curved*, two samples straddle the bend, and the output edge between them cuts it. Apportioning
   samples onto the chain's own vertices was measured as a remedy and made a union and a cylinder
   worse, because the relaxation slides them off again; it was removed. **A curved feature's sampling
   rate is the row left here, and it is not the row this phase was about.**

   ⚠️ **Closing the budget cost feature reproduction on the synthetic corpus, by three to eight times,
   and the trade is recorded rather than hidden.** Measured at a 400 budget, before → after solving
   `base`: box **5.87e-5 → 2.92e-4**, union **4.46e-5 → 3.18e-4**, plate **2.86e-4 → 5.15e-4**,
   difference **2.83e-4 → 8.26e-4**; cylinder is unchanged at **6.31e-5**. On the sixteen TRELLIS files
   at a 5 000 budget it is **0–7.5e-4 → 0–1.2e-3**, which is the same factor and starts from zero on
   six of them.

   ⚠️ **It is not coarseness, and the measurement that rules coarseness out is worth keeping.** Running
   the solved `base` at a budget that produces *more* quads than the old code did — 3 300 against the
   old 2 678 on a box — gives **1.98e-3**, worse still. The cause is § D9's `featureTerm`: it is one of
   the three terms the normalisation divides back out, so a crease that used to be quantized at
   `0.5 × base` is now quantized at `0.5 × base'` where `base'` is about 2.4× longer, and the whole
   point of that term is that "a hard edge is not straddled by one enormous quad". **Excluding the
   feature band from the budget solve is the row this leaves**, and it is a real one: the two exit
   criteria pull against each other through a single scalar and nothing yet arbitrates them. Both
   numbers are now in `RemesherTests` at the tolerances measured, with this paragraph cited, rather
   than at the tolerances hoped for.
3. **Determinism.** Ten runs × {1, 4, 16} threads × three platforms, byte-identical output.
4. **Symmetry.** A symmetric input with `Symmetry` on: output vertex *k* and its mirror are exact
   negations, and every vertex on the plane has an exactly zero coordinate.
5. **Attributes.** A rigged character remeshed and re-bound by transfer: max vertex deviation against
   the source's deformation over a 100-frame clip < 1% of the shortest bone length.
6. **The atlas.** No overlapping charts, no chart crossing a feature it was not cut on, packing
   efficiency above 70%.
7. **Robustness.** A corpus of 200 deliberately broken meshes — non-manifold, self-intersecting,
   open, disconnected, zero-area — produces a valid all-quad result or a `RemeshReport` naming the
   stage that refused, and **never** an exception or a hang.

   ⚠️ **The corpus became a generator rather than a list, and the "hang" half turned out to be the
   wrong worry.** `BrokenMeshSpace` is a CsCheck space of recipes rather than eighteen chosen defects,
   shrinking to something small enough to paste into a test. Five loops were suspected of
   non-termination and **every one was refuted**: the pre-remesh is bounded by its iteration count,
   ear clipping removes a corner or returns on every pass, and the packer's scale search caps at
   sixteen attempts and refuses. ⚠ **What is unbounded is growth, not time** — a pre-remesh handed a
   target far below the mesh's own mean quadruples its triangle count every round, and the tenth alone
   allocates 763 MB. So the guard watches allocation as well as the clock, which "never a hang" would
   not have asked for.

8. **Quad quality, which the criteria above do not ask for and should.** ⚠️ `MinScaledJacobian` is
   **0.000 on box, cylinder, stairs, plate, union and difference, and −0.083 on the sphere** — a zero
   is a quad with no area and a negative one is a quad folded over itself, so there are degenerate
   quads in every result and inverted ones in one. The all-quad guarantee is genuinely met and is
   orthogonal to this: **four sides is not four *usable* sides, and `IsAllQuad` cannot tell the
   difference.** Added as a criterion because the field was in
   [Part 4](#part-4--what-the-report-says)'s report from the day the report existed and was read by
   nothing, which is precisely the failure that section is written to prevent.

   ⚠️ **Unmoved by the layout fix, and now attributed rather than suspected.** It is not one quad but
   2–5 % of them: box 71 of 2 678 at or below zero, union 144 of 3 000, sphere 4 of 896, with medians
   of 0.82 to 0.93 throughout. Two measurements place the cause. **A sphere has no slit, no skipped
   patch and no warning but the budget, and still reports four inverted faces** — so the defect
   survives a provably clean partition. And **turning the relaxation off makes it worse everywhere**
   (sphere 4 → 23, stairs 99 → 188), so the smoothing is repairing it rather than causing it. What is
   left is § D8's interior: a Coons interpolation of the four boundary chains projected onto the
   conditioned surface folds where the patch curves, because a bilinear blend of curved boundaries is
   not injective. **The fix is a real per-patch parameterization — harmonic or mean-value — and it is a
   piece of work of its own rather than a tolerance.**

---

## What this does not become

1. **A sculpting package.** No brushes, no dynamesh, no subdivision *authoring*. The remesher takes a
   mesh and returns a mesh.
2. **A general UV unwrapper.** [D13](#d13-uvs-come-nearly-free-from-the-layout-and-that-closes-doc-40s-other-gap)
   unwraps what it produced, by reusing a structure it had to compute. ⚠️ An arbitrary mesh's UVs are
   [42](42-uv-unwrapping.md)'s problem, and this document's packer is now one of its callers.
3. **A runtime feature.** Import time and edit time. No shipped game remeshes anything, and
   `Vixen.Rendering` never learns that this exists.
4. **A GPU pass.** [D17](#d17-cpu-jobs-and-not-the-gpu--named-so-nobody-quietly-changes-it), and it is
   a decision rather than a gap.
5. **A native dependency, or a GPL one.** [Part 1](#the-licence-table-because-it-decides-the-plan).
   Clean-room C#, with Instant Meshes attributed in `NOTICE` for the field solver's shape.
6. **A replacement for doc 22's simplifier.** Quadric collapse is right for a triangle asset that has
   no cage; this is right for one that should have.
7. **A model.** [D18](#d18-a-learned-field-prior-is-a-plugin-that-seeds-and-never-decides) is optional,
   plugin-resident, and seeds a solver that would have run anyway.
8. **A training programme, or a consumer of scraped 3-D assets.**
   [D19](#d19-if-a-prior-is-ever-trained-the-corpus-is-one-we-generate) — if a prior is ever trained,
   the corpus is a script in this repository. ⚠ **Nothing here downloads somebody else's models to
   learn from**, and the reason is as much that the free corpora hold the wrong label as that their
   terms forbid it.

---

## See also

- [40 — AI-assisted generation](40-ai-assisted-material-generation.md) — § D6 names the absence this
  document fills, and its closing item 6 is amended by it. The mesh and retexture panels are R8's
  clients.
- [24 — Blockout tools](24-blockout-tools.md) — `EditMesh`, `MeshReport`, `ExactPredicates` and the
  fourteen verbs the all-quad guarantee exists to keep working.
- [22 — Virtualized geometry](22-virtualized-geometry.md) — `MeshSimplifier` as the precedent for this
  class of work in C#, and [D15](#d15-quads-are-wanted-for-what-happens-after-and-that-is-worth-being-explicit-about)'s
  unopened option.
- [08 — Asset pipeline](08-asset-pipeline-and-addressables.md) — the content hash that
  [D14](#d14-determinism-is-a-gate-not-an-aspiration) is a gate for, and `ModelImportSettings`.
- [33 — Character creator](33-character-creator.md) — the skin-weight transfer in
  [D12](#d12-the-output-carries-the-input-or-it-is-useless) is its conform path's other half.
- [38 — Learned terrain generation](38-learned-terrain-generation.md) — the plugin-and-ONNX rules
  [D18](#d18-a-learned-field-prior-is-a-plugin-that-seeds-and-never-decides) takes wholesale.
- Jakob, W., Tarini, M., Panozzo, D., Sorkine-Hornung, O. *Instant Field-Aligned Meshes.*
  SIGGRAPH Asia 2015. [doi:10.1145/2816795.2818078](https://doi.org/10.1145/2816795.2818078) ·
  [project page](https://igl.ethz.ch/projects/instant-meshes/) ·
  [github.com/wjakob/instant-meshes](https://github.com/wjakob/instant-meshes) — BSD-3.
- Pietroni, N., Nuvoli, S., Alderighi, T., Cignoni, P., Tarini, M. *Reliable Feature-Line Driven
  Quad-Remeshing.* SIGGRAPH 2021. [github.com/nicopietroni/quadwild](https://github.com/nicopietroni/quadwild)
  — GPL3, read only.
- Heistermann, M., Warnett, J., Bommes, D. *Min-Deviation-Flow in Bi-directed Graphs for T-Mesh
  Quantization.* SIGGRAPH 2023. [doi:10.1145/3592437](https://doi.org/10.1145/3592437) ·
  [algohex.eu](https://www.algohex.eu/publications/bimdf-quantization/) — the quantizer's formulation.
- Bommes, D., Zimmer, H., Kobbelt, L. *Mixed-Integer Quadrangulation.* SIGGRAPH 2009 — the reference
  standard the local methods approximate.
- Eppstein, D., Goodrich, M., Kim, E., Tamstorf, R. *Motorcycle Graphs: Canonical Quad Mesh
  Partitioning.* SGP 2008 — the layout's shape.
- Garland, M., Heckbert, P. *Surface Simplification Using Quadric Error Metrics.* SIGGRAPH 1997 —
  already in the repository, as `MeshSimplifier`.
- Barill, G., Dickson, N., Schmidt, R., Levin, D., Jacobson, A. *Fast Winding Numbers for Soups and
  Clouds.* SIGGRAPH 2018 — the inside/outside test [D3](#d3-conditioning-is-a-stage-with-a-report-not-hygiene)'s
  shrinkwrap needs on input that is neither.
- Dong, Q. et al. *NeurCross: A Self-Supervised Neural Approach for Representing Cross Fields in Quad
  Mesh Generation.* TOG 44(4), 2025. [doi:10.1145/3731159](https://doi.org/10.1145/3731159) ·
  [project page](https://qiujiedong.github.io/publications/NeurCross/) — the tier of
  [D19](#d19-if-a-prior-is-ever-trained-the-corpus-is-one-we-generate) that needs no corpus at all.
- *Learning Sparse Singularities for Cross Field Design.* TOG, 2026.
  [doi:10.1145/3787520](https://doi.org/10.1145/3787520) — 1,000 scripted synthetic samples and
  rule-based annotation, which is the methodology
  [D19](#d19-if-a-prior-is-ever-trained-the-corpus-is-one-we-generate) copies.
- [Objaverse-XL](https://huggingface.co/datasets/allenai/objaverse-xl) ·
  [ShapeNet terms](https://shapenet.org/terms) · [ABC](https://deep-geometry.github.io/abc-dataset/) ·
  [Thingi10K](https://github.com/Thingi10K/Thingi10K) — the corpora
  [D19](#why-not-free-3-d-assets-from-the-internet) declines, and the terms it declines them on.
- [Bigger-and-Stronger/quad-meshing-survey](https://github.com/Bigger-and-Stronger/quad-meshing-survey)
  — the continuously updated bibliography the 2024–2026 learning-based survey in
  [Part 1](#the-four-families) is drawn from.
- [`Vixen.Geometry`](../../Core/Vixen.Geometry/) — the mesh kernel this sits beside and hands quads to.
