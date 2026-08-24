---
title: Floats and clear
slug: ui/floats
kind: guide
area: Core
summary: CSS 2.1 §9.5 floats over Vixen's layout store — the exclusion list a formatting context carries, why a flow root moves out from under a float and a plain block does not, how clearance spends a margin instead of adding to one, and the half of the feature that is not implemented.
api: [T:Vixen.Ui.Layout.FloatSide, T:Vixen.Ui.Layout.Clear]
tags: [ui, layout, float, clear, css, block]
since: 0.2
status: preview
related: [ui/inline-layout, ui/box-alignment, ui/utility-composition]
---

## What it is

A **float** is a box taken out of normal flow and pushed to one side of its containing block, where
the content that follows it flows *around* it. It is the oldest layout tool CSS has, and it is the
one whose behaviour is least like anything else in the box model: a float is out of flow, and unlike
an absolutely positioned box it still changes where other boxes go.

```csharp
tree.SetFloat(sidebar, FloatSide.Left);
tree.SetClear(footer, Clear.Both);
```

Or, in a `.vcss`:

```css
#sidebar { float: left; width: 180px; }
#footer  { clear: both; }
```

Or with the utility classes: `float-left`, `float-right`, `float-none`, `clear-left`, `clear-right`,
`clear-both`, `clear-none`.

## What floating a box does

Setting `float` to anything but `FloatSide.None` does four things at once, and CSS 2.1 spreads them
over three sections, which is why the property surprises people:

| | Rule |
|---|---|
| §9.7 | The box becomes **block-level**, whatever `Display` says. |
| §9.5 | It is taken **out of flow** — it no longer advances its container's content height directly. |
| §9.4.1 | It becomes a **block formatting context root**, so its margins collapse with nothing and the floats inside it are invisible outside it. |
| §10.3.5 | Its `auto` width becomes **shrink-to-fit** rather than fill. |

The last one is the one people trip over. A `div` with no width fills its container; float it and it
collapses to the width of its contents.

## The exclusion list

Everything §9.5 does is expressed through one structure: the **exclusion list** a block formatting
context carries. Each entry is a placed float's *margin* box, in the formatting context root's
content coordinates.

Three things read that list, and they read it for three different reasons.

**Placing the next float.** A float goes as far to its side as it can at the current block position;
if the band left free there is too narrow, it drops to the bottom of the float that is in its way and
tries again. Floats therefore stack sideways until they run out of room and then wrap, which is what
makes a row of floated cards behave like a row of cards.

**A formatting context root beside a float.** This is the rule that is not widely known: a float
overlaps the border box of an ordinary block-level sibling and shortens only the *line* boxes inside
it — but a sibling that establishes a formatting context of its own may not overlap the float's
margin box at all. So adding `overflow: hidden` or `display: flow-root` to a box beside a float
changes it from "the text wraps around the float" to "the whole box moves".

```css
/* Text wraps around the float; the div's own border box starts at x = 0. */
#note { }

/* The div itself is pushed clear of the float, and narrows to what is left. */
#note { display: flow-root; }
```

If the box has a stated width and that width does not fit in the band beside the float, it goes
*below* the float instead of narrowing.

**Containing them.** A formatting context root's `auto` height is tall enough to hold the floats
inside it — §10.6.3. This is the whole content of the `flow-root` keyword in practice, and the reason
the "clearfix" hack existed before it.

## Clearance is not a margin

`clear` moves the box that declares it, not the floats it names. The box's top border edge is pushed
down until it is below the bottom margin edge of every earlier float on the named side, by inserting
**clearance** between the box's top margin and its top border.

Clearance behaves differently from a margin in two ways that matter:

**It spends the margin rather than adding to it.** A box whose top margin had collapsed all the way
out through its container's top edge has a hypothetical position of zero, so clearance replaces the
whole collapsed set rather than being measured from the end of it. A 400-point top margin over a
50-point float puts the box at 50, not at 450.

**It stops a margin collapsing through.** A zero-height box normally lets the margins on either side
of it meet. Give it clearance and it no longer does: the collapsed margin keeps its full length, and
the clearance is inserted into the middle of it, with the box's border edges at the clear point.

## What is not implemented

⚠ **A line box does not yet shorten as it passes a float.** That is §9.5's main clause and the
behaviour most people mean by the word, and it is missing: inline layout has no exclusion awareness,
so a paragraph beside a float is laid out at the container's full inner width and its text runs
under the float. Everything above — placement, formatting-context avoidance, clearance, containment —
works and is checked against 92 Chrome-derived fixtures. None of those 92 has any text in it, which
is how the gap survived being measured. It is recorded in `InlineKnownGaps.txt` and in
`Taffy/FloatKnownGaps.txt`.

⚠ **The logical keywords are absent.** `FloatSide` and `Clear` hold CSS 2.1's physical keywords, and
those do not flip with `Direction` — a `float: left` is on the left in an RTL container too. CSS
Logical Properties adds `inline-start` and `inline-end`, which do; neither the style bridge nor the
utility families accept them, because resolving one needs a writing mode the store does not carry,
and aliasing it onto `Left` would be correct in LTR and wrong in RTL inside the same declaration.

⚠ **A float inside an auto-centred block lands at the uncentred edge.** Resolving an `auto` inline
margin needs the used width of the layout the float's origin is about to start, so the origin reads
the stated margin instead.

## Cost

A tree with no `float` and no `clear` anywhere pays nothing: the layout pass scans the style array
once, and every float path is skipped. A tree that *does* contain one pays twice — each block child
is measured once to discover its collapsible margins before it can be placed, and the layout cache is
bypassed for the whole pass, because a cache hit returns a size without placing the floats every
later box reads.
