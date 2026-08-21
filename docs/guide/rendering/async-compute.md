---
title: Scheduling a frame onto two queues
slug: rendering/async-compute
kind: guide
area: Rendering
summary: What `PassKind` means now that something reads it — how the render graph cuts a frame into per-queue segments, why a resource has to be handed between queues rather than merely barriered, why two queues that only want to read one texture need `ResourceSharing.Concurrent` to stop taking turns, how a submission waits for another queue by value rather than by draining it, and which of this engine's passes came out of the audit able to leave the graphics queue.
api: [T:Vixen.Graphics.RenderGraph.PassKind, T:Vixen.Graphics.RenderGraph.QueueScheduling, T:Vixen.Graphics.RenderGraph.RenderGraphSchedule, T:Vixen.Graphics.RenderGraph.RenderGraphSegment, T:Vixen.Graphics.RenderGraph.IRenderGraphQueues, T:Vixen.Graphics.RenderGraph.DeviceQueues, T:Vixen.Graphics.RenderGraph.SerialisedQueues, T:Vixen.Graphics.TimelinePoint, T:Vixen.Graphics.ResourceSharing]
tags: [rendering, render-graph, graphics, synchronisation]
since: 0.1
status: preview
related: [rendering/reading-the-frame, rendering/timing-the-frame, rendering/choosing-a-frame]
---

## What it is

Every pass declares a `PassKind` — `Graphics`, `Compute` or `Transfer`. `RenderGraph.Scheduling`
decides whether the graph acts on that:

| Setting | What the frame becomes |
|---|---|
| `QueueScheduling.Single` (the default) | One segment, one command list, one queue |
| `QueueScheduling.Async` | One segment per run of passes on a queue, one command list each |

`Compile` produces a `RenderGraphSchedule`: a list of `RenderGraphSegment`s in declaration order,
each naming its queue, the passes it covers and the earlier segments its work must not start before.
`Execute(IRenderGraphQueues)` runs it, asking for one list per segment — `DeviceQueues` is the
implementation to hand it.

⚠ **Passes are never reordered.** A schedule is a *partition* of the declaration order, not a
rearrangement of it, and that is what makes the two ways of running one frame comparable call for
call. Only which list a pass is recorded into changes.

## What it is for

An async compute queue exists so that work with no graphics dependency — light clustering, a GPU
cull, a surface-cache update, a probe gather — runs *while* the rasteriser is busy rather than
between two of its draws. On hardware that has a second queue family this is free frame time; on
hardware that does not, it has to cost nothing.

That second half is the harder promise, and it is the one this is built around. OpenGL has one
queue and no way to express another; WebGPU has exactly one; MoltenVK on Apple silicon exposes one
universal family that does everything. **A pass marked `Compute` therefore has to draw the same
picture on a device that runs it inline**, which is why `QueueScheduling.Async` on a device whose
`HasAsyncCompute` is false silently produces exactly the single-queue schedule.

### Why a barrier is not enough

A barrier orders one queue against itself. Two queues need two more things, and neither is
something the existing barrier machinery was saying:

1. **An execution edge.** `RenderGraphSegment.WaitsOn` names the earlier segments whose work must
   have finished — every read-after-write, write-after-read and write-after-write that crosses a
   queue.
   How that edge is *enforced* is the device's business, and there are two ways. Where the device has
   timeline semaphores, each queue keeps a counter: a submission signals the next value and hands
   back a `TimelinePoint`, and a dependent submission is given that point and waits for it **on the
   device**, with the calling thread going straight on to record the next segment. Where it does not,
   the only cross-queue primitive left is draining the producing queue from the host, which is
   correct and costs the whole benefit.

2. **An ownership transfer.** Under Vulkan's exclusive sharing mode a resource belongs to one queue
   family, and moving it takes *two* barriers naming the same states: a **release** at the end of
   the owning segment's list and an **acquire** in front of the pass that wants it. Record only one
   and nothing complains — the destination simply reads whatever the memory held. That is the
   failure this page exists to make hard to reach.

`BufferBarrier` and `TextureBarrier` carry `SourceQueue` and `DestinationQueue` for it. Equal — the
default — means no transfer at all, and two `QueueKind`s that land on the same hardware family
collapse to "ignored" in the backend, so a one-queue device records the same barrier it always did.

⚠ **The two halves are not the same barrier recorded twice.** A release is recorded on the queue
giving the resource up, so its *destination* stages describe work on the other queue; an acquire is
recorded on the queue taking it, so its *source* stages do. Vulkan ignores the far half of each —
but a stage mask is still checked against the recording queue's capabilities *before* it is ignored,
so a release from compute to graphics naming `ColorAttachmentOutput` is an error on the compute list
that records it. `VulkanCommandList.Barrier` splits them.

