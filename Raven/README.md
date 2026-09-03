# Raven
### Universal Shader Compiler

Project is in it initial phase. Mostly as a research project.

## Overview

- Language is inspired by Typescript, C#, Kotlin and Stride shading language.
- Library's API is based on Roslyn.
- Targeting GLSL and SPIR-V, later HLSL and Metal. Both are working.
- GLSL is the easiest to implement as it's just a transpiler.
- I have no idea how to do the semantic passes. LOL. It will be fun.
- Compiler should be able to generate an "interaction" classes such as stride do.
- Have some generator interface so other languages can be targeted as well.
- Package manager for shaders? Maybe a bit of overkill for the size of this project.
- But it will be easy to distribute shaders or shader libraries (math and stuff).


## Not supported compared to Roslyn

Never had them:

- goto_statement, labeled_statement, lock_statement
- throw_statement, throw_expression, try_statement
- unsafe_statement, yield_statement
- stack_alloc_array_creation_expression
- function pointers, `ref readonly` types, `scoped` types

**Removed, because a GPU has no way to represent them:**

- **lambdas** — no function pointers, no closures
- **nullable types (`T?`), `null`, `??`, `??=`, postfix `!`** — there are no null references
- **anonymous objects** — no boxing, no dynamic dispatch
- **`char` and character literals**
- **`long`** — no 64-bit integers
- **`object`**
- **`string` as a type.** String *literals* still exist, because attribute
  arguments such as `[Semantic("SV_Target")]` are compile-time metadata. Using
  one as a value is an error.
- **String interpolation**, which follows from the line above rather than being a
  separate decision: an interpolation is an expression whose value is a `string`.
  ⚠ `$` is `RVN1002` and the lexer carries it as **trivia**, so in an attribute —
  the one position a literal is legal — `[Semantic($"SV_Target{0}")]` binds the
  semantic name to the braces verbatim, and only `RVN1002` being an error keeps
  that out of a backend. Both halves are pinned in `RemovedConstructsTests`.

An integer literal too large for `int` takes the `uint` shape rather than
widening to a type that does not exist.

## Usage

As a CLI tool
```
./raven compile --target glsl <input> <output>
```

`<output>` with an extension names a single file, and then the shader must have
exactly one stage. Anything else is a directory, which is what a shader with
both a vertex and a fragment entry point needs — it writes one file per stage,
named after the shader:

```
./raven compile Lambert.rvn out/
out/Lambert.vert.glsl
out/Lambert.frag.glsl

./raven compile --target spirv Lambert.rvn out/
out/Lambert.vert.spv
out/Lambert.frag.spv
```

