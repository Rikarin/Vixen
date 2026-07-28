# 05 — Graphics RHI

Per ADR-001: one explicit, Vulkan-shaped abstraction; five backends; Vulkan is the reference.

## Package reality check

Verified against the live NuGet index. Silk.NET 2.23.0 ships: `Vulkan` (+ KHR/EXT/AMD/NV extension
packages), `Direct3D11`, `Direct3D12`, `DXGI`, `Direct3D.Compilers`, `OpenGL`, `OpenGL.Legacy`,
`OpenGLES`, `EGL`, `WebGPU`, `SPIRV`, `SPIRV.Cross.Native`, `Shaderc`, `SDL`, `GLFW`, `Input`,
`OpenAL`, `OpenXR`, `Assimp`, `Maths`, `Core`.

**There is no `Silk.NET.Metal`.** Apple support is MoltenVK under the Vulkan backend (ADR-011).

| Target | Backend | Notes |
|---|---|---|
| Windows | Vulkan (primary). **D3D12 postponed past 1.0** but designed for — ADR-001 | Vulkan is the only Windows path at 1.0. The D3D12 project/package slot exists with stub implementations so adding it later is additive, not breaking. PIX/GPU-crash-dump parity is the eventual motivation. |
| Linux | Vulkan | Only. GL exists as a last-resort fallback for ancient hardware |
| macOS | Vulkan → MoltenVK (**v1.4.2, Vulkan 1.4**) | Direct-linked for shipping; Vulkan Loader + validation layers bundled for dev builds — MoltenVK does not load layers itself. Dev instance creation needs `VK_KHR_portability_enumeration` + `ENUMERATE_PORTABILITY_BIT`, or the Loader reports no devices. |
| iOS | Vulkan → MoltenVK, static-linked | `CAMetalLayer` surface via `VK_EXT_metal_surface`; Simulator supported |
| Android | Vulkan 1.1+ (API 26+), GLES 3.2 fallback | Real device fragmentation makes the GLES fallback mandatory, not optional |
| Web | WebGL2 (via `Silk.NET.OpenGLES` over the browser context), WebGPU when available | See [10](10-platforms.md) — this is the highest-risk target |

## The RHI surface

`Vixen.Graphics` is deliberately *not* a thin Vulkan wrapper and *not* a GL-era façade. It is the
minimum abstraction that D3D12 and Vulkan both map to natively.

### Objects and handles

Everything is a generation-checked `Handle<T>` (see [03](03-core-foundation.md)) into a backend-owned
table. No `IDisposable` GPU wrappers, no finalisers, no GC pressure per resource.

```
GraphicsDevice            — logical device, queues, feature caps, allocator, resource tables
GraphicsAdapter           — enumerable physical device with capability record
CommandQueue              — Graphics | Compute | Transfer, submitted independently
CommandList               — recorded on any thread; one per thread per frame; pooled
SwapChain                 — surface + images + present mode + resize handling

BufferHandle              — usage flags: Vertex|Index|Uniform|Storage|Indirect|Staging
TextureHandle             — 1D/2D/3D/Cube/Array, mips, samples, usage flags
TextureViewHandle         — format/subresource reinterpretation
SamplerHandle
PipelineHandle            — graphics | compute | (mesh, later)
PipelineLayoutHandle
DescriptorSetLayoutHandle
DescriptorSetHandle
QueryPoolHandle           — timestamp, occlusion, pipeline statistics
FenceHandle / SemaphoreHandle
```

### Pipeline state

Created ahead of time from a full `GraphicsPipelineDescription` (shaders, vertex layout, raster
state, blend state, depth-stencil state, render-pass compatibility). This is Vulkan's and D3D12's
model. The GL backend hashes the description and maps it to a program + cached GL state block, applying
state lazily on bind with a shadow-state diff — the standard technique, and the reason the GL backend
is the biggest one.

Pipelines are **cached and pre-warmed**: a build step records every pipeline description the content
needs into a `pipelines.cache` artefact, and boot creates them on background threads before first
frame. This eliminates the first-encounter hitch that plagues Vulkan titles and is the single
highest-value ergonomic feature in a modern RHI.

