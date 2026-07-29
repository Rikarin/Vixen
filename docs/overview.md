# Vixen — Implementation Overview

A single-page reconciliation of **what the plan asks for**, **what exists in the repository**, and
**what is holding the rest up**.

Sources: every file under [`docs/plan/`](plan/), [`docs/manual/`](manual/),
[`docs/rhi-backend-mapping.md`](rhi-backend-mapping.md), the per-project `README.md` files, the
`Directory.Packages.props` register, the Nuke targets in `build/`, and `.github/workflows/`.

> **Where the sources disagree, the code wins.** [`14-roadmap.md`](plan/14-roadmap.md) is the richest
> status record but its Phase 6/7/8 bullets predate the editor, animation and input work that has
> since landed; the per-project READMEs and [`02-repository-layout.md`](plan/02-repository-layout.md)
> are current for those. Rows below are marked against the code, and a divergence is called out in
> the note.

## Legend

| | Meaning |
|---|---|
| ✅ | Built, tested, and gated |
| 🟡 | Partially built — the named half works, the rest is listed under *Owed* |
| ⬜ | Not started — no project, or a project with no implementation |
| ⛔ | Blocked — cannot start until something else exists or a decision is made |
| ✂️ | Deliberately cut or postponed past 1.0 |

---

# Part 1 — Feature inventory

## 1.1 Build, CI and gates

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| Nuke `Clean Restore Compile Test Pack Benchmark` | ✅ | [build/Build.cs](../build/Build.cs) | |
| Nuke `CheckFormat`, `CheckArchitecture` | ✅ | [build/Build.ArchitectureRules.cs](../build/Build.ArchitectureRules.cs) | Enforces ADR-002 (no IL rewriting), ADR-015 (no ImageSharp), and the `Vixen.Ui` ⇸ `Vixen.Engine` boundary |
| Nuke `GoldenImages` | ✅ | build/ | 40 fixtures; generated on MoltenVK, verified on lavapipe |
| Nuke `CheckAot` (desktop) | 🟡 | build/ | Publishes one RID per invocation; three desktop OSes means one CI leg each — legs not wired |
| Nuke `CheckAotIos` | ✅ | build/ | `.ipa`, 7 MB native, zero managed assemblies, zero trim/AOT warnings |
| Nuke `CompileMobile`, `CompileWeb`, `RestoreNativeDeps` | ✅ | build/ | |
| Nuke `CheckApi` | ✅ | [build/Build.Api.cs](../build/Build.Api.cs), [Tools/Vixen.ApiCheck](../Tools/Vixen.ApiCheck/README.md) | 59 packable assemblies, 22 807 baselined entries, both directions gated (an unapproved addition *and* a silent removal). `Shipped` is empty everywhere, because nothing has shipped |
| `ci.yml` — 3 desktop runners, test + checks + pack | ✅ | [.github/workflows/ci.yml](../.github/workflows/ci.yml) | Doubles as the bit-exact-serialization gate (3 OSes, 2 architectures) |
| `nightly.yml` — long-running fuzz | ✅ | [.github/workflows/nightly.yml](../.github/workflows/nightly.yml) | 10 min/target vs. 1 s in the build gate |
| lavapipe Vulkan CI leg | ✅ | ci.yml | 155 Vulkan tests, zero skipped, validation-clean |
| NativeAOT publish leg on every PR | ⬜ | — | Gate exists, leg does not |
| CI leg that *runs* a sample (`--frames N`) | ⬜ | — | Both sample READMEs describe this as CI's proof; nothing is wired to it |
| Playwright browser smoke leg | ⬜ | — | ⛔ the only coverage for `Vixen.Platform.Web`'s interop |
| Content-determinism across 3 real runners | 🟡 | — | Green between two projects at different paths locally; cross-OS run waits on the legs |
| 10 k-asset import budget as a *gate* | 🟡 | — | Measured at ~1.05 s median against a 1 s budget; the fixture is hand-made, not a repeatable benchmark |
| `references/` submodules | 🟡 | [references/README.md](../references/README.md) | Clone commands tracked; clones left a local decision |

## 1.2 Core foundation

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Core` — identity, `GameTime`, `ServiceRegistry`, pooling, `DisposeBag`, `LeakTracker` | ✅ | Core/Vixen.Core | 86 tests |
| `Vixen.Core.Mathematics` — full ADR-003 type set + `Matrix3x3`, `ColorSpace`, `Oklab` | ✅ | Core/Vixen.Core.Mathematics | 126 tests + CsCheck properties. `Half` omitted (BCL has it) |
| `Vixen.Core.Collections` | ✅ | Core/Vixen.Core.Collections | `RobinHoodDictionary` and `FixedBitSet<N>` deferred with reasons |
| `Vixen.Core.Memory` — `NativeArray`, arena, buddy allocator | ✅ | Core/Vixen.Core.Memory | `GpuUploadRing` still owed |
| `Vixen.Core.Threading` — Chase–Lev deques, `JobHandle` DAG, `ScheduleParallel` | ✅ | Core/Vixen.Core.Threading | 45 tests |
| `VIXEN_JOB_SAFETY` access declarations | ⬜ | — | Needs the ECS to supply declarations |
| Thread pinning / affinity | ✅ | Platform/Vixen.Platform.{Windows,Linux} | The platform half. `SetThreadGroupAffinity` and `sched_setaffinity`, with performance/efficiency core classes; macOS reports `SupportsAffinity = false` and means it. What is owed is the scheduler asking |
| Job priorities / long-running tier | ⬜ | — | |
| `Vixen.Core.IO` — `VirtualPath`, mount table, providers, mmap, coalesced watch | ✅ | Core/Vixen.Core.IO | 123 tests. Android `AAssetManager` and Web IndexedDB/fetch providers landed with their platforms |
| `System.IO.Path` analyzer | ✅ | Core/Vixen.Core.IO.Analyzers | `VXIO0001`, 12 tests. Referenced by every `Core/` project from `Directory.Build.props`; off by name in the seven host-filesystem places. The synchronous-IO half of the rule is still review-only |
| `Vixen.Core.Serialization` + generator + `ObjectDatabase` | ✅ | Core/Vixen.Core.Serialization | 53 tests; LZ4/Zstd chunks, CRC-checked bundles |
| `Vixen.Core.Reflection` generator | ✅ | Core/Vixen.Core.Reflection | Generic types still unsupported |
| `Vixen.Core.Syntax` + generator (green/red trees) | ✅ | Core/Vixen.Core.Syntax | Shared by Raven, VXML — the Phase 0 extraction, and it paid off |
| `Vixen.Core.Yaml` — Vixen dialect, `.meta` model, migrations, `vx:` refs | ✅ | Core/Vixen.Core.Yaml | 73 tests incl. byte-identical round trip |
| `Vixen.Core.Imaging` — KTX2, mips, BC1/3/4/5/7/6H, IBL split-sum | ✅ | Core/Vixen.Core.Imaging | See gaps below |
| ASTC / ETC2 encoders | ⛔ | — | No managed encoder and none planned; needs `astcenc` pinned in `build/native-dependencies.json` |
| BC7 / BC6H multi-mode | 🟡 | Core/Vixen.Core.Imaging | One (single-subset) mode each — valid output, real quality ceiling. `ispc_texcomp` registered in doc 01 for the rest |
| KTX2/BCn verified against an independent implementation | ⬜ | — | Everything is asserted against hand-computed examples; `ktx validate` + a reference decoder are owed |
| `Vixen.Core.Diagnostics` — `[LoggerMessage]`, ring sink, profiler, Chrome trace | 🟡 | Core/Vixen.Core.Diagnostics | 18 tests; event ids registered in [log-events.md](manual/log-events.md) |
| Other log sinks (ZLogger file, console, `logcat`/`OSLog`, remote, `EventSource`) | ⬜ | — | ZLogger is in ADR-008 and **not in `Directory.Packages.props`** |
| Log rate limiting, UTF-8 record packing | ⬜ | — | |
| GPU profiling / memory attribution / Perfetto protobuf | ⬜ | — | ⛔ GPU half needs the allocators' reporting surface |

## 1.3 ECS, engine loop, scenes

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Ecs` — archetypes, chunks, edge graph, queries + generator, `CommandBuffer`, change versions | ✅ | Core/Vixen.Ecs | 90 tests |
| System scheduler — 9 phases, conflict graph, DAG on jobs, DOT/Mermaid dumps | ✅ | Core/Vixen.Ecs | |
| Read/write **inference** from query bodies | ⬜ | — | Attributes and programmatic declaration exist; the generator does not |
| World serialisation | ⛔ | — | Needs per-component serialisers from doc 08's scene work. `WorldDigest` (canonical hash) exists |
| `VIXEN_ECS_EVENTS` hooks | ⬜ | — | |
| Entity handle **reservation** (`World.TryRecreate`) | ✅ | Core/Vixen.Ecs | Allowed only when the slot's version is *exactly* one past the requested one — anything else would let one handle name two entities across its life |
| `Hierarchy.SetParentAfter` / `PreviousSiblingOf` | ✅ | Core/Vixen.Engine | Linking prepends, so undo needs a neighbour rather than an index — an index is invalidated by every insertion in front of it |
| Transform hierarchy with dirty propagation | 🟡 | Core/Vixen.Engine | Not depth-split — needs shared components. One visit per moved entity either way |
| `Vixen.Engine` — loop, fixed-step accumulator, `Behavior`, scenes, `SceneTag`, additive load | ✅ | Core/Vixen.Engine | 58 tests |
| Prefabs (capture + instantiate) | ✅ | Core/Vixen.Engine | |
| Prefab **overrides** and nested prefabs | ⬜ | — | Risk R7; needs the serialised scene format |
| Coroutines (`async Coroutine`, zero-alloc start) | ✅ | Core/Vixen.Engine | `WhenAny` owed; stopping a single launched coroutine refused by design |
| `DebugDraw` accumulator | ✅ | Core/Vixen.Engine | Lines, rays, arrows, boxes (AABB and oriented), spheres, circles, capsules, cones, frustums, crosses, axes, world labels, screen-space lines/rects/fills/text |
| `DebugDraw` **drawing** | ✅ | Core/Vixen.Engine.Renderer | Two line draws — world (billboarded labels included) and screen. Golden-image verified |
| Doc 13 overlays — frame stats, frame graph, log, console | ✅ | Core/Vixen.Engine | `IDiagnosticOverlay`, corner-stacked panels, `[ConsoleCommand]`. `AudioOverlay` in Core/Vixen.Audio; physics draws into the accumulator directly |
| Doc 13 overlays — render mode, UI debug, streaming | ⬜ | — | Render mode needs shader debug views in the compositor; the other two need `Vixen.Ui` and `Vixen.Assets` to report, and neither may reference `Vixen.Engine` (doc 02) — so each wants a join assembly or a data seam of its own |
| ImGui debug overlay | ✂️ | — | Cut in Phase 2 rather than built, and Phase 6's "delete it" step struck with it |

