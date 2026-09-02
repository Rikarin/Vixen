# Taffy's conformance corpus

An external oracle for the two layout modes Vixen has no tests for at all, and a second opinion on
the one it does. 5 524 fixtures from [Taffy](https://github.com/DioxusLabs/taffy), each generated
from an HTML file laid out by Chrome-for-Testing, so every expected number is a real browser's.

Doc 43 § B0. Licence: MIT — see the repository `NOTICE` and ADR-015.

## What is here

| | |
|---|---|
| `Corpus/*.xml` | The fixtures, one file per category, each `<test>` embedded **verbatim**. |
| `TaffyCorpus` | Loads them. |
| `TaffyStyleMap` | The attribute map: 56 attributes, every one either applied or refused by name. |
| `TaffyAhemMeasure` | The measure function the `<text>` fixtures were generated against. |
| `TaffyFixtureRunner` | Builds a `LayoutTree`, lays it out, compares to a tenth of a pixel. |
| `KnownGaps.txt` | The flex fixtures Vixen gets wrong, with a diagnosis each. |
| `BlockKnownGaps.txt` | The same for block, and the shape of that file is itself the result. |
| `GridKnownGaps.txt` | The same for grid. |
| `UnsupportedFixtures.txt` | The fixtures that assert **nothing**, by reason and by corpus. |

| Category | Fixtures | Pass | Fail | Refused |
|---|--:|--:|--:|--:|
| `flex` | 2 352 | 2 334 | 18 | 0 |
| `leaf` | 56 | 56 | 0 | 0 |
| `block` | 884 | 884 | 0 | 0 |
| `blockflex` | 28 | 28 | 0 | 0 |
| `blockgrid` | 56 | 56 | 0 | 0 |
| `grid` | 2 040 | 1 998 | 42 | 0 |
| `gridflex` | 24 | 24 | 0 | 0 |
| `float` | 84 | 84 | 0 | 0 |
| | **5 524** | **5 464** | **60** | **0** |

Every one of those numbers is asserted — the pass and fail columns by the four conformance suites,
the refused column additionally by `TaffyUnsupportedCensusTests`, which requires the census to match
`UnsupportedFixtures.txt` line for line.

⚠ **The refused column is zero for the first time, and a zero there is the state
`UnsupportedFixtures.txt` was written to warn about rather than to celebrate.** A census of nothing
and a census that did not run print the same page, so the guard behind that column no longer asserts
anything about refusals: it asserts that 5 524 fixtures reached a pass or a fail, that all eight
corpora contributed, and that `TaffyStyleMap` still refuses a value it does not know when it is
handed one directly. Read that file's TOTAL section before drawing a conclusion from this row.

⚠ **And `float`'s 84 are not the evidence they look like.** The whole corpus is block-level: there is
no `<text>` element in `Corpus/float.xml`, so not one of the 84 tests a line box shortening as it
passes a float, which is the rule everybody means by "float". See `FloatKnownGaps.txt`.

Each fixture name ends in one of four suffixes — `__{border,content}_box_{ltr,rtl}` — so the 5 524
are about 1 381 distinct cases run four ways.

## The result that justified building this first

**2 002 of the 2 208 runnable flex fixtures passed on the first run**, on a store built entirely
against a *different* browser-derived corpus. 176 more are refused for a property this store has no
field for, and 206 disagreed. **192 of those are now closed and 2 354 of the 2 408 pass** — 36 are
still refused and 18 disagree, which is what the table above counts.

That number was the point. Flexbox already had an oracle — Yoga's 534, green — so the flex corpus is
a **known-good target**: if the harness were wrong, it would be visibly wrong here, where the answer
is known, instead of invisibly wrong later, where grid failures would be indistinguishable from
harness failures. B0 goes before B1 and B2 for exactly this reason, and 0.4 EM was the price of
finding out.

**Thirteen of the original failures were the harness, not the algorithm, and all thirteen are
fixed** rather than written down:

- **`start` is not `flex-start`.** `flex-start` is flex-relative and reverses under
  `flex-wrap: wrap-reverse`; `start` is writing-mode-relative and does not. Vixen's `Align`, like
  Yoga's, carries only the flex-relative pair, so `TaffyStyleMap` resolves the difference at
  translation time against the container's wrap. Same story on the main axis, where `*-reverse`
  plays wrap-reverse's part for `justify-content`.
- **`self-start` resolves against the *item's* direction, not the container's**, and on a column
  container the cross axis *is* the inline axis, so the two can disagree.
  `flex_column_align_self_self_start_child_rtl` puts an `rtl` child at x=90 and its `ltr` sibling at
  x=0 from one declaration — which also means a self-relative `align-items` cannot be one
  container-level value, and is pushed down onto the children.

⚠ **Three CSS initial values are not Yoga's, and Chrome produced these numbers.** `flex-direction` is
`row`, `flex-shrink` is `1`, and `align-content` is `stretch`; `LayoutStyle.Default` says `column`,
`0` and `flex-start`, following Yoga's deliberate deviations. `ApplyCssInitialValues` resets them per
node. Getting this wrong does not cost a few fixtures, it costs thousands, and every one would read
as a flexbox bug.

## What the 206 were, and what the 94 are

Real, and grouped with evidence in `KnownGaps.txt`. The largest bucket is worth naming here because
the layout README predicted it, and because **closing it is what the corpus was built for**:

**CSS Flexbox §4.5, the automatic minimum size, when the content size comes from a descendant.** The
README says Yoga's generator "emits no fixture that shrinks a measured leaf past its own content",
and `AutomaticMinimumSizeTests` was hand-written to close that. It closed the *leaf* case — a node
with a measure function. A flex item that is itself a container has a min-content size too, and that
floor was not applied. `align_baseline_child_padding` is the clean demonstration: two 50px siblings
in a 90px content box, and Chrome shrinks one to 40 and the other not at all, because the second is
floored at min(specified 50, content 60) = 50. Vixen shrank both to 45.

**Closed**, together with min-larger-than-max precedence: 206 → 158. The cause was one distinction
CSS Sizing §5.2.2 draws that this store did not — a box's min-content *size* against its min-content
*contribution* — and an empty `width: 50px` box was contributing zero.

The next largest was **`aspect-ratio` against a minimum, a maximum or a stretch**, sixteen families,
and it is closed too: 158 → 94. It was one rule reaching three algorithms — the same sixteen names
appear in the grid and block corpora — so it was fixed once, in
`LayoutTree.Helpers.ResolveAspectBounds` and `LayoutTree.Absolute`, and took five block families and
four grid ones with it. `BlockKnownGaps.txt` has no failures left at all. The heading in
`KnownGaps.txt` carries the three sub-rules and the two fixtures that prove a flex parent and a block
parent answer the same declaration differently.

⚠ **The corpus was not sufficient to close its own biggest finding, and that is the most useful
thing this page can report.** Three further rules were needed, and the three oracles split them
cleanly between them: the percentage rule failed in both corpora, the wrapping rule failed in
**Yoga's alone** while all 2 208 flex fixtures here stayed green, and the clipping rule was invisible
to both and caught only by four committed editor screenshots — where the shell came out 2 385 points
wide inside a 1 100-point window. Ten times the size is not a superset, and neither corpus is a
substitute for looking at the thing.

The rest: §9.7's min/max violation loop (`min_width` — Chrome freezes the violating item and gives
the remainder to its sibling; Vixen raises the basis and splits evenly), `aspect-ratio` not
re-applied after the cross size is clamped, baseline alignment past the simple case, cyclic
percentage gaps, and `display: none` on the root.

