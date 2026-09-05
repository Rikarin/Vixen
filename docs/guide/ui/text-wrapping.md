---
title: Text wrapping
slug: ui/text-wrapping
kind: guide
area: Core
summary: Where a paragraph's lines end — the three questions CSS's text-wrap shorthand asks at once, why balance is a search rather than an algorithm, and why pretty is only one clause of what the specification licenses.
api: [T:Vixen.Ui.Text.TextWrapStyle, T:Vixen.Ui.Text.TextWrapMode]
tags: [ui, text, typography, vcss, utilities, line-breaking]
since: 0.2
status: preview
related: [ui/text-transform, ui/text-decoration, ui/inline-layout, editor/utility-styles]
---

## What it is

Wrapping a paragraph is three questions, and CSS's `text-wrap` shorthand asks all three with one
word. Vixen answers them in three places, and keeping them apart is what makes each one testable:

| Question | Who answers | Type |
|---|---|---|
| May this paragraph wrap at all? | `UiDocument.WrapsOf` | — |
| Where *may* a line end? | `LineBreaker`, by UAX #14 | — |
| What happens to a word too wide to fit? | `LineWrapper` | <xref:Vixen.Ui.Text.TextWrapMode> |
| Which of the legal breaks does it take? | `LineWrapper` | <xref:Vixen.Ui.Text.TextWrapStyle> |

⚠ **The last two are the pair that get confused, and they cannot answer each other's question.**
`TextWrapMode` is about a word that does not fit *at all* — a URL in a narrow sidebar — and decides
whether it overflows or is broken inside. `TextWrapStyle` is about a paragraph every one of whose
breaks is legal, some of which look better than others.

```vcss
.url     { overflow-wrap: anywhere; }   /* TextWrapMode  — break inside the word */
.heading { text-wrap: balance; }        /* TextWrapStyle — even lines, same count */
.blurb   { text-wrap: pretty; }         /* TextWrapStyle — no one-word last line  */
```

## What it is for

A heading that wraps to two lines with one word on the second reads as a mistake, and it is the
commonest typographic defect in an interface built out of boxes that resize. `balance` is for that:
titles, pull quotes, card headings, anything short and prominent. `pretty` is for prose — it makes
one promise, that the last line is not a single word, and leaves everything above it alone.

⚠ **Neither is free, and that is why `auto` is the default rather than the good-looking one.**
Greedy first-fit is one pass over the paragraph, and a user interface reflows on every resize and
every keystroke. `balance` costs ten more passes; `pretty` costs two measurements. Writing either on
a page of body text buys a reader nothing and charges for it on every frame, which is why they are
opt-in and why balancing gives up above a line count.

## Using it

From a stylesheet, or with the utility classes:

```vcss
.title  { text-wrap: balance; }
.para   { text-wrap: pretty; }
.code   { text-wrap: nowrap; }
```

```vxml
<label class="text-balance">A heading nobody wants a widow on</label>
<p class="text-pretty">Body text, and the last line will not be one word.</p>
```

Directly, for a caller doing its own layout:

```csharp no-compile="A fragment: `shaped` is a ShapedText and `lines` a List<WrappedLine>."
LineWrapper.Wrap(shaped, maxAdvance: 240f, lines, style: TextWrapStyle.Balance);
```

### `balance` is a search, not an algorithm

⚠ **Balancing must never cost a line.** Three even lines where two would have done is not balanced,
it is wrong — so what `balance` looks for is the *narrowest width that still wraps to the line count
the full width gave*, found by bisection over the greedy wrapper. Ten halvings put the answer within
a thousandth of the box, which is inside the narrowest step any real font produces.

A candidate is taken only if it is **no wider than the greedy answer** as well as no longer. A
narrower trial width can only push words later, and an unbreakable run wider than the trial overflows
it rather than moving — so without that second test a "balanced" paragraph could come out with a line
hanging further out of its box than the one it replaced. With it, `balance` guarantees two things
that can be measured rather than one that has to be eyeballed.

Minimum raggedness would be the textbook answer and it needs Knuth–Plass to optimise. For a two- or
three-line heading, which is what the keyword is for, the narrowest-no-longer width lands on the same
breaks — and it is what browsers do.

### `pretty` is one clause, deliberately

CSS Text 4 leaves `pretty` to the user agent: better hyphenation, fewer rivers, no short last line.
Vixen implements the clause the specification names outright — **no last line holding a single
word** — and claims nothing else. That clause needs the previous break and no search at all, so the
lines above the last two are untouched.

⚠ **It refuses its own cure where the cure would overflow.** Pulling a word down lengthens the last
line; if the pair does not fit, taking it anyway trades an orphan for a line hanging out of the box,
which is worse. It refuses equally where the penultimate line has no earlier break of its own, and
where the break between the two lines is one the *text* required — pulling a word across an authored
newline would change what the paragraph says.

## Examples

Four two-letter words in a room of eight, one advance per character — the case
`Vixen.Ui.Text.Tests.LineWrapTests` works in closed form:

| Style | Lines | Widest |
|---|---|--:|
| `Auto` | `aa bb cc` / `dd` | 8 |
| `Balance` | `aa bb` / `cc dd` | 5 |
| `Pretty` | `aa bb` / `cc dd` | 5 |

The same paragraph in a room of **four**, where the cure will not fit:

| Style | Lines |
|---|---|
| `Auto` | `aa bb` / `cc` / `dd` |
| `Pretty` | `aa bb` / `cc` / `dd` — unchanged, because `cc dd` is 5 and the room is 4 |

## See also

- [Inline layout](inline-layout.md) — line boxes, and the strut that decides how tall each one is.
- [Text transform](text-transform.md) — the other property that changes what a line holds.
- `Core/Vixen.Ui.Text/LineWrapper.cs` — the greedy fill and the two second passes over it.
- `Core/Vixen.Ui.Text.Tests/LineWrapTests.cs` — the closed-form cases above, and why they use a stub
  advance array where the rest of that file insists on a real font.
- `docs/plan/43-web-styling-parity.tsv` — the `text` root, whose refusal note this feature retired.