## 1.4 Graphics RHI and backends

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Graphics` RHI surface — formats, `synchronization2`-shaped barriers, typed handles, PSO/descriptor descriptions | ✅ | Core/Vixen.Graphics | 46 tests; reversed depth in the defaults |
| `DescriptorBinding` sample type / comparison sampler | ⬜ | — | ⛔ **blocks shadow maps on WebGPU**; an RHI change every other backend ignores |
| Placed resources (true memory aliasing) | ⬜ | — | Two of six planned backends cannot express it |
| `Vixen.Graphics.Null` + recording harness | ✅ | Platform/Vixen.Graphics.Null | Also the shipping dedicated-server backend |
| `Vixen.Graphics.RenderGraph` — culling, aliasing, batched barriers, derived store actions | ✅ | Core/Vixen.Graphics.RenderGraph | 34 tests incl. property tests |
| Async-compute queue scheduling | ⬜ | — | `PassKind` is declared and carried; every pass runs on one queue |
| `Vixen.Graphics.Vulkan` — whole device + command list | ✅ | Platform/Vixen.Graphics.Vulkan | 155 tests, validation-clean on MoltenVK 1.4.2 and lavapipe |
| Vulkan swapchain acquire/present automated coverage | ⬜ | — | Needs a window; AppKit aborts off the main thread. `Samples/01` exercises it |
| Timeline semaphores, MSAA resolve, query pools | ⬜ | — | |
| `Vixen.Graphics.OpenGL` — GL 4.5 core / GLES 3.0-3.2 / WebGL2 translation | ✅ | Platform/Vixen.Graphics.OpenGL | 131 tests against a recording `IGlApi` and a recording `IEglApi` |
| `Silk.NET.OpenGLES` + EGL context | ✅ | Platform/Vixen.Graphics.OpenGL | `SilkGlesApi` + `EglContext`. No `Silk.NET.EGL` exists for Silk.NET 2, so 19 entry points are loaded from `libEGL` through `Vixen.Platform.Native`. Nothing above `IGlApi` changed; `GL_FRAMEBUFFER_SRGB` is now gated, being desktop-only |
| `glBindImageTexture` (storage images) | ⬜ | — | Every compute path has a fullscreen-fragment variant meanwhile |
| `Vixen.Graphics.WebGPU` (native, Dawn/wgpu) | ✅ | Platform/Vixen.Graphics.WebGPU | Renders against pinned wgpu-native; push constants emulated as a dynamic UBO |
| `Vixen.Graphics.WebGPU.Browser` (`navigator.gpu`) | ✅ | Platform/Vixen.Graphics.WebGPU.Browser | Tested against a recording fake with no browser |
| WebGPU timestamp queries; Linux CI leg | ⬜ | — | wgpu-native on the lavapipe the workflow already installs would be a second implementation |
| `Vixen.Graphics.Direct3D12` | ✂️ | — | Postponed past 1.0 (ADR-001 / Q4). **The stub project ADR-001 reserves does not exist either.** GL is the abstraction validator |
| `docs/rhi-backend-mapping.md` | ✅ | [rhi-backend-mapping.md](rhi-backend-mapping.md) | |

## 1.5 Platform

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Platform` contracts (window, surface, display, files, clipboard, dialogs, lifecycle, input, IME, power, topology) | ✅ | Platform/Vixen.Platform | 26 tests |
| `Vixen.Platform.Headless` | ✅ | Platform/Vixen.Platform.Headless | 31 tests; drives the dedicated server |
| `Vixen.Platform.Desktop` (SDL **2** via Silk.NET) | ✅ | Platform/Vixen.Platform.Desktop | 58 tests. Doc 01 said SDL 3 and was wrong |
| File pickers, clipboard images/custom formats, thread affinity, thermal state | ✅ | Platform/Vixen.Platform.{Windows,Linux,MacOS} | SDL 2 has none of them; they arrive through `IPlatformSupplement`, chosen by operating system in `DesktopSupplements` |
| `Vixen.Platform.Windows` / `.Linux` / `.MacOS` | ✅ | Platform/Vixen.Platform.{Windows,Linux,MacOS} | 67 tests. `IFileDialog` · `zenity`/`kdialog` · `NSOpenPanel`; `CF_DIBV5` · `image/png` · `NSPasteboard`; affinity on two of the three; thermal on two of the three |
| `Vixen.Platform.Native` — RID chain, `runtimes/` search, `DllImportResolver`, `RestoreNativeDeps` | ✅ | Platform/Vixen.Platform.Native | Retired R11's desktop half with **no suppression** |
| Native-dependency acquisition beyond MoltenVK | 🟡 | [build/native-dependencies.json](../build/native-dependencies.json) | Holds MoltenVK (`ios-arm64`) and wgpu-native; R10 lists five more |
| `Vixen.Platform.iOS` (UIKit, `CAMetalLayer`, `CADisplayLink`, multi-touch, IME) | 🟡 | Platform/Vixen.Platform.iOS | Runs in the **Simulator**. Physical device ⛔ on a provisioning profile (an Apple account, not a build setting) |
| iOS sensors, haptics, Metal-layer HDR, `UIWindowSceneDelegate` | ⬜ | — | File dialogs, clipboard images, gamepads and hardware keyboard refused with reasons |
| `Vixen.Platform.Android` (SurfaceView, lifecycle, Choreographer, `AAssetManager`) | 🟡 | Platform/Vixen.Platform.Android | Runs on the **emulator** (`-gpu swiftshader_indirect` required). No physical device attached |
| Android GLES fallback + device-capability deny-list | ⬜ | — | Unblocked: the binding and the context are built. What is left is the head choosing GL over Vulkan and the deny-list that decides when |
| Android key translation, safe-area insets, sensors | ⬜ | — | |
| Android AOT gate (on its *default* runtime, not NativeAOT) | ⬜ | — | `XA1040` calls NativeAOT experimental on Android |
| `Vixen.Platform.Web` — canvas, all input, IndexedDB, fetch + ranges, single-thread job mode, lazy assemblies | ✅ | Platform/Vixen.Platform.Web | Not in `Vixen.slnx` (needs `wasm-tools` to evaluate) |
| Web: native dialogs, display enumeration, window position, thermal state, clipboard images | ⛔ | — | Absent by platform, not by omission — each documented with why |
| Browser transport for `Vixen.Net` | ⬜ | — | A browser cannot open a UDP socket; the existing `Vixen.Net.Transport.WebSocket` is a server/desktop implementation |
| `AudioWorklet` path (cross-origin-isolated pages) | ⬜ | — | Would cut WebAudio's 40 ms queue to ~2 ms |

## 1.6 Asset pipeline

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| Asset database — GUID index, reverse refs, duplicate repair, orphan quarantine | ✅ | Editor/Vixen.Editor.Core | 26 tests; 10 000 assets inside budget |
| `Vixen.Assets` catalog, `AssetHandle`, ref-counted scopes, label/glob loading | ✅ | Core/Vixen.Assets | 48 + 64 tests |
| Content references (`ContentReference<T>`) | ✅ | Core/Vixen.Core.Serialization | |
| Streamed content — `assets.Open(address)` over `ObjectDatabase.ReadRaw` | ✅ | Core/Vixen.Assets | Claims and caches nothing, so two callers get two independent streams — which is what a video whose picture and sound both seek needs. Build such payloads uncompressed |
| `ProjectWorkspace` + `ContentPipeline` (scan → import → plan → pack → write) | ✅ | Editor/Vixen.Editor.Assets | Moved out of `Vixen.Cli` so the editor and `vixen content build` cannot drift; the CLI keeps the console formatting and the worker pool |
| Content build — `.vxgroup`, `ContentBuilder`, content-hash bundle names, deterministic | ✅ | Editor/Vixen.Editor.Assets | 77 tests |
| `BuildPlanner` + sub-asset addressing (`characters/hero#Hero_Mesh`) | ✅ | Editor/Vixen.Editor.Assets | |
| Remote content — HTTP + ranges, `BundleCache`, resume, CRC | ✅ | Core/Vixen.Assets | 31 tests over a hostile transport |
| Content updates (hash file → catalog overlay, never throws) | ✅ | Core/Vixen.Assets | |
| `Tools/Vixen.ContentServer` | ✅ | Tools/Vixen.ContentServer | 34 tests, no socket; path traversal asserted 7 ways |
| Importers: Texture, Model (Assimp), Audio, NativeFormat, Raw/Default, NavMesh | ✅ | Editor/Vixen.Editor.Assets | |
| Out-of-process import worker (`Tools/Vixen.AssetCompiler`) | ✅ | Tools/Vixen.AssetCompiler | Crash isolation, not speed |
| **Parallel** import (N workers) | ⬜ | — | Pool runs N workers, `ImportPipeline` hands one job at a time; `--isolated` off by default |
| `AssetDatabase` per-entry persisted index (the remaining ~630 ms of the scan) | ⬜ | — | |
| `.vxscene` **authoring** format (YAML, `SceneFormat`/`SceneSerializer`/`SceneFileWriter`) | ✅ | Editor/Vixen.Editor.SceneView | Entities named by `EntityId` GUID, not by handle |
| `SceneCompiler` — `.vxscene` → runtime chunk, and a runtime scene asset | ⬜ | — | ⛔ **the single largest blocker in the repo** — see §3.1. `NativeFormatImporter` still only scans a `.vxscene` for dependencies and copies it through |
| `.vxnetrules` asset (importer + serialised form + per-prefab reference) | ⬜ | — | `NetworkRulesRegistry` is what it loads into |
| Colour-grading `.cube` LUT importer | ⬜ | — | |
| Server content profile (no textures/audio/shader permutations) | ⬜ | — | Doc 08's, not networking's |
| `Vixen.Sdk` MSBuild integration | ✅ | Tools/Vixen.Sdk | 7 tests, each a real `dotnet build` |
| SDK ships the `vixen` CLI in the package | ⬜ | — | Consumer still needs the tool restored or installed |
| Platform packaging (APK assets, iOS bundle, `wwwroot`) | ⬜ | — | Waits for those platforms |
| `Vixen.Cli` — `import`, `content build`, `content serve`, `doctor`, `new`, `build`, `run` | ✅ | Tools/Vixen.Cli | 41 tests incl. a byte-for-byte determinism gate |
| Signing, notarisation, DMG/IPA/AAB | ⬜ | — | Doc 17's table is still Nuke's |
| `app` / `plugin` / `tool` templates; `Tools/Vixen.Templates` | ⬜ | — | `game` scaffolds today; the project does not exist |
| `vixen doctor systems` | ⬜ | — | Needs a game assembly to load |

