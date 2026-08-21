---
title: Scheduling a frame onto two queues
slug: rendering/async-compute
kind: guide
area: Rendering
summary: What `PassKind` means now that something reads it — how the render graph cuts a frame into per-queue segments, why a resource has to be handed between queues rather than merely barriered, how a submission waits for another queue by value rather than by draining it, and why the switch is off by default.
api: [T:Vixen.Graphics.RenderGraph.PassKind, T:Vixen.Graphics.RenderGraph.QueueScheduling, T:Vixen.Graphics.RenderGraph.RenderGraphSchedule, T:Vixen.Graphics.RenderGraph.RenderGraphSegment, T:Vixen.Graphics.RenderGraph.IRenderGraphQueues, T:Vixen.Graphics.RenderGraph.DeviceQueues, T:Vixen.Graphics.RenderGraph.SerialisedQueues, T:Vixen.Graphics.TimelinePoint]
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
- **`PassKind.Transfer`.** A Vulkan transfer family accepts copies and *nothing else*, and until now
  nothing read `PassKind` at all — so no pass in the tree has ever had its body checked against
  that. Hoisting those wants an audit, not a switch.
- **Nothing sets `Scheduling` to `Async` in the engine**, and the audit that would change that is
  about *declarations*, not about `PassKind`. A wait edge is derived from declared reads and writes,
  so a compute pass that writes something the graph cannot see gets **no edge** — harmless on one
  queue, where declaration order is execution order, and a race on two. Nine renderers declare a
  compute pass today and most pair it with `SideEffect()`; `GpuCullingRenderer`'s declares
  `SideEffect()` and *no resource uses at all*, and `HiZRenderer`'s says in its own comment that
  what it writes is not a graph resource. Both would be hoisted into a segment nothing waits for.
  **That is the audit — every hoistable pass declaring what it touches — and it is owed before any
  renderer turns this on.**
- **Transient aliasing, once the frame is on two queues.** "Their lifetimes do not overlap" is a
  statement about pass order, and pass order stops being a statement about *time* the moment two
  queues run at once. Async frames give every transient its own memory.

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
