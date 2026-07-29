# 11 — Video Playback

A video on the screen: demuxed, decoded, uploaded as three planes, converted in the sampler.

```bash
dotnet run --project Samples/11-VideoPlayback
```

`--vixen-frames N` stops after N frames, which is how CI proves the whole path starts, presents and
stops without a validation error.

## What it is for

[Vixen.Video](../../Core/Vixen.Video/README.md)'s own suite asserts the container, the codec, the
clock and the upload calls — 144 tests of them — and not one of them puts a picture in front of a
person. The half that only a running frame exercises is here:

- the three planes reaching the GPU in the right order, at their own sizes;
- the six coefficients matching the shader that consumes them;
- the clock choosing frames at the rate the file was written at;
- the version check meaning a 25 fps video costs one upload in several frames rather than one per
  frame.

The pipeline, the descriptor set and the push block used to be in this file — which meant a video
could only ever be the whole screen, because what it drew was a full-screen triangle. They are now
[Vixen.Video.Rendering](../../Core/Vixen.Video.Rendering/README.md)'s, and what is left here is two
calls that say *where*:

```csharp
renderer.Begin();
renderer.Record(commands, VideoDraw.From(texture, VideoFit.Place(VideoScaling.Contain, player, whole)), surface);
renderer.Record(commands, VideoDraw.From(texture, VideoFit.Place(VideoScaling.Cover, player, corner)), surface);
```

The second one is the point. The same texture is drawn again into a sixth of the width in the
bottom-right corner, cropped rather than letterboxed — so the two rectangles between them exercise
both halves of `VideoPlacement`: the one that moves the rectangle and the one that moves the texture
coordinates. A picture-in-picture that lines up with the main one is what says both are right.

[`Shaders/video.frag`](Shaders/video.frag) is still what a material does with three planes, and it is
still about twenty lines of arithmetic.

## It carries no content

The engine ships no video codec — that is the whole design of `Vixen.Video`, and `UncompressedVideoCodec`
is the one decoder it does ship — so a committed fixture would have to be uncompressed, which at any
size worth looking at is megabytes of binary in the repository. [`GeneratedVideo`](GeneratedVideo.cs)
writes a legal WebM in memory at start-up instead: a header, one `V_UNCOMPRESSED` track, and a
cluster per frame.

The picture is colour bars with a white column sweeping across them, and both halves are deliberate.
The bars are authored in RGB and encoded with the **forward** BT.709 limited-range transform — the
exact inverse of what the fragment shader does on the way back — so if either half of the arithmetic
is wrong the bars come out the wrong colours, and a green picture or a washed-out one says which
half. The sweep is what makes the clock visible: it crosses the screen in exactly three seconds, so a
dropped frame is a stutter and a wrong frame rate is the wrong speed. The bottom fifth is a
black-to-white ramp, which is where a range error shows.

`ColourRoundTripTests` asserts the same round trip numerically. This is the version a person can see.

## The sound is the clock

The video's own Opus track plays through the mixer, and the picture follows where the sound has
actually got to:

```csharp
VideoAudioCodecs.RegisterOpus();
MatroskaAudioStreamDecoder.TryOpen(demuxer, out var track);

sound = new StreamingSampleProvider(track, loop: true);
audio.Streams.Register(sound);
audio.Play(sound, new PlaybackSettings());

player.FollowAudio(sound);
```

`FollowAudio` reads the provider's position, which is frames *delivered to the mixer* — not frames
decoded. The decoder runs half a second ahead by design, and slaving to it puts the picture half a
second in front of the sound with every part of it looking correct.

A beep sounds on every second and the white column crosses a third of the screen in that time, so
the two can be checked against each other by eye and ear. **No sound card is not an error**: the
sample says so and falls back to the frame delta, which is what a video with no audio track gets
anyway.

## The picture and the sound get a demuxer each

They are in the same file, and a single demuxer can serve both — but only while nothing seeks. Both
sides here loop, and a loop is a seek: one reader with two things seeking it yanks the file back to
the start under whichever of them did not ask. Two demuxers over the same bytes cost one more
position and a few hundred kilobytes; sharing one costs correctness.

Share a demuxer for a cutscene played once, straight through. Give each track its own the moment
either side loops or scrubs.

## Three things worth knowing

**The upload happens before the render pass, not inside one.** Copying into a texture needs barriers
either side of it, and a barrier inside a pass is invalid on every API — the transitions a pass needs
are declared by its attachments. So `VideoTexture.Upload` records onto the frame's command list
first, and the graph's pass runs after it on the same list.

**The swapchain is `Bgra8UNorm` and not sRGB.** A decoded video's RGB is already gamma-encoded — that
is what the BT.709 transfer function is — so writing it to an sRGB target encodes it a second time
and shows as mid-tones that are far too bright. A renderer lighting the video as a texture in a scene
would want the opposite; a player showing it directly wants the bytes to arrive as they are.

~~**The letterboxing is in the shader rather than in a viewport.**~~ It was, and the argument was
about the wrong thing. Painting the bars black is correct for a player showing a video on nothing and
wrong the moment the video is drawn over anything — opaque black over whatever was behind. `VideoFit`
shrinks the rectangle instead, and the pass's own clear fills what is left, which is what a viewport
could not do and a discard should not have.

## Reading the summary it prints

```
Reached 24.83 s in 26.57 s: 249 frame(s) shown, 372 dropped, 0 stall(s);
sound 24.95 s, 0 stream and 0 device underrun(s).
```

Worth printing, because "it did not crash" is not "it played". The position against the wall clock is
the sync check — those two and the sound's own position should stay within a frame of each other, and
a position still at zero means the master clock never advanced.

**Dropped frames are not a fault here.** At 1440p with the validation layers on, the window renders
at around ten frames a second against 25 fps content, so of every two or three frames that fall due
one is shown and the rest are skipped. That is the design — late frames are dropped, never shown late
— and the count going up while the position keeps pace with the wall clock is what correct looks
like. `stalls` is the number that means something is wrong: it counts updates that found nothing
decoded.

## What it does not do yet

**No compositor and no scene.** This is the RHI, the video module and `VideoRenderer`, with nothing
between them — deliberately, exactly as `01-HelloTriangle` is the RHI and nothing at all.
[`VideoRenderFeature`](../../Core/Vixen.Video.Rendering/README.md) is what puts the same renderer
inside a `RenderSystem`, and `VideoSurfaceUploader` is what drives it from an ECS world; neither is
exercised here, and both are asserted headlessly in `Vixen.Video.Rendering.Tests`.

**No interface.** A video inside a UI panel is
[`Vixen.Video.Ui`](../../Core/Vixen.Video.Ui/README.md), which needs a document, a theme and a font
atlas — a second sample's worth of setup for a path whose wiring is asserted end to end over the null
backend in `VideoInUiTests`.

**No material.** A video lit as a texture on a mesh in a scene is `MaterialRenderFeature`'s and, once
it lands, Raven's. The three plane views and six coefficients this binds are exactly what such a
material would consume.
