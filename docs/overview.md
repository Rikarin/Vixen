# Vixen — Implementation Overview

A single-page reconciliation of **what the plan asks for**, **what exists in the repository**, and
**what is holding the rest up**.

Sources: every file under [`docs/plan/`](plan/), [`docs/manual/`](manual/),
[`docs/rhi-backend-mapping.md`](rhi-backend-mapping.md), the per-project `README.md` files, the
`Directory.Packages.props` register, the Nuke targets in `build/`, and `.github/workflows/`.

> **Where the sources disagree, the code wins.** Every row below is marked against the code, and a
> divergence from a document is called out in the note. This file is the *only* place that carries
> per-feature status: [`14-roadmap.md`](plan/14-roadmap.md) keeps the phase boundaries and their exit
> criteria and points here, and the reasoning behind any one subsystem lives in that subsystem's
> `README.md`. Three places recording the same thing is how they come to disagree.

> **Detail lives in the issue tracker, not here.** Every open item is a GitHub issue on
> [Rikarin/Vixen](https://github.com/Rikarin/Vixen/issues) — Part 4 links each one by its stable
> number, and the wider backlog is filed there too. This page is the *shape* of the work: what
> exists, what does not, and what blocks what. The evidence, the history and the argument for any one
> item belong in its issue and in the owning module's `README.md`. Keep it that way; this file was
> once 383 KB of prose and nobody could see the state through it.

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
| Nuke `Clean Restore Compile Test Pack Benchmark` | ✅ | [build/Build.cs](../build/Build.cs) |  |
| Nuke `CheckFormat`, `CheckArchitecture` | ✅ | [build/Build.ArchitectureRules.cs](../build/Build.ArchitectureRules.cs) | Enforces ADR-002 (no IL rewriting), ADR-015 (no authoring-format importer in a runtime assembly) and the `Vixen.Ui` ⇸ `Vixen.Engine` boundary |
| Nuke `GoldenImages` | ✅ | build/ | 40 fixtures; generated on MoltenVK, verified on lavapipe. Also run under `Test`, so a wrong picture fails a normal build |
| Nuke `CheckAot` (desktop) | 🟡 | build/ | Publishes `Tools/Vixen.AotProbe` for the **host** RID only — ILC cross-compiles poorly, so three desktop OSes means one CI leg each, and `ci.yml`… |
| Nuke `CheckAotIos` | ✅ | build/ | `.ipa`, 7 MB native, zero managed assemblies, zero trim/AOT warnings |
| Nuke `CompileMobile`, `CompileWeb`, `PublishWeb`, `RestoreNativeDeps` | ✅ | build/ | ⚠ These projects are outside `Vixen.slnx` — a `net10.0-ios`/`-android`/`-browser` project cannot be *evaluated* without its workload — so `Test`… |
| Nuke `CheckApi` | ✅ | [build/Build.Api.cs](../build/Build.Api.cs), [Tools/Vixen.ApiCheck](../Tools/Vixen.ApiCheck/README.md) | 59 packable assemblies, both directions gated (an unapproved addition *and* a silent removal). `Shipped` is empty everywhere, because nothing has… |
| Nuke `Docs` | ✅ | [build/Build.Docs.cs](../build/Build.Docs.cs), [Tools/Vixen.DocGen](../Tools/Vixen.DocGen/README.md) | ⚠ Read in **Release**: the generators resolve through `ProjectReference` and any other configuration silently drops ~300 types |
| Nuke `CheckDocs` | ✅ | build/Build.Docs.cs | Coverage, the five-heading contract, link and snippet resolution, orphans, compiled examples, baseline agreement. Coverage is seeded by… |
| Nuke `CheckStrings` | ✅ | [build/Build.Strings.cs](../build/Build.Strings.cs) | Doc 46 § A3's *"an id used nowhere … is a build error"*, which no compilation can answer: six of `ControlStrings`' fifteen declarations are used only… |
| Nuke `Release` | ✅ | [build/Build.Release.cs](../build/Build.Release.cs) | Folds `PublicAPI.Unshipped.txt` into `Shipped`, archives the graph under `docs/api-history/<version>/`, and emits the… |
| `ci.yml` — 3 desktop runners, test + checks + web + pack | ✅ | [.github/workflows/ci.yml](../.github/workflows/ci.yml) | Three `test` legs (ubuntu, windows, macos-14 — two architectures), a `checks` leg running `CheckArchitecture CheckApi CheckFormat CheckDocs… |
| `docs.yml` — graph, site, image | ✅ | [.github/workflows/docs.yml](../.github/workflows/docs.yml) | Publishes vixenengine.org from `master` as `ghcr.io/rikarin/vixen-docs`; a PR gets a `pr-<n>` tag. No budget gate — the file count it enforced was a… |
| `nightly.yml` — long-running fuzz | ✅ | [.github/workflows/nightly.yml](../.github/workflows/nightly.yml) | A `matrix` job per target over all 20, `fail-fast: false`, each with its own budget, timeout and findings artifact from… |
| lavapipe Vulkan CI leg | ✅ | ci.yml | 155 Vulkan tests, zero skipped, validation-clean. ⚠ `VIXEN_REQUIRE_VULKAN` makes a *skip* a failure here: a runner that lost its ICD would otherwise… |
| NativeAOT publish leg on every PR | ⬜ | — | Gate exists (`CheckAot`), leg does not — `ci.yml` names neither AOT target |
| CI leg that *runs* a sample (`--frames N`) | 🟡 | [build/Build.SampleFrame.cs](../build/Build.SampleFrame.cs), ci.yml `sample-frame` | `nuke SampleFrame` boots `Samples/03-PbrShowcase` with no display for 64 frames, captures the last one and asserts five things that can each fail:… |
| Browser smoke leg (CDP, no Playwright) | ✅ | [build/Build.BrowserSmoke.cs](../build/Build.BrowserSmoke.cs), [Tools/Vixen.WebProbe](../Tools/Vixen.WebProbe/README.md), ci.yml `web` | **The `[JSImport]` calls themselves are covered now.** `nuke BrowserSmoke` publishes the head, serves it on an ephemeral loopback port with… |
| Content-determinism across 3 real runners | ✅ | [build/Build.ContentBytes.cs](../build/Build.ContentBytes.cs), [Testing/ContentDeterminism](../Testing/ContentDeterminism/README.md), ci.yml `content-bytes` | Wired, watched, and now **failing rather than reporting**. Each `test` leg runs `nuke ContentBytes` — one committed fixture, built through the CLI… |
| 10 k-asset import budget as a *gate* | ✅ | [Vixen.Editor.Assets.Tests/ImportBudgetTests.cs](../Editor/Vixen.Editor.Assets.Tests/ImportBudgetTests.cs) | Both halves gated now, and **neither by a clock**. ⚠ **A benchmark project was the wrong answer and was not written**: `nuke Benchmark` is… |
| `references/` submodules | ✂️ | [references/README.md](../references/README.md) | Deliberately not submodules, against doc 02: a submodule makes every clone pull gigabytes of other people's history, including on runners that never… |

## 1.2 Core foundation

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Core` — identity, `GameTime`, `ServiceRegistry`, pooling, `DisposeBag`, `LeakTracker` | ✅ | Core/Vixen.Core | 86 tests |
| `Vixen.Core.Mathematics` — full ADR-003 type set + `Matrix3x3`, `ColorSpace`, `Oklab` | ✅ | Core/Vixen.Core.Mathematics | 126 tests + CsCheck properties. `Half` omitted (BCL has it) |
| `Vixen.Core.Collections` | ✅ | Core/Vixen.Core.Collections | `RobinHoodDictionary` and `FixedBitSet<N>` deferred with reasons |
| `Vixen.Core.Memory` — `NativeArray`, arena, buddy allocator | ✅ | Core/Vixen.Core.Memory | `GpuUploadRing` still owed — see the [module README](../Core/Vixen.Core.Memory/README.md) and [plan/03](plan/03-core-foundation.md) |
| `Vixen.Core.Threading` — Chase–Lev deques, `JobHandle` DAG, `ScheduleParallel` | ✅ | Core/Vixen.Core.Threading | 70 tests |
| `VIXEN_JOB_SAFETY` access declarations | ⬜ | — | The flag exists and `JobScheduler` compiles checks under it, but only the ones needing no declarations. The declarations themselves need the ECS to… |
| Thread pinning / affinity | ✅ | Platform/Vixen.Platform.{Windows,Linux} | The platform half. `SetThreadGroupAffinity` and `sched_setaffinity`, with performance/efficiency core classes; macOS reports `SupportsAffinity =… |
| Job priorities / long-running tier | ✅ | Core/Vixen.Core.Threading · Core/Vixen.Rendering | `JobPriority.Frame` (default) and `JobPriority.Background`: a second deque per worker plus a second shared queue, every frame source drained before… |
| `Vixen.Core.IO` — `VirtualPath`, mount table, providers, mmap, coalesced watch | ✅ | Core/Vixen.Core.IO | 123 tests. Android `AAssetManager` and Web IndexedDB/fetch providers landed with their platforms |
| `System.IO.Path` analyzer | ✅ | Core/Vixen.Core.IO.Analyzers | `VXIO0001`, 12 tests. Referenced by every `Core/` project from `Directory.Build.props`; off by name in the seven host-filesystem places. The… |
| `Vixen.Core.Serialization` + generator + `ObjectDatabase` | ✅ | Core/Vixen.Core.Serialization | 53 tests; LZ4/Zstd chunks, CRC-checked bundles |
| `Vixen.Core.Reflection` generator | ✅ | Core/Vixen.Core.Reflection | Generic types still unsupported |
| `Vixen.Core.Syntax` + generator (green/red trees) | ✅ | Core/Vixen.Core.Syntax | Shared by Raven, VXML — the Phase 0 extraction, and it paid off |
| `Vixen.Core.Yaml` — Vixen dialect, `.meta` model, migrations, `vx:` refs | ✅ | Core/Vixen.Core.Yaml | 73 tests incl. byte-identical round trip |
| `Vixen.Core.Imaging` — KTX2, mips, BC1/3/4/5/7/6H, IBL split-sum | ✅ | Core/Vixen.Core.Imaging | See gaps below |
| ASTC / ETC2 encoders | ⛔ | — | The formats are named in `VkFormats`/`DataFormatDescriptor`; no encoder for either, no managed one exists, and none is planned. Needs `astcenc`… |
| BC7 / BC6H multi-mode | 🟡 | Core/Vixen.Core.Imaging | One (single-subset) mode each — valid output, real quality ceiling. `ispc_texcomp` registered in doc 01 for the rest |
| KTX2/BCn verified against an independent implementation | 🟡 | Core/Vixen.Core.Imaging.Tests · Tools/Vixen.BcnOracle | **Done, and it found nine defects.** `Ktx2ConformanceTests` puts every format and container shape past Khronos's `ktx validate --warnings-as-errors`… |
| `Vixen.Core.Diagnostics` — `[LoggerMessage]`, ring sink, profiler, Chrome trace | 🟡 | Core/Vixen.Core.Diagnostics | 36 tests; event ids registered in [log-events.md](manual/log-events.md). What is owed is the UTF-8 ring, the remote protocol and Perfetto protobuf… |
| Other log sinks (ZLogger file, console, `logcat`/`OSLog`, remote, `EventSource`) | ✅ | Core/Vixen.Core.Diagnostics | All five, over a shared `LogFilter`. The remote one takes an `IRemoteLogTransport`, since the inspector protocol is not written. ⚠ Apple gets… |
| Log rate limiting | ✅ | Core/Vixen.Core.Diagnostics | `LogRateLimiter`: a burst per window per (category, event id), the count carried on the line that follows. `Critical` and a novel event are never… |
| UTF-8 record packing in the ring | ⬜ **measured, and declined** | Core/Vixen.Core.Diagnostics.Tests | The profile this was deferred for has been taken, and it says not to do it. `AllocationTests` measures an enabled line at **128 B** (an 88-byte `LogRecord` plus the 40-byte message) and a disabled line at **exactly zero**, using `GC.GetAllocatedBytesForCurrentThread` rather than a clock. ⚠ **The floor is `ILogger`'s, not the ring's**: reading the `[LoggerMessage]` state's structured fields boxes the state and every value-type argument (**56 B/line** measured for one `int`) and the formatter contract returns a `string` (**40 B/line**), so packing bytes *moves* the allocation rather than removing it — doc 13's "the sink writes UTF-8 directly" needs ZLogger's shape, which is ADR-008's call. It would also spend three properties held for free: one reference per slot means a wrap loses a whole record and never half of one (a byte ring's fragment cut inside a multi-byte sequence is a *decode error*, not a truncation), `Exception` is a reference that cannot be packed without formatting it at write time, and the editor console — written since the deferral — collapses on `(Level, Category, Message)` and searches the message as text. ⚠ **A false claim was removed while doing this**: the deferral rested on "`[HotPath]` methods are barred from logging", and `[HotPath]` is applied to **no method in the tree** with no analyzer behind it. Logging does occur per-frame; what makes it affordable is that each such site is latched, watermarked, de-duplicated or throttled. Owed, as call-site bugs: `WebGpuDevice.WaitIdle` logs unlatched on a permanent condition, and `EditorFrames` builds a `string.Join` in front of its change check |
| GPU profiling / memory attribution / Perfetto protobuf | 🟡 | Core/Vixen.Graphics · Core/Vixen.Engine.Renderer | **GPU profiling landed and is reached**: `GpuProfiler` sits in `Vixen.Graphics` over `CreateQueryPool`/`WriteTimestamp`/`TryResolveQueries`… |

## 1.3 ECS, engine loop, scenes

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Ecs` — archetypes, chunks, edge graph, queries + generator, `CommandBuffer`, change versions | ✅ | Core/Vixen.Ecs | 90 tests |
| System scheduler — 9 phases, conflict graph, DAG on jobs, DOT/Mermaid dumps | ✅ | Core/Vixen.Ecs |  |
| Read/write **inference** from query bodies | ⬜ | — | Attributes and programmatic declaration exist; the generator does not |
| World serialisation | ✅ | Core/Vixen.Engine | `WorldSerializer` + `WorldContent`, in `Vixen.Engine` because the ECS references no serializer by design and the binders are… |
| `VIXEN_ECS_EVENTS` hooks | ⬜ | — | Named in [plan/04](plan/04-ecs-and-scripting.md); no flag and no hooks in the tree |
| Entity handle **reservation** (`World.TryRecreate`) | ✅ | Core/Vixen.Ecs | Allowed only when the slot's version is *exactly* one past the requested one — anything else would let one handle name two entities across its life |
| `Hierarchy.SetParentAfter` / `PreviousSiblingOf` | ✅ | Core/Vixen.Engine | Linking prepends, so undo needs a neighbour rather than an index — an index is invalidated by every insertion in front of it |
| Transform hierarchy with dirty propagation | 🟡 | Core/Vixen.Engine | Not depth-split — needs shared components. One visit per moved entity either way |
| `Vixen.Engine` — loop, fixed-step accumulator, `Behavior`, scenes, `SceneTag`, additive load | ✅ | Core/Vixen.Engine | 58 tests |
| Prefabs (capture + instantiate) | ✅ | Core/Vixen.Engine | A prefab is a **world of its own**, so instantiating is one `CreateMany` per archetype and a row copy each |
| Prefab **overrides** and nested prefabs | 🟡 | Editor/Vixen.Editor.Core · Editor/Vixen.Editor.SceneView · Editor/Vixen.Editor.AssetEditors | Risk R7, **format and wiring both built** — [plan/47](plan/47-prefab-overrides-and-nested-prefabs.md). ⚠ The old wording here ("nothing named… |
| Coroutines (`async Coroutine`, zero-alloc start) | ✅ | Core/Vixen.Engine | `WhenAny` owed; stopping a single launched coroutine refused by design |
| **Virtual cameras** (shots, body/aim stages, director, blending, noise, impulse) | ✅ | Core/Vixen.Engine | [plan/26](plan/26-virtual-cameras.md), and [Vixen.Engine/README](../Core/Vixen.Engine/README.md) § Virtual cameras. A Cinemachine-shaped system:… |
| Virtual cameras — dolly track, target groups, recentring | ⬜ | — | [plan/26](plan/26-virtual-cameras.md) § What is deliberately not built. The track wants a spline *asset* — authored, serialised, editable — and… |
| `DebugDraw` accumulator | ✅ | Core/Vixen.Engine | Lines, rays, arrows, boxes (AABB and oriented), spheres, circles, capsules, cones, frustums, crosses, axes, world labels, screen-space… |
| `DebugDraw` **drawing** | ✅ | Core/Vixen.Engine.Renderer | Two line draws — world (billboarded labels included) and screen. Golden-image verified |
| Doc 13 overlays — frame stats, frame graph, log, console, GPU, audio, water | ✅ | Core/Vixen.Engine · Core/Vixen.Engine.Renderer · Core/Vixen.App.Hosting | `IDiagnosticOverlay`, corner-stacked panels drawn out of `DebugDraw`'s screen list, `[ConsoleCommand]` verbs. **A game reaches them**:… |
| Doc 13 overlays — render mode, UI debug, streaming | ⬜ | — | Render mode needs shader debug views in the compositor; the other two need `Vixen.Ui` and `Vixen.Assets` to report, and neither may reference… |
| ImGui debug overlay | ✂️ | — | Cut in Phase 2 rather than built, and Phase 6's "delete it" step struck with it |

## 1.4 Graphics RHI and backends

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Graphics` RHI surface — formats, `synchronization2`-shaped barriers, typed handles, PSO/descriptor descriptions | ✅ | Core/Vixen.Graphics | 46 tests; reversed depth in the defaults |
| `DescriptorBinding` sample type / comparison sampler | ✅ | Core/Vixen.Graphics | `DescriptorSampleType` on the binding and `PixelFormats.SampleTypeOf` beside it. WebGPU declares and enforces it, Vulkan checks it when it is stated… |
| **Bindless descriptor arrays** — `BindlessTable`, `MaxBindlessDescriptors`, descriptor indexing in the Vulkan backend | ✅ | Core/Vixen.Graphics · Platform/Vixen.Graphics.Vulkan | Reached from `Vixen.Engine.Renderer`'s `WorldRenderer` and `Vixen.Rendering`'s `EffectSetWriter`, not only from tests. `HasBindless` is four opt-in… |
| Placed resources (true memory aliasing) | ⬜ | — | Two of six planned backends cannot express it |
| `Vixen.Graphics.Null` + recording harness | ✅ | Platform/Vixen.Graphics.Null | Also the shipping dedicated-server backend |
| `Vixen.Graphics.RenderGraph` — culling, aliasing, batched barriers, derived store actions | ✅ | Core/Vixen.Graphics.RenderGraph | 90 tests incl. property tests (the row said 34; measured 2026-08-21) |
| Async-compute queue scheduling | 🟡 | Core/Vixen.Graphics.RenderGraph · Core/Vixen.Graphics · Platform/Vixen.Graphics.Vulkan | **`PassKind` is read now.** `RenderGraph.Scheduling` cuts the frame into `RenderGraphSegment`s — one per run of passes on a queue, in declaration order, never reordered — and `Execute(IRenderGraphQueues)` records a list each. Queue ownership is in the RHI: `BufferBarrier`/`TextureBarrier` carry `SourceQueue`/`DestinationQueue`, the graph plans both halves of every handover from one walk, and Vulkan and Null refuse a list at neither end. Two kinds landing on one family collapse to `VK_QUEUE_FAMILY_IGNORED`, so a one-queue device records what it always did — verified on an M1 Max, where `HasAsyncCompute` is false. **A wait edge is now enforced by value where the device can**: `TimelinePoint` + `ICommandSubmitter.Submit(lists, waitFor)` is the RHI primitive, `DeviceQueues` is what a frame should be handed, and `SerialisedQueues` is what it becomes without timeline semaphores. ⚠ **`Single` by default, and it still buys no frame time — but the reason is now hardware, not a missing primitive**: `Async` hoists a pass only where `HasAsyncCompute` is true, which means a queue *family* of its own, and neither MoltenVK on an M1 Max nor lavapipe has one. Nothing in reach can measure overlap; the two-queue frame is executed against `NullDevice`, the only backend here reporting three distinct queues. **The audit is done** ([table](guide/rendering/async-compute.md)): all nineteen non-graphics passes were checked, and the eleven whose products the graph cannot see now declare `PassKind.Graphics` and say why at the declaration — `PassKind` is a claim about scheduling, not a description of the body. Backing it, `BuildSegments` hoists **only a compute pass that declares a write**, because every wait edge comes from one: under-declaration now fails towards the frame the engine already draws. **Two passes can honestly leave the graphics queue** — the generic `ComputeRenderer` and the water ripple step — and neither is worth overlapping, so turning `Async` on is safe and buys nothing yet. Two more gaps closed: a barrier's stage and access masks are now **clamped to the recording queue's family** (`ShaderRead` means vertex|fragment|compute, and a compute-only family has one of the three — every hoisted compute pass reading a texture produced that barrier), and the release and acquire halves are recorded asymmetrically rather than as one barrier twice. And **two queues that only read one resource no longer take turns**: the graph creates such a transient `ResourceSharing.Concurrent`, which has no owner to hand over. ⚠ Async frames still do not alias transients, and that is now a decision with a reason written down — the condition that would make it safe holds only between segments already forced to take turns. [guide](guide/rendering/async-compute.md) |
| `Vixen.Graphics.Vulkan` — whole device + command list | ✅ | Platform/Vixen.Graphics.Vulkan | 204 tests, validation-clean on MoltenVK and lavapipe (the row said 155; measured 2026-08-21 on an M1 Max, no skips) |
| Vulkan swapchain acquire/present automated coverage | ⬜ | — | Needs a window; AppKit aborts off the main thread. `Samples/01` exercises it |
| Timeline semaphores, MSAA resolve, query pools | 🟡 | Core/Vixen.Graphics | **Query pools are built and reached** — `CreateQueryPool`/`WriteTimestamp`/`TryResolveQueries` on Vulkan, under `GpuProfiler`; GL throws for them with a stated reason. **MSAA resolve is reached**: `ColorAttachment.ResolveView` + `StoreAction.Resolve` were honoured by the Vulkan and WebGPU backends and no renderer set either — `RenderGraphPassBuilder.ColourAttachment`'s resolve argument and `RenderPass.resolveTargets` are the pair that was missing, and `MsaaResolveImageTests` is the first frame here with any samples in it. ⚠ ~~Colour only; the depth resolve wants a shader~~ — the depth resolve shipped without one (row 327), and what it actually wants is a device that advertises the mode. `GraphicsDeviceFeatures.SupportedDepthResolveModes` is that question and `ClampDepthResolveMode` is the fallback; the Vulkan backend clamps rather than submits, because a mode outside `supportedDepthResolveModes` is invalid usage under VUID-VkRenderingInfo-pDepthAttachment-06102 and not a slow path. **Timeline semaphores are consumed** — `TimelinePoint` and `ICommandSubmitter.Submit(lists, waitFor)` are the RHI shape: one counter per *queue* (never per device — two queues signalling one counter finish in an order nobody controls, and a timeline signalled backwards is invalid usage), a submission signals the next value and hands the point back, and a dependent submission waits for it on the device rather than draining the producer. Vulkan implements it; `NullDevice` implements it and refuses a point that was never issued, which on hardware is a hang with no message; GL and WebGPU report `HasTimeline` false and the render graph drains instead. ⚠ **Two defects found doing it, both invisible on every device here**: `HasTimelineSemaphores` was `apiVersion >= 1.2 || extension`, which claims the *structure exists* rather than that the bit is granted — the `HasBindless`/`HasRayTracing` conjunction is now used — and the `timelineSemaphore` feature was **never enabled at device creation**, so the capability was a promise nothing had been asked to keep. MoltenVK creates the semaphore anyway and the layers say nothing, which is why it survived |
| `Vixen.Graphics.OpenGL` — GL 4.5 core / GLES 3.0-3.2 / WebGL2 translation | ✅ | Platform/Vixen.Graphics.OpenGL | 131 tests against a recording `IGlApi`. Reached by `Tools/Vixen.App`, which opens a `GlDevice` when `GraphicsBackend.OpenGl` is in the preference… |
| `Silk.NET.OpenGLES` + EGL context | 🟡 | Platform/Vixen.Graphics.OpenGL | `SilkGlesApi` + `EglContext` + `NativeEglApi` are written and tested against a recording `IEglApi` — 19 entry points loaded from `libEGL` through… |
| `glBindImageTexture` (storage images) | ⬜ | — | `GlDevice.Replay` throws for storage images by name. Every compute path has a fullscreen-fragment variant meanwhile |
| `Vixen.Graphics.WebGPU` (native, Dawn/wgpu) | ✅ | Platform/Vixen.Graphics.WebGPU | Renders against pinned wgpu-native; push constants emulated as a dynamic UBO. `VIXEN_REQUIRE_WEBGPU` makes a skip a failure on the macOS leg |
| `Vixen.Graphics.WebGPU.Browser` (`navigator.gpu`) | ✅ | Platform/Vixen.Graphics.WebGPU.Browser | Tested against a recording fake with no browser |
| WebGPU timestamp queries; Linux CI leg | ⬜ | — | wgpu-native on the lavapipe `ci.yml` already installs would be a second implementation, and is where the interesting bugs are. The leg is written… |
| `Vixen.Graphics.Direct3D12` | ✂️ | — | Postponed past 1.0 (ADR-001 / Q4). **The stub project ADR-001 reserves does not exist either.** GL is the abstraction validator |
| `docs/rhi-backend-mapping.md` | ✅ | [rhi-backend-mapping.md](rhi-backend-mapping.md) |  |

## 1.5 Platform

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Platform` contracts (window, surface, display, files, clipboard, dialogs, lifecycle, input, IME, power, topology) | ✅ | Platform/Vixen.Platform | 26 tests |
| `Vixen.Platform.Headless` | ✅ | Platform/Vixen.Platform.Headless | 31 tests; drives the dedicated server |
| `Vixen.Platform.Desktop` (SDL **2** via Silk.NET) | ✅ | Platform/Vixen.Platform.Desktop | 58 tests. Doc 01 said SDL 3 and was wrong |
| File pickers, clipboard images/custom formats, thread affinity, thermal state | ✅ | Platform/Vixen.Platform.{Windows,Linux,MacOS} | SDL 2 has none of them; they arrive through `IPlatformSupplement`, chosen by operating system in `DesktopSupplements` |
| `Vixen.Platform.Windows` / `.Linux` / `.MacOS` | ✅ | Platform/Vixen.Platform.{Windows,Linux,MacOS} | 67 tests. `IFileDialog` · `zenity`/`kdialog` · `NSOpenPanel`; `CF_DIBV5` · `image/png` · `NSPasteboard`; affinity on two of the three; thermal on two… |
| `Vixen.Platform.Native` — RID chain, `runtimes/` search, `DllImportResolver`, `RestoreNativeDeps` | ✅ | Platform/Vixen.Platform.Native | Retired R11's desktop half with **no suppression** |
| `Vixen.Platform.Ui` — the platform ⇄ document join | ✅ | Platform/Vixen.Platform.Ui | `PlatformInput` (one copy, where `Samples/02` and `Vixen.Editor.App` each had one) and `PlatformWindowHost`, which fills `IUiWindowHost` over… |
| Native-dependency acquisition beyond MoltenVK | 🟡 | [build/native-dependencies.json](../build/native-dependencies.json) | Two entries — MoltenVK (`ios-arm64`, static, link-time only) and wgpu-native. R10 lists five more |
| `Vixen.Platform.iOS` (UIKit, `CAMetalLayer`, `CADisplayLink`, multi-touch, IME) | 🟡 | Platform/Vixen.Platform.iOS | Runs in the **Simulator**. Physical device ⛔ on a provisioning profile (an Apple account, not a build setting) |
| iOS sensors, haptics, Metal-layer HDR, `UIWindowSceneDelegate` | ⬜ | — | None of the four appears in the assembly. File dialogs, clipboard images, gamepads and hardware keyboard refused with reasons |
| `Vixen.Platform.Android` (SurfaceView, lifecycle, Choreographer, `AAssetManager`) | 🟡 | Platform/Vixen.Platform.Android | Runs on the **emulator** (`-gpu swiftshader_indirect` required). No physical device attached |
| Android GLES fallback + device-capability deny-list | ⬜ | — | The binding and the EGL context are written, and nothing reaches them (§1.4). What is left is the head choosing GL over Vulkan and the deny-list that… |
| Android key translation, safe-area insets, sensors | ⬜ | — | The insets gap is recorded in `AndroidServices`: `WindowInsets` is the right source and needs a window it does not have |
| Android AOT gate (on its *default* runtime, not NativeAOT) | ⬜ | — | `XA1040` calls NativeAOT experimental on Android |
| `Vixen.Platform.Web` — canvas, all input, IndexedDB, fetch + ranges, single-thread job mode, lazy assemblies | ✅ | Platform/Vixen.Platform.Web | Not in `Vixen.slnx` (needs `wasm-tools` to evaluate) |
| Web: native dialogs, display enumeration, window position, thermal state, clipboard images | ⛔ | — | Absent by platform, not by omission — each documented with why |
| Browser transport for `Vixen.Net` | ⬜ | — | A browser cannot open a UDP socket; the existing `Vixen.Net.Transport.WebSocket` is a server/desktop implementation |
| `AudioWorklet` path (cross-origin-isolated pages) | ⬜ | — | `vixen-audio.js` is a `ScriptProcessorNode` and says so. Would cut WebAudio's 40 ms queue to ~2 ms; needs the page to be cross-origin isolated and… |

## 1.6 Asset pipeline

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| Asset database — GUID index, reverse refs, duplicate repair, orphan quarantine | ✅ | Editor/Vixen.Editor.Core | 26 tests; 10 000 assets inside budget. Sidecar envelopes only, read in parallel |
| `Vixen.Assets` catalog, `AssetHandle`, ref-counted scopes, label/glob loading | ✅ | Core/Vixen.Assets | 48 + 64 tests |
| Content references (`ContentReference<T>`) | ✅ | Core/Vixen.Core.Serialization |  |
| Streamed content — `assets.Open(address)` over `ObjectDatabase.ReadRaw` | ✅ | Core/Vixen.Assets | Claims and caches nothing, so two callers get two independent streams — which is what a video whose picture and sound both seek needs. ⚠ Build such… |
| `ProjectWorkspace` + `ContentPipeline` (scan → import → plan → pack → write) | ✅ | Editor/Vixen.Editor.Assets | Moved out of `Vixen.Cli` so the editor and `vixen content build` cannot drift; the CLI keeps the console formatting and the worker pool |
| Content build — `.vxgroup`, `ContentBuilder`, content-hash bundle names, deterministic | ✅ | Editor/Vixen.Editor.Assets | 77 tests |
| `BuildPlanner` + sub-asset addressing (`characters/hero#Hero_Mesh`) | ✅ | Editor/Vixen.Editor.Assets |  |
| Remote content — HTTP + ranges, `BundleCache`, resume, CRC | ✅ | Core/Vixen.Assets | 31 tests over a hostile transport |
| Unpacked content — a catalog over `Library/`, nothing packed | ✅ | Editor/Vixen.Editor.Assets, Tools/Vixen.App | Doc 17's **Editor variant**, the last of its five. `vixen content loose` writes a catalog whose entries name no bundle beside the artefact store the… |
| Remote content **reached from the boot path** | ✅ | Tools/Vixen.App | The host builds a `RoutedBundleSource` over a `BundleCache` under `/cache` — but only when the catalog actually names a URL, so a game that ships… |
| Content updates (hash file → catalog overlay, never throws) | ✅ | Core/Vixen.Assets |  |
| `Tools/Vixen.ContentServer` | ✅ | Tools/Vixen.ContentServer | 34 tests, no socket; path traversal asserted 7 ways |
| Importers: Texture, Model (Assimp), Audio, NativeFormat, Raw/Default, NavMesh | ✅ | Editor/Vixen.Editor.Assets |  |
| Out-of-process import worker (`Tools/Vixen.AssetCompiler`) | ✅ | Tools/Vixen.AssetCompiler | Crash isolation, not speed |
| **Parallel** import (N workers) | ✅ | Editor/Vixen.Editor.Assets | Both halves now: deciding was already `Parallel.For`, and `ImportAllAsync` dispatches `MaxConcurrency` imports at once — cores − 1 by default, so… |
| `AssetDatabase` per-entry persisted index | ✅ | Editor/Vixen.Editor.Core | `Library/GuidIndex` is written and read — still tab-separated text, deliberately, so a person can read it at four in the morning — and… |
| `.vxscene` **authoring** format (YAML, `SceneFormat`/`SceneSerializer`/`SceneFileWriter`) | ✅ | Editor/Vixen.Editor.SceneView | Entities named by `EntityId` GUID, not by handle |
| `SceneCompiler` — `.vxscene` → runtime chunk, and a runtime scene asset | ✅ | Editor/Vixen.Editor.Assets, Core/Vixen.Engine | **K1, built** — see §3.1. `SceneImporter` claims `.vxscene`/`.vxprefab` and writes a `SceneAsset`/`PrefabAsset` chunk; `SceneComponentRegistry` turns… |
| Scenes-in-build → a player that opens one | ✅ | Editor/Vixen.Editor.Assets, Tools/Vixen.App | The content build resolves `PlayerBuildSettings.Scenes` to addresses and writes a `SceneManifest` beside the catalog; `AppConfig.StartupScene`… |
| Path-derived default addresses + `excluded:` | ✅ | Editor/Vixen.Editor.Assets | An asset with no `address` ships as `Assets/Textures/Crate.png`, reversing doc 08's "no `addressable` block means not shipped". An explicit address… |
| Duplicate sub-asset names survive import | ✅ | Editor/Vixen.Editor.Assets | The second of two meshes called `Cube` in a `.glb` is `Cube_1` with a warning, rather than the whole asset failing with advice nothing in the editor… |
| A typed load out of a real content build | ✅ | Core/Vixen.Assets, Editor/Vixen.Editor.Assets | Pinned by the first test to put a whole build into an `AssetManager` — which is what found two defects that made every shipped `Load<T>` fail and… |
| `.vxnetrules` asset (importer + serialised form + per-prefab reference) | ⬜ | — | `NetworkRulesRegistry` is what it loads into; only prose in [plan/16](plan/16-networking.md) and the registry's own doc comment exist |
| Colour-grading `.cube` LUT importer | ✅ | Editor/Vixen.Editor.Assets | `CubeLutImporter` reads what Resolve, Baselight, Nuke and Photoshop export into an `Rgba16Float` volume, and `Tonemap.rvn` samples it with the… |
| **The unregistered-importer gate** | ✅ | Editor/Vixen.Editor.Assets.Tests | Task #168, and the reason #167 was one of **six**: `[Importer]` is a declaration nothing scans for and `BuiltInImporters.Create` is a hand-written… |
| Server content profile | ✅ | Editor/Vixen.Editor.Assets | `vixen content build --variant Server`, passed by `Vixen.Sdk` from `VixenVariant`. **A group membership question, per doc 17 and doc 27 § the realm**… |
| `Vixen.Sdk` MSBuild integration | ✅ | Tools/Vixen.Sdk | 7 tests, each a real `dotnet build` |
| SDK ships the `vixen` CLI in the package | ⬜ | — | Consumer still needs the tool restored or installed |
| Platform packaging (APK assets, iOS bundle, `wwwroot`) | ⬜ | — | Waits for those platforms |
| `Vixen.Cli` — `import`, `content build`, `content serve`, `doctor`, `doctor systems`, `new`, `build`, `run` | ✅ | Tools/Vixen.Cli | 47 tests incl. a byte-for-byte determinism gate |
| Signing, notarisation, DMG/IPA/AAB | ⬜ | — | `Build.Publish.cs` is the first half and says so; `Sign` and `Notarize` do not exist. Doc 17's table is still Nuke's |
| `Tools/Vixen.Templates` — `vixen-game` / `vixen-app` / `vixen-lib` / `vixen-mmo` / `vixen-plugin` | ✅ | Tools/Vixen.Templates | 48 tests; one file tree, packed for `dotnet new` and embedded in `vixen new`. Each template's C# is compiled by Roslyn against the assemblies its… |
| `vixen-plugin` template | ✅ | Tools/Vixen.Templates/templates/vixen-plugin | `plugin.yaml` + a class library + one `IEditorPlugin` that adds a command, a menu entry and a panel **through `PluginContext`**, which a test asserts… |
| `vixen-tool` template; per-platform heads in `vixen-game` | ⬜ | — | Unblocked — `Vixen.Platform.Headless` is built |
| `vixen doctor systems` | ✅ | Tools/Vixen.Cli · Core/Vixen.Ecs | Reads a built game assembly's frame: the resolved run order by phase, the ordering attributes that turn out to do nothing, what each system's… |
## 1.7 UI framework

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Ui.Reactive` — signals, computeds, effects, collections, async | ✅ | Core/Vixen.Ui.Reactive | 63 tests; diamond evaluated once, zero-alloc steady state |
| `EffectScheduler.Flush()` driven by the frame pass | ✅ | Core/Vixen.Ui | `UiDocument.Update` drains `Effects` once at the top of a pass, under the guard that refuses a nested one. ⚠ Draining is the *document's* job, not… |
| `Vixen.Ui.Layout` — SoA store + **four algorithms**: flexbox, block, grid, inline | ✅ | Core/Vixen.Ui.Layout | Flexbox and block complete; grid and inline partial and each says which part — see the README's *State* section and… |
| Yoga conformance suite (534 fixtures) | ✅ | Core/Vixen.Ui.Layout.Tests | 9 skipped (`display: contents`, out of scope) |
| Taffy block corpus (884 + 28 `blockflex`) | ✅ | Core/Vixen.Ui.Layout.Tests | 840 pass, 72 refused, **zero** known gaps. ⚠ 48 of the refusals test that `overflow` blocks a collapse and are refused for `scrollbar-width`: the… |
| Layout gates — zero-alloc settled tree, 11 ns unchanged pass, 1.16 ms at 10⁴ | ✅ | Benchmarks/Vixen.Benchmarks.Ui |  |
| **CSS Grid** | 🟡 | Core/Vixen.Ui.Layout | Doc 43 § B2. Placement (§8), most of track sizing (§12) and §11.8's baseline alignment are done — `fr`/`minmax`/`fit-content`/`repeat`, a second… |
| `Vixen.Ui.Styling` — selectors, cascade, `@layer`, `@media`, inheritance, `var()`, sharing | ✅ | Core/Vixen.Ui.Styling | Oracle gates green, verified by sabotage. **`@media` is per surface**: each `UiSurface` carries its own size, scale, gamut and colour scheme, a… |
| Style **invalidation** (`StyleUpdater`, `StyleInvalidator`) | ✅ | Core/Vixen.Ui.Styling | Wired into `UiDocument.Update`. One class toggle: 9.50 ms / 8.87 MB → **0.94 ms / 552 B**, and the allocation no longer scales with the document |
| Transitions, `@keyframes`, `cubic-bezier`/`steps`/`spring()`, Oklab | ✅ | Core/Vixen.Ui.Styling · Core/Vixen.Ui | Springs solved in closed form. On `StyleEngine.Animations`: `StyleUpdater` announces replaced styles, `UiDocument.Tick` advances, `UiDocument.Apply`… |
| Colour syntax — hex, named, `rgb()`, `oklab()`/`oklch()`, `color-mix()` | ✅ | Core/Vixen.Ui.Styling | Doc 43 A9/A10. Four interpolation spaces, premultiplied alpha, CSS Values 5 percentage normalisation. ⚠ Out of gamut is carried **unclamped** through… |
| Transform decomposition | ⬜ | — | Waits on a transform property existing; nothing in `Vixen.Ui` parses or carries one |
| `Vixen.Ui.Styling.Utilities` — tokens, scanner, variants, arbitrary values, `@apply` | ✅ | Core/Vixen.Ui.Styling.Utilities | 107 tests |
| Text: UAX#29 segmentation / UAX#14 line breaking / UAX#9 bidi | ✅ | Core/Vixen.Ui.Text | **22 048 + 91 707 conformance cases green** |
| Shaping (HarfBuzz) + itemisation | ✅ | Core/Vixen.Ui.Text | 328/413 Consortium cases; the 85 are HarfBuzz's and pinned in both directions |
| Cluster reconciliation, shaping cache, font fallback chain | ✅ | Core/Vixen.Ui.Text, Core/Vixen.Ui |  |
| Glyph outlines (`glyf` + `CFF`), variable fonts (`fvar`/`avar`/`gvar`) | ✅ | Core/Vixen.Ui.Text | 100 Consortium variable-font cases green; `gvar` deltas applied, packed point numbers included |
| `CVAR`, `CFF2` variation, direct `HVAR` | ⬜ | — | 6 cases excluded with the reason recorded; `GlyphOutlineSource` names all three as what it declines |
| Rasteriser, MSDF, atlas, `GlyphFieldCache` | ✅ | Core/Vixen.Ui.Text | Gated by Green's theorem and by field reconstruction. ⚠ The atlas carries a `Version` (coordinates moved) *and* a `Revision` (bytes changed); an… |
| Line wrapping (`LineWrapper`) | ✅ | Core/Vixen.Ui.Text | Greedy first-fit, deliberately not Knuth–Plass |
| **`text-decoration`** — line, colour, style, thickness, `text-underline-offset` | ✅ | Core/Vixen.Ui.Text, Core/Vixen.Ui | Positions and thicknesses come from the face's own `post`/`OS/2` through `FontFace.Decoration`, never a constant: across the fonts committed here the… |
| **Wrapping wired through** — `TextLayout` over `TextLine` over `TextRun`, `white-space`/`overflow-wrap` from the cascade | ✅ | Core/Vixen.Ui | Wrapping lives in `Vixen.Ui` because the widths do: a paragraph in two faces has no single design-unit scale. ⚠ Each wrapped line is re-shaped on its… |
| `CodeEditor` wrap; the *editing* half of `TextArea` (caret between lines, Enter starting one) | 🟡 | Core/Vixen.Ui.Controls | `TextArea` is done — `AcceptsNewlines` makes Enter insert a break, Up/Down move the caret between lines, Home/End are line-relative. ⚠ Ctrl-Enter… |
| **`direction` reaches the shaper** — `UiDocument.DirectionOf` → `UiElement.ParagraphDirection` → `ShapingCache.Shape` | ✅ | Core/Vixen.Ui | Until 2026-08-25 `ParagraphDirection` had **no reference outside `Vixen.Ui.Text`**: a conformant UAX#9 nobody asked the right question. `direction:… |
| **Bidi reordering across a fallback boundary** — `TextRun.Level`, `TextLine` pens in visual order | ✅ | Core/Vixen.Ui | Two things cut a line into runs and only one was cutting: `FontRegistry.Cover` where the face changes, UAX#9 where the level changes. A line whose… |
| `TextEditor` model — IME, caret affinity | ⬜ | — | Affinity is what makes bidi hit-testing answerable; nothing in the tree carries either. ⚠ Still true after the two 2026-08-25 bidi fixes: runs are… |
| Rich-text runs from markup (which stretch is bold) | ⬜ | — | The run list already carries face/size/tracking/leading |
| Geometry builder, path tessellation + antialiasing fringe, batching | ✅ | Core/Vixen.Ui | Trapezoid sweep, not ear clipping |
| `Vixen.Ui.Renderer` — box/text/solid pipelines, atlas upload, scissor clips | ✅ | Core/Vixen.Ui.Renderer | `ui-interface` and `ui-clipped` goldens; 13 sabotages |
| Element tree, `[UiProperty]` system, hit test, routed events, draw list, focus, tab + arrow nav, gestures | ✅ | Core/Vixen.Ui |  |
| Element removal, style-slot compaction, `Move`, reparenting | ✅ | Core/Vixen.Ui | Tombstone + compaction, not slot reuse — the ordering invariant is why |
| **Commands: the responder chain** — `CommandRoute`, `UiElement.AddCommandHandler`, `UiElement.CommandScope`, `ICommandResponder`, `ButtonBase.Command` | ✅ | Core/Vixen.Ui, Core/Vixen.Ui.Controls, Editor/Vixen.Editor.Ui | [45](plan/45-commands-and-focus-scope.md) staging steps **1, 3, 3b and 5**; step 2 is refuted (row below) and step 4 depends on it.… |
| **Strings: the catalogue** — `StringId`, `StringCatalog`, `Strings`, `ControlStrings` | ✅ | Core/Vixen.Ui, Core/Vixen.Ui.Controls, Editor/Vixen.Editor.Ui | [46](plan/46-what-an-application-needs.md) § A3, all three parts. **Promoted out of `Editor/Vixen.Editor.Ui/Localisation/`**, where an application… |
| **Background tasks** — `BackgroundTask`, `BackgroundTaskManager`, `UiApplication.Tasks` | ✅ | Core/Vixen.Ui, Platform/Vixen.Ui.Desktop, Editor/Vixen.Editor.Ui | **Promoted out of `Vixen.Editor.Ui` (2026-08-25)** — application-framework machinery no application could reach — the fifth witness in… |
| **The accessibility tree** — `UiElement.Role`/`AccessibleName`/`AccessibleDescription`/`AccessibleValue`/`AccessibleState`, relations, `UiDocument.AccessibilityInvalidated` | ✅ | Core/Vixen.Ui, Core/Vixen.Ui.Controls, Core/Vixen.Ui.Testing | [46](plan/46-what-an-application-needs.md) § A2, and [09](plan/09-ui-framework.md) now has the § Accessibility it was said to have and did not. ⚠… |
| Editor context ⇒ `CommandScope` conversion (doc 45 steps 2 and 4) | ⬜ | — | ⚠ **Doc 45 § G2's premise is refuted and the doc has been amended.** The editor's context is *not* pushed from focus handlers: every push in the tree… |
| **A dialog that answers** — `DialogService`, `DialogSession<T>` | ✅ | Core/Vixen.Ui.Controls | [46](plan/46-what-an-application-needs.md) § A4, **moved** out of `Vixen.Editor.Ui` rather than copied. `Dialog` already had modality — a real… |
| Pinch and rotate gestures | ✅ | Core/Vixen.Ui, Core/Vixen.Ui.Testing | `GestureRecognizer` tracks a second pointer and raises `TransformEvent` with cumulative *and* incremental scale and rotation, each past its own slop… |
| `Vixen.Ui.Composition` — `Component`, `@if`/`@switch`, keyed `@for` | ✅ | Core/Vixen.Ui |  |
| `scoped` scoping + a component stylesheet loaded once per **type** | ✅ | Core/Vixen.Ui | `StyleIsScoped` was parsed, carried, and then read by nothing |
| Named slot projection; LIS reorder pass | 🟡 | Core/Vixen.Ui, Core/Vixen.Ui.Markup | Projection is built end to end — `<slot name=…>` binds to `BoundSlot`, the emitter calls `BuildContext.Slot`, `Component.Slots` resolves the default… |
| `VirtualizingPanel` — the virtualisation primitive doc 09 asks for | ✅ | Core/Vixen.Ui.Controls | Realises on `LayoutFinished`. ⚠ Fixed row heights only — variable heights need a running-sum index, which is a different control. `TreeView` is… |
| Image / texture draw command (`DrawContext.DrawImage`, `BatchKind.Image`) | ✅ | Core/Vixen.Ui | Unblocked `Image`, `Viewport` drawing a `RenderTarget`, and the node-graph preview layer |
| Multi-window and DPI | ✅ | Core/Vixen.Ui, Platform/Vixen.Platform.Ui | `UiSurface` — one document, N windows, each with its own size, DPI scale, draw list and `vw`/`vh`. A window is a *surface* rather than a second… |
| `Vixen.Ui.Markup` — VXML lexer/parser/binder/emitter, `#line` spans, incremental reparse | ✅ | Core/Vixen.Ui.Markup | 118 tests; byte-exact round trip over every *prefix* of a real file. `VxmlParser` takes the shared `Blender` and reuses green nodes |
| `bind:` update events | ⬜ | — | Two-way `bind:value` works; `bind:value:oninput` does not — the modifier list (`stop`/`prevent`/`capture`/`once`/`self`/`handled`) is `on:`-only, so… |
| `<self />` — a markup spelling for the component's own element | ✅ | Core/Vixen.Ui.Markup | **2026-08-24.** A `.vxml`'s markup roots are the host's *children*, so `on:keydown.capture` on the first root is a different element with different… |
| `on:….handled` — a handler that hears an event something else dealt with | ✅ | Core/Vixen.Ui, Core/Vixen.Ui.Markup | **2026-08-24.** The one modifier `BuildContext.On` could not apply itself: `stop`, `once` and `self` filter a handler it owns, while… |
| ⚠ `on:click` on a `Control` that raises no activation bound a handler nothing could raise | ✅ | Core/Vixen.Ui.Controls | **2026-08-24.** `ControlMarkup` has replaced the `click` entry since 2026-07-31 so a `<Button>` hears Space, Enter, an access key and `Activate`… |
| ⚠ `UiDocument.Cursor` had no consumer, so no `cursor-*` class changed anything | ✅ | Platform/Vixen.Platform.Ui | **2026-08-24.** The cascade resolved `cursor: pointer` correctly and nothing read it; doc 43 scored the family *works* because its probe called… |
| `change:` — a value-change subscription markup can name | ✅ | Core/Vixen.Ui.Markup, Core/Vixen.Ui | **2026-08-22.** `on:change` could not exist: the `Subscribe` table holds `Action<UiElement, Action<UiEvent>, RoutingStrategy>` and no routed handler… |
| `refs` — a per-iteration handle | ✅ | Core/Vixen.Ui.Markup, Core/Vixen.Ui | **2026-08-22.** `ref` inside `@for` is still `VXML2010` and a `List<T>` would be worse — a surviving key's body is not re-run, so the list is a… |
| ⚠ A `[UiProperty]` was not registered until something read a static field of its type | ✅ | Core/Vixen.Ui.Generators | **2026-08-22.** The generated class had no static constructor, so it was `beforefieldinit` and the CLR could defer the `UiPropertyRegistry.Register`… |
| `Vixen.Ui.Markup.Generators` — `IIncrementalGenerator` | ✅ | Core/Vixen.Ui.Markup.Generators | 19 tests, two of them real `dotnet build`s |
| `vixen` CLI path emitting generated C# to disk | 🟡 | Tools/Vixen.Cli, Tools/Vixen.Sdk | The `CoreCompile` hook carries something now: `VixenImport` runs `vixen import --addresses`, writes `Addresses.g.cs` and adds it to `Compile`… |
| `Vixen.Ui.HotReload` — styles / markup / component replacement | ✅ | Core/Vixen.Ui.HotReload | 27 tests |
| Hot reload driven against a **running window** | ✅ | Editor/Vixen.Editor.App, Editor/Vixen.Editor.Host | Doc 36 § P4. `EditorApplication` builds a `HotReloadHost` over the shell's document, registers it with `MetadataUpdate` (weakly, so a closed window… |
| `Vixen.Ui.Controls` — 40-odd standard controls, `ControlTheme` as `UserAgent` origin | ✅ | Core/Vixen.Ui.Controls | 259 tests over a real theme and font |
| `Vixen.Ui.Controls.Advanced` — Docking, TreeView, PropertyGrid, NodeCanvas, CodeEditor, DataGrid, Viewport, ColorPicker, CurveEditor, GradientEditor, Timeline | ✅ | Core/Vixen.Ui.Controls.Advanced | 313 tests |
| **Floating dock groups in real OS windows** | ✅ | Core/Vixen.Ui.Controls.Advanced, Platform/Vixen.Platform.Ui | A tab dragged off the window tears out; a drop inside docks. Windows keyed on the group object rather than its index so a rebuild does not blink… |
| `UiDocument` "layout finished" callback | ✅ | Core/Vixen.Ui | All six controls on it. `Control.WhenResized` gates on the box changing; `Update` refuses a nested call, which is what lets a `Refresh` that runs its… |
| Undo inside controls | ⬜ | — | Deliberate rather than merely missing: `CodeBuffer` says undo belongs to the application, because it has to interleave with everything else the… |
| `Canvas2D` | ⬜ | — | Doc [09](plan/09-ui-framework.md)'s P2, no editor consumer. `Samples/06-CanvasStress`, the intended proof, does not exist either |
| `OkLch.ToSrgb` real gamut mapping | ✅ | Core/Vixen.Core.Mathematics | `GamutMap` does CSS Color 4's binary search on chroma in OkLch, holding hue where a per-channel clamp shifted it. `StyleValueParser` brings… |
| `StyleTree.AppendChild` O(children) | ✅ | Core/Vixen.Ui.Styling | Amortised constant now: a child run reserves capacity beyond its count and doubles on overflow, so copies fall off geometrically. ⚠ The reserved… |
| `Vixen.Ui.Testing` harness + software rasteriser | ✅ | Core/Vixen.Ui.Testing | Selector queries, interactions (taps, drags, `Pinch`), box and visual assertions. Group opacity, a third finger and the remaining box assertions are… |
| `Samples/02-HelloUi` | ✅ | Samples/02-HelloUi | 8 001 elements at 0.436 ms, 0 B — exit criterion met with margin. Rewritten in `.vxml`/`.vcss` on `Vixen.Ui.Desktop` (2026-08-23); the hand-rolled… |

## 1.8 Raven and shaders

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| Hand-written lexer + recursive-descent parser (Phase 5b migration) | ✅ | Raven/Vixen.Raven | ANTLR `.g4` kept as a differential oracle |
| Green/red tree from `Syntax.xml` (79 + 13 node types), trivia, spans | ✅ | Raven/Vixen.Raven |  |
| Semantic phase, target-independent IR | ✅ | Raven/Vixen.Raven |  |
| GLSL + SPIR-V emitters, `spirv-val` on every module | ✅ | Raven/Vixen.Raven | Golden `spirv-dis` snapshots |
| Incremental reparse with green-node reuse | ✅ | Core/Vixen.Core.Syntax |  |
| `protocol`, shader inheritance, compile-time generics, `compose` | ✅ | Raven/Vixen.Raven |  |
| Atomics (8 on `int`/`uint`) | ✅ | Raven/Vixen.Raven | Landed for the VFX compaction path |
| `.rvnlib` / `.rvnfx` artefact formats | ✅ | Raven/Vixen.Raven |  |
| `Raven/Library` — Core, Shading, Geometry, Material, Pipeline, PostFx, Ui, Vfx | ✅ | Raven/Library | Every shader reaches both backends under `glslc` and `spirv-val` |
| **Bindless texture arrays** (`Texture2D[]`) | ✅ | Raven/Vixen.Raven · Raven/Library | The only unsized array outside a storage block, and the one that is descriptors rather than memory: `OpTypeRuntimeArray` with no stride under… |
| **String interpolation** | ⬜ | — | Needs lexer modes; nothing shipped uses it |
| **Workgroup-shared memory** | ✅ | Raven/Vixen.Raven · Raven/Library | `groupshared` is a parser modifier, a flag on `SourceFieldSymbol`, and a per-entry-point reachability pass in the `Lowerer` — ⚠ a stage declares only… |
| `Vixen.Raven.Transpile` (SPIRV-Cross → ESSL/HLSL/MSL/WGSL) | ⬜ | — | ADR-012 says SPIRV-Cross owns these targets. **No SPIRV-Cross package in `Directory.Packages.props`**, and no such project under `Raven/` |
| Cross-compilation test pass | ⬜ | — | Blocked on the row above |
| Nuke `CompileShaderLibrary`; SPDX enforcement in `CheckFormat` | 🟡 | build/Build.Shaders.cs · build/Build.cs | `CheckShaders` is the half that matters and exists: it builds `Vixen.Raven.Cli`, recompiles each editor library shader from its whole import closure… |
| Numeric BRDF gate (GPU compute readback vs. C# port) | ✅ | Platform/Vixen.Raven.Gpu.Tests | The shipped `Brdf.rvn` — not a copy — evaluated on a device over 256 (angle, roughness) samples against arithmetic *derived* from Walter 2007 and… |
| Per-backend layout gate (reflection offsets vs. GPU readback) | ✅ | Platform/Vixen.Raven.Gpu.Tests | The host writes bytes at the offsets the reflection reports and the shader reads members by name, so a member the two disagree about arrives holding… |
| Negative-diagnostic fixture pairs | 🟡 | Raven/Vixen.Raven.Tests | **127 ids; 69 have the negative that proves the rule does not over-fire, 58 do not** — and two of the 58, `RVN2003` and `RVN2014`, cannot fire on any… |
| A file holds type declarations only (`RVN2054`) | ✅ | Raven/Vixen.Raven | A `func`, `const val`, `var`, `init`, property or `operator` written straight into a file is now an error, where it was **silence**. ⚠ The… |
| Stream interpolation control; per-module flat IR namespace | ⬜ | — | Recorded in doc [07](plan/07-raven-shader-pipeline.md) §Streams and §D. `flat` is applied automatically where an integer varying requires it… |
| `Vixen.Shaders` — typed parameter/permutation keys, std140 writers | ✅ | Core/Vixen.Shaders | Generated from Raven reflection |
| Effect system + 3 cache tiers (`EffectStore`, disk, remote) | ✅ | Core/Vixen.Shaders |  |
| `Tools/Vixen.ShaderCompiler` (`PermutationClosure`, `EffectBundleBuilder`) | ✅ | Tools/Vixen.ShaderCompiler | Zero-runtime-compilation criterion asserted by test |
| `Tools/Vixen.ShaderCompilerService` | ✅ | Tools/Vixen.ShaderCompilerService |  |
## 1.9 Rendering, video and XR

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `RenderSystem`, `RenderObject`/`RenderNode`, features, views, stages, sort modes | ✅ | Core/Vixen.Rendering |  |
| `VisibilityGroup` (job-parallel) + `GpuVisibilityGroup` (Hi-Z, indirect args) | ✅ | Core/Vixen.Rendering | Falls back where it cannot run |
| Mesh, transform, skinning, instancing, material, lighting, shadow-caster features | ✅ | Core/Vixen.Rendering |  |
| **Two-phase occlusion culling** (`GpuVisibilityGroup.TwoPhase`, `Culling.rvn`'s `Late` permutation) | ✅ | Core/Vixen.Rendering | Removes the frame of staleness. ⚠ The late pass writes a **difference**, not an answer — the union would draw every visible object twice. Needs the… |
| **Sprites, sprite sheets, 9-slice** | ✅ | Core/Vixen.Rendering · Core/Vixen.Ui · Core/Vixen.Core.Mathematics | `SpriteRenderFeature` + `SpriteGeometry` on the renderer side, `DrawContext.DrawNineSlice` on the interface side, sharing `NineSlice` because the two… |
| Compacted draws | ✅ | Core/Vixen.Rendering | `GpuDrawArguments.Compact` — one command per batch, count read from a buffer the host never sees. A clustered `ForwardPlus` frame merges; ⚠ the… |
| `GraphicsCompositor` as an asset, resolvable by address | ✅ | Core/Vixen.Rendering | Asserted in `Vixen.Assets.Tests` |
| **`WorldRenderer` in the boot path** | ✅ | Tools/Vixen.App | `AppGraphics` is the host's half — device (`GraphicsHost`: Vulkan where there is a surface, Null where there is not), swapchain, `EffectSystem` fed… |
| **Camera component → `RenderView`** | ✅ | Core/Vixen.Rendering | `CameraExtractionSystem`: lowest `Order` wins, `PreRender` after the transforms. ⚠ A world with no camera leaves the view alone and says so — a… |
| Materials — composable feature tree, 2 workflows, 7 shading models, both layering forms | ✅ | Core/Vixen.Rendering | Every combination through `glslc` + `spirv-val` |
| **A material feature that samples** (`TexturedMetalRoughnessSurface`) | ✅ | Raven/Library · Core/Vixen.Rendering | Needed four things that did not exist: `Texture2D[]` as a type, `[Shared]` so every sampling feature names one table, `uv` on `MaterialData`, and a… |
| Transmission / refraction | 🟡 | Core/Vixen.Rendering.Water · `Raven/Library/Water` | [35](plan/35-water.md) § D8 closed the *pass*: `!Water` reads a `!Copy` of the scene colour plus depth and integrates absorption and scattering over… |
| Bindless material textures (a feature that samples needs a binding index) | ✅ | Core/Vixen.Rendering | `MaterialRenderFeature.Textures` + `TextureIndices`: the shader declares a `uint`, the texture takes a table slot, and the slot goes into the… |
| Lighting — directional/point/spot/tube/rect, clustered binning, IBL, reflection probes | ✅ | Core/Vixen.Rendering | `EnvironmentBaker` + `SphericalHarmonics` on the CPU. ⚠ Intensities are **photometric**: `LightComponents.Default` gives a new directional light 100… |
| **Light probes** (tetrahedral interpolation) | ✂️ | Core/Vixen.Core.Mathematics · Core/Vixen.Rendering | **Withdrawn as an approach, not deleted as code.** `ExactPredicates` + `DelaunayTetrahedralization` + `LightProbeVolume` are built and correct, and… |
| **Distance fields** (bake, clipmap, tracer, traced pass) | ✅ | `Vixen.Rendering.DistanceFields` | [19](plan/19-lighting-and-global-illumination.md) § L1. Exact bake with a voted sign, camera-snapped scrolling clipmap, CPU tracer, volume textures… |
| **Irradiance field** (bricks, indirection, L1 payload, leak mitigation) | 🟡 | `Vixen.Rendering.IrradianceFields` · Core/Vixen.Rendering | [19](plan/19-lighting-and-global-illumination.md) § L2, and see the [README](../Core/Vixen.Rendering.IrradianceFields/README.md). 4³ probes in a 5³… |
| **Screen probe gather** (octahedral map, lattice, reference, traced atlas) | 🟡 | `Vixen.Rendering.ScreenProbes` · Core/Vixen.Rendering | [19](plan/19-lighting-and-global-illumination.md) § L3, and see the [README](../Core/Vixen.Rendering.ScreenProbes/README.md) for every claim below.… |
| **Surface cache** (cards, atlas, radiosity) | ✅ | `Vixen.Rendering.SurfaceCache` | [19](plan/19-lighting-and-global-illumination.md) § L4. Cards fitted by dominant-axis vote, shelf atlas, traced capture, direct sun behind a shadow… |
| **Traced reflections** (mirror, rough, fallback) | ✅ | `Vixen.Rendering.Reflections` | [19](plan/19-lighting-and-global-illumination.md) § L5, and see the [README](../Core/Vixen.Rendering.Reflections/README.md). The layer that reuses… |
| **Ray tracing** (RHI, query, tracer, referee) | ✅ | `Vixen.Rendering.RayTracing` · Core/Vixen.Graphics | [19](plan/19-lighting-and-global-illumination.md) § L6. A median-split triangle BVH as the reference — deterministic builds, front-to-back traversal… |
| **Doc 19's lit path as compositor nodes** — `!GlobalDistanceField`, `!IrradianceField`, `!DistanceFieldAo`, `!IndirectDiffuse` | ✅ | Core/Vixen.Rendering · Core/Vixen.Rendering.PostFx | The GI renderers all existed with no asset naming them, so a game could reach doc 19 only by assembling its compositor in C#. `CompositorBuilder` now… |
| **Screen probes, reflections and the surface cache as nodes** — `!ScreenProbeGather`, `!Reflections`, `!SurfaceCache` | ✅ | Core/Vixen.Rendering · Core/Vixen.Rendering.PostFx | The same seam extended: the document owns placement and the numbers, the host supplies `ScreenProbeTracer`/`Resolver`/`Accumulator`/`Filter`… |
| Per-object reflection probe selection | ✅ | Core/Vixen.Rendering · `Raven/Library/Pipeline` | Built and wired. `ForwardLightingRenderFeature` picks a probe per object and writes `probeIndex`/`probeWeight` into an object record… |
| Shadows — CSM, cube, spot, atlas, static caching, PCF/PCSS | ✅ | Core/Vixen.Rendering | The cascades' static cache reaches a document since 2026-08-21 and did not before: the node was complete and only a test ever set… |
| **Virtual shadow maps** — clipmap, page marking, page residency, per-page draws, composed lookup | 🟡 | Core/Vixen.Rendering · `Raven/Library/VirtualShadows` | [22](plan/22-virtualized-geometry.md) phase 7. A directional clipmap and a map per spot: the level is chosen **per pixel** from that pixel's own… |
| Punctual shadow caching | ✅ | Core/Vixen.Rendering | `PunctualShadowRenderer.Cached`, and `cached: true` on a `!PunctualShadows` node. A lamp keeps the slot it was drawn into and the texels in it; a… |
| Motion vectors — a stage with a shader override, as the shadow pass is | ✅ | Core/Vixen.Rendering | `MotionVectorRenderFeature` + `Pipeline/MotionVectors.rvn`, drawn as its own velocity pass — `Taa.rvn` had declared a `motionVectors` input since it… |
| **Blend shapes** — sparse quantised deltas, the import, the kernel, the compute scatter and the render feature | 🟡 | Core/Vixen.Rendering · `Raven/Library/Pipeline` · Editor/Vixen.Editor.Assets | [33](plan/33-character-creator.md) § D4's design, built as a vertical slice and no longer ⬜. `MorphTargetData` is the storage — `(index, Δposition… |
| `Vixen.Rendering.PostFx` — TAA, FXAA, sharpen, AO, fog, outline, vignette, lens distortion, chromatic aberration, grain, bloom, depth of field, motion blur, local exposure, lens flare, tonemap + CDL grading | ✅ | Core/Vixen.Rendering.PostFx | Seventeen node kinds a `.vxcompositor` names, each with its shader in `Raven/Library/PostFx` and its factory arm in `PostEffectAssets`… |
| **`!StandardFrame` + `RenderQuality.vxpreset`** — the seven-knob frame, the tier waterfall, `vixen frame explode` | 🟡 | Core/Vixen.Rendering.PostFx · Tools/Vixen.Cli | [39](plan/39-standard-frame-and-render-presets.md) and the [README](../Core/Vixen.Rendering.PostFx/README.md). One node that expands at build time… |
| SMAA · MSAA **depth** resolve | ✅ | — | Phase 5's post-FX list is closed, and the list was wrong about every item on it including these two. **SSR ships** as `!Reflections`. **MSAA colour… |
| **The water kernel and the evaluator** — bodies, the spectrum, the field, one surface definition | ✅ | Core/Vixen.Water | [35](plan/35-water.md)'s **W1**: one closed-form surface definition every consumer asks, and it opens no device, because § D1's kernel is what a… |
| **The water pass and the shading model** — `!Water`, `SingleLayerWaterShading`, the volume integration, § D8's tile classification | ✅ | Core/Vixen.Rendering.Water · `Raven/Library/Water` | [35](plan/35-water.md)'s **W2**. `WaterVolume` integrates absorption and scattering over the *view* path in closed form, `!Water` composites it once… |
| **The water zone and its sliding window** — the field, when it moves, and what it costs | 🟡 | Core/Vixen.Water · Core/Vixen.Rendering.Water | [35](plan/35-water.md)'s **W3**, and both halves are built and reached: `WaterZone`/`WaterZoneState` hold and move the field, `WaterInfoTexture`… |
| **The water surface mesh** — the quadtree, the coverage predicate, the far skirt, the two planes | ✅ | Core/Vixen.Water · Core/Vixen.Rendering.Water · `Raven/Library/Water` | [35](plan/35-water.md)'s **W4**, and the node without which no wet pixel can exist. **One quadtree, not two**: `PatchSelector` is `TerrainLodTree`'s… |
| **Water carving** — the reserved layer, the bed, the bank, and what survives a body moving | ✅ | Core/Vixen.Water | [35](plan/35-water.md)'s **W5**, on [31 § D4](plan/31-terrain-grass-and-trees.md)'s contract and **with no change to it** — which is the evidence doc… |
| **Swimming and underwater** — the fourth move mode, the immersion, the hysteresis, the volume shape, the waterline | ✅ | Core/Vixen.Physics · Core/Vixen.Water.Physics · Core/Vixen.Rendering.Water | [35](plan/35-water.md)'s **W6**, closing [29 § Where this stops](plan/29-players-and-possession.md) on that row's own condition. `CharacterMoveMode`… |
| **Buoyancy** — pontoons, the spherical cap, the flow drag, and a convergence test with a control | ✅ | Core/Vixen.Water · Core/Vixen.Water.Physics | [35](plan/35-water.md)'s **W7**, kernel and join. `Buoyancy.Solve` asks the same evaluator the surface is drawn from and produces a force at each… |
| **Ripples** — the sliding-window height field, the budget, and the boundary that does not mirror | ✅ | Core/Vixen.Water · Core/Vixen.Rendering.Water | [35](plan/35-water.md)'s **W8**, both halves: the CPU reference, and `Ripples.rvn` as § D12's step in one dispatch over W0's `PingPongTextures` — its… |
| **The water toolset** — one mode, three verbs, the zone panel's derived numbers, and the wiring | 🟡 | Editor/Vixen.Editor.Water · Core/Vixen.App.Hosting · Core/Vixen.Rendering.Water | [35](plan/35-water.md)'s **W9**, the phase that turns the stack from built into reachable — and most of it is. **One mode where doc 31 needed two**… |
| **The water seam** — `Water/Surface.rvn` against the C# evaluator, on a device | ✅ | `Raven/Library/Water` · Platform/Vixen.Graphics.Golden.Tests | [35](plan/35-water.md)'s **§ D2**, written before there was a renderer to see it fail in, which is the point: `WaterSurfaceProbe.rvn` dispatches the… |
| **Water's unblockers** — a volume shape interface, a third reserved terrain layer, a ping-pong pair, the scene-colour copy | ✅ | Core/Vixen.Rendering · Core/Vixen.Terrain · Core/Vixen.Graphics.RenderGraph | [35](plan/35-water.md)'s **W0**, and every one of the four is written for a consumer outside water, which is the phase's whole condition. **B2**:… |
| Grading LUT as an **asset** | ✅ | Core/Vixen.Rendering.PostFx · Editor/Vixen.Editor.Assets | Both halves. The consuming one: `TonemapRenderer.Lut` names a graph resource, `TonemapKeys.UseLut` folds the sample out when it is unset, and… |
| Auto exposure — the log-average chain **and** a 64-bin histogram | ✅ | Core/Vixen.Rendering.PostFx | Both of UE's meters. The histogram is **off by default** because it is a different question rather than a better chain — a geometric mean and a… |
| `AutoExposure.rvn` wiring | ✅ | Core/Vixen.Rendering.PostFx | `AutoExposureRenderer`: a chain of `ComputeRenderer` dispatches halving a 512-wide luminance image to 1×1, then easing the stored exposure toward it.… |
| **Terrain** — the heightfield kernel, the LOD quadtree with no cracks, the splat material's 4/8/12/16 slots, tiles and holes | ✅ | Core/Vixen.Terrain · Core/Vixen.Rendering.Terrain · `Raven/Library/Terrain` | [31](plan/31-terrain-grass-and-trees.md) T0–T4, T7, T8. The kernel opens no device — sculpt, paint, erosion, layer composition and the LOD descent… |
| **Terrain collision** — the heightfield as a collider | ✅ | Core/Vixen.Terrain.Physics · Editor/Vixen.Editor.Terrain.Physics | [31 § B1, § D10](plan/31-terrain-grass-and-trees.md). **The runtime half is done and reached**: `TerrainColliderSystem` is one assembly with two… |
| **Foliage and grass** — instance cells, the two-stage GPU cull, the scatter ring, impostors | ✅ | Core/Vixen.Foliage · Core/Vixen.Rendering.Terrain · `Raven/Library/Terrain` | [31](plan/31-terrain-grass-and-trees.md) T5–T7. Two paths that look alike and are not: foliage is *placed* instances persisted beside the scene as a… |
| **Texture streaming** — mip tails through the page service | ✅ | Core/Vixen.Engine.Renderer · Core/Vixen.Rendering | [22](plan/22-virtualized-geometry.md) improvement 6. `TexturePagePool`'s pages are fixed byte-size slices of a KTX2 file's level data, and it brought… |
| **Virtualized geometry** — cluster DAG, pages, hierarchical traversal, visibility buffer, material resolve, software raster | ✅ | `Vixen.Rendering.VirtualGeometry` · Core/Vixen.Rendering | [22](plan/22-virtualized-geometry.md) phases 0–6. Import → pages → residency → traversal → hardware raster → bin → shade runs on a device; phase 6's… |
| Deferred pipeline — GBuffer, shading-model dispatch, forward routing, decals | ✂️ | `Raven/Library/Pipeline` | Phase 10; cut-list #6. **The shader half exists and is gated**: `GBufferPass.rvn` (the same shader as `ForwardPlus` down to `surface.Compute`… |
| Volumetric fog (froxel) | ✅ | Raven/Library/PostFx · Core/Vixen.Rendering.PostFx | [plan/06 § P2](plan/06-rendering-pipeline.md), which corrects its own "shares the clustering grid" premise: what is shared is the Z distribution and… |
| Contact shadows, light shafts, SSS blur, upscaler + FSR1 | ⬜ | — | Phase 10. Nothing in the tree names a contact shadow, a light shaft, an `IUpscaler` or FSR. Subsurface is the partial exception and is **not** this… |
| Mesh shaders / meshlet culling behind capability flags | ⬜ | — | Phase 10; [22](plan/22-virtualized-geometry.md) is now the plan. `GraphicsDeviceFeatures.HasMeshShaders` is detected on Vulkan (`VK_EXT_mesh_shader`… |
| Golden-image fixture suite | ✅ | Platform/Vixen.Graphics.Golden.Tests | Six kinds — the backend at its simplest, one fixture per state bit a backend can silently ignore, the compositor, the UI, debug draw, and a whole… |
| `Samples/03-PbrShowcase` | ✅ | Samples/03-PbrShowcase | Converted to the standard frame: seven knobs stand where the raw-RHI passes were, the grid casts through the cascades, a baked Preetham sky is the… |
| **`Vixen.Video`** — managed WebM demuxer, codec seam, player with an audio-driven clock, YUV planes + conversion coefficients | ✅ | Core/Vixen.Video | 144 tests, and `Samples/11-VideoPlayback` drives it. Doc 06 § Other renderables. Landed far ahead of its Phase 10 slot |
| **`Vixen.Video.Codecs`** (Opus over loose WebM packets) | ✅ | Core/Vixen.Video.Codecs | Split so a game with an uncompressed logo sting links no Concentus |
| **`Vixen.Video.Rendering`** — one pipeline, three plane bindings, `VideoRenderTarget` | ✅ | Core/Vixen.Video.Rendering | `VideoRenderer` converts the planes to an ordinary colour texture, and `Samples/11` uses it. ⚠ `VideoRenderTarget` and `VideoRenderFeature` — the… |
| Video: MP4; a **material** (video lit on a mesh); frame-accurate seek; audio-track choice; subtitles; 10-bit / BT.2020 | ⬜ | — | MP4 is additive behind `IVideoStreamDecoder`, and `VideoImporter` claims-and-refuses it rather than half-reading it; the material is… |
| **`Vixen.Xr`** — session state machine, per-eye poses, asymmetric projections, runtime-owned swapchains, action input, ECS bridge, simulated headset | ✅ | Core/Vixen.Xr | All of it runs on a machine with no headset. ⚠ No sample or engine host drives it — the row below is why |
| **`Vixen.Xr.OpenXR`** — the three desktops and Android | ✅ | Platform/Vixen.Xr.OpenXR | The Vulkan instance/device/GPU are the *runtime's* choice, and the API shape makes that order impossible to get wrong |
| XR: a render feature; single-pass multiview; hand/eye tracking, passthrough, anchors | ⬜ | — | Two passes work today. Multiview's hook is `XrSwapchainDescription.ArrayLayers`, honoured by both backends; the `VK_KHR_multiview` half is… |
| Shader reflection: vertex input locations + push-constant stage coverage | ✅ | Core/Vixen.Shaders | A binding index is declaration order, so a literal in C# survives a renumbering and a generated constant does not — and the failure is silent CPU… |
## 1.10 Gameplay subsystems

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Physics` (Jolt 2.22.0) — bodies, shapes, constraints, character, queries, triggers, layers, CCD, ECS sync, debug draw, determinism gate | ✅ | Core/Vixen.Physics | Two binding bugs pinned by tests — [README](../Core/Vixen.Physics/README.md) § Two bugs in the binding |
| Physics on **iOS** | ⛔ | — | `JoltPhysics.Native` carries win/linux/osx/Android and **no iOS slice**; iOS is NativeAOT-only where a dylib would not load anyway. Needs a static… |
| Per-pair collision suppression; vehicles, ragdolls, soft bodies; double precision | ⬜ | — | Out of Phase 8 scope. Layer-pair filtering exists (`PhysicsLayers`); `Foundation.Init(doublePrecision: false)` is the pin |
| `Vixen.Audio` — software mixer, buses, sends, sidechains, 14 effects, streaming, ECS, events, parameters, interactive music, capture, HRTF, loudness | ✅ | Core/Vixen.Audio | Far beyond the line the roadmap asked for; nothing structural is owed |
| `Vixen.Audio.Codecs` (Vorbis, Opus, ADPCM) | ✅ | Core/Vixen.Audio.Codecs | Pure managed, rooted in the AOT probe |
| `Vixen.Audio.Physics` (Jolt occlusion), reverb zones | ✅ | Core/Vixen.Audio.Physics | Zones need no physics; occlusion does |
| Backends: OpenAL ✅, WebAudio ✅ | ✅ | Platform/Vixen.Audio.Backend.* |  |
| Measured HRTF sets | ⬜ | — | `HrtfPanner` is a synthetic model and says so; measured impulse-response sets are content |
| `Vixen.Animation` — skeletal playback, 1D/2D blend trees, layers + masks, state machine, two-bone/look-at/foot IK, root motion, events, GPU skinning, key reduction | ✅ | Core/Vixen.Animation | Benchmarked; `ParallelThreshold` = 32 from measurement |
| A runtime path for `.vxanim` | ✅ | Editor/Vixen.Editor.Assets | `AnimationClipImporter` compiles an authored clip to `AnimationClipContent` and a game loads one by address. `AnimationClipCache.Get` pairs it with a… |
| Move sets — flat catalogue, scored query, bake-time overlay | ✅ | Core/Vixen.Animation | Doc [34](plan/34-move-sets-and-pose-constraints.md) P1. `MoveSet` is a flat array with a contiguous scan table; `QueryMoveSelector` filters on… |
| Move transitions, phase sync and partial-body sets | ✅ | Core/Vixen.Animation | Doc [34](plan/34-move-sets-and-pose-constraints.md) P2. `ITransitionPolicy` fronts an ordered rule list, first match wins, and `RuleFor` says which… |
| Pose constraints — the stage, the four goal kinds, arbitration, and the frame's pre-evaluation pass | ✅ | Core/Vixen.Animation | Doc [34](plan/34-move-sets-and-pose-constraints.md) P3. `ConstraintStack` is an `IPoseProcessor`: position, orientation, aim and distance goals, each… |
| Proxy shapes, surface coordinates and adapted sockets — one authored contact on any body | ✅ | Core/Vixen.Animation · Editor/Vixen.Editor.Assets | Doc [34](plan/34-move-sets-and-pose-constraints.md) P4, and its central claim demonstrated: one authored contact, three bodies of different… |
| Trajectories — a goal that moves through a clip | ✅ | Core/Vixen.Animation | Doc [34](plan/34-move-sets-and-pose-constraints.md) P5. `TrajectoryFrame` wraps any other frame, so "this goal moves" is orthogonal to "this goal is… |
| Root placement, camera framing and the LOD governor | ✅ | Core/Vixen.Animation · Core/Vixen.Engine | Doc [34](plan/34-move-sets-and-pose-constraints.md) P6. `RigidBodySolver` is one solve for the two bodies with no skeleton — a character's placement… |
| Constraint authoring — the track, the generated inspector, templates, the ladder, proposals | ✅ | Core/Vixen.Animation · Editor/Vixen.Editor.Assets · Editor/Vixen.Editor.AssetEditors | Doc [34](plan/34-move-sets-and-pose-constraints.md) P7, which closed P3's authoring gap. A `.vxanim` carries a `constraints:` block that round-trips… |
| The four authoring views, and a timeline that can hold an interval | ✅ | Core/Vixen.Ui.Controls.Advanced · Editor/Vixen.Editor.AssetEditors | The views P7 first shipped without. **`Timeline` gained spans** — a track is instants or intervals and never both, an interval draws its ease ramps… |
| The variation harness | ✅ | Core/Vixen.Animation · Editor/Vixen.Editor.Assets · Editor/Vixen.Editor.AssetEditors | Doc [34](plan/34-move-sets-and-pose-constraints.md) P8 — the answer to its first risk, that the cost of constraints is knowing when to stop. Bodies… |
| Doc 34 wired through the editor and loadable by a game | ✅ | Core/Vixen.Animation · Core/Vixen.Rendering · Editor/Vixen.Editor.AssetEditors | The registration half: `.vxharness` has a document and a factory so the matrix opens and **Run** fills it; `AddAnimation`/`AddConstraintGizmos` are… |
| Doc 34's seams, implemented twice | ✅ | Core/Vixen.Animation · Editor/Vixen.Editor.Assets | Doc [34](plan/34-move-sets-and-pose-constraints.md) P9, less the sample. Every interface in its Part 4 has a second implementation differing in… |
| The animation documents actually connected to a project | ✅ | Core/Vixen.Animation · Editor/Vixen.Editor.App | The last mile: until it was walked the panels were inert in the shipped editor — the shape viewport drew nothing, **Run** had no project, **Propose… |
| `Vixen.Editor.AnimationGraph` | ✅ | Editor/Vixen.Editor.AnimationGraph | Cut-list #7 built rather than cut. An authored state machine as a serialisable document plus the compiler that turns it into `Vixen.Animation`'s… |
| Ragdoll integration | ⬜ | — | No implementation anywhere; lands with the animation/physics join. `Vixen.Net.Animation` already assumes one exists |
| `Vixen.Input` — devices, `InputControlPath`, actions, maps, processors, interactions, `.vxinput`, generated accessors, rebinding | ✅ | Core/Vixen.Input |  |
| **Players and possession** — `PlayerController`, `ControlRotation`, `MoveIntent`, the `Possessing`/`PossessedBy` pair, `Player`, `PlayerInputSystem`, `PossessionSystem`, `ActionPlayerInput` | ✅ | Core/Vixen.Engine | [29](plan/29-players-and-possession.md) **P0**, and reached by `Samples/13-ThirdPersonShooter`'s `PlayerRig`. Unreal's decomposition: the controller… |
| **Character movement** — `CharacterMovement`, `CharacterState`, `CharacterMotion`, `CharacterMovementSystem`, the `PhysicsScene` character bridge | ✅ | Core/Vixen.Physics | [29](plan/29-players-and-possession.md) **P1**. Walking, falling and flying; coyote time, jump buffering and a variable-height **clamp**; crouch… |
| Per-character slope and step limits | ⬜ | — | `CharacterControllerSettings` has both and Jolt fixes them at creation, so a component field means detecting the edit and recreating the controller.… |
| **Player camera rigs** — `PlayerCameras.FirstPerson` / `.ThirdPerson`, `PlayerCamera`, the aim → `PovAim` / `OrbitBody` wire | ✅ | Core/Vixen.Engine | [29](plan/29-players-and-possession.md) **P2**. Each builds a `Camera` + `CameraDirector` on the player's channel and a shot on the same one… |
| Split-screen **rendering** | ⛔ | — | Simulates and does not draw. Two players get two directors, two sets of shots and two independent cameras, but `CameraExtractionSystem` fills exactly… |
| **Networked players** — `PlayerMoveInput`, `PlayerPawn`, `PlayerSpawner`, `LocalPlayerSystem`, `PredictedPlayerMovement` | ✅ | Core/Vixen.Net.Engine · Core/Vixen.Net.Physics | [29](plan/29-players-and-possession.md) **P3**. `PlayerSpawner` is `AGameModeBase` minus the god object: join, respawn, leave, with the pawn spawned… |
| **Rollback smoothing, wired** — `PredictionSmoothingSystem`, `PredictionSmoothing`, `PlayerInputQuantizeSystem` | ✅ | Core/Vixen.Net.Engine | `PredictionSmoother` had been built and tested in `Vixen.Net` and **nothing used it**, so a corrected character teleported and took the camera with… |
| Zero-allocation cover for the player path | ✅ | Core/Vixen.Engine.Tests · Core/Vixen.Physics.Tests · Core/Vixen.Net.Physics.Tests | Input sampling, the possession pass, aim, intent, the character bridge, the motion rule and a **whole predicted tick including the native world… |
| Players: the end-to-end project (P4) | ✅ | Samples/13-ThirdPersonShooter | [29](plan/29-players-and-possession.md) **P4** landed as `Samples/13-ThirdPersonShooter` — a real project rather than a sample (`.vxproj`, `Assets/`… |
| ⚠ Three ways to write a prediction that reports agreement while the ends drift | ✅ | Core/Vixen.Net.Physics | A lesson rather than a feature, recorded in full in [README](../Core/Vixen.Net.Physics/README.md) § the prediction table: the tick must call… |
| Action-map editor + input debug panel | 🟡 | Editor/Vixen.Editor.AssetEditors | **The action-map editor is built and registered**: `InputActionsDocument` + `InputActionsView` open a `.vxinput` through `StandardEditors`, with… |
| Sensors, pen/stylus, MIDI, custom HID | ⛔ | — | `Vixen.Platform` reports none of the four |
| `Vixen.Navigation` — voxel bake, tiled mesh, A\* + funnel, crowd + RVO, off-mesh links, watershed, height detail, dynamic obstacles, sliced/jobbed queries | ✅ | Core/Vixen.Navigation | Managed, no Recast/Detour binding. 40 tests, zero steady-state allocation; consumed by `Vixen.Engine`, `Vixen.Ai.Nodes` and `Vixen.Xr` |
| Navmesh baked from a **compiled scene** | ⛔ | — | ⚠ **Blocked on a decision, not on the compiled scene format** (which exists — see §1.6). `NavMeshImporter` wants `(path, transform)` pairs; a scene… |
| `Samples/05-PlatformerGame` | ⬜ | — | Phase 8's exit criterion. Does not exist; where the *tuned* player rigs belong |
| **AI: the substrate** — the compiled blackboard, the action surface, per-agent memory, the governor, the debug record | ✅ | Core/Vixen.Ai | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P0, and the layer move that document exists to argue for: `Vixen.Ai` references `Vixen.Core`… |
| **AI: behaviour trees, runtime** — the compiler, the stepper, the node library, subtrees | ✅ | Core/Vixen.Ai | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P1. A `.vxbt` compiles to a flat array of nodes in depth-first pre-order, so **an index is a… |
| **AI: the node editor** — the `.vxbt` document, the model, the canvas, the importer | ✅ | Core/Vixen.Ai · Editor/Vixen.Editor.Ai · Editor/Vixen.Editor.AssetEditors · Editor/Vixen.Editor.Assets | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P2, and its exit criterion as one test: **thirty nodes put there by thirty gestures — saved… |
| **AI: perception** — the five senses, the perceived list, the three bounds | ✅ | Core/Vixen.Ai.Perception | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P3, and both exit criteria as numbers. **Five hundred listeners against five hundred sources… |
| **AI: nodes over the world** — movement, patrol, rotation, path existence, animation, sound | ✅ | Core/Vixen.Ai.Nodes | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P4, and its exit criterion as one test: **a guard patrols a baked navmesh, notices the player… |
| **AI: utility** — considerations, six curves, four selectors, inertia, the `.vxutility` asset and its editor | ✅ | Core/Vixen.Ai · Editor/Vixen.Editor.Ai · Editor/Vixen.Editor.AssetEditors | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P5, both exit criteria measured: **two actions within 2 % of each other over sixty seconds — 0… |
| **AI: GOAP** — world keys, conditions and effects, the bounded search, the queue, the derived viewer | ✅ | Core/Vixen.Ai · Core/Vixen.Ai.Nodes · Editor/Vixen.Editor.Ai · Editor/Vixen.Editor.AssetEditors | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P6, all three exit criteria measured: the **pear test** plans `pick-up-pear` then `eat-pear`… |
| **AI: the debugger** — the keyed overlay, breakpoints, the diagnosis over the log, the remote channel, the editor panel | 🟡 | Core/Vixen.Ai · Core/Vixen.Ai.Diagnostics · Core/Vixen.Ai.Perception · Editor/Vixen.Editor.Ai · Editor/Vixen.Editor.AssetEditors | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P7 and its live half. Both exit criteria measured: an agent with its inertia off is reported… |
| **AI: environment queries** — generators, tests with three purposes, the shared scorer, the `.vxquery` asset and its list editor | ✅ | Core/Vixen.Ai · Core/Vixen.Ai.Nodes · Core/Vixen.Ai.Diagnostics · Editor/Vixen.Editor.AssetEditors · Editor/Vixen.Editor.Assets | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P8, exit criterion measured on both halves. **"The best cover point with line of sight to the… |
| **AI: the seams twice, the sensor taxonomy and the sample** — doc 37 finished | ✅ | Core/Vixen.Ai · Core/Vixen.Ai.Nodes · Core/Vixen.Ai.Perception | [37](plan/37-ai-behaviour-trees-utility-and-goap.md)'s P9, and **the whole of doc 37 with it — all ten phases**. **`SeamTests` is a theory over… |
| `Symbol` lifted to `Vixen.Core` | ✅ | Core/Vixen.Core | Doc 37's P0 and its A-R8. It was in `Vixen.Animation.Moves` because move sets needed an interned name first; a blackboard key, a gameplay tag and a… |
| `Vixen.Vfx` — SoA storage, compiled graph, deterministic RNG, CPU jobs, `ParticleRenderFeature`, compute-shader emitter | ✅ | Core/Vixen.Vfx | 136 tests, zero-alloc frame. Three kernels emitted — initialize, update, reap — each reaching both backends under `glslc` and `spirv-val`.… |
| A `.vxvfx` is **an asset a scene names** | ✅ | Editor/Vixen.Editor.Assets · Core/Vixen.Rendering | `VfxImporter` compiles the node graph into `VfxEffectContent` at build time, and `VfxEmitter` is a `[Component] [DataContract]` with an `[AssetType]`… |
| The CPU expansion **reaches a screen** | ✅ | Raven/Library/Vfx · Core/Vixen.Engine.Renderer | `ParticleRenderFeature` had only ever been asserted about draw calls against the Null backend — no shader in the library took its three attributes.… |
| VFX **GPU dispatch** (upload, readback, reaping, indirect draw) | 🟡 | Core/Vixen.Rendering · Platform/Vixen.Vfx.Gpu.Tests | `VfxGpuSimulation` — storage, descriptors, the dispatch pair and both transfers — with the CPU/GPU agreement criterion asserted on a real device, validation-clean. **Reaping runs on the device**, as a third emitted kernel: every survivor claims the next slot with `atomicAdd` and copies itself there, so a reaping effect holds two full sets of the attribute buffers and the reap swaps which is live. `WriteDrawArguments` copies the counter's four bytes into a `DrawIndexedIndirect` command, so a draw reads its instance count from a buffer the host never sees. **Owed: nothing dispatches it, and that is four gaps rather than a missing call.** `VfxGpuSimulation` is constructed only by `Platform/Vixen.Vfx.Gpu.Tests`; `VfxExtractionSystem` contains no occurrence of "Gpu". (1) *Nothing supplies the module* — an emitted shader is per graph, `VfxImporter` "compiles and throws away" the Raven, `VfxEffectContent` carries no shader, and `Vixen.Rendering` links no compiler by design. (2) *Nothing can draw it* — the buffers are `Storage|CopySource|CopyDestination` with no `BufferUsage.Vertex`, and no `Raven/Library/Vfx` shader reads a particle from a buffer in a vertex stage. (3) *Spawning stays on the host*, so a device effect seeds through the `Upload` stall. (4) *Sub-emitters, `RecordDeaths`, `ParticleLights` and ribbons read a CPU `ParticleBuffer`*, so the selection must be an explicit opt-in with a stated refusal rather than a particle-count heuristic. ⚠ Not a gap: `WriteDrawArguments` leaves `firstInstance` a constant zero (asserted by `VfxReapTests`), so `HasDrawIndirectFirstInstance` does not apply here as it does to terrain's indirect draws. **Evidence that the backend agrees and that it ran**: `VfxBackendPictureTests` draws one graph through both backends with identical downstream code — worst channel 3/255, 25 pixels of 65 536 — and `Dispatches` on both classes is checked against `NullDevice`'s command stream by `VfxGpuDispatchTests`, because a constructed backend and a driven one are otherwise indistinguishable. ⚠ The survivors come out in an order the two backends do not share and neither promises — a particle's randomness follows its **identifier**, not its slot, which is why `VfxReapTests` compares the two as sets keyed by identifier. [README](../Core/Vixen.Vfx/README.md) |
| VFX **GPU sort** | 🟡 | Core/Vixen.Rendering · Raven/Library/Vfx | Built and device-tested, **not blocked** — the roadmap's "blocked on Raven workgroup-shared memory" is stale: `ParticleSort.rvn` uses no shared… |
| Mesh/ribbon/light renderers, custom attributes, force-field/curl-noise/collision/sub-emitter/trail updaters | 🟡 | Core/Vixen.Vfx · Core/Vixen.Rendering · Editor/Vixen.Editor.VfxGraph | **Most of this landed piecemeal and the row had not been re-read — twice now.** Built and tested: `VfxRendererKind.Mesh`/`Ribbon`/`Light`; custom… |
| Second view of one effect (shadow/reflection passes) | ⬜ | — | Expansion is CPU and once per view; the GPU path is the fix |
| **The gameplay kernel** — tags, `DefId`, definitions and their catalog, the attribute algebra, effects, requirements, the RNG, the module seam | ✅ | Gameplay/Vixen.Gameplay | [28](plan/28-gameplay-framework.md)'s **G0**, the milestone that document says is not optional. Every other `Gameplay/` library, both editor gameplay… |
| **The definition load path** — a build's labelled addresses out of `Vixen.Assets`, one catalog with its tag table baked | ✅ | Gameplay/Vixen.Gameplay.Content | [28](plan/28-gameplay-framework.md) § Definitions, the last of **G0**. `DefinitionContent.LoadAsync` finds definitions by label, reads them through… |
| **Address constants** — every address a build shipped, as C# a compiler checks | ✅ | Editor/Vixen.Editor.Assets · Tools/Vixen.Cli | The last thing [28](plan/28-gameplay-framework.md)'s **G0** owed, and what doc 28 called `Vixen.Gameplay.Generators`. It never became a project: **a… |
| **The `.vxdef` importer** — one importer, six extensions, the YAML type tag deciding | ✅ | Editor/Vixen.Editor.Assets | [28](plan/28-gameplay-framework.md) G-Q1 settled in favour of one, and `OneImporterClaimsEveryDefinitionExtension` is what stops the next definition… |
| Definitions through `Vixen.Assets`' ref-counted handles | ✂️ | — | **Refused by design rather than owed**, and [28](plan/28-gameplay-framework.md) § Definitions carries the correction: ref-counting the definitions… |
| **Items** — definitions, the sixteen-byte instance, rarity, durability, binding, affix rolling, the equip-time stat block | ✅ | Gameplay/Vixen.Gameplay.Items | [28](plan/28-gameplay-framework.md) **G1**. ⚠ **An instance is sixteen bytes and a test asserts the number** — everything an item *is* is recomputed… |
| **The container algebra** — one container type with policies, five operations, atomic transactions | ✅ | Gameplay/Vixen.Gameplay.Inventory | [28](plan/28-gameplay-framework.md) **G1**, and the part that document says "has to be exactly right because it is where duplication bugs live".… |
| **Loot tables** — weighted rows with conditions, nested tables, independent rows, pity, four distributions, salvage | ✅ | Gameplay/Vixen.Gameplay.Loot | [28](plan/28-gameplay-framework.md) **G1**. A roll is seeded from `(eventId, player)` and nothing else, so a drop is recomputable a year later. ⚠… |
| **The loot table editor** — the editable model, the flattened outline, the drop simulator | ✅ | Editor/Vixen.Editor.Gameplay.Loot | [28](plan/28-gameplay-framework.md) **G1**'s last line, whose real requirement is that the simulator runs `LootEvaluator` rather than an… |
| **Combat** — abilities, casting, channels, the global cooldown, charges, the six-stage damage pipeline, threat and taunt | ✅ | Gameplay/Vixen.Gameplay.Combat | [28](plan/28-gameplay-framework.md) **G2**'s first half. The pipeline is `Compute → Crit → Mitigate → Absorb → Apply → React`, extensible at named… |
| **Shooting** — weapons, spread, recoil, ammo and reload, falloff, penetration, the hit-claim validator, the rewind budget | ✅ | Gameplay/Vixen.Gameplay.Shooting | [28](plan/28-gameplay-framework.md) **G2**'s second half, and **G2** with it. Nothing in the hit path is new networking — prediction, the claim RPC… |
| **Progression** — XP curves, levels, talent trees, specialisations, professions, reputation | ✅ | Gameplay/Vixen.Gameplay.Progression | [28](plan/28-gameplay-framework.md) **G3**'s first half. `ProgressionState` is an `IRequirementContext`, which is what makes doc 28's own `requires:… |
| **Quests and dynamic events** — the kernel event bus, stages, ten objective types, contribution tiers, participant scaling, chains | ✅ | Gameplay/Vixen.Gameplay.Quests · Editor/Vixen.Editor.Gameplay.Quests | [28](plan/28-gameplay-framework.md) **G3**'s second half, and **G3** with it. ⚠ **The event bus went into the kernel**, closing a gap doc 28 named… |
| **Social and chat** — parties, squads and teams as one group with a policy; guilds with a tag-query permission matrix; friends, blocks, presence; channels, routing and moderation | ✅ | Gameplay/Vixen.Gameplay.Social · Gameplay/Vixen.Gameplay.Chat | [28](plan/28-gameplay-framework.md) **G4**. ⚠ **`PlayerId` went into the kernel and this is what forced it**: the spine forbids `Chat → Social`… |
| **Economy** — the ledger seam, currencies, vendors, the trade confirm-lock, mail, the auction house, the price model | ✅ | Gameplay/Vixen.Gameplay.Economy | [28](plan/28-gameplay-framework.md) **G5**, and one of the two doc-28 libraries that reach a runnable program: `Mmo.Soak` drives it through… |
| **Instances, PvP and matchmaking** — difficulty tiers, encounters, lockouts; four composable objective types; tickets, pools and two rating models | ✅ | Gameplay/Vixen.Gameplay.Instances · Gameplay/Vixen.Gameplay.Pvp · Live/Vixen.Live.Matchmaking | [28](plan/28-gameplay-framework.md) **G6**. ⚠ **A lockout reset is an absolute boundary, not a timer from whenever somebody entered**, it is issued… |
| **The world** — interaction and gathering, crafting, exploration, travel, vehicles with seats, leashing and spawning | ✅ | Gameplay/Vixen.Gameplay.Interaction · .Crafting · .Exploration · .Travel · .Movement · .Ai | [28](plan/28-gameplay-framework.md) **G7**, all six, and **none of them moves anything**. ⚠ **Movement's *transform* half is blocked on doc 16's owed… |
| **Owning** — plots, decoration, permission tiers, visitor access; collectibles, achievements, transmog, titles | ✅ | Gameplay/Vixen.Gameplay.Housing · .Collections | [28](plan/28-gameplay-framework.md) **G8**, both. ⚠ **Doc 28's "ten thousand houses are ten thousand rows, not ten thousand processes" is a claim… |
## 1.11 Editor

> **Phase 6's exit sentence is met.** The editor opens a project, imports assets, builds content,
> edits a scene, saves, and runs the game — entirely in `Vixen.Ui`, with no other toolkit anywhere in
> the dependency graph. The asset editors, the profiler, the debugger, the plugin host and the
> animation graph are all projects. What is left of that phase is **`PublishEditor`** with signing and
> notarisation, **golden screenshots** for editor layouts, and the **editor-shell performance bar**,
> which is unmeasured.
>
> ✅ **The viewport composes a real frame.** There are two presenters over the same three calls:
> `ScenePresenter` draws the editor's own instanced shapes lit by one key direction, and
> `FramePresenter` draws the scene through a real `GraphicsCompositor`. Both are reached; see the
> composed-viewport row below.

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Editor.Core` — `IEditorCommand`, `CommandStack` with merging/transactions/clean-marking, document model, `EditorProject`, `Selection<T>`, settings assets | ✅ | Editor/Vixen.Editor.Core | 48 tests incl. randomised do/undo/redo. [Editor.Core README](../Editor/Vixen.Editor.Core/README.md) |
| **One editing pipeline** — `EditTarget`, `EditProperty`, `EditValue`, `IEditMember`, `IEditProvider`, `SetValuesCommand` | 🟡 | Editor/Vixen.Editor.Core | [36](plan/36-an-extensible-editor.md) § P1. The inspector is on it (`InspectorField` *is* an `EditProperty`) and so is the gizmo, through… |
| **Built-in features register like plugins** — `PluginHost.Activate(id, name, module)`, `PluginContext.FindMenu`/`AddSubmenu` | 🟡 | Editor/Vixen.Editor.Plugin · Editor/Vixen.Editor.Blockout · Editor/Vixen.Editor.Terrain · Editor/Vixen.Editor.Water · Editor/Vixen.Editor.Scripts · Editor/Vixen.Editor.Diagnostics | [36](plan/36-an-extensible-editor.md) § P3. `Editor/Vixen.Editor.Host` is the composition root; `Vixen.Editor.App` is a library taking its modules as… |
| **The contribution registry** — `EditorRegistry`, `NewAssetKind`, `CustomInspector`, `SceneTool`, `PluginContext.Owns`/`With` | ✅ | Editor/Vixen.Editor.Core · Editor/Vixen.Editor.Plugin · Editor/Vixen.Editor.App | [36](plan/36-an-extensible-editor.md) § P2 and § D3. `OutOfTreePluginTests` compiles a plugin **at run time**, drops it in a folder, and an ordinary… |
| `Vixen.Editor.Ui` — shell, docking, command registry, menus/toolbars/context/palette as views, theming, notifications, background tasks, localisation | ✅ | Editor/Vixen.Editor.Ui | [Editor.Ui README](../Editor/Vixen.Editor.Ui/README.md). ⚠ **Localisation is no longer here**: [46](plan/46-what-an-application-needs.md) § A3 moved… |
| Editor panels written in VXML rather than in C# | 🟡 | Editor/Vixen.Editor.Ui · Editor/Vixen.Editor.App · Editor/Vixen.Editor.AssetEditors · Editor/Vixen.Editor.Profiler · Editor/Vixen.Editor.Terrain | [36](plan/36-an-extensible-editor.md) § P4 ✅ — the path is walked end to end: `binding-path`, `<PropertyField>`, `MarkupInspector.Of<T>` over a… |
| Keybinding editor UI; notification panel; ~~`Strings.Resource` generation~~ **the property it was for** | ✅ | Editor/Vixen.Editor.Ui · Editor/Vixen.Editor.App · Core/Vixen.Ui.Generators · build/ | `KeyBindingsView` and `MessageLogView` are the **only two panels the shell registers itself** — everything else is a view over something the… |
| `Vixen.Editor.Inspector` — generated drawers, attribute set, multi-object editing, `ref` accessors | ✅ | Editor/Vixen.Editor.Inspector | [Inspector README](../Editor/Vixen.Editor.Inspector/README.md) |
| Nested-object drawer / nested struct editing | ✅ | Core/Vixen.Ui.Controls.Advanced | ⚠ Set the leaf, then write each *owner* into its own owner innermost-first; `PropertyRow` carries a path where it used to carry a member |
| Multi-edit of a curve | ⬜ | — | `CurveDrawer` answers for one `AnimationCurve`. Merging twenty curves has no answer that is not a guess |
| **Asset fields — picker, type filter, drag-and-drop from the browser** | ✅ | Editor/Vixen.Editor.Inspector · Editor/Vixen.Editor.App | The drawer answers for `AssetReference` as well as `AssetId` — the type a scene actually stores. `Vixen.Core`'s `[AssetType]` is how a *runtime*… |
| `Vixen.Editor.SceneView` — viewport, gizmos, picking, camera nav, grid, outline, debug view modes, `SceneDocument`, `.vxscene` | ✅ | Editor/Vixen.Editor.SceneView | [SceneView README](../Editor/Vixen.Editor.SceneView/README.md) |
| **The composed viewport** — `FramePresenter`, `EditorWorldRenderer`, `EditorEffects`, per-pane `RenderView`, the View Mode menu | ✅ | Editor/Vixen.Editor.App · Editor/Vixen.Editor.Host | The editor compiles its own Raven variants, extracts the scene per frame and draws it through a real `GraphicsCompositor`; `EditorFrames` is the… |
| Undoable entity **create / delete / rename**, handle surviving a delete-and-undo | ✅ | Editor/Vixen.Editor.SceneView · Core/Vixen.Ecs | Five things come back: the handle (`World.TryRecreate`), the components (a scratch world — the only thing that can hold an arbitrary unconstrained… |
| Undoable **reparenting** + hierarchy drag-and-drop | ✅ | Editor/Vixen.Editor.SceneView · Editor/Vixen.Editor.App | `ReparentCommand`, and the outliner's drop wired to it through `TreeView.Moved`. ⚠ **The position is recorded as a neighbour, not an index** — an… |
| Clicking in the viewport to select | ✅ | Editor/Vixen.Editor.SceneView · Editor/Vixen.Editor.App | `EditorApplication.Configure` sets `pane.Picker` on every pane, and `SceneViewport.EndSelect` routes a band too small to be a band to `Pick` — a… |
| `ISceneWriter` / `SceneFileWriter` / scene save | ✅ | Editor/Vixen.Editor.SceneView |  |
| Play-in-editor, **in-process** (`WorldSnapshot` + `PlayModeController`, leak detection) | 🟡 | Editor/Vixen.Editor.SceneView | Restore clears first; the selection is translated through the handle table. ⚠ **This row read ✅ while Play stepped nothing at all** — `ShouldTick`… |
| Play-in-editor, **out-of-process** (`PlayerSessions`) | ✅ | Editor/Vixen.Editor.SceneView | Ports assigned by the set; a hung player is killed. **Supersedes the roadmap's "genuinely blocked" note in Phase 9** |
| `Vixen.Editor.App` / `.Host` — platform, window**s**, device, frame loop, `--frames N` | ✅ | Editor/Vixen.Editor.Host · Editor/Vixen.Editor.App | The loop, the swapchains, the windows and `--frames N` are `Vixen.Editor.Host`'s since the executable split off; `Vixen.Editor.App` is the library it… |
| Panels torn out into OS windows from the editor | ✅ | Editor/Vixen.Editor.App · Editor/Vixen.Editor.Ui | Drag a tab off the window, or **View ▸ Panels ▸ Float Panel**. Proved by `--run view.float-panel`, which opens the second window, gives it a… |
| **Project browser** (`AssetTree` + `ProjectBrowser`) — the asset database as a searchable tree over the real `Assets/` | ✅ | Editor/Vixen.Editor.Core · Editor/Vixen.Editor.App | Watched. `EditorApplication.FollowDisk` drains an `IFileWatcher` on the frame and rescans; the debounce, the atomic-save fold and the self-write… |
| **An edit made outside the editor reaches the document open on it** (`ExternalEdits`) | ✅ | Editor/Vixen.Editor.Core · Editor/Vixen.Editor.App | The last few metres of the same watcher: everything else on it read the drained list for a *count*. A `.vxcompositor` or a `.rvn` saved beside the… |
| **Import Assets / Build Content from the editor** (`ContentPipeline` on the background task manager) | ✅ | Editor/Vixen.Editor.App | The same call the CLI makes, so the two cannot produce different output for one project |
| Redraw-on-change (it redraws every frame today) | ⬜ | — | Every animation, toast expiry and task progress would have to say so, and one that forgets freezes a progress bar |
| Plugin loading (`Vixen.Editor.Plugin`, `AssemblyLoadContext`) | ✅ | Editor/Vixen.Editor.Plugin · Editor/Vixen.Editor.App | Collectible per plugin, so `Reload Plugins` picks up a rebuild without closing the project. The reason `Vixen.Editor.App` is not NativeAOT. ⚠… |
| **Project `Editor/` scripts** (`EditorScripts`, `ScriptModule`) | ✅ | Editor/Vixen.Editor.Scripts | [36](plan/36-an-extensible-editor.md) § P5. Roslyn over every `Editor/` folder in a project into one editor-only assembly, loaded through… |
| "Open project…" file dialog | ✅ | Editor/Vixen.Editor.App | `EditorProjects.PickProjectDirectory` over `platform.Dialogs` — the OS's own picker on all three desktops — behind `file.open-project`, in the menu… |
| Asset editors: texture, model, material, prefab, shader, UI, addressable groups, compositor | ✅ | Editor/Vixen.Editor.AssetEditors | Texture, model, material, sprite sheet, addressable groups, compositor, animation clip, animation graph, sequencer, audio mixer, input actions and… |
| **Sprite editor** (slice a texture into a sheet) | ✅ | Editor/Vixen.Editor.AssetEditors · Editor/Vixen.Editor.Assets | `SpriteSheetView`, a second **tab** over `TextureImportDocument` rather than a second document — a slice is rects written into the texture's own… |
| Scene and prefab editors (as asset editors) | ✅ | Editor/Vixen.Editor.AssetEditors | Authoring, save and both play-mode topologies, plus the **Compiled** tab — `CompiledSceneView` shows the archetype blocks, their entity counts, their… |
| `Vixen.Editor.Profiler` — CPU flame chart, GPU timeline, memory view, per-scene statistics | ✅ | Editor/Vixen.Editor.Profiler | [20](plan/20-editor-parity.md)'s B4. Over the sample rings the engine already keeps and the timestamp queries the RHI already has |
| `Vixen.Editor.Debugger` — frame debugger, remote inspector, device manager | ✅ | Editor/Vixen.Editor.Debugger | The other half of B4: a captured command stream, an attach to a running build, and whatever can be deployed to |
| `Vixen.Editor.AssetEditors` — the asset editors | ✅ | Editor/Vixen.Editor.AssetEditors | 84 files. [AssetEditors README](../Editor/Vixen.Editor.AssetEditors/README.md) |
| `Vixen.Editor.Testing` | ✅ | Editor/Vixen.Editor.Testing | The editor's own harness, above `Vixen.Ui.Testing` |
| **Editor modes** — `IEditorMode`, `EditorModes`, the mode bar, `SelectMode`, `PluginContext.AddMode` | ✅ | Editor/Vixen.Editor.Ui · Editor/Vixen.Editor.SceneView | [20](plan/20-editor-parity.md)'s A1 and [24](plan/24-blockout-tools.md)'s B2. A mode is "what the viewport's input means right now": an id, a title… |
| **Sub-object picking** — `SubObjectPicker`, `MeshElements`, `ISubObjectPicker`, `SceneViewport.PickSubObject` | ✅ | Editor/Vixen.Editor.SceneView | [24](plan/24-blockout-tools.md)'s B4: the ray test with a different payload rather than a new subsystem. A face is answered by a ray; a vertex and an… |
| **Snapping as one service** — `SnapContext` (element / base / modifiers), `ISceneProbe.TrySnap`, eleven commands and a Snap dropdown | ✅ | Editor/Vixen.Editor.SceneView · Editor/Vixen.Editor.App | [24](plan/24-blockout-tools.md)'s D4 and B5: an element set adding **edge** and **edge centre**, a **base** so `SnapBase.Pointer` puts the corner you… |
| **`Vixen.Geometry`** — `EditMesh`: faces over shared positions, an edge table that reports rather than refuses, face groups, corner layers | ✅ | Core/Vixen.Geometry | [24](plan/24-blockout-tools.md)'s D2 and P1–P7, all built: `MeshTopology`, `MeshSelection`, `MeshOperations` (fourteen verbs, ear clipping)… |
| **An entity carries an editable mesh** — `SceneDocument.MeshOf`, a `mesh:` key in `.vxscene`, `EditMeshCommand` | ✅ | Editor/Vixen.Editor.SceneView · Editor/Vixen.Editor.Core | P1's exit: a cube in a scene is an `EditMesh`, it saves/reloads/re-saves to identical bytes, and moving one vertex is undoable. ⚠ **The record is… |
| **Work plane** — `WorkPlane`, `SceneGrid` as a view of it, set-to-face / to-selection / to-world, offset along the normal, `]` and `[` | ✅ | Editor/Vixen.Editor.SceneView · Editor/Vixen.Editor.App | [24](plan/24-blockout-tools.md)'s D5. The floor grid is a plane you can put on a wall and build in, and every pane draws the editor's one plane. ⚠… |
| **Numeric entry during a drag** — `NumericEntry`, `TransformGizmo.Typed` | ✅ | Editor/Vixen.Editor.SceneView | Blender's `G X 5 ⏎`, which neither reference editor has. Every frame of a drag was already the pose at the grab plus a magnitude, so typing… |
| **Dimensions during a drag, the tape measure, and scale references** | ✅ | Editor/Vixen.Editor.SceneView · Editor/Vixen.Editor.App | The readout is above the middle of the pane rather than in a corner, and shows the typed text, the drag's magnitude, or a measurement. `SceneMeasure`… |
| **`Vixen.Editor.Blockout`** — the blockout mode, its element modes, its selection verbs and its geometry verbs | ✅ | Editor/Vixen.Editor.Blockout | [24](plan/24-blockout-tools.md)'s P0–P7: `BlockoutSelection` (loop, ring, grow, shrink, group, coplanar, linked, all, none, invert)… |
| **`Vixen.Editor.Terrain`** — the sculpt and foliage modes, the brush, the five panels, the layer and hole commands | ✅ | Editor/Vixen.Editor.Terrain · Editor/Vixen.Editor.Terrain.Physics | Registered through `PluginContext` like a third-party plugin, with `TerrainModuleSession` binding the modes to whichever scene is open.… |
| Editor network panel | ✅ | Editor/Vixen.Editor.Debugger · Editor/Vixen.Editor.Diagnostics | `NetworkView.vxml` — Tools ▸ Network. The claim held: the panel is a *view*, and not one line of `Vixen.Net` was widened to build it. Three panes… |
| Editor UI automation harness | ✅ | Core/Vixen.Ui.Testing · Editor/Vixen.Editor.Testing | Golden **screenshots** for editor layouts not started. `ViewportOverlayImageTests` and `ComposedPaneCaptureTests` are the pixel evidence that exists |
| `PublishEditor`, signing, notarisation, `.dmg`/AppImage/MSI | 🟡 | [build/Build.Publish.cs](../build/Build.Publish.cs) | `PublishEditor` publishes `Vixen.Editor.Host` self-contained and single-file per RID into `artifacts/publish/<rid>`, cleaning first, with optional… |
| `Vixen.Editor.NodeGraph` — model, generated registry, compiler, port typing | ✅ | Editor/Vixen.Editor.NodeGraph | [NodeGraph README](../Editor/Vixen.Editor.NodeGraph/README.md) |
| `NodeGraphView` (pan/zoom/marquee/wires/minimap/search-to-create) | ✅ | Editor/Vixen.Editor.NodeGraph | Over `NodeCanvas`. A one-way projection, rebuilt per structural change; drags write positions in place |
| Sub-graphs; undo commands; auto-layout; drag-from-port; preview layer | ✅ | Editor/Vixen.Editor.NodeGraph | Sub-graphs are **inlined**, not called. The layer draws a colour *or* a render target |
| **A shader-graph renderer that *fills* a preview thumbnail** | ✅ | Editor/Vixen.Editor.ShaderGraph · Editor/Vixen.Editor.App · Editor/Vixen.Editor.Host | **A sub-expression really is a whole shader**: `ShaderGraphPreview` copies the node and its upstream closure into a graph of its own, hangs a… |
| Selectable wires; in-place sticky-note editing; inlined-node → source-node map | ⬜ | — | Nothing in the tree names any of the three. The last is what lets a diagnostic inside a sub-graph name a node the author can select |
| Raven-span → node diagnostics mapping | ⬜ | — | `NodeDiagnostic` exists and carries no span; needs the emitter to record spans as it writes |
| `Vixen.Editor.ShaderGraph` — node library, `DynamicVector` typing, Raven emission | ✅ | Editor/Vixen.Editor.ShaderGraph | Unlit, Sprite and PBR masters. Property names are authored and live on the graph |
| The authoring surface — `.vxshadergraph`, canvas, show-generated-code, Create ▸ | ✅ | Editor/Vixen.Editor.AssetEditors | `Shading/`. Compiling runs the graph compiler **and** Raven's front end over what it emitted |
| Procedural nodes, custom-code node, Post + UI masters | ⬜ | — | Three masters shipped and no fourth or fifth; no noise, gradient or custom-code node in the library. **Preview thumbnails landed** — see the row above |
| A material that draws with a graph | ⬜ | — | The link and "Open shader graph" are live in `MaterialView`, and nothing consumes `ShaderGraphCompiler`'s Raven: turning it into the shader a… |
| `Vixen.Editor.VfxGraph` — node library + dual-target compilation | ✅ | Editor/Vixen.Editor.VfxGraph | One method produces both the CPU graph and the compute shader |
| Operator nodes, blocks for the remaining opcodes, sub-emitters/trails, live preview | 🟡 | Editor/Vixen.Editor.VfxGraph | The custom-attribute blocks landed on `[Setting]` (see §1.7's VFX row); `Rotate` is the one opcode left without a node. Still nothing: operator nodes… |
## 1.12 Networking

Phase 9 is the most complete phase in the repository — **all five exit criteria are met**.

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Vixen.Net` vocabulary, `ITransport`, `TransportConformance` suite | ✅ | Core/Vixen.Net | Time is a parameter, nothing delivered outside `Poll` |
| Transports: `Local` ✅, `NetworkSimulation` ✅, `Udp` ✅, `WebSocket` ✅, `Composite` ✅ | ✅ | Core/Vixen.Net.Transport.* | UDP has cookie-based connect, fragmentation, all four channels |
| Transport: **`Relay`** | ⛔ | — | No code anywhere. Blocked on a scope decision, not on work ([roadmap](plan/14-roadmap.md) § Phase 9): a relay *client* with no relay *server* is… |
| UDP adaptive congestion control, ack piggybacking, path MTU, DTLS | ⬜ | — | `UdpTransportOptions` caps unacknowledged datagrams, which bounds memory but is not a window that responds to loss — the loss it would respond to is… |
| `Tick`, `TickManager` (drift correction), `PacketWriter`/`Reader` (never throws) | ✅ | Core/Vixen.Net |  |
| `NetworkSession` — handshake, clock, players, reconnect window, host/offline modes | ✅ | Core/Vixen.Net |  |
| Bandwidth budgeting and priority shedding at the session layer | ⬜ | — | `BandwidthBudget` exists one layer down, in `ReplicationServer`; the session has none. The writer's overflow flag is what it would build on |
| RPC — generated senders, six pre-dispatch checks, ownership, rate limiting, manifest hash | ✅ | Core/Vixen.Net + Generators |  |
| Awaitable RPC (`CallAsync<T>` → `Task<T>`) | ✅ | Core/Vixen.Net | Doc 16 said `ValueTask<T>` and was corrected |
| Broadcasts (`IBroadcast<TSelf>`) | ✅ | Core/Vixen.Net |  |
| `NetworkRules` policy (stricter-wins composition) | ✅ | Core/Vixen.Net |  |
| `.vxnetrules` **asset** | ⬜ | — | Importer + serialised form + per-prefab reference; `NetworkRulesRegistry` is what it loads into. Was ⛔ on "the asset pipeline's half", which exists… |
| Replication — bit packing, `[Quantize]`, capture-once/copy-many, two-stage filter, ack'd baselines, shedding | ✅ | Core/Vixen.Net |  |
| Field-level delta (`DeltaCodec`) | ✅ | Core/Vixen.Net | 19.2 → 9.7 kbit/s a client on `Samples/08` |
| `SyncVar<T>` / `SyncList<T>` / `NetworkModule` | ✅ | Core/Vixen.Net.Engine | ⚠ **The dirty-marking system was missing until 2026-08-25**: `NetworkBehaviour.MarkChanged`'s own remarks said it is "called by the sync system… |
| System that marks dirty modules once a frame | ⬜ | — | `NetworkBehaviour.MarkChanged` is called by hand and no system calls it; wants the engine scheduler |
| Interest management — resolver chain, rules, `InterestGrid` (a source, not a filter), hysteresis, `NetworkLOD` rate | ✅ | Core/Vixen.Net | Two corrections to doc 16 recorded. ⚠ Built and correct but **unreached** — `InterestChain` is constructed only by tests; no sample or game wires one |
| Team / room / fog-of-war rules; resolver **composition** | 🟡 | Core/Vixen.Net(.Engine) | Composition landed: `InterestChain` takes an `IInterestSource` plus ordered `IInterestRule`s, and `InterestGrid` + `SceneInterestRule` chain distance… |
| Motion — `SnapshotBuffer`, clamped extrapolation, `NetworkTransform`, owner smoothing | ✅ | Core/Vixen.Net(.Engine) | 88 bits against 224 in memory |
| Per-axis enable, parent-relative replication | ⬜ | — | ⚠ Blocks [doc 28](plan/28-gameplay-framework.md) § Movement's *transform* half — a rider parented to a vehicle has no way to replicate |
| Networked rigid bodies, authority as a `NetworkRules` audience | ✅ | Core/Vixen.Net.Physics | Correction via velocity, not teleport |
| Lag compensation (pose ring, rewind scope, `ClampFor`) | ✅ | Core/Vixen.Net.Physics |  |
| Hit-claim message; per-bone rewind; rewind cost budget; drawing it | 🟡 | Gameplay/Vixen.Gameplay.Shooting | `HitClaim`, `HitClaimValidator` and `RewindBudget` landed with doc 28, one layer above networking. Per-bone rewind (wants animation pose history) and… |
| Networked animation (parameters reliable, state unreliable) + `NetworkBones` | ✅ | Core/Vixen.Net.Animation |  |
| Per-bone quantisation by importance; pose interpolation | ⬜ | — |  |
| Networked audio + `OwnershipClaim` | ✅ | Core/Vixen.Net.Audio |  |
| Spawn / scenes / instances (`NetworkSpawner`, prefab id = hash of **address**) | ✅ | Core/Vixen.Net.Engine | Corrects doc 16's "asset GUID" |
| Prefab registry filled from the content catalog by label | 🟡 | Core/Vixen.Net.Engine.Content | `NetworkPrefabContent.LoadAsync` fills a `NetworkPrefabRegistry` from the `networked-prefabs` label — addresses out of the content catalog… |
| Scene load/unload as session messages; client-requested spawns; `OnOwnerDisconnect` → `Despawn` | ⬜ | — | The local half is built — `NetworkSceneId`/`NetworkSceneMap` derive baked ids from the scene *name*, and `NetworkRulesRegistry.OnOwnerLeft` produces… |
| Security — validation, rate limits, closed-set deserialization, handshake hashes, `Vixen.Fuzz` | ✅ | Core/Vixen.Fuzz | 20 targets, 5 oracles, ~12.1 M cases per build in ~18 s. ⚠ **The `TookTooLong` budget now means "reproducibly slow", not "read slow once"** — a case… |
| Structure-aware mutation | 🟡 | Core/Vixen.Fuzz | `SyntaxDomain` mutates the parsed tree for `vxml` and `raven`, 1 case in 8 kept as byte havoc; the binary formats still get havoc only |
| `SharpFuzz` with real instrumentation | ⬜ | — | Targets are already `(ReadOnlySpan<byte>) -> outcome`; out-of-process is also what would name a stack overflow |
| Generated encoders pinned end to end in the wire corpus | ⬜ | — | The corpus has `packet` (the primitives) and the generator's source is pinned; the composition is not |
| Client-side prediction — input log, jitter buffer, rollback, tick-lead control, smoothing | ✅ | Core/Vixen.Net(.Engine) |  |
| Predicted spawns; running the scheduler's fixed-step group | ⬜ | — | Needs a client-allocatable id space; scheduler must be re-entrant |
| Metrics over OpenTelemetry (`NetworkMetrics` + `Vixen.Net.Telemetry`, OTLP push) | ✅ | Core/Vixen.Net.Telemetry · Live/Vixen.Live.Realm | Split so an offline game links no protobuf serializer. ⚠ **Was built but never fed until 2026-08-21**: fourteen instruments, a `Sample` whose remarks… |
| Traces (span per handshake); log bridge to OTLP; Grafana dashboard; client-side metrics | ⬜ | — | No `ActivitySource` anywhere in `Vixen.Net*` |
| Diagnostics — `BandwidthLedger` (type/field/RPC/connection), `SnapshotInspector` | ✅ | Core/Vixen.Net | Per-field costs a subtraction inside the delta encoder |
| RTT/jitter/loss graphs over time | ✅ | Editor/Vixen.Editor.Debugger | `NetworkTrend`'s ring, four lanes: round trip, jitter, resent %, lost inbound %. The two loss lanes are shares of one interval's traffic, differenced… |
| Dedicated-server variant boot path | ✅ | Tools/Vixen.App | `BuildVariant.Server` + headless platform + Null backend |
| Container image; server content profile | ✅ | Tools/Vixen.Templates | Both `vixen-game` and `vixen-mmo` ship a real chiselled `Dockerfile` (asserted by `TemplateTests`). The content build reads `VixenVariant` now… |
| **Out-of-process play mode** | 🟡 | Editor/Vixen.Editor.SceneView | `PlayerSessions` has the topology and its argument shapes, but **nothing constructs one** outside its own test — `EditorParity` still lists… |
| `ReplicationChannel` helper (ack has no transport of its own) | ⬜ | — | ⚠ **The duplication has one instance, and it argues against the helper.** `Samples/08-Multiplayer/MatchProtocol` is the only ack wiring over a… |
| `Samples/08-Multiplayer`, `09-NetworkSoak`, `10-VoiceChat` | ✅ | Samples/ | Soak: 30 min, 5 000 entities, 100 connections, 75.2 kbit/s, p99 tick 2.4 ms, 3 Gen0 |

## 1.13 Live — the online service layer (doc 27)

`Live/` is a top level: shipped and operated rather than run by a developer, and a game client may
link exactly one project in it. [Doc 27](plan/27-mmo-framework.md) § Cost sized the tier at 16
engineer-months across five milestones; **L0 through L4 have all landed**, and what is left is
editor-side (the `.vxplacement` asset). Every row below has a `Live/*/README.md` that owns its detail
and carries its ⚠ traps.

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `Live/` and `Gameplay/` as top levels, with the layer rule enforced | ✅ | build/Build.ArchitectureRules.cs | Nothing below may reference `Live/`; `Live → Tools` is one allow-listed pair, `Vixen.Live.Realm → Vixen.App` |
| `RealmSpec` — the one string a realm process boots from, argv or env | ✅ | Live/Vixen.Live.Abstractions | Hand-written `key=value` rather than JSON: the client links this transitively and is NativeAOT |
| Shard vocabulary — `ShardId`, `ShardKey`, `ShardState`, `ShardKind`, `ShardCapacity`, `RealmVersion`, `RealmEndpoint` | ✅ | Live/Vixen.Live.Abstractions | Endpoint is data, not configuration — M-Q1's relay seam is that property |
| `TransferTicket` + HMAC signer, five named refusals | ✅ | Live/Vixen.Live.Abstractions | ADR-020's ticket, checked at the door; the orchestrator mints one |
| `IRealmPlacement` — probe, start, stop, list, watch | ✅ | Live/Vixen.Live.Abstractions | `ListAsync` added to the ADR's four: reconciliation after an orchestrator restart needs it |
| `Placement.Process` — port pool, stdio lifecycle, Started/Ready/Stopped/Lost | ✅ | Live/Vixen.Live.Placement.Process | `IRealmProcessHost` is the seam that makes a fleet a unit test, as `Transport.Local` is for doc 16 |
| `Placement.Docker` — hand-written Engine API client, framed log stream, labelled containers | ✅ | Live/Vixen.Live.Placement.Docker | ADR-019's claim held: six calls, a `ConnectCallback`, no package. Reads logs and never writes — an orchestrated realm drains via its heartbeat's reply |
| `Placement.Kubernetes` — a `Pod` per realm, owner-referenced, `hostPort`, node external IP | ✅ | Live/Vixen.Live.Placement.Kubernetes | `KubernetesClient` 19.0.2 behind a six-method seam. ⚠ The only backend that overrules the realm about its own address: the realm's view is inside the… |
| Realm host — spec → session → admission → map → heartbeat, `Starting → Ready → Draining → Stopped` | ✅ | Live/Vixen.Live.Realm | `RealmApp.Run<TRealm>` rather than doc 27's `VixenApp.RunRealm`: `Vixen.App` is *below* `Live/` |
| `RealmDirectory` — ask-don't-await, drained once per update | ✅ | Live/Vixen.Live.Realm | ADR-016's rule as a type: it enforces *where the callback runs*, so Orleans plugs in behind it unchanged |
| `RealmHeartbeat` / `RealmHealth` — 2 s sample, tick p99 over a 256-tick window | ✅ | Live/Vixen.Live.Realm |  |
| Map lifetime | ✅ | Live/Vixen.Live.Realm | Deliberately thin — the map is `AppConfig.StartupScene`, and this answers only "is it up", which is what separates `Starting` from `Ready` |
| Placement — hard filters, the megaserver score, `placement explain`, `.vxplacement` weights | ✅ | Live/Vixen.Live.Orchestrator | A pure function of counts, so doc 27 § Testing's three properties run 45 000 randomised fleets in under a second. ⚠ Every placement explains itself… |
| Spawn/merge hysteresis (`MapFleet`) | ✅ | Live/Vixen.Live.Orchestrator | ⚠ Two traps the simulated traces found, both now policy fields: measure the arrival rate over the span arrivals landed in (not the nominal window)… |
| Grain contract — `IMapGrain`, `IShardGrain`, `IPlayerGrain`, `IFleetGrain`, and Orleans surrogates for the whole vocabulary | ✅ | Live/Vixen.Live.Cluster | Orleans 10.2.2, confined to `Live/` by `CheckArchitecture` and kept out of `Vixen.Live.Abstractions` — which is why the surrogates exist. ⚠ A… |
| The grains, as adapters over plain state machines | ✅ | Live/Vixen.Live.Orchestrator | `MapCoordinator`, `ShardLifecycle`, `PlayerLeaseState`. The lease's single-writer property (ADR-021) is asserted over 50 000 randomised operations |
| Silo host (`UseVixenOrchestrator`, `UseDevelopmentCluster`), self-ticking map grains | ✅ | Live/Vixen.Live.Orchestrator | Clustering is deliberately the caller's choice — ADR-016's providers tie a deployment to a target the brief keeps open |
| The realm's cluster client — heartbeat, ready, lease acquire/renew/release, roster | ✅ | Live/Vixen.Live.Realm.Cluster | A project doc 27 does not list, on `Vixen.Net.Telemetry`'s precedent: an L0 realm has no orchestrator and should not link a cluster framework. **M1… |
| `.vxplacement` importer and inspector | ⬜ | — | No `[Importer]` claims the extension; making it an addressable asset is editor-side (doc 11). The one thing `Live/` still owes. ⚠ **Not the same… |
| Transfer — the overlap protocol, every abort path, the reservation, the tick rebase | ✅ | Live/Vixen.Live.Transfer | Three state machines fed events: all eight aborts leave the player playing, and aborting a *committed* transfer is refused because that is the only… |
| The transfer oracle — four realms in one process, players walking a loop | ✅ | Live/Vixen.Live.Realm.Tests | Over a wire that misbehaves. The oracle checks after *every* step that no traveller is resident on two realms and that the world total never moves. ⚠… |
| The handoff payload, written with the replication codec | ✅ | Live/Vixen.Live.Realm | `HandoffCodec` lives in the realm, not in `Transfer`, because the codec is what needs a `World`. A payload that does not read cleanly is refused… |
| Accounts, characters, the double-entry ledger, idempotency, the schema | ✅ | Live/Vixen.Live.Persistence | Taken before L2 because nothing mints a ticket until a gate does. The world has accounts (`world/loot`, `world/vendor`), so conservation is total; ⚠… |
| Gate — sign-in, characters, catalog, `POST /play`, the WSS service plane | ✅ | Live/Vixen.Live.Gate | `GateService` holds every decision and has no ASP.NET in it. ⚠ `PlayStatus.Starting` is an answer, not an error. Ships no credential store… |
| The service plane's wire shapes, source-generated | ✅ | Live/Vixen.Live.Abstractions | In the one assembly a client may see, so the gate and the client cannot hold two shapes of `PlayResponse` |
| The client half of the service plane | ✅ | Live/Vixen.Live.Client | AOT- and trim-clean, unlike the rest of `Live/` — a phone runs this. Nothing throws for a refusal, and *unreachable* is a separate answer from… |
| Matchmaking | ✅ | Live/Vixen.Live.Matchmaking | Tickets, pools, `IMatchFunction`, and both an Elo and a TrueSkill-family rating model |
| **The gameplay ↔ persistence bridge** — the identity join, and doc 28's economy, lockouts, guilds and character state on doc 27's storage | ✅ | Live/Vixen.Live.Gameplay | The join docs 27 and 28 both assumed and neither built. Four bridges (`LedgerBridge`, `LockoutBridge`, `SocialBridge`, the profile codecs) plus the… |
| **`IAccountGrain`** — the account-scoped writer | ✅ | Live/Vixen.Live.Cluster · .Orchestrator | A grain doc 27 § Grains does not have; doc 28's G8 showed it was missing. ⚠ **Keyed by the account, not the character** — a mount earned on one… |
| **`IGuildGrain`** — one guild's roster and ranks | ✅ | Live/Vixen.Live.Cluster · .Orchestrator | Declared now that doc 28's G4 built the feature. ⚠ **The grain decides ordering; the caller decides permission** — it re-checks only the arithmetic… |
| **`IGuildRepository`** — the durable half | ✅ | Live/Vixen.Live.Persistence | Doc 27 § Persistence names it; L3 deferred it until the grain that owns the aggregate existed. ⚠ **The fence is a *revision*, not a lease epoch**… |
| **`IInstanceGrain`** — one saved instance | ✅ | Live/Vixen.Live.Cluster · .Orchestrator | ⚠ **A lockout is fleet-wide, which is why it is a grain and not a realm's table** — doc 28: *"a lockout one shard knew about is a lockout a player… |
| **`IQueueGrain`** — the matchmaking queue as a grain | ✅ | Live/Vixen.Live.Cluster · .Orchestrator | Doc 28's ticket as *"a grain-held record"*. ⚠ **Formed is not started** — a roster still needs a shard, so its tickets are held and the caller… |
| Content diff — the additive classifier, with its reasons | ✅ | Live/Vixen.Live.Orchestrator | ⚠ Deliberately pessimistic: anything it cannot decide is not additive, because calling a non-additive change additive corrupts a running world. A… |
| Rolling upgrades — drain width, the grace, `VersionSpread` | ✅ | Live/Vixen.Live.Orchestrator | ⚠ Every step it produces is a *drain*; nothing is ever killed. Emptiest shard first. A rollback restarts the grace, or it would force against the… |
| Fleet view, placement explain, `vixen live` | ✅ | Tools/Vixen.Cli | `LiveRunner` + `VixenCommand.Live`: `status` (the fleet view, as a table), `drain`, `explain` and the build half of `upgrade`, each against a real… |
| The soak | ✅ | Samples/14-Mmo/Mmo.Soak | Doc 28's exit criterion as well as doc 27's. Eight shards over three maps, five hundred connections, thirty minutes, continuous transfers and a… |
| `dotnet new vixen-mmo` / `vixen new mmo` — `.Contracts`, `.Shared`, `.Realm`, `.Client`, `.Content` | ✅ | Tools/Vixen.Templates | The first multi-project template. Doc 27's `.Cluster`, `.Orchestrator` and `.Gate` are left out: each needs a package that does not exist until L1 or… |
| `Samples/14-Mmo` — the soak, the exit criterion and the interface | ✅ | Samples/14-Mmo | Renumbered from doc 27's `13-Mmo`; thirteen is `13-ThirdPersonShooter`. **Complete**: 981 definitions and 19 scenes across six zones from a committed… |

## 1.14 Samples

Twelve numbered samples exist: `01`–`04`, `07`–`14`. `05` and `06` have no directory.

| Sample | Status | Note |
|---|---|---|
| `01-HelloTriangle` (+ `.Android`, `.iOS`) | 🟡 | Verified macOS, iOS Simulator, Android emulator. Windows/Linux and physical devices owed. ⚠ No CI leg runs it — `--vixen-frames` exists and nothing invokes it, because the Linux runner has no headless display |
| `02-HelloUi` | ✅ | Exit criteria met; no CI leg runs it; browser run is Phase 10's |
| `03-PbrShowcase` | ✅ | The standard frame's smallest complete project: seven knobs, shadows, occlusion, IBL from a baked sky — plus the terrain stack end to end, a multi-tile terrain with painted layers, a hole and a grass rule, spliced at `afterOpaque` |
| `04-EcsStressTest` | ✅ | |
| `05-PlatformerGame` | ⬜ | No directory. Phase 8's exit criterion; ⛔ on iOS by the Jolt slice |
| `06-CanvasStress` | ⬜ | No directory. P2, cut-list #4 — the editor is the application-platform proof |
| `07-AddressablesRemote` | ✅ | 144.6 KB cold → 48.6 KB update, asserted |
| `08-Multiplayer`, `09-NetworkSoak`, `10-VoiceChat` | ✅ | |
| `11-VideoPlayback` | ✅ | The half of video only a running frame exercises: three planes reaching the GPU at their own sizes, in order |
| `12-VirtualGeometry` | ✅ | The `Game` ⇄ `SceneRenderHost` join doc 22 phase 5 recorded as owed: a document-driven virtualized frame on a swapchain, presenting the visibility buffer as a debug view |
| `14-Mmo` | ✅ | 981 definitions and 19 scenes over six zones, ten projects (thirteen with tests), and the soak — the first thing that proves twenty gameplay libraries go together. ⚠ **Doc 28 has no creature type**, and the gap is structural: one needs `Items`, `Combat`, `Loot` and `Ai` at once and the spine allows only two, so it lives in the game — legal, because the spine is a rule about the libraries and not about their users. **#45** asks whether the engine should grow a `Vixen.Gameplay.Encounters`. ⚠ **Two things a game must do that nothing in the engine can do for it**: declare the composition (a `!Tag` resolves through `SerializerRegistry`, which a module initializer fills only when its assembly *loads*), and seed the composition's own tags into the catalog (`Event.Kill` is declared by `QuestModule` and mentioned by no quest file). ⚠ **A game's own definition type needs the generators named explicitly** — analyzers do not flow through a `ProjectReference`. [Its README](../Samples/14-Mmo/README.md) has the rest, including the `ReferenceTests`/`CoverageTests` holes and the `Mmo.Ui` VXML traps |
| `13-ThirdPersonShooter` | ✅ | A **project** rather than a sample: a `.vxproj` the editor opens, an `Assets/` the content build imports, and `VixenApp.Run<T>`. Doc 29's player, doc 22's virtualized path and doc 19's GI in one running frame. ⚠ Building it broke nine things in the engine and **seven of the nine produced a working program with a wrong answer rather than an error** |
## 1.15 Documentation and release (Phase 11)

| Item | Status |
|---|---|
| `docs/plan/` design record | ✅ — 46 numbered documents (00–45) plus [`plan/README.md`](plan/README.md) |
| `docs/manual/` — building a game and a server, diagnostic codes, log events, [third-party attribution](manual/third-party.md) | 🟡 (4 pages) |
| `docs/rhi-backend-mapping.md` | ✅ |
| ~~DocFX API reference~~ → generated from Roslyn symbols ([25](plan/25-documentation-generator-and-site.md), ADR-016) | ✅ ([Tools/Vixen.DocGen](../Tools/Vixen.DocGen/README.md)) — nodes are classified as what they are: controls, graph nodes, systems, shaders, components, importers, diagnostics, log events |
| vixenengine.org — the site | ✅ ([www/](../www)) — Angular 22 on xUI 2.1.0, prerendered to static files, served by nginx from a container image ([www/Dockerfile](../www/Dockerfile)), readable with JavaScript off. Landing page, per-page descriptions and canonicals, `sitemap.xml`, `robots.txt` |
| Site search (FlexSearch over API + guide) | ✅ — types, members and guide *sections*, exported at build time and read in a Web Worker; ⌘K, grouped results, kind chips, within the 300 kB / 2 MB budgets |
| Versioned docs and the release diff table | ✅ — [`docs/api-history/`](api-history/index.json) holds 0.1.0; `nuke Release` folds the baselines and writes the table into [CHANGELOG.md](../CHANGELOG.md) and `/docs/releases/<version>` |
| `vixen-mcp` + the `vixen` skill | ✅ ([www/mcp](../www/mcp/README.md), [.claude/skills/vixen](../.claude/skills/vixen/SKILL.md)) — six tools over the graph |
| Manual: getting started, per-subsystem guides, UI tutorial, Raven reference, Unity migration | 🟡 — [`docs/guide/`](guide) holds 155 pages across eleven areas. The per-type sweep is still P7, gated by `docs/DocsExempt.txt` |
| 12+ runnable samples | ✅ — 12 numbered samples (`05` and `06` are the two absent numbers), plus Android and iOS heads for `01` |
| `dotnet new` templates verified on six targets | 🟡 — five templates ship (`vixen-game`, `vixen-app`, `vixen-lib`, `vixen-mmo`, `vixen-plugin`); `vixen-tool` is the last one doc 17 names, and no template is verified on a clean machine — that needs a feed, not a template |
| `PublicAPI.Shipped.txt` freeze + API review | 🟡 — the fold is one command (`nuke Release` → `vixen-api-check --fold`, the same `Approved` path the gate uses). The reading nobody has done is now ~55 000 unshipped entries across 129 projects |
| Release automation (tag → signed builds + NuGet + GitHub Release) | 🟡 — `nuke Release` does the API fold, the graph archive and the changelog; `nuke PublishEditor` produces the bundle. Signing, notarisation, packaging and the GitHub release are not wired ([Build.Publish.cs](../build/Build.Publish.cs)) |
| 24 h editor / 24 h game soak | ⬜ — `nightly.yml` runs fuzz, properties, Postgres, Docker and Kubernetes legs, none of them a soak |
| Public triage process + compatibility policy | ⬜ |
| Third-party attribution manifest / `docs/manual/third-party.md` | 🟡 — [the page](manual/third-party.md) exists and every licence on it names the artefact it was read from (a package's own `.nuspec`, a licence text shipped inside the package, or `build/native-dependencies.json`), never a guess. It is kept true by a **gate, not a refresh step**: [`CheckAttribution`](../build/Build.Attribution.cs) runs inside `CheckFormat` (and as its own target) and fails on a pin with no row, a row with no pin, or a version that has drifted — over all 50 managed pins and both native ones. ⚠ **What the gate cannot check is the licence column itself** — that needs a network fetch, so it is verified by a person when the row is added, which is why each row carries its source. **Four findings are owed and none is this page's to close**: `Silk.NET.OpenAL.Soft.Native` 1.23.1 declares **LGPL-2.0-or-later** and ships `libopenal` in every game with sound, unrecorded in `NOTICE`; `libSDL2` **is** redistributed (via `Ultz.Native.SDL`, Zlib) where `NOTICE` and § 2.3 both say it comes from the system; four packages (`StbImageSharp`, `K4os.Compression.LZ4`, both ANTLR) ship **no licence statement at all** and are listed unresolved; and `NOTICE`'s claim that the manifest is generated by `Pack` was never true — `Pack` runs `DotNetPack` and nothing else. `NOTICE` is deliberately unmodified; the corrections it needs are listed on the page |
| Per-file SPDX enforcement in `CheckFormat` | ✅ — [`CheckLicenceHeaders`](../build/Build.cs) runs first in `CheckFormat` (milliseconds, ahead of the two minute-long `dotnet format` passes) over 4 510 files: `.cs`, `.g4`, `.vxml`, `.vcss`, `.ts`, matching the two SPDX **tags** rather than a comment syntax, since the header is `//` in three of those types, `/* */` in one and `<!-- -->` in another. ⚠ **Out of scope, deliberately**: `.rvn` (1 of 125 headed — a separate diff to read), `.csproj`/`.props`/`.targets` (3 of 421), Markdown (34 of 453), and — load-bearing — `Tools/Vixen.Templates/templates/`, whose files become a third party's source and must not carry Rikarin's copyright, plus `*.g.cs`, one of which is a source generator's output landing in someone else's compilation |

---

# Part 2 — Library inventory

## 2.1 Referenced and in use

Ground truth is [`Directory.Packages.props`](../Directory.Packages.props) — 50 pinned versions; the
plan of record is [`01-technology-decisions.md`](plan/01-technology-decisions.md).

| Package | Version | Status | Used by | Note |
|---|---|---|---|---|
| `Silk.NET.Core` | 2.23.0 | ✅ | `Vixen.Graphics` | |
| `Silk.NET.Vulkan` + `.Extensions.KHR` / `.EXT` | 2.23.0 | ✅ | `Vixen.Graphics.Vulkan` | Primary backend. `Vk.GetApi()` is never called (R11) |
| `Silk.NET.SDL` | 2.23.0 | ✅ | `Vixen.Platform.Desktop` | **SDL 2, not SDL 3** — doc 01 corrected. Bindings only; `libSDL2` comes from the system or `Platform.Native` |
| `Silk.NET.OpenGL` | 2.23.0 | ✅ | `Vixen.Graphics.OpenGL` | Desktop GL 4.5 core |
| `Silk.NET.OpenGLES` | 2.23.0 | ✅ | `Vixen.Graphics.OpenGL` | The GLES 3.0/3.2 and WebGL2 profiles — a second package because libGL and libGLESv2 are two libraries. ⚠ **No `Silk.NET.EGL` exists for 2.x** (it stops at 1.9.0), so `NativeEglApi` binds EGL itself |
| `Silk.NET.WebGPU` | 2.23.0 | ✅ | `Vixen.Graphics.WebGPU` | ⚠ Matches **no** wgpu-native release; the pin carries a refusal and a struct override |
| `Silk.NET.OpenXR` + `.Extensions.KHR` | 2.23.0 | ✅ | `Vixen.Xr.OpenXR` | Doc 14 lists VR/XR as a stretch; it landed early |
| `Silk.NET.OpenAL` + `.Extensions.*` | 2.23.0 | ✅ | `Vixen.Audio.Backend.OpenAL` | |
| `Silk.NET.OpenAL.Soft.Native` | 1.23.1 | ✅ | `Vixen.Audio.Backend.OpenAL` | OpenAL Soft's own version, unrelated to Silk.NET's |
| `Silk.NET.Assimp` | 2.23.0 | ✅ | `Vixen.Editor.Assets` | Import-time only, never in a runtime assembly. ⚠ On Linux the shipped native in `runtimes/` is not found by the Silk loader — a distribution `libassimp5` is installed by CI instead |
| `JoltPhysicsSharp` | 2.22.0 | ✅ | `Vixen.Physics` | No iOS slice — see §1.10 |
| `StbImageSharp` | 2.30.15 | ✅ | `Vixen.Editor.Assets` | **Replaced ImageSharp** (ADR-015 — 4.0.0 fails the build without a purchased key). Public domain; gained Radiance HDR, lost `.exr`/`.tif`/`.webp` |
| `ExCSS` | 4.3.2 | ✅ | `Vixen.Ui.Styling` | Spiked first. ⚠ Does not parse `@layer`; normalises through `var()` inconsistently; expands `transition`/`border-*` shorthands |
| `HarfBuzzSharp` (+ macOS/Linux/Win32 natives) | 14.2.1.1 | ✅ | `Vixen.Ui.Text` | Spiked first. ⚠ Exposes **no glyph outlines** — Vixen reads `glyf`/`CFF` itself. The WASM path is a version-coupled static link against Emscripten 3.1.56 |
| `K4os.Compression.LZ4` | 1.3.8 | ✅ | `Vixen.Core.Serialization` | |
| `ZstdSharp.Port` | 0.8.8 | ✅ | `Vixen.Core.Serialization` | Pure managed → works on WASM |
| `System.IO.Hashing` | 10.0.10 | ✅ | `Vixen.Core` | XxHash128 |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.10 | ✅ | `Vixen.Core.Diagnostics` | Interface only |
| `ZLogger` | 2.5.10 | ✅ | `Vixen.Core.Diagnostics` | The file sink only (ADR-008). Brings `Microsoft.Extensions.Logging` with it; publishes AOT with no trim warning |
| `NVorbis` | 0.10.5 | ✅ | `Vixen.Audio.Codecs` | |
| `Concentus` | 2.2.2 | ✅ | `Vixen.Audio.Codecs`, `Vixen.Video.Codecs` | Pinned to the **managed** path — the native libopus fallback ignored its bitrate |
| `OpenTelemetry` + OTLP/Console exporters + Runtime instrumentation | 1.17.0 | ✅ | `Vixen.Net.Telemetry` | Added beyond doc 01's register |
| `Microsoft.Orleans.Sdk` / `.Server` / `.Client` | 10.2.2 | ✅ | `Live/*` only | ADR-016/ADR-017. ⚠ Confined to `Live/` by an architecture rule: a cluster client handed to an untrusted machine makes every grain interface a public API. Its code generator is Roslyn, not an IL weaver, so ADR-002 survives; not NativeAOT-clean, and does not need to be |
| `KubernetesClient` | 19.0.2 | ✅ | `Vixen.Live.Placement.Kubernetes` | ADR-019's third placement backend |
| `YamlDotNet` | 18.1.0 | ✅ | `Vixen.Core.Yaml`, `Vixen.Editor.Core` | Low-level scanner/parser only; the emitter is Vixen's |
| `Npgsql` | 9.0.4 | 🟡 | `Vixen.Live.Persistence.Tests` **only** | The one database driver in the tree, and test-only by design: `Vixen.Live.Persistence` takes a `DbDataSource` so a game engine does not pin a driver |
| `Antlr4.Runtime` / `Antlr4.CodeGenerator` | 4.6.6 | 🟡 | `Vixen.Raven.Tests` **only** | Kept as a differential oracle after the Phase 5b migration |
| `Microsoft.CodeAnalysis.CSharp` / `.Analyzers` | 4.11.0 / 3.3.4 | ✅ | every `*.Generators` project, and `Vixen.Core.IO.Analyzers` | |
| `Nuke.Common` | 10.1.0 | ✅ | `build/_build.csproj` | |
| `System.CommandLine` | 2.0.10 | ✅ | `Vixen.Cli` and the tools | |
| `BenchmarkDotNet` | 0.15.8 | ✅ | `Benchmarks/*` | |
| `xunit.v3` + `runner.visualstudio` + `Microsoft.NET.Test.Sdk` | 3.2.2 / 3.1.5 / 18.8.1 | ✅ | every `*.Tests` | |
| `CsCheck` | 4.7.0 | ✅ | property-based suites | |

## 2.2 Planned, not yet referenced

Re-checked against `Directory.Packages.props`: all nine are still absent from it.

| Package | Planned for | Status | Blocks |
|---|---|---|---|
| `Silk.NET.SPIRV.Cross.Native` | `Vixen.Raven.Transpile` | ⬜ | HLSL/MSL/WGSL output (ADR-012) |
| `Silk.NET.Shaderc` / `.Native` | Raven's differential oracle | ✂️ | **Declined, and the oracle runs without it.** `SpirvDifferentialTests` compares Raven's SPIR-V against `glslc`(Raven's GLSL) over 13 fixtures; `ReferenceCompiler` finds `glslc` and `spirv-dis` on PATH, because a native NuGet asset would put shaderc's binaries in the restore graph of a project that must never ship them (doc 07 § C, doc 12 § optional tools). `ci.yml` installs them on all three legs |
| `Silk.NET.Direct3D.Compilers` | D3D12 backend | ✂️ | Postponed with the backend |
| `Silk.NET.Maths` | interop shim | ⬜ | Never needed — ADR-003 types carry their own conversions |
| `NSubstitute` 6.0.0, `Shouldly` 4.3.0 | test stack | ⬜ | Listed in doc 12; the props file deliberately omits unused versions |
| ~~`Pfim`~~ | DDS/TGA decode | ✂️ | **Not needed.** TGA is read by `StbImageDecoder`; DDS is a container over BCn, which `Vixen.Core.Imaging` already speaks, so `DdsDecoder` is a header parser and a format table rather than a codec |
| `SharpFuzz` | `Vixen.Fuzz` | ⬜ | Instrumented fuzzing alongside the build-time harness |
| `astcenc` (native) | `Vixen.Core.Imaging` | ⬜ | ASTC encoding — mobile texture budgets |
| `ispc_texcomp` (native) | `Vixen.Core.Imaging` | ⬜ | Full-quality BC7/BC6H |

## 2.3 Native dependencies

Ground truth is [`build/native-dependencies.json`](../build/native-dependencies.json), which holds
exactly two entries.

| Dependency | Status | Where |
|---|---|---|
| MoltenVK **1.4.1** (`ios-arm64` static, 431 `vk*` symbols; an optional simulator slice) | ✅ | `build/native-dependencies.json` + `Vixen.Platform.Native/build/MoltenVK.targets`. ⚠ Not 1.4.2 — deliberately, and the file says why |
| wgpu-native 0.19.4.1 (pinned + checksummed, five RIDs) | ✅ | `build/native-dependencies.json`. ⚠ The last release exporting the three entry points `Silk.NET.WebGPU` 2.23.0 calls; no `win-arm64` asset exists for it |
| Vulkan Loader + validation layers | ✅ | Developer/CI install; `VulkanLoader` probes Homebrew's path |
| lavapipe (Mesa software Vulkan) | ✅ | Linux CI leg |
| `libSDL2` | 🟡 | ⚠ **Corrected 2026-08-25: it is redistributed, not taken from the system.** `Silk.NET.SDL` 2.23.0's nuspec lists `Ultz.Native.SDL` 2.32.10 (**Zlib**) as a dependency in all five target-framework groups, unconditionally, and that package ships eleven `libSDL2` binaries. Still not in the acquisition manifest — it arrives through NuGet — so the obligation is recorded in [third-party.md](manual/third-party.md) § Native libraries that arrive through managed packages. `NOTICE` still says it comes from the system and needs the same correction |
| OpenAL Soft | ✅ | Via `Silk.NET.OpenAL.Soft.Native` |
| HarfBuzz | ✅ | Via `HarfBuzzSharp.NativeAssets.*` |
| Jolt (`JoltPhysics.Native`) | 🟡 | No iOS slice |
| `astcenc`, `ispc_texcomp`, and R10's remaining three | ⬜ | The schema exists; the entries do not |

## 2.4 Rejected / reference-only (unchanged)

`Arch` (ADR-004), `ru-ace/Flexbox` + Yoga + Taffy (ADR-006), `SignalsDotnet` (ADR-007), Stride,
PurrNet — cloned into [`references/`](../references/README.md) by hand, gitignored, built by nothing.
`BepuPhysics`, `Veldrid`, `Vortice`, `SharpDX`, `Avalonia`, WPF, ImGui,
`Mono.Cecil`/`dnlib`/`Fody`/`ILRepack` (ADR-002, enforced by `CheckArchitecture`), `R3` /
`System.Reactive` (ADR-007), `SixLabors.ImageSharp` (ADR-015 — build-breaking licence gate, swapped).
Recast/Detour is reference material only; `Vixen.Navigation` re-derives and links nothing.

---

# Part 3 — Dependency tree for the unimplemented work

Read top-down: everything in a wave has **no unmet dependency on anything else in the same wave**, so
a wave is a set of tracks that can run in parallel. An arrow `A → B` means B cannot start (or cannot
be finished honestly) until A lands.

## 3.1 The four keystones

Four items unblocked disproportionately more than anything else. **All four are built.** They are
kept here because a dependency tree with the resolved edges deleted reads as though they were never
there — but only the *unresolved* edges are still listed.

```
K1  Compiled scene + prefab content (doc 08)                                 ✅ built
    SceneAsset/PrefabAsset/SceneContent in Vixen.Engine.Scenes; SceneImporter compiles
    .vxscene/.vxprefab; the authored format lives in Vixen.Editor.Core so the viewport and
    the importer read one model. Two of its seven downstream items landed with it.
    ├──→ Prefab overrides + nested prefabs — risk R7. Format, wiring and one level of
    │      nesting built, see plan/47: four keys on SceneEntityData and PrefabOverrides,
    │      which answers "the prefab changed underneath this scene" by writing values,
    │      grafting the children the template gained, and removing nothing; the inspector
    │      marks and reverts from the claim list. Owed: model (B)
    ├──→ Networking: scene load/unload as session messages; scene-placed baked index
    ├──→ Samples/05-PlatformerGame — needs a shipped level
    └──⛔ Navigation: bake placements from a scene — NOT K1's after all. An importer can
           declare an asset GUID and cannot resolve one to a path

K2  Compute node in the compositor + GPU buffer upload/readback              ✅ built, SPENT
    ComputeRenderer, BufferUploadRenderer, BufferReadbackRenderer, authored as !Compute,
    !Upload and !Readback, so the edge that orders them is the graph's. All five downstream
    items are built.

K3  Per-OS platform assemblies, behind IPlatformSupplement                   ✅ built, SPENT
    Vixen.Platform.Windows/.Linux/.MacOS. All five downstream items built. The last of them,
    floating dock groups in OS windows, wanted multi-window + DPI rather than K3 — UiSurface
    + IUiWindowHost + Vixen.Platform.Ui, one document and N windows.

K4  Silk.NET.OpenGLES + an EGL context                                       ✅ built
    SilkGlesApi, and EglContext over a hand-loaded libEGL because there is no Silk.NET.EGL
    for Silk.NET 2. Nothing above IGlApi changed.
    └──→ All three downstream items are app-head work now, not binding work: an Android head
           that chooses GL (with its deny-list), a browser head for WebGL2, and Phase 10's
           browser exit criterion.
```

## 3.2 Wave 0 — startable today, fully parallel

Twenty-three tracks as first written; nineteen have landed. What is below the strike-through is
what is left.

| # | Track | Unblocks |
|---|---|---|
| ~~W0-1~~ | ~~**K1** — `SceneCompiler` + runtime scene/prefab asset~~ | Built. Two of its seven downstream items with it; four are owed and one turned out to be blocked elsewhere (§3.1) |
| ~~W0-2~~ | ~~**K2** — compute node + GPU buffer upload/readback~~ | Built and **spent** — all five downstream items are built |
| ~~W0-3~~ | ~~**K3** — `Vixen.Platform.Windows/.Linux/.MacOS`~~ | Built and **spent** — all five downstream items are built |
| ~~W0-4~~ | ~~**K4** — `Silk.NET.OpenGLES` + EGL~~ | Built. The three downstream items now want an app head that asks for a GL device, not a binding |
| ~~W0-5~~ | ~~`DescriptorBinding` sample type + comparison sampler (RHI)~~ | Built — `DescriptorSampleType`, translated and enforced by the WebGPU backend. WebGPU shadow maps now wait on **Raven** expressing a depth texture and a comparison sampler, since an effect's layout comes from its reflection |
| ~~W0-6~~ | ~~`Tools/Vixen.ApiCheck` + first `PublicAPI.Shipped.txt`~~ | Built; the gate is in CI. What is left is the Phase 11 reading of what it baselined |
| W0-7 | CI legs: ~~Windows/Linux Vulkan~~, NativeAOT publish, run-a-sample, WebGPU-on-lavapipe | The 3-OS test matrix is in `ci.yml` (`VIXEN_REQUIRE_VULKAN` on Linux, `VIXEN_REQUIRE_WEBGPU` on macOS). The WebGPU Linux leg is marked OWED in the workflow itself; the AOT-publish and run-a-sample legs do not exist |
| ~~W0-8~~ | ~~`UiDocument.Update` → `StyleUpdater` (incremental cascade)~~ | Built — the document records *what* changed rather than that something did |
| ~~W0-9~~ | ~~`UiDocument` "layout finished" callback~~ | Built — closes the resize lag in `ScrollView`, `TreeView`, `DataGrid`, `CodeEditor`, `NodeCanvas` and `Viewport` |
| ~~W0-10~~ | ~~Wire `LineWrapper` into `TextRun`/controls~~ | Built (`TextLayout`). The *editing* half — a caret that moves between lines — and `CodeEditor`'s own wrap are owed (#46, #47) |
| ~~W0-11~~ | ~~`Vixen.Core.Diagnostics` sinks + rate limiting~~ | Built — all five sinks, one shared `LogFilter`, `LogRateLimiter`. The downstream items want the editor UI and the inspector protocol; `RemoteSink` streams JSON lines into whatever `IRemoteLogTransport` that turns out to be |
| ~~W0-12~~ | ~~`Vixen.Editor.Plugin` (`AssemblyLoadContext`)~~ | Built — manifest, discovery, a collectible context per plugin, a registration scope that makes unloading undoing, an API baseline. `Vixen.Editor.App`'s AOT position is JIT, and it says why |
| ~~W0-13~~ | ~~`Tools/Vixen.Templates`~~ | Built — `vixen-game`, `vixen-app`, `vixen-lib`, `vixen-mmo` and now `vixen-plugin`, one file tree that `dotnet new` packs and `vixen new` embeds. `vixen-tool` is the one still owed. Phase 11's clean-machine criterion needs a feed, not a template |
| W0-14 | Pin a static `libjoltc.a` for `ios-arm64` | Physics on iOS → `Samples/05` on iOS. Still absent from `native-dependencies.json` |
| W0-15 | Add `astcenc` + `ispc_texcomp` to `native-dependencies.json` | ASTC/ETC2 · full BC7/BC6H · mobile texture budgets. Also proves R10's schema generalises. Still absent |
| ~~W0-16~~ | ~~ECS entity-handle **reservation**~~ | Built (`World.TryRecreate`), and spent: create/delete/rename are undoable in the scene view |
| W0-17 | Bindless material binding plan | **Built bar the table's pairing** — `BindlessTable`, descriptor indexing, Raven's `[Bindless]`/`[MaterialIndex]`, `GeometryBuffer`, `DrawIndexedIndirectCount` and compaction, all recorded in [plan/23](plan/23-bindless-materials.md) with the set-4 and push-constant traps. **Owed:** pairing beyond the one entry `WorldRenderer.Paired` writes — a material that renamed its map samples the fallback, and a surface declaring set 4 on a host that built no table gives a five-set layout with four bound |
| ~~W0-18~~ | ~~Light-probe exact predicates (robust Bowyer–Watson)~~ | Built and spent: `LightProbeVolume` interpolates tetrahedrally, and `ExactPredicates` is general — exact orientation and in-sphere live in `Vixen.Core.Mathematics` |
| ~~W0-19~~ | ~~`NodeGraphView` (pan/zoom/wires/minimap/search-to-create)~~ | Built. Shader-graph and VFX-graph authoring is a matter of nodes now, not of a canvas |
| ~~W0-20~~ | ~~Non-scene asset editors: texture · model · material · shader · UI · addressable groups · compositor~~ | All seven built, the UI one included (`MarkupDocument` lexes, parses and binds a `.vxml`). Owed is a **live** preview — a `.vxml` becomes a C# partial class, so running one is the hot-reload pipeline; the pane draws the static structure and says so |
| W0-21 | Relay **scope decision** (host one? in-box or addon?) | The `Relay` transport + transport fallback. No decision recorded and no code; `Vixen.Net.Transport.Relay` does not exist |
| W0-22 | `Vixen.Raven.Transpile` (SPIRV-Cross) | HLSL/MSL/WGSL targets + the cross-compilation test pass. The project does not exist |
| W0-23 | ~~CSS Grid~~ · `Canvas2D` · ~~pinch/rotate~~ · ~~multi-window + DPI~~ | `Canvas2D` is the only one left. Grid is built (`LayoutTree.Grid`/`.GridTracks`/`GridPlacement`); pinch and rotate are one two-pointer transform gesture in `Vixen.Ui/Gestures.cs`, driveable from `Vixen.Ui.Testing` |

## 3.3 Wave 1 — one dependency deep

| Track | Waits on | Note |
|---|---|---|
| ~~ECS world serialisation~~ | ~~W0-1~~ | Built — `WorldSerializer`/`WorldContent`, in `Vixen.Engine` because K1's binders are what a world is written through |
| ~~Scene + prefab **asset editors** over compiled content~~ | ~~W0-1~~ | Built — `CompiledSceneView`, a Compiled tab beside the hierarchy on both |
| Prefab overrides + nested prefabs | W0-1 | Risk R7. 🟡 Format, wiring, propagation over structure **and the inspector** — [plan/47](plan/47-prefab-overrides-and-nested-prefabs.md): four additive keys on `SceneEntityData`, `PrefabOverrides`, `PrefabReconcile`, a drop that places an instance, a serializer that round-trips the links, a reconcile at open time, add-back of template children read against the removed list, one level of nesting, and a panel whose marks and Revert item read the claim list rather than comparing values. 95 tests. Owed is model (B) |
| Navigation: placements from a compiled scene | ~~W0-1~~ | Not K1's — an importer has no GUID-to-path resolution. See §1.10 |
| Networking: scene load/unload messages, baked scene index | W0-1 | Turns "waiting for its scene" from a state into a handshake |
| ~~VFX GPU dispatch · reaping · indirect draw~~ | ~~W0-2~~ | Built, and Phase 7's exit criterion with them. ⚠ Built is not reached: nothing outside the GPU test project constructs it. The mesh, ribbon and light renderers and the remaining updaters all ship — that line was stale |
| GPU sort | — | ⚠ **Written and never dispatched.** `Vixen.Rendering/VfxGpuSort.cs` + `Raven/Library/Vfx/ParticleSort.rvn` exist and pass a device test, but the only callers are the test projects. ⚠ **A caller cannot simply be added**: the constructor takes a `VfxGpuSimulation`, which nothing outside the tests constructs either — so this is the GPU-dispatch row's four gaps, not one of its own. Not blocked on the compiler, though: workgroup-shared memory has shipped in `Culling.rvn` all along, and `ParticleSort.rvn` uses none |
| ~~`AutoExposure` wiring~~ | ~~W0-2~~ | Built — the last of W0-2's downstream work |
| ~~Editor "open project…" dialog~~ | — | Built: `PickProjectDirectory` over `platform.Dialogs`, behind `file.open-project` |
| ~~Thread affinity · thermal state · clipboard images~~ | ~~W0-3~~ | Built, each on the platforms where the OS has an answer |
| ~~Floating dock groups in OS windows~~ | ~~W0-23~~ | Built, and multi-window + DPI with it |
| Android GLES fallback + capability deny-list | ~~W0-4~~ | `Vixen.Platform.Android` names neither GL nor EGL yet — this is head work now |
| `Samples/02` in three browsers + browser smoke leg | ~~W0-4~~ | Phase 10 exit criterion, half met. The smoke leg exists (`nuke BrowserSmoke`, on the `web` leg, over CDP and **not** Playwright — see § 1.1), and it drives `Tools/Vixen.WebProbe` in **one** browser, Chrome. Three browsers is a separate claim and is owed: Firefox and WebKit speak different automation protocols, so it is not a flag on this leg. `Samples/02` in a browser at all is owed too — a web *sample* needs a backend-agnostic game plus WGSL, which the probe deliberately is not |
| WebGPU shadow maps; WebGPU Linux CI leg | ~~W0-5~~ + W0-7 | The RHI declares a sample type and the backend enforces it. The shadow map waits on Raven expressing a depth texture and a comparison sampler |
| API review pass | — | Unblocked: the gate is wired and the surface is written down. Reading ~55 000 entries and deciding which should not be `public` is the Phase 11 work |
| ASTC/ETC2 output; full-quality BC7 | W0-15 | Managed BC1/BC4/BC6H/BC7 encode is built (`BlockCompressor`); ASTC and ETC2 are enum values with no encoder. ~~Then `ktx validate` + reference-decoder verification~~ — both are wired now and found nine defects; what an encoder for the other BC7/BC6H modes would unblock is *verifying* them, since the reference reads all fourteen and Vixen writes one |
| ~~Undoable **reparenting** command + hierarchy drag-and-drop~~ | — | Built: `ReparentCommand` over `SetParentAfter`, with the outliner's drop wired through `TreeView.Moved` |
| ~~Viewport click-to-select~~ | — | Built on the CPU path — `SceneViewport.BeginSelect`/`EndSelect` → `IScenePicker.Under`. ⚠ The id-render-target path is **dead code**: `PickingRenderer` has no reference outside a doc comment and nothing assigns `SceneViewport.Picking` |
| ~~Compacted draws~~ | ~~W0-17~~ | Built — `GpuDrawArguments.Compact`, gated on no per-node contributor, which a clustered `ForwardPlus` frame no longer has |
| ~~Per-object reflection probes~~ | ~~W0-17~~ | Built — `ForwardLightingRenderFeature` picks a probe per object and writes `ProbeIndex`/`ProbeWeight` into the per-draw block; `ClusteredShading.rvn` reads them behind `UseReflectionProbe` |
| Shader-graph procedural/custom-code nodes, Post + UI masters | — | Unblocked: `NodeGraphView` is in and its preview layer draws a render target. The library ships Input/Math/Texture/Vector nodes and Unlit/Sprite/PBR masters. Previews are built — `ShaderGraphPreviewRenderer` |
| VFX-graph operator nodes, remaining opcode blocks, live preview | — | Unblocked: the GPU path landed. The view half is in; the live preview is the runtime's |
| `Relay` transport + transport fallback | W0-21 | |
| Cross-compilation test pass (ESSL/HLSL/MSL/WGSL) | W0-22 | |
| ~~`Vixen.Editor.Profiler` · `.Debugger` · editor console~~ | — | Built. The console reads `RingBufferSink` live and the profiler reads the sample rings; the GPU and memory *tracks* underneath are still owed (#10) |
| ~~Editor network panel~~ | — | Built — `NetworkView.vxml` in `Vixen.Editor.Debugger`, which already referenced `Vixen.Net` for the remote inspector's transport. No new public surface on `Vixen.Net`; see §1.11 |
| `.vxnetrules` asset | W0-1 (asset-pipeline shape) | |
| Prefab registry filled from the content catalog | W0-1 | |
| `Samples/05-PlatformerGame` | W0-1 + W0-14 | Phase 8 exit criterion |

## 3.4 Wave 2 — two or more deep

| Track | Waits on |
|---|---|
| Remote inspector attached to out-of-process players | The inspector **protocol** — the player-launch half is built and so is the sink (W0-11), which wants an `IRemoteLogTransport` to hand its bytes to |
| Deferred pipeline (shading-model dispatch, forward routing, decals) | Bindless materials (W0-17). ⚠ The G-buffer half is *not* the gap — `Deferred.rvn`/`GBuffer.rvn`/`GBufferPass.rvn` exist, `RenderStage` knows a G-buffer stage, and the standard frame already writes albedo/normal/specular beside `SceneHdr`. What no compositor declares is a deferred *shading* pass |
| ~~Volumetric fog~~ · contact shadows · light shafts · SSS blur · FSR1 | Volumetric fog is built (`VolumetricFogRenderer` + `VolumetricFog.rvn`). The rest need shaders that do not exist |
| Mesh shaders / meshlet culling | Deferred pipeline + capability flags |
| SMAA · MSAA **depth** resolve; ~~GTAO~~ · ~~SSR~~ · ~~MSAA colour resolve~~ | ⚠ This row used to strike GTAO off on the strength of `Ssao.rvn` existing, and the shader was HBAO wearing a GTAO comment — the arc integral has since been written and pinned by `GtaoImageTests`. SSR was struck off correctly: `ReflectionRenderer` is the node and standalone is its default. MSAA's **colour** resolve now runs end to end. SMAA still has no shader, and averaging a depth buffer is meaningless so its resolve is a pass of its own |
| Signing · notarisation · `.dmg`/AppImage/MSI | `nuke PublishEditor` builds the bundle; the packaging, `Sign` and `Notarize` steps are not written |
| Release automation (tag → signed builds + NuGet + Release) | Signing |
| 24 h editor / 24 h game soak | A complete editor and a complete game sample |
| Perf bars measured on the IHV matrix | Real hardware + all CI legs |
| Manual + samples + templates verified clean-machine | Effectively everything |
| Video **material** (a video lit on a mesh); XR render feature + single-pass multiview | The material system / `VK_KHR_multiview` in the RHI. Both modules themselves are ✅ |

## 3.5 Independent of everything (pure additions)

No dependency in either direction; pick any up whenever there is a gap. UDP congestion control /
ack piggybacking / path MTU / DTLS; `SharpFuzz` instrumentation and structure-aware mutation;
per-axis `NetworkTransform`; team/room/fog-of-war interest rules and resolver composition; ~~`SyncVar`
dirty-marking system~~ (built — `SyncStateSweepSystem`); `ReplicationChannel` helper; OpenTelemetry traces and the client-side metrics
route; Raven string interpolation; ~~blend shapes~~ (built — storage, import, kernel, compute scatter and
`MorphRenderFeature`; what is left is a scalar weight track on the clip format and the cluster-page
scatter); parallel asset import; ECS read/write inference
generator; `WhenAny` in coroutines; `GpuUploadRing`; transform decomposition. (Shadow caching has since been built for
both — `ShadowMapRenderer.StaticAtlas` for the cascades, `PunctualShadowRenderer.Cached` for the
lamps.)

---

# Part 4 — What is owed

Every named remainder. ⚠ **The numbers are stable identifiers and other documents
cite them** (`plan/28-gameplay-framework.md` cites item 69) — do not renumber.
Detail, evidence and history live in the linked issue and the owning module `README.md`.

| # | Subsystem | Owed | Issue |
|---|---|---|---|
| 1 | `Vixen.Core.Memory` | `GpuUploadRing` | [#145](https://github.com/Rikarin/Vixen/issues/145) |
| 2 | `Vixen.Core.Collections` | `RobinHoodDictionary`, `FixedBitSet<N>` | [#146](https://github.com/Rikarin/Vixen/issues/146) |
| 3 | `Vixen.Core.Threading` | `VIXEN_JOB_SAFETY` access declarations | [#147](https://github.com/Rikarin/Vixen/issues/147) |
| 4 | `Vixen.Core.Threading` | The scheduler *using* thread affinity — the platform half is built (K3) | [#148](https://github.com/Rikarin/Vixen/issues/148) |
| 5 | `Vixen.Core.Threading` | Job priority tier for streaming/decode | [#149](https://github.com/Rikarin/Vixen/issues/149) |
| 6 | `Vixen.Core.IO` | The synchronous-IO ban (the `System.IO.Path` half is built) | [#150](https://github.com/Rikarin/Vixen/issues/150) |
| 7 | `Vixen.Core.Reflection` | Generic *declaring* types — refused today with `VXS0201`; generic collection members are handled | [#151](https://github.com/Rikarin/Vixen/issues/151) |
| 8 | ~~`Vixen.Core.Diagnostics`~~ |  | [#152](https://github.com/Rikarin/Vixen/issues/152) |
| 9 | `Vixen.Core.Diagnostics` | UTF-8 record packing in the ring ( built: `LogRateLimiter`) | [#153](https://github.com/Rikarin/Vixen/issues/153) |
| 10 | `Vixen.Core.Diagnostics` | Memory attribution; Perfetto **protobuf** (the exporter emits Chrome JSON). GPU profiling is built and reached… | [#154](https://github.com/Rikarin/Vixen/issues/154) |
| 11 | `Vixen.Core.Imaging` | ASTC/ETC2 encoders (enum values only today); a full-quality BC7 path. Managed BC1/BC4/BC6H/BC7 encode is built… | [#155](https://github.com/Rikarin/Vixen/issues/155) |
| 14 | `Vixen.Ecs` | Read/write inference generator; `VIXEN_ECS_EVENTS` | [#156](https://github.com/Rikarin/Vixen/issues/156) |
| 16 | `Vixen.Engine` | Depth-split transform hierarchy | [#157](https://github.com/Rikarin/Vixen/issues/157) |
| 17 | `Vixen.Engine` | Doc 13's render-mode, UI-debug and streaming overlays — the frame-stats, log, console, frame-graph and GPU ones are… | [#158](https://github.com/Rikarin/Vixen/issues/158) |
| 18 | `Vixen.Engine` | `WhenAny` in coroutines | [#159](https://github.com/Rikarin/Vixen/issues/159) |
| 18b | `Vixen.Engine` | Virtual cameras: target groups, orbit recentring; and a **shipping** `ISplineSource` — the dolly track… | [#160](https://github.com/Rikarin/Vixen/issues/160) |
| 20 | `Vixen.Graphics` | Placed resources (true aliasing) | [#161](https://github.com/Rikarin/Vixen/issues/161) |
| 21 | `Vixen.Graphics.RenderGraph` | Async-compute queue scheduling — the schedule, the ownership transfers, the segmented execution, the wait values and… | [#162](https://github.com/Rikarin/Vixen/issues/162) |
| 22 | `Vixen.Graphics.Vulkan` | Swapchain acquire/present *coverage*; timeline semaphores (a capability flag only). Query pools are built, and  now… | [#163](https://github.com/Rikarin/Vixen/issues/163) |
| 23 | `Vixen.Graphics.OpenGL` | `glBindImageTexture` (storage images) — `GlDevice.Replay` throws on one today | [#164](https://github.com/Rikarin/Vixen/issues/164) |
| 24 | `Vixen.Graphics.WebGPU` | Timestamp queries — `WriteTimestamp` throws and the device refuses to create a query pool; Linux CI leg | [#165](https://github.com/Rikarin/Vixen/issues/165) |
| 26 | `Vixen.Platform.Native` | R10's remaining five native dependencies | [#166](https://github.com/Rikarin/Vixen/issues/166) |
| 27 | `Vixen.Platform.iOS` | Physical-device run; sensors, haptics, HDR layer; scene-delegate lifecycle | [#167](https://github.com/Rikarin/Vixen/issues/167) |
| 28 | `Vixen.Platform.Android` | GLES fallback + deny-list; key translation; safe-area insets; sensors; default-runtime AOT gate | [#168](https://github.com/Rikarin/Vixen/issues/168) |
| 29 | `Vixen.Platform.Web` | built as `nuke BrowserSmoke`, 37 checks, no Playwright and no npm; still owed: the `AudioWorklet` path, a browser… | [#169](https://github.com/Rikarin/Vixen/issues/169) |
| 30 | `Vixen.Assets` / pipeline | Parallel import; persisted per-entry index; the import-budget gate | [#170](https://github.com/Rikarin/Vixen/issues/170) |
| 32 | Asset pipeline | ✅ **Closed.** The `.cube` LUT importer was written, tested and unregistered, and so were the four AI importers; all… | [#171](https://github.com/Rikarin/Vixen/issues/171) |
| 33 | `Vixen.Sdk` | CLI shipped in the package; platform packaging; diagnostic file paths | [#172](https://github.com/Rikarin/Vixen/issues/172) |
| 34 | `Vixen.Cli` | Signing/notarisation/packaging; `plugin`/`tool` templates; `doctor systems` | [#173](https://github.com/Rikarin/Vixen/issues/173) |
| 36 | `Vixen.Ui.Styling` | Transform decomposition | [#174](https://github.com/Rikarin/Vixen/issues/174) |
| 37 | `Vixen.Ui.Text` | `TextEditor` model with IME + caret affinity | [#175](https://github.com/Rikarin/Vixen/issues/175) |
| 38 | `Vixen.Ui.Text` | `CVAR`, `CFF2` variation, direct `HVAR` | [#176](https://github.com/Rikarin/Vixen/issues/176) |
| 39 | `Vixen.Ui` | Rich-text runs from markup (which stretch is bold) | [#177](https://github.com/Rikarin/Vixen/issues/177) |
| 40 | `Vixen.Ui` | LIS reorder pass ( built — `Binder.BindSlot` → `ComponentEmitter` → `BuildContext.Slot`) | [#178](https://github.com/Rikarin/Vixen/issues/178) |
| 42 | `Vixen.Ui` | Per-corner radii on `DrawCommand` — the command carries one `float Radius`; four live in the `BoxStyle` side-table, and… | [#179](https://github.com/Rikarin/Vixen/issues/179) |
| 43 | `Vixen.Ui` | Computed-value stage for `line-height`/`letter-spacing`/`word-spacing`/`text-indent` | [#180](https://github.com/Rikarin/Vixen/issues/180) |
| 44 | `Vixen.Ui.Markup` | `bind:` update events; CLI path emitting generated C# to disk | [#181](https://github.com/Rikarin/Vixen/issues/181) |
| 45 | `Vixen.Ui.HotReload` | Driven against a running window | [#182](https://github.com/Rikarin/Vixen/issues/182) |
| 46 | `Vixen.Ui.Controls` | Variable-height virtualisation ( built — `AcceptsNewlines`, Up/Down, line-relative Home/End, Enter inserting `\n`… | [#183](https://github.com/Rikarin/Vixen/issues/183) |
| 47 | `Vixen.Ui.Controls.Advanced` | Undo (`CodeBuffer` states outright that it has no stack); `CodeEditor` wrap + caret blink; `AppendChild` O(n)… | [#184](https://github.com/Rikarin/Vixen/issues/184) |
| 48 | `Vixen.Ui.Testing` | Group opacity; a third finger; layout-box assertions | [#185](https://github.com/Rikarin/Vixen/issues/185) |
| 49 | `Vixen.Ui.Renderer` | Reconcile per-vertex box params with `Raven/Library/Ui`'s per-uniform ones | [#186](https://github.com/Rikarin/Vixen/issues/186) |
| 50 | Raven | String interpolation; a depth texture and a comparison sampler in the type system — what WebGPU shadow maps wait on (… | [#187](https://github.com/Rikarin/Vixen/issues/187) |
| 51 | Raven | `Vixen.Raven.Transpile`; cross-compilation pass | [#188](https://github.com/Rikarin/Vixen/issues/188) |
| 52 | Raven | `CompileShaderLibrary` Nuke target; SPDX enforcement | [#189](https://github.com/Rikarin/Vixen/issues/189) |
| 53 | Raven | Negative diagnostic fixtures ( built, in `Platform/Vixen.Raven.Gpu.Tests` — the one place the compiler and a driver… | [#190](https://github.com/Rikarin/Vixen/issues/190) |
| 54 | Raven | Stream interpolation control; per-module flat IR namespace | [#191](https://github.com/Rikarin/Vixen/issues/191) |
| 56 | `Vixen.Rendering` | Transmission — ⚠ `Raven/Library/Shading/Transmission.rvn` exists and **nothing imports it**; no shading model reaches… | [#192](https://github.com/Rikarin/Vixen/issues/192) |
| 57 | `Vixen.Rendering` | Light probes **on the GPU** — upload a volume and sample it in a shader. ⚠ The CPU `LightProbeVolume` is reached only… | [#193](https://github.com/Rikarin/Vixen/issues/193) |
| 58 | `Vixen.Rendering.PostFx` | SMAA, the MSAA **depth** resolve (, , , , , , , , ,  all built) | [#194](https://github.com/Rikarin/Vixen/issues/194) |
| 59 | `Vixen.Physics` | iOS slice; per-pair suppression; vehicles/ragdolls/soft bodies; double precision | [#195](https://github.com/Rikarin/Vixen/issues/195) |
| 60 | `Vixen.Audio` | Measured HRTF sets; per-title certification work | [#196](https://github.com/Rikarin/Vixen/issues/196) |
| 61 | `Vixen.Input` | An input debug panel; sensors/pen/MIDI/HID ( built — `InputActionsView`, registered in `StandardEditors`) | [#197](https://github.com/Rikarin/Vixen/issues/197) |
| 62 | `Vixen.Navigation` | Placements from a compiled scene — today `NavMeshImporter`'s are authored in the file and the editor bakes from the… | [#198](https://github.com/Rikarin/Vixen/issues/198) |
| 63 | `Vixen.Vfx` | A **caller** for the GPU sort — ⚠ `VfxGpuSort` + `ParticleSort.rvn` are written and device-tested, and `VfxRenderer`… | [#199](https://github.com/Rikarin/Vixen/issues/199) |
| 64 | `Vixen.Net` | `Relay` transport + fallback | [#200](https://github.com/Rikarin/Vixen/issues/200) |
| 65 | `Vixen.Net` | UDP congestion control, ack piggybacking, path MTU, DTLS | [#201](https://github.com/Rikarin/Vixen/issues/201) |
| 66 | `Vixen.Net` | Session bandwidth budgeting / priority shedding | [#202](https://github.com/Rikarin/Vixen/issues/202) |
| 67 | `Vixen.Net` | `.vxnetrules` asset — the registry it would fill is built and reached, the format and importer do not exist; a prefab… | [#203](https://github.com/Rikarin/Vixen/issues/203) |
| 68 | `Vixen.Net` | Team/room/fog-of-war rules; resolver composition | [#204](https://github.com/Rikarin/Vixen/issues/204) |
| 69 | `Vixen.Net` | Per-axis / parent-relative `NetworkTransform`; per-bone quantisation; pose interpolation | [#205](https://github.com/Rikarin/Vixen/issues/205) |
| 70 | `Vixen.Net` | Hit-claim message; per-bone rewind; rewind cost budget; rewind visualisation | [#206](https://github.com/Rikarin/Vixen/issues/206) |
| 71 | `Vixen.Net` | built, `SyncStateSweepSystem`; `ReplicationChannel` helper — ⚠ **the "every game writes the same six lines" premise has… | [#207](https://github.com/Rikarin/Vixen/issues/207) |
| 72 | `Vixen.Net` | Predicted spawns; scheduler fixed-step group | [#208](https://github.com/Rikarin/Vixen/issues/208) |
| 73 | `Vixen.Fuzz` | `SharpFuzz` instrumentation; structure-aware mutation for the *binary* formats; generated encoders end to end | [#209](https://github.com/Rikarin/Vixen/issues/209) |
| 74 | `Vixen.Net.Telemetry` | Traces; log bridge to OTLP; Grafana dashboard; client-side route | [#210](https://github.com/Rikarin/Vixen/issues/210) |
| 76 | Server variant | An engine-side container image (the `vixen-game` and `vixen-mmo` templates ship one).  — built, by group membership… | [#211](https://github.com/Rikarin/Vixen/issues/211) |
| 78 | `Vixen.Editor.Inspector` | Curve multi-edit; a *thumbnail grid* for the asset **picker**. ⚠ `ThumbnailCache`/`ThumbnailSurface` exist and are… | [#212](https://github.com/Rikarin/Vixen/issues/212) |
| 79 | `Vixen.Editor.SceneView` | **Textured** surfaces — `CompileFallback` binds one constant-colour `MetalRoughnessFeature` for every mesh. ⚠… | [#213](https://github.com/Rikarin/Vixen/issues/213) |
| 81 | `Vixen.Editor.NodeGraph` | Selectable wires; sticky-note editing; a node in two groups; inlined-node → source-node map; Raven-span diagnostics | [#214](https://github.com/Rikarin/Vixen/issues/214) |
| 82 | `Vixen.Editor.ShaderGraph` | Procedural + custom-code nodes; Post/UI masters (Unlit, Sprite and PBR ship); diagnostic mapping; an importer that… | [#215](https://github.com/Rikarin/Vixen/issues/215) |
| 83 | `Vixen.Editor.VfxGraph` | Operator nodes; a `Rotate` block; sub-emitters/trails; live preview | [#216](https://github.com/Rikarin/Vixen/issues/216) |
| 84 | Editor | Golden screenshots; `PublishEditor` with signing and notarisation; redraw-on-change; the shell perf bar; a plugin… | [#217](https://github.com/Rikarin/Vixen/issues/217) |
| 85 | Build/CI | NativeAOT leg; sample-running leg;  (built — `nuke BrowserSmoke`, on the `web` leg); 3-OS *determinism* run (the 3-OS… | [#218](https://github.com/Rikarin/Vixen/issues/218) |
| 86 | Build/CI | Per-file SPDX enforcement; third-party attribution manifest | [#219](https://github.com/Rikarin/Vixen/issues/219) |
| 87 | Samples | `05-PlatformerGame`; `06-CanvasStress`; `01` on Windows/Linux and physical devices | [#220](https://github.com/Rikarin/Vixen/issues/220) |
| 89 | `Vixen.Video` | MP4; a material; frame-accurate seek; audio-track choice; subtitles; 10-bit / BT.2020; Vorbis; >2 channels | [#221](https://github.com/Rikarin/Vixen/issues/221) |
| 90 | `Vixen.Xr` | A render feature; single-pass multiview; hand/eye tracking; passthrough; anchors. ⚠ `XrSession` has no reference… | [#222](https://github.com/Rikarin/Vixen/issues/222) |
| 88 | Docs | Manual sweep; template verification; release automation; soak tests; triage + compatibility policy | [#223](https://github.com/Rikarin/Vixen/issues/223) |

## 4.1 Owed by weight

| Bucket | Count | Comment |
|---|---|---|
| ~~Blocked on **K1** (scene format)~~ | 4 | Unblocked. Two built (world serialisation; the compiled-content asset editors); one left the bucket unbuilt — the navmesh bake is blocked on GUID-to-path resolution, not on K1 |
| ~~Blocked on **K2** (compute/readback)~~ | 0 | **Spent** — all five downstream items built |
| ~~Blocked on **K3** (per-OS assemblies)~~ | 0 | **Spent** — all five downstream items built |
| ~~Blocked on **K4** (`OpenGLES` + EGL)~~ | 0 | Built. The three are app-head work now: a head that asks for a GL device on Android or in a browser |
| Blocked on a **decision**, not code | 2 | Relay scope (W0-21); D3D12 (answered: post-1.0) |
| Blocked on **hardware or an account** | 3 | iPhone provisioning; an Android device; the IHV matrix |
| Genuinely independent | ~40 | Pick up in any order (§3.5) |
| Written but **never called** | 6 | This engine's characteristic defect, so it gets a bucket: `VfxGpuSort` (#63) · `PickingRenderer` (#79) · `CubeLutImporter` (#32) · `Transmission.rvn` (#56) · `LightProbeVolume` (#57) · `XrSession` (#90). Each compiles and is tested; none is reached from a frame or a pipeline |
| Closed since the first revision of this page | ~30 | Every one is struck through in §3.2–§3.4 and the table above; the four keystones account for fourteen of them |

---

## Appendix — headline numbers

Counts marked *(measured)* were re-counted against the tree for this revision; the rest come from
the last generator or gate run and are older.

| | |
|---|---|
| `.csproj` on disk *(measured)* | 395 — `Core` 170 · `Editor` 49 · `Gameplay` 44 · `Platform` 35 · `Live` 28 · `Samples` 28 · `Tools` 28 · `Benchmarks` 7 · `Raven` 3 · `build` 1 · doc spikes 2. Counting test siblings and generators per ADR-014, and excluding the seven `.csproj` inside `Tools/Vixen.Templates/templates/` |
| Test projects *(measured)* | 172 |
| Planned projects not created *(measured)* | `Vixen.Graphics.Direct3D12` (✂️ post-1.0), `Vixen.Net.Transport.Relay` (⛔ scope decision), `Vixen.Raven.Transpile` |
| Pinned NuGet versions *(measured)* | 50, in [`Directory.Packages.props`](../Directory.Packages.props) |
| Golden image fixtures *(measured)* | 49, in `Platform/Vixen.Graphics.Golden.Tests` |
| Guide pages written *(measured)* | 155 across eleven areas ([`docs/guide/`](guide)) |
| Plan documents *(measured)* | 46, numbered 00–45 |
| Fuzz target files *(measured)* | 11 domains under `Core/Vixen.Fuzz/Targets`; ~12.1 M cases per build in ~18 s |
| Public API surface awaiting review *(measured)* | ~55 000 `PublicAPI.Unshipped.txt` entries across 129 projects; almost nothing is shipped yet |
| Documentation graph | 3 679 types · 29 354 members, as recorded for the **0.1.0 baseline** dated 2026-07-31 ([`api-history/index.json`](api-history/index.json)). The current graph is larger and has not been re-measured |
| Conformance cases green | 534 Yoga · 22 048 UAX#14/#29 · 91 707 UAX#9 · 328/413 shaping · 100 variable-font |
| Phases complete | 0, 1, 2, 3 (bar CI legs and physical devices), 4, 5b, 6 (the exit sentence; the tooling around it is not), 9 |
| Phases partial | 5 (renderer — PostFx and D3D12), 7 (the CPU/GPU VFX criterion is met; the shader-graph preview renderer landed, and the VFX graph's live preview did not), 8 (samples), 10 (WebGPU, Video and XR landed early; deferred shading and the browser run did not) |
| Phases not started | 11 (polish and 1.0) |
| Roadmap estimate remaining | ~8–11 EM of the original ~48, concentrated in Phase 6's tooling, Phase 10's deferred shading pass, and Phase 11 |

*Generated from the documentation set and the repository as of 2026-08-18. Licensed under Apache-2.0.*
