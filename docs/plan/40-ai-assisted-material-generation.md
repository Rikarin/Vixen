<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 40 — AI-assisted generation: materials, meshes and retexturing

⚠️ **Extends [36](36-an-extensible-editor.md), [11](11-editor.md) and [08](08-asset-pipeline-and-addressables.md);
is [38](38-learned-terrain-generation.md)'s sibling and takes [39](39-standard-frame-and-render-presets.md)'s
authoring shape.** Doc 38 put ONNX Runtime in the repository as one plugin's private dependency and
wrote down, in as many words, that arguing for a general ML seam on the strength of a single consumer
would be wrong. There is now a second one. Doc 39 answered a different question — *how do you give
somebody four knobs over a thing that is really a graph, without lying about the graph* — and its
answer is this document's authoring model.

Two references prompted it:

- [amtarr/ComfyUI-TextureAlchemy](https://github.com/amtarr/ComfyUI-TextureAlchemy) — Apache-2.0
- [ubisoft/ComfyUI-Chord](https://github.com/ubisoft/ComfyUI-Chord) — Ubisoft ML License, research-only

**They are not the same kind of thing, and the difference decides most of this plan.** One is a model.
The other has no model in it at all.

**The claim this document has to earn.** An artist types *"weathered ship-hull steel, riveted"* into a
panel and gets a tiling PBR material — base colour, normal, roughness, metalness, height, occlusion —
written into the project as ordinary texture files with a `.vxmat` beside them. A second panel does
the same for a mesh. A third retextures a mesh that already exists. None of them shows a graph, all
three *are* graphs, and pressing **Explode** turns any of them into the graph it stood for. Vixen
redistributes no model weights, the editor's download does not grow for a project that never opens the
panel, and every generated file is a file an artist can open in Photoshop and a reviewer can diff.

⚠ **The half of that claim that needs no model at all is the half that ships first**, and it is not a
compromise — see [D2](#d2-the-deterministic-half-is-not-a-provider-and-it-ships-first).

---

## Part 0 — What the two references actually are

Surveyed at the versions live on 2026-08-05, by reading the code rather than the README.

### ComfyUI-Chord is a model, and a surprisingly cheap one

`config/chord.yaml` and `nodes.py` say what the paper's page does not:

| | |
|---|---|
| Backbone | **Stable Diffusion 2.1** — UNet, VAE and a CLIP text encoder, `fp16: true` |
| Sampling | **Single-step, image-conditioned.** `model(x)`, no scheduler loop |
| The chain | `basecolor ← render` · `normal ← render + approxIrr` · `rou_met ← render + approxRM` |
| Conditioning | "LEGO" — modality-specific weights over a shared backbone |
| Resolution | Fixed **1024²** internally; the node resizes in and back out |
| Prompts | **Constants** — `"Basecolor"`, `"Normal"`, `"Roughness and Metallic"` |
| Tiling | `vae_padding: circular`, and every `Conv2d` is switched to circular padding at load |
| Height | **Not from the model.** `normal_to_height.py` — a Poisson solve over overlapping subregions |
| Weights | `chord_v1.safetensors`, **2.76 GB**, gated, trained on [MatSynth](https://huggingface.co/datasets/gvecchio/MatSynth) |
| Licence | Ubisoft Machine Learning License — research-only, copyleft |

Three of those rows are why this is buildable rather than aspirational.

⚠ **Single-step means three network evaluations, not a hundred and fifty.** An ordinary SD 2.1
text-to-image run is 20–50 UNet evaluations. Chord's chain is three stages, each one step. The cost of
a material is therefore within a small constant of *one* diffusion step at 1024² — which is the
difference between "a plausible editor button" and "a render farm".

⚠ **The prompts are constants, so the text encoder is a compile-time artefact.** Three fixed strings
means three fixed embeddings. A port evaluates CLIP once, offline, bakes the three tensors, and never
ships a text encoder at all. That is roughly a third of the checkpoint gone and one fewer graph to
export.

⚠ **The tileability is circular padding and nothing else.** It is not a post-process, not a
seam-inpainting pass, not a loss term at inference — it is `layer.padding_mode = 'circular'` applied
to every convolution in the UNet and the VAE before the forward pass. Any port gets tiling for free
and any port that forgets gets visible seams.

⚠ **Chord is an *estimator*, and the panel this document builds asks for text.** The paper's pipeline
is generate-then-estimate: a fine-tuned text-to-image model makes a tileable texture, and Chord
decomposes it. So "text → PBR material" is always at least two stages, and the second is the
interesting one — see [D3](#d3-the-graph-is-of-intent-not-of-comfyui-nodes).

### ComfyUI-TextureAlchemy is not a model at all

363 KB of Python, zero weights, Apache-2.0. Fifty-odd nodes across nine categories, and every one of
them is ordinary image processing over a tensor:

| | |
|---|---|
| `pbr_extractor_node.py` | Takes **Marigold's** appearance + lighting outputs and applies gamma, brightness and a luminance weighting. That is the whole "extraction" |
| `normal_utils`, `height_advanced` | Height ↔ normal, normal combining, curvature |
| `texture_utils` | Seamless tiling with an edge mask for inpainting, scaling, projection |
| `filter_utils`, `detail_utils` | The high-pass "texture equalizer" that removes baked-in lighting |
| `channel_utils` | ORM / RMA packing, RGB split and merge |
| `effect_utils` | Wear and edge masks driven by curvature |

⚠ **It contributes no intelligence, and that is a finding rather than a criticism.** Its AI comes
entirely from optional third-party nodes — [Marigold](https://github.com/prs-eth/Marigold) for
intrinsic decomposition, Lotus for normals — and what it adds around them is a toolbox. Every node in
it is a pure function over a `float[]` that a C# method writes in twenty to a hundred lines, testable
against images built in a test with no GPU, no Python and no licence question.

**So the two references answer two different questions**, and the plan has to keep them apart:

| | TextureAlchemy | Chord |
|---|---|---|
| What it is | A toolbox | A model |
| What it needs | Nothing | 2.76 GB, SD 2.1, a GPU |
| Portable to C# | **Entirely**, and cheaply | Only via an export somebody has to make |
| Worth having if no model ever ships | **Yes** | — |

---

## Part 1 — What blocks it

Five. The first is much larger than the rest, and it has nothing to do with AI.

### B1. Vixen's materials sample exactly one texture ⛔

[`MaterialFeatures.cs`](../../Core/Vixen.Rendering/Materials/MaterialFeatures.cs) has thirteen
features. **One of them names a map:**

| Feature | What it carries |
|---|---|
| `TexturedMetalRoughnessFeature` | `BaseColorMap` — a name, resolved to a bindless slot |
| `NormalMapFeature` | `Vector3 NormalTS` — a **constant** |
| `OcclusionFeature` | `float OcclusionMap` — a constant, despite the name |
| `MetalRoughness`, `Emissive`, `ClearCoat`, `Sheen`, `Subsurface`, … | constants |

[`overview.md`](../overview.md) records `TexturedMetalRoughnessSurface` as *"the first one there could
be"*, and lists what it needed: `Texture2D[]` as a Raven type, `[Shared]`, `uv` on `MaterialData`, and
a table slot arriving as a value. All four exist. **Nobody has written the second, third and fourth
sampling features.**

⚠ **A generator that produces six maps and a renderer that binds one is a demo, not a feature.** The
honest ordering is that this document's most valuable phase has no AI in it, and its second most
valuable phase is a rendering gap that predates it. This is [23](23-bindless-materials.md)'s machinery
applied four more times — small, mechanical, and genuinely owed by [06](06-rendering-pipeline.md)
rather than by this document. It is scheduled here because nothing else here is worth building first.

### B2. The model licences are the author's to accept, and the editor's to state 🟡

⚠ **This row was written as a blocker in the first revision of this document and that was wrong.**
Vixen ships no weights, hosts no weights and converts no weights. What it ships is a button that
fetches from Hugging Face on the author's machine, under whatever grant the author accepts there. The
licensee is the person who pressed the button — which is the same relationship an editor has to every
other thing it can download.

| | Licence | Vixen redistributes? | The author's position |
|---|---|---|---|
| **Chord** | Ubisoft ML — research-only, copyleft | **No** | Fine for research, prototyping, and a non-commercial project. **Not for a commercial release** |
| **TRELLIS / TRELLIS.2** | **MIT**, code *and* weights | No (still on demand) | Unrestricted |
| Marigold / Marigold-IID | Code Apache-2.0; weights OpenRAIL++ | No | Behavioural use restrictions, commercially usable |
| StableMaterials | OpenRAIL | No | Same shape |
| Stable Diffusion 2.1 (base of most of the above) | CreativeML OpenRAIL++-M | No | Same shape |

**One nuance, stated once because it is easy to conflate.** That purely AI-generated output carries no
copyright — the US Copyright Office's position, and the reason nobody owns the pixels — is a fact
about *the output*. A research-only grant is a restriction on *running the model*, which is a licence
term rather than a copyright claim on what came out. The two do not cancel. But they do land on
different people, and neither lands on Vixen: **we distribute nothing, so the only obligation we have
is to be accurate about the terms on the surface where somebody accepts them** —
[D8](#d8-the-download-button-states-the-terms-and-then-gets-out-of-the-way).

⚠ **So no provider, model or workflow is excluded by this document.** Chord is downloadable from the
panel like anything else, with its terms shown. What would be excluded is Vixen *mirroring* the
weights, converting them to ONNX and hosting that, or shipping a workflow that silently assumes them.

### B3. ComfyUI is GPL-3.0 🟡 *manageable, and it constrains the shape*

[ComfyUI](https://github.com/comfyanonymous/ComfyUI) is GPL-3.0. Talking to a separately-installed
ComfyUI over its HTTP API is arm's length across a process boundary and is what every commercial
integration in the space does; vendoring it, shipping it, bundling a Python runtime with it, or
installing it on the author's behalf is not something an Apache-2.0 editor does.

⚠ **The real cost is not legal, it is that "install Python, install ComfyUI, install a custom node,
download 2.76 GB, start a server on 8188" cannot be the *only* path** — which is
[D1](#d1-one-seam-three-providers)'s third provider and [D9](#d9-inference-is-an-editor-assembly-not-a-core-one--and-doc-38-shares-it)'s
reason for existing.

### B4. Chord has no ONNX export, and one would have to be produced 🟡

The SD 2.1 half is well-trodden — `diffusers`' own conversion script produces UNet, VAE and text
encoder graphs, and ONNX Runtime's C# tutorial and the `OnnxStack` community stack both run exactly
that shape from .NET. What is *not* trodden is Chord's chain and its LEGO conditioning, which live in
Python (`src/module`) and would have to be either traced whole or exported per-stage with the chain
re-expressed in C# — the chain being three stages of forty lines, not a research project.

⚠ **This is the answer to "can .NET skip ComfyUI".** Yes, for a model somebody has exported, and the
export is a one-off script rather than an engine feature. Whether *we* run that script for Chord is
[D8](#d8-the-download-button-states-the-terms-and-then-gets-out-of-the-way)'s question and the answer
is no — an author can, and a licence-clean model makes it moot.

### B5. Mesh generation cannot be embedded at all ⛔ *for the local provider only*

[TRELLIS](https://github.com/microsoft/TRELLIS) and [TRELLIS.2](https://github.com/microsoft/TRELLIS.2)
are **MIT, weights included** — the cleanest licence in this document — and TRELLIS.2 generates
**PBR materials natively**: base colour, roughness, metallic and opacity on the mesh, not just a
diffuse bake. Output is GLB, which
[`ModelReader`](../../Editor/Vixen.Editor.Assets/Models/ModelReader.cs) already imports through
Silk.NET.Assimp. **The ingestion side is free.**

The inference side is not, and it is not a matter of effort. The sparse-voxel backbone is built on
`spconv`, `flash-attn` and `nvdiffrast` — custom CUDA kernels with no ONNX operators behind them.
Sparse convolution is not representable in ONNX and FlashAttention is a kernel rather than a graph.
**There is no export to make.** Add 16–24 GB of VRAM and it is not a thing that runs in an editor
process on an artist's laptop under any packaging.

⚠ **So the mesh panels are provider-only, and that is a permanent-looking property rather than a
phase ordering.** It is also why the seam in [D1](#d1-one-seam-three-providers) matters more than any
one provider: the material panels will eventually have a local path and the mesh panels will not, and
an author should not be able to tell from the panel.

---

## Part 2 — The design

### D1. One seam, three providers

```
Vixen.Editor.Generation          the seam:  the plan, the providers, provenance,
                                            the model cache and the download
      ├── …Generation.Comfy      an HTTP client for a ComfyUI the author installed
      ├── …Generation.Onnx       in-process, via Vixen.Editor.Inference
      └── …Generation.Remote     a hosted API, behind the same interface
```

| Provider | Needs | Reaches |
|---|---|---|
| **ComfyUI** | A running ComfyUI the author installed | Everything, including meshes and anything released next week |
| **Local ONNX** | An exported graph in the per-user cache | Materials. Never meshes — [B5](#b5-mesh-generation-cannot-be-embedded-at-all--for-the-local-provider-only) |
| **Hosted** | A key and a network | Everything, for studios that would rather pay than provision GPUs |

⚠ **One interface, and the panel does not know which it has.** A provider reports what it can do —
`Capabilities` — and a panel offers what is reachable and says plainly what is not, rather than
failing at the end of a two-minute task.

### D2. The deterministic half is not a provider, and it ships first

`Vixen.Editor.Texturing` — a pure, model-free image kernel, `float[]` in and `float[]` out, no device,
no network, no weights. TextureAlchemy's ideas, re-expressed:

| | |
|---|---|
| Seamless tiling | Offset-wrap with an edge mask, so the seam is a region a fill can be run over |
| Delight / equalize | High-pass over a large-radius blur — removes baked lighting from a photograph |
| Height ↔ normal | Both directions. The normal→height direction is Chord's own Poisson solve |
| Curvature, cavity, edge wear | From height, and they drive the masks an artist actually paints with |
| Ambient occlusion from height | Hemisphere sampling on the CPU |
| Channel packing | ORM / RMA, and the split back out |
| Roughness / metalness heuristics | Luminance and gamma, which is exactly what the reference's "extractor" is |

Four reasons this is the first phase and not an afterthought:

1. **It is the whole of what one reference contributes**, and it needs no model, no download, no GPU.
2. **It is useful on its own.** "Make this photograph tile, remove the lighting from it, derive a
   normal and an AO, pack the result" is most of what a texture artist does to a photograph.
3. **Every provider needs it downstream anyway.** Chord returns four maps at the input's resolution
   with no height, no AO and no packing. TRELLIS.2 returns a GLB whose maps still want packing.
   Something has to do the rest, and it is the same code.
4. **It is testable to the standard this repository holds** — a pure function of pixels, asserted
   against images built in a test, which is the argument
   [`SpriteSlicer`](../../Editor/Vixen.Editor.Assets/Textures/SpriteSlicing.cs) already made in this
   exact place.

### D3. The graph is of *intent*, not of ComfyUI nodes

This is the load-bearing decision and every other one about the authoring surface follows from it.

The temptation is to build a ComfyUI in the editor: checkpoint loaders, samplers, CLIP encoders,
latents, VAEs. **Refused.** ComfyUI's graph names ComfyUI's node types, its model files, its samplers
and its schedulers, and it is *good* at that because five hundred people maintain five hundred nodes.
A Vixen copy would be a second ComfyUI maintained by nobody, and every model release would become a
Vixen release.

**A generation graph in Vixen has about a dozen node types, and each is a statement of intent:**

| Node | |
|---|---|
| `Prompt`, `Image`, `Mesh`, `Material` | Inputs, from a field or from the project |
| `GenerateTexture` | Text → a tileable image. *Which* model is the provider's business |
| `EstimatePbr` | Image → base colour, normal, roughness, metalness |
| `GenerateMesh` | Image or text → geometry, and PBR maps if the provider has them |
| `RetextureMesh` | Mesh + prompt → maps on the mesh's existing UVs |
| `RenderViews` | Mesh → N conditioning renders. **Vixen's own**, see [D6](#d6-retexturing-is-where-vixen-has-an-unusual-advantage) |
| `Tile`, `Delight`, `NormalToHeight`, `Ao`, `Curvature`, `PackOrm` | [D2](#d2-the-deterministic-half-is-not-a-provider-and-it-ships-first)'s kernel, one node each |
| `WriteMaterial`, `WriteMesh` | The only nodes that touch the project |

⚠ **The provider resolves intent to execution, and that is the whole point.** `EstimatePbr` on the
ComfyUI provider becomes a Chord subgraph, or a Marigold one, or whatever the author's workflow
names. On the local provider it becomes an ONNX session. On a hosted one it becomes a request. **The
graph an artist saved in 2026 still means something in 2028**, because it says *estimate PBR* and not
*load `chord_v1.safetensors` and sample it with Euler at 20 steps*.

This is [39](39-standard-frame-and-render-presets.md)'s argument about `!StandardFrame`'s seven
semantic knobs, made one layer further out: **they say what is wanted, never how it is wired.**

### D4. Four panels, and each one is a preset node that explodes into the graph

Doc 39's layering, taken directly:

| 39 | 40 |
|---|---|
| `!StandardFrame` with seven knobs | A task panel with five or six fields |
| Its expansion into the frame graph | The panel's expansion into a `.vxgen` graph |
| `vixen frame explode` | **Explode** on the panel |
| Sample 13's document as the reference output | A shipped example graph per panel, as the reference output |

The four panels, which are the product:

| Panel | Fields | Expands to |
|---|---|---|
| **Material from text** | prompt · resolution · maps · style | `Prompt → GenerateTexture → EstimatePbr → Tile → NormalToHeight → Ao → PackOrm → WriteMaterial` |
| **Material from image** | source · maps · post | the same, without the first node |
| **Mesh from text or image** | prompt or image · target triangle count · with-materials | `… → GenerateMesh → WriteMesh` |
| **Retexture a mesh** | mesh · prompt · resolution | `Mesh → RenderViews → RetextureMesh → … → WriteMaterial` |

⚠ **"Modify an existing material or model" is not a fifth panel — it is any of the four re-opened
against a `generation:` block.** See [D7](#d7-refinement-is-a-re-run-and-provenance-is-what-makes-it-possible).

⚠ **The panel is the product and the graph is the escape hatch, in that order.** Most people who want
a material do not want a graph; the ones who do, want the *real* one rather than a simplified toy of
it. Doc 39's word for the alternative is *eject*, and the reason it refused blind ejection is the
reason here: an artist who explodes a panel gets the actual working graph, with comments, and can put
one node in the middle of it.

⚠ **Explode is one-way and says so.** Same as doc 39.

### D5. The graph is `NodeGraphModel`'s, and unlike a behaviour tree it fits

[`Vixen.Editor.NodeGraph`](../../Editor/Vixen.Editor.NodeGraph/README.md) exists and carries the
shader graph, the VFX graph and the compositor. [`Vixen.Editor.Ai`](../../Editor/Vixen.Editor.Ai/README.md)'s
README works through the framework's rules against a behaviour tree and finds two of four fail. The
same exercise against a generation graph:

| `NodeGraphModel`'s rule | A generation graph |
|---|---|
| An edge carries a typed value | ✓ an image, a mesh, a material — values, on edges |
| An input takes one edge | ✓ |
| No cycles | ✓ |
| Ordered children / attachments | — not wanted |

⚠ **Four for four.** This is dataflow, which is what the framework was built for, so the graph editor
is `NodeGraphView` with a node library and nothing else. Search-to-create, sub-graphs, the inspector
beside the canvas, copy/paste, undo per gesture and `NodeGraphLayout` all arrive built.

Two things it needs that do not exist:

1. **Two port kinds.** `PortKind` is a closed enum with `Texture` and `Sampler` in it and nothing for
   an image buffer or a mesh. `Image` and `Mesh` are two members and no rule changes. ⚠ Riding
   `Texture` was considered and refused: it would let search-to-create offer a shader-graph sampler on
   a generation port, which is a nonsense connection the type system would have permitted.
2. **A compile/execute split.** `NodeGraphCompiler<T>` is synchronous and pure, and a generation node
   takes ten to sixty seconds. ⚠ **The graph compiles to a `GenerationPlan` and the plan runs on
   `context.Shell.Tasks`** — compilation stays pure and testable with no provider at all, execution is
   a background task, and results arrive back as node previews through `INodePreviewSource`. **The
   per-node preview swatch is most of what makes ComfyUI legible**, and the framework already has it.

### D6. Retexturing is where Vixen has an unusual advantage

Retexturing an existing mesh means new maps on the UVs it already has. Two families do it: generate
directly in UV space (fast, seams), or generate N views of the mesh and back-project (better, and what
the current mesh-texturing models do).

⚠ **The multi-view family needs somebody to render the conditioning views — depth, normal and mask
from known cameras — and Vixen is a renderer.** That is a compositor, a camera rig and a readback, all
of which exist. `RenderViews` is therefore an *engine* node rather than a provider call, the views are
exactly reproducible from the recorded camera set, and the back-projection is ordinary rasterisation
into the atlas.

⚠ **What Vixen does not have is retopology or automatic UV unwrapping.** `Vixen.Geometry` has
`MeshSurfaces`' world/box/planar projection and doc 24's verbs; it has no unwrapper. A *generated*
mesh therefore arrives with whatever UVs the provider gave it, and a mesh with no UVs cannot be
retextured. This is named rather than solved, and it is the honest limit on the mesh panels.

⚠️ **Amended by [41 — Automatic retopology](41-automatic-retopology.md).** That limit was drawn
correctly for a document about inference and wrongly for the pipeline: a remesher is arithmetic — no
weights, no licence, no download — so it goes in `Core/` beside doc 24's kernel, and its patch layout
yields an atlas for nearly free. Doc 41 § R8 is what turns *"refuses a mesh with no UVs"* into
*"remesh it first"*. ⚠ It is still **not a general unwrapper** — it unwraps meshes it produced — so
the sentence above survives for a mesh whose topology is the point.

### D7. Refinement is a re-run, and provenance is what makes it possible

"Modify an existing material or model" is not an edit-in-place; it is a re-run with one input changed,
and that only works if the previous run wrote down what it was. The `.meta` beside every generated
file carries a `generation:` block:

```yaml
generation:
  graph: Generated/ship-hull.vxgen   # the exploded graph, or the panel preset that stood for it
  provider: comfy                    # or onnx, remote
  model: chord_v1                    # a name and a digest, never a redistributed file
  digest: sha256:…
  prompt: "weathered ship-hull steel, riveted"
  seed: 41823
  maps: [baseColor, normal, roughness, metalness]
  post: [tile, normalToHeight, ao, packOrm]
  writtenDigest: sha256:…            # what we wrote, so a painted-over map is detectable
  at: 2026-08-05T…
```

Which buys five things: **re-open the panel with every field as it was**, **regenerate with one field
changed**, **a diff that says what changed**, **a reviewer who can tell a generated map from a painted
one**, and — the one a studio asks for — **an audit trail of which model touched which shipped asset**.

⚠ **A file an artist has painted over is detected and never silently regenerated.** `writtenDigest` is
what makes that a check rather than a hope.

### D8. The download button states the terms, and then gets out of the way

[38 § D4](38-learned-terrain-generation.md#d4-the-weights-are-downloaded-hashed-and-cached-outside-the-project)
taken as decided — a pinned manifest with a SHA-256 per file, a per-user cache beside the layout and
the keymap, nothing fetched until somebody presses the button — plus two rules doc 38 did not need:

1. ⚠ **The licence name and the size are on the button, and a research-only grant says so in those
   words.** Not a link, not a footnote. Doc 38's weights were MIT and its download control only had to
   be honest about the *size*; here it has to be honest about the *terms*, because an artist who finds
   out after shipping has been failed by this panel. It is one line of text and it is the difference
   between a tool and a trap.
2. ⚠ **A gated repository is handled as a gate, not as a failure.** Chord's Hugging Face repo requires
   accepting terms before the file is served. The panel says so and opens the page rather than
   reporting a 401.

**And then it gets out of the way.** Once accepted, a model is a model: the panel does not re-ask, does
not warn per generation, and does not editorialise about what somebody may build.

### D9. Inference is an editor assembly, not a Core one — and doc 38 shares it

`Vixen.Editor.Inference` — ONNX session lifetime, execution-provider selection, tensor marshalling,
the model cache and its manifest. `Microsoft.ML.OnnxRuntime` + `…DirectML`, trimmed to desktop RIDs,
with doc 38's tier table taken as decided:

| Tier | Cost | Take it? |
|---|---|---|
| `Microsoft.ML.OnnxRuntime` | 15–38 MB per RID; **CoreML free on Apple Silicon** | ✅ |
| `…OnnxRuntime.DirectML` | +11 MB; any Windows GPU, any vendor, no user setup | ✅ |
| `…OnnxRuntime.Gpu` | Hundreds of MB; NVIDIA only; needs a user-side CUDA install | ❌ |

⚠ **This is the promotion doc 38 said to make only when a second consumer appeared, and it goes to
`Editor/`, not to `Core/`.** Doc 38's fourth "what this does not become" is precise about the trigger
and silent about the destination. `Editor/` is the destination because the argument against `Core` is
unchanged: a shipped game does not run an inference runtime, and an abstraction in `Core` would put
one on the trimming report of every game that never asked for it.

⚠ **Doc 38's plugin then references this instead of carrying its own copy**, so whichever of the two
lands second saves the session-and-provider work. Neither blocks the other, and a plugin can still
carry its own — `Vixen.Editor.Plugin`'s README already says a plugin's `runtimes/` is its business.

### D10. What comes back is files, not a buffer

Every generated map, mesh and material is written into the project as an ordinary file and imported by
the importers that already exist —
[`TextureImporter`](../../Editor/Vixen.Editor.Assets/Textures/TextureImporter.cs) and
[`ModelImporter`](../../Editor/Vixen.Editor.Assets/Models/ModelImporter.cs).

| Not this | Because |
|---|---|
| Maps held in the material document | An artist has to be able to open the roughness in Photoshop |
| A generated asset kind that re-runs on import | A build would depend on an inference runtime, a network and a GPU |
| Results cached by prompt | A merge would not be a merge, and a reviewer could not see what changed |

⚠ **The generator's output crosses into the project once and is then content like any other content.**
This is [38 § D2](38-learned-terrain-generation.md#d2-the-output-is-an-import-not-a-layer-that-regenerates)'s
decision, reached independently for the same reason: a model's output is not reproducible across
providers, execution providers or driver versions, so anything that regenerates it silently changes an
artist's work when they open the project somewhere else.

### D11. Licences and NOTICE

| | Licence | Obligation |
|---|---|---|
| TextureAlchemy (ideas, algorithms) | Apache-2.0 | NOTICE row; §4b modification notice on any ported file |
| Chord (read as a reference, nothing taken) | Ubisoft ML, research-only | Cited. **Nothing ported, nothing mirrored** |
| `Microsoft.ML.OnnxRuntime` / `…DirectML` | MIT | NOTICE rows; native attribution through `RestoreNativeDeps` |
| ComfyUI | GPL-3.0 | **No code, no vendoring.** An HTTP client against a documented API |
| Any model an author downloads | Theirs | Presented and accepted in the panel ([D8](#d8-the-download-button-states-the-terms-and-then-gets-out-of-the-way)) |

---

## Part 3 — The authoring surface

Every panel's fields are `[Inspector]` members of a `[DataContract]` settings type, testable with no
window — doc 31 § Part 2's rule, which doc 38 also took.

### Shared by all four

| Row | |
|---|---|
| **Provider** | ComfyUI · Local · Hosted. Unreachable ones say what is missing rather than being hidden |
| **Model** | What the provider reports, with `(licence)` beside it |
| **Target** | The asset to write, and the folder its files land in |
| **Generate** | Background through `context.Shell.Tasks`, per-stage progress, cancellable |
| **Explode** | Writes the `.vxgen` this panel stood for and opens it. One-way, and says so |
| **Empty state** | The licence, the size, and the download control ([D8](#d8-the-download-button-states-the-terms-and-then-gets-out-of-the-way)) |

### Material from text · Material from image

| Row | |
|---|---|
| Prompt, or source image | The one field that differs between the two |
| Resolution | `(derived)` — with the model's native 1024² named, so a 4K request that is an upsample says so |
| Maps | Which of the six to write; four from the model and two derived |
| Post | [D2](#d2-the-deterministic-half-is-not-a-provider-and-it-ships-first)'s chain — tile · delight · AO · pack, each independently on |
| Seed | With a re-roll |

### Mesh from text or image

| Row | |
|---|---|
| Prompt or image | Text goes through `GenerateTexture` first; it is two nodes, not two pipelines |
| Triangle budget | And ⚠ **what the provider gives is what arrives** — [D6](#d6-retexturing-is-where-vixen-has-an-unusual-advantage) is honest that there is no retopology |
| With materials | On if the provider produces PBR maps; TRELLIS.2 does |
| Collider | Through doc 24's `MeshCollision`, which exists |

### Retexture a mesh

| Row | |
|---|---|
| Mesh | ⚠ **Refuses a mesh with no UVs, on the form**, naming the reason — a lesson doc 38 § D5 paid for |
| Prompt | |
| Views | How many conditioning renders, and from where. `RenderViews` is Vixen's own |
| Resolution | Of the atlas, not of the views |

⚠ **No mode, no viewport tool, no key.** [38 § Part 2](38-learned-terrain-generation.md#part-2--the-authoring-surface)'s
rule: this is something you *run*, not something you *do*.

---

## Part 4 — Phases

### T0 — The spike · 0.25 EM

**No engine code.** Three questions.

1. **Is the model output better than the deterministic kernel alone?** Run Chord in ComfyUI over ten
   textures representative of what Vixen's samples use — Samples/13's concrete, brick, metal, fabric —
   and compare each against (a) the hand-authored `.vxmat` and (b) the same photograph through
   equalize → tile → normal → AO with no model at all. ⚠ **If the delta is small on the ordinary
   cases, [T1](#t1--the-deterministic-kernel--10-em) is the feature** and everything after it is
   convenience.
2. **What does an SD-2.1-shaped graph cost from .NET?** Export a stock SD 2.1 UNet + VAE to ONNX and
   time one step at 1024² through `Microsoft.ML.OnnxRuntime` on CPU, on CoreML and on DirectML. Three
   steps is a material; the number decides whether the local provider is a default or a fallback.
3. **What does TRELLIS.2 actually produce for a game?** One image through it, into the project through
   `ModelImporter`, drawn in Samples/13. Triangle count, UV quality, whether the PBR maps are usable.
   ⚠ This is the question that decides whether the mesh panels are a feature or a toy, and it is worth
   asking before T5 rather than during it.

**Exit:** a `RESULT.md` under [`spikes/`](spikes/), ending in which of T3–T6 are worth building.

### T1 — The deterministic kernel · 1.0 EM

[D2](#d2-the-deterministic-half-is-not-a-provider-and-it-ships-first). `Vixen.Editor.Texturing`, no
model, no network, no device. Tiling, equalize, height ↔ normal, curvature, AO, packing, and the
Poisson normal→height solve.

**Exit:** a photograph of a wall becomes a tiling base colour, a normal, a height, an AO and a packed
ORM, from the texture document, with every step asserted against images built in a test.

### T2 — The maps reach the renderer · 0.75 EM

[B1](#b1-vixens-materials-sample-exactly-one-texture-). `TexturedNormalMapFeature`,
`TexturedRoughnessMetalnessFeature` (or ORM), `TexturedOcclusionFeature`, each taking a bindless slot
the way `TexturedMetalRoughnessFeature` already does, with the Raven surfaces beside them.

⚠ **Owed by [06](06-rendering-pipeline.md)/[23](23-bindless-materials.md), scheduled here because
nothing above it is worth having without it.** If it lands from that side first, this phase is struck.

**Exit:** a `.vxmat` naming four maps draws with all four, in `Samples/13`, on a device.

### T3 — The graph and the seam · 1.25 EM

[D3](#d3-the-graph-is-of-intent-not-of-comfyui-nodes), [D5](#d5-the-graph-is-nodegraphmodels-and-unlike-a-behaviour-tree-it-fits),
[D7](#d7-refinement-is-a-re-run-and-provenance-is-what-makes-it-possible), [D10](#d10-what-comes-back-is-files-not-a-buffer).
The `.vxgen` document and its node library, the two port kinds, the compile-to-`GenerationPlan` split,
the provider interface with its capability report, the provenance block, and the write-through to the
importers.

⚠ **The graph is built before the panels**, because a panel is an expansion of it and building the
expansion first would mean designing the graph backwards from four forms.

**Exit:** a `.vxgen` compiles to a plan, the plan runs against a stub provider that returns fixed
images, and the result lands in the project with a `generation:` block — with no model anywhere.

### T4 — The panels and the ComfyUI provider · 1.25 EM

[D4](#d4-four-panels-and-each-one-is-a-preset-node-that-explodes-into-the-graph),
[Part 3](#part-3--the-authoring-surface), [D8](#d8-the-download-button-states-the-terms-and-then-gets-out-of-the-way).
The four presets and their expansions, Explode, the download and licence surface, and an HTTP client
over `POST /prompt` + `ws://…/ws` + `GET /history/{id}` with node-output mapping.

**Exit:** with a ComfyUI running, a prompt becomes a six-map `.vxmat`, the panel re-opens against the
provenance and regenerates identically, and Explode produces a graph that runs and can be edited.

### T5 — Meshes and retexturing · 1.5 EM

[D6](#d6-retexturing-is-where-vixen-has-an-unusual-advantage) and the two mesh panels. `GenerateMesh`
and `RetextureMesh` on the provider side; `RenderViews` and the back-projection on ours; the landing
through `ModelImporter`, the collider through doc 24's `MeshCollision`, and the UV refusal on the form.

⚠ **Gated on T0's third question**, which is the one most likely to come back "not yet".

**Exit:** an image becomes a mesh with materials in the project; an existing mesh with UVs is
retextured from a prompt and drawn in Samples/13.

### T6 — The local provider · 1.5 EM

[D9](#d9-inference-is-an-editor-assembly-not-a-core-one--and-doc-38-shares-it).
`Vixen.Editor.Inference` and `…Generation.Onnx`: sessions, provider selection, the download manifest
with its hashes and its licence gate, and the chain expressed in C# around whatever graphs the chosen
model exports. Materials only — [B5](#b5-mesh-generation-cannot-be-embedded-at-all--for-the-local-provider-only).

**Exit:** a material generated with no Python installed, on CPU and on at least one accelerated
provider, matching the ComfyUI provider within a stated tolerance.

### Cost

| Phase | EM | Blocked on |
|---|---|---|
| T0 — The spike | 0.25 | Nothing |
| T1 — The deterministic kernel | 1.0 | Nothing. ⚠ **Worth building whatever T0 says** |
| T2 — The maps reach the renderer | 0.75 | Nothing, and owed by 06 regardless |
| — | **2.0** | **the first cut line — a complete texture toolkit, no AI in it** |
| T3 — The graph and the seam | 1.25 | T1, T2 |
| T4 — The panels and ComfyUI | 1.25 | T3 |
| — | **4.5** | **the second cut line — materials, end to end, one provider** |
| T5 — Meshes and retexturing | 1.5 | T4, and T0's third question |
| T6 — The local provider | 1.5 | T4 |
| | **7.5** | |

⚠ **The first cut line is drawn below the AI deliberately.** Everything above it ships whatever the
model landscape does next, and it is the part an artist uses every day.

⚠ **T5 and T6 are independent of each other and either may go first.** T6 is what removes the Python
dependency for the common case; T5 is what makes the feature about more than materials. Which matters
more is a question about who is using the editor, and it is not this document's to answer in advance.

---

## What this does not become

1. **A ComfyUI in the editor.** [D3](#d3-the-graph-is-of-intent-not-of-comfyui-nodes). A dozen nodes
   of intent, and the provider resolves them. A five-hundred-node library maintained by us is the
   failure mode this decision exists to prevent.
2. **A runtime feature.** No shipped game infers a material or a mesh. `Vixen.Editor.*` throughout,
   and [D9](#d9-inference-is-an-editor-assembly-not-a-core-one--and-doc-38-shares-it) is where the
   line is drawn.
3. **A dependency of `Vixen.Rendering`.** T2 adds sampling features because materials should have had
   them; it does not make the renderer aware that a generator exists.
4. **A bundled Python, a bundled ComfyUI, or an installer for either.**
   [B3](#b3-comfyui-is-gpl-30--manageable-and-it-constrains-the-shape).
5. **A mirror of anybody's weights.** The button fetches from the source, on the author's machine,
   under the author's acceptance. [D8](#d8-the-download-button-states-the-terms-and-then-gets-out-of-the-way).
6. **A modelling package.** No sculpting. ⚠️ **Retopology and the unwrapper are no longer on this
   list** — [41](41-automatic-retopology.md) took them, on the argument that they are deterministic
   arithmetic rather than inference and so belong in `Core/` rather than behind a provider.
   [D6](#d6-retexturing-is-where-vixen-has-an-unusual-advantage) records the amendment.

---

## See also

- [39 — The Standard Frame, and render presets](39-standard-frame-and-render-presets.md) — the
  knobs-over-a-graph layering and the explode escape hatch this document's panels take wholesale.
- [38 — Learned terrain generation](38-learned-terrain-generation.md) — the download, hashing and
  cache rules, the ONNX Runtime tier table, and the "second consumer" clause this document answers.
- [36 — An extensible editor](36-an-extensible-editor.md) — the contribution surface, and the
  `AddSettingsPage` gap that is why the panels are documents rather than settings pages.
- [23 — Bindless materials](23-bindless-materials.md) — the table slot a fifth, sixth and seventh
  sampling feature take.
- [`Vixen.Editor.NodeGraph`](../../Editor/Vixen.Editor.NodeGraph/README.md) — the framework the
  generation graph is, and the four rules [D5](#d5-the-graph-is-nodegraphmodels-and-unlike-a-behaviour-tree-it-fits)
  checks against.
- [`Vixen.Editor.Ai`](../../Editor/Vixen.Editor.Ai/README.md) — the same four rules failed by a
  behaviour tree, which is the comparison that makes the fit here worth stating.
- Ying, Z., Rong, B., Wang, J., Xu, M. *Chord: Chain of Rendering Decomposition for PBR Material
  Estimation from Generated Texture Images.* SIGGRAPH Asia 2025, Article 164.
  [doi:10.1145/3757377.3763848](https://doi.org/10.1145/3757377.3763848) ·
  [arXiv:2509.09952](https://arxiv.org/abs/2509.09952)
- Xiang, J. et al. *Structured 3D Latents for Scalable and Versatile 3D Generation* (TRELLIS).
  CVPR 2025. [github.com/microsoft/TRELLIS](https://github.com/microsoft/TRELLIS) ·
  [TRELLIS.2](https://github.com/microsoft/TRELLIS.2)
