<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 48 — Material authoring: a texture graph, a layer stack and a paint tool

⚠️ **Extends [36](36-an-extensible-editor.md), [40](40-ai-assisted-material-generation.md) and
[23](23-bindless-materials.md); consumes [41](41-automatic-retopology.md) and
[42](42-uv-unwrapping.md); takes [39](39-standard-frame-and-render-presets.md)'s authoring shape.**

Doc 40 built a seam for *asking a model* for a material and, in the same breath, wrote down that its
most valuable phase has no AI in it — a deterministic image kernel that every provider needs
downstream anyway. That phase was never built, and the reason to build it is no longer the one doc 40
gave. **It is the whole product**: Substance Designer, Substance Painter and InstaMAT are that kernel,
a graph over it, a layer stack over that, and a brush.

**The claim this document has to earn.** An artist opens a `.vxtexgraph`, wires a noise into a warp
into a blend, sees every node's result under it, exposes four parameters, and saves. Somebody else
drags that graph onto a mesh as a layer, paints a mask over it with a brush, adds a rust layer whose
mask is a generator reading a curvature map the editor baked, and presses **Bake** — and what lands in
`Assets/` is a set of ordinary PNGs and a `.vxmat` that the existing importer, the existing content
build and the existing bindless material path already understand. **None of it is compiled into the
editor.** It is a plugin, loaded from a folder, through the door doc 36 built for a third party.

⚠ **The last sentence is the reason this document is worth writing rather than only the feature.**
Doc 36 § F2's finding was that the editor hard-referenced twelve feature assemblies, so the plugin
surface had never had to be sufficient for anything the editor itself does. Terrain and Water answered
that for a viewport mode and some panels. **Nothing has yet asked the plugin surface for a document
type, a GPU evaluator, an asset writer and a brush at once**, and if it is not sufficient for those,
the application-framework claim in the README is smaller than it reads.

---

## Part 0 — What the three references actually are

Surveyed 2026-09-04 from vendor documentation. They are commonly described as three of a kind and they
are not: **two of them are different products and the third is both**.

### Substance 3D Designer — a compositing graph that outputs images

A directed graph of image operations. Its **atomic** nodes are the whole vocabulary — Bitmap, Blend,
Blur, Channel shuffle, Curve, Directional blur, Directional warp, Distance, Emboss, FX-Map, Gradient,
Gradient map, Grayscale conversion, HSL, Levels, Normal, Pixel processor, SVG, Sharpen, Text,
Transformation 2D, Uniform colour, Value processor, Warp — and every one of the several hundred nodes
in its library is a **compound built out of those**, shipped as content rather than as code.

Three of its ideas are load-bearing and two of them are usually missed:

| | |
|---|---|
| **Function graphs** | A *parameter* is not a number, it is a small graph evaluated per instance. This is how one node exposes "amount" and means eleven things downstream |
| **Pixel processor** | A function graph run per texel, in parallel, with no access to a neighbour. The escape hatch that stops the atomic set having to be complete |
| **FX-Map** | A recursive quadrant splatter — the pattern-placement engine, and by the vendor's own description the most complex node in the application |

Its output is `.sbs` (authoring) and `.sbsar` (cooked, parameterised, **evaluated at runtime by the
Substance engine** in the host application). Outputs carry a **usage** — `baseColor`, `normal`,
`roughness` — which is what lets a graph drop into a renderer without wiring.

### Substance 3D Painter — a layer stack over a mesh

Nothing to do with a graph, from the artist's side. A **texture set** per material slot of the mesh,
a **channel** per map, and a **layer stack** per texture set. Each layer has a mask, and the mask has
a stack of its own — a paint mask, a *generator*, a filter, an **anchor point** that reads another
layer's result.

It runs on **mesh maps** baked from the geometry: ambient occlusion, curvature, thickness, position,
world-space normal, tangent normal, bent normals, height, ID and opacity. Those maps are what makes a
"generator" work — dirt in cavities is curvature multiplied by occlusion, and edge wear is curvature
with a histogram scan on it. Effects bind them by **usage or identifier**, automatically, when the
naming convention is followed.

