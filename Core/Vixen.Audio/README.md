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
| `AudioMixer` / `AudioBus` | the bus graph, its render order, and the block render |
| `AudioSend` | a copy of a bus's signal into another, so one reverb serves a whole level |
| `Voice` | one sound: a source, a rate conversion, a set of speaker gains |
| `Spatializer` | distance, cone, doppler and panning, as arithmetic |
| `IAudioEffect` | fourteen of them — see below |
| `Dsp/Fft` | radix-2 transform, for the convolution reverb and the analyser |
| `ISidechainEffect` | an effect that listens to one bus while processing another — how ducking is built |
| `IAudioSampleProvider` | a clip, a stream, a live push source, or anything a caller can produce samples from |
| `LiveSampleProvider` | frames pushed in as they arrive, for voice chat |
| `IAudioStreamDecoder` | the seam a codec plugs into — `PcmStreamDecoder` needs none |
| `AudioStreamPump` | the one thread that keeps every streaming voice fed |
| `IAudioBackend` | what a platform implements. `NullAudioBackend` is here; OpenAL and WebAudio are under `Platform/` |
| `Ecs/AudioSystem` | makes the mixer agree with the world, once a frame |
| `MixerAsset` / `MixerBuilder` | the whole mixer as a serialisable record graph |
| `MixerSnapshots` | named mix states, blended to over a duration |
| `AudioEvent` | a sound as a designer describes it: variants, variation, instance limits |
| `VariantSelector` | which take plays next, and why it is not the one that just played |
| `AudioEventAsset` | the same, as a file — the unit gameplay should actually be playing |
| `AudioParameterSheet` | named values a *sound* reads, with curves onto its gain, pitch and filters |
| `MixerParameters` | named values the *mix* reads, with curves onto buses, sends and effect knobs |
| `AudioCurve` | how a parameter's 0..1 maps onto decibels, semitones or hertz |
| `AudioListenerSet` | up to four pairs of ears, for split-screen |

## Inserts, sends, and the order it all runs in

An effect added with `AddEffect` is an **insert**: it processes the bus it is on, and everything
routed there gets all of it. A **send** is a copy of the bus's signal added into another one at some
level, and it is what makes a graph out of a tree.

```csharp
var reverb = engine.CreateBus("Reverb");
reverb.AddEffect(new ReverbEffect { Wet = 1f, Dry = 0f });

var ambience = engine.CreateBus("Ambience", world);
ambience.AddSend(reverb, 0.4f);        // post-fader: the fader takes the reverb with it
```

One reverb wanted by six buses at six different amounts is one reverb and six sends. As inserts it
would be six reverbs.

**Ducking is a sidechain**, which is the same idea pointing the other way:

```csharp
music.SetSidechain(dialogue);
music.AddEffect(new CompressorEffect { ThresholdDb = -40f, Ratio = 20f, AttackSeconds = 0f });
```

Now the music gets out of the way whenever anybody speaks, and no gameplay system that can produce
speech has to know the music bus exists.

**The render order is a topological sort**, not a depth sort. With only parent edges the graph is a
tree and "deepest first" is correct for free; a send is an edge that does not follow the tree, and a
sidechain is a third kind with the same requirement — the key has to have been rendered. `AddSend`
and `SetSidechain` refuse anything that would make a cycle, so the sort always succeeds.

**A bus's gain is applied in place**, which is load-bearing rather than incidental: it used to be
handed back for the parent sum to apply, which meant the buffer held a *pre*-fader signal — so a send
reading it, or a compressor keying off it, would have ignored the fader entirely.

## The effects

