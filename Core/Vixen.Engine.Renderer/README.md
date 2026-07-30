# Vixen.Engine.Renderer

The GPU half of the debug geometry. `Vixen.Engine` accumulates it and this draws it.

Spec: [docs/plan/13-diagnostics.md](../../docs/plan/13-diagnostics.md) § Debug rendering and
§ Diagnostic overlays.

```csharp
var debug = new DebugDrawRenderer(device, shaders, output);

// Once a frame, outside the render pass — this writes buffers.
debug.Upload(draw, camera.View, new Vector2(target.Width, target.Height));

graph.AddPass("debug", pass => {
    pass.ColourAttachment(colour, LoadAction.Load);
    pass.DepthAttachment(depth, LoadAction.Load);
    pass.Execute(context => debug.Record(context.CommandList, camera.ViewProjection(aspect)));
});
```

## Why this is its own assembly

The same reason `Vixen.Ui.Renderer` is. `Vixen.Engine` is the layer a game is written against and
does not reference a graphics API — which is what lets `Vixen.Physics`, `Vixen.Navigation` and
`Vixen.Audio` produce debug geometry without linking one. `Vixen.Rendering` draws lines without
knowing what a debug overlay is. Putting the join in either would drag one into the other.

## Drawing a frame

`SceneRenderHost` is the other join this assembly makes, and it is the same shape as the first: a
render system belongs to `Vixen.Rendering` and knows nothing about a window, a compositor is a
document, and a device outlives both. What was missing is the object that owns one of each and turns
them into a recorded frame — `new RenderSystem()` appeared only inside test projects, and every sample
opened a device and issued draws directly.

**It does not run the render system's phases.** `GraphicsCompositor.Build` already collects the frame's
views from its own nodes and then calls `RenderSystem.Draw`, in that order, because culling before the
views are collected culls against the previous frame's. A host that ran the phases itself would extract
every feature twice a frame — a correct-looking picture, and a renderer that profiles as half as fast.
`Every_feature_is_extracted_once_a_frame` is the assertion.

So what is left is three calls in an order — reset the graph, build it, execute it into a list the
caller owns. It opens no command list and submits nothing: when a frame is presented is the
application's business.

## Two draws, not one

`DebugDraw` accumulates three things and they come out as two draw calls:

| Accumulated | Drawn with | Depth |
|---|---|---|
| World lines | the view-projection | tested by default (`DepthTested`) |
| World labels | the same, billboarded onto the camera's plane | as above |
| Screen lines — the overlays | a pixel-to-clip matrix | never |

They cannot share a draw because they do not share a transform. `DebugGeometry` builds both vertex
spans and is a pure function of the accumulator plus a camera basis, so it is tested without a
device; `DebugDrawRenderer` owns the two `LineRenderer`s and the ring buffers.

⚠ **`RecordScreen` exists for the frame with no camera.** A build failing to load its level still
has frame stats and a log tail worth reading, and requiring a view-projection to show them would be
requiring the thing that is broken.

⚠ **`Upload` writes buffers, so it must be called outside a render pass.** Vulkan forbids a transfer
inside one. Same reason `UiRenderer` uploads its atlas before its pass rather than in it.

⚠ **Labels are faced using the *columns* of the view matrix.** A world-to-view transform is the
inverse of the camera's own, and for an orthonormal rotation the inverse is the transpose — so the
camera's world-space right axis is the first column. Reading the rows gives a basis that is correct
only for a camera at the origin looking down −Z, which is exactly how a first test is written.

## Overflow

A frame that produces more vertices than fit is truncated and counted (`Dropped`), not grown and not
thrown for: more lines than fit is a debug overlay somebody left on rather than a reason to take the
process down, and growing the buffer mid-frame would mean recreating it while the GPU reads it.

Licensed under Apache-2.0.
