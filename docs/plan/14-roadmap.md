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
- ✅ `Vixen.Platform.Native` — **both halves are built.** RID chain computed
  rather than looked up (a NativeAOT binary has no `runtimeconfig.json` to read one from), the
  `runtimes/<rid>/native/` layout searched before the operating system is asked, the versioned soname
  tried as well as the development symlink, and a `DllImportResolver` that answers before the default
  rules. 12 tests, all of them pure functions from a name to a list of candidates — a rule about
  Windows that can only be checked on Windows is a rule that is checked once a release.

  **The resolver is in force**, wired into `Vixen.Graphics.Vulkan`, which no longer calls
  `Vk.GetApi()` at all — and that turned out to be the whole of R11's desktop half. The six
  IL3000/IL3002 came from the default context `GetApi` builds, so removing the call removed them from
  the graph rather than merely from the execution path: rooting the backend now reports **zero**, and
  **no suppression was taken**. R11 predicted a suppression would be needed regardless and is
  corrected; the prediction was checked by putting the call back and watching all six return.

  **Acquisition, the half that was owed, is built.** `build/native-dependencies.json` pins each
  dependency and `nuke RestoreNativeDeps` fetches it: SHA-256 verified, only the named entries
  extracted, licence text copied out of the archive it was verified from, and nothing committed
  ([10](10-platforms.md) § Native binaries, R10). Its four failure modes are tested rather than
  described — see R10. **Owed:** it holds one dependency, MoltenVK for `ios-arm64`. The other five in
  R10's list are entries to add, and adding one is what will say whether the schema generalises.
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
- ✅ **`GoldenImages` target**, with five fixtures: clear, triangle, indexed quad with push constants,
  reversed-Z depth, and alpha blending. Rendered headless through the render graph, compared
  perceptually with a per-fixture tolerance, and — the part that matters — **generated on MoltenVK and
  verified against lavapipe**, so the tolerances are what cross-driver agreement needs rather than what
  one machine produces. A failure writes the rendering, the reference and a red-on-dimmed diff, which
  CI uploads. `--update-golden` rewrites the references, with a warning to look at them first.

  It justified itself before it had a reference image: every fixture initially rendered undefined
  memory, because the colour target was a graph transient nothing inside the graph read, so the graph
  correctly derived `StoreAction.DontCare` and discarded the picture. Importing the target is what says
  it outlives the frame. Verified by sabotage: inverting the depth comparison moves 76% of the
  reversed-Z fixture's pixels.

  The PNG codec is hand-written — ADR-015 keeps ImageSharp out of runtime assemblies, and a golden
  image nobody can open is one nobody will look at. Round-trip and filtered-input tested.

  **Owed:** the suite grows towards doc 05's ~40 fixtures with the rendering pipeline in Phase 4, and
  cross-backend equivalence waits for a second backend to compare against.
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

- ✅ `Vixen.Ecs`: archetypes, chunks, edge graph, queries + generator, `CommandBuffer`, change
  versions, managed component store. 90 tests. **Owed:** world serialisation — `WorldDigest` gives a
  canonical hash of a world's state, which is what the determinism test needed, but writing one to a
  stream needs the per-component serialisers of [08](08-asset-pipeline-and-addressables.md). Also
  owed: the `VIXEN_ECS_EVENTS` hooks.
- ✅ `Vixen.Ecs` scheduler: `ISystem`, nine phases, the conflict graph, DAG execution on the job
  system, DOT and Mermaid dumps. **Owed:** read/write *inference* — the attributes and the
  programmatic declaration are there, the generator that reads query bodies is not.
- 🟡 Transform hierarchy with dirty propagation. **Not depth-split**: a component's value takes no
  part in its archetype, which needs shared components. Roots are an archetype question and so are a
  sequential sweep; the levels below are walked through the child lists into reused per-depth
  buckets. One visit per moved entity either way, random access instead of sequential below the
  roots, and a steady state that allocates nothing.
- ✅ `Vixen.Engine`: game loop with fixed-step accumulator, `Behavior` with per-concrete-type bucket
  dispatch, `Transform` and camera façades, scenes, `SceneTag`, additive load/unload. 58 tests.
  **The dispatch generator turned out not to be needed** — `BehaviorBucket<T>` is closed at the
  `Add<T>` call site and its loop is the same monomorphic walk a generated method would be. The
  generator is still owed for `[Inspector]` metadata, which genuinely cannot be had another way.
- ✅ Prefabs: the subtree is captured into a world of its own, and instantiation is one
  `CreateMany` per distinct archetype plus a row copy each. The hierarchy is rebuilt from recorded
  indices rather than remapped, which also collapses the archetype count — without stripping the
  hierarchy components every depth would be its own archetype.
- 🟡 `DebugDraw` — the accumulator is built (lines, rays, boxes, spheres, axes, per-line lifetimes,
  aged in `PostRender` after a renderer would have drained). 9 tests. **The drawing is owed**, and
  needs a renderer; a subsystem written against this today needs no change when it arrives. The
  diagnostic overlays from [13](13-diagnostics.md) wait on the same thing.
- ~~ImGui debug overlay behind `VIXEN_DEBUG_IMGUI`~~ **cut, not deferred.** This plan already
  scheduled it for deletion in Phase 6, so building it would have meant standing up a second
  immediate-mode renderer, a font atlas and an input bridge in order to throw all three away. The
  editor shell is the thing that was ever going to show this information; `DebugDraw` covers the
  in-world half in the meantime. Phase 6's "delete the ImGui scaffold" step is struck with it.
