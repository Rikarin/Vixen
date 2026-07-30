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
| 🟡 | **String interpolation** — needs lexer modes; nothing shipped uses it | § I | nothing |
| ⚪ | **Nuke is not stood up**: `CompileShaderLibrary`, `CheckFormat` for SPDX enforcement, the CI workflows | § A, § G | shipping the library as a package; SPDX is a real gap, not a closed item |
| ⚪ | **`Vixen.Raven.Transpile`** (SPIRV-Cross wrapper) and the cross-compilation test pass | § A, § G | HLSL/MSL/WGSL output, which ADR-012 says SPIRV-Cross owns |

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
- **Navigation is shared, because the questions are.** `FindToken(position)`, `FindNode(span)`, the
  descendant and ancestor walks, a token's positioned trivia, `IsMissing`, and a trivia-insensitive
  `IsEquivalentTo` live in `Vixen.Core.Syntax` — none of them is language-specific, and all three front
  ends are asked the same two things by [doc 09](09-ui-framework.md)'s `CodeEditor` and by the shader
  graph's mapping from generated source back to the node that produced it. Two rules the traversal
  follows: **list nodes are flattened away**, since a list is a shape of the tree and not a construct
  of any grammar (the raw slot walk stays available as `ChildNodesAndTokens`, which the tree dumper and
  the round-trip tests are written against); and **`FindToken` answers for every position in the file**,
  trivia included, because a caret in a comment is still somewhere. `IsMissing` is a flag the parser
  sets rather than a zero-width test — an end-of-file token has no text either, and a missing token can
  still carry the trivia recovery skipped past.

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
| 🔴 | **GLSL emitter, Vulkan-flavoured**: `#version 450`+, explicit `layout(set = N, binding = M)` via `GL_KHR_vulkan_glsl`, `layout(push_constant)`, `layout(location = N)` on every stage in/out, explicit `std140`/`std430`. Required so `shaderc` can compile it back to SPIR-V for the **differential oracle** below, and because it is the most readable form for the frame debugger | ✅ |
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

✅ **A binding may be a table.** `var textures: Texture2D[]` — an array of textures with no length —
is the one unsized array outside a storage block, and the only one that is descriptors rather than
memory. There is no stride, nothing is packed, and the host sizes nothing from it: the shader
indexes it with a number it was handed and never asks how long it is. Both emitters say so their own
way — `OpTypeRuntimeArray` with no `ArrayStride` under `RuntimeDescriptorArray` and
`ShaderNonUniform`; `uniform texture2D t[]` under `GL_EXT_nonuniform_qualifier` — and the reflection
reports `Count == 0`, which is what the RHI already reads for an unbounded binding.

✅ **And a binding may be *shared*.** `[Shared]` says a binding is one resource for the whole
compilation rather than a contribution from each feature that names it. A composed feature's bindings
are qualified by the path they were reached through — which is what stops three features that each
declare a `strength` from colliding — and that makes a binding declared by two features two bindings.
For a value that is right; for the frame's texture table it is the opposite of what the table is, and
`CompositeSurface` chains up to eight features. `BindingPlan` collapses the declarations by their
declared name into one `(set, binding)` pair and lists the rest as `Aliases`, which both emitters
point at the single declaration they emitted — because each feature's body was compiled against its
own variable and all of them have to resolve. Two shared declarations that disagree about kind or set
are `RVN3011`: one of the two authors is wrong and nothing can say which.

✅ **And a per-material block may be a *record*.** `[MaterialIndex]` on a per-draw field turns the
shader's per-material block into one element of a buffer — a `BufferBlock` wrapping a strided runtime
array in SPIR-V, a `readonly buffer` of a named struct in GLSL — read as
`materials.records[index].value` at every use. The set and binding do not move; what changes is that
the set holds every material at once and is bound for the frame rather than for the draw, which is
what lets two materials' draws be the same draw. The packing moves with it, std140 to std430, and the
reflection reports a `StorageBuffer` at the offsets it was emitted at: reporting a uniform buffer for
a shader that reads a `BufferBlock` is a descriptor of the wrong type, which no API checks.

The marker takes an optional permutation — `[MaterialIndex("UseRecords")]` — and applies only where
that permutation is true, which is what lets one pass be a records pass on a bindless device and a
bound-per-material one on GL, WebGL2 and MoltenVK below argument-buffer tier 2. ⚠ Gating on the
marked field being *used* does not work and was checked: a binding is a declared field, so it
survives its last reader folding away.