### Descriptor model

Two tiers, both expressed in the RHI:

1. **Descriptor sets** (Vulkan sets / D3D12 root-signature tables). Fixed four-set convention:
   - set 0: per-frame (camera, time, lighting environment)
   - set 1: per-view (shadow matrices, view-dependent buffers)
   - set 2: per-material (textures, material constants)
   - set 3: per-draw (transforms via dynamic offset, instance data)

   This mirrors Stride's logical-group model and matches how Raven emits its bindings. Concretely:
   a Raven binding is marked `[PerFrame]`, `[PerView]`, `[PerMaterial]` or `[PerDraw]` and the set
   index follows from the marker; an unmarked field is per-material. Both of Raven's backends and
   its reflection take the pair from one `BindingPlan`, so the set and binding the RHI builds a
   layout from are the ones the module was decorated with ([07 § C](07-raven-shader-pipeline.md)).
2. **Bindless** (`VK_EXT_descriptor_indexing` / D3D12 SM6.6 dynamic resources) behind a capability
   flag, exposed as a global `TextureHandle → uint` bindless index table. GPU-driven culling and
   material batching use it where available; there is a non-bindless path for GL/WebGL and older
   Android.

✅ Sets whose contents are a *frame's* resources — anything a render-graph pass reads — come from
`DescriptorAllocator` rather than being created and destroyed. It recycles through a ring exactly
`FramesInFlight` deep, because a set written for frame *f* is still being read while the CPU records
*f+1*, and it shares one set between everything in a frame asking for the same writes. That is the
lifetime a frame graph needs and the four-set convention above does not describe: sets 0–3 are about
how *often* a binding changes, and this is about when the handle behind it comes into existence.

### Synchronisation

- **Explicit barriers** in the RHI (`CommandList.Barrier(in BarrierGroup)`), because implicit
  tracking is where every abstraction layer either becomes slow or becomes wrong.
- On top of it, an **optional automatic barrier tracker** (`RenderGraph`, below) that most engine code
  uses. Hand-written barriers remain available for the hot paths that need them.
- Frame pacing: N in-flight frames (default 2, configurable to 3), per-frame fence, per-frame
  descriptor pools and command allocators reset in bulk.

### Render graph

`Vixen.Graphics.RenderGraph` — a transient-resource, automatic-barrier pass graph:

- Passes declare reads/writes of virtual resources; the graph culls unreferenced passes, aliases
  transient memory (a 4 K GBuffer and a 4 K post-FX target that never coexist share memory), inserts
  barriers and layout transitions, and orders queue submissions.
- This is not optional garnish: with six backends, hand-maintaining barrier correctness across a
  Stride-scale pipeline (deferred, shadows, SSAO, SSR, TAA, bloom, DoF) is not achievable. The graph
  is how the pipeline stays correct on Vulkan while remaining expressible as no-ops on GL.
- Graph validation in debug: a pass that reads a resource nobody wrote is an error naming both passes;
  a resource written twice in a frame without a barrier is an error. Plus a Graphviz dump per frame
  for the frame debugger.

### Capability record

`GraphicsDeviceFeatures` is a flat `readonly record struct` of ~40 booleans/limits queried once:
compute, geometry/tessellation, mesh shaders, bindless, multi-draw-indirect, timeline semaphores,
async compute, sparse residency, `float64`, subgroup ops and size, max texture size, max anisotropy,
MSAA sample masks, format support table, texture-compression families available, UMA/discrete,
line width range, viewport count.

Feature use is **capability-gated with a documented fallback**, never a hard requirement, except a
hard floor:

**Minimum spec:** Vulkan 1.1 / D3D12 feature level 11_0 / GLES 3.0 / WebGL2. Below that, Vixen does
not run. Stated once, in the docs, and enforced at device selection with a readable error.

## Backend implementation order and shape

### `Vixen.Graphics.Null` — first

Written before Vulkan. Records the command stream into a structured, comparable log. This is what
makes RHI unit tests possible in CI without a GPU, and what makes "did my render feature emit the
right calls" a unit test rather than a screenshot diff. Every RHI unit test runs on Null.

