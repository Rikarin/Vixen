# 01 — Technology Decisions

## Platform baseline

| Item | Choice | Note |
|---|---|---|
| SDK | .NET 10 (`10.0.301` present on this machine) | `global.json` pins `rollForward: latestFeature` |
| TFM (runtime libs) | `net10.0` | single TFM; no netstandard, no multi-targeting |
| TFM (mobile) | `net10.0-android`, `net10.0-ios` | only for `Platform/*` and app heads |
| TFM (web) | `net10.0` + `Sdk="Microsoft.NET.Sdk.WebAssembly"` | **not** `net10.0-browser` — verified against the `wasmbrowser` template. Plus `WasmBuildNative=true` and the emcc GL flags; see [spikes/web-webgl2](spikes/web-webgl2/RESULT.md) |
| Language | C# 14, `LangVersion=latest` | see [00](00-vision-and-principles.md) for the subset that matters |
| Solution format | `Vixen.slnx` (.NET 10 XML solution) | plus per-area `.slnf` filters for fast IDE loads |
| Package management | Central Package Management (`Directory.Packages.props`) | `ManagePackageVersionsCentrally=true`, no floating versions |

## Dependency register

Versions verified against `api.nuget.org` at plan time. These go verbatim into
`Directory.Packages.props`. Anything not on this list requires an ADR.

### Runtime — always loaded

