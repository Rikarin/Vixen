# Vixen.Audio.Backend.WebAudio

The audio backend for a browser tab: an `AudioContext` with a scheduled queue of blocks the software
mixer fills.

Spec: [docs/plan/02](../../docs/plan/02-repository-layout.md) § Platform,
[docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 8, with the platform head itself in Phase 10.

```csharp
var backend = await WebAudioBackend.CreateAsync();
using var engine = AudioEngine.Create(backend);

// ...from a click handler, because of the autoplay policy:
((WebAudioDevice)engine.Device).Resume();
```

## Not in `Vixen.slnx`

This project targets `net10.0-browser`, which needs the `wasm-tools` workload even to *evaluate* —
and a solution that will not restore on a machine without it is a solution nobody can open. So it
sits on disk and out of the solution, exactly as `Vixen.Platform.Android` and `Vixen.Platform.iOS`
do, and for the same reason. `nuke Compile` does not build it and neither does CI today.

```bash
dotnet build Platform/Vixen.Audio.Backend.WebAudio
```

It is built by the web app head, which is Phase 10 work, and by anyone who has the workload.

## Like OpenAL, none of the platform's audio graph is used

No `PannerNode`, no `ConvolverNode`, no gain automation. [`Vixen.Audio`](../../Core/Vixen.Audio/README.md)
has already mixed, spatialised and reverberated in software, and what a browser gets is the same
finished interleaved signal a sound card gets. That is what makes a game sound the same on the web as
it does on a desktop — see that README for the argument.

## A scheduled queue, not an AudioWorklet

An `AudioWorklet` is the lower-latency answer and it needs three things a WebAssembly build cannot
rely on having: `SharedArrayBuffer`, the cross-origin isolation headers that unlock it, and .NET
threads. The worklet runs on the browser's audio thread and cannot call into a single-threaded
runtime at all.

So the mechanism is the one that works everywhere: render a block, wrap it in an `AudioBuffer`, and
`start()` an `AudioBufferSourceNode` at the time the previous block ends. A `setInterval` on the main
thread asks .NET for however many blocks are due to keep the queue ahead of the playhead.

The cost is latency — a block cannot be scheduled later than "now", so the queue has to run ahead.
Four 480-frame blocks is 40 ms, the same figure the OpenAL backend queues.

**Everything here runs on the browser's one thread**: the timer, the render and the game loop. There
is no audio thread to keep off, no lock to avoid and no memory barrier to place, which makes this the
simplest of the backends and the one with the least room to be subtly wrong. It also means the render
shares the frame's deadline — a frame that takes 40 ms is 40 ms in which the timer does not fire, and
the queue is what covers it.

## Two things the browser decides and you do not

**The sample rate.** `new AudioContext({ sampleRate })` is a request; Safari in particular refuses
rates it does not like rather than resampling. `IAudioDevice.Format` reports what was granted and the
mixer is prepared against *that*, so a clip at 48 kHz on a 44.1 kHz context is resampled per voice
like any other rate mismatch.

**When sound is allowed.** Every browser suspends an `AudioContext` created without a user gesture,
and `resume()` called from anywhere but an input handler is ignored. `WebAudioDevice.Resume()` is
what an application calls from its first click or key press. Until then the mixer runs, voices start
and finish, and the speakers are silent — the same shape as `NullAudioBackend`, so no code path is
special-cased for it.

There is also no device enumeration: a page cannot list the machine's outputs, because that is a
fingerprinting surface. `EnumerateDevices` returns the one entry the browser will give you.

## `vixen-audio.js`

The browser half. It has to be fetchable by URL at run time — `JSHost.ImportAsync` takes a path, not
a stream — so it is copied beside the assembly on build and packed as a content file. An application
that arranges its assets differently passes its own URL to `WebAudioBackend.CreateAsync`.

Blocks cross as their own **bytes**: `JSType.MemoryView` is defined for `byte`, `int` and `double`
and not for `float`, so JavaScript puts a `Float32Array` over the view. The alternative the
marshaller does offer — a `double[]` — would double the size of every block and convert every sample
twice, to reach an API that wants floats at the other end.

## Still to come

**An AudioWorklet path**, taken when the page *is* cross-origin isolated and threads are available.
It would cut the 40 ms queue to a couple of milliseconds, and it is a second `IAudioDevice` behind
the same backend rather than a change to this one.

**Tests.** There are none: driving a browser is what `Vixen.Platform.Web`'s harness will do, and that
is Phase 10. Everything about *what* is played is asserted in `Vixen.Audio.Tests` against a buffer,
which is the half that can be tested without a browser at all.

Licensed under Apache-2.0.
