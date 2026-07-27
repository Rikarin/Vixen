# Vixen.Audio

Engine audio data. Today that is one type: `AudioClip`, the decoded buffer the content pipeline
produces and a backend will submit.

Spec: [docs/plan/02](../../docs/plan/02-repository-layout.md) § Core, and
[docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § Importers for what fills it.

```csharp
var clip = artifacts.Read<AudioClip>(id);
clip.SampleRate;        // 48000
clip.Channels;          // 1 — positioned in the world
clip.Duration;          // 00:00:01.4
clip.AsInt16();         // the interleaved samples, no copy
```

## Why the assembly exists before the backend does

[Doc 02](../../docs/plan/02-repository-layout.md) puts `Vixen.Audio` in Core with
`Vixen.Audio.Backend.OpenAL` and `Vixen.Audio.Backend.WebAudio` in Platform, and
[doc 14](../../docs/plan/14-roadmap.md) schedules all of that for Phase 6. What Phase 3 needs is the
half a *content build* touches: something for `AudioImporter` to write and the object database to
store.

Putting that type anywhere else — in the importer, in `Vixen.Assets` — would mean moving it when the
backend arrives, and every artefact built before the move would name a type that no longer exists.
The name is in the chunk; a rename is a re-import of every audio asset in every project.

## Interleaved, and bytes

**Interleaved and not planar**, because that is what a device's buffer submission takes. Planar is
better for a mixer that processes channels independently and worse for the far more common job of
handing bytes to a driver, and the mixer can deinterleave the one clip it is working on.

**`byte[]` and not `short[]` or `float[]`.** The sample format is a value on the clip, not a fact
about the type, so a single array is the only representation that does not make `AudioClip` generic —
and a generic `[DataContract]` is a build error in this engine, for reasons
[Vixen.Core.Serialization](../Vixen.Core.Serialization/README.md) sets out. `AsInt16()` and
`AsFloat32()` reinterpret without copying, and return **empty** for the other format rather than
converting: a caller that asked for the wrong one should find that out where it asked.

Little-endian, like everything the serializer writes, so a clip built on one machine loads on another.

## Two formats

`Int16` and `Float32`, and no more. These are the two every audio API on every platform accepts
without a conversion pass. Twenty-four-bit, 8-bit and ADPCM exist in *files* and not in device
buffers; the importer converts on the way in, which is the one place that cost is paid once instead of
on every play.

## Still to come

**Streaming.** A clip is entirely in memory. Doc 08 wants Ogg or Opus kept compressed for music and
decoded as it plays, which needs a decoder in the *runtime* — not just at import — and a clip that is
a handle onto a stream rather than a buffer. That is a second type, and it is what stops a
five-minute track costing fifty megabytes of RAM.

**ADPCM**, which doc 08 lists for effects. It is four bits a sample and it decodes cheaply, but the
decode has to happen somewhere, and the somewhere is the mixer that does not exist yet.

**Everything that plays it.** Device enumeration, buses, 3D spatialisation, effects — Phase 6.

Licensed under Apache-2.0.
