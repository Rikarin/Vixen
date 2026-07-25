# 15 — Risks and Open Questions

## Corrections to the brief

Four things in the original brief are either factually wrong or need narrowing. Each is stated with
what is true, what the plan does instead, and what it costs.

### 1. There is no Silk.NET Metal binding — ✅ **MoltenVK approach confirmed**

> **Resolution (confirmed):** MoltenVK is the agreed path for Metal. Verified against its current
> release: **v1.4.2** (2026-07-24), a layered implementation of **Vulkan 1.4**, minimum macOS 12 / iOS
> 15, covering macOS/iOS/tvOS/Mac Catalyst and the Simulators. It consumes SPIR-V via SPIRV-Cross
> internally, which independently reinforces ADR-012. Constraints are enumerated in ADR-011 and are all
> capability-gated. One correction fell out of the verification: MoltenVK does not load Vulkan layers,
> so dev builds need the Vulkan Loader bundled — see [10](10-platforms.md).

**Brief said:** "Primary development target should be Vulkan, but OpenGL, WebGL, Metal and DirectX must
be supported as well. Use Silk.NET for the API."

**Reality:** verified against the live NuGet index — Silk.NET 2.23.0 ships Vulkan, Direct3D9/11/12,
DXGI, OpenGL, OpenGL.Legacy, OpenGLES, EGL, WebGPU, SPIRV, SPIRV.Cross, Shaderc, SDL, GLFW, OpenAL,
OpenXR, Assimp, Maths, Core. **No Metal package exists**, and there is no plausible one coming — Metal
is an Objective-C API and Silk.NET's SilkTouch generator targets C headers.

**Plan:** Metal support is delivered through **MoltenVK** (ADR-011), the Khronos-endorsed
Vulkan-on-Metal layer that ships in the Vulkan SDK and powers every Vulkan-first title on Apple
platforms. Performance is 90–95 % of native Metal, it works under iOS's no-JIT rules, and it means one
backend serves macOS and iOS.

**Cost:** MoltenVK's feature gaps become capability flags. The current documented set (ADR-011) is
narrower than commonly assumed — descriptor indexing needs Metal argument buffers and is Tier-1-limited,
buffer-device-address needs Tier 2, primitive restart cannot be disabled, pipeline-statistics queries are
unsupported, and PVRTC must be host-mapped rather than staged. None affect the P1 feature set. A native
Metal backend post-1.0 is a drop-in `IGraphicsBackend`, and Raven's SPIR-V already cross-compiles to MSL
— but it is several engineer-months, not a package reference.

### 2. "Do not use Mono" means "no Cecil" — ✅ **confirmed, resolved**

> **Resolution (confirmed):** the reading below is correct. No Mono.Cecil, no IL post-processing. The
> Mono-based WASM runtime is acceptable, so **Web stays in scope**. This is settled — treat it as a
> decision, not an open question.

**Brief said:** "Do not use Mono, stick to the .NET's source generator."

**Reality:** the .NET WASM runtime is Mono-based. `NativeAOT-LLVM` for `browser-wasm` exists but is
experimental. There is no way to run .NET in a browser today without the Mono-based runtime.

**Plan:** the constraint is read as its evident intent, and that intent is honoured completely:

