# Vixen.Ui.Layout

CSS flexbox over a struct-of-arrays node store. Per
[ADR-006](../../docs/plan/01-technology-decisions.md#adr-006--flexbox-port-the-yoga-algorithm-not-the-flexbox-library)
this is Yoga's *algorithm* re-implemented against Vixen's own data model, judged by Yoga's own
conformance suite — not a port of the `ru-ace/Flexbox` library, whose `class Node` with
`List<Node>` children and `class Style` of boxed values is one heap object per node per style per
result. A Blender-class UI has 10⁴–10⁵ nodes; that allocation profile is disqualifying and the
algorithm is the valuable part.

## State

**Flexbox is complete and the conformance suite is green: 552 tests, 534 of them Yoga's.**

| | |
|---|---|
| `LayoutTree` | The store: styles, results, links and node state as parallel `NativeArray`s, plus the tree operations and the whole style surface. |
| `LayoutStyle`, `StyleLength` | Every length as a `(value, unit)` pair, all nine CSS edges kept apart. |
| `StyleResolution`, `FlexAxis` | Edge precedence, percentages, box sizing; flow-relative to physical. |
| `LayoutTree.CalculateLayout` | The algorithm: flex basis, line breaking, the two-pass free-space distribution, justification, cross-axis alignment, multi-line alignment, absolute positioning, pixel-grid rounding. |
| `Generated/` | 534 conformance fixtures, translated from Yoga by `Tools/Vixen.YogaTestGen`. |

Every expected number in those fixtures came out of a real browser laying out a real HTML fixture.
That is what makes this a *conformance* suite rather than a regression suite, and it is the specific
defence doc 14 names against the failure mode of AI-assisted work — code that reads plausibly and is
wrong. It earned that on the first run: 530 of 534 passed, and of the four that did not, three were
a sloppy port of Yoga's *test helper* rather than of the algorithm, and one was a real rule —
a degenerate `aspect-ratio` has to behave as `auto` rather than be divided by.

### What is not covered by the ported suite

Sabotaging the CSS Flexbox §4.5 automatic minimum size leaves all 534 fixtures green. Yoga's
generator emits no fixture that shrinks a measured leaf past its own content, so roughly 150 lines
implementing a specification section had no test over it at all. `AutomaticMinimumSizeTests` is
hand-written to close that: four cases, two of which fail without the floor. An external oracle is
worth what doc 14 says it is worth, and it is still worth knowing where it stops.

### What is not implemented, and why

- `display: contents` — outside the algorithm scope doc 09 states. The nine fixtures using it are
  skipped by name.
- Yoga's errata flags and experimental features — a default configuration turns none of them on, so
  porting them would be porting dead branches.
- The separate min-content measure callback. Its fallback — asking the ordinary measure function
  under `AtMost 0` — is what a text measurer answers with its longest word anyway.

## Rounding reads the raw layout and writes somewhere else

The reference implementation rounds positions and sizes in place. That means the next pass reads
*rounded* values for every node it does not recompute, and an incremental layout drifts away from a
cold one by up to half a pixel per level. The property test in `PixelRoundingTests` found exactly
that within a hundred cases.

So the rounded result lives in its own fields and the raw layout is never overwritten. Rounding
becomes a pure function of (raw position, raw size, absolute offset), which is both easier to reason
about and what makes the pass safe to skip for a subtree whose algorithm did not run and whose
absolute offset has not moved — worth 2.4× to 3.3× on an incremental frame.

## The store

Five parallel arrays indexed by a dense `int`:

| Array | What it holds |
|---|---|
| `LayoutStyle` | What was written. ~400 bytes. |
| `LayoutResult` | What was computed, plus the measurement cache. |
| `LayoutLinks` | Parent, and a `(offset, count, capacity)` into the shared child arena. |
| `LayoutNodeState` | Live, dirty, has-new-layout, has-measure-function. |
| `ChildArena` | Every node's child ids, in one array, in power-of-two blocks with free lists. |

Three decisions worth naming.

**Children are a contiguous run, not a linked list.** Doc 09's `LayoutLinks` sketch implies
`firstChild`/`nextSibling`. The algorithm addresses children by index inside its inner loops — a
flex line *is* a range of them — and a linked list makes each of those a walk, turning several O(n)
passes into O(n²) on the widest nodes in the tree.

**All nine edges are stored, including the shorthands.** CSS resolves `margin-left`,
`margin-inline-start`, `margin-horizontal` and `margin` by a fixed precedence at read time, not by
expansion at write time: `padding: 5` then `padding-left: 9` is not the same document as the
reverse, and a store that expanded on write could not tell them apart.

**A style is ~400 bytes, not doc 09's 120.** That estimate was made before the edge shorthands and
the writing-mode-relative pair were counted. A hundred thousand nodes is therefore about 40 MB in
five allocations — against the reference port's several hundred thousand heap objects for the same
tree, which is the comparison ADR-006 was actually making.

## What is measured

| | |
|---|---|
| Steady-state allocation | **0 bytes** per frame — three `LayoutPassTests` gates, and the benchmark at 110 001 nodes. |
| An unchanged tree | **11 ns**, any size. One dirty-flag comparison; the pass never descends. |
| A one-leaf change in an 11 001-node tree | The algorithm runs **21** times. Dirty propagation and the measure cache do their job. |
| An incremental frame at 10⁴ elements | 354 µs, well inside the [doc 00](../../docs/plan/00-vision-and-principles.md) editor budget. |
| Incremental layout vs. laying out from cold | Identical, to the bit, under pixel rounding — a property test compares every node against a second tree built from scratch. |

Numbers and method in [the benchmark's README](../../Benchmarks/Vixen.Benchmarks.Ui/README.md).

## Regenerating the conformance suite

The fixtures are committed because CI has no reference clone. To re-translate after updating the
clone:

```bash
dotnet run --project Tools/Vixen.YogaTestGen -- references/yoga Core/Vixen.Ui.Layout.Tests/Generated
```

It reports every fixture it could not translate and why. Nine are skipped today, all of them
`display: contents`, which is outside the algorithm scope
[doc 09](../../docs/plan/09-ui-framework.md) states.

## Deliberately not here

**CSS Grid**, which doc 09 schedules as a separate algorithm over this same store. It is a harder
specification than flexbox and it does not share the flex line machinery, so it lands as its own
piece rather than as a variation on this one.

**Parallel layout.** Independent subtrees with a fixed available size are jobs, and text measurement
of siblings is where the win is. `Benchmarks/Vixen.Benchmarks.Ui` now gives the serial number to
beat, and it says the algorithm is not where an incremental frame's time goes — so this waits behind
the rounding pass, which is.

Licensed under Apache-2.0.
