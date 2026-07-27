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
| 100 | 1 101 | 466 µs | 100 µs | **11 ns** | **0 B** |
| 1 000 | 11 001 | 4.85 ms | 1.16 ms | **11 ns** | **0 B** |
| 10 000 | 110 001 | 60.0 ms | 18.7 ms | **13 ns** | **0 B** |

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

## What it found and did not change

**Incremental layout is near-perfect, and the frame cost is not the algorithm.** Instrumenting a
1 000-row tree: an unchanged pass runs the algorithm **0** times, a one-leaf change runs it **21**
times out of 11 001 nodes, and a cold pass runs it 22 001 times. Dirty propagation and the
measurement cache are doing exactly what they are supposed to.

**The pixel-grid rounding pass is the cost, and it is O(whole tree) every frame.**

| Rows | One leaf changed | …with rounding off | Rounding's share |
|---|---|---|---|
| 100 | 100 µs | 36 µs | 64 % |
| 1 000 | 1.16 ms | 0.33 ms | 71 % |
| 10 000 | 18.7 ms | 7.4 ms | 60 % |

The reason it cannot simply be skipped is real rather than an oversight: a node's rounded edges are
derived from its *absolute* position, not its relative one — that is the whole point, and it is what
stops two adjacent boxes rounding into a one-pixel seam. An ancestor moving by half a pixel therefore
changes every descendant's rounded result without any of them being dirty.

Skipping it correctly needs a per-node record of the absolute offset it was last rounded at, plus a
stamp saying whether the algorithm actually ran for that node this pass (a cache hit does not rewrite
its children, so its subtree is untouched). Both are small; the interaction between them and the
in-place rounding of positions is not, and it deserves its own change with its own tests rather than
being bolted onto the end of the port. `OneLeafChangedWithoutRounding` exists to keep the number
visible until then.

**It is not urgent at the scale that matters.** The editor-shell target in
[doc 00](../../docs/plan/00-vision-and-principles.md) is 10⁴ elements, where an incremental frame is
1.16 ms — inside budget with room to spare. The 110 001-node figure is a scaling limit worth knowing,
not a blocker.

Licensed under Apache-2.0.
