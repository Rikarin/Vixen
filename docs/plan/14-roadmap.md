# 14 — Roadmap

## Sizing, honestly

Effort is given in **engineer-months (EM)** — one experienced .NET/graphics engineer working full time.
These are estimates for *this* scope, benchmarked against what Stride and comparable engines actually
took, not against optimism.

| Phase | Deliverable | EM |
|---|---|---|
| 0 | Monorepo, build system, foundations | 2.0 |
| 1 | Core runtime + RHI + first triangle | 4.5 |
| 2 | ECS + engine loop + scenes | 3.0 |
| 3 | Asset pipeline + mobile bring-up | 4.0 |
| 4 | UI framework | 7.0 |
| 5 | Renderer (forward+, PBR, shadows, post FX) | 4.5 |
| 5b | **Raven parser migration** (ANTLR → hand-written) | 1.5 |
| 6 | Editor shell | 4.5 |
| 7 | Node graphs + VFX | 3.5 |
| 8 | Gameplay subsystems (physics, audio, animation, input) | 3.5 |
| 9 | **Networking and multiplayer** | 5.0 |
| 10 | Deferred, advanced rendering, Web | 2.5 |
| 11 | Polish, docs, 1.0 | 2.5 |
| | **Total** | **≈ 48.0 EM** |

Plus Raven's remaining work (semantic → IR → GLSL+SPIR-V → CLI → interaction classes), which your brief places
before Phase 1 and which is roughly **6–9 EM** on its own based on its current state.

So: **~55 engineer-months.** (Was ~50: deferring D3D12 saved ~1 EM per Q4 and demoting the canvas-stress
sample ~0.5 per Q3, then networking added 5.0 per Q7, then the parser migration added 1.5 —
see [18](18-raven-parser-migration.md).) With two strong engineers that is ~2.3 years; with four, ~16 months
allowing for coordination overhead. A solo effort is a ~4.5-year project, which is achievable — Stride's
predecessor and several notable engines were built that way — but the plan should not pretend
otherwise.

The phases are ordered so that **every phase ends with something that runs**, and so that the highest-risk
items are answered early rather than discovered late. Of the two originally flagged, the Web graphics
spike is **already retired** ([spikes/web-webgl2](spikes/web-webgl2/RESULT.md)); iOS/AOT correctness
remains front-loaded into Phase 3.

---

## Phase 0 — Foundations *(2.0 EM)*

**Goal:** a monorepo that builds, tests, and packages nothing useful, correctly.

- Monorepo init; absorb `Raven/` with history preserved (script in [02](02-repository-layout.md)).
- Rename Raven projects to `Vixen.Raven.*`; add `references/` submodules (stride, arch, flexbox, yoga,
  signals-dotnet).
- `Directory.Build.props/.targets`, `Directory.Packages.props` with every version from
  [01](01-technology-decisions.md), `global.json`, `.editorconfig`, `Vixen.slnx` + filters.
- ✅ Nuke: `Clean Restore Compile Test Pack CheckFormat CheckArchitecture Benchmark`, with
  `build.sh`/`build.cmd` as the entry point CI and developers share. `CheckApi` waits for
  `Tools/Vixen.ApiCheck` and the first `PublicAPI.Shipped.txt`.
- ✅ `ci.yml` on three desktop runners — test matrix, checks, pack. Branch protection is a repository
  setting, not a file, so it stays a manual step.
- 🟡 `references/` — the README with the clone commands is tracked; the clones themselves are a local
  decision rather than submodules, for the reason written there.
- **Extract `Vixen.Core.Syntax`** from Raven (green/red trees, `SyntaxGenerator`, `SourceText`,
  diagnostics) and retarget Raven onto it. This unblocks VXML and VCSS later and is the
  highest-leverage refactor available.
- ✅ `Vixen.Core` — annotations, identity types, `GameTime`, `ServiceRegistry`, pooling, `DisposeBag`,
  `LeakTracker`, with 86 tests green in Debug and Release. What differs from
  [03](03-core-foundation.md) is written down there.
- ✅ `Vixen.Core.Mathematics` — every type ADR-003 lists, plus `Matrix3x3` and `ColorSpace`, with
  `Conventions.md` and 126 tests including CsCheck properties for the algebraic laws and a
  clip-space oracle for frustum culling. `Half` is deliberately omitted: `System.Half` is in the BCL.
  SIMD paths measured by `Benchmarks/Vixen.Benchmarks.Math`, which found them slower than the scalar
  fallbacks and led to the fix.