| | |
|---|---|
| `BiquadFilterEffect` | seven shapes, RBJ cookbook coefficients |
| `EqualizerEffect` | those biquads in series, as bands |
| `ReverbEffect` | Freeverb — eight combs and four allpasses per channel |
| `ConvolutionReverbEffect` | an actual room, from a recording of it |
| `DelayEffect` | echo with damped feedback and ping-pong |
| `ModulatedDelayEffect` | chorus, flanger and vibrato, which are one effect |
| `PhaserEffect` | swept all-pass stages — notches that are *not* a harmonic comb |
| `CompressorEffect` | feed-forward, soft knee, sidechain input |
| `GateEffect` | the same pointing downwards, with a hold — what an open microphone needs |
| `LimiterEffect` | look-ahead brickwall, on the master by default |
| `DistortionEffect` | four waveshaping curves |
| `BitCrusherEffect` | quantise and decimate, both sweepable |
| `PitchShiftEffect` | pitch without length, which `Pitch` cannot do |
| `SpectrumAnalyzerEffect` | passes through, publishes magnitudes |

A few of these are worth a sentence about why they are the way they are.

**Chorus, flanger and vibrato are one class.** A flanger is a 1–10 ms swept delay with feedback, a
chorus is a 15–40 ms one with more depth and several taps, and a vibrato is either with the dry
signal off. Three classes would be the same two hundred lines three times, with the interesting
differences buried in the duplication instead of visible as the defaults they are. What makes them
sound different is the delay length: below about 15 ms the copies interfere across the whole audible
range and the ear hears one moving resonance; above it, it hears separate near-unison voices.

**A phaser is not a flanger.** A flanger's notches are harmonics of one frequency, so it sounds
pitched; a phaser's come from all-pass sections and land wherever they are put, unrelated to each
other. That is the difference between a jet and a swirl.

**The pitch shifter is time-domain**, two taps crossfaded with a raised cosine. It warbles on
sustained tones and smears transients, and there is no grain length that avoids both. A phase
vocoder sounds better and needs an FFT pair per hop, a window of latency, and transient handling; the
transform is now here, so it is a real option and it is owed.

**The gate is the compressor with its gain computer mirrored**, plus a hold — and the hold is the
part that matters. Speech dips below any useful threshold between syllables, so a gate without one
slams shut in the gaps, which is the chattering that makes gated dialogue sound worse than ungated.
It closes to `RangeDb` rather than to silence for the same reason: a gate that shuts completely is
obvious, because the room tone it was hiding stops dead the moment somebody speaks. `IsOpen` reads
the detector rather than the gain, so it doubles as a voice-activity flag — what a name plate lights
up from, and what a client uses to decide whether to send a packet at all.

**The distortion aliases.** Bending a waveform makes harmonics, and harmonics above Nyquist fold back
as inharmonic tones. Oversampling by four is the fix; for a radio voice or an explosion nobody
notices, and this is not the effect to put a lead guitar through yet.

**The convolution reverb is the expensive one** — a second of stereo response is around a hundred
complex multiply-accumulates of transform size per block. Put it on one aux bus and send to it. It
also does not resample a response whose rate disagrees with the device: that is a room of the wrong
size, `IsRateMatched` says so, and the fix belongs in the content build where it is paid for once.

## Fades

`AudioEngine.FadeTo`, `FadeOutAndStop` and `AudioBus.FadeTo` move a gain over time. `Stop` on its own
fades over one block — enough not to click, and nothing like a musical fade-out.

**Decibels by default.** Loudness is roughly logarithmic, so a linear fade-out sounds like nothing
happening followed by the sound falling off a cliff at the end. `AudioFadeCurve.Linear` is there for
cross-fades between takes of the same material, where the sum is what matters.

**Stepped on game time, in `Update(deltaSeconds)`.** A fade under a paused game stops and a fade
under slow motion slows down. `Update()` with no argument measures a wall clock instead, which is
the wrong answer for anything with a pause menu — the ECS integration passes the frame delta.

Sixty steps a second, each smoothed across an audio block by the ramp the voice already applies, is
indistinguishable from a per-sample envelope and keeps the audio thread free of anything needing a
clock.

## Distance dulls the sound

