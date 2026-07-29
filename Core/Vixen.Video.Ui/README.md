# Vixen.Video.Ui

Video inside a user interface. Two files, and that is the measure of whether the seam under them is
right.

```csharp
ui.SurfaceDrawers.Add(new VideoSurfaceDrawer(videoRenderer, uploader.TextureFor));

var view = document.Root.Add<VideoView>();
view.Player = playback.Player;
view.Fit = SurfaceFit.Contain;
```

## What had to exist first

Neither side was allowed to learn about the other. `Vixen.Ui` touches no device — which is what lets
every one of its tests run without one — so it cannot hold a texture; `Vixen.Video` holds no element
tree. So the draw list names an external picture the way it names a font: an index into a side list
of `object`, carried by `DrawCommandKind.Surface`, resolved by whoever is drawing.

| | |
|---|---|
| `DrawContext.Surface` | the primitive: a rectangle, a source, a tint. |
| `SurfaceView` | the control: a source, its displayed size, and one of three fits. |
| `IUiSurfaceDrawer` | the seam, in `Vixen.Ui.Renderer`. |
| `VideoSurfaceDrawer` | this: recognises a `VideoPlayer` or a `VideoTexture` and draws it. |
| `VideoView` | this: a `SurfaceView` that reads its size from the player. |

**Why an assembly of two files.** `Vixen.Video.Rendering` would have to reference a whole UI framework
— text shaping, an MSDF atlas, a styling engine — to hold these ten lines, and every game that draws
a cutscene and no interface would link it. `Vixen.Ui.Renderer` would have to reference a WebM demuxer
to hold them, and every game that draws a button would link that. Neither cost is worth avoiding one
project file.

## The renderer re-binds after every surface

⚠ Not politeness. A drawer binds its own pipeline, and Vulkan disturbs every descriptor set from the
first one two pipeline layouts disagree about — so an interface that carried on afterwards would
sample the video's planes through the glyph atlas's binding. That is undefined rather than an error,
and on the driver this was written against it happens to look correct. `UiRenderer` resets its bound
pipeline and its shared set unconditionally.

## No fitting happens here

`SurfaceView` has already decided the rectangle — that is what `SurfaceFit` is — and doing it twice
would either letterbox inside a letterbox or, worse, disagree. `Contain` shrinks the rectangle;
`Cover` pushes a clip and draws past it. Neither paints bars, because bars painted by a control are
opaque black over whatever the video was laid over.

## What is not here

**Controls.** A play button, a scrubber and a volume slider are `Vixen.Ui.Controls`' and a game's;
what a scrubber would need from the video module is frame-accurate seeking, which is
[owed](../Vixen.Video/README.md#what-is-not-here).

**Anything that advances a video.** A player is driven by `VideoSystem` or by whoever made it, and
may be on screen twice — an element that called `Update` would advance it once per place it appeared.