## 1.7 UI framework

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Ui.Reactive` — signals, computeds, effects, collections, async | ✅ | Core/Vixen.Ui.Reactive | 63 tests; diamond evaluated once, zero-alloc steady state |
| `EffectScheduler.Flush()` driven by the frame pass | ⬜ | — | **Found while auditing the READMEs.** It was owed on a `UiSystem` in 4d; 4d landed and no such type was built. `UiDocument.Update` plays the part and does not flush, so a host calls `EffectScheduler.Default.Flush()` itself — which every test does, which is why the gap is invisible |
| `Vixen.Ui.Layout` — SoA store + complete flexbox | ✅ | Core/Vixen.Ui.Layout | 552 tests |
| Yoga conformance suite (534 fixtures) | ✅ | Core/Vixen.Ui.Layout.Tests | 9 skipped (`display: contents`, out of scope) |
| Layout gates — zero-alloc settled tree, 11 ns unchanged pass, 1.16 ms at 10⁴ | ✅ | Benchmarks/Vixen.Benchmarks.Ui | |
| **CSS Grid** | ⬜ | — | A separate algorithm after flexbox; cut-list #5 |
| `Vixen.Ui.Styling` — selectors, cascade, `@layer`, `@media`, inheritance, `var()`, sharing | ✅ | Core/Vixen.Ui.Styling | Oracle gates green, verified by sabotage |
| Style **invalidation** (`StyleUpdater`, `StyleInvalidator`) | 🟡 | Core/Vixen.Ui.Styling | **Built and gated, but `UiDocument.Update` calls `ResolveAll` instead** — one class toggle costs 9.50 ms / 8.87 MB. Largest perf item in the phase |
| Transitions, `@keyframes`, `cubic-bezier`/`steps`/`spring()`, Oklab | ✅ | Core/Vixen.Ui.Styling | Springs solved in closed form |
| Transform decomposition | ⬜ | — | Waits on a transform property existing |
| `Vixen.Ui.Styling.Utilities` — tokens, scanner, variants, arbitrary values, `@apply` | ✅ | Core/Vixen.Ui.Styling.Utilities | 78 tests |
| Text: UAX#29 segmentation / UAX#14 line breaking / UAX#9 bidi | ✅ | Core/Vixen.Ui.Text | **22 048 + 91 707 conformance cases green** |
| Shaping (HarfBuzz) + itemisation | ✅ | Core/Vixen.Ui.Text | 328/413 Consortium cases; the 85 are HarfBuzz's and pinned in both directions |
| Cluster reconciliation, shaping cache, font fallback chain | ✅ | Core/Vixen.Ui.Text, Core/Vixen.Ui | |
| Glyph outlines (`glyf` + `CFF`), variable fonts (`fvar`/`avar`/`gvar`) | ✅ | Core/Vixen.Ui.Text | 100 Consortium variable-font cases green. `Vixen.Ui.Text/README.md`'s "owed: `gvar`" line is stale |
| `CVAR`, `CFF2` variation, direct `HVAR` | ⬜ | — | 6 cases excluded with the reason recorded |
| Rasteriser, MSDF, atlas, `GlyphFieldCache` | ✅ | Core/Vixen.Ui.Text | Gated by Green's theorem and by field reconstruction. ⚠ The atlas carries a `Version` (coordinates moved) *and* a `Revision` (bytes changed); an uploader watching the wrong one sends the texture once and never again |
| Line wrapping (`LineWrapper`) | ✅ | Core/Vixen.Ui.Text | Greedy first-fit, deliberately not Knuth–Plass |
| **Wrapping wired through** — `TextLayout` over `TextLine` over `TextRun`, `white-space`/`overflow-wrap` from the cascade | ✅ | Core/Vixen.Ui | Wrapping lives in `Vixen.Ui` because the widths do: a paragraph in two faces has no single design-unit scale. ⚠ Each wrapped line is re-shaped on its own — a ligature does not cross a break |
| `CodeEditor` wrap; the *editing* half of `TextArea` (caret between lines, Enter starting one) | ⬜ | — | The box grows downwards now; moving a caret through it is the text editor's item |
| `TextEditor` model — IME, caret affinity | ⬜ | — | Affinity is what makes bidi hit-testing answerable |
| Rich-text runs from markup (which stretch is bold) | ⬜ | — | The run list already carries face/size/tracking/leading |
| Geometry builder, path tessellation + antialiasing fringe, batching | ✅ | Core/Vixen.Ui | Trapezoid sweep, not ear clipping |
| `Vixen.Ui.Renderer` — box/text/solid pipelines, atlas upload, scissor clips | ✅ | Core/Vixen.Ui.Renderer | `ui-interface` and `ui-clipped` goldens; 13 sabotages |
| Element tree, `[UiProperty]` system, hit test, routed events, draw list, focus, tab + arrow nav, gestures | ✅ | Core/Vixen.Ui | |
| Element removal, style-slot compaction, `Move`, reparenting | ✅ | Core/Vixen.Ui | Tombstone + compaction, not slot reuse — the ordering invariant is why |
| Pinch and rotate gestures | ⬜ | — | One pointer at a time in `GestureRecognizer`; `Vixen.Ui.Testing` has a two-pointer transform |
| `Vixen.Ui.Composition` — `Component`, `@if`/`@switch`, keyed `@for` | ✅ | Core/Vixen.Ui | |
| `scoped` scoping + a component stylesheet loaded once per **type** | ✅ | Core/Vixen.Ui | `StyleIsScoped` was parsed, carried, and then read by nothing |
| Named slot projection; LIS reorder pass | ⬜ | — | The second is correctness-neutral — a move that changes nothing returns immediately |
| `VirtualizingPanel` — the virtualisation primitive doc 09 asks for | ✅ | Core/Vixen.Ui.Controls | Realises on `LayoutFinished`. ⚠ Fixed row heights only — variable heights need a running-sum index, which is a different control. `TreeView` is migrated onto it |
| Image / texture draw command (`DrawContext.DrawImage`, `BatchKind.Image`) | ✅ | Core/Vixen.Ui | Unblocked `Image`, `Viewport` drawing a `RenderTarget`, and the node-graph preview layer |
| Multi-window and DPI | ⬜ | — | Also what floating dock groups need |
| `Vixen.Ui.Markup` — VXML lexer/parser/binder/emitter, `#line` spans, incremental reparse | ✅ | Core/Vixen.Ui.Markup | 100 tests; byte-exact round trip over every *prefix* of a real file |
| `bind:` update events | ⬜ | — | |
| `Vixen.Ui.Markup.Generators` — `IIncrementalGenerator` | ✅ | Core/Vixen.Ui.Markup.Generators | 19 tests, two of them real `dotnet build`s |
| `vixen` CLI path emitting generated C# to disk | ⬜ | — | Doc 08 names it; the `CoreCompile` hook carries nothing |
| `Vixen.Ui.HotReload` — styles / markup / component replacement | ✅ | Core/Vixen.Ui.HotReload | 15 tests |
| Hot reload driven against a **running window** | ⬜ | — | Mechanism covered; never exercised end to end |
| `Vixen.Ui.Controls` — 40-odd standard controls, `ControlTheme` as `UserAgent` origin | ✅ | Core/Vixen.Ui.Controls | 78 tests over a real theme and font |
| `Vixen.Ui.Controls.Advanced` — Docking, TreeView, PropertyGrid, NodeCanvas, CodeEditor, DataGrid, Viewport, ColorPicker, CurveEditor, GradientEditor, Timeline | ✅ | Core/Vixen.Ui.Controls.Advanced | 253 tests |
| `UiDocument` "layout finished" callback | ✅ | Core/Vixen.Ui | All six controls on it. `Control.WhenResized` gates on the box changing; `Update` refuses a nested call, which is what lets a `Refresh` that runs its own pass be hung on the event |
| Undo inside controls | ⬜ | — | The four `Changed` events are the seams a real stack subscribes to |
| `Canvas2D` | ⬜ | — | Doc 09's P2, no editor consumer — see `Samples/06-CanvasStress` |
| `OkLch.ToSrgb` real gamut mapping | ⬜ | — | Clamps per channel today, which shifts hue |
| `StyleTree.AppendChild` O(children) | ⬜ | — | Every current control virtualises clear of it |
| `Vixen.Ui.Testing` harness + software rasteriser | ✅ | Core/Vixen.Ui.Testing | Group opacity, a third finger, and box assertions owed |
| `Samples/02-HelloUi` | ✅ | Samples/02-HelloUi | 8 001 elements at 0.230 ms, 0 B — exit criterion met with margin |