| Package | Version | Used by | Why this and not X |
|---|---|---|---|
| `Silk.NET.Core` | 2.23.0 | `Vixen.Graphics` | The only maintained, complete, AOT-friendly .NET binding set for Vulkan/D3D12/GL/WebGPU. Veldrid is unmaintained; SharpDX is dead; hand-binding Vulkan is a year of work. |
| `Silk.NET.Vulkan` (+ `.Extensions.KHR`, `.EXT`) | 2.23.0 | `Vixen.Graphics.Vulkan` | Primary target. |
| `Silk.NET.SDL` | 2.23.0 | `Vixen.Platform.Desktop` | Windowing/input/gamepad on all three desktops + surface creation for Vulkan/GL. Chosen over GLFW because SDL covers gamepads, haptics, IME, clipboard, and mobile. ⚠ **This is SDL 2, not SDL 3** — an earlier draft of this row said SDL 3 and was wrong. Silk.NET 2.x binds SDL 2 (verified: `CreateWindow` takes SDL 2's six arguments); SDL 3 bindings exist only in the Silk.NET 3.0 previews. Costs us `SDL_ShowOpenFileDialog`, which is why file pickers are owed to the per-OS assemblies. **Bindings only** — there is no `Silk.NET.SDL.Native` package, so `libSDL2` comes from the system or from `Vixen.Platform.Native`. **Not usable on web** — `Silk.NET.Windowing` has no browser TFM (verified), so `Vixen.Platform.Web` implements the surface itself. |
| `Silk.NET.OpenGLES` | 2.23.0 | `Vixen.Graphics.OpenGL` (GLES + WebGL2 profiles) | Verified driving real WebGL2 from `browser-wasm` via `LamdaNativeContext` + `emscripten_GetProcAddress`; trims to 25 KB Brotli. See [spikes/web-webgl2](spikes/web-webgl2/RESULT.md). |
| `Silk.NET.Maths` | 2.23.0 | interop shim only | **Not** the engine math type. See ADR-003. |
| `Silk.NET.Assimp` | 2.23.0 | `Vixen.Editor.Assets` (import-time only) | Model import. Never referenced by runtime assemblies. |
| `JoltPhysicsSharp` | 2.22.0 | `Vixen.Physics` | As specified. Modern, actively maintained, deterministic-capable, native binaries for all six targets. |
| ~~`SixLabors.ImageSharp`~~ → `StbImageSharp` | 2.30.15 | **`Vixen.Editor.Assets` only** | **Changed when it was built.** ImageSharp 4.0.0 *fails the build* without a purchased licence key — an error out of its own targets file, not a warning. An Apache-2.0 engine cannot require a contributor to buy a key to compile the editor, and pinning to the 3.1.x line to dodge it means sitting on a branch that gets no further security fixes. Took mitigation 2 below: StbImageSharp is public domain and reads PNG/JPEG/BMP/TGA/PSD/GIF **and Radiance HDR**, which is more of doc 08's table than ImageSharp reached. Still editor-only, behind `IImageDecoder`. **Owed:** `.exr`, `.tif`, `.webp`, `.dds` — doc 01 names Pfim (MIT) for the last of those. |
| `ExCSS` | 4.3.2 | `Vixen.Ui.Styling` | As specified, and **verified** — see [the spike](spikes/vcss-excss/RESULT.md). The selector tree is fully typed and reachable, specificity is computed, `var()` and unknown properties survive verbatim. **It does not parse `@layer`**, which arrives as an `UnknownRule` carrying its text; Vixen reads the prelude and re-parses the body. |
| `HarfBuzzSharp` | 14.2.1.1 | `Vixen.Ui.Text` | Text shaping. Non-negotiable for correct Arabic/Indic/emoji/ligatures. |
| `K4os.Compression.LZ4` | 1.3.8 | `Vixen.Core.Serialization` | Bundle chunk compression, fast path (Stride uses LZ4 for the same reason). |
| `ZstdSharp.Port` | 0.8.8 | `Vixen.Core.Serialization` | Bundle compression, size path for downloadable content. Pure managed → works on WASM. |
| `System.IO.Hashing` | 10.0.10 | `Vixen.Core` | XxHash128 for content IDs and cache keys. |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.10 | `Vixen.Core.Diagnostics` | Interface only — engine implements its own zero-alloc sink. |
| `ZLogger` | 2.5.10 | `Vixen.Core.Diagnostics` | Zero-allocation structured logging sink behind `ILogger`. See ADR-008. |

### Tooling / editor / build — never in a runtime package

| Package | Version | Used by |
|---|---|---|
| `Nuke.Common` | 10.1.0 | `build/_build.csproj` |
| `YamlDotNet` | 18.1.0 | `Vixen.Core.Yaml`, `Vixen.Editor.Core` (`.meta` + `.vxasset` I/O) |
| `Silk.NET.Shaderc` / `.Native` | 2.23.0 | Raven test-oracle only (validate our SPIR-V against a reference compiler) |
| `Silk.NET.SPIRV.Cross.Native` | 2.23.0 | `Vixen.Shaders.Transpile` — SPIR-V → GLSL/ESSL/HLSL/MSL/WGSL |
| `Silk.NET.Direct3D.Compilers` | 2.23.0 | HLSL → DXIL for the D3D12 backend |
| `Antlr4.Runtime` / `Antlr4.CodeGenerator` | 4.6.6 | Raven only (already in use). **Not** used for VXML or VCSS. |

### Test

| Package | Version |
|---|---|
| `xunit.v3` | 3.2.2 |
| `NSubstitute` | 6.0.0 |
| `Shouldly` | 4.3.0 |
| `BenchmarkDotNet` | latest at bring-up |
| `CsCheck` | latest at bring-up (property-based math/layout tests) |

### Rejected / reference-only

| Thing | Verdict | Reason |
|---|---|---|
| `Arch` (NuGet) | **Reference only, not a dependency** | ADR-004 |
| `Flexbox` (ru-ace) | **Reference only** | ADR-006 |
| `SignalsDotnet` | **Reference only** | ADR-007 |
| `BepuPhysics` | Not used | Jolt specified; Jolt has broader platform binaries and better character controller |
| `Veldrid`, `Vortice`, `SharpDX` | Not used | Silk.NET covers it |
| `Avalonia`, WPF, ImGui | Not used | The UI framework is the product; using another one defeats the exercise. ImGui may appear as a *debug-only* overlay behind `VIXEN_DEBUG_IMGUI` before `Vixen.Ui` is ready — a scaffold with a scheduled removal, tracked in the roadmap. |
| `Mono.Cecil` / assembly post-processing | **Banned** | ADR-002 |
| `R3` / `System.Reactive` | Not in core | ADR-007 |

---

## Architecture Decision Records

### ADR-001 — Vulkan is the reference backend; other APIs are conformance targets

**Decision.** `Vixen.Graphics` exposes an explicit, Vulkan-shaped RHI: physical/logical device split,
command buffers recorded on worker threads and submitted on a submit thread, explicit
render passes, explicit resource barriers, descriptor set layouts, pipeline state objects created
ahead of time, and explicit memory heaps with a sub-allocator. D3D12 maps almost 1:1. OpenGL/WebGL
and WebGPU are *emulated* over this model by a translation layer in each backend (barrier tracking
becomes no-ops or `glMemoryBarrier`; descriptor sets become bind-group caches; PSOs become
program+state tuples).

**Rejected alternative.** A lowest-common-denominator GL-shaped RHI (Stride's original approach).
It caps the renderer at GL-era capability and makes bindless, GPU-driven culling, async compute, and
multi-queue transfers impossible to express.

**Cost.** The GL backend is the hardest to write and the least performant. Accepted: GL/WebGL exists
for reach, not for the flagship path.

**D3D12 is designed-for but not implemented for 1.0 — ✅ decided.** The backend is postponed; the
abstraction must accommodate it without breaking changes when it lands. Concretely:

1. **Design the barrier model to D3D12 *Enhanced Barriers*, which are near-isomorphic to Vulkan
   `synchronization2`.** This is the single highest-risk place for Vulkan-only drift: legacy
   `D3D12_RESOURCE_STATES` are a *state-transition* model, whereas Vulkan sync2 is a
   *stage+access+layout* model. Targeting Enhanced Barriers (Agility SDK) keeps one vocabulary for both.
   Getting this wrong is the one mistake that would force an RHI-wide breaking change later.
2. **Prefer the concepts that already map both ways**: timeline semaphores (D3D12 fences are monotonic
   counters), explicit PSOs, ahead-of-time descriptor set layouts (→ root signature tables), bindless via
   capability flag (→ SM6.6 dynamic resources), `ShaderFormat.Dxil` already reserved in the bytecode
   enum ([05](05-graphics-rhi.md)).
3. **No Silk.NET.Vulkan type may appear in `Vixen.Graphics`' public surface** — no `VkFormat`, no
   `VkImageLayout`, no raw `Vk*` handles. Enforced by `CheckArchitecture` plus the PublicAPI baseline.
   The RHI defines its own enums and maps them per backend.
4. **`docs/rhi-backend-mapping.md`** — a maintained table of every RHI concept against Vulkan, D3D12,
   GL/GLES/WebGL2, WebGPU, and Metal-via-MoltenVK. Reviewed whenever the RHI surface changes. Cheap, and
   it makes design drift visible in a diff.
5. **OpenGL becomes the abstraction validator instead of D3D12.** This is the reassuring part: GL is
   *further* from Vulkan than D3D12 is — no PSOs, no descriptor sets, no explicit barriers, no
   multithreaded recording. An RHI that survives the GL backend will map to D3D12 comfortably. So
   deferring D3D12 costs less design confidence than it appears to, provided GL is not also deferred.

**Reserved, not built:** the `Vixen.Graphics.Direct3D12` project and package slot exist from Phase 1
with the interface implemented as `NotSupportedException` stubs, so the package identity, RID mapping,
and reference graph are settled and adding the real implementation is additive.

### ADR-002 — All metaprogramming is Roslyn source generators; IL post-processing is banned

**Decision.** Serializers, ECS queries, `Behavior` lifecycle dispatch, shader parameter keys, VXML
components, type registries, dependency-property boilerplate, and `[LoggerMessage]` logging are all
generated at compile time by incremental source generators living in `*.Generators` projects.

**Consequences.**
- Works under NativeAOT (mandatory on iOS) and full trimming.
- Debuggable — generated C# is emitted to disk (`EmitCompilerGeneratedFiles=true`, as Raven already
  does) and steppable.
- Every generator ships with snapshot tests over its output (see [12](12-build-ci-and-testing.md)).
- Generators must be *incremental* (`IIncrementalGenerator`) and must not read the filesystem outside
  `AdditionalFiles`, or IDE responsiveness dies. This is a review checklist item.

**This is the confirmed meaning of the "do not use Mono" constraint**: Stride's
`Stride.Core.AssemblyProcessor` rewrites IL with Mono.Cecil after every compile, and that is what is
rejected. The scope is the **compile-time toolchain**; the runtime host the .NET SDK selects per
platform (CoreCLR on desktop, CoreCLR-or-Mono on Android, Mono-based on WASM) is out of scope and does
not affect the engine. Settled — see [15](15-risks-and-open-questions.md) §2.

**Enforcement, not convention.** `CheckArchitecture` ([12](12-build-ci-and-testing.md)) fails the build
if `Mono.Cecil`, `dnlib`, `ILRepack`, `Fody`, or any IL-rewriting `AfterCompile` target appears in the
restore graph or the MSBuild target graph. An analyzer bans `System.Reflection.Emit`,
`Expression.Compile`, and `Activator.CreateInstance(Type)` in runtime assemblies. This constraint is
foundational enough that it must be impossible to violate accidentally in year three.

### ADR-003 — Vixen owns its math types

**Decision.** `Vixen.Core.Mathematics` defines `Vector2/3/4`, `Matrix4x4`, `Quaternion`, `Plane`,
`BoundingBox`, `BoundingSphere`, `BoundingFrustum`, `Color`, `Color3`, `Color4`, `Half`, `Rectangle`,
`Int2/3/4`, `Ray`, `Viewport` as `readonly record struct` with explicit layout.

**Rationale.** `System.Numerics` lacks `Matrix3x3`, bounding volumes, colour types, integer vectors,
and row-major/column-major control; `Silk.NET.Maths` is generic over `T:IFloatingPoint` which blocks
SIMD intrinsics and bloats generic instantiation on AOT. Both get free bidirectional
`implicit operator` conversions so interop is invisible at call sites, and
`System.Numerics.Vector128/256/512` is used *inside* our implementations.

> **`readonly struct`, not `readonly record struct`** — corrected once the types were written. The
> ADR's *other* requirement, public readonly **fields** so `ref` returns and `Unsafe.As` are legal and
> free, takes away everything `record` would have given: a positional record declares properties, not
> fields; with readonly fields and no positional parameters `with` has nothing to set; and `Equals`,
> `GetHashCode`, `==` and `ToString` are all hand-written (IEEE equality, normalising hash), so every
> synthesised member would be replaced. `record` was left contributing a keyword. Only the wording
> changes — every property the ADR asked for is in the implementation, and the reasoning is repeated
> in `Core/Vixen.Core.Mathematics/Conventions.md` where the next reader will be standing.

**Convention (write it down once, never argue again):** right-handed, Y-up, **row-vector**
convention with **row-major storage** (`M11..M44`, translation in `M41..M43`), matching Stride and
HLSL's `mul(v, M)`. Depth range 0..1 with reverse-Z. Raven's generated code assumes this.

> **Corrected wording.** This originally read "column-vector convention", which contradicted the rest
> of its own sentence: `mul(v, M)` puts the vector on the left, and a translation in `M41..M43` is the
> last *row*. Both are the row-vector convention, which is what Stride and HLSL use and what the
> implementation does. Only the word was wrong.
>
> **The GPU side is not a contradiction either.** Raven decorates matrices `ColMajor` while the host
> stores them row-major. Those are the same bytes read two ways, and they compose to exactly
> `mul(v, M)` at no cost — the derivation is in
> [07 § E](07-raven-shader-pipeline.md#e-conventions-raven-must-bake-in), pinned by a test that reads
> the emitted decorations.

### ADR-004 — Vixen implements its own archetype ECS, informed by Arch

**Decision.** Do not depend on `Arch`. Implement `Vixen.Ecs` with the same archetype-chunk design.

**Rationale.** Arch is excellent and the right *model* — archetype graph with add/remove edges,
chunked SoA storage, `IForEach` inlined queries, `CommandBuffer`. But:
- It targets `netstandard2.1;net6.0;net8.0` with a `PolySharp`-era polyfill layer and T4 templates;
  Vixen wants `net10.0`, C# 14 `InlineArray`, `allows ref struct`, and incremental generators instead
  of T4.
- Vixen needs things Arch does not model: managed-component side tables for the `Behavior` layer,
  deterministic entity IDs stable across save/load for the editor, per-chunk change-version tracking
  for the UI's signal integration and for the renderer's dirty-transform propagation, hierarchical
  transform ordering, and editor undo/redo journaling of world mutations.
- Retrofitting those into a third-party ECS means either forking it or fighting it. Forking it is
  the same work as writing it, without ownership of the API surface that every user of the engine
  will touch forever.

**Mitigation of the "reinventing" risk.** Arch's benchmark suite is ported as the ECS performance
gate: Vixen must match or beat Arch on create/destroy/iterate/add/remove micro-benchmarks before the
ECS phase closes. Arch stays cloned in `references/` for consultation.

### ADR-005 — Adopt Unity's `.meta` *sidecar pattern*; design the *content* natively

**Decision.** One `.meta` file per imported file, and one per folder, exactly as Unity does. The
schema inside is Vixen's own: YAML with type tags that deserialise straight into strongly-typed C#
records, closer in spirit to Stride's asset files than to Unity's. Detailed in
[08](08-asset-pipeline-and-addressables.md).

**Rationale.** The two halves of Unity's design have very different value.

The *pattern* is excellent and is adopted without change: a GUID that is the asset's identity so that
moving or renaming a file breaks nothing; importer settings living next to the asset they configure and
versioned with it in source control; one small text file per asset so merges are localised and a
conflict affects one asset instead of a project-wide database. Every one of those properties is worth
having, and no alternative (central database, settings-in-the-asset, path-based references) gets all
three.

The *schema* is fifteen years of accreted compatibility. Concretely, what is dropped and why:

| Unity artefact | Dropped because |
|---|---|
| `fileFormatVersion: 2` | A magic constant that has never changed. Replaced by a real `metaVersion` with a migration chain. |
| Importer selected by a block key named after the importer type | A convention that has to be special-cased in the reader. A YAML type tag (`!TextureImporter`) *is* the discriminator, and it maps directly to a C# type through the generated type registry. |
| Per-block `serializedVersion: 13` integers | Ad-hoc per-importer migration. One `version` per importer with a generated migration chain does the same job uniformly. |
| `platformSettings:` repeating every field per target | The reason a texture `.meta` is 100+ lines. Replaced by sparse overrides that carry only the differing fields. |
| `externalObjects`, `internalIDToNameTable` | Unity-internal remapping tables whose behaviour is undocumented and whose naming is opaque. Vixen models sub-assets and reference remapping explicitly. |
| `assetBundleName` / `assetBundleVariant` | Superseded by the addressable block. Keeping both would reproduce exactly the "four loading systems" mess that ADR-013 exists to avoid. |
| `{fileID: 2800000, guid: …, type: 3}` reference form | Three fields where two suffice, with a `type` nobody reads and a `fileID` whose numbers are Unity-internal. Replaced by a compact single-scalar reference that diffs and merges better. |
| `userData` as an untyped string blob | Replaced by a typed, tagged extension map. |

**Consequence — and it is now a non-issue.** Unity `.meta` files no longer parse as-is. Per **Q9,
no Unity compatibility is expected or attempted**: Unity is an implementation reference, and Vixen does
not adopt Unity's formats or conventions where its own judgement differs. That decision frees this
schema completely — the sidecar *pattern* is kept because it is genuinely the best design for the job,
and every byte inside the file is chosen for .NET and for Vixen's own subsystems, with no compatibility
tax and no migration path to maintain.

### ADR-006 — Flexbox: port the Yoga *algorithm*, not the Flexbox *library*

**Decision.** `Vixen.Ui.Layout` is a from-scratch implementation of the CSS Flexbox algorithm using
Yoga's structure as the reference (via the `ru-ace/Flexbox` C# port and Yoga's upstream C++), written
against a struct-of-arrays node store.

**Rationale.** The reference port is `TargetFrameworkVersion v4.6`, `class Node` with `List<Node>`
children and `class Style` with boxed `Value` types — one heap object per node per style per layout
result. A Blender-class UI has 10⁴–10⁵ nodes. That allocation profile is disqualifying. The
*algorithm* is the valuable part and is ~3 500 lines of well-tested logic.

**How correctness is guaranteed.** Yoga ships a generated conformance suite derived from HTML
fixtures rendered in Chrome. That suite is ported wholesale into `Vixen.Ui.Layout.Tests`. Flexbox is
not "done" until it passes. This converts the riskiest re-implementation in the project into a
mechanical exercise with an oracle.

### ADR-007 — Vixen implements its own signal graph, with SignalsDotnet as the API reference

**Decision.** `Vixen.Ui.Reactive` implements Angular-style signals (`Signal<T>`, `Computed<T>`,
`Effect`, `untracked`, `batch`, `linkedSignal`, resource/async signals) from scratch. Do not depend
on `SignalsDotnet`.

**Rationale.** SignalsDotnet is built on `R3`, and its `Effect` scheduling is Rx-scheduler-driven.
A game engine's UI must flush effects at a *precise point in the frame* (after input, before layout,
never mid-render) on a known thread, with a hard budget. Bolting that onto Rx schedulers is fighting
the library's core assumption. It also pulls `R3` + `PolySharp` into every shipped app.

