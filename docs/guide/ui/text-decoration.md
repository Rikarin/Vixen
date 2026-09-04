---
title: Text decoration
slug: ui/text-decoration
kind: guide
area: Core
summary: Underline, overline and line-through — where the lines come from (the font's own tables, never a constant), what the classes are, and the four places Vixen's behaviour is deliberately not CSS's.
api: [T:Vixen.Ui.TextDecoration, T:Vixen.Ui.TextDecorationLine, T:Vixen.Ui.TextDecorationStyle, T:Vixen.Ui.DecorationBar, T:Vixen.Ui.Text.DecorationMetrics]
tags: [ui, text, typography, vcss, utilities, underline, fonts]
since: 0.2
status: preview
related: [editor/utility-styles, ui/cascade-layers, ui/inline-layout, ui/text-transform]
---

## What it is

A line drawn beside the text: an underline, an overline, a strikethrough. Five properties, and the
whole of CSS's `text-decoration` that Vixen implements.

```vcss
.link     { text-decoration-line: underline; text-underline-offset: 2px; }
.removed  { text-decoration: line-through; }
.emphasis { text-decoration-line: underline; text-decoration-style: double;
            text-decoration-color: var(--color-accent); }
```

`TextDecoration` is the resolved form of those five, `DecorationMetrics` is what the *font* says about
where such a line belongs, and `DecorationBar` is one rectangle at the end of the arithmetic.

## What it is for

**Marking text without marking a box.** A border says something about an element; a decoration says
something about the words. The difference shows in three places: a decoration follows the text when
the line is centred, it stops where the glyphs stop rather than where the padding does, and it sits at
a height the *typeface* chose.

That last one is the reason this is a feature and not a `border-bottom`.

⚠ **A constant thickness would be wrong, and not subtly.** Across the twenty-two fonts committed to
this repository the underline thickness runs from **20** design units per 2048-unit em to **184** — a
factor of nine, between two faces a single document could reasonably mix. A hairline that looks
deliberate in a text face reads as a rendering fault in a display face beside it, and a test against
one font cannot tell the two apart. So every number comes out of the face's own `post` and `OS/2`
tables, read through HarfBuzz so that a variable font's `MVAR` deltas apply to the instance actually
being shaped.

## Using it

Most of it gets written as utility classes.

| Class | Property |
|---|---|
| `underline`, `overline`, `line-through`, `no-underline` | `text-decoration-line` |
| `underline-offset-0/1/2/4/8`, `underline-offset-auto` | `text-underline-offset` |
| `decoration-0/1/2/4/8`, `decoration-auto`, `decoration-from-font` | `text-decoration-thickness` |
| `decoration-solid`, `decoration-double` | `text-decoration-style` |
| `decoration-<colour>` | `text-decoration-color` |

`decoration-` carries three of those. A value in the keyword table is a thickness or a style, and
anything else is a colour — the same three-way split `text-` already has, resolved in the same order.

**Where the lines go.** An underline and a strikethrough are placed from the metrics the face states;
the *offset* it gives is the centre of the stem, not its top — which is the reading FreeType and Skia
take and therefore the one the fonts were drawn against. An overline has no metric at all, since no
OpenType table has the field, so it is derived: just above the ascent.

⚠ *Just above*, and that was measured rather than chosen. Putting its top edge **on** the ascent line
keeps a thick overline inside the line box, which sounds better until you meet a face whose ascent
barely clears its capitals — `TestShapeLana`'s ascent is 1556 units against a cap height of 1493, and
the bar landed on the tops of the letters. The cost of the fix is that an element clipping its
overflow clips an overline, which is what a browser does too.

**When the font says nothing.** `TestGSUBOne.otf` carries a real `post` table whose underline position
and thickness are both literally `0`. Believed, that is a zero-height line on the baseline — invisible
*and* in the wrong place, which reads as a broken feature rather than a broken font. A zero is
therefore treated as no opinion and synthesised from: a twentieth of an em thick, a tenth of an em
down, which are FreeType's numbers and so the ones every other toolkit's fallback text was measured
against.

**Sub-pixel hairlines.** An `auto` thickness under one pixel is floored at one, because a rasteriser
draws 0.13 of a pixel as a grey smear. ⚠ An **authored** thickness is not floored: `decoration-0`
means no line, and two adjacent thicknesses have to stay distinguishable at exactly the sizes somebody
would be comparing them at.

**What it costs.** Nothing new. A bar is a `Rectangle` draw command with a zero radius, so it reaches
the geometry builder, the rounded-box distance field and both executors by the path a background
colour already takes — the device and the software rasteriser agree because they are drawing the same
quad, not because two implementations were kept in step. One bar per *line* rather than per run,
spanning the line's width and taking its metrics from the line's first run, which is CSS's "first
available font" rule; per run it would break visibly at every change of face. And a decoration never
changes what the text measures, which is both CSS's rule and the only option here: measurement reports
whole *device* pixels, so a bar that widened a line would round the block up and shift everything
after it.

### Where this is not CSS

Four deliberate differences.

**The five properties inherit, and in CSS none of them does.** CSS does not inherit a decoration, it
*propagates* one: a block container's decoration is drawn by that container across the line boxes of
its in-flow descendants. Vixen has no line box shared between elements — one node produces one box —
so there is no ancestor to draw the line and propagation has nowhere to happen. Inheritance is the
only route from the container the class is written on to the element that owns the glyphs, and that
route is the whole feature, because a `.vxml` interpolation emits its text as a *child* element.

**So a child can switch a decoration off, and in CSS it cannot.** `no-underline` on a descendant wins
here; in a browser it does nothing. The forgiving direction, and `text-clip` is already the same shape
of opt-out for `text-overflow`.

**A relative thickness or offset resolves against the descendant's own font size** rather than the
decorating box's. Invisible for the pixel values every utility emits, and where it does show, scaling
a mark with the text it marks is the answer somebody would have wanted.

**`dotted` and `dashed` are drawn, and `wavy` is not.** The first two used to be absent for a reason
that is no longer true: there was no dash pattern anywhere in `Vixen.Ui`, which is the same finding
`border-style` and `divide-dashed` were absent under. There is one now, and a decoration bar is the
consumer that needs nothing else — it is an axis-aligned rectangle with no corner radius, so breaking
it up is breaking up a length, and the marks are the same quad the whole bar was.

**`wavy` is still absent, and the dash pattern does not touch its reason.** A wave is a stroked path
where every other decoration is a rectangle: it needs the tessellator, a thickness that is a stroke
width rather than a height, and an amplitude and a period CSS does not state. It would resolve
cleanly, compute a value, and paint a **straight** line, which is worse than not having it.

### What is not here at all

`text-shadow` — the draw list has no glyph-shadow path.

`line-clamp` **is** here now, and it landed exactly where the sentence this replaces said it belonged:
in the measure pass. `-webkit-line-clamp` is read by `UiElement.Block`, which drops the lines past the
budget before the height is reported — so a `line-clamp-3` block is three lines tall to its parent,
which is the one truncation in this engine that is a fact about the layout rather than about the
picture. The marker on the last kept line is this file's own ellipsis, put there at paint. ⚠ Vixen's
`line-clamp-N` emits one declaration where Tailwind emits four: `display: -webkit-box` and
`-webkit-box-orient` are a marker a browser needs and this engine does not, and `overflow: hidden` is
a clip for lines that here were never laid out.

`text-transform` **is** here now, on its own page: it was refused under exactly the blocker described
above — `straße` uppercases to `STRASSE`, so a case mapping changes the UTF-16 length and every caret
index in `TextRun`, `TextLine` and `TextField` is an index into the element's own string — and what
closed it was the index map rather than the four keywords. See [Text transform](text-transform.md).

`tab-size` **is** here now, as `tab-1` through `tab-8` and any count. A tab is the one character whose
advance is not a property of the character: CSS makes it the distance to the next stop, so it depends
on where the run *sits*, while `TextRun.Width`, `TextLine.Place`, the caret and every width in
`LineWrapper` are prefix sums over advances that do not. `TextRun.IsTab` and `TextLine.WidthOf` are
the seam — the line is the first thing that knows where a run begins — and `TextRun.Place` suppresses
U+0009's glyph, because a face that has no glyph for it shapes a tab to `.notdef` and draws a box.

⚠ **Two things about it that a browser would not lead you to expect.** The stops are measured from the
start of the line *box*, so a `text-indent` sits inside the first column rather than shifting the grid
— which is what makes a tabbed table under a hanging indent line up. And `tab-size` is visible on
ordinary text here, where in a browser it shows only under `white-space: pre`: Vixen's `white-space`
answers the wrapping question and no other, so a literal tab is never collapsed to a space.

The `<length>` form is dropped rather than resolved — it takes relative units, so it would have to be
computed and inherited beside `line-height`, and no utility can spell it. An element that writes one
keeps the initial eight, which is what a browser does with a declaration it cannot use.

`hyphens` **is** here now, as `hyphens-none` and `hyphens-manual`. ⚠ This one closed a *defect*: the
break already worked and the hyphen was never drawn. `LineBreaker` has always offered a break after a
soft hyphen — `"sup­ply"` and `"sup-ply"` return the identical opportunity list — so Vixen split
`sup|ply` where the author asked, and then showed nothing for it, because U+00AD is `Default_Ignorable`
and the shaper deletes it. `UiElement.Hyphenated` substitutes a drawn hyphen on a line that ends on
one, which is one character for one and so moves no caret index.

⚠ **The substituted character is U+002D and not U+2010**, which is worth knowing if you are reading an
older plan document that says otherwise: U+2010 HYPHEN is `.notdef` in Open Sans and in this repo's
test face alike, so substituting it draws a tofu box rather than a hyphen.

`hyphens-auto` is **not registered**, and that is a refusal rather than an omission: it needs a
per-language hyphenation pattern set *and* a language to choose one with, and `TextShaper` leaves
HarfBuzz's language unset on purpose so that shaping never depends on the machine's locale. A
hand-written `hyphens: auto` lands on `manual` — `auto` also honours the author's own soft hyphens, and
that half Vixen can do.

## Examples

A link that underlines only on hover, clear of the descenders:

```vxml
<label class="text-accent hover:underline underline-offset-2">Open the manual</label>
```

A superseded row, struck through in the muted colour rather than the text's:

```vcss
moveset-row.overridden { text-decoration: line-through; text-decoration-color: var(--color-muted); }
```

Two lines at once — one declaration, two bars, one painted under the glyphs and one over them:

```vcss
.annotated { text-decoration-line: underline overline; text-decoration-thickness: 2px; }
```

Asking the code directly, which is what the draw list does:

```csharp no-compile="a fragment; `run` is whatever `TextRun` the caller is drawing"
var decoration = new TextDecoration(TextDecorationLine.Underline, Thickness: float.NaN);

// NaN means "ask the face". The bar comes back in pixels, y downwards from the baseline.
foreach (var bar in run.Bars(decoration, under: true)) {
    Console.WriteLine($"{bar.Thickness:F2}px, {bar.Top:F2}px below the baseline");
}
```

⚠ `default(TextDecoration)` is **not** "a decoration with the defaults" — a record struct's parameter
defaults belong to its constructor, and the zero-initialised value has a `Thickness` of zero rather
than `NaN`. It reads as no decoration at all, which is why `TextRun.Bar` takes its decoration as a
required argument.

## See also

- [Utility styles](../editor/utility-styles.md) — the full family table, and which properties are read.
- [Cascade layers](cascade-layers.md) — where a `.vcss` rule sits against a utility class.
- [Inline layout](inline-layout.md) — line boxes, and why one node produces one box here.
