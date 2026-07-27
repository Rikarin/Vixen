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

**Mitigation — ✅ built.** `build/native-dependencies.json` is the manifest and `nuke RestoreNativeDeps`
is the target: pinned versions, checksummed download URLs, SHA-256 verification, and a third-party
licence manifest generated from the licence text *inside the verified archive* rather than from a URL
that can change. Binaries are never committed — they land under `artifacts/`, which git already
ignores, so that is a property of the layout rather than a rule to remember. A dependency update is an
edit to one file.

Four behaviours were checked rather than assumed: a second run uses the cache, a tampered cache entry
is re-fetched rather than trusted, a wrong pin is refused with expected-and-actual and leaves no
partial file behind, and an entry path that has drifted from the release's layout fails the target
instead of silently extracting nothing.

**One dependency is in it so far** — MoltenVK for `ios-arm64`, which is what R11 needed. Jolt,
HarfBuzz, SPIRV-Cross, astcenc and Recast are entries to add, and adding one is the exercise that will
say whether the schema generalises; the `.zip` and `.tar.gz` paths exist and are so far untested by a
real dependency.

### R11 — Vulkan through Silk.NET does not survive ahead-of-time compilation *(likelihood: certain · impact: high)* — **found, not predicted**

Measured in Phase 3 by `nuke CheckAot` and `nuke CheckAotIos`, which publish every runtime assembly
ahead of time with all of them rooted.

**The engine's own code is clean, on both targets.** Every `Core/` assembly, `Vixen.Platform`,
`Vixen.Platform.Headless` and `Vixen.Graphics.Null` publish with **zero** trim or AOT warnings for
`osx-arm64` *and* for `ios-arm64` — the iOS build produces a signed-nothing `.ipa` holding a 7 MB
native binary and no managed assemblies at all. **That is this phase's headline exit criterion met for
everything except the graphics backend.**

Adding `Vixen.Graphics.Vulkan` breaks it, in **two different ways that were initially conflated**.

**On the desktop, the loader cannot work.** Six diagnostics, every one inside a dependency:

```
IL3000  Silk.NET.Core.Loader.DefaultPathResolver…  'Assembly.Location' always returns an empty string
IL3002  Silk.NET.Core.Loader.DefaultPathResolver…  'Assembly.CodeBase' throws in a single-file app
IL3002  Silk.NET.Core.Loader.DefaultPathResolver…  'DependencyContext.Default' returns null
IL3002  Microsoft.Extensions.DependencyModel…      'DependencyContext.LoadDefault' …
```

Silk.NET finds a native library by asking where its managed assembly is on disk and by reading the
dependency manifest. Under NativeAOT there is neither. Not pedantic warnings — the loader describing
its own failure mode.

✅ **Resolved, and by removing the call rather than by suppressing the warning.** All six came from
`Vk.GetApi()`, which builds Silk.NET's default context and drags `DefaultPathResolver` in with it.
`VulkanLoader` no longer calls it: it resolves the library itself through `Vixen.Platform.Native` and
constructs `Vk` over a `LamdaNativeContext`, so every entry point is a function pointer it looked up.
Rooting `Vixen.Graphics.Vulkan` in `nuke CheckAot` now reports **zero**.

⚠ **This paragraph used to predict the opposite, and the prediction was wrong.** It said a suppression
would be needed regardless, on the correct general principle that ILC's analysis is static and code
unreachable *in practice* stays reachable *in the graph*. That holds only while something still calls
the code. Nothing does now, so `DefaultPathResolver` left the graph as well. Measured both ways:
putting `Vk.GetApi()` back brings all six straight back, which is what makes this a cause and not a
coincidence. **No suppression was taken, and none is needed** — worth keeping visible, because a
justified suppression and a genuinely unreachable path look identical in a green build and are not the
same thing at all.

**On iOS, the loader is beside the point and the link fails instead.** The same six appear (as
warnings or errors depending only on our own warnings-as-errors setting, not on the platform), but
what actually stops the build is `clang++`:

