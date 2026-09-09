# 13 — Diagnostics: Logging, Profiling, Debugging

Your brief: "Extensive logging, debugging, profiling must be in mind." The way to honour that is to
make observability a Phase-1 subsystem, not a Phase-8 one — retrofitted profilers measure the wrong
things, and retrofitted logging is always either too sparse or allocating.

## Logging

### API

`Microsoft.Extensions.Logging.Abstractions` interfaces with `[LoggerMessage]` source-generated
methods (ADR-008). No `string.Format`, no interpolation at the call site, no boxing.

```csharp
internal static partial class RenderLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "Effect {EffectName} permutation {Key} fell back to the default shader after {Ms} ms")]
    public static partial void EffectFallback(ILogger logger, string effectName, uint key, double ms);
}
```

- **Compile-time validated**: a mismatch between the message placeholders and the parameters is a build
  error.
- **Zero allocation** when the level is disabled (the generated method early-returns before touching
  arguments) and near-zero when enabled (the sink writes UTF-8 directly).
- **EventId ranges are allocated per subsystem** and recorded in `docs/manual/log-events.md`, so a log
  line's number is greppable and stable across versions. This is what makes support tractable and is
  almost always skipped.

### Sinks

| Sink | Purpose |
|---|---|
| `RingBufferSink` | last N (default 100 000) records in a lock-free ring, UTF-8 encoded, structured fields intact. **Always on, in every build.** The editor console and the crash reporter read it. |
| `ZLoggerFileSink` | rolling file, JSON lines, async, zero-alloc (ZLogger 2.5.10) |
| `ConsoleSink` | dev only; colourised, aligned |
| `PlatformSink` | `logcat` / `OSLog` / `console.log` / `OutputDebugString` — so platform tooling sees the log |
| `RemoteSink` | streams to the editor's console over the inspector protocol |
| `EventSourceSink` | `dotnet-trace` / PerfView / ETW integration |

### Discipline

- **Categories** are types (`ILogger<VulkanDevice>`), so filtering by subsystem is free.
- **Per-category level configuration** via `vixen.log.yaml`, so "turn on verbose asset loading without
  drowning in render spam" works. Built: `LogFilter` carries the rules (longest prefix wins) and
  `AppBuilder` hands *one* filter to every sink, so a rule is set once rather than per sink. The host
  reads `/app/vixen.log.yaml` and then `/data/vixen.log.yaml`, the machine's beating the shipped one,
  and `--vixen-log-level` beats the file's `minimumLevel` while the file's per-category rules always
  apply. ⚠ **The editor's live-editing panel over that filter is still owed** — the mechanism is
  reachable (`services.Logs.Filter`) and nothing in the editor surfaces it.
- **Rate limiting** on repeated identical events (`… (repeated 4 812 times)`), because one per-frame
  warning otherwise makes the log useless and costs real time.
- **No logging in the innermost loops.** ⚠ **This is a convention and not a rule**, and this bullet
  used to claim otherwise: `[HotPath]`-marked methods were said to be "analyzer-blocked from logging",
  and there is no such analyzer. `HotPathAttribute` exists (`Core/Vixen.Core/Annotations`) and is
  applied to nothing in the tree, so it blocks nothing either — its own summary says it is a contract
  for an *allocation* analyzer, which also does not exist. What is actually true is weaker and is why
  the cost has not bitten: logging does occur in per-frame code, and each such site is individually
  latched, watermarked, de-duplicated or interval-throttled, so its steady-state cost is a compare.
  `RingBufferSink`'s remarks carry the same correction.
- **Every `catch` either handles or logs with the exception object.** ⚠ **Also a convention and not a
  rule** — this bullet used to say "an analyzer flags silent catches" and no such analyzer exists.
  `Core/Vixen.Core.IO.Analyzers` is the precedent for building one: a diagnostic that
  `TreatWarningsAsErrors` turns into a build failure, with the legitimate exceptions turned off by
  name in `.editorconfig`, each carrying a written reason.

## Profiling

Three independent instruments, because CPU, GPU, and memory fail differently.

### CPU: scoped sampling

```csharp
using (Profiler.Begin(ProfilingKeys.Culling))
{
    // ...
}
```

- `ProfilingKey` is a pre-registered, interned id (a `readonly record struct` over an int) — no string
  work at runtime. Keys are declared in static classes per subsystem, mirroring Stride's
  `ProfilingKeys` pattern.
- Samples are written into a **per-thread ring of 16-byte records** (`key, timestampBegin/End, depth,
  frameIndex`). At ~5 ns per sample the instrumentation is affordable at high density, which is the
  whole point: a profiler you have to enable is a profiler that is off when the bug happens.
- **Always compiled in**, gated by a single `volatile bool` check. A `VIXEN_NO_PROFILER` build constant
  removes it entirely for shipping if a title wants the last nanosecond.
- **Every job is automatically sampled** by the job system, with its `TJob` type name as the key — so
  the frame graph is populated without any manual instrumentation.
- **Counters** alongside samples: draw calls, triangles, instances, culled objects, state changes,
  descriptor writes, entities, active behaviours, layout nodes measured, style recomputes, effects run,
  bundle reads, bytes uploaded. `System.Diagnostics.Metrics` so `dotnet-counters` works out of the box.

### GPU: timestamp queries

- A `QueryPool` per frame with begin/end timestamps around each render-graph pass, read back with the
  frame's fence (N frames late, labelled as such — a GPU profiler that pretends to be same-frame is
  lying).
