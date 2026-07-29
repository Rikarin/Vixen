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

## Two readers, one seeker

A video's picture and its sound are in one file, so they are read by one demuxer — two readers would
mean two file positions and a seek in one that the other knows nothing about. The demuxer is safe to
read from two threads, which is not incidental: the picture is decoded on the player's thread and the
sound on the audio pump's, so that is the ordinary way it is used.

**Seeking is the exception.** `SeekTo` moves the file, and the file is shared — so a seek issued for
the picture moves the sound with it and drops everything buffered for both. That is right when one
thing owns the playback and wrong the moment two do: a video looping on its own while its audio track
plays through the same reader yanks the file back to the start under the audio decoder, over and
over.

So share a demuxer when nothing seeks — a cutscene played once, straight through — and give each
track its own the moment either side seeks or loops. Two demuxers over the same file cost one handle
and a few hundred kilobytes of buffering, each skips the other's blocks where they lie, and each
seeks its own position.

## Audio is the master clock

The single design decision that matters in video playback, and the one everybody arrives at after
trying the other two. The ear resolves a few cents of pitch and forty milliseconds of timing; the eye
resolves neither. So the sound plays untouched at its own rate and the picture is chosen to match:

```csharp
player.FollowAudio(soundProvider);
```

`FollowAudio` wants the *provider's* position — frames delivered to the mixer — and not the decoder's.
A decoder is filled ahead of playback by design, so its position is where the sound *will* be. This is
the single easiest way to get A/V sync visibly wrong while every part of it looks correct.

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

`MatroskaAudioStreamDecoder` presents the track as an ordinary `IAudioStreamDecoder`, which is what
the mixer already streams — so nothing in `Vixen.Audio` knows the bytes came out of a film:

```csharp
var video = new WebMVideoStreamDecoder("cutscenes/intro.webm");

if (MatroskaAudioStreamDecoder.TryOpen(video.Container, out var track)) {
    var provider = new StreamingSampleProvider(track);

    engine.Streams.Register(provider);
    engine.Play(provider, new PlaybackSettings());
    player.FollowAudio(provider);
}
```

`TryOpen` consults `AudioPacketDecoderRegistry`, which has the uncompressed PCM codecs in it by
default. Referencing [Vixen.Video.Codecs](../Vixen.Video.Codecs/README.md) and calling
`VideoAudioCodecs.RegisterOpus()` adds the codec WebM actually ships with — and pulls in Concentus,
which is why it is a separate assembly.

**Both halves must be drained.** Reading video and never reading audio makes the demuxer hold every
audio packet in the file. A track nobody asks for at all costs nothing — its blocks are skipped where
they lie.

## What is not here

**MP4.** Doc 08 names it and it is a genuinely larger job: a box parser, sample tables, chunk offsets
and per-codec configuration in `stsd`. It is additive — the seam is `IVideoStreamDecoder` — and WebM
is the container the free codecs actually ship in. `VideoImporter` claims the extension and fails with
that sentence rather than letting an `.mp4` become an unplayable byte blob.

**Ten-bit and BT.2020.** Both belong with a wider pixel format than this module has. They are absent
rather than present and wrong.

~~**A render feature.**~~ [Vixen.Video.Rendering](../Vixen.Video.Rendering/README.md) draws it, and
its `VideoRenderTarget` converts it into an ordinary colour texture — which is what a user interface's
image command, and anything else that binds one view, needs. This module still stops at handing over
the plane views, the sampler and the coefficients. What is still owed is a **material** — a video lit
as a texture on a mesh in a scene, which is `MaterialRenderFeature`'s and Raven's.

**Frame-accurate seeking.** `Seek` lands on the last cue at or before where it was asked for, which is
right for a loop point and wrong for a scrubber. A cue bisect and a decode-forward-to-the-frame pass
are what a scrubbable video needs, and neither is here.

**Choosing between audio tracks.** `FindTrack` takes the first, so a file with an English and a
Japanese track plays whichever the muxer wrote first. Subtitles are not modelled at all.

## Playing one from the content build

`VideoClip` is a record naming a video; `VideoPlayback` is what opens it.

```csharp
VideoAudioCodecs.RegisterOpus();

var clip = assets.Load<VideoClip>("cutscenes/intro").Value;

if (!VideoPlayback.CanPlay(clip)) {
    return;                                    // no decoder for it, and the menu is still on screen
}

using var playback = VideoPlayback.Open(
    clip,
    new DelegatedVideoContentSource(assets.Open, assets.CanOpen),
    new VideoPlaybackOptions { AutoPlay = true }
);
```

`IVideoContentSource` is a seam rather than a dependency: nothing in `Core/` references
`Vixen.Assets`, so a video module that called `AssetManager.Load` would be the first and would make
every game that plays a sting link the addressables system to do it. `FileVideoContentSource` needs
nothing at all, which is what a video left loose beside the executable wants — and a video is
streamed, so that is a reasonable thing for one to be.

⚠ **`CanPlay` answers about the codec id, not about the stream.** That is what makes it useful — a
title finds out it has no VP9 decoder while it is drawing a menu rather than when the cutscene was
due — and it is also its limit. A yes means "worth opening", not "will play".
