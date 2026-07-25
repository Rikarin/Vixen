# 07 — Raven Shader Pipeline

Raven already exists at `Vixen/Raven` with a real front end: an ANTLR grammar (353-line lexer,
484-line parser), a Roslyn-style green/red syntax tree generated from `Syntax.xml` (83 concrete + 18
abstract node types), full trivia and spans, a diagnostics model, golden-file parse tests, and
round-trip fidelity on the sample corpus. Its own `docs/IMPLEMENTATION_PLAN.md` marks Phases 0 and 1
complete, with semantic analysis (Phase 2) as the next work.

Your brief says Raven finishes before engine work starts. This document specifies **the contract the
engine requires from Raven**, so that "finished" has a precise definition, and opens with a consolidated
checklist of every change the engine plan asks of Raven.

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
| ⚪ | `Vixen.Raven` and `Vixen.Raven.Cli` become shipped NuGet packages ([12](12-build-ci-and-testing.md)); the compiler is useful standalone | |
| ⚪ | Relicense to **Apache-2.0** with SPDX headers and NOTICE (ADR-015) | partial — `LICENSE` and `NOTICE` are in place, per-file SPDX headers are not |

**How the extraction landed.** Two decisions are worth knowing before touching the tree:

- **Kinds are `int` below the language line.** `GreenNode.RawKind` and `SyntaxNode.RawKind` are
  integers; each front end re-exposes its own enum (`RavenSyntaxNode.Kind` is `(SyntaxKind)RawKind`).
  List-ness is answered by `GreenNode.IsList`, never by comparing kinds. The one value the shared
  tree reserves is `SyntaxKinds.List`, and a language's list member must equal it
  (`SyntaxKind.ListKind = SyntaxKinds.List`) or projecting a list node's kind names the wrong member.
- **`Accept` stays in the language.** A generated `Accept` calls `visitor.VisitIdentifierName(this)`,
  so its parameter must be the language's visitor type — the shared `SyntaxNode` therefore declares
  no `Accept`, and `RavenSyntaxNode` adds it. This is the split Roslyn makes between `SyntaxNode` and
  `CSharpSyntaxNode`. The shared `SyntaxToken` and `SyntaxListNode` are outside that hierarchy;
  `SyntaxVisitor.Visit` routes them with a single type test rather than an override per node.
  `Syntax.xml` names the language's base in `Root`, and its output namespace in `Namespace`.

### B. Language and semantic features — Raven's Phase 2

| | Feature | Why the engine needs it |
|---|---|---|
| 🔴 | **`compose` — shader-typed members resolved at compile time.** `compose val diffuse: IDiffuseModel` inside a shader, bound to a concrete `shader` per material | This is *the* load-bearing feature. It lets `ForwardPlus.rvn` be written once against `IMaterialSurface` and instantiated per material. Without it the material system falls back to string-templating shader source — where Stride was fifteen years ago |
| 🔴 | **Permutation constants** — `[Permutation] val UseSkinning: bool`, plus `#if`-style conditional compilation driven by `defines` passed to `Emit` | The whole effect/permutation system ([06](06-rendering-pipeline.md)) is built on it |
| 🔴 | **`UsedPermutationKeys`** — the semantic phase must report *which* defines actually affected the output | Without it, 20 independent flags yield 2²⁰ cache entries where a handful are distinct. This is why Stride's shader cache is tractable and it cannot be added later |
| 🟡 | `protocol` (interface) declarations usable as `compose` targets — already in the language per `Example2.rvn` | Material feature contracts |
| 🟡 | Shader inheritance `shader X : Base, Other` — already in the README | Feature composition |
| 🟡 | Compile-time generics: `shader Blur<val TapCount: int>` | Parameterised post-FX without duplication |
| ⚪ | Explicit `RequiredCapabilities` reporting (e.g. `"DescriptorIndexing"`, `"Float64"`) | RHI capability gating ([05](05-graphics-rhi.md)) |

### C. Emitter requirements — GLSL and SPIR-V together

