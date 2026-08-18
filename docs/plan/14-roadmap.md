# 14 — Roadmap

Phases, exit criteria, sequencing, effort.

**What this document is for.** Each phase says what it set out to do, whether its exit criteria are
met, and what it left behind. It is deliberately *not* a build log: the reasoning behind a subsystem
lives in that subsystem's `README.md`, and the current status of every feature lives in
[`../overview.md`](../overview.md). Three places recording the same thing is how they come to
disagree, so this one keeps the phase boundaries and the gates, and points at the other two.

---

## Sizing, honestly

Effort is in **engineer-months (EM)** — one experienced .NET/graphics engineer, full time. These are
estimates for *this* scope, benchmarked against what Stride and comparable engines took, not against
optimism.

| Phase | Deliverable | EM | State |
|---|---|---|---|
| 0 | Monorepo, build system, foundations | 2.0 | ✅ |
| 1 | Core runtime + RHI + first triangle | 4.5 | ✅ |
| 2 | ECS + engine loop + scenes | 3.0 | ✅ |
| 3 | Asset pipeline + mobile bring-up | 4.0 | ✅ bar CI legs and physical devices |
| 4 | UI framework | 7.0 | ✅ |
| 5 | Renderer (forward+, PBR, shadows, post FX) | 4.5 | 🟡 post FX is most of the way (SMAA, MSAA resolve, full GTAO and SSR are the gaps); D3D12 postponed |
| 5b | Raven parser migration (ANTLR → hand-written) | 1.5 | ✅ |
| 6 | Editor shell | 4.5 | 🟡 the exit sentence and the tooling are met; packaging/signing and the perf bar are not |
| 7 | Node graphs + VFX | 3.5 | 🟡 graphs done; the VFX GPU path is too — what is left is the shader-graph preview, and dispatching the GPU sort |
| 8 | Gameplay subsystems (physics, audio, animation, input) | 3.5 | ✅ bar `Samples/05` |
| 9 | Networking and multiplayer | 5.0 | ✅ all five exit criteria met |
| 10 | Deferred, advanced rendering, Web | 2.5 | 🟡 WebGPU, video and XR landed early; deferred did not |
| 11 | Polish, docs, 1.0 | 2.5 | ⬜ |
| | **Original total** | **≈ 48.0** | |

**Raven's remainder is spent.** This table used to carry "plus ~6–9 EM of remaining Raven work
(semantic → IR → GLSL+SPIR-V → CLI)" as an item ahead of Phase 1. All of it is built, and the parser
migration added afterwards (5b) is built too — see [07](07-raven-shader-pipeline.md), the plan of
record for the compiler, which lists the four things still open in it.

**What has been added since, and where its budget lives.** Documents 19 and above are amendments with
their own phased estimates. They are not restated here, because a second copy of a number is a number
that drifts.

| Doc | Adds | Budget lives in |
|---|---|---|
| [19](19-lighting-and-global-illumination.md) | Lumen-shaped dynamic GI. **Retires baked lightmaps and tetrahedral probes**, which is where most of the saving is | § L1–L6. L1+L2 (~4.5 EM) is Phase 10's; L3–L6 is a post-1.0 track |
| [20](20-editor-parity.md) | The editor's *surface* — every panel, menu line and verb a professional reaches for | § Part E. ~11 EM on top of Phase 6's remainder |
| [21](21-realtime-collaboration.md) | Multi-user editing | § Milestones. ~5.75 EM, post-1.0 except the first |
| [22](22-virtualized-geometry.md) | A Nanite-class geometry pipeline | § Phases |
| [23](23-bindless-materials.md) | One descriptor array per frame, so a draw is an index | Folded into Phase 5's remainder |
| [24](24-blockout-tools.md) | In-viewport grey-boxing | § Part 3. 11.0 EM total, of which P0–P4 is 7.0 and is where the value is |
| [27](27-mmo-framework.md) | An orchestrator, realms, and seamless transfer between them | § Cost. 16.0 EM across L0–L4, each shippable on its own. **All five have landed** — `Live/` as a top level, realms as processes with lifecycles, the megaserver's placement as a pure function, seamless transfer with its oracle, persistence, the gate, and the soak. What is left is editor-side: the `.vxplacement` asset. See [`../overview.md`](../overview.md) § 1.13 |
| [28](28-gameplay-framework.md) | The gameplay library set on top of it | § Cost. 25.5 EM across G0–G8, taken by genre rather than whole; its libraries live in `Gameplay/`. **G0–G8 have all landed** — the kernel, items and loot, combat and shooting, progression and quests, social, the economy, competing, the world, owning. What each contains is in doc 28's milestone sections and in [`../overview.md`](../overview.md) § 1.10. G0 is the one milestone that document says is not optional; G2–G8 are independent tracks a game takes by genre. `Samples/14-Mmo`, which doc 28 calls the exit criterion for the whole thing, has landed. Owed: movement's *transform* half, which waits on doc 16's parent-relative replication (Part 4 item 69) |

> **27 and 28 together are ≈ 41.5 EM — near enough this table's whole original total.** That is
> deliberate and it is stated in both documents rather than buried: an MMO framework is the size of
> the engine it runs on. Neither is on the 1.0 path, and both are ordered so that stopping after any
> milestone leaves something a real game ships on.

The phases are ordered so **every phase ends with something that runs**, and so the highest-risk items
are answered early rather than discovered late. Both originally-flagged risks are retired: the Web
graphics unknown by [an executed spike](spikes/web-webgl2/RESULT.md) before Phase 0, and iOS/AOT
correctness by front-loading it into Phase 3.

---

## Phase 0 — Foundations ✅ *(2.0 EM)*

**Goal:** a monorepo that builds, tests and packages nothing useful, correctly.