- Pipeline-statistics queries (primitives, fragment invocations) where supported. **Not available on
  Apple** — MoltenVK does not support `VK_QUERY_TYPE_PIPELINE_STATISTICS` (ADR-011), so the profiler's
  statistics track is capability-gated and degrades to timestamps only on macOS/iOS. The editor's GPU
  view must render an explicit "unavailable on this backend" state rather than zeros, which read as
  "nothing happened" and mislead.
- **Debug markers** on every pass and draw batch via `VK_EXT_debug_utils` / D3D12 PIX events / `KHR_debug`,
  named from the render-graph pass names, so RenderDoc and PIX captures are self-documenting.
- **RenderDoc integration**: in-process API hookup (`renderdoc_app.h` via `[LibraryImport]`) so the
  editor has a "capture frame" button that opens RenderDoc on the result. Stride has
  `Stride.Graphics.RenderDocPlugin` for the same reason and it is one of its most useful features.

### Memory

- Managed: `GC.GetGCMemoryInfo`, allocation-rate counters, and a `GCHeapAllocationEventSource` listener
  in debug that attributes allocations to the frame phase (which is what makes the zero-allocation gates
  actionable, per [12](12-build-ci-and-testing.md)).
- Native: every `Vixen.Core.Memory` allocator reports live bytes, peak, and fragmentation by tag.
- GPU: per-heap usage from `VK_EXT_memory_budget` / D3D12 residency, plus per-resource-category
  attribution (textures / vertex / index / uniform / storage / render target).
- Assets: residency list with ref counts, sizes, and load times — the "why is my build 4 GB in RAM"
  answer.

### Trace export

The sample rings serialise to **Perfetto protobuf** (and Chrome `trace_event` JSON as a fallback), so
traces open in `ui.perfetto.dev` with no bespoke viewer. This is a deliberate choice over building a
custom trace format: Perfetto's UI is better than anything this project would build, it handles
multi-gigabyte traces, and it supports flow events for job dependencies and counter tracks for the
metrics. `vixen trace record --duration 10s` from the CLI, or the editor's capture button.

## Debugging

### Frame debugger

Editor-side ([11](11-editor.md)), engine support:

- The render graph is recorded per frame (passes, resources, barriers, draw calls with their pipeline,
  descriptor sets, and vertex/index ranges).
- Stepping to draw call N replays the frame's command stream up to N and presents the intermediate
  render target — so "what did the shadow pass actually write" is inspectable.
- Render-target inspection with channel isolation, exposure adjustment, and histogram.
- Per-draw shader source (Raven) with the resolved permutation, and the option to hot-edit and re-run.

### Debug rendering

`DebugDraw` — an immediate-mode API for lines, wire/solid boxes, spheres, capsules, cones, frustums,
arrows, text-in-world, and screen-space text, with a duration parameter (`DebugDraw.Line(a, b, Color,
duration: 2f)`). Batched into one draw call per primitive type per frame. Available in release builds
behind a flag, because production bugs need it.

### Remote inspector

A TCP/WebSocket protocol between a running build and the editor:

- Browse the live entity hierarchy; read and **write** component values; toggle behaviours.
- Live log stream, live counters, live profiler samples.
- Trigger a frame capture, a GC, an asset reload, a shader recompile.
- Change graphics settings and the graphics compositor live.

This is how mobile and console debugging actually happens, and it makes the Android/iOS phases
survivable. Stride's `ConnectionRouter` + `EffectCompilerServer` are the same idea and worth studying
for the discovery/pairing mechanics (which is the fiddly part: getting a device and a dev machine to
find each other across a network reliably).

### Diagnostic overlays

Toggleable, in every build:

| Overlay | Shows |
|---|---|
| Frame stats | fps, frame time (CPU/GPU split), draw calls, triangles, memory |
| Frame graph | a live mini flame chart of the last frame |
| Log | the tail of the ring buffer with level filtering |
| Console | command entry: `spawn`, `teleport`, `set`, `reload`, `capture`, `quality`, plus user-registered commands via `[ConsoleCommand]` |
| Render mode | albedo / normal / roughness / metallic / AO / overdraw / light complexity / mipmap-density / LOD-level / wireframe |
| Physics | collider wireframes, contact points, constraints, sleeping state |
| UI debug | element bounds, layout boxes (margin/border/padding/content, like a browser inspector), style origin for a hovered element, dirty-region highlight |
| Audio | active voices, mixer levels, 3D source positions |
| Streaming | which assets are resident, being loaded, or evicted |

The **UI debug overlay deserves emphasis**: an element inspector showing the box model, the matched CSS
rules, and which stylesheet each declaration came from is the single most valuable tool for anyone
building a UI in this framework, and it is nearly free given the styling engine already tracks rule
provenance for the cascade.

### Error handling philosophy

- **Fail loudly in development, degrade gracefully in production.** A missing asset is a magenta
  placeholder plus an error log in a shipping build, and an assertion in a dev build.
- **Assertions** (`Debug.Assert`-equivalent with a Vixen implementation that logs, breaks, and can
  continue) are used liberally in debug builds and compiled out in release.
- **Validation layers on by default in debug.** Vulkan validation, `spirv-val`, render-graph
  validation, job-system race detection, and the ECS structural-change checker are all *on* in a debug
  build. Slow debug builds that catch bugs beat fast debug builds that do not.
- **Every subsystem has a `Validate()` entry point** callable from the console, which asserts its
  internal invariants. Cheap to write alongside the data structure, and it turns "the ECS is corrupt"
  into "the ECS is corrupt at archetype 12, chunk 3, row 17".
