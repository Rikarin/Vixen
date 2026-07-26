# 07 — Raven Shader Pipeline

Raven is a working compiler at `Raven/`: a hand-written lexer and recursive-descent parser over shared
`Vixen.Core.Syntax` infrastructure, a Roslyn-style green/red tree generated from `Syntax.xml` (79
concrete + 13 abstract node types) with full trivia and spans, a semantic phase, a target-independent
IR, and GLSL and SPIR-V emitters. The ANTLR grammar it started on survives as a **test oracle** — the
`.g4` files are checkable documentation of the syntax, and a token-stream and tree differential against
them runs over the whole corpus ([doc 18](18-raven-parser-migration.md)).

Raven's own `docs/IMPLEMENTATION_PLAN.md` has been **retired**: its phases are complete, this document
is the plan of record, and two roadmaps for one compiler is how they come to disagree. What was still
open in it is [§ I](#i-gaps-carried-over-from-ravens-retired-implementation-plan) below, re-checked
against the code rather than copied across.

Raven finishes before engine work starts, so this document specifies **the contract the engine requires
from Raven** — giving "finished" a precise definition — and opens with a consolidated checklist of every
change the engine plan asks of it.

## What is left

The checklist below is mostly closed, so this is the short list of what is not — gathered here because
it would otherwise mean scanning ten status tables. Everything else in this document is a record of a
decision that has been made and built, kept because the reasons stay useful.

| | Open item | Where | Blocks |
|---|---|---|---|
| 🟡 | **`Raven/Library` is written** — 44 files across all eight packages, every shader reaching both backends under `glslc` and `spirv-val`. What is left is depth rather than breadth: the clustered light loop, the G-buffer geometry pass and the particle compute dispatch, each blocked by a language gap below rather than by content | § F | the perf gates; the numeric tests additionally need a writable resource |
| 🔴 | **Nothing a shader writes is writable** — no storage buffers, no storage images, and assigning to a uniform is refused by neither backend. So the compute stage computes and discards | § I | the numeric BRDF readback, `Random.rvn` bit-for-bit, doc 06's VFX compute path — everything that has to *read a result back* |
| 🟡 | **Multiple render targets** — an entry point returns one value, and an aggregate return is `RVN4001` in both backends | § F | the G-buffer *geometry* pass; `GBuffer.rvn` is the encoding only, and `Deferred.rvn` reads it |
| 🟡 | **Generic types and methods do not lower** — front-end only. An open definition is `RVN3001`, and so is an instantiation: there is no monomorphisation, so `Box<float4>` reaches no backend | § I | anything in § F's library that wants a generic container |
| ⚪ | **Small texture and stage intrinsics**: no `SampleLevel` (explicit mip), no `GetDimensions`, no `discard`, no `SV_VertexID` semantic, no `asfloat`/`asuint` | § F | each shaped a library file rather than blocking it — see § F's list of what it could not express |
| 🟡 | **`&&` / `\|\|` do not short-circuit** — sound for side-effect-free expressions, wrong the moment the right operand is a guard | § I | correctness of `i < n && data[i] > 0` |
| 🟡 | **Sized array types**, and therefore `Buffer<T>`-style storage buffers, unsized arrays, spread elements (`RVN3002`) and `ArrayStride` against the oracle | § I, § C | the writable-resource row above; `DescriptorType.StorageBuffer` and `LayoutRule.Std430` have nothing that produces them |
| 🟡 | **Inheritance is not flattened** — now `RVN3002` rather than three silent miscompilations | § I, mixins | the mixin question; `compose` covers the common case |
| 🟡 | **Push constants** — no syntax, so `PushConstants` is always empty | § C, § D | nothing yet; reported as absent rather than guessed |
| 🟡 | **String interpolation** — needs lexer modes; nothing shipped uses it | § I | nothing |
| ⚪ | **Flow analysis** — definite assignment and reachability. Dead-branch elimination is constant folding, not this | § I | silent partial initialisation of a struct |
| ⚪ | **Nuke is not stood up**: `CompileShaderLibrary`, `CheckFormat` for SPDX enforcement, the CI workflows | § A, § G | shipping the library as a package; SPDX is a real gap, not a closed item |
| ⚪ | **`Vixen.Raven.Transpile`** (SPIRV-Cross wrapper) and the cross-compilation test pass | § A, § G | HLSL/MSL/WGSL output, which ADR-012 says SPIRV-Cross owns |
| ⚪ | **`Vixen.Shaders.Generators`** — Raven supplies everything it needs; the generator waits for the engine's `ParameterKey` | § Generated C# bindings | deliberately engine-side |

Two smaller ones recorded where they belong rather than here: streams have **no interpolation control**
(§ Streams), and a library's **IR names share one flat namespace per module** (§ D).

## Consolidated change checklist

Everything the engine plan asks of Raven, gathered in one place because it is otherwise spread across ten
documents. **Criticality**: 🔴 engine-blocking · 🟡 needed for 1.0 · ⚪ mechanical or deferrable.

### A. Structural — Phase 0, mechanical ([02](02-repository-layout.md), [14](14-roadmap.md))

| | Change | |
|---|---|---|
| ⚪ | Absorb `Raven/` into the Vixen monorepo **with git history preserved** (`read-tree --prefix=Raven/`, do not squash), then delete `Raven/.git` | ✅ |
| ⚪ | Rename to the monorepo convention: `Compiler/` → `Vixen.Raven`, `Tests/` → `Vixen.Raven.Tests`, `Cli/` → `Vixen.Raven.Cli`, `Feed/` → `Library/` | ✅ |
| ⚪ | Add projects: `Vixen.Raven.Transpile` (SPIRV-Cross wrapper), `Vixen.Raven.Reflection` — each with a sibling `.Tests`. *`Vixen.Raven.Spirv` was listed here in error: both emitters land together and live in `Vixen.Raven`, per [02](02-repository-layout.md) § Raven, which the code already follows.* | |
| 🔴 | **Extract `Vixen.Core.Syntax`**: lift `GreenNode`, `SyntaxNode`, `SyntaxToken`, `SyntaxTrivia`, `SyntaxList<T>`, `SeparatedSyntaxList`, `SourceText`, the `Diagnostic`/`DiagnosticBag` model, and the `Syntax.xml` → node-classes generator out of Raven into shared `Core/` projects, then retarget Raven onto them. VXML and VCSS then declare their own `Syntax.xml` against the same infrastructure. **This is the single highest-leverage refactor in the plan** — it turns three parser front ends into one tested foundation plus three grammars | ✅ |
| ⚪ | Raven lands in the **Tooling** MSBuild profile ([02](02-repository-layout.md)): reflection and LINQ permitted, `IsAotCompatible` off. It is a compiler, not runtime code | ✅ |
| ⚪ | `Vixen.Raven` and `Vixen.Raven.Cli` become shipped NuGet packages ([12](12-build-ci-and-testing.md)); the compiler is useful standalone | ✅ |
| ⚪ | Relicense to **Apache-2.0** with SPDX headers and NOTICE (ADR-015) | ✅ headers; enforcement pending |

**Packaging.** Three packages: `Vixen.Core.Syntax`, `Vixen.Raven` (library) and
`Vixen.Raven.Cli` (a `dotnet tool` exposing `raven`). The generator is `IsPackable=false` —
it is an analyzer, not a shipped assembly. `Directory.Build.props` packs `NOTICE` into every
packable project, which Apache-2.0 §4(d) requires. Two things `dotnet pack` surfaced and that
are now fixed: `Vixen.Raven.Cli` would have taken the package id `raven` from its
`AssemblyName`, and the `.g4` grammars were being packed as `contentFiles` and copied into
every consuming project.

**SPDX enforcement is still outstanding.** Every hand-written `.cs` and `.g4` file now carries
`SPDX-FileCopyrightText` and `SPDX-License-Identifier`, but nothing stops a new file from
arriving without them. ADR-015 assigns that to the Nuke `CheckFormat` target, and Nuke is not
stood up yet ([12](12-build-ci-and-testing.md)) — so this is a real gap, not a closed item.

**How the extraction landed.** Two decisions to know before touching the tree:

- **Kinds are `int` below the language line.** `GreenNode.RawKind` and `SyntaxNode.RawKind` are
  integers and each front end re-exposes its own enum; list-ness is answered by `GreenNode.IsList`,
  never by comparing kinds. The one value the shared tree reserves is `SyntaxKinds.List`, and a
  language's list member must equal it or projecting a list node's kind names the wrong member.
- **`Accept` stays in the language,** because a generated `Accept` takes the language's visitor type.
  The shared `SyntaxNode` declares none and `RavenSyntaxNode` adds it — Roslyn's `SyntaxNode` /
  `CSharpSyntaxNode` split. The shared `SyntaxToken` and `SyntaxListNode` sit outside that hierarchy
  and `SyntaxVisitor.Visit` routes them with one type test. `Syntax.xml` names the language's base in
  `Root` and its output namespace in `Namespace`.

### B. Language and semantic features — Raven's Phase 2

| | Feature | Why the engine needs it | |
|---|---|---|---|
| 🔴 | **`compose` — shader-typed members resolved at compile time.** `compose val diffuse: IDiffuseModel` inside a shader, bound to a concrete `shader` per material | This is *the* load-bearing feature. It lets `ForwardPlus.rvn` be written once against `IMaterialSurface` and instantiated per material. Without it the material system falls back to string-templating shader source — where Stride was fifteen years ago | ✅ |
| 🔴 | **Permutation constants** — `[Permutation] val UseSkinning: bool`, plus `#if`-style conditional compilation driven by `defines` passed to `Emit` | The whole effect/permutation system ([06](06-rendering-pipeline.md)) is built on it | ✅ (`#if` dropped by decision) |
| 🔴 | **`UsedPermutationKeys`** — the semantic phase must report *which* defines actually affected the output | Without it, 20 independent flags yield 2²⁰ cache entries where a handful are distinct. This is why Stride's shader cache is tractable and it cannot be added later | ✅ |
| 🟡 | `protocol` (interface) declarations usable as `compose` targets — already in the language per `Example2.rvn` | Material feature contracts | ✅ |
| 🟡 | Shader inheritance `shader X : Base, Other` — already in the README | Feature composition | ✅ resolves, with cycle detection |
| 🟡 | Compile-time generics: `shader Blur<val TapCount: int>` | Parameterised post-FX without duplication | ✅ one instantiation per compilation |
| ⚪ | Explicit `RequiredCapabilities` reporting (e.g. `"DescriptorIndexing"`, `"Float64"`) | RHI capability gating ([05](05-graphics-rhi.md)) | ✅ |

**Permutation constants.** A `[Permutation]` field is a constant whose value arrives from outside the
source: `PermutationValues` is supplied at `Compilation.Create`, and a key with no supplied value takes
its initializer, which is therefore mandatory. Keys are `bool`/`int`/`uint` only — floats make poor
cache keys, and a shader that wants one should take a uniform.

The mechanism is deliberately small. A permutation field reports `IsConst` with the supplied value as
its `ConstantValue`, so the existing constant folding picks it up with no special case. What had to be
added was **dead-branch elimination**: a folded condition emits only the live branch, and a block stops
emitting after a terminator. Without that the fold changed a value but not the generated code, which is
the whole point. Two properties are worth keeping:

- **A switched-off permutation is still bound, so it is still type-checked** — the main advantage over
  textual `#if`: a variant nobody is currently building cannot quietly rot.
- **`UsedPermutationKeys` records a key when its value is read**, so a read that folding made
  unreachable does not count. `if (A) return 1` with `A` true leaves `B` below it unread, and variants
  differing only in `B` correctly share a cache entry.

**`compose`.** `compose val diffuse: IDiffuseModel` declares a slot; `ComposeBindings` says which
shader fills it. A binding may be qualified (`Lit.diffuse=Lambert`) when two shaders declare a slot of
the same name, and a qualified binding beats a bare one, so a compilation can bind most slots once and
override per shader.

Resolution is entirely static. The call binds against the protocol, so the shader type-checks against
the *feature* rather than an implementation; at lowering the protocol's bodyless method is swapped for
the bound shader's, matched by signature, and the receiver is dropped — a shader method is a free
function because its fields are globals. **There is no dispatch and no indirection**: the emitted unit
holds a direct call, and reachability means an implementation nobody bound is never emitted. The slot
itself is not data: no uniform, no constant-buffer field, nothing surviving to the target. `RVN2070`…
`RVN2077` cover every way a slot can fail to resolve, including a binding to a shader that does not
implement the protocol — the check that makes the whole thing type-safe.

`compose` also made two latent bugs ordinary, because a material's implementation sits wherever its
author put it: lowering created shells for structs but not functions, so a call to anything declared
later failed; and the GLSL emitter filtered reachable functions by *shader membership*, dropping the
very function the entry point called. Reachability alone excludes other stages — membership was never
the right filter, and the SPIR-V emitter had it right already.

**Value type parameters.** `shader Blur<val TapCount: int>` parameterises a shader by a compile-time
constant, modelled as a constant *member* rather than a `TypeParameterSymbol`: it is not a type, the
shader's arity is unchanged, and every existing generic path is untouched. Reporting `IsConst` with a
known value routes it through the same folding and dead-branch elimination a `[Permutation]` field
takes, on the same value channel, and into `UsedPermutationKeys` — it changes codegen, so it belongs in
the cache key. The one difference is that there is **no default**: a value is part of the signature, so
compiling without one is `RVN2082` rather than a fallback.

*Deliberate boundary:* one instantiation per compilation. `shader Blur8 : Blur<8>` — two side by side
in one module — is not supported, because value arguments would have to thread through `TypeMap`,
`ConstructedNamedTypeSymbol` and `SubstitutedSymbols`, and the lowerer would have to enumerate
constructed instantiations rather than declared types. A large change to the generic subsystem for a
case the engine does not have: it compiles one effect variant at a time.

**`RequiredCapabilities`.** `IrCapabilities.Of(module)` and `.Of(shader)` report the target features
needed — `Float64`, `Texture3D`, `TextureCube`, `Geometry`, `Compute` — as sorted *strings*, so a host
does not need recompiling against a new Raven to understand a capability it has not seen. Two decisions
inside it: they are collected from the **lowered IR** rather than the symbols, so a variant that never
reaches the `double` maths does not require `Float64` (asking for a feature this build has no use for
would narrow the hardware a game runs on for nothing); and they are reported **per shader** as well as
per module, because an engine gates a pipeline and what one shader needs says nothing about another.

**`#if` will not be implemented** — decided, not deferred. Typed permutation constants cover the same
ground better, since the switched-off branch stays type-checked. The lexer's `DIRECTIVE_MODE` is
deleted (third pruning pass, § J), and it was worse than vestigial: `#` was `skip`ped and every
directive token went to a dropped channel, so `#if X … #endif` compiled **every branch in, silently**.
A `#` anywhere is now a syntax error, which is what tells a Stride/HLSL author the mechanism does not
exist here.

### C. Emitter requirements — GLSL and SPIR-V together

| | Requirement | |
|---|---|---|
| 🔴 | **SPIR-V emitter** — the canonical output (ADR-012). The engine consumes it directly; no bridge, no intermediate | ✅ |
| 🔴 | **GLSL emitter, Vulkan-flavoured**: `#version 450`+, explicit `layout(set = N, binding = M)` via `GL_KHR_vulkan_glsl`, `layout(push_constant)`, `layout(location = N)` on every stage in/out, explicit `std140`/`std430`. Required so `shaderc` can compile it back to SPIR-V for the **differential oracle** below, and because it is the most readable form for the frame debugger | ✅ except `push_constant`, which needs syntax Raven has not got |
| 🔴 | **Reflection comes from the semantic phase**, never from either emitted form. The engine writes constant buffers by generated offset | ✅ |
| 🔴 | Honour the **four-set descriptor convention** (set 0 per-frame, 1 per-view, 2 per-material, 3 per-draw) when assigning bindings ([05](05-graphics-rhi.md)) — both emitters must agree, which the differential test enforces | ✅ |
| 🟡 | **Differential test**: Raven's SPIR-V vs `glslc`(Raven's GLSL) must be semantically equivalent. The strongest correctness signal available, and free once both emitters exist | ✅ interface-level |
| ⚪ | HLSL / MSL / WGSL emitters are **not required** — SPIRV-Cross covers them (ADR-012) | |
| ⚪ | `IRavenBackend` with swappable implementations is **not required** — the bridge is gone, so there is one code path | ✅ never built |