**It is also a shipping backend, not only a test one.** A dedicated server
([17](17-app-heads-and-shipping.md)) runs on `Null` — no GPU, no window, no display server. That is a
pleasant consequence of the existing design rather than new work: this backend already exists, and
because every RHI unit test targets it, it is the most thoroughly exercised backend in the engine. The
one addition it needs is that resource creation must genuinely no-op rather than allocate, so a
long-running server does not accumulate phantom GPU objects.

### `Vixen.Graphics.Vulkan` — the reference

- `Silk.NET.Vulkan` + `.Extensions.KHR` (`swapchain`, `surface`, `dynamic_rendering`,
  `timeline_semaphore`, `synchronization2`) + `.EXT` (`debug_utils`, `descriptor_indexing`,
  `memory_budget`).
- **Dynamic rendering** (`VK_KHR_dynamic_rendering`, core in 1.3) as the primary path — no
  `VkFramebuffer`/`VkRenderPass` object management. A 1.1/1.2 fallback path using real render passes
  exists for older Android drivers, behind a capability flag, because a meaningful slice of Android
  devices are still on 1.1.
- Own memory allocator (buddy over large heaps) rather than binding VMA — one less native dependency
  across six RIDs, and the allocator is ~1 500 lines of testable managed code.
- `VK_EXT_debug_utils` object naming wired to the engine's resource names, so RenderDoc and the
  validation layers show `"ShadowMap.Cascade2"` instead of `"VkImage 0x7f...".`
- Validation layers auto-enabled in debug builds, with the layer's messages routed into `ILogger` at
  matching severity and *failing the test run* in CI.

### `Vixen.Graphics.Direct3D12` — **postponed past 1.0, reserved from Phase 1**

Per ADR-001. The project and package exist from Phase 1 with every RHI entry point implemented as a
`NotSupportedException` stub and the capability record reporting nothing, so package identity, RID
mapping, and the reference graph are settled early and the real implementation is purely additive.

When it is built: `Silk.NET.Direct3D12` + `DXGI` + `Direct3D.Compilers` (DXC for HLSL→DXIL); root
signature generated from the four-set convention; descriptor heaps as ring allocators; placed resources
over committed heaps; **Enhanced Barriers** (Agility SDK), which is why the RHI's barrier model is
specified against Vulkan `synchronization2` rather than legacy resource states.

### `Vixen.Graphics.OpenGL` — ✅ **built, and now the abstraction validator**

One project, three profiles: GL 4.5 core (desktop), GLES 3.0/3.2 (Android), WebGL2 (browser), selected
at construction. Shares the state-shadowing, pipeline-emulation, and barrier-elision logic; differs in
extension availability and shader dialect.

With D3D12 deferred, this backend carries the job D3D12 was going to do: proving the RHI is genuinely
API-neutral. It is a *harder* test than D3D12 would have been — GL has no PSOs, no descriptor sets, no
explicit barriers, and no multithreaded recording, so anything Vulkan-shaped that leaked into the
abstraction shows up here immediately. Consequence: **GL must not also be deferred**, and its WebGL2
profile is already verified working ([spikes/web-webgl2](spikes/web-webgl2/RESULT.md)).

**What building it actually taught the RHI** is collected in
[`docs/rhi-backend-mapping.md`](../rhi-backend-mapping.md), which is ADR-001's fourth measure. Three
findings are worth naming here because they are about the *abstraction* rather than about GL:

- **`ResourceState` has to stay a flags enum.** GL needs the `ShaderWrite` bit specifically, to
  decide between "no call at all" and `glMemoryBarrier`. That the RHI's barrier model carries exactly
  enough to make that distinction, on an API with no barriers, is the strongest evidence it is not a
  Vulkan wrapper.
- **The four-set convention pays for itself on a backend with no sets.** Ordering sets by change
  frequency is what makes GL's flat binding indices stable across pipelines. Numbered arbitrarily it
  would not work at all.
- **`DescriptorWrite.Kind` is advisory; the layout is authoritative.** `DescriptorWrite.Uniform`
  produces the non-dynamic kind and there is no helper for the other, so a backend that trusted the
  write drops the dynamic offset of every caller who used the obvious one. Vulkan catches that only
  because its validation layers check the write's type against the layout's.

