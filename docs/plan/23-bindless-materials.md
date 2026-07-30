# Bindless material binding

The plan [W0-17](../overview.md#32-wave-0--startable-today-fully-parallel) names, and what has been
built of it.

Three things in the engine are marked blocked on this and on nothing else: **compacted draws**,
**per-object reflection probe selection**, and **a material feature that samples a texture** —
[doc 06](06-rendering-pipeline.md) § Materials calls the last one "materials are values, not
resources". They are blocked on the same sentence, which is worth stating precisely before any of it
is designed:

> `MeshRenderFeature` binds a vertex buffer, an index buffer and a material set per object.

A draw that binds a descriptor set is a draw that cannot be merged with a draw that binds a different
one. Everything else follows.

**None of those three clauses is true any more.** A material is a record of a buffer bound once per
effect (2b), a mesh's geometry is a range of a buffer shared with every other mesh of its layout (5),
and a run of objects that bind alike is one command whose draw count the host never learns (3 and 4).
The push constant that held the world matrix is gone with them (6) — it was not a binding, and it
stopped a merge anyway, because data in the command buffer is per command by construction.

What remains is the per-object *light block*, and only on the path that has one: with a uniform light
list each object binds its block at its own dynamic offset, and a dynamic offset travels in the bind.
With clustering on nothing is bound per object and a run does merge.

And a compositor document can now ask for all of it — see the last four sections.

⚠ **One step short of usable from a document alone.** Nothing outside the tests creates the
`BindlessTable` itself, so a material feature that samples still needs a host to wire one by hand —
§ *The one thing left*.

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

✅ **And the engine half writes them.** `MaterialRenderFeature.UseRecords` turns the per-variant
descriptor set into a record: `MaterialRecords` is one buffer per effect, each material a record in
it, and `RecordOf` gives back the group whose buffer holds an object's material and the record
within it. A shader whose effect declares no record keeps its set whatever the setting says, so a
frame mixing a pass that asked for records with one that did not is the same renderer.

⚠ **Per effect, which corrects the sketch below.** It said one buffer per *variant*. A variant is a
`(material, flags, shader)` triple, so that is one buffer per material with a single record in it —
the opposite of the point. What several materials genuinely share is their **effect**, and the sort
group is already the engine's name for "resolved to the same effect". Keyed by that, every record in
one buffer has the same layout by construction rather than by a check.

✅ **And the marker can be conditional**, which is what makes one pass able to be both.
`[MaterialIndex("UseRecords")]` applies only in the variants where that permutation is true, so a
device with bindless compiles the records form and GL, WebGL2 and MoltenVK below argument-buffer tier
2 compile the set form — out of one shader. Without it the shipped forward pass would have to be
written twice, and it is four hundred lines.

⚠ Gating on the marked field being *used* was the tempting alternative and does not work. A binding
is a declared field, so it survives its last reader folding away: a shader written that way reports a
record in **both** variants, which a probe established before the conditional marker existed. The
permutation is also the right conditional rather than merely the available one — the two forms are
different compilations with different descriptor layouts, which is what a permutation already means.

✅ **And the shipped pass declares it.** `ForwardPlus` carries
`[Permutation] val UseMaterialRecords: bool = false` and
`[PerDraw] [MaterialIndex("UseMaterialRecords")] var materialIndex: uint = 0u`, and
`ForwardLightingRenderFeature` writes the record index into the per-draw header at
`MaterialIndexOffset`.

⚠ It cost **nothing in layout**, and that was predicted rather than lucky: the shader's own comment
said three scalars leave four bytes of padding before `lights`, because std140 starts an array of
structures on a sixteen-byte boundary whatever precedes it. `materialIndex` is the fourth scalar and
fills exactly that. The regenerated reflection puts it at offset 12, `lights` still starts at 16, and
`HeaderSize` does not move — so nothing that writes that block had to change, and the golden images
did not move either, because the default variant is the one they render.

⚠ The index is written by the *lighting* feature although it is the material feature's number. The
per-draw block is one allocation with the light list in it, so it has one owner; two features writing
into one block at agreed offsets is worse than one feature asking the other for a value. It finds its
sibling through `Parent.SubFeatures`, the way `MaterialRenderFeature` finds its permutation
contributors, so the order a host calls `Add` in does not decide whether records reach the shader.

✅ **And the bind is one per group.** `MaterialRenderFeature` points every recorded variant's set at
its group's record buffer after the upload — every variant of a group asks the allocator for the same
layout and the same single write, the allocator is content-addressed, so they get one handle back and
`MeshRenderFeature`'s existing "did this differ from the last one" check turns a run of objects into
one bind. **Two materials of one effect are one `BindDescriptorSet` where they used to be two**, and
there is a test either side of that: the record path counts one, the bound-per-material path counts
two, so "one" is a measurement rather than a fixture with nowhere to bind.

`MeshRenderFeature` did not change at all. The seam was already in the right place — it binds
whatever `DescriptorsOf` hands back — which is what made the last piece of 2b a change to one method
rather than to the draw loop.

⚠ After the upload, not before: a record buffer has no handle until it has been uploaded once and
replaces it when it grows, so a set written earlier would point at a buffer that no longer exists —
the one failure the RHI's deferred destroy cannot save a caller from.

**2b is complete**, and so is the rest of the plan — see steps 2d, 3, 4 and 5 below.

The sketch this replaces:

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
[ADR-011](01-technology-decisions.md) makes the non-bindless path what runs on WebGL2 and on
GL, so it is not a legacy concession and cannot be allowed to rot.

**The permutation question.** A set is written per variant today because a permutation can fold a
texture out of the shader entirely, and a set written for the variant that has it does not fit the
layout of the variant that does not. A *record* has the same problem in a different shape — the
struct's layout is the variant's — so the record is per variant too, and the buffer is a
concatenation of runs rather than a flat array. Worth deciding explicitly rather than discovering:
one buffer per variant is simpler and costs a bind per variant, which is exactly the cost being
removed.

### 2d. ~~A set of its own, and a frame that actually takes the path~~ — built

⚠ **The blocker for using any of it, found the same way 2a′ was: by trying.** Nothing outside the
tests built a `BindlessTable`, set `TextureIndices`, turned `UseRecords` on, or asked for the
`UseMaterialRecords` variant. That was called "a wiring decision rather than a mechanism" above, and
it was not: two mechanisms were missing.

**A table cannot live in the frame's descriptor set.** Sets 0 to 3 are written each frame by
`DescriptorAllocator`, which is content-addressed — a set whose write list differs by a byte is a
different set object. A table's descriptors are written one at a time as textures enter it and there
may be thousands, so a table in set 0 would be written out again in full whenever a uniform block
moved within its upload ring. That is precisely the cost a table exists to remove.
`DescriptorSetSlot.Bindless` is set 4, Raven's `[Bindless]` marker puts a binding there, and
`HasBindless` gained a fifth requirement — `MaxDescriptorSets >= 5`, which Vulkan does *not*
guarantee and which a device answering on the indexing bits alone would fail at
`vkCreatePipelineLayout` rather than at a capability check. Only a shader that declares a table gets
the fifth layout; a variant compiled without it is the four-set layout it always was.

**And `EffectSetWriter` would have made a table into a missing binding.** It counts what a set wants
and refuses to bind one short of an entry, which is right. An unbounded array is not one of those
entries — the table owns it — so counting it made every set holding one permanently incomplete, and
the caller's answer to incomplete is to bind *nothing*: a shader that gained a table would have lost
the set it was already binding, and the frame would go dark rather than untextured.

`MaterialRenderFeature.EnableRecords(shaderKey)` is the decision, and it sets **both** halves. Either
alone draws: records nothing reads, or a subscript into a buffer the host is still filling descriptor
sets for. `Permutations` is a third layer applied after the material's values and after every
sub-feature's, because a material is authored on one machine and drawn on another and must not be
able to claim a capability the device does not have.

### 3. ~~The indirect draw whose count comes from the device~~ — built

`ICommandList.DrawIndexedIndirectCount` and `GraphicsDeviceFeatures.HasDrawIndirectCount`: Vulkan
through `VK_KHR_draw_indirect_count`, the Null backend recording it, GL and WebGPU refusing it with
the fallback named in the message. GL refuses rather than emulating — reading the count back and
issuing that many draws is a full pipeline stall mid-frame, which is the round trip this whole path
exists to avoid.

⚠ **Its own capability, and `HasMultiDrawIndirect` does not imply it.** They come apart on every API
that has both: Vulkan spells the count buffer as an extension promoted in 1.2, GL wants 4.6 where
multi-draw wants 4.3, and WebGPU and Metal have neither. MoltenVK reports multi-draw and not this, so
a host reading the wrong flag finds out on the first Mac it runs on.

⚠ **Asked as the extension at every Vulkan version.** The commands are core from 1.2 and gated there
behind `VkPhysicalDeviceVulkan12Features::drawIndirectCount`, a structure this backend does not
query; every driver that promoted the extension still advertises it. Asking for the extension makes
the capability, the enable and the loaded entry point one decision instead of three that must agree.

### 4. ~~Then compaction~~ — built

`DrawArguments.rvn` gains `[Permutation] val Compact`; survivors claim a slot with `atomicAdd` into
their batch's run and a culled object writes nothing at all. `GpuDrawArguments` lays the runs out —
a histogram and a prefix sum over the batch ids a source supplies — and `MeshRenderFeature` covers a
whole batch with one `DrawIndexedIndirectCount`.

Compaction costs an atomic and **no memory**: batches partition the objects, so their runs partition
a view's region and the buffer is exactly the size the padded form needed.

⚠ **Three conditions on a merged run, and each rules out a picture rather than an error.** Same
batch, or the command draws arguments belonging to geometry it is not bound for. Same effect and same
buffers, because one command binds one of each. And the run must be the *whole* batch — a batch is a
fact about objects and a run is a fact about one stage's node list, so a shadow cascade seeing half a
batch would otherwise draw the other half into the cascade. The third is the one that would have been
missed.

⚠ **The counts are cleared on the device before every dispatch.** An `atomicAdd` onto last frame's
count appends past the end of a batch's run and into the next batch's, which draws one batch's
geometry with another's arguments — and does so only in the frames where something became invisible.
Copied from a buffer of zeros rather than written from the host, because a host write into a buffer
an unfinished frame may still be reading is the hazard the upload ring exists for, and a source
written once and never again has none of it.

### 5. ~~The geometry half~~ — built

`GeometryBuffer` puts many meshes in one vertex buffer and one index buffer at their own offsets, and
`MeshRenderFeature` binds each only when it changed. Three meshes now cost one vertex bind, one index
bind and no per-material set — so the sentence at the top of this document has nothing left in it.

⚠ **One buffer per vertex layout, and it has to be.** A draw's `vertexOffset` is a vertex *count* the
GPU multiplies by the pipeline's stride, so two formats in one buffer would each be read at the
other's. The stride belongs to the buffer, which is the same reason `MeshDraw.VertexLayout` is
already part of `PipelineKey`.

⚠ **Fixed capacity: dropped rather than grown.** Growing means new handles, and the handles are the
problem — every `MeshDraw` already built holds the old ones. A caller needing more space makes a
second buffer, which costs one bind between the two runs and nothing within either.

## Where this stands

| Step | State |
|---|---|
| The RHI: `BindlessTable`, the capability, the Vulkan backend | ✅ built, device-verified |
| 1. Raven `Texture2D[]` | ✅ built, device-verified |
| 2a. A material's texture as a value in its block | ✅ built — closes "materials are values, not resources" |
| 2a′. A binding shared across composed features | ✅ built — `[Shared]`, collapsed by `BindingPlan`, aliased in both backends |
| 2c. A shader-library feature that samples through the table | ✅ built — `TexturedMetalRoughnessSurface`, and `uv` on `MaterialData` |
| 2b. The block as a record — shader half | ✅ built — `[MaterialIndex]`, both backends, reflection |
| 2b. The block as a record — engine half (records written) | ✅ built — `MaterialRecords`, one buffer per effect |
| 2b. A marker a permutation can switch off | ✅ built — `[MaterialIndex("Key")]`, so one pass is both |
| 2b. The shipped pass declares it, and the index reaches the block | ✅ built — and it cost no layout, filling padding that was already there |
| 2b. Binding the record buffer once per group | ✅ built — two materials of one effect are one bind |
| 2d. A set of its own, and a frame that takes the path | ✅ built — `DescriptorSetSlot.Bindless`, `EnableRecords` |
| 3. An indirect draw whose count comes from the device | ✅ built — `DrawIndexedIndirectCount`, behind its own capability |
| 4. Compaction | ✅ built — one command per batch, three conditions checked |
| 5. The geometry half | ✅ built — `GeometryBuffer`, one bind per run |
| 6. The transform out of the command buffer | ✅ built — `UseTransformRecords`, the index in `firstInstance` |
| 7. The material record out of the per-object block | ✅ built — a push constant, per run, at the offset the effect declares |
| 8. The probe scalars out of it as well | ✅ built — `UseObjectRecords`, and `Flat` in Raven to carry the index |
| 9. A document that asks for all of it | ✅ built — `gpuDriven:` on the asset root, `compact:` on the culling node |
| 10. **Something that creates the table** | ⬜ **not built** — see below, and it is the one thing left |

## 6. The transform, which was not a material and was still in the way

✅ **Built** — `TransformRenderFeature.EnableRecords`, `ForwardPlusKeys.UseTransformRecords`.

A push constant is not a binding, which is why nothing above touched it, and it stopped a merge all
the same: it is data travelling in the command buffer, so it is per command by construction. Three
objects that bind nothing between them still could not become one command while each had a matrix to
push, and a merged command has no point inside it at which the second object's could go.

The fix is the same one `[MaterialIndex]` was. Every object's matrix goes into one buffer at its own
slot, and the draw carries the slot in its own `firstInstance` — which the compaction shader already
copies and which the API adds into `SV_InstanceID` before the vertex stage runs. Not a new mechanism:
`InstancingRenderFeature` has always used that field for exactly this, so the two are now one thing
with a run of one.

Three details that are not obvious:

- **The buffer is bound whole and the shader is told where the frame starts.** The ring has a region
  per frame in flight, and a resource reaches a set through a handle a host named, with nowhere to
  put an offset. So `transformBase` is an index, added to the instance index — and it costs the
  block nothing, filling the four bytes std140 already left after `lightDirection`.
- **A record index is the object's own slot**, so there is nothing to allocate, nothing to rebuild
  when an object goes away, and no second copy of the map for the compaction shader to read.
- **There is a buffer even with the records off.** `transforms` is declared whichever way the
  permutation went, and a set short one entry is not bound at all — so a frame that pushed its
  matrices and left this empty would lose the whole of set 0 and go dark. One identity record.

The gate is `HasDrawIndirectCount`, which is not what reads the buffer — every device can read a
matrix out of one. It is what decides whether the read is worth it: no device-side draw count means
no compaction, no compaction means no merged command, and then a push constant is strictly cheaper.

## What is still between this and the shipped forward pass

⚠ **The transform was not the only per-node contributor.** With a uniform light list,
`ForwardLightingRenderFeature` binds each object's block at its own dynamic offset — and a dynamic
offset travels in the *bind*, not in the block, so there is nowhere inside a merged command to change
it. That path cannot merge, and no gate can talk it out of that.

With clustering on it binds nothing per object, because a fragment finds its own lights in the grid.
So a clustered `ForwardPlus` frame with transform records on does merge, and there is a test on each
side of that pair. The gate asks what a sub-feature is *doing* this frame rather than what type it is
— `IDrawSubFeature.IsRecording` — because asking the type gives the same answer to both of these and
that answer is wrong for one of them.

### The clustered path had a hole, and `materialIndex` was in it

✅ **Fixed** — the index is a push constant beside the world matrix.

The clustered path binds no per-draw set at all, so the whole set-3 block was undelivered in a
clustered frame — including `materialIndex`, which is the number this entire document is about.
Bindless materials and clustered lighting were quietly exclusive.

The fix turned on noticing that **it was never per-object data**. A variant is keyed
`(material, flags, shader)`, so one variant is one material; a batch keys on the variant; so every
object in a merged command has the same material and the same record. It is per *draw*. So it is
pushed, once per run, at the point the per-material set is bound on the path that has one — per run
and not per node, which is exactly what the merge gate permits. It also takes a cross-feature write
with it: the lighting feature used to write the material feature's number into its own block, which
its own comment apologised for.

The offset comes off the effect rather than a constant in the host. `EffectPushConstantData` carried
a range and no members, on the stated grounds that a caller reads the generated constants — but
nothing is generated for a push block, so the only offset a host had was one it assumed. That held
while the block was one matrix at zero, and would have stopped holding **silently**: a push at the
wrong offset inside a declared range is accepted by every layer there is.

### And `probeIndex` and `probeWeight` were in it too

✅ **Fixed** — `UseObjectRecords`, a record per object read through a flat varying.

These are genuinely per object — a probe is chosen by where the object *is* — so a push would be
wrong for them the way the block was wrong for `materialIndex`. They go in a buffer the lighting
feature owns, at the object's own slot, read in the fragment stage through an `objectIndex` varying.

Two things that had to exist first:

- **Raven had to emit `Flat`.** An integer varying has no interpolation it could take — the
  rasteriser weights by barycentric coordinates and that produces a fraction — so SPIR-V *requires*
  the decoration on a fragment input of integer type and GLSL requires the qualifier. Raven emitted
  neither. Both backends now ask one predicate, and the float varying is asserted **not** to be flat:
  decorating everything would satisfy the validator and quietly kill interpolation.
- **The record has to be addressable.** `SV_InstanceID` holds the object's slot only because the
  transform record path put it in `firstInstance`. So `UseObjectRecords` takes that as a parameter
  rather than checking for itself, and the compositor asks for it only where transforms turned on.
  Asked without them, every draw carries zero and every object reads record zero's probe.

⚠ The new varying takes a location, so `ForwardPlus`'s vertex attributes moved from 5–8 to 6–9. A
golden test had them written down and `vkCreateGraphicsPipelines` refused the pipeline outright —
which is how it was found, and the fixture now reads them off the effect as its own comment always
claimed it did.

## A document can ask for all of it

✅ **Built** — `GpuDrivenAsset`, and `compact:` on the culling node.

Everything above was reachable from a test and from **nothing a project authors**. `CompositorBuilder`
wired one thing out of the whole chain — the argument buffer — and never turned records on, never
asked for compaction. A mechanism nothing invokes is a mechanism that compiles.

```yaml
gpuDriven:
  shader: ForwardPlus
  materialRecords: true
  transformRecords: true
game: !Sequence
  children:
    - !GpuCulling
      readBack: false
      indirectDraws: true
      compact: true
```

**Every flag is a request and the device answers it**, which is what makes it safe in a document at
all: one authored frame runs on a machine with descriptor indexing and on one without, and the second
draws the same image through a descriptor set per material. `CompositorBuilder.GpuDriven` reports what
was actually turned on, so "the device said no" and "nobody asked" are not the same observation.

The frame-wide flags are on the asset root rather than on a node, because they are not a pass: they
decide where a material's values live and where an object's matrix lives, and the answer has to be the
same for every pass that draws.

## The one thing left: nothing creates the table

⬜ **Not built.** Every mechanism above is complete and exercised, and the shipped library has the
consumer — `TexturedMetalRoughnessSurface` inherits `MaterialTextures` and samples through
`SampleMap`. But **nothing outside the tests constructs a `BindlessTable` or fills
`MaterialRenderFeature.TextureIndices`**:

```bash
grep -rn "new BindlessTable" --include="*.cs" Core Editor Platform | grep -v Tests
```

returns nothing. So a project that asks for `materialRecords: true` gets records without a texture
table, and a material naming a base-colour map keeps `baseColorIndex = 0` because the registration is
skipped when `Textures` is null.

⚠ **And there is a sharper edge behind it, which the records flag does not cause.** A material
composed from `TexturedMetalRoughnessSurface` declares the table *whatever the permutation says* — a
binding is in the plan because it was declared, which is the rule this whole document keeps running
into. So its pipeline layout has five sets, while `MeshRenderFeature` binds set 4 only when
`materials.Textures is { Set.IsValid: true }`. With no table that is a five-set layout drawing with
four sets bound: a validation error on a real device, not a missing texture.

**Why it stopped here rather than being finished with the rest.** A table needs a capacity and a
*fallback texture view* — slot zero, what a material with no map samples. Both are project decisions
and the fallback is an actual asset, so inventing a 1×1 white texture inside `CompositorBuilder` would
have made the silent default one nobody chose. The two honest shapes are:

- the document names it — `bindlessTextures: { capacity: 4096, fallback: <asset> }` inside
  `gpuDriven:`, which keeps the rule that a file says *which* and a host binds it; or
- the builder creates the table with a generated 1×1 white view when `materialRecords` is on, and a
  host overrides it.

Whichever it is, the guard belongs with it and is worth having either way: **an effect that declares
set 4 with no table available should refuse to draw** rather than issue a draw whose layout it cannot
satisfy. Today that combination is only reachable by hand, which is the only reason it has not been
hit.

## Two things deliberately not planned here

**A bindless buffer table.** Vertex and index buffers per object are the *other* half of the sentence
at the top, and the answer to them is a shared geometry buffer with per-object offsets rather than a
descriptor array — a different change, in the mesh pipeline, with its own reasons. Compaction needs
both; they are independent.

✅ **Built, as `GeometryBuffer`** — see step 5 below. The answer stayed the one predicted here: an
offset, not a descriptor.

**Samplers in the table.** A material's sampler is one of a handful of presets that `SamplerCache`
already interns, so a small bounded array indexed by preset is the right shape and an unbounded one
would be a second capability for no gain. `BindlessTable` accepts `DescriptorKind.Sampler` because
the RHI has no reason to forbid it, not because the material path should use it.

## Where the capability is absent

`HasBindless` is false on GL, on GLES, on WebGL2, and on MoltenVK below Metal argument-buffer tier 2
— see [rhi-backend-mapping.md](../rhi-backend-mapping.md). On those targets every item above is a
no-op and the engine runs exactly as it does now: a descriptor set per material, one indirect command
per object with the culled ones zeroed, four reflection probes bound per frame, and a material
feature that cannot sample. That is a real product decision rather than a temporary state, and it is
the reason none of the work above may be written as a replacement for the existing path.

Licensed under Apache-2.0.