`SpatialSettings.AirAbsorption` puts a low-pass on a positioned voice that closes as it gets further
away — the reason a distant gunshot is a thump and a near one is a crack. One biquad per voice, and
only for the voices far enough away to need one.

The cutoff sweeps **logarithmically** from 20 kHz at the reference distance to
`AirAbsorptionCutoff` at the maximum: pitch is logarithmic and a filter sweep has to be too, or it
does not sound like moving away, it sounds like a switch. The `Spatializer` computes it, so it is
arithmetic a test can assert on rather than something only ears can check.

**Off by default**, because it compounds with the content — a clip recorded at distance, or authored
dull on purpose, would get dulled twice.

## When the pool is full

A `Play` that finds every voice busy **steals** the lowest-priority, quietest one. Priority is the
first key because it is what somebody set deliberately; audibility — gain times distance attenuation
times cone gain — is the tie-break, because among sounds nobody ranked, the one nobody can hear is
the one to lose. Nothing more important than the newcomer is ever taken, so a pool full of dialogue
refuses a footstep rather than making room for it.

**Higher priority survives, which is the opposite of Unity's convention** — there 0 is the most
important, inherited from a table where the number was a sort key, and it is a documented trap in
every project that uses it.

**The handoff happens on the audio thread.** A stolen voice is by definition one the audio thread may
be mid-render on, so the game thread only fills the pending fields and asks for the stop; the audio
thread picks the new source up at the point it would have marked the slot finished, which is the one
moment nothing is reading the render state. Swapping the source from the game thread would leave the
read cursor describing one provider's buffer and another's channel count — an index out of range, in
a driver callback.

## Voice chat

`LiveSampleProvider` is the push side of the mixer: the network thread writes decoded frames, the
mixer pulls them, and an empty ring is silence and a counter rather than the end of the voice.
Somebody who has stopped talking is not somebody who has left — a voice that ended on every late
packet would be rebuilt, with its bus and its spatialisation, several times a sentence.

**Effects are per bus, not per voice**, which decides how a session is wired. "Some players are
underwater" is two buses — one with a low-pass and a send to the underwater reverb, one without — and
each player's voice is routed to whichever matches where they are. That is one bus per *environment*
rather than per player, and it is both cheaper and how a mixer is meant to be used.

## The mixer as an asset

```csharp
var problems = engine.LoadMixer(asset);       // buses, effects, sends, sidechains
engine.Snapshots?.TransitionTo("Underwater", TimeSpan.FromSeconds(0.4));
```

`MixerAsset` is a serialisable record graph — buses with gains in decibels, sends, sidechains and
effects. Effect polymorphism is an interface with a `[DataContract]` name per implementation, so the
contract name is the YAML tag and nothing keeps a registration table in sync; `Vixen.Rendering`'s
compositor asset is the same arrangement. `IAudioEffectAsset.Create` is a method rather than a lookup
table, because constructing an effect from a name is reflection, and ADR-002 forbids that in runtime
code.

**No file format here.** The editor writes YAML, the content build bakes a chunk, and a shipping
runtime reads the chunk with no parser linked in. The same is true of `AudioEventAsset` below.

**A snapshot names only the buses it changes**, so "the player is underwater" is a two-line thing
rather than a copy of the whole mixer that goes stale the moment a bus is added. Transitions blend in
decibels and start from wherever things are — so interrupting one halfway does not jump, and a fader
moved by hand since the last transition is respected.

**Unknown names are diagnostics, not exceptions.** A mixer asset is content: a level whose ambience
bus lost its reverb send should still be playable while somebody works out why. `LoadMixer` returns
the problems.

## Events, and why gameplay should not play a clip

```csharp
var footsteps = engine.LoadEvent(asset, out var problems);

footsteps.Play(feet);        // and that is the whole of what gameplay knows
```

