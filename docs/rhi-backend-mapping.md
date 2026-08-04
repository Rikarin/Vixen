# RHI backend mapping

Every concept in `Vixen.Graphics` against every backend it has to reach.

This is the fourth of the five measures in
[ADR-001](plan/01-technology-decisions.md#adr-001--vulkan-is-the-reference-backend-other-apis-are-conformance-targets),
and it exists for one reason: **D3D12 is postponed past 1.0 and the RHI has to accommodate it without
breaking changes when it lands**. A table is cheap, it is reviewed in a diff, and it makes design
drift visible before it is expensive. Review it whenever the RHI's surface changes — that is the
whole obligation.

The fifth measure is that OpenGL takes over the abstraction-validator role D3D12 was going to have,
because GL is *further* from Vulkan than D3D12 is. Most of what is written here was learned building
[`Vixen.Graphics.OpenGL`](../Platform/Vixen.Graphics.OpenGL/README.md), and the column that keeps
being interesting is the GL one.

⚠ **The WebGPU column was written against the specification and not against the backend**, which
landed alongside this table rather than before it. Where the two disagree the backend is right and
this is stale; correcting a cell is the cheapest thing in this document and is exactly what "reviewed
whenever the RHI surface changes" is asking for.

## Columns

| Column | What it means |
|---|---|
| **Vulkan** | The reference. `Vixen.Graphics.Vulkan`, targeting 1.3 with a 1.1 fallback |
| **D3D12** | Not built. Agility SDK, **Enhanced Barriers**, SM6.6 |
| **GL** | `Vixen.Graphics.OpenGL` at its GL 4.5 core profile |
| **GLES/WebGL2** | The same backend at its GLES 3.0 / 3.2 and WebGL2 profiles, where they differ |
| **WebGPU** | `Vixen.Graphics.WebGPU` — `Silk.NET.WebGPU` natively, `JSImport` in a browser |
| **Metal** | Reached through MoltenVK, so "what MoltenVK can express" rather than what Metal has |

Legend: **=** native, near-isomorphic · **≈** expressible with a translation · **⚠** emulated at a
cost · **✗** absent, and the RHI must gate on a capability.

---

## 1. Devices, queues and submission

| RHI | Vulkan | D3D12 | GL | GLES/WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|
| `IGraphicsAdapter` | = `VkPhysicalDevice` | = `IDXGIAdapter` | ⚠ `GL_RENDERER` string only | ⚠ same | = `GPUAdapter` | = via MoltenVK |
| `AdapterKind` | = `VkPhysicalDeviceType` | = `DXGI_ADAPTER_FLAG` | ✗ always `Unknown` | ✗ | = `GPUAdapterType` | ≈ |
| `IGraphicsDevice` | = `VkDevice` | = `ID3D12Device` | ≈ the context | ≈ | = `GPUDevice` | = |
| `ICommandSubmitter` ×3 | = three `VkQueue`s | = three command queues | ⚠ **one**, named three times | ⚠ same | ≈ one `GPUQueue` | = |
| `HasAsyncCompute` | true where a family exists | true | **false** | **false** | false | true |
| `ICommandList` | = `VkCommandBuffer` | = `ID3D12GraphicsCommandList` | ⚠ replayed managed buffer | ⚠ same | = `GPUCommandEncoder` | = |
| Recording on worker threads | = | = | ⚠ **records without a context, replays on the GL thread** | ⚠ same | = | = |
| `WaitIdle` | = `vkDeviceWaitIdle` | ≈ fence wait | ≈ `glFinish` (heavier) | ≈ | ≈ `onSubmittedWorkDone` | = |

**The finding.** GL has exactly one command stream and no way to record into it off-thread. The RHI's
"recording is safe on any thread" contract survives because `GlCommandList` writes into managed memory
and `GlDevice.Replay` makes the calls at submit — which costs a struct write per command and keeps
`Vixen.Rendering` free of any GL-shaped branch. That is the shape of every emulation in this document:
the abstraction is kept, the cost is paid in one named place, and the capability record says so.

## 2. Resources

| RHI | Vulkan | D3D12 | GL | GLES/WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|
| `BufferHandle` | = `VkBuffer` + allocation | = `ID3D12Resource` | = a buffer name | = | = `GPUBuffer` | = |
| `BufferUsage` flags | = `VkBufferUsageFlags` | ≈ implied by state | ⚠ picks a *home target*; buffers are typeless-but-not-really | ⚠ same | = `GPUBufferUsage` | = |
| `MemoryAccess` | = heap choice | = heap type | ⚠ a usage *hint* the driver may ignore | ⚠ same | = `mappedAtCreation` + usage | = storage mode |
| `TextureHandle` | = `VkImage` | = `ID3D12Resource` | = a texture name | = | = `GPUTexture` | = |
| `TextureViewHandle` | = `VkImageView` | = a descriptor | ⚠ **a record, not an object** | ⚠ same | = `GPUTextureView` | = `MTLTexture` view |
| …reinterpreting the format | = free | = free | ≈ `glTextureView` (4.3) | ✗ | = | = |
| `SamplerHandle` | = `VkSampler` | = `D3D12_SAMPLER_DESC` | = a sampler object | = (ES 3.0+) | = `GPUSampler` | = |
| `AddressMode.ClampToBorder` | = | = | = | ✗ extension only | ✗ | ≈ |
| Anisotropy | = | = | = | ✗ extension only | ✗ | = |
| Deferred destruction | ⚠ backend defers by frame | ⚠ same | **= free**, the driver refcounts | = | = | = |

**The finding.** `TextureViewHandle` is the one place the RHI's Vulkan shape costs something real.
Attaching and sampling a subresource — which is what views are overwhelmingly for — is free
everywhere. *Reinterpreting a format* is free on Vulkan, D3D12, WebGPU and Metal, an extension on
desktop GL, and impossible on GLES. `Vixen.Graphics.OpenGL` refuses it on every profile rather than
on one, because offering it only on desktop means content that works there and fails on Android.
Nothing in the engine uses it today; if something wants to, that is a decision to take deliberately.

## 3. Pipelines and shaders

| RHI | Vulkan | D3D12 | GL | GLES/WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|
| `PipelineHandle` | = `VkPipeline` | = `ID3D12PipelineState` | ⚠ **program + state block** | ⚠ same | = `GPURenderPipeline` | = |
| `GraphicsPipelineDescription` | = one `VkGraphicsPipelineCreateInfo` | = one PSO desc | ⚠ split: shaders → program, everything else → loose state | ⚠ same | = | = |
| `PipelineLayoutHandle` | = `VkPipelineLayout` | = root signature | ⚠ a binding *plan*, no object | ⚠ same | = `GPUPipelineLayout` | ≈ argument encoders |
| `ShaderHandle` | = `VkShaderModule` (SPIR-V) | = DXIL blob | ⚠ **source, compiled at pipeline creation** | ⚠ same | = WGSL module | = MSL via MoltenVK |
| `ShaderFormat` | `Spirv` | `Dxil` (reserved) | `GlslSource` | `EsslSource` | `Wgsl` | `Spirv`→MSL |
| `PrimitiveTopology` | = | = | = | = | = (no fans) | = |
| `RasterizerState.Fill` wireframe | = | = | = | ✗ no `glPolygonMode` | ✗ | ✗ |
| `RasterizerState.DepthClamp` | = feature bit | = | = | ✗ | ✗ | ≈ clip mode |
| `DepthStencilState` | = | = | = loose state | = | = | = |
| `BlendState` per target | = feature bit | = | = `glBlendFunci` | ✗ below ES 3.2 | = | = |
| `PushConstants` | = push constants | = root constants | ⚠ **a `vec4[]` uniform** | ⚠ same | ⚠ a small uniform buffer | ≈ `setBytes` |

**The finding, and it is the reassuring one.** ADR-001 predicted that "PSOs become program+state
tuples" on GL and that is exactly what happened — `GlProgramCache` and `GlStateCache`, one file each.
What the ADR did not predict is the corollary: because the shaders and the state split apart, a dozen
pipelines that differ only in blend mode share one program, which is the case a material system
produces by the hundred. The cache keys on shaders *and layout* rather than shaders alone, because the
binding indices are baked into the translated source.

Push constants are the one concept with no native equivalent anywhere except Vulkan and D3D12. WebGPU
does not have them either. Keeping the RHI's floor at 128 bytes is what makes the `vec4[]` emulation
cheap enough not to matter.

## 4. Descriptors and binding

| RHI | Vulkan | D3D12 | GL | GLES/WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|
| `DescriptorSetLayoutHandle` | = `VkDescriptorSetLayout` | = a root table | ⚠ input to the binding plan | ⚠ same | = `GPUBindGroupLayout` | ≈ |
| `DescriptorSetHandle` | = `VkDescriptorSet` | = a heap range | ⚠ **a CPU-side list of binds** | ⚠ same | = `GPUBindGroup` | = argument buffer |
| `DescriptorSetSlot` ×4 | = set 0–3 | = root parameter 0–3 | ⚠ flattened per resource class | ⚠ same | = group 0–3 | = |
| `DynamicUniformBuffer` offsets | = | ≈ root CBV per draw | = `glBindBufferRange` | = | = `dynamicOffsets` | = |
| `Sampler` as its own descriptor | = | = | ✗ **a sampler binds to a texture unit** | ✗ | = | = |
| `SampledTexture` | = | = | = a texture unit | = | = | = |
| `DescriptorSampleType` | ⚠ inferred from the shader | ⚠ same | ⚠ same | ⚠ same | = **required** in the layout | ⚠ inferred |
| `StorageTexture` | = | = | ≈ `glBindImageTexture` | ✗ below ES 3.1 | = | = |
| `StorageBuffer` | = | = | = SSBO | ✗ below ES 3.1 | = | = |
| `HasBindless` | = descriptor indexing | = SM6.6 dynamic resources | ✗ vendor extension only | ✗ | ✗ | ⚠ argument-buffer tier |
| Unbounded binding (`Count == 0`) | = partially-bound + update-after-bind, sized from the device | = a heap range | ✗ | ✗ | ✗ | as the tier allows |
| Updating a set | cheap | cheap | **free** (no GPU object) | free | cheap | cheap |
| Binding a set | cheap | cheap | ⚠ **N calls** | ⚠ same | cheap | cheap |

**The finding, and this is the one worth reading twice.** GL's flat binding namespaces are handled by
walking the layout once, in slot order, and giving every binding the next index of its class —
`GlBindingPlan`. Slot order is what makes it stable: the RHI's four sets are ordered by how often they
change, so two pipelines sharing a per-frame set agree about where it lives. That is not a lucky
accident of the convention; it is the convention paying for itself on a backend it was not designed
for.

**The one place the RHI's model has no GL answer at all** is a standalone `Sampler` descriptor.
Vulkan, D3D12 and WebGPU all let a sampler be bound independently of the textures that read through
it; `glBindSampler` takes a *texture unit*, so there is no way to say "this sampler, for whichever
textures the shader pairs it with". The backend states a rule rather than guessing: a texture write
may carry its own sampler and that wins; otherwise the set's single standalone sampler applies. Two
standalone samplers in one set is refused, because resolving it arbitrarily would produce a picture
filtered by whichever one happened to win.

**The cost inversion is worth planning around.** Updating a descriptor set is free on GL and binding
one is not — the opposite of Vulkan. Code that allocates a set per draw is slow on both, for different
reasons, which is a reassuring kind of agreement: the dynamic-offset path the RHI provides is the
right answer everywhere.

## 5. Render passes and attachments

| RHI | Vulkan | D3D12 | GL | GLES/WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|
| `BeginRenderPass` | = `vkCmdBeginRendering` (1.3) | = `OMSetRenderTargets` | ⚠ bind an FBO | ⚠ same | = `beginRenderPass` | = |
| `HasDynamicRendering` | = 1.3, else `VkRenderPass` | n/a — always dynamic | reported **true** (no pass object exists) | true | true | true |
| `LoadAction.Clear` | = | ≈ `ClearRenderTargetView` | = `glClearBuffer*` | = | = | = |
| `LoadAction.DontCare` | = | = `DISCARD` | = `glInvalidateFramebuffer` | = | = | = |
| `StoreAction.DontCare` | = | = | = `glInvalidateFramebuffer` | = | = | = |
| `StoreAction.Resolve` | = resolve attachment | = `ResolveSubresource` | ≈ `glBlitFramebuffer` | ≈ | = | = |
| Multiple colour attachments | = | = | = `glDrawBuffers` | = | = | = |
| `IsReadOnly` depth | = layout | = state | ≈ depth mask off | ≈ | = | = |

**Three things GL forces to be explicit that the others do not.**

1. **`glDrawBuffers` must be said.** GL's default for a user framebuffer is attachment zero only, so a
   pass with two colour targets writes one and discards the other — no error, and it looks exactly
   like a shader that forgot its second output.
2. **A clear obeys the write masks and the scissor.** It goes through the same fixed-function path a
   draw does, so a pass that clears while the previous pipeline's depth mask is off clears no depth at
   all. `GlStateCache.PrepareClear` opens them and forgets the pipeline's, so the next bind re-sends.
3. **Framebuffer objects must be cached, not rebuilt.** Attaching a texture makes the driver
   re-validate the whole set. A renderer has the same twelve attachment sets every frame; keyed on
   *views* rather than textures, so two passes into two mips of one chain stay distinct.

**And one warning MoltenVK emits that is not yours.** Creating any graphics pipeline whose colour
target is a format Metal cannot blend — every integer format, so the visibility buffer's `R32Uint` —
logs *"Blending is enabled for attachment with format VK_FORMAT_R32_UINT, which does not support
it"*, **whether or not blending is enabled**. MoltenVK's guard (`MVKPipeline.mm`,
`addFragmentOutputToPipeline`, present at least through v1.4.2 and current `main`) skips the blend
state for unblendable formats and warns on the way past without ever reading `blendEnable`; the
engine's create-info provably carries `BlendEnable = false`, and a bisect ruled out the
`BlendConstants` dynamic state and the attachment clear. It is not filtered out of
`VulkanDiagnostics`, deliberately: the same message with blending genuinely enabled would be a real
bug, and a string filter would eat both. If the message names a format you *meant* to blend, believe
it; if it names an integer target, it is this.

