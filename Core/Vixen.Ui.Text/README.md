# Vixen.Ui.Text

Underestimating text is the classic UI-framework mistake, and the defence against making it is the
same one `Vixen.Ui.Layout` used: **be judged by somebody else's conformance suite.**

## State

**UAX#29 segmentation, UAX#14 line breaking, UAX#9 bidi and HarfBuzz shaping are built.** All
113 755 Unicode conformance cases pass, and 328 of the Consortium's 413 shaping cases — the other 85
are HarfBuzz's own nonconformance, pinned one by one rather than excused.
Both suites were committed before their implementations, which is what those commits' diffs show.

| | |
|---|---|
| `Tools/Vixen.UnicodeTableGen` | UCD → committed property tables, and the conformance suites as xunit tests. |
| `Tools/Vixen.TextRenderingTestGen` | text-rendering-tests → the shaping conformance suite, and the fonts it needs. |
| `GraphemeBreaker` | UAX#29 cluster boundaries. What backspace and the caret move in. |
| `WordBreaker` | UAX#29 word boundaries. What a double-click selects. |
| `LineBreaker` | UAX#14 break opportunities. Where a paragraph may wrap. |
| Property tables | 1 386 grapheme, 1 100 word, 2 920 line, 1 267 bidi, 1 201 width, 984 script, 473 conjunct, 156 pictographic ranges and 128 bracket pairs. Unicode 17.0.0. |
| `BidiAlgorithm` | UAX#9. Which way each character runs, and the order they are drawn in. |
| `TextItemizer` | UAX#24 script runs × bidi levels — the runs a shaper can be handed. |
| `FontFace`, `TextShaper` | HarfBuzz shaping, and the glyphs it produces. |
| MSDF atlas, font fallback | ⏳ |
| `TextEditor` model with IME | ⏳ |

## What the rules cost

Most of UAX#29 is a decision about two adjacent code points. The ones that are not are where
implementations go wrong, and there are four:

- **GB9c** holds an indic conjunct together across its virama, and needs a property — `InCB` — that
  lives in a UCD file none of the others use. Sixteen cases fail without it.
- **GB11** joins an emoji sequence, but only when the sequence *began* with a pictograph. Without
  that condition a joiner between two letters glues them together.
- **GB12/GB13** pair regional indicators, so four of them are two flags rather than a flag and two
  halves. Six cases fail without it.
- **WB4** makes format characters invisible to every other word rule — which means the rules that
  look ahead have to look past them too. It is applied by *skipping* rather than by classifying, and
  1 086 cases fail without it.

The word rules also need lookahead in a way the cluster rules do not: deciding the apostrophe in
`can't` requires knowing there is a `t` after it. So `WordBreaker` decodes into an array first, and
`GraphemeBreaker` streams.

**Line breaking finds opportunities, not lines.** It says where a break is permitted and where one is
mandatory; choosing which permitted break to take needs measured widths and is layout's job. Keeping
them apart is what makes the conformance suite applicable at all — the suite knows nothing about
fonts.

Its 19 338 cases arrive as an embedded data file and one `[Theory]`-shaped test rather than as
generated methods, and that is purely a size decision: a C# file with 19 338 `[Fact]`s is tens of
megabytes and minutes of discovery. They are still the Consortium's cases, and a failure still prints
the Consortium's own description of the rule it broke.

## What it found

**Two UCD properties cannot share one class table.** `Extended_Pictographic` comes from
`emoji-data.txt` and `Word_Break` from `WordBreakProperty.txt`, and they overlap: U+24C2 CIRCLED
LATIN CAPITAL LETTER M is `Word_Break=ALetter` *and* pictographic at the same time. Folding both into
one sorted range table makes one silently shadow the other, and which one wins depends on sort order.
Forty-four conformance cases said so, every one of them containing U+24C2. A code point has one
`Word_Break` property and separately may or may not be pictographic; the tables say so now.

That is the shape of bug the conformance suite exists for. It is not a misreading of the
specification — the rules were right — it is a data-modelling mistake one layer below them, and no
amount of re-reading UAX#29 would have surfaced it.

**The same mistake, twice, in line breaking.** LB9 gives a combining mark its base's *class*, which
is enough for every rule that reads classes and silently wrong for the four that read identity or
position: LB28a names U+25CC by code point, LB15a and LB15b ask whether a quotation mark is opening
or closing, LB30b asks whether a pictograph is unassigned, and LB30a counts regional indicators.
Every one of them was reading the mark instead of the mark's base — a quotation mark followed by a
diaeresis stopped being a quotation mark. `BaseOf` exists so that the rule is stated once.

**And a comment that disagreed with its own code, twice.** LB15a and LB20a both allow `SP` in the
context before them, and both were written to *skip* the spaces and then ask what was beyond —
looking straight past the answer. The comment above one of them said "SP is itself one of the classes
the rule allows" while the list below it omitted `SP`. Two cases out of nineteen thousand caught it:
`: « E` and `Mac Pro -tietokone`.