The semantics — glitch-free push-pull propagation, dependency auto-tracking via an ambient consumer
stack, version counters for equality short-circuit, `ReferenceEqualsComparer` opt-outs — are exactly
right, and its source (~40 files) is the design spec. Implementation is ~1 200 lines.

**Design specifics.** Pull-based with push invalidation (Angular's model, not MobX's): setting a
signal bumps a global version and marks dependents dirty without evaluating them; `Computed` values
re-evaluate lazily on read and compare against the last value to stop propagation; `Effect`s are
queued to a per-frame effect queue drained at a defined phase. Dependency edges live in pooled
arrays, not `List<T>`, so a steady-state UI allocates nothing.

### ADR-008 — Logging: `ILogger` interface, engine-owned sink

**Decision.** Code against `Microsoft.Extensions.Logging.Abstractions` with `[LoggerMessage]`
source-generated methods. Provide a `VixenLoggerProvider` writing to an in-memory ring buffer plus
ZLogger for file/console. The editor console reads the ring buffer directly.

**Rationale.** `[LoggerMessage]` gives zero-allocation, zero-boxing structured logging with
compile-time format validation and works on AOT. Standard interface means users can plug Serilog or
OpenTelemetry if they want. See [13](13-diagnostics.md).

### ADR-009 — Hand-written recursive descent for every front end — ⚠️ **amended**