## 6. Barriers and synchronisation

This is the row ADR-001 calls the highest-risk place for Vulkan-only drift, and the reason the RHI's
`ResourceState` is specified against `synchronization2` rather than the older stage-pair model.

| RHI | Vulkan | D3D12 | GL | GLES/WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|
| `BarrierGroup` | = `vkCmdPipelineBarrier2` | = `Barrier()` (Enhanced) | ⚠ **usually nothing** | ⚠ same | ✗ implicit | ≈ MoltenVK translates |
| `ResourceState` flags | = stage + access + layout | = `BARRIER_SYNC`/`ACCESS`/`LAYOUT` | ⚠ see below | ⚠ same | ✗ tracked by the runtime | ≈ |
| `ColourTarget` → `ShaderRead` | = a barrier | = a barrier | **nothing** | nothing | implicit | = |
| `ShaderWrite` → anything | = a barrier | = a barrier | = `glMemoryBarrier` | = (ES 3.1+) | implicit | = |
| Timeline semaphores | = | = fences are counters | ✗ | ✗ | ✗ | = |
| Queue ownership transfer | = | ≈ | n/a — one queue | n/a | n/a | = |

**Why `ResourceState` is a flags enum, vindicated.** GL's execution model orders every command against
every command before it, for every access *except* the ones it calls incoherent — shader storage
buffers and storage images. So the correct GL translation of a barrier is: **nothing, unless
`ShaderWrite` is on one side of it**, and then a `glMemoryBarrier` whose bits come from the *after*
state. A backend that emitted something for every barrier would insert a full pipeline flush per pass;
one that emitted nothing for all of them would produce a race that appears as intermittently stale
data on one vendor's driver.

