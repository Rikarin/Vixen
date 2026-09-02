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
| `ShapedText.CaretOffset` | Where a caret goes, and what a click means, in graphemes rather than glyphs. |
| `ShapingCache` | Shaped paragraphs with LRU eviction. Keyed without the size. |
| `FontFace.Decoration` | Where the face wants an underline and a line-through, and how thick. |
| MSDF atlas, font fallback, rich-text runs | ⏳ |
| `TextEditor` model with IME and caret affinity | IME ✅, affinity ⏳ — see below |

## ⚠ A conformance suite says nothing about the caller

Every number above is about this assembly in isolation, and for a while all of them were true while
right-to-left text still came out wrong on screen. `ParagraphDirection` — the argument that carries
`direction: rtl` into `BidiAlgorithm`, `TextItemizer` and `TextShaper` — had **no reference outside
this assembly and its own tests**. `Vixen.Ui` shaped every paragraph at `Auto`, so the CSS property
decided the box's logical insets and its `text-align` and never the order of the glyphs, and 91 707
green cases said nothing about it.

The lesson is a search, not a suite: a public parameter that only the tests of its own project ever
pass is a feature that has never been used. `git grep` for the *type* across `Core/` is the check,
and it is cheaper than any of the tables above.

Wired since 2026-08-25 — `UiDocument.DirectionOf` → `UiElement.ParagraphDirection` →
`ShapingCache.Shape` — and `Vixen.Ui.Tests.BidiDirectionTests` is what would notice it coming
unwired again. Those assertions are on *which glyph is leftmost*: a mis-ordered line is not a crash
and not visibly wrong to a reader of the wrong script, so an assertion on logical order would pass
against every bug this file is about.

The same audit found the same shape one level down: `Vixen.Ui` cut its runs where the *face* changed
and not where the *level* changed, so reordering stopped at a font-fallback boundary. `TextRun` now
carries a level and `TextLine` orders its pens by L2 — through `TextItemizer.VisualOrder`, which grew
a `ReadOnlySpan<int>` overload for the purpose rather than being copied into `Vixen.Ui`. **L2 lives
here and only here**, and a second copy of it would be a second thing to keep conformant.

⚠ A caller that cuts runs on something of its own — a face, a size, a rich-text span — must intersect
its boundaries with the itemiser's before it can reorder them, because reversing stretches of runs is
sound only when each run has one level throughout. It must *not* cut on script: the shaper re-itemises
by script itself, with the whole string in the buffer, which is how an Arabic letter finds out whether
its neighbour joins.

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

**And a fourth finding of a different shape, from `FontFace.Decoration`: the faces disagree far more
than a plausible constant would let you guess.** Across the twenty-two fonts committed here the
underline thickness runs from 20 design units per 2048-unit em to 184 — a factor of nine, between two
faces a single document could mix — and the underline position from 39 units below the baseline to
292. A hardcoded hairline is right for one of them and reads as a rendering fault in the other, which
is the whole argument for asking the face. Two of the twenty-two report a zero x-height, and one,
`TestGSUBOne.otf`, carries a `post` table whose underline position and thickness are *both* zero: a
reader that believes it draws a zero-height line on the baseline, which is invisible and in the wrong
place at once. `DecorationMetricsTests` asserts the four faces' numbers against a separate parse of
the binaries rather than against this code, so a wrong table offset cannot agree with itself.

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

## The cache, and what its key deliberately leaves out

`ShapingCache` keeps shaped paragraphs with least-recently-used eviction, judged against shaping
without one: a cache is only ever wrong by answering differently from the thing it stands in for, so
that is what is checked, over random sequences of lookups rather than over cases somebody chose.
Verified by sabotage — not promoting an entry on a hit, dropping the font or the direction from the
key, evicting one entry too late, and confusing two paragraphs of the same length all fail it.

**The size is not in the key**, which is the payoff for holding the font at design-unit scale. One
entry serves every size and every DPI scale the label is drawn at; a size-keyed cache would miss on
every frame of a growing label.

⚠ **Whole paragraphs, not runs**, and that follows from a decision two files away. A run is shaped
with the text around it as context, so its glyphs are not a function of the run alone — a run-keyed
cache would either be unsound or need the context in the key, at which point it is a paragraph cache
with extra steps. Reuse between paragraphs that share a word is given up on purpose.

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