**Decision.** VXML and the utility-class extractor are hand-written parsers producing full-fidelity
syntax trees with trivia, in the same shape as Raven's `Syntax/`. VCSS uses ExCSS for
tokenizing/parsing and a Vixen-owned cascade/selector-matching engine on top.

**Amended:** *and Raven, eventually, too.* ANTLR was right for Raven's **bootstrap** — it is not the
right end state. See [18-raven-parser-migration.md](18-raven-parser-migration.md) for the finding, the
plan and the timing. Summary of why the original rationale below no longer holds:

- **"Error recovery is table-driven"** was listed as a reason *for* ANTLR. In practice its recovery
  produces trees the ANTLR→green translator cannot map, and `SyntaxTree.ParseText` has to *discard the
  tree* to cope. Messages are of the "no viable alternative at input" and "expecting {…40 tokens…}"
  kind — fine for a CLI, wrong under an editor squiggle.
- **The Raven/VXML split does not hold.** The reason given for hand-writing VXML was sub-millisecond
  incremental reparse for hot reload. [Doc 09](09-ui-framework.md) specifies a `CodeEditor` doing
  *"syntax highlighting via `Vixen.Core.Syntax` (Raven/VXML/VCSS/C#)"* — so `.rvn` gets typed in an
  editor with live squiggles as well, and needs the same thing.
