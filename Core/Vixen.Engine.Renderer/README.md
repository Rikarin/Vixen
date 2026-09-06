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

**Which is also why the two diagnostic panels that need both sides live here.** `GpuOverlay` reads a
`GpuFrame`, which is `Vixen.Graphics`', and implements `IDiagnosticOverlay`, which is
`Vixen.Engine`'s; `StreamingOverlay` reads `WorldRenderer`'s residency and its texture streamer and
implements the same interface.

⚠ **`StreamingOverlay` is doc 13's third overlay, and the reason it was listed as blocked was
wrong.** The overview said it needed `Vixen.Assets` to report and that `Vixen.Assets` may not
reference `Vixen.Engine`, so it wanted a join assembly of its own. The numbers are not
`Vixen.Assets`' at all — [its README](../Vixen.Assets/README.md) says there is no streaming manager
there and points at `Vixen.Rendering`'s `PageResidency` — and the join assembly the blocker asked for
is this one, which has held `GpuOverlay` since before the blocker was written. No project reference
was added for it.

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

**And with a pool, it streams its mip tail instead.** `WorldRenderer.Textures` carries the quality
tier's `streamingPoolMegabytes`; a non-zero one builds a `TextureStreamer` over `PageResidency`,
whose pages are byte ranges of the KTX2 file's level data. A streamed texture arrives at the
resolution its pinned first page covers and is *replaced* by a larger complete image as pages
arrive — not patched in place, because `baseMipLevel` is ignored for sampled bindings on the OpenGL
backend and a full-size allocation would spend the memory streaming exists to save. Zero is the
default and is exactly the whole-file behaviour above: no residency, no ring, no per-frame cost.

**What decides how large a texture should be is `TextureDemand`.** Once a frame, at the top of
`Draw`, it walks the visible drawables of the mesh feature, `max`es each one's projected size into a
slot for its material, fans that out through `AssetMaterialSource.TexturesOf` to the files the
material samples, and quantises the result onto the ladder of mip widths with a dead band. The
maximum rather than the last user seen, because a texture is shared; a dead band because a wanted
width that oscillates at a level boundary is an image swap on alternate frames, and a swap is an
upload. The screen height comes from `SceneRenderHost.FrameSize`, and a height of zero surveys
nothing — which leaves every texture in the "sampled and not sized wants to be complete" branch
rather than asking for the smallest level of everything. See
[Streaming texture mip tails](../../docs/guide/rendering/texture-streaming.md).

## An interface over the world

`WorldRenderer.Ui` is a `UiRenderFeature`, registered by the constructor whether or not the
application has an interface — the same arrangement the particle feature and the terrain extraction
are in, and for the same reason: a feature with nothing mounted is walked over, and a host that
gains a HUD two scenes in must not have to rebuild the renderer.

⚠ **The feature existed and nothing constructed it.** `Vixen.Ui.Renderer` was written so that a
`UiDocument` could be drawn inside somebody else's renderer, and the somebody else was never
written — a grep for the type found three hits and all three were prose. The interface still
rendered the whole time, through `Vixen.Ui.Desktop` painting a document with `UiRenderer` directly,
which is what a UI-only application and the editor's chrome take; what did not exist was drawing one
as part of a *scene's* frame.

⚠ **This is the whole of the two-renderers rule for it, unusually.** `EditorWorldRenderer` does not
assemble features of its own — it owns a `WorldRenderer` — so registering here reaches the editor's
viewport as well, rather than needing a second registration that could drift from this one.

The shaders stay the host's. Building a `UiRenderer` needs the modules and the formats of the pass
the interface is drawn in, and this assembly knows neither; see `Vixen.Ui.Renderer`'s README on why
that assembly must not grow a compiler. The stage the interface is drawn in has to sort `ByGroup`.

⚠ **Two of the five host steps are outside the pass, and both were added after the registration
was.** `UiRenderFeature.Draw` runs inside the frame's pass, where a texture copy is forbidden and a
second pass cannot be opened — so it can only `Record`. `Upload` writes this frame's vertices and
copies the glyph atlas; `Compose` renders each composited group into a surface of its own. Skipping
the first draws a HUD out of memory nothing has written. Skipping the second draws every faded
group **opaque** rather than approximately faded, because `UiGeometryBuilder` emits a group's
contents at alpha one so the surface can carry the fade. Neither failure raises anything.

⚠ **The surface carries the display's density, and both of those calls read it.** `UiInterface.Scale`
is how many framebuffer pixels one of the geometry's units is; it defaults to one, which is right
only for a document laid out in physical pixels. A projection is a pure mapping from geometry units
to clip space and does not care, but a scissor is submitted in framebuffer pixels and cares about
nothing else — so a HUD laid out in points on a 2× display and drawn at a scale of one clips to the
top-left quarter of the window. `Compose` allocates each group's surface at that scale and `Record`
samples it at that scale, which is why it is a property of the surface rather than an argument to
either: the two disagreeing is a composited panel adrift from the frame around it.
`AMountedInterfaceIsComposedAndRecordedAtItsOwnScale` is the assertion, and it is a differential
between two runs of one frame rather than a hand-computed rectangle.

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

## What `WorldRenderer.Draw` puts on the list before the frame

Three things go on the caller's command list before `Host.Draw`, and every one of them is there because
of *when* rather than *what*:

| | |
|---|---|
| `Residency.Flush` | The vertices and indices themselves. Without it every draw reads whatever the allocator left, which is not a missing mesh but a wrong one. |
| `Environment.Upload` | Set 0's buffers. A set binds whole or not at all, so a frame short one binding draws nothing rather than drawing dimly. |
| `Morphing.Record` | The blend-shape pre-pass. |

⚠ **The morph pass goes after the flush and before every draw, and both halves matter.** It copies each
changed instance's rest pose out of the geometry buffer — so a pass recorded before the flush would
scatter deltas onto bytes that had not arrived — and what it writes is the vertex buffer the shading,
shadow, velocity and depth passes all read, which it leaves in `ResourceState.VertexInput` for them.
Outside any render pass, because the copies are transfers.

`WorldRenderer` owns all three ends of the feature: it constructs `Morphing`, adds it to `Meshes`, hands
it to `MeshExtractionSystem.Morphing` so an extracted mesh with shapes gets a range, and registers
`MorphWeightSystem` so a `BlendShapeWeights` component reaches it. A feature only one of the three
reaches costs memory and draws nothing, which is the state this one was in before.

`MorphWeightSystem` also publishes the mesh's shape *names* back onto the component, which is what a
clip's weight track binds against — see `Vixen.Rendering`'s README. Nothing here registers the
animation side: `Vixen.Animation` references this direction and not the other, so
`AnimationSystems.AddAnimation` adds `BlendShapeAnimationSystem`, and it deliberately needs no
renderer.

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