## What the corpus does **not** cover

The layout README's best section, done for this corpus. Two findings, one of them demonstrated.

⚠ **Direction inheritance is invisible to it.** The corpus sets `direction` explicitly on **every one
of its 22 776 nodes**, so `Direction.Inherit` is never stored and the owner-direction argument
threaded through the whole algorithm is never read. Demonstrated rather than argued: rewriting
`StyleResolution.ResolveDirection` to return `Ltr` for `Inherit` and ignore its owner leaves **all
2 241 Taffy tests green**, and fails **374 of Yoga's 534**.

That is worth more than a caveat. It says the two corpora are *complementary* rather than one being
a superset — Taffy's is ten times the size and covers two modes Yoga's cannot reach, and Yoga's
still holds the only test of a property Taffy's cannot express. Neither retires the other.

⚠ **`grid-template-areas` is not in it either, which mattered for B2.** Zero of the 2 040 grid
fixtures set it, and the reason is upstream: Taffy's own `tests/xml.rs` builds every other grid
property from the XML and then writes `grid_template_areas: Default::default()`. So named grid areas
— one of the features people reach for grid *for* — arrive with no oracle, and B2 had to bring its
own. It did: `GridTemplateAreasTests` is WPT's
`css/css-grid/grid-definition/grid-support-grid-template-areas-001.html`, thirty accepted values with
their serialisation and sixteen refused ones, quoted case for case. ⚠ **A serialisation assertion is a
better oracle than the reftests beside it**, because six of the thirty come back different from how
they were written — which is an assertion about the tokenisation that no measurement of a box can
make. `TaffyStyleMap` now maps the property instead of refusing it, so a refreshed corpus that starts
writing one asserts rather than skips.

