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

| Category | Fixtures | Status |
|---|--:|---|
| `flex` | 2 352 | judged per fixture |
| `leaf` | 56 | judged per fixture |
| `block` | 884 | pending B1 |
| `float` | 84 | pending B1 |
| `blockflex` | 28 | pending B1 |
| `grid` | 2 040 | pending B2 |
| `gridflex` | 24 | pending B2 |
| `blockgrid` | 56 | pending B1 + B2 |

Each fixture name ends in one of four suffixes — `__{border,content}_box_{ltr,rtl}` — so the 5 524
are about 1 381 distinct cases run four ways.

## The result that justified building this first

**2 002 of the 2 208 runnable flex fixtures passed on the first run**, on a store built entirely
against a *different* browser-derived corpus. 176 more are refused for a property this store has no
field for, and 206 disagreed. **48 of those are now closed and 2 074 pass.**

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

## What the 206 were, and what the 158 are

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

⚠ **`grid-template-areas` is not in it either, which matters for B2.** Zero of the 2 040 grid
fixtures set it, and the reason is upstream: Taffy's own `tests/xml.rs` builds every other grid
property from the XML and then writes `grid_template_areas: Default::default()`. So named grid areas
— one of the features people reach for grid *for* — arrive with no oracle, and B2 has to bring its
own. WPT's `css/css-grid/grid-definition/` is the place to look.

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

⚠ What the tallies say now is itself a result: **every block, float and grid fixture is refused at
exactly one point — the `display` keyword.** Not one is failing on arithmetic, because not one
reaches any. From this suite's point of view B1 is the day `Display` grows a `Block` member and B2
the day it grows `Grid`.

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