- **Sharing the tree turned into an argument against ANTLR.** With `Vixen.Core.Syntax` extracted, the
  node-reuse blender belongs in the shared layer and VXML/VCSS both get it. Raven cannot use it — ANTLR
  has no notion of reusing an existing tree — so Raven becomes the one front end that cannot benefit
  from the shared investment, inverting the point of extracting it.

**What has not changed:** ANTLR is genuinely better while a grammar is churning, and the `.g4` files
are readable specification. The migration is therefore sequenced *after* the language surface settles,
and the grammar is **kept afterwards as a differential oracle** rather than deleted.

**The framing that was wrong.** ADR-009 read this as a per-language judgement between two parser
technologies. The sharper statement: Roslyn's design is an **XML-generated tree plus a hand-written
parser**, with no grammar file or parser generator anywhere in it. Raven adopted the first half
verbatim — same `Syntax.xml`, same generator, same three generated files — and bolted ANTLR onto the
front through a 1 490-line translator. Hand-writing Raven's parser *completes* that design rather than
reversing a decision.

**Rationale (original, retained).** Hand-written parsers are what Roslyn, TypeScript, and every serious
IDE front-end use: incremental reparse, precise squiggle positions, and error recovery tuned to
"the user is mid-typing".

Reusing Raven's `GreenNode`/red-tree infrastructure across all three front ends (Raven, VXML, VCSS)
is an explicit goal — ✅ extracted into `Vixen.Core.Syntax` in Phase 0.

