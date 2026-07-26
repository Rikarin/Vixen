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

    var exposure: float {
        get => baseColor.a
        set => baseColor = float4(baseColor.rgb, value)
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

The full syntax sample lives in [`Library/Example1.rvn`](Library/Example1.rvn).

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
