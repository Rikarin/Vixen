<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 38 — Learned terrain generation

⚠️ **Extends [31](31-terrain-grass-and-trees.md), and is [36](36-an-extensible-editor.md)'s most
demanding client.** Doc 31 built the heightfield: edit layers, eight
sculpt tools, painted weights, a quadtree renderer, and 16-bit heightmap import that resamples onto
whatever tile shape a terrain declares. Every phase of it is built. What none of it answers is where
the *first* heightfield comes from — today that is a flat grid, a noise stamp, or a file somebody made
in World Machine.

[Terrain Diffusion](https://github.com/xandergos/terrain-diffusion) (Goslin, SIGGRAPH '26) is a
candidate answer, and this document is the assessment plus the plan if the assessment holds.

**The claim this document has to earn.** An environment artist creating a 32 km terrain gets a
plausible continent — mountain ranges that drain somewhere, coastlines that are not noise thresholded
at sea level — in under two minutes, on a machine with no CUDA installed, and then sculpts on top of
it with the tools doc 31 already built as though they had drawn it themselves. Nothing in
`Vixen.Terrain` learns what a mountain is, nothing in the editor's download grows by two gigabytes,
and a project that never opens the panel never knows it exists.

⚠ **This is the document most likely to end at [T0](#t0--the-spike--025-em).** The first phase is a
measurement whose plausible outcome is "not worth it", and it is written to be cheap enough that the
answer is worth buying either way.

---

## The row this reopens

[31 § Where the line goes](31-terrain-grass-and-trees.md#where-the-line-goes) lists, in the Out
column:

> | Sculpt · smooth · flatten · ramp · erosion · hydro · noise · holes | **A node-based procedural terrain generator** |

with the test that decides it:

> ⚠ **does an environment artist reach for it between two lighting builds?** Erosion is on the left
> because a ridge that has not been eroded reads as a cone; a biome graph is on the right because it
> is a content-generation product, not a tool, and every engine that has shipped one has shipped it
> as a plugin.

**That row is not overturned. It is taken literally.** Terrain Diffusion is exactly the class of
thing doc 31 pushed out, and doc 31 also named the correct destination for it in the same sentence:
*a plugin*. What has changed since is not the judgement but the substrate — `Vixen.Editor.Plugin`
now exists, ships a collectible load context, and its README already states that a plugin's own
`runtimes/` folder is its own business. The extension point doc 31 gestured at is built.

So this document adds nothing to the left-hand column. It builds the right-hand one for the first
time, and terrain generation is its first inhabitant.

---

## What the reference actually ships

Surveyed rather than remembered, at the versions live on 2026-08-04.

### The algorithm

**InfiniteDiffusion** is a training-free reformulation of diffusion sampling — a drop-in replacement
for MultiDiffusion — that makes sampling lazy and unbounded. From the paper's abstract, the three
properties it recovers are the ones that made noise indispensable: *"seamless infinite extent,
seed-consistency, and constant-time random access."* It is built on
[infinite-tensor](https://github.com/xandergos/infinite-tensor), a framework for constant-memory
manipulation of unbounded tensors.

⚠ **The contribution is the sampler, not a noise function.** "Procedural" here describes the
*interface* — seed in, coordinates in, deterministic terrain out, no bounded canvas. It does not
describe the cost. Behind that interface is a three-model stack and 2.3 GB of weights, and every
design decision below follows from that gap.

### The models

| | Resolution | Coarse cell | Stated use |
|---|---|---|---|
| `terrain-diffusion-30m` | 30 m/px | 7.7 km | Playable worlds — finer local control |
| `terrain-diffusion-90m` | 90 m/px | 23 km | Large-scale worldbuilding. *"Often too expansive"* |

Two stages. A coarse map lays out the world — generated procedurally from Perlin noise shaped to
match real-world statistics, or hand-drawn, or imported from an
[Azgaar](https://azgaar.github.io/Fantasy-Map-Generator/) fantasy-map export. The base and decoder
models then refine that sketch into a heightmap, with a per-input SNR controlling how much the sketch
binds. Trained on ETOPO 30-arc-second global relief and WorldClim bioclimatic variables.

**The output is elevation plus four climate channels**, and the API serialises them exactly as a
heightmap importer would want:

| Channel | Type | Meaning |
|---|---|---|
| Elevation | `int16` LE | Metres, floored |
| `temp` | `float32` LE | Annual mean temperature °C (WorldClim BIO1) |
| `t_season` | `float32` LE | Temperature seasonality (BIO4) |
| `precip` | `float32` LE | Annual precipitation mm/yr (BIO12) |
| `p_cv` | `float32` LE | Precipitation seasonality, CV % (BIO15) |

⚠ **Elevation is signed metres, so the model has an opinion about sea level and
[35](35-water.md) is the thing that has to agree with it.** The pipeline carries a `drop_water_pct`
and its coarse map decides where ocean is; doc 35's oceans are splines carrying a profile. A
generated terrain that arrives with a coastline and no water in it is half a result, so the bake
reports the sea level it generated against and the panel says so. Placing the water body is doc 35's
job and is out of scope here — **agreeing about the number is not**.

### The part that decides feasibility

The reference integration is a [Minecraft Fabric mod](https://github.com/xandergos/terrain-diffusion-mc),
and it does not embed Python. It runs
[`xandergos/terrain-diffusion-30m-onnx`](https://huggingface.co/xandergos/terrain-diffusion-30m-onnx)
— three ONNX graphs plus two JSON configs, MIT — through **ONNX Runtime 1.20**:

| Artifact | Size |
|---|---|
| `base_model.onnx` | 2030 MB |
| `decoder_model.onnx` | 224 MB |
| `coarse_model.onnx` | 22.5 MB |
| `world_pipeline_config.json`, `pipeline_data.json` | < 1 KB |

Stated requirements: **1.5 GB VRAM, 2.5 GB RAM**, models downloaded on first launch (~2.5 GB) into
`.minecraft/terrain-diffusion-models` with SHA-256 validation against a manifest pinned at build
time.

⚠ **This is the whole reason the document exists.** A PyTorch dependency would have ended the
assessment at the first paragraph — Vixen is a .NET engine and [ADR-002](01-technology-decisions.md#adr-002--all-metaprogramming-is-roslyn-source-generators-il-post-processing-is-banned)'s
neighbours are not the kind of constraint one embeds a Python runtime beside. A published ONNX export
that somebody has already proved runs a real-time workload from a managed host is a different
proposition entirely.

**What the mod had to write itself**, because the ONNX graphs are the models and not the pipeline —
roughly 127 KB of Java, excluding a vendored `FastNoiseLite` (112 KB, which has an official C#
single-file port) and the web explorer (65 KB, which we do not want):

| | |
|---|---|
| `infinitetensor/` — `InfiniteTensor`, `TensorWindow`, `MemoryTileStore`, `FloatTensor` | ~24 KB |
| `WorldPipeline` + `WorldPipelineModelConfig` | ~36 KB |
| `LocalTerrainProvider` | ~17 KB |
| `OnnxModel` — session lifetime, provider selection, offload | ~19 KB |
| `LaplacianUtils` — the compact encoding that survives Earth-scale dynamic range | ~9 KB |
| `SyntheticMapFactory` — the coarse Perlin map | ~10 KB |
| `EDMScheduler` | ~7 KB |
| `PortableRng`, `GaussianNoisePatch` | ~6 KB |

That is the port. It is a known quantity because somebody has already done it once in a language with
similar constraints, which is the best evidence a cost estimate ever gets.

---

## Where Vixen already is

Six seams, every one of them built for another reason.

| | Where | What it means here |
|---|---|---|
| **Heightmap ingestion** | `TerrainHeightmap.Import` | Bilinear resample onto any tile shape, corners pinned. Doc 31's own reasoning: external tools emit 512 / 1024 / 2049, a terrain of four 128-sample tiles is 509 across, *"they will essentially never match"* |
| **Non-destructive by default** | `TerrainHeightmap.Import` writes an **edit layer** | A generated terrain is sculptable on top of and re-generatable without losing the sculpt. [31 § D4](31-terrain-grass-and-trees.md#d4-edit-layers-are-the-storage-model-not-a-feature-on-top-of-it) already lists *"the create dialog, or a heightmap import"* as what writes the base |
| **Climate has somewhere to go** | `TerrainWeights`, `TerrainWeightmap` | Per-layer 8-bit masks in and out, through the sum-to-one invariant. The mod's biome classifier is 250 lines of rules over the same four numbers |
| **The plugin contract** | `Vixen.Editor.Plugin` | `plugin.yaml`, collectible ALC, `AddPanel` / `AddCommand` / `AddMode`, complete rollback on a failed `Activate`, and *"a plugin's own `lib/`, `runtimes/` and content are its business"* |
| **Background work** | `context.Shell.Tasks` | The manager the importer and the content build already use. A two-minute bake has a home that is not the frame thread |
| **Native payloads are not novel** | Jolt, Silk.NET, HarfBuzz, `Nuke.RestoreNativeDeps` | Shipping a native runtime per RID is a solved problem in this repository, including the attribution manifest it generates |

⚠ **Nothing in `Vixen.Terrain` changes.** The kernel has one project reference and it is to the
mathematics; a generator that made it name an inference runtime would invert
[31 § D1](31-terrain-grass-and-trees.md#d1-two-runtime-assemblies-and-one-editor-assembly-and-the-kernel-touches-no-device)
exactly. The plugin's entire write path is `TerrainHeightmap.Import` and `TerrainWeightmap.Import`,
both of which are public, both of which take arrays.

---

## What blocks it

Four, and only the first two are interesting.

### B1. The scale mismatch decides whether the feature is worth building 🟡

The finest model is 30 m/pixel. A 512 × 512 tile is **15.4 km across**. Vixen's terrains are one
16-bit `TerrainSamples` grid, which bounds the top end generously — 4096² samples is 33.5 MB of
heights, or 123 km at 30 m per quad — but says nothing about the bottom.

| Terrain | At 30 m/quad | Model pixels across | Verdict |
|---|---|---|---|
| 508 m (four 128-sample tiles at 1 m/quad) | — | **~17** | Worthless. Noise + erode wins on every axis |
| 2 km | 67 samples | ~67 | Marginal |
| 8 km | 267 samples | ~267 | The floor where it starts to pay |
| 32 km | 1067 samples · 2.3 MB | ~1067 | The target |
| 123 km | 4096 samples · 33.5 MB | ~4096 | The bound `TerrainSamples` imposes |

⚠ **Below about 8 km this is strictly worse than what doc 31 already built.** Squash a 15 km tile
onto a 1 km level and every ridge is a smooth blob, while `TerrainSculpt.Noise` plus `Erode` plus
`Hydro` give an artist detail at the scale they are actually working. The feature is not "generate a
terrain"; it is "generate a *large* terrain", and the panel has to say so rather than let somebody
discover it.

**What the model contributes that no kernel can**: large-scale coherence. Where a mountain range goes,
which way a valley system drains, whether a coastline reads as a coastline. An erosion kernel refines
a shape; it cannot invent where the shape belongs. That is the entire value proposition and it only
exists at range.

### B2. Latency on CPU is unmeasured, and it is the phase gate 🟡

The mod's README calls CPU inference *"very slow"*, but that judgement is about streaming tiles to a
player flying through a world — a tile every few hundred milliseconds, forever. An editor bake of a
32 km terrain is a handful of tile evaluations, once, behind a progress bar. Thirty to ninety seconds
is unremarkable there and disqualifying in Minecraft. **The two requirements differ by orders of
magnitude and the mod's verdict does not transfer.**

The repository ships the harness: `terrain_diffusion/evaluation/latency.py` measures TTFT (time to
first tile) and TTST (adjacent tile thereafter) and takes `device='cpu'`. [T0](#t0--the-spike--025-em)
runs it, and its number decides whether [D3](#d3-cpu-and-directml-never-cuda)'s CPU tier is the
default or a fallback nobody should choose.

### B3. Bit-exact determinism does not survive a change of provider ✅ *designed around*

ONNX float output differs between the DirectML, CUDA, CoreML and CPU execution providers. So "same
seed, same terrain" holds on one machine and does not hold across a team.

This is fatal for one design and irrelevant to the one [D2](#d2-the-output-is-an-import-not-a-layer-that-regenerates)
picks. A reserved layer in doc 31's sense — Splines, Scatter — is *regenerated wholesale* whenever its
inputs change, so it must produce the same deltas twice or an artist's terrain shifts under them when
they open the project on a different machine. A bake writes samples once and those samples are then
the asset. **Bake once, and the problem does not arise.**

### B4. Provenance of the training data needs one answer before shipping weights ⛔

The code and the ONNX artifacts are MIT — compatible with
[ADR-015](01-technology-decisions.md#adr-015--vixen-is-apache-20----decided) and one row in the
attribution manifest. ETOPO is US-government work and public domain. **WorldClim is the open
question**: the coarse model trains on its bioclimatic variables, and neither `worldclim.org` nor its
2.1 data page states licence terms that a commercial redistributor can rely on. The MIT grant on the
model repository is the author's grant over the author's work; it is not by itself an answer about the
training corpus.

⚠ **This is a blocking question, not a caveat, and it is asked of the model author rather than
resolved by reading.** It costs one email and it is [T0](#t0--the-spike--025-em)'s second deliverable.
See [D8](#d8-licensing-and-what-t0-has-to-settle).

---

## Part 1 — The design

### D1. It is a plugin, and doc 31 already said which one

`Plugins/terrain-diffusion/` — a `plugin.yaml`, one assembly, and its own `runtimes/`. Not an
assembly under `Editor/`, not a row in `Directory.Packages.props`, not a reference from
`Vixen.Editor.Terrain`.

Three reasons, in descending order of how much they would hurt to get wrong:

1. **[31 § Where the line goes](31-terrain-grass-and-trees.md#where-the-line-goes) put it there.**
   Reopening a line one document after drawing it, for the first thing that comes along, is how a
   scope boundary stops meaning anything.
2. **The editor's download must not grow.** ONNX Runtime is 15–38 MB of native per RID before a byte
   of weights. A project that never generates terrain should not carry it, and the plugin loader is
   the mechanism that makes "should not" into "does not".
3. **It is the reach [36](36-an-extensible-editor.md) says nobody has tested.** Doc 36's P1 was
   *"recorded as done"* on exit criteria that *"test the mechanism"* rather than its reach, and its
   Part 6 is the list of what a plugin still cannot do. This plugin is a hard case against that list
   on four axes at once — **a native payload per RID, a 2.3 GB downloaded cache, a two-minute
   background task, and a panel that writes through another feature's kernel** — and none of those
   four is exercised by an in-tree feature that was wired in through the back door.

⚠️ **What doc 36 already settled, and what it has not.** Since doc 36, importers *are* contributable
(`ImporterContributions` through `PluginServices`), tools and overlays and gizmos have contribution
records, and terrain is one of the four `IEditProvider`s — so **undo and dirty already work** for
what this writes. What 36 lists as owed and this plugin would like: `AddSettingsPage` is unbuilt,
which is one more reason [D5](#d5-the-download-button-is-the-panels-empty-state-not-a-settings-row)
puts the download control on the panel; and a contributed importer *"does not reach an out-of-process
compiler worker"*, which does not bite here because [D2](#d2-the-output-is-an-import-not-a-layer-that-regenerates)
registers no importer at all.

⚠ **`api: 0.1`, and the plugin is expected to break.** The contract's minor is the breaking number
while the major is `0`. A first-party plugin pinned to a moving contract is a maintenance cost that
should be visible, and hiding it inside the repository's own build would hide exactly the friction
third parties are going to feel.

### D2. The output is an import, not a layer that regenerates

The bake produces a `ushort[]` and hands it to `TerrainHeightmap.Import`. That is the whole write
path.

| Not this | Because |
|---|---|
| A fifth `TerrainLayerKind`, regenerated like Splines | [B3](#b3-bit-exact-determinism-does-not-survive-a-change-of-provider--designed-around) — regeneration on another machine writes different deltas |
| A runtime generator streaming tiles as the camera moves | Doc 31's terrain is bounded and in-memory by design; and [31 § B6](31-terrain-grass-and-trees.md#b6-there-is-no-world-streaming-) is a different document's problem |
| A contributed `IAssetImporter` | [36 § F8](36-an-extensible-editor.md#d4--the-extension-surface-completed) made importers contributable, so this is now *possible* and still wrong: **an importer imports a file, and there is no file.** The input is a seed and a coordinate |

⚠ **Import's default target is an edit layer and the panel keeps that default.** Generate into a
layer, sculpt underneath it, hide it to compare, regenerate with a different seed without losing an
hour of work. Writing the base is offered — it is what the create dialog does — and it is the
destructive option, so it is the one that is not preselected.

### D3. CPU and DirectML, never CUDA

`Microsoft.ML.OnnxRuntime` 1.28.0 is one package covering every RID. Verified against the package
rather than its description:

| RID | Native payload |
|---|---|
| `win-x64` | 15.1 MB |
| `linux-x64` | 23.1 MB |
| `osx-arm64` | **37.5 MB** |
| `win-arm64` | 15.2 MB |
| `linux-arm64` | 19.6 MB |

⚠ **The macOS build is 60 % larger than Linux's because CoreML is compiled into it.**
`_OrtSessionOptionsAppendExecutionProvider_CoreML` is an exported symbol in
`runtimes/osx-arm64/native/libonnxruntime.dylib`. This is why the mod's *CPU* build says it
"automatically uses CoreML on Apple Silicon" — and it means **the base package is not CPU-only on the
platform this engine is developed on.** Apple Silicon gets Neural Engine and GPU dispatch with no
extra dependency and no user setup.

Note also that ORT 1.28 no longer ships `osx-x64`. Intel Macs get no ONNX Runtime at all, which is a
row in the plugin's own requirements rather than something to work around.

The tier table:

| Tier | Cost | Covers | Take it? |
|---|---|---|---|
| `Microsoft.ML.OnnxRuntime` | 15–38 MB per RID | CPU everywhere; **CoreML free on Apple Silicon** | ✅ base |
| `…OnnxRuntime.DirectML` | **+11 MB** | Any modern GPU on Windows, any vendor, zero user setup | ✅ |
| `…OnnxRuntime.Gpu` | hundreds of MB–GB | NVIDIA only, and requires a user-side CUDA + cuDNN install | ❌ |

⚠ **DirectML is eleven megabytes and covers the majority of Windows workstations regardless of GPU
vendor.** Dropping it saves nothing worth having. CUDA is the one to refuse: the reference mod ships
a separate build and a dedicated `CUDA_INSTALL.md` for it, and its two most-documented failure modes
are both broken CUDA installations. **"No CUDA" is the decision; "CPU-only" is a description of one
tier, and on macOS it is not even accurate.**

⚠ **The RID list is trimmed to desktop.** The 132 MB package figure is dominated by `android`
(43.5 MB) and `ios` (53 MB), neither of which an editor plugin has any use for.

⚠ **Do not assume an fp16 export halves the download.** ORT's CPU provider inserts cast nodes around
fp16 operators on hardware with no native fp16 compute path, and the result is frequently slower than
fp32. For a CPU-first plugin fp32 is the likely right answer and the download stays ~2.3 GB. If T0
finds otherwise, it is a T0 finding.

### D4. The weights are downloaded, hashed, and cached outside the project

Three rules, each fixing a specific way this goes wrong:

1. **A pinned manifest and SHA-256 per file.** The plugin ships the expected digests; a file that
   does not match is not loaded. This is 2.3 GB fetched over the network and handed to a native
   runtime — it is a trust boundary, and the reference mod validates the same way for the same reason.
2. **The cache is per-user, never per-project.** `<user data>/terrain-diffusion-models/`, beside
   where the editor already keeps a layout and a keymap. Weights inside a project directory means
   somebody commits 2.3 GB, once, and then everybody clones it forever.
3. **Nothing is fetched until the panel is opened and the button is pressed.** Installing the plugin
   costs the assembly and the runtime. It does not cost the models.

### D5. The download button is the panel's empty state, not a settings row

⚠ **Settings hides the cost from the person incurring it.** The generate panel refuses to generate
until the models are present, and the thing it shows instead *is* the download control, reading
`Download models (2.3 GB)`.

This is [`TerrainFacts`](../../Editor/Vixen.Editor.Terrain/TerrainCreateSettings.cs)'s convention one
layer out. Doc 31 put a derived-cost readout on the create form because *"this is the dialog where a
person accidentally asks for eight gigabytes"*. A 2.3 GB download is the same problem with the same
answer: show the number on the surface where the choice is made, at the moment it is made.

Same convention for the terrain itself. The panel carries a `(derived)` row reading the model
resolution against the terrain's `MetresPerQuad` — *"30 m/px over 508 m: 17 model pixels (derived)"* —
and refuses below the [B1](#b1-the-scale-mismatch-decides-whether-the-feature-is-worth-building-) floor
with a message naming the extent that would work. A feature that is useless below 8 km must say so on
the form, not in a manual.

### D6. The bake is a background task, and cancellable

`context.Shell.Tasks`, which is what the importer and the content build already use, with the
interface touched only from the continuation the manager pumps —
`Vixen.Editor.Plugin`'s README is explicit that everything on `PluginContext` is frame-thread and that
real work goes to `Tasks`.

Progress is per tile, because that is the unit the pipeline actually completes and a fake percentage
over an unknown duration is worse than a count. Cancel is real: an artist who picked the wrong seed
should not wait ninety seconds to pick another.

### D7. Climate becomes weights, and it is the more interesting half

The model emits four climate channels per sample and Vixen has `TerrainWeights` with a sum-to-one
invariant and per-layer 8-bit masks. The mapping between them is a rules table — the reference mod's
entire biome classifier is 250 lines over the same four numbers.

So: a terrain generated with a snow layer, a rock layer, a grass layer and a sand layer already
painted, from temperature, precipitation and slope. That is the output no procedural noise setup gives
you, it uses machinery doc 31 already shipped, and it is where this stops being "a fancier noise
button".

⚠ **It is [T3](#t3--climate--weights--10-em) and not T2, because it is worthless if T1's heights are
not good.** The ordering is not a preference.

⚠ **The rules table is authored data, not code.** Which layer index means snow is a property of the
project's layer stack, and a classifier that hard-coded it would work exactly once.

### D8. Licensing, and what T0 has to settle

| | Licence | Obligation |
|---|---|---|
| `terrain-diffusion` (code, algorithms) | MIT | NOTICE row; §4b modification notice on any ported file |
| `terrain-diffusion-30m-onnx` (weights) | MIT | NOTICE row |
| ONNX Runtime | MIT | NOTICE row; native attribution through `RestoreNativeDeps` |
| FastNoiseLite (C# port) | MIT | NOTICE row, if the coarse map needs it |
| ETOPO (training input) | US-government, public domain | ✓ |
| **WorldClim 2.1 (training input)** | **⛔ unresolved** | See below |

⚠ **A ported file carries a modification notice, and the port is substantial.** ADR-015's table
already has a row for this — *"required where we port third-party algorithms"* — and this is a larger
port than any existing entry. The EDM scheduler, the Laplacian encoding and the InfiniteDiffusion
sampler are the paper's contributions being re-expressed in C#, and the attribution needs to say so
per file, plus the citation the paper asks for.

**The WorldClim question, asked precisely**, because "is it MIT" is not the question: the model
repository's MIT grant covers what its author authored. Whether weights derived from WorldClim
bioclimatic variables can be redistributed by a commercial engine is a question about WorldClim's
terms, and those terms are not stated on the pages that serve the data. Two acceptable outcomes —
a clear answer from the author or from WorldClim, or a coarse model retrained on ETOPO alone (the
README notes the coarse model is *"tiny"* and gives the retraining recipe, and elevation-only loses
climate, which loses [D7](#d7-climate-becomes-weights-and-it-is-the-more-interesting-half)). One
unacceptable outcome: shipping and finding out.

---

## Part 2 — The authoring surface

One panel, registered by the plugin, in the terrain category beside the ones
[31 § The terrain panel](31-terrain-grass-and-trees.md#the-terrain-panel) already describes. Every row
an `[Inspector]` member of a `[DataContract]` settings type, as doc 31 § Part 2 requires and for the
same reason — the panel is testable with no window.

| Row | |
|---|---|
| **Models** | Present, or the download control and nothing else ([D5](#d5-the-download-button-is-the-panels-empty-state-not-a-settings-row)) |
| **Device** | Auto · CPU · GPU. Auto reports what it picked, because "why is this slow" has one answer and it should be on screen |
| Model | 30 m · 90 m |
| Seed | With a re-roll, and it is the field an artist actually iterates on |
| World position | Where on the planet. The reason two terrains from one seed can be adjacent |
| Coarse influence | The pipeline's per-input SNR, as one artist-facing number |
| Target | **Edit layer** (default) · Base |
| Extent · resolution · model pixels | `(derived)`, with the [B1](#b1-the-scale-mismatch-decides-whether-the-feature-is-worth-building-) refusal attached |
| Preview | The coarse map, which is cheap — `coarse_model.onnx` is 22.5 MB against the base model's 2030 |
| Generate | Background, per-tile progress, cancellable |

⚠ **The coarse preview is what makes seed iteration bearable.** The coarse model is one nine-hundredth
the size of the base model and answers the only question an artist asks between rolls — *is there
land here, and does it look interesting* — so the expensive stage runs when somebody has already
decided.

⚠ **No mode, and no viewport tool.** This is a panel with a button. It does not own the viewport, it
has no brush, and it takes no key. Adding a mode would put it on the mode bar beside Sculpt and Paint
and imply it is something you *do* rather than something you *run*.

---

## Part 3 — Phases

### T0 — The spike · 0.25 EM

**No engine code.** Run the reference implementation and answer three questions.

1. **Is the output better than `Noise` + `Erode` + `Hydro` at the extents we build?** Export a
   heightmap with the repository's own `tiff-export`, convert to `r16`, import it through
   `TerrainHeightmap.Import` — which exists and needs nothing — and look at it beside a noise terrain
   at 2 km, 8 km and 32 km.
2. **What is CPU latency on our target machines?** `evaluation/latency.py --device cpu`, which reports
   TTFT and TTST. On an Apple Silicon machine, and on a Windows box with and without DirectML.
3. **What are WorldClim's terms?** [D8](#d8-licensing-and-what-t0-has-to-settle). One email.

**Exit:** a `RESULT.md` under [`spikes/`](spikes/) in the shape the five existing ones use, ending in
a go or a stop. ⚠ **Stop is a real outcome and the most likely single one.** If (1) is unconvincing at
8 km, nothing below is worth 4.5 EM. If (3) comes back wrong, nothing below is shippable at any price.

### T1 — The pipeline in C# · 2.5 EM

The port. `Vixen.Plugins.TerrainDiffusion`, targeting `api: 0.1`, referencing
`Microsoft.ML.OnnxRuntime` and `…DirectML`, with a trimmed desktop RID list.

The infinite-tensor window and tile store; the EDM scheduler; the Laplacian encode/decode; the
synthetic coarse map on FastNoiseLite's C# port; the portable RNG, which is what makes a seed mean the
same thing here as in the reference; ONNX session lifetime and provider selection with the offload
behaviour the mod's config exposes; and the model download with its pinned manifest and SHA-256
([D4](#d4-the-weights-are-downloaded-hashed-and-cached-outside-the-project)).

⚠ **The acceptance test is a differential one against the reference implementation, not a golden
image.** Same seed, same coordinates, same model, CPU provider on both sides, heights within a
tolerance the spike measured. This is [18](18-raven-parser-migration.md)'s differential-oracle pattern
and it is the only way to find out that the Laplacian decode is subtly wrong — which will otherwise
present as "the terrain looks a bit flat", six weeks later, with nothing to compare against.

**Exit:** a console harness generating a heightmap from a seed and a coordinate, matching the
reference within tolerance, on CPU and on at least one accelerated provider.

### T2 — The panel · 1.0 EM

[Part 2](#part-2--the-authoring-surface). The settings type, the derived rows and the extent refusal,
the coarse preview, the download empty state, the background bake, and the write through
`TerrainHeightmap.Import`.

**Exit:** a 32 km terrain generated from the panel in the editor, sculpted on top of with doc 31's
tools, the generated layer hidden and shown, and regenerated with a second seed without losing the
sculpt.

### T3 — Climate → weights · 1.0 EM

[D7](#d7-climate-becomes-weights-and-it-is-the-more-interesting-half). The four channels, an authored
rules table against the project's own layer stack, slope as a fifth input, and the write through
`TerrainWeightmap.Import` per layer — through the sum-to-one invariant, which means the checker that
names the offender is doing real work for the first time on data nobody painted.

**Exit:** the T2 terrain arriving with snow above the treeline, rock on the steep faces, grass in the
temperate basins and sand where the model says it is dry — and the weight sum asserted across every
sample.

### Cost

| Phase | EM | Blocked on |
|---|---|---|
| T0 — The spike | 0.25 | Nothing. ⚠ **Its outcome may be that the rest is not built** |
| T1 — The pipeline in C# | 2.5 | T0 |
| — | **2.75** | **the cut line — heights only, and it is a complete feature** |
| T2 — The panel | 1.0 | T1 |
| T3 — Climate → weights | 1.0 | T2 |
| | **4.75** | |

The cut line is after T1 plus T2, not after T1: a pipeline with no panel is a console harness. What
T3 buys is the half that is not available from any other tool, so it is the phase to protect rather
than the phase to drop — but it is also the one that survives being late.

---

## What this does not become

Four, written down because each is a plausible next request and each would undo a decision above.

1. **A runtime world generator.** Doc 31's terrain is one bounded 16-bit grid, in memory, by design.
   Streaming generated terrain as a player moves is [31 § B6](31-terrain-grass-and-trees.md#b6-there-is-no-world-streaming-)'s
   problem plus an inference runtime in a shipped game, and the second of those is a different engine.
2. **A dependency of `Vixen.Terrain`.** [D1](#d1-it-is-a-plugin-and-doc-31-already-said-which-one).
   The kernel has one project reference. A generator is not the thing that gets it a second.
3. **A replacement for the noise tool.** Below [B1](#b1-the-scale-mismatch-decides-whether-the-feature-is-worth-building-)'s
   floor, `TerrainSculpt.Noise` is better, and it is instant, and it is 200 lines rather than 2.3 GB.
4. **The engine's general ML seam.** ONNX Runtime enters the repository here as *a plugin's private
   dependency*, and it is not an argument for an inference abstraction in `Core`. The second consumer
   is what would make that a design question; the first one is not.

---

## See also

- [31 — Terrain, grass and trees](31-terrain-grass-and-trees.md) — the toolset this generates into,
  and the Out row this stays on the correct side of.
- [36 — An extensible editor](36-an-extensible-editor.md) — what a plugin can and cannot reach, and
  the Part 6 list this is a hard case against.
- [35 — Water](35-water.md) — who owns sea level once a generated coastline exists.
- [`Vixen.Editor.Plugin`](../../Editor/Vixen.Editor.Plugin/README.md) — the contract, the four rules
  that make unloading work, and the statement that a plugin's `runtimes/` is its own business.
- [`Vixen.Terrain`](../../Core/Vixen.Terrain/README.md) — `TerrainHeightmap.Import`, the resampling
  argument, and why the kernel names no device.
- [01 § ADR-015](01-technology-decisions.md#adr-015--vixen-is-apache-20----decided) — the licence
  obligations a new dependency and a ported algorithm each create.
- Goslin, A. *InfiniteDiffusion: Bridging Learned Fidelity and Procedural Utility for Open-World
  Terrain Generation.* SIGGRAPH Conference Papers '26. [doi:10.1145/3799902.3811080](https://doi.org/10.1145/3799902.3811080)
  · [arXiv:2512.08309](https://arxiv.org/abs/2512.08309)
