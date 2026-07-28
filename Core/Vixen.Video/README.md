# Vixen.Video

Video playback: a managed WebM demuxer, a codec seam with nothing linked behind it, a player that
decodes ahead of a clock the sound can drive, and the planes on the GPU with the coefficients a
shader converts them by.

Spec: [docs/plan/06](../../docs/plan/06-rendering-pipeline.md) § Other renderables, which lists video
textures, and [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § Importers, which
names mp4 and webm.

```csharp
var player = new VideoPlayer(new WebMVideoStreamDecoder("cutscenes/intro.webm"));
var texture = new VideoTexture(device, "intro");

player.Play();

// once a frame
player.Update(time.Delta);
texture.Upload(commands, player);
```

## The shape, and why it is this shape

Four things, and each of them can be used without the ones above it.

| | |
|---|---|
| `MatroskaDemuxer` | a file → packets. Knows nothing about codecs. |
| `IVideoCodec` | packets → pictures. Knows nothing about files. |
| `IVideoStreamDecoder` | the two joined, plus position and seeking. What a player holds. |
| `VideoPlayer` | frames ahead of a clock, and which one is current. |

The split between the first two is the one that pays. A codec that also parsed containers would have
to be rewritten for MP4; a container that also decoded would have to be rewritten for AV1. As it is,
an `Mp4Demuxer` arriving later reuses every codec anybody has written, and a VP9 codec arriving later
plays out of both containers on the day it lands.

## Nothing is linked by default

`UncompressedVideoCodec` is to video exactly what `PcmStreamDecoder` is to audio: the implementation
that needs no codec at all. It handles Matroska's `V_UNCOMPRESSED` — I420, YV12, Y800, I422, I444 and
BGRA — which is what a short sting, a rendered logo or a test fixture can afford to be, and it is what
the whole module is developed and tested against.

A game that needs VP9 or AV1 registers a codec:

```csharp
VideoCodecRegistry.Register(new MyVp9CodecFactory());
```

and nothing above the seam changes. The registry is deliberately last-registered-wins, so an
application can override a default without being able to unregister one.

**Why not ship a decoder.** A managed VP9 is thousands of hours and would not hit frame rate; a native
one means a binary per RID, a resolver like `OpenALLoader`'s, and no answer at all for the browser
target. `docs/plan/14` lists video as cuttable precisely because that trade has no good answer inside
the engine — so the engine ships the seam and the one decoder that costs nothing.

## Audio is the master clock

The single design decision that matters in video playback, and the one everybody arrives at after
trying the other two. The ear resolves a few cents of pitch and forty milliseconds of timing; the eye
resolves neither. So the sound plays untouched at its own rate and the picture is chosen to match:

```csharp
player.Clock.Master = () => audioStream.Position.Seconds();
```

With no audio track the clock integrates the *game's* delta, not the wall clock — so a video in a
paused game pauses, and a video in a slow-motion game runs slowly. Both of those are what a cutscene
should do and neither is what a wall clock gives.

**Late frames are dropped, never shown late.** If three frames became due since the last update, the
third is shown and two are counted in `FramesDropped`. Showing them in sequence would put the picture
behind the sound and keep it there — the failure mode that never recovers on its own.

## Three textures, and the conversion happens in the sampler

A 4:2:0 frame is a full-size luma plane and two quarter-size chroma planes. `VideoTexture` uploads
them as three `R8` textures exactly as the decoder produced them and hands out the six coefficients a
shader multiplies by. Converting to RGBA on the way would mean touching four times as many bytes on
the CPU and uploading four times as many, to do on a core what the sampler does for nothing —
`VideoColourConversion` exists for the cases that genuinely need it on the CPU (a thumbnail, a test,
a tool) and is not the playback path.

`VideoColourCoefficients.For` is the one place the BT.601/BT.709 and limited/full-range arithmetic
lives, so the CPU path and the shader cannot disagree.

## The audio track is in the same file

A video's sound is interleaved with its picture in one segment, so it has to be read by one demuxer —
two readers would mean two file positions and a seek in one that the other knows nothing about.
`MatroskaAudioStreamDecoder` shares the video decoder's demuxer and presents the track as an ordinary
`IAudioStreamDecoder`, which is what the mixer already streams:

```csharp
var video = new WebMVideoStreamDecoder("cutscenes/intro.webm");

if (MatroskaAudioStreamDecoder.TryOpen(video.Container, out var sound)) {
    engine.PlayStream(sound, new PlaybackSettings());
    player.Clock.Master = () => sound.Format.DurationOf(sound.Position);
}
```

`TryOpen` handles the uncompressed PCM codecs. An Opus track needs an `IAudioPacketDecoder` over
Concentus, which is a dozen lines in the assembly that already references it.

**Both halves must be drained.** Reading video and never reading audio makes the demuxer hold every
audio packet in the file. A track nobody asks for at all costs nothing — its blocks are skipped where
they lie.

## What is not here

**MP4.** Doc 08 names it and it is a genuinely larger job: a box parser, sample tables, chunk offsets
and per-codec configuration in `stsd`. It is additive — the seam is `IVideoStreamDecoder` — and WebM
is the container the free codecs actually ship in.

**Ten-bit and BT.2020.** Both belong with a wider pixel format than this module has. They are absent
rather than present and wrong.

**A render feature.** What a material does with three planes is the renderer's business.
`VideoTexture` stops at handing over the views, the sampler and the coefficients.
