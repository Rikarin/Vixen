# 07 — Raven Shader Pipeline

Raven already exists at `Vixen/Raven` with a real front end: an ANTLR grammar (353-line lexer,
484-line parser), a Roslyn-style green/red syntax tree generated from `Syntax.xml` (83 concrete + 18
abstract node types), full trivia and spans, a diagnostics model, golden-file parse tests, and
round-trip fidelity on the sample corpus. Its own `docs/IMPLEMENTATION_PLAN.md` marks Phases 0 and 1
complete, with semantic analysis (Phase 2) as the next work.

Your brief says Raven finishes before engine work starts. This document specifies **the contract the
engine requires from Raven**, so that "finished" has a precise definition, plus one recommendation
about Raven's own phase order.

## Raven's phase order: ✅ **unchanged, by decision (Q10)**

The plan previously recommended moving SPIR-V ahead of GLSL in Raven's roadmap. **That recommendation
was declined — Raven keeps its existing order**, and this section records the consequence and the bridge
that makes it a non-problem.

Raven's order stands as:
`2 Semantic → 3 IR → 4 GLSL → 5 CLI → 6 SPIR-V → 7 Interaction classes`

### The consequence

The engine's canonical shader IR is SPIR-V (ADR-012), and the Vulkan backend — the primary rendering
path — consumes it. Taken literally, the engine's renderer (Phase 5) would gate on Raven's *Phase 6*,
which is late in Raven's sequence.

### The bridge: GLSL → SPIR-V via `shaderc`

It does not have to gate, because `Silk.NET.Shaderc` (2.23.0) is already a dependency
([01](01-technology-decisions.md), listed as a Raven test oracle) and it compiles Vulkan-flavoured GLSL
to SPIR-V. So:

```
.rvn ──Raven Phase 4──▶ Vulkan GLSL ──shaderc──▶ SPIR-V ──▶ engine (unchanged)
                                                    ▲
.rvn ──Raven Phase 6──────────────────────────────┘   (later, drops the middle step)
```

The engine consumes SPIR-V either way. `IRavenBackend` has two implementations —
`GlslViaShadercBackend` and, later, `NativeSpirvBackend` — selected by configuration. Nothing above the
`RavenCompilation` interface knows or cares which produced the bytecode, so the swap is a one-line
change plus a golden-output comparison, not a migration.

**Two requirements this places on Raven's GLSL backend**, both worth handing over early because they
are cheap if designed in and expensive to retrofit:

1. **Emit Vulkan GLSL, not desktop-GL GLSL.** That means `#version 450` (or 460) and explicit
   `layout(set = N, binding = M)` qualifiers via `GL_KHR_vulkan_glsl`, plus `layout(push_constant)`,
   `layout(location = N)` on all stage in/out, and explicit `std140`/`std430` block layouts. Desktop-GL
   GLSL has no notion of descriptor sets, so a GL-flavoured emitter would produce output that cannot
   round-trip to SPIR-V with correct bindings — the failure would be silent and backend-specific.
   A readable side effect: this output is also exactly what a human wants to read when debugging.
2. **Reflection still comes from Raven, never from the GLSL.** `RavenReflection` (schema below) is
   produced by the semantic phase. Recovering it by parsing GLSL or by reflecting the shaderc output
   would lose `UsedPermutationKeys` and the precise member offsets the engine writes constant buffers
   by. The bridge converts *code*, not *metadata*.

### Cost, stated plainly

- One extra native dependency in the content-build path — already present, already checksummed by
  `RestoreNativeDeps`.
- A compile step per permutation that is somewhat slower than emitting SPIR-V directly. It runs in the
  content build and in the async dev-time compile, so it costs build throughput, not frame time.
- Two code paths to keep golden-tested until the native SPIR-V backend lands. This is the real cost, and
  it is bounded: the same fixture corpus runs through both and the SPIR-V must be semantically
  equivalent (validated by `spirv-val` plus the numeric BRDF tests below).
- One genuine risk: GLSL is a lossy intermediate for anything Raven can express that GLSL cannot
  (subgroup ops, some SPIR-V decorations, `float64`, mesh shaders). Those features are unavailable until
  Raven's native SPIR-V backend arrives. None are in the P1 feature set
  ([06](06-rendering-pipeline.md)), and the capability-gating architecture already handles their absence.

**Net effect: the engine is not blocked, and Raven's roadmap is untouched.** The GLSL backend that
Raven builds first becomes genuinely load-bearing rather than merely a debugging convenience, which is
arguably a better outcome for Raven than the reordering would have been.

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
| Reference oracle | Selected shaders compiled by both Raven and `Silk.NET.Shaderc` (from equivalent GLSL) and compared for semantic equivalence via reflection + numeric output on a compute readback |
| Numeric | BRDF functions ported to C# and compared against a GPU compute dispatch over a parameter sweep — the shader and the CPU reference must agree to 1e-4. This is the test that catches "the shader is subtly wrong" |
| Permutations | `UsedPermutationKeys` correctness: passing an unused define must produce a byte-identical module and the same cache key |
| Layout | Constant-buffer offsets from reflection compared against a GPU readback of a known pattern, per backend — the padding-rule test |
| Performance | Compile time for the full library, gated; incremental recompile of one leaf shader gated at < 500 ms |
