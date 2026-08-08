# Vixen.Ui.Layout

CSS flexbox **and block layout** over a struct-of-arrays node store. Per
[ADR-006](../../docs/plan/01-technology-decisions.md#adr-006--flexbox-port-the-yoga-algorithm-not-the-flexbox-library)
this is Yoga's *algorithm* re-implemented against Vixen's own data model, judged by Yoga's own
conformance suite — not a port of the `ru-ace/Flexbox` library, whose `class Node` with
`List<Node>` children and `class Style` of boxed values is one heap object per node per style per
result. A Blender-class UI has 10⁴–10⁵ nodes; that allocation profile is disqualifying and the
algorithm is the valuable part.

## State

**Flexbox is complete and the conformance suite is green: 534 of Yoga's fixtures, all passing, and
3 320 of Taffy's judged per fixture across three categories.** Of Taffy's flex and leaf, 2 082 pass,
168 ask for a property this store has no field for, and 158 are known gaps listed with a diagnosis
each — see [the corpus README](../Vixen.Ui.Layout.Tests/Taffy/README.md).

**Block layout landed with doc 43 § B1 and is the store's second algorithm.** 746 of the 912
`block` and `blockflex` fixtures pass, 124 are refused for a property this store has no field for,
and 42 fail — every one of them in the *absolute* path, in two buckets that predate block layout and
that a flex parent hits identically. See [the block section](#block-layout-and-what-a-second-algorithm-cost)
below and `Taffy/BlockKnownGaps.txt`.

| | |
|---|---|
| `LayoutTree` | The store: styles, results, links and node state as parallel `NativeArray`s, plus the tree operations and the whole style surface. |
| `LayoutStyle`, `StyleLength` | Every length as a `(value, unit)` pair, all nine CSS edges kept apart. |
| `StyleResolution`, `FlexAxis` | Edge precedence, percentages, box sizing; flow-relative to physical. |
| `LayoutTree.CalculateLayout` | The algorithm: flex basis, line breaking, the two-pass free-space distribution, justification, cross-axis alignment, multi-line alignment, absolute positioning, pixel-grid rounding. |
| `LayoutTree.Block`, `CollapsibleMargin` | The second algorithm: block stacking, the inline-axis fill, CSS 2.1 §8.3.1 margin collapsing, auto margins, the intrinsic-width probe. |
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

⚠ It now carries five more, all from closing the corpus's largest bucket, and **two of the five are
tests no corpus could have written** — see the table below. The pattern is worth naming: a rule
about *intrinsic sizing* is invisible to a fixture whose every box has a definite size, and both
corpora are largely made of those.

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

**2 074 of the 2 208 runnable flex fixtures pass**, up from 2 002 at the corpus's first run.
Thirteen of the original failures were the bridge and were fixed — `start` is not a spelling of
`flex-start`, and `self-start` resolves against the item's own direction. Of the 206 that were
Vixen's, **48 are closed** and 158 remain, catalogued in `Taffy/KnownGaps.txt`.

The largest bucket was **the paragraph above, one level further out**, and it turned on a
distinction CSS Sizing §5.2.2 draws and this store did not: a box's min-content **size** and its
min-content **contribution** are different numbers. The contribution of a box whose preferred size
is definite *is* that size — its contents are never consulted. Vixen asked every descendant for its
min-content size, so an empty `width: 50px` box answered **zero**, and every §4.5 floor computed
from such a descendant was missing. `align_baseline_child_padding`: two 50px siblings in a 90px
content box, Chrome shrinks one to 40 and floors the other at 50, Vixen shrank both to 45.

Three further rules had to come with it, and **each was found by a different one of the three
oracles** — which is the strongest argument in this README for keeping all three:

| Rule | Spec | Found by |
|---|---|---|
| A percentage preferred size is not definite while sizing intrinsically | Sizing §5.2.1 | both corpora, same fixture |
| A wrapping container's min-content main size is its widest item, not the sum | Flexbox §9.9.1 | **Yoga alone** — three fixtures; Taffy's 2 208 stayed green |
| A box that clips an axis contributes nothing along it but its own edges | Sizing §5.2.2 | **the committed screenshots alone** — both corpora stayed green |

That last one is the sharpest. Every box in the docking chain declares `overflow: hidden`, so the
moment descendants began contributing real sizes the hierarchy tree's rows propagated all the way
out and the editor shell came out **2 385 points wide inside a 1 100-point window**, with the
inspector pushed off the side — while 2 742 browser-derived fixtures reported no change at all. The
five hand-written cases in `AutomaticMinimumSizeTests` exist so that none of the four is ever again
resting on an oracle that cannot see it.

After that come §9.7's min/max violation loop, `aspect-ratio` against a clamped cross size, and
baseline alignment past the simple case. Min-larger-than-max precedence is also closed: CSS Sizing
§5.1 makes it the *order* of two clamps rather than a special case, and `BoundAxisWithinMinAndMax`
was returning the moment the maximum bit, so the minimum below it was never read.

⚠ **One thing the §4.5 work exposed and did not fix**, because the fix belongs with §9.7: §9.2 step
9 says the *hypothetical* main size is the flex base size clamped by the used min — and §4.5's
automatic minimum is the used min. Vixen consults it only inside the two distribution passes, so an
item that neither grows nor shrinks never sees its own floor even when that floor is now computed
correctly. `flex_basis_smaller_than_content_row` is the clean case.

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

## Block layout, and what a second algorithm cost

Doc 43 § B1 put block before grid deliberately: it is the smaller of the two, it makes `display` a
real enum, and it answers the question grid actually needs answered — **whether this store can carry
a second algorithm at all.** It can, and the price is small enough to write down in full.

**Three fields on `LayoutResult`, three matching ones on `CachedMeasurement`, and one dispatch.** The
flex algorithm needs nothing out of a child's layout but its size: ask how big, place it, done. Block
layout cannot work that way, because a child's top margin may not belong to the child — with no
border and no padding between them it belongs to the parent, and to the grandparent after that. So a
layout now returns a `CollapsibleMargin` at each end and a "can be collapsed through" flag beside its
size, and **every other algorithm has to answer for them too**: a flex container, a text leaf and an
empty box are all barriers, so their honest answer is "my own margin, and no". The cache entries
carry the same three, because a cached answer that replays only the size hands back whichever margin
set the last full run happened to leave on the node.

Nothing else moved. The child arena, the dirty propagation, the measure cache's shape, the rounding
pass and the absolute walk are all untouched, and the *input* side needed no new parameter at all —
whether a node's margins may collapse with its parent's is a function of the two styles, so it is
derived from the tree rather than threaded through `CalculateLayoutInternal` and made part of the
cache key. That is the sentence grid inherits.

**It implements margin collapsing rather than approximating it**, which is the whole reason it is
allowed to be called `block`. A stretch flex column gets stacking and filling right and margin
collapsing wrong, and the difference is not subtle: two cards with 8 points of margin between them
are 8 apart in a browser and 16 apart in the approximation. Sibling collapse, parent/first-child,
parent/last-child, collapse-*through* an empty box, all twelve things that block a collapse, and the
sign rule where a positive and a negative margin **add** rather than maximise — 216 of the corpus's
264 `margin_y_*` fixtures assert them and none fails. (`CollapsibleMargin` keeps two numbers for the
sign rule; collapsing implemented as a running `MathF.Max` is right for every all-positive case,
which is most of them.)

**Floats are not implemented and are not foreclosed.** They would attach at exactly two points — the
intrinsic-width probe would route a floated child into a left/right accumulator instead of the
running maximum, and the in-flow walk would ask a float context for a content slot instead of taking
the whole inner width — and nothing in the walk caches an assumption a float could not later narrow.
The 84 `float` fixtures stay refused at the style map, and they were never waiting on `display`:
`TaffyStyleMap` refuses them on the `float` attribute, so unlike block and grid they did not arrive
with the keyword.

### The 884 could not see three of the rules, and one of the three is Chrome's own fixture

The §4.5 story in the section above, repeated exactly, with the sabotage done rather than assumed:

| Rule | Sabotage result | Held by |
|---|---|---|
| `overflow` other than `visible` blocks a collapse | all 3 571 corpus tests green | `MarginCollapsingTests` |
| A flex container is a barrier to collapsing | all 3 571 corpus tests green | `MarginCollapsingTests` |
| A positive and a negative margin add | the corpus catches it | both |

The first is the sharpest one yet, and it is a new *shape* of blind spot. Chrome's fixtures for it
exist — 48 of them, the `block_margin_y_*_blocked_by_overflow_{x,y}_{hidden,scroll}` families, with
the right answers in them — and every single one also sets `scrollbar-width`, which this store has no
field for, so the harness refuses them. **The corpus contains the test, states the answer, and cannot
run it.** A gap in an oracle need not be a gap in its coverage; it can be a gap in the *bridge*, and
that one is invisible from either end.

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
every one of them refused today at the single point of the `display` keyword — and that prediction
has now been paid out once, because `display: block` was the same sentence about 884 more and they
went from 0 passing to 722 in the commit that added the keyword.

**Inline formatting**, and with it `inline-block`, `inline-flex`, `text-align` and `vertical-align`.
Doc 43 § B3. It is why those two `display` keywords are refused rather than aliased onto their
block-level twins: `inline-block` mapped onto `Block` would take the whole line, which is the one
thing an author writes it to prevent.

**Floats.** See the block section above for where they attach; 84 fixtures wait on them.

**Auto margins on an absolutely positioned box** (CSS 2.1 §10.3.7) and **`aspect-ratio` re-applied
after an absolute box's size is clamped** — the two buckets that make up all 42 block failures and
part of the 158 flex ones. Both live in `LayoutTree.Absolute.cs`, which is shared with Yoga's 534, so
they want a change of their own rather than a block-shaped patch.

**Parallel layout.** Independent subtrees with a fixed available size are jobs, and text measurement
of siblings is where the win is. `Benchmarks/Vixen.Benchmarks.Ui` now gives the serial number to
beat, and it says the algorithm is not where an incremental frame's time goes — so this waits behind
the rounding pass, which is.

Licensed under Apache-2.0.
