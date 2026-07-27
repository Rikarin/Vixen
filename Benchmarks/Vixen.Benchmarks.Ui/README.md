# Vixen.Benchmarks.Ui

What a layout pass costs at the scale [docs/plan/09](../../docs/plan/09-ui-framework.md) names, and —
the part that matters more — what it costs on the frame *after* the first one.

```bash
./build.sh Benchmark --configuration Release
```

or, for one of them:

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Ui -- --filter '*OneLeaf*'
```

The tree is a panel of `Rows` rows of ten cells: flex-grow columns, margins, padding,
`justify-content: space-between`. `Rows = 10 000` is 110 001 nodes.

## What it measured

Apple M-series, .NET 10, `--job short`.

| Rows | Nodes | Cold | One leaf changed | Nothing changed | Allocated |
|---|---|---|---|---|---|
| 100 | 1 101 | 466 µs | 37 µs | **11 ns** | **0 B** |
| 1 000 | 11 001 | 4.85 ms | 354 µs | **11 ns** | **0 B** |
| 10 000 | 110 001 | 60.0 ms | 7.8 ms | **13 ns** | **0 B** |

**Zero bytes, everywhere.** This is the measurement behind the claim that a settled UI allocates
nothing per frame, at a scale a unit test would not reach.

**A tree nothing changed in costs eleven nanoseconds regardless of its size.** That is one dirty-flag
comparison; the pass never descends. A static panel is genuinely free.

## What it changed

**The §4.5 min-content probe was measuring text on every frame.** `LayoutPassTests` caught it before
this benchmark existed: laying out a tree whose one changed leaf was somewhere else still called an
untouched leaf's measure function once per pass. The automatic-minimum-size rule probes each flex
item for its min-content size, and that probe called the measure function directly — bypassing the
measurement cache whose entire purpose, per doc 09, is that "text measurement is the dominant cost in
a real UI". Min-content size depends only on the subtree and on what percentages resolve against, so
it is now cached per node and per owner size, invalidated by the dirty flag. That is the difference
between a per-frame text shaping pass per item and none.

**The pixel-grid rounding pass was the cost, and it was O(whole tree) every frame.** Before it was
made incremental, one changed leaf cost 100 µs / 1.16 ms / 18.7 ms at the three sizes, of which
64 %, 71 % and 60 % was rounding. It is now 15–35 %, and an incremental frame is **2.4× to 3.3×
faster**.

| Rows | One leaf changed | …with rounding off | Rounding's share |
|---|---|---|---|
| 100 | 37.3 µs | 31.6 µs | 15 % |
| 1 000 | 354 µs | 286 µs | 19 % |
| 10 000 | 7.8 ms | 5.0 ms | 35 % |

What was left is not a shortcut anybody can take: the container whose child changed has to re-place
all of its children, and at 10 000 rows that is 10 000 of them. Rounding is now proportional to that
walk rather than to the whole tree.

## What it found and did not change

**Incremental layout is near-perfect, and the frame cost is not the algorithm.** Instrumenting a
1 000-row tree: an unchanged pass runs the algorithm **0** times, a one-leaf change runs it **21**
times out of 11 001 nodes, and a cold pass runs it 22 001 times. Dirty propagation and the
measurement cache are doing exactly what they are supposed to. What is left in an incremental frame
is the O(children) re-placement at the one container whose child moved, which is inherent.

**Numbers are medians where the mean is noisy.** The 10 000-row case has a long tail — a background
collection lands inside some iterations — so its median is quoted and its mean is not.

Licensed under Apache-2.0.