## 1.8 Raven and shaders

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| Hand-written lexer + recursive-descent parser (Phase 5b migration) | ✅ | Raven/Vixen.Raven | ANTLR `.g4` kept as a differential oracle |
| Green/red tree from `Syntax.xml` (79 + 13 node types), trivia, spans | ✅ | Raven/Vixen.Raven | |
| Semantic phase, target-independent IR | ✅ | Raven/Vixen.Raven | |
| GLSL + SPIR-V emitters, `spirv-val` on every module | ✅ | Raven/Vixen.Raven | Golden `spirv-dis` snapshots |
| Incremental reparse with green-node reuse | ✅ | Core/Vixen.Core.Syntax | |
| `protocol`, shader inheritance, compile-time generics, `compose` | ✅ | Raven/Vixen.Raven | |
| Atomics (8 on `int`/`uint`) | ✅ | Raven/Vixen.Raven | Landed for the VFX compaction path |
| `.rvnlib` / `.rvnfx` artefact formats | ✅ | Raven/Vixen.Raven | |
| `Raven/Library` — Core, Shading, Geometry, Material, Pipeline, PostFx, Ui, Vfx | ✅ | Raven/Library | Every shader reaches both backends under `glslc` and `spirv-val` |
| **String interpolation** | ⬜ | — | Needs lexer modes; nothing shipped uses it |
| **Workgroup-shared memory** | ⬜ | — | ⛔ blocks GPU sorting and per-workgroup compaction counters |
| `Vixen.Raven.Transpile` (SPIRV-Cross → ESSL/HLSL/MSL/WGSL) | ⬜ | — | ADR-012 says SPIRV-Cross owns these targets. **No SPIRV-Cross package in `Directory.Packages.props`** |
| Cross-compilation test pass | ⬜ | — | Not started |
| Nuke `CompileShaderLibrary`; SPDX enforcement in `CheckFormat` | ⬜ | — | SPDX is a real gap, not a closed item |
| Numeric BRDF gate (GPU compute readback vs. C# port) | ⬜ | — | Unblocked: **K2** landed the writable resource and the readback. What is owed is the gate itself |
| Per-backend layout gate (reflection offsets vs. GPU readback) | ⛔ | — | Needs a device |
| Negative-diagnostic fixture pairs | 🟡 | Raven/Vixen.Raven.Tests | Most ids have a trigger; few have the negative |
| Stream interpolation control; per-module flat IR namespace | ⬜ | — | Recorded in doc 07 §Streams and §D |
| `Vixen.Shaders` — typed parameter/permutation keys, std140 writers | ✅ | Core/Vixen.Shaders | Generated from Raven reflection |
| Effect system + 3 cache tiers (`EffectStore`, disk, remote) | ✅ | Core/Vixen.Shaders | |
| `Tools/Vixen.ShaderCompiler` (`PermutationClosure`, `EffectBundleBuilder`) | ✅ | Tools/Vixen.ShaderCompiler | Zero-runtime-compilation criterion asserted by test |
| `Tools/Vixen.ShaderCompilerService` | ✅ | Tools/Vixen.ShaderCompilerService | |

## 1.9 Rendering, video and XR

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `RenderSystem`, `RenderObject`/`RenderNode`, features, views, stages, sort modes | ✅ | Core/Vixen.Rendering | |
| `VisibilityGroup` (job-parallel) + `GpuVisibilityGroup` (Hi-Z, indirect args) | ✅ | Core/Vixen.Rendering | Falls back where it cannot run |
| Mesh, transform, skinning, instancing, material, lighting, shadow-caster features | ✅ | Core/Vixen.Rendering | Roadmap §Phase 5 still lists these "open"; the code says otherwise |
| **Two-phase occlusion culling** (`GpuVisibilityGroup.TwoPhase`, the `Late` permutation of `Culling.rvn`) | ✅ | Core/Vixen.Rendering | Removes the frame of staleness. The late pass writes a **difference**, not an answer — the union would draw every visible object twice. Needs the readback off; `LatePhaseRan` says which happened |
| Compacted draws | ⬜ | — | ⛔ wants bindless materials first |
| `GraphicsCompositor` as an asset, resolvable by address | ✅ | Core/Vixen.Rendering | Asserted in `Vixen.Assets.Tests` |
| Materials — composable feature tree, 2 workflows, 7 shading models, both layering forms | ✅ | Core/Vixen.Rendering | Every combination through `glslc` + `spirv-val` |
| Transmission / refraction | ⬜ | — | Needs the scene colour or an environment sample — a pass concern, not a lobe |
| Bindless material textures (a feature that samples needs a binding index) | ⬜ | — | ⛔ the same gap as the compositor's authored nodes |
| Lighting — directional/point/spot/tube/rect, clustered binning, IBL, reflection probes | ✅ | Core/Vixen.Rendering | `EnvironmentBaker` + `SphericalHarmonics` on the CPU |
| **Light probes** (tetrahedral interpolation) | 🟡 | Core/Vixen.Core.Mathematics · Core/Vixen.Rendering | `ExactPredicates` + `DelaunayTetrahedralization` + `LightProbeVolume`. The CPU half is done and the GPU half is not: nothing uploads a volume or samples one in a shader. The row used to read ⛔ *written, found wrong by its own tests, withdrawn* |
| Per-object reflection probe selection | ⬜ | — | ⛔ needs the binding-plan work |
| Shadows — CSM, cube, spot, atlas, static caching, PCF/PCSS | ✅ | Core/Vixen.Rendering | |
| Punctual shadow caching | ⬜ | — | Only the directional cascades are cached |
| Blend shapes | ⬜ | — | |
| `Vixen.Rendering.PostFx` — TAA, FXAA, sharpen, AO, fog, outline, vignette, chromatic aberration, grain, bloom, tonemap | ✅ | Core/Vixen.Rendering.PostFx | `ISceneRendererFactory` makes a game's own effect a first-class node |
| SMAA, MSAA resolve, full GTAO, SSR, DoF, motion blur | ⬜ | — | Each needs a shader that does not exist yet |
| Grading LUT as an **asset** | ⬜ | — | Needs a `.cube` importer |
| `AutoExposure.rvn` wiring | ⬜ | — | Unblocked: the compute node, the histogram's upload and the exposure's readback all exist (**K2**). What is owed is the chain |
| Deferred pipeline — GBuffer, shading-model dispatch, forward routing, decals | ⬜ | — | Phase 10; cut-list #6 |
| Volumetric fog, contact shadows, light shafts, SSS blur, upscaler + FSR1 | ⬜ | — | Phase 10 |
| Mesh shaders / meshlet culling behind capability flags | ⬜ | — | Phase 10 |
| Golden-image fixture suite | ✅ | Platform/Vixen.Graphics.Golden.Tests | One fixture per state bit a backend can silently ignore, plus `ClusteredShadingDeviceTests` — one composed Forward+ frame. It caught two engine bugs **nothing but a picture could see**: a composed material parameter's qualified name depending on lowering order, and one Raven struct used in both a uniform block and a storage buffer collapsing to one MSL type |
| `Samples/03-PbrShowcase` | 🟡 | Samples/03-PbrShowcase | Ambient is the analytic constant-radiance environment; nothing casts a shadow — both need content the importer does not produce |
| **`Vixen.Video`** — managed WebM demuxer, codec seam, player with an audio-driven clock, YUV planes + conversion coefficients | ✅ | Core/Vixen.Video | 144 tests. Doc 06 § Other renderables. Landed far ahead of its Phase 10 slot |
| **`Vixen.Video.Codecs`** (Opus over loose WebM packets) | ✅ | Core/Vixen.Video.Codecs | Split so a game with an uncompressed logo sting links no Concentus |
| **`Vixen.Video.Rendering`** — one pipeline, three plane bindings, `VideoRenderTarget` | ✅ | Core/Vixen.Video.Rendering | Converts to an ordinary colour texture, which is what the UI image command binds |
| Video: MP4; a **material** (video lit on a mesh); frame-accurate seek; audio-track choice; subtitles; 10-bit / BT.2020 | ⬜ | — | MP4 is additive behind `IVideoStreamDecoder`; the material is `MaterialRenderFeature`'s and Raven's |
| **`Vixen.Xr`** — session state machine, per-eye poses, asymmetric projections, runtime-owned swapchains, action input, ECS bridge, simulated headset | ✅ | Core/Vixen.Xr | All of it runs on a machine with no headset |
| **`Vixen.Xr.OpenXR`** — the three desktops and Android | ✅ | Platform/Vixen.Xr.OpenXR | The Vulkan instance/device/GPU are the *runtime's* choice, and the API shape makes that order impossible to get wrong |
| XR: a render feature; single-pass multiview; hand/eye tracking, passthrough, anchors | ⬜ | — | Multiview's hook is `XrSwapchainDescription.ArrayLayers`; the `VK_KHR_multiview` half is `Vixen.Graphics`'s. Two passes work today |
| Shader reflection: vertex input locations + push-constant stage coverage | ✅ | Core/Vixen.Shaders | A binding index is declaration order, so a literal in C# survives a renumbering and a generated constant does not — and the failure is silent CPU fallback, every frame |

## 1.10 Gameplay subsystems

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Physics` (Jolt 2.22.0) — bodies, shapes, constraints, character, queries, triggers, layers, CCD, ECS sync, debug draw, determinism gate | ✅ | Core/Vixen.Physics | Two binding bugs pinned by tests |
| Physics on **iOS** | ⛔ | — | `JoltPhysics.Native` ships no iOS slice; needs a static `libjoltc.a` pinned the way MoltenVK is |
| Per-pair collision suppression; vehicles, ragdolls, soft bodies; double precision | ⬜ | — | Out of Phase 8 scope |
| `Vixen.Audio` — software mixer, buses, sends, sidechains, 14 effects, streaming, ECS, events, parameters, interactive music, capture, HRTF, loudness | ✅ | Core/Vixen.Audio | Far beyond the line the roadmap asked for. "Nothing structural is owed" |
| `Vixen.Audio.Codecs` (Vorbis, Opus, ADPCM) | ✅ | Core/Vixen.Audio.Codecs | Pure managed, rooted in the AOT probe |
| `Vixen.Audio.Physics` (Jolt occlusion), reverb zones | ✅ | Core/Vixen.Audio.Physics | Zones need no physics; occlusion does |
| Backends: OpenAL ✅, WebAudio ✅ | ✅ | Platform/Vixen.Audio.Backend.* | |
| Measured HRTF sets | ⬜ | — | Structural model ships; measured sets are content |
| `Vixen.Animation` — skeletal playback, 1D/2D blend trees, layers + masks, state machine, two-bone/look-at/foot IK, root motion, events, GPU skinning, key reduction | ✅ | Core/Vixen.Animation | Benchmarked; `ParallelThreshold` = 32 from measurement |
| `Vixen.Editor.AnimationGraph` | ⬜ | — | Cut-list #7 — a code-driven state machine ships first |
| Ragdoll integration | ⬜ | — | Lands with the animation/physics join |
| `Vixen.Input` — devices, `InputControlPath`, actions, maps, processors, interactions, `.vxinput`, generated accessors, rebinding | ✅ | Core/Vixen.Input | |
| Action-map editor + input debug panel | ⬜ | — | Was ⛔ on the editor shell; **the shell now exists, so this is unblocked** |
| Sensors, pen/stylus, MIDI, custom HID | ⛔ | — | `Vixen.Platform` reports none of the four |
| `Vixen.Navigation` — voxel bake, tiled mesh, A\* + funnel, crowd + RVO, off-mesh links, watershed, height detail, dynamic obstacles, sliced/jobbed queries | ✅ | Core/Vixen.Navigation | Managed, no Recast/Detour binding. 40 tests, zero steady-state allocation |
| Navmesh baked from a **compiled scene** | ⛔ | — | Bakes from an authored placement list today; needs doc 08's scene compiler |
| `Samples/05-PlatformerGame` | ⬜ | — | Phase 8's exit criterion |
| `Vixen.Vfx` — SoA storage, compiled graph, deterministic RNG, CPU jobs, `ParticleRenderFeature`, compute-shader emitter | 🟡 | Core/Vixen.Vfx | 34 tests, zero-alloc frame |
| VFX **GPU dispatch** (upload, readback, reaping, GPU sort, indirect draw) | ⛔ | — | Nothing has uploaded a buffer or read one back; the CPU/GPU agreement criterion needs a device |
| Mesh/ribbon/light renderers, custom attributes, force-field/curl-noise/collision/sub-emitter/trail updaters | ⬜ | — | |
| Second view of one effect (shadow/reflection passes) | ⬜ | — | Expansion is CPU and once per view; the GPU path is the fix |

## 1.11 Editor

> **Phase 6's exit sentence is met.** The editor opens a project, imports assets, builds content,
> edits a scene, saves, and runs the game — entirely in `Vixen.Ui`, with no other toolkit anywhere in
> the dependency graph. What the sentence does not cover and Phase 6 still lists: the asset editors,
> the profiler and debugger, plugin loading, the automation harness, and `PublishEditor`. The
> editor-shell performance bar is unmeasured.
>
> ⚠ **The viewport draws lines, not meshes.** A scene of empties looks right; a scene with a model in
> it does not show the model. That wants a material system wired to an editor viewport.

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Editor.Core` — `IEditorCommand`, `CommandStack` with merging/transactions/clean-marking, document model, `EditorProject`, `Selection<T>`, settings assets | ✅ | Editor/Vixen.Editor.Core | 48 tests incl. randomised do/undo/redo |
| `Vixen.Editor.Ui` — shell, docking, command registry, menus/toolbars/context/palette as views, theming, notifications, background tasks, localisation | ✅ | Editor/Vixen.Editor.Ui | ~4 100 lines |
| Keybinding editor UI; notification panel; `Strings.Resource` generation | ⬜ | — | Models exist, views do not |
| `Vixen.Editor.Inspector` — generated drawers, attribute set, multi-object editing, `ref` accessors | ✅ | Editor/Vixen.Editor.Inspector | ~2 800 lines |
| Nested-object drawer / nested struct editing | ✅ | Core/Vixen.Ui.Controls.Advanced | The `ref`-accessor argument was wrong: set the leaf, then write each *owner* into its own owner innermost-first. `PropertyRow` carries a path where it used to carry a member |
| Multi-edit of a curve; the asset picker's browser | ⬜ | — | Browser belongs to the shell |
| `Vixen.Editor.SceneView` — viewport, gizmos, picking, camera nav, grid, outline, debug view modes, `SceneDocument`, `.vxscene` | ✅ | Editor/Vixen.Editor.SceneView | ~4 900 lines |
| Undoable entity create/destroy | ⛔ | — | `Vixen.Ecs` cannot reserve a handle, so redo would hand back a different one |
| Undoable entity **create / delete / rename**, handle surviving a delete-and-undo | ✅ | Editor/Vixen.Editor.SceneView | Five things come back: the handle (`TryRecreate`), the components (a scratch world — the only thing that can hold an arbitrary unconstrained struct is a chunk), the name, the stable id, and its place among its siblings. A delete takes the whole subtree |
| Undoable **reparenting** | ⬜ | — | The primitive (`SetParentAfter`) exists; the command does not. Hierarchy drag-and-drop is not wired either |
| Clicking in the viewport to select | ⬜ | — | Picking needs an id target; the gizmo can be dragged and what it drags comes from the hierarchy |
| `ISceneWriter` / `SceneFileWriter` / scene save | ✅ | Editor/Vixen.Editor.SceneView | |
| Play-in-editor, **in-process** (`WorldSnapshot` + `PlayModeController`, leak detection) | ✅ | Editor/Vixen.Editor.SceneView | Restore clears first; the selection is translated through the handle table |
| Play-in-editor, **out-of-process** (`PlayerSessions`) | ✅ | Editor/Vixen.Editor.SceneView | Ports assigned by the set; a hung player is killed. **Supersedes the roadmap's "genuinely blocked" note in Phase 9** |
| `Vixen.Editor.App` — platform, window, device, frame loop, `--frames N` | ✅ | Editor/Vixen.Editor.App | Panels are untested at the panel level (the app has no test project); the models under them are |
| **Project browser** (`AssetTree` + `ProjectBrowser`) — the asset database as a searchable tree over the real `Assets/` | ✅ | Editor/Vixen.Editor.Core, Editor/Vixen.Editor.App | ⚠ Not watched: a file added outside the editor appears on `Ctrl+R`. A watcher that missed half the events while claiming to be live would be worse |
| **Import Assets / Build Content from the editor** (`ContentPipeline` on the background task manager) | ✅ | Editor/Vixen.Editor.App | The same call the CLI makes, so the two cannot produce different output for one project |
| Redraw-on-change (it redraws every frame today) | ⬜ | — | Every animation, toast expiry and task progress would have to say so, and one that forgets freezes a progress bar |
| Plugin loading (`Vixen.Editor.Plugin`, `AssemblyLoadContext`) | ⬜ | — | The reason `Vixen.Editor.App` is not NativeAOT |
| "Open project…" file dialog | ⬜ | — | Unblocked: `platform.Dialogs` is the OS's own picker on all three desktops (K3). What is left is the editor calling it |
| Asset editors: texture, model, material, prefab, shader, UI, addressable groups, compositor | ⬜ | — | Shell + inspector exist, so these are unblocked |
| Scene editor (as an asset editor) | ⛔ | — | Needs the scene format |
| `Vixen.Editor.Profiler` (frame graph, frame debugger, memory view) | ⬜ | — | ⛔ partly on `Vixen.Core.Diagnostics`' GPU/memory tracks |
| `Vixen.Editor.Debugger` (remote inspector, live property editing) | ⬜ | — | ⛔ needs the remote log/telemetry sink |
| Editor network panel | ⬜ | — | Everything it would show is already public in `BandwidthLedger` / `SnapshotInspector` |
| Editor UI automation harness | ✅ | Core/Vixen.Ui.Testing | Golden **screenshots** for editor layouts not started |
| `PublishEditor`, signing, notarisation, `.dmg`/AppImage/MSI | ⬜ | — | |
| `Vixen.Editor.NodeGraph` — model, generated registry, compiler, port typing | ✅ | Editor/Vixen.Editor.NodeGraph | No UI, and none needed to check it |
| `NodeGraphView` (pan/zoom/marquee/wires/minimap/search-to-create) | ✅ | Editor/Vixen.Editor.NodeGraph | Over `NodeCanvas`. A one-way projection, rebuilt per structural change; drags write positions in place |
| Sub-graphs; undo commands; auto-layout; drag-from-port; preview layer | ✅ | Editor/Vixen.Editor.NodeGraph | Sub-graphs are **inlined**, not called. The layer draws a colour *or* a render target |
| A shader-graph renderer that *fills* a preview thumbnail | ⬜ | — | Unblocked. Compile one node's sub-expression, run it over a quad, keep the target alive across edits. `.ShaderGraph`'s, not the framework's |
| Selectable wires; in-place sticky-note editing; inlined-node → source-node map | ⬜ | — | The last is what lets a diagnostic inside a sub-graph name a node the author can select |
| Raven-span → node diagnostics mapping | ⬜ | — | Needs the emitter to record spans as it writes |
| `Vixen.Editor.ShaderGraph` — node library, `DynamicVector` typing, Raven emission | ✅ | Editor/Vixen.Editor.ShaderGraph | Unlit, Sprite, PBR masters |
| Procedural nodes, custom-code node, Post + UI masters, preview thumbnails | ⬜ | — | |
| `Vixen.Editor.VfxGraph` — node library + dual-target compilation | ✅ | Editor/Vixen.Editor.VfxGraph | One method produces both the CPU graph and the compute shader |
| Operator nodes, blocks for the remaining opcodes, sub-emitters/trails, live preview | ⬜ | — | |

## 1.12 Networking

Phase 9 is the most complete phase in the repository — **all five exit criteria are met**.

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Net` vocabulary, `ITransport`, `TransportConformance` suite | ✅ | Core/Vixen.Net | Time is a parameter, nothing delivered outside `Poll` |
| Transports: `Local` ✅, `NetworkSimulation` ✅, `Udp` ✅, `WebSocket` ✅, `Composite` ✅ | ✅ | Core/Vixen.Net.Transport.* | UDP has cookie-based connect, fragmentation, all four channels |
| Transport: **`Relay`** | ⛔ | — | A relay *client* with no relay *server* is untestable. Needs a scope decision (host one? in-box or addon?) before code. Transport **fallback** belongs with it |
| UDP adaptive congestion control, ack piggybacking, path MTU, DTLS | ⬜ | — | There is a cap on unacknowledged datagrams, which bounds memory |
| `Tick`, `TickManager` (drift correction), `PacketWriter`/`Reader` (never throws) | ✅ | Core/Vixen.Net | |
| `NetworkSession` — handshake, clock, players, reconnect window, host/offline modes | ✅ | Core/Vixen.Net | |
| Bandwidth budgeting and priority shedding at the session layer | ⬜ | — | The writer's overflow flag is what it would build on |
| RPC — generated senders, six pre-dispatch checks, ownership, rate limiting, manifest hash | ✅ | Core/Vixen.Net + Generators | |
| Awaitable RPC (`CallAsync<T>` → `Task<T>`) | ✅ | Core/Vixen.Net | Doc 16 said `ValueTask<T>` and was corrected |
| Broadcasts (`IBroadcast<TSelf>`) | ✅ | Core/Vixen.Net | |
| `NetworkRules` policy (stricter-wins composition) | ✅ | Core/Vixen.Net | |
| `.vxnetrules` **asset** | ⛔ | — | Importer + serialised form + per-prefab reference — the asset pipeline's half |
| Replication — bit packing, `[Quantize]`, capture-once/copy-many, two-stage filter, ack'd baselines, shedding | ✅ | Core/Vixen.Net | |
| Field-level delta (`DeltaCodec`) | ✅ | Core/Vixen.Net | 19.2 → 9.7 kbit/s a client on `Samples/08` |
| `SyncVar<T>` / `SyncList<T>` / `NetworkModule` | ✅ | Core/Vixen.Net.Engine | |
| System that marks dirty modules once a frame | ⬜ | — | `MarkChanged()` is called by hand; wants the engine scheduler |
| Interest management — resolver chain, rules, `InterestGrid` (a source, not a filter), hysteresis, `NetworkLOD` rate | ✅ | Core/Vixen.Net | Two corrections to doc 16 recorded |
| Team / room / fog-of-war rules; resolver **composition** | ⬜ | — | Deliberate: each is a game's idea. Composition is needed to chain scene + distance |
| Motion — `SnapshotBuffer`, clamped extrapolation, `NetworkTransform`, owner smoothing | ✅ | Core/Vixen.Net(.Engine) | 88 bits against 224 in memory |
| Per-axis enable, parent-relative replication | ⬜ | — | |
| Networked rigid bodies, authority as a `NetworkRules` audience | ✅ | Core/Vixen.Net.Physics | Correction via velocity, not teleport |
| Lag compensation (pose ring, rewind scope, `ClampFor`) | ✅ | Core/Vixen.Net.Physics | |
| Hit-claim message; per-bone rewind; rewind cost budget; drawing it | ⬜ | — | Per-bone wants animation pose history |
| Networked animation (parameters reliable, state unreliable) + `NetworkBones` | ✅ | Core/Vixen.Net.Animation | |
| Per-bone quantisation by importance; pose interpolation | ⬜ | — | |
| Networked audio + `OwnershipClaim` | ✅ | Core/Vixen.Net.Audio | |
| Spawn / scenes / instances (`NetworkSpawner`, prefab id = hash of **address**) | ✅ | Core/Vixen.Net.Engine | Corrects doc 16's "asset GUID" |
| Prefab registry filled from the content catalog by label | ⬜ | — | Filled by hand at start-up today |
| Scene load/unload as session messages; client-requested spawns; `OnOwnerDisconnect` → `Despawn` | ⬜ | — | |
| Security — validation, rate limits, closed-set deserialization, handshake hashes, `Vixen.Net.Fuzz` | ✅ | Core/Vixen.Net.Fuzz | 12 targets, 3 oracles, ~11 M cases per build in ~7 s |
| `SharpFuzz` with real instrumentation; structure-aware mutation | ⬜ | — | Targets are already `(ReadOnlySpan<byte>) -> outcome` |
| Generated encoders pinned end to end in the wire corpus | ⬜ | — | Source and primitives are pinned; the composition is not |
| Client-side prediction — input log, jitter buffer, rollback, tick-lead control, smoothing | ✅ | Core/Vixen.Net(.Engine) | |
| Predicted spawns; running the scheduler's fixed-step group | ⬜ | — | Needs a client-allocatable id space; scheduler must be re-entrant |
| Metrics over OpenTelemetry (`NetworkMetrics` + `Vixen.Net.Telemetry`, OTLP push) | ✅ | Core/Vixen.Net.Telemetry | Split so an offline game links no protobuf serializer |
| Traces (span per handshake); log bridge to OTLP; Grafana dashboard; client-side metrics | ⬜ | — | |
| Diagnostics — `BandwidthLedger` (type/field/RPC/connection), `SnapshotInspector` | ✅ | Core/Vixen.Net | Per-field costs a subtraction inside the delta encoder |
| RTT/jitter/loss graphs over time | ⬜ | — | Wants a ring of samples, not running totals |
| Dedicated-server variant boot path | ✅ | Tools/Vixen.App | `BuildVariant.Server` + headless platform + Null backend |
| Container image; server content profile | ⬜ | — | CI's and the asset pipeline's, not networking's |
| **Out-of-process play mode** | ✅ | Editor/Vixen.Editor.SceneView | `PlayerSessions` — the roadmap's "genuinely blocked on editor infrastructure" note is stale. The *remote inspector* it would attach to is still ⬜ |
| `ReplicationChannel` helper (ack has no transport of its own) | ⬜ | — | Every game will otherwise write the same six lines |
| `Samples/08-Multiplayer`, `09-NetworkSoak`, `10-VoiceChat` | ✅ | Samples/ | Soak: 30 min, 5 000 entities, 100 connections, 75.2 kbit/s, p99 tick 2.4 ms, 3 Gen0 |

## 1.13 Samples

| Sample | Status | Note |
|---|---|---|
| `01-HelloTriangle` (+ `.Android`, `.iOS`) | 🟡 | Verified macOS, iOS Simulator, Android emulator. Windows/Linux and physical devices owed |
| `02-HelloUi` | ✅ | Exit criteria met; no CI leg runs it; browser run is Phase 10's |
| `03-PbrShowcase` | 🟡 | Analytic ambient, no shadows — both need importer content |
| `04-EcsStressTest` | ✅ | |
| `05-PlatformerGame` | ⬜ | Phase 8's exit criterion; ⛔ on iOS by the Jolt slice |
| `06-CanvasStress` | ⬜ | P2, cut-list #4 — the editor is the application-platform proof |
| `07-AddressablesRemote` | ✅ | 144.6 KB cold → 48.6 KB update, asserted |
| `08-Multiplayer`, `09-NetworkSoak`, `10-VoiceChat` | ✅ | |
| `11-VideoPlayback` | ✅ | The half of video only a running frame exercises: three planes reaching the GPU at their own sizes, in order |

## 1.14 Documentation and release (Phase 11)

| Item | Status |
|---|---|
| `docs/plan/` design record (19 documents) | ✅ |
| `docs/manual/` — building a game and a server, diagnostic codes, log events | 🟡 (3 pages) |
| `docs/rhi-backend-mapping.md` | ✅ |
| DocFX API reference | ⬜ |
| Manual: getting started, per-subsystem guides, UI tutorial, Raven reference, Unity migration | ⬜ |
| 12+ runnable samples | 🟡 (11 exist) |
| `dotnet new` templates verified on six targets | ⬜ |
| `PublicAPI.Shipped.txt` freeze + API review | 🟡 (baselines exist and are gated; the freeze is folding `Unshipped` into `Shipped` at the release, and the review pass is the reading nobody has done yet) |
| Release automation (tag → signed builds + NuGet + GitHub Release) | ⬜ |
| 24 h editor / 24 h game soak | ⬜ |
| Public triage process + compatibility policy | ⬜ |
| Third-party attribution manifest / `docs/manual/third-party.md` | ⬜ |
| Per-file SPDX enforcement in `CheckFormat` | ⬜ |

---

# Part 2 — Library inventory

## 2.1 Referenced and in use

Ground truth is [`Directory.Packages.props`](../Directory.Packages.props); the plan of record is
[`01-technology-decisions.md`](plan/01-technology-decisions.md).

| Package | Version | Status | Used by | Note |
|---|---|---|---|---|
| `Silk.NET.Core` | 2.23.0 | ✅ | `Vixen.Graphics` | |
| `Silk.NET.Vulkan` + `.Extensions.KHR` / `.EXT` | 2.23.0 | ✅ | `Vixen.Graphics.Vulkan` | Primary backend. `Vk.GetApi()` is never called (R11) |
| `Silk.NET.SDL` | 2.23.0 | ✅ | `Vixen.Platform.Desktop` | **SDL 2, not SDL 3** — doc 01 corrected. Bindings only; `libSDL2` comes from the system or `Platform.Native` |
| `Silk.NET.OpenGL` | 2.23.0 | ✅ | `Vixen.Graphics.OpenGL` | Desktop GL 4.5 core |
| `Silk.NET.OpenGLES` | 2.23.0 | ✅ | `Vixen.Graphics.OpenGL` | The GLES 3.0/3.2 and WebGL2 profiles. A second package because libGL and libGLESv2 are two libraries. **No `Silk.NET.EGL` exists for 2.x** — it stops at 1.9.0 — so `NativeEglApi` binds EGL itself |
| `Silk.NET.WebGPU` | 2.23.0 | ✅ | `Vixen.Graphics.WebGPU` | ⚠ Matches **no** wgpu-native release; the pin carries a refusal and a struct override |
| `Silk.NET.OpenXR` + `.Extensions.KHR` | 2.23.0 | ✅ | `Vixen.Xr.OpenXR` | Doc 14 lists VR/XR as a stretch; it landed early |
| `Silk.NET.OpenAL` + `.Extensions.*` | 2.23.0 | ✅ | `Vixen.Audio.Backend.OpenAL` | |
| `Silk.NET.OpenAL.Soft.Native` | 1.23.1 | ✅ | `Vixen.Audio.Backend.OpenAL` | OpenAL Soft's own version, unrelated to Silk.NET's |
| `Silk.NET.Assimp` | 2.23.0 | ✅ | `Vixen.Editor.Assets` | Import-time only, never in a runtime assembly |
| `JoltPhysicsSharp` | 2.22.0 | ✅ | `Vixen.Physics` | No iOS slice — see §1.10 |
| `StbImageSharp` | 2.30.15 | ✅ | `Vixen.Editor.Assets` | **Replaced ImageSharp.** Public domain; gained Radiance HDR, lost `.exr`/`.tif`/`.webp` |
| `ExCSS` | 4.3.2 | ✅ | `Vixen.Ui.Styling` | Spiked first. Does not parse `@layer`; normalises through `var()` inconsistently; expands `transition`/`border-*` shorthands |
| `HarfBuzzSharp` (+ macOS/Linux/Win32 natives) | 14.2.1.1 | ✅ | `Vixen.Ui.Text` | Spiked first. Exposes **no glyph outlines** — Vixen reads `glyf`/`CFF` itself |
| `K4os.Compression.LZ4` | 1.3.8 | ✅ | `Vixen.Core.Serialization` | |
| `ZstdSharp.Port` | 0.8.8 | ✅ | `Vixen.Core.Serialization` | Pure managed → works on WASM |
| `System.IO.Hashing` | 10.0.10 | ✅ | `Vixen.Core` | XxHash128 |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.10 | ✅ | `Vixen.Core.Diagnostics` | Interface only |
| `NVorbis` | 0.10.5 | ✅ | `Vixen.Audio.Codecs` | |
| `Concentus` | 2.2.2 | ✅ | `Vixen.Audio.Codecs`, `Vixen.Video.Codecs` | Pinned to the **managed** path — the native libopus fallback ignored its bitrate |
| `OpenTelemetry` + OTLP/Console exporters + Runtime instrumentation | 1.17.0 | ✅ | `Vixen.Net.Telemetry` | Added beyond doc 01's register |
| `YamlDotNet` | 18.1.0 | ✅ | `Vixen.Core.Yaml`, `Vixen.Editor.Core` | |
| `Antlr4.Runtime` / `Antlr4.CodeGenerator` | 4.6.6 | 🟡 | Raven **tests only** | Kept as a differential oracle after the Phase 5b migration |
| `Microsoft.CodeAnalysis.CSharp` / `.Analyzers` | 4.11.0 / 3.3.4 | ✅ | every `*.Generators` project, and `Vixen.Core.IO.Analyzers` | |
| `Nuke.Common` | 10.1.0 | ✅ | `build/_build.csproj` | |
| `System.CommandLine` | 2.0.10 | ✅ | `Vixen.Cli` and the tools | |
| `BenchmarkDotNet` | 0.15.8 | ✅ | `Benchmarks/*` | |
| `xunit.v3` + `runner.visualstudio` + `Microsoft.NET.Test.Sdk` | 3.2.2 / 3.1.5 / 18.8.1 | ✅ | every `*.Tests` | |
| `CsCheck` | 4.7.0 | ✅ | property-based suites | |

## 2.2 Planned, not yet referenced

| Package | Planned for | Status | Blocks |
|---|---|---|---|
| `Silk.NET.SPIRV.Cross.Native` | `Vixen.Raven.Transpile` | ⬜ | HLSL/MSL/WGSL output (ADR-012) |
| `Silk.NET.Shaderc` / `.Native` | Raven's differential oracle | ⬜ | The `glslc`-vs-SPIR-V oracle is described as running; the package is not in the register |
| `Silk.NET.Direct3D.Compilers` | D3D12 backend | ✂️ | Postponed with the backend |
| `Silk.NET.Maths` | interop shim | ⬜ | Never needed — ADR-003 types carry their own conversions |
| `ZLogger` 2.5.10 | `Vixen.Core.Diagnostics` file/console sink | ⬜ | ADR-008's sink half. The ring-buffer sink is engine-owned and built |
| `NSubstitute` 6.0.0, `Shouldly` 4.3.0 | test stack | ⬜ | Listed in doc 12; the props file deliberately omits unused versions |
| `Pfim` | DDS/TGA decode | ⬜ | `.dds` import |
| `SharpFuzz` | `Vixen.Net.Fuzz` | ⬜ | Instrumented fuzzing alongside the build-time harness |
| `astcenc` (native) | `Vixen.Core.Imaging` | ⬜ | ASTC encoding — mobile texture budgets |
| `ispc_texcomp` (native) | `Vixen.Core.Imaging` | ⬜ | Full-quality BC7/BC6H |

## 2.3 Native dependencies

| Dependency | Status | Where |
|---|---|---|
| MoltenVK 1.4.2 (`ios-arm64`, static, 431 entry points exported) | ✅ | `build/native-dependencies.json` + `Vixen.Platform.Native/build/MoltenVK.targets` |
| wgpu-native (pinned + checksummed) | ✅ | `build/native-dependencies.json` |
| Vulkan Loader 1.4.350 + validation layers | ✅ | Developer/CI install; `VulkanLoader` probes Homebrew's path |
| lavapipe (Mesa software Vulkan) | ✅ | Linux CI leg |
| `libSDL2` | 🟡 | From the system; not in the acquisition manifest |
| OpenAL Soft | ✅ | Via `Silk.NET.OpenAL.Soft.Native` |
| HarfBuzz | ✅ | Via `HarfBuzzSharp.NativeAssets.*` |
| Jolt (`JoltPhysics.Native`) | 🟡 | No iOS slice |
| `astcenc`, `ispc_texcomp`, and R10's remaining three | ⬜ | The schema exists; the entries do not |

## 2.4 Rejected / reference-only (unchanged)

`Arch` (ADR-004), `ru-ace/Flexbox` + Yoga (ADR-006), `SignalsDotnet` (ADR-007), Stride, PurrNet —
cloned into `references/`, no code copied. `BepuPhysics`, `Veldrid`, `Vortice`, `SharpDX`, `Avalonia`,
WPF, ImGui, `Mono.Cecil`/`dnlib`/`Fody`/`ILRepack` (ADR-002, enforced by `CheckArchitecture`), `R3` /
`System.Reactive` (ADR-007), `SixLabors.ImageSharp` (ADR-015 — build-breaking licence gate, swapped).
Recast/Detour is reference material only; `Vixen.Navigation` re-derives and links nothing.

---

# Part 3 — Dependency tree for the unimplemented work

Read top-down: everything in a wave has **no unmet dependency on anything else in the same wave**, so
a wave is a set of tracks that can run in parallel. An arrow `A → B` means B cannot start (or cannot
be finished honestly) until A lands.

## 3.1 The four keystones

Four items unblock disproportionately more than anything else. If work is being scheduled, these go
first. **K4 has since landed** and is kept below with what it unblocked struck through, because a
dependency tree with the resolved edges deleted reads as though they were never there.

```
K1  Compiled scene + prefab content                                          ✅ built
    (doc 08 SceneCompiler · a [DataContract] runtime scene/prefab asset · per-component serialisers)
    SceneAsset/PrefabAsset/SceneContent in Vixen.Engine.Scenes — archetype-ordered blocks,
    a column per component, SceneComponentRegistry turning a contract name into a chunk write.
    SceneImporter compiles .vxscene/.vxprefab; the authored format grew a tagged component list
    and moved to Vixen.Editor.Core so the viewport and the importer read one model.
    The seven below are unblocked and still owed.
    │
    ├──→ Vixen.Ecs world serialisation
    ├──→ Scene + prefab asset editors (loading a compiled scene, not just an authored one)
    ├──→ Prefab overrides + nested prefabs            (risk R7)
    ├──→ Navigation: bake placements from a scene
    ├──→ Networking: scene load/unload as session messages
    ├──→ Networking: scene-placed baked index
    └──→ Samples/05-PlatformerGame                    (needs a shipped level)

K2  Compute-node in the compositor + GPU buffer upload/readback              ✅ BUILT
    ComputeRenderer declares what a dispatch reads and writes and fills its own uniform
    block; BufferUploadRenderer and BufferReadbackRenderer are the two copies at either
    end of it, as nodes, so the edge that orders them is the graph's and not a host's.
    Authored as !Compute, !Upload and !Readback. What is left is the five things that
    were waiting for it — none of which is blocked any more.
    │
    ├──→ Vfx GPU dispatch → reaping → GPU sort → indirect draw → 2nd-view particles
    ├──→ AutoExposure.rvn wiring
    ├──→ Raven numeric BRDF gate (compute readback vs. the C# port)
    ├──→ Raven per-backend layout gate
    └──→ Phase 7 exit criterion (CPU/GPU VFX agreement)

K3  Per-OS platform assemblies                                            ✅ BUILT
    (Vixen.Platform.Windows / .Linux / .MacOS, reached through IPlatformSupplement)
    │
    ├──✅ File pickers        ──→ Editor "open project…" ──→ a usable editor for a stranger
    ├──✅ Clipboard images and custom formats
    ├──✅ Thread affinity      (closes Vixen.Core.Threading's last deferral on Windows and
    │                          Linux; macOS answers "no" and is right to — see its README)
    ├──✅ Thermal state        (closes the quality-scaling policy loop on macOS and Linux;
    │                          Windows has no user-mode API for it and says so)
    └──→ Floating dock groups in real OS windows (with multi-window + DPI)

K4  Silk.NET.OpenGLES + an EGL context                      ✅ BUILT
    (SilkGlesApi over Silk.NET.OpenGLES · EglContext over a hand-loaded libEGL, because
     there is no Silk.NET.EGL for Silk.NET 2 · nothing above IGlApi changed)
    │
    ├──→ GLES 3.0/3.2 actually runs      ──→ Android GLES fallback + deny-list: now a head
    │                                        choosing GL over Vulkan, not a missing binding
    ├──→ WebGL2 has its binding          ──→ Samples/02 in a browser needs the head, not this
    └──→ Phase 10's browser exit criterion — its graphics half is no longer the blocker
```

## 3.2 Wave 0 — startable today, fully parallel

No unmet dependency. Twenty-three tracks as first written; ten are struck through, having landed
since. The rest can run in parallel.

| # | Track | Unblocks |
|---|---|---|
| ~~W0-1~~ | ~~**K1** — `SceneCompiler` + runtime scene/prefab asset~~ | Built. The 7 downstream items (§3.1) are unblocked and unstarted |
| ~~W0-2~~ | ~~**K2** — compute node + GPU buffer upload/readback~~ | Built. `ComputeRenderer` · `BufferUploadRenderer` · `BufferReadbackRenderer`, all three authorable. The 5 downstream items are startable |
| ~~W0-3~~ | ~~**K3** — `Vixen.Platform.Windows/.Linux/.MacOS`~~ | Built. Four of the five downstream items are closed; the docking one needs multi-window + DPI, not this |
| ~~W0-4~~ | ~~**K4** — `Silk.NET.OpenGLES` + EGL~~ | Built. `SilkGlesApi` + `EglContext`, with no change above `IGlApi`. What the three downstream items now want is an app head that asks for a GL device, not a binding |
| W0-5 | `DescriptorBinding` sample type + comparison sampler (RHI) | WebGPU shadow maps → deferred/forward parity on the web |
| ~~W0-6~~ | ~~`Tools/Vixen.ApiCheck` + first `PublicAPI.Shipped.txt`~~ | Built. The gate is in CI; what is left is the Phase 11 reading of what it baselined |
| W0-7 | CI legs: Windows/Linux Vulkan, NativeAOT publish, run-a-sample, WebGPU-on-lavapipe | Content determinism across 3 OSes; `Samples/01` on Windows/Linux; the AOT gate becoming continuous |
| W0-8 | `UiDocument.Update` → `StyleUpdater` (incremental cascade) | The largest UI perf item; nothing depends on it, everything benefits |
| ~~W0-9~~ | ~~`UiDocument` "layout finished" callback~~ | Built. The resize lag in `ScrollView`, `TreeView`, `DataGrid`, `CodeEditor`, `NodeCanvas` and `Viewport` is closed |
| ~~W0-10~~ | ~~Wire `LineWrapper` into `TextRun`/controls~~ | Built (`TextLayout`). What is left is the *editing* half — a caret that moves between lines — and `CodeEditor`'s own wrap |
| W0-11 | `Vixen.Core.Diagnostics` sinks (ZLogger file, console, platform, remote, `EventSource`) + rate limiting | Editor console · remote inspector · `Vixen.Editor.Profiler`/`.Debugger` |
| W0-12 | `Vixen.Editor.Plugin` (`AssemblyLoadContext`) | Editor extensibility; lets `Vixen.Editor.App` state its AOT position |
| W0-13 | `Tools/Vixen.Templates` (`vixen-game`/`app`/`lib`/`plugin`) | Phase 11's clean-machine criterion |
| W0-14 | Pin a static `libjoltc.a` for `ios-arm64` | Physics on iOS → `Samples/05` on iOS |
| W0-15 | Add `astcenc` + `ispc_texcomp` to `native-dependencies.json` | ASTC/ETC2 · full BC7/BC6H · mobile texture budgets. Also proves R10's schema generalises |
| ~~W0-16~~ | ~~ECS entity-handle **reservation**~~ | Built (`World.TryRecreate`), and spent: create/delete/rename are undoable in the scene view |
| W0-17 | Bindless material binding plan | Compacted draws · per-object reflection probes · material texture features. **Two-phase occlusion landed without it** |
| ~~W0-18~~ | ~~Light-probe exact predicates (robust Bowyer–Watson)~~ | Built, and spent: `LightProbeVolume` interpolates tetrahedrally. `ExactPredicates` is general — an exact orientation and in-sphere live in `Vixen.Core.Mathematics` now, for whatever else needs a sign rather than a number |
| ~~W0-19~~ | ~~`NodeGraphView` (pan/zoom/wires/minimap/search-to-create)~~ | Built. Shader-graph and VFX-graph authoring is now a matter of nodes, not of a canvas |
| W0-20 | Non-scene asset editors: texture, model, material, shader, UI, addressable groups, compositor | Phase 6's exit criterion, minus the scene half |
| W0-21 | Relay **scope decision** (host one? in-box or addon?) | The `Relay` transport + transport fallback |
| W0-22 | `Vixen.Raven.Transpile` (SPIRV-Cross) | HLSL/MSL/WGSL targets + the cross-compilation test pass |
| W0-23 | CSS Grid · `Canvas2D` · pinch/rotate · multi-window + DPI | Independent UI gaps; each is its own track. `VirtualizingPanel` and the image draw command are done |

## 3.3 Wave 1 — one dependency deep

| Track | Waits on | Note |
|---|---|---|
| ECS world serialisation | W0-1 | Needs per-component serialisers |
| Scene + prefab **asset editors** over compiled content | W0-1 | Authoring, save and in/out-of-process play mode are already done |
| Prefab overrides + nested prefabs | W0-1 | Risk R7 |
| Navigation: placements from a compiled scene | W0-1 | The importer already fills the list from an authored one |
| Networking: scene load/unload messages, baked scene index | W0-1 | Turns "waiting for its scene" from a state into a handshake |
| VFX GPU dispatch · reaping · GPU sort · indirect draw | W0-2 | Then mesh/ribbon/light renderers and the remaining updaters |
| `AutoExposure` wiring · Raven numeric + layout gates | W0-2 | |
| Editor "open project…" dialog | — | Unblocked: the picker is there, the menu item is not |
| ~~Thread affinity · thermal state · clipboard images~~ | ~~W0-3~~ | Built. Three long-standing deferrals closed, each on the platforms where the OS has an answer |
| Floating dock groups in OS windows | W0-23 (multi-window) | |
| Android GLES fallback + capability deny-list | W0-4 | |
| `Samples/02` in three browsers + Playwright leg | W0-4 | Phase 10 exit criterion |
| WebGPU shadow maps; WebGPU Linux CI leg | W0-5 + W0-7 | |
| API review pass | — | Unblocked: the gate is wired and the surface is written down. Reading 22 807 entries and deciding which of them should not be `public` is the Phase 11 work |
| ASTC/ETC2 output + full-quality BC7 | W0-15 | Then `ktx validate` + reference-decoder verification |
| Undoable **reparenting** command + hierarchy drag-and-drop | — | Unblocked: `SetParentAfter` is in. Create/delete/rename already landed |
| Viewport click-to-select | An id render target | The gizmo already drags what the hierarchy selects |
| Compacted draws; per-object reflection probes | W0-17 | |
| Shader-graph procedural/custom-code nodes, Post + UI masters, previews | — | Unblocked: `NodeGraphView` is in and its preview layer already draws a render target |
| VFX-graph operator nodes, remaining opcode blocks, live preview | W1(VFX GPU) | The view half is in; the live preview is the runtime's |
| `Relay` transport + transport fallback | W0-21 | |
| Cross-compilation test pass (ESSL/HLSL/MSL/WGSL) | W0-22 | |
| `Vixen.Editor.Profiler` · `.Debugger` · editor console | W0-11 | Plus the GPU/memory tracks in `Core.Diagnostics` |
| Editor network panel | W0-11 (host) | Everything it shows is already public |
| `.vxnetrules` asset | W0-1 (asset-pipeline shape) | |
| Prefab registry filled from the content catalog | W0-1 | |
| `Samples/05-PlatformerGame` | W0-1 + W0-14 | Phase 8 exit criterion |

## 3.4 Wave 2 — two or more deep

| Track | Waits on |
|---|---|
| Remote inspector attached to out-of-process players | Diagnostics remote sink (W0-11) — the player-launch half is built |
| Deferred pipeline (GBuffer, shading-model dispatch, forward routing, decals) | Bindless materials (W0-17); parallel to everything else |
| Volumetric fog · contact shadows · light shafts · motion blur · SSS blur · FSR1 | Deferred pipeline |
| Mesh shaders / meshlet culling | Deferred pipeline + capability flags |
| SMAA · MSAA resolve · GTAO · SSR · DoF | Shaders that do not exist yet; MSAA resolve also wants the Vulkan resolve path |
| Signing · notarisation · `.dmg`/AppImage/MSI · `PublishEditor` | W0-3 (per-OS) + a full editor |
| Release automation (tag → signed builds + NuGet + Release) | Signing |
| 24 h editor / 24 h game soak | A complete editor and a complete game sample |
| Perf bars measured on the IHV matrix | Real hardware + all CI legs |
| DocFX + manual + 12 samples + templates verified clean-machine | Effectively everything |
| Video **material** (a video lit on a mesh); XR render feature + single-pass multiview | The material system / `VK_KHR_multiview` in the RHI. Both modules themselves are ✅ |

## 3.5 Independent of everything (pure additions)

These have no dependency in either direction and can be picked up whenever there is a gap:
UDP congestion control / ack piggybacking / path MTU / DTLS; `SharpFuzz` instrumentation and
structure-aware mutation; per-axis `NetworkTransform`; team/room/fog-of-war interest rules and
resolver composition; `SyncVar` dirty-marking system; `ReplicationChannel` helper; OpenTelemetry
traces and the client-side metrics route; Raven string interpolation; blend shapes; punctual shadow
caching; parallel asset import; ECS read/write inference generator;
`WhenAny` in coroutines; `GpuUploadRing`; transform decomposition.

---

# Part 4 — What is owed

Every item the documents explicitly mark **Owed**, **Not here yet**, **Known gap** or **Still to
come**, in one table. "Owed" means the subsystem is otherwise done and this is the named remainder —
it is deliberately distinct from "not started" in Part 1.

| # | Subsystem | Owed | Kind | Blocked by |
|---|---|---|---|---|
| 1 | `Vixen.Core.Memory` | `GpuUploadRing` | Feature | — |
| 2 | `Vixen.Core.Collections` | `RobinHoodDictionary`, `FixedBitSet<N>` | Feature | ECS component budget (for `N`) |
| 3 | `Vixen.Core.Threading` | `VIXEN_JOB_SAFETY` access declarations | Correctness | ECS declarations |
| 4 | `Vixen.Core.Threading` | Thread pinning / affinity — **the platform half is built (K3)**; what is owed is the scheduler using it | Perf | — |
| 5 | `Vixen.Core.Threading` | Job priority tier for streaming/decode | Perf | — |
| 6 | `Vixen.Core.IO` | The synchronous-IO ban (the `System.IO.Path` half is built) | Discipline | A decision about `IOdbBackend`'s synchronous contract |
| 7 | `Vixen.Core.Reflection` | Generic type support | Feature | — |
| 8 | `Vixen.Core.Diagnostics` | ZLogger/console/platform/remote/`EventSource` sinks | Feature | — |
| 9 | `Vixen.Core.Diagnostics` | Rate limiting; UTF-8 record packing | Perf | — |
| 10 | `Vixen.Core.Diagnostics` | GPU profiling, memory attribution, Perfetto protobuf | Feature | Allocator reporting surface |
| 11 | `Vixen.Core.Imaging` | ASTC/ETC2 encoders; full BC7/BC6H | Feature | Native encoder acquisition (W0-15) |
| 12 | `Vixen.Core.Imaging` | `ktx validate` + reference-decoder verification | Correctness | — |
| 13 | `Vixen.Ecs` | World serialisation | Feature | **K1** |
| 14 | `Vixen.Ecs` | Read/write inference generator; `VIXEN_ECS_EVENTS` | Feature | — |
| 16 | `Vixen.Engine` | Depth-split transform hierarchy | Perf | Shared components |
| 17 | `Vixen.Engine` | Doc 13's render-mode, UI-debug and streaming overlays | Feature | Shader debug views; a reporting seam out of `Vixen.Ui` and `Vixen.Assets` |
| 18 | `Vixen.Engine` | `WhenAny` in coroutines | Feature | — |
| 19 | `Vixen.Graphics` | `DescriptorBinding` sample type / comparison sampler | API | — |
| 20 | `Vixen.Graphics` | Placed resources (true aliasing) | Perf | Two backends cannot express it |
| 21 | `Vixen.Graphics.RenderGraph` | Async-compute queue scheduling | Perf | — |
| 22 | `Vixen.Graphics.Vulkan` | Swapchain acquire/present coverage; timeline semaphores; MSAA resolve; query pools | Coverage / feature | Windowed test host |
| 23 | `Vixen.Graphics.OpenGL` | `glBindImageTexture` (storage images) | Feature | — (`Silk.NET.OpenGLES` + EGL is built) |
| 24 | `Vixen.Graphics.WebGPU` | Sampled depth + comparison sampler; timestamp queries; Linux CI leg | Feature | #19 |
| 25 | ~~`Vixen.Platform.Desktop`~~ | ~~File pickers, clipboard images/custom formats, thread affinity, thermal state~~ | Built (K3) — supplied by `Vixen.Platform.Windows`/`.Linux`/`.MacOS` through `IPlatformSupplement` | — |
| 26 | `Vixen.Platform.Native` | R10's remaining five native dependencies | Infra | — |
| 27 | `Vixen.Platform.iOS` | Physical-device run; sensors, haptics, HDR layer; scene-delegate lifecycle | Verification | Provisioning profile |
| 28 | `Vixen.Platform.Android` | GLES fallback + deny-list; key translation; safe-area insets; sensors; default-runtime AOT gate | Feature | — (the GLES binding and the EGL context now exist) |
| 29 | `Vixen.Platform.Web` | Playwright smoke test; `AudioWorklet` path; a browser transport | Coverage / feature | CI leg |
| 30 | `Vixen.Assets` / pipeline | Parallel import; persisted per-entry index; the import-budget gate | Perf | — |
| ~~31~~ | ~~Asset pipeline~~ | ~~`SceneCompiler` + scene/prefab asset~~ | Built | — (**K1** itself) |
| 32 | Asset pipeline | `.cube` LUT importer; server content profile | Feature | — |
| 33 | `Vixen.Sdk` | CLI shipped in the package; platform packaging; diagnostic file paths | Infra | — |
| 34 | `Vixen.Cli` | Signing/notarisation/packaging; `app`/`plugin`/`tool` templates; `doctor systems` | Infra | Nuke `Build.Release.cs` (signing), Vixen.Ui maturity (`app`) |
| 35 | `Vixen.Ui.Styling` | `UiDocument.Update` → incremental cascade | **Perf (largest in Phase 4)** | — |
| 35b | `Vixen.Ui.Reactive` | A frame-pass caller for `EffectScheduler.Flush()` | Correctness | — |
| 36 | `Vixen.Ui.Styling` | Transform decomposition | Feature | A transform property |
| 37 | `Vixen.Ui.Text` | `TextEditor` model with IME + caret affinity | Feature | — |
| 38 | `Vixen.Ui.Text` | `CVAR`, `CFF2` variation, direct `HVAR` | Feature | — |
| 39 | `Vixen.Ui` | Rich-text runs from markup (which stretch is bold) | Feature | — |
| 40 | `Vixen.Ui` | Named slot projection; LIS reorder pass | Feature | — |
| 41 | `Vixen.Ui` | Pinch and rotate; multi-window + DPI | Feature | — |
| 42 | `Vixen.Ui` | Per-corner radii on `DrawCommand` (one radius carried, rest dropped) | Feature | — |
| 43 | `Vixen.Ui` | Computed-value stage for `line-height`/`letter-spacing`/`word-spacing`/`text-indent` | Correctness | — |
| 44 | `Vixen.Ui.Markup` | `bind:` update events; CLI path emitting generated C# to disk | Feature | — |
| 45 | `Vixen.Ui.HotReload` | Driven against a running window | Verification | — |
| 46 | `Vixen.Ui.Controls` | `TextArea`'s editing half (caret between lines, Enter starting one); variable-height virtualisation | Feature | The text editor model |
| 47 | `Vixen.Ui.Controls.Advanced` | Undo; `CodeEditor` wrap + caret blink; `OkLch` gamut mapping; `AppendChild` O(n); `Canvas2D` | Feature / perf | — |
| 48 | `Vixen.Ui.Testing` | Group opacity; a third finger; layout-box assertions | Feature | Compositor decision (opacity) |
| 49 | `Vixen.Ui.Renderer` | Reconcile per-vertex box params with `Raven/Library/Ui`'s per-uniform ones | Consistency | Raven taking over UI shader compilation |
| 50 | Raven | String interpolation; workgroup-shared memory | Language | — |
| 51 | Raven | `Vixen.Raven.Transpile`; cross-compilation pass | Feature | — |
| 52 | Raven | `CompileShaderLibrary` Nuke target; SPDX enforcement | Infra | — |
| 53 | Raven | Numeric BRDF gate; per-backend layout gate; negative diagnostic fixtures | Coverage | A device (**K2** landed the readback) |
| 54 | Raven | Stream interpolation control; per-module flat IR namespace | Feature | — |
| 55 | `Vixen.Rendering` | Compacted draws | Perf | Bindless materials |
| 56 | `Vixen.Rendering` | Transmission; bindless material textures; blend shapes | Feature | Pass-level scene colour |
| 57 | `Vixen.Rendering` | Light probes **on the GPU** (upload a volume, sample it in a shader); per-object reflection probes; punctual shadow caching | Feature | Binding plan. The predicates and the CPU interpolation landed |
| 58 | `Vixen.Rendering.PostFx` | SMAA, MSAA resolve, GTAO, SSR, DoF, motion blur, LUT asset, `AutoExposure` | Feature | — (**K2** landed; `AutoExposure` is now a chain to write) |
| 59 | `Vixen.Physics` | iOS slice; per-pair suppression; vehicles/ragdolls/soft bodies; double precision | Platform / feature | Static `libjoltc.a` |
| 60 | `Vixen.Audio` | Measured HRTF sets; per-title certification work | Content | — |
| 61 | `Vixen.Input` | Action-map editor + debug panel; sensors/pen/MIDI/HID | Feature | Platform contracts (devices) |
| 62 | `Vixen.Navigation` | Placements from a compiled scene | Feature | **K1** |
| 63 | `Vixen.Vfx` | GPU dispatch, reaping, GPU sort, indirect draw; extra renderers/updaters; second view; screen-space collision | Feature | — (**K2** landed; GPU sort still wants workgroup-shared memory, #50) |
| 64 | `Vixen.Net` | `Relay` transport + fallback | Feature | Scope decision |
| 65 | `Vixen.Net` | UDP congestion control, ack piggybacking, path MTU, DTLS | Feature | — |
| 66 | `Vixen.Net` | Session bandwidth budgeting / priority shedding | Feature | — |
| 67 | `Vixen.Net` | `.vxnetrules` asset; prefab registry from the catalog; scene messages; client-requested spawns; `OnOwnerDisconnect` → `Despawn` | Feature | **K1** (partly) |
| 68 | `Vixen.Net` | Team/room/fog-of-war rules; resolver composition | Feature | — |
| 69 | `Vixen.Net` | Per-axis / parent-relative `NetworkTransform`; per-bone quantisation; pose interpolation | Feature | — |
| 70 | `Vixen.Net` | Hit-claim message; per-bone rewind; rewind cost budget; rewind visualisation | Feature | Animation pose history |
| 71 | `Vixen.Net` | `SyncVar` dirty-marking system; `ReplicationChannel` helper; generator packaged into the NuGet | Ergonomics | Engine scheduler |
| 72 | `Vixen.Net` | Predicted spawns; scheduler fixed-step group | Feature | Re-entrant scheduler |
| 73 | `Vixen.Net.Fuzz` | `SharpFuzz` instrumentation; structure-aware mutation; generated encoders end to end | Coverage | — |
| 74 | `Vixen.Net.Telemetry` | Traces; log bridge to OTLP; Grafana dashboard; client-side route | Observability | — |
| 75 | Networking | Editor network panel; RTT/jitter/loss graphs | Tooling | Panel host |
| 76 | Server variant | Container image; server content profile | Infra | CI / asset pipeline |
| 77 | `Vixen.Editor.Ui` | Keybinding editor; notification panel; `Strings.Resource` generation | Feature | — |
| 78 | `Vixen.Editor.Inspector` | Curve multi-edit; asset-picker browser | Feature | — |
| 79 | `Vixen.Editor.SceneView` | Undoable reparent command; hierarchy drag-and-drop; viewport click-to-select; meshes in the viewport | Feature | An id target; the material system |
| 80 | `Vixen.Editor.App` | Plugin loading; file dialog | Feature | `Vixen.Editor.Plugin` (K3 is built, so the dialog is only owed a caller) |
| 81 | `Vixen.Editor.NodeGraph` | Selectable wires; sticky-note editing; a node in two groups; inlined-node → source-node map; Raven-span diagnostics | Feature | Emitter span recording, for the last |
| 82 | `Vixen.Editor.ShaderGraph` | Procedural + custom-code nodes; Post/UI masters; previews; diagnostic mapping | Feature | Emitter span recording |
| 83 | `Vixen.Editor.VfxGraph` | Operator nodes; remaining opcode blocks; sub-emitters/trails; live preview | Feature | — |
| 84 | Editor | Asset editors; `Vixen.Editor.Profiler`/`.Debugger`/`.Plugin`/`.AnimationGraph`; golden screenshots; `PublishEditor`; redraw-on-change; the shell perf bar | Feature | Various |
| 85 | Build/CI | NativeAOT leg; sample-running leg; Playwright leg; 3-OS determinism run | Infra | — |
| 86 | Build/CI | Per-file SPDX enforcement; third-party attribution manifest | Licence obligation (ADR-015) | — |
| 87 | Samples | `05-PlatformerGame`; `06-CanvasStress`; `01` on Windows/Linux and physical devices | Coverage | **K1**, #59, CI legs |
| 89 | `Vixen.Video` | MP4; a material; frame-accurate seek; audio-track choice; subtitles; 10-bit / BT.2020; Vorbis; >2 channels | Feature | A wider pixel format, for the last two |
| 90 | `Vixen.Xr` | A render feature; single-pass multiview; hand/eye tracking; passthrough; anchors | Feature | `VK_KHR_multiview` in `Vixen.Graphics` |
| 88 | Docs | DocFX; manual; templates; release automation; soak tests; triage + compatibility policy | Phase 11 | Everything |

## 4.1 Owed by weight

| Bucket | Count | Comment |
|---|---|---|
| ~~Blocked on **K1** (scene format)~~ | 9 | Unblocked: the scene format is built. The nine are now startable |
| ~~Blocked on **K2** (compute/readback)~~ | ~~5~~ | Unblocked. The node and both copies are built; the five are now ordinary work, and finishing them closes Phase 7's exit criterion |
| ~~Blocked on **K3** (per-OS assemblies)~~ | ~~5~~ | Built. Four of the five are closed; floating dock groups wanted multi-window + DPI rather than this |
| ~~Blocked on **K4** (`OpenGLES` + EGL)~~ | ~~3~~ | K4 is built. The three are now app-head work: a head that asks for a GL device on Android or in a browser |
| Blocked on a **decision**, not code | 2 | Relay scope; D3D12 (already answered: post-1.0) |
| Blocked on **hardware or an account** | 3 | iPhone provisioning; an Android device; the IHV matrix |
| Genuinely independent | ~40 | Can be picked up in any order (§3.5) |
| Closed since the first revision of this page | 11 | **K2** · **K3** · **K4** · `CheckApi` · `LayoutFinished` · `NodeGraphView` · handle reservation · line wrapping · `VirtualizingPanel` · `scoped`/per-type stylesheets · the image draw command |

---

## Appendix — headline numbers

| | |
|---|---|
| `.csproj` on disk | 218 (`Core` 122 · `Platform` 34 · `Editor` 19 · `Tools` 21 · `Samples` 11 · `Benchmarks` 7 · `Raven` 3) — counting test siblings and generators, per ADR-014 |
| Planned projects not created | `Vixen.Graphics.Direct3D12`, `Vixen.Net.Transport.Relay`, `Vixen.Editor.AnimationGraph/.Profiler/.Debugger/.Plugin`, `Vixen.Templates`, `Vixen.Raven.Transpile` |
| Conformance cases green | 534 Yoga · 22 048 UAX#14/#29 · 91 707 UAX#9 · 328/413 shaping · 100 variable-font |
| Golden image fixtures | 40 |
| Fuzz targets / cases per build | 12 / ~11 M in ~7 s |
| Phases complete | 0, 1, 2, 3 (bar CI legs and physical devices), 4, 5b, 6 (the exit sentence; the tooling around it is not), 9 |
| Phases partial | 5 (renderer — PostFx and D3D12), 7 (VFX GPU path), 8 (samples), 10 (WebGPU, Video and XR landed early; deferred rendering and the browser run did not) |
| Phases not started | 11 (polish and 1.0) |
| Roadmap estimate remaining | ~8–11 EM of the original ~48, concentrated in Phase 6's tooling, Phase 7's GPU path, Phase 10's deferred pipeline, and Phase 11 |

*Generated from the documentation set and the repository as of 2026-07-29. Licensed under Apache-2.0.*
