---
title: Text transform
slug: ui/text-transform
kind: guide
area: Core
summary: uppercase, lowercase and capitalize — why they are a shaping-time change rather than a keyword table, why .NET's own casing is the wrong tool, and the index map that keeps a caret in the right character.
api: [T:Vixen.Ui.Text.TextTransform, T:Vixen.Ui.Text.TransformedText]
tags: [ui, text, typography, vcss, utilities, unicode, caret]
since: 0.2
status: preview
related: [ui/text-decoration, ui/text-input, editor/utility-styles]
---

## What it is

CSS's `text-transform`: the element draws different characters from the ones it was given.

```vcss
.section-heading { text-transform: uppercase; }
.title           { text-transform: capitalize; }
.shout-no-more   { text-transform: none; }
```

`TextTransform` is the resolved value; `TransformedText` is the drawn string *and* the map back to
what the author wrote, which is the part of this feature worth reading about.

## What it is for

**Saying how a word is presented without changing what it says.** A heading in small capitals is a
fact about the theme; the label is still the label, and a search, a screen reader, a copy of the text
and — the one that bites — an editable field's caret all have to go on seeing what was typed.

That last one is why this arrived later than the three-line keyword table it looks like.

⚠ **A full Unicode case mapping changes the string's length.** `straße` uppercases to `STRASSE`:
six characters in, seven out. `ﬁne` becomes `FINE`. From that point on, an index into what was
written and an index into what is drawn are different numbers — and `TextRun.Start`,
`TextLine.Start`, every caret offset and `TextField`'s selection are all indices.

Shipping the four keywords without a map between the two puts the caret in the wrong character of an
editable field, silently, and only on the strings that happen to expand. That is a worse outcome than
not having the feature, which is why it waited.

⚠ **And .NET's own casing would never have shown the problem.** `string.ToUpperInvariant`, `ToUpper`
in every culture, and `Rune.ToUpperInvariant` over all 1 112 064 scalars implement Unicode's *simple*
case mappings, which are one code point to one by definition — measured over the whole code space,
not assumed. So an implementation written on the framework's casing is perfectly caret-safe and draws
`STRAßE`, where every browser draws `STRASSE`. A different defect, and a visible one instead of a
silent one.

Both halves are closed together. `SpecialCasingTable` is the Unicode Character Database's
unconditional `SpecialCasing.txt` rows, generated beside the segmentation tables by
`Tools/Vixen.UnicodeTableGen`; `TransformedText` applies them and records where every index went.

## Using it

Four classes, one property.

| Class | Value |
|---|---|
| `uppercase` | `text-transform: uppercase` |
| `lowercase` | `text-transform: lowercase` |
| `capitalize` | `text-transform: capitalize` |
| `normal-case` | `text-transform: none` |

The property inherits, which is what makes it work on the text child a markup interpolation emits
rather than only where it is written — the same reason `text-decoration-line` inherits here and does
not in CSS.

**The map**, for a caller that needs to cross between the two strings:

```csharp no-compile="a fragment; the three lines are what the map answers"
var drawn = TransformedText.Of("straße", TextTransform.Uppercase);

drawn.Text;          // "STRASSE"
drawn.ToDrawn(5);    // 6 — the `e` the author typed is the seventh unit drawn
drawn.ToSource(5);   // 4 — the second `S` belongs to the `ß`
drawn.IsIdentity;    // false
```

⚠ **The two conversions are not inverses, and that is the behaviour a text field wants.** Source to
drawn and back is the identity; drawn to source and back is not, because both units of the `SS` map
to the one `ß` the author typed. There is no caret position between them, so a click in the middle of
an expansion snaps to its start.

**Identity costs nothing.** No transform, or a transform under which every character kept its length
— `hello` to `HELLO` — allocates no arrays and hands back the *same string instance*, so the shaping
cache's fast path and the element's own block cache go on meaning what they meant.

### Where it happens

`UiDocument.TextTransformOf` is read by `UiElement.Block`, **before** anything is shaped, wrapped or
measured. A capital is wider than its lowercase in every text face, so a transform applied at paint
would draw a paragraph the layout measured at a different width and wrapped at different characters.

`TextLine` carries the map, which is what keeps the distinction from leaking:

- `TextLine.Start`, `Length`, `CaretOffset` and `CaretPositionAt` speak the **element's own** string.
- `TextRun.Start` and `TextRun.Shaped.Text` speak the **drawn** string.

A consumer that goes through the line needs to know none of this. One that reaches past it into a run
is the single place the two spaces have to be held apart.

### Where this is not CSS

**`capitalize` leaves the rest of the word alone.** `iPhone` becomes `IPhone`, not `Iphone`. That is
what the specification says and what browsers do, and it surprises people every time.

**The first *letter*, not the first character.** `"hello` capitalises the `h` and not the quotation
mark. Word boundaries come from UAX#29 rather than from "the character after a space", so `don't` is
one word and gets one capital.

**A digit is not a letter unit**, so `1st` becomes `1St`. Also the specification's reading, also odd.

**Titlecase is a third case.** `ǆ` capitalises to `ǅ` and uppercases to `Ǆ`.

### What is deliberately not implemented

**The conditional case mappings.** `SpecialCasing.txt`'s remaining rows depend on surrounding context
or on a language — final sigma, the Turkish dotless *i*, the Lithuanian retained dot. `TextShaper`
leaves HarfBuzz's language unset on purpose so that shaping does not depend on the machine's locale,
and the same reasoning applies here: a case mapping that changed with the operating system's region
would make a golden image machine-dependent.

**`full-width` and `full-size-kana`.** Compatibility mappings for Japanese input methods, with no
utility class in any framework this project follows.

## Examples

A column header in the editor's own idiom:

```vxml
<label class="uppercase text-xs tracking-wide text-muted">Assets</label>
```

Undoing an inherited transform on one child, which is what `normal-case` is for:

```vxml
<div class="uppercase">
    <label>Project</label>
    <label class="normal-case">{FileName}</label>
</div>
```

## See also

- [Text decoration](text-decoration.md) — the other typographic layer over a run of text.
- [Text input and the input method](text-input.md) — the control whose caret this had to keep honest.