- ✅ `Vixen.Core.Collections` — `Handle<T>`/`HandlePool<T>`, `FreeList<T>`, `SparseSet<T>`, `BitSet`,
  `SmallList<T,TBuffer>` over `InlineArray` buffers, `ChunkedArray<T>`, `RingBuffer<T>` and an
  indexed priority queue with decrease-key. 34 tests, several against a BCL oracle.
  **Deferred with reasons in the README:** `RobinHoodDictionary` (no benchmark yet for it to beat)
  and `FixedBitSet<N>` (its capacity is the ECS's component budget, which is not decided).
- ✅ `Vixen.Core.Memory` — `NativeArray<T>`, `ArenaAllocator` with frame and scope arenas, and
  `BuddyAllocator`. 19 tests including a property test asserting suballocations never overlap and
  that releasing everything merges the region back whole. **Deferred:** `GpuUploadRing`, which needs
  mapped memory and frame fences and so lands with the RHI in Phase 1.
- ✅ `Vixen.Core.Diagnostics` — `[LoggerMessage]` plumbing, the always-on `RingBufferSink` with
  per-category levels, `ProfilingKey`/`Profiler` over per-thread sample rings, and Chrome-trace
  export that opens in Perfetto. 18 tests. Event-id ranges reserved in `docs/manual/log-events.md`.
  **Owed:** the other sinks, rate limiting, and UTF-8 record packing — each needs the thing it feeds.
- ✅ `Benchmarks/Vixen.Benchmarks.Math`, and the Nuke `Benchmark` target that runs it.

**Exit:** `nuke Test` green on Windows/Linux/macOS. Raven builds and tests green on
`Vixen.Core.Syntax`. Math and collections at > 90 % coverage with property tests. A `TestApp` stub
exists.

---

## Phase 1 — Core runtime and the first triangle *(4.5 EM)*

**Goal:** a window on three desktops with a Vulkan-cleared, triangle-drawing swapchain, and the
plumbing that everything else stands on.

- ✅ `Vixen.Core.Threading` — persistent workers over Chase–Lev deques, a `JobHandle` DAG, struct
  jobs dispatched with no boxing and no allocation, `ScheduleParallel` with automatic batching, the
  main-thread dispatcher, and per-job profiler samples. 45 tests, including one that asserts every
  item leaves a contended deque exactly once and one that runs twenty random 400-node graphs and
  checks every edge. `Benchmarks/Vixen.Benchmarks.Jobs` measures it against `Task.Run` and
  `Parallel.For`, and found the wake-up traffic that made a burst of jobs cost more per job than a
  single one. **Deferred with reasons in [03](03-core-foundation.md):** the `VIXEN_JOB_SAFETY`
  access-declaration system (needs the ECS, so Phase 2) and thread pinning (needs `Vixen.Platform`).
- ✅ `Vixen.Core.IO` — `VirtualPath`, the mount table, physical and in-memory providers behind one
  conformance suite, memory-mapped reads, and `Watch` with the coalescing that makes a real editor's
  save look like one change. 123 tests. **Deferred with reasons in [03](03-core-foundation.md):**
  the Android/iOS/browser/bundle providers, which arrive with the platform or database they read
  from, and the `System.IO.Path` analyzer.
- ✅ `Vixen.Core.Serialization` + generator — the wire format, `DataSerializer<T>`, the registry, a
  `[DataContract]` generator that emits readable C# and turns an unserialisable type into a build
  error, and the content-addressed `ObjectDatabase` with its loose-file and bundle backends, LZ4/Zstd
  chunk compression and CRC-checked bundles. 53 tests covering round-trip, additive evolution,
  migration, determinism, truncation, deduplication and corruption. **Deferred with reasons in
  [03](03-core-foundation.md):** content references (Phase 3) and bundle-packing *policy*, which
  belongs to the content build in [08](08-asset-pipeline-and-addressables.md).
- ✅ `Vixen.Core.Reflection` generator + `[ModuleInitializer]` registration — `TypeDescriptor` and
  `MemberDescriptor` with generated accessor lambdas, trait flags, inspector presentation, factories,
  and queries by type, name, trait and base type. 16 tests. **Deferred with reasons in
  [03](03-core-foundation.md):** `[Behavior]`, whose attribute arrives with the engine loop in
  Phase 2, and generic types.
- ✅ `Vixen.Platform` contracts — `IPlatform` over windows, surfaces, displays, files, clipboard,
  native dialogs, lifecycle, raw input, IME and power, with one `PlatformEvent` stream drained once
  per frame and capabilities asked at runtime rather than compiled in. 26 tests. Two decisions worth
  naming: `Key` is a physical position with no layout-dependent twin, and `WindowResized` carries the
  logical size and the pixel size separately. Also closes the contract half of the thread-pinning
  deferral from [03](03-core-foundation.md) as `IProcessorTopology`.
- ✅ `Vixen.Platform.Headless` (no window/GPU/audio) so the no-display path is real from day one
  rather than retrofitted for the server variant in Phase 9 ([17](17-app-heads-and-shipping.md)).
  31 tests. Headless windows are real windows without a picture, so the dedicated server runs the
  desktop's frame loop; the clipboard refuses rather than faking; and `Suspend`/`Resume`/
  `ReportMemoryPressure` are driveable, which is where the lifecycle fault-injection loop
  [10](10-platforms.md) asks for actually runs.
- ✅ `Vixen.Platform.Desktop` — Windows, Linux and macOS through one SDL implementation: windows,
  surfaces (Win32/X11/Wayland/`CAMetalLayer`), displays, cursors, clipboard text, IME, gamepads with
  rumble, drag-and-drop, message boxes, battery. 55 tests. **It is SDL 2, not SDL 3** — the
  dependency register in [01](01-technology-decisions.md) said otherwise and was wrong, and is
  corrected. **Owed, and visibly missing rather than approximated:** file pickers (SDL 2 has none),
  clipboard images and custom formats, thread affinity, thermal state — all four belong to
  `Vixen.Platform.Windows`/`.Linux`/`.MacOS`, which [02](02-repository-layout.md) already reserves.
- `Vixen.Platform.Native` — RID→binary mapping and checksummed acquisition. Now load-bearing rather
  than tidy: `Silk.NET.SDL` ships no native binary, so CI installs `libSDL2` from a package manager
  and Windows has nothing to install it with.
- Windows/Linux/macOS specialisations.
- ✅ `Vixen.App` host (`VixenApp.Run<TGame>()`) and the build-variant matrix — the boot sequence, the
  `Game` hooks, the frame loop, the `--vixen-*` argument contract, the headless fallback and frame
  pacing. 36 tests. Every step is public, so an editor's play mode and a test drive the same loop the
  host does — [17](17-app-heads-and-shipping.md)'s rule that nothing in the boot path is inaccessible.
  First user of the `[LoggerMessage]` id register in `docs/manual/log-events.md`, which had been
  empty. **Owed:** content (`--vixen-loose-content` is parsed and not yet honoured), rendering, and
  the fixed-step accumulator, which arrives with `Vixen.Engine` in Phase 2.
  `vixen-game`/`vixen-app` templates follow in Phase 3 with the CLI.
- ✅ `Vixen.Graphics` RHI surface — the vocabulary is built: `PixelFormat` with block sizes, sRGB
  pairing and level arithmetic; the enum set including the `synchronization2`-shaped `ResourceState`
  barrier model; `GraphicsDeviceFeatures`; typed handles; and self-validating resource descriptions.
  46 tests. Reversed depth is in the defaults rather than only in `Conventions.md` — an attachment
  clears to 0 and the shadow sampler compares `GreaterEqual`. The interfaces are built too:
  `IGraphicsAdapter`, `IGraphicsDevice`, `ICommandSubmitter`, `ICommandList`, `ISwapChain`, the
  pipeline and descriptor-layout descriptions, and the grouped `BarrierGroup`. Moving `SurfaceHandle`
  down into `Vixen.Core` was needed to keep the layering honest — see below. Both implementations
  now exist, and building the second one found three defaults in this layer that did not hold the
  values their own documentation described (see `Vixen.Graphics.Vulkan` below).
- ✅ `Vixen.Graphics.Null` + the recording harness — a device with no GPU that records the command
  stream into a comparable log, and refuses the dozen things that are undefined behaviour on a real
  backend: a draw outside a pass, a dispatch or copy inside one, a list submitted twice or before it
  was finished, a buffer copied onto itself, a handle used after it was destroyed. 29 tests.
  Recording is **off by default**, because [17](17-app-heads-and-shipping.md) makes this a shipping
  backend and a server that accumulated a command log would run out of memory. Resource creation
  allocates a handle and a description and nothing proportional to the size asked for, so a server
  that creates a 4K target every frame stays flat. `NullSwapChain.NextStatus` makes the out-of-date
  and device-lost paths reachable from a test, which is the fault injection [05](05-graphics-rhi.md)
  asks for.
- ⚠ **Prerequisite, discovered rather than planned: there is no Vulkan on the development machine.**
  No loader, no MoltenVK, no ICD — verified. So `Vixen.Graphics.Vulkan` cannot be written test-first
  locally: every test would skip, and a backend developed against a driver that is not there is the
  exact failure mode [00](00-vision-and-principles.md) warns about, code that reads plausibly and is
  wrong. Two things have to happen before it starts, in this order:
  1. **Install the Vulkan SDK** (MoltenVK + the Loader + validation layers) on macOS, per the
     two-flavour scheme in [10](10-platforms.md) § macOS. The development flavour is the one needed
     here, and it needs `VK_ICD_FILENAMES` and the `VK_KHR_portability_enumeration` flag or the
     Loader reports no devices on a machine that works.
  2. **Stand up the lavapipe CI leg first**, not last. [10](10-platforms.md) already calls Linux the
     most valuable CI target because lavapipe is a conformant Vulkan 1.3 driver with no GPU; making
     it the *primary* verification for this backend rather than a later addition is the difference
     between a backend that is tested on every push and one that is tested on one laptop.
- ✅ `Vixen.Graphics.Vulkan` — the whole of `IGraphicsDevice` and `ICommandList` against MoltenVK
  1.4.2 and Loader 1.4.350: instance and portability, adapter and queue-family selection, capability
  translation, a block-suballocating allocator, resources, descriptor sets, graphics and compute
  pipelines, command recording, barriers, both render paths, and the swapchain. 155 tests, and the
  suite is **validation-clean** — `VulkanDiagnostics` records what the layers say and
  `ValidationCleanTests` fails on any of it, which is what [00](00-vision-and-principles.md)'s
  non-negotiable has to mean to be worth stating. Most of the logic is pure functions tested with no
  driver present; the parts that need one are asserted by reading pixels back, because a backend that
  records the right calls and renders nothing passes every other kind of test.
  Findings worth carrying forward:
  - The loader is not on macOS's default search path when installed by Homebrew, so `VulkanLoader`
    probes for it; and `vulkan-validationlayers` is a separate formula, so a plain
    `brew install vulkan-loader molten-vk` runs unvalidated and now says so.
  - `VK_KHR_dynamic_rendering` requires `VK_KHR_create_renderpass2` and
    `VK_KHR_depth_stencil_resolve` below Vulkan 1.2. MoltenVK accepted the incomplete extension list;
    the layers did not.
  - `RasterizerState.Default`, `DepthStencilState.Default` and `BlendState.Opaque` were all
    zero-initialised rather than carrying their documented values, because `new()` on a record struct
    with an all-optional primary constructor binds the implicit parameterless constructor. The
    symptom was a pipeline that drew an entirely untouched attachment with no error from anywhere.
    `PipelineDefaultTests` now asserts each documented default.

  **Owed, and named rather than approximated:** the swapchain's acquire/present path has no
  automated coverage — presenting needs a window, and AppKit aborts when one is created off the main
  thread, which is why the desktop tests force SDL's dummy driver on macOS. Its pure choices are
  tested; `Samples/01` is what exercises the rest. Also owed: timeline semaphores where the device
  offers them, MSAA resolve beyond the attachment plumbing, and query pools.
- ✅ `Vixen.Graphics.RenderGraph` — passes declare what they read and write; the graph culls what
  nothing needs, gives non-overlapping resources the same memory, places barriers batched per pass,
  derives attachment store actions, and hands imported resources back in the state their owner
  expects. 34 tests, including the property tests [05](05-graphics-rhi.md) § Testing asks for: random
  pass graphs replayed against a tracker that knows only the emitted command stream, asserting that
  every pass sees the state it declared, that no barrier misstates what it is transitioning from,
  that aliased resources never coexist, and that culling keeps exactly what is reachable from an
  output. Verified by sabotage — dropping write-after-write detection and changing one `<` to `<=` in
  lifetime release each fail their own property.

  Two decisions worth naming. A resource taking over aliased memory is transitioned *from*
  `Undefined`, which means "discard the contents" — stating the true previous state would ask the
  driver to preserve garbage, and on hardware with compressed targets that is a decompress for
  nothing. And a target nothing reads afterwards is not stored, which on tiled hardware is the
  difference between a bandwidth-bound frame and one that is not, and is the decision nobody
  remembers to make by hand.

  **Owed, and named rather than approximated:** this reuses whole resources, it does not overlap
  differently-shaped ones in a single allocation. True memory aliasing needs placed resources, which
  `IGraphicsDevice` does not expose and which two of the six planned backends cannot express. Also
  owed: async-compute queue scheduling — `PassKind` is declared and carried, and every pass currently
  runs on one queue.
- **MoltenVK bring-up on macOS** — do it here, not later; it shapes the Vulkan backend's capability
  handling.
- 🟡 `Samples/01-HelloTriangle` — the whole stack at once: the app host opens a window, the desktop
  platform hands over its native surface, the Vulkan backend builds a device and swapchain from it,
  and the render graph places the barriers. **Verified on macOS**, presenting Bgra8UNormSrgb at
  2560×1440 with three images, validation-clean over hundreds of frames. Windows and Linux are owed
  and will come with the CI legs.

  It earned its place immediately. The first time it presented to a real window it found two
  synchronisation bugs the entire headless Vulkan suite had passed straight through — `BeginFrame`
  discarded the pending wait that `AcquireNextImage` had registered, so nothing ever waited on the
  acquire semaphore; and the present-wait semaphore came from a ring recycled on the frame fence,
  which knows when a submission finished and not when the presentation engine did. Both are fixed
  where they lived, with the reasoning.

  `--vixen-frames N` came out of it and belongs to the host rather than the sample, so every app head
  and every later sample is CI-runnable the same way.
- ✅ **lavapipe in CI.** The Linux leg installs Mesa's software Vulkan, the loader, the validation
  layers and `spirv-tools`, and runs the whole suite against it — 155 Vulkan tests, **zero skipped**,
  both render paths, validation-clean. `VIXEN_REQUIRE_VULKAN=1` turns a skip into a failure on that
  leg, because a runner that lost its ICD reporting a green build is the most expensive kind of green
  there is. Verified locally in a container before being committed, rather than pushed and hoped for.

  A second driver earns its keep immediately, which is the whole argument for this leg:
  - The instance asked for Vulkan 1.1 and everything above had to come from extensions, so a
    `VkPipelineRenderingCreateInfo` on a 1.4 device was invalid usage. MoltenVK accepted it in
    silence; lavapipe's validation named it. The instance now asks for what the loader offers, and
    every core-versus-extension decision reads the lesser of the instance's version and the device's.
  - `Environment.GetFolderPath` returns *the empty string* on Unix for a directory that does not exist
    yet — every one of them on a fresh account, in a container, on a runner. `StandardFileSystemHost`
    was therefore producing relative paths, and the engine would have written its saves into whatever
    the working directory happened to be. It passed every macOS and Windows run.
- `GoldenImages` target with the first fixture.
- ~~Web graphics spike~~ ✅ **already done, before Phase 0** — see
  [`spikes/web-webgl2/RESULT.md`](spikes/web-webgl2/RESULT.md). `Silk.NET.OpenGLES` renders a WebGL2
  triangle from `browser-wasm`; bridge is ~40 lines; trimmed payload 0.93 MB Brotli. R1 retired. The
  remaining Phase-1 web work is just folding the verified bridge into `Vixen.Graphics.OpenGL`'s WebGL2
  profile, which can now be scheduled with confidence instead of contingency.

**Exit:** triangle on three desktops via Vulkan (MoltenVK on macOS). RHI unit tests green on Null.
Vulkan validation clean under lavapipe in CI. Zero-allocation gate green for an empty frame.

---

## Phase 2 — ECS, engine loop, scenes *(3.0 EM)*

**Goal:** entities with transforms and behaviours, rendering nothing but debug lines, at 10 k scale.

- `Vixen.Ecs`: archetypes, chunks, edge graph, queries + generator, `CommandBuffer`, change versions,
  managed component store, world serialisation.
- `Vixen.Ecs` scheduler: `ISystem`, phases, read/write inference, DAG execution on the job system.
- Transform hierarchy with depth-split archetypes and dirty propagation.
- `Vixen.Engine`: game loop with fixed-step accumulator, `Behavior` + generated dispatch,
  `Transform`/`Camera` façades, scenes, `SceneTag`, additive load/unload.
- Prefabs: serialised subtree + bulk instantiate plan.
- `DebugDraw` + the diagnostic overlays from [13](13-diagnostics.md).
- ImGui debug overlay behind `VIXEN_DEBUG_IMGUI` (scaffold; deleted in Phase 6).
- Ported Arch benchmarks in `Benchmarks/Vixen.Benchmarks.Ecs`.
- `Samples/04-EcsStressTest`.

**Exit:** 100 k entities created/iterated within the Arch benchmark baseline. 10 k-entity scene with
transform hierarchy at zero Gen0 collections over 10 000 frames. `Behavior` lifecycle golden-ordering
tests green. Determinism test (two worlds, identical input log, 10 000 steps) green.

---

## Phase 3 — Asset pipeline and mobile bring-up *(4.0 EM)*

**Goal:** real content loads from bundles, and it does so on a phone. AOT correctness is proven before
the codebase is large enough for it to be expensive to fix.

- `Vixen.Core.Yaml` with the tagged-polymorphic emitter; `.meta` reader/writer, envelope fast-scan
  parser, migration chain; byte-identical round-trip corpus test.
- `Vixen.Editor.Core` asset database: GUID index, reverse-reference index, duplicate detection.
- `Vixen.Editor.Assets`: `TextureImporter`, `ModelImporter` (Assimp), `AudioImporter`,
  `NativeFormatImporter`, `DefaultImporter`. Out-of-process worker (`Tools/Vixen.AssetCompiler`).
- `Vixen.Core.Imaging`: KTX2, BCn/ASTC/ETC2 encoding, mip generation, IBL prefiltering.
- Content build: compiler DAG, `ObjectDatabase` (file + bundle backends), bundle packer, catalog.
- `Vixen.Assets` runtime: `AssetHandle`, ref counting, scopes, label/glob loading, streaming manager.
- Addressable groups (`.vxgroup`), local + remote providers, `Tools/Vixen.ContentServer`.
- `Vixen.Sdk` MSBuild integration so `dotnet build` does content builds.
- `Vixen.Cli` (`new`, `import`, `build`, `run`, `doctor`).
- **`Vixen.Platform.Android`** + Vulkan/GLES on device; lifecycle, `AAssetManager`, touch input.
- **`Vixen.Platform.iOS`** + MoltenVK static; **NativeAOT publish in CI on every PR from here on**.
- `Samples/07-AddressablesRemote`.

**Exit:** `Samples/01` runs on a physical Android device and a physical iPhone. iOS NativeAOT publish
with **zero** trim/AOT warnings. Content build determinism gate green across three OSes. Remote content
update fetches only changed bundles (asserted by byte count). Incremental import of one texture < 1 s
in a 10 k-asset fixture project.

> This phase is deliberately early and deliberately painful. Every plan that defers iOS discovers in
> month 30 that some subsystem needs reflection, and pays for it ten times over.

---

## Phase 4 — UI framework *(7.0 EM)*

**Goal:** a standalone Vixen application with a real interface. The largest phase; sequenced so each
sub-piece has its own gate.

**4a — Reactive and layout (2.0 EM)**
- `Vixen.Ui.Reactive`: signals, computed, effects, batching, collection signals, scheduler,
  runaway detection. Zero-allocation and diamond-correctness gates.
- `Vixen.Ui.Layout`: SoA store, flexbox algorithm, measure cache, dirty propagation, parallel layout.
- **Port Yoga's conformance suite.** Gate: it is green.

**4b — Styling (1.5 EM)**
- `Vixen.Ui.Styling`: ExCSS integration, cascade, `@layer`, rule bucketing, ancestor bloom filter,
  style sharing cache, invalidation, transitions, keyframe animations.
- `Vixen.Ui.Styling.Utilities`: token config, candidate scanner, utility grammar, variant system,
  arbitrary values, `@apply`, generated stylesheet.
- Gate: selector-matching oracle tests, invalidation-minimality tests, utility family tests.

**4c — Text (1.0 EM)**
- HarfBuzz shaping, bidi, UAX#14 line breaking, UAX#29 segmentation, MSDF atlas with LRU eviction,
  font fallback, rich-text runs, `TextEditor` model with IME.
- Gate: shaping golden tests per script; UAX conformance data green.

**4d — Element tree, markup, rendering (1.5 EM)**
- `Vixen.Ui`: element tree, generated property system, event routing, focus, hit testing, gestures,
  draw list, batching, clipping, path rendering, virtualisation primitive, multi-window, DPI.
- `Vixen.Ui.Markup`: VXML lexer/parser on `Vixen.Core.Syntax`, binder, emitter, `#line` mapping.
- `Vixen.Ui.HotReload`: three reload channels, keyed reconciliation, `[HotReloadState]`.
- UI render feature integrated into the renderer.
- Gate: draw-list golden tests; parser golden trees + error-recovery tests; hot-reload scenario tests.

**4e — Controls (1.0 EM)**
- `Vixen.Ui.Controls` (the full standard set) and the `Advanced` set's first three: `DockingHost`,
  `TreeView`, `PropertyGrid`.
- `Samples/02-HelloUi`.

**Exit:** `Samples/02` runs on Windows/Linux/macOS and in a browser. Yoga suite green. UI frame under
2 ms with 5 000 elements and zero steady-state allocation. Hot reload of `.vxml`/`.vcss` preserves
scroll/focus/selection. A `DockingHost` layout round-trips through serialisation.

---

## Phase 5 — Renderer *(4.5 EM)*

**Goal:** the forward+ pipeline with full PBR, shadows, and post FX. Depends on Raven's codegen phase
(GLSL + SPIR-V) being complete.

- `Vixen.Shaders`: effect system, permutation keys and cache (three tiers), `Vixen.Shaders.Generators`
  for parameter keys, build-time permutation pre-generation, `Tools/Vixen.ShaderCompilerService`.
- `Raven/Library`: the full shader library from [07](07-raven-shader-pipeline.md) — Core, Shading,
  Geometry, Material, Pipeline, PostFx, Ui.
- `Vixen.Rendering`: `RenderSystem`, `RenderObject`/`RenderNode`, root + sub render features
  (mesh, transform, skinning, instancing, material, lighting, shadow-caster), `VisibilityGroup` with
  parallel and GPU culling, `RenderView`/`RenderStage`, `GraphicsCompositor` as an asset, sort modes.
- Materials: the composable feature tree, metallic-roughness + spec-gloss, all BSDF layers from
  [06](06-rendering-pipeline.md), layering, cel shading.
- Lighting: all light types, clustered binning, IBL (prefiltered + SH), light probes, reflection probes.
- Shadows: CSM, cube, spot, atlas + static caching, PCF/PCSS.
- `Vixen.Rendering.PostFx`: the P1 effect set including TAA, FXAA, SMAA, MSAA resolve, GTAO, SSR,
  bloom, DoF, tonemapping, colour grading, auto-exposure, CAS, outline.
- `Vixen.Graphics.Direct3D12` — **not built** (Q4: postponed past 1.0). Stub project only. The abstraction
  validator role passes to `Vixen.Graphics.OpenGL`, which is a stricter test — see ADR-001.
- `docs/rhi-backend-mapping.md` written and kept current, so D3D12 mappability is reviewed by inspection
  rather than discovered later.
- `Samples/03-PbrShowcase`.
- Golden-image fixture suite (~40) on lavapipe.

**Exit:** `Samples/03` at the [00](00-vision-and-principles.md) performance bar on Vulkan and D3D12.
Golden images within tolerance on Vulkan/lavapipe and MoltenVK. White-furnace and BRDF numeric tests
green. **Zero runtime shader compilation** in a shipping build of `Samples/03`, asserted by test.
Shader hot reload under 500 ms.

---

## Phase 5b — Raven parser migration *(1.5 EM)*

**Goal:** replace Raven's ANTLR front end with a hand-written Roslyn-style lexer and recursive-descent
parser, and land incremental reparse in `Vixen.Core.Syntax`. Full finding and step-by-step plan in
[18](18-raven-parser-migration.md); ADR-009 is amended accordingly.

**Why here.** After Phase 5 because `Raven/Library` is what shakes out the last of the syntax, and
migrating into a churning grammar pays the cost twice. Before Phase 6 because the editor's `CodeEditor`
needs incremental reparse and squiggle-grade diagnostics for `.rvn`, and ANTLR can give neither.

- Freeze the corpus: golden trees and byte-exact round-trip over every construct and every
  `Raven/Library` file. The safety net, and worth having regardless.
- `SlidingTextWindow`, `SyntaxParser` base and `Blender` into `Vixen.Core.Syntax` — VXML and VCSS need
  all three anyway, so this cost was already committed by ADR-009.
- `RavenLexer.cs` and `RavenParser.cs`, emitting green nodes directly. Delete `SyntaxAntlrVisitor`
  (1 490 lines), the ANTLR package references, and the `catch` that discards trees ANTLR's recovery
  mangled.
- **Keep the `.g4` files** in a test-only project as a permanent differential oracle: parse every corpus
  file with both and compare trees. Same technique as the SPIR-V-vs-`shaderc` oracle.
- Then incremental reparse via the blender, as a separate change — one hard problem at a time.
- Then diagnostics worth reading: expected-token messages instead of "no viable alternative".

**Exit criteria.** Byte-identical trees to the ANTLR front end across the whole corpus; ANTLR gone from
the shipping projects; a `.rvn` edit reparsing incrementally; the differential oracle green in CI.

## Phase 6 — Editor shell *(4.5 EM)*

**Goal:** the editor is usable for real work, and ImGui is deleted.

- `Vixen.Editor.Core`: command stack with merging, per-document + global stacks, signal-backed
  document model, project model, settings assets.
- `Vixen.Editor.Ui`: docking shell, command registry, menus/toolbars/context menus, command palette,
  theming, notifications, background-task manager, localisation.
- `Vixen.Editor.Inspector`: generated drawers, attribute set, custom drawers, multi-object editing.
- `Vixen.Editor.SceneView`: viewport, gizmos, picking stage, selection outline, debug view modes,
  camera nav, drag-and-drop, play-in-editor with world snapshot.
- `Vixen.Ui.Controls.Advanced`: `DataGrid`, `NodeCanvas`, `CodeEditor`, `ColorPicker`, `Timeline`,
  `CurveEditor`, `GradientEditor`, `Viewport`.
- Asset editors: texture, model, material, scene, prefab, shader, UI, addressable groups, graphics
  compositor.
- `Vixen.Editor.Profiler` + `.Debugger` (frame graph, frame debugger, memory view, remote inspector).
- `Vixen.Editor.Plugin` with `AssemblyLoadContext` loading.
- Editor UI automation harness + golden screenshots.
- **Delete the ImGui scaffold.**
- `PublishEditor`, signing, notarisation, `.dmg`/AppImage/MSI.

**Exit:** the editor opens a project, imports assets, edits a scene, saves, builds content, and runs
the game — entirely in `Vixen.Ui`. The editor-shell performance bar from
[00](00-vision-and-principles.md) is met. `Sign`/`Notarize` produce installable artefacts. ImGui appears
nowhere in the dependency graph.

---

## Phase 7 — Node graphs and VFX *(3.5 EM)*

- `Vixen.Editor.NodeGraph`: model, view (`NodeCanvas`-based), generated node registry, undo, groups,
  sub-graphs, search-to-create, drag-from-port, previews, auto-layout, minimap.
- `Vixen.Editor.ShaderGraph`: node library, `DynamicVector` port typing, Raven emission, show-generated-
  code, diagnostics mapped to ports, master nodes (PBR/unlit/sprite/UI/post).
- `Vixen.Vfx` runtime: SoA attribute storage, spawners/initializers/updaters/renderers, deterministic
  RNG shared between CPU and GPU paths, CPU jobs + GPU compute simulation, GPU sort, indirect draw.
- `Vixen.Editor.VfxGraph`: node library + dual-target compilation + live preview.
- Particle render feature integrated.

**Exit:** a PBR material authored entirely in the shader graph renders identically to its hand-written
Raven equivalent (golden image). A VFX graph produces identical output on the CPU and GPU paths
(deterministic-RNG test). Graph → artefact golden tests green.

---

## Phase 8 — Gameplay subsystems *(3.5 EM)*

- `Vixen.Physics` (Jolt 2.22.0): bodies, shapes, compound shapes, constraints, character controller,
  raycasts/overlaps, triggers, layers, CCD, ECS integration with a fixed-step sync, debug rendering.
- `Vixen.Audio`: OpenAL backend + WebAudio backend, 3D spatialisation, mixer buses, effects (reverb,
  filter), streaming, ECS integration.
- `Vixen.Animation`: skeletal playback, blend trees (1D/2D), layers + masks, state machine, IK (two-bone,
  look-at, foot placement), root motion, events, GPU skinning integration.
- `Vixen.Editor.AnimationGraph`.
- `Vixen.Input`: full device set + the Unity-style action system, `.vxinput` asset, generated accessors,
  runtime rebinding, action-map editor, input debug panel.
- `Vixen.Navigation`: Recast/Detour binding, navmesh baking as a build step, agents, avoidance.
- `Samples/05-PlatformerGame` — physics, input, animation, audio, VFX end to end on all platforms where
  it is in scope.

**Exit:** `Samples/05` playable on Windows/Linux/macOS/Android/iOS. Physics fixed-step determinism test
green. Input rebinding works at runtime with conflict detection.

---

## Phase 9 — Networking and multiplayer *(5.0 EM)*

**Goal:** a server-authoritative multiplayer sample playable across the network, with replication,
interest management, and lag compensation. Full design in [16](16-networking.md).

Sequenced here because it depends on the ECS change-version machinery (Phase 2), deterministic prefab
and content IDs (Phase 3), and physics for lag compensation (Phase 8).

- `Vixen.Net.Transport.Local` **first** — in-process transport, so every later piece is unit-testable
  without sockets. Then `Udp` (reliable + unreliable + fragmentation), `WebSocket`, `Relay`, `Composite`,
  and the `NetworkSimulation` decorator (latency/jitter/loss injection, on by default in dev builds).
- Session layer: Server/Client/Host/Offline topologies, players, authentication hook, reconnect tokens.
- `TickManager`: fixed tick shared with the ECS `FixedUpdate` phase, clock sync, RTT/jitter estimation.
- Identity and spawning: `NetworkId`, prefab ids derived from asset GUIDs, build-time-baked ids for
  scene-placed objects, ownership with transfer.
- **`NetworkRules`** policy assets (`.vxnetrules`) — spawn/despawn/call/observe/write permissions.
- `Vixen.Net.Generators`: RPC senders (the `Rpc.Method(...)` accessor pattern — see
  [16](16-networking.md) on why we do not weave IL), manifest with stable hashed ids, serializers, delta
  serializers, quantizers, networked-type registry.
- Replication: per-connection baselines + acks, delta encoding over the ECS `.WithChanged(sinceTick)`
  query, bit packing, `[Quantize]`, `SyncVar<T>`/`SyncList<T>`/`NetworkModule`, bandwidth budget with
  priority shedding.
- Interest management: scene scope, explicit overrides, distance grid, `NetworkLOD` rate falloff.
- Motion: snapshot interpolation, clamped extrapolation, `NetworkTransform`, owner-side smoothing.
- Lag compensation: transform/collider history ring + rewound Jolt shape casts.
- Security pass: packet validation, rate limits, closed-set deserialization, protocol/content hash
  handshake. Fuzzing corpus over the packet reader.
- **Server variant end to end** ([17](17-app-heads-and-shipping.md)): headless host on the `Null`
  backend, server content profile (no textures/audio/shader permutations), container image, metrics
  endpoint. Plus **out-of-process play mode** in the editor, which is what makes multiplayer testable.
- Diagnostics: bandwidth attribution per object/type/RPC, packet inspector, editor network panel.
- `Samples/08-Multiplayer` — server-authoritative, 8 players, movement + shooting with lag comp.

**Exit:** `Samples/08` playable server↔client across a real network and under 20 % injected packet loss.
N-client in-process replication convergence tests green. Bit-exact serialization across all three desktop
OSes (same gate as content determinism). Packet-reader fuzzing clean. 100-connection / 5 000-entity soak
holds its bandwidth, CPU, and allocation budgets for 30 minutes.

> **Client-side prediction is explicitly *not* in this phase** — see [16](16-networking.md). PurrNet does
> not have it either. The tick loop and snapshot APIs are shaped to accept it later (+2 EM), and the
> ECS's chunk-copy world snapshots plus input-log replay are already the rollback primitives.

---

## Phase 10 — Deferred, advanced rendering, Web *(2.5 EM)*

- Deferred pipeline: GBuffer layout, shading-model-ID dispatch, automatic forward routing for
  non-representable materials, decals.
- Volumetric fog, contact shadows, light shafts, motion blur, SSS blur, upscaler interface + FSR1.
- Mesh shaders / meshlet culling behind capability flags.
- `Vixen.Graphics.WebGPU` (native + browser surfaces).
- `Vixen.Platform.Web` completion: canvas, all input, IndexedDB providers, fetch provider with range
  requests, single-threaded job mode, size optimisation (trimming, SIMD, Brotli, lazy assemblies).
- `Samples/02` running in Chrome/Firefox/Safari (WebGL2 path already verified — see
  [spikes/web-webgl2](spikes/web-webgl2/RESULT.md)).
- `Samples/06-CanvasStress` — **P2, cuttable**: huge scrollable canvas, layers, tool overlays. Demoted
  from a phase gate because the editor is now the application-platform proof.
- `Vixen.Video`, VR/XR via `Silk.NET.OpenXR` (stretch).

**Exit:** deferred and forward+ both pass the golden-image suite. `Samples/02` and `Samples/06` run in
three browsers within the download-size budget. `Samples/06` holds 60 fps with a 4 K canvas and 20
layers.

---

## Phase 11 — Polish and 1.0 *(2.5 EM)*

- Every performance bar in [00](00-vision-and-principles.md) measured and green on real hardware across
  the IHV matrix.
- `PublicAPI.Shipped.txt` frozen for all packages; API review pass; obsolete/remove the leftovers.
- Documentation: DocFX API reference, a manual (getting started, per-subsystem guides, a UI framework
  tutorial, a Raven language reference, migration notes from Unity), and 12+ runnable samples.
- `dotnet new` templates for game, application, library, and editor plugin, verified from a clean
  machine on all six targets.
- Release automation end to end: tag → signed editor builds for three desktops + NuGet push + GitHub
  Release with changelog.
- Fuzzing corpora seeded and running nightly; soak tests (24 h editor session, 24 h game session) clean.
- A public issue-triage and support process, and a written compatibility policy.

**Exit:** a person who has never seen the repo can install the SDK, create a project, build it for all
six targets, and ship it — using only the published documentation.

---

## Delivering this solo, with AI assistance *(Q8)*

**The constraint:** one person implementing, using AI. That is the single biggest input to how this plan
should be *executed*, and it changes the shape of the roadmap more than any technical decision in it.

### The arithmetic, stated honestly

~46.5 EM of engine work plus ~6–9 EM of remaining Raven work is **~53 engineer-months of work**. I am
not going to claim an AI multiplier, because I cannot substantiate one and a number invented here would
propagate into planning decisions that deserve better. What is defensible is *where* assistance helps:

| Helps a lot | Helps little |
|---|---|
| Porting known algorithms with an oracle (the Yoga conformance suite is the ideal case) | Novel architecture decisions — the ones already made in these documents |
| Source generators, serializers, boilerplate, the 60-project scaffold | Debugging driver-specific GPU behaviour |
| Test suites, fixtures, golden files, benchmark harnesses | Performance tuning against real hardware |
| Mechanical refactors across many files | Anything needing a physical device in your hands |
| Documentation, XML docs, the manual | Long-horizon architectural consistency (this is *your* job; see below) |
| Reading Stride/Arch/PurrNet/Yoga to answer "how did they do X" | Deciding which of several defensible designs to commit to |

So: the total does not shrink to a quarter, but the *tedious* fraction — which in a project this
scaffold-heavy is large — compresses meaningfully. Treat the EM figures as work content, not calendar
time, and plan against milestones rather than a completion date.

### What actually matters: ship something useful early

The dominant risk for a solo multi-year project is not technical. It is **arriving at 60 % complete with
nothing shippable**, losing momentum, and stopping. Every mitigation below serves that.

Four milestones, each independently useful and publishable:

| Milestone | Phases | ~EM | What exists | Who it is for |
|---|---|---|---|---|
| **M1 — "it runs"** | 0–2 | 9.5 | Boots on 3 desktops, Vulkan triangle → meshes, ECS at 100 k entities, transforms, debug draw, job system, profiler | You, plus early technical onlookers. Publishable as `0.1.0` NuGet previews + `Samples/01`, `04`. |
| **M2 — "it is a game engine"** | +3, 5, 8 | +12 | Assets and bundles, PBR forward+ renderer, input, physics, audio, animation. Mobile bring-up. A programmer can build a real 3D game, code-only. | Programmers willing to work without an editor — a genuine and underserved audience. `0.4.0`. |
| **M3 — "it has an editor"** | +4, 6 | +11.5 | UI framework, editor shell, inspectors, scene view, asset editors. Content authoring without hand-writing everything. | Everyone else. `0.7.0` — the first release that looks like a product. |
| **M4 — "it is complete"** | +7, 9, 10, 11 | +13.5 | Node graphs, VFX, networking, deferred, Web, docs, templates | 1.0 |

M1 and M2 are the ones that matter psychologically and practically: **M2 is a usable engine**, and
reaching it is roughly 40 % of the total. If the project stopped at M2 it would still be a real thing
that works, which is not true of stopping mid-Phase-4.

### One reordering worth considering: renderer before UI

Currently Phase 4 (UI framework, 7.0 EM — the largest single phase) precedes Phase 5 (renderer, 4.5 EM).
For a solo build, **consider swapping them**:

**For swapping:**
- An engine with a renderer and no editor is useful and shippable. A UI framework with no renderer is
  not an engine.
- It defers the largest, least-visible phase until after there is something to show.
- The renderer carries more technical risk and more learning; doing it earlier de-risks more, sooner.
- The ImGui scaffold ([11](11-editor.md)) covers inspection needs perfectly well for longer.
- It brings M2 forward, which is the milestone that most protects against abandonment.

**Against swapping:**
- The editor arrives later, so content authoring stays code-only for longer, and authoring content is
  how a renderer gets exercised in anger.
- `Vixen.Ui` needs only 2D quad/text rendering, not PBR — so it is not truly blocked by Phase 5, and the
  dependency argument is weaker than it looks in both directions.

**Recommendation: swap them.** Ship a working renderer and a code-only engine (M2) before investing
7 EM in the UI framework. The phase contents are unchanged; only the order is. This is a judgement call
about risk and morale rather than about architecture, which is why it is a recommendation rather than a
decision written into the phase list.

### Practices that matter more when solo

- **These documents are the durable memory.** With no colleagues to hold context and AI sessions that
  start fresh, `docs/plan/` *is* the architectural continuity. Keeping it current when a decision changes
  is load-bearing engineering work, not documentation hygiene. The ADR register and the resolved-question
  table exist for exactly this.
- **External oracles are worth disproportionately more than usual.** The Yoga conformance suite, UAX#14/#29
  test data, `spirv-val`, the CSS Grid WPT subset, Arch's benchmarks, and the golden-image fixtures all
  share one property: *they judge correctness without you having to.* This is the specific defence against
  the failure mode of AI-assisted work — code that reads plausibly and is wrong. The testing strategy in
  [12](12-build-ci-and-testing.md) was written before this constraint was known and happens to be exactly
  right for it; lean on it harder rather than less.
- **The automated gates substitute for code review.** Warnings-as-errors, `AnalysisLevel=latest-all`,
  `CheckArchitecture`, `CheckApi`, the zero-allocation tests, the determinism tests. A solo developer has
  no reviewer; the build is the reviewer. Do not weaken these to move faster — they are the reason
  moving fast stays possible.
- **Finish subsystems.** Resist breadth. A fully tested `Vixen.Ui.Layout` with the Yoga suite green is
  worth more than five subsystems at 70 %, because 70 %-complete subsystems interact and their bugs
  multiply.
- **Keep `references/` cloned.** Stride, Arch, Yoga, PurrNet, SignalsDotnet, Flexbox are the highest-value
  context available for any "how is this normally done" question, and grepping them is faster and more
  reliable than recalling them.
- **Automate the boring safety.** Nightly fuzzing, soak tests, and the platform matrix run without you.
  Set them up in Phase 0 while the surface is small.
- **Use the cut list without guilt.** It is ordered ([below](#what-can-be-cut-if-it-must-be)) and
  networking — 5 EM, cleanly separable — sits at #2 precisely so there is an obvious lever.

### Revised expectation

Plan M1 → M2 → M3 → M4 as the unit of progress. Publish at each. Do not commit publicly to a 1.0 date;
commit to milestones. If M2 is reached and the project stops there, it will have produced a working
.NET game engine — which is a good outcome, and the roadmap is deliberately ordered so that every
stopping point is one of these rather than an arbitrary 60 %.

---

## Sequencing rules

These constraints matter more than the phase numbers:

1. **Raven gates Phase 5 only, and only loosely.** Raven's GLSL and SPIR-V emitters land in the same
   phase ([07](07-raven-shader-pipeline.md)), so the renderer gates on *one* codegen phase with no bridge
   and no intermediate. Engine Phases 0–4 need no shaders beyond a triangle/UI pair, which can be
   checked-in SPIR-V blobs, so engine work can begin as soon as Raven reaches its Phase 2.
2. **iOS/NativeAOT lands in Phase 3.** Non-negotiable. It is the cheapest possible insurance against the
   reflection debt that kills AOT ports.
3. ~~The Web spike happens in Phase 1~~ — ✅ **done up front**, and it paid for itself: it retired R1,
   corrected a size estimate that was an order of magnitude wrong, and surfaced the silent-WebGL1-
   downgrade trap that would otherwise have cost days in Phase 9. The general lesson is worth keeping:
   *spike the unknown before planning around it.*
4. **The Yoga conformance suite is ported before the flexbox implementation is written**, not after. A
   red suite driving the implementation is a completely different experience from writing 3 000 lines and
   then finding out.
5. **`TestApp` and `RecordingBackend` are built in Phase 1.** Every later phase's testability depends on
   them, and they cost days if built early and weeks if retrofitted.
6. **`Vixen.Ui` must never reference `Vixen.Engine`.** Checked by `CheckArchitecture` from Phase 0. The
   moment this is violated, the application-framework claim is dead and the violation is cheap to
   introduce and expensive to unwind.
7. **ImGui has a deletion date** (end of Phase 6) recorded as an exit criterion. Scaffolds without
   demolition dates become load-bearing.
8. **Every phase ends with a runnable sample**, and every sample stays running — the sample suite is
   part of CI, so a phase cannot break the previous phase's proof.

## What can be cut, if it must be

Stated in advance so the decision is made calmly rather than in month 30. Cut in this order:

1. `Vixen.Navigation`, `Vixen.Video`, VR/XR — genuinely optional, cleanly separable.
2. **Networking as a whole, slipped to 1.1** — it is its own package with no reverse dependencies, so
   deferring it is the cleanest 5 EM available. Cut only if 1.0 must ship sooner; the design in
   [16](16-networking.md) does not decay by waiting.
3. WebGPU backend — WebGL2 is sufficient for the Web scope actually committed to, and now verified.
4. `Samples/06-CanvasStress` — already demoted to P2 (Q3); the editor is the application-platform proof.
5. CSS Grid — flexbox covers the editor; grid is a convenience.
6. Deferred pipeline — forward+ covers the 1.0 use cases; deferred is a post-1.0 addition and the
   render-feature architecture accommodates it later without rework.
7. `AnimationGraph` node editor — a code-driven state machine ships first.
8. Full accessibility bridge — hooks stay, the platform bridges slip.
9. The Web target entirely — the most defensible cut if schedule pressure is severe, because it is
   ~15 % of the effort for the platform with the least clear payoff. Cutting it does not compromise the
   other five.

**Not cuttable, in any scenario:** the `.meta`/GUID model, addressables, the object database, the
capability-gated RHI, the Yoga conformance suite, the source-generator discipline, iOS AOT correctness,
and the `Vixen.Ui` ⇸ `Vixen.Engine` boundary. Each of those is either a foundation others build on or a
decision that cannot be retrofitted.