| | |
|---|---|
| `-t`, `--target` | Backend to generate for: `glsl`, `spirv` or `essl` — see [Cross-compilation](#cross-compilation). |
| `-D`, `--define` | Set a `[Permutation]` key: `-D UseSkinning=true`, `-D TapCount=8`. A bare name means `true`. Repeatable. |
| `-C`, `--compose` | Fill a `compose` slot: `-C diffuse=Lambert`, or `-C Lit.diffuse=Lambert` when two shaders declare the same slot name. Repeatable. |
| `-r`, `--reference` | Bind against a compiled library: `-r Core/Math.rvnlib`. Its declarations and lowered bodies are linked in without its source being reparsed. Repeatable. |
| `--emit-library` | Write a `.rvnlib` for these inputs *instead of* generating for a target — the compiled library other shaders reference. |
| `--emit-effect` | Also write a `.rvnfx` per shader — the compiled effect a runtime loads instead of compiling. |
| `--emit-reflection` | Also write the reflection as JSON: descriptor sets, member offsets, the flattened parameter list. |
| `--emit-ir` | Also write the target-independent IR dump. |
| `--emit-listing` | For `spirv`, also write the readable `.spvasm` listing beside the bytes. |
| `--capabilities` | Print the target features each shader requires (`Float64`, `Texture3D`, …). |
| `-v`, `--verbose` | Name every file as it is written. Otherwise a successful run is silent. |
| `--no-color` | Never colour the diagnostics. Colour is off anyway when stderr is redirected, or when `NO_COLOR` is set. |

A library is compiled once and then referenced, which is the two-step a shader library is built
with:

```
./raven compile Core/Math.rvn Core/Math.rvnlib --emit-library
./raven compile Lit.rvn out/ --reference Core/Math.rvnlib
```

A reference is not the same thing as another input. An input is recompiled as part of this
compilation; a reference is read already lowered, and only what the shader reaches is linked in — so
referencing a library does not enlarge the shader that uses one function from it.

Diagnostics go to stderr with the source under them:

```
Lambert.rvn(6,16): error RVN2010: The name 'nrmalize' does not exist in the current context

  6 |     return nrmalize(v)
    |            ^^^^^^^^
```

Exit codes are `0` for success, `1` when the input produced errors, and `2` when
the command line or a path was wrong — so a build script can tell "you invoked
me wrong" from "the shader is wrong".

There are 128 diagnostic ids. Each is meant to have two tests and not one: a **trigger** showing it
fires, and a **negative** — a shader that comes within one predicate of it and must stay silent.
The second is the one that matters more, because an over-firing rule refuses correct work and cannot
be argued with, while a missing rule only lets a mistake through. 74 ids have a negative today and 54
do not; `Raven/Vixen.Raven.Tests/NegativeDiagnosticTests.cs` holds 67 of the 74 and explains the
method. Of the 54 owed, two — `RVN2003` and `RVN2014` — cannot fire on any input and so can never
have one, which puts the reachable ceiling at 126.

⚠ **Those five numbers are derived rather than typed, and this paragraph is held to them.** Four
batches of this work have run and every one found a figure in its brief wrong, twice in a correction
that was itself off by one — because a number in prose is a claim nothing evaluates.
`DiagnosticCoverageTests` counts the descriptors by reflection and the negatives out of the fixtures,
carries the owed ids as a list that only shrinks, and fails on any disagreement with this paragraph
or with `docs/overview.md` § 1.8 — in both directions. Landing a negative therefore fails the suite
until the id is struck off and the numbers here are corrected, which is the point.

Two rules to keep if you add one:

- **A negative is a *near miss*, not an unrelated valid shader.** For "X may not appear under Y" it
  is Y with something that looks like X, or X under the Y′ that is allowed. It shares the shape of
  the trigger and differs by the one fact the rule turns on.
- **Prove it by widening the rule in the compiler, watching the fixture go red, and reverting.** A
  fixture that was green before the widening and green after it proves nothing, and ⚠ a widening
  that fails to compile is not a red test — that attempt proved nothing and has to be tried again.
  ⚠ Nor is one that leaves the test green: check that the predicate you added can change the answer
  at all before you believe it. `RVN2061`'s first attempt demanded `IsConst`, which a
  `[Permutation]` marker already forces true, and `RVN2033`'s widened `MinimumArgumentCount` — which
  the applicability check never reads, because `TryMapArguments` fills defaults itself.

The order to work in is **by the cost of an over-fire, not by id**. What separates a cheap over-fire
from an expensive one is a difference of *kind*, and there have been two. The first is the
approximation: the flow analysis over-fires by being one lattice step too coarse rather than by being
written down wrong, so it went above every predicate. The second is the **comparison** — a rule that
holds a parsed or inferred representation of a fact against a declared one. Those over-fire whenever
the two disagree for a reason that is nobody's mistake (a normalisation, a parse order, an interning
identity), they say nothing about it because the rule reads like a tautology, and they reach every
declaration of their kind. `RVN2064` was one, and so are `RVN2083`, `RVN2108`, `RVN2109` and
`RVN2138`. Hunt that shape before you work down the id list. A rule scoped to a whole file
refuses a file, and the shipped library's files each hold several entry points and several features:
`RVN2050` keyed on anything but the stage refuses every graphics shader, `RVN2100`–`RVN2103` refuse
every shader that has a varying, `RVN2054` narrowed to the shader refuses every `struct`, `protocol`
and `enum` in the library. Above all of those sits the flow analysis — `RVN2127`, `RVN2128`,
`RVN2129` — because it is an *approximation* rather than a predicate: every other rule over-fires
only by being written down wrong, an analysis over-fires by being one lattice step too coarse, and it
reaches every function in the language rather than one construct.

⚠ **`RVN2064` was the first over-fire three batches of this found**, and it needed no widening — the
fixture was red on the rule as shipped. `PermutationValues.TryParse` tries bool, then int, then uint,
so `-D Slots=16` is an `int` whatever key it is for and the `uint` branch is reached only above
`int.MaxValue`. Comparing CLR types therefore rejected every value a build could supply for a `uint`
key: the define reported `RVN2064`, the key kept its declared default, and the variant compiled as
though nothing had been asked for. `SuppliedValue.TryCoerce` lets the declared type decide and the
parsed type only reach it; a negative against a `uint` is still a mismatch.

`UnprovenDiagnosticTests` covers the other half: an id that nothing ever makes fire is not a rule at
all. `Every_declared_descriptor_has_a_raise_site` fails on any descriptor declared without one —
that is how `RVN2012` was found, having shipped for as long as it existed with nothing behind it.