### ADR-010 — Fine-grained reactivity, not a virtual DOM

**Decision.** A `.vxml` component compiles to imperative construction code plus signal
subscriptions that mutate exactly the affected element property. There is no VDOM, no diffing pass,
no reconciliation.

**Rationale.** You asked for "Angular or React to handle change detection." Both are worth studying,
but React's model (re-render subtree, diff, patch) allocates a new tree every update — untenable at
120 fps in a frame budget shared with rendering. Angular's *modern* model (signals + fine-grained
DOM updates, post-Ivy) is the right one and is where the industry has converged (Solid, Svelte 5
runes, Vue Vapor, Angular signal components). So: Angular-style signals + Solid-style compilation.

Concretely, `<span>@Count</span>` compiles to a `TextNode` plus one `Effect` that assigns
`node.Text`. A list `@for` compiles to a keyed reconciler over a single collection signal that
moves/creates/destroys only changed children.

### ADR-011 — Metal via MoltenVK — ✅ **confirmed**

**Decision.** macOS and iOS run the Vulkan backend on MoltenVK. No native Metal backend before 1.0.

**Rationale.** There is no Silk.NET Metal binding (verified). Writing one means hand-binding
Objective-C runtime interop for the whole Metal API plus a full second backend — several
engineer-months for a target MoltenVK already serves at 90–95% of native performance. MoltenVK is
what Dota 2, Baldur's Gate 3, and the Vulkan SDK's own Apple support ship. It is distributed as a
static/dynamic library and works under iOS's no-JIT rules.

**Verified against MoltenVK's current release and runtime guide** (checked at plan time):

| Fact | Value |
|---|---|
| Latest release | **v1.4.2**, published 2026-07-24 — actively maintained, releases land continuously |
| Vulkan level | **A layered implementation of Vulkan 1.4** — higher than this plan's Vulkan 1.1 floor, so the RHI's minimum spec is comfortably met on Apple |
| Minimum OS | macOS 12.0, iOS 15, tvOS 15 |
| Distribution | Universal `XCFramework`, or a dynamic `libMoltenVK.dylib` on macOS |
| Reach | macOS, iOS, tvOS, Mac Catalyst, **and the iOS/tvOS Simulators** — the simulator support matters for CI ([10](10-platforms.md)) |
| Shader path | **MoltenVK consumes SPIR-V and converts to MSL using SPIRV-Cross internally** |

That last row is a meaningful reinforcement of **ADR-012**: Apple support consumes SPIR-V through
SPIRV-Cross whether we cross-compile ourselves or let MoltenVK do it. Raven emitting SPIR-V is
therefore the correct single investment for Vulkan, Metal, and (via the same tool) GL/HLSL/WGSL.

**Documented constraints, all capability-gated in the RHI:**

- `VK_EXT_descriptor_indexing` requires **Metal argument buffers enabled in config**, and is limited on
  Tier 1 hardware (96/128 textures, 16 samplers) and on Intel GPUs. → the bindless path
  ([05](05-graphics-rhi.md)) must genuinely have a non-bindless fallback on Apple, not just on GL.
