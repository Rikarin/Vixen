# Vixen.Graphics

The render hardware interface. Per ADR-001: one explicit, Vulkan-shaped abstraction, five backends,
Vulkan as the reference.

## What is here so far

The **vocabulary** — the types every backend and every render feature speaks in, and the ones that
have to be right before anything can be built on them.

| | |
|---|---|
| `PixelFormat` + `PixelFormats` | The format set, with block sizes, block extents, sRGB pairing, level sizes and mip counts. |
| `GraphicsEnums` | Queues, usages, topology, blend, depth-stencil, sampler, vertex, load/store, present, and the `ResourceState` barrier model. |
| `GraphicsDeviceFeatures` | ~25 capabilities and limits, queried once. |
| `Resources` | Typed handles, and the descriptions a device is asked to create things from. |

**Still to come, and next:** `IGraphicsDevice`, `ICommandList`, `ICommandQueue`, `ISwapChain`, then
`Vixen.Graphics.Null` and the `RecordingBackend` harness — which is what makes "did my render feature
emit the right calls" a unit test rather than a screenshot diff, and which
[doc 14](../../docs/plan/14-roadmap.md) § Sequencing makes a Phase 1 obligation because every later
phase's testability depends on it.

## The decisions

**Handles, not disposable wrappers.** Every resource is a generation-checked `Handle<T>` into a
backend-owned table. No finalisers, no `IDisposable` per GPU object, no garbage collector between the
engine and the driver — and a stale handle is caught by its generation rather than being a
use-after-free.

**Barriers are stated, not inferred.** `ResourceState` is specified against Vulkan's
`synchronization2` rather than the older stage-pair model, deliberately: D3D12's Enhanced Barriers
map onto it directly, so a D3D12 backend added later is additive rather than a re-specification
(ADR-001). Implicit tracking is where an abstraction layer either becomes slow or becomes wrong; the
automatic version lives one layer up, in `RenderGraph`.

**Reversed depth is in the defaults, not in a convention document nobody reads.** A
`DepthStencilAttachment` clears to `0` — which is *far* — and `SamplerDescription.Shadow` compares
with `GreaterEqual`. Clearing depth to 1 is the classic mistake and produces a scene that depth-tests
away entirely; making the correct value the default is worth more than documenting the wrong one.

**Descriptions validate themselves, with the resource's name in the message.** A multisampled texture
with mip levels, a readback buffer nothing can copy into, a cube with four faces — each is caught
where it is described rather than surfacing as a validation-layer message about image creation flags.
The `Name` field is not decoration either: a RenderDoc capture full of `VkImage 0x7f…` is a capture
nobody can read.

**Capabilities are a flat record queried once**, and everything in it has a documented fallback. The
only hard floor is Vulkan 1.1 / D3D12 FL 11_0 / GLES 3.0 / WebGL2, stated once and enforced at device
selection. `GraphicsDeviceFeatures.Minimum` claims nothing it does not have, so a backend that
forgets a line takes the fallback path rather than promising something it cannot do.

**`LoadAction.DontCare` is not a micro-optimisation.** On a tiled mobile GPU, `Load` makes the driver
read the whole attachment from main memory into tile memory before the pass — milliseconds, and
battery. Saying so when it is true is one of the highest-value things the enum does.

**The format set is deliberately not exhaustive.** Every format here is one the engine uses or a
swapchain may hand us. A format nobody has a use for is a format nobody has tested.

## Nothing here names Vulkan

No Silk.NET type appears in this assembly's public surface — ADR-001 §3, which is what keeps the RHI
mappable to a second backend. The same rule is why the surface handles a swapchain is built from live
in `Vixen.Platform` as a discriminant and two `nint`s rather than as a `VkSurfaceKHR`.

Licensed under Apache-2.0.
