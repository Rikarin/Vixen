---
title: Reading a shader's reflection
slug: raven/shader-reflection
kind: guide
area: Raven
summary: What the compiler can tell a host about a shader it produced — descriptor sets, vertex inputs, fragment outputs, push constants, specialisation constants, permutations and value parameters — plus the std140/std430 layout rules a uniform block's offsets come from.
api: [T:Vixen.Raven.Reflection.RavenReflection, T:Vixen.Raven.Reflection.ReflectionBuilder, T:Vixen.Raven.Reflection.DescriptorSetInfo, T:Vixen.Raven.Reflection.BindingInfo, T:Vixen.Raven.Reflection.DescriptorType, T:Vixen.Raven.Reflection.MemberInfo, T:Vixen.Raven.Reflection.ShaderDataType, T:Vixen.Raven.Reflection.ShaderStages, T:Vixen.Raven.Reflection.VertexInputInfo, T:Vixen.Raven.Reflection.FragmentOutputInfo, T:Vixen.Raven.Reflection.PushConstantInfo, T:Vixen.Raven.Reflection.SpecConstantInfo, T:Vixen.Raven.Reflection.PermutationInfo, T:Vixen.Raven.Reflection.ValueParameterInfo, T:Vixen.Raven.Reflection.ParameterInfo, T:Vixen.Raven.Reflection.StageInterface, T:Vixen.Raven.Reflection.ShaderLayout, T:Vixen.Raven.Reflection.LayoutRule, T:Vixen.Raven.Reflection.BindingPlan, T:Vixen.Raven.Reflection.PlannedBinding, T:Vixen.Raven.Reflection.PlannedStream, T:Vixen.Raven.Reflection.StreamPlan]
tags: [raven, shaders, reflection, pipelines]
since: 0.1
status: preview
related: [raven/compiling-a-shader, raven/compiled-artefacts]
---

## What it is

A `RavenReflection` is everything a host needs to build a pipeline for a shader without reading
the shader. `ReflectionBuilder.Describe` produces one from a lowered `IrShader`, or a dictionary of
them from a whole `IrModule`:

- `Sets` — the descriptor sets, as `DescriptorSetInfo`, each holding `BindingInfo`s that carry the
  binding index, its `DescriptorType`, the `ShaderStages` that use it and, for a block, its
  `MemberInfo` members with their offsets.
- `VertexInputs` and `Outputs` — the stage interface at either end, as `VertexInputInfo` and
  `FragmentOutputInfo`: a location, a name, a `ShaderDataType` and the semantic if one was written.
- `PushConstants` and `SpecConstants` — the two ways a value reaches a shader without a descriptor.
- `Permutations` and `ValueParameters` — what the shader declared as `[Permutation]` and as a value
  parameter, with defaults.
- `Parameters` — the flattened material-facing view, as `ParameterInfo`, derived from the sets.
- `RequiredCapabilities` — what the module needs of a device.
- `UsedPermutationKeys` — ⚠ narrowed to keys *this shader* declares, not the compilation's whole
  list. A cache key hashes this, and a key another shader branched on would otherwise be reported
  as having affected this output. Describing a whole library at once is what made that visible: a
  two-permutation post effect reported forty.

`StageInterface` is the pair of a stage's inputs and outputs, used when two stages have to agree.

## What it is for

Three consumers, and they want different halves.

**A pipeline builder** wants `Sets` and `VertexInputs`: a descriptor set layout is exactly the
bindings, their types and their stage flags, and a vertex input layout is exactly the locations and
their formats. `Tools/Vixen.ShaderCompiler` translates a `RavenReflection` into the engine's own
`DescriptorBinding`s on this basis.

**A material system** wants `Parameters` and `ValueParameters` — the names and types an author
sets, without the descriptor machinery underneath them.

**A writer of bytes into a uniform block** wants `ShaderLayout`, which answers where each member
goes. `Alignment`, `Size`, `MatrixStride`, `ArrayStride` and `Members` all take a `LayoutRule` —
`Std140` for a uniform block, `Std430` for a storage buffer — because the two disagree about array
and matrix stride and a struct laid out under the wrong one is silently misread.

## Using it

**Describe the lowered shader, not the generated text.** `ReflectionBuilder.Describe` reads the
`IrShader`, so what it reports is what the back ends decorated — set and binding indices come from
`BindingPlan`, which is the same plan the emitters used. Reflecting the SPIR-V afterwards would be
a second implementation that can disagree with the first.

**Pass the compilation's used keys in.** The overload takes `usedPermutationKeys` because it is a
fact about *how this variant was produced*, not about its contents, and the IR cannot hold it.
`Compilation.UsedPermutationKeys` is what to hand it.

**`BindingPlan` is the layer under the reflection**, and it is public because the back ends and the
shader compiler both need it. `BindingPlan.Of(shader)` gives the `PlannedBinding`s — including
which ones are blocks, which aliases fold into one, and `RecordIndex` for a per-material block that
is one record of a buffer rather than a set bound per material. `StreamPlan` and `PlannedStream`
are the same idea for stage-to-stage streams: one assignment of locations that both stages read, so
a vertex output and a fragment input cannot end up on different numbers.

## Examples

Describing every shader in a module:

```csharp no-compile="a fragment; the module and the compilation are the caller's"
var reflections = ReflectionBuilder.Describe(module, compilation.UsedPermutationKeys);

foreach (var (name, reflection) in reflections) {
    foreach (var set in reflection.Sets) {
        foreach (var binding in set.Bindings) {
            Console.WriteLine($"{name}: set {set.Set} binding {binding.Binding} is {binding.Type}");
        }
    }
}
```

Where a member of a uniform block goes:

```csharp no-compile="a fragment; the member types are the caller's"
var (offsets, size) = ShaderLayout.Members(memberTypes, LayoutRule.Std140);
```

⚠ `Std140` is the default on every one of these methods, and it is the *wrong* answer for a storage
buffer — an array of `float` is stride 16 under std140 and stride 4 under std430. Pass the rule
that matches the binding rather than taking the default, or a buffer read from the CPU side lands
four times too far apart.

Reading the reflection back out of a compiled effect, which is the usual case at runtime — a
`CompiledEffect` carries its `Reflection` rather than making a host recompile to get one:

```csharp no-compile="a fragment; the path is the caller's"
var effect = CompiledEffectReader.ReadFile("Lit.rvnfx");
var vertexLocations = effect.Reflection.VertexInputs.Select(input => input.Location);
```

## See also

- [Compiling a shader](compiling-a-shader.md) — the four phases that produce the `IrShader` this
  describes.
- [Compiled artefacts](compiled-artefacts.md) — where a reflection is stored so that a host does
  not recompute it.
