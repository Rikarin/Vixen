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

## Known gaps

**A sampled depth texture and a comparison sampler are refused.** WebGPU needs a sampled texture's
type — filterable float, unfilterable float, depth, integer — and a sampler's comparison-ness declared
*in the bind group layout*. The RHI's `DescriptorBinding` carries a binding index, a kind, some stages
and a count, and nothing about formats. So every layout this backend builds says "filterable float"
and "filtering sampler", and binding a shadow map through one is caught by
`WebGpuDevice.UpdateDescriptorSet` with that explanation — rather than by a browser's own error
message, a frame later, naming no binding.

Closing it means the RHI's binding description growing a sample type, which every other backend would
ignore. That is a change to `Vixen.Graphics`, not to this project, and it is owed before a shadow map
renders on the web.

**No timestamp queries.** The feature is requested where offered and nothing reads it yet.

## Nothing ships Dawn

Unlike Vulkan, no desktop operating system ships a WebGPU implementation, and `Silk.NET.WebGPU` is
bindings only — there is no NuGet package carrying the binaries for the RIDs the engine targets. So
the ordinary outcome of `NativeWebGpuBinding.TryCreate` on a developer's machine is **failure**, and
that is backend selection working rather than an error. [`WebGpuLoader`](Native/WebGpuLoader.cs)
reports where it looked, and `VIXEN_WEBGPU_PATH` points it somewhere else.

It also does not call `WebGPU.GetApi()`, for the reason `VulkanLoader` gives: that builds Silk.NET's
default context, which finds a native library through `Assembly.Location` and
`DependencyContext.Default` — neither of which exists in a NativeAOT binary (R11).

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