As a library
```csharp
var text = File.ReadAllText("Shader.rvn");
var tree = SyntaxTree.ParseText(text, path: "Shader.rvn");

// Syntax Tree
var root = tree.GetRoot();

// Semantic model
var compilation = Compilation.Create("MyShaders", tree);

foreach (var diagnostic in compilation.GetDiagnostics()) {
    Console.WriteLine(diagnostic);   // Shader.rvn(12,9): error RVN2010: ...
}

var model = compilation.GetSemanticModel(tree);
var symbol = model.GetSymbolInfo(someExpression).Symbol;   // what a name refers to
var type = model.GetTypeInfo(someExpression).Type;         // what type it has

foreach (var entryPoint in compilation.GetEntryPoints()) {
    Console.WriteLine($"{entryPoint.Stage}: {entryPoint.ToDisplayString()}");
}

// Lowering to the target-independent IR
var bag = new DiagnosticBag();
var module = Lowerer.Lower(compilation, bag);
IrVerifier.Verify(module, bag);

Console.WriteLine(IrPrinter.Print(module));   // readable IR dump

// Code generation — one translation unit per pipeline stage.
// "spirv" works the same way; its units carry bytes as well as a listing.
var backend = TargetBackends.Create("glsl")!;

foreach (var unit in backend.Generate(module, bag)) {
    var path = $"{unit.Name}{backend.FileExtension}";

    if (unit.Binary is { } binary) {
        File.WriteAllBytes(path, binary);   // .spv
    } else {
        File.WriteAllText(path, unit.Code); // .glsl
    }
}

// TODO: HLSL, Metal
```


## Language Example

```typescript
package Vixen.Shaders

shader Lambert {
    const val Ambient = 0.1

    var world: mat4
    var lightDirection: float3
    var baseColor: float4 = float4(1, 1, 1, 1)

    var albedo: Texture2D
    var albedoSampler: Sampler

    // Get-only, because a uniform is host state: a shader can derive a value from a binding
    // but never store back into one (RVN2119). A writable value is a local or an RWBuffer.
    var exposure: float {
        get => baseColor.a
    }

    func Diffuse(normal: float3): float {
        val ndotl = dot(normalize(normal), normalize(-lightDirection))
        return max(ndotl, Ambient)
    }

    [VertexShader]
    [Semantic("SV_Position")]
    func Vertex(position: float3): float4 {
        return world * float4(position, 1)
    }

    [FragmentShader]
    [Semantic("SV_Target")]
    func Fragment(normal: float3, uv: float2): float4 {
        val sampled = albedo.Sample(albedoSampler, uv)
        val lit = Diffuse(normal)
        return float4(sampled.rgb * baseColor.rgb * lit, sampled.a)
    }
}

struct Ray {
    var origin: float3
    var direction: float3

    func At(t: float): float3 => origin + direction * t
}
```

The full syntax sample lives in [`Library/Example1.rvn`](Library/Example1.rvn), and a compute
shader in [`Library/Example2.rvn`](Library/Example2.rvn).

A file holds a `package` line, its imports, and **type declarations only** — `shader`, `struct`,
`protocol`, `enum`. There are no free functions and no package-level constants. A helper is a
`static func` on a field-less struct, which is what [`Library/Core/Math.rvn`](Library/Core/Math.rvn)
has said in its own header since it was written: "there is no namespace-level function, and a
field-less struct costs nothing — it never reaches the IR, only its functions do". A member written
straight into a file is `RVN2054`.

⚠ It reported *nothing at all* before that id existed, which is worse than either answer. The
compilation unit and a type body share one `ParseMemberDeclaration`, so a package-level `func`
parses into a real declaration; the compilation then kept the members that name a type and dropped
the rest without a word. The body was never bound, so an undefined name inside it was silent too —
the file compiled clean around a function that was not there, and calling it reported `RVN2010` at
the call site, the one line that was right. Reported rather than bound because a namespace here
holds namespaces and types and nothing else, so no lookup could ever reach one.

### Line breaks

A newline ends a statement, so it is a terminator nearly everywhere. Inside the parentheses of a
signature or a call it is layout instead, and a wide one may be broken over lines:

```typescript
shader Fade {
    var world: mat4

    static func March(
        origin: float3,
        direction: float3,
        maxDistance: float,
        maxSteps: int,
        threshold: float
    ): float3 {
        var travelled = 0f

        for (step in 0 .. maxSteps - 1) {
            travelled = min(travelled + threshold, maxDistance)
        }

        return origin + normalize(direction) * travelled
    }

    [VertexShader]
    [Semantic("SV_Position")]
    func Vertex(position: float3, normal: float3): float4 {
        val marched = March(
            position,
            normal,
            4f,
            8,
            0.01f
        )

        return world * float4(marched, 1f)
    }
}
```

