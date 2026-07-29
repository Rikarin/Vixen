# Vixen.Graphics.WebGPU

The WebGPU backend. Bind groups are descriptor sets, render pipelines are PSOs, passes are explicit —
the RHI maps onto it almost one for one.

```csharp
if (!NativeWebGpuBinding.TryCreate(new() { Surface = window.Handle }, out var binding, out var why)) {
    return;  // No Dawn, no wgpu-native. Selection moves on to Vulkan.
}

using var device = new WebGpuDevice(binding);
using var swapChain = device.CreateSwapChain(new(window.Handle, window.Size));
```

Spec: [docs/plan/05](../../docs/plan/05-graphics-rhi.md) § `Vixen.Graphics.WebGPU`,
[docs/plan/10](../../docs/plan/10-platforms.md) § Web, [docs/plan/14](../../docs/plan/14-roadmap.md)
§ Phase 10.

## Two ways in, one backend

`Silk.NET.WebGPU` binds `webgpu.h`, which Dawn and wgpu-native implement and **which a browser does
not expose at all**. So doc 05 asks for two *surface implementations* behind one backend, and the
seam is [`IWebGpuBinding`](IWebGpuBinding.cs):

| | Where | What it is |
|---|---|---|
| `NativeWebGpuBinding` | here, `Native/` | Dawn or wgpu-native through Silk.NET |
| `BrowserWebGpuBinding` | [`Vixen.Graphics.WebGPU.Browser`](../Vixen.Graphics.WebGPU.Browser/README.md) | `navigator.gpu` through `[JSImport]` |

Everything above that line — translation, validation, handle lifetime, deferred destruction,
push-constant emulation, the recorded command stream and its replay — is written once and runs
unchanged on both. **That is what makes the web path testable**: `Vixen.Graphics.WebGPU.Tests` drives
all of it against a recording fake, on a CI machine with no GPU, no Dawn and no browser.

It is also the reason `IWebGpuBinding` decides nothing. Every method on it marshals and returns; if
one of them ever had a branch in it, that branch would exist twice.

## Deferred recording, like the GL backend

A WebGPU command encoder belongs to one thread, and a browser tab has one thread. The RHI promises
lists record on any thread. So a list records into a flat managed stream and `WebGpuQueue` replays it
into an encoder at submit — the same answer `Vixen.Graphics.OpenGL` gives, for the same reason.

Replay also does the two things WebGPU's model needs that the RHI's does not mention:

- **It opens a compute pass around dispatches.** The RHI has render passes and no compute passes — a
  dispatch simply happens between them — and WebGPU has no dispatch outside one. Replay opens a pass
  on demand and closes it when a render pass or a copy arrives.
- **It turns every `PushConstants` into a ring allocation and a bind.** See below.

## Push constants, on an API that has none

WebGPU's answer to push constants is a uniform buffer, so that is what
[`PushConstantRing`](PushConstantRing.cs) is: one buffer, an aligned slot per write, bound with a
dynamic offset.

**Where the block is bound is the part a shader has to agree with.** It is bind group
`PipelineLayoutDescription.Sets.Length` — immediately after the caller's own sets — binding 0. That is
where SPIRV-Cross puts a Vulkan push-constant block when it emits WGSL, so a module that came through
Raven's cross-compilation ([07 § ADR-012](../../docs/plan/07-raven-shader-pipeline.md)) already
declares it there.

A pipeline layout that uses **all four** bind groups and also wants push constants is refused at
creation, with the arithmetic in the message: WebGPU guarantees four groups and there is nowhere left
to put a fifth. Fold the per-draw constants into the per-draw set through a dynamic offset.

## What WebGPU does not have

Each of these is a capability the RHI already had, reported honestly, with the fallback it forces:

| Absent | Consequence |
|---|---|
| Geometry, tessellation, mesh shaders | Vertex, fragment and compute, and no plan for more |
| Bindless | GPU-driven culling and material batching take the non-bindless path — the one MoltenVK forces too (ADR-011) |
| Multi-draw indirect | `DrawIndexedIndirect` with a count above one is **refused**, not looped, so the cost is visible at the call site |
| A second queue | All three RHI submitters are the one queue; `HasAsyncCompute` and `HasAsyncTransfer` are false |
| Timeline semaphores, sparse resources, pipeline statistics, wireframe, float64, subgroups | None exist |
| Border colours | `ClampToBorder` becomes `ClampToEdge`. **`SamplerDescription.Shadow` notices**: outside a shadow map reads as the edge texel rather than as lit, so a renderer targeting the web clamps the lookup itself |
| A "do not care" load | Becomes `Clear`, which is the right of the two — a tile fill, never a read from main memory |
| 16-bit normalised formats | `R16UNorm` and `Rgba16UNorm` are refused by name rather than silently substituted |
| Eight or sixteen samples | The set is fixed at 1 and 4 by specification; there is nothing to query |

And two that are present where a reader might not expect them. **Compute is always there** — unlike
WebGL2, which is the whole reason this is the better long-term web story. **Dynamic rendering is
always there** too: WebGPU has no render-pass or framebuffer objects to fall back to, which is exactly
what that capability reports.

