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
related: [editor/utility-styles, ui/cascade-layers, ui/inline-layout]
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

`text-shadow` — the draw list has no glyph-shadow path. `line-clamp` — it changes how many lines there
are, which belongs to the measure pass. `text-transform` — a shaping-time change, and the blocker is
sharper than the shaping: `straße` uppercases to `STRASSE` and `ﬁne` to `FINE`, so a case mapping
changes the UTF-16 length, and every caret index in `TextRun`, `TextLine` and `TextField` is an index
into the element's own string.

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