**`order` is absent here too**, exactly as it is from Yoga. Taffy's `Style` has no `order` field and
its XML format has no attribute for it, so `LayoutTree.Order` keeps `OrderTests` and its WPT
transcriptions as its only oracle — see the layout README.

Two more the corpus structurally cannot see, both properties of *how* it is run rather than of what
it contains: every fixture is a **cold layout**, so nothing here exercises dirty propagation, the
measure cache or incremental correctness (`LayoutPassTests` and `PixelRoundingTests` hold that); and
every fixture rounds at **scale 1** or not at all, so a fractional `PointScaleFactor` — a retina
editor — is untested by it.

## Regenerating

The corpus is committed because CI has no reference clone.

```bash
git clone --depth 1 https://github.com/DioxusLabs/taffy.git references/taffy
dotnet run --project Tools/Vixen.TaffyTestGen -- references/taffy Core/Vixen.Ui.Layout.Tests/Taffy/Corpus
```

The tool refuses rather than ignores: an element or attribute name it does not know, or an
expectation tree whose shape does not match its input tree, drops that fixture and names it in the
report. Nothing is dropped silently, and today nothing is dropped at all — 5 524 in, 5 524 out.

⚠ **Committed as XML, not translated into C#, and that is the one structural decision worth
defending.** Yoga's fixtures had to be translated because they were C++; there was no other way to
run them. Taffy's are already language-neutral, so translating them would *add* a step whose bugs are
indistinguishable from layout bugs — which is the exact failure this corpus exists to catch. It is
also a tenth the weight: 5.3 MB of XML against roughly 20 MB and 375 000 lines of generated C# at the
rate `Generated/` runs at, recompiled on every build.

The price is that a mis-read attribute would be a silent no-op rather than a compile error. Two
things pay it: `TaffyStyleMap` throws on an attribute it does not recognise, and
`TaffyCorpusCoverageTests` re-derives the attribute set from the committed corpus and asserts the map
answers for all 56.

## How the pending modes are represented

Not as 3 116 skipped tests, which is noise, and not by deletion, which throws the oracle away. Yoga's
suite skips nine `display: contents` fixtures by name in a generated file's header — fine at nine,
absurd at three thousand.

Instead each pending corpus gets **one** test that runs every fixture in it and pins the tally. The
fixtures really execute, so a crash or a hang in an unimplemented path shows up today; the number
cannot move by accident; and when B1 and B2 land these become the progress meter.

⚠ What the tallies said before B1 was itself a result: **every block and grid fixture was refused at
exactly one point — the `display` keyword.** Not one was failing on arithmetic, because not one
reached any. From this suite's point of view B1 was the day `Display` grew a `Block` member and B2 is
the day it grows `Grid`.

⚠ **That bet has now been settled once, and it paid.** The day the keyword landed, 884 block and 28
blockflex fixtures went from zero passing to 746 with **no change to this harness at all** — one
line in `TaffyStyleMap` mapping the keyword, and a new conformance file that is a copy of the flex
one pointed at two more categories. Eight *flex* fixtures came with them, which nobody predicted:
they were whole trees being refused for one descendant's `display: block`. The remaining pending
tallies are grid's 2 112, and they are the same bet.

⚠ **`float`'s 84 did not move then, and that was a correction to the sentence above.** They were
refused on the `float` attribute, not on `display`, so unlike block and grid they were never one
keyword away. Every one of them named a block container this store already laid out correctly right
up to the point a float would narrow a line.

⚠ **They moved in the end, and the correction needs a correction.** They *were* one keyword away —
`FloatSide`, a keyword `LayoutStyle` had no field for at all rather than a value of a field it had —
which is a distinction about where the missing thing lives and not about how much of it was missing.
All 84 pass, plus the 8 in `block` that needed a float and a flow root, and `float.xml` is judged by
`TaffyFloatConformanceTests` now. What none of the 92 touches is the sentence's last clause: no
fixture in this corpus has a float narrow a line, because no fixture in this corpus has a line.

⚠ **And the corpus turned out to have a blind spot of a kind the last audit did not have a name
for.** 48 of the block fixtures test that `overflow` blocks a margin collapse — Chrome's answers are
in the file — and every one of them also sets `scrollbar-width`, which this store has no field for,
so all 48 were refused. The corpus *contains* the test and cannot run it. That is not a hole in the
oracle's coverage; it is a hole in the bridge, and it is invisible from either side. The rule is held
by `MarginCollapsingTests` instead, and deleting it leaves all 3 571 corpus tests green.

