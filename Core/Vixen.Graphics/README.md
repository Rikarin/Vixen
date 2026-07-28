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
| `Pipelines` | Descriptor set slots and kinds, pipeline layouts, vertex layouts, and the graphics and compute pipeline descriptions. |
| `Barriers` | Buffer and texture barriers, submitted as a group rather than one at a time. |

The **interfaces** — `IGraphicsDevice`, `ICommandList`, `ICommandSubmitter`, `ISwapChain` — and the one
piece of machinery that is not vocabulary: `DescriptorAllocator`.

`Vixen.Graphics.Null` implements all of it against no GPU, and with recording turned on is what makes
"did my render feature emit the right calls" a unit test rather than a screenshot diff — which
[doc 14](../../docs/plan/14-roadmap.md) § Sequencing makes a Phase 1 obligation because every later
phase's testability depends on it.

## The decisions

**Handles, not disposable wrappers.** Every resource is a generation-checked `Handle<T>` into a
backend-owned table. No finalisers, no `IDisposable` per GPU object, no garbage collector between the
engine and the driver — and a stale handle is caught by its generation rather than being a
use-after-free.

**`Destroy` is deferred, and that is a contract rather than an implementation detail.** Freeing an
object a submitted command buffer still references is undefined behaviour, and the unsafe window is
exactly `FramesInFlight` frames wide — which a caller has no way of knowing. So a handle becomes
invalid to the caller immediately and the object is freed once no frame that could reference it is
running. That is what lets a renderer recreate a buffer mid-frame without waiting. It does *not*
extend to overwriting a live resource's **contents**, which no backend can defer for anybody; that is
what the ring in `DescriptorAllocator` — and the ones in the renderer's upload buffers — are for.

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

## The lifetime the RHI does not have

`DescriptorAllocator` is the one thing here that is machinery rather than vocabulary, and it exists
because a frame graph needs a descriptor lifetime nothing else does. A pass that samples the shadow
atlas cannot own a set: the atlas is a graph resource, so its handle does not exist until the graph
has compiled, and next frame the same name may alias different memory. The set has to be written
*after* the graph resolves and thrown away when the frame ends.

Three things it does, in the order they matter:

**Sets are recycled, not destroyed.** A retired set goes back to a free list keyed by its layout.
`vkResetDescriptorPool` is cheap and `vkAllocateDescriptorSets` is not, and a pool that grows to fit
the worst frame never shrinks.

**The ring is `FramesInFlight` deep, and that is not a tuning knob.** A set written for frame *f* is
still being read while the CPU records *f+1*. Rewriting it earlier points a descriptor the GPU is
reading at something else — a use-after-free that most drivers execute without a word, and that the
validation layers only catch with synchronisation validation switched on. `BeginFrame` already blocks
until frame *f − FramesInFlight* completed, so a ring of exactly that depth is necessary and
sufficient. The test for it is that the same request on consecutive frames returns *different* sets
until the ring comes round, and that a steady frame then settles at exactly that many.

**Identical writes within a frame return the same set.** The cache is content-addressed over the
layout plus the exact sequence of `DescriptorWrite`s, so every pass reading the same atlas and the
same light list shares one set — the difference between a set per pass and a set per *distinct
combination*. Which also means the set you are handed may be shared, so never `UpdateDescriptorSet`
on it yourself. The cache does not survive the frame, and that is the point rather than an oversight:
the handles in it name transient memory the next frame's graph is free to give to something else, so
a persistent cache would be correct exactly until two frames' graphs differed.

A lookup probes with a `ReadOnlySpan<DescriptorWrite>` through `Dictionary.GetAlternateLookup`, not
with a constructed key. Building a key would mean copying the writes into an array on every request
including the hits — the allocation the cache exists to avoid, once per lookup instead of once per
set.

## Nothing here names Vulkan

No Silk.NET type appears in this assembly's public surface — ADR-001 §3, which is what keeps the RHI
mappable to a second backend. The same rule is why the surface handles a swapchain is built from live
in `Vixen.Platform` as a discriminant and two `nint`s rather than as a `VkSurfaceKHR`.

Licensed under Apache-2.0.
