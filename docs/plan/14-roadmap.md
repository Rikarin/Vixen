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
- 🟡 `Vixen.Platform.Native` — **the runtime half is built; acquisition is not.** RID chain computed
  rather than looked up (a NativeAOT binary has no `runtimeconfig.json` to read one from), the
  `runtimes/<rid>/native/` layout searched before the operating system is asked, the versioned soname
  tried as well as the development symlink, and a `DllImportResolver` that answers before the default
  rules. 12 tests, all of them pure functions from a name to a list of candidates — a rule about
  Windows that can only be checked on Windows is a rule that is checked once a release.

  **What it fixes and what it does not, verified rather than assumed.** A registered resolver means
  the binding library's probing is never reached at run time, which is the functional half of R11's
  desktop mitigation. It does **not** silence the six IL3000/IL3002 diagnostics: rooting
  `Vixen.Graphics.Vulkan` with this in place still reports six, because ILC's analysis is static and
  code unreachable *in practice* is still reachable *in the graph*. Suppressing them is a separate
  decision that only becomes defensible once this is in force, and is deliberately not taken in the
  same commit as the thing that would justify it.

  **Owed:** the acquisition half — pinned versions, checksummed URLs, SHA-256 verification, a licence
  manifest, restored by a Nuke target and never committed ([10](10-platforms.md) § Native binaries,
  R10) — which belongs in the `build/Build.Native.cs` that [02](02-repository-layout.md) already
  reserves. And nothing registers the resolver yet: `Vixen.Graphics.Vulkan` and
  `Vixen.Platform.Desktop` keep their own loading, which works because neither is published ahead of
  time today. Wiring them up belongs with the acquisition that puts the binaries where this looks.
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
- `Vixen.Editor.Assets`: `TextureImporter`, `ModelImporter` (Assimp), `AudioImporter`,
  `NativeFormatImporter`, `DefaultImporter`. Out-of-process worker (`Tools/Vixen.AssetCompiler`).
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
  packages" is not met; nothing generates C# yet, so the `CoreCompile` hook is ordering without cargo
  until Phases 4d and 5; platform packaging (APK assets, iOS bundle, `wwwroot`) waits for those
  platforms; and a build-plan diagnostic carries no file, because `ImportDiagnostic` has no path
  field — its messages name the asset in their text, so only the IDE's jump-to-file loses.
- 🟡 `Vixen.Cli` — **`import`, `content build`, `content serve` and `doctor` are built; `new`, `run`
  and `build` are not, and are absent rather than stubbed.** The first four are the whole pipeline
  from a terminal, which is what the phase's own gates need: an incremental import, a deterministic
  content build, and a laptop a phone can be pointed at. 19 tests, driving the real parser over a
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

  **Owed, with reasons:** `new` needs the `Vixen.Sdk` package layout to scaffold against; `build` and
  `run` wrap `dotnet publish`, which is [17](17-app-heads-and-shipping.md)'s story and needs the
  platform packaging that arrives with Android and iOS. `vixen doctor systems` from
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

  ⚠ **Adding `Vixen.Graphics.Vulkan` breaks it, in two different ways that were initially conflated —
  see R11, which is corrected.** On the desktop, Silk.NET's `DefaultPathResolver` cannot work under
  AOT, and the fix is `Vixen.Platform.Native`'s `DllImportResolver`. On iOS the resolver is beside the
  point: everything links statically, so `DllImport`s become symbol references and `clang++` fails
  with twelve undefined `vk*` symbols because **MoltenVK is not being linked in**. A resolver cannot
  help there — there is no resolution step to intercept. The first write-up of this named one cause
  and one fix; designing against it would have produced something that worked on a laptop and failed
  on the device, which is the exact failure this phase exists to prevent.

  **Owed:** each gate publishes for one RID, so covering three desktop operating systems means one CI
  leg each; Android is not gated yet, and should be gated on its *default* runtime rather than on
  NativeAOT, which `warning XA1040` calls experimental and not suitable for production — the plan only
  ever committed to NativeAOT for iOS.
- **`Vixen.Platform.Android`** + Vulkan/GLES on device; lifecycle, `AAssetManager`, touch input.
- **`Vixen.Platform.iOS`** + MoltenVK static; **NativeAOT publish in CI on every PR from here on**.
- `Samples/07-AddressablesRemote`.

**Exit:** `Samples/01` runs on a physical Android device and a physical iPhone. iOS NativeAOT publish
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
Android, iOS and the AOT publish are not started.

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
- ⚠ **MSDF has an unanswered question underneath it: HarfBuzzSharp exposes no glyph outlines.** The
  assembly has `TryGetGlyphExtents`, which is a bounding box, and no draw, paint or outline surface
  at all. Distance-field generation needs contours, so something else has to produce them.

  **Decided direction, to be spiked before the atlas is planned** (sequencing rule 3, as with ExCSS
  and HarfBuzz): a **managed `glyf`/`CFF` outline parser**, fed by `Face.ReferenceTable`, which
  HarfBuzzSharp *does* expose. The alternatives are FreeType — a second native dependency, and one
  whose WebAssembly story would have to be re-run from scratch — or SkiaSharp, which is heavy and
  duplicates HarfBuzz. The managed route adds no native dependency, keeps the WASM path exactly as
  the HarfBuzz spike left it, and reuses the binary-format parsing this repository already does for
  KTX2. What it costs is a real parser for two outline formats, which is why it is a spike and not
  an assumption.
- Owed: MSDF atlas with LRU eviction, font fallback, rich-text runs, variable-font axes,
  `TextEditor` model with IME and caret affinity.
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

  ⚠ **The tree is append-only**, because `StyleTree` is: elements are created parents-first and never
  removed. Enough to lay out a document and not enough to run an application. Owed with the rest.
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
- Owed in `Vixen.Ui`: access keys, line wrapping, rich-text runs,
  font fallback and weight matching, gradients, per-corner elliptical radii, pinch and rotate,
  virtualisation primitive, multi-window, DPI, and element removal.
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