## Glyph outlines, which HarfBuzz does not have

`FontFace.GetOutline` returns a glyph's contours in design units. It exists because
**HarfBuzzSharp exposes no outline API at all** — `TryGetGlyphExtents` is a bounding box, and there
is no `Draw`, `Outline`, `Path` or `Paint` type in the pinned assembly — so an MSDF atlas has
nothing to build a distance field from. Vixen reads the raw `glyf`/`loca` and `CFF ` tables that
`Face.ReferenceTable` will hand over. Spiked before it was built on:
[`docs/plan/spikes/text-glyph-outlines/RESULT.md`](../../docs/plan/spikes/text-glyph-outlines/RESULT.md).

**Curves stay curves**, for the reason `PathBuilder` gives in `Vixen.Ui`: how finely to flatten
depends on a device scale nothing here knows, and a distance field wants the curve itself. ⚠ **Both
quadratic and cubic segments appear and neither is converted** — TrueType draws in quadratics, CFF
in cubics. Promoting a quadratic to a cubic is exact and would double the control points a distance
function solves against; the other direction is an approximation. A consumer handles both verbs.

⚠ **The outline is positioned, and the font's own coordinates are not.** HarfBuzz reports a `glyf`
glyph's extents shifted so its `xMin` lands on the left side bearing, and where a font's stored
`xMin` disagrees with its `lsb` — common, and universal in italics — the two spaces differ by that
much. Every other number in this assembly comes from HarfBuzz, so the outline is put in HarfBuzz's
space rather than the other way round. A glyph drawn straight from the table sits `lsb − xMin` units
off, on exactly the fonts nobody tests with.

### What the gate can and cannot see

The gate is HarfBuzz's own extents over every glyph of all fourteen embedded fonts — a separate
implementation of the same tables, which at 2,066 glyphs is the only oracle available. The spike ran
the same comparison over 242 system fonts and 259,298 glyphs: 99.999 % on `glyf`, 99.777 % on `CFF`.

⚠ **A bounds oracle cannot see a path, and two sabotages proved it.** The rules that turn TrueType's
points into a path — an implied on-curve point midway between two off-curve ones, and a contour that
begins off-curve — move points that already lie inside the hull of their neighbours. Break either and
the shape changes while the bounding box does not, so every comparison stays green. Golden paths for
three glyphs close that, and finding the right three meant counting which branch each of the 2,066
took: all the Kannada contours start on-curve, so the first golden caught only one of the two rules.

⚠ **And the CFF interpreter is barely gated here at all** — counted, not guessed: the embedded corpus
contains **zero stem operators and zero hintmasks**, so the width-parity rule that decides how many
bytes a `hintmask` skips is never executed, and inverting it passes every test in this project. That
rule's real gate was the spike's 17,934 CFF glyphs, whose fonts belong to the operating system and
cannot be committed. The flex operators are unreached for the same reason.

**Not implemented, and not owed**: point-matched composites and `seac`. No glyph in 242 fonts used
either.

~~**Owed**: `gvar` deltas, so a variable font currently reads at its default instance.~~ **A font is
read at an instance.** `FontVariation` normalises user-space axis values through `fvar` and warps
them through `avar`; `GlyphVariations` applies `gvar`'s tuples, with packed point numbers, packed
deltas, intermediate regions, shared tuples, phantom points, composite component offsets, and
inferred deltas for the points a tuple does not name. `TextShaper` honours the instance too, which
closed a gap where `ShapingCache` keyed on the axis position and nothing upstream varied by it.

Gated by the Consortium's own variable-font cases — all 100 of `GVAR-1…9` and `AVAR-1` — which is a
stronger oracle than the shaping suite, because nothing else shapes a `gvar` delta: every contour is
read, varied and interpolated by code in this project and compared against expectations written by
hand from the specification. Verified by sabotage kept as a test: reading the same hundred cases at
each font's default instance fails **82** of them.

