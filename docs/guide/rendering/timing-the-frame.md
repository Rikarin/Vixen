---
title: Timing the frame on the GPU
slug: rendering/timing-the-frame
kind: guide
area: Rendering
summary: The render graph brackets every pass it runs with a named scope, so a document that adds a node gets a bar in the timeline without anybody opting in — and an optional sink is what keeps that free when nobody is measuring.
api: [T:Vixen.Graphics.IGpuScopeSink, T:Vixen.Graphics.GpuProfiler, T:Vixen.Graphics.GpuFrame, T:Vixen.Graphics.GpuScope, T:Vixen.Graphics.GpuTimestamps, L:13025]
tags: [rendering, render-graph, profiling, diagnostics, gpu]
since: 0.1
status: preview
related: [rendering/choosing-a-frame, rendering/standard-frame, rendering/reading-the-frame, rendering/capturing-a-frame]
---

## What it is

Four pieces, and one rule that ties them together: **the frame times itself, rather than forty
renderers each remembering to.**

| Piece | What it is |
|---|---|
| `RenderGraph.Profiler` | An optional sink. Null by default, and null means nothing is recorded. |
| `IGpuScopeSink` | What the graph talks to — `Begin` a named region, `Close` it. |
| `GpuProfiler` | The sink that answers with timestamp queries, one pool per frame in flight. |
| `GpuFrame` / `GpuScope` | What comes back: a list of named regions with GPU-clock readings. |

Attach a sink to a graph and every pass the graph runs is bracketed by a scope named after it. Attach
nothing and the emission is a null check per pass.

## What it is for

Answering "what is this frame spending its time on" without editing a renderer to find out.

The emission is in the framework on purpose. Unity's SRP wraps each `ScriptableRenderPass` in a
`ProfilingSampler`; Unreal's RDG emits a draw event per pass automatically; both reached the same
conclusion, which is that **a scheme requiring every renderer to remember is a scheme that produces an
empty timeline.** That is not hypothetical here — the panel, the flame chart and the readback path
were all complete and shipped for months against a profiler nothing ever called.

So one loop in `RenderGraph.Execute` covers every node any document names, present or future.
`docs/plan/13-diagnostics.md` asks for exactly this: "timestamps around each render-graph pass".

## Using it

### In a game

`--vixen-gpu-profile` on the command line, or `GraphicsOptions.GpuProfiling` in `OnConfigure`. The
readings arrive on `AppGraphics.GpuFrame`, a few frames late.

```csharp no-compile="a fragment; the override belongs to the project's Game subclass"
protected override void OnConfigure(AppConfig config) {
    config.Graphics.GpuProfiling = true;
}
```

### Anywhere else

```csharp no-compile="a fragment; the device, graph and frame index are the host's"
using GpuProfiler profiler = new(device);

graph.Profiler = profiler;

// Once per frame, on the list the passes are recorded into, outside any render pass: the reset it
// records is a transfer-shaped operation and Vulkan will not allow one inside a pass.
profiler.BeginFrame(commands, frameIndex);

graph.Execute(commands);

commands.Finish();
device.GraphicsQueue.Submit([commands]);

// After the submit, and it reads a frame from several submissions ago.
if (profiler.Resolve()) {
    Show(profiler.Latest);
}
```

## Three things that will bite

### Off is the default, and it has to be

A timestamp is a GPU write. On tile-based hardware — every Apple GPU, every mobile GPU, and MoltenVK
is what this engine develops against — a query write can force a tile resolve, so **an always-on
instrument changes the timings it reports.** In sample 13's frame (49 passes, 98 timestamp writes) the
measured cost of turning it on is under 1%; that is small and it is not nothing, and a project that
left it on in shipping would be carrying a cost nobody could account for.

Null is therefore not a degraded mode, it is the shipping mode: with no sink attached the graph
records no query commands at all.

### The readings are late, and never waiting for them is the point

`Resolve` asks the device for the oldest pool and takes "not yet" for an answer. The alternative is a
stall on the frame thread once per frame — a profiler that halves the frame rate it is reporting. The
first few frames after attaching therefore yield nothing, which is correct.

### A timestamp's zero point means nothing

Two readings from one device subtract to a duration; a reading compared with a CPU clock compares
nothing. Lining the two up needs `VK_EXT_calibrated_timestamps`, which many drivers do not have —
which is why a GPU timeline is drawn relative to its own first reading, and why the editor shows a GPU
track beside a CPU one rather than one merged track. See `GpuTimestamps`.

## One level, and why not more

`GpuScope.Level` exists and the graph never raises it: every pass it runs is emitted at whatever depth
the sink is already at. A host that opens a scope around `Execute` gets its passes one level in; a
host that does not gets a flat list.

That is deliberate. **A pass has no caller, and nothing runs "inside" another pass in any sense a
timestamp can see** — so a control drawing rows of nesting would be inventing structure that is not
there. The one level of grouping a debug group gives is what `Level` is for.

The case for more is real but is a different mechanism: Unreal needs *two*, draw events for the tree
and GPU stats for coarse buckets, because a bucket like "Shadows" aggregates passes from several
places in the frame and cannot be derived from a pass tree. Note also that a group's duration is
already implied by its children — first begin to last end — so hierarchy, when it arrives, should be
metadata on the scope rather than more queries.

## Captures get the same names

The same loop pushes a debug group around every pass **without attachments**, which is the half of a
frame a capture could not name. A backend already labels a render pass from its description — the
Vulkan one turns it into a debug group, WebGPU into a pass label — so an attachment pass is legible
already and a second group would nest its own name inside itself. A compute dispatch has no
description at all, and in a real frame that is most of the interesting work: the GPU cull, the
clipmap, the surface cache, the probe gather, the exposure reduce.

⚠ **A pass named `""` gets no group.** A backend may decline to open a group it cannot name while its
pop is unconditional, which closes a group somebody else opened — an unbalanced label stack that
surfaces as a validation error at submit, a frame away from the pass that caused it.

## Examples

Sample 13 reads `AppGraphics.GpuFrame` at shutdown and prints its own timeline, which is what "open
the profiler" means at a command line:

```
GPU frame 297: 58.402 ms across 49 pass(es), 57.352 ms attributed, in declaration order.
   1. Gather                         26.777 ms   45.8%
   2. SunPages.Mark                   6.434 ms     11%
   3. Main                            5.275 ms      9%
   4. Ssao                            2.158 ms    3.7%
   …
```

Two numbers in that summary are worth more than the ranking:

- **How much of the frame the passes account for.** A large remainder means the timeline is missing
  work — either the graph ran something outside a pass, or scopes fell off the end of
  `GpuProfiler.ScopeCapacity`, which `GpuProfiler.Dropped` counts rather than hiding.
- **Whether the readings ascend.** They must, because the graph runs passes in declaration order. If
  they do not, the pool being read is one the GPU is still writing — and the symptom is a timeline
  whose bars overlap impossibly rather than an error.

## See also

- [Choosing a frame](choosing-a-frame.md) — what the passes being timed are.
- [Reading the frame](reading-the-frame.md) — the other two ways a pass consumes its own output.
- `docs/plan/13-diagnostics.md` — where the per-pass timestamp rule is written down.
