---
title: Recording a CPU trace
slug: core/recording-a-trace
kind: guide
area: Core
summary: The profiler is compiled into every build and switched off; one flag turns it on and a second writes the run out as a file that opens in Perfetto.
api: [T:Vixen.Core.Diagnostics.Profiler, T:Vixen.Core.Diagnostics.Profiler.Scope, T:Vixen.Core.Diagnostics.ProfilingKey, T:Vixen.Core.Diagnostics.ProfilerSample, T:Vixen.Core.Diagnostics.ProfilerThreadSamples, T:Vixen.Core.Diagnostics.TraceExporter, L:13036, L:13037]
tags: [profiling, diagnostics, tracing, perfetto, performance]
since: 0.1
status: preview
related: [rendering/timing-the-frame, rendering/diagnostic-overlays, engine/host-logging]
---

## What it is

Two flags and a file:

```
dotnet run -- --vixen-headless --vixen-frames 600 --vixen-trace ./run.json
```

The run records every `Profiler.Begin` scope it passes through, and writes them at shutdown as a
Chrome `trace_event` document. Drag it onto [ui.perfetto.dev](https://ui.perfetto.dev) or
`chrome://tracing`.

| Piece | What it is |
|---|---|
| `--vixen-profile` | Turns the CPU profiler on. Nothing else changes. |
| `--vixen-trace <path>` | Writes the run out at shutdown. **Implies `--vixen-profile`.** |
| `--vixen-run-for <seconds>` | Stops the loop after that much of the frame clock. The duration form of `--vixen-frames`. |
| `vixen trace record --duration 10s` | The two above and a build, from the CLI. Writes into `<project>/Traces/`. |
| `AppConfig.Profiling` / `AppConfig.TracePath` | The same two from `OnConfigure`, for a head that always records. |
| `Vixen.Core.Diagnostics.Profiler` | The recorder: `IsEnabled`, `Begin`, `BeginFrame`, `Collect`. |
| `Vixen.Core.Diagnostics.ProfilingKey` | An interned scope name. An `int` at run time. |
| `Vixen.Core.Diagnostics.Profiler.Scope` | What `Begin` returns; disposing it records the sample. |
| `Vixen.Core.Diagnostics.ProfilerSample` | One scope: key, depth, begin, duration, frame. |
| `Vixen.Core.Diagnostics.ProfilerThreadSamples` | One thread's samples, with its id and name. |
| `Vixen.Core.Diagnostics.TraceExporter` | The samples as JSON, or as a text table for a CI log. |
| `L:13036` | The trace was written — with the sample, frame and dropped counts. |
| `L:13037` | The trace was not written, and why. A warning at shutdown, not a failure. |

## What it is for

**A profiler you have to enable is a profiler that is off when the bug happens**, so the scopes are
compiled into every build and cost one volatile read while they are off. That is the design, and it
is what makes `--vixen-profile` a flag on an ordinary run rather than a special build.

⚠ **It had no switch.** `Profiler.IsEnabled` defaults to false, and until these flags existed nothing
in any game host ever set it — so a shipped game's `framegraph` overlay drew the words *profiler is
off* and there was nothing anywhere that answered it. `TraceExporter` was finished, correct and
constructed by nothing outside its own tests. This is the join, not the feature.

⚠ **And a second one under it**: `Profiler.BeginFrame` was called by the editor host and by nobody
else, so every sample a game recorded carried frame **zero**. A trace like that opens, looks
complete, and attributes a whole run to one frame — the shape of wrongness that survives review
because nothing about it is empty.

## Using it

### Measuring your own code

```csharp compile
using Vixen.Core.Diagnostics;

public static class HarvestKeys {
    public static readonly ProfilingKey Plan = ProfilingKey.Register("Game.Harvest.Plan");
}
```

and then, wherever the work is:

```csharp no-compile="A fragment: `Plan` is the caller's own work."
using (Profiler.Begin(HarvestKeys.Plan)) {
    Plan();
}
```

Register the key once in a static field. `Begin` on a disabled profiler returns a default scope and
touches nothing, so the `using` costs a branch.

### What a frame is

The host calls `Profiler.BeginFrame()` at the top of each frame while the profiler is on, and every
sample after that carries the new index. Perfetto shows it as the `frame` argument on each slice,
which is how a slice is attributed to the frame it happened in rather than to the second it landed
in.

### ⚠ Only one thing may collect

`Profiler.Collect()` **drains** the rings. Two collectors means each gets whatever the other did not,
and nothing anywhere says that half the run is missing.

While `--vixen-trace` is set the host owns the collection: it collects once a frame, appends to the
trace, and hands the same samples to the `framegraph` overlay through `FrameGraphOverlay.Capture`
with that panel's own `Collects` turned off. The chart is then one frame behind, which is the right
way round — the trace is complete and the panel is a frame stale.

If your own code calls `Collect`, it is now the collector, and a trace taken beside it will be
missing exactly what you took.

### What the log line tells you

```
info  Vixen.App  Wrote a Chrome trace to ./run.json: 48120 sample(s) over 600 frame(s), 0 dropped.
```

⚠ **Read the dropped count.** A ring overwrites rather than grows: `Profiler.CapacityPerThread`
samples per thread is the window, and anything a frame recorded beyond it is gone before the
collection. A trace with a large dropped count is missing its middle, and a total read off it is
wrong by however much.

There is also a ceiling on the trace itself — four million samples — because the document is built up
in the host's heap. Everything past it is counted as dropped by the same number, so a truncated
document says that it is truncated.

### A text summary instead of a file

`TraceExporter.Summarize` gives total, mean and call count per scope, worst first. That is what a CI
log wants, where nobody is going to open a trace viewer:

```csharp no-compile="A fragment: the caller decides where the text goes."
Console.WriteLine(TraceExporter.Summarize(Profiler.Collect()));
```

⚠ It collects, so it is subject to the paragraph above.

### From the command line

```
vixen trace record --duration 10s
```

builds for this machine the way `vixen run` does, runs it with `--vixen-trace` and
`--vixen-run-for`, and prints where the document went. `--duration` takes `10s`, `500ms`, `2m` or a
bare number of seconds; `-o` names the file; anything after `--` is passed to the game, and wins,
because a command line is applied in order.

⚠ **`--vixen-run-for` is spent on the frame clock, not on a wall clock.** It compares against
`GameTime.Total`, so with `--vixen-fixed-step` the run ends on the same frame every time — the only
form a test can assert — and without one it ends after the seconds an operator meant. A paused or
scaled clock slows it down, which is what a capture wants: ten seconds of simulation.

⚠ **The verb checks that the file is there before it names it.** The trace is written at shutdown, so
a run that crashed or was killed leaves none, and an instrument that printed a path either way would
be reporting success on the day it did not run.

⚠ **Chrome `trace_event` JSON, which is not the Perfetto protobuf doc 13 names**
([#25](https://github.com/Rikarin/Vixen/issues/25)). It opens in the same viewer.

### From the editor

Doc 13's other entry point. The Profiler panel's **Export Trace** writes whatever capture the panel
is holding into `<project>/Traces/<source>-<timestamp>.json` — the same folder and the same shape
`vixen trace record` uses, so the two kinds of recording sort as one list — and the status line under
the toolbar says where the file went.

⚠ **It is the only way to get a trace of the editor itself.** The verb above builds and runs a
*game*; the panel exports whichever source is selected, and one of those is the editor's own frame.
Which is what doc 20 means by "the profiler must be able to profile the editor".

⚠ **An empty capture is refused rather than written.** A trace document with no events in it opens
perfectly and reads as a process that did nothing, so the button is greyed until there is a capture
and says so if it is pressed with none — the same reason the verb refuses a zero duration.

## Examples

**A repeatable trace of a sample**, at a fixed step so the frames are comparable between two runs:

```
dotnet run -- --vixen-headless --vixen-frames 600 --vixen-fixed-step 0.016666 --vixen-trace ./before.json
```

Change the code, run it again into `after.json`, and open both. ⚠ A fixed step makes the frames
comparable and **does not** make the timings comparable to a real run's: the simulation is being told
each frame took the same time, and nothing about the machine reaches it.

**The panel rather than the file**, when the question is "what is slow right now":

```
dotnet run -- --vixen-overlays --vixen-overlay framegraph --vixen-profile
```

⚠ `--vixen-profile` is the part that was missing. Without it the panel draws *profiler is off*, which
is a different sentence from *no samples this frame* and the panel says which.

**Neither flag** is the shipping case, and it is what the whole design is for: the scopes stay in the
binary, every one of them costs a volatile read, and the run is the run a player gets.

## See also

- [Timing the frame on the GPU](../rendering/timing-the-frame.md) — the same idea one device along,
  where the scopes are the render graph's passes and the flag is `--vixen-gpu-profile`.
- [Diagnostic overlays and the console](../rendering/diagnostic-overlays.md) — the `framegraph` panel
  this shares its samples with.
- [Host logging and vixen.log.yaml](../engine/host-logging.md) — where `13036` and `13037` end up, and
  what it takes to see them.
- `Core/Vixen.Core.Diagnostics/README.md` — why the rings are per thread and never grow.
