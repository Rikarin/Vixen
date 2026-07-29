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

### 2a. ~~A material's texture becomes a value~~ — built

The half of step 2 that needs nothing new on the shader side beyond step 1, and the one that closes
doc 06's *"materials are values, not resources"* outright.

A material feature could carry channels and could not carry a texture, because sampling one needs a
binding index only the compiled shader knows — and a feature is composed into a shader it has never
seen. With a table it needs no index of the shader's. The shader declares a `uint`, the texture goes
in the table, and the slot goes into the material's own uniform block beside the base colour, where
`EffectConstants` writes it out of the same offset table it writes every other constant from. A
material texture is now a *value*, in the only sense that was ever missing.

`MaterialRenderFeature.Textures` is the table and `TextureIndices` is the pairing — explicit, for the
reason `PermutationSources` gives about its own: a shader's parameter name and a material's texture
name belong to different things, and a convention that stripped `Index` and matched the rest would
guess silently. An unmatched pair leaves the index at zero, which is a valid slot holding somebody
else's texture.

⚠ The registration is per **material**, not per variant, and it is idempotent. A permutation can fold
a texture out of the block but cannot change which texture the material carries, so indexing per
variant would take two references to one view and release neither. And this runs in `Prepare`, every
frame: a table asked for the same view sixty times a second raises a count nothing lowers, and the
symptom is not a wrong picture but a table that fills up after a few minutes and refuses a texture.
A settled material costs one dictionary hit, no table write and no upload.

What this does **not** do is remove the per-material descriptor set — the block is still a uniform
buffer in set 2, so a draw still binds one. That is 2b.

### 2a′. ~~A shared binding, which Raven does not have~~ — built

⚠ **The blocker for putting 2a to use, found by trying.** Nothing in `Raven/Library` declares a table
and nothing outside the tests sets `TextureIndices`, so the next step was to give a real surface
feature a base-colour map. It does not fit, and the reason is worth writing down before anything is
built on the assumption that it does.

A composed feature's bindings are **contributed**, and every contribution is qualified by the path it
was reached through — which is right, and is what keeps three features that each declare a
`strength` from colliding. But it means a binding declared by two features is two bindings. Compiling
a chain of two features that each declare `[PerFrame] var textures: Texture2D[]` gives:

```
set0:0 Texture 'Composite.BaseColor.textures'
set0:1 Texture 'Composite.NormalMap.textures'
```

Two unbounded arrays, two descriptor-array bindings, two pools of `MaxBindlessDescriptors` — and
`CompositeSurface` chains up to eight features, most of which would want a map. The table is
supposed to be *the* table: one array bound once for the frame, which is the entire economy.

It compiles and it runs, so this is not a bug in what was built. It is a capability the language does
not have: **a binding that is one resource for the whole compilation rather than a contribution from
each feature that mentions it.**

**`[Shared]` is the answer, and it is the middle of the three that were open.** A binding says for
itself that it is one resource; declarations of it are recognised by the *declared* name, collapsed
into one `(set, binding)` pair by `BindingPlan`, and refused by `RVN3011` when two of them disagree
about kind or set. Deduplicating identical contributions automatically was the cheap alternative and
is the wrong default — two features that happened to name a texture `noise` would silently share one
descriptor, and neither author would have said anything to that effect. Letting a feature reach the
composing shader's bindings was the general one, and is a much larger change to how composition
works.

⚠ Collapsing the plan is only half of it, and the other half is what a first attempt leaves out. Each
feature's body was compiled against its *own* variable, so the second feature's sample refers to
something the emitter never declared — a SPIR-V diagnostic about a variable that is plainly there, or
a GLSL identifier the unit does not contain. `PlannedBinding.Aliases` carries the other declarations
and both backends point every one of them at the single declaration they emitted.

An unshared binding beside a shared one still gets one per feature and is still qualified, which is
the control worth keeping: three features with a `strength` each are three values, and sharing them
is a material where moving one slider moves three.

### 2c. ~~A shader-library feature that samples~~ — built

`TexturedMetalRoughnessSurface` is the first feature in `Raven/Library` that reads a texture, and the
first material feature in the engine that could. It needed all three of the pieces above and one
more: a feature cannot read the pass's streams, so a coordinate to sample at had to arrive through
the one thing every feature is handed. `uv` is a channel on `MaterialData` now, seeded by
`MaterialDefaults.Begin` — which is the argument at the top of that file applied to itself, *"a
feature that starts caring about a new channel does not change the contract every other feature is
written to"*.

`MaterialTextures` is the shared base: the table and its sampler, declared once, inherited by every
feature that samples. `TexturedMetalRoughnessFeature` is the authored record, and it carries a
**name** and no handle — because a material is serialised on machines with no device, which is every
machine that authors one. The host joins that name to the shader parameter the compiler predicted
(`BaseColorIndexParameter`), and `MaterialRenderFeature` writes the slot.

The index defaults to zero, which is a slot that *exists*: a material whose map never reached a table
— no bindless device, or a host that never set the pairing — samples the table's fallback view. A
defined thing to read and a visible mistake, where an unwritten descriptor is whatever the driver
left there.

### 2b. A material becomes a record rather than a set — the shader half is built

`[MaterialIndex]` on a per-draw field turns that shader's whole per-material block into one *record*
of a buffer: a `readonly buffer` of records at the same set and binding the block had, and every read
of a per-material value spelled `materials.records[index].value`. Set 2 stays set 2 — what changes is
that it holds every material at once and is bound for the frame rather than for the draw, so nothing
renumbers and the four-set convention says what it always said.

The packing changes with it. A record is std430 because it is an element of a storage buffer; a block
was std140 because it was a uniform block. Both backends lay it out the new way and the reflection
reports the offsets it was *emitted* at, as a `StorageBuffer` with a count of zero — because
reporting a uniform buffer for a shader that reads a `BufferBlock` builds a descriptor of the wrong
type, which no API checks and which reads as a frame lit by whatever those bytes meant.

Without the marker nothing changes at all, which is the control worth keeping: the bound-per-material
path is what runs on GL, on WebGL2 and on every device with no bindless, so it is not a legacy branch.

**What is left is the engine half**, and it is the larger one:

- `MaterialRenderFeature` writes records into one buffer instead of a descriptor set per variant,
  and hands out the index each variant landed at.
- A per-object value carries that index into the per-draw data, the way
  `ForwardLightingRenderFeature` already carries a probe index and weight.
- `MeshRenderFeature` stops binding a per-material set.
- `ForwardPlus` declares the `[MaterialIndex]`, which is one line once the three above exist.

**The permutation question, now answerable.** A record's layout is the *variant's* — a permutation
can fold a value out of the block — so one buffer per variant rather than one for every material,
and the index is an index into that variant's buffer. Which costs a bind per variant, and a variant
is already a pipeline change.

### 2b (original sketch)

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
- ~~**A material feature may sample.**~~ Built in 2a — its texture is a name the material carries and
  an index the table hands out, and nothing in the shader has to know a binding number.

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
| 2a. A material's texture as a value in its block | ✅ built — closes "materials are values, not resources" |
| 2a′. A binding shared across composed features | ✅ built — `[Shared]`, collapsed by `BindingPlan`, aliased in both backends |
| 2c. A shader-library feature that samples through the table | ✅ built — `TexturedMetalRoughnessSurface`, and `uv` on `MaterialData` |
| 2b. The block as a record — shader half | ✅ built — `[MaterialIndex]`, both backends, reflection |
| 2b. The block as a record — engine half | ⬜ — the one compacted draws and per-object probes wait on |
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
