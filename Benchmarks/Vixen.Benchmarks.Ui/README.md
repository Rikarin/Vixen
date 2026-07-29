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

Two suites, and the difference between them is the point. `LayoutBenchmarks` measures the flexbox
engine alone. `DocumentBenchmarks` measures **a whole frame of a themed document** — the cascade, the
font sizes, the layout style, flexbox and the draw list — which is what
[doc 14](../../docs/plan/14-roadmap.md)'s Phase 4 exit criterion is a claim about: three of its four
passes are above the layout tree.

---

# LayoutBenchmarks

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

---

# DocumentBenchmarks

Rows of a label, a button and a checkbox under `ControlTheme`, which is a real user-agent stylesheet
rather than three rules written to be cheap. `Controls` counts controls; a row of five is eight
elements, so **`Controls = 5 000` is 8 001 style nodes**.

## What it measured

Apple M1 Max, .NET 10.0.9, `DefaultJob`. The right-hand pair of columns is the run that closed the
incremental cascade; the left-hand pair is the run that opened it.

| Controls | Nodes | Nothing changed | One class toggled | Cold | Steady alloc | Toggled alloc |
|---|---|---|---|---|---|---|
| 500 | 801 | 36.4 µs | **85.9 µs** | 1.48 ms | **0 B** | **552 B** |
| 5 000 | 8 001 | **436 µs** | **937 µs** | 14.6 ms | **0 B** | **552 B** |
| 20 000 | 32 001 | 2.75 ms | **5.37 ms** | 105 ms † | **0 B** | **552 B** |

† median. The 20 000 cold case has a standard deviation two thirds of its mean — a background
collection lands inside some iterations — so its median is quoted and its mean is not.

**Doc 14's Phase 4 exit criterion is met, with margin.** *"UI frame under 2 ms with 5 000 elements and
zero steady-state allocation"* — 8 001 elements at **0.436 ms** against a 2 ms budget, allocating
**zero bytes**. It still holds at 32 001 elements and 2.75 ms, which is four times the scale the
budget was written for and the first size at which it does not hold.

⚠ **That is the frame an interface spends most of its time in and it is not the frame that matters.**
The steady number is this good because `UiDocument.Update` returns immediately on a clean document, so
what is being timed is the draw walk and the frame diff. The column next to it is the one an
application actually pays.

⚠ **The steady frame is slower than the 0.230 ms this file recorded when the suite first ran, and
that is not explained here.** Allocation is zero in both, so it is real work added to the draw walk
between the two runs rather than pressure; the machine was also visibly noisier this time — cold
frames varied by 2.7× across three runs of the same build. It is quoted as measured rather than
reconciled, and re-running it on a quiet machine is owed before anybody reads a regression into it.

## What it found

⚠ **Toggling one class cost a full cascade, and `StyleUpdater` had no production caller.**

One class on one row of 8 001 elements cost **9.50 ms and 8.87 MB** — 41× the steady frame, and 80 %
of a cold frame that resizes the viewport and relays out everything. An interaction was nearly as
expensive as a theme reload.

The cause was one line: `UiDocument.Update` called `StyleEngine.ResolveAll`, which cascades every live
node from scratch. `StyleUpdater` and `StyleInvalidator` — Phase 4b's incremental restyle, whose gate
is *"toggling `.selected` on one row of a 100×100 grid restyles exactly one element"*, with an
oracle and four sabotages behind it — were **referenced only by their own project's tests**. Nothing in
the running framework had ever used them.

Measured, not inferred:

| | |
|---|---|
| `StyleEngine.ResolveAll` allocation, one pass | 8 642 120 B — within 3 % of the whole frame's 8.87 MB |
| Cascades per pass, 8 001 nodes | ~6 840 |
| Sharing-cache hits per pass | ~975 (12 %) |
| `UiDocument.StylesApplied` | **1** |

**`StylesApplied = 1` is why no test caught this.** Doc 14's claim under 4d — *"one changed class
rebuilds one element"* — is true, and it is a claim about what gets rebuilt *downstream* of the
cascade, which the `ComputedStyle` interning makes cheap to check. It says nothing about the cost of
the cascade that produced the answer, and the two were read as one. `UiDocument.StylesResolved` is the
second number, and it exists because this one was read as both.

⚠ **And the sharing cache cannot cover for it, by construction.** Its key holds the parent *element* —
the correction Phase 4b made to doc 09, and the right one — so it shares between identical siblings
and nowhere else. A 10 000-cell grid is one parent and shares 9 999 times; an inspector of 1 000 rows
of four differing children shares only the 1 000 rows, which is the 12 % above. Sharing makes a *cold*
pass cheap for grid-shaped documents and does nothing for an incremental pass on any shape.

## What it changed

**The document records what changed rather than that something did.** A class change and a state
change are the two mutations `StyleUpdater` can narrow, so they go into a log that the next pass
replays through the updater; everything else still comes through `Invalidate` and still costs a cold
pass. An interaction went from **9.50 ms to 0.937 ms** at 8 001 nodes and from 37.8 ms to 5.37 ms at
32 001 — 7× to 10× at every size.

**The allocation is the number worth reading, because it stopped depending on the document.** A
toggle allocated 889 KB, 8.87 MB and 36.1 MB at the three sizes; it now allocates **552 bytes at all
three**. That flatness is the whole claim of an incremental cascade, and it is the shape a ratio
hides: 65 000× at the largest size is the same fact as *the cascade no longer looks at the document*.

⚠ **The run also found the zero-allocation criterion had quietly stopped holding, and not because of
any of this.** The steady frame was allocating 160 KB at 8 001 nodes and 640 KB at 32 001 — exactly
40 bytes per element with children, which is `List<T>`'s enumerator being boxed. `UiElement.PaintOrder`
returned `IReadOnlyList<UiElement>`, and because its two branches return two different concrete lists
the JIT could not devirtualise the `foreach` in the draw walk the way it does everywhere else. It
returns `List<UiElement>` now, and `UiDocument`'s `Apply` and `Accumulate` take a concrete
`ChildList` for the same reason.

**That regression is dated, and the dates are the point.** The run that recorded *"0 B"* in the table
above was 17:01; `PaintOrder` arrived with `z-index` at 18:06, an hour later. The claim was true when
it was measured and false within the hour, and nothing re-ran the benchmark — which is the argument
for this file being a build step rather than a document somebody remembers to update.

Licensed under Apache-2.0.