```
Undefined symbols for architecture arm64:
  "_vkAllocateCommandBuffers", referenced from: _Silk_NET_…
  "_vkAllocateDescriptorSets",  referenced from: _Silk_NET_…
  … twelve of them
```

iOS links everything statically, so Silk.NET's `DllImport`s become direct symbol references that
something must satisfy at link time — and nothing does, because **MoltenVK is not being linked in**.
This is the "MoltenVK static" line in [14](14-roadmap.md)'s Phase 3 turning out to be load-bearing
rather than a note: a `DllImportResolver` cannot help here, because there is no resolution step to
intercept.

✅ **Resolved — and the interesting part is that fixing the desktop half made the link error vanish on
its own, which was a trap.** Dropping `Vk.GetApi()` removed the last direct reference to `vk*`, so
`clang++` stopped complaining and `nuke CheckAotIos` went green with MoltenVK nowhere in the binary.
A gate that had been loudly right became quietly wrong: the link no longer failed, and an application
calling Vulkan on a device would have failed at `vkCreateInstance` instead — a runtime failure on
hardware in place of a build failure on a laptop, which is strictly worse. Caught by asking the
binary (`nm` reported zero defined `vk` symbols) rather than by believing the green tick.

The real fix is three things, and each of them fails silently on its own:

1. **Link the archive.** `Vixen.Platform.Native/build/MoltenVK.targets` adds it as a `NativeReference`.
   Not ILC's `LinkerArg` — the iOS workload assembles its own `clang++` command in `_LinkNativeExecutable`
   and never reads `LinkerArg`, so flags put there are applied to nothing. Found by reading the actual
   invocation.
2. **Force it in.** A static archive contributes only what something references, and after (1) nothing
   references `vk*`. Without `ForceLoad` the archive is accepted and nothing is taken out of it.
3. **Export the symbols.** The iOS SDK links with `-exported_symbols_list` and then strips, so a
   symbol that is present but not exported is invisible to `dlsym` — and `dlsym` on the main program
   is the only way to reach a statically linked MoltenVK. All 431 entry points are read out of the
   archive with `nm` at build time and declared as `ReferenceNativeSymbol`, rather than written down,
   because a hand-kept list would be wrong on the first version bump and silent about it.

`VulkanLoader` closes it at run time: where the platform links statically it asks
`NativeLibrary.GetMainProgramHandle()` and probes for `vkGetInstanceProcAddr`, which is the one
function every Vulkan implementation must export and therefore the one that proves both (2) and (3)
happened.

**Still unproven, and stated plainly:** none of this has run on a device. What is verified is that the
symbols are defined and exported in the shipped binary (431 of them, and the binary grew from 7.9 MB
to 11.5 MB), that the gate is not vacuous, and that the runtime path asks for them the only way it
could. Whether MoltenVK then works on an iPhone is what `Vixen.Platform.iOS` and the phase's real exit
criterion are for.

**Why the distinction matters.** The first write-up of this risk named one cause and one fix. There are
two causes and two fixes, and the iOS one — ship MoltenVK as a static library and give the linker its
symbols — is a build-integration problem in `Vixen.Platform.iOS`, not a trimming problem. Designing
`Vixen.Platform.Native` as though a resolver solved both would have produced something that worked on a
laptop and failed on the device, which is the exact failure mode this phase exists to prevent.

`Vixen.Graphics.Vulkan` is now in the reference and root lists of **both** probes, so neither half can
regress without a gate going red.

**The lasting lesson is not about Vulkan.** Both halves of this risk were, at one point, green for a
reason that had nothing to do with being fixed: the desktop diagnostics would have been suppressed
rather than removed, and the iOS link stopped failing because its references disappeared rather than
because its dependency arrived. In both cases the check that settled it was asking the artefact —
`nm` on the binary, and putting the offending call back to see the warnings return — rather than
reading the build's summary line.

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
