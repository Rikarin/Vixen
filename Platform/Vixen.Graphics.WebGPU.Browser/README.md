# Vixen.Graphics.WebGPU.Browser

WebGPU as a browser has it: `navigator.gpu`, reached through `[JSImport]`.

```csharp
var binding = await BrowserWebGpuBinding.CreateAsync(new() { CanvasSelector = "#vixen" });
using var device = new WebGpuDevice(binding);

using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new(1280, 720)));
```

Spec: [docs/plan/05](../../docs/plan/05-graphics-rhi.md) § `Vixen.Graphics.WebGPU`,
[docs/plan/10](../../docs/plan/10-platforms.md) § Web, with the platform head itself in Phase 10.

## Why this is a separate project

`Silk.NET.WebGPU` binds `webgpu.h`, which Dawn and wgpu-native implement and **which a browser does
not expose at all**. Browser WebGPU is JavaScript objects and promises, so doc 05 asks for two
*surface implementations* behind one backend, and this is the second.

The seam is [`IWebGpuBinding`](../Vixen.Graphics.WebGPU/IWebGpuBinding.cs). Everything above it —
enum translation, validation, handle lifetime, deferred destruction, the recorded command stream and
its replay — comes from [`Vixen.Graphics.WebGPU`](../Vixen.Graphics.WebGPU/README.md) unchanged and
is tested there. What is here is marshalling.

## Not in `Vixen.slnx`

This project targets `net10.0-browser`, which needs the `wasm-tools` workload even to *evaluate* —
and a solution that will not restore on a machine without it is a solution nobody can open. So it
sits on disk and out of the solution, exactly as `Vixen.Audio.Backend.WebAudio`,
`Vixen.Platform.Android` and `Vixen.Platform.iOS` do. `nuke Compile` does not build it and neither
does CI today.

```bash
dotnet build Platform/Vixen.Graphics.WebGPU.Browser
```

## Descriptors cross as packed bytes

A render pipeline descriptor has around sixty fields nested in run-time-length arrays. One
`[JSImport]` parameter per field is not expressible; a call per field is dozens of boundary crossings
to create one pipeline; JSON allocates and parses text on a path with a frame budget. So
[`WebGpuPacker`](WebGpuPacker.cs) writes a descriptor into a byte buffer and `vixen-webgpu.js` reads
it with a `DataView`.

**Every layout is written down twice** — at the C# method that packs it and at the JavaScript
function that reads it — in the same order and the same words. That repetition is the cost of the
design, and it is deliberate: a mismatch between the two is silent, and the paired comments are what
make it findable.

Enum values cross as `webgpu.h`'s numbers, which the JavaScript side turns into WebGPU's strings
through sparse tables. Those numbers are asserted against Silk.NET's by
`Vixen.Graphics.WebGPU.Tests`, so a binding upgrade that renumbered anything is a red build rather
than a wrong texture format.

## Two things a browser cannot do

Both are reported rather than faked, because a backend that pretends is worse than one that says so.

- **`WaitIdle` returns false.** A tab has one thread and it is the one that would have to run the
  completion callback, so blocking on the queue is a deadlock and not a wait. The RHI's `WaitIdle` is
  called at shutdown and while recreating a swapchain, and both are correct without it on WebGPU: the
  implementation will not destroy anything submitted work still names.
- **`Read` throws.** WebGPU's buffer map is a promise everywhere, and there is no thread here to
  resolve it on. A frame that needs a value back on the web has to ask for it a frame early and pick
  it up later — which is what a well-built readback does anyway, on every backend.

**SPIR-V is refused.** A browser accepts WGSL and nothing else; the specification dropped SPIR-V long
before shipping. Raven cross-compiles to WGSL through SPIRV-Cross
([07 § ADR-012](../../docs/plan/07-raven-shader-pipeline.md)), and a web build ships that output.

## One crossing per call, for now

Each method here is one interop crossing, so a frame of a few thousand draws is a few thousand
crossings. That is measurable and it is not free.

The recorded command stream a layer up is a flat array of blittable structs with its variable-length
parts in side buffers, **precisely so a bulk path can be added without disturbing anything above the
binding**: hand the whole frame over once, replay it in JavaScript. It is not here yet. Doing it
before there is a frame to measure would be guessing at which of these calls actually costs anything.

## The canvas is ours, not the page's

`configureSurface` sets `canvas.width` and `canvas.height` rather than reading them, so the size the
renderer was told about and the size it draws into cannot disagree. That disagreement is the whole of
"my UI is blurry on a high-DPI display".

The alpha mode is `opaque`, not `premultiplied`. A premultiplied canvas composites with the document
behind it, so anything the renderer leaves with alpha below one shows the page through it — a
surprise on the web, impossible anywhere else, and not what a game window means.

Licensed under Apache-2.0.