- `VK_EXT_buffer_device_address` requires **Tier 2 argument buffers**.
- **Primitive restart cannot be disabled** — Metal has no such control, so
  `VK_DYNAMIC_STATE_PRIMITIVE_RESTART_ENABLE_EXT` is a no-op. Index buffers must never rely on
  `0xFFFF`/`0xFFFFFFFF` being an ordinary index.
- **`VK_QUERY_TYPE_PIPELINE_STATISTICS` is not supported.** → see [13](13-diagnostics.md); the GPU
  profiler's pipeline-statistics track is unavailable on Apple and must degrade to timestamps only.
- **`VkAllocationCallbacks` are ignored.** Harmless — we own our device-memory allocator anyway.
- PVRTC-compressed images must be loaded by host-visible memory mapping, not via a staging buffer.
- **MoltenVK does not load Vulkan layers itself** — see the validation-layer consequence in
  [05](05-graphics-rhi.md), which changes how the macOS dev build is assembled.

**Post-1.0 escape hatch.** The RHI is Vulkan-shaped, so a native Metal backend is a drop-in
`IGraphicsBackend` implementation if profiling ever justifies it. Raven's SPIR-V output already
cross-compiles to MSL via SPIRV-Cross, so shaders are ready.

### ADR-012 — Raven emits SPIR-V as the canonical IR; everything else is cross-compiled

**Decision.** Raven's authoritative backend is SPIR-V. Other targets are produced by SPIRV-Cross
(GLSL 4.5 core, ESSL 3.0, HLSL 6.0, MSL, WGSL) from that SPIR-V, except where a target-specific
Raven backend measurably wins.

**Rationale.** One well-tested backend beats five half-tested ones. SPIR-V has a validator
(`spirv-val`), an optimiser (`spirv-opt`), and a reference cross-compiler, all shipped by Khronos and
available via `Silk.NET.SPIRV.Cross.Native`. Raven's GLSL emitter is built in the same phase as the
SPIR-V one, and serves two non-shipping jobs: readable output for the frame debugger, and a
**differential oracle** — `shaderc`(Raven GLSL) compared against Raven's own SPIR-V
([07](07-raven-shader-pipeline.md)).

**Consequence for Raven.** Its semantic phase must produce enough type/binding information to emit
valid SPIR-V with explicit descriptor set/binding decorations, which is a stricter bar than
transpiling to GLSL. This is called out as a Raven prerequisite in [07](07-raven-shader-pipeline.md).

### ADR-013 — Addressables from day one; there is no non-addressable path

**Decision.** *All* runtime content is addressed by a string address resolved through a catalog.
There is no `Resources`-folder equivalent and no direct-path loading in shipped builds. Local content
and remote content differ only in the catalog entry's provider.

**Rationale.** Unity's original sin was bolting Addressables onto `Resources` + `AssetBundle` +
direct references, leaving four loading systems and a decade of confusion. Starting with one system
where local is just "remote with a `file://` provider" costs nothing and removes an entire class of
future migration pain. Stride's `ContentManager` + `ObjectDatabase` + bundle model already works this
way; Vixen adds the catalog/group/label layer on top.

### ADR-014 — Tests live beside the code they test

**Decision.** `Core/Vixen.Core.Mathematics/` and `Core/Vixen.Core.Mathematics.Tests/` are siblings.
No top-level `tests/` mirror tree.

**Rationale.** Locality — a subsystem folder is self-contained and reviewable. Matches Stride and
Arch. Nuke globs `**/*.Tests.csproj`; the `.slnx` groups them into a solution folder so the IDE view
stays clean. `Samples/` and `Benchmarks/` are top-level because they cross-cut.

### ADR-015 — Vixen is Apache-2.0 — ✅ **decided**

**Decision.** Apache License 2.0 for all Vixen code, packages, and the editor.

**Rationale.** For an engine, Apache-2.0's **express patent grant** (§3) is worth more than MIT's
brevity: studios shipping commercial titles on a third-party engine care about patent peace, and legal
review at a publisher is materially easier with Apache-2.0 than with a bare permissive licence. It also
matches xunit, Arch, MoltenVK, SPIRV-Cross, shaderc, and astcenc, so the notice story is consistent.

**Obligations this creates, all mechanised in the build:**

| Requirement | Where |
|---|---|
| `LICENSE` with the full Apache-2.0 text | repo root |
| `NOTICE` file, propagated into every NuGet package | generated by Nuke `Pack` |
| Third-party attribution manifest (managed + native deps, with licence texts) | generated by Nuke `RestoreNativeDeps` + `Pack`; shipped in the editor's About dialog and in `docs/manual/third-party.md` |
| `PackageLicenseExpression=Apache-2.0` on every csproj | `Directory.Build.props` |
| Per-file SPDX header (`// SPDX-License-Identifier: Apache-2.0`) | enforced by `CheckFormat`; Apache recommends but does not require this — cheap to automate, and it removes all ambiguity for anyone vendoring a single file |
| Modification notices on derived files (§4b) | required where we port third-party algorithms — see the table below |