**Three positions, not "anywhere inside the parens"**: after the `(`, after each `,`, and before
the `)`. Nothing in the grammar begins with a `,` or a `)`, so a newline in front of one cannot be
the end of anything — which is what makes these three safe and the rest not. A newline in the
middle of a parameter or an argument is still a terminator, and still an error.

Nothing else changes: the newlines are trivia, so the tree still reproduces the file, and a
signature reads the same to a caller, to the reflection and to both backends however it is laid
out. The eleven-parameter signatures in `Library/DistanceFields/DistanceField.rvn` are what wanted
this — they ran past 120 columns with nowhere to break.

⚠ **A binary expression cannot be broken over lines at all, and for three shipped shaders that was a
silent miscompile.** Grouping parentheses are not a call's, so `(\n a\n + b\n)` is `RVN1001`; so is
trailing the operator, `x = x +` with the operand below. What is left is leading the operator:

```typescript
delta.position = delta.position
    + float3(…) * weight          // ⚠ not a continuation — a second statement
```

which parses cleanly into *two* statements, because a newline ends one. The first stores what it
just loaded, the second is a unary `+` whose value nothing reads, and the accumulation is not there.
`ClusterRaster.rvn`, `Terrain/GrassScatter.rvn` and `Terrain/Impostor.rvn` each shipped in that
state — `GrassScatter.comp.spv` held the jitter as an `OpFMul` with no consumer, so every blade
stood on the exact centre of its cell whatever `GrassType.Jitter` said, and the host-side parity
test was green because it re-implements the shader's arithmetic in C# rather than reading it.
`RVN2141` refuses the second statement now. Name the operand in a `val`, or use `+=`.

### `compose`

A `compose` slot is a protocol-typed member filled by a concrete shader chosen when the shader is
compiled, so a pipeline shader is written once and instantiated per material:

```typescript
protocol IMaterialSurface {
    func Compute(inout d: MaterialData)
}

shader MetalRoughnessSurface : IMaterialSurface {
    var baseColor: float3
    var metalness: float

    func Compute(inout d: MaterialData) {
        d.diffuseColor = baseColor * (1f - metalness)
    }
}

shader Forward {
    compose val surface: IMaterialSurface
    // ...
}
```

`--compose surface=MetalRoughnessSurface` picks the implementation. The call resolves at compile time
— only the chosen implementation is emitted, and there is no dispatch.

**The implementation's own bindings become the effect's descriptors.** A feature's material parameters
are part of what the host binds, so they are merged into the consuming shader and reported by the
reflection under a name qualified by the feature that declares them —
`MetalRoughnessSurface.baseColor`, or `Layered.Ggx.alpha` when a feature fills a slot of its own.
Qualified always rather than only on a clash, so adding an unrelated feature never renames another
one's parameters. Features are authored independently and do collide: three of the features in
[`Library/Material/MaterialSurface.rvn`](Library/Material/MaterialSurface.rvn) declare a `strength`.

Two slots filled with the *same* implementation share one set of parameters — the implementation is
one shader with one set of storage. Per-slot parameters would mean instantiating it per slot, which
`compose` does not do.

**Every slot in the compilation has to resolve, reached or not**, because a slot with no implementation
is a shader that cannot be emitted and finding that out per entry point rather than per declaration
would report it against the wrong file. That is a heavier obligation than it looks: it is about the
*compilation*, so a compute shader sharing a package with a pass that declares `surface` has to answer
for `surface` too.

A slot may therefore name its own **default**, used whenever the compilation binds nothing:

```typescript
shader Forward {
    compose val irradiance: IIrradianceSource = NoIrradiance
}
```

What this is for is a feature a shader can *do without*. Without it, a pass that can read indirect light
makes indirect light everybody's problem — every material compiled beside it has to name something for
a slot it never reaches — and the only way to decline is to not declare the slot, which is a pass that
silently cannot use the feature. A binding still wins over the default, so naming a real implementation
is unchanged.

The initializer is a bare identifier and not an expression, because what it names is a *type*: there is
nothing to evaluate, and `RVN2072` says so if it is given anything else. A slot with neither a binding
nor a default is `RVN2073` as before.

### `inout`

A parameter marked `inout` is passed by reference, so the callee's changes reach the caller:

```typescript
struct Feature {
    static func Apply(inout s: Surface, tint: float3) {
        s.color = s.color * tint
    }
}
```