| | Requirement |
|---|---|
| 🔴 | **SPIR-V emitter** — the canonical output (ADR-012). The engine consumes it directly; no bridge, no intermediate |
| 🔴 | **GLSL emitter, Vulkan-flavoured**: `#version 450`+, explicit `layout(set = N, binding = M)` via `GL_KHR_vulkan_glsl`, `layout(push_constant)`, `layout(location = N)` on every stage in/out, explicit `std140`/`std430`. Required so `shaderc` can compile it back to SPIR-V for the **differential oracle** below, and because it is the most readable form for the frame debugger |
| 🔴 | **Reflection comes from the semantic phase**, never from either emitted form. The engine writes constant buffers by generated offset |
| 🔴 | Honour the **four-set descriptor convention** (set 0 per-frame, 1 per-view, 2 per-material, 3 per-draw) when assigning bindings ([05](05-graphics-rhi.md)) — both emitters must agree, which the differential test enforces |
| 🟡 | **Differential test**: Raven's SPIR-V vs `shaderc`(Raven's GLSL) must be semantically equivalent. The strongest correctness signal available, and free once both emitters exist |
| ⚪ | HLSL / MSL / WGSL emitters are **not required** — SPIRV-Cross covers them (ADR-012) |
| ⚪ | `IRavenBackend` with swappable implementations is **not required** — the bridge is gone, so there is one code path |

### D. Public API contract the engine codes against

Detailed below. Summary: `RavenCompilation.Create/GetDiagnostics/GetSemanticModel/Emit` (Roslyn-shaped),
`RavenEmitResult`, `RavenShaderModule`, and the full `RavenReflection` schema.