**Landed.** The monorepo with Raven absorbed and history preserved; `Directory.Build.props`,
`Directory.Packages.props`, `global.json`, `.editorconfig`, `Vixen.slnx`. Nuke with
`Clean Restore Compile Test Pack CheckFormat CheckArchitecture CheckApi Benchmark`, and `ci.yml` on
three desktop runners. `Vixen.Core.Syntax` extracted from Raven — the highest-leverage refactor
available, and the one VXML later cashed in. `Vixen.Core` (86 tests), `.Mathematics` (126, with
CsCheck properties for the algebraic laws), `.Collections` (34), `.Memory` (19), `.Diagnostics` (18),
and `Benchmarks/Vixen.Benchmarks.Math`.

**Exit — met.** `nuke Test` green on all three desktops; Raven green on `Vixen.Core.Syntax`; math and
collections above 90 % with property tests.

**Owed.** Branch protection is a repository setting rather than a file, so it stays manual. Everything
else is in [`../overview.md`](../overview.md) § 1.1–1.2.

---

## Phase 1 — Core runtime and the first triangle ✅ *(4.5 EM)*

**Goal:** a window on three desktops with a Vulkan-cleared, triangle-drawing swapchain, and the
plumbing everything else stands on.

**Landed.** `Vixen.Core.Threading` (45 tests), `.IO` (123), `.Serialization` + generator (53),
`.Reflection` generator (16). `Vixen.Platform` contracts (26), `.Headless` (31), `.Desktop` over SDL 2
(55), `.Native` — whose `DllImportResolver` retired R11's desktop half with **no suppression taken**.
`Vixen.App` host and the build-variant matrix (36). The RHI surface (46), `Vixen.Graphics.Null` (29),
`Vixen.Graphics.Vulkan` (155, validation-clean), `Vixen.Graphics.RenderGraph` (34, with the property
tests doc 05 asks for). The `GoldenImages` target. `Samples/01-HelloTriangle`.

**Two things worth carrying forward.** There was **no Vulkan on the development machine** when this
started — no loader, no MoltenVK, no ICD — so the backend could not be written test-first locally, and
standing up **lavapipe in CI first rather than last** is what made it verifiable. A second driver
earned its keep immediately: it caught an instance asking for Vulkan 1.1 while using a 1.4 structure,
which MoltenVK accepted in silence. And `Samples/01` found two synchronisation bugs on its first real
present that the entire headless suite had passed straight through — a swapchain is not testable
without a window.

**Exit — met.** Triangle on macOS via MoltenVK; RHI green on Null; Vulkan validation-clean under
lavapipe in CI; zero-allocation gate green for an empty frame. **Windows and Linux presents are owed
with their CI legs.**

---

## Phase 2 — ECS, engine loop, scenes ✅ *(3.0 EM)*

**Goal:** entities with transforms and behaviours, rendering nothing but debug lines, at 10 k scale.

**Landed.** `Vixen.Ecs` — archetypes, chunks, edge graph, queries + generator, `CommandBuffer`, change
versions (90 tests) — and the nine-phase scheduler with its conflict graph on the job system.
`Vixen.Engine` — fixed-step loop, `Behavior`, scenes, additive load (58). Prefabs. Coroutines (25),
which allocate **zero** bytes per start against 160 for the same method written as a plain
`async ValueTask`. Ported Arch benchmarks, which changed code twice.

**Two decisions recorded rather than discovered.** The **dispatch generator turned out not to be
needed** — `BehaviorBucket<T>` is closed at the `Add<T>` call site, so its loop is already the
monomorphic walk a generated method would be. And the **ImGui debug overlay was cut, not deferred**:
building it meant standing up a second immediate-mode renderer, a font atlas and an input bridge in
order to throw all three away, so Phase 6's "delete the ImGui scaffold" step is struck with it.

**Exit — met.** 100 k entities at 70 ns to create and 0.50 ns each to iterate; a 10 k-entity hierarchy
over 10 000 frames with **zero Gen0 collections** at 514 µs mean; `Behavior` golden-ordering green;
determinism green — two worlds, one input log, 10 000 steps, compared by `WorldDigest` throughout.

**Owed.** The transform hierarchy is not depth-split (that needs shared components); ~~`DebugDraw`
accumulates and does not yet draw, which was blocked on a renderer and is not any more.~~ Built —
`Vixen.Engine.Renderer` draws both the world and the screen list, golden-image verified.

**World serialisation has since landed and is Phase 2's in spirit**, although it waited on the scene
format for the one thing this phase could not give it: a way to name a component. It lives in
`Vixen.Engine` rather than in `Vixen.Ecs`, because the ECS references no serializer by design and the
binders belong to doc 08's registry. ⚠ What it cannot carry is a fact about the ECS worth restating
here — an `Entity` is a slot, a generation and a world id, so a saved handle means nothing when read
back. The hierarchy travels as a table of indices and is rebuilt; a game component holding an `Entity`
is the caller's to translate, and the serialiser hands it the table.

---

## Phase 3 — Asset pipeline and mobile bring-up ✅ *(4.0 EM)*

**Goal:** real content loads from bundles, and it does so on a phone. AOT correctness is proven before
the codebase is large enough for it to be expensive to fix.

**Landed.** `Vixen.Core.Yaml` (73 tests), the asset database (26), `Vixen.Core.Imaging` (146),
`Vixen.Assets` — catalog, loading, remote content, content updates (48 + 64 + 31 + 19),
`Tools/Vixen.ContentServer` (34), `BuildPlanner` and sub-asset addressing, the importers
(Texture/Model/Audio/NativeFormat/Raw), the out-of-process worker, `Vixen.Sdk` (7, each a real
`dotnet build`), `Vixen.Cli` (41), both AOT gates, `Vixen.Platform.iOS`, `.Android`, and
`Samples/07-AddressablesRemote`.

