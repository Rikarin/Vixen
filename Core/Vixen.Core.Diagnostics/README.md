# Vixen.Core.Diagnostics

Observability as a foundation, not a retrofit. A profiler added in year three measures the wrong
things, because by then the code has been shaped around not being measured; logging added late is
always either too sparse or allocating.

## What is here

| | |
|---|---|
| `ProfilingKey` | An interned name for a timed scope. An `int` at run time; a readable name only when a report is written. |
| `Profiler`, `ProfilerSample` | Scoped CPU sampling into a per-thread ring. No lock, no allocation, no contention. |
| `TraceExporter` | Chrome `trace_event` JSON, which opens in `ui.perfetto.dev`. Plus a text summary for CI logs. |
| `RingBufferSink`, `LogRecord` | The always-on log ring behind `ILogger`, with per-category levels. |

## The profiler is meant to be left on

A profiler you have to enable is a profiler that is off when the bug happens. An inactive scope costs
one `volatile` read; an active one costs a timestamp pair and a ring write. `VIXEN_NO_PROFILER`
removes even that for a shipping build that wants the last nanosecond.

```csharp
static class RenderKeys {
    public static readonly ProfilingKey Culling = ProfilingKey.Register("Render.Culling");
}

using (Profiler.Begin(RenderKeys.Culling)) {
    // …
}
```

Rings overwrite rather than grow, so a thread that samples heavily and is never collected keeps its
most recent history. `Profiler.DroppedSampleCount` says how much went over the side — a truncated
trace that does not admit it is worse than no trace.

## Log through `[LoggerMessage]`, not the extension methods

ADR-008. The generated method checks the level and returns before touching its arguments, so a
disabled log line allocates nothing and boxes nothing:

```csharp
static partial class RenderLog {
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "Effect {EffectName} permutation {Key} fell back after {Ms} ms")]
    public static partial void EffectFallback(ILogger logger, string effectName, uint key, double ms);
}
```

Event ids are allocated per subsystem in [`docs/manual/log-events.md`](../../docs/manual/log-events.md),
so a number in a support ticket is greppable and stable across versions.

`RingBufferSink` is on in every build. The editor console reads it live and the crash reporter dumps
it, which is the point: the interesting moment is usually the thirty seconds before a crash, and
asking a player for a log file is asking for nothing.

## What is not here yet

**The sink stores formatted strings, not UTF-8 with structured fields intact**, which doc 13 asks
for. An enabled log line therefore allocates. The disabled path already does not, and the enabled
path is not in any hot loop — `[HotPath]` methods are barred from logging and increment counters
instead. Packing records into a byte ring is the optimisation to make when a profile asks for it;
doing it now would mean guessing at the field layout the editor console has not been written to want.

**The other sinks** — ZLogger file, console, platform (`logcat`/`OSLog`/`OutputDebugString`), remote,
`EventSource` — and **rate limiting on repeated events**. Each is small on its own and none can be
validated without the thing it feeds.

**GPU profiling and memory attribution** need the RHI and the allocators' reporting surface, so they
land with those.

**Perfetto protobuf export.** The JSON form every tool accepts is here. Protobuf is smaller and
streams, and is worth adding when trace size is a measured problem rather than an anticipated one —
picking up a protobuf dependency to save bytes nobody has counted is the wrong order.

Licensed under Apache-2.0.