`engine.Play(clip, settings)` requires the caller to have already decided which of the five takes, at
what level and pitch, on which bus, how far it carries, how many copies may sound at once and which
one gives way when they do. Every one of those is a decision a sound designer will want back, usually
late and usually all at once — and every one of them is in C# where they cannot reach it.

An `AudioEvent` owns all of it. It plays through the ordinary front door: every path ends in
`AudioEngine.Play` with a `PlaybackSettings` it computed, so there is no second way into the mixer
and nothing here the mixer knows about. It is an authoring idea, and the mixer stays a thing that
renders voices.

**Variation is the cheapest quality there is.** A repeated footstep is heard as a bug within about
two steps. `VariantSelector` picks between takes, `GainVarianceDb` and `PitchVarianceSemitones` move
each play a little, and together they are a few dozen lines standing between "has audio" and "sounds
finished".

**Shuffle is the default and not plain random.** A bag plays every variant once before any plays
twice, so a run cannot happen and the rare take is actually heard. Five variants drawn ten times at
random will produce a back-to-back pair about nine times in ten. The one place a bag can still repeat
is its boundary — the last of one round and the first of the next are independent — so a fresh bag
that opens with the sound that just played swaps it away.

`Random` and `RandomNoRepeat` honour weights, which a bag cannot: a bag visits every entry once a
round, so "one usual sound and three rare ones" has to be a distribution rather than an order.
`Sequential` is for a sequence that means something — a three-part reload.

**`MaxInstances` is the limit that actually gets hit.** `PlaybackSettings.Priority` decides who loses
when the whole engine runs out of voices; this decides how much of the engine one event is allowed to
be, which is what stops forty simultaneous impacts being forty voices and a wall of level. `Oldest`
is the default because the oldest copy is furthest through its decay. `Newest` sounds strange until
the event is a held note, and then the copy a minute in is the one being listened to. `Quietest`
ranks by the same audibility a steal does, so distance counts and not just the fader. `None` refuses,
which is right when the sound's beginning is the point.

**The room check happens before the draw.** A refused play must not advance the shuffle bag, or a
busy event quietly skips variants and the guarantee that every one is heard is not one.

**Seeded, so a run is reproducible.** Which is what makes a test of variation possible at all —
otherwise every assertion about which take came out is a coin toss.

**Nothing is allocated by a play.** Variants, weights, the bag and the instance table are sized once
at construction. A footstep is the frame loop, and doc 00 forbids garbage there.

### From an entity

```csharp
world.Add(entity, AudioSource.Playing);
world.Add(entity, new AudioEventRef { Event = footsteps });
world.Add(entity, AudioSpatial.Default);      // supplies the position, and nothing else
```

`AudioEventRef` replaces `AudioClipRef` rather than joining it; an entity carrying both is a question
with one good answer, and `WithNone` makes that an archetype fact rather than a branch that runs
twice and starts two sounds.

**An `AudioSpatial` beside an event supplies where and how fast, and none of the rest.** Its own
rolloff and cone are not read: where a sound is belongs to the entity, how it attenuates belongs to
the sound. That split is what lets a designer change a rolloff without opening a scene.

**`AudioSource.Gain` and `Pitch` become trims**, multiplied into what the event chose, and
`AudioSource` carries `VoiceGainScale` and `VoicePitchScale` so the per-frame push can scale rather
than replace. Without them an event's two decibels of level variation would last exactly one frame
and every copy would snap to the same level — audible long before it is found.

## Parameters

```csharp
footsteps.Play(feet);                                  // an event, as before

var voice = engine.Play(microphone, settings);         // a player talking
engine.AttachParameters(voice, submersion);
engine.SetParameter(voice, "depth", 1f);               // and that player alone is underwater
```

A parameter is a named float, a range, and some curves saying what moving it does. Gameplay writes
the number; what the number *means* is an asset edit. That indirection is the same one events are
for, one level down.