Barriers are validated for shape and dropped. WebGPU tracks resource state itself, so there is no call
to make — the same elision the GL backend does. They are not useless: a render graph that gets them
wrong is wrong on Vulkan, and this backend is where that costs nothing to find out.

## A sampled texture's type is declared, not inferred

WebGPU needs a sampled texture's type — filterable float, unfilterable float, depth, integer — and a
sampler's comparison-ness stated *in the bind group layout*, which is built before there is a resource
to ask. `DescriptorBinding.SampleType` is where that comes from, and it is the one field in the RHI's
descriptor vocabulary that exists for this backend; Vulkan reads past it and GL has no use for it at
all.

A shadow map is the case that is not the default. Both halves of it say the same thing:

```csharp
new DescriptorSetLayoutDescription(DescriptorSetSlot.PerView, [
    new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, SampleType: DescriptorSampleType.Depth),
    new(1, DescriptorKind.Sampler,        ShaderStage.Fragment, SampleType: DescriptorSampleType.Depth)
], "Shadow");
```

A binding and a resource that disagree — a depth view in a float binding, an ordinary sampler in a
comparison one, a filtering sampler where the texture is an integer — is caught by
`WebGpuDevice.UpdateDescriptorSet`, naming the binding and the declaration to change, rather than by a
browser's own error message a frame later naming neither. `PixelFormats.SampleTypeOf` is what a caller
holding the texture already can declare from.

What this does *not* yet reach is a layout built from shader reflection: Raven has no depth texture and
no comparison sampler in its type system, so `EffectData` reports every sampled binding as an ordinary
float one and an effect's layout still says so. Shadow maps on the web want that half too — the RHI is
no longer what is missing.

## Known gaps

**No timestamp queries.** The feature is requested where offered and nothing reads it yet.

## Nothing ships Dawn, so the binary is fetched

Unlike Vulkan, no desktop operating system ships a WebGPU implementation, and `Silk.NET.WebGPU` is
bindings only — there is no NuGet package carrying the binaries for the RIDs the engine targets.

```bash
./build.sh RestoreNativeDeps
```

That fetches the pinned, checksummed wgpu-native named in
[`build/native-dependencies.json`](../../build/native-dependencies.json) into `artifacts/native/`,
and [`WgpuNative.targets`](../Vixen.Platform.Native/build/WgpuNative.targets) copies it into
`runtimes/<rid>/native/` — the first place `NativeSearch` looks. Without it,
`NativeWebGpuBinding.TryCreate` reports failure, which is backend selection working rather than an
error; `VIXEN_WEBGPU_PATH` points the search somewhere else.

`WebGPU.GetApi()` is not called, for the reason `VulkanLoader` gives: it builds Silk.NET's default
context, which finds a native library through `Assembly.Location` and `DependencyContext.Default` —
neither of which exists in a NativeAOT binary (R11).

## The version pin is not a preference, and it is not a version either

**`Silk.NET.WebGPU` 2.23.0 matches no wgpu-native release.** Its function list is the one from before
August 2024 — it declares `wgpuAdapterGetProperties`, `wgpuDeviceSetUncapturedErrorCallback` and
`wgpuSurfaceGetPreferredFormat`, all three of which wgpu-native removed in v22.1.0.1 — while its
`WGPURenderPassColorAttachment` carries the `depthSlice` field wgpu-native *added* in that same
release. It is a Dawn binding, and there is no wgpu-native that agrees with it on both counts.

So two things happen, and both are checked rather than assumed:

- **v0.19.4.1 is pinned**, the last release that exports everything the binding calls, and
  [`WebGpuLoader`](Native/WebGpuLoader.cs) refuses anything newer at load with a message naming the
  missing entry points. Silk resolves them lazily through function pointers, so without that check a
  newer library is not a link error — it is a null call some frames later, with a stack that names
  nothing.
- **One struct is written in the older layout**, in the one place it is built. Passing Silk's to
  wgpu-native puts `resolveTarget`'s low half where `loadOp` belongs, and wgpu panics with `invalid
  load op for render pass color attachment: 0` — a message that names neither the struct nor the
  version. Told apart by `wgpuDevicePoll`, which is wgpu-native's own extension and which Dawn does
  not export.

`wgpuInstanceProcessEvents` is the other one. wgpu-native 0.19 declares it and does not implement it:
calling it panics the Rust runtime, which aborts the process rather than raising anything .NET can
catch. `wgpuDevicePoll` is used where it exists, and the specification's entry point only where it
does not.

Moving forward means moving `Silk.NET.WebGPU` forward first — and 2.23.0 is what
`Directory.Packages.props` pins for every other Silk binding.

## The trap this backend is full of

A name used inside `Vixen.Graphics.WebGPU.Native` is looked up through the **enclosing namespaces**
before any `using` directive. So an unqualified `BlendFactor` there resolves to
`Vixen.Graphics.BlendFactor`, not Silk's — and `(BlendFactor)value` still compiles, because both are
enums. The result would be a cast that changes nothing and a pipeline built from the RHI's numbering.

Every clashing name is therefore aliased, and `WebGpuEnumAgreementTests` asserts every member of the
backend's own WebGPU enums against Silk's, one case per value. A binding upgrade that inserted one
texture format would otherwise shift every format above it: every pipeline would still compile, every
draw would still run, and the picture would be wrong.

Licensed under Apache-2.0.
