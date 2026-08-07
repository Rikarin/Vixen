# Vixen.Ui.Layout

CSS flexbox over a struct-of-arrays node store. Per
[ADR-006](../../docs/plan/01-technology-decisions.md#adr-006--flexbox-port-the-yoga-algorithm-not-the-flexbox-library)
this is Yoga's *algorithm* re-implemented against Vixen's own data model, judged by Yoga's own
conformance suite — not a port of the `ru-ace/Flexbox` library, whose `class Node` with
`List<Node>` children and `class Style` of boxed values is one heap object per node per style per
result. A Blender-class UI has 10⁴–10⁵ nodes; that allocation profile is disqualifying and the
algorithm is the valuable part.

## State

**Flexbox is complete and the conformance suite is green: 2 990 tests, of which 534 are Yoga's and
2 408 are Taffy's.** Yoga's are all green. Of Taffy's, 2 026 pass, 176 ask for a property this store
has no field for, and 206 are known gaps listed with a diagnosis each — see
[the corpus README](../Vixen.Ui.Layout.Tests/Taffy/README.md).

| | |
|---|---|
| `LayoutTree` | The store: styles, results, links and node state as parallel `NativeArray`s, plus the tree operations and the whole style surface. |
| `LayoutStyle`, `StyleLength` | Every length as a `(value, unit)` pair, all nine CSS edges kept apart. |
| `StyleResolution`, `FlexAxis` | Edge precedence, percentages, box sizing; flow-relative to physical. |
| `LayoutTree.CalculateLayout` | The algorithm: flex basis, line breaking, the two-pass free-space distribution, justification, cross-axis alignment, multi-line alignment, absolute positioning, pixel-grid rounding. |
| `LayoutTree.Order` | §5.4 `order`, the one part that is not Yoga's. One redirection: the algorithm reaches children only through `ChildIds`, so sorting what that returns is the whole property. |
| `Generated/` | 534 conformance fixtures, translated from Yoga by `Tools/Vixen.YogaTestGen`. |
| `Taffy/` | 5 524 more, from Taffy, vetted by `Tools/Vixen.TaffyTestGen`. A second browser-derived opinion on flexbox, and the oracle block and grid will be judged by. |

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

The same file now also covers the §4.5 escape hatch being **per axis**. Yoga carries one `overflow`
per node; `LayoutStyle` carries `OverflowX` and `OverflowY`, because CSS has two properties and each
rule that reads them is about one axis — §4.5's opt-out is the *main* axis's overflow, and a scroll
container's fit-content size is clamped only along the axis that scrolls. Every fixture in the ported
suite sets both to the same value, so it cannot tell a correct per-axis reading from a collapsed one;
`The_opt_out_is_the_main_axis_s_own_overflow` is what does. Where the two agree, the arithmetic is
byte-for-byte Yoga's — including the width-propagation rule in step 2, which stays keyed on the main
axis rather than on `overflow-x` precisely so that plain `overflow: scroll` on a column keeps
answering what the fixtures expect.

### A second corpus, and what it found

**[Taffy's 5 524 fixtures](../Vixen.Ui.Layout.Tests/Taffy/README.md) now run beside Yoga's 534**, per
doc 43 § B0. They are the same kind of artefact from a different engine — HTML laid out by
Chrome-for-Testing — and they exist here for block and grid, which have no oracle at all. Flexbox got
them first on purpose: it is the one mode where the answer is already known, so a wrong harness would
be visibly wrong there rather than invisibly wrong inside grid later.

**2 002 of the 2 208 runnable flex fixtures pass.** Thirteen of the original failures were the
bridge and were fixed — `start` is not a spelling of `flex-start`, and `self-start` resolves against
the item's own direction — and the 206 that remain are Vixen's, catalogued in `Taffy/KnownGaps.txt`.

The largest bucket is **the paragraph above, one level further out.** §4.5's floor is applied to a
measured leaf and not to a flex item that is itself a container, whose min-content size comes from
its descendants. `align_baseline_child_padding`: two 50px siblings in a 90px content box, Chrome
shrinks one to 40 and floors the other at 50, Vixen shrinks both to 45. `AutomaticMinimumSizeTests`
could not see it because it was written around the case Yoga's fixtures also miss. After that come
§9.7's min/max violation loop, `aspect-ratio` against a clamped cross size, and min-larger-than-max
precedence.

⚠ **And the new corpus has its own blind spot, which the old one covers.** Taffy's fixtures set
`direction` on every one of their 22 776 nodes, so `Direction.Inherit` is never stored: breaking
`ResolveDirection` so that it ignores its owner leaves **all 2 241 Taffy tests green** and fails
**374 of Yoga's 534**. Ten times the size is not a superset, and neither suite retires the other.

**`order` is not in Yoga at all**, which is a harder version of the same problem. §4.5 was a rule the
fixtures merely failed to exercise; here the oracle does not implement the property and never could
have — Yoga's style surface goes from `flexWrap` to `overflow` with nothing between, so
`Vixen.YogaTestGen` emits no fixture that sets one and all 534 stay green against a tree that ignores
the field entirely. Deleting the sort leaves the whole ported suite passing.

So the oracle for `order` is **[`web-platform-tests`](https://github.com/web-platform-tests/wpt),
`css/css-flexbox/`** — a browser conformance suite rather than one engine's regression suite — and
`OrderTests` names the file each case comes from. They are re-expressed rather than translated: WPT's
`order` tests are mostly reftests and `offsetLeft` comparisons over auto-sized text, and this store
has neither a renderer nor a default font, so what carries across is the relation each test asserts
with the geometry restated in fixed sizes. It earned that immediately — `flexbox_order-noninteger-invalid`
says `order: 1.5` is an *invalid declaration* and computes to `0`, where this bridge had been written
to round it to `2`.

Two things that suite does not reach, and one it cannot. It has no case for **sort stability**,
because a browser's sort is stable and the property is specified as a sequence rather than as a sort —
that is an implementation hazard, and `Items_in_the_same_ordinal_group_keep_document_order` is
hand-written for it. ⚠ It needs **thirty-four items**: written with eight it passed against a
deliberately unstable sort, because .NET's introsort delegates any span of sixteen or fewer to an
insertion sort, which *is* stable. A small stability test certifies stability the implementation does
not have. And **paint order** is not testable here at all — `order` changes painting as well as
layout, but this store draws nothing; `Vixen.Ui.Tests.OrderTests` and the utilities inventory hold
that half.

The two places `order` must *not* reach are guarded rather than fixed: `:nth-child` matches over
`StyleTree`'s own `IndexInParent` and focus traversal walks `UiElement.Children`, so neither reads
this store and neither could have started following visual order. The tests exist so that a future
change wiring one of them to the layout tree has to argue with a red test.

There is no `Overflow.Auto`. CSS's `auto` and `scroll` establish the same scroll container and differ
only in whether a scrollbar gutter is reserved, and nothing above this draws a scrollbar of its own —
so `LayoutStyleBuilder` maps `auto` onto `Scroll` rather than splitting every `== Scroll` here in two.
The keyword itself survives in the computed style if an engine with gutters ever needs to tell them
apart.

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
piece rather than as a variation on this one. **Its oracle is here already**: 2 040 Taffy fixtures,
every one of them refused today at the single point of the `display` keyword. `display: block` is the
same story with 884 more, plus 84 for `float`.

**Parallel layout.** Independent subtrees with a fixed available size are jobs, and text measurement
of siblings is where the win is. `Benchmarks/Vixen.Benchmarks.Ui` now gives the serial number to
beat, and it says the algorithm is not where an incremental frame's time goes — so this waits behind
the rounding pass, which is.

Licensed under Apache-2.0.
