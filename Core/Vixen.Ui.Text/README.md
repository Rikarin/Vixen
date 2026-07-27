# Vixen.Ui.Text

Underestimating text is the classic UI-framework mistake, and the defence against making it is the
same one `Vixen.Ui.Layout` used: **be judged by somebody else's conformance suite.**

## State

**UAX#29 segmentation and UAX#14 line breaking are built, and all 22 048 conformance cases pass.**
The UAX#29 suite was committed before its implementation, which is what that commit's diff shows.

| | |
|---|---|
| `Tools/Vixen.UnicodeTableGen` | UCD → committed property tables, and the conformance suites as xunit tests. |
| `GraphemeBreaker` | UAX#29 cluster boundaries. What backspace and the caret move in. |
| `WordBreaker` | UAX#29 word boundaries. What a double-click selects. |
| `LineBreaker` | UAX#14 break opportunities. Where a paragraph may wrap. |
| Property tables | 1 386 grapheme, 1 100 word, 2 920 line, 1 201 width, 473 conjunct, 156 pictographic ranges. Unicode 17.0.0. |
| UAX#9 bidi | ⏳ next |
| HarfBuzz shaping, MSDF atlas, font fallback | ⏳ |
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

## Why the tables are generated and committed

CI has no copy of the Unicode Character Database, and fetching one at build time would make a build
depend on a website. So the generator runs by hand when the UCD version moves, and its output is
committed with the version stamped in a header — a mismatch shows up in a diff rather than at runtime.

The ranges are sorted, merged and stored as one flat array rather than a table per property, because
segmentation asks for the class of every code point of every string it measures. The layout matters
more than the range count does.

Licensed under Apache-2.0. The generated tables are derived from Unicode data files, which carry the
[Unicode terms of use](https://www.unicode.org/terms_of_use.html).
