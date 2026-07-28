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

It is also the shape a renderer's video material will take. `VideoTexture` hands over plane views, a
sampler and `VideoColourCoefficients`; [`Shaders/video.frag`](Shaders/video.frag) is what a material
does with them, and it is twenty lines.

## It carries no content

The engine ships no codec — that is the whole design of `Vixen.Video`, and `UncompressedVideoCodec`
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

## Three things worth knowing

**The upload happens before the render pass, not inside one.** Copying into a texture needs barriers
either side of it, and a barrier inside a pass is invalid on every API — the transitions a pass needs
are declared by its attachments. So `VideoTexture.Upload` records onto the frame's command list
first, and the graph's pass runs after it on the same list.

**The swapchain is `Bgra8UNorm` and not sRGB.** A decoded video's RGB is already gamma-encoded — that
is what the BT.709 transfer function is — so writing it to an sRGB target encodes it a second time
and shows as mid-tones that are far too bright. A renderer lighting the video as a texture in a scene
would want the opposite; a player showing it directly wants the bytes to arrive as they are.

**The letterboxing is in the shader rather than in a viewport.** A viewport would leave the bars
untouched by the clear and full of whatever the last frame put there. The same triangle draws the
picture and the black either side of it.

## What it does not do yet

No audio: the generated file has no audio track, and the one thing that would prove is the clock
following the sound rather than the frame delta. That wants a real WebM with an Opus track, and an
`IAudioPacketDecoder` over Concentus — a dozen lines in the assembly that already references it.

No render feature. What a material does with three planes is `Vixen.Rendering`'s business, and this
sample is deliberately the RHI and the video module with nothing between them, exactly as
`01-HelloTriangle` is the RHI and nothing at all.