### Why a stage mask has to be clamped

`ResourceState.ShaderRead` means the vertex, fragment **and** compute stages, because the RHI does
not say which stage will read it. On a graphics queue that is right and merely wide. On a
compute-only queue family there is no vertex stage and no fragment stage, and naming one is invalid
usage — and *every hoisted compute pass that reads a texture produces exactly that barrier*.

So the backend intersects both masks, and the matching access masks, with what the recording
queue's family supports. Narrowing is safe here and only here: the stages being dropped cannot
execute on this queue, so there is no work on this queue in them to order against; what the *other*
queue does in those stages is ordered by the handover and its wait edge instead, which is a thing a
barrier could never have done. On a device with one universal family nothing is dropped, which is
what keeps a scheduled frame identical to an unscheduled one there.

### Why two readers had to take turns

Ownership follows **use**, not writing. Under exclusive sharing the moment a second queue reads a
resource the first has to release it and the second to acquire it — and an acquire cannot begin
until the release has finished. A depth buffer that graphics drew and that a compute pass and a
later graphics pass then both merely *read* makes those two readers take turns, over a texture
neither of them is changing.

`ResourceSharing.Concurrent` is the only thing that removes it, because it is not a cheaper barrier
— it is the absence of an owner. The graph asks for it exactly where exclusive sharing would
serialise two readers:

> a transient **read by more than one queue** and **written by at most one**.

Two *users* is not the test. A resource written on one queue and read on another is one handover for
a dependency that genuinely exists — the reader waits for the write whatever the sharing mode says —
so making it concurrent would trade the driver's compression away for nothing. Some hardware answers
a concurrently-shared image by declining to compress it, and that is bandwidth paid on every access
for a synchronisation saving taken twice a frame.

⚠ **Imports are never upgraded.** Sharing is a property of the created resource, so it can only be
asked for by whatever calls the create — which for an import is somebody else. The graph reads the
importer's own `TextureDescription.Sharing` and believes it. An importer that wants the
handover-free read creates its texture `Concurrent` and says so; one that does not keeps the
handover it needs.

## Using it

```csharp compile
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

public static class Clustering {
    public static void Frame(IGraphicsDevice device) {
        var graph = new RenderGraph(device) { Scheduling = QueueScheduling.Async };

        var depth = graph.CreateTexture(
            new TextureDescription(PixelFormat.Depth32Float, 1280, 720, TextureUsage.DepthStencilTarget)
        );

        var clusters = graph.CreateBuffer(new BufferDescription(4096, BufferUsage.Storage));

        graph.AddPass("depth", pass => {
            pass.DepthAttachment(depth);
            pass.Execute(_ => { });
        });

        graph.AddPass("cluster lights", pass => {
            pass.Kind = PassKind.Compute;
            pass.Reads(depth, ResourceState.ShaderRead);
            pass.Writes(clusters);
            pass.SideEffect();
            pass.Execute(context => context.CommandList.Dispatch(20, 12, 24));
        });

        // One command list per segment, submitted by the graph rather than by the caller.
        graph.Execute(new DeviceQueues(device));
        graph.DisposePool();
    }
}
```

`Execute(ICommandList)` still exists and is still what a single-queue frame uses. It refuses a
multi-queue schedule rather than quietly running it on one list, because one list belongs to one
queue and the barriers in the plan name two.

### Reading what you got

`RenderGraph.Schedule` is readable straight after `Compile`, before anything is recorded — "did my
compute pass actually go anywhere" is a question about the schedule, and profiling a frame answers
it far too late.

```csharp no-compile="a fragment — the graph is the one built above"
graph.Compile();

foreach (var segment in graph.Schedule!.Segments) {
    Console.WriteLine($"{segment.Name}: passes {segment.FirstPass}-{segment.LastPass}");
}
```

`ToGraphviz()` draws the same thing: a box per segment, with the wait edges dashed between them.
Two segments on two queues with an edge between them are two segments that run one after the
other — obvious in a drawing and invisible in a list.

### What is deliberately not hoisted

- **A pass with attachments.** Attachments are a draw, whatever the pass says its kind is.
- **`PassKind.Transfer`.** A Vulkan transfer family accepts copies and *nothing else*. The bodies
  have now been audited (below) and six of them really are copies — but the graph's own barriers are
  not: the acquire in front of a transfer pass names the state the resource was in, which for a
  colour target is a stage no transfer family has. The clamp above handles that, and hoisting
  transfers is still a separate switch nobody has thrown.