**Copy-in/copy-out, not aliasing.** The argument's value goes in and the parameter's value comes
back out when the call returns. GLSL defines its own `inout` the same way and SPIR-V has no
reference type at all, so a promise of aliasing could not be kept on either target. Two `inout`
arguments naming the same storage therefore do not interfere until the copies are written back, in
argument order.

The argument must be assignable storage of *exactly* the parameter's type. Exactly, because a
widening on the way in would have to narrow on the way out and lose whatever the callee wrote — so
an `int` passed to an `inout float` is an error rather than a silent round trip. `inout` cannot
appear on an entry point's parameter (the pipeline has nowhere to copy back to), on an operator's
(an expression has no syntax for it), or alongside a default (an omitted argument has no storage).

Its reason for existing is the composable material interface: a feature reads the surface as
previous features left it and writes back, so adding a feature to the chain changes no other
feature's signature.

### Sized arrays

An array type carries its length, and the length can be any compile-time constant:

```typescript
shader ForwardPlus {
    /// The host's budget, so a project that ships eight lights does not pay for sixty-four.
    [Permutation] val MaxLights: int = 16

    var lights: PunctualLight[MaxLights]
    var lightCount: int = 0

    func Punctual(): float3 {
        var total = float3(0f)

        for (i in 0 .. MaxLights - 1) {
            if (i >= lightCount) {
                break
            }

            total += Shade(lights[i])
        }

        return total
    }
}
```

The length is part of the *type*: `float[4]` and `float[]` are different types and neither converts to
the other. That is not pedantry — everything downstream needs the number. SPIR-V's `OpTypeArray` takes a
constant extent, GLSL writes it into the declaration, the `ArrayStride` decoration is computed from it,
and the host reads it back out of the reflection to size the buffer it uploads. An *unsized* array has no
answer for any of them, so it is refused by both backends, and letting a sized array widen into one would
only be a way to fail later.

A size must fold at compile time — a GPU allocates nothing at run time. A literal, a `const val`, an enum
member and a `[Permutation] val` all qualify; a uniform does not. Zero and the negatives are refused
because `OpTypeArray` requires a positive length and GLSL rejects a zero-length array too. A **constant**
index outside the array is an error rather than undefined behaviour, which on a GPU means a wrong pixel on
one driver and a device loss on another; a runtime index is left alone.

**`[…]` sizes in a type and indexes in an expression** — the position decides, never what is between the
brackets. So `var data: float[4]` declares four floats, `data[4]` is an out-of-range access, and
`(a[4]) - 1` is arithmetic rather than a cast. `T[a][b]` nests right to left, as in C and GLSL: two arrays
of `b`.

A collection literal infers its own length, which is what lets a spread be flattened:

```typescript
val kernel = [0.42f, 0.5f, 0.08f]   // float[3]
val padded = [0f, ..kernel, 0f]     // float[5]
```

Passing an array is **by value** in both targets, as GLSL's copy-in and SPIR-V's `OpFunctionCall` both
specify. A `float[3]` filter kernel is a fine parameter; a `mat4[256]` bone palette is not — it would copy
sixteen kilobytes at every call. Index a large array where it is declared, and pass what you took out of
it.

Still missing: a **storage buffer**, which needs a writable storage class and an unsized last member.

### Writable resources

`Buffer<T>` is a read-only storage buffer, `RWBuffer<T>` a read-write one — the first thing a Raven
shader can store into:

```typescript
shader ParticleUpdate {
    var particles: RWBuffer<Particle>
    var deltaTime: float = 0.016f

    [ComputeShader(64)]
    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
        val index = int(id.x)

        // The dispatch is rounded up to a whole workgroup, so the tail invocations have no
        // particle. An out-of-range access is undefined on a GPU, so this test is not optional.
        if (index >= particles.Length) {
            return
        }

        var p = particles[index]
        p.position = p.position + p.velocity * deltaTime
        particles[index] = p
    }
}
```

Both are `VK_DESCRIPTOR_TYPE_STORAGE_BUFFER`, laid out **std430**. That is the reason a buffer is not
just a bigger uniform block: std140 rounds an array's stride up to 16 and std430 does not, so a
host-side `Particle[]` uploads as a straight memcpy. `Length` is answered at run time — the host decides
how many elements it bound — which is what distinguishes a buffer from a sized array.

**Written with angle brackets, but not generic.** It is a structural type, the same as `T[4]`: there is
no declaration to find and no substitution to do. Raven's real generics do not lower yet, and this does
not wait for them.

**Read-only versus read-write is one bit, not two descriptor types**, because in Vulkan it is one. The
difference is an access decoration — `readonly` in GLSL, `NonWritable` in SPIR-V — and declaring it lets
a driver hoist a load out of a loop from a buffer nothing writes to.

