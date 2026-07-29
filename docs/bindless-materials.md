# Bindless material binding

The plan [W0-17](overview.md#32-wave-0--startable-today-fully-parallel) names, and what has been
built of it.

Three things in the engine are marked blocked on this and on nothing else: **compacted draws**,
**per-object reflection probe selection**, and **a material feature that samples a texture** —
[doc 06](plan/06-rendering-pipeline.md) § Materials calls the last one "materials are values, not
resources". They are blocked on the same sentence, which is worth stating precisely before any of it
is designed:

> `MeshRenderFeature` binds a vertex buffer, an index buffer and a material set per object.

A draw that binds a descriptor set is a draw that cannot be merged with a draw that binds a different
one. Everything else follows.

## What is built

**The RHI half.** `Vixen.Graphics.BindlessTable`, `GraphicsDeviceFeatures.MaxBindlessDescriptors`,
`DescriptorBinding.IsUnbounded()`, `DescriptorSetLayoutDescription.BindlessCapacity`, and real
descriptor-indexing support in the Vulkan backend — partially-bound and update-after-bind binding
flags, an update-after-bind layout, a dedicated pool per table sized to the capacity the host asked
for, the four device features enabled at `vkCreateDevice`, and an array-bounds check on every write.
Verified against a driver with the validation layers on
(`Vixen.Graphics.Golden.Tests/BindlessTableDeviceTests`), which is the only thing that can see any of
it: the Null backend has no layout object, no pool and no features to disagree with.

`Core/Vixen.Graphics/README.md` § *The set a shader indexes rather than a draw binds* is the
reference for that half.

**The shader half.** `Texture2D[]` — an unsized array of textures — is a type Raven accepts, and the
only unsized array outside a storage block. It reaches SPIR-V as an `OpTypeRuntimeArray` with no
`ArrayStride` under `RuntimeDescriptorArray` and `ShaderNonUniform`, GLSL as `uniform texture2D t[];`
under `GL_EXT_nonuniform_qualifier`, and the reflection as `Count == 0` — which is the number the RHI
already reads. Every subscript of one is decorated `NonUniform` on both the index and the pointer it
produces; every other array's is left alone.

The gate is the one every Raven feature has, plus one it usually does not need:
`Vixen.Graphics.Golden.Tests/BindlessSamplingDeviceTests` dispatches
`Shaders/BindlessProbe.rvn` on a driver with sixty-four invocations reading sixty-four *different*
slots and compares the readback texture by texture. That is not ceremony. A non-uniform index that
was decorated uniform is legal SPIR-V, passes `spirv-val`, and produces the right answer for every
draw that happens to use one material — which is most of a test scene and all of a golden image.

## What is not built, and in what order

### 1. ~~Raven has to be able to declare the array~~ — built

The rule it did not have: an unsized array is still `RVN2126` for every element type but a texture,
still refused with a second dimension, and still refused in a struct (`RVN2053`) or as a stream
(`RVN2103`). A resource-typed *local* or parameter is accepted by nothing downstream and reported by
nothing — a gap `Texture2D[]` shares with a plain `Texture2D` rather than one it opened.

### 2. A material becomes a record rather than a set

This is the change the three blocked items are actually waiting for.

Today `MaterialRenderFeature.Bind` writes one descriptor set per **variant** — every texture, every
sampler and the uniform block, resolved through `Effect.Bindings`. The bindless form writes the same
information into a **buffer** instead:

```
struct MaterialRecord {
    // whatever the per-material uniform block already holds, unchanged
    …
    // and one uint per texture the variant declares, in binding order
    uint albedo;
    uint normal;
    …
}
```

One storage buffer of these, indexed by a material index the per-draw block already has room for —
`ForwardLightingRenderFeature` demonstrates the shape, since the probe index and weight went into two
words of existing padding for exactly this reason.

Three things this makes true, and they are the three blocked items:

- **A draw binds nothing per material.** Sets 0 and 1 for the frame and the view, the table with
  them, and per-draw data through a dynamic offset. A run of objects sharing a pipeline is a run
  sharing every binding.
- **A reflection probe is a table index**, so per-object probe selection stops being "which of four
  cubes did the frame bind" and becomes a number in the per-object block. `EffectSetWriter`'s
  remark about `probes[clamp(probeIndex, …)]` — a slot no probe occupies still has to hold a cube —
  stops being a constraint at all.
- **A material feature may sample.** Its texture is a name the material carries and an index the
  table hands out; nothing in the shader has to know a binding number, which is the authoring gap
  doc 06 records.

**The fallback is today's path, unchanged.** `MaterialRenderFeature` already forks on
`Device`/`Descriptors` being set, and `DescriptorsOf` already falls back to `Material.Descriptors` —
the README calls that "what a host with a bindless table or a texture array still wants". So the fork
exists; what is added is a third arm rather than a rewrite. That matters more than it looks:
[ADR-011](plan/01-technology-decisions.md) makes the non-bindless path what runs on WebGL2 and on
GL, so it is not a legacy concession and cannot be allowed to rot.

**The permutation question.** A set is written per variant today because a permutation can fold a
texture out of the shader entirely, and a set written for the variant that has it does not fit the
layout of the variant that does not. A *record* has the same problem in a different shape — the
struct's layout is the variant's — so the record is per variant too, and the buffer is a
concatenation of runs rather than a flat array. Worth deciding explicitly rather than discovering:
one buffer per variant is simpler and costs a bind per variant, which is exactly the cost being
removed.

### 3. The indirect draw whose count comes from the device

Small, and buys nothing until 2 is done. `ICommandList.DrawIndexedIndirect` takes `drawCount` as a
host integer; a compacted run needs `vkCmdDrawIndexedIndirectCount`, `ExecuteIndirect` with a count
buffer, or `glMultiDrawElementsIndirectCount`. Behind a capability, with the zeroed-instance-count
form as the fallback — which is what `GpuDrawArguments` does today and will keep doing on WebGL2
forever.

### 4. Then compaction

`GpuDrawArguments` appends survivors instead of zeroing them. The atomic add has been available since
Raven got atomics; what was missing was 2 and 3.

## Where this stands

| Step | State |
|---|---|
| The RHI: `BindlessTable`, the capability, the Vulkan backend | ✅ built, device-verified |
| 1. Raven `Texture2D[]` | ✅ built, device-verified |
| 2. A material record rather than a per-material set | ⬜ — the one the three blocked items wait on |
| 3. An indirect draw whose count comes from the device | ⬜ |
| 4. Compaction | ⬜ |

## Two things deliberately not planned here

**A bindless buffer table.** Vertex and index buffers per object are the *other* half of the sentence
at the top, and the answer to them is a shared geometry buffer with per-object offsets rather than a
descriptor array — a different change, in the mesh pipeline, with its own reasons. Compaction needs
both; they are independent.

**Samplers in the table.** A material's sampler is one of a handful of presets that `SamplerCache`
already interns, so a small bounded array indexed by preset is the right shape and an unbounded one
would be a second capability for no gain. `BindlessTable` accepts `DescriptorKind.Sampler` because
the RHI has no reason to forbid it, not because the material path should use it.

## Where the capability is absent

`HasBindless` is false on GL, on GLES, on WebGL2, and on MoltenVK below Metal argument-buffer tier 2
— see [rhi-backend-mapping.md](rhi-backend-mapping.md). On those targets every item above is a
no-op and the engine runs exactly as it does now: a descriptor set per material, one indirect command
per object with the culled ones zeroed, four reflection probes bound per frame, and a material
feature that cannot sample. That is a real product decision rather than a temporary state, and it is
the reason none of the work above may be written as a replacement for the existing path.

Licensed under Apache-2.0.