- **A compute pass that declares no write.** Every wait edge in a schedule comes from a declared
  write, so a pass that declares none is a pass nothing can be made to wait for. Hoisting it puts it
  in a segment with an edge going in and none coming out, and whatever it really produced — a HiZ
  pyramid, a shadow page table, a draw-argument buffer — is then read by the graphics queue while
  the dispatch is still running. This is a rule in `BuildSegments`, not advice: under-declaration
  fails towards the frame the engine already draws.
- **Transient aliasing, once the frame is on two queues.** See below.

## The audit

`PassKind` was carried and never read, so no renderer's declaration had ever been checked against
its body. Seventeen declared a non-graphics kind. What the audit asked of each was not "is the body
a dispatch" but **"can the graph see everything this pass touches"** — because a wait edge comes from
a declared use and from nothing else.

**A pass that cannot honestly declare its uses is marked `Graphics`, and says why at its
declaration.** That is a correct outcome. `PassKind` is a claim about scheduling, not a description
of the body: on one queue, declaration order *is* execution order, which is the ordering such a pass
was always relying on.

| Pass | Declared | Body | Verdict |
|---|---|---|---|
| `ComputeRenderer` | every named buffer and texture read, written or bound | dispatch | **`Compute`** — the model, and the one node a document authors directly |
| `WaterRippleSimulation` | both halves of the ping-pong pair, imported and declared | dispatch | **`Compute`** |
| `GpuCullingRenderer` | `SideEffect()` and *nothing* | dispatch + argument fill | → `Graphics`: what it writes is read by later passes' *draw calls* |
| `ClusterCullingRenderer` | `SideEffect()` and *nothing* | page flush, traversal, two argument fills | → `Graphics`: four invisible products |
| `HiZRenderer` | reads depth; says in its own comment its product is not a graph resource | dispatch | → `Graphics`: an edge in and none out |
| `VirtualShadowRenderer.Mark` | reads depth | dispatch into the atlas page tables | → `Graphics`: same shape |
| `ScreenProbeGatherRenderer` | reads depth when screen-tracing | five dispatches into the atlas and planes | → `Graphics`: same shape |
| `ReflectionRenderer` | reads depth, normals, colour | dispatch into an import it brackets itself, plus a HiZ chain a host may share with the gather | → `Graphics`: two invisible products |
| `VisibilityBufferRenderer.Software` | reads depth, writes identity | dispatch | → `Graphics`: its *extents* come from a buffer `ClusterCullingRenderer` filled |
| `VisibilityBufferRenderer.Tiles` | reads identity, writes colour and the split planes | binning + resolve | → `Graphics`: the resolve binds the page pool and the instance buffer |
| `SurfaceCacheRenderer` | `SideEffect()` and nothing | capture, upload, dispatch | → `Graphics`: no declared use, and the upload's barriers name shader stages |
| `IrradianceFieldRenderer` | `SideEffect()` and nothing | upload, dispatch | → `Graphics`: same |
| `GlobalDistanceFieldRenderer` | `SideEffect()` and nothing | composite, upload | → `Graphics`: same |
| `BufferUploadRenderer` | writes the target as `CopyDestination` | one `CopyBuffer` | **`Transfer`**, honestly |
| `BufferReadbackRenderer` | reads the source as `CopySource` | one `CopyBuffer` | **`Transfer`**, honestly |
| `TextureCopyRenderer` | both ends declared | one `CopyTexture` | **`Transfer`**, honestly |
| `PickingRenderer.Readback` | reads the id target as `CopySource` | one `CopyTextureToBuffer` | **`Transfer`**, honestly |
| `SmaaRenderer.Table` | writes the imported table as `CopyDestination` | one `CopyBufferToTexture` | **`Transfer`**, honestly |
| `ScreenProbeGatherRenderer.Readback` | both planes as `CopySource` | two `CopyTextureToBuffer` | **`Transfer`**, honestly |

Three of those had declared `Transfer` over a body that is not a copy: `GlobalDistanceFieldTexture`,
`IrradianceFieldTexture` and `SurfaceCacheTexture` all bracket their copies with barriers into
`ResourceState.ShaderRead`, and no transfer family has a shader stage to name. Nothing had caught it
because nothing read the declaration, and because `PassKind` was never what kept those copies
outside a render pass — declaring no attachments is.

### So can anything run `Async` yet?

**Turning it on is now safe, and buys almost nothing.** Two passes in the tree can honestly leave
the graphics queue — the generic `ComputeRenderer` a document authors, and the water ripple step —
and neither is in the critical path of a frame that would notice. Every node whose dispatch *is*
worth overlapping (the cull, the HiZ reduce, the probe gather, the surface cache) turns out to keep
its product outside the graph on purpose, because the product outlives the frame and a graph
resource would be aliased away.

