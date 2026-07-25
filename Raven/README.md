# Raven
### Universal Shader Compiler

Project is in it initial phase. Mostly as a research project.

## Overview

- Language is inspired by Typescript, C#, Kotlin and Stride shading language.
- Library's API is based on Roslyn.
- Targeting GLSL, SPIR-V, later HLSL and Metal.
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

// Code generation — one translation unit per pipeline stage
var backend = TargetBackends.Create("glsl")!;

foreach (var unit in backend.Generate(module, bag)) {
    File.WriteAllText($"{unit.Name}{backend.FileExtension}", unit.Code);
}

// TODO: SPIR-V, HLSL, Metal
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

The full syntax sample lives in [`Feed/Example1.rvn`](Feed/Example1.rvn).