**The four-set convention is named, not numbered.** A binding is marked `[PerFrame]`, `[PerView]`,
`[PerMaterial]` or `[PerDraw]`, and the set index follows from the marker — a shader never spells
`set = 3`, because the number is the engine's to choose and `[PerDraw]` says *why* the value is
where it is. An unmarked field is **per-material**, since a shader's own `var`s are its material
parameters; defaulting to set 0 instead would drop every unannotated shader on top of the engine's
camera and lighting buffers. Two markers on one field is `RVN2090`; a marker on something that
never becomes a binding — a `const`, a `[Permutation]` key, a `compose` slot — is the warning
`RVN2091`, because the shader is still correct but the author believes something untrue about where
that value lives.

**Bindings restart at 0 in each set,** which is what a Vulkan descriptor set layout is: one
namespace per set. Within a set the uniform block comes first so that adding a texture never
renumbers it, then textures, then samplers, each in declaration order.

**"Both emitters must agree" is structural rather than checked.** `Vixen.Raven.Reflection.BindingPlan`
is the only code that assigns a `(set, binding)` pair; both emitters and `ReflectionBuilder` read
the plan. There is nothing to keep in step, which is the same reasoning as the shared `ShaderLayout`
one level down. The differential test verifies it, but the plan is what makes it true.

**Vulkan GLSL is a faithful mirror of the SPIR-V, not a lossy sibling.** The emitter used to fold a
texture and its sampler into one combined `sampler2D` and report the dropped sampler binding as an
informational diagnostic. It now emits separate `texture2D` and `sampler` objects and pairs them at the
sample site — `texture(sampler2D(albedo, linear), uv)`, the shape SPIR-V always had, and the reason the
two backends' binding indices can be compared at all. A `.Load(…)` becomes `texelFetch` on the bare
texture under `GL_EXT_samplerless_texture_functions`, declared only in the units that need it because a
driver may reject an extension the shader does not use. Nothing about a sampler is dropped any more.

#### The differential oracle, and what it does and does not prove

Two independent paths from one source to one target:

```
              ┌── Raven SPIR-V emitter ─────────────────▶ SPIR-V (A)   ← what the engine uses
.rvn ─IR─────┤
              └── Raven GLSL emitter ──▶ Vulkan GLSL ──glslc──▶ SPIR-V (B)   ← the oracle
```

`SpirvDifferentialTests` compiles both, disassembles both with `spirv-dis`, and compares the
**host-visible interface**: the `(set, binding)` of every descriptor, every member's `Offset`,
`MatrixStride`, `ArrayStride` and majorness, every stage `Location`, and the entry point's execution
model. Variables are matched by *name*, since the two compilers number ids in their own order — and
a uniform block by the struct name inside its pointer type, because glslang leaves the block
variable unnamed.

**It bites, and that was verified rather than assumed.** Two faults were injected and the test
caught both:

- offsetting GLSL's `set` by one → all four fixtures fail on the descriptor comparison;
- making `float3` align to 12 bytes instead of 16 in `ShaderLayout` — the classic std140 mistake,
  and exactly the failure § D warns about — → the packing fixtures fail on member offsets.

The second is the interesting one. Raven's SPIR-V offsets come from `ShaderLayout`; the GLSL's come
from **glslang computing std140 itself**. So the layout engine is now checked against a second,
independent implementation of the same spec, not only against the literals in `ShaderLayoutTests`.

Honestly bounded:

- **Instruction streams are not compared.** glslang structures a body differently for the same
  meaning, so a body-level diff would be noise. Arithmetic correctness is the numeric BRDF tests'
  job (§ G), and that is why both techniques are in the plan.
- **A bug in the shared IR shows up in both paths and stays invisible here.** Both emitters read the
  same lowered IR; the oracle compares emitters, not the lowering.
- **`ArrayStride` is not yet covered against the oracle.** Raven cannot declare a sized array — its
  `array_rank_specifier` is `[]` only — and an unsized array is not legal in a uniform block. The
  stride rules are pinned against the spec as literals, but not against a second implementation.
- **The tools are found on PATH, not restored.** `glslc` (brew install shaderc) and `spirv-dis`
  (brew install spirv-tools); the CLI tools rather than `Silk.NET.Shaderc`, so shaderc's native
  binaries never enter the restore graph of a project that must not ship them. Absence is reported
  through the test output rather than silently passing — the same treatment `spirv-val` already had.

The weaker half is asserted separately, so a failure reads as *"the GLSL does not compile"* rather
than *"the interfaces differ"*: `glslc` accepting Raven's output at all is a full GLSL front end
reading every line the emitter produced, which is a check the GLSL path never had before — its
`glslangValidator` test had been silently skipping.

### D. Public API contract the engine codes against