Every GL call goes through `IGlApi`, and the test assembly drives a recording implementation of it —
so the translation layer is checked on every build rather than only on the CI leg that has Mesa. The
Silk.NET binding is one file, is nothing but transcription, and is the only file the suite does not
touch.

Known concessions, documented up front so nobody is surprised:
- No true multithreaded command recording. Command lists are recorded into a deferred, replayable
  command buffer in managed memory and replayed on the GL thread at submit. This preserves the RHI's
  threading contract at a modest CPU cost.
- No bindless, no async compute, no timeline semaphores, no sparse resources.
- Uniform buffers only (no storage buffers on WebGL2 — compute is unavailable there entirely, which
  cascades into the post-FX chain having a non-compute fallback for every effect).

### `Vixen.Graphics.WebGPU` — **built**

`Silk.NET.WebGPU` binds native Dawn/wgpu. In the browser, WebGPU is reached through JS interop, not
through the native binding — so `Vixen.Graphics.WebGPU` has two surface implementations (native
Dawn for desktop testing, `JSImport` for browser) behind one backend. WebGPU maps well to the RHI
(bind groups ≈ descriptor sets, render pipelines ≈ PSOs, explicit passes), which makes it a better
long-term web story than WebGL2. It is sequenced after WebGL2 because browser availability, while good
in 2026, still needs the WebGL2 floor.

The seam between the two surfaces is `IWebGpuBinding`, and **it decides nothing**: it marshals and
returns. Translation, validation, handle lifetime, deferred destruction, push-constant emulation and
command replay all sit above it and run unchanged on both, which is what makes the *web* path
testable — `Vixen.Graphics.WebGPU.Tests` drives all of it against a recording fake on a machine with
no GPU, no Dawn and no browser. The browser half is `Vixen.Graphics.WebGPU.Browser`, out of
`Vixen.slnx` for the reason `Vixen.Audio.Backend.WebAudio` is.

Three things the RHI has that WebGPU does not, and what happens instead:

- **Push constants.** Emulated as a dynamic uniform buffer bound at group
  `PipelineLayoutDescription.Sets.Length` — where SPIRV-Cross puts a Vulkan push-constant block when
  it emits WGSL ([07 § ADR-012](07-raven-shader-pipeline.md)). A layout that uses all four bind
  groups *and* declares push constants is refused: WebGPU guarantees four and there is nowhere left.
- **Compute outside a pass.** The RHI has render passes only; WebGPU has no dispatch outside a
  compute pass, so replay opens one on demand and closes it when a render pass or a copy arrives.
- **Border colours.** `ClampToBorder` becomes `ClampToEdge`, which `SamplerDescription.Shadow`
  notices: outside a shadow map reads as the edge texel rather than as lit.

**Owed, and it is a change to `Vixen.Graphics` rather than to the backend.** WebGPU requires a
sampled texture's type and a sampler's comparison-ness to be declared in the bind group layout;
`DescriptorBinding` carries a kind, some stages and a count and nothing about formats. So every
layout the backend builds says "filterable float", and a shadow map bound through one is refused with
that explanation. Sampling depth on WebGPU needs `DescriptorBinding` to grow a sample type — which
every other backend would ignore.

#### The implementation is fetched, and the pin is not a version

Nothing ships a WebGPU implementation — no operating system has one, and `Silk.NET.WebGPU` is
bindings only — so `nuke RestoreNativeDeps` fetches a pinned, checksummed wgpu-native, exactly as it
does MoltenVK. Without it the backend reports itself unavailable, which is backend selection working.

**`Silk.NET.WebGPU` 2.23.0 matches no wgpu-native release**, and finding that out is what running
against a real implementation bought. Its function list predates August 2024 — three entry points it
declares were removed in v22.1.0.1 — while its `WGPURenderPassColorAttachment` carries the
`depthSlice` field added in that same release. It is a Dawn binding. So v0.19.4.1 is pinned, the
loader refuses anything newer with a message naming the missing entry points, and one struct is
written in the older layout for wgpu-native, told apart by an extension only it exports. Three
further things were only findable this way: `wgpuInstanceProcessEvents` is declared and
unimplemented and aborts the process rather than raising; a device created without asking for the
adapter's own limits reports the specification's floor; and `maxColorAttachments` comes back as zero,
so an unreported limit is normalised to the guaranteed floor rather than believed.

