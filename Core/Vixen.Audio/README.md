# Vixen.Audio

The audio subsystem. A software mixer with buses and effects, 3D spatialisation, streaming, and an
ECS integration — plus `AudioClip`, the decoded buffer the content pipeline produces.

Spec: [docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 8, with the clip half from
[docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § Importers.

```csharp
using var engine = AudioEngine.Create(new OpenALBackend(), logger);

var music = engine.CreateBus("Music");
music.Gain = 0.4f;
music.AddEffect(new BiquadFilterEffect { Kind = BiquadFilterKind.LowPass, Frequency = 800f });

engine.SetListener(new AudioListener { Position = camera.Position, Forward = camera.Forward });

engine.Play(footstep, new PlaybackSettings {
    IsSpatial = true,
    Spatial = new SpatialSettings { Position = feet, MinDistance = 2f }
});

engine.Update();   // once a frame, on the game thread
```

## Vixen mixes in software

This is the decision everything else follows from, and it was not the obvious one. OpenAL will
spatialise, attenuate and — with EFX — reverberate; WebAudio has a whole node graph with panners and
convolvers in it. Both were rejected, and a backend here receives finished interleaved frames and
does nothing but get them to the hardware.

**The same sound on every platform.** OpenAL Soft's panner, a browser's panner and a phone's mixer
disagree about attenuation curves, about what a cone does at its edge, and about how a stereo source
is placed. A game mixed on a desktop would have to be re-mixed for the web.

**It is testable.** [docs/plan/12](../../docs/plan/12-build-ci-and-testing.md) says audio correctness
is tested at buffer level, and that is only possible if there is a buffer to test. `AudioMixer.Render`
is a function from a world state to samples, and every claim in `Vixen.Audio.Tests` is an assertion
about numbers it returned — including the ones about doppler, cone edges, and where the 3 dB point of
a filter is.

**Effects and buses would otherwise be written twice**, once against EFX and once against WebAudio's
node graph, and neither maps onto the other.

The cost is CPU: a hundred voices at 48 kHz is a few per cent of one core. That is what every engine
that owns its mixer pays.

## The pieces

| | |
|---|---|
| `AudioEngine` | the front door: play, stop, buses, listener, statistics |
| `AudioMixer` / `AudioBus` | the bus tree and the block render |
| `Voice` | one sound: a source, a rate conversion, a set of speaker gains |
| `Spatializer` | distance, cone, doppler and panning, as arithmetic |
| `IAudioEffect` | `BiquadFilterEffect` (seven shapes) and `ReverbEffect` (Freeverb) |
| `IAudioSampleProvider` | a clip, a stream, or anything a caller can produce samples from |
| `IAudioStreamDecoder` | the seam a codec plugs into — `PcmStreamDecoder` needs none |
| `AudioStreamPump` | the one thread that keeps every streaming voice fed |
| `IAudioBackend` | what a platform implements. `NullAudioBackend` is here; OpenAL and WebAudio are under `Platform/` |
| `Ecs/AudioSystem` | makes the mixer agree with the world, once a frame |

## Two threads, and no lock between them

The game thread starts and stops sounds and moves them about; the device's thread renders. They meet
at three kinds of shared state, and each has its own lock-free mechanism:

- **A voice's life** — free, playing, paused, stopping, finished — is one `int` moved with a
  compare-and-swap. A stop racing a natural end resolves to whichever got there first, and the loser
  does nothing.
- **Scalar parameters** — gain, pitch, pan, a bus's volume — are written straight in. The CLR writes
  a `float` atomically; the worst case is a change taking effect one block later than it was made.
- **Whole structs** — a source's spatial settings, the listener — go through `Published<T>`, a
  sequence lock. Neither side waits, and a reader that catches a write in progress keeps the value it
  already had for one block.

**There is no command queue, and that is the point.** A hundred moving emitters at sixty frames a
second is six thousand enqueues, and `ConcurrentQueue` grows a segment every few hundred of them —
hundreds of kilobytes a second of garbage in the frame loop, which
[docs/plan/00](../../docs/plan/00-vision-and-principles.md) forbids in as many words.

**`AudioEngine.Update()` must be called once a frame.** It is where a finished voice goes back to the
pool, where a stream is handed back to the pump, and where the counters the audio thread wrote become
statistics and log lines. An engine that is never updated plays its first sixty-four sounds and then
goes quiet.

Nothing is logged from the audio thread. A log call takes locks, formats strings and may write to a
file, and a callback that did any of those would drop out — so the render path counts and `Update`
reports.

## The ECS integration is declarative

```csharp
var entity = world.Create();
world.Add(entity, AudioSource.Default with { Playback = AudioPlayback.Playing, Loop = true });
world.Add(entity, new AudioClipRef { Clip = alarm });
world.Add(entity, AudioSpatial.Default);
```

Game code writes what should be happening; `AudioSystem` makes it so, and writes
`AudioPlayback.Stopped` back when a sound runs out. So "is the alarm still going" is a component read
rather than a handle somebody had to keep across frames, and a sound survives a save and a reload.

It runs in `PostRender`, deliberately: `WorldTransform` is resolved in `PreRender`, so that is the
first phase in which a source's position is this frame's rather than last frame's — and audio has
nothing to say to the renderer, so doing it after submission overlaps it with the GPU.

`AudioSpatial`'s presence is the switch between a sound in the world and a sound in the room, so
"is this positional" is an archetype question and a UI click carries no cone settings it will never
use. Velocity is worked out from how far the entity moved unless `AutoVelocity` is turned off — the
alternative is every gameplay system that moves something also remembering to tell the audio.

## Buffer-level testing

`NullAudioBackend` is in this assembly rather than under `Platform/` because it is not a backend, it
is the absence of one — and it is what this assembly's own tests render through. It also ships: a
dedicated server has no sound card and still has to run the mixer, or a sound that was started never
finishes and whatever gameplay was waiting on it strands.

```csharp
var device = (NullAudioDevice)new NullAudioBackend().OpenDevice(options);
using var engine = new AudioEngine(device, new AudioEngineOptions());

engine.Play(clip);
var rendered = new float[64 * 2];
device.Render(rendered);        // now assert on the numbers
```

## `AudioClip`

**Interleaved and not planar**, because that is what a device's buffer submission takes. WebAudio's
`copyToChannel` is the exception, and it deinterleaves on the way in anyway.

**`byte[]` and not `short[]` or `float[]`.** The sample format is a value on the clip, not a fact
about the type, so a single array is the only representation that does not make `AudioClip` generic —
and a generic `[DataContract]` is a build error in this engine, for reasons
[Vixen.Core.Serialization](../Vixen.Core.Serialization/README.md) sets out. `AsInt16()` and
`AsFloat32()` reinterpret without copying, and return **empty** for the other format rather than
converting: a caller that asked for the wrong one should find that out where it asked.

**`Int16` and `Float32`, and no more.** These are the two every audio API on every platform accepts
without a conversion pass. Twenty-four-bit, 8-bit and ADPCM exist in *files* and not in device
buffers; the importer converts on the way in, which is the one place that cost is paid once instead
of on every play. Little-endian, like everything the serializer writes.

The conversion to float happens per block in `ClipSampleProvider` rather than once at load, because
converting at load would triple what a 16-bit clip costs for as long as it is resident, to save a
multiply on the few hundred frames that are actually playing.

## Still to come

**A surround panner.** Beyond two channels a sound is placed in the first two and the rest are
silent. Silence in the surrounds is wrong in a way somebody will notice and describe; a quiet, wrong
smear across five speakers is wrong in a way they will not.

**A windowed-sinc resampler.** The rate conversion is linear interpolation, which aliases when
pitching up hard. The content build resamples clips to the rate they will be played at, so the common
ratio is exactly one and the interpolator is bypassed by the arithmetic itself — but a pitched-up
sound effect is audibly cheap.

**Voice stealing.** A pool with nothing free drops the request and counts it. Dropping the quietest
voice instead is the usual answer and is owed.

**Codecs.** `IAudioStreamDecoder` is the seam and `PcmStreamDecoder` is the implementation that needs
none, so the streaming path works today at the cost of disk. Ogg or Opus is what makes a five-minute
track cost a megabyte instead of fifty, and it belongs with the content pipeline's half of Phase 8.

**ADPCM**, which [doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md) lists for effects.

**An HRTF panner.** The panning is amplitude panning, which has a left and a right and no front and
back — something behind you sounds like something in front of you. An HRTF is a pair of convolutions
per voice with a filter set that has to be shipped, and it is only correct on headphones. It plugs in
behind the same `Spatializer.Evaluate` call.

Licensed under Apache-2.0.
