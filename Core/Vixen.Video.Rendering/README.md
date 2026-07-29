# Vixen.Video.Rendering

The half of video playback that draws: one pipeline, three plane bindings, sixty-four bytes of push
constant, and the render feature that puts a video on the screen inside somebody else's renderer.

Spec: [docs/plan/06](../../docs/plan/06-rendering-pipeline.md) § Other renderables, which lists video
textures.

```csharp
var renderer = new VideoRenderer(device, shaders, output);

// once a frame, outside the pass
texture.Upload(commands, player);

// once a frame, inside it
renderer.Begin();
renderer.Record(commands, VideoDraw.From(texture, VideoFit.Place(VideoScaling.Contain, player, area)), surface);
```

## Why it is a separate assembly

The same reason [Vixen.Ui.Renderer](../Vixen.Ui.Renderer/README.md) is one. `Vixen.Video` decodes and
uploads and knows nothing about a renderer, which is what lets a thumbnailer link it alone;
`Vixen.Rendering` draws things without knowing what a video is. Putting the join in the first would
drag a renderer into a module a tool uses; putting it in the second would put a WebM demuxer in every
game that draws a mesh.

| | |
|---|---|
| `VideoRenderer` | the device half: a pipeline, a descriptor set per texture, a draw. |
| `VideoConstants` | the sixty-four bytes, laid out to match the shader's block field for field. |
| `VideoRenderFeature` | one render object per video, drawn in a stage that sorts `ByGroup`. |
| `VideoSurfaceUploader` | the ECS bridge: a texture per player, uploaded and extracted once a frame. |
| `VideoRenderTarget` | the same conversion into a target of its own, for consumers that bind one view. |

`VideoRenderer` is separate from `VideoRenderFeature` so a sample or a golden image can drive it
without a `RenderSystem`, a camera or a compositor — which is the only way to find out whether the
shader agrees with the six coefficients the module computed.

## Screen-space, and that is a scope rather than a shortcut

A quad in a rectangle of the target is what a cutscene, a menu background and a panel in a user
interface all are. A video on a **surface in the world** — a television in a corridor, lit by the
scene's lights — is a material on a mesh, which belongs to `MaterialRenderFeature` and, once it
lands, to Raven. Three planes and six coefficients are exactly what a material node would consume,
and nothing here is in its way.

## The shaders are supplied

Turning shader source into modules belongs to `Vixen.Shaders` and, once Raven lands, to it. Until
then a caller hands over what it has — `Samples/11-VideoPlayback` hands over hand-written GLSL —
and what this must not do is grow a compiler. `Shaders/video.vert` and `Shaders/video.frag` in that
sample are the reference implementation, and they are forty lines between them.

⚠ **Nothing checks that the push block matches the shader's**, on any engine. A mismatch is a picture
in the wrong place or the wrong colour rather than an error, so `VideoConstants` and the GLSL block
carry the same field names and `VideoConstantsTests` asserts the size and the arithmetic.

## Fitting is in Vixen.Video, on purpose

`VideoFit` lives one assembly down because both renderers need it and they must agree: a video drawn
in a scene and the same video drawn in an interface panel that letterboxed differently would be a
difference nobody could explain and nothing could test.

**No bars are painted.** `VideoScaling.Contain` shrinks the rectangle and leaves the pass's clear to
fill what is left; the shader used to discard and write black, which is right for a player showing a
video on nothing and wrong the first time somebody puts one behind a menu. `Cover` crops the texture
coordinates instead, which is the other half of `VideoPlacement`.

## The ECS path

`VideoSystem` in `Vixen.Video` advances the players in `SystemPhase.Update` and its own remarks say
the picture is uploaded in `PreRender`. `VideoSurfaceUploader` is that step, and it is a plain class
rather than a `SystemBase` because it needs a command list and an ECS system here is handed a world
and a time and nothing else:

```csharp
uploader.Upload(world, commands);                       // outside the pass
uploader.Extract(world, renderSystem, feature, surface); // before the sort
```

An entity draws when it has a `VideoScreenPlacement` beside its `VideoSurface`, whose `Area` is a
fraction of the target rather than pixels — so a cutscene written as `(0, 0, 1, 1)` is full-screen on
every display. A texture is owned per *player*, not per entity, so a video on a wall and the same
video in a mirror decode once and draw twice.

## A video as an ordinary texture

`VideoRenderer` draws planes straight into whatever pass it is recorded in, which is the cheapest
thing for a cutscene and no use at all to a consumer that can only bind *one* view — a user
interface's image command, a material slot, a thumbnail. `VideoRenderTarget` runs the same conversion
into a target of its own and hands over the view:

```csharp
target.Draw(commands, planes, player);        // outside any pass
ui.RegisterImage(handle, target.View);        // whenever target.Revision changes
```

That is the whole of "a video in a user interface". `Vixen.Ui` already draws a texture nobody there
wrote — an element puts a number in an image command and the host registers a view against it — so
nothing had to be added to it, and neither assembly references the other.

⚠ **It costs a texture and a pass per video per frame, and that is the whole trade.** A full-screen
cutscene should not use it. What it buys is that three R8 planes stop being something every consumer
has to understand.

⚠ **Watch `Revision`.** A resize destroys the texture and makes a new one, so a descriptor set still
naming the old view names freed memory — undefined rather than an error, and it shows as a picture
that is fine until the window is dragged.

The target is sized to the picture's *display* size, so anamorphic content is resampled to square
pixels once here rather than leaving every consumer to remember that this particular texture is not
the shape it claims.

## What is not here

**A material.** See above: a video lit as a texture in a scene is the material system's, and it wants
the same three views this binds.

**Colour management beyond BT.601/709.** Ten-bit and BT.2020 belong with a wider pixel format than
`Vixen.Video` has, and are absent rather than present and wrong.