**Two kinds, and they drive disjoint things.** `AudioParameterSheet` is attached to a *sound* and
drives that sound's gain, pitch and filters. `MixerParameters` is engine-wide and drives bus gains,
send levels and named knobs on inserts. They are separate types rather than one with a scope flag,
because there is no overlap at all in what each can reach — and because the per-sound one is the
only one that can say "this player is underwater and that one is not".

**Which is the thing a bus per environment could not do.** The old answer to voice chat was one bus
per environment and each player routed to whichever matched. That is right for two environments and
absurd for twenty, and it cannot express "half submerged". A per-voice low-pass driven by a parameter
can, and `Voice` now carries one — a second biquad, separate from the distance filter, because
distance is nobody's decision and this is a designer's.

**Curves output the target's own unit** — decibels for a gain, semitones for a pitch, hertz for a
cutoff — which is what makes a straight line the right default. A linear ramp in decibels sounds
linear; a linear ramp in amplitude does not.

**Gains and pitches add where two parameters meet; cutoffs take the extreme.** Decibels and semitones
are already logarithmic, so adding them multiplies what they describe. Hertz are not: a sound both
underwater and behind a door is muffled by whichever is muffling it more, and there is no sense in
which two cutoffs combine.

**`SeekSeconds` is what stops it clicking.** A parameter driven by a gameplay boolean crosses its
whole range in one frame, and a filter cutoff crossing two octaves in one frame is a click. What
gameplay sets is a target; the value moves towards it at a rate.

**Everything is evaluated on the game thread**, once a frame in `Update`, and what reaches the audio
thread is four floats a voice was already reading. No curve, no name lookup and no allocation goes
anywhere near a device callback.

**A driven effect knob is set outright**, unlike a bus gain or a send, which are multipliers on top
of what the mix already has. The unit is the effect's own — hertz, a ratio, seconds — and there is no
rule for combining two of them that is right for all three, so two parameters driving one knob is not
a thing to do: the last one declared wins.

**Effect knobs are reached by a switch each effect writes**, not by reflection — ADR-002 forbids that
in runtime code and it does not survive trimming. The match is case-sensitive, deliberately: matching
loosely means lowering the name on every call, which is a string allocation per driven property per
frame. The cost of exactness is a typo, and a typo is reported when the automation resolves rather
than being silently ignored for the life of the project.

**A snapshot is still the right answer for a named state.** `MixerSnapshots.TransitionTo("Underwater",
0.4s)` is a destination arrived at; a parameter is a dial held at a position. "The underwater mix" is
the first and "this much rain" is the second, and neither replaces the other.

## Virtual voices

```csharp
new AudioEngineOptions { VoiceCapacity = 256, AudibleVoices = 32 }
```

Two numbers instead of one: how many sounds may be *playing*, and how many of those may be
*rendering*. Every frame the engine ranks by priority and then audibility, and the ones that do not
make the cut keep advancing through their sources while producing nothing.

**Which is a different answer from stealing, and a better one where it applies.** A stolen sound is
gone — its handle stops naming anything. A virtual one comes back at the right place, so the
three-minute looping ambience a player walked away from is where it should be when they walk back
rather than restarting or never returning. A sound only actually dies once all 256 slots are busy.

**It works by clearing the target gains**, exactly as a stopping voice does, so a demoted voice fades
out over one block and a promoted one fades in over one — no click at either end and no special case
in the mixing loop. Once it is fully silent the loop stops accumulating and only the source read
remains, which is the whole cost of being virtual.

**Paused voices are left out of the ranking.** They render nothing already, so counting them against
the budget would silence something audible on behalf of something that is not.

Off by default: `AudibleVoices` at zero means every voice is real and there is no ranking pass at all.

## Several pairs of ears

`AudioListenerSet` holds up to four listeners with weights, which is split-screen — four players at
four places, and one stereo output that has to represent all of them. There is no correct answer to
that, so what matters is picking a wrong one nobody notices.

