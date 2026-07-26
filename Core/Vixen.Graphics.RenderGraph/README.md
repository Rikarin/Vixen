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

Specified in [`docs/plan/05-graphics-rhi.md`](../../docs/plan/05-graphics-rhi.md) § Render graph.