That the RHI's barrier model carries exactly enough information to tell those two cases apart, on an
API with no barriers at all, is the strongest evidence in this document that it is not a Vulkan
wrapper. **Do not** collapse `ResourceState` into a single-state enum: the flags are what make the
distinction possible, and they are also what maps to D3D12's separate `SYNC`, `ACCESS` and `LAYOUT`
triple.

## 7. Coordinate systems — the quiet one

**The engine's convention is neither API's default**, which is the thing to hold on to before
reading the row below. `Core/Vixen.Core.Mathematics/Conventions.md` fixes right-handed, **+Y up**,
row-vector, with reversed depth in `[0, 1]`. So *every* backend converts something.

| Convention | Engine | Vulkan | D3D12 | GL | GLES/WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|---|
| Clip-space `y` | **up** | down | down | up | up | down | down |
| NDC depth | `[0, 1]` | `[0, 1]` | `[0, 1]` | **`[-1, 1]`** | `[-1, 1]` | `[0, 1]` | `[0, 1]` |
| Viewport rect origin | top-left | top-left | top-left | **bottom-left** | bottom-left | top-left | top-left |
| Texture row 0 | top | top | top | top | top | top | top |
| Front face at CCW | as declared | as declared | as declared | **inverted** | inverted | as declared | as declared |