A buffer's element type has to be something the host can lay out, so a texture or a sampler is refused
(`RVN2118`): a descriptor is not a value and has no bytes to place.

**A bare `Buffer<mat4>` is rejected by the validator, and the fix is a struct of one member.** A matrix
inside a storage buffer has to state its stride and which way its majorness runs, and SPIR-V's only
place for those decorations is a struct *member* — so a runtime array of matrices has nowhere to put
them. `Geometry/Transform.rvn`'s `ObjectTransform` and `Geometry/Skinning.rvn`'s `BoneMatrix` are both
that wrapper, and both say so where somebody reaching for `Buffer<mat4>` will read it.

**Writing to anything else is refused.** A shader's `var` is host-uploaded state, so a store into one is
`RVN2119` — checked at the root of the access chain, which means `tint`, `tint.rgb` and
`lights[i].color` are all caught. Mutable state belongs to a local, or to a struct whose fields are
values the shader owns:

```typescript
shader Lit {
    var tint: float4

    // Get-only. A setter would store into a binding, which no GPU can do.
    var exposure: float {
        get => tint.a
    }
}
```

The other writable resource is a **storage image** — `[Format("rgba16f")] var target: RWTexture2D<float4>`,
read and written with `Load` and `Store`. The format is part of the declaration because both targets
put it there: GLSL as a layout qualifier, SPIR-V as the image type's own format, and without it a read
needs a capability not every device has.

### Compute

A compute entry point declares its workgroup size on the stage attribute, so the size cannot be
separated from the stage it sizes:

```typescript
shader Threshold {
    var scale: float

    [ComputeShader(8, 8, 1)]
    func Main([Semantic("SV_DispatchThreadID")] id: uint3, [Semantic("SV_GroupIndex")] slot: uint) {
        val weight = float(id.x) * scale + float(id.y)
        // ...
    }
}
```

One to three dimensions; the ones not written are 1, so `[ComputeShader(64)]` means `(64, 1, 1)`.
The size is required rather than defaulted — a shader written for 64 invocations and dispatched as
if it were 1 reads out of bounds, and nothing downstream could tell a guessed size from a chosen
one.

A compute stage has no pipeline interface: nothing feeds a parameter from a vertex buffer and no
framebuffer takes a result. So it returns nothing, and every parameter must carry a dispatch
built-in:

| `[Semantic(…)]` | Type | GLSL | SPIR-V |
|---|---|---|---|
| `SV_DispatchThreadID` | `uint3` | `gl_GlobalInvocationID` | `GlobalInvocationId` |
| `SV_GroupID` | `uint3` | `gl_WorkGroupID` | `WorkgroupId` |
| `SV_GroupThreadID` | `uint3` | `gl_LocalInvocationID` | `LocalInvocationId` |
| `SV_GroupIndex` | `uint` | `gl_LocalInvocationIndex` | `LocalInvocationIndex` |

They are unsigned in both targets, so a signed declaration is refused rather than silently
converted. A `stream` is refused too — it is a location in the pipeline's interface, and a compute
dispatch has no pipeline.

A compute shader persists through a `RWBuffer<T>` or a storage image, coordinates with the other
invocations through the atomics below, and stages through the workgroup-shared memory below that.

### Workgroup-shared memory

`groupshared` declares storage one workgroup shares: one copy per group rather than one per
invocation, which is the whole difference between it and a local.

```typescript
shader Reduce {
    const val GroupSize: int = 64

    groupshared var tile: float[GroupSize]

    var input: Buffer<float>
    var output: RWBuffer<float>

    [ComputeShader(64)]
    func Main([Semantic("SV_DispatchThreadID")] id: uint3, [Semantic("SV_GroupIndex")] local: uint) {
        tile[int(local)] = input[int(id.x)]

        // Everybody has arrived, and everything they wrote is visible. Both halves, because the code
        // after a barrier is without exception code that reads what the others wrote.
        barrier()

        if (local == 0u) {
            var sum = 0f

            for (i in 0 .. GroupSize - 1) {
                sum = sum + tile[i]
            }

            output[int(id.x)] = sum
        }
    }
}
```

It is **not a binding**: no descriptor, no `(set, binding)`, nothing the host writes, and nothing in
the reflection. So declaring one never renumbers the resources around it.

`barrier()` is an execution barrier *and* a memory barrier over shared storage, which is what GLSL's
is in a compute stage. `memoryBarrierShared()` is the memory half alone, for where the arrival is
already established.