⚠ **That blind spot closed in two steps with two different causes, which is the difference the whole
census turns on.** 24 of the 48 are the `_overflow_{x,y}_hidden` variants. `hidden` clips without a
scrollbar, so it reserves no gutter, so `scrollbar-width` on those boxes could not move a single
number — the refusal was the *bridge's* and not the store's, and those 24 ran and passed with one
line of harness. The other 24 say `scroll`, where the gutter is real; they were a genuine engine gap
until `LayoutStyle.ScrollbarWidth` landed, and all 24 pass now too. Same property, same paragraph,
two completely different things — and the second one took a field, six call sites and a rule about
absolute positioning that nothing in the paragraph predicted.

## The fixtures that assert nothing

⚠ **A skip reads as a pass in every summary anyone looks at, and 408 of these did.** The three
conformance suites turn a `TaffyUnsupportedException` into `Assert.Skip`, so the fixture never
reaches the algorithm — it cannot fail, and it is not counted as a gap either. The suites *did* pin
the refusal count, so nothing drifted silently. What no file recorded was what the refusals were
**for**, and four gap files described corpora that were nearly closed without mentioning the largest
bucket of fixtures in the project.

⚠ **The census's own largest entry is now closed, and it is the one result that argues for writing
census files at all.** `scrollbar-width` accounted for 180 of the 284 engine refusals — more than
the other four engine buckets put together, and more than any single entry in any of the four gap
files. Nothing named it before the census did: `BlockKnownGaps.txt` mentioned it in a footnote about
64 fixtures, and the layout README argued in the `Overflow` enum's own remarks that the distinction
it turns on could not matter because nothing here paints a scrollbar. That argument was about
painting. All 180 now pass, with no entry in any gap file.

`UnsupportedFixtures.txt` is that census: every distinct refusal message, per corpus, with the
fixture count it accounts for, re-derived from an actual run by `TaffyUnsupportedCensusTests` and
compared line for line. A second test asserts the census is not a census of *nothing* — a corpus
that failed to reach the output directory, or a style map that stopped throwing, would otherwise
make both sides agree on an empty list and go green.

⚠ **The split it forces is worth more than the total.** A refusal is either a **harness** gap — a
value the map never learned, on a feature the store has — or an **engine** gap. The first kind is
worth nothing to anyone and costs the whole fixture, including every unrelated property it sets. Of
the original 408 the ratio was **124 harness against 284 engine**, which nobody would have guessed
in either direction. All 124 are closed: 92 became passes, and 32 became newly *visible* failures,
filed under three new headings in the gap files. Two of those three are arithmetic that had been
wrong since it was written, with the only fixtures that exercise it refused on an unrelated
property.

⚠ **96 of the 284 engine gaps have since been closed as well, and the contrast with the harness
batch is the thing to take from it.** Safe alignment (76), the legacy `text-align` keywords (16) and
`display: flow-root` (8, of which 4 also need floats) were each a field `LayoutStyle` genuinely did
not have; the census said so, refused to let them be translated away, and all three were written.
**96 fixtures started running and 96 passed.** That is a much weaker result than the harness batch's
92-and-32, and it should read that way: code written *against* a set of fixtures agrees with them,
so an engine gap converting tells you the work is done and nothing about the algorithm. It is the
harness kind — fixtures whose arithmetic was never checked by anybody — that finds defects.

**0 are refused now.** `scrollbar-width` (180) went, and then floats (92) went, and the census is
empty. ⚠ The last 92 are the purest engine gap the project has had — an entire corpus that had never
executed once — so they converted 92-for-92 and tell you nothing whatever about whether the
algorithm is right. The only float evidence that was not written against its own oracle is the 8
`block_flow_root_*_float` fixtures, which live in a corpus written for something else.

## The Ahem measure function

⚠ **"The Ahem stub" is a misnomer: no font is involved, on either side.** Ahem is a test font whose
every glyph is exactly one em square, which is what let Taffy replace text measurement with
arithmetic — Chrome laid the fixtures out with the real font at 10 px, and Taffy's harness reproduces
those numbers with ten points per character and ten per line. `TaffyAhemMeasure` is a line-for-line
port of that, so it is not an approximation standing in for a text engine; it is the exact model the
expected numbers were produced with. Vixen needing no font here is a property of the corpus, not a
compromise.

Two details are load-bearing and both look wrong: words are separated by **zero-width space**, not by
U+0020 — a run of ASCII spaces measures as characters, because in Ahem it is one — and the
line-breaking loop starts a new line for a word that does not fit without checking whether it fits
the new line either.

Only 800 of the 5 524 fixtures carry text at all.