⚠ **A tag is four bytes.** Zycon's axes are `M1␣␣` and every caller — CSS, a test file, a person —
writes `M1`; matching only the padded form left all six axes at their defaults, which on screen is
indistinguishable from a font with no variation data. ⚠ **And an untouched point's rule is not the
obvious one**: two references at the same coordinate pulling different ways infer *nothing*, where
taking either is the natural mistake.

**Still not implemented**: `CVAR` — it varies hinting control values, so its expectations differ from
an unhinted outline and need an interpreter — `CFF2` charstring variation, and `HVAR` read directly
rather than through HarfBuzz.

## Rasterising, and the distance field

`GlyphRasterizer` fills an outline into coverage by scanline and non-zero winding.
`DistanceField` turns one into the multi-channel signed distance field doc 09 asks for. Both take a
scale and an origin, which is where **the decision to keep curves as curves is finally spent**: the
flattener's tolerance comes from the caller's pixel size, the thing nobody knew until here.

### The oracles, and where each one stops

**The rasteriser is judged by Green's theorem.** ∮(x dy − y dx)/2 gives the exact area a path
encloses straight from its control points; the integrand for a Bézier is a polynomial, so four-point
Gauss–Legendre evaluates it to the last bit. It shares no code and no reasoning with the scanline
fill — it never asks where an edge crosses a row.

⚠ **It is compared per contour, not per glyph.** Green's theorem measures *algebraic* area, so a
region two contours both cover counts twice; a non-zero fill measures *covered* area, so it counts
once. They part company exactly where contours overlap, which is not exotic — `TestShapeLana` builds
letters from stacked strokes, and 22 % of one glyph's algebraic area is covered more than once. Per
contour the multiplicity disappears and the check is exact again.

**The field is judged by the rasteriser**: threshold the median, compare pixel by pixel against the
same outline filled, ignore the boundary where a binary answer and an antialiased one differ by
design. Every glyph of every embedded font.

⚠ **And the corner claim needs a third oracle, because the first two cannot see it.** A field is read
by interpolating it, and interpolation is where a single channel loses a corner. So: store a square,
reconstruct it, and find where the isoline crosses — against the closed-form signed distance to a
rectangle, sampled and interpolated identically, which is what one channel would have held. Two
earlier versions of that test measured nothing. Counting misclassified pixels hides the effect, since
a plain field's corner error is a fraction of a texel and any band wide enough to ignore boundary
noise swallows it. And **the corner's diagonal is the one direction where the three channels are
symmetric and none of them can help** — measured there, the median *is* a plain field, exactly. What
the channels buy is that the edges stay straight up to the corner.

### Three findings, all from sabotages that failed to fail

⚠ **A corner is a property of the outline, not of the flattening.** Twice over: a curve cut into
twenty chords has nineteen joins that each turn a few degrees, and even at a genuine segment boundary
two neighbouring chords differ by about a step's worth of curvature. Either one makes a circle come
out striped. Corners are found from the outline's own tangents.

⚠ **Each channel carries its own sign, and that is the mechanism rather than a detail.** Taking one
sign from the fill and applying it to all three leaves the values differing only in magnitude, so
their median can never disagree with a single channel about which side of the shape a point is on —
which is the whole of what the median is for. The first version did exactly that and reconstructed a
square's corner no better than a plain field. The fill still settles the *overall* answer, because a
sign from an edge's orientation is wrong wherever two contours overlap.

⚠ **A run's colour must differ from its neighbour's, and the last run wraps.** Cycling the three
combinations in order gives four corners the sequence RG, GB, BR, RG — so one join has both sides the
same, and it is a corner. The test only scanned the other three until a sabotage passed.

### What is not gated

⚠ **The pseudo-distance is insurance.** Clamping to the segment instead fails nothing: two shapes
were built to reach it, and the answers differ in magnitude but never in sign, so a thresholded
reconstruction moves by 0.02 of a texel. What it should buy is a truer gradient for the shader's own
antialiasing, and nothing here looks at a gradient yet.

## The atlas

`GlyphAtlas` holds the fields in one texture, packed as they are asked for and evicted
least-recently-used when it fills. Dynamic rather than built ahead: CJK alone is tens of thousands
of glyphs, and the set an interface actually uses is a few hundred.