**This phase is deliberately early and deliberately painful, and it paid.** The AOT wall arrived on day
one: the obvious object binder needs `Array.CreateInstance`, `MakeGenericType` and
`Activator.CreateInstance(Type)` — all three `RequiresDynamicCode`, and the build refused all three. A
binder built on them would have worked on a desktop and thrown on a phone, and would have been found in
this phase's last week rather than its first. Two further findings are the kind only shipping finds: a
**silent serializer bug** gave every immutable struct in `Vixen.Core.Mathematics` a serializer with no
members at all, writing two varints and reading every component back as zero with no diagnostic; and
`Environment.GetFolderPath` returns *the empty string* on Unix for a directory that does not exist yet,
so the engine would have written its saves into whatever the working directory happened to be.

**Doc 01's ImageSharp decision did not survive contact.** ImageSharp 4.0.0 fails the build without a
purchased licence key, from its own targets file, before any code compiles. A repository people are
meant to be able to clone cannot require that, so `Vixen.Editor.Assets` took doc 01's own stated
fallback, `StbImageSharp`. The swap cost one class — `IImageDecoder` earning itself on its first day.
Coverage shifted rather than shrank: Radiance HDR arrived, `.exr`/`.tif`/`.webp` left.

**Exit — mostly met.**

| Criterion | State |
|---|---|
| iOS NativeAOT publish, zero trim/AOT warnings | ✅ `nuke CheckAotIos` produces an `.ipa` of native code with **no managed assemblies in it** |
| Remote content update fetches only changed bundles | ✅ asserted by URL *and* by byte count — 144.6 KB cold, 48.6 KB update |
| Content-build determinism across three OSes | 🟡 green locally between two projects at different paths whose assets carry different GUIDs; the three-runner comparison waits on CI legs |
| Incremental import of one texture < 1 s in a 10 k-asset project | 🟡 measured at **0.88–1.2 s, median ~1.05 s** — on the line rather than under it, on a quiet machine, and not yet a repeatable gate |
| `Samples/01` on a physical Android device and iPhone | 🟡 runs on the iOS Simulator and the Android emulator; an iPhone needs a provisioning profile, which is an Apple account rather than a build setting |

**One trap worth keeping.** The Android emulator must be started with `-gpu swiftshader_indirect`: its
host-GPU path reports every step succeeding and presents nothing. That is the emulator's, not the
engine's, and it cost an hour and two wrong fixes reasoned from the symptom.

---

## Phase 4 — UI framework ✅ *(7.0 EM)*

**Goal:** a standalone Vixen application with a real interface. The largest phase; sequenced so each
sub-piece has its own gate.

| Sub-phase | Landed | Gate |
|---|---|---|
| **4a** Reactive + layout | `Vixen.Ui.Reactive` (63 tests), `Vixen.Ui.Layout` — the complete flexbox algorithm (552) | ✅ **534 Yoga conformance fixtures**, committed before the implementation; 530 passed on the first run. Zero-alloc settled tree; an unchanged pass costs 11 ns whatever the size |
| **4b** Styling | Selector engine, cascade, invalidation, transitions and `@keyframes`, the utility preprocessor | ✅ selector-matching, style-sharing and invalidation-minimality oracles; cascade/`@layer` order; utility families |
| **4c** Text | UAX#29/#14/#9, shaping through HarfBuzz, cluster reconciliation, outlines, variable fonts, rasteriser, MSDF, atlas, tessellation, the GPU renderer | ✅ **22 048 + 91 707 Consortium conformance cases**; 328/413 shaping cases with the 85 HarfBuzz failures pinned *in both directions*; 100 variable-font cases |
| **4d** Element tree, markup, rendering | `UiElement`/`UiDocument`, the generated property system, hit testing, routed events, the draw list, focus, tab and arrow navigation, gestures, removal and compaction, `Vixen.Ui.Composition`, VXML and its generator, hot reload | ✅ draw-list goldens; parser golden trees and error recovery; markup compiled, loaded and driven by a signal end to end |
| **4e** Controls | `Vixen.Ui.Controls` — 40-odd over one base (78 tests) — and `.Controls.Advanced` (253) | ✅ a `DockingHost` layout round-trips through YAML |

**Exit — met.** Yoga suite green. **UI frame under 2 ms with 5 000 elements and zero steady-state
allocation** — measured at 8 001 elements, 0.230 ms, **0 B**, and still holding at 32 001 elements and
1.18 ms. Docking layout round-trips. 🟡 `Samples/02` runs on macOS and is tested on Windows/Linux in
CI, but **no CI step runs either sample**, so the `--frames N` flag both READMEs describe as CI's proof
is not wired to anything. The browser run is Phase 10's.

**The findings from this phase live in the assemblies' own READMEs**, which is where a reader standing
in the code will be. Four are worth surfacing here because they are about *testing* rather than about
the UI, and because they recur:

- **An oracle that shares an implementation with its subject is not an oracle.** The incremental-style
  oracle first built its cold reference by replaying the same mutations on a second tree, so anything
  that code got wrong was wrong identically on both sides.
- **A generator needs a coverage assertion for the same reason a test does.** Every stylesheet the
  property generator produced contained a sibling selector, which turns style sharing off — so the
  sabotage meant to catch a stale sharing cache was unreachable by the property meant to catch it.
- **A test that cannot reach the code it names passes for the wrong reason.** Four separate times: a
  guard behind a value ExCSS validates away, a zero-size guard the beam had already excluded, a measure
  function over no text, and a check inside control flow every fixture wrote around.
- **A gate is only a gate for what it can observe.** Shaping each run without the text around it fails
  *nothing* across 413 external cases, because every case is a single run — and that context is what
  decides whether an Arabic letter joins.

**Owed.** ~~The largest item is a performance one: `UiDocument.Update` calls `StyleEngine.ResolveAll`,
so the incremental cascade that 4b built and gated is referenced only by its own project's tests.~~
Built — the class toggle went from 9.50 ms / 8.87 MB to 0.94 ms / 552 B. So is the other half of that
sentence's problem, found later: `Update` did not drain the document's **effects** either, so a host
that did not know to call `Flush` itself drew an interface whose bindings never ran — invisible
because every test in the repository flushes by hand. The rest — `TextEditor` with IME and caret
affinity, rich-text runs from markup, named slot projection, pinch and rotate, CSS Grid — is in
[`../overview.md`](../overview.md) § 1.7; multi-window and DPI have since landed.

