# Log event ids

Every `[LoggerMessage]` in the engine carries a stable numeric `EventId`. This file is the register.

**Why this exists.** A number in a support ticket, a crash dump or a player's log is greppable and
survives the message text being reworded. Without a register, ids get picked at random, collide, and
stop meaning anything — which is the state most projects are in, because allocating them is a
five-minute job that is easy to skip until it is too late to fix.

## Rules

- **An id is permanent.** Once shipped it never changes meaning and is never reused, even if the log
  line it named is deleted. Retire the range entry instead, so an old log still decodes.
- **The message text may change freely.** The id is the contract; the wording is not.
- **The level may change.** A warning that turns out to be noise can become `Debug` without a new id.
- **Add the entry in the same commit as the log line.** A register updated later is a register that
  is wrong.
- **`0` means unassigned** and is what an un-annotated call site gets. Anything logging with event 0
  in a shipping build is a bug.

## Ranges

Allocated per subsystem, a thousand apiece, so a subsystem never has to come back for more and an id
identifies its origin on sight.

| Range | Subsystem | Status |
|---|---|---|
| 1 000 – 1 999 | `Vixen.Core.*` — services, assets of the foundation, allocators | reserved |
| 2 000 – 2 999 | `Vixen.Graphics`, backends | **in use** |
| 3 000 – 3 999 | `Vixen.Shaders`, Raven integration | reserved |
| 4 000 – 4 999 | `Vixen.Rendering`, `Vixen.Rendering.PostFx` | reserved |
| 5 000 – 5 999 | `Vixen.Ecs`, `Vixen.Engine` | reserved |
| 6 000 – 6 999 | `Vixen.Assets`, content pipeline | reserved |
| 7 000 – 7 999 | `Vixen.Ui.*` | **in use** |
| 8 000 – 8 999 | `Vixen.Platform.*` | reserved |
| 9 000 – 9 999 | `Vixen.Physics`, `Vixen.Audio`, `Vixen.Animation`, `Vixen.Input` | **in use** |
| 10 000 – 10 999 | `Vixen.Net.*` | reserved |
| 11 000 – 11 999 | `Vixen.Editor.*` | reserved |
| 12 000 – 12 999 | `Vixen.Raven` — the compiler's own diagnostics are `RVNxxxx`, not these | reserved |
| 13 000 – 13 999 | `Vixen.App` — the host and the app heads | **in use** |
| 14 000 – 14 999 | `Samples/*` — the samples, which use the same generated call sites the engine does | **in use** |
| 15 000 – 15 999 | `Vixen.Video` and its codecs | reserved |
| 16 000 – 16 999 | `Vixen.Xr` and its backends | **in use** |

## Allocated ids

### `Vixen.Graphics` and its backends

| Id | Level | Message | Since |
|---|---|---|---|
| 2001 | Warning | The Vulkan validation layers were asked for and are not installed | 0.1.0 |
| 2002 | Warning | The validation layer was found but would not load; the instance was created without it | 0.1.0 |

### `Vixen.Ui.Reactive` — the signal graph

| Id | Level | Message | Since |
|---|---|---|---|
| 7001 | Error | `The effect declared at {Origin} re-triggered itself {Runs} times in one flush and has been suspended.` | 0.1.0 |
| 7002 | Error | `The effect declared at {Origin} threw and has been suspended.` | 0.1.0 |
| 7003 | Warning | `An effect flush hit its budget of {Budget} runs with work still queued.` | 0.1.0 |

### `Vixen.Audio` and its backends

The 9 000 range is shared by four gameplay subsystems, so it is subdivided a hundred at a time:
physics 9 000, audio 9 100, animation 9 200, input 9 300. A hundred ids is more than any of them will
ever need and the block boundary makes the owner obvious from the number.

**Nothing here is logged from the audio thread.** A log call takes locks, formats strings and may
write to a file; a callback that did any of those would drop out. The render path counts and
`AudioEngine.Update` reports once a frame, which is also why several of these say "since the engine
started" rather than "just now".

| Id | Level | Message | Since |
|---|---|---|---|
| 9100 | Information | `Audio on {Backend}: {Device}, {SampleRate} Hz, {Channels} ch, {BufferFrames}-frame blocks.` | 0.1.0 |
| 9101 | Warning | `No audio device on {Backend} ({Reason})` — the mixer runs against nothing, so voices still start and finish | 0.1.0 |
| 9102 | Warning | `{Dropped} play requests were dropped: all {Capacity} voices were busy.` | 0.1.0 |
| 9103 | Warning | `Audio streaming fell behind {Underruns} times` — a track played silence while its decoder caught up | 0.1.0 |
| 9104 | Warning | `The audio device reported {Underruns} underruns.` | 0.1.0 |
| 9105 | Error | `The audio render threw and the block was silenced.` — an exception onto a driver's callback thread would take the process with it | 0.1.0 |
| 9110 | Warning | `OpenAL could not be loaded ({Reason}).` — the backend reports itself unavailable and selection moves on | 0.1.0 |
| 9111 | Error | `The OpenAL pump thread threw and has stopped; the process keeps running and is silent.` | 0.1.0 |

