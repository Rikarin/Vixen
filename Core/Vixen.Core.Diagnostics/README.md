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
| `RingBufferSink`, `LogRecord` | The always-on log ring behind `ILogger`. |
| `ZLoggerFileSink` | Rolling JSON-line files, written asynchronously. The one a player attaches to a bug report. |
| `ConsoleSink` | The terminal: colourised, aligned, errors to `stderr`. |
| `PlatformSink` | `logcat` · the Apple unified log · the Linux journal · `OutputDebugString` · the browser console. |
| `RemoteSink` | JSON lines to the editor over an `IRemoteLogTransport`, on a thread of its own. |
| `EventSourceSink` | `dotnet-trace` / PerfView / ETW, as the `Vixen-Diagnostics-Log` provider. |
| `LogFilter`, `LogRateLimiter` | Per-category levels, and suppression of repeated events. |

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

## One filter, several sinks

Each sink is an `ILoggerProvider`; a host composes the ones its variant calls for and hands them all
the same `LogFilter`, so that "turn on verbose asset loading" is one call rather than five sinks to
walk and keep in step:

```csharp
var levels = new LogFilter { MinimumLevel = LogLevel.Information };
levels.SetCategoryLevel("Vixen.Assets", LogLevel.Debug);

var ring = new RingBufferSink(filter: levels);          // always
var console = new ConsoleSink(filter: levels);          // not in a shipping build
var file = new ZLoggerFileSink("/var/log/game", filter: levels);
```

Prefixes match longest-first, so `Vixen.Graphics` is a single switch for everything under it and a
rule naming one type still beats it whatever order the two were added in. Giving a sink its own
filter is equally valid and is how the file stays verbose while the console stays quiet.

## Rate limiting

One warning inside the frame loop is sixty lines a second: a file nobody can read, a console the
editor cannot keep up with, and real time spent formatting the sixty-thousandth copy of a message
whose first copy said everything.

```csharp
console.RateLimiter = new LogRateLimiter(TimeSpan.FromSeconds(1), burst: 4);
```

A message's identity is the pair **(category, event id)** — which is what ADR-008's id register is
for, and what makes the decision to drop a line cost no formatting. The first few per window get
through, the rest are counted, and the next one that does get through carries the count:
`… (repeated 4 812 times)`. `Critical` is never suppressed, and neither is the first report of an
event the tracking table had no room for: a limiter that loses a novel error is worse than none.

## What is not here yet

**The ring stores formatted strings, not UTF-8 with structured fields intact**, which doc 13 asks
for. This was deferred "until a profile asks"; the profile has now been taken, and it says not to do
it. `AllocationTests` measures the enabled line at **128 bytes** — an 88-byte `LogRecord` plus the
40-byte message — and the disabled line at **exactly zero**, with
`GC.GetAllocatedBytesForCurrentThread` rather than a stopwatch, because allocation is a property a
counter measures perfectly.

⚠ **The 128 is not this sink's to spend.** The floor belongs to `ILogger.Log<TState>`: the
`[LoggerMessage]` state is a struct reachable only through
`IReadOnlyList<KeyValuePair<string, object?>>`, so reading the structured fields boxes the state once
and every value-type argument again — 56 B/line measured, for one `int` — and the formatter's
contract is to return a `string`, 40 B/line on its own. Encoding that string into a byte ring copies
it rather than un-allocating it. So doc 13's "near-zero when enabled (the sink writes UTF-8
directly)" is not reachable from behind `[LoggerMessage]` at any implementation quality; it needs
ZLogger's shape, which is ADR-008's decision to revisit and not this sink's.

⚠ **Packing would also spend properties the current shape has for free.** One reference per slot
means a wrap replaces exactly one whole record and no reader ever sees half of one — whereas a byte
ring that wraps mid-record leaves a fragment, and a fragment cut inside a multi-byte UTF-8 sequence
is a decode error rather than a truncation. `Exception` is an object reference and cannot be packed
without formatting it at write time, which costs more than it saves. And the editor console, which
has since been written, collapses rows on `(Level, Category, Message)` and searches the message as
text, so it would decode on read what the ring encoded on write.

⚠ **One sentence that used to stand here was false and has been removed**: that the enabled path is
never in a hot loop because `[HotPath]` methods are barred from logging. `[HotPath]` is applied to no
method in the tree and no analyzer enforces it. Logging *does* happen in per-frame code — the UI
builder's diagnostic drain, the streaming residency report, the render-graph frame lint — and what
actually keeps it affordable is that every one of those sites is individually latched, watermarked,
de-duplicated or interval-throttled, so steady state is a compare and not a record. Two sites are
owed a latch and are call-site bugs rather than ring-format ones: `WebGpuDevice.WaitIdle` logs
unlatched on a condition that is a permanent property of the surface, and `EditorFrames` builds a
`string.Join` in front of its change check rather than behind it.

The *file* sink does keep its fields — that is why it is ZLogger's and not ours — so `{Ms}` in a log
event is a number in a field called `Ms` in the `.jsonl`, not a fragment of a sentence.

**The remote sink has no protocol under it.** It formats, batches and hands bytes to an
`IRemoteLogTransport`; discovery, pairing and framing belong to doc 13's remote inspector, which is
not written. This assembly sits below the networking stack and stays there — a foundation library
that opened its own socket would drag a transport into every build that logs.

**Apple gets `syslog(3)`, not `os_log` proper.** The unified logging system captures it, so the lines
do appear in `log stream` and Console.app; what is lost is the subsystem/category pairing.
`_os_log_impl` takes a compiled format descriptor that cannot be produced from managed code, so
reaching it needs a native shim in `Vixen.Platform.Native` — which is a reason for a shim, not a
reason for the sink not to exist.

**GPU profiling and memory attribution** need the RHI and the allocators' reporting surface, so they
land with those — and the first half now has. `Vixen.Graphics` has timestamp queries
(`CreateQueryPool`, `ICommandList.WriteTimestamp`, `TryResolveQueries`) and
`Vixen.Editor.Profiler`'s `GpuProfiler` is the thing above them. It is deliberately *not* here: a
GPU scope is written into a command list and resolved from a device, neither of which this assembly
is allowed to know about, and a timestamp's zero point is not comparable with `Stopwatch` anyway —
so a GPU timeline sits beside a CPU one rather than merging into these rings.

**Perfetto protobuf export.** The JSON form every tool accepts is here. Protobuf is smaller and
streams, and is worth adding when trace size is a measured problem rather than an anticipated one —
picking up a protobuf dependency to save bytes nobody has counted is the wrong order.

Licensed under Apache-2.0.