| | Requirement |
|---|---|
| 🔴 | `RavenReflection` with **explicit `Offset`, `Size`, `ArrayStride`, `MatrixStride` on every block member.** The engine writes constant buffers by generated offset, not by runtime reflection. Get the std140/std430-vs-HLSL packing rules pinned and golden-tested or every backend disagrees about `float3` padding |
| 🟡 | `.rvnlib` (compiled library: symbols + IR, referenced without reparsing source) and `.rvnfx` (compiled effect: modules + reflection + permutation key + source hash) artefact formats |
| 🟡 | **Incremental reparse** via `SourceText.WithChanges` — the < 500 ms shader hot-reload budget ([00](00-vision-and-principles.md)) depends on it. Comes free from `Vixen.Core.Syntax` |
| 🟡 | Diagnostics surfaced through the shared model so the editor's error list, the engine log, and the on-screen shader-error overlay all use one implementation |
| 🟡 | Accept **generated** source with span fidelity, so `Vixen.Editor.ShaderGraph` can emit Raven and map diagnostics back to node ports ([11](11-editor.md)) |
| ⚪ | "Interaction classes" (Raven's Phase 7) feed `Vixen.Shaders.Generators`, which emits the C# `ParameterKey`/`PermutationKey` classes |

### E. Conventions Raven must bake in

Get these wrong and every shader is subtly incorrect in a way that is painful to find later.

| | Convention |
|---|---|
| 🔴 | **Right-handed, Y-up, column-vector with row-major storage** (`M11..M44`, translation in `M41..M43`), i.e. HLSL's `mul(v, M)` (ADR-003) |
| 🔴 | **Reverse-Z, depth range 0..1** |
| 🟡 | UV origin top-left |
| 🟡 | Linear working space; sRGB decoded on sample; HDR render targets |
| 🟡 | `Random.rvn` must match the CPU implementation **bit-for-bit** — the VFX system compiles one graph to both a C# job and a Raven compute shader, and their outputs are compared in a test ([06](06-rendering-pipeline.md)) |

### F. The shader library to write *in* Raven — Phase 5, ~the largest content task

`Raven/Library/` becomes a shipped, version-locked artefact compiled by the Nuke `CompileShaderLibrary`
target. Full tree in [§ Source layout](#source-layout-what-is-written-in-raven); in summary:
`Core/` (Math, Sampling, ColorSpaces, Random) · `Shading/` (Brdf, DiffuseModels, SpecularModels,
ClearCoat, Sheen, Hair, Subsurface, Transmission, Ibl, Lighting) · `Geometry/` · `Material/` ·
`Pipeline/` (ForwardPlus, Deferred, GBuffer, DepthOnly, ShadowCaster) · `PostFx/` (one per effect) ·
`Ui/` · `Vfx/`.

### G. Testing and CI additions

| | Addition |
|---|---|
| 🟡 | Extend the existing golden-tree and round-trip corpus to **the whole `Raven/Library` tree** — every shipped shader must round-trip byte-identically |
| 🟡 | `spirv-val` on every emitted module; golden `spirv-dis` disassembly snapshots so codegen changes are reviewable |
| 🟡 | Cross-compile every module through SPIRV-Cross to GLSL 450 / ESSL 300 / HLSL 60 / MSL / WGSL without error; GLSL/ESSL additionally through `glslang` |
| 🟡 | **Numeric BRDF tests**: CPU ports of the shading functions compared against a GPU compute readback over a parameter sweep, agreeing to 1e-4. This is the test that catches "the shader is subtly wrong" |
| 🟡 | Constant-buffer layout: reflection offsets verified against a GPU readback of a known pattern, **per backend** |
| 🟡 | Permutation correctness: passing an unused define produces a byte-identical module and the same cache key |
| 🟡 | Positive/negative fixture pairs per diagnostic ID; `compose`-resolution golden trees per material-feature combination |
| ⚪ | `SharpFuzz` corpus over the Raven parser, alongside the VXML/VCSS/`.meta`/bundle readers ([12](12-build-ci-and-testing.md)) |
| ⚪ | Perf gates: full-library compile time, and < 500 ms incremental recompile of a leaf shader |
| ⚪ | Nuke `CompileShaderLibrary` target: Raven over `Raven/Library/**/*.rvn` → `.rvnlib`, `spirv-val` each, **fail on any diagnostic** |

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

---

## Raven's codegen: ✅ **GLSL and SPIR-V land together** *(supersedes Q10)*

**Decision.** Raven's GLSL and SPIR-V backends are built in the same phase, not sequentially. This
supersedes both the earlier recommendation (move SPIR-V ahead of GLSL) and its replacement (keep the
order, bridge with `shaderc`). Raven's order becomes:

`2 Semantic → 3 IR → 4 GLSL + SPIR-V → 5 CLI → 6 Interaction classes`

### What this removes

**The `shaderc` bridge is no longer needed and is deleted from the plan.** With SPIR-V arriving at the
same time as GLSL, ADR-012 applies directly: Raven emits SPIR-V, the engine consumes it, and nothing
sits in between. Concretely gone:

- `IRavenBackend` with two implementations (`GlslViaShadercBackend` / `NativeSpirvBackend`) — collapses
  to one code path. No configuration switch, no swap-over milestone.
- Two codegen paths golden-tested in parallel — the real cost of the bridge, now zero.
- GLSL as a lossy intermediate. Subgroup operations, `float64`, explicit SPIR-V decorations, and mesh
  shaders become available as soon as the emitter supports them, rather than waiting for a second phase.
  These were the features the bridge could not carry.
- `Silk.NET.Shaderc` as a production dependency. It stays in the plan, but **only as a test oracle** —
  which is what [01](01-technology-decisions.md) always listed it as.

### What this creates: a differential oracle

Building both emitters against one IR gives something neither previous plan had — **two independent
paths from the same source to the same target**, which can be diffed:

```
              ┌── Raven SPIR-V emitter ─────────────────▶ SPIR-V (A)   ← what the engine uses
.rvn ─IR─────┤
              └── Raven GLSL emitter ──▶ Vulkan GLSL ──shaderc──▶ SPIR-V (B)   ← the oracle
                                                │
                                                └──glslang──▶ validation
```

`A` and `B` must be semantically equivalent. Disagreement means one of the two emitters is wrong, and
the diff usually says which. This is a *much* stronger test than validating either alone:

- `spirv-val` proves SPIR-V is well-formed, not that it means what the source said.
- A golden disassembly snapshot detects change, not incorrectness.
- The differential test catches the class of bug where the emitter is internally consistent and
  semantically wrong — which is precisely the hard class.

Both emitters read the same lowered IR, so a bug in the IR shows up in both and the differential test
stays silent — worth knowing, and it is why the numeric BRDF tests (CPU port vs GPU readback) remain
necessary alongside it. The two techniques catch different things.

### Consequence: the GLSL emitter's flavour requirement stays, for a better reason

Emit **Vulkan-flavoured GLSL** — `#version 450`+, explicit `layout(set = N, binding = M)` via
`GL_KHR_vulkan_glsl`, `layout(push_constant)`, `layout(location = N)` on all stage in/out, explicit
`std140`/`std430`. Under the bridge this was mandatory because GLSL was a production path. Now it is
mandatory because **it is what makes the differential oracle possible**: `shaderc` must be able to
compile the GLSL to SPIR-V with bindings that match what Raven's own emitter produced, or there is
nothing to compare.

Pleasant side effect: Vulkan GLSL with explicit bindings is also the most useful thing to read when
debugging a shader, which is the other job the GLSL emitter does (per-draw shader source in the frame
debugger, [13](13-diagnostics.md)).

### Consequence for the engine's schedule

The renderer (engine Phase 5) now gates on **one** Raven codegen phase rather than on its fourth or
sixth. The gating story simplifies to: *Raven needs Semantic → IR → codegen; the engine's Phases 0–4
need none of it* (a triangle and a UI shader can be checked-in SPIR-V blobs). Engine work can still
begin as soon as Raven reaches its Phase 2.

Combining the two emitters should also cost slightly less than building them separately — they share the
IR-to-target lowering scaffold, the reflection production, and one testing pass — though not enough to
move the overall estimate with any confidence.

## The contract Raven must satisfy

The engine consumes Raven through **one library API** and **one artefact schema**. Both are
specified here so they can be built against before Raven is complete (with a stub/mock
implementation behind the interface, which is also how the engine's shader tests run without Raven).

### API

```csharp
namespace Vixen.Raven;

public sealed class RavenCompilation
{
    public static RavenCompilation Create(
        RavenCompilationOptions options,
        ImmutableArray<SyntaxTree> trees,
        ImmutableArray<RavenReference> references);          // compiled .rvnlib libraries

    public ImmutableArray<Diagnostic> GetDiagnostics();       // shared Vixen.Core.Syntax model
    public SemanticModel GetSemanticModel(SyntaxTree tree);

    public RavenEmitResult Emit(
        RavenEmitOptions options,                             // target, optimisation, debug info
        ImmutableDictionary<string, string> defines,          // ← permutation keys
        CancellationToken ct);
}

public sealed record RavenEmitResult
{
    public bool Success { get; init; }
    public ImmutableArray<Diagnostic> Diagnostics { get; init; }
    public ImmutableArray<RavenShaderModule> Modules { get; init; }   // one per entry point
    public RavenReflection Reflection { get; init; }
}

public sealed record RavenShaderModule
{
    public ShaderStage Stage { get; init; }        // Vertex, Fragment, Compute, Geometry, TessControl, TessEval, Mesh, Task
    public string EntryPoint { get; init; }
    public ReadOnlyMemory<byte> Spirv { get; init; }
    public Vector3I ThreadGroupSize { get; init; } // compute only
}
```

### Reflection schema — the part that must be exactly right

This is what `Vixen.Shaders.Generators` turns into C# and what the RHI turns into descriptor set
layouts. Anything vague here becomes a bug that only reproduces on one backend.

```csharp
public sealed record RavenReflection
{
    ImmutableArray<DescriptorSetInfo>   Sets;          // set index → bindings
    ImmutableArray<VertexInputInfo>     VertexInputs;  // location, format, semantic name
    ImmutableArray<FragmentOutputInfo>  Outputs;       // location, format
    ImmutableArray<PushConstantInfo>    PushConstants;
    ImmutableArray<SpecConstantInfo>    SpecConstants;
    ImmutableArray<ParameterInfo>       Parameters;    // the flattened, engine-facing parameter list
    ImmutableArray<string>              RequiredCapabilities;  // e.g. "DescriptorIndexing", "Float64"
    ImmutableArray<string>              UsedPermutationKeys;   // which #defines actually affected output
}

public sealed record DescriptorSetInfo(int Set, ImmutableArray<BindingInfo> Bindings);
public sealed record BindingInfo(
    int Binding, string Name, DescriptorType Type, int Count,   // Count > 1 ⇒ array; 0 ⇒ runtime array
    ShaderStageFlags Stages, ImmutableArray<MemberInfo> Members); // Members for uniform/storage blocks
public sealed record MemberInfo(string Name, ShaderDataType Type, int Offset, int Size, int ArrayStride, int MatrixStride);
```

Two requirements that are easy to miss and expensive to retrofit:

1. **`UsedPermutationKeys`.** The effect cache key must be the *hash of the keys that actually
   mattered*, not the hash of every define passed in. Without this, a permutation matrix of 20
   independent flags yields 2²⁰ cache entries where a handful are distinct. Stride gets this right
   via its mixin/effect-validator system, and it is why Stride's shader cache is tractable.
2. **Explicit `Offset`/`ArrayStride`/`MatrixStride` on every block member.** The engine writes constant
   buffers by generated offset, not by reflection lookup at runtime. Get the layout rules (std140 for
   uniform, std430 for storage, with the HLSL packing differences accounted for) pinned down and
   golden-tested, or every backend disagrees subtly about `float3` padding.

### Artefact schema

Two on-disk formats, both content-addressed into the object database ([08](08-asset-pipeline-and-addressables.md)):

| Extension | Contents |
|---|---|
| `.rvnlib` | A compiled Raven *library* — semantic symbols + IR, for cross-file/package reference without reparsing source. Analogous to a `.dll` reference. |
| `.rvnfx` | A compiled *effect*: SPIR-V modules for all stages + reflection + the permutation key that produced it + source hash. This is the unit the runtime loads. |

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

Vixen's equivalent, expressed in Raven's existing language shape (which already has `shader X : Base,
Other` inheritance per the README):

| Mechanism | Raven construct | Used for |
|---|---|---|
| Interface | `protocol IMaterialSurface { func Compute(inout MaterialData d) }` | the contract a material feature satisfies |
| Implementation | `shader MetalRoughnessSurface : IMaterialSurface { … }` | one concrete feature |
| Composition | `compose val diffuse: IDiffuseModel` — a *shader-typed member* resolved at compile time | plugging chosen features into a template |
| Conditional | `#if VIXEN_SKINNING` / `[Permutation] val UseSkinning: bool` | permutation flags |
| Generics | `shader Blur<val TapCount: int>` | compile-time-parameterised shaders |

**`compose` is the critical feature and it must be in Raven's semantic phase.** It is what lets
`ForwardPlus.rvn` be written once against `IMaterialSurface` and be instantiated per material. If
Raven's semantic model does not support shader-typed members with compile-time resolution, the engine
falls back to string-templating shader source, which is where Stride was fifteen years ago and where
nobody should go voluntarily.

Practical consequence: **shader-typed members and permutation constants are the two Raven semantic
features on the engine's critical path.** Everything else in Raven's Phase 2 can land in any order.

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

## Testing

| Layer | Test |
|---|---|
| Parse | Existing golden-tree + round-trip corpus, extended to the whole `Raven/Library` tree — every shipped shader must round-trip byte-identically |
| Semantic | Positive/negative fixture pairs; each diagnostic ID has a test that triggers it and a test that does not |
| `compose` resolution | Golden test on the resolved shader tree for each material-feature combination in the standard material |
| SPIR-V emission | Every emitted module passes `spirv-val`; golden SPIR-V disassembly (`spirv-dis`) snapshots so codegen changes are visible in review |
| Cross-compilation | Every module cross-compiles via SPIRV-Cross to GLSL 450 / ESSL 300 / HLSL 60 / MSL / WGSL without error; the GLSL/ESSL output additionally passes `glslang` |
| **Differential emitter test** | Raven's own SPIR-V vs `shaderc`-compiled Raven GLSL, over the whole `Raven/Library` corpus, compared for semantic equivalence. Catches the hard class of bug: an emitter that is internally consistent and semantically wrong. Note it is blind to bugs in the shared IR — hence the numeric tests below |
| Numeric | BRDF functions ported to C# and compared against a GPU compute dispatch over a parameter sweep — the shader and the CPU reference must agree to 1e-4. This is the test that catches "the shader is subtly wrong" |
| Permutations | `UsedPermutationKeys` correctness: passing an unused define must produce a byte-identical module and the same cache key |
| Layout | Constant-buffer offsets from reflection compared against a GPU readback of a known pattern, per backend — the padding-rule test |
| Performance | Compile time for the full library, gated; incremental recompile of one leaf shader gated at < 500 ms |