| Intent | Status |
|---|---|
| No IL post-processing / Mono.Cecil weaving (what Stride's `AssemblyProcessor` does) | **Honoured — banned by ADR-002 and enforced by analyzer** |
| No embedded Mono runtime as a scripting host (Unity's old scripting backend) | **Honoured — C# via Roslyn only** |
| All metaprogramming via Roslyn source generators | **Honoured — every generator listed in [02](02-repository-layout.md)** |
| No `Reflection.Emit` / `Expression.Compile` / runtime type scanning in runtime assemblies | **Honoured — analyzer-banned** |
| The WASM SDK's choice of runtime host | **Not the engine's choice** |

The distinction that matters, stated once so it is never re-litigated: **the engine cares about the
compile-time toolchain, not the runtime host.** Cecil and IL weaving are banned because they are a
build-step choice Vixen makes and would have to maintain across six platforms, and because they break
NativeAOT and trimming. Which runtime the .NET SDK hosts our IL on is not a choice Vixen makes and has
no bearing on the engine's architecture.

The same nuance applies mildly to Android, where .NET 10 may use CoreCLR or Mono depending on publish
configuration. Because nothing in the plan weaves IL, either works, and the choice is a publish property.

**Enforcement.** Because this is now a settled constraint rather than an interpretation, it gets a
build gate rather than a convention: `CheckArchitecture` ([12](12-build-ci-and-testing.md)) fails if
`Mono.Cecil`, `dnlib`, `ILRepack`, `Fody`, or any `AfterCompile` IL-rewriting target appears anywhere in
the restore graph or the MSBuild target graph. An analyzer separately bans `System.Reflection.Emit`,
`Expression.Compile`, and `Activator.CreateInstance(Type)` in runtime assemblies. A constraint this
foundational should be impossible to violate accidentally three years from now, and the cheapest way to
guarantee that is a red build.

### 3. Yoga has no CSS Grid, and the Flexbox port is not usable as a dependency

**Brief said:** "Flexbox support — here is C# port of the Yoga engine … clone it and use it as a
reference for the implementation."

You already framed it as a reference, which is correct. Two things worth being explicit about:

- The port targets `TargetFrameworkVersion v4.6`, with `class Node`, `List<Node>` children, `class Style`,
  and boxed `Value` structs. Approximately four heap objects per node per layout. For a
  100 000-element application UI that is disqualifying. `Vixen.Ui.Layout` re-implements the algorithm over
  a struct-of-arrays store (ADR-006).
- **Yoga implements flexbox only.** CSS Grid is a separate, harder specification, and the editor's
  property panels want it. It is scheduled as its own work item and is on the cut list.

**De-risking:** Yoga's upstream conformance suite (generated from HTML fixtures rendered in a real
browser) is ported into `Vixen.Ui.Layout.Tests` **before** the implementation is written. This turns the
riskiest re-implementation in the project into a red/green loop with an authoritative oracle.

### 4. Scale

**Brief said:** an engine, an application framework, an editor, a shader language, six platforms, five
graphics backends, a CSS engine, a markup language and compiler, a reactive system, a layout engine, a
text stack, three node editors, an asset pipeline, and an addressables system.

**Reality:** ~50 engineer-months including Raven's remaining work ([14](14-roadmap.md)). Stride is a
15-year, multi-company effort; Unity is thousands of engineer-years. Vixen is smaller in scope than
either, but it is not a six-month project, and a plan that implies otherwise is not bulletproof.

**Plan:** phases each end with something runnable; the highest-risk items are front-loaded; a cut list is
written in advance. No mitigation makes the number smaller — the mitigation is that the number is known.

---

## Ranked risks

### R1 — Web platform graphics — ✅ **RETIRED by spike** *(was: likelihood high · impact high → now: low-medium · medium)*

**Original concern.** Driving WebGL2 through `Silk.NET.OpenGLES` over Emscripten's GL layer was
plausible but undocumented; `Silk.NET.Windowing` has no browser support; download size might be
prohibitive regardless.

**Resolved.** The spike was run rather than scheduled —
[`spikes/web-webgl2/RESULT.md`](spikes/web-webgl2/RESULT.md). A triangle renders from managed C# through
`Silk.NET.OpenGLES` on a real WebGL2 context, on .NET 10.0.302 + Emscripten 3.1.56 + Silk.NET 2.23.0, in
Chromium. The bridge is ~40 lines (`emscripten_GetProcAddress` via `DllImport("*")` + Silk.NET.Core's
`LamdaNativeContext`), needs no Silk.NET fork, and every P/Invoke shape the RHI requires works. Payload
floor measured at **0.93 MB Brotli** trimmed — the earlier "tens of megabytes" estimate in this plan was
wrong by an order of magnitude. The `[JSImport]` hand-binding fallback is not needed.

**What remains, and it is labour rather than uncertainty:**
- Windowing/surface/input on the web are ours to write (already assumed; `Silk.NET.Windowing` confirmed
  to have no browser TFM).
- WebGL2 has no compute → fallbacks for clustered lighting, GPU particles, GTAO, compute post FX, GPU
  culling. Already a design requirement in [06](06-rendering-pipeline.md).
- A **silent WebGL1 downgrade trap** if the emcc flags are omitted, which surfaces as a misleading
  `ArgumentOutOfRangeException` from Silk.NET's `GetShaderInfoLog`. Mitigated by a post-context
  `GL_VERSION` assertion, wrapped info-log getters, and shipping the flags in
  `Vixen.Platform.Web`'s `.targets`. Documented in the spike write-up so nobody rediscovers it.
- Threads need COOP/COEP; the single-threaded job mode is already a CI leg.

**Consequence for the plan.** Web stays in scope with more confidence, and it stays last on the cut list
rather than first — but the reason to cut it would now be *effort*, not *feasibility*.

### R2 — Raven maturity *(was: medium · high → now: medium · medium)*

Raven has a strong parser and no semantic analysis, IR, or codegen. The engine's renderer (Phase 5)
cannot ship without shader compilation, and `compose` (shader-typed members with compile-time
resolution) is a non-trivial semantic feature the material system depends on absolutely.

**Mitigation, as decided:**

- **GLSL and SPIR-V are built in the same Raven phase** ([07](07-raven-shader-pipeline.md)), which
  supersedes both the reorder recommendation and the `shaderc` bridge that replaced it. The renderer gates
  on *one* codegen phase. No bridge, no dual-backend abstraction, no swap-over milestone, and no
  lossy intermediate — subgroup ops, `float64`, and mesh shaders are available as soon as the emitter
  supports them.
- The engine consumes Raven through **one interface with a stub implementation**, so Phases 0–4 use
  checked-in SPIR-V blobs for the triangle and UI shaders and are not blocked at all.
- The two engine-critical Raven semantic features (**`compose`** and **permutation constants**) are named
  explicitly in [07](07-raven-shader-pipeline.md) so they can be prioritised inside Raven's Phase 2.
- The GLSL emitter must be **Vulkan-flavoured** (explicit `layout(set, binding)`) and reflection must come
  from the semantic phase, never from emitted code. Under the bridge this was mandatory because GLSL was a
  production path; now it is what enables the **differential oracle** — Raven's SPIR-V compared against
  `shaderc`(Raven's GLSL), which is the strongest correctness signal available and is free once both
  emitters exist.

**Residual risk:** the remaining exposure is simply that Raven's semantic phase and codegen are unbuilt
work on the engine's critical path for Phase 5, with `compose` the single most important feature in it.
The mitigations above are structural rather than contingent — there is no interim compromise to unwind
later, which is a better position than the bridge left us in.

### R3 — UI framework scope *(likelihood: high · impact: high)*

Phase 4 is 7 EM and contains five substantial subsystems. Browser engines have hundreds of
engineers on the equivalent. Text and CSS are both deep specifications where "90 % done" means "visibly
broken for a lot of users".

**Mitigation:** a written, enforced supported-subset for VCSS ([09](09-ui-framework.md)) — the *unsupported*
list is as important as the supported one. External conformance suites (Yoga, UAX#14, UAX#29, WPT Grid)
as the gates. `Vixen.Ui.Layout`/`.Styling`/`.Text`/`.Markup` are separate projects with separate gates, so
progress is visible and a slip is localised. The **editor shell** gate ensures the framework is
validated against a genuinely demanding application rather than a demo.

### R4 — Zero-allocation discipline erodes *(likelihood: high · impact: medium)*

It always does. One `List<T>` in an extraction loop, one closure in a query, one `string.Format` in a
log, and the promise is quietly gone. It is usually discovered at ship time.

**Mitigation:** the allocation gates in [12](12-build-ci-and-testing.md) are **ordinary tests that run on
every PR**, and they name the allocating call site via a `GCHeapAllocationEventSource` listener. A red
build on the PR that introduces it is the only mechanism that works.

### R5 — Six-platform maintenance cost *(likelihood: certain · impact: medium)*

Each platform is a permanent tax: SDK updates, OS releases, driver regressions, store-policy changes.
Six platforms × five backends is thirty combinations, and nobody has thirty CI runners.

**Mitigation:** lavapipe makes Vulkan CI-testable without a GPU. The Null backend makes RHI logic
testable without any driver. Real-hardware testing is a nightly + pre-release activity with a documented
device matrix, not a per-PR one. Backends have explicit, published support tiers: Vulkan (tier 1,
gated on every PR), D3D12 (tier 1), GL/GLES (tier 2, gated nightly), WebGPU (tier 3, best effort).
Publishing the tiers sets expectations instead of implying uniform quality.

### R6 — Editor-in-own-UI bootstrap *(likelihood: medium · impact: medium)*

If `Vixen.Ui` is not good enough by Phase 6, the editor is unbuildable and the temptation to reach for
Avalonia becomes overwhelming — and taking it kills the framework's reason to be good.

**Mitigation:** ImGui scaffold for Phases 1–5 with a **recorded deletion date** (end of Phase 6).
`Samples/02-HelloUi` proves the framework standalone before the editor depends on it. The `Advanced`
controls that the editor needs most (`DockingHost`, `TreeView`, `PropertyGrid`) are in Phase 4e, ahead of
the shell. If the framework is genuinely not ready at Phase 6, the correct response is to extend Phase 4,
not to fork the UI stack.

### R7 — Prefab overrides and nested prefabs *(likelihood: medium · impact: medium)*

Unity took roughly a decade to get nested prefabs and override propagation right, and the result is still
the source of a large share of user confusion. The plan lists it in Phase 2 as a bullet.

**Mitigation:** ship a **restricted model in 1.0**: prefab instances with a sparse property-override list,
single-level nesting, no prefab variants. Document the restriction. Full nesting and variants are post-1.0,
designed against real user projects rather than speculation. Over-designing this early is a classic sink.

### R8 — Source generator IDE performance *(likelihood: medium · impact: medium)*

Nine or more generators running on every keystroke can make the IDE unusable, and the failure mode is
"the whole team hates working on the repo".

**Mitigation:** every generator is `IIncrementalGenerator` with properly-cached pipeline stages, no
filesystem access outside `AdditionalFiles`, and no `Compilation`-wide symbol walks in the hot path.
Generator throughput is benchmarked (`Benchmarks/`) and gated. Code review checklist item. If the VXML
generator becomes the bottleneck, an out-of-band pre-generation step (`vixen import` writing `.g.cs` to
disk) is the escape hatch — a documented, designed-for fallback rather than an emergency.

### R9 — Determinism across platforms *(likelihood: medium · impact: low-medium)*

Content-build determinism is asserted in CI, which is good. Simulation determinism across architectures
(x64 vs arm64 floating point) is much harder and is often assumed rather than verified.

**Mitigation:** the plan does not promise cross-architecture float bit-identity ([10](10-platforms.md)).
Deterministic simulation, where required, uses fixed-point or a documented deterministic subset. This is
stated so nobody builds lockstep netcode on a false assumption.

### R10 — Third-party native dependency drift *(likelihood: medium · impact: low)*

MoltenVK, Jolt, HarfBuzz, SPIRV-Cross, astcenc, Recast — six native dependencies × ten RIDs, each with
its own release cadence, licence, and build requirements.

**Mitigation:** `Vixen.Platform.Native` owns all of it: pinned versions, checksummed download URLs,
SHA-256 verification, a generated third-party licence manifest, and one Nuke target
(`RestoreNativeDeps`). Binaries are never committed. A dependency update is a single reviewed PR touching
one manifest.

---

## Decision register — ✅ all resolved

Every question raised by this plan has been answered. Kept with strikethrough and the resolution rather
than deleted, so the reasoning behind each decision stays discoverable and numbering stays stable.

| # | Question | Resolution |
|---|---|---|
| ~~Q1~~ | ~~Is the "no Mono" reading in §2 acceptable, or must Web be cut?~~ | ✅ **Resolved.** No Cecil, no IL weaving; the Mono-based WASM runtime is fine. Web stays in scope with the limits in [10](10-platforms.md). Enforced by a `CheckArchitecture` gate. |
| ~~Q2~~ | ~~Licence?~~ | ✅ **Resolved: Apache-2.0.** ADR-015 records the obligations (NOTICE, SPDX headers, third-party manifest) and a full dependency audit. One finding: **ImageSharp 4.0.0 is the Six Labors Split License, not Apache-2.0** — mitigated by confining it to `Vixen.Editor.Assets` behind `IImageDecoder`, so no shipped game links it. |
| ~~Q3~~ | ~~Target audience priority: game developers, or application developers?~~ | ✅ **Resolved: game developers are primary; the editor is the first large-scale application-platform consumer.** [00](00-vision-and-principles.md) now states the ordering and its consequence — the UI framework is scoped by what the editor needs, and `Samples/06` is demoted from phase gate to P2. |
| ~~Q4~~ | ~~Is D3D12 genuinely wanted, or is Vulkan-on-Windows sufficient?~~ | ✅ **Resolved: postponed past 1.0, but the RHI is designed for it.** Five concrete measures in ADR-001, chiefly specifying barriers against Vulkan `synchronization2` so D3D12 *Enhanced Barriers* map directly. Stub project reserved from Phase 1. Saves ~1 EM; abstraction-validator role passes to OpenGL, a stricter test. |
| ~~Q5~~ | ~~Editor as the only app head, or a separate "player" runtime?~~ | ✅ **Elaborated and decided in [17](17-app-heads-and-shipping.md): no prebuilt player binary.** The game *is* a normal .NET executable (Stride's model, not Unity's), with a one-line `VixenApp.Run<TGame>()` host and a `vixen build` wrapper that makes it feel one-click. Five build variants incl. a **dedicated server**. Sub-decisions Q5a–d all resolved there: one project for client+server, loose-content flag permitted in Release with loud signposting, a `Dockerfile` template rather than container tooling, and a `vixen-tool` headless head. |
| ~~Q6~~ | ~~Console platforms ever?~~ | ✅ **Resolved: not currently planned.** No console work, no NDA'd SDKs, no console-shaped compromises in the abstraction. `Vixen.Platform`'s contracts and the capability-gated RHI happen to leave the door open at no cost, which is the correct amount of investment in a maybe. |
| ~~Q7~~ | ~~Networking/multiplayer — in scope for 1.0?~~ | ✅ **Resolved: in scope**, designed in [16](16-networking.md) against PurrNet as reference. Server-authoritative + interpolation + lag compensation; client-side prediction is P2 but designed for. New Phase 9, **+5.0 EM**. |
| ~~Q8~~ | ~~Team size and timeline expectation?~~ | ✅ **Resolved: one person, AI-assisted.** Phase order stays sequential (already was). [14](14-roadmap.md) gains a *Delivering this solo* section: four publishable milestones (M1 "it runs" → M2 "it is a game engine" → M3 "it has an editor" → M4 1.0), a recommendation to **swap Phases 4 and 5** so a working renderer ships before the 7 EM UI investment, and the practices that matter most without a reviewer. |
| ~~Q9~~ | ~~Should Vixen aim for Unity project *import*?~~ | ✅ **Resolved: no. No Unity compatibility is expected or attempted.** Unity is an implementation *reference* only; Vixen does not adhere to Unity's standards, formats, or conventions where its own judgement differs. This retroactively removes the last constraint from ADR-005 and closes the question permanently. |
| ~~Q10~~ | ~~Raven phase reorder (SPIR-V before GLSL)?~~ | ✅ **Superseded: neither. GLSL and SPIR-V land in the same phase.** Order becomes `Semantic → IR → GLSL + SPIR-V → CLI → Interaction classes`. This removes the `shaderc` bridge, `IRavenBackend`, the dual-codegen test burden, and GLSL's lossiness — and creates a **differential oracle** (Raven SPIR-V vs `shaderc`(Raven GLSL)). Requirements retained: Vulkan-flavoured GLSL, reflection from the semantic phase. See [07](07-raven-shader-pipeline.md). |

## Where this plan could be wrong

Stated plainly, because a plan that claims no uncertainty is not credible:

- **The `Behavior`-on-ECS performance claim** ([04](04-ecs-and-scripting.md)) is designed but unproven.
  Bucketed monomorphic dispatch over contiguous instances *should* devirtualise and be cache-friendly.
  It needs a spike in Phase 2 to confirm, and if the numbers disappoint, the honest response is to
  document `ISystem` as the performance path more loudly rather than to chase the last 20 %.
- **The signal graph's per-frame effect flush** may interact badly with layout in ways that only appear
  with a real UI — e.g. an effect that changes a style, triggering layout, triggering a measure, which
  reads a signal and dirties another effect. The runaway detector catches the pathological case; the
  merely-slow case needs `Samples/06` to surface it.
- **The 16 KB chunk size and the four-descriptor-set convention** are inherited defaults (from Arch and
  Stride respectively). Both are probably right and neither has been measured for Vixen's workloads.
- **Perceptual golden-image comparison** trades false-negative risk for maintainability. Some real
  regressions will slip through a tolerance threshold. The alternative (bitwise) is unmaintainable across
  five backends and three driver stacks, so this is the right trade — but it is a trade.
- **7 EM for the UI framework** is the estimate I have least confidence in. It could be 10.