---

## Phase 5 — Renderer 🟡 *(4.5 EM)*

**Goal:** the forward+ pipeline with full PBR, shadows and post FX.

**Landed.** `Vixen.Shaders` — typed parameter and permutation keys, the constant-buffer writers, the
effect system and all three cache tiers, build-time pre-generation in `Tools/Vixen.ShaderCompiler`, and
`Tools/Vixen.ShaderCompilerService`. `Raven/Library` — the full shader library, every shader reaching
both backends under `glslc` and `spirv-val`. `Vixen.Rendering` — the spine, both visibility groups
including **two-phase GPU occlusion culling**, and the concrete features (mesh, transform, skinning,
instancing, material, lighting, shadow-caster). Materials as a composable feature tree, with every
feature and shading model composed into the shipped `ForwardPlus` and validated. All light types,
clustered binning, IBL and reflection probes. Shadows — CSM, cube, spot, atlas, static caching,
PCF/PCSS. `Vixen.Rendering.PostFx` with eleven effects. `Vixen.Graphics.OpenGL` (92 tests against a
recording `IGlApi`, so the translation is exercised on every build rather than only where a driver is).
[`../rhi-backend-mapping.md`](../rhi-backend-mapping.md). `Samples/03-PbrShowcase`. The golden-image
suite at forty fixtures — one per state bit a backend can silently ignore — plus one composed Forward+
frame.

**`Vixen.Graphics.Direct3D12` is not built** (Q4: postponed past 1.0), and the stub project ADR-001
reserves does not exist either. The abstraction-validator role passes to `Vixen.Graphics.OpenGL`, which
is a stricter test — GL is *further* from Vulkan than D3D12 is, so an RHI that survives it will map to
D3D12 comfortably.

**Two engine bugs the golden images caught that nothing else could**, both worth knowing because both
produce valid SPIR-V, no validation message, and a black frame: a composed material parameter's
qualified name depended on the order the lowerer merged types, so the engine predicted one name and the
compiler emitted another and every material value uploaded as zero; and one Raven struct used in both a
uniform block and a storage buffer became two SPIR-V types with the same debug name, which a translator
with one namespace collapses — on Metal the padded `float3` won and the fragment stage read a light four
bytes late while the compute stage that filled the same buffer read it correctly.

**Exit — partly met.** Zero runtime shader compilation in a shipping build of `Samples/03` is asserted
by test. Golden images are within tolerance on MoltenVK and lavapipe. **Not met:** the doc 00
performance bar on Vulkan *and D3D12* cannot be met while D3D12 is postponed; white-furnace and BRDF
numeric tests need a compute readback ([07](07-raven-shader-pipeline.md)); shader hot reload under
500 ms is unmeasured.

**Owed.** SMAA, MSAA resolve, the full GTAO integral and SSR — each needs a shader that does not exist
yet, or the compute node. Light probes are **withdrawn rather than owed**: tetrahedral interpolation
needs exact predicates, it was written, found wrong by its own tests, and taken back out — and
[19](19-lighting-and-global-illumination.md) retires the whole approach.

*This list used to be longer.* Compacted draws, per-object reflection probes, depth of field, motion
blur and `AutoExposure` wiring have all since landed; the grading LUT is half-landed — the frame
consumes one, but `CubeLutImporter` is not registered, so nothing can author one.
[`../overview.md`](../overview.md) § 1.9 carries the per-feature state and wins on disagreement.

---

## Phase 5b — Raven parser migration ✅ *(1.5 EM)*

**Goal:** replace Raven's ANTLR front end with a hand-written Roslyn-style lexer and recursive-descent
parser, and land incremental reparse in `Vixen.Core.Syntax`.

**Why it was placed here.** After Phase 5 because `Raven/Library` is what shakes out the last of the
syntax, and migrating into a churning grammar pays the cost twice. Before Phase 6 because the editor's
`CodeEditor` needs incremental reparse and squiggle-grade diagnostics for `.rvn`, and ANTLR can give
neither.

**Landed, and it cost its cheapest** because the language surface settled first — doc 07 § J's pruning
passes removed a third of the syntax before any parser was written. Steps 1–6 are complete: the corpus
frozen, `SlidingTextWindow`/`SyntaxParser`/`Blender` lifted into `Vixen.Core.Syntax`, `RavenLexer` and
`RavenParser` emitting green nodes directly, `SyntaxAntlrVisitor` and the `catch`-and-discard gone,
ANTLR out of the shipping projects. Full record in [18](18-raven-parser-migration.md).

**The `.g4` files are kept** in a test-only project as a permanent differential oracle: every corpus
file is parsed by both front ends and the trees compared. Same technique as the SPIR-V-vs-`shaderc`
oracle, and it is what made the migration safe rather than hopeful.

**Exit — met.** Byte-identical trees across the whole corpus; ANTLR gone from shipping; a `.rvn` edit
reparsing incrementally with green-node reuse at member granularity; the differential oracle green in
CI.

---

## Phase 6 — Editor shell 🟡 *(4.5 EM)*

**Goal:** the editor is usable for real work.

**Landed.** `Vixen.Editor.Core` (48 tests, including the randomised do/undo/redo/merge sequences doc 11
asks for, checked against a snapshot model). `Vixen.Editor.Ui` — the shell, with menus, toolbars,
context menus and the command palette all as *views over one command registry*, so a new action appears
everywhere at once. `Vixen.Editor.Inspector` — generated drawers reaching a field by reference.
`Vixen.Editor.SceneView` — viewport, gizmos, picking, camera navigation, `SceneDocument`, the
`.vxscene` authoring format, and play-in-editor in **both** topologies. `Vixen.Editor.App` and the
project browser. `Vixen.Ui.Controls.Advanced`'s remaining eight controls.