**And once more in bidi, in a different disguise.** The implicit rules (I1, I2) raise levels *in
place* — a right-to-left character by one, a number by two. Everything that reads a level for
*context* has to read what the explicit rules decided, not what a rule from a different sequence has
since written there. Without that snapshot the isolating run sequences corrupt each other in source
order, and the symptom looks nothing like the cause: an `LRE` paragraph came out with exactly the
levels of the `RLE` one, because the run before it had already been raised.

Three subsystems, three variants of the same mistake: **reading a mutated array where the unmutated
one was meant.** It is worth naming because it will happen again.

## Shaping, and how to test something you did not write

Vixen writes no shaping algorithm. HarfBuzz does, and that makes the obvious gate a worthless one:
comparing Vixen's glyphs against `hb_shape`'s glyphs is HarfBuzz judging itself, and would stay green
through any mistake that handed the shaper the same wrong arguments twice — which is most of the
mistakes available here.

**What Vixen owns is everything around the call.** Which runs the text is cut into, what direction
and script each is given, what order the results are drawn in, and how a glyph relates back to the
character it came from. A shaper's output depends on all of it, so a correct shaper given a Kannada
string labelled Latin produces wrong glyphs. That is why the gate is the Consortium's
[text-rendering-tests](https://github.com/unicode-org/text-rendering-tests), whose expectations were
written by hand from the OpenType specification by people who were not running a shaper.

**Sabotage says how good a gate it is.** Shaping every run as Latin fails 203 cases. Forcing every
run left to right fails 6. Giving spaces and punctuation runs of their own fails 2 — one of them the
case the Consortium named *Space Isn't Nothing*, which exists for that exact mistake and catches it
by name.

⚠ **And the same sabotage found the hole.** Shaping each run *without the text around it* fails
nothing at all, because every case in the suite is a single run and so has no neighbour to lose. Yet
that context is what decides whether an Arabic letter joins, and dropping it also makes every cluster
index relative to the run instead of to the text — an off-by-three in every caret and hit test
downstream. Four hundred external cases cannot see either. `ShapingTests` covers what they miss, and
knowing which half that is cost one sabotage run.

**Two more things worth having found.** The suite's positions are in a **1000-unit em**, not the
font's: the harness renders at a 1000-pixel size, so nine of the fourteen fonts have expectations
scaled by 1000/2048. Compared naively, every case with two or more glyphs fails by a factor of 2.048
and every single-glyph case passes — because a lone glyph sits at the origin in any scale. And a
**bracket that opens before the first letter** remembers a script that does not exist yet, so
`(ಲ್ಲಿ)` came out as Kannada followed by a one-character run of nothing in particular. Backfilling
the leading characters was not enough; the bracket stack had to be backfilled too.

**Shaping is held at design-unit scale and never at a pixel size**, because HarfBuzz's OpenType path
has no hinting and no size-specific behaviour. The same string at 12pt and at 48pt shapes identically
at proportional positions, which is what will make the shaping cache size-independent — one entry per
string rather than one per string per DPI scale.

## The caret, and a function that cannot be inverted

A shaping cluster is not a grapheme cluster. A cluster is whatever the shaper could not subdivide —
a ligature, a reordered Indic syllable — and it can hold several user-perceived characters behind one
glyph. A caret moves in graphemes, so it has to land *inside* such a glyph; `CaretOffset` interpolates
across the cluster by grapheme count. Snapping to the cluster edge instead skips a character in
Kannada and jumps the whole of an `ffi` in Latin, which both look like a broken arrow key.

The gate is a round trip rather than a table of numbers: hit-test a caret's own offset and you must
get the caret back. It holds for scripts nobody thought to write a case for. Verified by sabotage —
not reversing right-to-left clusters into logical order fails 7 of the 18, treating zero steps as
"the next boundary" fails 6, forgetting that the fraction runs the other way inside a right-to-left
cluster fails 4, and snapping to the cluster edge fails 3.

⚠ **But the round trip is only true where the text runs one way, and that is a property of bidi
rather than a gap.** In `abcلسان` the index 3 is both *after the c* and *before the first Arabic
letter*, and those are at opposite ends of the Arabic run — one index, two places. The same point on
screen therefore answers to two indices. No function from an index to a position can return both, so
this one answers with the leading edge of the character the index names, and drawing order breaks the
tie in the other direction. Telling them apart needs a caret **affinity** carried beside the index,
which is an editor's concern and is owed with `TextEditor`. Asserting the round trip everywhere would
have meant deleting the mixed case or inventing a rule to make it pass; both would have buried this.

## Why the tables are generated and committed

CI has no copy of the Unicode Character Database, and fetching one at build time would make a build
depend on a website. So the generator runs by hand when the UCD version moves, and its output is
committed with the version stamped in a header — a mismatch shows up in a diff rather than at runtime.

The ranges are sorted, merged and stored as one flat array rather than a table per property, because
segmentation asks for the class of every code point of every string it measures. The layout matters
more than the range count does.

Licensed under Apache-2.0. The generated tables are derived from Unicode data files, which carry the
[Unicode terms of use](https://www.unicode.org/terms_of_use.html).
