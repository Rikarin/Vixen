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

An integer literal too large for `int` takes the `uint` shape rather than
widening to a type that does not exist.

## Usage

As a CLI tool
```
./raven compile --target glsl <input> <output>
```

`<output>` with an extension names a single file, and then the shader must have
exactly one stage. Anything else is a directory, which is what a shader with
both a vertex and a pixel entry point needs — it writes one file per stage,
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
| `-t`, `--target` | Backend to generate for: `glsl` or `spirv`. |
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

    [PixelShader]
    [Semantic("SV_Target")]
    func Pixel(normal: float3, uv: float2): float4 {
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

Still missing: a **storage image** — a writable texture. GLSL wants a format qualifier on the
declaration and SPIR-V an image format on the type, so it needs syntax that does not exist yet.

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

What a compute shader cannot do yet is **persist anything**: there are no storage buffers and no
storage images, so it can read bindings and compute but has nothing writable to store into. That
gap is tracked in [docs/plan/07](../docs/plan/07-raven-shader-pipeline.md).

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

    [PixelShader]
    func Pixel(): float4 {
        val n = normalize(normalWS)
        return float4(n * 0.5f + 0.5f, uv.x)
    }
}
```

Nothing declares a direction. The vertex stage writes both streams, so both are its outputs; the pixel
stage reads both, so both are its inputs — worked out from what each stage's code does, which is why
`WriteNormal` can contribute one without any signature between it and the pipeline mentioning it.

A stream's location is its position in the shader's declaration list, so the writing stage and the
reading stage agree on it without either knowing about the other. The consequence is that a stage's own
parameters are located *after* the streams: adding a stream renumbers the vertex attributes, which the
reflection reports.

A stream is not a binding — no descriptor, nothing the host writes — and it does not cross a `.rvnlib`
boundary, because its location belongs to the shader that declares it.