**Exit — the sentence is met.** The editor opens a project, imports assets, builds content, edits a
scene, saves, and runs the game — **entirely in `Vixen.Ui`**, with no other toolkit anywhere in the
dependency graph and never was. Creating, deleting and renaming entities are undoable with the handle
surviving a delete-and-undo (`World.TryRecreate`) and the entity returning to its own place among its
siblings (`Hierarchy.SetParentAfter`).

**What the sentence did not cover has since landed as well:** the asset editors
(`Vixen.Editor.AssetEditors`), the profiler, the debugger, the plugin host, the automation harness and
the animation graph are all projects now — so cut-list #7 was built rather than cut.

✅ **The viewport composes a real frame.** It draws the scene through a `GraphicsCompositor` into the
window's render graph — every pane of a quad layout, each with its own `RenderView` — rather than the
lines and one-directional-term meshes this paragraph used to describe. `Editor/Vixen.Editor.App`'s
`FramePresenter` and `EditorWorldRenderer` are the entry points; [20](20-editor-parity.md) and
[`../overview.md`](../overview.md) § 1.11 carry the per-feature state.

**What is left of this phase**: the *packaging* half of publishing — `build/Build.Publish.cs` has a
working `PublishEditor` that publishes self-contained per RID, but `.app`/`.dmg`, AppImage and MSI,
with signing and notarisation, are owed — plus golden screenshots for editor layouts, and the
editor-shell performance bar, which is **unmeasured**: nothing runs that benchmark yet.

**[20 — Editor Parity](20-editor-parity.md) is the sequel to this phase**, and its framing is the honest
one: this phase makes the editor work, and that document is what the difference between "the editor
works" and "the editor is one a professional will use" actually costs.

---

## Phase 7 — Node graphs and VFX 🟡 *(3.5 EM)*

**Landed.** `Vixen.Editor.NodeGraph` — model, generated registry, compiler, port typing, and the view
over `NodeCanvas` with pan, zoom, marquee, wires, minimap, search-to-create, drag-from-port and
auto-layout. `Vixen.Editor.ShaderGraph` and `.VfxGraph` on top of it. `Vixen.Vfx` — SoA storage, the
compiled graph, a deterministic RNG shared by both paths, CPU jobs, `ParticleRenderFeature`, and a
compute-shader emitter.

**Two decisions that paid for themselves.** A sub-graph is **inlined rather than called** — every
target here is a straight-line program over values, with no function to call and no stack to put one on
— so `SubGraphs.Flatten` hands the compiler a graph containing none and the compiler has no idea
sub-graphs exist. That cost one property and four lines. And the compiled VFX form is **an array of
fixed-size operations**, designed in rather than retrofitted, because that is the only shape a CPU loop,
a shader emitter, a constant buffer and a golden test can all read — so `VfxShaderEmitter` is a `switch`
writing a line per operation rather than a second implementation, and a node graph that produces the
array produces the shader **by calling one method**.

**The view is a one-directional projection**, rebuilt from the model on every structural change: the
canvas already culls to the viewport, so the cost is bounded by the screen rather than by the graph, and
a projection that is rebuilt cannot drift from the document. A drag is the exception and writes
positions in place, because that is the path that runs every frame.

**Emitting the compute shader found a lowering bug in Raven**, written up in
[07](07-raven-shader-pipeline.md) § the binding merge: an inherited `RWBuffer` arrived read-only, and
only GL objected, because `spirv-val` accepts a `NonWritable` variable that is then stored into. It
also settled what the language was missing for the rest of the GPU path, and it was one thing:
**atomics**, now built.

**Exit — one of two met.** ✅ **A VFX graph produces the same particles on both paths**, asserted on a
real device at stated tolerances and validation-clean: `VfxGpuSimulation` owns the storage, the
descriptors and both transfers, and `Platform/Vixen.Vfx.Gpu.Tests` puts the runtime, the compiler and
the driver in one process so the question can be asked at all. ⬜ A PBR material authored in the shader
graph rendering identically to its hand-written Raven equivalent still needs a preview renderer.

**Reaping was the part a dispatch cannot do the obvious way** — a compacting reap needs two full sets
of the attribute buffers, and the survivors come out in an order neither backend promises. Both paths
are correct only because a particle's randomness follows its *identifier* rather than its slot, which
is a Phase 7 week-one decision cashed in here. Full account in
[`Core/Vixen.Vfx/README.md`](../../Core/Vixen.Vfx/README.md).

**Owed.** GPU sort — and **not** for the reason this paragraph used to give. It said the sort was
blocked on Raven workgroup-shared memory; that is wrong twice over. `groupshared` is built and
already spent by `Culling.rvn`, `VisibilityTiles.rvn` and `WaterTiles.rvn`, and `ParticleSort.rvn`
uses none of it. The shader is written; what is owed is the dispatch. Mesh, ribbon and light
renderers; the force-field, curl-noise, collision, sub-emitter and trail updaters. A shader-graph
preview renderer — the framework's preview layer already draws a render target, so this is unblocked.
Raven-span-to-node diagnostic mapping, which needs the emitter to record spans as it writes.

---

## Phase 8 — Gameplay subsystems ✅ *(3.5 EM)*

**Landed.** `Vixen.Physics` over Jolt 2.22.0 — everything the phase asked for, plus the bit-exact
determinism gate. `Vixen.Audio`, which went well past the line: sends and sidechains, fourteen effects,
voice stealing and virtualisation, `AudioEvent` with variants and instance limits, parameters with
authored curves, interactive music with sample-accurate scheduling, quad/5.1/7.1 panning, four listeners
for split-screen, capture, a BS.1770 loudness meter, and voice chat joined up once Phase 9 landed the
transport it was waiting for. `Vixen.Animation` — skeletal playback, 1D/2D blend trees, layers and
masks, a state machine, IK, root motion, events, GPU skinning, key reduction. `Vixen.Input` — the full
device set and the Unity-style action model. `Vixen.Navigation` — **Vixen's own managed code rather than
a Recast/Detour binding**, 40 tests, with zero steady-state allocation measured rather than claimed.