⚠ **Every subscript of one is decorated non-uniform, and both halves of it are.** SPIR-V marks the
index *and* the pointer the access chain produced; GLSL wraps the index in `nonuniformEXT`. A module
carrying one and not the other is valid SPIR-V that a driver may read one descriptor per subgroup
from — which is the correct picture for any draw that happens to use a single material, and
therefore for almost every test that is not written to catch it. The one that is written to catch it
is `BindlessSamplingDeviceTests`, on a device, with sixty-four invocations reading sixty-four
different slots. Nothing shorter can see it. See [bindless-materials.md](23-bindless-materials.md).

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
- **`ArrayStride` is now covered.** `SizedArrayTests` puts a `float4[4]` and a `float[8]` in a uniform
  block and asserts the stride, and both reference tools read the result — so the std140 round-up to 16
  is checked against two full front ends rather than only against the spec as literals. What is still
  uncovered is `std430`, which nothing produces until there is a storage buffer to produce it.
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
| ⚪ | "Interaction classes" (Raven's Phase 7) feed `Vixen.Shaders.Generators`, which emits the C# `ParameterKey`/`PermutationKey` classes | ✅ both halves; see [§ Generated C# bindings](#generated-c-bindings) |

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

**`PushConstants` is populated** from a shader's `[PushConstant]` fields — one range, offset 0, std430
members, which is what a Vulkan pipeline layout takes. **`SpecConstants` is still always empty, and
deliberately:** a `[Permutation]` key is resolved when the shader is *compiled* rather than left
specialisable at pipeline creation, which is what makes the dead branch disappear. An empty array is
honest; a fabricated one would be a bug the engine could not see.

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

#### ✅ Written: 47 files across all eight packages

`LibraryTreeTests` holds the tree to four claims, each failing differently: every file parses and
round-trips; the tree binds as **one** compilation, so the library agrees with itself rather than
being files that each happen to compile; **every shader with an entry point reaches both backends**,
with `glslc` and `spirv-val` as the verdict; and a shader compiles against the free-function packages
through `.rvnlib` references.

✅ **The reflection for the shaders the engine binds by name is checked in beside them**, and
`LibraryReflectionTests` regenerates and compares it. That is what gives `Vixen.Rendering` typed keys
and — the part that could not be written down by hand — the binding indices, which Raven assigns from
declaration order within a set and therefore renumbers whenever a resource is added above another.
Checked in rather than compiled during the build, because the alternative is the engine's render
project depending on the compiler being built first. Only `PostFx/Bloom` and `PostFx/Tonemap` so far:
the list grows when a node starts binding a shader, since every entry is a file somebody has to keep
compiling.

The reflection describes **one variant**, so a resource only a non-default variant reads generates no
key. `Bloom`'s `previous` texture is exactly that shape — read only by the upsample mode — and a test
asserts it survives the default rather than leaving it to luck.

| Package | Files |
|---|---|
| `Core/` | `Math` (constants, `SafeNormalize`, branchless basis, spherical, octahedral, matrix-first transforms) · `ColorSpaces` (sRGB exact and cheap, Rec.709/2020 luminance, Reinhard, ACES, AgX, PQ, YCoCg) · `Random` (PCG hash, uniform floats, sphere/hemisphere/disk) · `Sampling` (radical inverse, Hammersley, Halton, concentric disk, cosine hemisphere, GGX importance sampling) |
| `Shading/` | `Brdf` (the D/V/F primitives and `ShadingAngles`) · `DiffuseModels` · `SpecularModels` (GGX, anisotropic, Beckmann, multi-scatter, horizon occlusion) · `ClearCoat` · `Sheen` · `Hair` · `Subsurface` · `Transmission` · `Ibl` (split-sum DFG fit, SH9 irradiance, parallax-corrected probes) · `Lighting` (punctual and sphere lights, both shadow biases, PCF, cascade fade) |
| `Geometry/` | `Transform` (the spaces, depth reconstruction, reprojection) · `Normals` (tangent frames, one- and two-channel decode, whiteout blend, geometric normal) · `Skinning` (linear and dual-quaternion) · `Instancing` (packed transforms, per-instance variation) · `Displacement` (height, Gerstner waves, wind, parallax occlusion) |
| `Material/` | `MaterialSurface` (the `inout` contract and five features) · `ComputeColor` (the shader-graph vocabulary: blend modes, ramps, UV nodes, value noise) |
| `Pipeline/` | `ForwardPlus` (both light loops) · `ClusterCulling` (the binning dispatch) · `GBufferPass` · `Deferred` · `GBuffer` (the encoding both passes share) · `DepthOnly` · `ShadowCaster` |
| `PostFx/` | `Fullscreen` · `Tonemap` (+ grading and LUT) · `Bloom` (Jimenez down/up, Karis average) · `AutoExposure` (the one compute effect) · `Fxaa` · `Ssao` (GTAO horizon search, bent normals) · `Taa` (reprojection, YCoCg variance clipping) · `Fog` · `Vignette` (+ aberration and grain) · `Sharpen` (CAS) · `Outline` |
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

#### ✅ The three passes the library was missing

Breadth was never the gap. Every package had files; what three of them did not have was the pass that
makes the package *mean* something, and each was blocked on a language feature that has since landed.
Writing them closed the last content item — and each turned out to say something the fixtures could
not, because a fixture is written to exercise a feature and a pass is written to do a job.

**`Pipeline/GBufferPass.rvn` — the geometry pass.** `GBuffer.rvn` had held the encoding for as long as
multiple render targets were unexpressible, with `Deferred.rvn` reading through `Decode` and nothing
writing through `Encode`. Now both exist, and the shape worth keeping is that **the geometry pass is
`ForwardPlus` down to the `surface.Compute(d)` call and diverges only after it** — one `compose val
surface: IMaterialSurface` slot filled the same way in both, so a material is authored once and either
pipeline can render it. What deferred costs is then stateable in one sentence: a feature contributing
something the layout has no room for does not reach the lighting pass, and nothing warns.

**`Pipeline/ClusterCulling.rvn` — the binning dispatch.** The loop had been there since sized arrays
landed; what was host-side was the culling. The pass is one invocation per cluster, and the reason it
is shaped that way is a language constraint turned into a design: **Raven has no atomics**, so instead
of every thread appending to a shared list behind an atomic counter, each cluster owns a fixed slice of
the output that exactly one invocation writes. That removes the sharing rather than synchronising it,
at the price of a per-cluster capacity — overflow drops lights in the densest part of a scene rather
than crashing, which is the same failure a global budget has, localised. `ForwardPlus` reads it behind
`[Permutation] UseClusteredLights`, a permutation rather than a branch because the two loops need
different *bindings*: with it off, the buffers and the `positionVS` stream fold away and the host binds
nothing for them.

The grid constants are `const val` on a struct rather than permutations, and the distinction is worth
keeping: `MaxLights` sizes a *binding*, so it can vary per variant, while `ClusterGrid.Capacity` sizes
an array **inside a struct** — a type both the shader and the host agree the bytes of, which cannot
differ per variant.

**`PostFx/AutoExposure.rvn` — the compute post-process.** Every other effect in the package is a
fullscreen triangle writing one target. This one cannot be, and the reason is sharper than "it is a
compute pass": **its output is not an image.** It reduces the frame to one number and leaves it in a
storage buffer the *next frame's* tonemapper binds as an ordinary uniform. A fragment stage writes the
targets bound to it and nothing else, so the alternative is a readback through the host — a frame of
latency and a pipeline stall for eight bytes. It is the first thing in the library that needed both
new resource kinds at once, a storage image for the reduction chain and a storage buffer for the
result that outlives the frame.

#### Three defects writing them found

Each is a case of content asking a question no fixture had.

| Found by | Defect |
|---|---|
| `Material/MaterialSurface.rvn` | **`NormalMapSurface` wrote a tangent-space vector into `normalWS`.** The field name said world space and the value was not — every normal-mapped surface was lit as though it faced +Z. The cause was structural rather than a slip: the feature had no way to reach the tangent frame, because the frame belongs to the *mesh and the pass* and `MaterialData` carried no geometry at all. Fixed by giving it one, seeded by the pass through `MaterialDefaults.Begin`, so a feature says which way to bend and the pass says what to bend it against |
| `Pipeline/Deferred.rvn` | **A comment claimed `SV_VertexID` did not exist** three lines above the code using it — stale since the built-in landed. Comments do not compile, which is exactly why a file's prose is the part most worth re-reading when the thing it apologises for gets built |
| `Library/Pipeline/*`, `PostFx/*` | **Three permutation-gated paths had no test that switched them on**, so the cutout `discard`, the whole clustered loop and the auto-exposure buffer write were dead code in every test that compiled the tree. A `[Permutation]` key folding a branch away before lowering is the feature working; a test suite that only ever compiles the default variant is the suite not noticing |

#### Four defects the first pass over the library found

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

- ~~**No sized array types**~~ — landed, and it took the two named library gaps with it.
  `Pipeline/ForwardPlus.rvn` got the light loop over a `PunctualLight[MaxLights]` — the pass that
  *culls* into it is `ClusterCulling.rvn`, written later — and
  `Pipeline/ShadowCaster.rvn` skins from a `mat4[Skinning.MaxBones]` palette. What the *library* learnt
  from it is a calling-convention rule rather than a syntax one: a function parameter is by value in
  both targets, so a `mat4[256]` parameter would copy sixteen kilobytes at every call. Indexing
  therefore belongs to the shader that declares the palette, where it is an access chain, and the blend
  belongs to `Geometry/Skinning.rvn`, where the arithmetic is. Small arrays — a `float[3]` filter kernel
  — pass by value quite happily; the rule is about size, not about arrays.
- ~~**No writable resources**~~ — landed. `Vfx/ParticleUpdate.rvn` is the dispatch, and
  `ParticleSimulate.rvn` stayed exactly as it was: free functions over a `Particle` value, touching no
  binding. That turned out to be the right shape rather than a consolation — the split is what keeps
  doc 06's CPU/GPU bit-for-bit comparison a transliteration, and `WritableResourceTests` now asserts
  the split rather than only the compile, because a force that read a binding would break the
  comparison silently.
- ~~**No multiple render targets**~~ — landed, and `GBufferPass.rvn` is now written against it: the
  fragment stage returns a struct of the targets, `Deferred.rvn` reads through the same `Decode`, and the
  two agree in one place. See
  [§ The three interface shapes](#the-three-interface-shapes-that-are-not-a-set-of-uniforms).
- ~~**No `SampleLevel`**~~ — landed, along with `GetDimensions` and `asfloat`/`asint`/`asuint`. All
  three are the same shape of change (a symbol, an IR opcode, a line in each backend). `Msdf.rvn` now
  queries its atlas instead of hard-coding 1024, a packed storage buffer can be read back at all, and
  a vertex stage sampling a heightmap can say which mip it means. `Ibl` and `Bloom` are unchanged and
  deliberately so: `Ibl` takes prefiltered radiance as a parameter rather than sampling, which is what
  lets a deferred pass reuse it, and `Bloom` runs per mip because the chain is a sequence of passes.
  Two things worth knowing came out of it: `Sample` takes its level from derivatives, so outside a
  fragment stage it never meant what it looked like — SPIR-V was quietly substituting level zero; and a
  size query takes the *plain* image in both targets, which is why the GLSL side asks for
  `GL_EXT_samplerless_texture_functions` and the SPIR-V side for the `ImageQuery` capability, each
  declared only in the units that need it.
- ~~**No `SampleGrad`**~~ — landed, and it is the third sampling form rather than a variation on the
  other two. `Sample` takes its gradients from the fragment quad, which means nothing where the pixel
  next door is a different triangle of a different material — every silhouette and every material
  boundary in a visibility-buffer resolve. `SampleLevel` states one number, and one number has no
  anisotropy in it, which is visible as blur on every floor at a grazing angle. So the gradients arrive
  as *values*, computed from the triangle's screen-space plane and propagated through the UV
  interpolation: SPIR-V's `Grad` image operand, GLSL's `textureGrad`, legal in every stage because a
  stated gradient needs no quad to derive one from. Blocks phase 5 of
  [virtualized-geometry.md](22-virtualized-geometry.md), and it is the only prerequisite the Forward+
  resolve adds that a GBuffer resolve would not also need.
- ~~**No `SV_VertexID`**~~ — landed, and it turned up a claim the library was not keeping. Every
  post-process effect took the fullscreen triangle's index as `vertexIndex: float`, an *attribute*,
  so the host had to bind a vertex buffer of floats — for a shader whose whole point is binding none.
  Ten files now take the built-in. See
  [§ Stage built-ins](#stage-built-ins-a-value-the-pipeline-supplies-not-the-host).
- ~~**No `discard`**~~ — landed, and it was the odd one out of this group for the reason predicted:
  not a table entry but a keyword, a statement node, a bound node, an IR terminator and a rule in the
  flow analysis. The two cut-out passes no longer return zero and hope the host masked colour writes
  — which never addressed the actual problem, since a returned zero still writes *depth*. See
  [§ `discard`](#discard-the-only-statement-that-ends-more-than-a-function).
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
| ✅ | **Sized array types as type syntax** — `float4[4]`, `mat4[MaxBones]`. The `a[i]` ambiguity is resolved by *position* rather than by token shape; see [§ Sized arrays](#sized-arrays-the-length-is-part-of-the-type) |

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
catches it. It blocked the corpus freeze in [doc 18](18-raven-parser-migration.md) — a frozen corpus
that omits them is not a safety net, and freezing is what flushed them out — and it still affects
anything that reprints the tree: a formatter, a refactoring, or the shader graph's generated-source
span mapping.

#### Semantics and lowering

| | Gap | |
|---|---|---|
| 🔴 | **`m[i]` meant a row in the IR and a column in both targets** | ✅ fixed in [§ E](#e-conventions-raven-must-bake-in) |
| ✅ | **`&&` and `\|\|` short-circuit** — and `?:` runs one arm — *when the guarded operand can index, call or assign*; otherwise they keep the branch-free `logicalAnd`/`select` form. See [§ Short circuiting](#short-circuiting-a-branch-only-where-one-is-owed) |
| 🟡 | **Stream I/O declarations between stages** — no `stream` keyword; interstage data passes as entry-point parameters and returns | ✅ built; see [§ Streams](#streams-interstage-values-declared-once) |
| ✅ | **`Buffer<T>`-style resources** — `Buffer<T>` and `RWBuffer<T>`, std430, with a runtime-sized last member and `Length` answered at run time. Not generic: a structural type the binder builds, as `T[4]` is, so it never reaches the monomorphiser at all. `DescriptorType.StorageBuffer` and `LayoutRule.Std430` now have something that produces them. See [§ Writable resources](#writable-resources-the-first-thing-a-shader-can-store-into) |
| ✅ | **Kept in the language but not lowered** — resolved by Tier B: `switch`, operators and tuples are finished, the rest are dropped |
| ✅ | **Inheritance is flattened** — a base's fields reach the derived layout, and an `override` replaces the base's member in the base's own calls. See [§ Flattening](#flattening-a-derived-type-is-a-context-too) |
| ✅ | **Generics lower, by monomorphisation** — one concrete copy per instantiation, for structs and for methods; the open definition is emitted nowhere and costs nothing. See [§ Monomorphisation](#monomorphisation-one-copy-per-instantiation-and-none-of-the-definition) |
| ✅ | **A spread element in a collection** — flattening `[1, ..xs, 5]` needs `xs`'s length, which an array type now carries. Lowering emits one extract per index; a spread of an *unsized* array is still `RVN3002`, which is now a statement about that array rather than about spreads |
| ✅ | **Assigning to a uniform** — now `RVN2119`, checked at the root of the access chain so `tint.rgb = …` and `lights[i].color = …` are caught too. It went unreported for as long as it did because a shader with nothing writable had no correct alternative to name; `RWBuffer<T>` is that alternative |
| ✅ | **Flow analysis** — definite assignment (`RVN2127`), reachability (`RVN2128`) and falling off the end of a value-returning function (`RVN2129`). See [§ Flow analysis](#flow-analysis-what-is-true-on-every-path) |

#### Backends

| | Gap |
|---|---|
| ✅ | **Reading a whole struct out of a uniform block** (was `RVN4002`, SPIR-V). Its laid-out type is a distinct type from the plain one, so it needs a member-by-member copy — built, because `lights[i]` in a light loop is exactly that read and there is no way to write the loop without it |
| 🟡 | **A boolean in a uniform, or a boolean/aggregate as stage I/O** (`RVN4001`). `OpTypeBool` has no size and no memory layout. Reported rather than mis-emitted, but note the targets **disagree about what is legal**: GLSL hides it by giving a bool four bytes in a std140 block |
| ✅ | **Unsized arrays** — `RVN2126` at the declaration, naming the two ways out: give it a length, or make it a `Buffer<T>`, which is what a count the host decides actually is. It was `RVN4001` from both backends, about a lowered type, with no source span between them |

**The matrix indexing defect — fixed.** `m[i]` was typed as a *row* while both targets index by
column: SPIR-V refused to emit it (`RVN4002`) and GLSL emitted the wrong thing silently. It read as a
language decision needing a coin-flip (HLSL indexes rows, GLSL columns) and was not — once the
byte-level relationship between host and shader storage was worked out, exactly one answer was free in
both backends *and* the intuitive one. The derivation is in
[§ E](#e-conventions-raven-must-bake-in).

#### Flow analysis: what is true on every path

Two questions nothing could answer, with the same shape. Constant folding already removes a branch
whose condition is known — that is what makes a `[Permutation]` key pay for itself — but it says
nothing about a value written on only one side of an `if`.

**Reading an unassigned local is an error, not a warning,** and the reason is what a GPU does with
one: not an exception and not a zero, but whatever was in the register — which differs between
drivers, between invocations, and between debug and release. That is the shape of bug that
reproduces on one machine and nowhere else, so it is refused, on the same reasoning that made a
missing workgroup size `RVN2104`. `RVN2129` is the same undefined value seen from the other end: a
function that promises a value and can reach its end hands the caller whatever the target had, and
neither backend can diagnose it because by then the return is simply missing.

**Sound and deliberately incomplete.** It reports only what it can prove — a read where *no* path
assigns the local — and three boundaries are drawn on purpose, each because the false positive would
land on correct code:

- **Partial initialisation is not tracked.** Writing `r.origin` counts as assigning `r`. Proving
  otherwise needs per-field state, and filling a struct field by field is how a value is built in a
  language with no constructor requirement.
- **An `inout` argument counts as written.** Strictly it is both — `inout` is copy-in/copy-out — but
  filling a value is what it is *for*: Raven has no `out`, and `MaterialSurface`'s whole contract is
  a feature accumulating into a surface the caller declared.
- **A loop body's assignments do not survive the loop**, and one arm of an `if` is not both. The same
  rule C# applies, for the same reason.

What it found on the way in, in code that had been compiling: `var r: Ray` followed by a read of
`r.origin` — which doc 07 § J had recorded as a property of a value language rather than a defect,
because there was no analysis to catch it. Both targets accept it and hand back register contents.
The skip itself is still legal and still not fixable; what is closed is the read.

#### Stage built-ins: a value the pipeline supplies, not the host

`[Semantic("SV_VertexID")] vertexIndex: int` — the same mechanism the compute dispatch ids already
used, widened to the vertex stage. One table (`Symbols/StageBuiltIns`) that the binder, both backends
and the reflection read, so a built-in's semantic, its type and its spelling in each target are one
decision.

What made it more than a table entry is that **a graphics stage has located inputs**. A built-in gets
a `BuiltIn` decoration, and `Location` and `BuiltIn` are mutually exclusive — so it must not *consume*
a location either, or one sitting between two attributes would leave a hole in the vertex layout the
host binds against. The numbering therefore comes from `StreamPlan.InputLocations`, which both
emitters and the reflection read; three copies of that rule would be three chances to disagree, and
the disagreement is invisible until a mesh renders with its normals in the tangent slot.

Two smaller decisions:

- **Signed, unlike HLSL.** GLSL declares `in int gl_VertexIndex` and SPIR-V's `VertexIndex` is a
  signed 32-bit integer under Vulkan, so `uint` would put a conversion nobody wrote in front of every
  use. Refused (`RVN2109`) rather than converted, exactly as the dispatch ids are.
- **The vertex table is open where the compute one is closed.** An unrecognised semantic on a vertex
  parameter is `POSITION` or `TEXCOORD0` — an ordinary attribute — while a compute stage has no
  attributes at all, so an unknown name there is `RVN2108`. That is why the table is keyed on
  (semantic, *stage*) rather than on the name alone.

**`SV_IsFrontFace` is the fragment stage's entry in the same table**, and the one built-in whose type
is `bool`. A two-sided pipeline has no other source for it: the inside of an open shape — a plane seen
from below, a cone with the camera inside it — arrives with its normal pointing away from the viewer,
and only the rasterizer knows which winding it saw.

What it cost beyond a table row is one exemption, and it is a distinction worth having drawn.
`StageInterface` refuses a boolean because *a location* has no boolean representation — and a built-in
has no location for that rule to be about, its type being the target's own (`gl_FrontFacing` and
SPIR-V's `FrontFacing` are both declared `bool` by the target) rather than something a host lays out.
The GLSL backend had always skipped the check for a built-in, by declaring nothing for one at all; the
SPIR-V backend asked it of every stage variable and so refused a built-in *both* targets spell. So
this is the same "two copies of a rule is how two backends come to differ" the rest of this section is
about, found from the other end — the shared predicate was shared and the decision about *when to ask
it* was not.

**What it fixed in the library.** `Fullscreen.rvn` says a triangle needs no vertex buffer, and every
post-process effect then took the index as `vertexIndex: float` — an *attribute*, so the host had to
bind a buffer of floats after all. Ten files now take `SV_VertexID` and bind nothing, which is what
the file always claimed.

#### Unsized arrays: refused where they can be fixed

`var lookup: int[]` was `RVN4001` from both backends, about a lowered type, with no source span
between them. It is now `RVN2126` at the declaration — and the message matters more than the move,
because "not expressible" is not something an author can act on. There are exactly two ways out and
it names both: give the array a length, or declare it a `Buffer<T>`, which is what an array whose
count the host decides actually is.

**A length is part of an array's type,** not a detail of it: SPIR-V's `OpTypeArray` takes a constant
extent, GLSL writes one into the declaration, `ArrayStride` is computed from it, and the host reads it
back to size the buffer it uploads. So there is nowhere an unsized array can go — not a binding, not
a parameter (both targets pass arrays by value), not a local. Both backends keep their `RVN4001` as a
backstop for the one route that skips the binder, an unsized array decoded out of a `.rvnlib`.

**`Library/Example1.rvn` now compiles end to end** — the last thing between the language showcase and
a backend was the two unsized arrays it declared. Its test has moved with it: bind-clean, then
lower-clean, now generate-clean. A contract that stops where the language stops cannot tell you when
the language catches up.

#### `discard`: the only statement that ends more than a function

Listed with the small stage intrinsics and never one of them. A table entry would have given
`discard()` — a call — and a call is exactly the wrong shape, because **a function signature cannot
say that control does not come back.** Nothing after it would have been known to be unreachable, a
value-returning function whose last path discards would have been asked for a return it has no value
for, and both emitters would have had to guess where the block ended. So it is a keyword, a statement
node, a bound node, an IR terminator and a rule in the flow analysis.

**It writes no depth, and that is the whole point.** The two cut-out passes returned zero and relied
on the host's colour write mask, which reads like an adequate workaround and is not one: a depth
prepass and a shadow map write *depth*, and a colour mask does nothing about that. A cut-out leaf
filled the prepass with depth for texels it does not cover and cast the shadow of a solid quad. The
comment in `DepthOnly.rvn` said the value was never observed; the value was not what was being
written.

**Which stages may reach it is a call-graph question.** `RVN3008` is reported against a function
reachable from a non-fragment entry point, not against the file the keyword is written in — the same
reasoning that decides which functions belong to a stage in the first place (§ Streams). A cutout
helper shared by the depth prepass and a compute pass is wrong only in the second, and the file it
lives in cannot tell. The check runs over the whole lowered module, so a helper linked in from a
`.rvnlib` is covered too; that one has no span to report at, which still beats the alternative, since
SPIR-V's `OpKill` is valid only under the Fragment execution model and `spirv-val` would otherwise be
the first thing to notice — about a module, with no source position at all.

**The one place the two targets genuinely disagree.** SPIR-V's `OpKill` is a *block terminator*: it
must be the last instruction in its block, so a function ending in one is complete and needs nothing
after it. GLSL's `discard` is an ordinary statement, and glslang's own flow analysis then refuses a
value-returning function whose end it can reach. The GLSL emitter therefore owes it a `return` that
will never run — an uninitialised local, because that is correct for every type with no per-type
spelling, and reading it is exactly as impossible as reaching the line. Emitted only for a function
that can actually discard, so nothing else grows one. The difference stays inside the function: the
differential oracle compares the host-visible interface, and a `discard` fixture is in it.

For the flow analysis, `discard` is its own exit rather than a synonym for `return`, and the only
reason is the diagnostic: everywhere a decision is made the two behave alike, but "unreachable code
after a `return`" would be a lie about a line that follows a `discard`.

#### Flattening: a derived type is a context too

A base's fields never reached the derived layout and an `override` did not replace the base's
member — three silent miscompilations, which is why it was `RVN3002` rather than a gap left open.

**Flattening is monomorphisation over a different axis,** and that is what made it affordable
directly after: the same "emit this body in a context" machinery generics needed does this too. A
derived type is a context in which `self` has the derived layout, a base's field resolves to the
derived storage, and a call to an overridden member reaches the override. One copy of each inherited
body per derived type is what turns a language with **no dynamic dispatch** into one where `override`
means something — the choice is made once per type at compile time, which is the same trade
monomorphisation makes for generics.

Three decisions inside it:

- **A shader's storage is merged; a struct's is copied.** A shader's fields are module-scope globals
  and a global is a name and a type, so the derived shader lists the same `IrVariable` the base does
  — exactly what `compose` already did through `MergeInterface`. A struct's fields are reached by
  *index*, so a derived struct genuinely holds the base's, base-first, and a body reaching one
  resolves it against the derived layout. That index is what the worst of the three defects was: a
  derived struct's indices are its own, so reading the inherited `a` emitted a read of `b` —
  type-correct, accepted by `glslc`, and wrong.
- **Which copy a call reaches comes from the receiver's static type**, or from the type being emitted
  for when there is no receiver. `self` is typed as the *declaring* type inside a body, because that
  is what it was bound against, so a copy has to read it as the type the copy is for — the one place
  the symbol cannot be taken at face value.
- **An inherited binding keeps its name; a composed one is qualified.** `MergeInterface` does both,
  and the difference is not an inconsistency: a composed feature's parameter belongs to the feature,
  so `Diffuse.strength` is what a host should see, while an inherited field belongs to the type that
  inherited it and the author who wrote `tint` on a base reads `tint` in the derived shader.

Ordering follows the same split: a shader's own bindings come first and everything it pulls in
follows — one rule for inheritance and `compose` alike, so a shader's layout does not move when a
base gains a field. A struct is base-first, so a derived value's prefix is the base's layout.

**What this closes.** The mixin question below is answered: `compose` remains the composition to
reach for, because it is static, has no dispatch and no indirection, and says *what* a shader needs
rather than where it came from — but source-declared inheritance now lowers correctly rather than
being refused, so the two are choices rather than one working mechanism and one trap.

#### Monomorphisation: one copy per instantiation, and none of the definition

**It is the only way a generic reaches a GPU.** SPIR-V's types are fully concrete and GLSL has no
templates, so there is nothing either target could do with `Box<T>` — which means the open definition
is never emitted, and `Box<float4>` is, as an ordinary struct called `Box_float4`. A generic nobody
instantiates costs nothing, which is the property that makes a generic library affordable.

The bodies are **bound once**, against the open definition, and lowered once per instantiation through
a substitution. Binding each instantiation separately would type-check the same code twice for the
same answer; the front end's `TypeMap` / `ConstructedNamedTypeSymbol` / `SubstitutedSymbols` machinery
already read a member's signature through a map, and what was missing was only the lowering half.

Four things it needed, each of which is a shape worth knowing:

- **A worklist, not a pass.** An instantiation can name another: `Holder<float>`'s field is a
  `Pair<float>`, and that is only visible once `Holder<T>`'s members are read through its own map. So
  discovery is seeded from the non-generic declarations — an entry point is never generic, so anything
  a shader reaches is reachable from a concrete one — and closes transitively.
- **Canonical instantiations.** A `SubstitutedMethodSymbol` has reference identity: two call sites
  writing `Pick<float>(…)` build two objects for one instantiation, and a table keyed by the symbol
  would emit the function twice. They are keyed by declaration-and-arguments instead, and the first
  symbol seen becomes the one everything else resolves to.
- **A field belongs to the instantiation, not the definition.** A body says `return value`, bound
  against `Box<T>.value`, and there is no struct for `Box<T>` — so while an instantiation is being
  lowered, the definition's fields resolve to *its* struct.
- **Names are flattened and uniquified.** Neither target has angle brackets, so `Box<Pair<float>>`
  becomes `Box_Pair_float` — recursing through the same rule rather than beating the punctuation out
  of a display string, so it still reads in a frame debugger. Uniquified because flattening cannot be
  injective, and a module's struct names are one flat namespace.

**The boundary, stated:** a generic method *of* a generic type (`Box<T>.Map<U>()`) is not covered —
its map would carry `U` and not `T`, and the leftover `T` comes back as `RVN3001`. Nothing in
`Raven/Library` wants one, and the honest error is better than a half-substituted body.

What this closed beyond the row itself: **`Library/Example1.rvn` now lowers clean**, where its contract
used to stop at binding because a generic struct and a spread element could not reach a backend. The
one thing between it and code generation is now the unsized arrays it declares outside a storage block.

#### The three interface shapes that are not a set of uniforms

Multiple render targets, push constants and storage images landed together, and they belong together:
each is a thing a shader presents to the pipeline that is neither a uniform block nor a stage
parameter, and each needed the *shape* of the interface to change rather than a new intrinsic.

**Multiple render targets.** An interface variable takes one `location` and therefore has to be one
scalar or vector, which is why an aggregate output stayed `RVN4001` for as long as it did. So a
fragment stage that writes four targets returns a struct and the entry-point wrapper takes it apart —
one extract, one store, per target. `IrEntryPoint.Outputs` is a list now, and an output carries the
index of the member it came from so neither backend has to re-derive which member is which target.
**Declaration order is target order**, the same rule `StreamPlan` uses: a number both sides derive
beats a number one side spells. Fragment stages only — a vertex stage's several outputs are `stream`s,
where a location is a property of the shader and the two stages agree without either declaring the
other's struct.

**Push constants.** `[PushConstant] var offset: float2`, beside the `[PerFrame]`…`[PerDraw]` markers
and deliberately *not* a fifth one: a push constant is not in a descriptor set at all, which is the
entire reason to reach for it. One block per shader, because that is what a Vulkan pipeline layout
takes, laid out std430 in both targets. Three checks earn their keep: a descriptor cannot be pushed
(`RVN2120` — a texture is a handle, not bytes), a set marker on one says something untrue
(`RVN2121`), and a block over 128 bytes warns (`RVN3007`) because that is the guaranteed minimum and
the failure is otherwise invisible until a device refuses the pipeline.

**Storage images.** `[Format("rgba16f")] var target: RWTexture2D<float4>` — structural like
`Buffer<T>`, so the monomorphiser never sees it. Two things make it more than "a buffer with two
indices":

- **It is not sampled.** No sampler, no filtering, no mips. `Load`/`Store`/`GetDimensions` by integer
  texel, which is `imageLoad`/`imageStore`/`imageSize` and `OpImageRead`/`OpImageWrite`/
  `OpImageQuerySize`. `Store` is the one intrinsic in the language that returns nothing, which is why
  it emits as a statement in both backends.
- **The format is part of the type**, not of the binding, because it is part of the type in SPIR-V:
  `OpTypeImage` carries an `ImageFormat`, so two images with different formats are different types
  and a function parameter has to say which it takes. That is why a parameter carries its own
  `[Format]`. It is *required* (`RVN2123`) rather than defaulted: GLSL needs the qualifier on any
  image that is read, SPIR-V needs a known format or the `StorageImageReadWithoutFormat` capability,
  and there is nothing to guess — the host creates the view.

The element is always a four-lane vector, and that is not a simplification: both targets read and
write four components whatever the format stores, so an `r32f` image reads as `(r, 0, 0, 1)`.
Declaring `RWTexture2D<float>` would be a shape neither target has, so it is `RVN2122`.

**One pre-existing hole came out of it.** Assigning a binding *itself* — `target = source`,
`data = other` — was refused for read-only bindings and let through for writable ones, because the
check asked "is this resource writable" where it meant "is this a write *through* the resource". A
descriptor is not a value in either target; it is now `RVN2119` for the writable forms too.

#### Short circuiting: a branch only where one is owed

`i < n && data[i] > 0` used to read `data[i]` whichever way the bound went. Both operands were lowered
and handed to a `logicalAnd`, as `?:` was lowered to a `select` — sound for the side-effect-free
expressions shaders are mostly made of, and undefined behaviour the moment the right operand is a
guard.

The fix is not "always branch", and that is the part worth recording. **A branch costs a GPU the whole
warp**, and moving an implicit-LOD texture sample under one makes its derivatives undefined — so
lowering every `&&` into a branch would trade a correctness bug for a performance one and a second
correctness one. Instead the operand is examined, and exactly three things earn a branch:

- an **index**, because that is the guard the feature exists for and an out-of-range read is undefined
  in both targets;
- a **call** to a declared function, which may store into a writable resource;
- an **assignment** or increment, whose effect is the point of writing it.

Everything else — arithmetic, swizzles, loads, and the intrinsic library, which is pure by construction
— keeps the branch-free form, so an ordinary `a > 0 && b < 1` still emits one `&&` and no local. The
guarded form is `t = a; if (t) { t = b }`, with the test negated for `||`, which is a structured `if`
inside an expression: GLSL hoists the local above it and SPIR-V structures the merge. Neither backend
needed a new instruction, and the golden GLSL, SPIR-V and IR were untouched — which is the evidence
that the narrow rule was the right one.

#### Nested type arguments: splitting a token the lexer had no way to split

`Buffer<Buffer<float>>` ends in `>>`, and a maximal-munch lexer takes that as a right shift. Roslyn
solves this from the other side — its C# lexer emits `>` twice and the parser merges them for a shift
— but Raven's token spelling is pinned by the token-stream differential against the grammar oracle
([doc 18](18-raven-parser-migration.md)), so the split happens in the parser instead: one `>` comes off
the front and the rest stays a token for the enclosing list to take. `>>>` works by the same rule.

**What keeps this from swallowing shifts** is that a split has to be *paid for*. The speculative scan
that decides whether `a < …` is a generic name at all counts the `>`s it took out of a `>>` and must
have an enclosing type-argument list to hand the leftover to. In `a < b >> c` there is none, so the
scan is rejected and the expression stays the comparison it always was — the same rule C# applies,
arrived at from the other side. `>=` is deliberately *not* splittable: its tail is not something an
enclosing list could take, so splitting it would quietly turn `a < b >= c` into a generic name and an
assignment.

A nested buffer is still illegal, and that is the visible change: it is `RVN2118` about the element
type now, rather than `RVN1001` about syntax that was fine.

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

**Compute can now persist.** `RWBuffer<T>` is what it stores into, and
`Library/Vfx/ParticleUpdate.rvn` is the dispatch that does it. What is still missing is a storage
*image* — a writable texture — which needs a format on the declaration; a compute pass that has to read
a number back needs no image. Two consequences worth
naming: `Library/Example2.rvn` predates the buffer and still computes into a local, and the numeric
BRDF readback now has a resource to read back through.

#### Streams: interstage values declared once

`stream var normalWS: float3` on a shader declares a value one stage writes and the next reads. The
alternative — what this row described — was threading it through signatures: a vertex entry point
returning a struct of everything the fragment stage might want, and every contributing function taking and
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

#### Sized arrays: the length is part of the type

`float4[4]`, `mat4[Skinning.MaxBones]`, `PunctualLight[MaxLights]`. The length is not a detail of an
array type, it **is** part of it: `OpTypeArray` takes a constant extent, GLSL writes it into the
declaration, `ArrayStride` is computed from it, and the host reads it back out of the reflection to
size the buffer it uploads. Four different consumers, none of which has anything to do without it.

**The size is a constant *expression*, and that is the part that earns its keep.** A literal, a `const`,
an enum member — or a `[Permutation] val`, which lets the *host* pick the length. `MaxLights` and
`MaxBones` are budgets rather than hard-coded numbers, and a project that ships eight lights per cluster
does not pay for sixty-four.

**The one ambiguity, and how it is resolved.** `a[4]` is either an element access or a sized array type,
and the note this document carried for months said `array_rank_specifier` was `[]`-only "deliberately, so
that `a[i]` is unambiguously element access". That framing was the mistake: the ambiguity is not between
two *token shapes*, it is between two *positions*. In a type position nothing but the type can own a `[`;
in an expression `[…]` always indexes. So:

| Position | `[…]` means | Where |
|---|---|---|
| declaration annotation, return type, type argument, tuple element, base type, `default(…)` | a size | `ParseType`, grammar rule `type` |
| an expression, and a cast's type | an index | `ParsePrimary`/`ParsePostfix`, grammar rule `unsized_type` |

The cast belongs on the second row and only the oracle noticed: `(a[4]) - 1` is arithmetic, and reading a
size there would have made it a cast of `-1`. The hand-written parser had it right by construction and
the ANTLR grammar did not, because **ANTLR's subrules are greedy** — leaving the sized alternative
reachable from the expression rule let `type array_rank_specifier+` swallow the `[4]` before
`#ElementAccessExpression` was ever offered it. That is why the grammar now has two rules where it had
one, which is duplication bought deliberately: the alternative was a hand parser and an oracle that
disagreed about every `data[i]` in the corpus.

**A sized array and an unsized one are different types, and neither converts to the other.** The tempting
alternative — letting `T[4]` widen to `T[]` — would let code bind and then fail in the backend, because
an unsized array is `RVN4001` in both. A declaration you cannot lower is not a useful thing to convert
into, so `Library/Example1.rvn` declares `int[6]` where it used to say `int[]`.

**What it closed, beyond itself.** Each of these was already in the plan as its own row:

| Was | Now |
|---|---|
| a spread element cannot be lowered (`RVN3002`) | flattened as one extract per index; `RVN3002` now describes the *unsized* array, not spreads |
| `ArrayStride` untested against a second implementation (§ C) | a `float4[4]` and a `float[8]` in a block, checked by `glslc` and `spirv-val` |
| reading a whole struct out of a uniform block (`RVN4002`, SPIR-V) | member-by-member copy — `lights[i]` cannot be written without it |
| a collection expression binds and lowers but cannot emit (`RVN4001`, § J Tier B) | emits in both backends |
| `Lighting.rvn` has the per-light maths but not the clustered loop (§ F) | `ForwardPlus.Punctual` iterates `PunctualLight[MaxLights]` |
| `Skinning.rvn` takes four explicit matrices for want of a palette (§ F) | `ShadowCaster` indexes `mat4[Skinning.MaxBones]` |

Two defects the library found while it was being rewritten onto them, both of the shape worth recording
because the fixtures were never going to reach either:

- **A member that is an *array of* matrices was not decorated with `MatrixStride`.** `spirv-val` rejected
  the module outright — "Structure decorated as Block must be explicitly laid out with MatrixStride
  decorations" — because the decoration was written only for a member whose *own* type was a matrix.
  `ShadowCaster.rvn`'s `mat4[256]` palette is the first thing in the tree that is an array of matrices.
- **A constant index into a value with no storage was `RVN4002`.** `OpCompositeExtract` takes *literal*
  indices, so pulling element 2 out of a value the function never stored needs the 2 readable at emit
  time — which nothing in the IR distinguishes, correctly, because the distinction is a target's. The
  SPIR-V emitter now tracks its own constants. Every spread flatten hits this path.

**What it does not close.** A sized array is a *read-only* uniform array of records. A storage buffer
needs the writable storage class and the unsized last member as well, so `DescriptorType.StorageBuffer`
and `LayoutRule.Std430` still have nothing that produces them. And the calling convention is a real
constraint the library had to design around: a function parameter is by value in both targets, so a
`mat4[256]` parameter would copy sixteen kilobytes at every call. Indexing a large palette belongs to the
shader that declares it, where it is an access chain; a small array — a `float[3]` filter kernel — passes
by value quite happily.

#### Writable resources: the first thing a shader can store into

`Buffer<T>` is a read-only storage buffer, `RWBuffer<T>` a read-write one. Both are
`VK_DESCRIPTOR_TYPE_STORAGE_BUFFER`, laid out **std430**, with the element count decided by the host
and answered at run time by `Length`.

```
shader ParticleUpdate {
    var particles: RWBuffer<Particle>

    [ComputeShader(64)]
    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
        val index = int(id.x)
        if (index >= particles.Length) { return }

        var p = particles[index]
        ParticleSimulate.Step(p, forces, deltaTime)
        particles[index] = p
    }
}
```

**Not generic, and that was the load-bearing decision.** Raven's real generics did not lower at the
time — waiting for monomorphisation would have blocked this indefinitely — and the choice is still the
right one now that they do: `BufferTypeSymbol` is a **structural** type the binder constructs directly,
the same treatment `ArrayTypeSymbol` gets for `T[4]`, so it needs no instantiation and reaches the
monomorphiser not at all. The angle brackets are the only thing it shares with a generic, because
there is no declaration to find and no substitution to do. One buffer concept rather
than HLSL's several, too — a typed (texel) buffer is a different descriptor with no advantage on either
target, and `ByteAddressBuffer` trades the element type, which is the thing that makes an offset
checkable, for manual byte arithmetic.

**Read-only versus read-write is one bit, not two descriptor types**, because in Vulkan it is one. The
difference is an access decoration — `NonWritable` in SPIR-V, `readonly` in GLSL — and it is worth
declaring rather than dropping: a driver may hoist a load out of a loop from a read-only buffer and may
not from a writable one.

##### The gap it closed at the other end

The plan carried **"assigning to a uniform is refused by nobody"** as a ⚪ for a long time. It was never
a small thing — every stage emitted the store and both reference compilers rejected it — and the reason
it stayed unreported is worth naming: *a shader with nothing writable had no correct alternative to
suggest*. `RWBuffer<T>` is that alternative, so the diagnostic is now actionable and it is `RVN2119`.

It is checked at the **root of the access chain**, not at the target, because `tint`, `tint.rgb` and
`lights[i].color` are all writes to the same binding and only the innermost expression says which
binding that is. That check immediately found six writes to host state in sources that had been passing:

| Where | What it was |
|---|---|
| `README.md`'s language example | a property *setter* over the `baseColor` uniform |
| `Library/Example1.rvn` | the same shape, plus a `counter` uniform incremented in four places |
| `SymbolTests`, `LoweringTests` | settable properties on a shader, which now live on a struct |
| `ConstructorTests` | an `init` assigning a binding, alongside the `RVN2092` it was testing for |

None of them had failed, and the reason is instructive: a property setter nobody calls is unreachable,
so it was pruned before emission and the reference compilers never saw it. "It compiles because nothing
calls it" is not a property worth preserving. The fix in every case was to move the mutable state to
where it can exist — a local, or a struct, whose fields are values the shader owns rather than memory
the host uploads.

##### What it needed underneath

Three things had to be built, and each was forced rather than chosen:

- **`LayoutRule` instead of an is-laid-out flag** in the SPIR-V backend. std140 and std430 are two
  layouts *of the same Raven type*: a `float[4]` member has a 16-byte stride in a uniform block and a
  4-byte one in a storage buffer. A single "laid out" variant would have given a storage buffer the
  uniform block's offsets, silently. The flag became a rule in `SpirvTypes`, in `SpirvPointer`, and on
  `SpirvGlobal` — the last because a uniform block and a storage buffer share the `Uniform` storage
  class in the form Vulkan 1.0 accepts, so the class no longer identifies the layout.
- **`IrArrayLengthInstruction`**, which takes a *place* rather than a value. An unsized array cannot be
  loaded, so there is nothing to hand an intrinsic; both targets agree, since GLSL's `data.length()` and
  SPIR-V's `OpArrayLength` each name the block member. The first probe caught the bug this replaced:
  `particles.Length` silently folded to **0**, because the existing fold only matched a sized array.
- **`StoreAcrossLayout`**, the mirror of the member-by-member read that sized arrays needed. `spirv-val`
  is what said so — *"OpStore Pointer's type does not match Object's type"* — because `particles[i] = p`
  writes a plain struct into a laid-out one and SPIR-V has no conversion between two struct types.

**`BufferBlock` with `Uniform` storage**, not `Block` with `StorageBuffer` storage. The two spell the
same thing, but the second needs `SPV_KHR_storage_buffer_storage_class` in SPIR-V 1.0. This form needs
no extension — and it is the form `glslc` produces for the same GLSL, which is what keeps § C's
differential comparing like with like. That was checked by reading the oracle's output rather than
assumed.

##### What the host gets

`RavenReflection` reports a storage buffer with `DescriptorType.StorageBuffer`, `Count` 0 — this
schema's spelling for "the host decides" — `Size` as one *element's* std430 stride, and per-leaf offsets
relative to the start of an element, which is what a host writing an array of them needs. `IsWritable`
is reported too, and cannot be inferred: read-only and read-write are the same descriptor type, and the
difference decides which barrier the frame graph inserts around the dispatch.

##### ✅ Atomics: the one thing a dispatch cannot do without

A storage buffer let a compute shader persist. What it still could not do was let its invocations
**agree about a number** — and almost everything a dispatch is used for needs that once. Stream
compaction is the case that made it urgent: `Vixen.Vfx` reaps dead particles by swap-removal, which is
sequential, and the GPU form of it is every survivor taking the next slot from a shared counter. The
value the atomic hands back *is* the slot.

`atomicAdd`, `atomicMin`, `atomicMax`, `atomicAnd`, `atomicOr`, `atomicXor`, `atomicExchange` and
`atomicCompareExchange`, on scalar `int` and `uint`, named as GLSL names them.

**The design question is the first argument, and it has one answer.** An atomic operates on memory;
a value handed to a function is a copy, and nothing done to a copy is indivisible. So the first
argument has to be a *place* — the same conclusion `buffer.Length` reached for a different reason, and
the same `IrPlace` machinery. What is new is that nothing in the signature can say so. `inout` exists
and is exactly wrong: it is defined as copy-in/copy-out on both targets, which is the one property an
atomic must not have. So the requirement is a rule about the call rather than about the parameter —
`RVN2130` after overload resolution, for the same reason `inout`'s check is there rather than inside
applicability, and `Lowerer` takes the argument's place instead of loading it.

Three consequences worth keeping:

- **Free functions, not members of `RWBuffer`.** A member taking an index could only reach
  `buffer[i]`; the target is any place inside one, including `cells[i].population`.
- **A place is necessary and not sufficient.** The first draft's rule was "must be a place", which
  admitted a local and read as the more general statement. It is not: GLSL allows atomics only on
  "shader block storage or shared variables", so a local target would bind, verify, emit and be
  rejected by the GLSL front end — the exact failure this language exists to move earlier. It is also
  right on the merits, since an atomic on memory one invocation owns has nothing to be indivisible
  against. So the root must be a writable resource — the dispatch's — or a `groupshared` variable —
  the workgroup's. Found by asking `glslangValidator` rather than by reading the spec, which is the
  honest account.
- **A read-modify-write is a write**, so an atomic on a read-only `Buffer<T>` is `RVN2119` with the
  same message and the same one-character fix. Reusing the diagnostic is the point: the rule is not
  "atomics are special", it is "this is a store".
- **Signedness splits in one target and not the other.** GLSL's `atomicMin` covers both;
  `OpAtomicSMin` and `OpAtomicUMin` do not. The IR carries one `Min` and the place's type decides,
  because the split belongs to the backend that has it.

Both targets emit **device scope and relaxed semantics**, which is what glslang emits for the same
GLSL — checked by reading the operand ids out of the listing rather than assumed. Scope is the failure
that would not show up in a test: a workgroup-scoped atomic on a storage buffer is correct for every
dispatch small enough to be one workgroup and wrong for every one that is not.

Scalar integers only, and that is the targets' limit rather than a choice: GLSL 4.5 core has no atomic
on a float and none on a vector, so the overloads simply are not declared and a float atomic is
`RVN2031` — no applicable overload — instead of a signature the emitter would have to break.

##### ✅ Both widths, because 32 bits is depth *or* an id

`int64` and `uint64` exist for one job, and it is worth stating rather than generalising: a
single-pass software rasterizer wants `atomicMax` on a word packing depth above a cluster id, and with
32 bits you get one of the two. The alternative is two passes over the same triangles — a depth pass by
`atomicMin` and an id pass testing equality — which costs roughly what it sounds like. See
[virtualized-geometry.md § B2](22-virtualized-geometry.md).

Four decisions, each of which could have gone the other way:

- **Names, not keywords.** `int64` and `uint64` resolve through the same scope `Texture2D` does, so the
  lexer, the parser and the ANTLR oracle are untouched by a type that only compute shaders ask for.
- **No vectors and no matrices.** Nothing wants a `uint64_2`, both targets' atomics are scalar anyway,
  and each lane would cost a name, a layout rule and a row in the conversion table for a shape with no
  use.
- **Nothing widens implicitly.** `uint64(x)` is written out. A silent `uint` → `uint64` would make both
  the 32-bit and the 64-bit overload of every atomic applicable to the same call, and tie-breaking
  would decide the width of an operation whose width is the entire point. A *literal* still widens —
  it has no type of its own to be surprised by, and the first argument of an atomic is a place, which
  already says which overload is meant.
- **Two capabilities, not one.** `Int64` is the type and `Int64Atomics` is the operation, because they
  are two Vulkan features and a device may have the first without the second. Reporting only the type
  would be a pipeline that creates and a dispatch that does not do what the shader says.

What the emitters had to learn is narrower than it sounds. SPIR-V splits a width change from a
signedness change — `OpUConvert`/`OpSConvert` require the widths to differ and `OpBitcast` requires
them to match — so `int64(someUint)` is a widen in the *source's* signedness followed by a
reinterpretation, and doing it the other way round sign-extends a number that was never signed. And
every place the backend asked `component == UInt` to pick between a signed and an unsigned opcode had
to start asking whether it is *unsigned* — right while 32 bits was the only width, and exactly wrong
for a packed key whose top bit is data.

⚠ **A 64-bit component cannot cross a stage boundary.** Vulkan's interface slots are four 32-bit
components wide, so a wide one consumes two locations and stops matching the numbers `StreamPlan`
assigned. `StageInterface.CanCarry` now refuses it — which also closes the same defect for `double`,
where it had been accepted and silently taking two locations all along.

##### ✅ Workgroup-shared memory, and the barriers

The storage class § A listed as the one the language could not declare. `groupshared var tile: float[64]`
is a shader member that is deliberately **not a binding** — no descriptor, no `(set, binding)`, nothing
the host writes — and deliberately not a local either: one copy per workgroup rather than one per
invocation, which is the entire difference and the entire point. It is what a hierarchical traversal is
made of ([virtualized-geometry.md § B1](22-virtualized-geometry.md)), and what a reduction or a bitonic
sort stages through.

A modifier rather than an attribute, matching `stream` and `compose`: `[PushConstant]` and friends are
markers *about* a binding, and this is not one. It costs a keyword, a token kind, a row in the parser's
modifier table and two lines in the ANTLR oracle — which is what the grammar being executable
specification means in practice.

`barrier()` and `memoryBarrierShared()` come with it, named as GLSL names them for the reason the
atomics are. `barrier()` is **both** an execution barrier and a memory barrier over shared storage,
matching GLSL's definition in a compute stage rather than inventing a weaker primitive: an execution
barrier alone guarantees that the other invocations arrived and not that what they wrote is visible,
and the code after a barrier is without exception code that reads what they wrote. Both targets say the
same thing — `OpControlBarrier` at workgroup scope with `AcquireRelease | WorkgroupMemory`, which is
what glslang emits for the same GLSL.

Two rules the compiler enforces rather than leaving to a backend:

- **Only a compute stage may reach either** (`RVN3012`), decided by *reachability* rather than by where
  the declaration sits — the same argument `RVN3008` makes about `discard`, since a helper belongs to
  whichever stages call it. The alternative is not silence: `Workgroup` storage and a workgroup-scoped
  barrier are legal only under the `GLCompute` execution model, so this would otherwise reach
  `spirv-val`, about a module, with no span.
- **A stage declares only what it reaches.** Workgroup memory is a budget a device only has to offer
  16 KB of, so emitting every shader-level declaration into every stage's unit would spend one entry
  point's tile against another's limit — a pipeline that fails to create, for storage the stage never
  reads.

And five things a declaration cannot be, each reported at the declaration because that is what has to
change: not outside a shader (`RVN2131`), not also a `const`, a `[Permutation]` key, a `compose` slot
or a `stream` (`RVN2132`), not a descriptor (`RVN2133`), not initialized (`RVN2134` — workgroup storage
starts undefined in both targets, and one invocation writing what every other also writes is a race
rather than an initialization), and not read-only (`RVN2135` — nothing else can ever write it, so every
read would be undefined).

##### ✅ The writable bit did not survive being inherited

The first real consumer — `Vixen.Vfx`'s compute emitter, which puts its buffers on a base shader and
inherits them into two kernels — found that `MergeInterface` rebuilt each `IrBinding` **without its
writable flag**, so it took the parameter's default. Every `RWBuffer` reaching a shader through
inheritance arrived read-only, and so did every one contributed by a `compose`d feature, since both go
through the same merge.

What let it live is that only one target objects. SPIR-V decorates the variable `NonWritable`, stores
into it anyway, and `spirv-val` passes the module; GLSL writes `readonly` and its front end refuses the
store outright. So the shader ran on Vulkan and would not build for GL — which reads as a backend bug
and was one argument in the merge. Two lessons worth keeping: **a validator that accepts is weaker
evidence than a front end that has to compile**, so both reference tools are worth running; and a
constructor with defaulted parameters is a place where a rebuild silently loses information, which is
what "copies one shader's bindings onto another" had quietly become.

##### ✅ And the storage image that went with it

A **storage image** — a writable texture — landed as `[Format("rgba16f")] var target:
RWTexture2D<float4>`. It was a smaller thing than it looked but not a free one, and the format is the
reason: GLSL requires a qualifier on the declaration and SPIR-V an `ImageFormat` operand on
`OpTypeImage`, so it needed syntax and a decision about which formats to admit — sixteen, all of them
in Vulkan's must-support-storage list. `PostFx/AutoExposure.rvn` is the pass that needed it. See
[§ The three interface shapes](#the-three-interface-shapes-that-are-not-a-set-of-uniforms).

Also found while writing this, and since **fixed**: `Buffer<Buffer<float>>` did not parse. The `>>`
lexed as a right shift and the type-argument scanner did not split it — pre-existing in every nested
generic, with a nested buffer only the first type anyone would nest. A nested buffer is still illegal,
but it is now `RVN2118` about the element type rather than `RVN1001` about syntax that was fine. See
[§ Nested type arguments](#nested-type-arguments-splitting-a-token-the-lexer-had-no-way-to-split).

#### Superseded rather than carried

Recorded so nobody reintroduces them from the retired file's Phase 7:

- **HLSL and Metal emitters** → § C: not required, SPIRV-Cross covers them (ADR-012).
- **A shader package manager** → § H: `.rvnlib` references plus addressable content.
- **ANTLR as the end-state parser** → [doc 18](18-raven-parser-migration.md).
- **Interaction classes** → § D. Raven's half is done — the reflection reports declared permutation
  keys and value parameters; the generator that turns them into C# is engine-side, and
  [§ Generated C# bindings](#generated-c-bindings) records
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
**generic struct** `Box<float>` now lowers by monomorphisation — see § I. A **collection expression** now
binds, lowers and emits in both backends, including a spread: it was gated on sized arrays, because
flattening `[1, ..xs, 5]` needs `xs`'s length.

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

What a constructor **cannot** do is enforce an invariant: `var r: Ray` skips it. HLSL and GLSL behave
the same way, so that is a property of a value language with no heap rather than a defect — an `init`
is convenience, not a guarantee, and `ConstructorTests` pins that so the C# reading does not carry
over. **What the skip used to cost is now closed from the other end:** reading `r` unfilled is
`RVN2127`, so the value a constructor would have supplied has to come from somewhere. *Partial*
initialisation is still silent, and deliberately — see [§ Flow analysis](#flow-analysis-what-is-true-on-every-path)
for why the boundary is there.

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

  Its contract is now **lower**-clean, which it was not: the two constructs holding it back — a generic
  struct and a spread element — both lower now, so the weaker bar would have stopped noticing a
  regression in either. It stops short of code generation for one remaining reason, the unsized arrays
  it declares outside a storage block (`RVN4001`, the table at the top). Removing them to get a greener
  test would make the showcase misrepresent the language.

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

**And SPIR-V is not the last step, which `OpName` found out the hard way.** A module that validates and
that `spirv-val` accepts can still be one no driver will take: MoltenVK cross-compiles it to Metal
Shading Language and takes variable names *from the debug names*, so a name that is an ordinary
identifier in Raven and a keyword in C++ produces source that will not compile. Raven lowers
`a && b` into a local so `b` can be skipped and called it `and` — which is how C++ spells `&&` — so
every shader with a short-circuiting operand that needed guarding failed on Apple hardware with
`vkCreateComputePipelines … ErrorInitializationFailed` and no mention of a name. `SpirvNames` now
sanitises at the one place `OpName` is written, against the words that are legal in Raven, legal in
GLSL, and reserved in C++: the alternative operator spellings and the keywords GLSL leaves alone. The
lesson generalises past the fix — **a backend's output is only as portable as what the next compiler
downstream will accept**, and only a real device says which that is.

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

3. **An array of resources is one binding with a `Count`.** `TextureCube[4]` is four descriptors in one
   binding, which is how a shader picks a reflection probe or a shadow atlas slice by index without a
   descriptor set per choice. `ReflectionBuilder` was written for this from the start; the *front end*
   was not, and the disagreement was silent — a field's resource kind came from its type, an array
   reported none, and the lowerer's fall-through arm makes anything that is not a declared resource a
   member of the uniform block. So `var probes: TextureCube[4]` compiled into a block containing
   `OpTypeImage`, which `glslc` rejects with "member of block cannot be or contain a sampler" and which
   `spirv-val` accepts and no driver would. Fixed in `ArrayTypeSymbol.ResourceKind`, and the emitted
   GLSL is now held against `glslc`'s own rule rather than against our reading of it.

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

### ✅ The inheritance in that table was not implemented below the symbol layer — now it is

An earlier draft described Raven as already having `shader X : Base, Other` inheritance, on the README's
word. That was **taken on trust and was false**. Member lookup did walk the base chain, nearest first,
so the binder accepted inheritance and resolved everything, while lowering flattened nothing: a type
contributed only its *declared* members. Three silent miscompilations came out of that, each first
made `RVN3002` and now **fixed** — see
[§ Flattening](#flattening-a-derived-type-is-a-context-too):

| Written | What happened |
|---|---|
| a derived shader reading an inherited uniform | GLSL named an **undeclared identifier** — `glslc` rejected it, Raven said nothing. SPIR-V was the only backend that noticed, as `RVN4002`. Now the base's binding is one of the derived shader's, merged the way `compose` already merged one |
| a derived struct reading an inherited field | **the wrong field.** Access lowers to an index and a derived type's indices are its own, so `d.a` emitted as `d.b` — type-correct, accepted by `glslc`, wrong. Now the base's fields are in the derived layout, base-first |
| `override func` on a derived shader | **dropped.** The base's call was bound to the base's method and its body lowered once, so `Compute()` kept returning the base's value. Now the derived type gets its own copy of the base's body, in which the call reaches the override |

Refusing them was the intermediate step rather than the answer, and the checks were deliberately
narrow while it lasted: **inheritance used only to supply a member always worked** — a stateless base
whose method satisfies a protocol that a `compose` slot resolves against lowers correctly, and
`ComposeTests` covers it. Rejecting every base type would have taken a working mechanism down with
the broken ones.

#### Should Stride's mixin resolver be built?

**The smaller half is built; the larger one is still not worth it.** Making `override` work *is* part
of the mixin mechanism — a base's callers have to reach the derived member, which means flattening —
and that half landed once monomorphisation showed it was the same machinery over a different axis.
What is still unbuilt is *choosing the chain per effect*, and the arguments against that are
unchanged:

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
was built for, so try that first. The two halves were separable, as predicted: **flattening a
source-declared chain** was the smaller one and landed alone.

## Generated C# bindings

`Vixen.Shaders.Generators` reads `.reflect.json` reflection as `AdditionalFiles` and emits, per shader:

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

### ✅ Status: built, and what building it found

`Core/Vixen.Shaders` and `Core/Vixen.Shaders.Generators` exist. The generator reads
`.reflect.json` as `AdditionalFiles` and emits, per shader, a `…Keys` class (a typed
`ParameterKey`/`PermutationKey` per parameter, resource and permutation) and a `…Constants` struct
whose `Write(Span<byte>)` stores every value at the offset Raven computed.

**A block per set, not "the" block.** Raven gathers a shader's loose uniforms into one block *per set*,
and a shader that marks none of its bindings has one set — which is why "the uniform block" was a
well-formed phrase for as long as it was. A pass that says where each binding belongs has up to four,
and generating for the first left three sets' worth of values reachable only by spelling the name out.
So: a key for every block's values, a `PerFrameBlockSize`/`PerDrawBlockSize` pair per set, and a writer
struct per block (`ForwardPlusPerDrawConstants`). A shader with one block keeps `ConstantBufferSize`
and `<Shader>Constants` unchanged, because that is every shader that marks no sets and every host that
names one.

**The offsets are copied, never recomputed** — which is the entire point. They come out of the same
`ShaderLayout` pass that told the GLSL and SPIR-V emitters where to put things, so a host and a shader
cannot disagree about `float3` padding. A second implementation of std140 on the engine side would
eventually differ, and differ *silently*, because every byte still lands inside the buffer.

Three shapes needed the writer to know something a memcpy does not — a `float3` followed by a `float`
share one sixteen-byte slot, a `bool` occupies four bytes, a `mat3` is three twelve-byte columns in
sixteen bytes of space each — and one shape deliberately needed it to do nothing: a `Matrix4x4` is a
straight blit, because the shader reading the host's row-major bytes as `ColMajor` is what makes its
matrix the transpose that `mul(v, M)` wants (§ E).

**`AdditionalFiles` was the only shape that works, and two consequences came with it.** A generator
targets netstandard2.0/2.1 and runs inside the compiler while `Vixen.Raven` targets net10.0, so the
generator cannot call the compiler — the reflection model is hand-written against the *schema*, with
only the fields actually read declared, so Raven can extend the schema without breaking a build. And a
generator runs in the compiler's assembly load context, so `System.Text.Json` would compile and then
fail to load in the *consuming* project; the JSON reader is ours, which this section predicted as the
cost of the analyzer route.

#### The defect it found in Raven's own reflection

**A struct array in a uniform block reported no element layout at all.** `Flatten` descended into a
struct and stopped at an array of them, and `BuildParameters` then skipped the aggregate as
"not writable on its own" — so `lights: PunctualLight[MaxLights]` contributed *nothing* a host could
write through. A light list is a struct array in a uniform block, and so is every per-instance table,
which made a shader's most important parameter the one thing the reflection could not describe.

It now reports the element's fields once, under `name[].field`, with the element stride on each leaf:
element *i*'s field is `Offset + i * ArrayStride`. One entry per field rather than per element, since
sixty-four lights would otherwise be 512 entries saying the same eight things at a fixed spacing.

The generator turns those leaves back into one element struct with an indexed writer rather than four
parallel arrays — the flat form is honest to the layout and awful to use, and reassembling it is
exactly the loop a caller was going to write.

#### Where the line was drawn

`ParameterCollection`, the effect system and the three-tier bytecode cache are **not** built. They are
the rest of Phase 5's `Vixen.Shaders` bullet and they need `Vixen.Rendering` to be designed against —
which is this section's own argument, applied to itself: designing an API against no consumer is how
it gets designed twice. Keys and writers had a consumer already, and it was the generator.

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