Five things a declaration cannot be, each reported where it is written: outside a shader (`RVN2131`),
also a `const`, a `[Permutation]` key, a `compose` slot or a `stream` (`RVN2132`), a descriptor
(`RVN2133`), initialized (`RVN2134` — workgroup storage starts undefined, so write it and then
`barrier()`), or read-only (`RVN2135` — nothing else can ever write it). And only a compute stage may
reach the storage or a barrier (`RVN3012`), decided by which stages call the code rather than by where
it is written.

### Atomics

An atomic is an indivisible read-modify-write of one integer in memory, and it answers with the value
that was there:

```typescript
shader Compact {
    var alive: Buffer<uint>
    var indices: RWBuffer<uint>
    var counter: RWBuffer<uint>
    var count: int

    [ComputeShader(64)]
    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
        val index = int(id.x)

        if (index >= count || alive[index] == 0u) {
            return
        }

        // The value that comes back is the slot. Every surviving invocation gets a different one,
        // and together they are exactly 0..n — which is stream compaction, and is the reason
        // atomics exist at all.
        val slot = atomicAdd(counter[0], 1u)
        indices[int(slot)] = uint(index)
    }
}
```

`atomicAdd`, `atomicMin`, `atomicMax`, `atomicAnd`, `atomicOr`, `atomicXor`, `atomicExchange` and
`atomicCompareExchange`, on `int`, `uint`, `int64` and `uint64`. Named as GLSL names them, because
that is what both a reader and the nearer target already say.

**The first argument is storage, not a value**, and that is the one unusual thing about them. Nothing
in a signature can say so: `inout` is the language's only by-reference direction and it is defined as
copy-in/copy-out, which is exactly what an atomic must not be — a copy has nothing indivisible about
it. So it is a rule about the *call*, checked after overload resolution (`RVN2130`) and honoured by
lowering, which takes the argument's place rather than loading it. `atomicAdd(count + 1u, 1u)` is
refused rather than quietly turned into an ordinary add.

They are free functions rather than members of `RWBuffer` so the target can be any place inside one —
`counts[i]`, but also `cells[i].population`, which a member taking an index could not reach.

**And it has to be memory more than one invocation reaches.** A local is a place and is refused: GLSL
admits only "shader block storage or shared variables", and an atomic on storage one invocation owns
has nothing to be indivisible against anyway. So the root is a writable resource — the dispatch's — or
a `groupshared` variable — the workgroup's. A read-modify-write **is** a write, so an atomic on a
read-only `Buffer<T>` is the same `RVN2119` a store would give, with the same one-character fix in its
message.

**Scalar integers only.** GLSL 4.5 core has no atomic on a float and none on a vector at all, so a
wider set would be a signature one backend could not emit. Both operands and the result are the
place's type, which is also what tells SPIR-V apart from GLSL here: `atomicMin` is one name for both
signednesses and `OpAtomicSMin`/`OpAtomicUMin` are two, so the split lives in the backend that needs
it.

**Both widths, and 64 bits is why the width is worth writing down.** `int64` and `uint64` are scalar
types spelled by name, and they exist for one job: a word wide enough to hold a depth above an id and
be `atomicMax`'d as a unit, which is what a single-pass software rasterizer resolves visibility with.

```typescript
val packed = (uint64(depth) << 32) | uint64(clusterId)
val previous = atomicMax(visibility[pixel], packed)
```

Nothing widens into 64 bits on its own — `uint64(x)` is written out, because a silent widening would
make both the 32-bit and the 64-bit overload of every atomic applicable to the same call, and
tie-breaking would decide the width of an operation whose width is the point. A *literal* still
widens, since it has no type of its own to be surprised by.

They are optional hardware everywhere — `VK_KHR_shader_atomic_int64` on Vulkan, SM6.6 on D3D12, absent
from WebGPU — so a shader using them reports **two** capabilities rather than one: `Int64` for the type
and `Int64Atomics` for the operation, because a device may offer the first without the second. There
are no 64-bit vectors, and a 64-bit value cannot cross a stage boundary: an interface slot is four
32-bit components wide, so a wide one would consume two locations and stop matching the numbers the
stream plan assigned.

Both targets get **device scope and relaxed semantics** — the same two constants glslang emits for the
same GLSL. Device because a storage buffer is visible to the whole dispatch, and a workgroup-scoped
atomic on one is correct for every dispatch small enough to be a single workgroup and wrong for every
one that is not.

### Streams

A `stream` is a value one pipeline stage writes and the next reads, declared once on the shader
instead of threaded through every signature between its producer and the pipeline:

```typescript
shader Lit {
    stream var normalWS: float3
    stream var uv: float2

    var world: mat4

    func WriteNormal(normal: float3) {
        normalWS = (world * float4(normal, 0f)).xyz
    }

    [VertexShader]
    func Vertex(position: float3, normal: float3, texcoord: float2): float4 {
        WriteNormal(normal)
        uv = texcoord
        return world * float4(position, 1f)
    }

    [FragmentShader]
    func Fragment(): float4 {
        val n = normalize(normalWS)
        return float4(n * 0.5f + 0.5f, uv.x)
    }
}
```