**Why the navmesh binding was not built**, since the decision is the interesting part: Recast/Detour
publishes no binaries and has no C API, so a binding is a C shim plus a build per RID plus an entry in
the native manifest — and iOS is NativeAOT-only while WebAssembly has no dynamic loading at all. The
algorithms are re-derived and credited; no code is copied. Two of its own numbers are recorded as tables
rather than as hopes: watershed partitioning is **not uniformly better** than the row sweep (25 % fewer
polygons on an axis-aligned level, 19 % fewer on a round obstacle), and the height detail pass takes
mean error on a 24 m hill from 0.76 m to 0.15 m for a bake 56 % longer.

**One gap is a platform one.** `JoltPhysics.Native` ships **no iOS slice**, so `Samples/05` cannot run
there until a static `libjoltc.a` is pinned the way MoltenVK already is.

**Exit — not met, for one reason.** The fixed-step determinism gate is green and runtime rebinding with
conflict detection works. `Samples/05-PlatformerGame` **does not exist**, so "playable on five
platforms" has nothing to play. It no longer waits on the compiled scene format, which exists — it
wants an authored level, and on iOS it also wants the Jolt slice above. The *end-to-end player* proof
doc 29 called P4 has meanwhile landed elsewhere, as `Samples/13-ThirdPersonShooter`.

**Owed.** Navmesh baking from a compiled scene — ⚠ blocked on a **decision** rather than on the
format: `NavMeshImporter` wants `(path, transform)` pairs and a scene gives an `AssetReference` GUID.
Ragdoll integration, which lands with the animation/physics join. Sensors, pen, MIDI and HID, which
need platform contracts before they can have action-side ones.

---

## Phase 9 — Networking and multiplayer ✅ *(5.0 EM)*

**Goal:** a server-authoritative multiplayer sample playable across the network, with replication,
interest management and lag compensation.

Sequenced here because it depends on the ECS change-version machinery (Phase 2), deterministic prefab
and content IDs (Phase 3), and physics for lag compensation (Phase 8). **This is the most complete phase
in the repository.** Full design in [16](16-networking.md); the per-package READMEs under
`Core/Vixen.Net*` carry the detail, each with its own *Owed* section.

**Landed.** The transport contract and its executable conformance suite, run against every transport;
`Local`, `NetworkSimulation`, `Udp`, `WebSocket` and `Composite`. The tick clock, the never-throwing
packet codec, and the session. RPC with generated senders and six pre-dispatch checks; awaitable RPC;
broadcasts. `NetworkRules` as a declaration rather than a `switch`. Replication — bit packing,
`[Quantize]`, capture-once and copy-many, two-stage filtering, acknowledged baselines, priority
shedding, and field-level delta. `SyncVar`/`SyncList`/`NetworkModule`. Interest management. Motion,
`NetworkTransform`, networked rigid bodies, lag compensation, networked animation and audio, spawning
and scenes. The security pass and `Vixen.Fuzz`. Metrics over OpenTelemetry. Client-side prediction,
both mechanism and wiring. `Samples/08`, `09` and `10`.

**Three decisions in the transport contract are load-bearing and were made here rather than discovered
later.** One object holds both halves, so a listen server is one transport with a loopback in it rather
than a second code path through every layer above. Nothing is delivered outside `Poll` — no callback on
a socket thread — which is what lets replication be ordinary code at a known point in the schedule. And
**time is a parameter, not a reading**: `Poll(elapsed, events)` is *told* how much time passed, so a
test observing a 200 ms round trip does it in a loop rather than in 200 ms.

**Exit — all five criteria met.**

| Criterion | Result |
|---|---|
| `Samples/08` playable server↔client under 20 % injected loss | ✅ green at 0 %, 20 % and 40 % with latency to match, exiting non-zero when the clients disagree with the server |
| N-client in-process convergence tests | ✅ |
| Bit-exact serialization across three desktop OSes | ✅ committed bytes, one hex line per named case, asserted by the existing CI matrix — three operating systems and two architectures, so a dedicated job would be the same assertion a fourth time |
| Packet-reader fuzzing clean | ✅ 20 targets, 5 oracles, ~12.1 M cases on every build in about eighteen seconds |
| 100-connection / 5 000-entity soak holding its budgets for 30 minutes | ✅ 54 000 ticks: **75.2 kbit/s a client, a p99 tick of 2.4 ms against a 33 ms budget, three Gen0 collections in the half hour** |

**The fuzzer's own lessons are the ones to carry.** It found four defects on its first run, all in code
that had tests and review. Then **the harness had the same class of bug it was written to find**: a
signature folded from lifetime counters strictly increases, so every case looked novel and the guidance
switched itself off — `rpc` kept 1,027,530 inputs out of 1,027,508 cases. And two targets' first
versions managed *two distinct behaviours* in two million cases, which presents as a clean run: every
datagram was refused at the handshake, because completing one needs a cookie no amount of mutation
guesses. That is the cookie working exactly as designed, and it meant the reliability layer and the
fragment reassembler sat behind a door the fuzzer could not open. An authenticated client is still an
untrusted one, so the target now connects properly and *then* sends rubbish.

**Owed.** The `Relay` transport is **blocked on a scope decision rather than on work** — doc 16 asks for
"rendezvous + relay client", and a relay client with no relay server to talk to is untestable and
unshippable, so whether one is hosted, in-box or an addon wants an answer before code. Transport
*fallback* belongs with it. Everything else is in the `Vixen.Net*` READMEs and
[`../overview.md`](../overview.md) § 1.12.

---

## Phase 10 — Deferred, advanced rendering, Web 🟡 *(2.5 EM + 4.5 EM lighting)*