⚠ **The key carries no point size.** A distance field is read at any scale — the whole reason for
one — so a key with the size in it would miss on every frame of a growing label and fill the atlas
with the same glyph. Same property that keeps the shaping cache size-independent.

**Shelf packing**, which wastes the difference between a row's height and each glyph's. A skyline
packer wastes less and has to move entries to stay that way, and moving one invalidates a texture
coordinate somebody is holding.

⚠ **Eviction leaves a hole of one exact size**, so freed slots are kept per shelf and matched by
width. What that cannot answer is a glyph wider than every hole while the atlas is nominally full,
which is what `Compact` is for — it changes every region, so `Version` moves and a caller re-reads.

⚠ **`Version` is for coordinates and `Revision` is for pixels.** They answer two different questions
and an uploader that watches the wrong one sends the texture once and never again. A version says
"every region moved, re-read the ones you cached"; a revision says "the bytes changed, send them" —
so adding a glyph moves the revision alone, and compacting or clearing moves both.

⚠ **Evict first, compact only when the space is there and the shape is wrong.** Compacting first
would be tidier and would bump the version on every addition to a full atlas, throwing away every
texture coordinate in flight — for a steady-state interface that is every frame. So entries go one
at a time until either one fits or enough area has been freed that fragmentation must be the reason
it does not.

Verified by sabotage: a hit that does not refresh its entry fails 2, evicting the newest instead of
the coldest fails 2, never reusing a freed slot fails 1, dropping the padding fails 1, a compaction
that does not move the version fails 1, an addition that does not move the revision fails 1, a hit
that moves the revision fails 1, a hit that marks the texture dirty fails 1, and writing a
glyph at the wrong row fails 1 — that last only after a test placed something below the first shelf,
since everything else lands on row zero where the bug is invisible.

⚠ **One claim is insurance and is labelled as such.** Compaction replaces entries warmest first so
that anything dropped would be the coldest, and a sabotage reversing that fails nothing: compaction
only ever runs on a set that already fitted, so it is not clear it can lose one. Shelf packing is
not monotone in the insertion order, which is why the guard is there — but several attempts to build
a set that repacks worse than it packed all fitted.

### One question, from a renderer's side

`GlyphFieldCache` is the join: ask where a glyph is, get an atlas region and the quad to draw it in,
and never learn that outlines, fields or packing exist. A miss reads the outline, encodes the field
and packs it; everything after is a lookup.

⚠ **The placement is in ems.** The atlas is size-independent on purpose, so its metadata has to be
too — a placement in pixels is right for one font size and wrong for the next, and the mistake stays
invisible until somebody draws the same word twice at two sizes. The same goes for the range a
shader thresholds against, which scales with the size and would otherwise blur as text grew and
alias as it shrank.

⚠ **A placement outlives its pixels.** Eviction takes the entry; where the glyph sits relative to
the pen came from the font and cannot have changed, so it is remembered separately and a re-request
only re-encodes.

⚠ **The quad covers the padded cell, not the silhouette.** A glyph drawn with an outline or a glow
reads past its own edge, and a cell cropped to the glyph has nothing there to read.

Verified by sabotage: a placement in pixels fails 1, a quad cropped to the glyph fails 1, an
unpadded field fails 2, dropping the font from the key fails 1, and a screen-pixel range that
ignores the resolution fails 1. ⚠ Two more needed the tests sharpened first — remembering that a
glyph draws nothing is not observable through the atlas, since an empty glyph never reaches it, so
the reads are counted; and reporting a remembered placement beside a region the atlas no longer
holds passes every assertion about the placement while sampling whatever has since been packed at
the origin.

## Why the tables are generated and committed

CI has no copy of the Unicode Character Database, and fetching one at build time would make a build
depend on a website. So the generator runs by hand when the UCD version moves, and its output is
committed with the version stamped in a header — a mismatch shows up in a diff rather than at runtime.

The ranges are sorted, merged and stored as one flat array rather than a table per property, because
segmentation asks for the class of every code point of every string it measures. The layout matters
more than the range count does.

Licensed under Apache-2.0. The generated tables are derived from Unicode data files, which carry the
[Unicode terms of use](https://www.unicode.org/terms_of_use.html).