**Dependency licence audit** (verified from package nuspecs and repository `LICENSE` files at plan
time; all compatible with shipping an Apache-2.0 engine):

| Dependency | Licence | Note |
|---|---|---|
| Silk.NET (all) | MIT | ✓ |
| JoltPhysicsSharp | MIT | ✓ |
| ExCSS | MIT | ✓ |
| HarfBuzzSharp | MIT | ✓ |
| YamlDotNet, ZLogger, ZstdSharp.Port, Nuke.Common | MIT | ✓ |
| K4os.Compression.LZ4 | MIT (file-declared, no SPDX expression) | ✓ record explicitly in NOTICE |
| xunit.v3 | Apache-2.0 | ✓ |
| NSubstitute, Shouldly | BSD-3-Clause | ✓ test-only |
| MoltenVK, SPIRV-Cross, shaderc, astcenc | Apache-2.0 | ✓ |
| Assimp | BSD-3-Clause | ✓ editor-only |
| Recast/Detour | zlib | ✓ *reference material only — `Vixen.Navigation` re-derives the algorithms and links nothing* |
| **SixLabors.ImageSharp 4.0.0** | **Six Labors Split License 1.0 — *not* Apache-2.0** | ⚠ see below |
| *Reference material:* Yoga | MIT | algorithm + conformance suite (ADR-006) |
| *Reference material:* `ru-ace/Flexbox` | BSD (legacy Yoga text) | ✓ retain notice if any code is derived |
| *Reference material:* SignalsDotnet | MIT | ✓ |
| *Reference material:* Arch | Apache-2.0 | ✓ same licence as Vixen |
| *Reference material:* Stride | MIT | read-only; no code copied (ADR: [00](00-vision-and-principles.md)) |

**⚠ ImageSharp needs a deliberate decision.** ImageSharp 4.0.0 is **not** plain Apache-2.0. Its
`LICENSE` is the *Six Labors Split License 1.0*: Apache-2.0 applies only if you are (a) using it in
Open Source or Source Available software, (b) consuming it as a **transitive** package dependency,
(c) a for-profit under **$1M USD annual gross revenue**, or (d) a non-profit. Everyone else needs a
paid Six Labors Commercial License.

Applied to Vixen:

- **Vixen itself qualifies** under (a) — Vixen is Apache-2.0 open source.
- **Vixen's commercial users qualify** under (b) — they get ImageSharp transitively through Vixen's
  tooling, not as a direct reference. A studio that *also* adds its own direct `PackageReference` to
  ImageSharp falls outside the grant and needs a commercial licence, which is not our problem but is
  worth one line in the docs.

**Mitigations, both cheap and both adopted:**

1. **ImageSharp is editor- and tooling-only. It never appears in a runtime package.** This was already
   the design intent — the runtime reads only KTX2/DDS through `Vixen.Core.Imaging`
   ([03](03-core-foundation.md)) — but an earlier draft of the dependency table listed ImageSharp
   against `Vixen.Core.Imaging` as well, which contradicted it. Corrected: ImageSharp belongs to
   `Vixen.Editor.Assets` only. Enforced by `CheckArchitecture`. Consequence: **no shipped game ever
   links ImageSharp**, so the licence question does not reach the runtime at all.
2. **Import-time decoding sits behind `IImageDecoder`** in `Vixen.Editor.Assets`, with ImageSharp as
   one implementation. If the split licence ever becomes uncomfortable, the zero-ambiguity swap is
   `StbImageSharp` (public domain, covers PNG/JPG/TGA/BMP/HDR/PSD) plus `Pfim` (MIT, DDS/TGA) — lower
   fidelity on EXR/TIFF/PSD, and a day of work behind the interface rather than a refactor.

> ✅ **Mitigation 2 was taken, and sooner than this expected.** ImageSharp 4.0.0 does not merely have
> an uncomfortable licence: it refuses to build without a purchased key, failing in
> `SixLabors.ImageSharp.targets` before any code compiles. That is disqualifying for a repository
> anyone is meant to be able to clone and build, so `Vixen.Editor.Assets` uses `StbImageSharp`.
>
> The swap cost one class, which is the interface earning itself — nothing in `TextureImporter`, the
> pipeline or the tests changed. Coverage moved rather than shrank: Radiance HDR arrived, which is
> the format an environment map actually ships in and which the BC6H encoder and the IBL prefilter
> were waiting for; `.exr`, `.tif` and `.webp` left, and `.dds` was never there.
>
> **`CheckArchitecture`'s ADR-015 rule stays.** Nothing references ImageSharp now, so the rule guards
> against a future mistake rather than a present one — which is what it was for.
