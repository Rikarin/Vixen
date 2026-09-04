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
        └── Shaders/*.rvn          the 44 compute kernels
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
   44 compute kernels and their SPIR-V in `Core/` would grow every build for a capability no build
   uses. This is doc 40 § D9's decision — *inference is an editor assembly, not a core one* — reached
   by the same argument for the deterministic half doc 40 left out.
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
two divergent copies of `Ui.rvn` that each matched the module beside it. **Forty-four kernels are not
forty-four more tuples.** M1 makes that half read its folders, which is
[#371](https://github.com/Rikarin/Vixen/issues/371)'s and
[#512](https://github.com/Rikarin/Vixen/issues/512)'s shape a third time, and exit criterion 3 is the
same assertion one layer up.

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

### D5. Forty-four atomic kernels, and everything else is a compound

The full list is [Part 4](#part-4--the-node-catalogue) and it is not repeated here — a vocabulary
written down twice is two lists that have to agree, which is the argument `NodeGraph`'s own README
makes about declaring a port's type beside its field.

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

1. **A document type.** `.vxtexgraph` and `.vxlayers` need an editor registration, a create-menu entry
   and a thumbnail — and doc 36 § D4's last two rows (`AddSettingsPage`, `AddPreview`) are the two of
   nine that were never built. **This plugin is the consumer that makes them worth building**, and if
   `AddPreview` still does not exist when M4 lands, the plugin's own asset thumbnails are the evidence.
2. **A GPU device.** No plugin has yet asked the host for one. `ShaderGraphPreviewRenderer` shows an
   editor assembly holding an `IGraphicsDevice`, but the plugin contract publishes `EditorProject`,
   `SceneDocument`, `DrawerRegistry` and `IEditorRegistry` — and not a device. ⚠ **Either a device is
   published through `PluginServices` or a third party cannot write anything that draws**, which is a
   real gap in the extensibility claim and is exactly the kind doc 36 § F2 was written to find.

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
  asked for `Image` and `Mesh` and neither was added — and grey/colour is a *format* on it. Grey into
  a colour port splats; colour into a grey port is a type error naming the port. ⚠ This is
  `DynamicVector`'s widening rule reused rather than a second type system, and it is what stops the
  library needing a `BlendGrayscale` beside every `Blend`.
- **Every scalar parameter accepts a Raven expression** over the graph's exposed parameters (§ D6), so
  `amount * 0.5 + rust` is a field rather than eleven nodes.
- **Every radius, width and length is in texels at the base resolution** (§ D8), and the evaluator
  scales it.

### 4.1 Sources — 8 kernels

| Node | Out | Parameters | |
|---|---|---|---|
| **Uniform** | image | colour or grey, format | The `float4` every "which node is at fault" bisection starts from |
| **Bitmap** | image | asset, filter, **colour space** | ⚠ An sRGB texture decoded as linear and then blended is the commonest wrong-looking graph there is. The node decodes on the asset's declared space and the port carries it |
| **Gradient** | image | linear · radial · angular · reflected, angle, centre, ramp | The ramp is `Vixen.Ui.Controls.Advanced`'s `Gradient`, and ⚠ this is **`GradientEditor`'s first production consumer** — `overview.md:270` records that it has none, and a grep confirms it: the control, its tests and a string table |
| **Shape** | grey | disc · square · triangle · paraboloid · gaussian · cone · half-bell · gradation, scale, rotation, falloff | The splatter's usual pattern input. Analytic rather than rasterised, so it is exact at every resolution — which is half of D8's scale-invariance criterion passing for free |
| **Noise** | grey **+ cell id** | basis: value · gradient · worley · white; octaves, lacunarity, gain, **seed**, tiling | ⚠ One kernel with a **permutation**, because that is how this engine already varies a shader. Worley also outputs F1, F2 and a **cell index** — which is what a splatter wants and what saves a flood fill downstream |
| **Checker** | grey | scale, rotation, offset | `ComputeColor.rvn:169` has one already, for the shader graph |
| **Text** | grey | string, font, size, alignment, tracking | ⚠ Nearly free: `Vixen.Ui.Text` shapes, breaks, itemises and rasterises today, and its `Outlines` path is what a 4K texture wants rather than the glyph atlas |
| **Svg Path** | grey | path data (`d`), fill rule, scale | ⚠ A wrap: `Core/Vixen.Ui/SvgPath.cs` parses path data and `Rendering/PathTessellator.cs` fills it. **A whole `.svg` document — groups, strokes, its own gradients — is not this node**, and is refused |

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

### 4.6 Surface — 6 kernels

| Node | Parameters | |
|---|---|---|
| **Height → Normal** | intensity, format | ⚠ **The green convention** is whatever `TexturedNormalMapSurface` samples, asserted by a test against a known ramp rather than claimed by a comment. A flipped green is the defect that survives every review because it looks like lighting |
| **Normal → Height** | iterations | The Poisson solve doc 40 named. ⚠ **The solver exists**: doc 42 § B1 recorded that there was no sparse linear solver anywhere in the repository and then built one — `Vixen.Geometry.Uv/Solving/ConjugateGradient.cs`, warm-started, with a *fixed iteration budget because a residual test is not deterministic*. A grid Poisson is the easiest client it will ever have. ⚠ It is also the one entry here that is **not** a compute kernel: it runs on the CPU, which is a deliberate exception to D3 and carries a comment saying so |
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
| Mesh maps | § D12's ten: normal · displacement · AO · bent normal · curvature · thickness · position · world normal · ID · opacity |
| Brush | radius · flow · spacing · falloff · rotation · jitter · alpha · symmetry · curve mode |

### 4.11 The count, and the claim it corrects

| | |
|---|---|
| Compute kernels | **44** — 8 sources · 9 colour · 5 space · 11 filters · 3 analysis · 6 surface · 2 placement |
| Node classes | **49** — those, plus the five of [4.8](#48-graph-structure--5-node-classes-no-kernel) |
| Not a kernel | One: `Normal → Height`, on the CPU, by exception |
| Shipped compounds | **24 ●** of the ~60 named in [4.9](#49-the-compound-library--content-not-code) |

⚠ **Forty-four is not the reference's twenty-four, and § D7 is the reason.** In Designer every noise
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
(R8 / RG8 / RGBA8 / R16F / RGBA16F), the resolution rules of D8, and the seed — **plus turning
`CheckShaders`' `EditorSources` from a hand-kept list into a folder read**, per D1. Tests are device
tests that name their adapter and skip loudly without one.

### M2 — The atomic kernels, part I · 1.5 EM

[4.1](#41-sources--8-kernels), [4.2](#42-colour-and-channels--9-kernels) and
[4.3](#43-space--5-kernels) — twenty-two kernels. Every one gets a golden, a closed-form assertion
where one exists, a scale-invariance check at ×2, and a sabotage that proves the golden red.

### M3 — The atomic kernels, part II · 1.5 EM

[4.4](#44-filters--11-kernels) through [4.7](#47-placement--2-kernels) — twenty-two more, and the four
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

1. **A forty-node graph at 2048² evaluates in under 250 ms** on the reference machine, and a parameter
   change re-evaluates only the affected sub-graph.
2. **Scale invariance.** Every atomic node, baked at 1K and at 4K, agrees within 2/255 after
   downsampling. ⚠ A node that fails this has D8's bug and no other test finds it.
3. **A golden per node, and the library is read rather than listed.** The test enumerates
   `Vixen.Editor.TextureGraph/Shaders` and fails on a kernel with no golden — [#512](https://github.com/Rikarin/Vixen/issues/512)'s
   shape, applied before it can go wrong rather than after.
4. **A sabotage per node.** Perturb a kernel's constant; the golden goes red. A node whose golden
   survives is a golden of a black image.
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
| 8 / 16 / 32-bit and float formats | ✅ | ✅ | ✅ | ◐ M1 — R8…RGBA16F. **32-bit float is not planned** |
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
| AO · bent normal · curvature · thickness · position · world normal · ID · opacity | ✅ | ✅ | ✅ | ● M6 — § D12 |
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

⚠ **This is the row no engineering plan closes.** A tool with forty-four kernels and eleven compounds
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
