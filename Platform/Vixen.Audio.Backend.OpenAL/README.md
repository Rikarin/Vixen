# Vixen.Audio.Backend.OpenAL

The audio backend for the three desktops, Android and iOS. OpenAL Soft, used as a sink for a mixer
Vixen already ran.

Spec: [docs/plan/02](../../docs/plan/02-repository-layout.md) § Platform,
[docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 8.

```csharp
using var backend = new OpenALBackend(logger);

foreach (var device in backend.EnumerateDevices()) {
    Console.WriteLine($"{device.Name}{(device.IsDefault ? " (default)" : "")}");
}

using var engine = AudioEngine.Create(backend, logger);
```

## What this backend does not do is the interesting part

OpenAL will spatialise, attenuate, apply a distance model and — with EFX — reverberate. **None of
that is used.** `AL_DISTANCE_MODEL` is set to `NONE`, the source is `SOURCE_RELATIVE` at the origin,
and its gain is left at one. What arrives here is a finished interleaved signal from
[`Vixen.Audio`](../../Core/Vixen.Audio/README.md)'s software mixer, and the whole backend is one
source with a queue of buffers and a thread that keeps it full.

That is not a waste of OpenAL. It is what makes OpenAL *replaceable*: a browser's audio API cannot be
driven the way OpenAL's can, so a backend that leaned on either one's mixer would have had to be
written twice and would still have sounded different on the two.

## A thread, because OpenAL has no callback

OpenAL is a pull API with nobody to do the pulling: you ask how many of the buffers queued on a
source have been played, refill those, and queue them again. Somebody has to ask, and it cannot be
the game thread — a frame that took 40 ms would be 40 ms of silence.

So there is one thread per device, at `AboveNormal` priority. Above normal because the mixer must
beat the game thread to the CPU or it drops out under load; not `Highest`, because taking priority
over the operating system's own work is how an audio thread makes a machine unresponsive rather than
making it sound good.

**Four buffers of 480 frames is 40 ms queued ahead** at 48 kHz. The queue is the entire safety
margin: the thread has four blocks' worth of time to be scheduled in before the source runs dry.
Larger blocks trade latency for margin; fewer blocks trade margin for nothing. When the source *does*
run dry, `Underruns` counts it — that number and `AudioStatistics.Load` are what a dropout
investigation starts from.

## Float where the driver takes it

`AL_EXT_FLOAT32` is present on every OpenAL Soft build, and where it is, the mixer's floats go
straight across with no conversion. Where it is not, they are quantised to signed 16-bit here — the
mixer has already clamped to ±1, so it is one multiply and cannot wrap.

**Mono or stereo, and nothing else.** Wider layouts need `AL_EXT_MCFORMATS`, which is not present
everywhere OpenAL is. A request for six channels is clamped to two and `Format` says so, because a
mixer that asked for 5.1 and quietly got stereo is worse than one that was told.

## OpenAL Soft travels with the game

Unlike Vulkan — where the loader and the driver come from the platform — there is no OpenAL on a
stock Windows or Linux install, and macOS's is a deprecated 1.1 shim Apple stopped shipping headers
for. The `Silk.NET.OpenAL.Soft.Native` package puts the implementation in
`runtimes/<rid>/native`, which is where the .NET host looks first, for win/linux/macOS on x64 and
arm64.

If it is not there, `IsAvailable` is false and says why in the log rather than throwing. A machine
with no audio — a container, a CI runner, a build agent — should still run the game;
`AudioEngine.Create` falls back to the null device, and the mixer keeps running so that voices still
start and finish and gameplay keyed off them still behaves.

## Testing

The tests here are about the three things a backend can get wrong that the mixer cannot: whether the
device opens, whether it pulls, and whether it lets go. Everything about *what* it plays is asserted
in `Vixen.Audio.Tests` against a buffer.

Every test that needs real hardware skips itself when there is none. A CI runner with no sound card is
the ordinary case, and a suite that goes red on it is a suite people learn to ignore.

## Still to come

**Device change notification.** `ALC_EXT_disconnect` says a device has gone away — a USB headset
unplugged mid-game — and nothing here listens for it yet, so the sound stops and does not come back
on the speakers.

**iOS and Android session handling.** Both platforms have an audio session that has to be configured
and that interrupts (a phone call, another app taking the route). `ILifecycle` is where that goes and
it arrives with those platform heads.

Licensed under Apache-2.0.