**Landed, ahead of schedule and partly outside the original scope.** `Vixen.Graphics.WebGPU` on both
surfaces — native Dawn/wgpu and `navigator.gpu` — over one `IWebGpuBinding`, with **the web path
covered by tests that run on a CI machine with no browser**. `Vixen.Platform.Web`. `Vixen.Video` with
its codec and rendering siblings, and `Vixen.Xr` with the OpenXR backend: both were cut-list items and
both landed early. Global illumination phases **L1** and **L2** complete — all three of L2's exit
criteria are asserted end to end, and its last known defect, the device border sync racing its own
reads, is closed by committing borders rank by rank on both sides. **L3** is started and runs as a
schedule: the octahedral map with exact texel solid angles, the probe lattice, and the traced
reference exist in `Vixen.Rendering.ScreenProbes` checked against the same closed forms L2 was; the
trace, resolve and upsample all dispatch and agree with that reference; one compositor node places
probes from the frame's own depth buffer and draws the result; and the trace order opens with a
screen trace against that same depth on both sides. The denoiser — § L3's stated risk — is not
started. See
[19](19-lighting-and-global-illumination.md), which **retires baked lightmaps and tetrahedral probes**
rather than deferring them, and which is where most of that phase's saving comes from.

**The WebGPU version pin is not a preference and not a version either.** `Silk.NET.WebGPU` 2.23.0
matches **no** wgpu-native release: it declares three functions wgpu-native removed in v22.1.0.1 while
carrying a struct field that same release added. It is a Dawn binding, and there is no wgpu-native that
agrees with it on both counts — so the pin carries a refusal and a struct override, both checked rather
than assumed.

**Not started.** The deferred pipeline — GBuffer layout, shading-model-ID dispatch, automatic forward
routing for non-representable materials, decals. Contact shadows, light shafts, SSS blur, the upscaler
interface and FSR1. Mesh shaders and meshlet culling behind capability flags, for which
[22](22-virtualized-geometry.md) is now the plan. (Motion blur has since landed, and volumetric fog is
partly built — see [`../overview.md`](../overview.md) § 1.9.)

**Exit — not met.** Deferred does not exist, so it cannot pass the golden-image suite. `Samples/02` in
three browsers still needs a GLES context actually stood up: `SilkGlesApi` and `EglContext` are
written and tested, but every `EglContext` construction in the tree is in that project's own test
assembly, so nothing outside it has run one. `Samples/06-CanvasStress` is P2 and uncut; the editor
became the application-platform proof instead.

---

## Phase 11 — Polish and 1.0 ⬜ *(2.5 EM)*

The per-item state is [`../overview.md`](../overview.md) § 1.15; this is the checklist.

- Every performance bar in [00](00-vision-and-principles.md) measured and green on real hardware across
  the IHV matrix.
- `PublicAPI.Shipped.txt` frozen; the API review pass. The *gate* exists, so what is left is the
  reading nobody has done and the fold at release.
- Documentation, specified in [25](25-documentation-generator-and-site.md) at ~10 EM — of which the
  writing is a third and is continuous rather than a phase. Plus 12+ runnable samples; twelve exist,
  `05` and `06` being the gaps.
- `dotnet new` templates verified from a clean machine on all six targets. The plugin template is owed
  rather than blocked; "from a clean machine" also wants a feed with the engine packages on it, which
  is what makes this a Phase 11 line rather than a done one.
- Release automation end to end: tag → signed editor builds for three desktops + NuGet push + GitHub
  Release with a changelog.
- ✅ **Fuzzing corpora seeded and running nightly** — `nightly.yml` runs a matrix job per target over
  all twenty, `fail-fast: false`, each with its own budget and artifact. ⚠ One job per target is the
  point: a target that ends its own process then costs only its own night's depth. Soak tests (24 h
  editor, 24 h game) are **not** clean — the 30-minute network soak is the only one that exists.
- A public issue-triage and support process, and a written compatibility policy.

**Exit:** a person who has never seen the repo can install the SDK, create a project, build it for all
six targets, and ship it — using only the published documentation.

---

## Delivering this solo, with AI assistance *(Q8)*

**The constraint:** one person implementing, using AI. It changes how this plan is *executed* more than
any technical decision in it.

### The arithmetic, stated honestly

No AI multiplier is claimed here, because none can be substantiated and a number invented in a planning
document propagates into decisions that deserve better. What is defensible is *where* assistance helps:

| Helps a lot | Helps little |
|---|---|
| Porting known algorithms with an oracle — the Yoga suite is the ideal case | Novel architecture decisions, which are the ones already made in these documents |
| Source generators, serializers, boilerplate, the 200-project scaffold | Debugging driver-specific GPU behaviour |
| Test suites, fixtures, golden files, benchmark harnesses | Performance tuning against real hardware |
| Mechanical refactors across many files | Anything needing a physical device in your hands |
| Documentation, XML docs, the manual | Long-horizon architectural consistency — *your* job |
| Reading Stride/Arch/PurrNet/Yoga for "how did they do X" | Deciding which of several defensible designs to commit to |

The total does not shrink to a quarter, but the *tedious* fraction — large in a project this
scaffold-heavy — compresses meaningfully. Treat the EM figures as work content, not calendar time, and
plan against milestones rather than a completion date.

### Ship something useful early

The dominant risk for a solo multi-year project is not technical. It is **arriving at 60 % complete with
nothing shippable**, losing momentum, and stopping. Four milestones, each independently useful and
publishable:

| Milestone | Phases | ~EM | State |
|---|---|---|---|
| **M1 — "it runs"** | 0–2 | 9.5 | ✅ reached |
| **M2 — "it is a game engine"** | +3, 5, 8 | +12 | ✅ reached in substance — a programmer can build a real 3D game, code-only |
| **M3 — "it has an editor"** | +4, 6 | +11.5 | 🟡 the shell, the asset editors and a composing viewport are reached; [20](20-editor-parity.md)'s surface is not |
| **M4 — "it is complete"** | +7, 9, 10, 11 | +13.5 | 🟡 Phase 9 is done; 7, 10 and 11 are not |

