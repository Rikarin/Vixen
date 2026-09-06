# Vixen.Ui.Layout

CSS flexbox, **block layout, grid and inline formatting** over a struct-of-arrays node store. Per
[ADR-006](../../docs/plan/01-technology-decisions.md#adr-006--flexbox-port-the-yoga-algorithm-not-the-flexbox-library)
this is Yoga's *algorithm* re-implemented against Vixen's own data model, judged by Yoga's own
conformance suite — not a port of the `ru-ace/Flexbox` library, whose `class Node` with
`List<Node>` children and `class Style` of boxed values is one heap object per node per style per
result. A Blender-class UI has 10⁴–10⁵ nodes; that allocation profile is disqualifying and the
algorithm is the valuable part.

## State

**Flexbox is complete and the conformance suite is green: 534 of Yoga's fixtures, all passing, and
3 320 of Taffy's judged per fixture across three categories.** Of Taffy's flex and leaf, 2 242 pass,
152 ask for a property this store has no field for, and 14 are known gaps listed with a diagnosis
each — see [the corpus README](../Vixen.Ui.Layout.Tests/Taffy/README.md).

**Block layout landed with doc 43 § B1 and is the store's second algorithm.** 788 of the 912
`block` and `blockflex` fixtures pass, 124 are refused for a property this store has no field for,
and **none fail** — `Taffy/BlockKnownGaps.txt` is down to its refusal list, and the committed failure
count is zero, so the next block regression names itself. See
[the block section](#block-layout-and-what-a-second-algorithm-cost) below.

**Grid landed with doc 43 § B2 and is the third.** 2 038 of the 2 120 `grid`, `blockgrid` and
`gridflex` fixtures pass, 40 are refused, and 42 fail in the buckets `Taffy/GridKnownGaps.txt`
names one at a time. It is **partial and says which part**: placement (§8), the bulk of track
sizing (§12), §11.8's baseline alignment, CSS Grid §9's containing block for an out-of-flow child
and §7.3's `grid-template-areas` are done; **named lines written into a track list** are not — see
[the grid section](#grid-and-the-part-with-no-oracle).

**Inline formatting landed with doc 43 § B3 and is the fourth**, and it is the first mode to arrive
with **no corpus at all**: not one of the 6 058 fixtures sets `display: inline*` or `vertical-align`,
verified by enumeration. Its oracle had to be fetched from `web-platform-tests`, and most of WPT's
inline suite could not cross either — a line box's metrics depend on a font, and this store has none.
It is **partial and says which part**: atomic inlines are done, and so is **fragmentation** — a
non-atomic `inline` box crossing a line break is now one box per line, which is the first time a node
in this store has produced more than one rectangle. ⚠ **And so is the strut**, which this file listed
as structurally out of reach for as long as inline formatting has been here: §10.8's strut is font
metrics, this store has no font, and neither of those facts stopped it — a strut is five *numbers*,
so `StrutMetrics` is a computed value the layer with the `FontRegistry` writes down, and every rule
that depends on one is arithmetic. ⚠ **And so are nested spans**, which this sentence listed as owed
until the blocker was read rather than repeated: it was never the rebasing of a union inside a union,
which was already free — it was one box's fragments being a contiguous slice of a shared scratch,
which two boxes open at the same line's end cannot both have. What is still owed is generated boxes,
a span with an out-of-flow child, and a span's own strut. See
[the inline section](#inline-formatting-and-the-invariant-nobody-had-written-down) and
`InlineKnownGaps.txt`.

| | |
|---|---|
| `LayoutTree` | The store: styles, results, links and node state as parallel `NativeArray`s, plus the tree operations and the whole style surface. |
| `LayoutStyle`, `StyleLength` | Every length as a `(value, unit)` pair, all nine CSS edges kept apart. |
| `StyleResolution`, `FlexAxis` | Edge precedence, percentages, box sizing; flow-relative to physical. |
| `LayoutTree.CalculateLayout` | The algorithm: flex basis, line breaking, the two-pass free-space distribution, justification, cross-axis alignment, multi-line alignment, absolute positioning, pixel-grid rounding. |
| `LayoutTree.Block`, `CollapsibleMargin` | The second algorithm: block stacking, the inline-axis fill, CSS 2.1 §8.3.1 margin collapsing, auto margins, the intrinsic-width probe. |
| `LayoutTree.Grid*`, `GridTrackSize`, `TrackArena` | The third: CSS Grid §8 placement, §12 track sizing, `fr`/`minmax`/`fit-content`/`repeat`, and the variable-length track lists that made a second arena necessary. |
| `LayoutTree.Inline`, `VerticalAlign` | The fourth: CSS 2.1 §9.4.2 line boxes, §10.3.9 shrink-to-fit, §10.8.1 baselines and vertical alignment. |
| `LayoutTree.Fragments`, `LayoutFragment`, `FragmentArena` | CSS Display §2.2 fragmentation, and the third arena — the only one on the *output* side. What relaxed *one node produces one box*, and the zero default that kept every existing consumer of `GetLeft` unchanged. |
| `LayoutTree.Intrinsic` | CSS Sizing § 5's `min-content`/`max-content`/`fit-content` on the six size slots. A bottom-up pre-pass that measures the node and substitutes a `Point` before the algorithm runs, because a parent settles a child's width before it hands it down and there is no seam downstream of every such read. |
| `LayoutTree.Order` | §5.4 `order`, the one part that is not Yoga's. One redirection: the algorithm reaches children only through `ChildIds`, so sorting what that returns is the whole property. |
| `Generated/` | 534 conformance fixtures, translated from Yoga by `Tools/Vixen.YogaTestGen`. |
| `Taffy/` | 5 524 more, from Taffy, vetted by `Tools/Vixen.TaffyTestGen`. A second browser-derived opinion on flexbox, and the oracle block, grid and float **were** judged by. Every category now runs per fixture, and nothing in any of the eight is refused. |

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

### The same argument, for grid and for inline

`AutomaticMinimumSizeTests` closes the hole for flex. `GridBlindSpotTests` and
`InlineBlindSpotTests` are its equivalents, and the three gaps they name were **measured on the
tree** rather than assumed:

| Blind spot | The measurement |
|---|---|
| **Direction inheritance** | Every one of Taffy's 22 776 nodes states its own `direction` — the count of ` direction="` across the eight corpus files is 22 776 exactly. So `Direction.Inherit` is never stored, and `StyleResolution.ResolveDirection`'s owner argument is never read. Rewriting it to ignore its owner leaves **every Taffy fixture green** and turns **374 of Yoga's 534** red, plus two in `ScrollbarGutterTests` and three across the two inline files — and ⚠ **not one grid test anywhere**. |
| **Every fixture is a cold layout** | Nothing in either corpus lays a changed tree out twice, so dirty propagation, the measure cache and line-box reuse are asserted by nothing. Breaking `MarkDirtyAndPropagate` so that it marks its node and never reaches an ancestor leaves **all eight Taffy corpora and all 534 Yoga fixtures green**, and takes down eight hand-written tests. |
| **Every fixture rounds at scale 1 or not at all** | A fractional `PointScaleFactor` — a retina editor — is untested by them. `PixelRoundingTests` had the only coverage of it, on flex. |

The oracle for the last two is `PixelRoundingTests`': a second tree built from scratch with the same
styles and laid out cold, which by construction takes no shortcut. ⚠ **A line box is the case that
oracle matters most for**, because its advance is a running sum — rounding each box's width on its
own and then laying them end to end accumulates, so an error in the first box moves every box after
it.

### A second corpus, and what it found

**[Taffy's 5 524 fixtures](../Vixen.Ui.Layout.Tests/Taffy/README.md) now run beside Yoga's 534**, per
doc 43 § B0. They are the same kind of artefact from a different engine — HTML laid out by
Chrome-for-Testing — and they exist here for block and grid, which have no oracle at all. Flexbox got
them first on purpose: it is the one mode where the answer is already known, so a wrong harness would
be visibly wrong there rather than invisibly wrong inside grid later.

**2 242 of the 2 256 runnable flex fixtures pass**, up from 2 002 at the corpus's first run.
Thirteen of the original failures were the bridge and were fixed — `start` is not a spelling of
`flex-start`, and `self-start` resolves against the item's own direction. Of the 206 that were
Vixen's, **192 are closed** and 14 remain, catalogued in `Taffy/KnownGaps.txt`.

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

After that come `aspect-ratio` against a clamped cross size and baseline alignment past the simple
case. Min-larger-than-max precedence is also closed: CSS Sizing §5.1 makes it the *order* of two
clamps rather than a special case, and `BoundAxisWithinMinAndMax` was returning the moment the
maximum bit, so the minimum below it was never read.

### §9.7's free space is not the line's size

⚠ **The bucket here was recorded as a missing re-distribution loop, and that diagnosis was wrong.**
The loop is present and correct. What it was handed was a pool that had already paid for the clamp.

§9.3 collects items into lines by each item's outer **hypothetical** main size — its flex base
clamped by the used min and max. §9.7 step 3 builds the free space out of a **different** sum: the
frozen items' target sizes and the unfrozen items' *unclamped* flex base sizes. One field served
both, so a clamp was charged twice — once by shrinking the pool it came out of, and again by the
pass that re-applied it to the item. `FlexLine` now carries the two sums separately and says on each
which question it answers.

Step 2 came with it, and it is where the §4.5 leftovers went: an item with no usable flex factor is
**frozen at its hypothetical main size**, which is where §4.5's automatic minimum already lived, so
an item that neither grows nor shrinks finally sees its own floor with nothing new applied anywhere.
Freezing also removes the item's flex *factor*, not just its size — leaving the factor in was a
latent divide-by-zero that surfaced as a `NaN` width the moment step 2 arrived.

⚠ **The sentence that was left open is the same mistake one level down, and it is closed.**
`ComputedFlexBasis` was read back out of a trial layout that had already clamped it, so it was not a
flex base size at all: §9.2 makes the flex **base** size and the **hypothetical** main size two
numbers, and this was the second wearing the first's name. An empty `min-width: 60px` box reported 60
where §9.2 says its base is 0, base and hypothetical agreed by construction, §9.7 step 2 could never
freeze the item, and `min_width` answered 80 and 20 where Chrome says 60 and 40.
`LayoutResult.UnclampedMeasuredDimensions` records each axis's measurement before `BoundAxis`, at
every site that writes a measurement — all three formatting contexts, the inline one, and the
measurement cache, which replays it for the reason it already replays the collapsible margins.
`LayoutTree.SetMeasuredDimension` is what stops the two being written apart.

⚠ **The half that was not scoped is that the overflow test read the same field**, and it is what turns
this from +12-with-four-regressions into +12. STEP 3 summed the items' flex *bases* to decide whether
the main axis overflows; that is §9.3's question and §9.3 asks it of the outer hypothetical sizes. The
two agreed only because the base was the clamped measurement. With a real base,
`gap_column_gap_wrap_align_stretch` read five zero-basis items as 20 points of gap in a 300-point row,
concluded nothing overflowed, and stretched every item to the container's full height instead of
halving it between the two lines it still broke into.

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
only in whether a scrollbar gutter is reserved *when there is nothing to scroll* — so
`LayoutStyleBuilder` maps `auto` onto `Scroll` rather than splitting every `== Scroll` here in two.
The keyword itself survives in the computed style if an engine that wants the always/sometimes
distinction ever needs to tell them apart.

⚠ **The second half of that argument used to be "and nothing above this draws a scrollbar of its
own", and it was wrong for a reason worth keeping.** The gutter is not a painting concern: a scroll
container reserves `scrollbar-width` inside its padding box and *every size below it* is computed
against what is left, so a store with no such field puts `ScrollView`'s content beside the bar at
the wrong width. 180 of Taffy's fixtures were the bill, and `LayoutStyle.ScrollbarWidth` is the
field. What survives of the argument is only the `Auto` half above — whether the gutter is reserved
unconditionally — and that is genuinely a distinction nothing here can act on.

The gutter crosses the axes (`overflow-y` reserves *width*), sits at the inline-end edge, shrinks the
content box, and does **not** raise the node's minimum size or take part in `box-sizing`. Those last
two are the traps: see `LayoutStyle.ScrollbarWidth`, and
`StyleResolution.ContentInsetForAxis`, which exists beside `PaddingAndBorderForAxis` so that each
call site has to say which of the two it means.

### What is not implemented, and why

- `display: contents` — outside the algorithm scope doc 09 states. The nine fixtures using it are
  skipped by name.
- Yoga's errata flags and experimental features — a default configuration turns none of them on, so
  porting them would be porting dead branches.
- The separate min-content measure callback. Its fallback — asking the ordinary measure function
  under `AtMost 0` — is what a text measurer answers with its longest word anyway, and it is what
  `LayoutTree.Intrinsic` uses to answer `width: min-content`.
- `LayoutUnit.Stretch`. It is carried in the enum and resolved by nothing: no utility emits it, and
  the two generated fixtures that set it want different answers from the same keyword —
  `Stretch_width` the containing block's width, `Stretch_flex_basis_column` its own content's height.
  Both pass while it behaves as `undefined`, each falling through to a different default.

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

**`display: flow-root` is a member and not an alias**, for the one reason the keyword exists: it
establishes a block formatting context whatever `overflow` says. Aliasing it onto `Block` would be
wrong on precisely the fixture that tests it — `block_flow_root_margin_non_collapse` puts a
`flow-root` beside a plain `block` with byte-identical content and Chrome makes one 10 points tall
and the other 60. So `EstablishesBlockFormattingContext` has a clause for it and
`BlockMarginsCollapsibleWithParent` deliberately has none: that method's two literal `Display.Block`
tests are what stop a margin escaping through a flow root in either direction.

**Floats are implemented, and this paragraph's prediction about them was half right.** It said they
would attach at exactly two points: the intrinsic-width probe, which now routes a floated child into
an accumulator instead of the running maximum, and the in-flow walk, which would "ask a float context
for a content slot". The first is four lines and exactly as forecast. The second was the wrong shape
— a float context is not something the walk *asks*, it is something the walk has to position itself
*inside* — and it cost a probe pass per child, a saved and restored origin, a suppressed layout cache
and a new file, `LayoutTree.Floats.cs`. The claim that nothing in the walk cached an assumption a
float could not later narrow did hold up.

The exclusion list is per block formatting context, in the context root's content coordinates, and a
nested context hides the outer one's entries behind a mark rather than allocating a second list. Four
things read it: placing the next float (§9.5.1), keeping a formatting-context root's border box off a
float's margin box (§9.5), clearance (§9.5.2) and a root's contains-its-floats height (§10.6.3). All
92 fixtures pass — the 84 in the `float` corpus plus the eight `block_flow_root_*` ones that needed a
flow root *and* a float, four of which had already changed census buckets once when `flow-root`
landed.

⚠ **This paragraph used to say a line box does not shorten as it passes a float, and that
`LayoutTree.Inline` has no exclusion awareness at all. Both halves are false and have been since the
line walk learned §9.5.** `WalkInlineLines` asks `InlineBandForLine` for the band at each line's own
top and height, shortens the line box to it, applies §9.5's shift-downward clause when the band is
too narrow for the first item, and places a float declared inside a run at the top of the line it was
written on. Eighteen tests in `InlineFloatInteractionTests` hold it, and their expectations had to be
read out of Chrome 148 case by case — because the reason the gap survived measurement is unchanged
and is the thing worth keeping in this paragraph: `Corpus/float.xml` has no `<text>` element in it,
so the corpus named after the feature is entirely block-level and cannot see the feature's headline
rule. A text leaf breaking around a float's *staircase* has since landed for a **block-level** leaf, which
is the case §9.5 is about — `LayoutTree.ContentBands` is the query it asks — and what is left is the
**inline-level** one; see `InlineKnownGaps.txt` and `Taffy/FloatKnownGaps.txt`, and
`docs/guide/ui/floats.md` for the shape of what is there.

⚠ **A float-bearing tree pays for the cache.** A cache hit returns a node's size without re-running
its layout, and a block container's layout has the side effect of appending its floats to the
formatting context around it — six replayed numbers cannot replay that. So `CalculateLayoutInternal`
bypasses the cache whenever the tree contains a float or a `clear`, decided by one scan of the style
array per pass. A tree with neither is byte-for-byte the tree it was.

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

## Grid, and the part with no oracle

Doc 43 § B2, and the largest single item in that plan at 3.5 EM of about 19. **The corpus was
already here**: 2 040 `grid` fixtures plus 56 `blockgrid` and 24 `gridflex`, every one refused at
exactly one point — the `display` keyword — since B0 committed them. The prediction paid out for the
second time: they went from 8 passing to 1 526 in the commit that added the keyword and the
algorithm behind it — 1 622 as of §9's containing block — and nothing about the harness changed.

### What a *third* algorithm cost, and it was not what block cost

Block's whole price was three **outputs**: a `CollapsibleMargin` at each end and a collapse-through
flag, because a child's top margin may belong to its parent. Grid needed none of them — it is a
barrier to margin collapsing exactly as a flex container is, so its honest answer to all three is
"my own margin, and no", which `CalculateLayoutImpl` already writes before dispatching.

**What grid needed is on the input side, and it is a second arena.** `grid-template-columns` is an
arbitrary-length list of sizing functions; `LayoutStyle` is a fixed-size unmanaged struct in a
`NativeArray`, which is the whole reason a hundred thousand nodes are four allocations. So the four
track-list properties live in `TrackArena` and the style carries a `(offset, count)` handle into it —
the same shape, and for the same reason, as `ChildArena`. A node that sets no template pays one `-1`.

It has to hold rather more than a stylesheet suggests: `repeat(40000, 10px 10px)` is a legal
declaration and the corpus contains it, so fixed repetitions are expanded once on write and
`LayoutLimits.MaximumGridTracks` is what stops a list being unbounded.

⚠ **`GridTrackList` is the `<track-list>` grammar, and it is in this project deliberately** — the
only text parsing here, and the exception is argued rather than accidental. It is the inverse of
`GridTrackSize.ToString`, which already emits `minmax(0,1fr)`; and it is what lets the conformance
corpus and the CSS bridge read a track list with the same lines. That matters more than the layering
does: every one of the passing grid fixtures arrives through `TaffyStyleMap` and never touches
CSS, so a grammar written only for stylesheets would have had no adversarial coverage at all.
`TaffyTrackListParser` is now an adapter between its returned refusal and the corpus's thrown one.

⚠ **Two consequences worth knowing.** A whole-style write has to carry the destination node's own
handles across, because a `LayoutStyle` copied between nodes would alias a block one of them will
later free — which is why those four fields are `internal` and set only through
`SetGridTemplateColumns` and its siblings. And the per-pass working storage is a **bump allocator
with a watermark** (`GridScratch`), because a grid can contain a grid: track sizing measures its
items, and measuring an item may run the whole algorithm on another grid container whose scratch
must not overwrite the outer one's.

### `grid-template-areas` has zero fixtures, and it landed against somebody else's suite

⚠ **This is the one part of grid with no oracle in either corpus.** Taffy's own XML harness leaves
`grid-template-areas` at `Default::default()`, so not one of the 5 524 fixtures sets it — verified
across all eight corpus files, not assumed. Named lines are the same story: no track list in the
corpus contains a `[name]`, and all 6 636 placement values match `-?<int>` or `span <int>` exactly.

This section used to end "it is left out and recorded as left out", with one condition on
implementing it later: **write the oracle first**. That is what `GridTemplateAreasTests` is.
`web-platform-tests`' `css/css-grid/grid-definition/grid-support-grid-template-areas-001.html` drives
thirty values through `getComputedStyle` and asserts each one's **serialisation**, plus sixteen it
requires to compute to `none` — and it is a far better oracle than a reftest, because a serialisation
is an assertion about the *tokenisation*. Six of the thirty come back different from how they were
written, which is the whole of what it can see that a geometric test cannot: a run of full stops is
**one** null cell, so `".a..."` is three columns and not five, and a per-character reading round-trips
its own mistake into a grid that lays out at the wrong width.

⚠ **Eight of the sixteen refusals lay out perfectly well if they are accepted**, which is why the
refusal half is worth as much as the acceptance half. `"a b a"`, `"a b" "b a"` and four more are
areas that are not a single filled rectangle; an implementation that takes each name's bounding box
and asks no further question puts an item over cells another area owns and says nothing. The three
row-count mismatches are the other half — §7.3 invalidates the whole declaration rather than the
row, so a parser that padded the short row would build a grid nobody wrote.

⚠ **And the placement half needed three things outside this assembly, any one of which left out
would have shipped a property nothing could use.** `grid-area` was a shorthand `ShorthandExpansion`
deliberately did not expand — its own header said so — so `grid-area: header`, which is how a named
area is written, resolved and did nothing. §8.4's rule that an omitted edge repeats a
`<custom-ident>` and not a number was unreachable while a name could not be stored, and a comment in
that file warned that whoever added names had to add the duplication in the same change. And a
placement longhand now has two grammars, so `LayoutStyleBuilder` reads each of the four twice — once
as a line, once as a name — and exactly one of the two readers reports a refusal.

⚠ **What is still not implemented is named lines written into a track list** — `[col] 50px [col]`.
WPT's files for those are reftests whose geometry is not stated, `GridTrackList` has nowhere to put a
name, and no fixture in either corpus writes one. `grid-template-areas`' implicit `name-start` and
`name-end` lines are the half that had an oracle.

⚠ **One divergence, pinned rather than left to drift**: §8.3 says a name matching no line makes every
*implicit* line carry it, which places an item on a line the author never wrote. This store
auto-places instead, which is what makes a typo look like a typo.
`GridTemplateAreasTests.A_name_no_area_carries_is_auto_placed` is that decision written down.

### What is done, per feature, and what is not

| Feature | State |
|---|---|
| Placement (§8): lines, negatives, spans, auto-placement, sparse and dense, both flows | **done** — every `grid_placement_*`, `grid_auto_flow_*` and non-indefinite `grid_span_*` family is green |
| Track sizing (§12): base sizes, growth limits, the five §12.5.1 rounds, maximise, `fr`, stretch | **mostly** — 472 of the 548 `grid_flex_track_*` fixtures, which is the family that exercises it hardest |
| `minmax()`, including a maximum below its minimum | **done** for the unspanned case; a *spanned* clamped pair is a listed gap |
| `repeat()`, `auto-fill`, `auto-fit` including collapsing | **done** |
| `fit-content()` | **done** against a definite container; a percentage argument against an indefinite one is listed |
| Gaps, including percentage gaps | **done** |
| `justify-*`/`align-*` items, self and content, including §4.4's `safe` overflow fallback | **done** |
| Baseline alignment (§11.8) | **done** — `ResolveBaselineShims`, and every `grid_align_items_baseline_*` family is green |
| An out-of-flow child's grid area as its containing block (§9) | **done** — 96 `grid_absolute_*` fixtures, see below |
| `grid-template-areas` (§7.3), including the implicit `name-start`/`name-end` lines | **done** — no corpus fixture sets it; the oracle is WPT, see above |
| Named lines in a track list, `subgrid`, `masonry` | **not implemented**, no oracle |

⚠ **Two rows of that table said "not implemented" long after they were**, which is worth recording
because it is the same staleness the state document is written to prevent: §11.8 and §9 both landed
from separate branches and `GridKnownGaps.txt` says so at the top, while this table went on naming
them as the largest gap. Where the two disagree the gaps file is the one measured by a test.

⚠ **One thing about §9 is a measurement rather than a judgement.** The static-position half — record
each abspos child's grid-area corner, reuse block's `BlockStaticLeft`/`BlockStaticTop` pair, let the
absolute walk read it for a grid parent too — was written, measured, and **taken back out**: it fixed
six fixtures and broke eight, for a net loss of two. The half that pays is resolving an *inset*
against the area, and that needs a per-child containing block inside `LayoutTree.Absolute`, a file
shared with Yoga's 534. Doing the cheap half alone is worse than doing neither.

### What the corpus cannot see, again

The README's standing warning, now with a third instance. Two rules in grid are invisible to all
2 120 fixtures:

- **Line 0 does not exist.** §8.3 numbers lines from 1 and from −1 with nothing between, so a
  declared `grid-column-start: 0` is invalid and computes to `auto`. No fixture writes one — the
  corpus's 6 636 placement values contain no zero — so `GridPlacement.Line(0)` returning `Auto` is
  a rule with no fixture over it.
- **`grid-auto-rows` is a cycling list.** Two fixtures use the three-value form and both happen to
  have three implicit tracks, so reading only the *first* entry passes them; it takes a fourth
  implicit track to tell a cycling list from a repeated first element.

And one gap is in a **shared** helper rather than in grid: `ComputeMinContentSizeUncached` has no
grid branch, so a grid asked for its min-content size sums its children along `FlexDirection` as
though every container were a flex one. Exactly one fixture (`grid_min_content_flex_column`) reaches
it, because the corpus's grids are almost always the root.

## Inline formatting, and the invariant nobody had written down

Doc 43 § B3, the last of the three big layout modes and the fourth algorithm. **What it landed is
atomic inlines: `inline`, `inline-block` and `inline-flex` are real keywords, a container whose
in-flow children are all inline-level lays them onto line boxes, and `vertical-align` has three of
its eight values.** What it did *not* land was one fact rather than a list — and that fact has since
been paid for, which is the interesting part twice over.

### What a *fourth* algorithm cost, and what it asked for and eventually got

Block cost three **outputs**. Grid cost variable-length **input** and a second arena. Inline cost
**one output and no arena** — and then hit a wall, which came down later for a third arena on the
**output** side.

The output is `LayoutResult.InlineBaseline`, and it is an output for a different reason than block's
margins were. A collapsible margin is an output because it belongs to somebody else; this is an
output because it is **not recomputable**. CSS 2.1 §10.8.1 puts an `inline-block`'s baseline on its
*last* line box, and `CalculateBaseline` reconstructs a flex container's baseline by descending into
a child — which works because a flex container's baseline *is* a child's. **A line box is not a
node**: no id, no style, no entry in the child arena. There is nothing to descend into, so the walk
records it or nobody can ask. It is replayed by the measure cache for exactly the reason block's
three are, and the failure mode is quieter: a cache hit restoring the sizes and not this aligns a
nested `inline-block` against whichever baseline the node's last *full* layout left behind — right
cold, wrong incrementally.

**The arena grid needed is not needed here, and the reason is worth stating because it looked like it
would be.** A line box holds an arbitrary number of items, which is the shape that forced
`TrackArena`. But a line is a *contiguous range of the existing child span* — exactly as a flex line
is — and every item's size is already sitting in `results[child].MeasuredDimensions` from the sizing
pass. So the two passes each line needs (one to find the baseline, one to place against it) are two
loops over an index range, and the whole algorithm allocates nothing. Variable-length **output** was
the thing grid did not have and inline does not either; what grid needed was variable-length *input*,
and a line box has none.

⚠ **The wall was an invariant that three algorithms preserved without ever having to say so: one
node produces one box.** A `LayoutResult` holds one `Position` and one `Dimensions`; `GetLeft`, the
rounding pass, the absolute walk and hit testing all rested on it, and it is what makes a hundred
thousand nodes four allocations. CSS Display §2.2's non-replaced `inline` box breaks it — a `span`
crossing a line break is **fragmented** into one box per line, with the horizontal border and
padding drawn at the two real ends and not at the breaks.

### The wall came down for one arena and three ints, and nothing downstream changed

`FragmentArena` is variable-length **output** — the same shape `TrackArena` is on the input side —
and `LayoutResult` carries a handle into it. The thing that made it additive rather than a migration
is the zero default: **`FragmentCount == 0` means "one box, and it is `Position` and `Dimensions`,
exactly as before"**. So `GetLeft` was not touched, the rounding pass gained one loop, and the
absolute walk gained nothing at all — nor did the four properties on `Vixen.Ui`'s `UiElement` that
every hit test and draw list in the engine funnels through.

⚠ **When the count is non-zero, `Position` and `Dimensions` hold the *union* of the fragments, and
that is CSS's own answer rather than a compromise.** CSS 2.1 §10.1 makes the containing block of an
absolutely positioned descendant of an inline box the bounding box of its first and last fragments.
The union is therefore exactly what the absolute walk wants, which is why the absolute walk needed
nothing. Individual boxes are there for whoever needs them — `GetFragmentCount` and `GetFragment`,
the latter reporting which of the box's *real* ends each fragment carries so a painter knows which
vertical border to stroke and which break to leave open.

⚠ **The other direction — a box with *no* node — is not served by any of this, and half of it has
landed anyway.** An anonymous block box (§9.2.1.1) and a generated box (`::before`, doc 43's A12)
are not fragments: storing one against a nearby node would give it that node's style. The two are
also not the same as each other, and that is what let one of them through. An anonymous block box
takes initial values for every non-inherited property, so it is never painted and never hit-tested
and **needs no stored rectangle at all** — only a line walk over a sub-range of a container's
children, which is what `WalkInlineLines` now takes and what `WalkBlockChildren` hands it for each
run of inline-level children in a mixed container. Nothing was added to `LayoutResult` for it.
A generated box still needs a style slot, which is a different problem and is still open. See
`InlineKnownGaps.txt`, which costs them separately.

⚠ **The claim that a line box allocates nothing survived, and it was the thing most at risk.** A
line used to be a *contiguous range of the existing child span*, exactly as a flex line is, and
every item's size was already on the item. A span breaks that: it is not *on* a line, its children
are, and it contributes only its two horizontal edges wherever they fall. So a line is now a
contiguous range of a flattened Open/Atomic/Close stream — held in a watermarked buffer reused
across passes, alongside a second one for fragments in flight. `LayoutPassTests`'s zero-byte gate
holds with a span re-fragmenting on every frame, which is what makes a side arena affordable where a
list per node would not have been.

⚠ **`inline-block` genuinely does not take the whole line, and the mechanism was already in the
store.** §10.3.9's shrink-to-fit is `SizingMode.FitContent`, which the block path has branched on
since B1 and the flex path since Yoga's 534. What was missing for two plan items was not the
arithmetic — it was a *caller* that asked for it. Doc 43 § F4 read the absence of the keyword as the
absence of the sizing; only the first half was true.

### There is no second text wrapper, and that was the sharpest design constraint

`Vixen.Ui`'s `TextLayout` already breaks a string into lines across a font-fallback chain — in
pixels, across faces, which is why it lives there and not in `Vixen.Ui.Text`'s single-font
`LineWrapper`. It reaches this store the way every leaf does: as a measure function. The inline walk
treats such a leaf as **one atomic item** and asks it exactly the question the measure cache is keyed
on. A second wrapper breaking text inside the line box would disagree with the first about kerning,
fallback and UAX #14 the moment either changed.

The cost is stated rather than hidden: **an inline-level text leaf's first line is not shortened to
the space left on the line box it lands on.** ⚠ And it is *still* not, now that fragmentation has
landed — which is worth saying because the two were filed as the same blocker and are not. There is
now somewhere to put a shortened first line, and the reason it was refused was never storage.
⚠ Nor is it the same item as the float staircase any more, which landed: what stops the same band
query serving this one is a **pass order** rather than a protocol, because `WalkInlineLines` sizes
every item before it breaks a single line, so at the moment such a leaf is measured there is no line
box for it to be shortened to.

⚠ **Nor was it a second wrapper, which this section asserted until #901 was audited.** Both routes to
a staircase call `TextLayout` — the first wrapper — either once per line or once with a band list, so
no rival is created and the paragraph above is a true statement about a thing nobody proposed. What
the refusal reduces to is narrower and worth writing down: the *answer* never had to change, because
CSS 2.1 §9.5 shortens **line boxes** beside a float and leaves the block box itself full width, so a
text leaf's measured size is one rectangle either way; only the *question* would have to carry the
band, and `MeasureRequest` already carries `Tree` and `Node`. The one thing that would have made such
a query unsound — the measure cache serving one width's answer at a different `y` — is already gone:
`CalculateLayoutInternal` bypasses the cache outright whenever `treeHasFloats`, for the reason two
paragraphs up, and `floatOriginY` is the child's own top edge by the time its measure function runs.
That is what landed, as `LayoutTree.ContentBands`: a block-level leaf asks for the room each of its
own lines has, from inside its own measure function, and gets one entry per line slot for as long as
a float takes any of it away. ⚠ **Two of the three things this paragraph used to say were owed were
not needed.** A `TextLine` carrying a per-line inline offset already existed — `text-indent` put it
there, and the draw list, the caret, the selection band and the hit test have read it all along. And
`LineWrapper` did not have to take a per-line width: a greedy wrapper's state at a line boundary is
one integer, so asking it for the *first* line of what is left, at this line's own width, is the same
answer — which is why the staircase landed with nothing changed below `Vixen.Ui` at all. ⚠ The day text breaking does move into the line box, Vixen's UAX #14 conformance stops being
the right target: browsers do not implement it as written, and the reference for any change to a
break position is Parley's `break_overrides.rs` or the 2 048 Chrome-recorded positions beside it —
not the algorithm.

### The oracle had to be fetched, and most of it could not cross

⚠ **Neither corpus has anything to say here, and this was verified by enumeration rather than
assumed.** Taffy's `display` attribute takes exactly five values across all eight files — `block`
2 276, `flex` 764, `flow-root` 12, `grid` 2 496, `none` 68 — and not one is inline. Every occurrence
of the string `inline` in the corpus is a *test name* about a grid's inline axis; every occurrence of
`vertical` is a test name too. Yoga's 534 are the same. Taffy does not do inline layout at all — it
delegates measurement — so it is not even a code reference. **This is the first mode to arrive with
zero fixtures**, where block and grid each went from single digits to four figures the day their
keyword was mapped.

The oracle is **`web-platform-tests`, BSD-3-Clause**, re-expressed rather than translated exactly as
`OrderTests` re-expresses WPT's `order` tests. What crossed is
`css/css-flexbox/inline-flex.html`: three 50×50 boxes — `inline-block`, `inline-flex`,
`inline-block` — with `data-offset-x` of 0, 50 and 100, `data-offset-y` of 0 throughout, and two
`flex: 1` children at `data-expected-width` 25. **Every box carries an explicit size, so not one
number depends on a font**, and that is precisely why it crossed when the rest did not.

⚠ **What stopped the rest is structural rather than licensing.** A line box's height and baseline
depend on the *strut* — the container's own font ascent and descent — so almost every inline test in
WPT is implicitly a font test, and this store has no font. `css/css-inline/` is 24 files of reftests
and crashtests plus one useful subdirectory whose fixtures size their boxes in `em`. Doc 43 counted
14 `check-layout` tests under `css-inline` against 510 under `css-grid`; the ratio is the finding.

One reachable oracle is named and **not** taken: `css/css-sizing/keyword-sizes-on-inline-block.html`
is written in Ahem, whose glyphs are 1em × 1em by specification and which this repo already models in
`Taffy/TaffyAhemMeasure.cs` — so it is reproducible despite being a text test. Its blocker used to be
`min-content`/`max-content`/`fit-content` as *keyword sizes*, which `StyleLength` did not carry; it
does now (`LayoutTree.Intrinsic`), so what is left is the import itself rather than a missing
feature.

`InlineKnownGaps.txt` is shaped differently from its three siblings for the same reason: with no
corpus there are no fixture names to list, so it lists **rules**, each marked as implemented,
deliberately absent, or absent for want of an oracle.

### And the gate could not see it, for the third time

⚠ `vertical-align` sat in `InertProperties.txt` with a task number, and the entry would have gone on
being green after this landed: every one of the nine scenes in `UtilityConsumptionProbe` made the
probe a flex, block or grid container, and CSS applies `vertical-align` to none of those. A tenth
scene — `inlined`, the only one with a line box in it — is what closes that, and it was added in the
same commit as the algorithm. This is the same failure the `gridded` scene records and the same one
flex-shrink hit; three instances now, and the pattern is that **a property needs a scene that puts it
in the situation it is defined for**, which is not the situation the other scenes happen to provide.

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

**Named grid lines in a track list, `subgrid` and `masonry`** — the parts of grid still with no
oracle in either corpus. See the grid section above for why writing them against expectations of our
own devising is the wrong trade.

**What remains in grid is arithmetic rather than a missing feature**, listed per fixture in
`Taffy/GridKnownGaps.txt`. The three whole features this section used to name have all landed.
§11.8's baseline alignment and §9's containing block for an out-of-flow child came from separate
branches — the grid area is cut out of the finished tracks and handed to `LayoutTree.Absolute` as a
per-child rectangle, which is what closed 96 `grid_absolute_*` fixtures. ⚠ **`grid-template-areas`
came from somewhere else entirely: it is the first layout feature here judged by a suite neither
corpus contains**, and the condition this section set on it — write the oracle first — was met by
lifting WPT's parsing suite case for case rather than by re-expressing a reftest's geometry.

**Generated boxes** — the part of inline formatting still open. Four of the five this line used to
name have closed: non-atomic inline fragmentation, anonymous block boxes for mixed content,
`text-align`, and ⚠ **the strut**, whose refusal was the right answer to the wrong question. "A
strut is font metrics and this store has no font" is true; what does not follow is that the store
cannot have one, because the five numbers a font produces cross this boundary as easily as a
resolved `font-size` does. With them come `line-height` on a container, a line that is never shorter
than its text, and the five font-relative `vertical-align` values. See [the inline section](#inline-formatting-and-the-invariant-nobody-had-written-down)
and `Taffy/../InlineKnownGaps.txt`.

⚠ **`text-align` is two fields, and both are implemented.** CSS Text §7.1's three legacy keywords —
`-webkit-left`, `-webkit-center`, `-webkit-right` — align a block container's *block-level children*
rather than its inline content, which is a block-layout rule needing no line box at all:
`LegacyTextAlign` on `LayoutStyle`, read once in `WalkBlockChildren`, sixteen Taffy fixtures. The
inline half — distributing the items on a *line* — is `TextAlign`, read once in `PlaceLine`, and it
has no oracle in either corpus, so `InlineTextAlignTests` is closed-form rather than recorded. ⚠ One
CSS property, two fields, because a container can hold both kinds of child and the two answers are
not the same answer. `justify` is refused at the stylesheet bridge rather than aliased.

⚠ **A legacy keyword writes both fields, and this paragraph used to say it wrote only the block
one.** `-webkit-center` is not a third alignment: it is `center` *plus* a block-level rule. The
proof is the element the value exists for — a browser's UA stylesheet puts
`text-align: -webkit-center` on `<center>` and nothing else, and `<center>` centres its text. So the
two keyword tables in `LayoutStyleBuilder` are deliberately not disjoint, and the pair of lookups
that reads them is deliberately not an `else`. ⚠ The Taffy harness still writes only
`LegacyTextAlign`, because it reproduces Taffy's model rather than CSS's and every fixture in that
corpus holds block children only.

**Floats** — *done for block-level content, owed for inline.* All 92 fixtures pass. What none of
them tests, and what is therefore still owed, is a line box narrowing beside a float: there is no
`<text>` anywhere in the corpus named after the feature. See the block section above.

**`aspect-ratio` re-applied after a box's size is clamped or stretched** — *done*. It was one rule
in three places: the sixteen-family flex bucket, five block families and four grid ones, all of them
the same failure to carry a minimum or a maximum across the ratio into the other axis, and all of
them closed together. `LayoutTree.Helpers.ResolveAspectBounds` merges the bounds so that one clamp
settles both axes; `LayoutTree.Absolute` re-derives the axis the ratio owns after clamping and drops
the over-constrained inset rather than the ratio; and the flex-versus-grid split over whether a
stretched axis accepts a transferred bound is `IsFlexItem` against `IsFlexOrGridItem`. It emptied
`BlockKnownGaps.txt` and cost nothing in Yoga's 534.
Auto margins on an absolutely positioned box (CSS 2.1 §10.3.7 and §10.6.4) were the other half of
that sentence and are now implemented, judged by the 22 `block_absolute_margin_auto_*_with_inset`
fixtures and, for the cases none of them reaches, by `AbsoluteAutoMarginTests`.

**CSS containment (`contain`)** — *four of the five values are here; the fifth is refused.* The
property is `LayoutStyle.Containment`, a `[Flags] Containment`, read from a stylesheet by
`Vixen.Ui.ContainmentReader`. ⚠ It is **five independent effects behind one property**, which is why
sizing it as a single item was the mistake: `contain: content` is `layout paint style` and
`contain: strict` adds `size`, and both are whole values here because the only keyword this engine
cannot honour is the one it can measure as inert.

- **`size`** and **`inline-size`** — *done.* One branch at the top of `CalculateLayoutImpl`, above the
  dispatch and above the measure-function leaf: `MeasureNodeWithoutChildren` settles the contained
  axes, and the ordinary algorithm then re-enters with those axes offered as `StretchFit`. ⚠ It is
  emphatically **not** "skip the children" — they are laid out, painted and hit-tested against a box
  they cannot move, which is § 3.2's actual sentence. ⚠ **And the intrinsic pre-pass needed no
  matching half after all**: `ProbeContentSize` asks a content keyword through `CalculateLayoutImpl`
  itself, so `width: max-content` on a contained box resolves through the same branch and
  `LayoutTree.Intrinsic` never learns the property exists.
- **`layout`** and **`paint`** — *done, and the observable half is one sentence each.*
  `EstablishesAbsoluteContainingBlock` is the containing-block half — it replaced the literal
  `PositionType != Static` written out at five sites, four that begin the absolute walk and one that
  decides whether to descend through a child, which had to agree with each other anyway — and
  `EstablishesBlockFormattingContext` is the independent-formatting-context half. The clip is
  `OverflowReader`'s: paint containment is a second *reason* to push the clip `overflow` already
  pushes, so the picture, the hit test and the sticky scrollport keep giving one answer. ⚠ It cuts at
  the border box where CSS says the padding box, because that is where `overflow` cuts here.
- **`style`** — ⛔ **refused in writing, and understood rather than rejected.** It scopes counters and
  quotes, and this engine has neither. The keyword parses and contributes no flag, which is what
  leaves `contain: layout style` still containing layout; an *unrecognised* word drops the whole
  declaration, as CSS does with a value it cannot parse.

⚠ **The instrument came first, because a contained box and an uncontained one draw the same picture
wherever the children happen to fit.** Every fixture in `ContainmentTests` — the store's in
`Vixen.Ui.Layout.Tests`, the stylesheet's in `Vixen.Ui.Tests` — is an auto-sized box whose child
overflows it, and each asserts the child is still where it was in the same test. ⚠ The control arm of
the containing-block fixture writes `position: static` out by hand: `LayoutStyle.Default` is *Yoga's*
initial state and Yoga's `position` is `relative`, so every node in a bare `LayoutTree` is already a
containing block and the test could not otherwise have failed.

⚠ **What is not here is any *pruning*.** Nothing skips a measurement, a layout pass or a draw-list
walk because of a promise made through this property; containment changes what the answer is, not how
long it takes to get. And no `contain-*` utility class is registered — the parity ledger's row stays
`absent` until the family lands. See `docs/guide/ui/containment.md` and
`docs/plan/43-web-styling-parity.md` § Part 9, Bucket 3.

**Parallel layout.** Independent subtrees with a fixed available size are jobs, and text measurement
of siblings is where the win is. `Benchmarks/Vixen.Benchmarks.Ui` now gives the serial number to
beat, and it says the algorithm is not where an incremental frame's time goes — so this waits behind
the rounding pass, which is.

Licensed under Apache-2.0.