How each backend gets there:

- **Vulkan** flips `y` with a negative-height viewport — core since 1.1, and cheaper than a fixup in
  every vertex shader. Depth needs nothing.
- **GL 4.5** uses `glClipControl(GL_UPPER_LEFT, GL_ZERO_TO_ONE)`, which does both at once: the depth
  range becomes `[0, 1]` and the clip-to-window `y` direction inverts, so clip `y = +1` reaches the
  *lowest* framebuffer row — which is texel row zero, which is what everything else calls the top.
- **GLES and WebGL2** have no clip control, so the vertex shader does it: negate `y`, and remap `z`
  from `[0, 1]` to `[-1, 1]`. The remap is against `w`, in clip space, before the divide — doing it
  after is the usual version of this mistake and produces depth that is correct only where `w` is
  one, which is every orthographic projection and no perspective one, so it looks like it works.
- **Winding** inverts on GL, on *both* paths, because both change the clip-to-window `y` direction
  and that reverses signed area. `FrontFace.CounterClockwise` therefore reaches the rasteriser as
  `GL_CW`. It is one change with the axis and is wrong separately.
- **Viewport and scissor rectangles** are converted from top-left to bottom-left against the target's
  height on GL, on every profile including the one with clip control: clip control changes how clip
  space maps into the rectangle, not where the rectangle is measured from.