⚠ **And no measurement is possible on any hardware here.** `HasAsyncCompute` is false on M1 Max
through MoltenVK and false on lavapipe — one universal family each — so the schedule collapses to a
single segment before any of this is reached. `NullDevice` is the only backend reporting three
distinct queues, and it records rather than executes. Nothing in this tree can observe overlap, so
nothing here claims a speed-up.

The honest reading is that the audit's value was the four passes it stopped: `GpuCullingRenderer`,
`ClusterCullingRenderer`, `HiZRenderer` and `ScreenProbeGatherRenderer` would each have been hoisted
into a segment nothing waits for the first time somebody set `Scheduling` on a discrete card.

### Transient aliasing under async

The trade stands: **an async frame gives every transient its own memory.** The reason is not that
the condition is hard to compute — it is that the condition is different, and where it holds it is
worthless.

Aliasing needs the segment taking the memory to be guaranteed to *start* after the segment giving it
up has *finished*. There are two ways to know that:

- **An explicit wait edge.** But a schedule is transitively reduced precisely to have as few of
  those as possible, so aliasing under this rule would only reuse memory between segments already
  forced to take turns — which is to say, exactly where the second queue bought nothing.
- **Two segments on one queue, one submitted after the other.** This does *not* qualify. Vulkan
  orders when batches begin, not when they end.

Against that, the failure is silent and needs genuine concurrency to appear, so it reproduces on the
user's discrete card and never in CI. `TransientResourcePool` reports `Count`, `InUse` and `Reuses`,
so what the decision costs is a number a host can read — which is the right way round: a measurable
amount of memory rather than an unmeasurable class of bug.

## Examples

### Turning it on for one graph

```csharp no-compile="a fragment — the graph and the device are the ones above"
// Scheduling is read by Compile, so set it before the frame is built.
graph.Scheduling = device.Features.HasAsyncCompute
    ? QueueScheduling.Async
    : QueueScheduling.Single;
```

Setting it unconditionally is also correct — the graph downgrades itself — but stating the
capability makes the fallback visible at the call site rather than three assemblies away.

### Supplying your own queues

```csharp no-compile="a sketch of the seam — the device and the submitter lookup are the implementer's"
sealed class MyQueues : IRenderGraphQueues {
    public ICommandList Begin(RenderGraphSegment segment) =>
        device.BeginCommandList(segment.Queue, segment.Name);

    public void Submit(RenderGraphSegment segment, ICommandList list) {
        // segment.WaitsOn names what must have finished. How that is enforced is yours.
        list.Finish();
        SubmitterFor(segment.Queue).Submit([list]);
    }
}
```

### Waiting by value, and waiting by drain

`DeviceQueues` picks per device and says which it picked:

```csharp no-compile="a fragment — the device is the one above"
var queues = new DeviceQueues(device);

// True where every queue has a counter; false on OpenGL, on WebGPU, and on a
// Vulkan 1.1 driver that declines the feature bit.
Console.WriteLine(queues.UsesWaitValues);
```

| | Wait by value | Drain |
|---|---|---|
| Needs | `HasTimelineSemaphores` | nothing |
| Who waits | the device | the calling thread |
| What else stops | nothing | every other submission on that queue |
| Spelled | `Submit(lists, waitFor)` → `TimelinePoint` | `WaitIdle()` |

`SerialisedQueues` is the drain path on its own, and it stays public for two jobs: it is what
`DeviceQueues` becomes where there are no timeline semaphores, and it is the other arm of the A/B
that proves the two paths draw the same frame.

⚠ **A `TimelinePoint` may only be one a submitter handed back.** Values belong to the queue that
issues them. One built by hand that is beyond what will ever be signalled is a device-side hang with
no validation message and no stack; one below it is a wait that returns before the work it named
finished. The Null backend refuses a point that was never issued, which is the only place that
mistake is cheap to catch.

### What it still does not buy

**Frame time, on any hardware in this tree.** `QueueScheduling.Async` hoists a pass only where
`HasAsyncCompute` is true, and that means a queue *family* of its own — which MoltenVK on Apple
silicon does not have, and lavapipe does not have. On a device with one universal family the
schedule collapses to a single segment before any of this is reached, and the frame is the frame it
always was.

So the wait-value path is built, exercised and validation-clean, and what it is waiting for is
hardware with a second queue family. What `Async` buys meanwhile is **correctness coverage** — the
segmentation, the ownership transfers and the wait edges. A golden image taken through either path
stays valid, because the frame is the same frame.

## See also

- [A pass that reads the frame so far](reading-the-frame.md) — the other place a pass's declarations
  decide what the graph does behind it.
- [Timing the frame on the GPU](timing-the-frame.md) — the scopes are per pass, and a segment's
  barriers are charged to the pass that needed them.
- [Choosing a frame](choosing-a-frame.md) — which compositor a document runs, and what its passes are.