This is what [12](12-build-ci-and-testing.md)'s cross-backend equivalence level is for, arriving a
phase early: the same triangle, offscreen, read back and asserted on by position — centre covered,
corners not, and exactly one winding surviving the cull.

## Shader interface

`Vixen.Shaders` owns effects; the RHI only consumes bytecode + a reflection record:

```
ShaderBytecode { Stage, byte[] Data, ShaderFormat Format }   // Spirv | Dxil | GlslSource | EsslSource | Wgsl | Msl
ShaderReflection { DescriptorSetLayout[] Sets, VertexInput[] Inputs, PushConstantRange[] , ThreadGroupSize }
```

Raven produces both (see [07](07-raven-shader-pipeline.md)). The RHI never parses shader source.

## Testing

| Level | Mechanism |
|---|---|
| Unit | Every RHI operation tested against `Null`, asserting the recorded command stream. Handle lifetime/generation tests. Allocator tests (fragmentation, alignment, OOM behaviour). |
| Render graph | Property tests: random pass DAGs produce correct barrier placement, verified against an independent reference tracker; aliasing never overlaps live ranges |
| Validation | Vulkan validation layers + `spirv-val` run in CI on Linux with **lavapipe** (Mesa software Vulkan) — a real Vulkan driver with no GPU, so full API conformance is CI-testable |
| Golden image | ✅ `Samples/01-HelloTriangle` and a suite of **forty** rendering fixtures rendered headless on lavapipe, compared with a perceptual (not bitwise) diff and a tolerance per fixture. Bitwise comparison across drivers is a maintenance sinkhole; perceptual with an explicit threshold is the workable version. The bulk of the suite is one fixture per state bit a backend can silently ignore — which is the row below, made concrete. |
| Cross-backend equivalence | The same fixture rendered on Vulkan/lavapipe and on GL/Mesa-softpipe must match within tolerance. This catches the class of bug where a backend silently ignores a state bit. |
| Device loss | A fault-injection mode in `Null` and Vulkan (`VK_ERROR_DEVICE_LOST` on demand) proving the engine recreates the device and reloads resources rather than crashing — Android and driver-update reality make this mandatory |

### What the first real backend added to this table

Two kinds of test turned out to be missing, and both earned their place by catching something the
levels above did not.

- **Validation as a gate, not a log.** The Vulkan backend's first run against a real driver produced
  twenty-three validation errors while every test passed, because the messages went to the console
  and a console message is not a gate. `VulkanDiagnostics` records what the layers say and a test
  fails on any of it. Validation-clean-in-debug ([00](00-vision-and-principles.md)) is only a
  standard if something enforces it.
- **Read the pixels.** Every RHI test that asserts a recorded command stream passes against a
  backend that draws nothing at all — which is exactly what happened, for a whole afternoon, because
  `BlendState.Opaque` was silently zero-initialised to a write mask of `None`. The headless
  offscreen draws now assert *where the picture is*: centre covered, corners not; culling front vs
  back producing different pictures, which pins the winding convention against the viewport's Y flip;
  a push constant moving a quad from one half to the other. None of those can pass by accident.

### Defaults, and a C# rule with no diagnostic behind it

On a record struct whose primary-constructor parameters are all optional, `new()` binds the
*implicit parameterless struct constructor* — zero-initialising and never running the primary
constructor. Every `public static X Default => new();` in this layer therefore held its enum zero
values rather than the ones its documentation described. Passing one argument forces the right
constructor to bind, and `PipelineDefaultTests` asserts each documented default.

The related trap is that C# cannot give a struct parameter any default but all-zeros, so
`ColourTargetState(format)` meant "write no colour channels". The RHI resolves that once, in
`ColourTargetState.EffectiveBlend`, rather than leaving each backend to rediscover it.