**Texture rows need nothing, and that is the row worth pausing on.** It looks as though GL's
lower-left window origin must store everything upside down, and the first version of
`Vixen.Graphics.OpenGL` flipped every row of every upload and readback to correct for it — one
transfer call per row. It was correcting for a problem the engine's +Y-up convention had already
solved: Vulkan flips down-to-up and GL flips up-to-down, from opposite defaults, and both land clip
`y = +1` at row zero. What made it visible was the golden suite: its triangle fixture puts an apex at
negative `y` and the committed reference has it at the *bottom*, which is only true if the engine is
+Y up.

**None of this is in the RHI's surface**, which is the point. The engine's matrices, its reversed-Z
convention and its UV origin are the same on every backend.

## 8. Capability gates

Every one of these has a documented fallback. A backend that cannot do something reports it; nothing
in the engine may assume.

| Capability | Vulkan | D3D12 | GL 4.5 | GLES 3.2 | GLES 3.0 / WebGL2 | WebGPU | Metal |
|---|---|---|---|---|---|---|---|
| `HasCompute` | ✓ | ✓ | ✓ | ✓ | ✗ | ✓ | ✓ |
| `HasGeometryShaders` | ✓ | ✓ | ✓ | ✓ | ✗ | ✗ | ✗ |
| `HasTessellation` | ✓ | ✓ | ✓ | ✓ | ✗ | ✗ | ✓ |
| `HasMeshShaders` | ext | ✓ | ✗ | ✗ | ✗ | ✗ | ✓ |
| `HasBindless` | ✓ four opt-in features | ✓ | ✗ | ✗ | ✗ | ✗ | ⚠ tier-dependent |
| `MaxBindlessDescriptors` | lesser of the two update-after-bind ceilings | SM6.6 heap size | 0 | 0 | 0 | 0 | as MoltenVK reports |
| `HasMultiDrawIndirect` | ✓ | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ |
| `HasDrawIndirectCount` | ✓ `VK_KHR_draw_indirect_count` | ✓ count buffer on `ExecuteIndirect` | ✗ (4.6) | ✗ | ✗ | ✗ | ✗ |
| `HasInt64Atomics` | ext bit `shaderBufferInt64Atomics` | ✓ SM6.6 | ✗ (NV only) | ✗ | ✗ | ✗ | ⚠ Apple7+, and **MoltenVK reports it false** |
| `HasTimelineSemaphores` | ✓ | ✓ | ✗ | ✗ | ✗ | ✗ | ✓ |
| `HasAsyncCompute` | ✓ | ✓ | ✗ | ✗ | ✗ | ✗ | ✓ |
| `HasSparseResources` | ✓ | ✓ | ✗ | ✗ | ✗ | ✗ | ✓ |
| `HasDepthClamp` | ✓ | ✓ | ✓ | ✗ | ✗ | ✗ | ≈ |
| `HasWireframe` | ✓ | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ |
| `HasIndependentBlend` | ✓ | ✓ | ✓ | ✓ | ✗ | ✓ | ✓ |
| `HasPipelineStatistics` | ✓ | ✓ | ✗ | ✗ | ✗ | ✗ | ✗ (MoltenVK) |
| Base instance in a draw | ✓ | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ |

The GLES 3.0 / WebGL2 column is the one that decides the engine's floor, and the cascade from
`HasCompute` being false there is the largest single consequence in the whole design: clustered light
binning, GPU particle simulation, GTAO, compute post-processing and GPU culling all need a
fullscreen-fragment or CPU path. [`docs/plan/06`](plan/06-rendering-pipeline.md) requires every post
effect to declare a non-compute variant for exactly this reason.

---

## What this table has changed so far

Kept honest by writing down what it actually caused, rather than only what it describes.

- **`ResourceState` stays a flags enum.** Collapsing it to one state per resource was considered while
  the Vulkan backend was the only one; the GL backend needs the `ShaderWrite` bit specifically to
  decide between "no call" and "`glMemoryBarrier`", and D3D12's Enhanced Barriers want the same
  separation for its `SYNC`/`ACCESS`/`LAYOUT` triple.
- **The four-set convention earned its keep on a backend with no sets.** Ordering sets by change
  frequency is what makes GL's flat binding indices stable across pipelines. If the sets were numbered
  arbitrarily this would not work at all.
- **`DescriptorWrite.Kind` is advisory; the layout is authoritative.** `DescriptorWrite.Uniform`
  produces the non-dynamic kind and there is no helper for the dynamic one, so a backend that trusted
  the write would drop the dynamic offset of every caller who used the obvious helper — a per-draw
  transform on the wrong object, which is a picture and not an error. Vulkan catches this only because
  its validation layers check the write's type against the layout's. Any future backend should read
  the layout.
- **One backend needed a field the other five infer, and it belongs in the RHI anyway.**
  `DescriptorBinding.SampleType` exists because a WebGPU bind group layout declares what a sampled
  texture holds and whether a sampler compares, before there is a resource to ask. It could have been
  a WebGPU-side guess; it is not, because the guess is wrong for exactly one case — a shadow map — and
  a per-backend guess is a per-backend picture. Vulkan checks it when it is stated and ignores it when
  it is not, which costs nothing and moves the failure to the machine the renderer is written on.
- **A standalone `Sampler` descriptor has no universal meaning.** It is the one RHI concept with no GL
  answer. It stays in the surface because Vulkan, D3D12, WebGPU and Metal all have it and removing it
  would cost them something real — but anything in the engine that uses one should prefer carrying the
  sampler on the texture write, which every backend can express.
- **`TextureViewHandle` format reinterpretation is unused, and should stay that way** until something
  needs it enough to accept that GLES cannot do it.
- **The engine's +Y-up convention is load-bearing, not cosmetic.** It is what makes the Vulkan and GL
  texture layouts agree without either of them flipping rows at a copy. Anyone tempted to "simplify"
  it to Vulkan's native +Y down should know that the cost lands on the GL backend as a per-row
  transfer, and would land on WebGPU and Metal the same way.