Nothing declares a direction. The vertex stage writes both streams, so both are its outputs; the
fragment stage reads both, so both are its inputs — worked out from what each stage's code does, which is why
`WriteNormal` can contribute one without any signature between it and the pipeline mentioning it.

A stream's location is its position in the shader's declaration list, so the writing stage and the
reading stage agree on it without either knowing about the other. The consequence is that a stage's own
parameters are located *after* the streams: adding a stream renumbers the vertex attributes, which the
reflection reports.

A stream is not a binding — no descriptor, nothing the host writes — and it does not cross a `.rvnlib`
boundary, because its location belongs to the shader that declares it.

### Cross-compilation

`--target essl` writes GLSL ES rather than Vulkan GLSL, through
[SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) in `Vixen.Raven.Transpile`:

```
./raven compile --target essl Lambert.rvn out/
out/Lambert.vert.glsl        # #version 300 es
out/Lambert.frag.glsl
```

**This is ADR-012 rather than a fourth emitter.** SPIR-V is the canonical output; every other dialect
is a *translation* of it. One well-tested backend beats five half-tested ones, and SPIRV-Cross is
Khronos's own — it is what MoltenVK runs underneath in any case.

⚠ **`--target glsl` is not a GL dialect and never was.** It is Vulkan GLSL, and a GL or GLES front end
rejects it three ways over. Measured with `glslangValidator` on this repository's own `lambert`
golden:

| What Raven writes | What a GL front end says |
|---|---|
| `uniform texture2D albedo;` + `uniform sampler s;`, read as `sampler2D(albedo, s)` | `syntax error, unexpected IDENTIFIER` — on **desktop** GLSL 450, not only on ES. These are `GL_KHR_vulkan_glsl` and there is no GL profile that parses them |
| `layout(std140, set = 2, binding = 0)` | `'descriptor set' : only allowed when using GLSL for Vulkan` |
| no `precision` line anywhere | `'float' : type requires declaration of default precision qualifier` |

SPIRV-Cross fixes all three, and fixes them from the **module** rather than from the text — combining
a texture and a sampler is a rewrite of the SPIR-V, which is why the transpiler can report which pairs
it made and a regex over the source could not. ⚠ Each combined object is created **unnamed** and would
otherwise be emitted as `_112`; every GL profile below 3.1 binds samplers by name after the link, so
they are renamed after the texture they came from.

**What it deliberately does not do** is the clip-space and depth-range fixup. The engine is +Y up with
reversed depth in `[0, 1]` and GL is neither; that is a *convention* rather than a dialect, it lives in
`Platform/Vixen.Graphics.OpenGL/GlslTranslator.cs`'s wrapped `main`, and doing it here as well would
apply it twice on any profile that has `glClipControl`.

#### The dialect gate

The version is a knob, so `#version 310 es` and `320 es` come free — but what each version *has* is
not, and a refusal is `RVN4001` naming the shader and the feature:

| | 3.00 | 3.10 | 3.20 |
|---|---|---|---|
| vertex, fragment | ✅ | ✅ | ✅ |
| compute, storage buffer, storage image | ⬜ | ✅ | ✅ |
| geometry, an **array of textures** indexed by anything but a constant | ⬜ | ⬜ | ✅ |
| `Int64`, `Float64`, `RayQuery`, a **bindless** `Texture2D[]` | ⬜ | ⬜ | ⬜ |

⚠ **Asked by the backend rather than left to SPIRV-Cross, which does not ask.** It will emit a compute
shader under `#version 300 es` quite happily, and a `layout(std430) buffer` under it too — files
naming things the version does not define, which fail at `glCompileShader` on a device rather than at
build time on a desk.

The bottom row is the interesting one: `ClusterSoftwareRaster` needs a 64-bit atomic min for its
depth-and-payload word, and GLSL ES has no 64-bit integer at any version. **Software rasterisation is
a thing GLES does not get** — a fact about the profile, not a gap in the translator.

**Owed:** HLSL, MSL and WGSL. Each is one `Backend` enum value away and none of them is done, because
a target is not finished until something downstream will compile its output —
`Raven/Vixen.Raven.Transpile.Tests` holds ESSL to `glslangValidator` over the whole of
`Raven/Library`, and HLSL wants `dxc`, MSL wants `metal`, WGSL wants `naga` or `tint`. A dialect with
no oracle is a string, not a shader.