The library surface the engine binds against, and the two artefact formats it loads. Detailed under
[§ The contract Raven must satisfy](#the-contract-raven-must-satisfy).

| | Requirement | |
|---|---|---|
| 🔴 | `RavenReflection` with **explicit `Offset`, `Size`, `ArrayStride`, `MatrixStride` on every block member.** The engine writes constant buffers by generated offset, not by runtime reflection. Get the std140/std430-vs-HLSL packing rules pinned and golden-tested or every backend disagrees about `float3` padding | ✅ |
| 🟡 | `.rvnlib` (compiled library: symbols + IR, referenced without reparsing source) and `.rvnfx` (compiled effect: modules + reflection + permutation key + source hash) artefact formats | ✅ both |
| 🟡 | **Incremental reparse** via `SourceText.WithChanges` — the < 500 ms shader hot-reload budget ([00](00-vision-and-principles.md)) depends on it. Comes free from `Vixen.Core.Syntax` | ✅ including green-node reuse at member granularity |
| 🟡 | Diagnostics surfaced through the shared model so the editor's error list, the engine log, and the on-screen shader-error overlay all use one implementation | ✅ via `Vixen.Core.Syntax` |
| 🟡 | Accept **generated** source with span fidelity, so `Vixen.Editor.ShaderGraph` can emit Raven and map diagnostics back to node ports ([11](11-editor.md)) | ✅ `ParseText(text, path:)` already |
| ⚪ | "Interaction classes" (Raven's Phase 7) feed `Vixen.Shaders.Generators`, which emits the C# `ParameterKey`/`PermutationKey` classes | ✅ everything Raven owes it; the generator itself is engine-side |

**The layout engine is shared, and that is the point.** `Vixen.Raven.Reflection.ShaderLayout` is the
only implementation of the packing rules. It was private to the SPIR-V backend as `Std140Layout`;
lifting it out and generalising it to `std430` means the SPIR-V `Offset`/`ArrayStride`/`MatrixStride`
decorations and the reflection the engine writes buffers from are computed by the same code. Two
copies is how backends come to disagree about `float3` padding, so there is one.

Verified rather than assumed: for a block of `float4, float, float3, mat4` the reflection reports
offsets 0/16/32/48 with size 112 and `MatrixStride 16`, and the emitted SPIR-V decorates
`Offset 0/16/32/48` and `MatrixStride 16`. `ShaderLayoutTests` pins the numbers themselves against
the spec as literals — `float3` aligning to 16 while occupying 12, `float[4]` costing 64 bytes in
std140 and 16 in std430, a matrix's stride following its column count.

**Matrices are column-major** in the shader, and that is now reconciled with ADR-003's "row-major
storage" rather than merely flagged: the two describe the same bytes from the host's and the shader's
side, and they compose to exactly `mul(v, M)`. The derivation, and the matrix *indexing* fix that the
earlier flag correctly guessed was the real problem, are in
[§ E](#e-conventions-raven-must-bake-in).

**Reflection comes from the IR, never from parsing emitted output back.** So a value behind a false
`[Permutation]` is already gone and the reported interface is the one this variant actually has.
`raven compile --emit-reflection` writes it as JSON; `--capabilities` prints the required features
per shader.

**`.rvnfx` is done.** A magic number, a version, a JSON header and the modules' bytes appended
raw. The split is deliberate: the header is JSON so a shipped artefact is inspectable without a
bespoke viewer and can grow fields, while SPIR-V goes in verbatim because base64 would cost a third
of its size for nothing. The reader rejects a wrong magic, an unknown version and a truncation
rather than half-loading — a partly-read effect surfaces as a driver error with no trace of the real
cause. `raven compile --emit-effect` writes one per shader.

The `PermutationKey` in it holds **only the keys that were read**, with the values they were read as.
That is the economy of the whole permutation system: two variants differing only in an unread flag
produce the same key and share one artefact, instead of filling the cache with duplicates.
`SourceHash` is SHA-256 over the sources, so a stale artefact is detectable without recompiling to
compare.

**`.rvnlib` was a phase rather than a task.** What held it back was never serialization: for a library
to be "referenced without reparsing source", its loaded symbols have to *participate in binding* as
`NamedTypeSymbol`/`MethodSymbol`/`FieldSymbol` — a second symbol hierarchy backed by metadata rather
than syntax, Roslyn's source-vs-PE-symbol split at Raven's scale. [Doc 18](18-raven-parser-migration.md)
put the parser migration first because serialising trees wants trees you trust; both landed, in that
order.

Two halves in one artefact, and both are load-bearing:

- **Declarations.** `Symbols/Metadata/` derives from the same abstract bases the source symbols do, so
  nothing in the binder, the conversions, the overload resolution or the `compose` resolver can tell
  the difference. A call into a library type-checks on exactly the terms a call within the compilation
  does.
- **Lowered IR.** `Lowerer.Linking` rebuilds the library's functions in the module being compiled and
  maps each metadata symbol onto the function its body lowered to. `LowerCall` finds a callee in the
  same dictionary either way, both backends see one module of ordinary IR, and a reference costs
  nothing at runtime for the same reason `compose` does — it is resolved before the backend runs.
  `CompiledLibraryTests` pins that a linked function's IR dump is identical to lowering its source
  alongside the consumer, over a fixture exercising every statement and access shape the IR has.

The halves are linked by name (`LibraryMethod.IrFunction`, `LibraryType.IrStruct`), because a name
survives a recompilation of the library and an index does not. The container is **all JSON**, unlike
`.rvnfx`: a library has no binary payload — its bodies are structure, not bytes — so nothing needs
keeping out of the text, and a diffable artefact beats the space. Framing and rejections are the same.
`--emit-library` writes one; `--reference Core/Math.rvnlib` consumes it.

**Three things are refused at write time, because that is where they can be fixed** rather than
rediscovered in every consumer:

- A body that reads a **shader binding** (`RVN5001`, transitively): a binding's `(set, binding)` is
  assigned per effect, so linking its reader elsewhere would name storage that shader never declared —
  the same silent GLSL miscompilation unflattened inheritance produced.
- A body that touches a **stream** (`RVN5007`), for the neighbouring but different reason that a
  stream's location belongs to the consuming shader.
- A **`[Permutation]` key read while building** has its value baked in (`RVN5006`) — the one thing an
  artefact cannot carry, because the key is resolved at compile time so the dead branch is gone before
  the body is written down. Said rather than discovered: the symptom otherwise is a consumer's
  `--define` that appears to be ignored.

Entry points are not exported either (`RVN5002`, informational): a library supplies types and
functions, and a stage is generated per effect from the shader that declares it.

**A reference is not a tax on the output.** A library's whole IR must be present before any body is
lowered — a body may call anything in it — and `ImportPruner` then drops what nothing reached. Not the
backends' reachability walk, which is per entry point and decides what one *unit* emits; this decides
what the *module* holds, so the IR dump, the verifier and `IrCapabilities` describe the shader that was
compiled rather than the library it borrowed one function from. Without it, referencing a library with a
`double` anywhere in it would make every consumer require `Float64` — the exact mistake § B avoids.

**Libraries compose, and source wins.** A library built against another records the dependency by name
and the consumer resolves it against its own references (`RVN5004` when it cannot), reaching the *same*
struct object the consumer's locals are typed by rather than a private copy that would fail the
verifier. A source type shadows a referenced one of the same name — what every compiler with a
reference model does — and the shadowing is reported (`RVN5003`), because silently preferring one of two
same-named types is how a shader ends up bound against the definition its author was not reading.

Honestly bounded:

- **IR names are one flat namespace per module**, so two libraries exporting the same IR name collapse
  to the first. That is a property of the IR — one `Structs` list, one `Functions` list, names global to
  both emitters — not something linking introduced; the fix is qualifying IR names by declaring type,
  which is its own change. A library entity whose name the compilation itself uses gives way, and only
  then is it renamed, so the GLSL in a frame debugger still says `Saturate`.
- **A generic library type is exported but still not lowerable** — `Box<float>` is `RVN3001`/`RVN3003`
  either way (§ J) — so its type parameters and enforced `where` clauses round-trip and nothing more.
- **The libraries themselves are not written.** § F is the content task; this is the mechanism it needs.

*A defect this uncovered, and not in the new code:* a `static func` on a struct was still given a `self`
parameter, so calling one from outside the struct passed one argument to a function taking two —
malformed IR, which means the construct could not be compiled at all. Nothing in the corpus called a
static struct method across a type boundary; a struct of static helpers is exactly what `Core/Math.rvn`
is, so it surfaced immediately. `SelfTypeFor` and `BuildArguments` now agree that a static member has no
receiver.

**Incremental reparse, and it really is incremental now.** `SourceText.WithChanges` applies sorted,
non-overlapping edits in the old text's coordinates and remembers where the result differs;
`GetChangeRanges` answers exactly for the immediate predecessor and conservatively — whole document —
for anything else, because being silently wrong about a region would let a reparser trust a subtree the
edit had invalidated. `SyntaxTree.WithChangedText` is the hot-reload entry point.

An earlier draft recorded that this "comes free from `Vixen.Core.Syntax`" was false, because ANTLR
owned parsing and had no notion of reusing a tree — the concrete cost of the parser being generated
rather than hand-written, and one of the reasons [doc 18](18-raven-parser-migration.md) argued for the
migration. Both landed: the parser is hand-written, and `Vixen.Core.Syntax.Parsing.Blender` reuses green
nodes at **member granularity**, shared by all three front ends as the doc wanted.

Two properties make it safe rather than merely fast. A candidate survives only when no change touches
its old full span **with a character of margin on each side**, so an edit that is merely *adjacent*
cannot glue itself onto the node's first or last token — `var tint: float4` gaining ` => tint` at its
end becomes a property, which wholesale reuse would have missed. And the parser re-verifies that a
reused node's width lands on a token boundary of the new stream, so a candidate that fails either check
is simply reparsed: **the blender can only make a parse faster, never different**, which is pinned by
comparing every incremental result against a full parse.

**Reported as absent rather than guessed:** `PushConstants` and `SpecConstants` are always empty.
Raven has no syntax for push constants, and a `[Permutation]` key is resolved at compile time rather
than left specialisable — that is what makes the dead branch disappear. An empty array is honest; a
fabricated one would be a bug the engine could not see.

**What a shader can be varied by is reported separately from the cache key,** and the separation is the
point rather than duplication:

| | Holds | Same across variants? |
|---|---|---|
| `Permutations` / `ValueParameters` | what the shader **declares** — key, type, and a default for a permutation but not for a value parameter (`RVN2082`) | yes, necessarily |
| `UsedPermutationKeys` | what this variant **read** | no — that is the whole economy |

Generating a C# key class from the *read* set would give an API whose members depend on which variant
happened to compile: build with `UseDetail=false` and a key only read in the true branch vanishes from
the surface. So the declared set is what `Vixen.Shaders.Generators` and doc 06's build-time permutation
pre-generator consume, and `ReflectionTests` pins that a declared-but-unread key is still reported and
that the declared set is byte-identical across two variants whose read sets differ.

Two things follow from folding erasing the evidence. A `[Permutation]` key leaves **no trace in the
lowered IR** — that is what makes the dead branch disappear — so `IrShader` records what was declared
before folding erases it, keeping reflection IR-derived rather than reaching back into the symbol table.
And because reading a key's value is exactly what *records a use*, lowering reads
`FieldSymbol.DeclaredValue` instead: describing a shader must not add keys the body never touched, or
the cache fills with variants that differ in nothing.

Defaults travel as **text**, matching `CompiledEffect.PermutationKey`: a boxed `object` survives
`System.Text.Json` as a `JsonElement` and stops comparing equal to what went in, which would make a
round-trip quietly lossy. The type is in the same record, so nothing is lost by rendering the value the
way a `--define` spells it.

### E. Conventions Raven must bake in

Get these wrong and every shader is subtly incorrect in a way that is painful to find later.

| | Convention | |
|---|---|---|
| 🔴 | **Right-handed, Y-up, row-vector with row-major storage** (`M11..M44`, translation in `M41..M43`), i.e. HLSL's `mul(v, M)` (ADR-003) | ✅ settled and pinned |
| 🔴 | **Reverse-Z, depth range 0..1** | ✅ nothing to do, and now asserted |
| 🟡 | UV origin top-left | ✅ `OriginUpperLeft` |
| 🟡 | Linear working space; sRGB decoded on sample; HDR render targets | not the compiler's — format and § F |
| 🟡 | `Random.rvn` must match the CPU implementation **bit-for-bit** — the VFX system compiles one graph to both a C# job and a Raven compute shader, and their outputs are compared in a test ([06](06-rendering-pipeline.md)) | § F; the compute stage landed, but reading the result back needs a writable resource |

Two of these are the compiler's to bake in, and both are done. The other three are not, which is worth
saying plainly rather than leaving them looking outstanding.

#### The matrix convention, settled

ADR-003 said "column-vector convention with row-major storage … matching HLSL's `mul(v, M)`", and
§ D previously flagged that as reading like a contradiction against a backend that decorates matrices
`ColMajor`. It resolves into one wrong word and one thing that was never a contradiction.

**The wrong word.** `mul(v, M)` puts the vector on the left and a translation in `M41..M43` is the last
*row*: both are the **row-vector** convention, which is what Stride and HLSL do and what the
implementation does. ADR-003 now says row-vector.

**The part that was never a contradiction.** Row-major storage is a statement about the host's bytes;
`ColMajor` is a statement about how the shader reads them. They are the same bytes:

```
host, row-major:   [M11 M12 M13 M14][M21 M22 M23 M24][M31 M32 M33 M34][M41 M42 M43 M44]
                    └── row 1 ────┘                                    └── translation ┘

shader, ColMajor,  [── column 0 ──][── column 1 ──][── column 2 ──][── column 3 ──]
MatrixStride 16:                                                     └── translation ┘
```

So the matrix the shader sees is **Mᵀ**, obtained for free — no instruction transposes anything, the
same 64 bytes are simply indexed differently. And then

> `m * v` = Mᵀ·v = (vᵀ·M)ᵀ = **`mul(v, M)`**

which is exactly ADR-003's multiplication order. Raven's existing emission already implemented the ADR
precisely; nothing had to change. The translation lands in column 3, which is where `Mᵀ·v` expects it.

**The convention that follows for shader source: matrix on the left.** Write `world * position`.
`position * world` also compiles — it emits `OpVectorTimesMatrix` and computes the *untransposed*
matrix applied to a column vector, which is a different and usually wrong transform. It stays legal
because it is meaningful when deliberate, but the library and the shader graph emit matrix-first.

**`m[i]` is a column** — `IrMatrixType.ColumnType`, as many lanes as the matrix has rows. This closes
the 🔴 defect that § I inherited, and the choice made itself once the bytes were understood:

- Both targets index a matrix by column, so a column is free and a row would need a gather.
- Because the shader's matrix is the host's transpose, the shader's column *i* **is** the host
  matrix's row *i* — so the free answer is also the intuitive one for anyone thinking in terms of the
  `Matrix4x4` they wrote on the CPU.
- Matrix construction already filled columns in both backends, so construction and indexing now agree:
  `mat3(a, b, c, …)` fills the column that `m[0]` reads back.

A non-square matrix is now a differential-oracle fixture, because on a square one a row and a column
have the same lane count — which is exactly why the defect survived so long.

#### What is pinned, and what is still owed

`ConventionTests` reads the emitted artefact rather than restating the convention: the `ColMajor`
decoration and `MatrixStride`, the reflection agreeing about both, `OpMatrixTimesVector` for
matrix-first and `OpVectorTimesMatrix` for the other order, columns for construction and indexing, and
`OriginUpperLeft` with no `DepthReplacing`. The `mul(v, M)` derivation above is a test too — it parses
the stride and majorness **out of the compiled module** and uses them to unpack a host matrix, so
switching the emitter to `RowMajor` fails it rather than silently invalidating this section.

Still owed, and not the compiler's to give:

- **Reverse-Z** lives in the host's projection matrix. Vulkan's depth range is already 0..1, so there
  is nothing for Raven to bake in — only something to avoid disturbing, which is asserted.
- **Linear working space, sRGB decode, HDR targets** are image-format decisions plus `ColorSpaces.rvn`
  in § F. A shader never decodes sRGB itself; the view format does.
- **`Random.rvn` bit-for-bit** needs § F's library, a CPU port to compare against, and a writable
  resource to read the GPU side back out of. The compute stage itself is no longer the blocker. It is a
  § F exit criterion, not a § E one.
- **Numeric agreement on a real device** — the GPU-readback tests in § G. Everything above pins the
  *convention*; only a device proves the arithmetic.

### F. The shader library to write *in* Raven — Phase 5, ~the largest content task

`Raven/Library/` becomes a shipped, version-locked artefact compiled by the Nuke `CompileShaderLibrary`
target. Full tree in [§ Source layout](#source-layout-what-is-written-in-raven).

#### ✅ Written: 44 files across all eight packages

`LibraryTreeTests` holds the tree to four claims, each failing differently: every file parses and
round-trips; the tree binds as **one** compilation, so the library agrees with itself rather than
being files that each happen to compile; **every shader with an entry point reaches both backends**,
with `glslc` and `spirv-val` as the verdict; and a shader compiles against the free-function packages
through `.rvnlib` references.

| Package | Files |
|---|---|
| `Core/` | `Math` (constants, `SafeNormalize`, branchless basis, spherical, octahedral, matrix-first transforms) · `ColorSpaces` (sRGB exact and cheap, Rec.709/2020 luminance, Reinhard, ACES, AgX, PQ, YCoCg) · `Random` (PCG hash, uniform floats, sphere/hemisphere/disk) · `Sampling` (radical inverse, Hammersley, Halton, concentric disk, cosine hemisphere, GGX importance sampling) |
| `Shading/` | `Brdf` (the D/V/F primitives and `ShadingAngles`) · `DiffuseModels` · `SpecularModels` (GGX, anisotropic, Beckmann, multi-scatter, horizon occlusion) · `ClearCoat` · `Sheen` · `Hair` · `Subsurface` · `Transmission` · `Ibl` (split-sum DFG fit, SH9 irradiance, parallax-corrected probes) · `Lighting` (punctual and sphere lights, both shadow biases, PCF, cascade fade) |
| `Geometry/` | `Transform` (the spaces, depth reconstruction, reprojection) · `Normals` (tangent frames, one- and two-channel decode, whiteout blend, geometric normal) · `Skinning` (linear and dual-quaternion) · `Instancing` (packed transforms, per-instance variation) · `Displacement` (height, Gerstner waves, wind, parallax occlusion) |
| `Material/` | `MaterialSurface` (the `inout` contract and five features) · `ComputeColor` (the shader-graph vocabulary: blend modes, ramps, UV nodes, value noise) |
| `Pipeline/` | `ForwardPlus` · `Deferred` · `GBuffer` (the encoding) · `DepthOnly` · `ShadowCaster` |
| `PostFx/` | `Fullscreen` · `Tonemap` (+ grading and LUT) · `Bloom` (Jimenez down/up, Karis average) · `Fxaa` · `Ssao` (GTAO horizon search, bent normals) · `Taa` (reprojection, YCoCg variance clipping) · `Fog` · `Vignette` (+ aberration and grain) · `Sharpen` (CAS) · `Outline` |
| `Ui/` | `UiQuad` (and the premultiply/clip/SDF conventions) · `Msdf` · `RoundedRect` · `Blur` · `Gradient` |
| `Vfx/` | `ParticleBillboard` (three facing modes, sub-UV) · `ParticleRibbon` · `ParticleSimulate` (the forces and integrator) |

Free functions are `static func` on a field-less struct, which is Raven's only shape for one — and it
is what makes a package exportable: `RVN5001` refuses to export a function that reads a shader
binding, so "the package exports cleanly" and "the package is written as free functions" are the same
statement. That splits the tree into **two shipping models**: `Core`, `Shading` and `Geometry` ship as
`.rvnlib` references; `Material`, `Pipeline`, `PostFx`, `Ui` and `Vfx` are shaders with bindings and
compose slots, so they ship as source and are compiled with their consumer.

**Conventions written into the code rather than left to callers**, in each case because the wrong
version also compiles and looks plausible: matrix-first (§ E), roughness squared exactly once at
`Brdf.Alpha`, the `4·NdotL·NdotV` denominator inside the visibility term so `D*V*F` is the whole BRDF,
premultiplied alpha for everything in `Ui`, and linear depth — not device depth — for every
depth-difference test.

**What the content exercised, beyond itself.** The `.rvnlib` path had only ever run against fixtures.
On real content, across packages, two properties held that only appear at this scale: a function
reached through several references keeps **one identity** (`Math.SafeNormalize` arrives three ways and
is emitted once — the one-shared-IR-decoder decision in § D paying off), and referencing a library
**does not enlarge** the shader. Plus the stronger claim: a function read out of a `.rvnlib` lowers to
**identical IR** to compiling its source alongside, without which a library is a source of divergence
between a developer build and a shipped one.

#### Four defects the library found

Every one was a silent or asymmetric failure that a passing test suite had not reached, because
nothing in the fixtures was shaped like real library code.

| Found by | Defect |
|---|---|
| every file | **GLSL emitted `struct S { };` for a field-less struct**, which is a syntax error — and a field-less struct is exactly how Raven spells a namespace of free functions. Fixed by dropping the declaration; nothing can use the type as a value |
| `Geometry/Skinning.rvn` | **A struct declared after its first user had no fields when that user's body lowered**, so a field write was `RVN3003` "no storage the target can address" — on ordinary source the binder resolves perfectly. Fixed by populating every struct's fields before any body lowers |
| `Pipeline/GBuffer.rvn` | **An aggregate stage output emitted `out SomeStruct` in GLSL** while SPIR-V correctly reported `RVN4001`. One backend noticing and the other not is the shape worth removing; both now read one shared `StageInterface` predicate |
| `Ui/RoundedRect.rvn` | **GLSL's "reserved for future use" words were not mangled** — a local called `half` is good Raven and produced GLSL `glslc` rejects outright. All 39 added |

#### What the library could not express

Each of these shaped a file rather than blocking it, and each is recorded in the table at the top.

- **No sized array types**, so nothing can index a buffer. `Lighting.rvn` has the per-light maths but
  not the clustered light loop — which is what "forward *plus*" names — and `Skinning.rvn` takes four
  explicit bone matrices instead of a palette. Four influences is what glTF stores, so the limitation
  costs less than it sounds, but the loop is host-side for now.
- **No writable resources**, so `Vfx/ParticleSimulate.rvn` is the forces and the integrator rather
  than the compute shader it should be. Written as free functions over a `Particle` value, which is
  also the form that makes doc 06's CPU/GPU bit-for-bit comparison a transliteration.
- **No multiple render targets** — an entry point returns one value. So `GBuffer.rvn` is the *encoding*
  and not the geometry pass that fills it; `Deferred.rvn` reads through the same `Decode`, so when MRT
  arrives there is one place for the two passes to agree.
- **No `SampleLevel`**, so nothing can select a mip explicitly. `Ibl` computes the LOD a caller should
  use and `Bloom` runs per mip instead, which works but means the prefilter contract is a comment
  rather than a call.
- **No `discard`**, so `DepthOnly` and `ShadowCaster` return zero and rely on the host's colour write
  mask.
- **No `GetDimensions`**, so `Msdf` hard-codes its atlas width where it should query it.
- **A texture cannot be a struct field** (`RVN2053`, correctly — a descriptor is not a value), so
  `GBuffer.Sample` takes three texture parameters rather than a bundle.
- **No line continuation**, which shaped every signature in the tree: anything over one line becomes a
  block body with named locals, and a long parameter list becomes a struct. `ShadingAngles`,
  `SpotLight`, `ProbeBox`, `BackLight`, `ParallaxRay` and `BonePalette` all exist partly for that
  reason — though each turned out to be better design anyway, since those values do travel together.

#### ✅ `inout`, and `Material/MaterialSurface.rvn`

The composition table below specifies
`protocol IMaterialSurface { func Compute(inout MaterialData d) }`, and `inout` did not exist at any
layer — no token, no syntax kind, no symbol. It does now, and `Material/MaterialSurface.rvn` is
written as specified: the `MaterialData` surface, the protocol, and five features that contribute to
it (metal-roughness, specular-glossiness, normal map, emissive, occlusion).

The alternative was to return the struct — `func Compute(d: MaterialData): MaterialData` — which was
expressible already. What decided it against: a feature accumulating into a shared surface *is* a
mutation, Stride's model is mutation, and both targets support the real thing natively, so faking it
with a fold would have been a divergence between what the source says and what the target does. (The
sketch's `inout MaterialData d` is C-style, incidentally; Raven's parameter syntax is
`d: MaterialData`, so the specification was never quite Raven either.)

**Copy-in/copy-out is the definition, not the implementation.** GLSL specifies its own `inout` the
same way and SPIR-V has no reference type, so a promise of aliasing could not have been honoured on
either target. Two `inout` arguments naming the same storage therefore do not interfere until the
copies are written back, in argument order.

**The argument's type must match exactly**, which is the rule that surprises people. A widening on
the way in would have to narrow on the way out and lose what the callee wrote, so `int` to
`inout float` is `RVN2111` rather than a silent round trip. Checked *after* overload resolution
rather than as part of it: direction distinguishes nothing at a call site, and folding it into
applicability turns "you passed a literal" into "no overload applies". Also refused: `inout` on an
entry point (`RVN2112` — the pipeline has nowhere to copy back to), on an operator (`RVN2114` — an
expression has no syntax for it), and with a default (`RVN2113` — an omitted argument has no
storage). A `val` argument reuses the assignment's own read-only message rather than inventing one.

**The call site always copies through a function-scoped temp, and that is forced rather than
chosen.** SPIR-V requires a pointer argument to `OpFunctionCall` to be a *memory object declaration*,
so an access chain such as `d.color` cannot be handed over at all, and a global's storage class could
never match the parameter's `Function`. Narrowing the IR to the one shape both targets accept —
`IrArgument` is a value or a whole local — is what keeps each backend from inventing its own way to
cope. GLSL then emits its native `inout` and copies a second time into the parameter, which is
redundant and free. The IR verifier checks direction agreement both ways, because a value where a
reference belongs loses the write and a reference where a value belongs is a pointer `spirv-val`
rejects.

The `.rvnlib` format went to **version 2**: a parameter carries its `RefKind` and a call's arguments
became objects rather than bare value ids. Both halves have to carry the direction — the symbol side
so a consumer's binder still demands assignable storage, the IR side so the linked body still
declares a by-reference parameter — and a version-1 artefact is now rejected by version rather than
by a confusing JSON error.

#### ✅ A `compose`d feature now contributes its interface

Writing `Material/` found that `compose` had been half-implemented all along: it resolved the slot,
called the right function and pruned the rest, but the implementation's **bindings** never reached the
consuming translation unit. The feature's material parameters live on its own `IrShader`, the emitter
declared only the consuming shader's, and the GLSL therefore named identifiers it never declared —
`glslc` rejected it and Raven said nothing, the same shape as the three inheritance defects in § J.

It survived a passing suite for one reason: nothing in the tests composed an implementation that
declared a `var`. `compose` worked for a stateless feature, and every real feature has parameters.

**Merged in lowering, not in the emitters.** `MergeComposedInterfaces` gives each shader the bindings
and streams of the shaders it composes, transitively, before anything reads the module — so
`BindingPlan`, `StreamPlan` and the reflection all see one answer. Two emitters each patching their
own interface is exactly how they come to disagree. The merge reuses the *same* `IrVariable` the
implementation's body was lowered against; a copy would leave the body reading storage the consumer
never declared, which is the original bug with an extra step.

**Every binding the feature declares, not only the reached ones** — matching how a shader's own unused
bindings are already kept. A descriptor set layout is what the host writes against, and a material
parameter vanishing from the reflection because this variant happened not to read it is a far worse
failure than a spare slot.

**Contributed bindings are qualified by the shader that declares them** — `MetalRoughnessSurface.roughness`,
and `Layered.Ggx.alpha` for a transitive one. This is not decoration: features are authored
independently and collide, and three of the five in `MaterialSurface.rvn` declare a `strength`. Two
reflection entries with one name is a host writing the wrong offset, or a generated binding with two
properties of the same name. Every contributed binding is qualified rather than only the clashing
ones, because a name that changed depending on what else the material composed would break a host
when an unrelated feature was added. The identifier a backend emits is still derived from the variable
and uniquified per unit — a `.` is not a GLSL identifier — and the two were always free to differ.

**And a second defect underneath it: validation ran as a side effect of resolution.** Transitive
`compose` did not work at all, for an unrelated reason — a shader that both implemented a protocol and
declared a slot of its own was reported as not implementing it (`RVN2076`, on correct source).
`EnsureMembers` ran the shader checks, so resolving the middle shader's base list reached the outer
shader's compose check, which asked the middle one for its interfaces while `EnsureBases` was still
mid-flight; the reentrancy guard answered with the empty list it had built so far. The fix is an
invariant one line long — **nothing reachable from resolution validates** — implemented as
`SourceNamedTypeSymbol.EnsureValidated` and a second pass in `SemanticModel`. A check asking another
type for its bases now either finds them resolved or resolves them, and neither can re-enter.

Two smaller gaps recorded rather than fixed: **the CLI takes a single input file**, so composing
against a library *source* file (as opposed to a `.rvnlib` reference) is only reachable through the
API; and **`Material/` cannot ship as a `.rvnlib` at all** — `RVN5001` correctly refuses to export a
function that reads a shader binding, so the tree has two shipping models, free-function packages by
reference and shader packages by source.

One limitation that is a design consequence rather than a defect: two slots filled with the *same*
implementation share one set of parameters, because the implementation is one shader with one set of
storage. Per-slot parameters would mean instantiating the implementation per slot, which is
monomorphisation and not what `compose` does.

#### Smaller things the library ran into

- **No line continuation.** Raven is newline-sensitive and an expression cannot wrap, so anything
  over one line becomes a block body with named locals. Same root cause as a parameter list not being
  able to span lines, which is why `Library/Example2.rvn` declares its two compute parameters on one
  long line. Not a defect, but it shapes how the library reads and is worth a decision if the library
  is to grow much further.
- **No `asfloat`/`asuint`.** No bit-reinterpretation intrinsic, though SPIR-V already emits `OpBitcast`
  for int↔uint conversion and GLSL has `uintBitsToFloat`. `Random.rvn` does not need it — its float
  conversion is a shift and a multiply by 2⁻²⁴, which is exactly reproducible on the CPU and therefore
  *better* for the bit-for-bit requirement than the usual `asfloat` bit-stuffing — but packing work in
  `Math.rvn` will want it.
- **A shift count must be `int`.** `a >> s` with `s: uint` is `RVN2020`, where both targets accept an
  unsigned count.
- **Range `for` is inclusive.** `for (i in 0 .. 4)` runs five times, Kotlin-style. Correct and
  documented, but it is the kind of thing a library gets wrong once.

### G. Testing and CI additions

The full testing story, by layer and with the status of each. This is the only testing table in the
document; an earlier draft carried a second one at the end that said the same things differently, which
is how two lists come to disagree.

| | Layer | Test | |
|---|---|---|---|
| 🟡 | Parse | Golden-tree and round-trip corpus over **the whole `Raven/Library` tree** — every shipped shader round-trips byte-identically | ✅ mechanism: the corpus walks the tree recursively, so each file § F adds is covered on arrival |
| 🟡 | Semantic | Positive/negative fixture pairs per diagnostic ID; `compose`-resolution golden trees per material-feature combination | partial — most IDs have a trigger, few have the negative |
| 🟡 | SPIR-V | `spirv-val` on every emitted module; golden `spirv-dis` snapshots so codegen changes are reviewable | ✅ |
| 🔴 | Both emitters | **Differential test**: Raven's SPIR-V vs `glslc`(Raven's GLSL), compared for semantic equivalence — the hard class of bug, an emitter internally consistent and semantically wrong | ✅ interface-level; blind to the shared IR, hence the numeric tests |
| 🟡 | Cross-compile | Every module through SPIRV-Cross to GLSL 450 / ESSL 300 / HLSL 60 / MSL / WGSL without error; GLSL/ESSL additionally through `glslang` | not started |
| 🟡 | Numeric | BRDF functions ported to C# and compared against a GPU compute readback over a parameter sweep, agreeing to 1e-4 — the test that catches "the shader is subtly wrong" | blocked on § F and on a writable resource; the compute stage itself landed |
| 🟡 | Layout | Reflection offsets against a GPU readback of a known pattern, **per backend** | needs a device |
| 🟡 | Permutations | An unused define produces a byte-identical module and the same cache key | ✅ |
| ⚪ | Fuzz | `SharpFuzz` corpus over the Raven parser, alongside the VXML/VCSS/`.meta`/bundle readers ([12](12-build-ci-and-testing.md)) | not started |
| ⚪ | Perf | Gates on full-library compile time and < 500 ms incremental recompile of a leaf shader | needs § F |
| ⚪ | CI | Nuke `CompileShaderLibrary`: Raven over `Raven/Library/**/*.rvn` → `.rvnlib`, `spirv-val` each, **fail on any diagnostic** | Nuke not stood up ([12](12-build-ci-and-testing.md)) |

### H. Burden the plan *removes* from Raven

Worth stating, because it is a net reduction against Raven's own roadmap:

- **No hand-written HLSL, MSL, or WGSL backends** — SPIRV-Cross produces all three (ADR-012). Raven's
  README lists HLSL and Metal as eventual targets; they are no longer needed.
- **No package manager for shaders** — Raven's README floats the idea; `.rvnlib` references plus the
  engine's addressable content system cover distribution.
- **No `shaderc` bridge, no dual-backend abstraction** — building GLSL and SPIR-V together removes both.
  `shaderc` remains only as a test oracle.
- **No Unity-style interaction-class compatibility** — the generated-binding shape is ours to choose
  (Q9).

### I. Gaps carried over from Raven's retired implementation plan

`Raven/docs/IMPLEMENTATION_PLAN.md` is gone; every phase in it was complete, and keeping a second
roadmap alive is how two roadmaps come to disagree. These are the items that were still open in it,
**each re-checked against the code** — a stale gap recorded in the plan of record is worse than none.
Nothing here is engine-blocking except where marked.

#### Syntax fidelity

| | Gap |
|---|---|
| 🔴 | **Four nodes silently drop their tokens.** `RepeatStatementSyntax` has no `repeat`/`while` keywords or parens, `CastExpressionSyntax` no parens, `SelfExpressionSyntax`/`BaseExpressionSyntax` no keyword at all. Fix is the recipe every other node already follows: token slots in `Syntax.xml`, then wire the visitor |
| 🟡 | **String interpolation** — needs lexer modes for embedded expressions. Nothing shipped uses it |
| 🟡 | **Sized array types as type syntax** — `array_rank_specifier` is `[]`/`[,]` only, deliberately, so that `a[i]` is unambiguously element access. Consequence: no sized-array uniform, so § C's oracle cannot check `ArrayStride` against a second implementation |

The first is worse than "loses a keyword", and it is verified rather than inherited. All four parse
with **zero diagnostics** and reprint as something else:

| Source | `ToFullString()` |
|---|---|
| `repeat { x += 1 } while (x < 4)` | `{ x += 1 }x < 4` |
| `(int)b` | `intb` |
| `self.b` | `.b` |
| `base.b` | `.b` |

The output is not merely different, it is **not Raven** — and `self` and `base` become
indistinguishable from each other. None of the four is in the round-trip corpus, which is why nothing
catches it. This blocks step 1 of [doc 18](18-raven-parser-migration.md) (a frozen corpus that omits
them is not a safety net) and anything that reprints the tree: a formatter, a refactoring, or the
shader graph's generated-source span mapping.

#### Semantics and lowering

| | Gap | |
|---|---|---|
| 🔴 | **`m[i]` meant a row in the IR and a column in both targets** | ✅ fixed in [§ E](#e-conventions-raven-must-bake-in) |
| 🟡 | **`&&` and `\|\|` do not short-circuit.** They lower to `logicalAnd`/`logicalOr`, which evaluate both operands, as `?:` lowers to `select`. Sound for the side-effect-free expressions shaders are made of; wrong the moment the right operand is a guard (`i < n && data[i] > 0`) |
| 🟡 | **Stream I/O declarations between stages** — no `stream` keyword; interstage data passes as entry-point parameters and returns | ✅ built; see [§ Streams](#streams-interstage-values-declared-once) |
| 🟡 | **`Buffer<T>`-style resources** — the built-in named types are not generic, so there are no storage buffers. This is also why `DescriptorType.StorageBuffer` and `LayoutRule.Std430` exist in the reflection with nothing that produces them, and why the compute stage has nothing writable to store into |
| ✅ | **Kept in the language but not lowered** — resolved by Tier B: `switch`, operators and tuples are finished, the rest are dropped |
| 🟡 | **Inheritance is not flattened** — a base's fields never reach the derived layout and an `override` does not replace the base's member. Now `RVN3002` instead of three silent miscompilations; see the mixin section for what implementing it would cost |
| 🟡 | **Generics do not lower at all** — not the open definition and not an instantiation either: `Box<float4>` is `RVN3001` the same as `T` is, because there is no monomorphisation. They parse, bind, and enforce `where` clauses, then stop. Found by making `Example1.rvn` bind |
| 🟡 | **A spread element in a collection cannot be lowered** — flattening `[1, ..xs, 5]` needs `xs`'s length and an array type carries none. It built an `array<i32>` operand where the construct wanted an `i32`, and only the IR verifier stood between that and a backend; now `RVN3002`, gated on sized arrays |
| ⚪ | **Assigning to a uniform is refused by nobody** — every stage emits the store and both reference compilers reject it. Pre-existing and stage-independent; compute made it visible by having nothing else to write to |
| ⚪ | **Flow analysis** — definite assignment and reachability. Dead-branch elimination landed in § B, but that is constant folding, not reachability |

#### Backends

| | Gap |
|---|---|
| 🟡 | **Reading a whole struct out of a uniform block** (`RVN4002`, SPIR-V). Its laid-out type is a distinct type from the plain one, so it needs a member-by-member copy that is not built. Field-by-field reads — what lowering actually emits — are unaffected |
| 🟡 | **A boolean in a uniform, or a boolean/aggregate as stage I/O** (`RVN4001`). `OpTypeBool` has no size and no memory layout. Reported rather than mis-emitted, but note the targets **disagree about what is legal**: GLSL hides it by giving a bool four bytes in a std140 block |
| 🟡 | **Unsized arrays** (`RVN4001`) — legal only as a storage block's last member, which the IR cannot express |

**The matrix indexing defect — fixed.** `m[i]` was typed as a *row* while both targets index by
column: SPIR-V refused to emit it (`RVN4002`) and GLSL emitted the wrong thing silently. It read as a
language decision needing a coin-flip (HLSL indexes rows, GLSL columns) and was not — once the
byte-level relationship between host and shader storage was worked out, exactly one answer was free in
both backends *and* the intuitive one. The derivation is in
[§ E](#e-conventions-raven-must-bake-in).

#### The compute stage: a workgroup size, the dispatch ids, and no interface

`[ComputeShader(8, 8, 1)]` — one to three positional dimensions, the rest 1. On the stage attribute
rather than an attribute of its own, so the size cannot be separated from the stage it sizes, written
twice with two answers, or left behind on a declaration whose stage attribute was removed.

**Required, not defaulted to `(1, 1, 1)`.** A default compiles, runs, and is wrong by whatever factor
the author assumed — one invocation per workgroup where 64 were intended reads past every tile — and
nothing downstream could distinguish a guessed size from a chosen one. `RVN2104` for absent,
`RVN2105` for unreadable, kept distinct because reporting "no workgroup size" for
`[ComputeShader(0)]` sends the author looking for the wrong thing. `RVN2106` warns on a graphics
stage, per the RVN2091 policy.

**A compute stage has no pipeline interface**, which is the part that made enabling it more than
deleting two `RVN4002`s. Nothing feeds a parameter from a vertex buffer and no framebuffer takes a
result, so a return value and a plain parameter are both `RVN2107`, and every parameter carries one of
four dispatch built-ins — routed through the existing `[Semantic("…")]` that already carries
`SV_Position`, because a compute built-in is the same mechanism rather than a new one:

| `[Semantic(…)]` | Type | GLSL | SPIR-V |
|---|---|---|---|
| `SV_DispatchThreadID` | `uint3` | `gl_GlobalInvocationID` | `GlobalInvocationId` |
| `SV_GroupID` | `uint3` | `gl_WorkGroupID` | `WorkgroupId` |
| `SV_GroupThreadID` | `uint3` | `gl_LocalInvocationID` | `LocalInvocationId` |
| `SV_GroupIndex` | `uint` | `gl_LocalInvocationIndex` | `LocalInvocationIndex` |

One table (`Symbols/ComputeBuiltIns`) that the binder and both backends read, for the reason
`BindingPlan` and `StreamPlan` exist: a built-in's name, type and spelling in each target are one
decision. Unsigned in both targets, so `int3` is `RVN2109` rather than a conversion nobody wrote. In
GLSL the built-in is passed straight into the entry point — no declared input at all; in SPIR-V it is
a `BuiltIn`-decorated `Input` in the entry point's interface list, and never a located one, since
`Location` and `BuiltIn` are mutually exclusive.

**One silent miscompilation came out of enabling it.** A `stream` written by a compute stage emitted
the store while the stream itself went undeclared — GLSL assigning to an identifier the translation
unit never declared, which `glslc` rejects and nothing in Raven caught. Now `RVN3006`, an error rather
than `RVN3005`'s warning, because there is no honest thing to emit: a stream is a location in the
pipeline's interface and a compute dispatch has no pipeline.

**What compute still cannot do is persist anything.** No storage buffers and no storage images, so
there is nothing writable to store into — a compute shader can read bindings and compute, and that is
where it stops. It is gated on sized array types (below), not on the stage. Two consequences worth
naming: `Library/Example2.rvn` computes into a local and says so rather than assigning to a uniform,
and the numeric BRDF readback still needs a writable resource before it can read anything back.

Separately and pre-existing: **assigning to a uniform is not refused in any stage**, so a pixel shader
can do it too and both backends emit a store the reference compilers reject. Compute made it visible
by having nothing else to write to.

#### Streams: interstage values declared once

`stream var normalWS: float3` on a shader declares a value one stage writes and the next reads. The
alternative — what this row described — was threading it through signatures: a vertex entry point
returning a struct of everything the pixel stage might want, and every contributing function taking and
returning it. That works, and it makes an interstage value a property of every signature between its
producer and the pipeline, which is exactly the cost `compose` avoids for implementations. `[PerMaterial]`
says where a *binding* lives without spelling a set number; `stream` does the same for the pipeline's own
interface.

**Direction is derived, not declared.** Per entry point the stage's reachable code decides: stored to ⇒
output, *read before written* ⇒ input, both ⇒ both — legal, since a stage's input and output locations
are separate namespaces. That is the property worth having: a `compose`d surface function three calls
deep can write `normalWS` and the vertex stage grows an output with nothing between them mentioning it.
Reachability rather than shader membership decides what "the stage's code" means, for the reason § C
records — a composed implementation's functions live in a different `IrShader`.

"Read *before* written" rather than "read at all" is the subtle part, and it earns its keep.
`normalWS = n; return normalize(normalWS)` in a vertex stage reads a stream that stage produces; taking
any read as an input would declare a vertex attribute nobody binds. The read resolves to the *output*
variable instead, which both targets permit — only SPIR-V's `Input` is read-only. "Before" is a
pre-order walk with calls expanded at their call sites: exact for the straight-line code shaders are
made of, and conservative the safe way otherwise, since a spurious input costs a location while a
missing one would read undefined values. A partial write (`normalWS.x = …`) keeps the rest of the value,
so it counts as a read.

**A location is a property of the shader, not of the stage.** `StreamPlan` assigns a stream its index in
declaration order and both emitters and the reflection read the plan — the same construction as
`BindingPlan`, and for the same reason. Nothing has to be kept in step: the writing stage and the reading
stage arrive at 0 without either knowing the other exists. Deriving it from "index among this stage's
outputs" would have them disagree the moment one touches a stream the other does not.

The consequence, stated rather than discovered: **a stage's own parameters are located after the
streams**, so adding a stream renumbers a shader's vertex attributes — visible in the reflection the
engine builds its vertex layout from, which is where a renumbering has to be visible. The alternative
would make a stream's location depend on which stage was looking at it, and there is no number both
stages could agree on. The one exception is a fragment output, which stays at location 0 because it is a
render-target index; that is also why a stream *written* by a fragment stage is `RVN3005`, on the same
warning policy `RVN2091` uses for a marker on a non-binding.

**A stream is not a binding**: no `(set, binding)`, no uniform block member, nothing in the flattened
parameter list. It lowers to a module-scope global, which is what a stage interface *is* in both targets
— a SPIR-V `Input`/`Output` and a GLSL `in`/`out` are both module scope — so a read is an ordinary load
and a write an ordinary store, with only the direction resolved per stage. No new instruction, and body
lowering learns nothing.

Restrictions, each at the declaration where it can be fixed rather than twice over from the backends:
a shader field only (`RVN2100`); not also `const`, a `[Permutation]` key or a `compose` slot, none of
which has storage to thread (`RVN2101`); no initializer, its value coming from the stage that writes it
(`RVN2102`); and a non-boolean scalar or vector, the restriction stage I/O already lives under since
Vulkan has no boolean interface type and an aggregate would need a location per leaf (`RVN2103`).

Honestly bounded:

- **Streams do not cross a `.rvnlib` boundary** (`RVN5007`) — a stream's location is the *consuming*
  shader's stream list, so linking the function would mean matching two shaders' streams by name: the
  flattening half of the mixin problem (§ J), not a serialization gap. Within one compilation a stream
  crosses any number of functions freely.
- **No interpolation control** — no `flat`, `noperspective` or `centroid`; every stream is smoothly
  interpolated. An integer stream would want `flat` in GLSL and the type check permits one, so this is a
  real gap, and the syntax for it is an attribute on the declaration when something needs it.
- **A geometry stage is unchanged** — its per-vertex arrays are untouched. Compute now has a stage
  interface of its own kind (the dispatch built-ins, below), and a stream on one is `RVN3006`.

#### Superseded rather than carried

Recorded so nobody reintroduces them from the retired file's Phase 7:

- **HLSL and Metal emitters** → § C: not required, SPIRV-Cross covers them (ADR-012).
- **A shader package manager** → § H: `.rvnlib` references plus addressable content.
- **ANTLR as the end-state parser** → [doc 18](18-raven-parser-migration.md).
- **Interaction classes** → § D. Raven's half is done — the reflection reports declared permutation
  keys and value parameters; the generator that turns them into C# is engine-side, and
  [§ Generated C# bindings](#status-ravens-side-is-done-the-generator-waits-for-the-engine) records
  what it is waiting for.
- **Raven's own `.github/workflows/ci.yml`** — it did not survive the merge into the monorepo, which
  has no workflows at all yet. [Doc 12](12-build-ci-and-testing.md) specifies them.

#### Design deviations worth keeping

Not gaps — decisions that make the code deliberately unlike Roslyn, recorded so they are not
"corrected" by someone who assumes they were oversights:

- **Tokens are a class, not a value type.** Red `SyntaxToken` derives from `SyntaxNode` and wraps a
  green token, so traversal, `GetSlot` and the tree dumper work uniformly over tokens and nodes.
  Roslyn's value-type token avoids allocation at a scale Raven does not operate at.
- **Symbols are an abstract class hierarchy, not `ISymbol` over internal implementations.** Roslyn's
  split exists to keep its model private across assembly boundaries; Raven is one compiler assembly,
  so the split would double the public surface for nothing. Interfaces stay a mechanical wrapper if
  the API ever needs them.
- **The IR is SSA pre-mem2reg.** Instructions are SSA; locals stay in memory behind explicit loads and
  stores, which is what both target families want to consume. A mem2reg pass can promote to registers
  later if a backend prefers them.

### J. Language-surface audit: does the syntax fit a shader language?

Asked directly, and answered by probing every construct through the real pipeline rather than by
reading the grammar. **The core fits well. Roughly a third of the syntax is inherited C# that does
not, and five constructs were compiling to the wrong thing.**

#### What fits

`shader` plus stage attributes and `[Semantic]`; `compose`/`protocol` for static zero-dispatch mixins;
`[Permutation]` and `val` type parameters for compile-time specialisation; the `[PerFrame]`…`[PerDraw]`
markers; structs, functions, `for..in`/`while`/`repeat`, vectors, matrices, swizzles, intrinsics.
Properties with `get`/`set`/`willSet`/`didSet` compile end to end, which was worth checking rather than
assuming. Nothing important is *missing* except the items already in § I.

The first pruning pass — lambdas, nullables, anonymous objects, `char`/`long`/`object`/`string` —
established the right rule: **if it can never work on a GPU, remove it rather than diagnose it.** The
finding here is that it stopped too early.

Two constructs came out of the audit still open, and both are recorded rather than fixed: a **range in
value position** stays `RVN3001` (the syntax remains because `for (i in 0 .. 4)` needs it), and a
**generic struct** `Box<float>` stays `RVN3001`/`RVN3003` — see § I. A **collection expression** binds
and lowers but cannot emit (`RVN4001`) for want of sized arrays, which is the same gap § C's oracle
cannot check `ArrayStride` against.

#### Tier A — compiled to the wrong thing, removed

Not "unimplemented". Each of these produced a **valid module** that meant something the target cannot
do, with no diagnostic:

| Written | What it did |
|---|---|
| `sizeof(float4)` | bound to a literal `null` typed `int` → evaluated to **0**, never a size |
| `ref x` | binder returned the operand; `ref` silently discarded |
| `f(out x)`, `f(in x)` | modifier parsed and ignored; the IR has no by-reference parameters |
| `using (val x = 1f) { }` | kept the block, **discarded the declaration** — `x` was not even in scope |
| `class Widget` | `TypeKind.Struct or TypeKind.Class` everywhere: value semantics under a name promising references |

A rejection is recoverable; a wrong answer is not. All five are gone — grammar, tokens, syntax nodes,
kinds, translator, binder and `TypeKind` — and `RemovedConstructsTests` pins each with the reason. Two
of them (`sizeof`, `ref`) now read as ordinary undefined names, exactly the treatment `null` got in the
first pass. `TypeKind.Nullable` went with them: nothing had referenced it since nullables were removed.

#### Tier B — parses and binds, cannot compile: three finished, the rest dropped

**Finished**, and each is now covered by the § C differential oracle so both backends keep agreeing:

| Construct | How |
|---|---|
| `switch` statement | desugars into an if/else chain over equality tests, so neither backend needed anything new. The governing expression is evaluated once into a local; several labels on a section become a disjunction; `default` becomes the final `else`; a trailing `break` is dropped because sections do not fall through |
| user-defined operators | resolved against the operand types **after** the built-ins fail, so no declaration can change what `float + float` means. Named for the operator in the IR — `Spectrum_Add`, not the `operator_`/`operator_1` the GLSL mangler would produce from `operator+` |
| tuples | one struct per distinct shape, named after its element types so the name is stable rather than a counter. Element names come from the symbol, which already gives an unnamed element `Item1`, `Item2`, … so access needs nothing special |

**Dropped**, pinned in `RemovedConstructsTests`: all nine pattern forms and the `is` that used them,
switch *expressions* and arms, `when` clauses, variable designations, declaration expressions, local
functions, indexers, conversion operators, and binary `as`. Patterns are C# flow-typing — they narrow
a static type by testing a value, which needs runtime type information that does not exist here; `as`
was a reference conversion and there are no reference types. Ranges keep their syntax, because
`for (i in 0 .. 4)` needs it, and a range in value position stays `RVN3001`.

Tuples brought one deliberate language change: `(rgb: float3, a: float)` rather than
`(float3 rgb, float a)`. The tuple type was the **only** place in Raven where a name followed its
type; a field, a parameter and a `val` all lead with the name.

Finishing `switch` also turned up two more nodes carrying no keyword tokens (the statement, its labels,
`break` and `continue`), so they vanished on round-trip — two more instances of § I's token-dropping
class, surfaced because `Example1.rvn` started using `switch`. The corpus doing its job.

#### Tier C — C# shapes with no shader meaning, removed

Probing these first changed the verdict on half of them: several were not merely unused but **silently
ignored**, which puts them in Tier A's category rather than this one.

| Construct | What it did |
|---|---|
| `ExpressionColonSyntax` | **zero grammar rules, zero translator references** — five pieces of generated code for a node no input could produce |
| `[property: Semantic(…)]` | target parsed and dropped, so it silently meant `[Semantic(…)]` |
| `func P.Q()` explicit interface | silently ignored — the method bound and was callable as an ordinary member |
| `struct Point(x: float, y: float)` | parameters became neither fields nor a constructor; the declaration looked fine and the call site failed with `RVN2034` |
| `readonly record struct` | promised value equality, `ToString` and `Deconstruct`; none of the three existed |
| `init(a) : base(a)` | produced **malformed IR** (`RVN3010`) rather than a diagnostic |
| `Foo::Bar` | no alias table exists, so it could never resolve (`RVN2010` — at least honest) |
| `.Foo` leading-dot member | the binder had already given up, returning an error node with that reasoning in a comment |
| `~init()` | reported (`RVN3002`); no object lifetime on a GPU |

All gone, along with `MethodKind.Destructor` and the `record` modifier. `ExpressionColonSyntax` took its
abstract base with it: `BaseExpressionColonSyntax` existed only to hold it beside `NameColonSyntax`, so
`NameColon` now derives from the root and the hand-written bridge that manufactured an `ExpressionColon`
when a name was not an identifier is gone too.

#### What the pruning cost the surface

Across all three passes: **113 → 79 concrete syntax nodes, 18 → 13 abstract, 247 → 186 syntax kinds**,
and the `.g4` grammar from 866 lines to 706. Nothing was lost that compiled — three `Example1.rvn` lines
changed, and the golden GLSL, SPIR-V and IR were untouched.

The line counts are no longer worth tracking here: the generated tree and the translator moved to
`Vixen.Core.Syntax` and its generator, and the grammar is now [doc 18](18-raven-parser-migration.md)'s
test oracle rather than the front end. Node and kind counts still measure the language surface, which is
the thing pruning was about.

#### Constructors: valid, and now correct

Worth its own answer, because the intuition cuts both ways. **A constructor is valid on a GPU**: it
needs no heap, no lifetime and no dispatch, being a function that builds a value and returns it — which
is exactly how Raven lowers one, as `Ray Ray_init(vec3 o, vec3 d) { Ray self; …; return self; }`. MSL
and Slang have constructors, GLSL generates a positional one per struct, HLSL and WGSL spell it as an
aggregate initialiser. The name `Ray_init` keeps out of the way of GLSL's own implicit `Ray(...)`. This
is also the line that made removing `~init` right: **a destructor needs a lifetime, a constructor needs
only a return value.**

- 🔴 **`init` on a `shader` was a silent no-op.** A shader is the pipeline, not a value, so nothing
  constructs one — the body lowered to `func S.init(…)`, was dropped by reachability, and never ran
  while reading as though it initialised the bindings. Now `RVN2092`, pointing at the honest
  alternative: a binding default, which the backend reports as host-side data (`RVN4003`).
- 🟡 **A struct with no `init` is constructible from its fields.** Only the zero-argument form existed,
  making Raven *stricter than every one of its targets* and forcing a hand-written `init` per small data
  type. There is no synthesized symbol and no generated function: it binds to the same
  constructor-less `BoundObjectCreationExpression` a vector build produces, which lowering turns into
  one `IrConstructInstruction`, emitting as GLSL's own `Ray(a, b)`. The binder's field filter mirrors
  `Lowerer.LowerStruct`'s exactly, because arguments match IR fields **by position**. A declared `init`
  takes over rather than adding to it, so field order never becomes part of a struct's surface by
  accident.

What a constructor **cannot** do is enforce an invariant: `var r: Ray` skips it, and partial
initialisation is silent for want of definite-assignment analysis (§ I). HLSL and GLSL behave the same
way, so this is a property of a value language with no heap rather than a defect — but an `init` is
convenience, not a guarantee, and `ConstructorTests` pins that so the C# reading does not carry over.

#### ✅ The two files that define "what Raven looks like" — fixed

Both had rotted for a year while a round-trip test proved the bytes survived and said nothing about
whether they meant anything. `LibraryExampleTests` now asserts a contract per file, so neither can rot
again silently.

- **`Library/Example1.rvn`** — the syntax showcase — had 9 semantic errors (`RVN2002` ×3, `RVN2010`,
  `RVN2020`, `RVN2022`, `RVN2033`, `RVN2092` ×2). Two of them were `RVN2092`, a diagnostic the language
  gained *after* the file was written: the corpus doing its job and nobody acting on it. It now
  **parses and binds clean**. Fixing it meant declaring the bases it named (as a stateless shader plus
  a protocol, which is the inheritance that lowers), moving the shader's `init`s to ordinary functions,
  declaring the `CoreClass` it called, and making `Epsilon` a `float` — which alone accounted for three
  of the nine, the last cascading through an error type into a bogus arity error.

  Its contract is bind-clean, not lower-clean, and deliberately: two constructs it demonstrates cannot
  reach a backend (a generic struct, a spread element, both rows in the table at the top). Removing
  them to get a greener test would make the showcase misrepresent the language.

- **`Library/Example2.rvn`** — 21 `RVN1001`s — was **retired and replaced**. Every error was a
  deliberately-removed construct: `class`, `string` as a type, `long`, `null`, `int?`, force-unwrap
  `!`, string interpolation. Fixing it would have meant deleting everything it showed. It is now a
  compute shader that compiles end to end, with `glslc` and `spirv-val` as the verdict, and it joined
  the differential corpus.

**What the retired file earned on its way out: `else if` had never parsed.** `} else if (x) {` was
`RVN1001: expected '{', found 'if'` — in a C-family language, from the first day of the hand-written
parser. The tree shape had always allowed it (`ElseClauseSyntax.Statement` is a `StatementSyntax`, not
a block) and the binder and lowerer both took whatever statement the clause carried; only the parser
hard-coded `ParseBlock()`. That is what made it invisible: nothing was *wrong*, one thing was missing.
Fixed in the parser, the `.g4` oracle and its visitor, with three chain shapes added to the ambiguity
probes so the two parsers are held to the same nesting.

The lesson is the one the file was retired for failing to deliver: a corpus that only checks bytes
round-trip cannot tell you the language is broken. Both files are now checked for what they claim.

#### The third pass: every lexer token audited

A token-by-token audit of the lexer against the parser, binder, lowering and both emitters —
prompted by "are `static`, `public`, `private` actually used?" — found one more Tier-A miscompilation,
one dangerous "vestigial" mode, and a band of surface that parsed with nothing behind it.
`RemovedConstructsTests` pins each.

**Fixed rather than removed:**

- **Enum member values were silently wrong.** Only a *literal* initializer was honoured; `C = B`,
  `D = 2 + 3`, even `E = -1` silently became the declaration ordinal, and an implicit member continued
  from its ordinal rather than the previous value (`A, B = 5, C` made `C` 2, not 6). A shared
  `ConstantEvaluator` now evaluates initializers through the binder, sibling references included;
  implicit values continue C-style, a non-constant initializer is `RVN2094`, a cycle is the existing
  circular-definition error. `const` fields gained the same evaluator.
- **`where` clauses are enforced** — parsed, bound, stored and never read before. A type argument that
  is not the constraint, derived from it, or an implementer is `RVN2096`.
- **Modifiers and statement attributes with no effect warn** (`RVN2093`, `RVN2095`) — the RVN2091
  policy applied to modifiers and to things like `[Unroll]` that nothing reads.

**Removed, by the established rule** (grammar, tokens, kinds, translator, symbol plumbing): the
directive machinery (§ B — `#if` compiled both branches in, silently); **`public`/`private`/`protected`
and the whole `Accessibility` model**, parsed into a symbol property with zero readers, so a `private`
field was readable from any type, along with `abstract` (an `abstract func` *with a body* compiled) and
`partial`; the dead tokens `when`, `implicit`, `explicit`, `;` and `@`, which only poisoned the
identifier space — `@name` parsed and the `@` vanished from the tree, a fifth instance of § I's
token-dropping class; `global import`, type-parameter variance, modifier positions with no meaning,
`operator true/false`, prefix `^`, and the unreachable `#ImplicitElementAccess`. `static`, `const`,
`readonly`, `override`, `compose` and `stream` remain, each with a reader.

**Loose parens tightened**: a tuple's close paren was optional and fabricated when missing
(`(1f, 2f` round-tripped to `(1f, 2f)`) and the switch parens were independently optional
(`switch x) {` parsed). Both are now required and balanced.

The argument for pruning *before* the parser migration — that [doc 18](18-raven-parser-migration.md)
priced it per production, so every cut was a direct discount — was acted on: all three passes landed
first, and the migration was written against the smaller surface. The standing rule remains, and it is
cheap while nothing outside `Library/` is written in Raven: **a construct that can never work on a GPU is
removed, not diagnosed.** Each node costs a `Syntax.xml` entry, five pieces of generated code, a parser
production and a permanent round-trip obligation.

---

## Codegen: GLSL and SPIR-V landed together *(supersedes Q10)*

**Decision, and it held.** Both backends were built in one phase rather than sequentially, superseding
the earlier recommendation (SPIR-V first) and its replacement (keep the order, bridge with `shaderc`).
So ADR-012 applies directly — Raven emits SPIR-V, the engine consumes it, nothing sits between — and
the bridge that would have sat there is gone from the plan along with `IRavenBackend`, its two
implementations, and the second set of golden tests they needed. `Silk.NET.Shaderc` stays only as a
test oracle, which is what [01](01-technology-decisions.md) always listed it as.

Two things follow, and both are why the decision was worth making rather than merely cheaper:

- **The differential oracle exists at all.** Two independent paths from one source to one target can be
  diffed; neither previous plan had that. See
  [§ C](#the-differential-oracle-and-what-it-does-and-does-not-prove) for what it compares, the two
  injected faults that proved it bites, and where its coverage stops.
- **Vulkan-flavoured GLSL is mandatory for a better reason than before.** Under the bridge it was
  required because GLSL was a production path; now it is required because it is what makes the oracle
  possible — `glslc` has to compile the GLSL with bindings matching Raven's own emitter, or there is
  nothing to compare. Explicit sets, bindings and locations are also the most useful thing to read in a
  frame debugger, which is the GLSL emitter's other job ([13](13-diagnostics.md)).

Nothing lossy sits in the middle any more, so subgroup operations, `float64`, explicit SPIR-V
decorations and mesh shaders are available as soon as the emitter supports them rather than waiting for
a second phase. Those were the features the bridge could not have carried.

## The contract Raven must satisfy

The engine consumes Raven through **one library API** and **one artefact schema**. Both are
specified here so they can be built against before Raven is complete (with a stub/mock
implementation behind the interface, which is also how the engine's shader tests run without Raven).

### API

**The code is the contract now, so this section states the requirements and points at it** rather than
carrying a sketch that would drift: `Compilation.Create/GetDiagnostics/GetSemanticModel`,
`Lowerer.Lower`, an `ITargetBackend` per target, `RavenReference.FromFile`, and
`Vixen.Raven.Reflection.RavenReflection`. An earlier draft of this section sketched
`RavenCompilation`, `RavenEmitOptions` and `RavenEmitResult`; those names were never built, and a
fictional API in the plan of record is worse than none — it invites the engine to be written against
something that does not exist.

The shipped names drop the `Raven` prefix where the type is not an artefact — `Compilation`, not
`RavenCompilation` — because there is one compiler assembly and nothing to disambiguate against.
`RavenReference` and `RavenReflection` keep theirs, being the two types a host actually names.

What the engine requires of the shape, none of which is obvious from the signatures:

- **One diagnostic model**, the shared `Vixen.Core.Syntax` one, so the editor's error list, the engine
  log and the on-screen overlay are one implementation.
- **A compilation per variant.** Permutation values and compose bindings are supplied at
  `Compilation.Create`, not at emit, because both change what the code *means* — the dead branch has to
  be gone before lowering, not selected afterwards.
- **Emission is per backend, not per compilation.** `Lowerer.Lower` produces one `IrModule`; each
  backend turns it into one translation unit per entry point. A stage is a unit, which is why
  reachability rather than shader membership decides what a unit contains.

### Reflection schema — the part that must be exactly right

`Vixen.Raven.Reflection.RavenReflection` is the authority; it is what `Vixen.Shaders.Generators` turns
into C# and what the RHI turns into descriptor set layouts, and anything vague in it becomes a bug that
reproduces on one backend only. Two requirements are easy to miss and expensive to retrofit, so they
are recorded here rather than left to be inferred from the type:

1. **`UsedPermutationKeys`.** The effect cache key must be the *hash of the keys that actually
   mattered*, not of every define passed in. Without it, twenty independent flags yield 2²⁰ cache
   entries where a handful are distinct. Stride gets this right via its mixin/effect-validator system,
   and it is why Stride's shader cache is tractable.
2. **Explicit `Offset`/`Size`/`ArrayStride`/`MatrixStride` on every block member.** The engine writes
   constant buffers by generated offset, never by a reflection lookup at draw time. The layout rules —
   std140 for uniform, std430 for storage — have to be pinned and golden-tested, or every backend
   disagrees subtly about `float3` padding.

Everything else it reports, and why each is separate rather than merged, is in
[§ D](#d-public-api-contract-the-engine-codes-against) above: `Permutations` and `ValueParameters`
(what the shader declares) apart from `UsedPermutationKeys` (what this variant read),
`RequiredCapabilities` as names rather than an enum, and `PushConstants`/`SpecConstants` reported empty
rather than guessed at.

### Artefact schema

Two on-disk formats, both content-addressed into the object database ([08](08-asset-pipeline-and-addressables.md)):

| Extension | Contents |
|---|---|
| `.rvnlib` | A compiled Raven *library* — semantic symbols + IR, for cross-file/package reference without reparsing source. Analogous to a `.dll` reference. Magic, version, length and the whole library as JSON; `--emit-library` writes one and `--reference` consumes it. See [§ D](#d-public-api-contract-the-engine-codes-against) for the two halves and what they cost. |
| `.rvnfx` | A compiled *effect*: SPIR-V modules for all stages + reflection + the permutation key that produced it + source hash. This is the unit the runtime loads. Magic, version, JSON header, module bytes appended raw. |

## Source layout: what is written in Raven

```
Raven/Library/                          — shipped with the engine, compiled into .rvnlib
├── Core/
│   ├── Math.rvn                        — trig/vector/matrix helpers, packing, encoding
│   ├── Sampling.rvn                    — Hammersley, importance sampling, blue noise, Halton
│   ├── ColorSpaces.rvn                 — sRGB/linear, ACES, AgX, PQ, octahedral encode
│   └── Random.rvn                      — hash-based PRNG matching the CPU implementation bit-for-bit
├── Shading/
│   ├── Brdf.rvn                        — NDF/visibility/Fresnel primitives
│   ├── DiffuseModels.rvn               — Lambert, OrenNayar, Burley
│   ├── SpecularModels.rvn              — GGX, Beckmann, multi-scatter compensation
│   ├── ClearCoat.rvn  Sheen.rvn  Hair.rvn  Subsurface.rvn  Transmission.rvn
│   ├── Ibl.rvn                         — split-sum, SH irradiance, parallax-corrected probes
│   └── Lighting.rvn                    — clustered light iteration, shadow sampling, area lights
├── Geometry/
│   ├── Transform.rvn  Skinning.rvn  Instancing.rvn  Displacement.rvn
│   └── Normals.rvn                     — TBN, normal-map decode, geometric normal reconstruction
├── Material/
│   ├── MaterialSurface.rvn             — the composable material interface (Stride's model)
│   └── ComputeColor.rvn                — the shader-graph node primitives
├── Pipeline/
│   ├── ForwardPlus.rvn  Deferred.rvn  GBuffer.rvn  DepthOnly.rvn  ShadowCaster.rvn
├── PostFx/                             — one .rvn per effect from the 06 inventory
├── Ui/
│   ├── UiQuad.rvn  Msdf.rvn  RoundedRect.rvn  Blur.rvn  Gradient.rvn
└── Vfx/
    ├── ParticleSimulate.rvn  ParticleBillboard.rvn  ParticleRibbon.rvn
```

The `Raven/Library` tree is version-locked to the engine and compiled by a Nuke target
(`Build.Shaders.cs`) into `.rvnlib` artefacts shipped in the `Vixen.Shaders` NuGet package.

## Composition: the mixin problem

Stride's `.sdsl`/`.sdfx` system exists to solve one problem: **a material is assembled from features
chosen at author time, and the shader must be assembled to match.** Stride does this with shader
classes that support inheritance, `compose` members, and a mixin resolver — effectively a
shader-level object system. It works, and it is the least-understood, most-load-bearing part of Stride.

Vixen's equivalent, all of it built:

| Mechanism | Raven construct | Used for |
|---|---|---|
| Interface | `protocol IMaterialSurface { func Compute(inout d: MaterialData) }` | the contract a material feature satisfies |
| Implementation | `shader MetalRoughnessSurface : IMaterialSurface { … }` | one concrete feature |
| Composition | `compose val diffuse: IDiffuseModel` — a *shader-typed member* resolved at compile time | plugging chosen features into a template |
| Conditional | `[Permutation] val UseSkinning: bool` | permutation flags — not `#if`, see § B |
| Generics | `shader Blur<val TapCount: int>` | compile-time-parameterised shaders |
| Interstage data | `stream var normalWS: float3` | a value one stage writes and the next reads |

`compose` was the critical path and it is closed: the slot is protocol-typed, the binding resolves at
compile time, only the chosen implementation is emitted and called — no dispatch — and the
implementation's own bindings become the consuming effect's descriptors. What it buys is
`ForwardPlus.rvn` written once against `IMaterialSurface` and instantiated per material; the
alternative was string-templating shader source, which is where Stride was fifteen years ago.

That last clause was the half that was missing until `Material/` was written: resolution, calling and
pruning all worked, and a feature with a single parameter emitted GLSL naming an identifier nothing
declared. See § F for the fix and for the resolution-order defect underneath it.

### ⚠️ The inheritance in that table was never implemented below the symbol layer

An earlier draft described Raven as already having `shader X : Base, Other` inheritance, on the README's
word. That was **taken on trust and is false**. Member lookup does walk the base chain, nearest first,
so the binder accepts inheritance and resolves everything. Lowering never flattens it: a type
contributes only its *declared* members. Three silent miscompilations came out of that, all now
`RVN3002`:

| Written | What happened |
|---|---|
| a derived shader reading an inherited uniform | GLSL naming an **undeclared identifier** — `glslc` rejects it, Raven said nothing. SPIR-V was the only backend that noticed, as `RVN4002` |
| a derived struct reading an inherited field | **the wrong field.** Access lowers to an index and a derived type's indices are its own, so `d.a` emitted as `d.b` — type-correct, accepted by `glslc`, wrong |
| `override func` on a derived shader | **dropped.** The base's call was bound to the base's method and its body lowered once, so `Compute()` kept returning the base's value |

The checks are deliberately narrow. **Inheritance used only to supply a member still works** — a
stateless base whose method satisfies a protocol that a `compose` slot resolves against lowers
correctly, and `ComposeTests` covers it. Rejecting every base type would have taken a working
mechanism down with the broken ones.

#### Should Stride's mixin resolver be built?

**Not now — and the `override` row is why the question is sharper than it looks.** Making `override`
work *is* the mixin mechanism: a base's callers have to reach the derived member, which means
flattening, and there is no cheap version of that. Against building it:

- Reimplementing what this document calls *"the least-understood, most-load-bearing part of Stride"* is
  a poor bet.
- `compose` covers what mixins are mostly used for and covers it **better**: the slot is
  protocol-typed, so the contract is checked. A mixin chain is untyped by construction.
- Linearization makes errors non-local — a mixin list assembled in one file changes the meaning of a
  method in a file that never mentions it. Everything else here went the other way: `compose` resolved
  statically, one `BindingPlan`, one `StreamPlan`, a differential oracle.
- **There is no consumer yet.** Building a resolver before writing § F's library is designing against
  Stride's shape rather than against a requirement.

The trigger to watch for: write § F's material library against `compose`, protocols, streams and
non-inheriting shaders, and see what cannot be expressed. The likely candidate is a *chain* of
surface-modifying features where each needs the previous one's result — which is also what `stream`
was built for, so try that first. Note the two halves are separable: **flattening a source-declared
chain** is the smaller one and can land alone, while **choosing the chain per effect** is expensive
and may never be needed.

## Generated C# bindings

`Vixen.Shaders.Generators` reads `.rvnfx` reflection as `AdditionalFiles` and emits, per shader:

```csharp
// generated from Shading/Lighting.rvn
public static class LightingKeys
{
    public static readonly ParameterKey<int>       LightCount   = ParameterKeys.New<int>("Lighting.LightCount");
    public static readonly ParameterKey<Buffer>    LightBuffer  = ParameterKeys.New<Buffer>("Lighting.LightBuffer");
    public static readonly PermutationKey<bool>    UseShadows   = ParameterKeys.NewPermutation(false, "Lighting.UseShadows");

    // strongly typed constant-buffer writer with baked offsets — no runtime reflection
    public static void WriteLightingConstants(Span<byte> cb, in LightingConstants v) { … }
}
```

This is Stride's `ParameterKey`/`ParameterCollection` idea (which its shader generators also produce),
with the reflection cost moved entirely to compile time. Permutation keys are typed and enumerable, so
the build-time permutation pre-generator ([06](06-rendering-pipeline.md)) can iterate them.

### Status: Raven's side is done, the generator waits for the engine

**Nothing is missing on the compiler side.** `Parameters` gives every writable value its dotted name,
type and baked offset, so the constant-buffer writer above is pure data plus `Span<byte>` with no engine
types involved; `Permutations` and `ValueParameters` supply the keys.

**`AdditionalFiles` is not a workaround but the only shape that works.** A Roslyn generator targets
netstandard2.0/2.1 and runs inside the compiler ([Directory.Build.props](../../Directory.Build.props)
§ Generator profile) while `Vixen.Raven` targets net10.0, so a generator *could not* call the compiler
even if it wanted to.

**What blocks it is engine-side, deliberately.** `ParameterKey<T>`, `ParameterKeys`,
`PermutationKey<T>` and `Buffer` do not exist because `Vixen.Shaders` and the RHI do not. Their shape
should follow from how the renderer binds them and how `ParameterCollection` is consumed, which is why
[14](14-roadmap.md) sequences the generator in Phase 5 beside the effect system: designing that API
against no consumer is how it gets designed twice.

**If output is wanted sooner, emit from the CLI, not an analyzer.** `raven compile --emit-bindings`
writing C# beside the `.rvnfx` is the same emission with strictly less machinery — no `Vixen.Shaders`
project in a repo with no engine, no JSON reader inside an analyzer, and Raven's own tests can pin the
generated text. A build step has to run before the C# compiles either way; that is what the Nuke
`CompileShaderLibrary` target is, and the analyzer can wrap the same schema later.

## Development-time flow

```
.rvn edited
   → file watcher (Vixen.Core.IO.Watch)
   → RavenCompilation.Emit on a background thread, incremental (only affected trees reparse)
   → spirv-val in debug builds
   → EffectSystem swaps the Effect object; PipelineHandle recreated
   → next frame draws with the new shader; no device reset, no scene reload
```

Target: **< 500 ms** from save to visible change for a leaf shader; < 2 s for a change to
`Brdf.rvn` that invalidates most of the library. Achieved by (a) incremental reparse via
`Vixen.Core.Syntax`'s change tracking, (b) `.rvnlib` caching so unchanged library modules are not
re-bound, (c) permutation-level parallelism across the job system, (d) compiling only permutations
currently resident in the effect cache rather than the whole matrix.

(b) is the one of the four that is now available rather than planned: a `RavenReference` is read once
and reused across recompilations of a shader that references it, so editing a leaf shader neither
reparses nor re-binds the library it sits on. What the hot-reload path still needs is the *watching*
and the cache invalidation, which is engine-side.

Diagnostics from Raven surface in three places, all from the one `Diagnostic` model: the engine log,
the editor's error list with clickable source spans, and an on-screen overlay in dev builds (a failed
shader shows the error text rendered on the offending object — a small feature that saves a
disproportionate amount of time).

## Shader graph → Raven

`Vixen.Editor.ShaderGraph` ([11](11-editor.md)) emits Raven source, not IR. Reasons: the generated
source is inspectable and debuggable ("show generated code" is a right-click in the graph editor); the
graph gets Raven's type checking and diagnostics for free, mapped back to node ports via source
spans; and hand-written and graph-generated shaders are indistinguishable downstream. This is what
Unity's Shader Graph does (it emits HLSL) and it is the correct choice.

The mapping is mechanical: each node is a function call or an inline expression in `Material/
ComputeColor.rvn`'s vocabulary; the graph is topologically sorted into a sequence of `val`
declarations; sub-graphs become `func`s; the master node writes into `MaterialData`.
