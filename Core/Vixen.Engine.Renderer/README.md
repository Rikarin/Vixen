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

## A world, drawn

`WorldRenderer` is the whole join: the standard features, the shared geometry buffer and its residency,
the extraction systems, and a `SceneRenderHost` to draw with. Before it, a game had to build four
features, a buffer, a residency, two systems and a host — in an order, with every reference between them
right — which is why the samples opened a device and issued draws instead, and why none of them is a
game.

```csharp
using var renderer = new WorldRenderer(device, effects);

renderer.Host.Builder.Views["Camera"] = camera;
renderer.Host.Load(compositor);
renderer.Mount(assets);                  // mesh references now resolve
renderer.Register(loop, opaque.Mask);    // entities now reach the render system

loop.Frame(elapsed);
renderer.Draw(list);
```

`renderer.Draw` rather than `renderer.Host.Draw`: the texture copies a material's maps need go on the
list before anything samples them. A host that skips it leaves every textured material sampling the
table's fallback for ever, which reads as *all my materials are the same flat colour*.

**And the host does all of that for you.** Those six lines are what `Vixen.App` runs at boot:
`Services.Graphics` owns a `WorldRenderer`, a device, a swapchain and the compositor, and drives them
once a frame — so `VixenApp.Run<MyGame>(args)` is a game that draws. Writing them out by hand is for
a head with two windows, its own device, or a frame recorded into somebody else's command list. See
[`Vixen.App`'s README](../../Tools/Vixen.App/README.md).

**`AssetMeshSource` is what made `MeshRenderable` mean something.** Both sides of it were finished —
the catalog resolves a reference, the manager loads and shares the bytes, the extraction system
reconciles entities into render objects — and between them stood one function returning an empty mesh,
so an entity carrying a mesh reference was *authored, saved, compiled, loaded and invisible*.

**Nothing waits.** A load starts on the first ask and the answer is "not yet" until it lands; the
entity keeps no render handle, so next frame's reconciliation asks again. That is the whole
asynchronous story and it needs no queue. The alternative — a synchronous load inside extraction —
stalls the frame a level starts on, once per mesh in it.

**`AssetMaterialSource` did the same for `MeshRenderable.Material`**, which was authored, compiled and
loaded and never resolved — so every drawable in a scene took the one material a host had assigned by
hand. It reads the `MaterialContent` the build wrote, compiles it once per reference, and hands the same
object to every entity that names it: one material is one descriptor set, one uniform block and one
resolved variant, which is the economy `MaterialRenderFeature` is built on.

**Textures do not hold a material up.** A material is answered as soon as it compiles, with its texture
parameters unset; `AssetTextureSource` fills them in as they land and `MaterialRenderFeature.Index`
notices, because it compares the view it holds against the one the material carries. Until then the
index stays zero — the table's fallback, a defined thing to sample. Waiting instead would hold a whole
level's geometry off screen for its slowest texture.

A texture takes three stages and the split is where the costs are: reading a bundle and decoding a
KTX2 are file work and happen on a task; creating the texture is a device call on whichever thread
asked; recording the copy needs a command list and happens in `Draw`. So a texture is viewable the
frame after its bytes were recorded — and the view is created *after* the copy is on the list, never
before, or a material samples undefined memory for a frame.

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