- ✅ Ported Arch benchmarks in `Benchmarks/Vixen.Benchmarks.Ecs`. Two findings, both of which changed
  code: the obvious chunk loop keeps its bounds checks and is 34% slower than the generated
  per-entity forms (bounding by the span's own length makes it the fastest form instead), and
  `Create` was building a `ComponentSignature` per entity for a set fixed at compile time — caching
  the archetype per combination of type parameters made it 46% faster.
- ✅ Coroutines: `async Coroutine` with `await NextFrame()`, `await Seconds()`, `await Until()`, a
  frame-synchronous scheduler drained at four resume points, and cancellation on destroy. 25 tests.
  Two properties are measured rather than claimed: resumption order is the order the waits were made
  (which the determinism criterion needs), and a Release build allocates **zero** bytes per start
  against 160 for the same method written as a plain `async ValueTask`.
- ✅ `Samples/04-EcsStressTest`.

**Exit:** ✅ 100 k entities created and iterated — 70 ns to create, 0.50 ns per entity to iterate.
✅ 10 k-entity scene with a transform hierarchy, 10 000 frames, **zero Gen0 collections**, 514 µs mean
frame. ✅ `Behavior` lifecycle golden-ordering test green. ✅ Determinism test green — two worlds, one
input log, 10 000 steps, one running direct and the other through a command buffer's parallel writer,
compared by `WorldDigest` throughout.

**Not met:** the drawing half of `DebugDraw` and the [13](13-diagnostics.md) overlays, both waiting
on a renderer — Phase 2's goal line says "rendering nothing but debug lines" and nothing renders yet,
which is the one part of this phase Phase 4 has to carry.

**Owed inside the coroutines:** `WhenAny`, which needs a completion source of its own rather than the
sequential awaits `WhenAll` gets away with; and stopping a *single* launched coroutine, which the
design refuses on purpose — see [04](04-ecs-and-scripting.md) § Layer 3.

---

## Phase 3 — Asset pipeline and mobile bring-up *(4.0 EM)*

**Goal:** real content loads from bundles, and it does so on a phone. AOT correctness is proven before
the codebase is large enough for it to be expensive to fix.

- ✅ `Vixen.Core.Yaml`: node model, reader over YamlDotNet's event stream, Vixen-dialect emitter,
  tagged-polymorphic object mapping through the generated type registry, `.meta` model, envelope
  fast-scan parser, migration chain, `vx:` asset references, stable sub-asset ids, per-target
  override resolution. 73 tests, including a byte-identical round-trip over a fixture corpus and a
  2 000-iteration property test that found three dialect rules wrong.
- ✅ `Vixen.Editor.Core` asset database: GUID index, reverse-reference index, duplicate detection and
  repair, orphan quarantine. 26 tests; ten thousand assets scanned well inside doc 08's budget.
- ✅ `Vixen.Core.Imaging`: KTX2 container, mip chains with the three variants
  [03](03-core-foundation.md) asks for (sRGB-correct, alpha-weighted, normal renormalisation), BC1,
  BC3, BC4, BC5, BC7 and BC6H encoders, and the split-sum IBL pieces — SH-9 irradiance projection,
  GGX cubemap prefiltering and the DFG lookup table. 146 tests.

- ✅ `Vixen.Assets`: the content catalog — address to chunk, labels, globs, dependency closure,
  per-bundle download sizing, and the remote-over-local merge that makes a content update possible.
  Binary `catalog.bin` with a sorted string table, deterministic by construction, CRC-verified on
  read. 48 tests. (The object database, chunk format and bundle reader were already built in
  Phase 1's serialization work; this is the index over them.)
- ✅ Content references — the piece [03](03-core-foundation.md) deferred out of Phase 1.
  `ContentReference<T>` writes its chunk id and resolves its value on load, so a material names its
  textures rather than containing them and two materials sharing one get one object. Ambient
  resolution during deserialisation, which is defensible where an ambient asset *scope* was not:
  reading a chunk is synchronous end to end, so "the resolver in force" has one meaning throughout.
  `ObjectDatabase.ReadObject` and a by-type-id serializer index give the loader the way back from a
  chunk header to a type. The `[DataContract]` generator emits a registration for every closed
  `ContentReference<T>` it sees, which is what keeps it AOT-correct.
- ✅ Content build: `.vxgroup` addressable groups and `ContentBuilder` — packs chunks into bundles
  by the group's policy (together, separately, by label), names them with their content hash so a CDN
  cannot serve stale bytes, and emits the catalog. Deterministic, and its build log is too. 77 tests
  in `Vixen.Editor.Assets.Tests`, including the first end-to-end one in the repository: an address
  goes into the builder and an object comes out of the runtime.
- ✅ `Vixen.Assets` loading: `AssetHandle` with ref-counted claims, dependency closures claimed by
  their dependents, deduplicated deserialisation, explicit scopes, label and glob loading, and a
  local bundle source over the VFS. 64 tests over real bundles rather than stubs. **Deviation:**
  scopes take the loads rather than capturing them ambiently — doc 08's sketch does not survive an
  `await`, and the reason is written where the type is. **Owed:** content references, so a
  dependency's deserialised object is shared and not just its bundle and lifetime.
- ✅ Remote content: `IContentTransport` (HTTP, with byte ranges), `BundleCache`,
  `RemoteBundleSource` and `RoutedBundleSource`, plus the download surface [08](08-asset-pipeline-and-addressables.md)
  names — `DownloadSize`, `DownloadAsync`, `ClearCache`. The cache is keyed by content hash so a
  rebuilt bundle is an ordinary miss; downloads resume from a partial file; a server that ignores a
  byte range is detected and restarted rather than appended to; nothing is committed without matching
  both the catalog's length and its CRC. A cache *hit* checks length only, with `VerifyAsync` there
  for a caller who wants the full re-hash. 31 tests, over a transport that can be told to drop the
  connection, ignore ranges, answer from the wrong offset and serve corrupt bytes.
  **Enabler:** `IFileProvider.OpenAppend`, without which a resume has to buffer the whole partial
  download in memory.
- ✅ Content updates — step 2 of [08](08-asset-pipeline-and-addressables.md)'s boot sequence.
  `ContentUpdate` fetches the tiny hash file beside the catalog and downloads the catalog only when it
  names something new, then lays it over the shipped one. **Nothing the server does throws**:
  unreachable, half-published, built for another platform or corrupt each come back as an outcome
  with a reason and the best catalog on the device, because every one of them happens in the field
  and none is a reason for a game not to start. `Offline` and `Rejected` are separate outcomes
  because one fixes itself and the other does not — a distinction the plan did not name and a log
  needs. Nothing is cached until it has parsed *and* merged, so an unusable catalog cannot overwrite
  a usable one. 19 tests, including **this phase's exit criterion**: the server publishes a second
  build in which one of two packs changed, and the client fetches that pack and nothing else —
  asserted both by which URLs were requested and by byte count. `HttpContentTransport` got its own
  8 tests against an `HttpMessageHandler`, which is where the `206`/`200`/`Content-Range` reasoning
  lives and where it had none.
- ✅ `Tools/Vixen.ContentServer` — serves a content build directory over HTTP with byte ranges, so a
  phone can be pointed at a laptop instead of a CDN. All three range forms, a 416 for a range that
  starts past the end, and a synthesised `catalog.bin.hash` so a build directory can be served exactly
  as the build wrote it. 34 tests and **no socket**: the request logic is a class, the listener is a
  shell over it, which is how [12](12-build-ci-and-testing.md)'s "no real network in tests" rule is
  obeyed without leaving the interesting half unchecked. Path traversal is asserted against seven
  spellings including percent-encoded ones — and that makes `VirtualPath`'s "escapes above the root"
  rule load-bearing for a security property for the first time, which is worth knowing before anyone
  relaxes it. **Sabotage found dead code claiming to be a gate:** a containment check after the path
  was already normalised, which no mutation could make fail because a normalised path holds no
  `..`. Removed rather than kept, because a redundant check that reads as defence in depth
  invites the next reader to believe the real gate is optional.
- ✅ `BuildPlanner` — the step between "every asset has been imported" and "there is a build", which
  nothing had built: imports produce chunks and know nothing about addresses, `ContentBuilder` takes
  addresses and knows nothing about imports, and until now only tests bridged the two by hand. Reads
  the `addressable:` block, inherits `group` from the nearest folder that names one (labels are not
  inherited, and the README says why), and invents a reported `Default` group so a project that
  configures nothing still builds. 17 tests. **The check worth having:** an addressable asset
  depending on one with no address is an error, because the catalog records dependencies by address —
  so that chunk is in no bundle, and the build succeeds, ships, and fails at load on a device.
- ✅ **Addressing sub-assets** — the piece `BuildPlanner` owed, and the prerequisite for any importer
  that produces more than one thing. `ImportRecord` carries the `SubAssetId` beside each chunk id, and
  a sub-asset is addressed under its owner as `characters/hero#Hero_Mesh` — the name where a `vx:`
  reference carries the id, because an address is typed by a person and eight hex digits are not.
  Doc 08 records the form; it had specified the reference and not the address.

  **The part worth stating: a sub-asset needs a catalog entry, not just a place in a bundle.** A chunk
  is reachable only once the bundle holding it is mounted, and what mounts one is an address in the
  load closure — so the asset depends on its own parts, which both mounts them and deserialises them
  first, which is what lets the model's reference to its mesh resolve to the object. Without it a
  model in a `PackSeparately` group loads with its meshes in a file nobody opened. Verified by
  sabotage: dropping that dependency fails three tests, and dropping the claim on a sub-asset's
  address fails the collision test.

  A chunk that cannot be named refuses the whole asset — an artefact the sidecar does not declare, two
  chunks for one sub-asset, or an import with no main object. Shipping the nameable half is how a model
  reaches a device with its meshes missing. The import cache's format is version 2 for the pair it now
  stores, and a line it cannot parse is dropped rather than thrown on: it is a cache in `Library/` that
  a killed editor can truncate, and the cost of not understanding one line is re-importing one asset.
- ✅ `TextureImporter`, the first real importer: `IImageDecoder`, StbImageSharp and KTX2 decoders,
  and settings that say what a texture's bytes mean — which decides the transfer function, the mip
  filter's variant and the compressed format together. 63 tests in `Vixen.Editor.Assets.Tests`.

> **Doc 01's ImageSharp decision did not survive contact and is corrected there.** ImageSharp 4.0.0
> fails the build without a purchased licence key — an error from its own targets file, before any
> code compiles. A repository people are meant to clone and build cannot require that, so
> `Vixen.Editor.Assets` took doc 01's own stated fallback and uses `StbImageSharp`, which is public
> domain. The swap cost one class: nothing in the importer, the pipeline or the tests moved, which is
> `IImageDecoder` earning itself on its first day. Coverage shifted rather than shrank — Radiance HDR
> arrived, `.exr`, `.tif` and `.webp` left.

> **Two boundaries in this assembly are worth stating in the plan rather than only in its README.**
>
> **ASTC and ETC2 have no managed encoder and are not getting one.** Doc 03 already said native was
> the right call; this makes it load-bearing. Both formats keep their sizes, block extents and KTX2
> numbers so a build with `astcenc` restored can ship them, and `BlockCompressor` names what is
> missing rather than reporting an unknown format. **BC7 and BC6H write one mode each** — the
> single-subset ones — which is valid output at the right size and a real quality ceiling on blocks
> with an edge through them; doc 01 already registers `ispc_texcomp` for the rest.
>
> **Nothing here has been checked against an independent implementation.** Every container and block
> layout is written from its specification and asserted byte-for-byte against a hand-computed
> example, which catches a misread of a field's position and not a misunderstanding of what the field
> means. Running Khronos's `ktx validate` over the KTX2 output and a reference decoder over the BC
> output is owed, and until then "valid" is a claim about intent. The IBL half is in better shape:
> irradiance, solid angles and the roughness-zero BRDF all have closed forms to test against, which
> is why the exact solid-angle formula replaced the midpoint one during this work.

> **The AOT wall arrived on day one of this phase, which is what it was scheduled early for.** The
> obvious object binder needs `Array.CreateInstance(elementType, n)`, `MakeGenericType` and
> `Activator.CreateInstance(Type)`; all three are `RequiresDynamicCode`, this repository compiles
> `IL3050` as an error, and the build refused all three. A binder built on them would have worked on
> a desktop and thrown on a phone, and would have been found in this phase's last week rather than
> its first.
>
> The fix is the principle the engine already runs on: a generator saw the type in the source, so a
> generator writes the constructor. `CollectionFactory` holds one per collection type reachable from
> any described member. Two things in [08](08-asset-pipeline-and-addressables.md)'s sketch were
> unbuildable as written and that document now says so — `ImmutableArray<T>` became `T[]`, and
> `TargetOverride<T>` became a node-level merge rather than a generic partial record. Reaching
> `init`-only setters through `[UnsafeAccessor]` was a third prerequisite the plan had not foreseen.
- ✅ `Vixen.Editor.Assets`: `TextureImporter`, `ModelImporter` (Assimp), `AudioImporter`,
  `NativeFormatImporter`, `DefaultImporter`. Out-of-process worker (`Tools/Vixen.AssetCompiler`).

  **`DefaultImporter` already existed under doc 08's name for it.** `RawImporter` is the fallback —
  verbatim copy, an address for anything nothing else claimed — so this is a note rather than a
  second class under the other name.

  **`NativeFormatImporter`'s output is the dependency graph, not a conversion.** A `.vxmat` is
  already in the engine's format; what is *not* already known is what it points at, and without that
  a material's artefact is correct the day it is built and stale for ever after. It walks the node
  tree rather than scanning the text, because a GUID in a comment is not a reference and a dependency
  on one would never change and never break anything — the kind of wrongness with no symptom. What it
  writes is the document, because the YAML → runtime-chunk step is `MaterialCompiler`'s and putting it
  here would move the compiler's decisions somewhere the artefact cache key cannot see them.

  **`AudioImporter` brought `Vixen.Audio` into existence** — the clip type alone, because the name is
  in the chunk and moving it when the backend lands would invalidate every audio artefact ever built.
  The WAV reader is written rather than taken: a PNG decoder is a compression implementation and a WAV
  file is a chunk header and then the samples. Four things about that format each cost a test that
  fails when its line is removed — the chunk walk (a DAW puts `LIST`, `bext` and `cue ` between the
  header and the samples, and a fixed 44-byte seek reads metadata *as audio*), the odd-length pad
  byte, 8-bit being unsigned, and `WAVE_FORMAT_EXTENSIBLE` hiding the real format code in a GUID.

  **`ModelImporter` is the first importer that produces more than one thing**, and so the first real
  consumer of the sub-asset addressing `BuildPlanner` has had since it was written. Every matrix is
  transposed on the way in: Assimp stores a column-vector matrix row-major and Vixen a row-vector one,
  so a field-for-field copy assembles every hierarchy inside out — consistently and quietly wrong
  rather than obviously broken. There is no axis conversion, deliberately; a Z-up file arrives Z-up
  and correcting it is a rotation an artist can see. Parts name their meshes rather than indexing
  them, because an exporter reorders meshes whenever a material is added.

  **The worker's promise is crash isolation and not speed.** An importer that *throws* was already
  caught; one that takes its process down — a malformed FBX inside a C++ library — is not catchable
  from inside that process. The seam is `IImportExecutor` in `Vixen.Editor.Assets`, so one asset's
  worth of work crosses the boundary and the cache, the key and the sidecar stay in one copy either
  side. Doc 08's parallelism is **owed**: the pool runs N workers and `ImportPipeline` hands them one
  job at a time, because its sequential path-ordered loop is what guarantees a dependent sees its
  dependency's new artefacts. `--isolated` is therefore off by default.

  **A silent serializer bug was found by needing to write these artefacts.** Every immutable struct
  in the engine — every type in `Vixen.Core.Mathematics` — generated a serializer with no members at
  all, because the fallback dropped `readonly` fields along with computed properties and left nothing
  for a constructor to match; it wrote two varints and read every component back as zero, with no
  diagnostic, and nothing had ever written a `Vector3` so nothing had noticed. A property with no
  setter is derived and a `readonly` field is not, so the computed ones now come off first and the
  match is retried. That is the same failure, in the same place, as the `init`-accessor one the
  compositor work found independently and fixed through `[UnsafeAccessor]` — which is why `VXS0102`
  has still never been reported: both of the shapes that hit it are handled rather than warned
  about.
- `Vixen.Core.Imaging`: KTX2, BCn/ASTC/ETC2 encoding, mip generation, IBL prefiltering.
- Content build: compiler DAG, `ObjectDatabase` (file + bundle backends), bundle packer, catalog.
- `Vixen.Assets` runtime: `AssetHandle`, ref counting, scopes, label/glob loading, streaming manager.
- Addressable groups (`.vxgroup`), local + remote providers, `Tools/Vixen.ContentServer`.
- ✅ `Vixen.Sdk` MSBuild integration so `dotnet build` does content builds — import before
  `CoreCompile`, content build after `Build`, the result copied beside the binary and into a publish,
  and `Clean` taking back what it copied and nothing else. Both consumption forms
  (`<Project Sdk="Vixen.Sdk">` and a plain `PackageReference`) land on one pair of files. 7 tests,
  each of them a real `dotnet build` of a real project, because there is no way to test MSBuild
  integration except by running MSBuild — they are the slowest tests in the repository and that is
  the price of the only kind that can catch what they catch.

  **Doc 08's point 6 is met and is the reason the CLI grew a diagnostic format.** The tool is invoked
  with `--format msbuild`, so what an importer said arrives as `<absolute path>: error VX1001: …` — an
  entry in the IDE's error list rather than prose from a subprocess. The path has to be absolute (a
  relative one resolves against the build's directory, not the project's) and the code has to exist or
  MSBuild reads the line as prose. Codes are registered in
  [`docs/manual/diagnostic-codes.md`](../manual/diagnostic-codes.md), which is new and follows the
  log-event register's rules.

  **`content build --no-import` exists for exactly one caller.** The SDK imports as its own step so
  generated C# can precede the compiler, so the content build would otherwise repeat a full scan and
  ten thousand decisions inside one build. The flag follows the same condition as the target, or a
  project that turned the import step off would pack what nothing had imported.

  **The rule the first real build found**, written down in the targets and the README: anything
  derived from another property is computed in the `.targets`, never in the `.props`. A `.props` is
  imported before the consuming project's body, so a plain default is safe there — an unconditional
  assignment in a `.csproj` overwrites it — but a property computed *from* one has already been
  computed by then and nothing recomputes it. `VixenToolCommand` derives from `VixenToolPath` and is
  what proves it. **The first sabotage written to verify this was wrong** and is recorded as such:
  moving the `VixenTarget` block into the `.props` changes nothing, for the reason above. Moving
  `VixenToolCommand` fails six of the seven tests.

  **Owed, and named:** the CLI is not shipped inside the package, so a consumer still needs `vixen`
  restored or installed and doc 08's "restores the Vixen tool versions matching the referenced
  packages" is not met; ~~nothing generates C# yet, so the `CoreCompile` hook is ordering without
  cargo until Phases 4d and 5~~ — **overtaken in 4d, and not the way this expected**: VXML is
  compiled by a Roslyn source generator, which needs no ordering at all because it runs *inside*
  `CoreCompile`. The hook is still there and still carries nothing; what would use it is the `vixen`
  CLI path for a build that wants the generated C# on disk, which doc 08 also names and which is
  owed. Platform packaging (APK assets, iOS bundle, `wwwroot`) waits for those platforms; and a
  build-plan diagnostic carries no file, because `ImportDiagnostic` has no path
  field — its messages name the asset in their text, so only the IDE's jump-to-file loses.
- ✅ `Vixen.Cli` — **every verb the plan names is built**: `import`, `content build`, `content serve`,
  `doctor`, and now `new`, `build` and `run`. The first four are the whole pipeline
  from a terminal, which is what the phase's own gates need: an incremental import, a deterministic
  content build, and a laptop a phone can be pointed at. 41 tests, driving the real parser over a
  real project on a real disk — including **the determinism gate at the level a person runs it**: two
  builds of one project, byte for byte, catalog and bundles alike.

  What the CLI made visible rather than invented: nothing had ever loaded a `.vxgroup` from disk (the
  planner took groups as an argument and only tests supplied them), and nothing had ever written a
  content build to a directory — every test until now held bundles in memory. Both are the kind of
  gap that only a tool with a working directory finds.

  Three decisions worth naming. **`content build` imports first**, always, because it is incremental
  and a build that packed a stale artefact because somebody forgot a step is a bug report about the
  wrong thing. **The build writes `catalog.bin.hash`** even though `Vixen.ContentServer` synthesises
  one, because the shipping path is a CDN and a CDN synthesises nothing. And **`doctor` repairs
  nothing** — it is the first caller of `ScanOptions.ReadOnly`, which was built for exactly this and
  had none.

  **`new`, `build` and `run` landed once the two things they were waiting for existed.** `new`
  scaffolds `<Project Sdk="Vixen.Sdk/x.y.z">` — a template listing package references would be wrong
  one release later — and `build` runs the content build before `dotnet publish`, which is the whole
  reason it is a command rather than a note. Verified end to end: `vixen new game` then `vixen run`
  scaffolds, imports, builds content, publishes and launches the result.

  Two things fell out of doing it. `build` turns the SDK's own import and content steps off, because
  it has just done them — leaving them on repeats a full scan inside the publish *and* demands the
  `vixen` tool on the PATH of a process the tool itself started. And the variant travels as
  `-p:VixenVariant` rather than as the compiler configuration, because doc 17's variants are
  orthogonal to Debug/Release and a Development build is optimised.

  **Owed, and named:** nothing is signed, notarised or packaged beyond what `dotnet publish` emits, so
  doc 17's DMG/IPA/AAB table is still Nuke's. The `app`, `plugin` and `tool` templates are not written
  — `app` is the practical test that `Vixen.Ui` has no `Vixen.Engine` dependency and should wait until
  there is enough of `Vixen.Ui` to scaffold against. `vixen doctor systems` from
  [04](04-ecs-and-scripting.md) needs a game assembly to load, and the GPU and driver checks would
  put a graphics dependency in a tool that today needs none.
- 🟡 **The NativeAOT gate** — `nuke CheckAot` publishes every runtime assembly ahead of time with all
  of them **rooted**, so ILC compiles every method rather than the few a probe happens to reach, and
  fails on any trim or AOT warning. `Tools/Vixen.AotProbe` is its subject.

  **Every `Core/` assembly, `Vixen.Platform`, `Vixen.Platform.Headless` and `Vixen.Graphics.Null`
  publish with zero warnings, and the binary runs.** That is the phase's "prove AOT correctness before
  the codebase is large enough for it to be expensive" goal, met for the engine's own code — and it is
  a real result rather than a hopeful one because rooting is what makes it one: the same probe relying
  on reachability from `Main` produced a 1.3 MB binary against 8 MB rooted, which is the measure of how
  much a reachability-only gate would have left unexamined.

- ✅ **The iOS half of the gate — and the phase's headline exit criterion, met.** `nuke CheckAotIos`
  publishes the same rooted set for `ios-arm64`, which is the target that matters because
  [10](10-platforms.md) makes iOS NativeAOT-only. It produces an `.ipa` holding a 7 MB native binary
  and **no managed assemblies at all**, with zero trim and zero AOT warnings under
  `TreatWarningsAsErrors`. So: *iOS NativeAOT publish with zero trim/AOT warnings* is **met for the
  engine's own code**, which is everything except the graphics backend.

  The probe is a second project outside `Vixen.slnx`, because a `net10.0-ios` project cannot be
  evaluated at all without the `ios` workload and putting it in the solution would break `dotnet
  build` for every developer and CI leg that is not a Mac with Xcode. The cost is that `CheckFormat`
  does not see its two files.

  ✅ **`Vixen.Graphics.Vulkan` is now in both probes' rooted sets, and both gates are green.** It used
  to break them in two different ways — see R11, which records both and the traps in each. In short:
  the desktop's six IL3000/IL3002 came from `Vk.GetApi()` and went away when the call did, with no
  suppression; and on iOS the same change made the link error vanish *without fixing anything*, which
  had to be caught by asking `nm` whether MoltenVK was actually in the binary rather than by trusting
  the green tick. MoltenVK is now linked, force-loaded and — separately, and just as necessary — has
  its 431 entry points exported, read out of the archive at build time by
  `Vixen.Platform.Native/build/MoltenVK.targets`.

  **Not the same as working on a phone**, and worth not overstating: what is proven is that the
  symbols are defined and exported in the shipped 11.5 MB binary and that the runtime path asks for
  them through `NativeLibrary.GetMainProgramHandle()`. Running it needs `Vixen.Platform.iOS`.

  **Owed:** each gate publishes for one RID, so covering three desktop operating systems means one CI
  leg each; Android is not gated yet, and should be gated on its *default* runtime rather than on
  NativeAOT, which `warning XA1040` calls experimental and not suitable for production — the plan only
  ever committed to NativeAOT for iOS.
- 🟡 **`Vixen.Platform.iOS`** — built, and not yet run. UIKit behind `IPlatform`: a `CAMetalLayer`-backed
  view for MoltenVK to present to (a `UIView` cannot be given one after the fact, which is why the view
  is a type rather than a configured `UIView`), the lifecycle translated into `MobileLifecycle`, and a
  `CADisplayLink` where a desktop has a `while` loop — `UIApplicationMain` never returns, so there is
  nowhere to put one, which is the first time doc 17's "every step of the boot path is public" pays
  for itself. Multi-touch, the soft keyboard with its rectangle read from the system notification
  rather than guessed, `UIPasteboard` text, `UIAlertController`, battery and thermal state.

  **Refused rather than approximated**, each with the reason in the README: file dialogs (iOS has a
  document picker returning a security-scoped URL, not a path), clipboard images, gamepads, and
  hardware keyboard and trackpad.
- 🟡 **`Vixen.Platform.Android`** — built, and not yet run. A `SurfaceView` giving Vulkan an
  `ANativeWindow` through `ANativeWindow_fromSurface`, the activity lifecycle, the `Choreographer`
  driving frames, multi-touch, `AAssetManager` behind an `IFileProvider`, clipboard, soft keyboard,
  battery and thermal.

  **The ordering on the way down is the design**, and doc 10 said why in advance: the surface is
  destroyed under a running renderer, and `surfaceDestroyed` may not return until nothing is using it.
  `OnPause` removes the frame callback first, `OnStop` raises `Suspending` while the window is still
  valid, and only then does the surface go — so no handshake is needed and no frame is ever in flight
  across the teardown. `IWindow.Surface` reports `CanPresent` false in between, which is a state an
  application spends real time in rather than an error.

  **Owed:** the GLES fallback and the device-capability deny-list doc 10 asks for, key translation
  (`Key` is a physical position by contract and Android's keycodes are a mix of positions and labels,
  so the table is the easy part), safe-area insets, and sensors.

  > **Neither can be in `Vixen.slnx`, and that costs something worth naming.** A `net10.0-ios` or
  > `net10.0-android` project cannot be *evaluated* without its workload — not built, evaluated — so
  > either one in the solution breaks `dotnet build` outright for a developer or a CI leg without it,
  > and iOS additionally needs macOS and Xcode. `nuke CompileMobile` builds them instead, skipping the
  > iOS half off macOS rather than failing. The cost is that neither is seen by `Test`, `CheckFormat`,
  > `CheckArchitecture` or `Pack`.
  >
  > **Which is why the testable half is not in them.** The finger bookkeeping (`TouchTracker`) and the
  > lifecycle state machine (`MobileLifecycle`) are in `Vixen.Platform`, where the solution does see
  > them, with 19 tests. That is not a workaround: both are genuinely shared — UIKit identifies a touch
  > by an object address and Android by a renumbering pointer index, and neither is a number an
  > application should see — and the transitions worth testing are the ones nobody exercises by hand,
  > like a repeated suspend that must not raise twice or a memory warning at an unchanged level that
  > must.
- **NativeAOT publish in CI on every PR from here on** — the gate exists (`nuke CheckAotIos`); the CI
  leg does not.
- ✅ `Samples/07-AddressablesRemote` — the phase's remote-content exit criterion, made watchable.
  Builds content, serves it with the same `Vixen.ContentServer` that `vixen content serve` runs,
  downloads it into a cold cache, republishes with one asset changed, and downloads again: **144.6 KB
  then 48.6 KB**, with `characters/hero` reported as a cache hit and every request listed. It fails
  with a non-zero exit code if the update is not cheaper than the cold start, because a demo that
  quietly stops demonstrating is worse than none.

  The saving is not diffing: bundles are named by content hash, so an unchanged group builds to
  identical bytes and therefore to a file the client already has. Five things had to be right and each
  was wrong first — the README lists them, because each is a trap with a misleading symptom. The best
  of them: the payload was a run of one repeated byte, LZ4 turned 96 KB into 484, and the measurement
  became one of the compressor rather than the update.

**Exit:** `Samples/01` runs on a physical Android device and a physical iPhone. 🟡 **It runs on the
iOS Simulator and the Android emulator** — same game class, one head each, and a screenshot of the
triangle on both. The Android emulator must be started with `-gpu swiftshader_indirect`: its
host-GPU path reports every step succeeding and presents nothing, which is the emulator's and not
the engine's. Physical devices are what is left: an iPhone needs a provisioning profile, which is an
Apple account rather than a build setting, and no Android device is attached.

Running it is what found the bug the AOT gate could not — a delegate-to-function-pointer thunk that
iOS will not JIT; see R11. That is the phase's whole thesis arriving on schedule, just later in the
phase than the gate suggested. iOS NativeAOT publish
with **zero** trim/AOT warnings. Content build determinism gate green across three OSes. Remote content
update fetches only changed bundles (asserted by byte count). Incremental import of one texture < 1 s
in a 10 k-asset fixture project.

**Where the exit criteria stand.** ✅ **iOS NativeAOT publish with zero trim/AOT warnings — met for
the engine's own code**, by `nuke CheckAotIos`, which produces an `.ipa` of native code with no managed
assemblies in it. The graphics backend is not in that set and cannot be until MoltenVK is linked
statically; see the gate above and R11. ✅ Remote content update fetches only the changed pack, asserted by
URL and by byte count. 🟡 Content build determinism is green between runs, and green between two
projects **at different paths, whose assets were created in a different order and carry different
GUIDs** — which is what would actually break across operating systems, tested without needing a second
one: an absolute path reaching the catalog, an enumeration order leaking into it, or an authoring
identity being shipped each fail it. (It also asserts doc 08's own sentence, which nothing had
checked: the GUID never appears in a shipped build.) Running the comparison across three real runners
still waits for the CI legs.
🟡 The import budget is **measured, and it lands on the line rather than under it** — see below.

🟡 **Both mobile platforms and both AOT gates are built and green**, and the sample runs on the iOS
Simulator and the Android emulator. What is left is a physical device each, and CI legs for the
gates — see the bullets above, which supersede the sentence that used to stand here saying none of
this was started.

✅ **The boot path mounts content**, which is the goal sentence's own word and was the last thing
missing. `Services.Assets` is an `AssetManager` over the content build the application shipped with,
found at `/app/Content` — **through the virtual file system rather than through a path**, because
`IFileSystemHost.ApplicationDirectory` is documented as empty where content is not a directory at
all, which is an APK's assets and an iOS bundle. Going through `/app` means Android's
`AAssetManager` answers the same call a desktop directory does.

No content is not an error and a catalog that will not read is reported rather than thrown: a
sample, a batch tool and a test each have nothing to load, and a catalog truncated by a failed
download happens in the field to an application that then has to be able to say so.
`--vixen-loose-content` is honoured, with doc 17 Q5b's other half — the warning repeats every sixty
seconds, because one line at startup has scrolled away by the time anyone reads a QA build's log.

✅ **The host drives the engine.** `Services.Engine` is an `EngineLoop`, on by default because
`VixenApp.Run<TGame>()` takes a `Game`, and `config.UseEngine = false` for the heads that want the
host without a world. Its frame runs *before* `Game.OnUpdate`, which is where an application reads
the world it is about to render. It is handed the unscaled delta and `TimeScale` separately, because
passing the scaled value with the scale squares it — and `VixenApplication.TimeScale` reaches both
clocks, so a paused game owes no simulation steps rather than paying them all at once when the menu
closes. Nothing had ever checked that the two composed: both were built and tested alone, and
nothing in the shipping path referenced `Vixen.Engine` at all.

> **The 10 k-asset import budget, measured rather than assumed.** A fixture project of 10 200 assets
> (1 000 of them real PNGs through `TextureImporter`) imports cold in ~6 s. Changing one texture and
> re-importing cost **2.0–2.3 s** against a budget of one second, and — the part that pointed at the
> cause — a run where *nothing* had changed cost the same. The budget is not about importing; it is
> about deciding not to.
>
> Three things were wrong, all of them per-asset and all of them on the do-nothing path. Every source
> file was **opened and hashed twice**, because an asset's own source is in its declared file
> dependencies as well as being `sourceHash`. Every asset's settings were **bound into an object**
> before the cache was consulted, to be thrown away. And the whole decision pass ran **sequentially**,
> at about 104 % CPU on a machine with cores to spare, while the scan beside it was already parallel.
>
> With the source hash reused, the binding moved after the check, and deciding done for the whole
> project at once, the same change now costs **0.88–1.2 s, median ~1.05 s** — about half, and *at* the
> line rather than beyond it. Measured on an Apple Silicon laptop with other work running, which is
> why the spread is wide and why the honest reading is "met on a quiet machine, without margin".
>
> **What is left is the scan**: ~630 ms of it, two-thirds of the remaining cost, opening and
> fast-scanning ten thousand sidecars. It cannot be skipped with `AssetDatabase.IsStale()`, and the
> reason is worth writing down — that heuristic compares the number of `.meta` files and the newest
> one's write time, and **a newly added source file has no `.meta` yet**, so it moves neither. The
> heuristic is sound for the running editor it was built for, which watches the filesystem; a command
> whose whole job is "import what changed" cannot use it. Making the scan cheaper means trusting the
> persisted index per entry on an unchanged mtime, which is `AssetDatabase`'s own performance story
> and is owed.
>
> **Also owed: the gate itself.** These numbers come from a fixture generated by hand for the
> measurement. A repeatable version belongs with the benchmark suite, the way the layout gates in
> Phase 4a do, and until it exists this is a measurement rather than a gate.

> This phase is deliberately early and deliberately painful. Every plan that defers iOS discovers in
> month 30 that some subsystem needs reflection, and pays for it ten times over.

---

## Phase 4 — UI framework *(7.0 EM)*

**Goal:** a standalone Vixen application with a real interface. The largest phase; sequenced so each
sub-piece has its own gate.

**4a — Reactive and layout (2.0 EM)**
- ✅ `Vixen.Ui.Reactive` — `Signal<T>`, `Computed<T>`, `Effect` with the frame-phase
  `EffectScheduler`, `CollectionSignal<T>`, `LinkedSignal<TSource,T>`, `AsyncComputed<TRequest,T>`,
  `Untracked`, `Batch`, and the owning-thread check. 63 tests. **Both gates are met and measured
  rather than claimed:** the diamond `a → b, a → c, b+c → d` evaluates its join exactly once per
  change, and a thousand write-and-flush cycles over a settled graph allocate **zero** bytes. A
  brute-force oracle — random DAGs, random writes, every value compared against what recomputing
  from the leaves would give — is the correctness net doc 14 argues for. Verified by sabotage:
  disabling edge pooling and disabling the equality short-circuit each fail their own test.

  Four things differ from [09](09-ui-framework.md) and that document now says so: edge storage is
  pooled arrays rather than slices of a shared `ChunkedArray` (a slice must be contiguous and chunks
  are not, so an arena needs a per-node cap or a second allocation path); the thread check is a
  runtime opt-in rather than a `DEBUG` assertion; `Batch` turns out to be about flush *ordering*
  rather than coalescing, because queued effects and lazy computeds already coalesce; and
  `AsyncComputed` had to split into a tracked synchronous request and an untracked asynchronous load,
  because dependency tracking cannot survive an `await`.

  **Owed:** liveness is decided per node and the notification walk is per write, which is a tight
  loop over an array that no measurement has yet asked to improve; and `Flush()` has no caller until
  `UiSystem` arrives in 4d.
- ✅ `Vixen.Ui.Layout` — the SoA store and **the complete flexbox algorithm**. `LayoutTree` holds
  styles, results, links and node state as parallel `NativeArray`s with a shared arena for child
  ids; `CalculateLayout` is the port of Yoga's `CalculateLayout`, `AbsoluteLayout`, `FlexLine`,
  `Baseline`, `PixelGrid` and the measure cache. 552 tests.
  Two departures from [09](09-ui-framework.md), recorded there: children are a contiguous run
  rather than a linked list, because the algorithm addresses them by index inside its inner loops
  and a list would make several O(n) passes O(n²) on the widest nodes; and a style is ~400 bytes
  rather than 120, because the estimate predated counting the edge shorthands and the
  writing-mode-relative pair.
- ✅ **Yoga's conformance suite is ported — 534 fixtures — and it is green.** Committed before the
  implementation, which is what sequencing rule 4 asks for and which is why it was able to do its
  job: 530 of 534 passed on the first run. `Tools/Vixen.YogaTestGen` translates Yoga's generated
  C++ into xunit against `LayoutTree`; the output is committed because CI has no reference clone.
  A line the translator does not recognise drops the whole fixture and says so rather than guessing.
  Nine fixtures are skipped, all `display: contents`, which doc 09 puts outside the scope; each is
  named in the generated file's header.

  Of the four fixtures that failed on the first run, three were a careless port of Yoga's *test
  helper* rather than of the algorithm, and one was a real rule the port had missed: a degenerate
  `aspect-ratio` behaves as `auto` rather than being divided by (css-sizing-4).

  **And a limit of the oracle, found by sabotage and worth recording.** Deleting the CSS Flexbox
  §4.5 automatic-minimum floor leaves all 534 fixtures green — Yoga's generator emits no fixture
  that shrinks a measured leaf past its own content, so ~150 lines implementing a specification
  section had no test over it. `AutomaticMinimumSizeTests` is hand-written to close that, and two of
  its four cases fail without the floor. External oracles are worth what doc 14 says they are worth;
  knowing where they stop is part of using them.

- ✅ `Benchmarks/Vixen.Benchmarks.Ui` and the layout-pass gates. **A settled tree allocates zero
  bytes per frame** — asserted by three tests and by the benchmark at 110 001 nodes — and **an
  unchanged tree costs 11 ns whatever its size**, because the pass never descends past the dirty
  flag. An incremental frame at the 10⁴ elements doc 00's editor bar names is 1.16 ms.

  Two findings, one fixed and one recorded.

  **Fixed:** the CSS §4.5 min-content probe was calling measure functions directly, bypassing the
  measurement cache — a per-frame text measurement of every flex item, which is precisely what doc
  09 says that cache exists to prevent. Min-content size depends only on the subtree and on what
  percentages resolve against, so it is now cached per node and per owner size and invalidated by
  the dirty flag. A test asserts an untouched leaf is measured once and never again.

  **Also fixed, and it turned out to be two bugs rather than one optimisation.** The frame cost was
  never the algorithm: instrumented, a one-leaf change in an 11 001-node tree runs it 21 times
  against a cold pass's 22 001. What cost 60–70 % of an incremental frame was the **pixel-rounding
  pass walking the whole tree every time**. Skipping unchanged subtrees needs a stamp for whether
  the algorithm actually ran for a node — a cache hit does not rewrite its children — and a record
  of the absolute offset it was last rounded against, because rounded edges derive from *absolute*
  positions and an ancestor moving half a pixel changes every descendant without any of them being
  dirty.

  Writing that shortcut and then writing a property test for it — every node of an incrementally
  updated tree against a second tree built from the same styles and laid out cold — found that the
  two **already disagreed, with the shortcut disabled**. The cause is in the reference design:
  rounding writes back into the position and size that the next pass reads for every node it does
  not recompute, so an incremental layout drifts from a cold one by up to half a pixel per level.
  The rounded result now lives in its own fields and the raw layout is never overwritten, which
  makes rounding a pure function of the raw layout — correct, and what makes the shortcut sound.
  An incremental frame is **2.4× to 3.3× faster**, and identical to a cold one to the bit.

**4b — Styling (1.5 EM)**
- ✅ **ExCSS verified as the front end before anything was built on it** —
  [spikes/vcss-excss](spikes/vcss-excss/RESULT.md), following sequencing rule 3. ADR-009 stands: the
  selector tree is fully typed and reachable, so the selector work it saves is real. One finding that
  changes what has to be written — **ExCSS 4.3.2 does not parse `@layer`**, which arrives as an
  unknown rule with its text intact, so Vixen reads the prelude and re-parses the body. Doc 09 and
  doc 01 now say so. Cheap to know now; expensive to find in the middle of the cascade.
- 🟡 `Vixen.Ui.Styling` — **the selector engine is built and its gate is green; the cascade is not.**
  `StyleTree` is the element store a selector questions; `SelectorCompiler` is the ExCSS visitor
  ADR-009 buys by taking the dependency; `SelectorMatcher` matches right to left with the 128-bit
  ancestor bloom in front of every descendant combinator; `RuleIndex` buckets by the rightmost
  compound's most selective part. 15 tests.

  **The selector-matching oracle gate is met:** over four hundred randomised trees and stylesheets,
  the rules the bucketed-and-bloomed path finds are exactly the rules a brute-force pass finds.
  Verified by sabotage — dropping the bloom's second hash fails four tests. Two things the oracle
  cannot say are tested separately: whether either path is right about CSS (one test per selector
  kind, combinator and attribute operator), and whether the index is any use at all (fifteen hundred
  rules, of which one element reaches three).

  Two bugs it found, each of the kind that does not announce itself. A defaulted struct id silently
  meant "element zero", so every root without an explicit parent became a child of the first element
  ever made. And nested `:not()`/`:is()` selectors interleaved with the contiguous ranges being
  built around them.

  ⚠ **A third finding recorded here was wrong, and is corrected.** This entry and the commit said
  `:has()` had been compiled as `:not()`, on the reasoning that both carry an `.Inner` and that
  matching on shape rather than type is how a selector comes to mean its own opposite. Checking it
  against the library rather than against the story: `HasSelector` and `NotSelector` are siblings
  under `StylesheetNode`, neither derived from the other, so `case NotSelector` never caught a
  `:has()`. It was dropped correctly the whole time. The actual defect was the diagnostic, which
  read `HasSelector is not supported` — an internal type name of a third-party parser shown to
  someone whose stylesheet says `.bad:has(.x)`. A smaller finding, and a real one: diagnostics now
  quote the selector as written. The general point is the one worth keeping — **a finding is not
  verified until it has been reproduced against the thing it is a claim about**, and this one was
  written up from a plausible reading of a stack trace instead.

- 🟡 **The cascade is built and its gate is green.** `StyleSheetLoader` (rules, `@layer`, `@media`),
  `CascadePrecedence`, `ComputedStyle` with interning, `StyleResolver` (cascade, inheritance,
  `var()`, style sharing). 74 tests in total for the project.

  **The style-sharing gate is met:** over three hundred randomised trees and stylesheets, resolving
  with the sharing cache produces exactly what resolving every element separately produces. Verified
  by sabotage twice — dropping the element state from the key and dropping the parent from it each
  fail the property. The property carries its own vacuity guard: one position-dependent rule turns
  sharing off, so it asserts sharing was *enabled* and *fired* before believing the agreement.

  Cascade ordering has no oracle, and that is stated rather than worked around — CSS Cascading 5 §6
  is the specification and the tests are its clauses, each naming one tie-break with every other one
  tied.

  ⚠ **Doc 09's style-sharing key was unsound, and is corrected.** It specified the parent's
  *computed style*; two parents can hold the same computed style and still be told apart by a
  selector, so `.a .row` would reach a child that shared with a `.b`'s. The key holds the parent
  *element*. What separates out of the correction is worth carrying: **interning** and **sharing**
  were conflated under one heading and do different jobs. Interning gives 10 000 grid cells one
  `ComputedStyle` object and is untouched; sharing skips the cascade and now does so per row rather
  than per grid — 102 cascades for 10 001 elements. Doc 09 says so now.

  Three bugs it found. A `DeclarationRange` that did not say *which* arena it indexed had the
  resolver reading inline styles out of the rule store — fixed by making `InlineStyleId` a distinct
  type, so the mistake is unrepresentable rather than merely repaired, which is the same remedy the
  defaulted `StyleNodeId` needed. A `>=` where the cascade wanted `>`, so the *first* of two
  declarations that tie completely won instead of the last. And — the one worth remembering — **a
  test that was asserting document order and calling it importance**: flattening every important
  origin to a single rank left the whole suite green, because that test loaded its two sheets in the
  order where source order gave the right answer anyway. Sabotage found it; nothing else would have.

  ⚠ **A second ExCSS finding the spike did not reach.** ExCSS normalises what it can see and cannot
  see through a `var()`, so `color: red` arrives as `rgb(255, 0, 0)` and `color: var(--c)` with
  `--c: red` arrives as `red`. Every value parser in the property system has to accept both. Doc 09
  records it.

- 🟡 **Invalidation is built and its gate is green.** `StyleInvalidator` derives from the rule set
  what changing one name on one element can reach; `StyleUpdater` runs the pass, cold or
  incremental. 90 tests in total for the project.

  **The invalidation-minimality gate is met** — toggling `.selected` on one row of a 100×100 grid
  restyles exactly one element — and the property that matters more with it: after any sequence of
  class and state changes, **every** element's computed style equals what a pass from scratch would
  have produced. Both halves are needed. An invalidator that gave up and restyled everything passes
  the oracle; one that skipped too much passes the counts by producing a smaller number.

  Two bounds, and conflating them is what makes an invalidator either wrong or useless. The
  dependency map bounds what the *rules* reach, narrowing by the far end's names so `.selected .cell`
  reaches the cells and not the subtree. Inheritance bounds what a *changed value* reaches, and no
  dependency map can see it — the descent continues only while the properties a child would have
  inherited actually differ. Testing "did anything differ" instead was what made selecting one row
  restyle its hundred cells, since a highlight setting `background` cannot reach one. Doc 09 now
  states the qualifier this implies: the one-element claim holds for non-inherited properties, and
  an inherited one legitimately costs the row and its cells.

  ⚠ **Two of the findings are about the tests, and are the ones worth carrying forward.**

  *An oracle that shares an implementation with its subject is not an oracle.* The incremental
  oracle first built its cold reference by replaying the same mutations on a second tree. Both sides
  then reach their final state through the same mutation code, so anything that code gets wrong is
  wrong identically on both — deleting the ancestor-bloom propagation in `AddClass`, which breaks
  matching outright, left the property green over three hundred iterations. It builds its tree
  directly in the final state now.

  *A generator needs a coverage assertion for the same reason a test does.* Every stylesheet the
  generator produced contained a sibling or position selector, which turns style sharing off for the
  whole rule set — so a sabotage leaving the sharing cache stale across passes was unreachable by
  the property meant to catch it. Sharing-safe stylesheets have their own property now, which
  asserts sharing was enabled before believing what it observed.

  Both were found by sabotage and neither would have been found any other way. Four sabotages run
  against the final gates; all four fail it.

- 🟡 **Transitions and animations are built.** `Oklab` in `Vixen.Core.Mathematics`, checked against
  its author's published values; `StyleValue` and its parser; `TimingFunction` with `cubic-bezier`,
  `steps` and the `spring()` extension; `Animator` running transitions and `@keyframes`. 149 tests
  in total for the project, plus 10 for Oklab.

  Springs are solved in **closed form** rather than integrated — a value depending only on elapsed
  time cannot drift, so a dropped frame does not change where it ends up — and are checked against a
  numerical integration of the differential equation itself, which is two independent routes to the
  same curve. Interrupting a transition reverses from where the element actually is and takes the
  half-duration it has left, without which moving a pointer on and off a button drifts further
  behind every pass.

  ⚠ **A third thing ExCSS leaves to Vixen**, and the pattern is now familiar: it expands the
  `transition` shorthand only when it recognises every part, so `spring()` — Vixen's own extension —
  decides whether the longhands exist at all. Vixen parses the shorthand itself as well. By contrast
  `@keyframes` ExCSS *does* parse, which probing established and assumption would not have.

  Two bugs worth carrying. **A curve solver terminating on the wrong quantity**: inverting a cubic
  Bézier stopped when the error in *x* was small, which pins nothing where the curve is flat in x —
  and `cubic-bezier(0, y, 0, y)`, an ordinary slow-start easing, is exactly that near the origin.
  Found by a property test, and only findable that way: every hand-picked easing passed. **A comma
  split that cut a function call in half**: `spring(2, 180, 12)` has commas inside it, so the same
  feature that defeats ExCSS's expansion also defeats a naive split of the shorthand — the same
  shape as matching braces inside an `@layer` body.

  Also corrects a test-writing habit: `Assert.Equal(x, y, 4)` rounds to four decimals and compares,
  so a true value sitting on a rounding boundary fails on 3e-7 of float noise. Tolerances, not digit
  counts.

  **Owed:** several simultaneous animations per element (`animation-name: a, b` runs the first), and
  transform decomposition, which waits on there being a transform property.
- ✅ **`Vixen.Ui.Styling.Utilities` is built and its gate is green.** Token config, candidate
  scanner, utility grammar, variant system, arbitrary values, `@apply`, generated stylesheet.
  78 tests.

  Everything lands in `@layer utilities`, and that one line is what makes the system behave: a
  generated `.p-4` is one class and a hand-written `.card .body` is two, so on specificity alone the
  utility loses every time. The layer settles it declaratively and specificity never enters into it.

  The assertion worth the most is not the family table but the end-to-end one: **a generated utility
  computes to what the hand-written rule would**, checked by loading the generated sheet into the
  style engine and resolving an element. That checks the generator against the *engine* rather than
  against an expectation of the text it ought to produce.

  Two bugs, and one is a repeat. **A bracket-aware search that could never find a bracket** — the
  parser updated bracket depth in the same `switch` that tested for the separator, so searching for
  `[` hit the depth-increment arm and returned nothing, and every arbitrary value silently stopped
  being one. And **a layer test that was asserting document order**: the check that `@layer
  utilities` loses to an unlayered component rule loaded that rule second, where source order gives
  the same answer, so it passed with the whole `@layer` wrapper replaced by `@media all`. That is
  the same mistake the cascade suite's important-origins test made, caught the same way. The lesson
  is worth stating once more because twice is a pattern: **a test that asserts a winner where the
  rules differ in more than one respect is testing whichever difference happens to be implemented.**
- Gate: ✅ selector-matching oracle tests, ✅ style-sharing oracle tests, ✅ cascade/specificity/
  `@layer` order tests, ✅ invalidation-minimality tests, ✅ utility family tests. **4b is complete.**

**4c — Text (1.0 EM)**
- ✅ **UAX#29 segmentation is built and the conformance data is green** — all 2 710 of the
  Consortium's cases, 766 grapheme and 1 944 word, Unicode 17.0.0. Half of 4c's gate is met.

  Sequencing rule 4 applied a second time, and the same bet as the Yoga fixtures: the suite was
  committed *before* the implementation, excluded from compilation by an ItemGroup whose removal is
  the next commit's diff. `Tools/Vixen.UnicodeTableGen` produces both the suite and the property
  tables the implementation reads.

  ⚠ **The finding is a data-modelling one, not a rule one.** `Extended_Pictographic` and
  `Word_Break` come from different UCD files and *overlap* — U+24C2 is `Word_Break=ALetter` and
  pictographic at once — so folding them into one sorted range table makes one silently shadow the
  other, with sort order deciding which. Forty-four cases failed, all containing that one code
  point. The rules were right the whole time; the mistake was a layer below them, and re-reading
  UAX#29 would never have surfaced it. **That is what a conformance suite is for.**

  Verified by sabotage: removing regional-indicator pairing fails 6 cases, GB9c fails 16, and WB4 —
  the rule that makes format characters invisible to every other rule — fails 1 086.
- ✅ **UAX#14 line breaking is built and its conformance data is green** — all 19 338 of the
  Consortium's cases. With UAX#29 that is **22 048 conformance cases passing**, and the whole of the
  "UAX conformance data green" half of 4c's gate.

  It finds *opportunities*, not lines: where a break is permitted and where one is mandatory.
  Choosing which permitted break to take needs measured widths and is layout's job, and keeping the
  two apart is what makes the suite applicable at all — it knows nothing about fonts.

  ⚠ **The same class of bug as the UAX#29 finding, four times over.** LB9 gives a combining mark its
  base's *class*, which is enough for every rule that reads classes and silently wrong for the ones
  that read identity or position — LB28a names U+25CC by code point, LB15a/LB15b ask whether a
  quotation mark opens or closes, LB30b asks whether a pictograph is unassigned, LB30a counts
  regional indicators. A quotation mark followed by a diaeresis stopped being a quotation mark.

  ⚠ **And a comment that disagreed with its own code, twice.** LB15a and LB20a both permit `SP`
  immediately before them, and both were written to *skip* the spaces and then ask what lay beyond,
  looking past the answer. One of them carried a comment saying "SP is itself one of the classes the
  rule allows" above a list that omitted `SP`. Two cases out of nineteen thousand caught it. **A
  comment is not a test.**

  Also worth recording: LB25 was a regular expression until Unicode 15.1 restated it as pairs, and
  the pair form is both easier to implement and easier to be sure of — the regex passed most of the
  suite and failed on `HY × NU`, which has no regex form because a hyphen before a number is not
  part of the number. Verified by sabotage: removing LB25 or LB9, or mis-resolving `CJ` in LB1, each
  fails the suite.
- ✅ **UAX#9 bidi is built and its conformance data is green** — all 91 707 of the Consortium's
  code-point cases. Paragraph level, per-character levels and visual order are all checked; a level
  array that is right with a reordering that is wrong is a real and common failure.

  `BidiCharacterTest.txt` rather than `BidiTest.txt`: the first is written in real code points and
  exercises the property table as well as the algorithm, the second in class names and tests the
  algorithm alone. Committing both would put fifteen megabytes in the repository to say one thing
  twice.

  ⚠ **One bug, and it is the third variant of the same mistake.** The implicit rules raise levels
  *in place*, so everything reading a level for *context* — which run a position belongs to, and the
  `sos`/`eos` at a sequence's boundaries — must read the explicit rules' output, not what a later
  rule has since written there. Without the snapshot the isolating run sequences corrupt each other
  in source order, and the symptom is unrecognisable from the cause: an `LRE` paragraph came out
  with exactly the levels of the `RLE` one.

  Segmentation had it as "a combining mark inherits its base's class but not its identity"; line
  breaking had it four times over; bidi has it as "the array you are reading has already been
  rewritten". **Reading a mutated structure where the unmutated one was meant** — worth naming,
  because it will happen again.

  ⚠ **The bidi class defaults are not `L`.** `DerivedBidiClass.txt` carries `@missing` lines saying
  unassigned code points in the Hebrew block are `R` and in the Arabic blocks `AL`, so that a
  character added tomorrow behaves correctly today. The generator honours them; reading only the
  explicit ranges would have made every unassigned Arabic code point left-to-right.

  Verified by sabotage: dropping N0's paired brackets, L1's whitespace reset, or I1's two-level bump
  for numbers each fails the suite.
- ✅ **HarfBuzzSharp spiked before being built on** —
  [spikes/text-harfbuzz](spikes/text-harfbuzz/RESULT.md), following sequencing rule 3 as the ExCSS
  spike did. Doc 01's choice stands. NativeAOT publishes with **zero** IL warnings, which is a
  stronger result than ExCSS could give since the managed surface is a thin P/Invoke layer the
  analyzers can see all of. Every target platform has a native asset at the pinned version.

  The risk actually worth spiking was WebAssembly: the package ships *static* archives, so they must
  be linked by the same Emscripten the .NET WASM build uses. It ships 3.1.34 and 3.1.56, and .NET 10
  pins `Emscripten.3.1.56.Sdk`. They match. ⚠ Recorded as unverified rather than claimed — no WASM
  link was performed, because `wasm-tools` is not installed on the machine, so this is read from two
  manifests rather than demonstrated. **Carry forward: the WASM path is a version-coupled static
  link, so a bump of either HarfBuzzSharp or the SDK has to be checked against the other.**

  One design consequence, and it validates the order this phase took: HarfBuzz shapes one run at a
  time and wants runs already itemised by direction, then script, then font — so bidi comes first,
  which is what was just built. Shaping written first would have been written against a run model
  that did not exist.
- ✅ **Shaping is built, and it is judged by somebody else's cases rather than by HarfBuzz's own.**
  328 of the Consortium's 413
  [text-rendering-tests](https://github.com/unicode-org/text-rendering-tests) shaping cases pass —
  Arabic in Nastaliq, Balinese, Kannada, Tai Tham, and the GSUB/GPOS/KERN/CMAP tables shaping is
  made of. `Tools/Vixen.TextRenderingTestGen` ports them; sequencing rule 4 for the fourth time,
  suite before implementation.

  ⚠ **The gate this doc asked for was unbuildable as written.** "Shaping golden tests per script
  against HarfBuzz reference output" is HarfBuzz judging itself: Vixen writes no shaping algorithm,
  so that comparison stays green through any mistake that hands the shaper the same wrong arguments
  twice. What Vixen owns is the itemisation — which runs, what direction, what script, what order,
  and how a glyph maps back to a character — and the Consortium's expectations, written by hand
  from the OpenType specification, are sensitive to exactly that. **The gate is restated as
  external-oracle shaping conformance**, which is stronger than what was asked for and is the same
  bet as the Yoga fixtures and the UAX suites.

  Verified by sabotage, and this is the evidence that the suite tests Vixen and not only HarfBuzz:
  shaping every run as Latin fails **203** cases, forcing every run left to right fails **6**, and
  giving spaces and punctuation runs of their own fails **2** — one of which is the case the
  Consortium named *Space Isn't Nothing*, which exists for that mistake and catches it by name.

  ⚠ **The same sabotage found the hole, which is the more useful half.** Shaping each run *without
  the text around it* fails **nothing**: every case in the suite is a single run and so has no
  neighbour to lose. That context decides whether an Arabic letter joins, and losing it also makes
  every cluster index relative to the run rather than to the text — an off-by-three in every caret
  and hit test downstream. Four hundred external cases cannot see either; `ShapingTests` covers what
  they miss. **A gate is only a gate for what it can observe, and finding out which half that is
  cost one sabotage run.**

  ⚠ **The 85 failures are HarfBuzz's, and they are pinned case by case rather than excused.** The
  test fails if a quarantined case starts *passing* just as loudly as if a healthy one starts
  failing. Listing them by group would have been four lines instead of eighty — and would have
  hidden the 131 Tai Tham cases that now pass: the Consortium's own 2023 report for HarfBuzz fails
  all 209 of them, and at 14.2.1.1 only 78 still do. A group-level rule would have thrown that away
  silently and gone on doing so.

  Two findings worth carrying. The suite's positions are in a **1000-unit em**, not the font's, so
  nine of the fourteen fonts have expectations scaled by 1000/2048 — compared naively, every case
  with two or more glyphs fails by a factor of 2.048 while every single-glyph case passes, which
  reads as a shaping bug rather than a units one. And a **bracket that opens before the first
  letter** remembers a script that does not exist yet, so `(ಲ್ಲಿ)` came out as Kannada followed by a
  one-character run of nothing in particular; backfilling the leading characters was not enough, the
  bracket stack had to be backfilled too.

  Shaping is held at **design-unit scale and never at a pixel size** — HarfBuzz's OpenType path has
  no hinting, so a string shapes identically at every size. That is what will make the shaping cache
  size-independent rather than one entry per string per DPI scale.
- ✅ **Shaping clusters reconciled with grapheme clusters** — the thing the HarfBuzz spike flagged as
  "agree often enough to be dangerous". A caret moves in graphemes and a glyph is drawn per shaping
  cluster, so a caret has to land *inside* a ligature or a reordered Kannada syllable;
  `CaretOffset` interpolates across the cluster by grapheme count rather than snapping to its edge.

  Gated by a round trip rather than a table of numbers — hit-test a caret's own offset and get the
  caret back — which holds for scripts nobody wrote a case for. Verified by sabotage: not reversing
  right-to-left clusters into logical order fails 7 of 18, treating zero steps as "the next
  boundary" fails 6, forgetting that the fraction runs the other way inside a right-to-left cluster
  fails 4, snapping to the cluster edge fails 3.

  ⚠ **The round trip is only true where the text runs one way, and that is bidi rather than a gap.**
  In `abcلسان` index 3 is both *after the c* and *before the first Arabic letter*, at opposite ends
  of the Arabic run — one index, two places, and the same screen point answering to two indices. No
  index-to-position function can return both. Telling them apart needs a caret **affinity** carried
  beside the index, which is owed with `TextEditor`. Asserting the round trip everywhere would have
  meant deleting the mixed case or inventing a rule to make it pass, and both would have buried a
  real property of the writing system.
- ✅ **The shaping cache**, with LRU eviction and an oracle gate: a cache is only ever wrong by
  answering differently from the thing it stands in for, so that is what is checked, over random
  sequences of lookups rather than over chosen cases. Verified by sabotage — failing to promote an
  entry on a hit, dropping the font or the direction from the key, evicting one entry too late, and
  confusing two paragraphs of the same length each fail it.

  **The size is not in the key**, which is the payoff for holding the font at design-unit scale: one
  entry serves every size and DPI scale, where a size-keyed cache would miss on every frame of a
  growing label. ⚠ And it caches **paragraphs rather than runs**, which follows from the context
  decision — a run is shaped with the text around it, so its glyphs are not a function of the run
  alone, and a run-keyed cache would either be unsound or need the context in the key. Reuse between
  paragraphs sharing a word is given up on purpose.
- ✅ **The glyph-outline question is answered, and the managed route stands** —
  [spikes/text-glyph-outlines](spikes/text-glyph-outlines/RESULT.md), sequencing rule 3 for the
  fifth time. MSDF had an unanswered question underneath it: HarfBuzzSharp exposes no outlines at
  all — `TryGetGlyphExtents` is a bounding box and there is no draw, paint or outline surface — so
  distance-field generation had nothing to generate from.

  A managed `glyf`/`CFF` parser over `Face.ReferenceTable` reads them, in ~600 lines for both
  formats, with no new native dependency and the WebAssembly path exactly as the HarfBuzz spike left
  it. **242 fonts, 259,298 glyphs, every font read without an exception: 99.999 % of `glyf` glyphs
  and 99.777 % of `CFF` ones agree with HarfBuzz's own extents** — a separate implementation of the
  same tables, which is the only oracle available at that scale.

  ⚠ **HarfBuzz reports *positioned* extents and an outline is not positioned.** For `glyf` it shifts
  the glyph so `xMin` lands on the left side bearing; where a font's stored `xMin` disagrees with its
  own `lsb` — common, and universal in italics — the extents come back translated. That correction is
  the difference between reading 95.3 % and 99.999 %, **and the atlas will need the same shift when
  it places a glyph**, so it is a fact about the pipeline rather than about the test.

  ⚠ **For `glyf`, HarfBuzz returns the box the font stores rather than one it computes**, so the
  comparison checks point decoding and not curve evaluation — and where a font's stored box is wrong,
  disagreeing is correct. All three remaining `glyf` misses are that, verified by hand: glyph 274 of
  Arial, Arial Bold and Times New Roman claims an `xMax` its own two components do not reach.

  Two bugs, and both are the kind that reads correctly on the page. `r.Position += r.U16()` skips
  from where the *length* started, because a compound assignment reads its target first — 8.6 %
  agreement before, 95.3 % after, one line. And a Type 2 width test inverted for stem operators,
  which miscounts stems, so `hintmask` skips the wrong number of bytes and the rest of the charstring
  is read as garbage — a wrong shape rather than an error, and only in fonts hinted heavily enough to
  have a `hintmask` at all.

  Not built, and **not owed**: point-matched composites and `seac` — no glyph in 242 fonts used
  either. Owed with the variable-font axes: `gvar` deltas, so a variable font currently parses at its
  default instance.
- ✅ **The outline reader is built** — `FontFace.GetOutline`, over `glyf`/`loca` and `CFF ` Type 2
  charstrings, positioned to agree with the extents everything else in the assembly comes from. The
  spike's parser, made AOT- and trim-clean and gated in CI.

  Gate: HarfBuzz's own extents over every glyph of all fourteen embedded fonts — 2,066 of them.
  Verified by sabotage: restoring the compound-assignment bug fails 10, dropping the left-side-bearing
  shift fails 1, and stopping a composite after its first component fails 7.

  ⚠ **Two sabotages failed to fail for a reason worth keeping: a bounds oracle cannot see a path.**
  The rules that turn TrueType's points into a path — an implied on-curve point midway between two
  off-curve ones, and a contour that begins off-curve — move points that already lie inside the hull
  of their neighbours, so breaking either changes the shape and not the box. Golden paths for three
  glyphs close it, and finding the right three meant counting which branch each of the 2,066 glyphs
  took: every Kannada contour starts on-curve, so the first golden reached only one of the two rules.

  ⚠ **Two more are unreachable with the corpus that can be committed, and that is measured rather
  than assumed.** The embedded fonts contain **zero stem operators and zero hintmasks**, so the CFF
  width-parity rule is never executed and inverting it passes everything; and **not one of their 530
  composite components carries both a transform and an offset**, so the rule about which matrix the
  offset travels through is never exercised. Both were gated by the spike's 259,298 glyphs, whose
  fonts belong to the operating system. Named here rather than papered over with a test that cannot
  reach what it claims.

- ✅ **The rasteriser, and the oracle it exists to be.** `GlyphRasterizer` fills an outline by
  scanline and non-zero winding; sequencing rule 4 put it before the distance field it judges.

  **Gated by Green's theorem**, which gives the exact area a path encloses straight from its control
  points — the integrand for a Bézier is a polynomial, so four-point Gauss–Legendre evaluates it
  without error. A real oracle: it shares no code and no reasoning with the fill.

  ⚠ **Compared per contour, and that is the oracle's own limit.** Green's theorem measures
  *algebraic* area and a non-zero fill measures *covered* area, so a region two contours both cover
  counts twice in one and once in the other. Not exotic: `TestShapeLana` builds letters from stacked
  strokes, and 22 % of one glyph's algebraic area is covered more than once. Found by the whole-glyph
  comparison failing on one font of fourteen.

  Verified by sabotage: rounding spans to whole pixels fails 1, flattening every curve to one chord
  fails 5, ignoring an edge's direction fails 17, leaving an unclosed contour open fails 8. ⚠ Two
  failed to fail, and one was **a claim written in a comment and never tested** — even-odd fill
  agrees with non-zero on a hole and differs only where two contours wound the same way overlap. The
  other was the half-open y rule, observable only when a vertex lands exactly on a sample line.

- ✅ **Multi-channel signed distance fields.** `EdgeColoring` and `DistanceField`: the corner-keeping
  encoding doc 09 names, gated by reconstructing the shape back out of the field and comparing
  against the rasteriser filling the same outline.

  ⚠ **Three sabotages failed to fail, and each found a real defect.** *A corner is a property of the
  outline, not of the flattening* — twice over, since a flattened curve's internal joins each turn a
  few degrees and even a genuine segment boundary shows a step's worth of curvature between
  neighbouring chords; either reading makes a circle come out striped. *Each channel carries its own
  sign*, and taking one sign from the fill for all three leaves them differing only in magnitude, so
  their median can never disagree with a single channel about which side of the shape a point is on
  — which is the whole of what the median is for, and the first version reconstructed a square's
  corner no better than a plain field. And *a run's colour must differ from its neighbour's with the
  last one wrapping*: cycling three combinations gives four corners RG, GB, BR, RG, so exactly one
  join has both sides the same.

  ⚠ **The corner claim needed a third oracle, and two attempts at it measured nothing.** Counting
  misclassified pixels hides the effect, because a plain field's corner error is a fraction of a
  texel. And **the corner's diagonal is the one direction where the three channels are symmetric and
  none can help** — measured there the median *is* a plain field, exactly. What the channels buy is
  that the edges stay straight up to the corner, so the test walks across an edge instead, against
  the closed-form distance to a rectangle sampled and interpolated identically.

  ⚠ **The pseudo-distance is insurance and is labelled as such.** Clamping to the segment fails
  nothing; two shapes were built to reach it, and the answers differ in magnitude but never in sign,
  so a thresholded reconstruction moves 0.02 of a texel. What it should buy is a truer gradient for
  the shader's antialiasing, which nothing here reads yet.

- ✅ **The atlas**, and with it **the whole of 4c's rasterisation line: outline → coverage → field →
  texture.** `GlyphAtlas` shelf-packs the fields and evicts least-recently-used, keyed by font and
  glyph and *not* by point size — a field is read at any scale, so a size in the key would miss on
  every frame of a growing label.

  ⚠ **Evict first, compact only when the space is there and the shape is wrong.** Compaction changes
  every region and so moves the version, which throws away every texture coordinate in flight;
  compacting whenever a full atlas is added to would do that every frame of a steady-state interface.
  Entries go one at a time until either one fits or enough area has been freed that fragmentation
  must be the reason it does not.

  Verified by sabotage: a hit that does not refresh its entry fails 2, evicting the newest fails 2,
  never reusing a freed slot fails 1, dropping the padding fails 1, a compaction that does not move
  the version fails 1, and a hit that marks the texture dirty fails 1. ⚠ Writing a glyph at the wrong
  row failed to fail until a test placed something below the first shelf — everything else lands on
  row zero, where dropping the region's y is invisible. And **compaction's warmest-first order is
  insurance**: a sabotage reversing it fails nothing, because compaction only runs on a set that
  already fitted, and several attempts to build one that repacks worse than it packed all fitted.

- ✅ **`GlyphFieldCache`, the join a renderer talks to.** Ask where a glyph is; get an atlas region
  and the quad to draw it in. Outline, field and packing are all behind it.

  ⚠ **In ems, not pixels** — the atlas is size-independent on purpose, so its metadata has to be, and
  a placement in pixels is right for one size and wrong for the next. ⚠ **A placement outlives its
  pixels**: eviction takes the entry, and where a glyph sits relative to the pen came from the font.

  Verified by sabotage: a placement in pixels fails 1, a quad cropped to the silhouette fails 1, an
  unpadded field fails 2, dropping the font from the key fails 1, a screen-pixel range that ignores
  the resolution fails 1. ⚠ Two needed sharper tests first — remembering that a glyph draws nothing
  is invisible through the atlas, which an empty glyph never reaches; and reporting a remembered
  placement beside a region the atlas no longer holds passes every assertion about the placement
  while sampling whatever has since been packed at the origin.

- ✅ **The geometry a renderer submits** — `UiGeometryBuilder`, the CPU half of the UI render
  feature. A draw list in, vertices out, and a pure function of the list so all of it is checked
  without a device.

  Boxes are one quad each with the corner radius evaluated in the shader; clips are **resolved**
  into a scissor rectangle per draw rather than replayed as commands, and a nested one intersects.

  ⚠ **A glyph's position is an offset along its run, not a place on the surface** — the command
  carries where the line starts, which is what lets two identical labels in different places hold
  identical glyph runs and therefore what lets the batcher and the frame diff notice. Found while
  writing the tests: the first fixture put its run at the origin, where the two are the same thing.

  Verified by sabotage: reading glyph offsets as absolute fails 1, a quad ignoring the font size
  fails 1, an unflipped baseline fails 1, a threshold range that does not scale fails 1, a nested
  clip that replaces fails 1, a clip never popped fails 1, a box not parameterised from its centre
  fails 1, emitting empty draws fails 3, a silent dropped glyph fails 1. All nine land.

- ✅ **A wider vertex index.** `UiGeometry.Indices` was `ushort` and the builder refused to emit past
  65 535 vertices rather than wrap. Refusing was honest while the index was narrow and it is not a
  fix: a dense editor really can pass sixteen thousand quads, and the symptom of dropping the rest is
  a frame missing its bottom half. Thirty-two bits, not because a frame is expected to need them but
  because the one that does wraps *silently*, drawing geometry from the top of the frame in the
  middle of it. Sabotaging `start` back to a 16-bit truncation fails the test.

- ✅ **Path tessellation.** `PathFlattener` turns curves into contours at a tolerance the caller
  chooses — which is where `PathBuilder`'s decision to keep curves as curves is finally spent, and
  why a path drawn at two zoom levels is right at both. `PathTessellator` turns contours into
  triangles, filled or stroked.

  ⚠ **A trapezoid sweep, not an ear clip.** Ear clipping is the usual answer and is wrong for the
  input this gets: it needs one simple polygon, so holes need bridging and self-intersection needs
  resolving first — and a five-pointed star drawn as five lines self-intersects, which is exactly
  where the two fill rules disagree and therefore the shape a fill rule is *for*. Sweeping makes the
  rule the whole of the algorithm. Bands are cut at every vertex **and every crossing**, so no edge
  begins, ends or crosses another inside one, which is what makes each span an exact trapezoid. The
  cost is quadratic in the edge count; that is written down along with the Bentley–Ottmann sweep that
  would fix it, rather than discovered later.

  Strokes are per-segment quads plus a wedge on the outside of each turn: miter with a limit —
  without one, a nearly-doubled-back corner grows a spike that runs to infinity — round, and bevel;
  butt, round and square caps. A closed contour joins at the seam and is not capped, which is the
  whole reason `Closed` survives flattening.

  **Two oracles carry the suite and neither knows how the tessellator works.** A fill is right when a
  point is covered exactly when the winding rule calls it inside. A stroke with round joins and round
  caps is right when a point is covered exactly when it lies within half a width of the path — the
  Minkowski sum of the polyline with a disc, available in closed form.

  ~~⚠ **Nothing here is antialiased**, and that is stated rather than hidden.~~ ✅ **It is now**, by a
  fringe: the interior comes out at full coverage and a half-pixel strip along the outline carries the
  ramp to zero, in the vertex where a box and a glyph carry a distance. Multisampling is still the
  other answer and remains the compositor's to choose — `UiGeometryBuilder.Fringe = 0` switches this
  one off, because two antialiasing schemes over one edge do not make it twice as smooth.

  ⚠ **Which way is out is asked of the fill rule, not derived from the winding.** The cheap version
  takes a contour's signed area as its orientation and is wrong for exactly the shapes that need a
  fill rule: under even-odd a hole is a hole however it is wound, so an inner contour wound the same
  way as its outer one gets its fringe drawn *into* the shape — a bright band around every counter in
  an icon set. Each edge is probed on both sides instead and kept only where exactly one is inside.

  ⚠ **And an edge is cut where anything crosses it before being asked.** A pentagram's chords all pass
  through the pentagon in the middle, so probed once at the midpoint every one of them reads
  "interior" and the star comes out with no antialiased edge at all. Splitting first is the same thing
  the sweep does to its bands, for the same reason.

  ⚠ A stroke's fringe is emitted per piece, so it overlaps on the inside of every turn — invisible for
  an opaque stroke and only for an opaque one, since a ramp in the same colour over a pixel already
  that colour leaves it unchanged. At a partial alpha it is a faint line down the inside of each
  corner, and the alternative is resolving the union of the pieces into one outline, which is the
  offset-curve problem the stroker declines to solve.

  Verified by sabotage: eleven, all landing — the direction taken from the winding fails 2, the fringe
  drawn inward fails 2, an edge decided whole fails 1, the corner wedges left out fails 1, a fringe at
  full coverage throughout fails 1, an ignored width fails 2, an unfeathered stroke or fill fails 2
  each, a coverage that never reaches the vertex fails 3, and a shader that ignores it fails 1.

  Verified by sabotage: twelve, eleven landing first time. ⚠ **The twelfth failed to fail** —
  deleting the seam-duplicate removal broke nothing, because the test used `AddRectangle`, which
  never walks back to its start, so the duplicate was never there to remove. The shape that needs it
  is what an imported SVG produces: an explicit line home, then a close. Sharpened, and it lands.

- ✅ **The GPU half** — `Vixen.Ui.Renderer`. Three pipelines over one vertex layout (box, text,
  solid), host-visible buffers rewritten per frame, an atlas texture uploaded only when its version
  changes, and a clip applied as a scissor. `UiRenderFeature` is a thin `RootRenderFeature` over it,
  so the part that touches a device can be driven by a golden image without a `RenderSystem`.

  A separate assembly on purpose: the join belongs in neither half. `Vixen.Ui` would gain a graphics
  API it is meant to be usable without, and `Vixen.Rendering` would gain a UI framework every
  renderer would then carry.

  ⚠ **One pipeline layout for all three pipelines, including the two that never sample the atlas.**
  A layout each is the obvious arrangement and the one that must be got right per draw, because
  Vulkan disturbs every set from the first one two layouts disagree about — so a box between two runs
  of text unbinds the atlas. That is undefined behaviour rather than an error, this machine's driver
  keeps the binding, and the validation layers do not object, so **no golden image here can see it**.
  Found by a sabotage that changed nothing through two rewrites of the fixture built to catch it.
  Identical layouts make the question not arise.

  ⚠ **The corrected guess.** `DrawBatch` reasoned that because `RenderSortMode.ByGroup` exists "for
  UI and anything else already ordered", the batch index must be the sort group. It cannot be: a
  render object is one *surface*, because the store's objects live across frames under a dense id
  every feature's array is keyed on, and an object per batch would churn the store on every label
  change. Painting order within a surface is already the order of `UiGeometry.Draws`. The group
  orders surfaces against each other. Both `DrawBatch`'s remarks and this entry are corrected rather
  than quietly left standing — and the other open question there is closed too: the batch list *is*
  used, by the geometry builder, one batch to one draw, behind the frame diff.

- Gate: ✅ `ui-interface` and `ui-clipped` golden images. Thirteen sabotages, all landing — the
  projection agreeing with Vulkan instead of the engine fails 2, a scissor never set fails 1, an
  unbound atlas crashes the driver, the shared state pushed before any pipeline is bound trips
  validation, the vertex layout swapping colour and shape fails 2, a 16-bit index format fails 2, the
  box distance's sign fails 2, a border drawn as a fill fails 1, an ignored corner radius fails 1,
  one channel instead of the median fails 1, an ignored pixel range fails 1, a second y flip fails 2.

  ⚠ **The first version was drawn upside down**, with a comment above the projection arguing at
  length that it should not be: Vulkan's clip space does have +y down, but nothing sees it, because
  the backend submits a negative-height viewport so the engine's +y-up convention holds everywhere.
  ⚠ **And the clip fixture did not notice**, because its box was symmetric about the scissor's edge —
  a clip test whose picture is its own mirror image cannot see the most common mistake in the file it
  tests.

- ✅ **A stroke's join and cap are carried on the command**, beside its thickness, rather than set
  once for the whole frame on the geometry builder — a join is part of the stroke somebody asked for,
  and nobody would have put the thickness anywhere else. `MiterLimit` is on it too, where ⚠ **zero
  means the default of four**: the command is a struct, so its default is all-zeroes, and a real
  limit of zero would bevel every corner of a stroke whose caller set only the thickness.

  ⚠ **And a claim next door stopped being true.** `DrawBatcher` puts the fill rule in the batch key
  on the argument that "two filled paths read by different rules are not the same draw". Since the
  tessellator, they are: `UiGeometryBuilder` reads the rule per *command*, so merging them loses
  nothing. The rule stays in the key as **insurance** against a renderer that resolves it on the GPU
  — stencil-then-cover, where the rule really is pipeline state — and is now labelled as insurance
  rather than as a covered claim. The join and cap are deliberately *not* in the key, because there
  is no implementation in which a join is anything but geometry.

- ✅ **Line wrapping** — `LineWrapper` fills `LineBreaker`'s opportunities into lines of a given
  width. The two are deliberately apart: the first answers "where *may* a line end", which is a
  question about Unicode and is judged by the Consortium's suite; this one answers "where does it
  end", which needs measured widths and cannot be judged that way at all.

  ⚠ **A line is a range of the source, not a slice of the shaped glyphs.** Cutting a shaped paragraph
  at a break keeps whatever the shaper did across it — a ligature spanning the break survives onto one
  of the two lines, and a cursive script keeps a medial form on a letter that is now final. The only
  correct fix is to shape each line, and all a caller needs for that is where the line starts and ends.

  ⚠ **The width is accumulated per cluster, not along the glyph list.** A right-to-left run hands its
  glyphs back in visual order, so their clusters descend and a running sum measures a bidi paragraph
  as though it were Latin. What a line's width *is*, is the total advance of the characters in it,
  which does not depend on the order they are drawn in.

  ⚠ **And `LineBreaker.IsMandatory` is true at the end of the text**, because LB3 says "always break
  at end of text" — right for a conformance suite, and not a break a *line* was forced into. Left in,
  every paragraph's last line comes back marked mandatory and a paragraph that fits on one line comes
  back as one mandatory line. Found by the first test written.

  Greedy first-fit rather than Knuth–Plass: an interface reflows on every resize and every keystroke,
  so paying for an optimum that changes as fast as it is computed is the wrong trade — and greedy is
  what every browser does, so a panel wraps where somebody expects.

  Verified by sabotage: eight, seven landing. ⚠ **The eighth failed to fail** — replacing the
  grapheme boundaries the "break anywhere" mode cuts at with every UTF-16 index changes nothing,
  because a cluster's whole advance is recorded at its first character, so every cut inside a cluster
  measures the same as the cut at its end and the largest that fits lands on the end anyway. The
  guard is kept and labelled as insurance against the cluster reconciliation going away, which is
  what makes it unreachable.

- ✅ **Gradients and per-corner elliptical radii.** Four corners with a pair of radii each, and a
  two-stop linear gradient along an axis in the box's own space.

  ⚠ **A storage buffer, one record per box, and the vertex carries the index.** Fourteen more floats
  on the vertex would take it from forty-eight bytes to a hundred and four, and every glyph and path
  triangle in the frame would carry fields no shader reads on them; per box it is eighty bytes
  against the sixty-four its four vertices already spend, and the vertex layout does not move. The
  draw list keeps the authored form in a side buffer beside the glyphs and the path segments, for the
  same reason it keeps those there — and the frame diff reads it, or a button whose gradient is being
  animated emits identical commands every frame and keeps drawing the old colours.

  ⚠ **The exact distance to an ellipse has no closed form.** The corner quadrant is scaled into a
  circle and the distance scaled back by the *smaller* semi-axis: exact on the axes and within a
  fraction of a pixel between them, which is all a one-pixel antialiasing band can tell apart.
  ⚠ And `q` is the offset from the ellipse's *centre*, so `q <= 0` on an axis means the boundary
  there is a straight edge — measuring from the centre where the edge is straight eats the whole flat
  part of the side, which is what the first version did.

  Verified by sabotage: fourteen, thirteen landing. ⚠ **One is unreachable**: the `flat` qualifier on
  the shape index insures against an index a float stops holding exactly — past sixteen million boxes
  — and interpolating a value equal at all three corners is exact, so no fixture can see it. Labelled
  as insurance. ⚠ **And one needed a new fixture**: a frame with more boxes than the last one replaces
  the buffer the descriptor set names, and a suite that uploaded once never grew it — so deleting the
  rewrite broke nothing, because the descriptor had been written by the atlas path on the way past
  and was correct by accident.

- Owed: font fallback, rich-text runs, variable-font axes, `TextEditor` model with IME and caret
  affinity. On the rendering side: reconciling the per-vertex box parameters here with
  `Raven/Library/Ui`'s per-uniform ones when Raven takes over shader compilation.
- Gate: ✅ UAX conformance data green. ✅ shaping conformance green against an external oracle,
  with the quarantine pinned in both directions.

**4d — Element tree, markup, rendering (1.5 EM)**
- ✅ **The styling↔layout bridge**, which was 4d's first owed item and is what `Vixen.Ui` now
  contains. `Vixen.Ui.Styling` decides which declaration wins without knowing what a length
  measures and `Vixen.Ui.Layout` measures without knowing where its numbers came from; neither
  references the other, and this closes the gap that leaves. `LengthContext` carries what a relative
  length is relative to, `LayoutStyleBuilder` maps a `ComputedStyle` onto a `LayoutStyle`.

  `em`, `rem`, `vw`, `vh`, `vmin` and `vmax` are now parsed and carried by `StyleValue`. They were
  deliberately left out on the argument that resolving them needs a context that does not exist at
  parse time — **right about resolution, wrong about representation, and transitions settled it**:
  the animator interpolates `StyleValue`, so a unit the type cannot express is a unit that cannot
  animate, and `width: 2em` under a `transition` snapped while its neighbours eased.

  ⚠ **Yoga's initial values are not CSS's, in four places** — `flex-direction`, `align-content`,
  `position` and `box-sizing` all differ. `Vixen.Ui.Layout` is right to start where Yoga starts
  since it is judged by Yoga's suite; the bridge is the boundary where a VCSS author's expectations
  take over, so `LayoutStyleBuilder.CssInitial` exists and `LayoutStyle.Default` is not what an
  element with no declarations gets.

  ⚠ **A predicted limitation that turned out not to exist, caught by writing the test first.** The
  bridge was built to expand the box shorthands itself, reasoning that the cascade stores shorthand
  and longhand separately and the layout store resolves edges by fixed precedence rather than
  document order — so `margin-left: 0; margin: 8px` would give zero where a browser gives eight. Its
  tests said every one of those paths was dead: **ExCSS expands on parse**, exactly as a browser
  does, so document order does the work. Had the claim been believed rather than tested it would now
  be a documented known limitation of something that works correctly.

  Two parser findings. **CSS has a unit that begins with the exponent character** — scanning `e`
  unconditionally made `2em` scan as `2e`, fail, and come back `Unknown`, dropping every `em` in the
  document. And `aspect-ratio: 16 / 9` reaches the cascade as `16/9`, spaces normalised away, so a
  whitespace-splitting parser sees one token.

  Verified by sabotage: starting from Yoga's defaults, resolving `font-size`'s `em` against the
  element's own size, resolving percentages in the bridge, swapping `vw` and `vh`, and dropping the
  leave-the-initial-value-alone guard each fail the suite. ⚠ **That last one took two attempts**, and
  the failure is the point — written against a stylesheet, an invalid value never reaches the bridge
  at all, because ExCSS validates as it parses. The test had to go through inline declarations
  *and* use a value that parses but is not a length. **A test that cannot reach the code it names
  passes for the wrong reason**, which is the third time this phase that has come up.
- ✅ **The element tree and the frame pass.** `UiElement` and `UiDocument`: a tree registered with
  both the style tree and the layout tree, and the four walks that turn a stylesheet into rectangles.
  Three subsystems built and tested apart now run together, and it is the first thing in this phase
  that can be judged by looking at it rather than by a conformance suite.

  Elements are **classes**, which is the departure from the rest of the engine doc 09 argues for: a
  UI node has identity, virtual behaviour and handlers, and there are 10⁴ of them rather than 10⁶.
  The struct-of-arrays discipline stays where the loops are — the layout store, and later the draw
  list — and `UiElement` holds no geometry and no style of its own, only handles into the two stores
  that do.

  **An unchanged document does no work on the next frame, and one changed class rebuilds one
  element.** That is what interning `ComputedStyle` buys, and `StylesApplied` reports the count
  because a claim about work avoided that cannot be measured is a claim nobody can check. ⚠ The
  resolved font size has to be part of that test as well as the style: an element whose own
  declarations did not change still needs rebuilding when an ancestor's font size did, and its
  computed style is the same interned object, so a check on the style alone skips it.

  ⚠ **A real finding about the cascade: it inherits *specified* values, and CSS inherits *computed*
  ones.** A child inheriting the text `font-size: 1.5em` resolves that `em` against its own parent a
  second time, so a size meant to apply once compounds at every level — two deep is 2.25× where CSS
  says 1.5×, and the error grows with depth. CSS avoids it by computing `font-size` to an absolute
  length before anyone inherits it, so **`font-size` is removed from `InheritedProperties` and
  inherited in computed form by `Vixen.Ui`**, which is both what CSS means and simpler than what was
  there. Owed: the same gap stays open for `line-height`, `letter-spacing`, `word-spacing` and
  `text-indent`, where an inherited relative unit measures against the descendant's font size — the
  error is bounded at one level there because none of them feeds back into its own unit, and the
  general fix is a computed-value stage in the cascade.

  Verified by sabotage: inheriting `font-size` as a specified value again fails 2, testing the
  computed style without the font size fails 1, letting a resize mark the document dirty without
  forgetting what was applied fails 1 — every `vw` keeps its old value while the window visibly
  changes size — and building against the parent's font size rather than the element's fails 3.

  ⚠ **The tree was append-only**, because `StyleTree` was: elements were created parents-first and
  never removed. Enough to lay out a document and not enough to run an application — closed below.
- ✅ **The generated property system.** `[UiProperty]` on a partial property, and
  `Vixen.Ui.Generators` supplies the accessors, the default, coercion, the change callback, optional
  inheritance and a `UiPropertyKey` the runtime can find by name. Generated rather than reflected and
  generated rather than rewritten — Stride builds the equivalent with a runtime
  `DependencyPropertyFactory` and ADR-002 rejects that category.

  ⚠ **Storage is a field, not a sparse table**, which is the opposite of what WPF does and
  deliberate. A dependency-property table pays a dictionary probe per read to save memory on the
  hundreds of properties a WPF element declares and never sets; a Vixen control declares perhaps a
  dozen, there are 10⁴ elements, and reads happen every frame. The table is the more famous design
  and the slower one.

  ⚠ **Inheritance is generated as a typed walk.** Each inheriting property emits its own loop testing
  `ancestor is TOwner`, so `Panel.Tint` finds the nearest `Panel` and an `Overlay` that also declares
  a `Tint` is not it — a name-keyed lookup would have found the wrong one and looked right.

  ⚠ **Construction and registration had to be split.** An element must be registered with both trees,
  which needs a document, and a base constructor taking one plus two internal node handles would put
  those handles in every subclass's signature in assemblies where they are not visible — so
  subclassing `UiElement` from another assembly was impossible until it had a parameterless
  constructor and `UiDocument.Create<T>` bound it afterwards. Which is also the shape markup needs,
  since a generated `new Button()` cannot know a document either.

  Verified by sabotage: ignoring whether an ancestor actually set a value fails 2, reading the old
  value out of the backing field rather than through the property fails 1 — a spurious change on
  every element that agrees with its parent — and dropping the registry's `RunClassConstructor` fails
  2, since a property of a type nothing has touched would otherwise correctly report not existing.
  Ignoring the attribute's declared default does not fail a test: it fails to *compile*, because the
  generated code is type-checked like any other, which takes a class of generator bugs off the
  testing budget entirely.
- ✅ **Hit testing and routed events.** Layout results accumulate into document-space rectangles once
  per pass; `HitTest` finds what is under a point front to back; events route capture → target →
  bubble with `Handled`, and a captured pointer overrides the hit test entirely.

  ⚠ **The first version skipped a subtree whenever the point was outside its parent**, which is
  wrong for CSS's default: `overflow: visible` means a child may hang outside and still be drawn, so
  it must still be clickable. That makes every dropdown, tooltip and popover unhittable, and the bug
  looks like the click landing on whatever is behind them. The clip is asked about on the *parent*,
  because the child has no idea it is being cut — and a dead condition in the first draft was hiding
  the whole question.

  ⚠ **`pointer-events: none` is transparent without making its children so**, which is what makes an
  overlay usable — the subtree-as-one-unit reading either blocks everything under a full-screen layer
  or lets clicks through a modal.

  Verified by sabotage: testing children in document order fails 1, skipping a subtree outside its
  parent fails 1, hiding the children of a `pointer-events: none` element fails 1, ignoring pointer
  capture fails 1, and letting `Handled` not stop the route fails 1.

  ⚠ **One sabotage failed to fail, and the comment was corrected rather than the code.** The router
  snapshots the route before invoking anything, on the argument that a handler may change the tree
  mid-event. That is the right model and it is currently *untestable*: the tree is append-only and
  `Parent` is fixed at creation, so no handler can change an ancestor chain and walking as you go is
  indistinguishable. Kept as insurance, and now labelled as insurance rather than as a covered claim.
  *(Removal has since made the first half of that reason false and the conclusion still true — see
  the removal entry below. `Parent` survives removal, so the chain a later walk would climb is still
  there; reparenting is what will finally make the difference visible.)*

  Doc 09 asks for a quadtree over the top level and says the simple version was "measured to be
  sufficient". This descends the tree, entering only subtrees containing the point; **that
  measurement has not been taken here** and should be before the quadtree is written.
- ✅ **The draw list**, and with it the whole chain this assembly exists for: cascade → bridge →
  flexbox → commands. Backgrounds, borders, corner radii and clip push/pop, in document space.

  **Painting order is document order and hit testing walks it in reverse**, asserted together in one
  test — the element drawn last is on top, so it is the one a click lands on, and a rule that made
  them disagree would be a UI where things are not where they look.

  **The frame diff is against the previous content, not a dirty flag**, which is what doc 09 asks for
  when it says a static UI re-submits a cached command buffer. A flag says what the framework
  believes changed; the content says what actually did, and they part company exactly when something
  is invalidated too eagerly — the failure a cache should absorb rather than propagate. There is a
  test where a class changes, the computed style changes, and the drawing correctly does not.

  ⚠ **ExCSS expands `border-color` and `border-radius` as well** — the second time that assumption
  has cost something here. Written against the shorthands, every border and every rounded corner in
  the document silently disappears. And a corner radius arrives as *two* lengths even when one was
  written, since CSS corners are elliptical; `DrawCommand` carries one radius for four corners, so
  the rest is dropped and owed rather than approximated.

  Verified by sabotage: painting children before their parent fails 5, never popping a clip fails 2,
  bumping the version on every rebuild fails 2, and emitting commands for a zero-sized element fails
  1 — that last only after a test was written that could reach the guard, since `display: none`
  arrives as geometry rather than as a keyword and nothing else in the suite gave a hidden element
  anything to draw.
- ✅ **Focus, focus scopes and the tab order.** `Focusable`, `TabIndex` and `IsFocusScope` are
  `[UiProperty]`s — the property system's first real user rather than a test of it — and `:focus` and
  `:focus-within` are set on the style tree, so a focus ring is a stylesheet's business rather than a
  special case in the renderer.

  **HTML's tab order, faithfully rather than sanely.** A positive index comes before *every* zero, so
  one element written at the bottom of a form jumps to the front of it; zero is document order;
  negative is focusable but not a stop. Quietly reinterpreting this gives a tab order nobody can
  predict from the markup. The sort is stable because two elements sharing a positive index must stay
  in document order relative to each other — an unstable one changes the tab order with the number of
  elements on the page, which is a bug nobody can reproduce.

  Verified by sabotage: sorting positive indices among the zeroes fails 2, an unstable sort fails 2,
  making negative indices stops fails 1, and ignoring focus scopes so Tab escapes a dialog fails 1.

  ⚠ **Two sabotages failed to fail, and both were answered by changing what was written rather than
  what runs.** One found **dead code**: `Collect` filtered on tab index, which the two buckets in
  `TabOrder` already do, so a negative index was excluded twice — and a redundant test in a second
  place is worse than none, because a reader believes the rule lives in both and keeps them in step.
  The other found **a comment inventing a consequence**: the focus-state walk clears the old chain
  before setting the new one, and the comment claimed this stopped a transition restarting. It does
  not — state is only read during `Update`, which cannot run part-way through the method, so nothing
  can observe the intermediate. The ordering is still the correct model; it is now labelled as
  unobservable rather than as defended.
- ✅ **Arrow navigation, by the beam model.** Tab walks an *order* the document decides in advance;
  an arrow walks a *layout*, decided by where things ended up. Two questions that move the same
  focus, so `NavigationDirection` is its own enum rather than two more members of `FocusDirection`.

  A candidate has to start past the edge the arrow points at. Among those, the ones whose other axis
  overlaps this element's are **in the beam**, and any of them beats any candidate outside it however
  close that one is; inside the beam nearest along the axis wins, outside it nearest by straight line
  between the two rectangles. **The point is that there is no constant to tune.** The alternative —
  distance along plus some multiple of distance across — has no principled multiplier, so it gets
  tuned until the layouts someone happened to test behave and Down drifts diagonally in the ones they
  did not.

  ⚠ **Touching is not overlapping**: the beam test is a strictly positive overlap, because two cells
  of a grid share an edge exactly and a non-strict test puts the diagonal neighbour in the beam
  alongside the one directly below. **An element's own focusable children fall out as unreachable**
  without a rule saying so — they are inside it, so they are past none of its edges. And **arrows do
  not wrap**, because holding Down in a list that wrapped would never settle.

  Verified by sabotage: a non-strict beam overlap fails 4, letting a near candidate outside the beam
  win fails 5, requiring a strict gap so abutting elements are unreachable fails 5, and navigating
  the whole tree rather than the focus scope fails 1.

  ⚠ **One sabotage failed to fail, and it is the same shape as the one the bridge found.** Deleting
  the zero-size guard broke nothing: the test used `display: none`, which arrives as a 0×0 box, and a
  box with no extent on *either* axis shares no width with anything, so the beam had already excluded
  it a step earlier. The guard is for an element collapsed on one axis only — full height and no
  width, squarely in the beam and exactly as near as the real destination. **A test that cannot reach
  the code it names passes for the wrong reason**, which is now the fourth time in this phase.
- ✅ **Gestures.** Taps with a count, long presses and drags, read out of the pointer stream by
  `GestureRecognizer` and delivered as routed events like anything else.

  **Time arrives on the event rather than from a clock the recogniser reads.** One that calls
  `DateTime.Now` cannot be tested without sleeping, cannot replay a recorded trace, and reports a
  different gesture when a breakpoint holds the frame — and the platform layer already knows what
  time the input happened. **A long press is the one gesture that fires because nothing happened**,
  which is why `Tick` exists: nothing in the input stream can report the absence of input.

  ⚠ **Slop is one-way.** Once a press has wandered far enough to be a drag it can never be a tap
  again, even when the pointer returns to where it started — which it does at the end of every flick
  that overshoots and settles. Asking how far the pointer is from the press *now* fires a tap at the
  end of a scroll. **A double tap raises `TapEvent` twice, counting up**, rather than raising a
  different event, because splitting them forces every handler to answer "is a double tap also two
  taps" and there is no general answer.

  ⚠ **One pointer at a time**: state is per pointer id, so two fingers are two drags. Pinch and
  rotate are owed rather than approximated, and a test says which of the two it currently is.

  Verified by sabotage: not latching the slop fails 5, letting a long press also be a tap fails 1,
  delivering a drag wherever the pointer now is rather than to the element it started on fails 1,
  dropping either half of the double-tap test — the interval or the distance — fails 1 each, letting
  a drag become a long press fails 1, and reporting a cancelled drag as completed fails 1.

  ⚠ **One sabotage failed to fail, and the comment was corrected rather than the code.** The previous
  tap is remembered as a nullable, and the comment claimed a plain struct would make the first tap of
  a session a double tap. It does not: the count is derived as `previous.Count + 1` and a default tap
  has a count of zero, so the answer is one either way — by arithmetic rather than by the guard. Kept
  because "there has not been a tap yet" is not "there was a tap at the origin at time zero", and
  now labelled as unobservable.
- ✅ **Text runs.** `font-family` names a face in a `FontRegistry`, the string is shaped through the
  document's cache, the layout tree asks the shaping how big it is through a measure function, and
  the draw list gets a `Text` command naming a range of one glyph buffer. Four things built
  separately in 4a–4c, finally joined.

  **Fonts are registered rather than discovered**: a game ships its fonts, and an interface laid out
  by whatever the operating system happened to have installed lays out differently on every machine.
  ⚠ That registry is **not font fallback** — the list is tried until a *registered* family is found,
  not per character until one with a glyph is found — and weight and style matching is not there
  either. Both owed and said rather than half-implemented.

  ⚠ **The frame diff has to cover the side buffer.** A command names a *range* of the glyph array, so
  two frames whose text changed from one word to another of the same length hold byte-identical
  commands and entirely different glyphs; comparing commands alone, the label changes and the version
  does not.

  ⚠ **Two findings from the layout tree, both of which it was right about.** A node that measures
  itself may not also have children — its size would be decided twice by two rules that need not
  agree — so **an element with text is a leaf, full stop**, and the note claiming it would draw both
  was wrong before a test reached it. And a node may not be hand-dirtied unless it measures itself,
  which makes the null-or-empty test in the change callback load-bearing rather than tidy: `null` and
  `""` are both "no text", so setting one to the other reaches the dirty call with no measure
  function attached and throws.

  ⚠ **A laid-out width is a measured width snapped to the pixel grid**, so text measurement and
  element size differ by a fraction, and a test written against the exact measurement fails in a way
  that looks like a scaling bug.

  Verified by sabotage: drawing the run from the top rather than the baseline fails 1, ignoring the
  padding fails 1, diffing the commands without the glyph buffer fails 1, and shaping outside the
  cache fails 2.

  ⚠ **Two sabotages failed to fail, and both were answered with better tests.** Deleting the y
  negation broke nothing, because **every Latin glyph in the test font sits on the baseline at a zero
  offset** — the assertion was vacuous, and it is now written in Tai Tham, where a vowel sign hangs
  below the letter and the sign of the offset decides which side. And leaving the measure function
  attached when the text goes broke nothing, because a measure function over no text answers zero and
  looks exactly like not having one; the consequence is that the node stays a *leaf*, so the test now
  gives the ex-label a child and checks that it is laid out.
- ✅ **Path rendering, and the custom-drawing hook it exists for.** A stylesheet describes boxes and
  most of an interface is boxes; a chart, a knob and a hand-drawn icon are not. `UiElement.OnDraw` is
  where a control draws itself, `DrawContext` is what it draws with, and `PathBuilder` is what it
  draws — called after the element's background, border and text and before its children, which is
  where CSS puts an element's own content.

  ⚠ **Curves are kept as curves.** How finely to flatten a Bézier depends on how large it will be on
  screen, which is a device scale the draw list does not know; flattened here, a path built once and
  drawn at two zoom levels is faceted at one of them and nothing downstream can recover the curve.
  **One fixed-size struct per verb** rather than Skia's verb array beside a point array — smaller
  there, but it needs two ranges on the command and two cursors to walk, and one array keeps the
  frame diff a comparison and the command's reference one range.

  ⚠ **`Close` carries the point it closes to**, because a stroked path's closing join is drawn
  differently from a line back to the same place — and a second contour closes to its own `MoveTo`,
  which is what makes a path with a hole in it possible. `EvenOdd` is carried alongside `NonZero`
  because it is how most icon sets punch the hole in a letter `o`.

  Verified by sabotage: turning `Close` into a line fails 2, forgetting the contour start on `MoveTo`
  fails 3, diffing without the path buffer fails 1, emitting a command for an empty path fails 1,
  drawing custom content over the children rather than under them fails 1, and dropping the clip
  fails 2.

  ⚠ **One sabotage failed to fail and the test was sharpened.** Resetting the pen in `Clear` broke
  nothing, because every test cleared and then moved — the reset only shows when a caller reads
  `Current` on a freshly cleared builder, which is what a control reusing one between frames does.
- ✅ **Batching.** `DrawBatcher` groups the frame's commands into runs a renderer can submit as one.

  **Runs of consecutive commands, and never a reordering** — which is worth being blunt about,
  because reordering is what batching means everywhere else. A 3D renderer sorts draws by material
  because a depth buffer decides what ends up in front; a user interface has no depth buffer, so
  order *is* the answer, and moving two runs of the same font together across the panel between them
  draws the text over the panel that was meant to cover it. The win is therefore bounded and honest:
  a hundred alternating labels and boxes batch into two hundred batches, and that is correct rather
  than a failure to optimise.

  **The batches partition the commands** — every one is in exactly one batch, in order — so a
  consumer walks the batches alone and cannot miss anything, which is why a clip gets a batch of its
  own instead of being skipped. And batching sits **behind the frame diff**: a frame that drew the
  same thing has the same batches by construction, so the cached command buffer keeps its batches
  with it, and `Batched` counts the rebuilds.

  ⚠ **`BatchKind` was written as a guess at a renderer that turned out to exist**, and checking it
  against `Vixen.Rendering` changed what it claims rather than what it does. Three findings:

  1. **A pipeline is already keyed**, on the effect, the stage, the vertex layout and the render
     output — and `PipelineKey`'s own remarks argue those four are what make the key complete rather
     than merely sufficient so far. Only **two** of them are a draw list's to know: which shader and
     which vertex format. The stage carries blend, depth and raster state and the output carries
     attachment formats, and both belong to the compositor. So `BatchKind` is a coarse stand-in for
     two of four, and the thing it must not do is grow to describe the other two.
  2. **The renderer does not use a batch list at all.** `MeshRenderFeature` walks its nodes in sorted
     order and re-binds only when the pipeline handle changes — the same runs, two locals, no array.
     That is right for a mesh, whose nodes are rebuilt from culling every frame so nothing
     precomputed survives; a UI is the opposite case, since most frames draw what the last one drew,
     and the runs are worked out *behind the frame diff*. If the UI render feature binds on change
     anyway, `Batches` is what stops it regrouping every frame; if it does not, `Batches` is the
     thing to delete. **Recorded as the open question it is rather than settled either way.**
  3. **`RenderSortMode.ByGroup` already exists and says it is "for UI and anything else already
     ordered."** It sorts stably on a group value with depth left out — which means the UI render
     feature has to make that group *be* the painting order, because a group meaning a material or a
     texture would reorder the interface on the way to the screen. The batch index is that number,
     and this is the no-reordering argument arriving independently from the renderer's side.

  What is not a guess is that the batches are contiguous, ordered and maximal — properties held by a
  CsCheck generator over random command streams rather than by examples.

  Verified by sabotage: never merging fails 3, merging with any earlier batch rather than the last —
  the reordering this exists to refuse — fails 5, letting a clip join a batch fails 1, dropping the
  font or the fill rule from the key fails 2 each, treating a stroke as a fill fails 2, and batching
  on every frame rather than behind the diff fails 1.
- ✅ **Element removal**, which was 4d's longest-standing owed item and the one the element tree kept
  being described as too incomplete without. `UiElement.Remove()` takes an element and its subtree out
  of all three stores at once — which is why it lives on the document rather than in any of them.

  ⚠ **A removed style slot is tombstoned and never reused, and that is the decision.** The obvious
  implementation is a free list, and it would quietly break three separate things resting on one
  unwritten invariant — *a parent's index is lower than its children's*. `ResolveAll` walks slots
  ascending because that is parents-before-children and inheritance needs it; the incremental pass
  uses the index as a queue priority for the same reason; and the bloom sweep gives up the moment a
  climb passes below the ancestor's index. Fill a hole with a new child of a later parent and the
  first two resolve a child before its parent, while the third answers "not a descendant" about
  something that is — a descendant selector that silently stops matching. So slots leak,
  `StyleTree.DeadCount` says by how much, and **compaction rather than reuse is the fix**, because
  rebuilding without the dead slots preserves relative order where reuse is exactly what does not.
  ~~Owed, and it is the one thing keeping this from being finished rather than merely working.~~

- ✅ **Compaction**, which was that owed item. `StyleTree.Compact` rebuilds the store — and all three
  arenas, so it also reclaims the child runs `AppendChild` abandons when it relocates one — and
  **hands back a mapping rather than doing it quietly**, because a slot is an index and moving one
  moves every `StyleNodeId` in existence. `StyleUpdater` and `Animator` follow it; `UiDocument` owns
  the ids, so `CompactStyles` is what walks the element tree applying the mapping, and `Update` calls
  it when the tombstones outnumber the elements and there are at least sixty-four of them. Not per
  removal: compaction is O(elements), so doing it there would make tearing down a thousand-row list
  quadratic — which is the loop that produces the leak in the first place.

  ⚠ **The animator is remapped, not cleared.** Clearing was one line and already available, and it
  restarts every fade on the frame a document happens to compact — so deleting one row would jolt the
  rows transitioning around it. A worse bug than the leak, and rarer, which is the combination nobody
  finds.

  The oracle is **a tree that never held the removed elements**: compaction should leave a store
  indistinguishable from one built without them, so the test builds that store and compares every
  observable rather than asserting the arrays.

  Verified by sabotage: thirteen, and ⚠ **five failed to fail first time**, four of them because the
  fixture could not reach what they broke. The arena tests had classes and attributes only *before*
  the removed subtree, where a stale range still lands on the right run. The document test removed a
  *tail*, where every survivor keeps the slot it had, so the mapping is the identity and deleting the
  remap changes nothing — a compaction test whose survivors do not move cannot see the only thing
  compaction does. The fifth was a sabotage that was a no-op. ⚠ And clearing the tail of the arrays
  turns out to be **unobservable** — every getter validates against `Count` and `CreateElement`
  writes every field of a slot before handing it out — so it is kept and labelled as insurance, with
  what it insures against written next to it.

  ⚠ **The layout tree already reused its slots and the style tree cannot**, and the asymmetry is not
  an oversight: the layout algorithm descends from the root, so it never cared what order the slots
  were in; the cascade walks the array by index and reads each parent's resolved table, so for it the
  slot number *is* the ordering.

  ⚠ **The frame pass now walks the tree rather than a list in creation order** — which removal forced
  and which should have been there anyway. The list version was correct only because elements were
  created parents-first and never removed, so its index order *happened* to be its depth order. The
  property the pass needs is "parents before children", and a descent is that by construction. It
  also deleted two parallel arrays: what an element had applied last time now lives on the element,
  so removing one takes its bookkeeping with it.

  ⚠ **Whatever was pointing at it has to stop** — the focus, a captured pointer, a gesture in
  progress — and each has to be checked against the whole *subtree* rather than the element itself,
  because a dialog closing takes the focused field inside it. A drag whose target is removed ends
  **silently** rather than as a cancellation: a cancelled drag tells its target to put back what it
  was carrying, and the target is the thing being deleted.

  Verified by sabotage: leaving the later siblings' `IndexInParent` stale fails 1 — `:first-child`
  landing on nothing — releasing nothing that pointed at it fails 3, checking only the element itself
  for the focus rather than the subtree fails 1, letting a gesture survive its target fails 1, and
  letting a removed element answer instead of throwing fails 2.

  ⚠ **One sabotage failed to fail.** Killing only the element handed in, rather than its descendants,
  broke nothing: the test asserted `IsRemoved` on the children, and that flag is set by the document's
  own walk rather than by the store. The descendants would have been unreachable from any live parent
  and cascaded every frame regardless. The test now asserts `StyleTree.LiveCount`, which is the store
  speaking for itself.
- ✅ **`Vixen.Ui.Composition` — the runtime a compiled `.vxml` calls.** `Component`,
  `BuildContext`, and the two primitives that make the shape of the tree depend on state. This is
  what the markup emitter was writing against, and it is now the real thing rather than a declared
  contract: `Vixen.Ui.Markup.Tests` compiles its output against this assembly, loads the result,
  builds it into a `UiDocument` and drives it with a signal.

  **`@if` and `@switch` are one primitive.** `ctx.Switch` takes a selector saying which arm is live
  and a builder that constructs it — a condition chain and a pattern match differ only in how the
  number is produced, and two constructs for swapping a subtree in and out would be two places to
  get the disposal of a branch's effects wrong.

  ⚠ **Regions answer "where", and they have to ask rather than remember.** An `@if` in the middle of
  a `<div>` has siblings on both sides and the element tree only appends, so a region knows what it
  comes *after*: an element answers "one past me", a preceding region answers "wherever I end", and
  an empty one defers to its host. The first version snapshotted the position instead, which put a
  branch that *opens* a loop item at index zero of the parent — inside somebody else's item. Found
  by a sabotage that failed to fail, fixed, and now has a test whose only job is that shape.

  ⚠ **The alternative was an anchor element**, as the DOM frameworks use. Here it would be a real
  element in all three stores, and a real element is counted by `:nth-child`. Rows that stripe
  wrongly because of a hidden marker is a worse bug than this is complexity.

  Prerequisite, and its own piece of work: **`UiDocument.Move`**, reordering a sibling across the
  element, style and layout trees at once. Reordering is a *style* change as much as a layout one —
  `:nth-child`, `:first-child` and the sibling combinators all read position — which is exactly why
  a reconciler that moves elements beats one that rebuilds them, since a rebuild loses the focus and
  the scroll offset too. Within one parent only: reparenting would move a style slot relative to its
  new parent's, breaking the same invariant that makes removal tombstone rather than reuse.

  Also landed: `UiElement.PropertyChanged`, raised through a non-virtual `RaisePropertyChanged` that
  the generated setter calls — so an override forgetting to call its base cannot silently
  unsubscribe every two-way binding on the element.

  Gate: 203 tests in `Vixen.Ui`, and the markup project's end-to-end one — markup to syntax tree to
  component model to C# to IL to an element tree that reacts to a signal.

  Verified by sabotage: a region that ignores its predecessor fails 5, one that follows nothing and
  does not ask its host fails 1, a region that clears without disposing its effects fails 1, a move
  that skips the style tree fails 1, a style move that leaves `IndexInParent` stale fails 1, a loop
  that rebuilds instead of reusing fails 2, a loop that does not rechain after a reorder fails 1, a
  component that builds into the mount rather than its own root fails 16, a class binding that
  appends rather than replaces fails 1, `once` that does not unsubscribe fails 1, and children that
  ignore the default slot fail 1.

  ⚠ **Two sabotages failed to fail and both were test bugs worth having found.** A leaked effect
  counted its runs *after* touching its element — and touching a removed element throws, so the
  scheduler suspended the effect before it could count, and a leak looked like a clean shutdown. And
  a stale `IndexInParent` broke nothing because the only reorder test read the child arena, which is
  a different fact; it takes a `:first-child` rule to reach the field at all.

  Owed here: named slot projection, `scoped` actually scoping, a component stylesheet loaded once
  per type rather than per instance, and a longest-increasing-subsequence pass so a reorder moves a
  minimal set. The last is correctness-neutral — a move that changes nothing returns immediately.
- Owed in `Vixen.Ui`: style-slot compaction, access keys, line wrapping, rich-text runs,
  font fallback and weight matching, gradients, per-corner elliptical radii, pinch and rotate,
  virtualisation primitive, multi-window and DPI.
- `Vixen.Ui.Markup`: ✅ **VXML — lexer, parser, binder, emitter and `#line` mapping.** A `.vxml`
  becomes a green/red tree over `Vixen.Core.Syntax`, then a `BoundComponent`, then a C# partial
  class. Second grammar on the shared tree, which is the first evidence that the Phase 0 extraction
  paid for itself: VXML brought `Syntax.xml`, a kind enum and two files of front end, and got trivia
  fidelity, precise spans and one diagnostics model for nothing.

  ⚠ **The binder resolves no types, and the plan said it would.** [09](09-ui-framework.md) specified
  a binder running inside the source generator, resolving `<Counter Title="x" />` against the C#
  type and typechecking the parameter through Roslyn's `Compilation`. It does not need to: if the
  emitter writes the tag name where a type name goes and the attribute name where a property name
  goes, both under a `#line`, then an unknown component, a misspelt parameter and a wrong expression
  type are **all reported by Roslyn against the right character of the `.vxml`** with no type
  resolution on this side. That leaves the binder exactly the mistakes a C# compiler cannot see —
  duplicate attributes, an event handler given a string, two slots with one name, a loop without
  keys — which is what every `VXML2xxx` now is. Doc 09 is corrected accordingly; there is
  deliberately no diagnostic range for type errors.

  ⚠ **`#line` uses the span form, not the line form.** `#line N "file"` lands a squiggle at the
  start of a generated line, which for `ctx.Bind(n3, "class", () => kind)` is several tokens from
  the word that came out of the markup. `#line (l,c)-(l,c) offset "file"` carries the exact
  characters — verified by asserting that a missing member reports at the *member name's* column in
  the `.vxml`, not the expression's.

  **Two rules earn their keep.** *A `}` closes a directive body only at the element depth its `{`
  was written at*, so `@if (x) { <div>a } b</div> }` reads the first brace as text with no lookahead
  and no backtracking; the same test keeps `case` inside a `<p>` from starting a switch section.
  And *a whitespace run that crosses a line break is trivia, one that does not is text* — indentation
  is formatting, the space you typed on one line is content, and no later pass has to guess.

  **VXML never parses C#.** Every expression, `@code` body and `<style>` body is one token found by
  balancing, with C# strings, chars, comments, verbatim and raw strings skipped. `SkipCSharp` knows
  how a literal ends and nothing else.

  Gate: 100 tests. Byte-exact round-trip over every construct **and over every prefix of a real
  file** — the editor reparses on each keystroke and half of those land mid-word. The emitter's gate
  is a real Roslyn compilation of its output, because "does this compile" and "does the error point
  at the markup" are questions only a compiler can answer.

  Verified by sabotage: a keyword boundary that allows name characters fails 1 (`default:` never
  ends), a brace that closes at any depth fails 1, a code body that does not skip C# strings fails 2,
  inverting the mismatched-close ancestor lookup fails 2, dropping skipped source instead of keeping
  it as trivia fails 1, treating any `on…` name as an event fails 1, a `#line` that ignores the
  generated offset fails 5, and a keyless loop that keys by index fails 1.

  ⚠ **One sabotage failed to fail.** Making the "component builds nothing" check stop at control
  flow broke nothing — every test whose markup lived inside an `@if` also had markup outside one, so
  the recursion the check had just been given was never reached. A test for it was added, and the
  sabotage then fails 1.

  ⚠ **The runtime the generated code calls now exists** — see `Vixen.Ui.Composition` above, landed
  immediately after this. The note that stood here said the emitter's output was compiled against a
  written-out declaration of the contract and that nothing built an element; that was true when it
  was written and is not now. The gate compiles against the real assembly, loads it and runs it.
  ✅ **`@namespace`** — the file wins over the build. The generator offers the project's root
  namespace plus the file's folders, which is right nearly always and is not right for a component
  whose folder is not what its namespace should be; renaming the folder is not a fix a library can
  rely on. It interleaves freely with `@using`, because a header order nobody can remember is a
  diagnostic nobody wants, and a second one is rejected rather than replacing the first — falling
  through to the same "unexpected" path every other stray directive takes, so its characters survive
  in the tree as trivia. ⚠ Emitted **file-scoped**, whatever it came from: every `#line` span carries
  a generated column computed from the emitter's depth, and a braced namespace shifts all of them by
  four. Verified by sabotage: six, all landing — a braced namespace fails 4, the caller winning
  fails 1, an unbound directive fails 2, a duplicate that replaces fails 1, a fixed header order
  fails 1, and a keyword length wrong by one fails 6.

  ✅ **Incremental reparse**, which was the other owed item and the one recorded as an open *design*
  question rather than as missing code. ⚠ **VXML's unit of reuse is a content node whose subtree
  reported nothing**, and the reasoning that settles it is the point: the worry written down was that
  an element is reusable only if nothing about its *enclosing* content changed, because an unclosed
  tag above it changes what it is. That is true of where the node ends up and false of the node
  itself — `<panel/>` parses to the same green node whether it is a child or a sibling, and the
  enclosing parse decides which either way. The parser's one piece of enclosing state is the list of
  elements currently open, **every branch that reads it reports a diagnostic**, so a clean subtree
  never consulted it. The same rule settles a second problem for free: a reused subtree's diagnostics
  are not re-reported, and one that had none has none to lose.

  The file is always re-*lexed*; only the parse reuses. That is what keeps a reused node correct when
  an edit above it disturbs the lexer's mode stack.

  ⚠ **And it found a latent bug in the shared parser.** An incremental reuse is the only place a
  parser resumes at a position computed from a *text offset* rather than one it had already been at,
  and the token starting exactly where a reused node ends is very often the whitespace before the
  next real one. `ResetTo` left `RawPosition` on trivia, which breaks the invariant every `Kind` and
  `At` rests on — the symptom was an element whose close tag vanished the moment its last child was
  reused. `SyntaxParser.ResumeAt` is the fix, and Raven's member reuse had the same shape and had
  been recovering by accident, because its caller skips newlines immediately afterwards.

  Gated by a full reparse as the oracle over two thousand random edits, plus a run of chained edits —
  the shape a keystroke actually arrives in, and the one where a position shifted by the wrong delta
  accumulates instead of showing up once. `SyntaxTree.ReusedNodes` says reuse happened, because equal
  trees only prove it was allowed to.

  Verified by sabotage: eight, five landing. ⚠ **Three did not**, and all three are the same finding:
  the `Blender`'s one-character margin, the token-boundary re-check and the edge case of a diagnostic
  ending exactly where a node starts are each unreachable because the others already cover them — an
  edit that merges with a reused node's first token moves that token's start, so the lookup by full
  start simply misses. Insurance, labelled as insurance.

  Still owed: `bind:` update events. The `IIncrementalGenerator` wrapper is built — see `Vixen.Ui.Markup.Generators`
  below, which also records the two bugs in *this* project that only a generator could find.
- ✅ **`Vixen.Ui.HotReload` — three reload channels, and what each one is allowed to lose.**

  **Styles** reload without rebuilding anything: the rule set is replaced and the cascade runs
  again, so every element keeps its identity and therefore its focus and its animation state. This
  needed a change one layer down — `StyleEngine` now keeps the text of every sheet and rebuilds from
  them, because rules are appended and never removed and a sheet cannot be lifted out of the middle
  of a set. ⚠ **That is the difference between a reload and an overlay**: replaying the sheets is
  what makes a *deleted* rule stop applying, where re-adding the new text leaves the old one
  underneath, still winning wherever the new one says nothing. A sheet that does not load puts the
  previous one back, because half a stylesheet is worse than the old one.

  **Markup** re-runs `Build` on the same component objects, so their fields — their signals above
  all — survive by construction. ⚠ **The elements do not, and cannot**: two `Build` bodies are two
  different programs, with no identity shared beyond position, and reconciling on position alone
  would move state onto whatever happened to be in the same slot. The focus is put back by path and
  the report says whether that worked. ⚠ **A `Build` that throws leaves the component empty** —
  clear-then-build has no snapshot. Doc 09 promised "a deliberately broken file leaves the previous
  UI intact"; that is true of the *file* case, where a broken `.vxml` does not compile so no update
  arrives, and not of a `Build` that throws at run time. Recorded rather than glossed.

  **Component replacement** is the third channel and the only one `[HotReloadState]` is for. The
  original plan implied the attribute carried state across every reload; it does not need to,
  because a re-run keeps the instance. It earns its keep when the instance is replaced — a rude edit
  — and it carries by name, checking that the value still fits, because the point of a reload is
  that the type changed.

  ⚠ **What this does not do is deliver the new code.** A changed `.vxml` becomes a different `Build`
  only after something recompiles it. `MetadataUpdate` is registered as a .NET
  `MetadataUpdateHandler` and reloads every live host, so the runtime half is wired; the build half
  is `Vixen.Ui.Markup.Generators`. ~~which does not exist yet~~ — **corrected in the build: it does,
  and it landed immediately after this.** See its entry below. The sentence stood for one commit and
  the note is kept rather than deleted, because what it names is still the boundary: this assembly
  reloads, and something else has to compile.

  Gate: 15 tests. Verified by sabotage: a style reload that adds instead of replacing fails 2, a
  broken sheet that is not rolled back fails 1, asking only the loader what went wrong fails 1, a
  rebuild that leaves the previous elements fails 5, a replacement that carries nothing fails 1, one
  that lands at the end rather than in its place fails 1, a focus reported restored without being
  restored fails 1, carried state written without a type check fails 1, and a rebuild that keeps the
  previous build's slots fails 1.

  ⚠ **Three sabotages failed to fail.** Two were test gaps, now closed — a carried value of the
  wrong type was aimed at a member that was never carried, and the slot test only covered a slot the
  new build *also* declared, where overwriting hides the bug; it takes a slot the new build drops.
  The third was a false claim in a comment: `ReloadStyles` said forgetting every applied style
  catches a case a plain `Invalidate` would miss, and it does not — the reload rebuilds the
  interning cache, so every computed style is a new object and the pass already reports every
  element as changed. The call is kept and the comment now says why it is redundant today and what
  would make it necessary.
- ✅ **`Vixen.Ui.Markup.Generators` — the `IIncrementalGenerator`, and the markup channel is now
  usable on a file save.** A `.vxml` in a project becomes a class in the compilation with no item in
  the `.csproj`: `Vixen.Ui` carries `build/Vixen.Ui.targets`, which globs the files into
  `AdditionalFiles`, and both UI generators now travel inside that package. The namespace is the root
  namespace plus the file's own folders, which is the convention a hand-written `.cs` beside it
  already follows.

  ⚠ **The front end is compiled twice, and the alternatives were worse.** A generator runs inside the
  compiler, so it targets `netstandard2.1`; `Vixen.Core.Syntax` and `Vixen.Ui.Markup` are `net10.0`.
  Multi-targeting them fixes the *compile* and leaves the *load* — an analyzer's `ProjectReference`
  dependencies do not reach the analyzer path, so both assemblies would still have to be put there by
  hand and mis-versioned against the `net10.0` copies. Linking the sources gives one self-contained
  analyzer with nothing to resolve, and it is cheap here for two checked reasons: neither project
  touches the file system, the environment or the console, so RS1035 has nothing to say; and
  `Vixen.Ui.Markup` reaches the internal green tree through `InternalsVisibleTo`, which one assembly
  makes moot. The cost is one compatibility file — `init` needs `IsExternalInit`, and 116 guard
  clauses call throw helpers that live *on* framework exception types where no extension method
  reaches.

  **Two bugs, both found by the move rather than by the tests.** The stricter analyzer set caught
  every diagnostic message in `Vixen.Core.Syntax` being formatted with `CultureInfo.CurrentCulture`,
  which makes one machine's compiler output differ from another's — the templates are hard-coded
  English, so it localised nothing and only cost determinism. And **`VXML1002` and `VXML1003` read
  their span off a node still under construction**, whose position is relative to itself, so every
  unclosed element was reported a few characters into the file whichever one it was about. The
  parser's own tests assert *which* diagnostics were reported and never where; it took a generator
  turning those spans into editor squiggles for the first one to land on line zero.

  ⚠ **Syntax errors stop the emit and binding errors do not**, and the split is the diagnostic
  numbering earning its keep. A `VXML1xxx` means the tree is a guess made during recovery, and C#
  emitted from a guess may not parse — which buries the real diagnostic under a page about generated
  code the author cannot see. A `VXML2xxx` means the tree is right and its meaning is wrong, so the
  class is still emitted and the type keeps existing: withholding it turns one real error into one at
  every use site, none of which names the cause.

  Gate: 19 tests. Most drive a `CSharpGeneratorDriver` — including one that emits the assembly, loads
  it and drives the component with a signal — and **two run a real `dotnet build`**, for the reason
  `Vixen.Sdk.Tests` gives: a glob in a `.targets` and two `CompilerVisibleProperty` items do not exist
  until a build engine reads them.

  Verified by sabotage: folding the hint name's underscores fails 1 (Roslyn throws on a duplicate hint
  name, and naming files after their component collides between folders), emitting from a recovered
  tree fails 1, withholding the class on any error fails 1, keeping an absolute path in the hint name
  fails 4, dropping the namespace fails 4, taking a diagnostic span off a detached node fails 2 here
  and 1 in `Vixen.Ui.Markup.Tests`, treating every additional file as markup fails 1, and dropping the
  diagnostic message's arguments fails 1.

  ⚠ **Three sabotages failed to fail.** Two were test gaps, now closed: nothing reached the
  namespace's leading-digit guard, because every fixture used folders that were already C#
  identifiers; and nothing reached `EquatableArray`'s equality at all, which takes an edit that
  re-runs the compile step and then *agrees with itself* — plus its mirror, an error that becomes a
  different error, because an equality that always answers "same" passes the first test and leaves a
  corrected file still showing its old message.

  The third was a false claim in a comment, and the pattern is the one this phase keeps finding.
  Passing a VXML message as a composite format string was written up as a `FormatException` surfacing
  as CS8785. **It is not**: Roslyn catches it and falls back to the unformatted template, which here
  is the finished message, so a brace arrives intact and nothing crashes. The `{0}` indirection is
  kept — the fallback discards arguments silently and has no contract — and is now labelled as
  insurance rather than as a covered claim.

  Still owed: incremental reparse and the `vixen` CLI path for a build that wants the generated C#
  on disk.
- ✅ **UI render feature integrated into the renderer** — `Vixen.Ui.Renderer`, written up under 4c
  because it is the other end of the geometry builder. `UiRenderFeature` is a `RootRenderFeature`
  whose objects are surfaces; the stage it is drawn in has to sort `ByGroup`, because every other
  mode puts depth in the key and an interface has none.
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

- `Vixen.Shaders`: ✅ **typed parameter/permutation keys and the constant-buffer writers**, with
  `Vixen.Shaders.Generators` emitting both from Raven's reflection — see
  [07 § Generated C# bindings](07-raven-shader-pipeline.md#generated-c-bindings). Still open: the
  effect system, the three cache tiers, build-time permutation pre-generation and
  `Tools/Vixen.ShaderCompilerService`, all of which need `Vixen.Rendering` to design against.
- `Raven/Library`: ✅ the full shader library from [07](07-raven-shader-pipeline.md) — Core, Shading,
  Geometry, Material, Pipeline, PostFx, Ui, Vfx — every shader reaching both backends under `glslc`
  and `spirv-val`.
- `Vixen.Rendering`: ✅ **the spine** — `RenderSystem`, `RenderObject`/`RenderNode`, the
  root/sub render-feature extension points, `VisibilityGroup` with parallel CPU culling,
  `RenderView`/`RenderStage`, sort modes. Still open: the concrete features (mesh, transform,
  skinning, instancing, material, lighting, shadow-caster), GPU culling, and `GraphicsCompositor` as
  an asset.
- Materials: ✅ **the composable feature tree** — `MaterialDescriptor` and `MaterialCompiler` over two
  `compose` slots on the pass, `surface` for what a point on the surface *is* and `shading` for what it
  does with light. Metallic-roughness and spec-gloss, normal map, emissive, occlusion, anisotropy,
  clear coat (with its own normal), sheen and subsurface as features; standard, anisotropic,
  clear-coat, sheen, subsurface, hair and cel as shading models; layering both ways — `BlendSurface`
  for two different surfaces and `MaterialLayersSurface` for N layers of one workflow, which is the
  case composition cannot express because a composed shader's parameters belong to its type. The
  composition is part of the `EffectKey`, so two materials differing only in features are two variants.

  Two things this deliberately does not have. **Transmission** has channels and no shading model:
  refraction needs the scene colour or an environment sample, both of which belong to the pass rather
  than the lobe. And **materials are values, not resources** — a feature that samples a texture needs
  a binding index only the compiled shader knows, which is the same authoring gap the compositor's
  nodes have and closes with the same fix.

  Gate: every feature and every shading model composed into the shipped `ForwardPlus` and run through
  `glslc` and `spirv-val`; the compiler's predicted parameter names held against the checked-in
  reflection in both directions. Verified by sabotage: hard-coding the lobes back into the pass fails
  all six shading-model tests, dropping the chain from the parameter path fails the reflection oracle,
  taking the composition out of the effect key fails the variant test, and handing a depth prepass the
  material's composition fails the one that says it must not.
- Lighting: ✅ **all light types, clustered binning, IBL and reflection probes.** Directional, point and
  spot were already there; tube and rectangle join them in the same eighty-byte record and the same
  loop, through the representative-point approximation rather than LTC — which needs a fitted table an
  offline optimisation produces and this repository cannot run. IBL is both halves of the split sum
  *and the producers for them*: `EnvironmentBaker` prefilters a cube per roughness and
  `SphericalHarmonics` projects it into nine coefficients, on the CPU where closed forms can check the
  result. Reflection probes are parallax-corrected against a box or a sphere and faded against the sky.

  Two defects fell out of wiring the environment up, both of which had survived by looking like
  something else: the pass sampled the reflection at mip zero whatever the roughness said — so
  `Ibl.SpecularLod` and `environmentMipCount` were dead — and the diffuse term took a radiance sample
  where irradiance belongs, which is where a missing `1/π` was hiding.

  ⚠ **Light probes are not built.** The spherical-harmonic half is, and the tetrahedral interpolation
  doc 06 asks for is not: Bowyer–Watson over probe positions needs exact predicates to survive the
  inputs people actually author — a grid of probes is cospherical, and a near-degenerate cell's
  circumsphere is large enough to eat the mesh. Written, found wrong by its own tests, withdrawn.

  ⚠ **A reflection probe applies per group, not per object.** A probe's cube is a texture, so
  per-object selection needs a descriptor set per probe bound per draw, and the per-draw set is owned
  whole by `ForwardLightingRenderFeature` — sharing it is the binding-plan work rather than a detail
  of probes.
- Shadows: CSM, cube, spot, atlas + static caching, PCF/PCSS.
- `Vixen.Rendering.PostFx`: ✅ **the project, and seven of the effect set.** TAA (with its own
  alternating history, since a pass cannot read the target it writes), FXAA, sharpening, ambient
  occlusion at half resolution, fog, outline, and the lens trio of vignette, chromatic aberration and
  grain. Each was a shader that shipped in `Raven/Library/PostFx` with nothing in the engine calling
  it — which compiles, validates and shades nothing, the same failure the material system's BSDF
  layers had.

  Adding the project needed one change in `Vixen.Rendering`: `SceneRenderer`'s phase methods are
  `protected internal`, so a composite node in another assembly could not drive a child. `BuildChild`
  is that seam — without it, a game's own post effect could not be a node at all.

  Publishing the reflection for those shaders also turned up a generated file that did not compile:
  `Fog` declared a `[Permutation] HeightFalloff` and a uniform `heightFalloff`, which Raven allows and
  C# does not, and both became one identifier. Renamed in the shader, where the distinction is worth
  saying out loud anyway.

  ⚠ Still to come: SMAA, MSAA resolve, the full GTAO horizon integral, screen-space reflections,
  depth of field, motion blur, and colour grading as an asset — each needs a shader that does not
  exist yet rather than a pass over one that does. `AutoExposure.rvn` is also still unwired: it is two
  compute passes over a histogram and a buffer that survives the frame, so it wants the compute node
  rather than the full-screen one.
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

- ✅ `Vixen.Editor.Core`: `IEditorCommand` and a `CommandStack` with merging, capacity, transactions
  and clean-marking; a per-document stack plus the project's global one; the signal-backed document
  model (`EditorObject`, `EditorProperty<T>`, `SetPropertyCommand<T>`); `EditorProject` over the
  Phase 3 asset database; `Selection<T>`; and settings assets as `[DataContract]` types under
  `ProjectSettings/`. 48 tests, including the randomised do/undo/redo/merge sequences doc 11 asks for
  — checked against a snapshot model, which caught a merge that kept the wrong pre-edit value.
  Two decisions worth recording: **merging ends on an explicit `Seal()`** rather than on a time
  window, because a window makes how many undo steps an edit produced depend on how fast somebody
  moved a mouse; and **a global operation discards the redo stacks of the documents it touched**,
  declared by the command through `EditorContext.Touch`, because rewriting those entries instead
  would need every command type to know how to be rebased.
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
- ~~Delete the ImGui scaffold.~~ There is none: it was cut in Phase 2 rather than built.
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

- ✅ `Vixen.Physics` (Jolt 2.22.0): bodies, shapes, compound shapes, constraints, character controller,
  raycasts/overlaps, triggers, layers, CCD, ECS integration with a fixed-step sync, debug rendering.
  Everything on the line above is built, plus the bit-exact determinism gate the exit criteria name.
  **One gap, and it is a platform one:** `JoltPhysics.Native` ships no iOS slice, so `Samples/05`
  cannot run there until a static `libjoltc.a` is pinned the way MoltenVK already is. See
  [Vixen.Physics/README.md](../../Core/Vixen.Physics/README.md) § Platforms and § Known gaps.
- ✅ `Vixen.Audio`: OpenAL backend + WebAudio backend, 3D spatialisation, mixer buses, effects (reverb,
  filter), streaming, ECS integration. Vixen mixes in software and the backends are sinks — see
  `Core/Vixen.Audio/README.md` for why, and for what is still owed (surround panning, a sinc
  resampler, voice stealing, and an Ogg/Opus decoder behind `IAudioStreamDecoder`).
- `Vixen.Animation`: skeletal playback, blend trees (1D/2D), layers + masks, state machine, IK (two-bone,
  look-at, foot placement), root motion, events, GPU skinning integration.
- `Vixen.Editor.AnimationGraph`.
- `Vixen.Input`: full device set + the Unity-style action system, `.vxinput` asset, generated accessors,
  runtime rebinding, action-map editor, input debug panel.
- ✅ `Vixen.Navigation` — bake, query, agents and avoidance, **as Vixen's own managed code rather
  than a Recast/Detour binding**. The voxel pipeline (rasterise → filter → erode → regions →
  contours → convex polygons), a tiled mesh whose tiles can be added and removed under live paths,
  `NavMeshQuery` (nearest polygon, A\*, funnel, surface raycast, move-along-surface), and a `Crowd`
  with path corridors, sampled reciprocal velocity obstacles and an ECS bridge. 40 tests.

  **Why the binding was not built:** Recast/Detour publishes no binaries and has no C API, so a
  binding is a C shim plus a build per RID plus an entry in `build/native-dependencies.json`, none of
  which exists — and iOS is NativeAOT-only while WebAssembly has no dynamic loading at all. The
  algorithms are re-derived and credited; no code is copied. `Core/Vixen.Navigation/README.md` records
  the trade and what it costs.

  **Baking is a build step.** `NavMeshImporter` claims `.vxnavmesh`, which names a collision mesh and
  carries its bake parameters in its `.meta` — so the per-target overrides bake a coarser mesh for a
  phone, and the geometry is a declared dependency, which is what makes re-exporting it re-bake. What
  comes out is a serialised `NavMeshAsset`, and two bakes of one level are byte-identical.

  **Zero steady-state allocation is measured, not claimed.** Search, string-pull, raycast,
  move-along-surface and a sixteen-agent crowd each allocate **0 bytes** over a thousand frames after
  warm-up (`NavigationAllocationTests`), with `Benchmarks/Vixen.Benchmarks.Navigation` for the times.
  One thing failed that gate and was fixed: the proximity grid allocated a bucket per newly-visited
  cell, which is a drip that never stops for a crowd that keeps walking somewhere new.

  **Off-mesh connections are in**, as polygons with two vertices: authored on the tile, linked to the
  ground at each end when it loads, searched by A\*, turned at by the funnel — whose portal is a
  single point there, so no special case was needed — and crossed by a crowd agent over time, with the
  authored id and the progress on `CrowdAgentState` so a game can play the climb. The mesh, a
  corridor, a path and a crowd can also be drawn now, which is how a bad bake stops being something
  you infer from a failing path.

  **Region merge-and-filter is in**, which is the half of watershed partitioning that the monotone
  sweep also wants: small regions absorbed into the smallest neighbour that will take them, unreachable
  groups dropped as groups. It is hole-safe because a merge is refused when two regions touch along
  more than one stretch of boundary. At Recast's default threshold it changes nothing here — monotone
  regions are long rather than small — and at a high one it is 23 % fewer polygons for an identical
  path, which is recorded in the README as a number rather than a hope.

  **Pathfinding is sliced and queued.** A search across an eighty-metre level is 13 µs, so 256 agents
  retargeting in one update is 3.5 ms in one frame — more than the whole crowd. `NavPathQueue` runs
  searches a slice at a time against a shared budget and agents keep walking their old corridor while
  they wait. There is one A\*: `FindPath` is the sliced search run to completion, and a test asserts
  the two produce the same corridor polygon for polygon.

  **Watershed partitioning is in**, and with it the hole merging that makes it safe: a region that
  grows round a pillar is traced as two outlines, and the second is bridged into the first with a
  zero-width slit rather than handed to the polygoniser as a solid slab over the obstacle. The ear
  clipper needed a fallback pass for that slit, because a polygon that touches itself has no strict
  ear anywhere near it. **It is not uniformly better and the README says so with a table**: on an
  axis-aligned level the row sweep produces 25 % fewer polygons and bakes in half the time, and on a
  round obstacle watershed is 19 % fewer polygons and 32 % fewer nodes expanded per search. It is the
  default because levels are not grids; `Monotone` stays for a tile being rebaked per frame.

  **The height detail pass is in.** Each polygon gets its own triangulation of the ground sampled
  back out of the heightfield, so the surface follows a hill instead of lidding it: mean height error
  on a 24 m hill goes from 0.76 m to 0.15 m and the worst from 1.41 m to 0.31 m, for a bake 56 %
  longer — and for nothing at all on flat ground, where the sampling adds no vertices. The greedy
  split alone was not enough and the measurement is what said so: splitting a triangle keeps all
  three of its edges, so a fan over a large polygon stays exact at its samples and a metre out between
  them. Lawson's flip after each insertion fixed it and halved the vertices needed. The constant
  one-cell-height offset is *not* fixed — that is the voxelisation, not the polygon — and a test
  asserts it so it stays a decision.

  **Dynamic obstacles are in**, as `NavTileCache`: the voxelised level kept resident so that dropping
  a crate rebuilds the tiles under it rather than the level. The cut is between the half of the bake
  that turns triangles into a surface and the half that decides the surface's shape, because an
  obstacle only changes the second. Carving happens *before* erosion and cost-stamping after, which is
  the difference between a shape claim and a cost claim. Measured on an eighty-metre level: 0.75 ms to
  rebuild a tile against 1.54 ms to bake one, four tiles dirtied by a crate, 2.2 MB resident — which
  the cache reports itself, because the memory is the whole cost of the design.

  **The searches run on jobs.** `NavPathQueue.Scheduler` puts each slice on `Vixen.Core.Threading`;
  null, the default, runs them inline. The queries were separate objects with separate node pools from
  the start and only read the mesh, so there is nothing to lock. **Both paths run the same rounds and
  give the same answers in the same updates** — a test runs two queues side by side for sixty-four
  updates and asserts request-for-request agreement — and scheduling a slice allocates nothing.
  Measured at under 1.8× on nine workers, and the reason is written down rather than hidden: a round
  is a barrier, so it costs its longest search. Free-running queries would recover the rest and would
  cost the property that makes a scheduler an implementation detail.

  **A navmesh bakes from a list of placed pieces**, not just one merged collision export: `geometry`
  in a `.vxnavmesh` takes a `source`, `position`, `rotation` and `scale` per entry, each declared as a
  dependency of its own. That is the half of "bake a scene" that does not need a scene. The other half
  does, and is genuinely blocked: there is no `[DataContract]` scene or prefab asset in the repo at
  all — `SceneManager` builds scenes procedurally, `Prefab` captures a live `World` with nothing to
  serialise, and `NativeFormatImporter` claims `.vxscene` only to scan it for dependencies and copy it
  through. Doc 08's `SceneCompiler` carries no "Built" marker. When it exists, this importer fills the
  same list of placements from it and nothing else changes.

  **The two endpoint lookups a retarget did not need are gone.** Planning used to begin by searching
  for the polygon the agent was standing on — which is its corridor's first polygon and has been kept
  current by every move — and for the polygon its destination is on, once per plan attempt rather than
  once per destination. The first is now read, the second is resolved when the destination is set, and
  `SetTarget(handle, poly, point)` lets a caller that already has the answer skip it altogether. Both
  remembered references are validated before use — the reference has to still resolve *and* the filter
  has to still accept it — so a rebuilt tile or a closed door falls back to a search. Writing it down
  found a real bug: `AddAgent` and `ClearTarget` set the target without its polygon, so a recycled
  agent slot inherited the previous occupant's destination.

  **A connection now reaches as far as it was authored to.** Relinking visited four neighbours, which
  is exactly how far a border edge reaches and nowhere near how far a zip line does — so a jump across
  four tiles attached at the near end and dangled at the far one. Three tiles have a stake in a long
  connection and all three are revisited on a load or unload; building a tile's links asks every tile
  that declares connections, because a tile cannot know which faraway one declared a jump into it.
  Three tests fail on the old code and pass on the new one, including the streaming order where the
  far end arrives last.

  **A surface is reported where it is, not where its voxel is.** A flat floor used to read one whole
  cell height above itself, because the height handed to an agent came from the same integer the step
  and ledge filters compare. A span now also records where inside that voxel the triangle was, in
  sixteenths, carried past every filter that wants a grid and first read by the contour tracer — the
  first stage that reports a height rather than comparing one. Flat floors are exact, a ramp keeps only
  the error its cell size implies, and the hill the detail pass is measured on is three times closer
  with its systematic bias gone. One number got worse and the README says why: a hill with detail off
  is three enormous planes sitting below the ground, and the old upward bias was accidentally
  cancelling part of that.

  **Owed:** reading placements from a compiled scene, once doc 08's scene compiler exists.
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