⚠ **Painter's smartness is entirely in the bakes.** Without mesh maps its generators have nothing to
read and its smart materials are flat colours. This is the single most transferable finding in Part 0,
and it points at machinery this repository already has — see [B3](#b3-mapbaker-bakes-two-of-the-ten-mesh-maps-).

### InstaMAT — both, and the more modern architecture

One **Element Graph** that mixes mediums — images, meshes, point clouds — rather than a separate graph
type per medium, with an **nPass** variant that persists data across passes so scatters and simulations
are ordinary graphs. Over it, two artist-facing project types: **Material Layering** (drag materials
onto a stack, blend, and the result stays procedural) and **Asset Texturing** (the Painter workflow,
with **layer references** so one layer's authored content appears in many places). Its 2026 release
added curve brushes, radial symmetry, stroke smoothing, and 2D painting directly on UVs with UDIM.

⚠ **InstaMAT is the shape to copy, not Substance's split.** Two applications with two file formats and
a hand-off between them is a product decision from 2010 that its own successor did not repeat.

### The one idea under all three

| | Designer | Painter | InstaMAT |
|---|---|---|---|
| Pixel graph | ✅ the product | — (consumes cooked ones) | ✅ Element Graph |
| Layer stack on a mesh | — | ✅ the product | ✅ Layering / Asset Texturing |
| Brush | — | ✅ | ✅ |
| Bakers | ✅ (from a mesh input) | ✅ the foundation | ✅ |
| Runtime re-evaluation | ✅ `.sbsar` | — | partial |

⚠ **A layer stack is a graph presented as a stack, and an anchor point is the proof.** A mask that
reads another layer's evaluated output is an edge in a DAG; the moment that exists, the stack is not a
list. Every one of these tools ends up with one evaluator and two front ends over it, and the ones that
built two evaluators regretted it. **That is [D1](#d1-two-front-ends-one-evaluator-and-the-stack-compiles-to-the-graph).**

---

## Part 1 — What Vixen already has

Audited rather than assumed. This is the half of the document that decides the cost, and it is much
better than doc 40's Part 1 suggested.

| | Where | What it buys |
|---|---|---|
| A node-graph framework | [`Vixen.Editor.NodeGraph`](../../Editor/Vixen.Editor.NodeGraph/README.md) | The model, the generated registry, `NodeGraphCompiler<T>`, port typing, sub-graphs, undo per gesture, search-to-create, auto-layout, clipboard, **and the per-node preview swatch** (`NodePreview`, `INodePreviewSource`) |
| A second graph already built on it | [`Vixen.Editor.ShaderGraph`](../../Editor/Vixen.Editor.ShaderGraph/README.md) | The exact split this wants: a compiler that knows nothing about a project, a preview renderer that takes an `IGraphicsDevice`, and a panel elsewhere |
| A shader language with compute | [`Raven`](../../Raven/README.md) — `README.md:577`, `:612` | Workgroup size on the stage attribute, `RWBuffer<T>` and storage images. **A node kernel is a Raven compute shader** — ⚠ an *editor* one, which `CheckShaders` reaches only through a hand-kept list of four; see § D1 |
| Offscreen rendering inside the editor | `Editor/Vixen.Editor.App/ThumbnailSurface.cs`, `ShaderGraphPreviewRenderer.cs` | A device in an editor assembly, drawing into a target, with no window |
| Image containers and codecs | [`Vixen.Core.Imaging`](../../Core/Vixen.Core.Imaging/README.md) | `TextureData`, `MipChain`, `PngCodec`, `Ktx2`, `BlockCompression`, `DataFormatDescriptor` |
| Texture import | `Editor/Vixen.Editor.Assets/Textures/` | `TextureImporter`, `StbImageDecoder`, `DdsDecoder`, and `SpriteSlicing` — doc 40's cited precedent for a pure pixel kernel tested with images built in a test |
| An atlas rasteriser and a cage bake | `Core/Vixen.Geometry.Remeshing/Transfer/` — `AtlasRaster.cs`, `SourceSurface.cs`, `MapBaker.cs:142` | Conservative texel coverage, gutter dilation, opposed ray casting with a fallback, and a triangle tree over the source. **The expensive half of a mesh-map baker** |
| UV unwrapping | [`Vixen.Geometry.Uv`](../../Core/Vixen.Geometry.Uv/README.md) | Charting, flattening, packing — so a mesh with no UVs is not a refusal |
| A brush, a stroke and a falloff | `Core/Vixen.Terrain/BrushStroke.cs`, `BrushFalloff.cs`, `TerrainPaint.cs`; `Editor/Vixen.Editor.Terrain/TerrainPaintCommand.cs` | Pointer → stamp → kernel → **one undo entry per drag**, already solved once |
| A plugin host, and two features that use it | [`Vixen.Editor.Plugin`](../../Editor/Vixen.Editor.Plugin/README.md), `TerrainModule`, `WaterModule` | Commands, panels, modes, layouts, keybindings, contributions, `Owns`/`With`, collectible unload |
| A material asset and three sampling features | `MaterialAsset.cs:133` (`.vxmat`), `MaterialFeatures.cs:93`, `:220`, `:288` | Base colour, tangent normal and a packed ORM all sample from the bindless table today |
| The authoring pattern to copy | [doc 39](39-standard-frame-and-render-presets.md), [doc 40 § D4](40-ai-assisted-material-generation.md) | A simple surface that *is* a graph, and an **Explode** that hands over the real one, one-way |

⚠ **Doc 40 § B1 is out of date and this document corrects it.** It said thirteen material features and
only one names a map. Three name one now — `TexturedMetalRoughnessFeature`, `TexturedNormalMapFeature`
and `TexturedOrmFeature` — and `WorldRenderer.cs:1058` pairs all three into the bindless table. The
gap that remains is real but different, and it is [B1](#b1-a-layer-stack-cannot-ship-as-a-live-layered-material--for-the-runtime-path-only) below.

---

## Part 2 — What blocks it

Six. Two are ⛔ and both are smaller than they look.

### B1. A layer stack cannot ship as a live layered material ⛔ *for the runtime path only*

`MaterialLayersFeature` (`MaterialFeatures.cs:511`) blends N metal-roughness layers — and
`MaterialLayerValue` (`:487`) carries `Weight` as a **`float` constant**. Its own remarks say where the
weight comes from is the caller's business; no caller supplies one from a texture, and there is no
`TexturedMaterialLayersFeature`. `BlendFeature` (`:553`) has the same shape one level up.

⚠ **So "stack materials to make a new one" cannot mean "ship the stack".** A ten-layer stack with
painted masks is not expressible as a material this renderer can draw. It is expressible as **the
images that stack evaluates to**, which is what every one of the three references ships in practice
anyway, and which is [D4](#d4-the-output-is-a-file-and-that-is-what-makes-determinism-a-non-question).

Two further rows in the same table, for completeness: there is no textured **emissive**, **height** or
**opacity** feature, and the shader-parameter-to-texture pairing is a hand-kept list of exactly three
with unchecked completeness — [#371](https://github.com/Rikarin/Vixen/issues/371) — so the fourth
sampling feature anybody writes shades a surface with a checker and says nothing. A graph-authored
material's textures never reach the table at all,
[#493](https://github.com/Rikarin/Vixen/issues/493).

> ⚠ **Landed.** [M11](#m11--the-runtime-layering-gap--075-em--optional-and-separable) closed all of
> that except height: `TexturedMaterialLayersFeature` takes its weights from a splat map,
> `TexturedEmissiveFeature` and `TexturedOpacityFeature` exist, `MaterialPairingInventoryTests` reads
> `Raven/Library` for every shader inheriting `MaterialTextures` and asserts a pairing entry for each,
> and `AssetMaterialSource.Pair` adds a graph's own `Maps`. **Height is deliberately not built** —
> parallax, height-blending and true displacement are three different features wearing one name and
> only the middle one is small, [#615](https://github.com/Rikarin/Vixen/issues/615). ⚠ And a second
> defect came out with it: nothing registered `MaterialKeys.LayerCount` in
> `MaterialRenderFeature.PermutationKeys`, so a three-layer material had always resolved the variant
> compiled for two.

**None of that blocks the authoring tool**, because the tool writes files. It blocks the *optional*
last phase, [M11](#m11--the-runtime-layering-gap--075-em--optional-and-separable), and it is named
here so nobody discovers it in the middle of M7.

### B2. There is no image-processing kernel anywhere in the repository ⛔

`Bitmap` (`Core/Vixen.Core.Imaging/Bitmap.cs:23`) is a `readonly record struct` of width, height and
bytes, with one method on it: `Offset(x, y)`. `MipChain` downsamples, `BlockCompression` compresses,
`SpriteSlicing` finds rectangles. **There is no blur, no blend, no levels, no curve, no warp, no
gradient map, no distance transform, no histogram operation, anywhere.**

⚠ **This is the largest single item in the plan and it is also the most mechanical.** Each operation is
a page of arithmetic with a closed-form test. What makes it large is that there are about thirty of
them and every one needs a golden, a sabotage and a scale-invariance check.

### B3. `MapBaker` bakes two of the ten mesh maps 🟡

`MapBaker.Bake` (`MapBaker.cs:142`) fills a normal map and a signed displacement map. The seven the
generators actually read — ambient occlusion, curvature, thickness, position, world-space normal, bent
normal, ID — are **not there**, and neither is the hierarchy that would make them fast.

⚠ **But the part that is hard is done.** Conservative texel coverage (a texel is covered when the chart
touches its *square*, which is what stops a hole at every chart edge), gutter dilation, the opposed
ray cast with a closest-point fallback, the `SearchRadius`-as-a-fraction rule, and the triangle tree
in `SourceSurface` are all built and documented in
[`docs/guide/engine/map-baking.md`](../guide/engine/map-baking.md). **Each new map is a different
measurement at a texel whose position, normal and tangent frame the existing raster already hands
over.** That is why M6 is 1.25 EM and not 3.

### B4. A GPU evaluator and a deterministic content build pull in opposite directions 🟡

An image graph at 2K with forty nodes is forty full-resolution passes. On the CPU that is seconds per
edit and the tool is unusable; the references are all GPU for exactly this reason. But floating-point
results differ between vendors and drivers, and the content build hashes what it is given — so a graph
evaluated on the build machine would churn the cache for every artist on a different card.

⚠ **The temptation is a GPU path and a CPU path, and it must be refused.** This repository has been
bitten by precisely that: a parity test that checked a C# re-implementation rather than the shader. Two
implementations means the tested one is the one nobody ships. [D3](#d3-one-evaluator-on-the-gpu-in-raven-and-no-cpu-twin)
and [D4](#d4-the-output-is-a-file-and-that-is-what-makes-determinism-a-non-question) are the pair that
resolves this, and the resolution is that **the content build never sees a graph**.

### B5. Node count is the reference tools' moat, and it cannot be matched in C# 🟡

Substance's library is several hundred nodes. Writing several hundred `[Node]` classes is not a plan,
it is a decade. ⚠ **The vendor already answered this and the answer is in Part 0**: the atomic set is
about two dozen, and everything else is a compound built from atomics and shipped as content. So the
deliverable is two dozen kernels plus a **sub-graph mechanism that is good enough that the library is
authored in the tool** — which `NodeGraphModel` has, by inlining, today.

### B6. Nothing in the editor views a 2D image at zoom, and the panels need one 🟡

`TexturePreview` (`Editor/Vixen.Editor.AssetEditors/Importing/TexturePreview.cs`) shows an imported
texture; `NodePreview` draws a swatch under a node. Neither is a pannable, zoomable, channel-isolating
image view with a UV overlay, which both front ends need and the 2D paint view needs most. It is a
`Vixen.Ui` control and it is small, but it is not free, and it belongs to whoever builds M4 rather than
being discovered in M9.

---

## Part 3 — The design

### D1. Two front ends, one evaluator, and the stack compiles to the graph

```
Editor/Vixen.Editor.TextureGraph   TexturePlan · TextureOp · the evaluator · the [Node] library ·
                                   TextureGraphCompiler : NodeGraphCompiler<TexturePlan> · previews
        └── Shaders/*.rvn          § 4.11's 41 kernels + the chain dispatches three nodes need
                                   Holds a device; knows nothing about a project, a document or a panel.
Editor/Vixen.Editor.Texturing      THE PLUGIN: documents, panels, the layer stack, the brush,
                                   the bake, the asset writes. TexturingModule : IEditorPlugin.
Core/Vixen.Geometry.Remeshing      + seven mesh-map bakers on the existing raster — the ONLY Core change
```

⚠ **All of it is editor-side, and `Core/` gains nothing but the bakers.** Four reasons, and the first
is the one that makes the rest free:

1. **The CLI already reaches into `Editor/`.** `Tools/Vixen.Cli/Vixen.Cli.csproj:62-63` references
   `Vixen.Editor.Assets` and `Vixen.Editor.Core`, because the content pipeline lives there. So a
   headless `vixen texture bake` costs exactly nothing by being an editor assembly — which is the
   fact that usually decides this question the other way.
2. **A shipped game never evaluates one.** § D4 means the runtime sees PNGs and a `.vxmat`, so putting
   forty-odd compute kernels and their SPIR-V in `Core/` would grow every build for a capability no
   build uses. This is doc 40 § D9's decision — *inference is an editor assembly, not a core one* —
   reached by the same argument for the deterministic half doc 40 left out.
3. **`Core/` is the wrong altitude for a device-bound image kernel** whose only consumers are a panel
   and a content-authoring command. `Vixen.Rendering.PostFx` is in `Core/` because a frame draws it;
   nothing in a frame draws this.
4. **Editor-only Raven already exists.** `Editor/Vixen.Editor.Host/Shaders/*.rvn` are compiled and
   enumerated by `EditorEffects`, so the kernels live beside the editor's own shaders and not in
   `Raven/Library`.

⚠ **The one real cost of that placement, and it must not be discovered late.** `CheckShaders` has an
editor half already — `Build.Shaders.cs:140`'s `EditorSources`, which recompiles a standalone `.rvn`
beside its project and diffs the committed module — so the kernels are *gateable*. But that list is
**four hand-written tuples**, and its own remarks say what a source it does not know about costs:
*"a source this gate did not know about is a source somebody can edit without recompiling, which is
exactly the state this whole target exists to make impossible"* — which has already fired once, on
two divergent copies of `Ui.rvn` that each matched the module beside it. **Forty-odd kernels are not
forty-odd more tuples.** M1 makes that half read its folders, which is
[#371](https://github.com/Rikarin/Vixen/issues/371)'s and
[#512](https://github.com/Rikarin/Vixen/issues/512)'s shape a third time, and exit criterion 3 is the
same assertion one layer up.

✅ **Landed ahead of M1, and the kernels can be written against it.** `EditorSources` is gone:
`Build.Shaders.cs`'s `DiscoverEditorSources` walks the tree and compiles every `.rvn` with a `.spv`
for one of its shaders committed beside it, so a kernel dropped into
`Editor/Vixen.Editor.Host/Shaders` is gated by the next run with no edit to the build
([#564](https://github.com/Rikarin/Vixen/issues/564)). Two things a kernel author should know: a
kernel that `import`s a library package is **refused by name**, not skipped — it is not standalone,
and belongs in `EditorShaders` with its `--source` closure — and the walk's floor is
`EditorSourceFloor`, which exists so a walk that has gone blind fails rather than deriving an empty
list and printing success.

⚠ **The mesh-map bakers are the exception and they stay in `Core/`** — `MapBaker` is already there, it
is CPU arithmetic with no device in it, and its own guide says it runs at import time inside a content
build. Moving it out to keep this document tidy would be the wrong direction.

⚠ **The layer stack does not get an evaluator of its own.** `LayerStackDocument` compiles to a
`TexturePlan` — the same artefact the graph compiler produces — so a mask generator, a blend mode and
an anchor point are the same three ops whether they came from a stack or from a wire. A second
evaluator is how the two front ends come to disagree about what "overlay" means.

⚠ **And a stack **explodes** into a graph, one-way, exactly as doc 39's frame and doc 40's panel do.**
The artist who outgrows the stack gets the real graph — with comments — rather than a simplified
picture of it.

### D2. A texture graph is not a shader graph, and the two stay apart

They will be asked to merge. The answer is no, and the reason is not taste:

| | `Vixen.Editor.ShaderGraph` | `Vixen.Editor.TextureGraph` |
|---|---|---|
| Evaluated | per pixel, per frame, on the mesh | once, at author time, into an image |
| Output | Raven source, composed into a pass | pixels, written as files |
| A node's cost | must be nearly free | 4 ms is fine |
| Neighbourhood access | none — a fragment sees one texel | **the whole point** — blur, warp, distance, flood fill |
| Resolution | the framebuffer's | the graph's, declared, inherited |
| What it *is* | a material's shading | a material's textures |

⚠ **A blur cannot exist in a shader graph and a lighting model cannot exist in a texture graph.** One
node vocabulary spanning both would be a vocabulary where two thirds of the nodes are invalid in
whichever graph you happen to be in, which is a type system nobody can see. They share
`Vixen.Editor.NodeGraph` and they share nothing else.

### D3. One evaluator, on the GPU, in Raven, and no CPU twin

Every atomic operation is a Raven compute shader in `Vixen.Editor.TextureGraph/Shaders/`, dispatched
over a storage image, and gated by the folder-reading check D1 owes. The plan runner allocates
from a pool of intermediate images, evaluates in the compiler's topological order, and frees an
intermediate when its last reader has run.

⚠ **There is no CPU implementation of any node, and a headless bake needs a real device.** B4's
argument, and it has a second edge worth stating: `--vixen-offscreen` is what buys a real GPU device on
this repository's headless runs, and without one everything falls back to the Null device, exits 0 and
prints healthy counters. **A texture-graph test that passes on the Null device has proved that a black
image equals a black image.** Every device test in this area asserts the adapter name first, and the
suite skips loudly rather than passing quietly.

⚠ **The CPU is used for exactly one thing: comparing against arithmetic in a test.** A Gaussian blur's
impulse response, a levels curve at three points, a distance transform on a single lit texel — those
are closed forms asserted against the *shader's* output, not against a second implementation of it.

### D4. The output is a file, and that is what makes determinism a non-question

**The content build never evaluates a graph.** A bake happens on the artist's machine, at the moment
they press the button, and writes PNGs (or KTX2 for anything over 2K) plus a `.vxmat` into `Assets/`.
From there it is an ordinary texture asset: the existing `TextureImporter`, the existing `.meta`, the
existing streaming, the existing block compression, and a file the artist can open in Photoshop and a
reviewer can diff.

This is doc 40 § D7's provenance block, reused verbatim in shape:

```yaml
texturing:
  source: Materials/ship-hull.vxtexgraph   # or .vxlayers
  outputs: [baseColor, normal, orm, height]
  resolution: 2048
  parameters: { rust: 0.6, tiling: 3, seed: 41823 }
  adapter: "AMD Radeon RX 7900 XT"         # recorded, never asserted
  writtenDigest: sha256:…                  # so a painted-over map is detectable
  at: 2026-09-04T…
```

⚠ **`adapter` is recorded and never compared.** A re-bake on the same machine is byte-identical and
that *is* asserted; a re-bake on a different card is not, and pretending otherwise would make the first
artist with a different GPU a bug report. What the digest buys is the honest thing: a file whose bytes
no longer match what the graph would produce is *flagged*, not silently overwritten — because the most
common reason for the mismatch is that somebody painted on it.

⚠ **This is also the answer to "why not `.sbsar` at runtime".** See
[What this does not become](#what-this-does-not-become).

### D5. Forty-one atomic kernels, and everything else is a compound

The full list is [Part 4](#part-4--the-node-catalogue) and it is not repeated here — a vocabulary
written down twice is two lists that have to agree, which is the argument `NodeGraph`'s own README
makes about declaring a port's type beside its field.

⚠ **This heading said forty-four for five batches, and the arithmetic was wrong in the way § 4.11
describes**: it counted every row of Part 4's tables as a kernel while three of those rows said in
their own cells that they are not compute shaders. The design is unchanged — the number is a *reading*
of Part 4 and only ever was — but a heading is what people quote, and this one was quoted into two
READMEs and a milestone. [§ 4.11](#411-the-count-and-the-claim-it-corrects) is where the correction
lives.

⚠ **The kernels are the C#; the several hundred nodes are content.** A `Scratches`, a `Rust`, a
`Dirt`, a `Metal Edge Wear` is a `.vxtexgraph` in a shipped library folder, authored in the tool,
reviewed as a file. Anything that has to be written in C# to be fast is a bug in the atomic set, and
that is the standing test of whether the set is right.

⚠ **Noise takes a seed and the seed is part of the graph.** A procedural texture whose output changes
between runs is not a source asset. This costs one hash function and saves the entire class of "the
bake differs and nobody knows why".

### D6. The pixel processor is Raven, not a second expression language

The escape hatch — arbitrary arithmetic per texel — is a node whose setting is a **Raven expression
body**, compiled by the real Raven compiler into the plan's own kernel, with diagnostics mapped back to
the node. Not a hand-rolled expression evaluator, not a scripting language, not a nested "function
graph" of forty tiny nodes.

⚠ **The machinery for the mapping already exists and already has a UI caller.** `NodeDiagnostic`
carries a `NodeSpan`, `RavenEmitter` counts the lines it writes, and `ShaderGraphView.vxml:352` reads
`SourceNodeDiagnostics` so a Raven complaint names a node the author can select. This node is that
mechanism's second consumer, which is the cheapest possible proof it was built at the right altitude.

⚠ **Refusing Designer's function graph is a real divergence and it is deliberate.** A function graph is
a visual programming language, it is where every Substance tutorial loses half its audience, and Vixen
already has a typed, diagnosed, tested language for exactly this arithmetic. Where Designer exposes a
*parameter* as a function graph, a Vixen graph exposes it as a Raven expression over the graph's other
parameters — one line, in a field, in the inspector.

### D7. The scatter is a node with a count, not a recursive quadrant machine

FX-Map's recursion is refused. A `Splatter` node takes a pattern input, a count, and per-instance
distributions for position, rotation, scale, colour and mask, evaluated from a seed; `Tile Sampler`
is the same machine on a grid with jitter. Between them they are what nearly every FX-Map in practice
is doing.

⚠ **What is lost is genuine and worth naming**: unbounded recursive subdivision, and patterns whose
instance count depends on the pattern. What is bought is a node whose cost is knowable before it runs
and whose output is a golden image rather than a stack overflow.

⚠ **InstaMAT's nPass is the general answer and it is deferred, not refused.** A plan that can run a
sub-plan N times with its own output as an input is a small change to the runner (an op that names a
loop bound and a feedback image) and a large change to the compiler's typing. It is [M10](#m10--the-library-smart-materials-and-export--10-em)'s
optional half, and simulations and erosion are what it would unlock.

### D8. Resolution is a graph property, relative everywhere, and radii are in texels-at-base

The graph declares a base resolution. Every node is *relative* to it — `×1`, `×½`, `+1 mip` — and only
a Bitmap input is absolute. A filter's radius is expressed in **texels at the base resolution** and
scaled by the evaluator.

⚠ **This is doc 42 § B4's bug with a two-year fuse, in a second place.** A blur radius stored as
absolute texels looks right at the resolution it was tuned at and is half as wide at 4K — so a graph
authored at 1K and shipped at 4K is a *different material*, and nobody associates the change with the
resolution field. Storing it as a fraction of the image instead has the mirror-image failure at
non-square resolutions. Texels-at-base, with the base written in the file, is the only form where both
questions have one answer.

⚠ **And it is testable, which is the point**: bake at 1K and at 4K, downsample the second, and require
them to agree within a small tolerance. A node that fails that has a resolution bug and no other test
in this plan would have found it.

⚠ **Two nodes do not keep this promise, and the plan now says so rather than baking them wrong.** A
node whose op *count* depends on the baked extent — [4.5](#45-analysis--3-kernels)'s `Distance`
(`log2(n)` ping-ponged dispatches) and `Flood Fill` (a budget chosen against the mask's size), plus
`Auto Levels`' reduction — is compiled *for one bake*. Re-baking is expressed by building a plan with
the same `Ops` and a different `BakeLevelOffset`, and doing that to one of these chains leaves too few
halvings: a distance field wrong at long range, which looks like a soft field rather than like a bug.
Every op of such a chain therefore carries `TextureOp.EmittedForExtent`, and `TexturePlan.Validate`
refuses the list at any other extent — [#689](https://github.com/Rikarin/Vixen/issues/689). **The
honest reading is that a plan is a compiled artefact**: the front end re-emits, and the promise above
is a promise about the *graph*.

### D9. A published graph is a node, and its parameters are its ports

A `.vxtexgraph` with `Input` and `Output` nodes is usable inside another graph, as a node, with its
exposed parameters as settings. `NodeGraphModel`'s sub-graphs are **inlined** rather than called, which
is the right choice here: the plan is flat, the intermediate images are pooled across the whole
expansion, and an inlined node's diagnostics are already re-addressed to a node the author has.

Exposed parameters are `[Setting]`-shaped: a name, a type, a default, a range, and a group. They are
what a layer's inspector shows when the layer is a graph, and what a `.vxsmartmat` overrides.

### D10. The layer stack, and the four kinds of layer

A `.vxlayers` document is, per texture set:

| Layer kind | |
|---|---|
| **Fill** | A constant, a texture, or a graph, projected — UV, triplanar, or planar |
| **Paint** | Strokes, in the atlas, stored as pixels ⚠ not as strokes — see below |
| **Filter** | An adjustment over everything under it: levels, HSL, blur, a graph with an `Input` |
| **Group** | A stack with one mask, which is how an artist keeps twenty layers legible |

Every layer carries: an opacity, a blend mode, a per-channel enable (so a layer writes roughness and
not base colour), and a **mask** which is itself a small stack of: a paint mask, a generator (a graph
reading the mesh maps), a filter, and an **anchor** — a reference to another layer's evaluated result.

⚠ **A paint layer stores pixels, not strokes.** Storing strokes is tempting: it re-renders at any
resolution and it diffs beautifully. It also means every brush, every falloff and every blend mode
becomes a *format compatibility surface* — change the falloff curve and every existing project repaints
differently. Painter and InstaMAT both store pixels. The stroke list is kept for the session's undo and
discarded on save.

⚠ **An anchor is what makes the stack a DAG, so the compiler must refuse a cycle** — and
`NodeGraphModel` already refuses one as it is made. The stack compiles *through* the graph model, which
is where that check lives, rather than growing its own.

### D11. Texture sets, channels, and the write

A **texture set** is a material slot on the mesh. A **channel** is one output map; the default set is
base colour, normal, ORM (occlusion·roughness·metalness packed, which is what
`TexturedOrmFeature.cs:288` reads), height and emissive, and the set is editable.

The bake writes, per texture set: one file per channel, one `.vxmat` naming them, and the provenance
block. ⚠ **It writes through the asset database's scan-then-read-back-the-GUID sequence rather than
minting an id** — `ProjectMeshBaker` established that dance and its remarks say why: a file in
`Assets/` has no `AssetId` until the database has seen it, and re-baking must *overwrite* so that every
entity already pointing at the material picks up the new maps.

### D12. Mesh maps are seven more measurements on one existing raster

`MapBaker` grows `BakedMaps` members, not a second baker:

| Map | The measurement, at a texel whose surface point and frame the raster already gives |
|---|---|
| Ambient occlusion | Cosine-weighted hemisphere rays against `SourceSurface`'s tree; the sphere's analytic answer is the test |
| Bent normal | The average unoccluded direction from the same rays — one accumulator, no second pass |
| Curvature | Per-vertex mean curvature from the source's cotangent Laplacian, interpolated; a sphere of radius *r* must read 1/*r* |
| Thickness | The same hemisphere, inverted, against the *inside* — occlusion of the flipped normal |
| Position | The surface point, normalised to the bounding box. Two lines |
| World normal | The source normal, unrotated. Two lines |
| ID | The source's material or island index, as a distinct colour per index, **nearest-sampled and never filtered** |

⚠ **AO and thickness are the only expensive ones**, and they share their ray budget and their
acceleration structure with the normal bake that already runs. ⚠ **ID must be excluded from gutter
dilation's *filtering***, or the dilated border becomes a colour that belongs to no id and every
generator keyed off it gets a hairline of a fourth material.

### D13. Painting is doc 31's brush aimed at an atlas instead of a heightfield

The chain is the same one that already exists: pointer → stamp → kernel → one undo entry per drag
(`TerrainStrokeCommand`, `BrushStroke`, `BrushFalloff`). What is different is **where the stamp lands**:

1. **In the 3D view**, by projection — a ray to the surface, the hit's UV, and a stamp in the atlas
   footprint that the screen-space brush covers. ⚠ **The stamp must be dilated across the seam**, or
   every stroke crossing a UV island edge leaves a hairline that only appears after mipping.
2. **In a 2D UV view**, directly, with the islands drawn under it — InstaMAT's 2026 addition, and the
   only way to fix the places the 3D view cannot reach.

Symmetry is a mirrored second stamp; a curve brush is a stamp path with spacing; smoothing is a filter
on the input points. All three are stroke-level and none of them touches the kernel.

⚠ **Painting is the most expensive phase in this plan and the least novel.** Two EM, and the risk in it
is latency rather than correctness: a stamp that costs a full-atlas evaluation of the stack above it
will feel broken at 4K. The stack is therefore evaluated **once per stroke start** into a cached
composite below and above the painted layer, and the stroke composites into that — which is what makes
the brush feel free and what M9's exit criterion measures.

### D14. It is a plugin, and that is the test

`TexturingModule : IEditorPlugin`, registered exactly as `TerrainModule` is, referencing
`Vixen.Editor.App` **not at all**, asking for the project and the scene through `PluginServices.Require`,
and unloading cleanly with `PluginHost.WaitForCollection` reporting no leak.

⚠ **Two things it will need that no existing module has needed, and finding out is the point.**
⚠ **It needed four**, and this line stood above a list that had already grown past it. Items 3 and 4
are the ones worth reading: they are the gaps the design did *not* predict, which is the whole
argument for building the plugin rather than reasoning about it. The prediction is left as written,
with this note, because *what was foreseen* and *what was found* are different facts and collapsing
them into one list would delete the result — but a header that undercounts its own list is how a
reader stops trusting the rest of the section, which is why it says so here.

1. **A document type.** `.vxtexgraph` and `.vxlayers` need an editor registration, a create-menu entry
   and a thumbnail — and doc 36 § D4's last two rows (`AddSettingsPage`, `AddPreview`) are the two of
   nine that were never built. **This plugin is the consumer that makes them worth building**, and if
   `AddPreview` still does not exist when M4 lands, the plugin's own asset thumbnails are the evidence.
2. **A GPU device.** No plugin has yet asked the host for one. `ShaderGraphPreviewRenderer` shows an
   editor assembly holding an `IGraphicsDevice`, but the plugin contract publishes `EditorProject`,
   `SceneDocument`, `DrawerRegistry` and `IEditorRegistry` — and not a device. ⚠ **Either a device is
   published through `PluginServices` or a third party cannot write anything that draws**, which is a
   real gap in the extensibility claim and is exactly the kind doc 36 § F2 was written to find.

   ✅ **Closed, and three of the sentences around it were wrong.** `PluginServices` now publishes
   `IEditorGraphics` and the texture panel evaluates a plan on the editor's device
   ([#737](https://github.com/Rikarin/Vixen/issues/737)).

   - ⚠ **"One `.Add(device)` line" could not have worked.** `EditorApplication.PluginPoints` runs
     from the constructor and the host sets `GraphicsDevice` afterwards, when the window can
     present — and back to `null` on the way down. `PluginServices.Add` throws on a second publish
     of a type, so there was no moment at which a device could be added. What a plugin can be
     handed is a **live view**, the shape `IActiveScene` and `IActiveView` beside it already take.
   - ⚠ **A narrower "lend me a surface and run this on it" was the intended answer and the
     evaluator refutes it.** `TexturePlanEvaluator` caches one compiled pipeline per kernel and
     output format across evaluations, so a borrow-per-call would recompile every kernel a plan
     touches on every preview. A plugin that dispatches its own work needs a device it can *hold*,
     and nothing narrower expresses that. What is narrowed instead is the way **back** to the
     screen: `Upload` takes pixels rather than a texture view, because a plugin's image is created
     for what it dispatches into and a view registered from a storage image is missing `Sampled`
     and in the wrong layout — which MoltenVK forgives and a discrete card does not.
   - ⚠ **And a device does not by itself make a plugin able to draw.** `ImageView.Image` is a
     number the *interface renderer* resolves, and nothing in `IGraphicsDevice` mints one. The
     upload half is the second member of the contract, and leaving it out would have published a
     device through which nothing could reach the screen.

3. **A file extension.** ⚠ Not predicted here, and the reason `.vxtexgraph` had no double-click:
   `AssetEditorRegistry.Add` had no `Remove`, so a plugin claiming an extension could never give it
   back and its assembly was pinned for the session with no error anywhere. ✅ Closed — `Add` hands
   back an `IDisposable`, the way `IEditorRegistry.Add` already did
   ([#739](https://github.com/Rikarin/Vixen/issues/739)).

4. **A public compiler.** ⚠ Also not predicted, and it survived both of the fixes above:
   `TextureGraphCompiler` was `internal`, so a plugin could register the node library — the generated
   `NodeTypes.Register` is `public` — and could not turn what an author wired into a plan. The panel
   therefore evaluated the graph's *base layer* and said so. ✅ The type is `public` now
   ([#738](https://github.com/Rikarin/Vixen/issues/738)).

   ⚠ **Closing a visibility is not the same as closing a gap, and this one shows the difference.**
   Nothing in the plugin has been changed to use it: six places in `Vixen.Editor.Texturing` still say
   the compiler is internal, including a **status line the user reads**, and the preview is still the
   base layer ([#792](https://github.com/Rikarin/Vixen/issues/792)). This document's own standing
   warning — that the commonest defect here is a finished thing nothing calls — applies to the fix
   for it as much as to the feature.

   ⚠ **Four findings from one plugin, three of them unpredicted**, which is the measurement § D14 was
   written to take. The claim it was testing — that the plugin surface is sufficient for what the
   editor itself does — is answered *no, and here is the list*, which is worth more than a panel that
   worked by reaching around the contract.

   ✅ **And the fourth was predicted, exactly, and it held.** Item 2 above was written before any
   plugin existed and said a plugin would find no device published and therefore be unable to draw.
   That is what happened, in those terms, and it is recorded here as a *result* rather than left to
   read as background: a design record that only ever records its corrections teaches the reader that
   the design was always wrong, and the reason to write predictions down is that some of them come
   true. ⚠ **What the prediction did not get right is the remedy** — it imagined one line publishing a
   device, and the three sub-points above are why no such line could have existed. So the useful form
   of this result is narrow: § D14 was right about *where* the gap was and wrong about how wide, which
   is the most a design document should expect of itself and considerably more than the header's
   "two things" managed.

### D15. What is deliberately taken from each reference, and what is not

| | Taken | Refused |
|---|---|---|
| Designer | Atomic set · compounds as content · output usages · relative resolution | FX-Map recursion · function graphs · `.sbs`/`.sbsar` |
| Painter | Texture sets · channels · the mask stack · anchors · mesh maps · smart materials | Two separate applications · a proprietary project format |
| InstaMAT | One graph, two front ends · layer references · 2D UV painting · nPass (deferred) | Point clouds as a first-class medium |

---

## Part 4 — The node catalogue

Every atomic below is **one Raven compute kernel, one `[Node]` class, one golden, one sabotage and one
scale-invariance case** — [exit criteria 2–4](#exit-criteria-measured). Every compound is **a
`.vxtexgraph` in the shipped library**, authored in the tool, and is content rather than code.

Three rules the whole catalogue obeys:

- **Grey and colour are one port kind, and grey promotes.** `PortKind` gains `Image` — doc 40 § D5
  asked for `Image` and `Mesh` and neither was added. Grey into a colour port splats; colour into a
  grey port is a type error naming the port. ⚠ This is `DynamicVector`'s widening rule reused rather
  than a second type system, and it is what stops the library needing a `BlendGrayscale` beside every
  `Blend`.
  - ⚠ **This paragraph used to say grey/colour was "a *format* on" the port kind, and that is not
    where it could live.** A `PortKind` is one enum member shared by three graphs and carries no
    format at all, so `PortKinds.Accepts` says yes to every image-to-image wire and the rule cannot be
    stated there. It is `TextureGraphCompiler`'s: `TextureChannels` is the format, a node resolves to
    the widest thing arriving at its image inputs, a grey feeding one that resolved to colour is
    splatted by an inserted `ChannelShuffle`, and a colour arriving at a port that *measures* is
    refused by name. That is the same division `PortKind.Dynamic` already makes — a width resolved by
    a compiler rather than by the enum — which is why the rule survives the correction unchanged and
    only its address moves.
- **Every scalar parameter accepts a Raven expression** over the graph's exposed parameters (§ D6), so
  `amount * 0.5 + rust` is a field rather than eleven nodes.
- **Every radius, width and length is in texels at the base resolution** (§ D8), and the evaluator
  scales it.

### 4.1 Sources — 6 kernels and two that cannot be

| Node | Out | Parameters | |
|---|---|---|---|
| **Uniform** | image | colour or grey, format | The `float4` every "which node is at fault" bisection starts from |
| **Bitmap** | image | asset, filter, **colour space** | ⚠ An sRGB texture decoded as linear and then blended is the commonest wrong-looking graph there is. The node decodes on the asset's declared space and the port carries it |
| **Gradient** | image | linear · radial · angular · reflected, angle, centre, ramp | The ramp is `Vixen.Ui.Controls.Advanced`'s `Gradient`, and ⚠ this is **`GradientEditor`'s first production consumer** — `overview.md:270` records that it has none, and a grep confirms it: the control, its tests and a string table |
| **Shape** | grey | disc · square · triangle · paraboloid · gaussian · cone · half-bell · gradation, scale, rotation, falloff | The splatter's usual pattern input. Analytic rather than rasterised, so it is exact at every resolution — which is half of D8's scale-invariance criterion passing for free |
| **Noise** | grey **+ cell id** | basis: value · gradient · worley · white; octaves, lacunarity, gain, **seed**, tiling | ⚠ One kernel with a **permutation**, because that is how this engine already varies a shader. Worley also outputs F1, F2 and a **cell index** — which is what a splatter wants and what saves a flood fill downstream |
| **Checker** | grey | scale, rotation, offset | `ComputeColor.rvn:169` has one already, for the shader graph |
| **Text** | grey | string, font, size, alignment, tracking | ⚙️ **Half built.** `TextureText.Rasterize` shapes and fills the string through the `Outlines` path and `TextureUploads.AddCoverage` puts it on the device — closed on an adapter, texel for texel, in `TextureTextDeviceTests`. ⚠ **There is still no node, and the reason recorded here has expired.** It said a node cannot allocate an *external* image ([#732](https://github.com/Rikarin/Vixen/issues/732), shared with `Bitmap`, `Gradient`, `Curve` and `Gradient Map`). That closed: `TextureEmitter.External` exists and all four of those nodes were written on it. So `Text` is now simply **unwritten** rather than blocked, which is a smaller and more actionable thing to say — and worth saying, because a row that keeps citing a closed issue is how work stays unclaimed. ⚠ And it is **not** a kernel — [#687](https://github.com/Rikarin/Vixen/issues/687) — because a compute kernel has no rasteriser and cannot reach a font |
| **Svg Path** | grey | path data (`d`), fill rule, scale | ⛔ **Refused here, and the reason that was written down first is wrong.** See the measurement below |

⚠ **`Svg Path`'s refusal, re-derived — and the closure argument it rested on does not survive.**
Batch 5 refused the node on a measurement: `Core/Vixen.Ui`'s project closure at 20 against
`Vixen.Editor.TextureGraph`'s 17, so an editor-side kernel assembly must not take it. Re-derived over
every `ProjectReference` in the tree on 2026-09-05, both columns are wrong and the conclusion with
them:

| | |
|---|---|
| `Vixen.Editor.TextureGraph`'s closure | **29** projects — and `Vixen.Ui` and `Vixen.Ui.Text` are already two of them |
| `Vixen.Ui`'s closure | **14**, a strict subset of those 29 |
| What naming `Vixen.Ui` would add to `bin/` | **nothing** |

The interface framework arrived with the `Vixen.Editor.NodeGraph` reference M4 could not do without
(→ `Vixen.Ui.Controls.Advanced` → `Vixen.Ui` → `Vixen.Ui.Text`), and that csproj's own comment
already says so at length. **So the cost of the reference is not assemblies.**

⚠ **And the wrap really is a wrap.** `PathVerb` and `OutlineVerb` are the *same five verbs* —
`Move`, `Line`, `Quadratic`, `Cubic`, `Close` — declared as one fixed-size struct per verb, in both
places, with each file's remarks citing the other's decision. `PathBuilder` → `GlyphOutline` is a
five-case switch, and `GlyphRasterizer` then fills it exactly as `Text` above is filled.

**What actually refuses it is different, and it is worth keeping:**

- **A compile surface, not an output directory.** `DisableTransitiveProjectReferences` is set here so
  that what this assembly may *spell* is exactly what it names. `Vixen.Ui.Text` is a leaf — its own
  closure is one project, `Vixen.Core` — and naming it buys fonts and a scanline fill. Naming
  `Vixen.Ui` buys `UiElement`, `Signal`, styling, layout and input inside an assembly whose job is a
  compute plan, and [#720](https://github.com/Rikarin/Vixen/issues/720) exists to make this assembly
  *less* of a UI assembly rather than more.
- **Fill rule.** § 4.1 lists one, and `GlyphRasterizer` is non-zero winding only — deliberately, with
  a reason about counters in an `o` that fonts depend on. Even-odd means changing the only rasteriser
  in `Vixen.Ui.Text` to take a rule, which moves a `CheckApi` baseline in a `Core/` assembly to serve
  one editor caller.
- ⚠ **The third reason was [#732](https://github.com/Rikarin/Vixen/issues/732) and it has gone.** It
  read: *until an external image can be allocated by a node, an `Svg Path` rasteriser is a second
  finished thing nothing calls.* A node can allocate one now, so the refusal rests on the two reasons
  above and not on three. **Saying so matters more than it looks**: a refusal held up by a reason that
  has since been fixed is how a decision outlives its argument, and the two that remain are about
  what this assembly may *spell* and about a `Core/` rasteriser's API — neither of which any texture
  slice will close by accident.

**Where the node should live instead: on the far side of [#720](https://github.com/Rikarin/Vixen/issues/720)'s
split.** The path is rasterised where a *node* is compiled and never where a *plan* is evaluated, so
the evaluator half — the one the headless content build loads — never needs `SvgPath` at all. The
node half is a UI assembly by construction, and `Editor/Vixen.Editor.Texturing` already references
the stack. [#753](https://github.com/Rikarin/Vixen/issues/753) carries this.

### 4.2 Colour and channels — 9 kernels

| Node | Parameters | |
|---|---|---|
| **Levels** | in black / white / gamma, out black / white, per channel | |
| **Curve** | a spline per channel | `CurveEditor` exists and already has consumers — `AnimationClipView`, the AI views |
| **Gradient Map** | grey → colour through a ramp | The `Gradient` control again |
| **HSL** | hue rotate, saturation, lightness | `ComputeColor.rvn:78` has the hue rotation |
| **Grayscale Conversion** | weights, default Rec. 709 | ⚠ A weight set that does not sum to one is a brightness change nobody asked for, so the node normalises and says so |
| **Invert** | per channel | |
| **Channel Shuffle** | per output channel, a source channel of one of two inputs | |
| **Blend** | mode (the sixteen below), opacity, optional mask | The most-used node in any graph, and the one whose golden set is per *mode* |
| **Auto Levels** | none | ⚠ **Two dispatches**: a min/max **reduction**, then the map. `NearestReduce` / `HiZReduce` are the shape. It is the first node whose output depends on every texel of its input, which the plan runner has to know — it can never be evaluated in tiles |

**The sixteen blend modes**: Copy · Add · Subtract · Multiply · Divide · Screen · Overlay · Hard Light ·
Soft Light · Darken (min) · Lighten (max) · Difference · Exclusion · Colour Dodge · Colour Burn ·
Signed Add.

### 4.3 Space — 5 kernels

| Node | Parameters | |
|---|---|---|
| **Transform 2D** | rotate · scale · offset · shear, tiling mode, filter | ⚠ Minification must be mip-correct or every rotated tile aliases, and the aliasing is the artefact people blame on the noise |
| **Mirror** | axis (X · Y · corner), offset | |
| **Tile** | integer repeat X/Y, per-tile offset | |
| **Crop** | a rect | ⚠ Produces a *different resolution*, which is the one place D8's relative rule has to be answered rather than inherited |
| **Resample** | target scale, filter | For the node that wants half resolution deliberately — a blur chain's cheap half |

### 4.4 Filters — 11 kernels

| Node | Parameters | |
|---|---|---|
| **Blur** | radius | Box, separable, two dispatches |
| **Blur HQ** | sigma | Gaussian, separable. ⚠ Asserted against the analytic impulse response, which is the closed form this catalogue's easiest node has and most of the others do not |
| **Directional Blur** | angle, length | |
| **Radial Blur** | centre, amount | |
| **Non-Uniform Blur** | radius **from a map**, max radius | ⚠ Not separable, which is why it is a kernel of its own rather than a parameter on `Blur` |
| **Sharpen** | amount, radius | Unsharp mask |
| **Emboss** | angle, elevation, intensity | |
| **Warp** | intensity, by the **gradient** of a grey input | |
| **Directional Warp** | angle, intensity, by a grey input | |
| **Vector Warp** | intensity, by an RG map | ⚠ The encoding — signed −1..1 carried in an unorm map — is written in the file and asserted, because the two conventions differ by a factor of two and a sign |
| **Slope Blur** | samples, intensity, mode (blend · min · max) | ⚠ **The one everybody gets subtly wrong.** It is *iterative* — N warps toward the gradient — so the sample count changes the result, and a single-pass approximation looks right on a blob and wrong on every edge, which is exactly where it is used |

### 4.5 Analysis — 3 kernels

| Node | Out | |
|---|---|---|
| **Distance** | grey | ⚠ **Jump flooding**: log₂(n) ping-ponged dispatches. The naive kernel is O(r²) per texel and is the difference between four milliseconds and four seconds at 2K |
| **Flood Fill** | id · random value · UV-within-island · bounding box · size | ⚠ **The highest-risk kernel in the catalogue.** Connected components on a GPU is label propagation to a fixed point; its cost depends on the *shape* of the input rather than its size, and it needs an iteration ceiling that **reports truncation** rather than a while-loop on the device. Everything Substance calls "flood fill to …" is this node plus a `Channel Shuffle` |
| **Edge Detect** | grey | Sobel, width, threshold |

⚠ **Histogram Scan, Range and Select are compounds over `Levels`**, as they are in the reference, and
are listed in [4.9](#49-the-compound-library--content-not-code). They are the wear-and-dirt knob every
generator turns, and they are three files rather than three kernels.

### 4.6 Surface — 5 kernels and one CPU solve

| Node | Parameters | |
|---|---|---|
| **Height → Normal** | intensity, format | ⚠ **The green convention** is whatever `TexturedNormalMapSurface` samples, asserted by a test against a known ramp rather than claimed by a comment. A flipped green is the defect that survives every review because it looks like lighting |
| **Normal → Height** | iterations, intensity | ✅ **Built** — `NormalToHeightOperation`, a node, and the first production user of [#688](https://github.com/Rikarin/Vixen/issues/688)'s CPU seam. Doc 42 § B1's `ConjugateGradient` is the solver; ⚠ **it was reachable only by a csproj line** — every type under `Vixen.Geometry.Uv/Solving` is `internal` and had no caller outside that assembly, so this takes an `InternalsVisibleTo` and [#752](https://github.com/Rikarin/Vixen/issues/752) records that the honest fix is an assembly of its own. ⚠ **The answer has mean zero and is therefore signed**: a gradient field fixes a height only up to a constant, and picking it by min-max would make the node depend on one extreme texel. A `Levels` after it is what makes a `[0, 1]` map. ⚠ It is the one entry here that is **not** a compute kernel, by the exception § D3 states |
| **Normal Combine** | mode | ⚠ Reoriented normal mapping, not whiteout. Whiteout is cheaper and wrong at grazing detail, and the two **agree on the flat case a lazy test would use** |
| **Normal Transform** | flip green, rotate, renormalise | |
| **Curvature from Normal** | radius | ⚠ The cheap one, from a height field. **Not** § D12's mesh bake, and the node's own inspector says which a generator should prefer |
| **Ambient Occlusion from Height** | radius, samples | Horizon search. Same caveat, same sentence in the inspector |

### 4.7 Placement — 2 kernels

| Node | Parameters | |
|---|---|---|
| **Tile Sampler** | grid X/Y · pattern input(s) · mask input · per-instance random **position · rotation · scale · colour · pattern index** · size and rotation *maps* · accumulation mode · **seed** | The workhorse. Nearly every pattern in 4.9 is this node with a `Shape` in it |
| **Splatter** | the same, without the grid: a count and a distribution, optionally placed by a vector map | The FX-Map replacement of § D7 |

### 4.8 Graph structure — 5 node classes, no kernel

| Node | |
|---|---|
| **Input** | Typed, with a usage and a default — what makes a graph usable as a node |
| **Output** | With a **usage**: `baseColor` · `normal` · `roughness` · `metalness` · `occlusion` · `height` · `emissive` · `opacity` · `mask` |
| **Material Output** | The grouped set a bake writes, per § D11 |
| **Mesh Map Input** | Binds **by usage** to a baked map (§ D12) — Painter's automatic connection, and the reason one generator compound works on every mesh |
| **Sub-graph** | A published `.vxtexgraph` as a node, inlined (§ D9) |

⚠ **There is deliberately no Bake node.** A graph that could bake would drag `EditMesh`, the atlas
raster and the ray tree into the evaluator. The bake is a command that writes maps; the graph reads
them by usage.

⚠ **Three of those five turned out not to be node classes in this assembly, and the design is better
for it.** The row above is the *vocabulary*; where each entry lives is what M4 answered:

- **`Input` and `Sub-graph` are the framework's already.** `Vixen.Editor.NodeGraph` has
  `SubGraphs.InputType` / `OutputType` — boundary types built per graph rather than registered — and
  `SubGraphLibrary` turns a published graph into a node type. Writing texture-graph copies of either
  would have been a second implementation of § D9 that had to agree with the shader graph's.
- ⚠ **`Material Output` is not needed at all.** The bake takes a dictionary keyed by
  `MaterialMapUsage`, and every `Output` node already carries a usage — so the grouped node would be
  a second way of saying the same thing, and the one that could disagree with the first. **A row of
  this catalogue being deleted by the implementation is the good outcome**, and it is recorded rather
  than quietly dropped because the argument (one usage per output, resolved at the bake) is the part
  worth keeping.
- **`Mesh Map Input` is § D12's** and arrives with the mesh-map front end rather than with M4.

So `Output` is the one `[Node]` class § 4.8 costs, and the count in
[§ 4.11](#411-the-count-and-the-claim-it-corrects) counts an entry rather than a file.

### 4.9 The compound library — content, not code

⚠ **This list is the backlog, not the deliverable.** [M10](#m10--the-library-smart-materials-and-export--10-em)
ships the two dozen marked ●, authored in the tool, and the rest is how a library grows.

| | |
|---|---|
| **Utility** | Histogram Scan ● · Histogram Range ● · Histogram Select ● · Safe Transform ● · Highpass ● · Contrast/Luminosity ● · **Make It Tile** ● (offset-wrap with an edge mask — doc 40 § D2's first row) · **Delight / Equalize** ● (its second) · Anti-Alias · Dilate · Skew · Quantize · Colour Variation |
| **Patterns** | Brick ● · Panels ● · Tile Random ● · Rivets ● · Scratches ● · Wood Grain ● · Cells ● · Weave · Hexagon Grid · Bolts · Stitches · Chain · Fibres · Marble Veins · Gravel · Sand · Water Drops · Snow · Moss · Leather · Cloth |
| **Grunges** | A family of eight ●, which is `Noise` and `Slope Blur` in eight arrangements — and the honest description of most of what a grunge library is |
| **Surface** | Height Blend ● · **Bevel** ● (`Distance` → `Height → Normal`) · Curvature Smooth ● · Height to AO ● · Metal Reflectance ● (a named-metal lookup) · Normal Sobel · Basecolor/Metallic/Roughness converter |
| **Mask generators** | Every one reads § D12's maps **by usage**: Dirt ● · Curvature Edge Wear ● · Metal Edge Wear ● · Grunge Rough Dirty ● · Dust ● · Position Gradient ● · **Mask Editor** ● — the big composite with the sliders, which is what most artists actually reach for · Drips · Light · Water Level |
| **Smart materials** | `.vxsmartmat`: Painted Metal ● · Rusted Iron ● · Worn Wood ● · Concrete ● · Plastic ● · Leather |

### 4.10 The layer and mask vocabulary

The same catalogue question for the other front end (§ D10), listed here so it is in one place:

| | |
|---|---|
| Layer kinds | Fill · Paint · Filter · Group |
| Mask sources | Paint · Generator (a compound) · Bake (a map by usage) · **Anchor** (another layer's result) |
| Mask effects | Levels · Blur · Warp · any single-input graph |
| Per layer | opacity · blend mode · per-channel enable · projection (UV · triplanar · planar) |
| Blend modes | the sixteen of [4.2](#42-colour-and-channels--9-kernels) |
| Mesh maps | § D12's **nine**: normal · displacement · AO · bent normal · curvature · thickness · position · world normal · ID. ⚠ **This row said ten and listed `opacity` as the tenth, and that is a borrow from the row two above it in § 4.8** — `opacity` is an *output* usage, one of the nine a bake writes, and a mesh has no opacity to measure. D12's heading ("seven more measurements") and M6 were right throughout; this row and [A.4](#a4-baking) were the two that drifted, which is what happens when a vocabulary is written down a second time |
| Brush | radius · flow · spacing · falloff · rotation · jitter · alpha · symmetry · curve mode |

### 4.11 The count, and the claim it corrects

| | |
|---|---|
| Compute kernels | **41**, and it is the *sum of the seven headings above* rather than a number kept here: [4.1](#41-sources--6-kernels-and-two-that-cannot-be) 6 · [4.2](#42-colour-and-channels--9-kernels) 9 · [4.3](#43-space--5-kernels) 5 · [4.4](#44-filters--11-kernels) 11 · [4.5](#45-analysis--3-kernels) 3 · [4.6](#46-surface--5-kernels-and-one-cpu-solve) 5 · [4.7](#47-placement--2-kernels) 2. ⚠ **Not 44, and the three that came off are the three rows below.** That arithmetic counted every *row* of every table as a kernel while three of those rows said in their own cells that they are not compute shaders. Change a heading and this changes with it, which is the only reason it is allowed to stay: there is nothing here to keep in step separately |
| Node classes | ⚠ **Deleted, because this is the number the document could not keep.** It has read **49** (= 44 + 5, both wrong), then **46** (= 41 + § 4.8's five) — and 46 is wrong in a way that is worth stating, because it is wrong *by this table's own prose*: § 4.8 says three of its five are not classes in this assembly, and § D6's `Pixel Processor` is a class Part 4 deliberately never lists. What the catalogue actually implies is a **rule**, not a total: one class per kernel, plus one for each catalogue entry that is not a kernel, plus § D6's. **The number itself lives in the registry** — `NodeTypes`, reconciled against the kernel folder by `TextureNodeLibraryTests.Every_kernel_has_a_node_or_a_written_reason_not_to` — and its reading of the day is reported in [`docs/overview.md`](../overview.md) § 1.11. Three successive batches each corrected this cell into a different wrong number; a number a document cannot keep is worse than no number |
| Not a kernel | One: `Normal → Height`, on the CPU, by exception — **built**, and declared in `TextureKernels.Cpu.cs` so that the roll calls can name the category rather than reading it as a kernel whose `.rvn` went missing |
| Not a kernel and not an op | One: `Text`, which is CPU pixels *uploaded* rather than an op of any kind — `TextureText` + `TextureUploads.AddCoverage`. ⚠ It has no node, and no longer for a reason: [#732](https://github.com/Rikarin/Vixen/issues/732) closed and § 4.1's row says what is left |
| Not a kernel and not built | One: `Svg Path`, refused — the measurement is under [4.1](#41-sources--6-kernels-and-two-that-cannot-be) and [#753](https://github.com/Rikarin/Vixen/issues/753) carries where it should live instead |
| Shipped compounds | ⚠ **Also deleted, and for a sharper version of the same reason.** This cell read **24 ●** while [§ 4.9](#49-the-compound-library--content-not-code) carries **34** ● marks — one of which ("a family of eight ●") stands for eight, and five of which are `.vxsmartmat` smart materials rather than compounds. So the cell and the list it summarised could not both be read the same way by anybody, and no single number was ever right for both. The ● in § 4.9 is the mark, [M10](#m10--the-library-smart-materials-and-export--10-em) is the phase that ships them, and what is in the tree on any given day is `TextureCompoundLibrary`'s and [`docs/overview.md`](../overview.md)'s |

⚠ **And the numbers above are the plan's, not the tree's — deliberately, and they will not agree.**
Two structural reasons, neither of which is a shortfall:

- **`Shaders/` holds more `.rvn` files than this table has kernels**, because several catalogue
  entries are *chains* rather than single dispatches. `JumpFlood`, `FloodBounds`, `FloodResidual` and
  `MinMaxReduce` have no catalogue row and no node of their own, and never will — how many dispatches
  a jump flood or a reduction ladder takes is the node's business and not the vocabulary's.
- **`Nodes/` holds a class the catalogue does not name**: § D6's `Filters/Pixel Processor` is
  described as a decision rather than as a node, so Part 4 never lists it. It is a node all the same.

⚠ **This document is therefore not where the count is checked, and a snapshot of the tree does not
belong in it.** `TextureKernelTests.Every_kernel_the_folder_holds_is_embedded` and
`TextureNodeLibraryTests` read the folder and the registry and reconcile them against each other; a
dated measurement written here would be a third list, and this paragraph has already been one — it
said thirty-six nodes and eight unreachable kernels within a batch of both being wrong.

⚠ **Two cells of the table above have now been deleted rather than corrected again, and that is the
paragraph taking its own advice.** The node count and the compound count were each re-derived by three
successive batches into three different wrong numbers, in a table whose very next sentence argues that
a second list always drifts. A cell nobody can keep is not a weaker version of a fact; it is a claim
this document makes and then contradicts, and the reader who checks one and finds it wrong has no way
to know which of the others to trust. The kernel count survives **only** because it is a sum of the
seven headings on the same page — one pass re-derives it, and it cannot drift away from them
independently.

The state of what is built belongs in `docs/overview.md` and in
[#577](https://github.com/Rikarin/Vixen/issues/577); this table is the vocabulary they are counted
against.

⚠ **Forty-one is still not the reference's twenty-four, and § D7 is the reason.** In Designer every noise
and every pattern is an FX-Map compound; refusing FX-Map's recursion means the *bases* have to be
kernels. The trade is a larger kernel folder against nodes whose cost is knowable before
they run — and it is why 4.9's patterns are compounds of `Tile Sampler` rather than of a recursive
quadrant machine.

---

## Part 5 — The files

| | |
|---|---|
| `.vxtexgraph` | A `NodeGraphAsset`, as the shader graph's already is. Positions as two floats, identities stable, YAML, mergeable |
| `.vxlayers` | The stack, per texture set. Layers, masks, anchors, parameters — **and no pixels** |
| `.vxpaint` | The painted pixels a `.vxlayers` refers to, one per paint layer or mask, as KTX2. ⚠ Separate because a stack is a file people merge and a paint layer is not |
| `.vxsmartmat` | A stack fragment plus parameter overrides — a "smart material". A `.vxlayers` group with no mesh binding |
| `Assets/…` outputs | PNG or KTX2 per channel, plus a `.vxmat`, plus the `texturing:` block in the `.meta` |

⚠ **The mesh maps are ordinary texture assets in the project**, not a hidden cache. An artist wants to
look at the curvature map when a generator behaves oddly, and a build wants to not re-bake them.

---

## Part 6 — Phases

Each is a branch, merged as it lands, with the affected suites run before the merge and the full
`./build.sh` gate run once on master at the end.

**Filed, and tracked by [#577](https://github.com/Rikarin/Vixen/issues/577)** — every one carries the
`material-authoring` label:
[M0 #565](https://github.com/Rikarin/Vixen/issues/565) ·
[M1 #566](https://github.com/Rikarin/Vixen/issues/566) ·
[M2 #567](https://github.com/Rikarin/Vixen/issues/567) ·
[M3 #568](https://github.com/Rikarin/Vixen/issues/568) ·
[M4 #569](https://github.com/Rikarin/Vixen/issues/569) ·
[M5 #570](https://github.com/Rikarin/Vixen/issues/570) ·
[M6 #571](https://github.com/Rikarin/Vixen/issues/571) ·
[M7 #572](https://github.com/Rikarin/Vixen/issues/572) ·
[M8 #573](https://github.com/Rikarin/Vixen/issues/573) ·
[M9 #574](https://github.com/Rikarin/Vixen/issues/574) ·
[M10 #575](https://github.com/Rikarin/Vixen/issues/575) ·
[M11 #576](https://github.com/Rikarin/Vixen/issues/576).
⚠ D1's shader-gate finding is filed separately as
[#564](https://github.com/Rikarin/Vixen/issues/564), because it is a defect in a gate that exists
today rather than work this document creates.

### M0 — The spike · 0.5 EM

Three Raven compute kernels (blend, blur, levels), a hand-built `TexturePlan` of six ops, an offscreen
dispatch through `ThumbnailSurface`'s device path, and a PNG on disk. **Answers the one question that
decides the shape: what does a forty-op 2K evaluation actually cost, and does the intermediate pool
behave.** No graph, no UI, no node classes.

### M1 — `Vixen.Editor.TextureGraph`: the plan, the evaluator and its shader gate · 1.25 EM

`TexturePlan`, `TextureOp`, the image pool with liveness-based reuse, the dispatcher, the format rules
(R8 / RG8 / RGBA8 / R16F / RGBA16F), the resolution rules of D8, and the seed.

⚠ **Two of those five format rows were wrong and the ban on 32-bit float is narrower than it reads.**
R8 and RG8 cannot be *written*: `Raven/Vixen.Raven/Symbols/ImageFormats.cs` admits sixteen
storage-image formats and neither is among them, and Vulkan requires neither for `STORAGE_IMAGE`
either — so `TextureFormats.IsStorable` admits three, and a plan takes an R8 bitmap **in** and
computes in one of the three. And the 32-bit exclusion is a decision about *material maps* rather than
a capability: Raven admits `r32f`, `rg32f` and `rgba32f`, the RHI maps all three
(`Platform/Vixen.Graphics.Vulkan/VulkanFormats.cs`), and `Core/Vixen.Rendering/HiZPyramid.cs` already
dispatches into an `R32Float` storage image in production. ⚠ [#690](https://github.com/Rikarin/Vixen/issues/690)
asks for it on behalf of the two § 4.5 kernels that carry a *position* rather than a colour, and its
own remedy is refuted: both records are **four** channels — `JumpFlood.rvn` stores
`float4(inside.xy, outside.xy)` and `FloodBounds` stores a min and a max — so the format they want is
`rgba32f`, not the `r32f` / `rg32f` the issue names. That is a widening with a stated reason (a scratch
record is never an output and never a material map), and it is **not free**: the memory figure this
document and the assembly's README both quote is understated by 8×, because an `Rgba16Float`
intermediate at 4K is 128 MiB and its `Rgba32Float` twin is 256 MiB, and a jump flood ping-pongs two of
them. So it is admitted with the slice that lifts `TextureAnalysis.ExactExtent`, measured, rather than
by widening `TextureFormats.Storable` for every kernel in the folder first. ✅ The shader-gate half
of this milestone — **turning `CheckShaders`' `EditorSources` from a hand-kept list into a folder
read**, per D1 — landed ahead of it under
[#564](https://github.com/Rikarin/Vixen/issues/564), so a kernel added here is gated without a build
edit. Tests are device tests that name their adapter and skip loudly without one.

### M2 — The atomic kernels, part I · 1.5 EM

[4.1](#41-sources--6-kernels-and-two-that-cannot-be), [4.2](#42-colour-and-channels--9-kernels) and
[4.3](#43-space--5-kernels) — twenty kernels. Every one gets a golden, a closed-form assertion
where one exists, a scale-invariance check at ×2, and a sabotage that proves the golden red.
⚠ This said twenty-two, from § 4.11's old forty-four: `Text` and `Svg Path` are § 4.1 rows and
neither is a kernel this phase can write.

### M3 — The atomic kernels, part II · 1.5 EM

[4.4](#44-filters--11-kernels) through [4.7](#47-placement--2-kernels) — twenty-one more (⚠ not
twenty-two: § 4.6's `Normal → Height` is the CPU exception), and the four
with real algorithmic content in them: the jump-flooded distance transform, flood fill's label
propagation, the Poisson solve, and slope blur, which is the one everybody gets subtly wrong.

### M4 — The graph: nodes, compiler, document, panel · 1.25 EM

`[Node]` classes over the kernels, `TextureGraphCompiler : NodeGraphCompiler<TexturePlan>`, per-node
previews through `INodePreviewSource`, exposed parameters, sub-graphs, and **the 2D image view of B6**.
This is the first phase with a picture an artist can use.

### M5 — Baking to a material · 0.75 EM

Outputs with usages, channel packing to ORM, mip and block-compression through `Vixen.Core.Imaging`,
the `.vxmat` write, the scan-then-read-back GUID dance, and the provenance block with its digest check.

### M6 — Mesh maps · 1.25 EM

D12's seven measurements on `MapBaker`'s existing raster, a bake panel, and the maps landing as
project assets. Closed-form oracles on a sphere and a plane.

### M7 — The layer stack · 1.5 EM

`.vxlayers`, the four layer kinds, blend modes, per-channel enables, groups, the compile to a
`TexturePlan`, and **Explode**. The differential test that makes this phase honest: a stack and its
exploded graph bake byte-identical outputs.

### M8 — Masks, generators and anchors · 1.25 EM

The mask stack, generators as shipped `.vxtexgraph`s reading the mesh maps by usage, anchors as DAG
edges, and the cycle refusal proved by a test that tries to make one.

### M9 — Painting · 2.0 EM

Brush, stroke, stamp, the atlas footprint, the seam dilation, the cached composite, the 3D projection
path, the 2D UV view, symmetry, curve strokes, smoothing.

### M10 — The library, smart materials and export · 1.0 EM

Twenty compound nodes and ten smart materials authored *in the tool*, `.vxsmartmat`, export presets,
and the tool's own dogfooding report — which is the only real answer to "is the atomic set right".
Optionally nPass ([D7](#d7-the-scatter-is-a-node-with-a-count-not-a-recursive-quadrant-machine)).

### M11 — The runtime layering gap · 0.75 EM · optional, and separable

B1's list: a textured emissive, height and opacity feature; a `TexturedMaterialLayersFeature` whose
weights come from a splat map; the completeness test for `WorldRenderer.Paired` that
[#371](https://github.com/Rikarin/Vixen/issues/371) asks for; and
[#493](https://github.com/Rikarin/Vixen/issues/493)'s missing wiring. ⚠ **This is owed by doc 06 and
doc 23 rather than by this document**, it belongs to the renderer, and nothing above it depends on it.

⚠ **Landed, less height** — see the note under [B1](#b1-a-layer-stack-cannot-ship-as-a-live-layered-material--for-the-runtime-path-only).
Height is [#615](https://github.com/Rikarin/Vixen/issues/615) and is a decision before it is work.

### Cost

| | EM |
|---|---|
| M0–M5 — a working texture graph that bakes a material | 6.75 |
| M6–M9 — the Painter half | 6.0 |
| M10 — the library that makes it usable | 1.0 |
| **Total, without M11** | **13.75** |

⚠ **That is a large number and it is the honest one.** Two thirds of it is the thirty kernels and the
brush, neither of which compresses. What makes it *tractable* is Part 1: the graph framework, the node
registry, the previews, the undo, the atlas raster, the cage bake, the unwrapper, the brush model, the
plugin host and the asset write already exist, and every one of them would otherwise be in this table.

---

## Exit criteria (measured)

1. **A forty-node graph at 2048² is *recorded*, and what is gated is the work rather than the clock.**
   The milliseconds are measured with the adapter's name against them and asserted only by a hang
   check; what is asserted is the three deterministic counters that decide whether that number is tens
   of milliseconds or seconds — a chain of any length pools two textures and no more, one bake is one
   frame however many dispatches it holds, and forty ops of one kernel compile one variant. ⚠ **This
   criterion said "under 250 ms" and asserting that would have been the flake this repository warns
   about**; the reference measurement is 40 ms at 2048² on an Apple M1 Max, six times under. ⚠ **And
   a parameter change re-evaluates only the affected sub-graph — which is unimplemented, and was read
   past by six audits in a row** ([#846](https://github.com/Rikarin/Vixen/issues/846)): the first
   half of this sentence was measurable, so the sentence got measured. A criterion holding two claims
   joined by an "and" is scored on whichever of them somebody can score.
2. **Scale invariance.** Every atomic node, baked at 1K and at 4K, agrees within 2/255 after
   downsampling. ⚠ A node that fails this has D8's bug and no other test finds it.
3. **Every node is covered by an assertion that would notice its picture changing, and the library is
   read rather than listed.** The enumeration is the shipped surface — the embedded `Shaders/*.rvn`,
   the assembly's `ITextureCpuOperation` types, and the node registry's own paths — so a node or a
   kernel that arrives uncovered is red by existing, which is
   [#512](https://github.com/Rikarin/Vixen/issues/512)'s shape applied before it can go wrong rather
   than after. ⚠ **This criterion said "a golden per node" and that is the wrong instrument here**;
   see the correction below.
4. **A sabotage per shipped op implementation.** Perturb what the kernel stores; the picture must
   move. A node whose evaluation survives a perturbation of its own source is a golden of a black
   image — and nothing may ship without one, which is a roll call over the implementations rather
   than a habit.
5. **Closed forms where they exist.** A Gaussian's impulse response; a distance transform from one lit
   texel; AO on a sphere against the analytic hemisphere; curvature of a sphere of radius *r* reading
   1/*r*; a levels curve at three points.
6. **A stack and its explosion are byte-identical.** The one test that proves D1's "one evaluator".
7. **A re-bake on the same machine is byte-identical**, and a bake on a different adapter is *recorded*
   and not asserted.
8. **Paint latency**: a stroke on a 4K texture set with twelve layers under it stays under 16 ms per
   stamp.
9. **A painted-over output is detected**, never silently regenerated — the `writtenDigest` check, with
   a test that edits a file and re-bakes.
10. **The plugin loads from a folder, activates, and unloads with no leak** —
    `PluginHost.WaitForCollection` reports collection, with nothing of the plugin's registered
    afterwards. ⚠ **And it references `Vixen.Editor.App` in no build**, asserted by
    `CheckArchitecture`.
11. **A device is confirmed by name in every GPU test in this area.** A suite that ran on the Null
    device reports a skip, never a pass.
12. **A frame is photographed.** A mesh, textured entirely in the tool, rendered through the real
    `StandardFrame` — because "tests pass" is not evidence for a visual defect, and three wrong-frame
    bugs have shipped past clean counters in this repository already.

### Which of the twelve somebody has measured, and which are inferred

⚠ **This is not the scoreboard the section below refuses.** Whether a criterion *holds* is state and
lives in [#577](https://github.com/Rikarin/Vixen/issues/577); what kind of evidence exists for it is a
fact about the criterion as written, which is what this document is for. The distinction is the whole
point: **a criterion nobody has measured and a criterion that passes look identical in a status
table**, and six audits in a row scored criterion 1 without noticing that half of its sentence had
never been evaluated at all. Re-measured 2026-09-06; the mechanism column names the file, so a row
that has rotted is a `git grep` away from being caught.

| # | The criterion, short | Evidence | Mechanism | ⚠ What is not measured |
|---|---|---|---|---|
| 1 | Forty-node graph at 2048², recorded | **half measured** | `TextureEvaluationCostTests` — milliseconds recorded with the adapter named, three deterministic counters asserted (two pooled textures, one frame, one compiled variant) | **The second clause has never been evaluated.** "A parameter change re-evaluates only the affected sub-graph" is unimplemented — `TexturePlanEvaluator` caches compiled variants and no evaluated image, and `TextureGraphPreviews` marks whole graphs dirty ([#846](https://github.com/Rikarin/Vixen/issues/846)) |
| 2 | Scale invariance at 1K and 4K | **measured, unenumerated** | `TextureSourceDeviceTests.A_source_kernel_bakes_the_same_picture_at_both_resolutions`, 64 against a downsampled 256 | "Every atomic node" is inferred: nothing enumerates the atomic nodes and requires the next one to have a case. ⚠ And the criterion is false as written for a hard-edged source ([#640](https://github.com/Rikarin/Vixen/issues/640)) |
| 3 | An assertion per node that would notice its picture changing | **measured** | `TextureNodeLibraryTests`' roll calls over the shipped surface, plus 4's per-implementation sabotage and 5's closed forms | A node whose parameters are right and whose picture is merely ugly — said plainly below, and a golden would have recorded it rather than caught it |
| 4 | A sabotage per shipped op implementation | **measured** | `TextureKernelSabotageTests` — one case per implementation, the perturbation generated from the kernel's own source | The subject set was one assembly's types and a CPU operation in `Vixen.Editor.Texturing` would have been outside it; cross-checked against the tree's sources since ([#872](https://github.com/Rikarin/Vixen/issues/872)) |
| 5 | Five closed forms | **three of five** | Gaussian impulse response (`TextureFilterDeviceTests`), distance transform from one lit texel (`TextureAnalysisDeviceTests`), levels curve at three points (`TexturePlanDeviceTests`) | ⚠ **AO on a sphere and curvature of a sphere reading 1/*r* do not exist.** `git grep -i sphere` over both test projects returns nothing, and the criterion has been scored Met since batch 6 ([#847](https://github.com/Rikarin/Vixen/issues/847)). They are the two that calibrate a *scale*; the surviving oracles for those kernels are a direction and a constant |
| 6 | A stack and its explosion are byte-identical | **measured, weaker than its wording** | `LayerStackBakeDeviceTests` on a device, `LayerStackExplodeTests` without one | ⚠ `LayerStackDifferential` compares a stack against **its own explosion** — one pipeline twice, so it proves the round trip and the decoration agree and nothing about whether either is right. Two compositing defects lived under it green for a whole batch |
| 7 | A re-bake on the same machine is byte-identical | **measured** | `MaterialBakeAssetTests.A_re_bake_is_byte_identical`; the cross-adapter half is recorded, not asserted, by `A_re_bake_on_another_adapter_is_not_refused` | — |
| 8 | Paint latency under 16 ms per stamp | **measured as work, recorded as time** | `PaintCostTests` at 4096² with twelve layers: the stamp's work is asserted equal to its own footprint, the milliseconds are printed, and the one time assertion is an absurd ceiling whose message says it is a hang check | The wall-clock number itself is deliberately not gated |
| 9 | A painted-over output is detected | **measured** | `MaterialBakeAssetTests` — refused, overwritten when forced, and an untouched set not called painted | — |
| 10 | The plugin loads, activates, unloads, and links the app in no build | **measured, both halves** | `Vixen.Editor.Plugin.Tests/LoadingTests` via `PluginHost.WaitForCollection`; `PluginReferenceRule` called by `CheckArchitecture` and by `PluginReferenceRuleTests` | — |
| 11 | A device confirmed by name in every GPU test in this area | **measured for one of the two projects** | `TextureAdapterRollCallTests` walks `Vixen.Editor.TextureGraph.Tests`' own sources | ⚠ **The area is two test projects.** `Vixen.Editor.Texturing.Tests` has five device files and the same convention through `TexturingDevice.Adapter`, and nothing enumerates them — so the twentieth file there is exactly the case the roll call was built for, one project along ([#883](https://github.com/Rikarin/Vixen/issues/883)) |
| 12 | A frame is photographed | **measured** | `BakedMaterialImageTests` — maps from `TexturePlanEvaluator`, packed by `MaterialBake`, drawn through `StandardFrameAsset`, differenced against `MetalRoughnessFeature` | It is a golden-suite file, so it skips without a device; ⚠ eighteen files in that suite *passed* rather than skipped until 2026-08-21 |

⚠ **Two of the twelve are cited in the tests by the wrong number, and one by wording the criterion no
longer has.** `TextureSourceDeviceTests` calls scale invariance "exit criterion 3" three times, and it
is criterion 2; `TextureEvaluationCostTests` quotes criterion 1 as "under 250 ms", which is the
sentence this document amended precisely because asserting it would have been a flake. A criterion
cited by number in a file that outlives the numbering is a small instance of the same thing this
section is about — a claim that reads as measured and is a copy of something older
([#884](https://github.com/Rikarin/Vixen/issues/884)).

### What measuring them for the first time said about the criteria themselves

⚠ **Where the answers live: [#577](https://github.com/Rikarin/Vixen/issues/577), not here.** The
twelve above are what this document promised; which of them hold today is state, and state belongs in
the tracking issue and in `docs/overview.md`. A plan that keeps a scoreboard becomes a scoreboard
nobody updates. What *does* belong here is what the first honest pass over them revealed about the
criteria as written — because four of them cannot fail, and this repository's own rule is that an
instrument reporting success on the day it did not run is the first thing to fix.

⚠ **Four were properties of the tests with nothing requiring the next test to have them** — and the
four resolved four different ways, which is the part worth keeping. 11 was made mechanical as
written; 4 was made mechanical and its subject set turned out to be wrong; 10 was right and the gate
was built to match it, and then the gate turned out never to have run; **3 is the one where the
criterion itself is the defect** and the answer is to amend it rather than to build what it asked
for.

- **3, a golden per node — ⚠ and the correction is that the instrument was wrong, not that the work
  was skipped.** The half that *is* enforced is the half about the library being read rather than
  listed. "Per node" was not: nothing failed when a node arrived without a golden. But a golden per
  node should not be built, and the reasons are all facts about this repository rather than
  preferences. A golden is a picture recorded from the code it then guards, so on the day it is
  written it asserts nothing about whether the picture is *right* — criterion 4's own sentence says
  as much. The golden suites here are the ones that have actually gone quiet: eighteen files
  **passed** rather than skipped without a device until 2026-08-21, and they run on one platform,
  so a golden per node multiplies by thirty-six an instrument whose failure mode is reporting
  success on the day it did not run. And a committed reference drifts the moment a neighbouring
  branch changes the renderer, which is the same cross-branch shape as the five roll calls below.
  What is worth having instead is what a golden was standing in for: **an assertion per node that
  would notice the picture changing, enumerated from the shipped surface**. That is three derived
  mechanisms rather than a folder of PNGs — the closed forms of criterion 5 where a node has one,
  the generated per-implementation sabotage of criterion 4 for sensitivity, and
  `TextureNodeLibraryTests`' roll calls, which compile *every* node type into one plan and hold each
  op to its kernel's parameters, input count and defaults in both directions. ⚠ **What none of them
  covers, said plainly**: a node whose parameters are right and whose picture is merely ugly. A
  golden would not have caught that either — it would have recorded it.
- **4, a sabotage per node.** Sabotage arguments appeared across the suites and no mechanism noticed
  a missing one — the same shape as 3, and the more serious of the two, because a golden with no
  sabotage may be a golden of a black image. It is now generated from each kernel's own source, and
  the roll call is taken over the shipped implementations. ⚠ **And the subject set was the second defect**: the
  roll call read the embedded `.rvn` files, so § 4.6's CPU operations — which ship no shader — were
  outside it, and it reported complete coverage of a surface it could not see part of. The criterion
  says "per **node**", and a CPU operation is one.
- **10, "and it references `Vixen.Editor.App` in no build".** ⚠ **This was recorded here as a
  criterion naming the wrong instrument, and the criterion turns out to have been right — so the gate
  was built to match it.** The finding as written said `CheckArchitecture` did not check this and
  `ModuleReferenceTests` did. Both halves were true and the conclusion was backwards: a test reading
  `Assembly.GetReferencedAssemblies` sees only what the compiler *emitted* a reference for, so a
  `ProjectReference` nobody has used yet is invisible to it — and the criterion is about the
  **reference**, which lives in a project file. `CheckArchitecture` now asserts it, transitively
  (reaching the application through one intermediate ships it in the plugin's folder just as surely)
  and **derived** — a plugin is any project naming the plugin contract, so the tenth plugin is covered
  the day it is added with no edit to the rule. ⚠ **This is the only one of the four that a later
  batch answered by agreeing with the plan rather than by amending it**, and it is worth leaving
  visible: a design record whose corrections are all in one direction is not being read carefully.
  ⚠ **And the rule that answered it had never produced an answer.** It lived inside
  `CheckArchitecture`'s `Executes`, so the only way to see what it said was to run a target that
  compiles the solution in Release — which the batch that wrote it was not permitted to do. The rule
  is now a pure function of project files that the gate is one caller of and
  `PluginReferenceRuleTests` is the other, run over this tree and over a fixture where a plugin
  reaches the application through an intermediate. Its first answer corrected its own subject set:
  **eight plugins, not nine** — `Vixen.Editor.App` references the plugin contract because it *hosts*
  plugins, and was being counted as one of the projects it bans. It could never have produced a
  violation, which is exactly why nobody saw it: a rule's subject set is the half of it that nothing
  checks.
- **11, a device confirmed by name in every GPU test.** Every device file does it, through one
  harness, by convention. Nothing enumerates the device files and requires the next one to.

⚠ **And 2 is false as written, which no reading of the criteria predicted.** "Every atomic node,
baked at 1K and at 4K, agrees within 2/255 after downsampling" cannot hold for a **hard-edged** source
and the reason is a property of the picture rather than a defect in any kernel: the 4K bake is
anti-aliased by the downsample and the 1K one is not, so a falloff-zero disc or a checkerboard
disagrees by a full step all the way round every boundary while agreeing *exactly* everywhere else.
The comparison is meaningful for a field band-limited at the **lower** resolution — a soft-edged
shape, a gradient, a noise — and that is where the suites make it. The criterion needs a stated scope
rather than a wider tolerance ([#640](https://github.com/Rikarin/Vixen/issues/640)); widening it to
2/255-except-at-edges would delete the D8 bug it exists to catch. ⚠ And "every atomic node" is the
same unenumerated shape 3, 4 and 11 had — it is checked for the nodes somebody wrote a case for, and
it is now the **last** of the four to be, because the other three were made mechanical and this one
cannot be until its scope is stated. That order is backwards: a criterion that is false as written
should be fixed before the three that were merely unenforced.

⚠ **And 1 was a threshold nothing gates**: the timing is recorded and printed, and what is asserted is
a hang check. That is the right call for a wall-clock budget in this repository — a number calibrated
on an idle machine is its single largest flake source — so the criterion now says *recorded*, because
"under 250 ms" read as a gate and was not one. What it gained instead is the gate that a millisecond
budget is really about: three deterministic counters, each of whose failures **draws exactly the same
picture** and so is invisible to every golden and every closed form in the area. Forty ops in forty
textures is 2.6 GB at 4K rather than 128 MB; forty frames rather than one is forty device drains; forty
compilations rather than one runs the Raven front end once per op for a single image. ⚠ The measurement
itself has since printed 27 ms and 40 ms at 2048² on the same laptop in two different weeks, which is
the argument in one line.

**6 and 8 name phases rather than properties**, so they could not be measured before M7 and M9
existed. That is not a failure of the criteria and it is not a result either; it is why the exit
criteria are counted at the end of the document and not at the end of a batch. ⚠ **M7 has since
landed and 6 is measured**, which is the shape working as intended — a criterion naming a phase
becomes measurable exactly once, when the phase arrives, and needs no amendment to do it. 8 still
waits on M9. What 6's first measurement *did* change is § D1: the stack compiles by **building the
graph** and handing it to the one public compiler, so there are not two compilers here to disagree,
and the differential is left measuring the round trip and the decoration rather than two independent
emitters. The criterion is met and it is a weaker statement than its wording implies — which belongs
here, because that is a fact about the criterion.

⚠ **The pattern under all of this is one rule, applied to a plan instead of to a test.** Every
criterion above that says "per node" or "every" is an exact-equality claim over a surface later
slices grow — and five such roll calls in this workstream have been green on a branch and red on the
merge. A criterion of that shape has to be *derived* — enumerate the folder, enumerate the registry,
compare — or carry a named exemption with a reason. Written as prose in a plan, it is satisfied by
whatever exists on the day somebody reads it.

---

## What this does not become

- **An `.sbs` / `.sbsar` reader or writer.** `.sbsar` is a cooked format whose evaluation is the
  Substance engine's; implementing it means either reverse-engineering a proprietary runtime or
  shipping one. ⚠ **A `.sbsar` importer that bakes through the vendor's own tooling is a different
  thing and is not refused** — it is an importer somebody can write as a plugin, on their own machine,
  against a licence they hold.
- **A runtime substance engine.** Materials do not re-evaluate a graph at runtime; that is what the
  shader graph is, and D2 is why they are different tools. The one thing that would legitimately want
  it — a material whose parameters vary per instance — is served by exposing shader-graph properties,
  not by running an image graph per frame.
- **A general 2D image editor.** No selections, no text layout beyond a `Text` node, no filters
  gallery. The 2D view is for looking at a texture and painting into an atlas.
- **A competitor on node count.** D5's atomic set is the deliverable; the library is content, and it
  grows the way a library grows.
- **A second scripting language.** D6.
- **A second UV channel or a second unwrapper.** Doc 42 § B3 and § D1 stand; this consumes them.
- **A mesh editor.** A mesh arrives from the importer, doc 41 or doc 42. The tool textures it.

---

## Appendix A — The reference feature inventory

Compiled from vendor documentation and product pages (see [the references, as read](#the-references-as-read)),
so that **nothing the three tools do is lost by not appearing in Parts 3–6**. It is a checklist to
re-read at each phase's exit, not a promise: a row marked ● is in the 13.75 EM, and a row marked 🕓 is
work this document has *named* and not budgeted, which is the state a feature should be in rather than
forgotten.

**Legend** — ● in scope, with the phase · ◐ partly, with what is left out · 🕓 named, not budgeted ·
✖ refused, with the reason · — not applicable · ? not established from the documentation read.
**SD** Substance 3D Designer · **SP** Substance 3D Painter · **IM** InstaMAT.

### A.1 Graph authoring

| | SD | SP | IM | Here |
|---|---|---|---|---|
| A node graph of image operations | ✅ | — | ✅ | ● M4 |
| An atomic node set | ✅ | — | ✅ | ● M2–M3, enumerated in [Part 4](#part-4--the-node-catalogue) |
| Compounds / instanced sub-graphs | ✅ | — | ✅ | ● M4 — inlined, § D9 |
| Search-to-create, drag-from-port | ✅ | — | ✅ | ● **free** — `NodeGraphView` |
| Per-node preview thumbnails | ✅ | — | ✅ | ● **free** — `NodePreview`, `INodePreviewSource` |
| Comments, frames, groups | ✅ | — | ✅ | ◐ free, but ⚠ a node in two groups draws in one — [#214](https://github.com/Rikarin/Vixen/issues/214) |
| Reroute / dot nodes | ✅ | — | ✅ | 🕓 a canvas feature, not a node |
| A 2D view with channel isolation | ✅ | ✅ | ✅ | ● M4 — § B6 |
| **Function graphs** (a parameter *is* a program) | ✅ | — | ✅ | ✖ § D6 — a Raven expression in a field |
| **Pixel processor** | ✅ | — | ✅ | ● M4 — § D6, in Raven |
| **FX-Map** (recursive quadrant splatter) | ✅ | — | — | ✖ § D7 — `Tile Sampler` + `Splatter` |
| Value processor (arithmetic on a value, not an image) | ✅ | — | ✅ | ✖ § D6 |
| Iteration / loops over a sub-graph | ◐ | — | ✅ nPass | 🕓 M10's optional half |
| Meshes, point clouds and images in one graph | ◐ | — | ✅ | ✖ mesh maps enter by usage (§ 4.8); point clouds are not a Vixen medium |
| MDL material graphs | ✅ | — | — | ✖ there is no MDL anywhere in Vixen |
| A dedicated noise editor | ✅ | — | ? | ✖ `Noise`'s own parameters are it |
| Dependency management / relocation | ✅ | ✅ | ✅ | ● **free** — `.meta` GUIDs and the asset database |
| A text file that merges | ◐ | ✖ `.spp` is binary | ? | ● YAML with stable identities — Part 5 |

### A.2 Parameters, publishing, resolution

| | SD | SP | IM | Here |
|---|---|---|---|---|
| Exposed parameters with ranges and groups | ✅ | — | ✅ | ● M4 — § D9 |
| Parameter presets saved in the graph | ✅ | — | ✅ | 🕓 M10 — `.vxsmartmat` is the stack's version of it |
| Output **usages** | ✅ | ✅ | ✅ | ● M5 — § 4.8 |
| System variables (`$outputsize`, `$randomseed`) | ✅ | — | ✅ | ● M1 — resolution and seed are plan inputs |
| Absolute vs relative-to-parent resolution | ✅ | — | ✅ | ● § D8 — relative only, with `Crop` and `Resample` as the two exits |
| 8 / 16 / 32-bit and float formats | ✅ | ✅ | ✅ | ◐ M1 — R8…RGBA16F, of which three can be written. **32-bit float is not a material-map format**; ⚠ it is available in Raven and the RHI today and `rgba32f` is owed to § 4.5's two position-carrying records — [#690](https://github.com/Rikarin/Vixen/issues/690) |
| Physical size metadata | ✅ | — | ? | 🕓 |
| A cooked, runtime-parameterised material (`.sbsar`) | ✅ | consumes | ◐ | ✖ § What this does not become |
| Importing `.sbs` / `.sbsar` | ✅ | ✅ | ✅ | ✖ same — and an importer over the vendor's own tooling is a third-party plugin's business |

### A.3 Layering and painting

| | SD | SP | IM | Here |
|---|---|---|---|---|
| Texture sets per material slot | — | ✅ | ✅ | ● M7 — § D11 |
| Configurable channel list | — | ✅ | ✅ | ● M7 |
| A non-destructive layer stack | — | ✅ | ✅ | ● M7 |
| Fill layers, projected UV / triplanar / planar | — | ✅ | ✅ | ● M7 |
| Paint layers | — | ✅ | ✅ | ● M9 |
| Filter / adjustment layers | — | ✅ | ✅ | ● M7 |
| Groups with their own mask | — | ✅ | ✅ | ● M7 |
| Per-channel enable, opacity, blend mode | — | ✅ | ✅ | ● M7 |
| A mask with an effect stack | — | ✅ | ✅ | ● M8 |
| **Anchor points / layer references** | — | ✅ | ✅ | ● M8 — and § D1's whole argument |
| Smart materials | — | ✅ | ✅ | ● M10 |
| Smart masks | — | ✅ | ✅ | ● M10 |
| Colour / ID selection masks | — | ✅ | ✅ | ● M8 — the ID bake plus a colour pick |
| Geometry masks (hide by mesh part) | — | ✅ | ✅ | 🕓 needs a per-triangle selection the paint path has no other use for |
| **UDIM tiles** | — | ✅ | ✅ | 🕓 doc 42 § D11 makes it a tiling of the packer; **the painting half is not budgeted** |
| 3D projection painting | — | ✅ | ✅ | ● M9 |
| 2D painting on the UVs | — | ✅ | ✅ 2026 | ● M9 |
| Stencils / projecting an image | — | ✅ | ✅ | 🕓 M9 stretch |
| Planar symmetry | — | ✅ | ✅ | ● M9 |
| Radial symmetry | — | ◐ | ✅ 2026 | 🕓 |
| Stroke smoothing / lazy mouse | — | ✅ | ✅ 2026 | ● M9 |
| Curve and path strokes | — | ◐ | ✅ 2026 | ● M9 |
| Brush alphas and presets | — | ✅ | ✅ | ● M9 |
| Tablet pressure and tilt | — | ✅ | ✅ | ◐ M9 — pressure needs a platform input path that does not exist; named here rather than assumed |
| Particle brushes / dynamic strokes | — | ✅ | ? | ✖ a simulation inside a brush; not planned |
| UV reprojection when the mesh changes | — | ✅ | ✅ | 🕓 **a real gap**, and the one an artist notices on day two of a production |
| Sparse virtual texturing for the paint buffer | — | ✅ | ? | ✖ the resolution ceiling is stated instead of engineered around |

### A.4 Baking

| | SD | SP | IM | Here |
|---|---|---|---|---|
| High→low bake with a cage | ✅ | ✅ | ✅ | ● **exists** — `MapBaker` |
| Normal, height / displacement | ✅ | ✅ | ✅ | ● **exists** |
| AO · bent normal · curvature · thickness · position · world normal · ID | ✅ | ✅ | ✅ | ● M6 — § D12's seven. ⚠ This row listed `opacity` as an eighth; it is an output usage, not a measurement of a mesh |
| Matching by mesh name (`_low` / `_high`) | — | ✅ | ✅ | 🕓 M6 stretch — the importer already knows the names |
| Ray-traced bakers (irradiance, shadows, caustics) | ✅ | — | ✅ | ✖ |
| GPU bakers | ◐ | ✅ | ✅ | ✖ CPU and deterministic, which is doc 41 § D17's argument, not an oversight |
| 8K and non-square bakes | ✅ | ✅ | ✅ | ◐ square only — `BakeSettings.Resolution` is one number |
| Bevel baking | ✅ | — | ✅ | ● the `Bevel` compound, § 4.9 |

### A.5 Scan and photo processing

| | SD | SP | IM | Here |
|---|---|---|---|---|
| Make-it-tile / smart auto-tile | ✅ | — | ✅ | ● M10 compound — doc 40 § D2's first row |
| Delight / colour equalizer | ✅ | — | ✅ | ● M10 compound — its second |
| Crop tool | ✅ | — | ✅ | ● § 4.3 |
| Multi-angle → albedo / normal | ✅ | — | ✅ | 🕓 needs a capture rig, not a node |
| Smart patch clone | ✅ | — | ✅ | 🕓 |
| Image → PBR decomposition | ◐ | ◐ | ✅ Materialize | ● **doc 40's panels**, not this document |
| AI super-resolution and synthesis | — | — | ✅ | 🕓 doc 40's provider seam |

### A.6 Viewport, preview and rendering

| | SD | SP | IM | Here |
|---|---|---|---|---|
| A real-time PBR viewport that matches the engine | ✅ | ✅ | ✅ | ● **free, and better than the references can be** — it *is* the engine, so there is no second shading model to keep in step |
| Environment / IBL switching, exposure | ✅ | ✅ | ✅ | ● free — `StandardFrame`, `.vxlook` |
| Shader settings (SSS, displacement, tessellation) | ✅ | ✅ | ✅ | ◐ whatever the engine's material features carry |
| Camera bookmarks, post effects in the view | ◐ | ✅ | ✅ | ● free — doc 24's bookmarks, doc 32's volumes |
| Path-traced preview (Iray) | — | ✅ | ✅ | ✖ |

### A.7 Output and integration

| | SD | SP | IM | Here |
|---|---|---|---|---|
| Export presets per target engine | ✅ | ✅ | ✅ | ● M10 |
| Channel packing (ORM and friends) | ✅ | ✅ | ✅ | ● M5 |
| Bit depth and compression per output | ✅ | ✅ | ✅ | ● **free** — `TextureImportSettings` |
| File formats (PNG · TGA · EXR · DDS · TIFF) | ✅ | ✅ | ✅ | ◐ PNG and KTX2; anything else is the existing importer's question, not this tool's |
| DCC and engine integrations (Maya · Max · Blender · UE · Unity) | ✅ | ✅ | ✅ | ✖ **n/a by construction** — the output is already in the project it will be used in, which is the one structural advantage of building this inside the engine |
| "Send to" round trips | ✅ | ✅ | ✅ | — |

### A.8 Automation and extensibility

| | SD | SP | IM | Here |
|---|---|---|---|---|
| Command-line batch processing | ✅ `sbscooker`/`sbsrender`/`sbsbaker` | ◐ | ✅ Pipeline | ● M5 — `vixen texture bake`, running **the same code the panel runs** |
| CI/CD integration | ✅ | ◐ | ✅ | ● free — it is the content build |
| A scripting API (Python / C++ SDK) | ✅ | ✅ | ✅ | ✖ ADR-002 and doc 36 — an extension is a C# assembly, and there is no scripting host to add |
| Custom node libraries and shelves | ✅ | ✅ | ✅ | ● M10 — a folder of `.vxtexgraph` |
| A third-party plugin surface | ✅ | ✅ | ✅ | ● **the whole thing is one** — § D14 |

### A.9 Content, which is the honest gap

| | SD | SP | IM | Here |
|---|---|---|---|---|
| A stock library of materials, filters and generators | ✅ several hundred | ✅ | ✅ 1000+ | ◐ **24 compounds and 6 smart materials at M10** |
| Monthly asset drops | — | ✅ | ✅ | ✖ |

⚠ **This is the row no engineering plan closes.** A tool with forty-one kernels and eleven compounds
is a tool an artist opens once. The references' libraries are years of full-time content authoring,
they are what people actually buy, and M10's two dozen is a *seed* — enough to prove the atomic set is
sufficient and to texture the samples, and not enough to compete. Saying so here is cheaper than
discovering it in a review after M9.

### A.10 The four permanent refusals, in one place

| | Why |
|---|---|
| A runtime `.sbsar`-equivalent | § D2 — runtime procedural material *is* the shader graph, and it already exists |
| `.sbs` / `.sbsar` read or write | A cooked format whose evaluator is the vendor's; a converter using their own tooling is a plugin somebody else writes under a licence they hold |
| A Python or C++ scripting host | ADR-002 and doc 36 — metaprogramming is Roslyn, extension is an assembly |
| DCC integrations | The output is written into the project the engine already opened |

---

## See also

- [23 — Bindless materials](23-bindless-materials.md) · what a sampling feature costs, and the pairing
- [36 — An extensible editor](36-an-extensible-editor.md) · § D4 and § F2, which this is the proof of
- [39 — Standard frame and render presets](39-standard-frame-and-render-presets.md) · the surface that
  *is* a graph, and **Explode**
- [40 — AI-assisted material generation](40-ai-assisted-material-generation.md) · the deterministic
  half it named and did not build; ⚠ its § B1 is corrected in [Part 1](#part-1--what-vixen-already-has)
- [41 — Automatic retopology](41-automatic-retopology.md) · § R5's transfer and bake, and the atlas
- [42 — UV unwrapping](42-uv-unwrapping.md) · § B4's texels-and-margin, which is § D8 again
- [`Vixen.Editor.NodeGraph`](../../Editor/Vixen.Editor.NodeGraph/README.md) ·
  [`Vixen.Editor.ShaderGraph`](../../Editor/Vixen.Editor.ShaderGraph/README.md) ·
  [`Vixen.Editor.Plugin`](../../Editor/Vixen.Editor.Plugin/README.md) ·
  [`map baking`](../guide/engine/map-baking.md)
- Issues: [#371](https://github.com/Rikarin/Vixen/issues/371) ·
  [#493](https://github.com/Rikarin/Vixen/issues/493) ·
  [#494](https://github.com/Rikarin/Vixen/issues/494) ·
  [#512](https://github.com/Rikarin/Vixen/issues/512)

### The references, as read

- Substance 3D Designer — [atomic nodes](https://helpx.adobe.com/substance-3d-designer/substance-compositing-graphs/nodes-reference-for-substance-compositing-graphs/atomic-nodes.html),
  [FX-Map](https://experienceleague.adobe.com/en/docs/substance-3d-designer/using/substance-graphs/nodes-reference-for-substance-graphs/atomic-nodes/fx-map),
  [pixel processor](https://experienceleague.adobe.com/en/docs/substance-3d-designer/using/substance-graphs/nodes-reference-for-substance-graphs/atomic-nodes/pixel-processor),
  [function graphs](https://helpx.adobe.com/substance-3d-designer/function-graphs/the-function-graph.html)
- Substance 3D Painter — [mesh maps](https://experienceleague.adobe.com/en/docs/substance-3d-painter/using/content/creating-custom-effects/mesh-map)
- InstaMAT — [Element Graph](https://docs.instamat.io/Products/InstaMAT_Studio/Canvas/Element_Graph),
  [material layering](https://docs.instamat.io/en/Products/InstaMAT_Studio/Material_Layering),
  [asset texturing](https://docs.instamat.io/en/Products/InstaMAT_Studio/Layering),
  [the 2026 release](https://www.cgchannel.com/2026/03/abstract-releases-instamat-2026-with-new-curves-brushes/)
