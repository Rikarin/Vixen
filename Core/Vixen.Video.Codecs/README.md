# Vixen.Video.Codecs

Opus as a video container's audio track, behind [Vixen.Video](../Vixen.Video/README.md)'s
`IAudioPacketDecoder`.

```csharp
VideoAudioCodecs.RegisterOpus();

// From here, TryOpen handles the codec WebM actually ships with.
MatroskaAudioStreamDecoder.TryOpen(demuxer, out var track);
```

## Why it is an assembly rather than a class

The same argument `Vixen.Audio.Codecs` makes, one layer up. A game whose only video is an
uncompressed logo sting should not carry Concentus, so `Vixen.Video` registers nothing but the
uncompressed PCM pair and referencing *this* is what pulls a codec in. That is the whole reason for
the split; there is one type of substance in here.

Registration is a call rather than a side effect of linking, because a module that altered global
state merely by being referenced would behave differently under a trimmer — and "it works in Debug"
is the worst possible symptom for a codec.

## The adapter is thin because the seam was right

`Vixen.Audio.Codecs.OpusPacketDecoder` already takes a packet and produces frames, which is exactly
what a Matroska audio track needs. That is not luck: Opus's codec and its container come from
different places, which is *why* `IAudioPacketDecoder` exists in both modules in the same shape.

What this adds is the pre-skip.

## The pre-skip is not optional

Every Opus stream begins with samples the encoder needed and the listener must not hear. A decoder
that plays them starts every track with a few milliseconds of artefact — and, more measurably, with
more frames than the file claims to hold.

Matroska states the number twice, and they can disagree:

| | |
|---|---|
| `CodecDelay` on the track | what the muxer wrote, knowing what it put in the clusters |
| the `OpusHead`'s own field | what the encoder wrote, passed through untouched |

`CodecDelay` wins. A remux that trimmed the start updates it and *cannot* update the codec header,
so believing the header there clips the beginning of the sound.

**It is not re-armed on `Reset`.** The priming is a property of the start of the stream, and a seek
has already passed it: discarding it again would drop six milliseconds that should be heard and put
the position that far out of step with the container's own timestamps. The cost is that looping back
to zero plays the priming once, which is an artefact shorter than a frame of video.

## What is not here

**Vorbis.** `A_VORBIS` exists in WebM and is legacy — Opus replaced it for exactly this use — and
NVorbis is built around an Ogg stream rather than around loose packets, so the adapter that works for
Opus does not work for it. It would be a different piece of work for a codec nothing new is written
in.

**More than two channels.** Concentus decodes mono and stereo; Opus's channel mapping families do
more, and a 5.1 WebM is a thing that exists. It is refused with the channel count in the message
rather than silently downmixed.