### `Vixen.App` — the host

| Id | Level | Message | Since |
|---|---|---|---|
| 13001 | Information | `Vixen {Variant} on {Platform}, {Workers} workers.` | 0.1.0 |
| 13002 | Warning | `No window: {Reason}` — the desktop platform was wanted and headless was used | 0.1.0 |
| 13003 | Warning | `LOOSE CONTENT — reading from {Path} instead of bundles.` (docs/plan/17 Q5b) | 0.1.0 |
| 13004 | Warning | `Unrecognised engine argument {Argument} — it was ignored.` | 0.1.0 |
| 13005 | Information | `Stopping after {Frames} frames.` | 0.1.0 |
| 13006 | Critical | `The frame loop threw and the application is stopping.` | 0.1.0 |
| 13007 | Information | `Content mounted from {Root}: {Addresses} addresses.` | 0.1.0 |
| 13008 | Information | `No content: {Reason}` — ordinary for a sample or a tool, and the line that makes "my asset was not found" a five-second diagnosis | 0.1.0 |
| 13009 | Warning | `LOOSE CONTENT — still reading from {Path} instead of bundles.` — repeated every 60 s, per docs/plan/17 Q5b | 0.1.0 |

Every other range is still reserved rather than allocated: the subsystems that will log have not been
written, and the ranges exist so that when they are, nobody has to invent a numbering scheme under
deadline.

<!--
    Format, once entries start arriving:

    | Id | Level | Message | Since |
    |---|---|---|---|
    | 2001 | Warning | `Effect {EffectName} permutation {Key} fell back after {Ms} ms` | 0.1.0 |
-->

### `Vixen.Xr` and its backends

**Nothing here is logged per frame.** A session's whole life produces a handful of lines — it started,
it changed state, the device went away — because the frame loop is paced by a compositor at ninety
hertz and a log call in it is a dropped frame. The two warnings that can repeat (`16003`, `16005`) are
runtime events that a healthy session does not produce at all.

| Id | Level | Message | Since |
|---|---|---|---|
| 16001 | Information | `OpenXR on {Runtime}: {System}, {Views} view(s) at {Width}×{Height}, {Samples} sample(s).` | 0.1.0 |
| 16002 | Information | `No OpenXR: {Reason}` — no loader, no device, or a runtime for another graphics API | 0.1.0 |
| 16003 | Warning | `The OpenXR runtime dropped {Events} event(s)` — a frame took long enough for the runtime to give up on the application hearing about a state change | 0.1.0 |
| 16004 | Information | `The OpenXR session moved to {State}.` | 0.1.0 |
| 16005 | Warning | `The active interaction profile changed` — the user plugged in a different controller, or rebound something | 0.1.0 |
| 16006 | Error | `The runtime reports the device is being lost.` — everything must be recreated | 0.1.0 |
| 16007 | Warning | `The runtime offers no swapchain format this engine knows; {Format} was requested and {Chosen} was taken instead.` | 0.1.0 |

### 14 000 — Samples

A sample uses the same generated call sites as the engine. It would be easy to argue that a demo may
call `LogInformation` directly — and then the one place a reader looks to learn how to write against
Vixen would show them the thing the analyzer forbids everywhere else.

| Id | Level | Message | Since |
|---|---|---|---|
| 14001 | Information | `Running on {Adapter} ({Kind}), presenting {Format} at {Width}×{Height} with {Images} images.` | 0.1.0 |
| 14002 | Error | `There is no window to present to.` — `Samples/01` needs a real display | 0.1.0 |
| 14003 | Error | `The device was lost.` — recreation arrives in Phase 2 | 0.1.0 |
| 14004 | Information | `The swapchain was out of date and has been rebuilt at {Width}×{Height}.` | 0.1.0 |
| 14005 | Information | `Generated {Width}×{Height} at {Rate} Hz, {Duration} s, {Megabytes} MB uncompressed.` — `Samples/11` writes its own WebM rather than carrying one | 0.1.0 |
| 14006 | Information | `Bound {Planes} plane(s) of a {Width}×{Height} picture.` — once per video, not per frame | 0.1.0 |