**M2 was the one that mattered**, and it is passed: if the project stopped here it would still be a real
thing that works, which is not true of stopping mid-Phase-4.

> **A reordering that was considered and is now spent.** This document used to recommend swapping Phases
> 4 and 5 so a working renderer shipped before the 7 EM UI investment. Both are built, so the
> recommendation is history — recorded because the *reasoning* still applies to any future pair: prefer
> the order that reaches a shippable artefact sooner, unless a dependency forbids it.

### Practices that matter more when solo

- **These documents are the durable memory.** With no colleagues holding context and AI sessions that
  start fresh, `docs/plan/` *is* the architectural continuity. Keeping it current when a decision
  changes is load-bearing engineering work, not documentation hygiene. The ADR register and the
  resolved-question table exist for exactly this.
- **External oracles are worth disproportionately more than usual.** The Yoga suite, the UAX test data,
  `spirv-val`, the Consortium's shaping and variable-font cases, Arch's benchmarks, the golden images —
  all share one property: *they judge correctness without you having to.* That is the specific defence
  against the failure mode of AI-assisted work, which is code that reads plausibly and is wrong.
- **Sabotage is how you find out whether a gate is a gate.** Break the thing on purpose and check the
  suite goes red. Every phase above turned up tests that were green for the wrong reason, and nothing
  else would have found them.
- **The automated gates substitute for code review.** Warnings-as-errors, `AnalysisLevel=latest-recommended`,
  `CheckArchitecture`, `CheckApi`, the zero-allocation tests, the determinism tests. A solo developer
  has no reviewer; the build is the reviewer. Do not weaken these to move faster — they are the reason
  moving fast stays possible.
- **Finish subsystems.** Resist breadth. A fully tested `Vixen.Ui.Layout` with the Yoga suite green is
  worth more than five subsystems at 70 %, because 70 %-complete subsystems interact and their bugs
  multiply.
- **Keep `references/` cloned.** Stride, Arch, Yoga, PurrNet, SignalsDotnet and Flexbox are the
  highest-value context available for any "how is this normally done" question.
- **Automate the boring safety.** Nightly fuzzing, soak tests and the platform matrix run without you.

---

## Sequencing rules

These constraints mattered more than the phase numbers. Four are spent; four still bind.

**Still binding:**

1. **iOS/NativeAOT lands in Phase 3.** Non-negotiable, and it paid on day one of that phase. The
   general form: the cheapest insurance against reflection debt is a gate that fails before the
   codebase is large.
2. **Port the conformance suite before writing the implementation.** Applied five times — Yoga, UAX#29,
   UAX#14, UAX#9, shaping and variable fonts — and a red suite driving the implementation is a
   completely different experience from writing 3 000 lines and then finding out.
3. **`Vixen.Ui` must never reference `Vixen.Engine`.** Checked by `CheckArchitecture` from Phase 0 and
   still checked. The moment it is violated the application-framework claim is dead, and the violation
   is cheap to introduce and expensive to unwind.
4. **Every phase ends with a runnable sample, and every sample stays running.** Currently **not
   honoured by CI**: no leg runs a sample, so the `--frames N` proof both sample READMEs describe is
   not wired to anything.

**Spent, with the lesson that outlived each:** Raven gated Phase 5 only, and loosely (codegen and the
parser migration are both done). Spike the unknown before planning around it — the Web spike retired
R1 and caught a silent WebGL1 downgrade. Build the test harness in Phase 1; every later phase's
testability rested on `TestApp` and `RecordingBackend`. Give a scaffold a demolition date — ImGui was
cut in Phase 2 rather than built.

---

## What can be cut, if it must be

Stated in advance so the decision is made calmly rather than in month 30 — noting that the first three
have since been *built*, so they are no longer available as savings.

| # | Item | State |
|---|---|---|
| 1 | `Vixen.Navigation`, `Vixen.Video`, VR/XR | ✅ all three built. No longer a lever |
| 2 | **Networking as a whole, slipped to 1.1** | ✅ built. Was the cleanest 5 EM available, and is now spent |
| 3 | WebGPU backend | ✅ built |
| 4 | `Samples/06-CanvasStress` | ⬜ still available, still P2 — the editor became the application-platform proof |
| 5 | CSS Grid | 🟡 **no longer a clean lever** — placement and most of track sizing are built, 1 526 of 2 120 Taffy fixtures pass, and it reaches the layout store from a stylesheet. Nothing authored uses it yet, so what remains cuttable is finishing it |
| 6 | Deferred pipeline | ⬜ still available. Forward+ covers the 1.0 use cases, and the render-feature architecture accommodates deferred later without rework |
| 7 | `AnimationGraph` node editor | ✅ built rather than cut — `Vixen.Editor.AnimationGraph`. No longer a lever |
| 8 | Full accessibility bridge | ⬜ still available. Hooks stay, the platform bridges slip |
| 9 | The Web target entirely | ⬜ still available, and still the most defensible cut if schedule pressure is severe — ~15 % of the effort for the platform with the least clear payoff. Cutting it does not compromise the other five |

**The post-1.0 tracks in documents 19–24 are the new levers**, and each says so itself: GI phases L3–L6,
collaboration milestones 2–5, virtualized geometry, and blockout phases P5–P7 are all separable and all
ordered so that stopping after any one leaves a tool somebody uses rather than a branch somebody
abandons.

**Not cuttable, in any scenario:** the `.meta`/GUID model, addressables, the object database, the
capability-gated RHI, the conformance suites, the source-generator discipline, iOS AOT correctness, and
the `Vixen.Ui` ⇸ `Vixen.Engine` boundary. Each is either a foundation others build on or a decision that
cannot be retrofitted.
