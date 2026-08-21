# Vixen.Graphics.RenderGraph

A transient-resource, automatic-barrier pass graph over the Vixen RHI.

Passes declare what they read and write. The graph then:

- **culls** passes nothing needs,
- **reuses** memory between resources whose lifetimes do not overlap,
- **places barriers**, batched per pass rather than one at a time,
- **derives store actions**, so a target nothing reads afterwards never reaches memory,
- **hands imported resources back** in the state their owner expects,
- **schedules passes onto the device's queues**, when it is asked to — see below.

Hand-written barriers stay available through `ICommandList.Barrier` for the hot paths that want them.
This layer is the one most engine code uses, because with six backends, hand-maintaining barrier
correctness across deferred shading, shadows, SSAO, SSR, TAA, bloom and depth of field is not
achievable.

```csharp
using var pool = new TransientResourcePool(device);
var graph = new RenderGraph(device, pool);

var depth = graph.CreateTexture(new(PixelFormat.Depth32Float, w, h, TextureUsage.DepthStencilTarget, Name: "depth"));
var colour = graph.ImportTexture(backbuffer, backbufferView, backbufferDescription,
    ResourceState.Undefined, ResourceState.Present);

graph.AddPass("opaque", pass => {
    pass.ColourAttachment(colour, LoadAction.Clear, Color4.Black);
    pass.DepthAttachment(depth);                 // clears to 0 — far, under reversed-Z
    pass.Execute(ctx => DrawScene(ctx.CommandList));
});

graph.Execute(commandList);
graph.Reset();
```

## A simulation that reads its own previous frame

`PingPongTextures` is two persistent textures and a rotation between them —
[`docs/plan/35-water.md`](../../docs/plan/35-water.md) § B5. They are **imported** rather than
declared, because the graph's transients are recycled at the frame boundary precisely because their
lifetime ends there, and a ping-pong's does not.

```csharp
using var ripples = new PingPongTextures(device, description);

// Per frame: both halves go in, whether or not the step touches them.
var pair = ripples.Import(graph);

graph.AddPass("step", pass => {
    pass.Kind = PassKind.Compute;
    pass.Reads(pair.Read);
    pass.Writes(pair.Write, ResourceState.ShaderWrite);
    pass.Execute(ctx => ctx.CommandList.Dispatch(16, 16));
});

graph.Execute(commandList);
ripples.Advance();          // after Execute, never mid-declaration
```

⚠ **The pair remembers the state each texture was left in.** An import that entered as `Undefined`
would be telling the driver the previous contents may be discarded — which on hardware with
compressed targets they will be — so an untracked ping-pong is thrown away silently on the frame it
first mattered.

⚠ **`HasHistory` exists because the graph cannot catch a first-frame read.** An import counts as
produced by read validation, so reading one nothing has written is legal and silent, and the zeroes
most drivers hand back are exactly what a settled height field looks like. `Clear` primes both halves;
it is a render pass, so the textures need `ColourTarget` and a storage-only pair is refused by name.

## Async compute

`PassKind` is what a pass says about which queue its work belongs on, and `RenderGraph.Scheduling` is
whether the graph acts on it. `Compile` produces a `RenderGraphSchedule`: one `RenderGraphSegment` per
run of consecutive passes on a queue, each naming the earlier segments its work must not start before.

```csharp
graph.Scheduling = QueueScheduling.Async;
graph.Execute(new SerialisedQueues(device));     // one command list per segment
```

⚠ **`QueueScheduling.Single` is the default, and turning it on is a claim every pass has to keep.**
`PassKind` was carried on every pass and read by nothing until this existed, so no renderer's
declaration of it has ever been checked against what its body records. A pass that says `Compute` and
draws is a frame that stops working.

⚠ **A schedule never reorders passes.** It partitions the declaration order, which is what lets the
same frame scheduled both ways be compared call for call — and is why a device with one queue (GL,
WebGPU, MoltenVK) produces exactly the single-queue frame from the same declarations.

⚠ **A cross-queue handover is two barriers, not one.** `BufferBarrier` and `TextureBarrier` carry
`SourceQueue` and `DestinationQueue`; the release goes at the end of the owning segment's list and the
acquire in front of the pass that wants it, with identical states. Recording one half is not an error
on any API — the destination reads whatever the memory held — so the graph plans both from one walk
and the backends refuse a list at neither end.

⚠ **Async frames do not alias transients.** "Their lifetimes do not overlap" is a statement about pass
order, and pass order stops being a statement about time once two queues run at once.

⚠ **An aliased take-over transitions from `Undefined`, and that is a claim about the contents only.**
State is tracked per virtual resource, so the second tenant of a pool slot honestly says its previous
state was `Undefined` — the contents may be discarded, which is what avoids a decompress for garbage.
What the barrier must still wait for is the *previous* tenant's last write to that same physical
resource. A backend that reads `Undefined` as "there is nothing to wait for" and names a top-of-pipe
source orders against nothing at all, on the single-queue path, which is the one every real device
here runs. `VulkanBarriers.SourceStage` is where that distinction lives.

**A wait edge is enforced by value where the device can.** `DeviceQueues` submits each segment with
the `TimelinePoint`s its producers reached, so the device waits and the calling thread does not;
`SerialisedQueues` is the same frame with each edge enforced by draining the producing queue, and is
what `DeviceQueues` becomes where `HasTimelineSemaphores` is false.

**What it still does not buy is frame time**, and that is now a hardware fact rather than a missing
primitive: `Async` hoists a pass only where `HasAsyncCompute` is true, which means a queue *family* of
its own, and neither MoltenVK on Apple silicon nor lavapipe has one. See
[the guide page](../../docs/guide/rendering/async-compute.md).

Specified in [`docs/plan/05-graphics-rhi.md`](../../docs/plan/05-graphics-rhi.md) § Render graph.
