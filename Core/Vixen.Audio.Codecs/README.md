# Vixen.Audio.Codecs

Ogg Vorbis and Opus behind `IAudioStreamDecoder`, so a five-minute track costs a megabyte instead of
fifty.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § Importers, which names
Ogg/Opus for streaming; the seam they plug into is
[Vixen.Audio](../Vixen.Audio/README.md)'s `IAudioStreamDecoder`.

```csharp
engine.PlayStream(new VorbisStreamDecoder("music/level1.ogg"), new PlaybackSettings { Loop = true });
engine.PlayStream(new OpusStreamDecoder("music/level2.opus"), new PlaybackSettings());
```

## Why this is a separate assembly

A game with no compressed audio should not carry a decoder, which is the whole reason
`IAudioStreamDecoder` is an interface and `PcmStreamDecoder` — the one implementation that needs no
codec — lives in `Vixen.Audio` instead. Referencing this assembly is what pulls the two packages in.

## Why both decoders are managed

A native `libvorbis` and `libopus` would decode faster. They would also mean shipping a binary for
every RID and a resolver like `OpenALLoader`'s, and the browser target has no answer for that at all.
NVorbis and Concentus publish under NativeAOT and run in WebAssembly, and a stereo stream costs a
fraction of a per-cent of a core — on the pump's own thread, which is the thread that is allowed to
take as long as it likes.

NVorbis is MIT. Concentus is BSD-3-Clause. Neither adds an obligation beyond attribution.

## The container is ours for Opus and theirs for Vorbis

A Vorbis stream only ever comes in an Ogg, so the library that decodes one reads the other. Opus is
not like that: the codec and the container come from different places, and Concentus takes a packet
and knows nothing about where it came from. So `OggReader` is here.

The format is small — a page is a fixed header, a table of segment lengths and the segments; a packet
is however many segments it takes until one is shorter than 255. That rule is the whole of the
framing, and it is why a packet whose length is a multiple of 255 is followed by a zero-length
segment rather than being a special case.

**The checksum is not verified.** It would catch a corrupt file, and a corrupt file is a content-build
problem rather than a runtime one — a game that has shipped is reading bytes it produced. Verifying
costs a pass over every byte of every page on a thread decoding audio in real time, for an error
nobody can act on.

## Two things about Opus specifically

**It always decodes at 48 kHz.** Opus can produce 8, 12, 16, 24 or 48, and 48 is the only rate at
which nothing is resampled on the way out. The `OpusHead`'s "input sample rate" is a note about what
was fed to the encoder and has no bearing on what comes out.

**The pre-skip is not optional.** Every Opus stream begins with priming samples the encoder needed and
the listener must not hear. A decoder that ignores them starts every track with a few milliseconds of
artefact.

**Seeking rewinds and decodes forward.** Doing it properly means bisecting the file on page granules
and then decoding a little before the target to let the decoder settle — a real amount of code for
something a game does at a loop point and almost nowhere else. Decoding forward is correct at every
position and costs a few milliseconds per second skipped.

**`FrameCount` is −1.** The length is the last page's granule, which is at the end of the file, and
finding it means seeking there and scanning back — on a stream that may not seek at all. A track's
length is something the content build knows and can put in the asset.

## Testing

The fixtures are real Ogg files, produced once with ffmpeg and checked in: one second of a 440 Hz
sine at an amplitude of 0.7, which is a signal whose every property is known before it is decoded. A
test that shelled out to an encoder would be a test of whichever machine it ran on.

Licensed under Apache-2.0.