**The direction blends and the level does not.** Speaker gains are summed across listeners in
proportion to how well each hears the sound, then scaled so the total is the loudest listener's
alone.

**Summing outright was rejected**: two players standing together beside a generator would hear it
twice as loud as one player standing there, and every sound in the level would get louder as the
party gathered.

**And so was nearest-wins.** It has the level right and the pan flips the instant a sound crosses the
midpoint between two players, which is audible. The blend is continuous across that line. What it
does not fix is that near the midpoint the pan is dominated by which listener is nearer, so a sound
crossing it can appear to move the wrong way — that is inherent to representing two places with two
speakers, and continuity is worth more than it costs.

Distance, doppler and the absorption cutoff come from the best listener rather than being blended:
they are properties of one path from the sound to one pair of ears, and the average of two doppler
shifts is a pitch neither listener would hear.

A set of one behaves exactly as the single listener always did, and that is the path almost every
game takes.

## The master

Ends in a `LimiterEffect` by default, and then a clamp. The limiter is the level control: a
sliding-window maximum over the look-ahead window means the gain applied to a sample was decided by a
window containing that sample, so the ceiling is a guarantee rather than a tendency, and the step
that brings the gain down lands during the quiet part before the transient that caused it.

The clamp behind it is a guard — a NaN out of a misbehaving effect, or an overshoot nothing caught.
A 16-bit backend would wrap a sample above one round to the opposite rail, which is the loudest click
a machine can make.

Turn the limiter off with `AudioEngineOptions.MasterLimiter` to balance levels without a safety net
underneath, which is a real thing to want: a limiter hides the problem you are trying to hear.

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

**Built-in parameters.** Distance, direction, elevation and speed as parameters a curve can be drawn
against, without gameplay setting them. `Spatializer` already computes all four; what is missing is
the plumbing that feeds them into a sheet each frame, which is why they are the obvious next thing
rather than a project.

**A loudness meter.** EBU R128 / LUFS, which is what console certification measures against.

**Per-voice sends.** Sends are per bus, so every source on a bus shares one send amount. For a room's
reverb that is right; for a reverb amount that tracks how far into the room each emitter is, it is
not.

**Occlusion and reverb zones**, both of which want physics — the geometry between a source and the
listener is a raycast, and there is nothing to cast against yet.

**A surround panner.** Beyond two channels a sound is placed in the first two and the rest are
silent. Silence in the surrounds is wrong in a way somebody will notice and describe; a quiet, wrong
smear across five speakers is wrong in a way they will not.

**A windowed-sinc resampler.** The rate conversion is linear interpolation, which aliases when
pitching up hard. The content build resamples clips to the rate they will be played at, so the common
ratio is exactly one and the interpolator is bypassed by the arithmetic itself — but a pitched-up
sound effect is audibly cheap.

**Oversampling for the distortion**, and a phase-vocoder pitch shifter — both now cheap to add, since
the transform they want is in `Dsp/Fft`.

**A real-input FFT.** Audio is real, so half the transform's input is zeroes and half its output is
the mirror of the rest. A real-input transform is twice as fast for the same answer, and it doubles
the index arithmetic — which is where a transform goes quietly wrong, so it is not taken until there
is a profile that asks for it.

**Codecs.** `IAudioStreamDecoder` is the seam and `PcmStreamDecoder` is the implementation that needs
none, so the streaming path works today at the cost of disk. Ogg or Opus is what makes a five-minute
track cost a megabyte instead of fifty, and it belongs with the content pipeline's half of Phase 8.

**ADPCM**, which [doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md) lists for effects.

**An HRTF panner.** The panning is amplitude panning, which has a left and a right and no front and
back — something behind you sounds like something in front of you. An HRTF is a pair of convolutions
per voice with a filter set that has to be shipped, and it is only correct on headphones. It plugs in
behind the same `Spatializer.Evaluate` call.

Licensed under Apache-2.0.
