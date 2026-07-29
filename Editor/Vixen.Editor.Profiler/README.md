# Vixen.Editor.Profiler

The measuring half of [doc 20's B4](../../docs/plan/20-editor-parity.md#b4--diagnostics). A CPU
flame chart over the sample rings the engine already keeps, a GPU timeline over timestamp queries, a
memory view over the four arenas [doc 13](../../docs/plan/13-diagnostics.md) names, and per-scene
statistics.

## What is here

| | |
|---|---|
| `IProfileSource`, `LocalProfileSource`, `BufferedProfileSource` | Where samples come from: this process, or a build streaming them across. |
| `ProfileCapture`, `ProfileThread` | One press of Record, as a value. Immutable, so two can exist at once. |
| `FlameNode` | The nesting the rings deliberately do not keep, rebuilt per capture. |
| `ProfileSummary`, `CaptureComparison` | The table under the chart, and two captures subtracted. |
| `GpuProfiler`, `GpuFrame`, `GpuScope` | Timestamps written into a query pool and read back without waiting. |
| `MemorySnapshot`, `MemoryProviders` | Managed heap, native allocations, and two arenas somebody else supplies. |
| `SceneStatistics`, `StatisticsBudget` | Counts, ceilings, and what is worth saying out loud. |
| `ProfilerModel` | Which source, what state, what capture, what baseline. |
| `ProfilerView`, `FlameChartView`, `GpuTimelineView`, `MemoryView`, `StatisticsView` | The panels. |

## The profiler must be able to profile the editor

Doc 20 says so in as many words, and it is why nothing here touches `Profiler` directly. The panel
asks an `IProfileSource`, and which source is a dropdown — so the editor's own frame and a running
game are the same panel with a different selection. `EditorHost` instruments its loop with the four
phases its own remarks name, which is what gives the "Editor" source anything to show.

```csharp
var model = new ProfilerModel();
model.Add(new LocalProfileSource("Editor"));

model.Start();          // empties the rings, so the capture starts at the press
model.Tick();           // once a frame: the rings overwrite, so a long capture has to drain
model.Stop();           // gathers, merges by thread, and builds the capture
```

⚠ **Collecting empties the rings**, which is why a source is an object rather than a call. Two
readers of one ring see half a frame each, so there is one source per set of rings and the panel
owns it.

⚠ **Samples arrive in *completion* order.** A scope is recorded when it closes, so a parent lands in
the ring after every child it contains — the obvious reading of the array builds every tree upside
down. `FlameNode.Build` sorts before it walks, and that sort is the whole trick.

## The GPU timeline was blocked on the RHI, and is not any more

Doc 20 called this "the one thing in E4 that cannot start with the panel": there was no query API in
`Vixen.Graphics` to build against. There is now — `CreateQueryPool`, `ICommandList.WriteTimestamp`,
`TryResolveQueries` and `GraphicsDeviceFeatures.HasTimestampQueries` — implemented for real on
Vulkan, recorded on the Null backend, and declared unsupported with a reason on OpenGL and WebGPU.

```csharp
gpu.BeginFrame(commands, frameIndex);       // resets the pool this frame writes into

var pass = gpu.Begin(commands, "shadows");
// … the pass's draws …
gpu.End(commands, pass);

if (gpu.Resolve()) {                        // never waits; false until the GPU catches up
    view.Show(gpu.Latest);
}
```

⚠ **One pool per frame in flight, and reading never waits.** A single pool written every frame is
one the GPU is still writing while the CPU reads it, so the readings would be a mixture of two
frames. And asking the driver to wait would stall the frame thread once per frame — a profiler that
halves the frame rate it is reporting. The first few frames after attaching therefore produce
nothing, which is correct.

⚠ **A GPU timestamp's zero point means nothing.** It is comparable with another reading from the
same device and with nothing at all on the CPU; lining the two up needs a calibrated pair, which is
an extension many drivers lack. So the timeline is drawn relative to its own first reading, beside
the CPU chart rather than merged into it.

## What the memory view can and cannot see

The managed heap is the runtime's own and is exact. Native allocations come from `LeakTracker`,
which **compiles out of release builds** — so the panel says "not tracked in this build" rather than
reading zero, because a memory panel claiming no native allocations on a build that cannot see them
is the most misleading thing it could do. GPU heaps and asset residency arrive through
`MemoryProviders`: the first needs `VK_EXT_memory_budget`, which the Vulkan backend does not query
yet, and the second is the editor's asset database.

⚠ **Refreshing must not collect.** `GC.GetTotalMemory(true)` would give a tidier number by running a
blocking gen-2 collection first — which changes what is being measured and stalls the editor.

## Statistics are a traversal and nothing else

Doc 20's B4 says "scene traversal only", and that is what this is: entities, archetypes, chunks,
chunk memory, component instances and hierarchy depth, each against a budget a project can set. No
draw calls and no triangles — the viewport draws lines, and a draw-call count at this point in the
engine's life would be a guess presented as a measurement.

⚠ **Archetype count is the figure worth watching that nobody thinks to watch.** It decides how many
chunks a query walks, and a scene that grew from forty archetypes to four hundred is one where
somebody added a tag component per entity — free to store, and it fragments every query in the game.

Licensed under Apache-2.0.
