# Vixen.Shaders

The engine's side of the shader contract: **typed keys** for naming a shader's parameters, and
**constant-buffer writers** whose offsets were computed by the shader compiler rather than here.

Raven ([docs/plan/07](../../docs/plan/07-raven-shader-pipeline.md)) compiles a `.rvn` shader and
reports its reflection — descriptor sets, member offsets, permutation keys. `Vixen.Shaders.Generators`
reads that reflection at build time and emits a `…Keys` class and a `…Constants` struct per shader.
This project is what the generated code is written against.

## The shape

```csharp
// Generated from Lighting.rvn — no reflection, no name lookup, no layout arithmetic.
var constants = new LightingConstants {
    WorldViewProjection = camera.ViewProjection,
    Ambient             = new Vector3(0.1f, 0.1f, 0.12f),
    Exposure            = 1.4f,
    LightCount          = visible.Count,
};

constants.Write(buffer);                       // every offset baked at build time

for (var i = 0; i < visible.Count; i++) {
    new LightingLightsElement {
        Position  = visible[i].Position,
        Range     = visible[i].Range,
        Color     = visible[i].Color,
        Intensity = visible[i].Intensity,
    }.Write(buffer, i);
}
```

## Why the offsets are copied and never recomputed

The numbers in the generated writer come out of the same `ShaderLayout` pass that told the GLSL and
SPIR-V emitters where to put things. Computing them here would be a **second implementation of
std140**, and the two would eventually disagree — silently, because every byte still lands inside the
buffer. The symptom of that class of bug is "setting the ambient colour resets the exposure", three
subsystems from the cause.

Three cases where the CLR's layout and the shader's genuinely differ, and the writers exist to know
the difference:

| Shape | The trap |
|---|---|
| `float3` followed by `float` | std140 packs both into one 16-byte slot. A `Vector4`-shaped write clears the scalar |
| `bool` | four bytes in a block, not one. Writing one leaves three bytes of whatever was there, and a non-zero one of them is a `true` nobody set |
| `mat3` | three columns of 12 bytes in 16 of space. The only matrix that has to be taken apart |
| `float[4]` | 64 bytes, not 16 — std140 rounds an array's element stride up to 16 whatever the element is |

And one case where they deliberately do **not**: a `Matrix4x4` is a straight blit. The engine stores
row-major with the translation in `M41..M43`; the shader reads the same bytes as `ColMajor`, which
makes its matrix the host's transpose — which is exactly what `mul(v, M)` needs. Transposing here
would compute the wrong transform more expensively. See
[docs/plan/07 § E](../../docs/plan/07-raven-shader-pipeline.md).

## Keys

A `ParameterKey` is **interned by name**, so equal names are the same object and a key costs a
pointer compare as a dictionary key. Two assemblies that generated bindings from the same shader get
the same key rather than two that merely look alike.

`PermutationKey<T>` is a different type from `ParameterKey<T>` rather than a flag on it, because the
two are consumed by different machinery and confusing them is expensive in one direction: setting a
permutation at draw time does nothing until something recompiles, and making a value key a
permutation multiplies the shader cache for no gain. Raven reports which is which — a `[Permutation]`
field is not a uniform — so the generator never guesses.

Registering one name as two types, or as both a value and a permutation, throws at creation. The
alternative is writing four bytes of the wrong interpretation into a correctly sized slot: a value
that is wrong rather than absent, which is the harder kind to notice.

## What is not here yet

`ParameterCollection`, the effect system and the three-tier bytecode cache. They are the rest of
Phase 5's `Vixen.Shaders` bullet in [docs/plan/14](../../docs/plan/14-roadmap.md) and they need a
consumer — `Vixen.Rendering` — to be designed against. Keys and writers had one already: the
generator.
