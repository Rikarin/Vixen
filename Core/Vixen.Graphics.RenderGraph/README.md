# Vixen.Graphics.RenderGraph

A transient-resource, automatic-barrier pass graph over the Vixen RHI.

Passes declare what they read and write. The graph then:

- **culls** passes nothing needs,
- **reuses** memory between resources whose lifetimes do not overlap,
- **places barriers**, batched per pass rather than one at a time,
- **derives store actions**, so a target nothing reads afterwards never reaches memory,
- **hands imported resources back** in the state their owner expects.

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

Specified in [`docs/plan/05-graphics-rhi.md`](../../docs/plan/05-graphics-rhi.md) § Render graph.
